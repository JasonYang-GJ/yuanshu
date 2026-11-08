using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Vision.Windows;

namespace ScreenGuide.DesktopHost.Runtime;

internal sealed record DesktopVisualWorkerRequest(string Operation, DesktopTaskWindow Window,
    DesktopVisualSnapshot? Snapshot = null, DesktopVisualProposal? Proposal = null);
internal sealed record DesktopVisualWorkerResponse(bool Succeeded, string? ErrorCode = null, string? Message = null,
    DesktopVisualSnapshot? Snapshot = null, byte[]? Png = null, DesktopVisualReceipt? Receipt = null);

/// <summary>Private child transport; never an externally callable IPC or persisted image.</summary>
internal sealed class DesktopVisualWorkerClient : IDesktopVisualAutomation
{
    public async Task<DesktopVisualObservation> ObserveAsync(DesktopTaskWindow window, CancellationToken token)
    {
        var response = await RunAsync(new("Observe", window), token);
        var png = response.Png;
        try
        {
            var snapshot = response.Snapshot;
            if (png is null || png.Length is < 1 or > 4194304 || snapshot is null
                || !DesktopTaskExecutionService.SameWindow(window, snapshot.Window)
                || snapshot.Id == Guid.Empty || snapshot.ImageWidth is < 1 or > 1600 || snapshot.ImageHeight is < 1 or > 1600
                || snapshot.OcrText.Length > 16000 || snapshot.ImageSha256 != Convert.ToHexString(SHA256.HashData(png)))
                throw InvalidResponse();
            var result = new DesktopVisualObservation(snapshot, png);
            png = null; // Ownership transferred; observation disposes and clears it.
            return result;
        }
        finally { if (png is not null) CryptographicOperations.ZeroMemory(png); }
    }

    public async Task<DesktopVisualReceipt> ExecuteAsync(DesktopVisualSnapshot expected, DesktopVisualProposal action,
        CancellationToken token) => (await RunAsync(new("Execute", expected.Window, expected, action), token)).Receipt
        ?? throw InvalidResponse();

    private static async Task<DesktopVisualWorkerResponse> RunAsync(DesktopVisualWorkerRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var assembly = typeof(Program).Assembly.Location;
        var exe = Path.ChangeExtension(assembly, ".exe");
        var start = new ProcessStartInfo(File.Exists(exe) ? exe : "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(assembly)!
        };
        if (!File.Exists(exe)) start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--desktop-visual-worker");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Operation == "Execute" ? 45 : 25));
        using var child = Process.Start(start) ?? throw new DesktopTaskException("desktop_visual_worker_start", "看图操作进程未能启动。");
        try
        {
            using var lifetime = new DesktopTaskWorkerLifetime(child);
            using var stop = timeout.Token.Register(() => KillOwnedChild(child));
            var output = ReadBoundedAsync(child.StandardOutput, 7 * 1024 * 1024, timeout.Token);
            var errors = ReadBoundedAsync(child.StandardError, 8192, timeout.Token);
            var payload = JsonSerializer.Serialize(request, DesktopTaskWorkerClient.Json);
            if (payload.Length > 160000) throw InvalidResponse();
            await child.StandardInput.WriteLineAsync(payload.AsMemory(), timeout.Token);
            child.StandardInput.Close();
            var resultText = await output;
            await child.WaitForExitAsync(timeout.Token);
            _ = await errors;
            token.ThrowIfCancellationRequested();
            if (child.ExitCode != 0) throw InvalidResponse();
            var result = JsonSerializer.Deserialize<DesktopVisualWorkerResponse>(resultText, DesktopTaskWorkerClient.Json)
                ?? throw InvalidResponse();
            if (!result.Succeeded)
                throw new DesktopTaskException(result.ErrorCode ?? "desktop_visual_worker_failed", result.Message ?? "看图操作未能可靠完成。");
            return result;
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            KillOwnedChild(child);
            token.ThrowIfCancellationRequested();
            throw new DesktopTaskException("desktop_visual_worker_timeout", "看图操作响应超时，已停止后续步骤。");
        }
        finally
        {
            KillOwnedChild(child);
            // A stopped Turn must not return while its owned process could still
            // inject input. Use an independent deadline, never the cancelled token.
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await child.WaitForExitAsync(shutdown.Token); }
            catch (OperationCanceledException) { throw new DesktopTaskException("desktop_visual_worker_stop_unconfirmed", "看图进程尚未确认退出，请暂停操作；本次任务没有继续派发步骤。"); }
        }
    }

    internal static async Task<string> ReadBoundedAsync(TextReader reader, int limit, CancellationToken token)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                if (count > limit - builder.Length) throw InvalidResponse();
                builder.Append(buffer, 0, count);
            }
            return builder.ToString();
        }
        finally { Array.Clear(buffer); builder.Clear(); }
    }

    private static void KillOwnedChild(Process child)
    {
        try { if (!child.HasExited) child.Kill(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private static DesktopTaskException InvalidResponse() => new("desktop_visual_worker_response", "看图操作没有返回可信结果，已停止。");
}

internal static class DesktopVisualWorkerEntry
{
    internal static async Task<int> RunAsync()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        DesktopVisualObservation? observation = null;
        try
        {
            var input = await DesktopVisualWorkerClient.ReadBoundedAsync(Console.In, 160001, CancellationToken.None);
            var request = JsonSerializer.Deserialize<DesktopVisualWorkerRequest>(input, DesktopTaskWorkerClient.Json);
            if (request is null) return 1;
            var automation = new WindowsDesktopVisualAutomation();
            DesktopVisualWorkerResponse response;
            if (request.Operation == "Observe" && request.Snapshot is null && request.Proposal is null)
            {
                observation = await automation.ObserveAsync(request.Window, CancellationToken.None);
                var bytes = observation.PngBytes.ToArray();
                try
                {
                    response = new(true, Snapshot: observation.Snapshot, Png: bytes);
                    Console.Write(JsonSerializer.Serialize(response, DesktopTaskWorkerClient.Json));
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            else if (request.Operation == "Execute" && request.Snapshot is { } snapshot && request.Proposal is { } proposal
                && request.Window == snapshot.Window)
            {
                response = new(true, Receipt: await automation.ExecuteAsync(snapshot, proposal, CancellationToken.None));
                Console.Write(JsonSerializer.Serialize(response, DesktopTaskWorkerClient.Json));
            }
            else return 1;
            return 0;
        }
        catch (DesktopTaskException exception)
        {
            Console.Write(JsonSerializer.Serialize(new DesktopVisualWorkerResponse(false, exception.Code, exception.Message), DesktopTaskWorkerClient.Json));
            return 0;
        }
        catch
        {
            Console.Write(JsonSerializer.Serialize(new DesktopVisualWorkerResponse(false, "desktop_visual_worker_error", "看图工作进程无法确认操作结果。"), DesktopTaskWorkerClient.Json));
            return 0;
        }
        finally { observation?.Dispose(); }
    }
}
