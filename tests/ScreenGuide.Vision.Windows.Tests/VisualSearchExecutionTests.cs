using System.Drawing;
using System.Drawing.Imaging;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class VisualSearchExecutionTests
{
    private const string Query = "示例助手";
    private static readonly ForegroundWindowSnapshot Window = new(42, "测试搜索窗口", "fixture", 123,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1));
    private static readonly VisualSearchField Field = new(new(30, 30, 200, 30), new(70, 38, 40, 16));
    private static readonly VisualSearchWindowBounds Geometry = new(100, 100, 640, 400);

    [Fact]
    public async Task PendingConsentAndCancellationPermitNoCaptureActivationOrInput()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture();
        fixture.Consent.Run = token => pending.Task.WaitAsync(token);

        var operation = fixture.Create().SearchAsync(Window, " ＡＩ 查询 ", cancellation.Token);
        Assert.False(operation.IsCompleted);
        Assert.Equal(Window, fixture.Consent.Window);
        Assert.Equal("AI 查询", fixture.Consent.Query);
        Assert.Empty(fixture.Input.Actions);
        Assert.Equal(0, fixture.Capture.Calls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Empty(fixture.Input.Actions);
        Assert.Equal(0, fixture.Capture.Calls);
    }

    [Fact]
    public async Task ConsentDenialDoesNotObserveOrAct()
    {
        var fixture = new Fixture();
        fixture.Consent.Run = _ => Task.FromException(new UnauthorizedAccessException("fixture denied"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Create().SearchAsync(Window, Query));
        Assert.Empty(fixture.Input.Actions);
        Assert.Equal(0, fixture.Capture.Calls);
        Assert.Equal(0, fixture.FindCalls);
    }

    [Fact]
    public async Task AlreadyCancelledRequestDoesNotAskConsentOrCapture()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Create().SearchAsync(Window, Query, cancellation.Token));
        Assert.Equal(0, fixture.Consent.Calls);
        Assert.Equal(0, fixture.Capture.Calls);
        Assert.Empty(fixture.Input.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowIdentityChangeBeforeOrWhileAwaitingConsentStopsBeforeCapture(bool duringConsent)
    {
        var fixture = new Fixture();
        if (duringConsent)
            fixture.Consent.Run = _ => { fixture.Windows.Current = Window with { ProcessId = 999 }; return Task.CompletedTask; };
        else fixture.Windows.Current = Window with { ProcessStartTimeUtc = Window.ProcessStartTimeUtc.AddSeconds(1) };

        var error = await Assert.ThrowsAsync<WindowIdentityException>(() => fixture.Create().SearchAsync(Window, Query));
        Assert.Equal(WindowIdentityErrorCodes.Changed, error.Code);
        Assert.Equal(duringConsent ? 1 : 0, fixture.Consent.Calls);
        Assert.Equal(0, fixture.Capture.Calls);
        Assert.Empty(fixture.Input.Actions);
    }

    [Theory]
    [InlineData("window")]
    [InlineData("geometry")]
    [InlineData("technology")]
    [InlineData("dimensions")]
    public async Task ChangedOrUntrustedCaptureCannotAuthorizeInput(string change)
    {
        var fixture = new Fixture();
        fixture.Capture.OnCapture = _ =>
        {
            switch (change)
            {
                case "window": fixture.Windows.Current = Window with { WindowTitle = "另一窗口" }; break;
                case "geometry": fixture.Input.Bounds = Geometry with { X = Geometry.X + 1 }; break;
                case "technology": fixture.Capture.Technology = "EntireDesktop"; break;
                case "dimensions": fixture.Capture.Width = Geometry.Width - 1; break;
            }
        };
        if (change == "window")
            await Assert.ThrowsAsync<WindowIdentityException>(() => fixture.Create().SearchAsync(Window, Query));
        else
            Assert.Equal(DesktopSearchErrorCodes.TargetChanged, (await Assert.ThrowsAsync<DesktopSearchException>(
                () => fixture.Create().SearchAsync(Window, Query))).Code);
        Assert.Equal(0, fixture.Input.Clicks);
        Assert.Equal(0, fixture.Input.Writes);
        Assert.Equal(0, fixture.FindCalls);
    }

    [Theory]
    [InlineData(DesktopSearchErrorCodes.CandidateNotFound)]
    [InlineData(DesktopSearchErrorCodes.CandidateAmbiguous)]
    public async Task MissingOrAmbiguousVisualFieldNeverClicksOrTypes(string code)
    {
        var fixture = new Fixture { Find = _ => throw new DesktopSearchException(code, "fixture") };
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => fixture.Create().SearchAsync(Window, Query));
        Assert.Equal(code, error.Code);
        Assert.Equal(0, fixture.Input.Clicks);
        Assert.Equal(0, fixture.Input.Writes);
    }

    [Theory]
    [InlineData(-1, 30, 200, 30)]
    [InlineData(30, 30, 63, 30)]
    [InlineData(30, 30, 200, 15)]
    [InlineData(600, 30, 200, 30)]
    [InlineData(30, 380, 200, 30)]
    public async Task OutOfBoundsOrUndersizedFieldNeverClicks(int x, int y, int width, int height)
    {
        var fixture = new Fixture { Find = _ => Field with { Bounds = new(x, y, width, height) } };
        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, (await Assert.ThrowsAsync<DesktopSearchException>(
            () => fixture.Create().SearchAsync(Window, Query))).Code);
        Assert.Equal(0, fixture.Input.Clicks);
        Assert.Equal(0, fixture.Input.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchFieldChangesBeforeClickOrAfterFocusPreventTyping(bool afterFocus)
    {
        var fixture = new Fixture
        {
            Match = call => call != (afterFocus ? 2 : 1)
        };
        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, (await Assert.ThrowsAsync<DesktopSearchException>(
            () => fixture.Create().SearchAsync(Window, Query))).Code);
        Assert.Equal(afterFocus ? 1 : 0, fixture.Input.Clicks);
        Assert.Equal(0, fixture.Input.Writes);
        Assert.Equal(1, fixture.FindCalls);
        Assert.Equal(afterFocus ? 2 : 1, fixture.MatchCalls);
    }

    [Fact]
    public async Task ChangedPixelsAtSameFieldCoordinatesPreventClick()
    {
        var fixture = new Fixture();
        fixture.Capture.Png = call => Image(call == 2 ? Field.Bounds : null);
        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, (await Assert.ThrowsAsync<DesktopSearchException>(
            () => fixture.Create().SearchAsync(Window, Query))).Code);
        Assert.Equal(0, fixture.Input.Clicks);
        Assert.Equal(0, fixture.Input.Writes);
    }

    [Fact]
    public async Task NoInsertionCaretMeansNoTextInput()
    {
        var fixture = new Fixture();
        fixture.Capture.Png = _ => Image();
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => fixture.Create().SearchAsync(Window, Query));
        Assert.Equal(DesktopSearchErrorCodes.FocusChanged, error.Code);
        Assert.Equal(1, fixture.Input.Clicks);
        Assert.Equal(0, fixture.Input.Writes);
    }

    [Fact]
    public async Task NativeCaretInsideKnownFieldAllowsInputWithoutImageBlinkOrRepeatedPlaceholderOcr()
    {
        var fixture = new Fixture
        {
            Find = call => call == 1 ? Field : throw new InvalidOperationException("Placeholder must only be identified once.")
        };
        fixture.Input.NativeCaret = true;
        fixture.Capture.Png = _ => Image();

        var result = await fixture.Create().SearchAsync(Window, Query);

        Assert.True(result.ApplicationVerified && result.QuerySubmitted && result.ResultsVerified);
        Assert.False(result.FollowUpInvoked || result.FollowUpVerified);
        Assert.Equal("LiveFilter", result.SubmissionMode);
        Assert.Equal(1, fixture.FindCalls);
        Assert.Equal(2, fixture.MatchCalls);
        Assert.Equal(["activate", "click-field", "replace-text"], fixture.Input.Actions);
        Assert.Equal(1, fixture.Input.Writes);
        Assert.Same(fixture.Input.Receipt, fixture.Input.ReceivedReceipt);
        Assert.Equal(Query, fixture.Input.Query);
    }

    [Fact]
    public async Task LosingNativeCaretAtWriteBoundaryPreventsAnyTextInput()
    {
        var fixture = new Fixture();
        fixture.Input.NativeCaret = true;
        fixture.Capture.Png = _ => Image();
        fixture.Input.BeforeWrite = () => fixture.Input.NativeCaret = false;

        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => fixture.Create().SearchAsync(Window, Query));

        Assert.Equal(DesktopSearchErrorCodes.FocusChanged, error.Code);
        Assert.Equal(1, fixture.FindCalls);
        Assert.Equal(2, fixture.MatchCalls);
        Assert.Equal(1, fixture.Input.Clicks);
        Assert.Equal(0, fixture.Input.Writes);
        Assert.Equal(0, fixture.FieldReads);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("type")]
    public async Task RealExecutionInvokesTargetValidatorAtBothInputBoundaries(string boundary)
    {
        var fixture = new Fixture();
        var targetChanged = false;
        if (boundary == "click") fixture.Input.BeforeClick = () => targetChanged = true;
        else fixture.Input.BeforeWrite = () => targetChanged = true;
        var checks = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Create().SearchAsync(Window, Query,
            validateTarget: () => { checks++; if (targetChanged) throw new UnauthorizedAccessException("changed binding"); }));
        Assert.True(checks > 0);
        Assert.Equal(boundary == "type" ? 1 : 0, fixture.Input.Clicks);
        Assert.Equal(0, fixture.Input.Writes);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("click")]
    [InlineData("type")]
    public async Task CancellationAtCaptureOrInputBoundaryPreventsFurtherInput(string boundary)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        if (boundary == "capture") fixture.Capture.OnCapture = _ => cancellation.Cancel();
        else if (boundary == "click") fixture.Input.BeforeClick = cancellation.Cancel;
        else fixture.Input.BeforeWrite = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Create().SearchAsync(Window, Query, cancellation.Token));
        Assert.Equal(boundary == "type" ? 1 : 0, fixture.Input.Clicks);
        Assert.Equal(0, fixture.Input.Writes);
    }

    [Fact]
    public async Task WindowGeometryChangeAtWriteBoundaryStopsBeforeText()
    {
        var fixture = new Fixture();
        fixture.Input.BeforeWrite = () => fixture.Input.Bounds = Geometry with { X = 200 };
        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, (await Assert.ThrowsAsync<DesktopSearchException>(
            () => fixture.Create().SearchAsync(Window, Query))).Code);
        Assert.Equal(0, fixture.Input.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessRequiresCaretExactReadbackAndFreshResultEvidence(bool noResults)
    {
        var fixture = new Fixture();
        fixture.AfterResults = noResults ? [Text("没有搜索结果", 80)] : [Text("搜索结果", 80), Text(Query, 120)];
        var result = await fixture.Create().SearchAsync(Window, Query);

        Assert.True(result.ApplicationVerified && result.QuerySubmitted && result.ResultsVerified);
        Assert.Equal(noResults, result.NoResults);
        Assert.False(result.FollowUpInvoked || result.FollowUpVerified);
        Assert.Equal("LiveFilter", result.SubmissionMode);
        Assert.Equal(["activate", "click-field", "replace-text"], fixture.Input.Actions);
        Assert.Equal(Query, fixture.Input.Query);
        Assert.Same(fixture.Input.Receipt, fixture.Input.ReceivedReceipt);
        Assert.Equal(1, fixture.Input.Clicks);
        Assert.Equal(1, fixture.Input.Writes);
        Assert.Equal(1, fixture.FindCalls);
        Assert.Equal(3, fixture.MatchCalls);
        Assert.Equal(1, fixture.FieldReads);
        Assert.Equal(2, fixture.ResultReads);
        Assert.All(fixture.Capture.Targets, target =>
        {
            Assert.Equal(Window.WindowHandle, target.WindowHandle);
            Assert.Equal(Window.ProcessId, target.ProcessId);
            Assert.Equal(Window.ProcessStartTimeUtc, target.ProcessStartTimeUtc);
            Assert.Equal(Window.WindowTitle, target.WindowTitle);
        });
        Assert.All(fixture.Capture.Buffers, bytes => Assert.All(bytes, value => Assert.Equal((byte)0, value)));
    }

    [Fact]
    public async Task WrongReadbackNeverProceedsToResultsAndCancellationStopsPolling()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.Read = region =>
        {
            if (region != Field.Bounds) return [];
            cancellation.Cancel();
            return [Text("另一关键词", Field.Bounds.Y)];
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Create().SearchAsync(Window, Query, cancellation.Token));
        Assert.Equal(1, fixture.FieldReads);
        Assert.Equal(1, fixture.ResultReads);
        Assert.Equal(1, fixture.Input.Writes);
    }

    [Fact]
    public async Task CancelledReadbackRejectsEvenOtherwiseValidLateResult()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.Read = region =>
        {
            if (region == Field.Bounds) return [Text(Query, Field.Bounds.Y)];
            if (fixture.Input.Writes == 0) return [];
            cancellation.Cancel();
            return [Text("搜索结果", 80), Text(Query, 120)];
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Create().SearchAsync(Window, Query, cancellation.Token));
        Assert.Equal(1, fixture.Input.Writes);
    }

    [Fact]
    public void UnchangedSameNameChatItemAndHeadingAreNotNewSearchEvidence()
    {
        VisualSearchText[] old = [Text("联系人", 80), Text(Query, 120)];
        Assert.Null(WindowsVisualSearchAutomation.VerifyResults(Query, old, old));
        Assert.Null(WindowsVisualSearchAutomation.VerifyResults(Query, [], [Text(Query, 120)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public void OcrSpacingAndSmallPositionDriftDoNotTurnOldRowsIntoNewResults(int drift)
    {
        VisualSearchText[] old = [Text("联 系 人", 80), Text("示 例 助 手", 120)];
        VisualSearchText[] current = [Text("联系人", 80 + drift, 40 + drift), Text(Query, 120 + drift, 40 + drift)];
        Assert.Null(WindowsVisualSearchAutomation.VerifyResults(Query, old, current));
        Assert.Null(WindowsVisualSearchAutomation.VerifyResults(Query, [Text("没 有 搜 索 结 果", 80)],
            [Text("没有搜索结果", 80 + drift, 40 + drift)]));
    }

    [Theory]
    [InlineData("搜索结果")]
    [InlineData("Contacts")]
    [InlineData("Search results")]
    public void FreshHeadingAndExactResultBelowItVerifyResults(string heading)
    {
        Assert.Equal(false, WindowsVisualSearchAutomation.VerifyResults(Query, [], [Text(heading, 80), Text(Query, 120)]));
        Assert.Null(WindowsVisualSearchAutomation.VerifyResults(Query, [], [Text(heading, 80), Text(Query + "副本", 120)]));
        Assert.Null(WindowsVisualSearchAutomation.VerifyResults(Query, [], [Text(Query, 40), Text(heading, 80)]));
        Assert.Null(WindowsVisualSearchAutomation.VerifyResults(Query, [], [Text(heading, 80), Text("Q" + Query, 120)]));
    }

    [Theory]
    [InlineData("没有搜索结果")]
    [InlineData("No results")]
    [InlineData("No results found")]
    public void EmptyResultsMustBeNew(string label)
    {
        VisualSearchText[] empty = [Text(label, 80)];
        Assert.Equal(true, WindowsVisualSearchAutomation.VerifyResults(Query, [], empty));
        Assert.Null(WindowsVisualSearchAutomation.VerifyResults(Query, empty, empty));
    }

    [Theory]
    [InlineData("示例助手", "示例助手", false, true)]
    [InlineData("示 例 助 手", "示例助手", false, true)]
    [InlineData("Ｑ示例助手", "示例助手", true, true)]
    [InlineData("Q示例助手", "示例助手", false, false)]
    [InlineData("示例助手副本", "示例助手", true, false)]
    [InlineData("AI查询", "AI 查询", true, false)]
    public void ReadbackIsExactAndSearchIconPrefixIsAllowedOnlyInsideInput(string observed, string query, bool input, bool expected) =>
        Assert.Equal(expected, WindowsVisualSearchAutomation.MatchesQuery(observed, query, input));

    [Theory]
    [InlineData(45, 36, 1, 18, true)]
    [InlineData(45, 36, 4, 18, true)]
    [InlineData(45, 36, 5, 18, false)]
    [InlineData(45, 36, 1, 5, false)]
    [InlineData(30, 36, 1, 18, false)]
    [InlineData(45, 80, 1, 18, false)]
    public void CaretEvidenceMustBeThinTallAndInsideTheField(int x, int y, int width, int height, bool expected) =>
        Assert.Equal(expected, WindowsVisualSearchAutomation.HasCaretBlink(Image(), Image(new(x, y, width, height)), Field.Bounds));

    private static VisualSearchText Text(string text, int y, int x = 40) => new(text, new(x, y, 150, 18));

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void FreshFieldAllowsOnlyAThinCaretAboveThePixelNoiseLimit(int width, bool expected)
    {
        var region = new VisualSearchRect(30, 30, 166, 30);
        Assert.Equal(expected, WindowsVisualSearchAutomation.SameFieldPixels(
            Image(), Image(new(100, 35, width, 20)), region));
    }

    [Fact]
    public void FreshFieldRejectsEvenWeakChangesOutsideTheCaret()
    {
        var before = Image();
        using var source = new MemoryStream(Image(new(100, 35, 3, 20)));
        using var bitmap = new Bitmap(source);
        bitmap.SetPixel(50, 40, Color.FromArgb(254, 254, 254));
        using var after = new MemoryStream();
        bitmap.Save(after, ImageFormat.Png);
        Assert.False(WindowsVisualSearchAutomation.SameFieldPixels(before, after.ToArray(), new(30, 30, 166, 30)));
    }

    private static byte[] Image(VisualSearchRect? ink = null)
    {
        using var bitmap = new Bitmap(Geometry.Width, Geometry.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            if (ink is not null) graphics.FillRectangle(Brushes.Black, ink.X, ink.Y, ink.Width, ink.Height);
        }
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
        public Func<int, VisualSearchField>? Find { get; init; }
        public Func<int, bool>? Match { get; set; }
        public Func<VisualSearchRect, IReadOnlyList<VisualSearchText>>? Read { get; set; }
        public IReadOnlyList<VisualSearchText> AfterResults { get; set; } = [Text("搜索结果", 80), Text(Query, 120)];
        public int FindCalls { get; private set; }
        public int MatchCalls { get; private set; }
        public int FieldReads { get; private set; }
        public int ResultReads { get; private set; }
        public WindowsVisualSearchAutomation Create() => new(Capture, Windows, Consent, Input,
            (_, _) =>
            {
                FindCalls++;
                return Task.FromResult(Find?.Invoke(FindCalls) ?? Field);
            },
            (_, region, _) =>
            {
                if (region == Field.Bounds) FieldReads++;
                else ResultReads++;
                return Task.FromResult(Read?.Invoke(region) ?? (region == Field.Bounds
                    ? new[] { Text(Query, Field.Bounds.Y) }
                    : Input.Writes == 0 ? Array.Empty<VisualSearchText>() : AfterResults));
            },
            matchingControl: (_, field, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal(Field.Bounds, field);
                MatchCalls++;
                return Match?.Invoke(MatchCalls) ?? true;
            });
    }

    private sealed class Consent : IVisualSearchConsent
    {
        public Func<CancellationToken, Task>? Run { get; set; }
        public int Calls { get; private set; }
        public ForegroundWindowSnapshot? Window { get; private set; }
        public string? Query { get; private set; }
        public Task RequestAsync(ForegroundWindowSnapshot window, string query, CancellationToken cancellationToken = default)
        {
            Calls++;
            (Window, Query) = (window, query);
            return Run?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class Windows : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot Current { get; set; } = Window;
        public ForegroundWindowSnapshot? GetLastExternalWindow() => Current;
        public ForegroundWindowSnapshot? ResolveWindow(long windowHandle) => Current;
    }

    private sealed class Capture : IWindowCaptureService
    {
        public int Calls { get; private set; }
        public Action<int>? OnCapture { get; set; }
        public Func<int, byte[]> Png { get; set; } = call => Image(call >= 5 ? new(45, 36, 2, 18) : null);
        public string Technology { get; set; } = "Windows.GraphicsCapture.SingleHwnd";
        public int Width { get; set; } = Geometry.Width;
        public List<WindowCaptureTarget> Targets { get; } = [];
        public List<byte[]> Buffers { get; } = [];
        public Task<CapturedWindowFrame> CaptureAsync(WindowCaptureTarget target, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Targets.Add(target);
            OnCapture?.Invoke(Calls);
            var bytes = Png(Calls);
            Buffers.Add(bytes);
            return Task.FromResult(new CapturedWindowFrame(bytes, Width, Geometry.Height, Technology));
        }
    }

    // The interface deliberately has no Enter, result activation, message send, or arbitrary key method.
    private sealed class Input : IVisualSearchInputDriver
    {
        public List<string> Actions { get; } = [];
        public VisualSearchWindowBounds Bounds { get; set; } = Geometry;
        public VisualSearchClickReceipt Receipt { get; } = new(1234, 84);
        public VisualSearchClickReceipt? ReceivedReceipt { get; private set; }
        public bool NativeCaret { get; set; }
        public Action? BeforeClick { get; set; }
        public Action? BeforeWrite { get; set; }
        public int Clicks { get; private set; }
        public int Writes { get; private set; }
        public string? Query { get; private set; }
        public void Activate(long window, CancellationToken token) { token.ThrowIfCancellationRequested(); Actions.Add("activate"); }
        public void RequireForeground(long window) => Assert.Equal(Window.WindowHandle, window);
        public VisualSearchWindowBounds GetBounds(long window) => Bounds;
        public bool HasSearchCaret(long window, VisualSearchWindowBounds bounds, VisualSearchRect field)
        {
            Assert.Equal(Window.WindowHandle, window);
            Assert.Equal(Bounds, bounds);
            return NativeCaret;
        }
        public VisualSearchClickReceipt Click(long window, VisualSearchWindowBounds bounds, VisualSearchRect field, Action validate, CancellationToken token)
        {
            Assert.Equal(Window.WindowHandle, window);
            Assert.Equal(Geometry, bounds);
            Assert.Equal(Field.Bounds, field);
            BeforeClick?.Invoke();
            validate();
            token.ThrowIfCancellationRequested();
            Clicks++;
            Actions.Add("click-field");
            return Receipt;
        }
        public void ReplaceText(long window, string query, VisualSearchClickReceipt click, Action validate, CancellationToken token)
        {
            Assert.Equal(Window.WindowHandle, window);
            ReceivedReceipt = click;
            BeforeWrite?.Invoke();
            validate();
            token.ThrowIfCancellationRequested();
            Writes++;
            Query = query;
            Actions.Add("replace-text");
        }
    }
}
