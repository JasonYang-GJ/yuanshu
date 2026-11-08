using System.Diagnostics;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class DesktopLaunchNotificationTests
{
    [Fact]
    public void NativeLaunchNotifiesAfterStartButBeforeWaitingForVisibleWindow()
    {
        var runtime = new Runtime();
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1));
        var notified = false;
        runtime.OnWait = () => Assert.True(notified);
        launcher.OpenApplicationVisible(App, CancellationToken.None, () =>
        {
            Assert.True(runtime.Started);
            Assert.False(runtime.Ready);
            notified = true;
        });
        Assert.True(notified);
        Assert.Equal(1, runtime.StartCount);
    }

    [Fact]
    public void AlreadyVisibleWindowNotifiesAfterExactActivationWithoutStartingAnotherProcess()
    {
        var runtime = new Runtime { Ready = true };
        var notified = 0;
        new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1)).OpenApplicationVisible(App, CancellationToken.None,
            () => { Assert.True(runtime.Activated); notified++; });
        Assert.Equal(1, notified);
        Assert.Equal(0, runtime.StartCount);
    }

    [Fact]
    public void CancellationAtProcessStartDoesNotNotify()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime { OnStart = cancellation.Cancel };
        var notified = false;
        Assert.ThrowsAny<OperationCanceledException>(() => new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1))
            .OpenApplicationVisible(App, cancellation.Token, () => notified = true));
        Assert.False(notified);
    }

    [Fact]
    public void LegacyLauncherInterfaceNotifiesOnlyAfterItsExistingOpenCompletes()
    {
        var legacy = new LegacyLauncher();
        IDesktopProcessLauncher launcher = legacy;
        var notified = 0;
        launcher.OpenApplicationVisible(App, CancellationToken.None, () => { Assert.True(legacy.Opened); notified++; });
        Assert.Equal(1, notified);
    }

    private static readonly KnownDesktopApplication App = new("test", "测试音乐", @"C:\Fixture\cloudmusic.exe");
    private static readonly ApplicationWindowSnapshot Window = new(new IntPtr(42), "测试音乐",
        new(123, "cloudmusic", DateTimeOffset.UnixEpoch, true, App.LaunchTarget, null, null, null))
        { ClassName = "OrpheusBrowserHost" };
    private sealed class Runtime : IApplicationActivationRuntime
    {
        private long _ticks;
        public bool Started { get; private set; }
        public bool Ready { get; set; }
        public bool Activated { get; private set; }
        public int StartCount { get; private set; }
        public Action? OnStart { get; init; }
        public Action? OnWait { get; set; }
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public long GetTimestamp() => _ticks;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.FromTicks(_ticks - startingTimestamp);
        public ApplicationProcessLaunch? Start(ProcessStartInfo info)
        { Started = true; StartCount++; OnStart?.Invoke(); return new(123, "cloudmusic"); }
        public IReadOnlyList<ApplicationWindowSnapshot> EnumerateVisibleWindows() => Ready ? [Window] : [];
        public ApplicationWindowSnapshot? ReadWindow(IntPtr handle) => Ready && handle == Window.Handle ? Window : null;
        public bool TryActivate(IntPtr handle, TimeSpan timeout, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Activated = true; return true; }
        public void Wait(TimeSpan delay, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); OnWait?.Invoke(); _ticks += delay.Ticks; Ready = true; }
    }
    private sealed class LegacyLauncher : IDesktopProcessLauncher
    {
        public bool Opened { get; private set; }
        public int? Start(string target) => throw new NotSupportedException();
        public VisibleDesktopLaunchResult OpenApplicationVisible(string target)
        { Opened = true; return new(123, 42, "测试音乐"); }
        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? target, Uri website) => throw new NotSupportedException();
    }
}
