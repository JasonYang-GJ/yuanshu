using System.Security.Cryptography;

namespace ScreenGuide.Skills.Abstractions;

public enum DesktopVisualActionKind { Click, DoubleClick, TypeText, ScrollUp, ScrollDown, Wait, Ask, Finish }

// Coordinates are pixels in the exact image sent to the model, never screen coordinates.
public sealed record DesktopVisualBounds(int X, int Y, int Width, int Height);

public sealed record DesktopVisualSnapshot(Guid Id, DesktopTaskWindow Window, DesktopTaskBounds PhysicalBounds,
    int SourceWidth, int SourceHeight, int ImageWidth, int ImageHeight, int Dpi, string ContentFingerprint,
    string ImageSha256, string OcrText, DateTimeOffset CapturedAtUtc);

public sealed record DesktopVisualProposal(Guid SnapshotId, DesktopVisualActionKind Action,
    DesktopVisualBounds? Bounds, string? Text, string Target, string Reason, string? EvidenceText = null);

public sealed record DesktopVisualReceipt(bool InputSent, string Summary);

/// <summary>Owns a transient screenshot. Disposal clears its bytes; images must never be persisted.</summary>
public sealed class DesktopVisualObservation : IDisposable
{
    private byte[]? _pngBytes;

    public DesktopVisualObservation(DesktopVisualSnapshot snapshot, byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0) throw new ArgumentException("窗口图像不能为空。", nameof(pngBytes));
        Snapshot = snapshot;
        _pngBytes = pngBytes;
    }

    public DesktopVisualSnapshot Snapshot { get; }
    public ReadOnlyMemory<byte> PngBytes => _pngBytes ?? throw new ObjectDisposedException(nameof(DesktopVisualObservation));
    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _pngBytes, null);
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }
}

public interface IDesktopVisualAutomation
{
    Task<DesktopVisualObservation> ObserveAsync(DesktopTaskWindow window, CancellationToken token);
    Task<DesktopVisualReceipt> ExecuteAsync(DesktopVisualSnapshot expected, DesktopVisualProposal action, CancellationToken token);
}

public interface IDesktopVisualPreviewLease : IDisposable
{
    bool IsVisible { get; }
}

public interface IDesktopVisualTargetPreview
{
    Task<IDesktopVisualPreviewLease> ShowAsync(DesktopVisualObservation observation,
        DesktopVisualProposal proposal, CancellationToken token);
}
