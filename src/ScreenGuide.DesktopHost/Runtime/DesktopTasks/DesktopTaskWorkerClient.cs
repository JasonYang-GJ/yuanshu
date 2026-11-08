using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

internal sealed class DesktopTaskWorkerClient : IDesktopTaskAutomation
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public async Task<DesktopTaskWindow> BindAsync(long windowHandle, CancellationToken cancellationToken) =>
        (await RunAsync(new("Bind", windowHandle), cancellationToken)).Window
            ?? throw InvalidResponse();

    public async Task<DesktopTaskSnapshot> ObserveAsync(DesktopTaskWindow window, CancellationToken cancellationToken) =>
        (await RunAsync(new("Observe", window.Handle, window), cancellationToken)).Snapshot
            ?? throw InvalidResponse();

    public async Task<DesktopTaskActionReceipt> ExecuteAsync(DesktopTaskWindow window, DesktopTaskControl control,
        DesktopTaskProposal action, CancellationToken cancellationToken, string? reviewedContextFingerprint = null) =>
        (await RunAsync(new("Execute", window.Handle, window, control, action, reviewedContextFingerprint), cancellationToken)).Receipt
            ?? throw InvalidResponse();

    private static async Task<DesktopTaskWorkerResponse> RunAsync(DesktopTaskWorkerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        start.ArgumentList.Add("--desktop-task-worker");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Execution locates the control, re-observes the full consented context,
        // checks the final native boundary and reads back the result. Real Word
        // UIA takes longer than one observation; cancellation still kills this
        // owned worker immediately, independently of this bounded deadline.
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Operation == "Execute" ? 45 : 15));
        using var child = Process.Start(start) ?? throw new DesktopTaskException("desktop_task_worker_start", "桌面操作进程未能启动。");
        try
        {
            // No payload is sent until crash-safe ownership is established.
            using var lifetime = new DesktopTaskWorkerLifetime(child);
            using var stop = timeout.Token.Register(() => KillOwnedChild(child));
            var output = child.StandardOutput.ReadLineAsync(timeout.Token).AsTask();
            var errors = child.StandardError.ReadToEndAsync(timeout.Token);
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), timeout.Token);
            child.StandardInput.Close();
            var line = await output;
            await child.WaitForExitAsync(timeout.Token);
            _ = await errors;
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null || line.Length > 240000 || child.ExitCode != 0) throw InvalidResponse();
            var result = JsonSerializer.Deserialize<DesktopTaskWorkerResponse>(line, Json) ?? throw InvalidResponse();
            if (!result.Succeeded)
                throw new DesktopTaskException(result.ErrorCode ?? "desktop_task_worker_failed", result.Message ?? "操作未能可靠完成，已停止。");
            return result;
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            KillOwnedChild(child);
            cancellationToken.ThrowIfCancellationRequested();
            throw new DesktopTaskException("desktop_task_worker_timeout", "软件控件响应超时，本次操作已停止；不会继续派发步骤。");
        }
        finally { KillOwnedChild(child); }
    }

    private static void KillOwnedChild(Process child)
    {
        try { if (!child.HasExited) child.Kill(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static DesktopTaskException InvalidResponse() => new("desktop_task_worker_response", "桌面操作没有返回可信结果，已停止。");
}
