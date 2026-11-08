using System.Drawing;
using System.Drawing.Imaging;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class VisualSearchResultWindowTests
{
    private const string Query = "示例联系人";
    private static readonly ForegroundWindowSnapshot Main = new(42, "示例搜索工具", "fixture", 123,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1));
    private static readonly ForegroundWindowSnapshot Popup = Main with { WindowHandle = 84, WindowTitle = "搜索结果" };
    private static readonly VisualSearchWindowBounds MainBounds = new(100, 100, 640, 400);
    private static readonly VisualSearchWindowBounds PopupBounds = new(130, 160, 240, 180);
    private static readonly VisualSearchField Field = new(new(30, 30, 200, 30), new(70, 38, 40, 16));
    private static readonly VisualSearchRect PopupRegion = new(0, 0, PopupBounds.Width, PopupBounds.Height);

    [Fact]
    public async Task PendingResultConsentDoesNotCapturePopupOrRepeatTheSearch()
    {
        var permission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture();
        fixture.Consent.ResultDecision = token => permission.Task.WaitAsync(token);
        var operation = fixture.Create().SearchAsync(Main, Query);
        await fixture.Consent.ResultRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(operation.IsCompleted);
        Assert.Equal(Main, fixture.Consent.Origin);
        Assert.Equal(Popup, fixture.Consent.Popup);
        Assert.Equal(Query, fixture.Consent.Query);
        Assert.Equal(0, fixture.Capture.PopupCalls);
        Assert.Equal(0, fixture.PopupReads);
        Assert.Equal(1, fixture.Input.Clicks);
        Assert.Equal(1, fixture.Input.Writes);

        permission.SetResult();
        var result = await operation;
        Assert.True(result.ResultsVerified);
        AssertSingleSearch(fixture);
        Assert.Equal(1, fixture.Consent.ResultCalls);
    }

    [Fact]
    public async Task DeniedResultConsentCannotCaptureOrReadPopup()
    {
        var fixture = new Fixture();
        fixture.Consent.ResultDecision = _ => Task.FromException(new UnauthorizedAccessException("denied"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Create().SearchAsync(Main, Query));
        AssertNoPopupRead(fixture);
        AssertSingleSearch(fixture);
    }

    [Fact]
    public async Task OriginWindowConsentAloneCannotAuthorizeAnotherWindow()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Create(new OriginOnlyConsent()).SearchAsync(Main, Query));
        AssertNoPopupRead(fixture);
        AssertSingleSearch(fixture);
    }

    [Fact]
    public async Task CancellationWhileResultConsentIsPendingStopsWithoutPopupCapture()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.Consent.ResultDecision = token => Task.Delay(Timeout.Infinite, token);
        var operation = fixture.Create().SearchAsync(Main, Query, cancellation.Token);
        await fixture.Consent.ResultRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        AssertNoPopupRead(fixture);
        AssertSingleSearch(fixture);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("main-window")]
    [InlineData("bound-target")]
    public async Task LateConsentCannotContinueAfterRequestOrTargetChanges(string change)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        var changedTarget = false;
        fixture.Consent.ResultDecision = _ =>
        {
            if (change == "cancel") cancellation.Cancel();
            else if (change == "main-window") fixture.Windows.Current = Main with { ProcessId = 999 };
            else changedTarget = true;
            return Task.CompletedTask;
        };
        Task operation = fixture.Create().SearchAsync(Main, Query, cancellation.Token,
            () => { if (changedTarget) throw new UnauthorizedAccessException("changed application binding"); });
        if (change == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else if (change == "main-window") await Assert.ThrowsAsync<WindowIdentityException>(() => operation);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operation);
        AssertNoPopupRead(fixture);
        AssertSingleSearch(fixture);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedResultWindowVerificationBeforeOrAfterConsentPreventsCapture(int failedCheck)
    {
        var fixture = new Fixture();
        fixture.Results.FailAtCheck = failedCheck;
        var error = await Assert.ThrowsAsync<WindowIdentityException>(() => fixture.Create().SearchAsync(Main, Query));
        Assert.Equal(WindowIdentityErrorCodes.Changed, error.Code);
        Assert.Equal(failedCheck - 1, fixture.Consent.ResultCalls);
        AssertNoPopupRead(fixture);
        AssertSingleSearch(fixture);
    }

    [Fact]
    public async Task ResultWindowReplacementDuringCapturePreventsOcr()
    {
        var fixture = new Fixture();
        fixture.Capture.OnPopup = () => fixture.Results.FailAllChecks = true;
        await Assert.ThrowsAsync<WindowIdentityException>(() => fixture.Create().SearchAsync(Main, Query));
        Assert.Equal(1, fixture.Capture.PopupCalls);
        Assert.Equal(0, fixture.PopupReads);
        AssertSingleSearch(fixture);
    }

    [Theory]
    [InlineData("technology")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("geometry")]
    public async Task UntrustedOrChangedPopupFrameIsRejectedBeforeOcr(string change)
    {
        var fixture = new Fixture();
        switch (change)
        {
            case "technology": fixture.Capture.PopupTechnology = "EntireDesktop"; break;
            case "width": fixture.Capture.PopupWidth = PopupBounds.Width + 1; break;
            case "height": fixture.Capture.PopupHeight = PopupBounds.Height + 1; break;
            case "geometry": fixture.Capture.OnPopup = () => fixture.Input.ResultBounds = PopupBounds with { X = 150 }; break;
        }
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => fixture.Create().SearchAsync(Main, Query));
        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, error.Code);
        Assert.Equal(1, fixture.Capture.PopupCalls);
        Assert.Equal(0, fixture.PopupReads);
        AssertSingleSearch(fixture);
    }

    [Fact]
    public async Task PopupSuccessRequiresExactQueryRecheckInTheOriginalWindow()
    {
        var fixture = new Fixture();
        var result = await fixture.Create().SearchAsync(Main, Query);

        Assert.True(result.ApplicationVerified && result.QuerySubmitted && result.ResultsVerified);
        Assert.False(result.NoResults || result.FollowUpInvoked || result.FollowUpVerified);
        Assert.Equal("LiveFilter", result.SubmissionMode);
        AssertSingleSearch(fixture);
        Assert.Equal(1, fixture.Consent.ResultCalls);
        Assert.Equal(1, fixture.Capture.PopupCalls);
        Assert.Equal(1, fixture.PopupReads);
        Assert.Equal(2, fixture.QueryReads);
        Assert.Equal(Main.WindowHandle, fixture.Capture.Targets[^1].WindowHandle);
        var popupTarget = Assert.Single(fixture.Capture.Targets, target => target.WindowHandle == Popup.WindowHandle);
        Assert.Equal(Popup.ProcessId, popupTarget.ProcessId);
        Assert.Equal(Popup.ProcessStartTimeUtc, popupTarget.ProcessStartTimeUtc);
        Assert.Equal(Popup.WindowTitle, popupTarget.WindowTitle);
        Assert.All(fixture.Capture.Targets, target => Assert.Contains(target.WindowHandle, new[] { Main.WindowHandle, Popup.WindowHandle }));
        Assert.All(fixture.Capture.Buffers, bytes => Assert.All(bytes, value => Assert.Equal((byte)0, value)));
    }

    [Fact]
    public async Task LoadingPopupIsReadAgainWithoutRepeatingConsentOrInput()
    {
        var fixture = new Fixture
        {
            PopupObservation = capture => capture == 1
                ? [Text("正在加载", 10)]
                : [Text("搜索结果", 10), Text(Query, 60)]
        };

        var result = await fixture.Create().SearchAsync(Main, Query);

        Assert.True(result.ApplicationVerified && result.QuerySubmitted && result.ResultsVerified);
        Assert.False(result.NoResults || result.FollowUpInvoked || result.FollowUpVerified);
        Assert.Equal("LiveFilter", result.SubmissionMode);
        AssertSingleSearch(fixture);
        Assert.Equal(1, fixture.Consent.ResultCalls);
        Assert.Equal(1, fixture.Results.FindCalls);
        Assert.Equal(2, fixture.Capture.PopupCalls);
        Assert.Equal(2, fixture.PopupReads);
        Assert.Equal(2, fixture.QueryReads);
        Assert.Equal(Main.WindowHandle, fixture.Capture.Targets[^1].WindowHandle);
    }

    [Fact]
    public async Task ChangedMainQueryRejectsOtherwiseValidPopupResults()
    {
        var fixture = new Fixture { MainQuery = read => read == 1 ? Query : "另一联系人" };
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => fixture.Create().SearchAsync(Main, Query));
        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, error.Code);
        Assert.Equal(1, fixture.PopupReads);
        Assert.True(fixture.QueryReads > 1);
        AssertSingleSearch(fixture);
    }

    [Theory]
    [InlineData("missing-heading")]
    [InlineData("wrong-query")]
    [InlineData("query-prefix")]
    public async Task UnverifiedPopupEvidenceCannotCompleteBeforeFurtherObservation(string invalid)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture { PauseOnNextObservation = true };
        fixture.PopupItems = invalid switch
        {
            "missing-heading" => [Text(Query, 60)],
            "wrong-query" => [Text("搜索结果", 10), Text("另一联系人", 60)],
            _ => [Text("搜索结果", 10), Text(Query + "副本", 60)]
        };
        var operation = fixture.Create().SearchAsync(Main, Query, cancellation.Token);
        try
        {
            // If invalid popup text were reported as success, there would be no next
            // observation and this bounded wait would fail instead of passing on cancel.
            await fixture.NextObservation.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(operation.IsCompleted);
            AssertSingleSearch(fixture);
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task NativeMainResultsDoNotRequestOrCaptureASecondWindow()
    {
        var fixture = new Fixture { MainItems = [Text("搜索结果", 80), Text(Query, 120)] };
        var result = await fixture.Create().SearchAsync(Main, Query);
        Assert.True(result.ResultsVerified);
        Assert.Equal(0, fixture.Results.FindCalls);
        Assert.Equal(0, fixture.Consent.ResultCalls);
        AssertNoPopupRead(fixture);
        AssertSingleSearch(fixture);
    }

    // Geometry is only the discovery filter. These tests do not claim to verify the
    // native GetWindow(owner) / process identity checks; the fake below exercises callers.
    [Theory]
    [InlineData(130, 160, 240, 180, true)]
    [InlineData(130, 160, 99, 180, false)]
    [InlineData(130, 160, 240, 29, false)]
    [InlineData(191, 160, 240, 180, false)]
    [InlineData(130, 191, 240, 180, false)]
    [InlineData(99, 160, 240, 180, false)]
    [InlineData(130, 160, 620, 180, false)]
    [InlineData(130, 160, 240, 350, false)]
    [InlineData(130, 160, 1001, 180, false)]
    [InlineData(130, 160, 240, 801, false)]
    public void DiscoveryAcceptsOnlyANearbyDropdownInsideTheMainWindow(int x, int y, int width, int height, bool expected) =>
        Assert.Equal(expected, WindowsVisualSearchResultWindowVerifier.IsAdjacentDropdown(MainBounds, Field.Bounds,
            new(x, y, width, height)));

    private static void AssertSingleSearch(Fixture fixture)
    {
        Assert.Equal(1, fixture.Input.Clicks);
        Assert.Equal(1, fixture.Input.Writes);
        Assert.Equal(Query, fixture.Input.Query);
        Assert.Equal(["activate-main", "click-main-field", "replace-main-text"], fixture.Input.Actions);
    }
    private static void AssertNoPopupRead(Fixture fixture)
    {
        Assert.Equal(0, fixture.Capture.PopupCalls);
        Assert.Equal(0, fixture.PopupReads);
    }
    private static VisualSearchText Text(string text, int y) => new(text, new(20, y, 150, 18));
    private static byte[] Image(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private sealed class Fixture
    {
        public Consent Consent { get; } = new();
        public Windows Windows { get; } = new();
        public Input Input { get; } = new();
        public Capture Capture { get; } = new();
        public ResultWindows Results { get; } = new();
        public IReadOnlyList<VisualSearchText> PopupItems { get; set; } = [Text("搜索结果", 10), Text(Query, 60)];
        public Func<int, IReadOnlyList<VisualSearchText>>? PopupObservation { get; init; }
        public IReadOnlyList<VisualSearchText> MainItems { get; init; } = [];
        public Func<int, string> MainQuery { get; init; } = _ => Query;
        public bool PauseOnNextObservation { get; init; }
        public TaskCompletionSource NextObservation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int QueryReads { get; private set; }
        public int PopupReads { get; private set; }
        public WindowsVisualSearchAutomation Create(IVisualSearchConsent? consent = null) =>
            new(Capture, Windows, consent ?? Consent, Input, (_, _) => Task.FromResult(Field), ReadAsync,
                matchingControl: (_, _, token) => { token.ThrowIfCancellationRequested(); return true; }, resultWindows: Results);
        private async Task<IReadOnlyList<VisualSearchText>> ReadAsync(byte[] bytes, VisualSearchRect region, CancellationToken token)
        {
            if (region == PopupRegion)
            {
                PopupReads++;
                if (PauseOnNextObservation && PopupReads > 1) await PauseAsync(token);
                return PopupObservation?.Invoke(Capture.PopupCalls) ?? PopupItems;
            }
            if (region == Field.Bounds)
            {
                QueryReads++;
                if (PauseOnNextObservation && QueryReads > 2) await PauseAsync(token);
                return [new(MainQuery(QueryReads), Field.Bounds)];
            }
            return Input.Writes == 0 ? [] : MainItems;
        }
        private async Task PauseAsync(CancellationToken token)
        {
            NextObservation.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
    }

    private sealed class Consent : IVisualSearchConsent
    {
        public Func<CancellationToken, Task>? ResultDecision { get; set; }
        public TaskCompletionSource ResultRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ResultCalls { get; private set; }
        public ForegroundWindowSnapshot? Origin { get; private set; }
        public ForegroundWindowSnapshot? Popup { get; private set; }
        public string? Query { get; private set; }
        public Task RequestAsync(ForegroundWindowSnapshot window, string query, CancellationToken token = default)
        {
            Assert.Equal(Main, window);
            Assert.Equal(VisualSearchResultWindowTests.Query, query);
            return Task.CompletedTask;
        }
        public Task RequestResultReadAsync(ForegroundWindowSnapshot originWindow, ForegroundWindowSnapshot resultWindow,
            string query, CancellationToken cancellationToken = default)
        {
            ResultCalls++;
            (Origin, Popup, Query) = (originWindow, resultWindow, query);
            ResultRequested.TrySetResult();
            return ResultDecision?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class OriginOnlyConsent : IVisualSearchConsent
    {
        public Task RequestAsync(ForegroundWindowSnapshot window, string query, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ResultWindows : IVisualSearchResultWindows
    {
        public int FindCalls { get; private set; }
        public int VerifyCalls { get; private set; }
        public int FailAtCheck { get; set; }
        public bool FailAllChecks { get; set; }
        public ForegroundWindowSnapshot? Find(ForegroundWindowSnapshot origin, VisualSearchWindowBounds bounds, VisualSearchRect field)
        {
            Assert.Equal(Main, origin);
            Assert.Equal(MainBounds, bounds);
            Assert.Equal(Field.Bounds, field);
            FindCalls++;
            return Popup;
        }
        public void Verify(ForegroundWindowSnapshot originWindow, ForegroundWindowSnapshot resultWindow)
        {
            Assert.Equal(Main, originWindow);
            Assert.Equal(Popup, resultWindow);
            if (++VerifyCalls == FailAtCheck || FailAllChecks)
                throw new WindowIdentityException(WindowIdentityErrorCodes.Changed, "fixture window binding changed");
        }
    }

    private sealed class Windows : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot Current { get; set; } = Main;
        public ForegroundWindowSnapshot? GetLastExternalWindow() => Current;
        public ForegroundWindowSnapshot? ResolveWindow(long handle) => handle == Main.WindowHandle ? Current : null;
    }

    private sealed class Capture : IWindowCaptureService
    {
        public int PopupCalls { get; private set; }
        public string PopupTechnology { get; set; } = "Windows.GraphicsCapture.SingleHwnd";
        public int PopupWidth { get; set; } = PopupBounds.Width;
        public int PopupHeight { get; set; } = PopupBounds.Height;
        public Action? OnPopup { get; set; }
        public List<WindowCaptureTarget> Targets { get; } = [];
        public List<byte[]> Buffers { get; } = [];
        public Task<CapturedWindowFrame> CaptureAsync(WindowCaptureTarget target, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Targets.Add(target);
            if (target.WindowHandle == Popup.WindowHandle)
            {
                PopupCalls++;
                OnPopup?.Invoke();
                return Frame(PopupWidth, PopupHeight, PopupTechnology);
            }
            Assert.Equal(Main.WindowHandle, target.WindowHandle);
            return Frame(MainBounds.Width, MainBounds.Height, "Windows.GraphicsCapture.SingleHwnd");
        }
        private Task<CapturedWindowFrame> Frame(int width, int height, string technology)
        {
            var bytes = Image(width, height);
            Buffers.Add(bytes);
            return Task.FromResult(new CapturedWindowFrame(bytes, width, height, technology));
        }
    }

    private sealed class Input : IVisualSearchInputDriver
    {
        public int Clicks { get; private set; }
        public int Writes { get; private set; }
        public string? Query { get; private set; }
        public List<string> Actions { get; } = [];
        public VisualSearchWindowBounds ResultBounds { get; set; } = PopupBounds;
        private readonly VisualSearchClickReceipt _receipt = new(1234, Main.WindowHandle);
        public void Activate(long window, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(Main.WindowHandle, window);
            Actions.Add("activate-main");
        }
        public void RequireForeground(long window) => Assert.Equal(Main.WindowHandle, window);
        public VisualSearchWindowBounds GetBounds(long window) => window switch
        {
            42 => MainBounds,
            84 => ResultBounds,
            _ => throw new InvalidOperationException("Unexpected window.")
        };
        public bool HasSearchCaret(long window, VisualSearchWindowBounds bounds, VisualSearchRect field)
        {
            Assert.Equal(Main.WindowHandle, window);
            Assert.Equal(MainBounds, bounds);
            Assert.Equal(Field.Bounds, field);
            return true;
        }
        public VisualSearchClickReceipt Click(long window, VisualSearchWindowBounds bounds, VisualSearchRect field,
            Action validate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            validate();
            Assert.Equal(Main.WindowHandle, window);
            Assert.Equal(Field.Bounds, field);
            Clicks++;
            Actions.Add("click-main-field");
            return _receipt;
        }
        public void ReplaceText(long window, string query, VisualSearchClickReceipt click, Action validate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            validate();
            Assert.Equal(Main.WindowHandle, window);
            Assert.Same(_receipt, click);
            Writes++;
            Query = query;
            Actions.Add("replace-main-text");
        }
    }
}
