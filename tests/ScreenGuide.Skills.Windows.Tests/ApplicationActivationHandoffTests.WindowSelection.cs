namespace ScreenGuide.Skills.Windows.Tests;

public sealed partial class ApplicationActivationHandoffTests
{
    [Theory]
    [InlineData(0x00000080u)]
    [InlineData(0x08000000u)]
    public void AuxiliaryWindowShownBeforeMainWindowIsNeverActivated(uint auxiliaryStyle)
    {
        var main = ExistingApplication() with { ClassName = "SyntheticMain" };
        var auxiliary = main with { Handle = new IntPtr(2000), ClassName = "SyntheticHelper", ExtendedStyle = auxiliaryStyle };
        var runtime = new ScriptedActivationRuntime([auxiliary], [auxiliary, main]);

        var result = new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1)).OpenApplicationVisible(
            new("example", "示例软件", main.Process.ExecutablePath!), CancellationToken.None);

        Assert.Equal(main.Handle.ToInt64(), result.WindowHandle);
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(1, runtime.WaitCount);
        Assert.Equal(1, runtime.ActivationCount);
    }

    [Fact]
    public void MusicStartupHintCannotReplaceTheMainBrowserWindow()
    {
        var main = MusicMainWindow();
        var hint = main with { Handle = new IntPtr(2000), Title = "Synthetic Music Hint", ClassName = "SystemHintWindow" };
        var runtime = new ScriptedActivationRuntime([hint], [hint, main]);

        var result = new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1)).OpenApplicationVisible(
            MusicApplication(), CancellationToken.None);

        Assert.Equal(main.Handle.ToInt64(), result.WindowHandle);
        Assert.Equal(1, runtime.WaitCount);
        Assert.Equal(1, runtime.ActivationCount);
    }

    [Fact]
    public void MusicAlreadyOpenReusesTheMainWindowAlongsideItsHelpers()
    {
        var main = MusicMainWindow();
        var hint = main with { Handle = new IntPtr(2000), ClassName = "SystemHintWindow" };
        var runtime = new ScriptedActivationRuntime([hint, main]) { BeforeStartWindows = [hint, main] };

        var result = new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(3)).OpenApplicationVisible(
            MusicApplication(), CancellationToken.None);

        Assert.Equal(main.Handle.ToInt64(), result.WindowHandle);
        Assert.Equal(0, runtime.StartCount);
        Assert.Equal(0, runtime.WaitCount);
        Assert.Equal(1, runtime.ActivationCount);
    }

    [Theory]
    [InlineData("SystemHintWindow")]
    [InlineData("DesktopLyrics")]
    [InlineData("MiniPlayer")]
    [InlineData("")]
    public void MusicHelpersWithoutAMainWindowNeverCountAsSuccessfulOpen(string className)
    {
        var helper = MusicMainWindow() with { ClassName = className };
        var runtime = new ScriptedActivationRuntime([helper]);

        Assert.Throws<InstalledApplicationResolutionException>(() =>
            new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(300)).OpenApplicationVisible(
                MusicApplication(), CancellationToken.None));

        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(0, runtime.ActivationCount);
    }

    [Fact]
    public void ARecognizedMainWindowClassDoesNotAuthorizeAnotherExecutable()
    {
        var impostor = MusicMainWindow() with
        {
            Process = MusicMainWindow().Process with { ExecutablePath = @"C:\Other\cloudmusic.exe" }
        };
        var runtime = new ScriptedActivationRuntime([impostor]);

        Assert.Throws<InstalledApplicationResolutionException>(() =>
            new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(300)).OpenApplicationVisible(
                MusicApplication(), CancellationToken.None));

        Assert.Equal(0, runtime.ActivationCount);
    }

    [Fact]
    public void WaitingForTheActualMainWindowRemainsCancellable()
    {
        using var cancellation = new CancellationTokenSource();
        var helper = MusicMainWindow() with { ClassName = "SystemHintWindow" };
        var runtime = new ScriptedActivationRuntime([helper]) { BeforeWait = cancellation.Cancel };

        Assert.Throws<OperationCanceledException>(() =>
            new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1)).OpenApplicationVisible(
                MusicApplication(), cancellation.Token));

        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(0, runtime.ActivationCount);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("style")]
    [InlineData("handle")]
    public void AlreadySelectedWindowCannotBecomeAHelperOrMoveDuringActivation(string change)
    {
        var main = MusicMainWindow();
        var changed = false;
        var runtime = new ScriptedActivationRuntime([main])
        {
            BeforeStartWindows = [main], BeforeActivate = () => changed = true,
            ReadWindowOverride = _ => !changed ? main : change switch
            {
                "class" => main with { ClassName = "SystemHintWindow" },
                "style" => main with { ExtendedStyle = 0x08000000u },
                _ => main with { Handle = new IntPtr(4000) }
            }
        };

        Assert.Throws<InstalledApplicationResolutionException>(() =>
            new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1)).OpenApplicationVisible(
                MusicApplication(), CancellationToken.None));

        Assert.Equal(0, runtime.StartCount);
        Assert.Equal(0, runtime.WaitCount);
        Assert.Equal(1, runtime.ActivationCount);
    }

    [Fact]
    public void NormalMainWindowMayChangeTitleAndTopmostFlagDuringActivation()
    {
        var main = MusicMainWindow();
        var changed = false;
        var runtime = new ScriptedActivationRuntime([main])
        {
            BeforeStartWindows = [main], BeforeActivate = () => changed = true,
            ReadWindowOverride = _ => !changed ? main : main with { Title = "合成曲目", ExtendedStyle = 0x00000008u }
        };

        var result = new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1)).OpenApplicationVisible(
            MusicApplication(), CancellationToken.None);

        Assert.Equal(main.Handle.ToInt64(), result.WindowHandle);
        Assert.Equal(0, runtime.StartCount);
        Assert.Equal(1, runtime.ActivationCount);
    }

    private static KnownDesktopApplication MusicApplication() => new("synthetic-music", "合成音乐", @"C:\Fixture\cloudmusic.exe");

    private static ApplicationWindowSnapshot MusicMainWindow() => new(new IntPtr(3000), "合成音乐主窗口",
        new(300, "cloudmusic", AttemptTime.AddMinutes(-10), true, MusicApplication().LaunchTarget, null, null, null))
        { ClassName = "OrpheusBrowserHost" };
}
