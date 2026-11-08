namespace ScreenGuide.Skills.Windows.Tests;

public sealed partial class ApplicationActivationHandoffTests
{
    [Theory]
    [InlineData(false, "cef.win64")]
    [InlineData(true, "cef.win64")]
    [InlineData(false, "cef.win7x64")]
    [InlineData(true, "cef.win7x64")]
    public void SteamOpensItsVerifiedHelperMainWindowAndIgnoresOffers(bool alreadyOpen, string layout)
    {
        var main = SteamMainWindow(layout);
        var offers = main with { Handle = new IntPtr(4200), Title = "特惠" };
        var runtime = new ScriptedActivationRuntime([offers, main])
        { BeforeStartWindows = alreadyOpen ? [offers, main] : [] };
        var result = new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1))
            .OpenApplicationVisible(SteamApplication(), CancellationToken.None);
        Assert.Equal(main.Handle.ToInt64(), result.WindowHandle);
        Assert.Equal(alreadyOpen ? 0 : 1, runtime.StartCount);
        Assert.Equal(1, runtime.ActivationCount);
        // Opening a helper window does not expand application-internal search authority.
        Assert.False(TrustedApplicationActivation.MatchesRegisteredTarget(SteamApplication(), main.Process));
    }

    [Theory]
    [InlineData("parent-missing")]
    [InlineData("parent-path")]
    [InlineData("parent-exited")]
    [InlineData("parent-reused")]
    [InlineData("helper-path")]
    [InlineData("game")]
    [InlineData("offers-only")]
    [InlineData("class")]
    public void SteamHelperCannotBorrowAnotherIdentity(string change)
    {
        var main = SteamMainWindow();
        main = change switch
        {
            "parent-missing" => main with { Process = main.Process with { ParentProcess = null } },
            "parent-path" => main with { Process = main.Process with { ParentProcess = main.Process.ParentProcess! with { ExecutablePath = @"C:\Other\steam.exe" } } },
            "parent-exited" => main with { Process = main.Process with { ParentProcess = main.Process.ParentProcess! with { IsRunning = false } } },
            "parent-reused" => main with { Process = main.Process with { ParentProcess = main.Process.ParentProcess! with { ProcessStartTimeUtc = main.Process.ProcessStartTimeUtc.AddSeconds(1) } } },
            "helper-path" => main with { Process = main.Process with { ExecutablePath = @"C:\Other\steamwebhelper.exe" } },
            "game" => main with { Process = main.Process with { ProcessName = "game", ExecutablePath = @"C:\Fixture\steamapps\game.exe" } },
            "offers-only" => main with { Title = "特惠" },
            _ => main with { ClassName = "UntrustedWindow" }
        };
        var runtime = new ScriptedActivationRuntime([main]);
        Assert.Throws<InstalledApplicationResolutionException>(() =>
            new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(300)).OpenApplicationVisible(SteamApplication(), CancellationToken.None));
        Assert.Equal(0, runtime.ActivationCount);
    }

    [Fact]
    public void TwoSteamMainWindowsAreAmbiguous()
    {
        var main = SteamMainWindow();
        var runtime = new ScriptedActivationRuntime([main, main with { Handle = new IntPtr(4300) }]);
        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1)).OpenApplicationVisible(SteamApplication(), CancellationToken.None));
        Assert.Equal(InstalledApplicationErrorCodes.Ambiguous, error.Code);
        Assert.Equal(0, runtime.ActivationCount);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("parent-started")]
    [InlineData("parent-path")]
    [InlineData("child-reused")]
    [InlineData("child-path")]
    [InlineData("title")]
    [InlineData("cancel")]
    public void SteamBindingIsRecheckedAfterActivation(string change)
    {
        var main = SteamMainWindow();
        var changed = false;
        using var cancellation = new CancellationTokenSource();
        var runtime = new ScriptedActivationRuntime([main])
        {
            BeforeStartWindows = [main],
            BeforeActivate = () => { changed = true; if (change == "cancel") cancellation.Cancel(); },
            ReadWindowOverride = _ => !changed ? main : change switch
            {
                "title" => main with { Title = "特惠" },
                "parent-started" => main with { Process = main.Process with { ParentProcess = main.Process.ParentProcess! with { ProcessStartTimeUtc = AttemptTime } } },
                "parent-path" => main with { Process = main.Process with { ParentProcess = main.Process.ParentProcess! with { ExecutablePath = @"C:\Other\steam.exe" } } },
                "child-reused" => main with { Process = main.Process with { ProcessStartTimeUtc = AttemptTime } },
                "child-path" => main with { Process = main.Process with { ExecutablePath = @"C:\Other\steamwebhelper.exe" } },
                _ => main with { Process = main.Process with { ParentProcess = main.Process.ParentProcess! with { ProcessId = 999 } } }
            }
        };
        var launcher = new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1));
        if (change == "cancel") Assert.Throws<OperationCanceledException>(() => launcher.OpenApplicationVisible(SteamApplication(), cancellation.Token));
        else Assert.Throws<InstalledApplicationResolutionException>(() => launcher.OpenApplicationVisible(SteamApplication(), cancellation.Token));
        Assert.Equal(0, runtime.StartCount);
    }

    private static KnownDesktopApplication SteamApplication() => new("synthetic-steam", "Steam", @"C:\Fixture\steam.exe");

    [Fact]
    public void ColdSteamIdentityChangeCannotSwitchToAnotherWindow()
    {
        var main = SteamMainWindow();
        var replacement = main with { Handle = new IntPtr(4300) };
        var runtime = new ScriptedActivationRuntime([main], [replacement])
        {
            ReadWindowOverride = handle => handle == main.Handle
                ? main with { Process = main.Process with { ParentProcess = null } }
                : replacement
        };
        var error = Assert.Throws<InstalledApplicationResolutionException>(() =>
            new DesktopProcessLauncher(runtime, TimeSpan.FromSeconds(1))
                .OpenApplicationVisible(SteamApplication(), CancellationToken.None));
        Assert.Equal(InstalledApplicationErrorCodes.TargetChanged, error.Code);
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(0, runtime.WaitCount);
        Assert.Equal(0, runtime.ActivationCount);
    }

    [Fact]
    public void SteamHelperCannotSatisfyADifferentApplicationTarget()
    {
        var runtime = new ScriptedActivationRuntime([SteamMainWindow()]);
        Assert.Throws<InstalledApplicationResolutionException>(() =>
            new DesktopProcessLauncher(runtime, TimeSpan.FromMilliseconds(300)).OpenApplicationVisible(
                new("other", "Other", @"C:\Fixture\other.exe"), CancellationToken.None));
        Assert.Equal(0, runtime.ActivationCount);
    }

    private static ApplicationWindowSnapshot SteamMainWindow(string layout = "cef.win64") => new(new IntPtr(4100), "Steam",
        new(200, "steamwebhelper", AttemptTime.AddMinutes(-1), true,
            Path.Combine(@"C:\Fixture\bin\cef", layout, "steamwebhelper.exe"), null, null, null)
        { ParentProcess = new(100, "steam", AttemptTime.AddMinutes(-2), true, SteamApplication().LaunchTarget, null, null, null) })
        { ClassName = "SDL_app" };
}
