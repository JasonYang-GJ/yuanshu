namespace ScreenGuide.Skills.Windows.Tests;

// Exercises the real workflow driver, UI automation service and safe executor together.
// Only the OS-facing launcher/window/input providers are fake; no UIA session or app is opened.
public sealed partial class SearchReadinessAuthorityTests
{
    private const string Query = "示例歌曲";
    private static readonly KnownDesktopApplication App = new("test-player", "合成播放器", @"C:\fixtures\cloudmusic.exe");
    private static readonly ForegroundWindowSnapshot Window = new(42, "合成播放器", "cloudmusic", 123,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1));

    [Fact]
    public void TwoReadyHintsCannotAuthorizeAmbiguousFinalCandidates()
    {
        var setup = new Harness();
        setup.Prepare();
        setup.Input.Controls = [Control("final-a", "input-a"), Control("final-b", "input-b")];

        var error = Assert.Throws<DesktopSearchException>(() => setup.Search());

        Assert.Equal(DesktopSearchErrorCodes.CandidateAmbiguous, error.Code);
        Assert.Equal(2, setup.HintReads);
        Assert.Equal(1, setup.Input.Discoveries);
        AssertNoInput(setup.Input);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("read-only")]
    [InlineData("disabled")]
    [InlineData("offscreen")]
    [InlineData("no-value-pattern")]
    [InlineData("unbound")]
    public void PreparedHintsDoNotBypassTheFinalCandidatesSafetyChecks(string unsafeState)
    {
        var setup = new Harness();
        setup.Prepare();
        var final = Control("final-a", "input-a");
        setup.Input.Controls = [final with
        {
            Snapshot = final.Snapshot with
            {
                IsPassword = unsafeState == "password",
                IsReadOnly = unsafeState == "read-only",
                IsEnabled = unsafeState != "disabled",
                IsOffscreen = unsafeState == "offscreen",
                SupportsValuePattern = unsafeState != "no-value-pattern",
                NativeSearchIdentity = unsafeState == "unbound" ? "" : final.Snapshot.NativeSearchIdentity,
                HasNativeSearchSemantics = unsafeState != "unbound"
            }
        }];

        Assert.Throws<DesktopSearchException>(() => setup.Search());

        Assert.Equal(1, setup.Input.Discoveries);
        AssertNoInput(setup.Input);
    }

    [Fact]
    public void PreparationIsReadOnlyAndActualSearchUsesAFreshlyDiscoveredControl()
    {
        var setup = new Harness();
        setup.Prepare();

        Assert.Equal(2, setup.HintReads);
        Assert.Equal(0, setup.PageReads);
        Assert.Equal(0, setup.Input.Discoveries);
        AssertNoInput(setup.Input);

        setup.Search();

        Assert.Equal(2, setup.HintReads);
        Assert.Equal(1, setup.PageReads);
        Assert.Equal(1, setup.Input.Discoveries);
        Assert.Equal(1, setup.Input.Focuses);
        Assert.Equal(1, setup.Input.Writes);
        Assert.Equal(1, setup.Input.Submissions);
        Assert.Equal("final-a", setup.Input.LastWrittenReference);
        Assert.Equal(Query, setup.Input.LastWrittenValue);
    }

    [Theory]
    [InlineData("foreground")]
    [InlineData("pid")]
    [InlineData("process-start")]
    [InlineData("executable")]
    public void PreparedHintsDoNotSurviveAChangedTarget(string change)
    {
        var setup = new Harness();
        setup.Prepare();
        switch (change)
        {
            case "foreground": setup.Windows.Foreground = 43; break;
            case "pid": setup.Windows.Current = Window with { ProcessId = 456 }; break;
            case "process-start": setup.Windows.Current = Window with { ProcessStartTimeUtc = Window.ProcessStartTimeUtc.AddSeconds(1) }; break;
            case "executable": setup.Windows.Executable = @"C:\fixtures\other.exe"; break;
        }

        Assert.Throws<ApplicationSearchWorkflowException>(() => setup.Search());

        Assert.Equal(0, setup.Input.Discoveries);
        Assert.Equal(0, setup.PageReads);
        AssertNoInput(setup.Input);
    }

    [Fact]
    public void CancellationAfterPreparationPreventsFinalDiscoveryAndInput()
    {
        using var cancellation = new CancellationTokenSource();
        var setup = new Harness();
        setup.Prepare(cancellation.Token);
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => setup.Search(cancellation.Token));

        Assert.Equal(0, setup.Input.Discoveries);
        Assert.Equal(0, setup.PageReads);
        AssertNoInput(setup.Input);
    }

    [Fact]
    public void CancellationInsideFinalDiscoveryIsCheckedBeforeFocus()
    {
        using var cancellation = new CancellationTokenSource();
        var setup = new Harness();
        setup.Prepare(cancellation.Token);
        setup.Input.OnDiscover = cancellation.Cancel;

        Assert.ThrowsAny<OperationCanceledException>(() => setup.Search(cancellation.Token));

        Assert.Equal(1, setup.Input.Discoveries);
        AssertNoInput(setup.Input);
    }

    [Fact]
    public void FinalDiscoveryCannotContinueAfterTheUserChangesForeground()
    {
        var setup = new Harness();
        setup.Prepare();
        setup.Input.OnDiscover = () => setup.Windows.Foreground = 43;

        Assert.Throws<ApplicationSearchWorkflowException>(() => setup.Search());

        Assert.Equal(1, setup.Input.Discoveries);
        AssertNoInput(setup.Input);
    }

    [Fact]
    public void ReadyHintsDoNotPermitRetryingAMissingFinalCandidate()
    {
        var setup = new Harness();
        setup.Prepare();
        setup.Input.Controls = [];

        var error = Assert.Throws<DesktopSearchException>(() => setup.Search());

        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
        Assert.Equal(1, setup.Input.Discoveries);
        Assert.Equal(2, setup.HintReads);
        AssertNoInput(setup.Input);
    }

    [Theory]
    [InlineData("focus")]
    [InlineData("value")]
    [InlineData("native-identity")]
    [InlineData("foreground")]
    public void SubmitBoundaryFailureAfterAWriteNeverReplaysTheSearch(string change)
    {
        var setup = new Harness();
        setup.Prepare();
        setup.Input.BeforeSubmit = () =>
        {
            if (change == "foreground") setup.Windows.Foreground = 43;
            else setup.Input.Mutate("final-a", state => change switch
            {
                "focus" => state with { HasKeyboardFocus = false },
                "value" => state with { Value = "其他内容" },
                _ => state with { NativeSearchIdentity = "replacement" }
            });
        };

        if (change == "foreground") Assert.Throws<ApplicationSearchWorkflowException>(() => setup.Search());
        else Assert.Throws<DesktopSearchException>(() => setup.Search());

        Assert.Equal(1, setup.Input.Discoveries);
        Assert.Equal(1, setup.Input.Focuses);
        Assert.Equal(1, setup.Input.Writes);
        Assert.Equal(0, setup.Input.Submissions);
        Assert.Equal(2, setup.HintReads);
    }

    private static void AssertNoInput(InputDriver input)
    {
        Assert.Equal(0, input.Focuses);
        Assert.Equal(0, input.Writes);
        Assert.Equal(0, input.Submissions);
    }

    private static DesktopSearchControl Control(string reference, string nativeIdentity) => new(reference,
        new DesktopSearchControlSnapshot("uia:" + reference, Window.WindowHandle, "", "", "", "",
            true, false, false, false, true, false, new(10, 10, 200, 30), "", nativeIdentity, true));

    private sealed class Harness
    {
        private readonly WindowsSearchResultDriver _workflow;
        private ForegroundWindowSnapshot? _opened;
        public Windows Windows { get; } = new();
        public InputDriver Input { get; }
        public RecordingAutomation Automation { get; }
        public Action ValidateTarget { get; set; } = () => { };
        public int HintReads { get; private set; }
        public int PageReads { get; private set; }
        public int ResultDiscoveries { get; private set; }
        public IReadOnlyList<DesktopSearchControl> ResultInputs { get; set; } = [];
        public Func<int, NativeSearchPageObservation> ObservePage { get; set; } = count => count == 1
            ? new(true, false, "", "", "", false, [])
            : new(true, true, Query, "result-page", "new-results", false, [new("song-a", "示例歌曲", new object())]);

        public Harness()
        {
            Input = new(Windows);
            Automation = new(new WindowsUiAutomationService(Windows, Input));
            var clock = new ReadinessClock();
            _workflow = new(new Launcher(), Automation, Windows,
                _ => Windows.Native, () => Windows.Foreground,
                _ => { HintReads++; return [Control("hint-a", "input-a")]; }, clock,
                (duration, cancellation) => { cancellation.ThrowIfCancellationRequested(); clock.Advance(duration); },
                (_, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    PageReads++;
                    return ObservePage(PageReads);
                }, discoverResultInputs: _ => { ResultDiscoveries++; return ResultInputs; });
        }

        public void Open(CancellationToken cancellation = default) => _opened = _workflow.Open(App, cancellation);

        public void Prepare(CancellationToken cancellation = default)
        {
            Open(cancellation);
            _workflow.PrepareSearch(_opened!, cancellation);
        }

        public void Search(CancellationToken cancellation = default) =>
            _workflow.Search(_opened ?? throw new InvalidOperationException("Prepare first"), Query, ValidateTarget, cancellation);

        public SearchResultPage ReadResults(string query = Query, CancellationToken cancellation = default) =>
            _workflow.ReadResults(Windows.Current, query, cancellation);

        public bool VerifyReceipt(string query = Query, CancellationToken cancellation = default) =>
            (Automation.LastResult?.VerifySubmittedInput ?? throw new InvalidOperationException("No submitted-input receipt"))
                (Windows.Current, query, cancellation);
    }

    // Transparent recording wrapper around the real service, so tests can inspect its receipt.
    private sealed class RecordingAutomation(WindowsUiAutomationService service) : IReliableDesktopAutomation,
        IBoundApplicationSearchAutomation
    {
        public DesktopAutomationResult? LastResult { get; private set; }
        public DesktopAutomationResult Search(ForegroundWindowSnapshot window, string query) => Search(window, query, default);
        public DesktopAutomationResult Search(ForegroundWindowSnapshot window, string query, CancellationToken cancellation)
        {
            LastResult = null;
            return LastResult = service.Search(window, query, cancellation);
        }
        public DesktopAutomationResult SearchBoundApplication(ForegroundWindowSnapshot window, string query,
            DesktopSearchControl? locator, Func<CancellationToken, ForegroundWindowSnapshot> validateWindow,
            CancellationToken cancellation)
        {
            LastResult = null;
            return LastResult = service.SearchBoundApplication(window, query, locator, validateWindow, cancellation);
        }
        public DesktopAutomationResult Describe(ForegroundWindowSnapshot window) => throw new NotSupportedException();
    }

    private sealed class Windows : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot Current { get; set; } = Window;
        public long Foreground { get; set; } = Window.WindowHandle;
        public string Executable { get; set; } = App.LaunchTarget;
        public ApplicationWindowSnapshot Native => new(new IntPtr(Current.WindowHandle), Current.WindowTitle,
            new(Current.ProcessId, Current.ProcessName, Current.ProcessStartTimeUtc, true, Executable, null, null, null));
        public ForegroundWindowSnapshot? GetLastExternalWindow() => Current;
        public ForegroundWindowSnapshot? ResolveWindow(long handle) => handle == Current.WindowHandle ? Current : null;
    }

    private sealed class Launcher : IDesktopProcessLauncher
    {
        public int? Start(string target) => throw new NotSupportedException();
        public VisibleDesktopLaunchResult OpenApplicationVisible(string target) => new(Window.ProcessId, Window.WindowHandle, Window.WindowTitle);
        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? target, Uri website) => throw new NotSupportedException();
    }

    private sealed class ReadinessClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class InputDriver(Windows windows) : IDesktopSearchAutomationDriver
    {
        private readonly Dictionary<string, DesktopSearchControlSnapshot> _states = new(StringComparer.Ordinal);
        public IReadOnlyList<DesktopSearchControl> Controls { get; set; } = [Control("final-a", "input-a")];
        public Action? OnDiscover { get; set; }
        public Action? OnRead { get; set; }
        public Action? AfterFocus { get; set; }
        public Action? AfterWrite { get; set; }
        public Action? BeforeSubmit { get; set; }
        public int Discoveries { get; private set; }
        public int Reads { get; private set; }
        public int Focuses { get; private set; }
        public int Writes { get; private set; }
        public int Submissions { get; private set; }
        public string? LastWrittenReference { get; private set; }
        public string? LastWrittenValue { get; private set; }
        public bool IsWindow(long handle) => handle == windows.Current.WindowHandle;
        public bool TryActivate(long handle) => handle == windows.Foreground;
        public long GetForegroundWindowHandle() => windows.Foreground;

        public IReadOnlyList<DesktopSearchControl> Discover(long handle)
        {
            Discoveries++;
            OnDiscover?.Invoke();
            foreach (var control in Controls) _states[(string)control.NativeReference] = control.Snapshot;
            return Controls;
        }

        public DesktopSearchControlSnapshot Read(DesktopSearchControl control)
        {
            Reads++;
            OnRead?.Invoke();
            return _states[(string)control.NativeReference];
        }
        public void Mutate(string reference, Func<DesktopSearchControlSnapshot, DesktopSearchControlSnapshot> change) =>
            _states[reference] = change(_states[reference]);
        public void Focus(DesktopSearchControl control)
        {
            Focuses++;
            Mutate((string)control.NativeReference, state => state with { HasKeyboardFocus = true });
            AfterFocus?.Invoke();
        }
        public void SetValue(DesktopSearchControl control, string value)
        {
            Writes++;
            LastWrittenReference = (string)control.NativeReference;
            LastWrittenValue = value;
            Mutate(LastWrittenReference, state => state with { Value = value });
            AfterWrite?.Invoke();
        }
        public void SubmitEnter(DesktopSearchControl control, Action validateAtSendBoundary)
        {
            BeforeSubmit?.Invoke();
            validateAtSendBoundary();
            Submissions++;
        }
    }
}
