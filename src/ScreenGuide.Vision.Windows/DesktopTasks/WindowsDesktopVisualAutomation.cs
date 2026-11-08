using System.Security.Cryptography;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Vision.Windows;

internal sealed record DesktopVisualGeometry(DesktopTaskBounds Bounds, int Dpi);
internal sealed record DesktopVisualFocus(long Handle, uint LastInputTime);

internal interface IDesktopVisualNative
{
    DesktopTaskWindow RequireWindow(DesktopTaskWindow window);
    DesktopVisualGeometry ReadGeometry(DesktopTaskWindow window);
    void PreparePointer(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds,
        Action validate, CancellationToken token);
    uint Click(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds,
        bool doubleClick, Action validate, CancellationToken token);
    DesktopVisualFocus RequireCaret(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds);
    void TypeText(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds,
        DesktopVisualFocus focus, string text, Action validate, CancellationToken token);
    void Scroll(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds,
        bool up, Action validate, CancellationToken token);
}

/// <summary>Bounded single-window visual input. Host owns consent, process lifetime and task state.</summary>
public sealed class WindowsDesktopVisualAutomation : IDesktopVisualAutomation
{
    private readonly IWindowCaptureService _capture;
    private readonly ILocalOcrTextExtractor _ocr;
    private readonly IDesktopVisualNative _native;
    private readonly TimeProvider _time;

    public WindowsDesktopVisualAutomation()
    {
        var verifier = new WindowsWindowCaptureTargetVerifier();
        _capture = new WindowsSingleWindowCaptureService(new WindowsGraphicsCaptureBackend(verifier),
            new WindowsSensitiveWindowPolicy(), verifier);
        _ocr = new WindowsLocalOcrTextExtractor();
        _native = new WindowsDesktopVisualNative();
        _time = TimeProvider.System;
    }

    internal WindowsDesktopVisualAutomation(IWindowCaptureService capture, ILocalOcrTextExtractor ocr,
        IDesktopVisualNative native, TimeProvider? time = null) =>
        (_capture, _ocr, _native, _time) = (capture, ocr, native, time ?? TimeProvider.System);

    public Task<DesktopVisualObservation> ObserveAsync(DesktopTaskWindow window, CancellationToken token) => CaptureAsync(window, true, token);

