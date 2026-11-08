using System.Diagnostics;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopTaskWorkerLifetimeTests
{
    [Fact]
    public void CompiledWorkerEntryPointCarriesStaAttribute()
    {
        var entry = typeof(Program).Assembly.EntryPoint;
        Assert.NotNull(entry);
        Assert.Contains(entry.GetCustomAttributesData(), attribute => attribute.AttributeType == typeof(STAThreadAttribute));
    }
    [Fact]
    public async Task ClosingTheOwningJobTerminatesOnlyItsWaitingWorkerBeforeAnyRequest()
    {
        var assembly = typeof(Program).Assembly.Location;
        var exe = Path.ChangeExtension(assembly, ".exe");
        var start = new ProcessStartInfo(File.Exists(exe) ? exe : "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (!File.Exists(exe)) start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--desktop-task-worker");
        using var worker = Process.Start(start)!;
        try
        {
            using (var lifetime = new DesktopTaskWorkerLifetime(worker))
            {
                await Task.Delay(150);
                Assert.False(worker.HasExited);
                // No JSON is sent: a new worker has no authority or action until
                // its parent has successfully established this crash-safe job.
            }
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(worker.HasExited);
            Assert.Empty(await worker.StandardOutput.ReadToEndAsync());
        }
        finally { if (!worker.HasExited) worker.Kill(); }
    }
}
