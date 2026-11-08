using System.Runtime.InteropServices;
using static ScreenGuide.Skills.Windows.LegacyBrowserSearchSemantics;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed partial class SearchReadinessAuthorityTests
{
    [Fact]
    public void LocatorUsesNewCompleteProofAndNewSnapshotWithoutRepeatingFindFirst()
    {
        var calls = new List<string>();
        var fresh = Control("fresh-token", "input-a");
        var result = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(
            () => { calls.Add("complete-proof"); return new(NativeEditProofState.Unique, "input-a"); },
            () => { calls.Add("find-first"); return null; }, Window.WindowHandle,
            identity => WindowsDesktopSearchAutomationDriver.TryReadPreparationLocator(identity, Window.WindowHandle,
                () => { calls.Add("current-edit-type"); return true; },
                () => { calls.Add("fresh-token-read"); return fresh; }));

        Assert.Same(fresh, result);
        Assert.Equal(new[] { "complete-proof", "current-edit-type", "fresh-token-read" }, calls);
    }

    [Theory]
    [InlineData("control-type")]
    [InlineData("password")]
    [InlineData("read-only")]
    [InlineData("disabled")]
    [InlineData("offscreen")]
    [InlineData("value-pattern")]
    [InlineData("native-id")]
    [InlineData("owner")]
    [InlineData("semantics")]
    [InlineData("pending-container")]
    [InlineData("root-unavailable")]
    [InlineData("parent-unavailable")]
    [InlineData("null")]
    public void InvalidLocatorFallsBackToFreshFindFirstBeforeAnyInput(string changed)
    {
        var freshReads = 0;
        var findFirst = 0;
        var expected = Control("actual-first", "input-a");
        var result = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(
            () => new(NativeEditProofState.Unique, "input-a"),
            () => { findFirst++; return expected; }, Window.WindowHandle,
            identity => WindowsDesktopSearchAutomationDriver.TryReadPreparationLocator(identity, Window.WindowHandle,
                () => changed != "control-type",
                () =>
                {
                    freshReads++;
                    if (changed == "root-unavailable") throw new COMException("Synthetic unavailable root");
                    if (changed == "parent-unavailable") throw new System.Windows.Automation.ElementNotAvailableException();
                    if (changed == "null") return null;
                    var control = Control("old-locator-new-read", "input-a");
                    return control with { Snapshot = control.Snapshot with
                    {
                        IsPassword = changed == "password", IsReadOnly = changed == "read-only",
                        IsEnabled = changed != "disabled", IsOffscreen = changed == "offscreen",
                        SupportsValuePattern = changed != "value-pattern",
                        NativeSearchIdentity = changed == "native-id" ? "different" : "input-a",
                        OwnerWindowHandle = changed == "owner" ? 99 : Window.WindowHandle,
                        HasNativeSearchSemantics = changed != "semantics",
                        NativeSearchContainerPending = changed == "pending-container"
                    } };
                }));

        Assert.Same(expected, result);
        Assert.Equal(1, findFirst);
        Assert.Equal(changed == "control-type" ? 0 : 1, freshReads);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("multiple")]
    [InlineData("incomplete")]
    public void LocatorCannotReplaceACompleteUniqueNativeProof(string state)
    {
        var proofReads = 0;
        var locatorReads = 0;
        var firstReads = 0;
        var proofState = state == "none" ? NativeEditProofState.None
            : state == "multiple" ? NativeEditProofState.Multiple : NativeEditProofState.Incomplete;
        Assert.Null(WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(
            () => { proofReads++; return new(proofState); },
            () => { firstReads++; return Control("first", "input-a"); }, Window.WindowHandle,
            _ => { locatorReads++; return Control("prepared", "input-a"); }));
        Assert.Equal(state == "incomplete" ? 2 : 1, proofReads);
        Assert.Equal(0, locatorReads);
        Assert.Equal(0, firstReads);
    }

    [Fact]
    public void LocatorReadCancellationDoesNotFallBackOrWrite()
    {
        var firstReads = 0;
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(
            () => new(NativeEditProofState.Unique, "input-a"),
            () => { firstReads++; return Control("first", "input-a"); }, Window.WindowHandle,
            identity => WindowsDesktopSearchAutomationDriver.TryReadPreparationLocator(identity, Window.WindowHandle, () => true,
                () => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); return null; })));
        Assert.Equal(0, firstReads);
    }

    [Fact]
    public void LocatorValidForOneRootCannotHideAnotherRootsSearchInput()
    {
        var windows = new Windows();
        var input = new InputDriver(windows);
        var firstRoot = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(
            () => new(NativeEditProofState.Unique, "input-a"), () => throw new InvalidOperationException(), Window.WindowHandle,
            identity => WindowsDesktopSearchAutomationDriver.TryReadPreparationLocator(identity, Window.WindowHandle,
                () => true, () => Control("root-a-locator", "input-a")));
        var secondRoot = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(
            () => new(NativeEditProofState.Unique, "input-b"), () => Control("root-b-first", "input-b"), Window.WindowHandle,
            identity => WindowsDesktopSearchAutomationDriver.TryReadPreparationLocator(identity, Window.WindowHandle,
                () => true, () => Control("root-a-locator", "input-a")));
        input.Controls = [firstRoot!, secondRoot!];
        var service = new WindowsUiAutomationService(windows, input);
        var error = Assert.Throws<DesktopSearchException>(() => service.Search(Window, Query));
        Assert.Equal(DesktopSearchErrorCodes.CandidateAmbiguous, error.Code);
        AssertNoInput(input);
    }

    [Fact]
    public void PreparationLocatorDoesNotReplaceAnInjectedExecutorWithARealOsDriver()
    {
        var windows = new Windows();
        var input = new InputDriver(windows);
        var service = new WindowsUiAutomationService(windows, input);
        var result = service.SearchWithPreparationLocator(Window, Query, Control("synthetic-locator", "input-a"), default);
        Assert.True(result.Verified);
        Assert.Equal(1, input.Discoveries);
        Assert.Equal("final-a", input.LastWrittenReference);
        Assert.Equal(1, input.Writes);
        Assert.Equal(1, input.Submissions);
    }

    [Fact]
    public void TwoStableHintsAreConsumedByOnlyTheNextSearch()
    {
        var setup = new LocatorHarness();
        setup.Prepare();
        setup.Search();
        Assert.Equal("first-hint", setup.Automation.Locators.Single()!.NativeReference);
        setup.HintReference = "next-search-hint";
        setup.Search();
        Assert.Equal(new[] { "first-hint", "next-search-hint" }, setup.Automation.Locators.Select(item => item!.NativeReference));
        Assert.Equal(4, setup.HintReads);
        Assert.Equal(2, setup.Input.Writes);
        Assert.Equal(2, setup.Input.Submissions);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("prepare-failure")]
    [InlineData("search-cancelled")]
    [InlineData("search-page-failure")]
    public void OpenFailureOrCancellationCannotReuseAnEarlierPreparationLocator(string scenario)
    {
        var setup = new LocatorHarness();
        setup.Prepare();
        if (scenario == "open") setup.Open();
        if (scenario == "prepare-failure")
        {
            setup.OnHint = () => throw new OperationCanceledException();
            Assert.Throws<OperationCanceledException>(() => setup.Prepare());
            setup.OnHint = null;
        }
        if (scenario == "search-cancelled")
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => setup.Search(cancellation.Token));
        }
        if (scenario == "search-page-failure")
        {
            setup.OnPage = () => throw new InvalidOperationException("Synthetic page read failed");
            Assert.Throws<InvalidOperationException>(() => setup.Search());
            setup.OnPage = null;
        }
        Assert.Empty(setup.Automation.Locators);
        AssertNoInput(setup.Input);
        setup.HintReference = "replacement-hint";
        setup.Search();
        Assert.Equal("replacement-hint", setup.Automation.Locators.Single()!.NativeReference);
        Assert.Equal(1, setup.Input.Writes);
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(true, true, "renderer")]
    [InlineData(false, false, null)]
    public void EmptyRendererOnlyPrimesTopProviderWithoutReturningATopHint(bool preparation, bool rendererHasInput, string? expected)
    {
        var topReads = 0;
        var result = WindowsDesktopSearchAutomationDriver.ReadFirstSearchInput(preparation,
            () => rendererHasInput ? Control("renderer", "input-a") : null,
            () => { topReads++; });
        Assert.Equal(expected, result?.NativeReference);
        Assert.Equal(preparation && !rendererHasInput ? 1 : 0, topReads);
    }

    [Fact]
    public void TopPrimingLeavesAnEmptySampleThenTheNextRendererHintIsReadLive()
    {
        var discoveries = 0;
        var topQueries = 0;
        var liveReads = 0;
        var renderer = Control("renderer-hint", "input-a");
        var probe = WindowsDesktopSearchAutomationDriver.CreateReadOnlyPreparationProbe(_ =>
        {
            discoveries++;
            var candidate = WindowsDesktopSearchAutomationDriver.ReadFirstSearchInput(true,
                () => discoveries == 1 ? null : renderer, () => { topQueries++; });
            return candidate is null ? [] : [candidate];
        }, control => { liveReads++; return control.Snapshot; });

        Assert.Empty(probe(Window.WindowHandle));
        Assert.Equal(1, discoveries);
        Assert.Equal(1, topQueries);
        Assert.Equal(0, liveReads);

        Assert.Same(renderer, Assert.Single(probe(Window.WindowHandle)));
        Assert.Equal(2, discoveries);
        Assert.Equal(0, liveReads);

        var stable = Assert.Single(probe(Window.WindowHandle));
        Assert.Equal(renderer.NativeReference, stable.NativeReference);
        Assert.Equal(renderer.Snapshot.NativeSearchIdentity, stable.Snapshot.NativeSearchIdentity);
        Assert.Equal(2, discoveries);
        Assert.Equal(1, topQueries);
        Assert.Equal(1, liveReads);
    }

    private sealed class LocatorHarness
    {
        private readonly WindowsSearchResultDriver _workflow;
        private ForegroundWindowSnapshot _opened = Window;
        public Windows Windows { get; } = new();
        public InputDriver Input { get; }
        public LocatorRecordingAutomation Automation { get; }
        public string HintReference { get; set; } = "first-hint";
        public int HintReads { get; private set; }
        public Action? OnHint { get; set; }
        public Action? OnPage { get; set; }
        public LocatorHarness()
        {
            Input = new(Windows);
            Automation = new(new WindowsUiAutomationService(Windows, Input));
            var clock = new ReadinessClock();
            _workflow = new(new Launcher(), Automation, Windows, _ => Windows.Native, () => Windows.Foreground,
                _ => { HintReads++; OnHint?.Invoke(); return [Control(HintReference, "input-a")]; }, clock,
                (elapsed, cancellation) => { cancellation.ThrowIfCancellationRequested(); clock.Advance(elapsed); },
                (_, cancellation) => { cancellation.ThrowIfCancellationRequested(); OnPage?.Invoke(); return new(true, false, "", "", "", false, []); });
            Open();
        }
        public void Open() => _opened = _workflow.Open(App, default);
        public void Prepare() => _workflow.PrepareSearch(_opened, default);
        public void Search(CancellationToken cancellation = default) => _workflow.Search(_opened, Query, cancellation);
    }

    private sealed class LocatorRecordingAutomation(WindowsUiAutomationService service)
        : IReliableDesktopAutomation, IPreparedNativeSearchAutomation, IBoundApplicationSearchAutomation
    {
        public List<DesktopSearchControl?> Locators { get; } = [];
        public DesktopAutomationResult SearchWithPreparationLocator(ForegroundWindowSnapshot window, string query,
            DesktopSearchControl? locator, CancellationToken cancellation)
        {
            Locators.Add(locator);
            return service.SearchWithPreparationLocator(window, query, locator, cancellation);
        }
        public DesktopAutomationResult Search(ForegroundWindowSnapshot window, string query) => service.Search(window, query);
        public DesktopAutomationResult Search(ForegroundWindowSnapshot window, string query, CancellationToken cancellation) => service.Search(window, query, cancellation);
        public DesktopAutomationResult SearchBoundApplication(ForegroundWindowSnapshot window, string query,
            DesktopSearchControl? locator, Func<CancellationToken, ForegroundWindowSnapshot> validateWindow,
            CancellationToken cancellation)
        {
            Locators.Add(locator);
            return service.SearchBoundApplication(window, query, locator, validateWindow, cancellation);
        }
        public DesktopAutomationResult Describe(ForegroundWindowSnapshot window) => throw new NotSupportedException();
    }
}