    private async Task<DesktopVisualObservation> CaptureAsync(DesktopTaskWindow window, bool readText, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = _native.RequireWindow(window);
        var geometry = _native.ReadGeometry(current);
        DesktopVisualImage.RequireGeometry(geometry.Bounds, geometry.Dpi);
        var capturedAt = _time.GetUtcNow();
        using var frame = await _capture.CaptureAsync(new(current.Handle, current.Title, current.ProcessName,
            current.ProcessId, current.ProcessStartedAtUtc, capturedAt), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var after = _native.RequireWindow(current);
        if (after != current || _native.ReadGeometry(after) != geometry
            || frame.CaptureTechnology != "Windows.GraphicsCapture.SingleHwnd"
            || frame.PixelWidth != geometry.Bounds.Width || frame.PixelHeight != geometry.Bounds.Height)
            throw DesktopVisualImage.Error("window_changed", "窗口或截图范围发生变化，未执行视觉操作。");
        var image = DesktopVisualImage.Prepare(frame.PngBytes, frame.PixelWidth, frame.PixelHeight, token);
        try
        {
            var text = readText ? await _ocr.ExtractAsync(image.Png, token).ConfigureAwait(false) : string.Empty;
            token.ThrowIfCancellationRequested();
            if (_native.RequireWindow(current) != current || _native.ReadGeometry(current) != geometry)
                throw DesktopVisualImage.Error("window_changed", "读取期间窗口发生变化，已清除图像。");
            text ??= string.Empty;
            if (text.Length > DesktopVisualImage.MaximumOcrText) text = text[..DesktopVisualImage.MaximumOcrText];
            var snapshot = new DesktopVisualSnapshot(Guid.NewGuid(), current, geometry.Bounds,
                frame.PixelWidth, frame.PixelHeight, image.Width, image.Height, geometry.Dpi,
                image.ContentFingerprint, Convert.ToHexString(SHA256.HashData(image.Png)), text, capturedAt);
            return new(snapshot, image.Png);
        }
        catch { CryptographicOperations.ZeroMemory(image.Png); throw; }
    }

    public async Task<DesktopVisualReceipt> ExecuteAsync(DesktopVisualSnapshot expected, DesktopVisualProposal action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DesktopVisualImage.RequireSnapshot(expected, _time.GetUtcNow());
        DesktopVisualImage.RequireAction(expected, action);
        using var fresh = await CaptureAsync(expected.Window, false, token).ConfigureAwait(false);
        RequireSameFrame(fresh.Snapshot);
        var geometry = new DesktopVisualGeometry(expected.PhysicalBounds, expected.Dpi);
        var sourceBounds = DesktopVisualImage.MapBounds(expected, action.Bounds);
        Check();
        _native.PreparePointer(expected.Window, geometry, sourceBounds, Check, token);
        // Moving the pointer can open a hover menu or rearrange a custom canvas.
        // Recheck the whole source image AFTER that move, then send input without
        // moving the pointer again. Even a cosmetic hover change fails closed.
        using var pointed = await CaptureAsync(expected.Window, false, token).ConfigureAwait(false);
        RequireSameFrame(pointed.Snapshot);
        Check();
        switch (action.Action)
        {
            case DesktopVisualActionKind.Click:
            case DesktopVisualActionKind.DoubleClick:
                _native.Click(expected.Window, geometry, sourceBounds, action.Action == DesktopVisualActionKind.DoubleClick, Check, token);
                break;
            case DesktopVisualActionKind.TypeText:
                var clickGeneration = _native.Click(expected.Window, geometry, sourceBounds, false, Check, token);
                await Task.Delay(100, token).ConfigureAwait(false);
                Check();
                var focus = _native.RequireCaret(expected.Window, geometry, sourceBounds);
                if (focus.LastInputTime != clickGeneration)
                    throw DesktopVisualImage.Error("user_input", "点击之后检测到其他输入，已停止文字输入。");
                _native.TypeText(expected.Window, geometry, sourceBounds, focus, action.Text!, Check, token);
                break;
            case DesktopVisualActionKind.ScrollUp:
            case DesktopVisualActionKind.ScrollDown:
                _native.Scroll(expected.Window, geometry, sourceBounds, action.Action == DesktopVisualActionKind.ScrollUp, Check, token);
                break;
            default: throw DesktopVisualImage.Error("invalid_action", "不支持这类视觉输入。");
        }
        token.ThrowIfCancellationRequested();
        return new(true, "已发送本次窗口输入，仍需重新观察实际结果。");

        void RequireSameFrame(DesktopVisualSnapshot actual)
        {
            if (actual.Window != expected.Window || actual.PhysicalBounds != expected.PhysicalBounds || actual.Dpi != expected.Dpi
                || actual.SourceWidth != expected.SourceWidth || actual.SourceHeight != expected.SourceHeight
                || actual.ImageWidth != expected.ImageWidth || actual.ImageHeight != expected.ImageHeight
                || actual.ContentFingerprint != expected.ContentFingerprint || actual.ImageSha256 != expected.ImageSha256)
                throw DesktopVisualImage.Error("image_changed", "确认后的窗口画面或位置已变化，未执行旧图上的动作。");
        }

        void Check()
        {
            token.ThrowIfCancellationRequested();
            DesktopVisualImage.RequireSnapshot(expected, _time.GetUtcNow());
            if (_native.RequireWindow(expected.Window) != expected.Window || _native.ReadGeometry(expected.Window) != geometry)
                throw DesktopVisualImage.Error("window_changed", "窗口位置、焦点或身份改变，已停止后续输入。");
        }
    }
}
