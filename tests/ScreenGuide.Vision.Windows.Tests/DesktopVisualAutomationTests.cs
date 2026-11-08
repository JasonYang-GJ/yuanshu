using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using ScreenGuide.AI.Core;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Vision.Abstractions;
using ScreenGuide.Vision.Windows;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class DesktopVisualAutomationTests
{
    [Fact]
    public async Task ObservationIsBoundAndDisposalClearsBothOwnedImageBuffers()
    {
        var rig = new Rig();
        var observation = await rig.Service.ObserveAsync(rig.Native.Window, CancellationToken.None);
        Assert.Equal(rig.Native.Window, observation.Snapshot.Window);
        Assert.Equal(rig.Native.Geometry.Bounds, observation.Snapshot.PhysicalBounds);
        Assert.Equal("合成界面文字", observation.Snapshot.OcrText);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(observation.PngBytes.Span)), observation.Snapshot.ImageSha256);
        using var modelImage = new ChatImage(observation.PngBytes.Span);
        Assert.Equal((observation.Snapshot.ImageWidth, observation.Snapshot.ImageHeight), (modelImage.Width, modelImage.Height));
        Assert.All(rig.Capture.Buffers[0], value => Assert.Equal(0, value));
        var memory = observation.PngBytes;
        observation.Dispose();
        Assert.All(memory.ToArray(), value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => observation.PngBytes);
    }

    [Fact]
    public async Task ScreenshotIsResizedButItsFingerprintStillIncludesAllSourcePixels()
    {
        var rig = new Rig(3200, 64);
        using var first = await rig.Service.ObserveAsync(rig.Native.Window, CancellationToken.None);
        Assert.Equal((3200, 64, 1600, 32), (first.Snapshot.SourceWidth, first.Snapshot.SourceHeight,
            first.Snapshot.ImageWidth, first.Snapshot.ImageHeight));
        rig.Capture.Change = true;
        using var second = await rig.Service.ObserveAsync(rig.Native.Window, CancellationToken.None);
        Assert.NotEqual(first.Snapshot.ContentFingerprint, second.Snapshot.ContentFingerprint);
        using var stream = new MemoryStream(first.PngBytes.ToArray());
        using var bitmap = new Bitmap(stream);
        Assert.Equal((1600, 32), (bitmap.Width, bitmap.Height));
        using var modelImage = new ChatImage(first.PngBytes.Span);
        Assert.Equal((1600, 32), (modelImage.Width, modelImage.Height));
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("source_size")]
    [InlineData("actual_png_size")]
    [InlineData("geometry")]
    [InlineData("dpi")]
    [InlineData("window")]
    [InlineData("foreground")]
    public async Task UnreliableCaptureNeverReachesOcrOrInputAndClearsSourceBytes(string change)
    {
        var rig = new Rig();
        rig.Capture.OnCapture = () =>
        {
            if (change == "fallback") rig.Capture.Technology = "Windows.PrintWindow.SingleHwnd";
            if (change == "source_size") rig.Capture.ReportWidth++;
            if (change == "actual_png_size") rig.Capture.Width++;
            if (change == "geometry") rig.Native.Geometry = rig.Native.Geometry with { Bounds = new(11, 20, 100, 80) };
            if (change == "dpi") rig.Native.Geometry = rig.Native.Geometry with { Dpi = 144 };
            if (change == "window") rig.Native.Window = rig.Native.Window with { ProcessId = 99 };
            if (change == "foreground") rig.Native.Foreground = false;
        };
        await Assert.ThrowsAsync<DesktopTaskException>(() => rig.Service.ObserveAsync(Rig.Window, CancellationToken.None));
        Assert.Equal(0, rig.Ocr.Calls);
        Assert.Equal(0, rig.Native.InputCount);
        Assert.All(rig.Capture.Buffers.SelectMany(x => x), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CancellationBeforeCaptureAndDuringOcrPreventsInputAndClearsImages()
    {
        var rig = new Rig();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.ObserveAsync(Rig.Window, cancellation.Token));
        Assert.Equal(0, rig.Capture.Calls);
        using var second = new CancellationTokenSource();
        rig.Ocr.OnRead = () => second.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.ObserveAsync(Rig.Window, second.Token));
        Assert.Equal(0, rig.Native.InputCount);
        Assert.All(rig.Capture.Buffers.SelectMany(x => x), value => Assert.Equal(0, value));
        Assert.All(rig.Ocr.LastImage.ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("old")]
    [InlineData("future")]
    [InlineData("left")]
    [InlineData("overflow")]
    [InlineData("empty")]
    [InlineData("geometry")]
    [InlineData("size")]
    [InlineData("fingerprint")]
    [InlineData("wait")]
    [InlineData("unknown")]
    [InlineData("unexpected_text")]
    public async Task InvalidOrStaleActionIsRejectedBeforeRecaptureOrInput(string change)
    {
        var rig = new Rig();
        using var observation = await rig.Service.ObserveAsync(Rig.Window, CancellationToken.None);
        var snapshot = observation.Snapshot;
        var action = Click(snapshot);
        switch (change)
        {
            case "id": action = action with { SnapshotId = Guid.NewGuid() }; break;
            case "old": snapshot = snapshot with { CapturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3) }; break;
            case "future": snapshot = snapshot with { CapturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) }; break;
            case "left": action = action with { Bounds = new(-1, 10, 20, 20) }; break;
            case "overflow": action = action with { Bounds = new(int.MaxValue, 0, int.MaxValue, 10) }; break;
            case "empty": action = action with { Bounds = new(10, 10, 0, 10) }; break;
            case "geometry": snapshot = snapshot with { PhysicalBounds = new(double.NaN, 20, 100, 80) }; break;
            case "size": snapshot = snapshot with { ImageWidth = 99 }; break;
            case "fingerprint": snapshot = snapshot with { ContentFingerprint = "not-a-hash" }; break;
            case "wait": action = action with { Action = DesktopVisualActionKind.Wait }; break;
            case "unknown": action = action with { Action = (DesktopVisualActionKind)999 }; break;
            case "unexpected_text": action = action with { Text = "不允许的附加文字" }; break;
        }
        await Assert.ThrowsAsync<DesktopTaskException>(() => rig.Service.ExecuteAsync(snapshot, action, CancellationToken.None));
        Assert.Equal(1, rig.Capture.Calls);
        Assert.Equal(0, rig.Native.InputCount);
    }

    [Theory]
    [InlineData("pixel")]
    [InlineData("location")]
    [InlineData("dpi")]
    [InlineData("title")]
    [InlineData("pid")]
    [InlineData("foreground")]
    public async Task ChangedWindowOrWholeSourcePictureNeverUsesAnOldProposal(string change)
    {
        var rig = new Rig();
        using var observation = await rig.Service.ObserveAsync(Rig.Window, CancellationToken.None);
        if (change == "pixel") rig.Capture.Change = true;
        if (change == "location") rig.Native.Geometry = rig.Native.Geometry with { Bounds = new(-100, -20, 100, 80) };
        if (change == "dpi") rig.Native.Geometry = rig.Native.Geometry with { Dpi = 144 };
        if (change == "title") rig.Native.Window = rig.Native.Window with { Title = "更改后的窗口" };
        if (change == "pid") rig.Native.Window = rig.Native.Window with { ProcessId = 33 };
        if (change == "foreground") rig.Native.Foreground = false;
        await Assert.ThrowsAsync<DesktopTaskException>(() => rig.Service.ExecuteAsync(observation.Snapshot, Click(observation.Snapshot), CancellationToken.None));
        Assert.Equal(0, rig.Native.InputCount);
    }

    [Theory]
    [InlineData(DesktopVisualActionKind.Click)]
    [InlineData(DesktopVisualActionKind.DoubleClick)]
    [InlineData(DesktopVisualActionKind.ScrollUp)]
    [InlineData(DesktopVisualActionKind.ScrollDown)]
    public async Task OneBoundedInputFollowsFreshImageAndDoesNotClaimTaskCompletion(DesktopVisualActionKind kind)
    {
        var rig = new Rig(3200, 160);
        using var observation = await rig.Service.ObserveAsync(Rig.Window, CancellationToken.None);
        var receipt = await rig.Service.ExecuteAsync(observation.Snapshot,
            Click(observation.Snapshot) with { Action = kind, Bounds = new(10, 10, 30, 20) }, CancellationToken.None);
        Assert.True(receipt.InputSent);
        Assert.Contains("仍需重新观察", receipt.Summary);
        Assert.Equal(3, rig.Capture.Calls);
        Assert.Equal(1, rig.Ocr.Calls);
        Assert.Equal(1, rig.Native.PointerMoves);
        Assert.Equal(1, rig.Native.InputCount);
        Assert.Equal(new(20, 20, 60, 40), rig.Native.LastBounds);
        Assert.Equal(kind == DesktopVisualActionKind.DoubleClick, rig.Native.DoubleClick);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypingRequiresCaretInsideTheJustClickedBoundedRegion(bool caret)
    {
        var rig = new Rig();
        rig.Native.Caret = caret;
        using var observation = await rig.Service.ObserveAsync(Rig.Window, CancellationToken.None);
        var action = Click(observation.Snapshot) with { Action = DesktopVisualActionKind.TypeText, Text = "合成输入" };
        if (caret)
        {
            var receipt = await rig.Service.ExecuteAsync(observation.Snapshot, action, CancellationToken.None);
            Assert.True(receipt.InputSent);
            Assert.Equal("合成输入", rig.Native.Typed);
        }
        else
        {
            await Assert.ThrowsAsync<DesktopTaskException>(() => rig.Service.ExecuteAsync(observation.Snapshot, action, CancellationToken.None));
            Assert.Null(rig.Native.Typed);
        }
        Assert.Equal(1, rig.Native.ClickCount);
    }

    [Fact]
    public async Task CancellationAfterFocusClickPreventsTypingWithoutRetryingTheClick()
    {
        var rig = new Rig();
        using var observation = await rig.Service.ObserveAsync(Rig.Window, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        rig.Native.AfterClick = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.ExecuteAsync(observation.Snapshot,
            Click(observation.Snapshot) with { Action = DesktopVisualActionKind.TypeText, Text = "合成输入" }, cancellation.Token));
        Assert.Equal(1, rig.Native.ClickCount);
        Assert.Null(rig.Native.Typed);
    }

    [Fact]
    public async Task UserInputBetweenFocusClickAndCaretObservationPreventsTyping()
    {
        var rig = new Rig();
        using var observation = await rig.Service.ObserveAsync(Rig.Window, CancellationToken.None);
        rig.Native.AfterClick = () => rig.Native.LastInputTime++;
        await Assert.ThrowsAsync<DesktopTaskException>(() => rig.Service.ExecuteAsync(observation.Snapshot,
            Click(observation.Snapshot) with { Action = DesktopVisualActionKind.TypeText, Text = "合成输入" }, CancellationToken.None));
        Assert.Equal(1, rig.Native.ClickCount);
        Assert.Null(rig.Native.Typed);
    }

    [Theory]
    [InlineData(DesktopVisualActionKind.Click)]
    [InlineData(DesktopVisualActionKind.DoubleClick)]
    [InlineData(DesktopVisualActionKind.TypeText)]
    [InlineData(DesktopVisualActionKind.ScrollUp)]
    [InlineData(DesktopVisualActionKind.ScrollDown)]
    public async Task HoverChangesAfterPointerMoveBlockEveryActualInput(DesktopVisualActionKind kind)
    {
        var rig = new Rig();
        using var observation = await rig.Service.ObserveAsync(Rig.Window, CancellationToken.None);
        rig.Native.AfterPrepare = () => rig.Capture.Change = true;
        await Assert.ThrowsAsync<DesktopTaskException>(() => rig.Service.ExecuteAsync(observation.Snapshot,
            Click(observation.Snapshot) with { Action = kind, Text = kind == DesktopVisualActionKind.TypeText ? "合成输入" : null }, CancellationToken.None));
        Assert.Equal(3, rig.Capture.Calls);
        Assert.Equal(1, rig.Native.PointerMoves);
        Assert.Equal(0, rig.Native.InputCount);
        Assert.Equal(0, rig.Native.ClickCount);
        Assert.Null(rig.Native.Typed);
    }

    [Fact]
    public async Task CancellationAfterPointerMovePreventsClickAndDoesNotMoveAgain()
    {
        var rig = new Rig();
        using var observation = await rig.Service.ObserveAsync(Rig.Window, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        rig.Native.AfterPrepare = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.ExecuteAsync(observation.Snapshot,
            Click(observation.Snapshot), cancellation.Token));
        Assert.Equal(2, rig.Capture.Calls);
        Assert.Equal(1, rig.Native.PointerMoves);
        Assert.Equal(0, rig.Native.InputCount);
    }

    [Theory]
    [InlineData("第一行\n第二行")]
    [InlineData("https://example.invalid")]
    [InlineData("powershell -Command test")]
    [InlineData("=1+2")]
    [InlineData("password=FAKE_ONLY")]
    [InlineData("hello\u200bworld")]
    [InlineData("a; b")]
    public async Task UnsafeTypingIsRejectedBeforeEvenTheFocusClick(string text)
    {
        var rig = new Rig();
        using var observation = await rig.Service.ObserveAsync(Rig.Window, CancellationToken.None);
        await Assert.ThrowsAsync<DesktopTaskException>(() => rig.Service.ExecuteAsync(observation.Snapshot,
            Click(observation.Snapshot) with { Action = DesktopVisualActionKind.TypeText, Text = text }, CancellationToken.None));
        Assert.Equal(1, rig.Capture.Calls);
        Assert.Equal(0, rig.Native.InputCount);
    }

    [Fact]
    public void NativeInputBatchesAlwaysPairTheirOwnDownAndUpEvents()
    {
        foreach (var twice in new[] { false, true })
        {
            var events = WindowsDesktopVisualNative.BuildClickEvents(twice);
            Assert.Equal(twice ? 4 : 2, events.Length);
            for (var i = 0; i < events.Length; i += 2)
            {
                Assert.Equal(0u, events[i].Type);
                Assert.Equal(2u, events[i].Data.Mouse.Flags);
                Assert.Equal(4u, events[i + 1].Data.Mouse.Flags);
            }
        }
        var text = WindowsDesktopVisualNative.BuildTextEvents("中文A");
        Assert.Equal(6, text.Length);
        for (var i = 0; i < text.Length; i += 2)
        {
            Assert.Equal(1u, text[i].Type);
            Assert.Equal(0, text[i].Data.Keyboard.VirtualKey);
            Assert.Equal(text[i].Data.Keyboard.Scan, text[i + 1].Data.Keyboard.Scan);
            Assert.Equal(4u, text[i].Data.Keyboard.Flags);
            Assert.Equal(6u, text[i + 1].Data.Keyboard.Flags);
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(91)] [InlineData(92)]
    public void EveryUserMouseButtonOrModifierBlocksInput(int held) =>
        Assert.Throws<DesktopTaskException>(() => WindowsDesktopVisualNative.RequireReleasedInputs(
            key => key == held ? unchecked((short)0x8000) : (short)0));

    [Fact]
    public void NativeCaretRequiresPhysicalInteriorAndValidVisibleRectangle()
    {
        var geometry = new DesktopTaskBounds(-1200, 100, 800, 600);
        var field = new DesktopVisualBounds(30, 40, 200, 50);
        Assert.True(WindowsDesktopVisualNative.IsCaretWithin(1, geometry, field, -1165, 145, -1163, 170));
        Assert.False(WindowsDesktopVisualNative.IsCaretWithin(0, geometry, field, -1165, 145, -1163, 170));
        Assert.False(WindowsDesktopVisualNative.IsCaretWithin(1, geometry, field, -1170, 145, -1168, 170));
        Assert.False(WindowsDesktopVisualNative.IsCaretWithin(1, geometry, field, -1165, 145, -1163, 200));
        Assert.False(WindowsDesktopVisualNative.IsCaretWithin(1, geometry, field, -1165, 145, -1165, 170));
    }

    private static DesktopVisualProposal Click(DesktopVisualSnapshot snapshot) =>
        new(snapshot.Id, DesktopVisualActionKind.Click, new(10, 10, 20, 20), null, "合成输入区域", "测试明确的图像目标");

    private sealed class Rig
    {
        internal static DesktopTaskWindow Window => new(42, 17, "SyntheticFixture", DateTimeOffset.UnixEpoch,
            @"C:\synthetic\fixture.exe", "SyntheticCanvas", "合成测试窗口");
        internal FakeCapture Capture { get; }
        internal FakeNative Native { get; } = new();
        internal FakeOcr Ocr { get; } = new();
        internal WindowsDesktopVisualAutomation Service { get; }
        internal Rig(int width = 100, int height = 80)
        {
            Capture = new(width, height);
            Native.Geometry = new(new(10, 20, width, height), 96);
            Service = new(Capture, Ocr, Native);
        }
    }

    private sealed class FakeCapture(int width, int height) : IWindowCaptureService
    {
        internal int Width = width;
        internal int ReportWidth = width;
        internal int Calls;
        internal bool Change;
        internal string Technology = "Windows.GraphicsCapture.SingleHwnd";
        internal Action? OnCapture;
        internal List<byte[]> Buffers = [];
        public Task<CapturedWindowFrame> CaptureAsync(WindowCaptureTarget target, CancellationToken cancellationToken = default)
        {
            Calls++;
            OnCapture?.Invoke();
            using var bitmap = new Bitmap(Width, height);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
            if (Change) bitmap.SetPixel(Width - 1, height - 1, Color.FromArgb(255, 254, 255, 255));
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            var bytes = stream.ToArray();
            Buffers.Add(bytes);
            return Task.FromResult(new CapturedWindowFrame(bytes, ReportWidth, height, Technology));
        }
    }

    private sealed class FakeOcr : ILocalOcrTextExtractor
    {
        internal int Calls;
        internal Action? OnRead;
        internal ReadOnlyMemory<byte> LastImage;
        public Task<string> ExtractAsync(ReadOnlyMemory<byte> pngBytes, CancellationToken cancellationToken = default)
        {
            Calls++; LastImage = pngBytes; OnRead?.Invoke();
            return Task.FromResult("合成界面文字");
        }
    }

    private sealed class FakeNative : IDesktopVisualNative
    {
        internal DesktopTaskWindow Window = Rig.Window;
        internal DesktopVisualGeometry Geometry = new(new(10, 20, 100, 80), 96);
        internal bool Foreground = true;
        internal bool Caret = true;
        internal bool DoubleClick;
        internal int ClickCount;
        internal int InputCount;
        internal int PointerMoves;
        internal string? Typed;
        internal DesktopVisualBounds? LastBounds;
        internal Action? AfterClick;
        internal Action? AfterPrepare;
        internal uint LastInputTime = 123;
        public DesktopTaskWindow RequireWindow(DesktopTaskWindow expected)
        {
            if (!Foreground || Window with { Title = expected.Title } != expected) throw DesktopVisualImage.Error("window_changed", "changed");
            return Window;
        }
        public DesktopVisualGeometry ReadGeometry(DesktopTaskWindow window) => Geometry;
        public void PreparePointer(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds,
            Action validate, CancellationToken token)
        { token.ThrowIfCancellationRequested(); validate(); PointerMoves++; AfterPrepare?.Invoke(); }
        public uint Click(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds,
            bool doubleClick, Action validate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); validate(); InputCount++; ClickCount++;
            var generation = LastInputTime;
            LastBounds = sourceBounds; DoubleClick = doubleClick; AfterClick?.Invoke();
            return generation;
        }
        public DesktopVisualFocus RequireCaret(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds) =>
            Caret ? new(42, LastInputTime) : throw DesktopVisualImage.Error("caret_unverified", "no caret");
        public void TypeText(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds,
            DesktopVisualFocus focus, string text, Action validate, CancellationToken token)
        { token.ThrowIfCancellationRequested(); validate(); InputCount++; Typed = text; }
        public void Scroll(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds,
            bool up, Action validate, CancellationToken token)
        { token.ThrowIfCancellationRequested(); validate(); InputCount++; LastBounds = sourceBounds; }
    }
}
