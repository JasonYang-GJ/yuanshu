using Microsoft.Extensions.Hosting;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;
using System.Text;
using System.Text.Json;

namespace ScreenGuide.DesktopHost;

public static class Program
{
    [STAThread]
    public static int Main(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    // Keep the real compiler entry point synchronous: C#'s async-Main wrapper
    // does not inherit STAThread, which the scoped Word COM worker requires.
    private static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args is ["--desktop-visual-worker"]) return await DesktopVisualWorkerEntry.RunAsync();
        if (args is ["--desktop-task-worker"])
        {
            // A worker waits for the owning Host's private stdin. It never starts
            // services, reads credentials, or acquires the normal Host lease.
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                var buffer = new StringBuilder();
                for (var i = 0; i < 32768; i++)
                {
                    var next = Console.Read();
                    if (next < 0) return 1;
                    if (next == '\n') break;
                    buffer.Append((char)next);
                }
                if (buffer.Length >= 32768) return 1;
                var request = JsonSerializer.Deserialize<DesktopTaskWorkerRequest>(buffer.ToString(), DesktopTaskWorkerClient.Json);
                if (request is null) return 1;
                var result = new WindowsDesktopTaskWorker().Execute(request);
                Console.WriteLine(JsonSerializer.Serialize(result, DesktopTaskWorkerClient.Json));
                return 0;
            }
            catch
            {
                Console.WriteLine(JsonSerializer.Serialize(new DesktopTaskWorkerResponse(false,
                    "desktop_task_worker_error", "桌面工作进程无法确认操作结果。"), DesktopTaskWorkerClient.Json));
                return 0;
            }
        }
        var options = DesktopHostOptions.FromEnvironment();
        using var singleInstance = HostSingleInstanceLease.TryAcquire();
        if (singleInstance is null)
        {
            return 0;
        }

        var runOnce = args.Any(argument =>
            string.Equals(argument, "--run-once", StringComparison.OrdinalIgnoreCase));
        var hostArguments = args
            .Where(argument => !string.Equals(
                argument,
                "--run-once",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        IHost? host = null;
        try
        {
            HostStartupStatusStore.Clear(options);
            host = DesktopHostFactory.Build(hostArguments, options);
            if (runOnce)
            {
                await host.StartAsync().ConfigureAwait(false);
                await host.StopAsync().ConfigureAwait(false);
            }
            else
            {
                await host.RunAsync().ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception exception)
        {
            if (host is not null)
            {
                await HostFailureRecorder.TryRecordUnhandledAsync(
                    host.Services,
                    exception,
                    CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                HostStartupStatusStore.Record(options, exception);
            }
            catch
            {
            }

            return 1;
        }
        finally
        {
            host?.Dispose();
        }
    }
}
