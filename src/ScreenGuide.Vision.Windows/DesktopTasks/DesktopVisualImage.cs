using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Vision.Windows;

internal static class DesktopVisualImage
{
    internal const int MaximumImageBytes = 4 * 1024 * 1024;
    internal const int MaximumImageSide = 1600;
    internal const int MaximumSourcePixels = 32_000_000;
    internal const int MaximumText = 500;
    internal const int MaximumOcrText = 16000;
    internal static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(2);

    internal static DesktopTaskException Error(string code, string message) => new("desktop_visual_" + code, message);

    internal static void RequireGeometry(DesktopTaskBounds bounds, int dpi)
    {
        if (bounds is null || !IsInteger(bounds.X) || !IsInteger(bounds.Y) || !IsInteger(bounds.Width)
            || !IsInteger(bounds.Height) || bounds.Width < 2 || bounds.Height < 2
            || bounds.Width * bounds.Height > MaximumSourcePixels || dpi is < 48 or > 960
            || bounds.X + bounds.Width > int.MaxValue || bounds.Y + bounds.Height > int.MaxValue)
            throw Error("geometry", "无法可靠确定窗口的物理像素位置，未执行操作。");
    }

    internal static void RequireSnapshot(DesktopVisualSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot is null || snapshot.Id == Guid.Empty || snapshot.Window is null || snapshot.OcrText is null
            || snapshot.OcrText.Length > MaximumOcrText || snapshot.CapturedAtUtc == default
            || snapshot.CapturedAtUtc > now.AddSeconds(2) || now - snapshot.CapturedAtUtc > MaximumSnapshotAge
            || !IsHash(snapshot.ContentFingerprint) || !IsHash(snapshot.ImageSha256))
            throw Error("stale_snapshot", "视觉快照无效或已经过期，请重新观察窗口。");
        RequireGeometry(snapshot.PhysicalBounds, snapshot.Dpi);
        if (snapshot.SourceWidth != snapshot.PhysicalBounds.Width || snapshot.SourceHeight != snapshot.PhysicalBounds.Height
            || snapshot.ImageWidth < 2 || snapshot.ImageHeight < 2
            || snapshot.ImageWidth > MaximumImageSide || snapshot.ImageHeight > MaximumImageSide
            || snapshot.ImageWidth > snapshot.SourceWidth || snapshot.ImageHeight > snapshot.SourceHeight
            || Dimensions(snapshot.SourceWidth, snapshot.SourceHeight) != (snapshot.ImageWidth, snapshot.ImageHeight))
            throw Error("geometry", "图像尺寸与绑定窗口不一致，未执行操作。");
    }

    internal static void RequireAction(DesktopVisualSnapshot snapshot, DesktopVisualProposal action)
    {
        if (action is null || action.SnapshotId != snapshot.Id || !Enum.IsDefined(action.Action)
            || action.Action is DesktopVisualActionKind.Wait or DesktopVisualActionKind.Ask or DesktopVisualActionKind.Finish
            || string.IsNullOrWhiteSpace(action.Target) || action.Target.Length > 300
            || string.IsNullOrWhiteSpace(action.Reason) || action.Reason.Length > 1000)
            throw Error("invalid_action", "视觉动作没有绑定当前图像和明确目标。");
        _ = MapBounds(snapshot, action.Bounds);
        if (action.Action == DesktopVisualActionKind.TypeText) RequireText(action.Text, action.Target);
        else if (action.Text is not null) throw Error("invalid_action", "该视觉动作不接受附加文字。");
    }

    internal static void RequireText(string? text, string target)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumText
            || text.Any(c => char.IsControl(c) || char.IsSurrogate(c)
                || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format)
            || Regex.IsMatch(text, @"(?i)(?:[a-z][a-z0-9+.-]*://|\bwww\.|\b(?:bearer|sk-[a-z0-9]|eyJ[a-z0-9_-]{10,})|(?:api[ _-]?key|password|passwd|密码|密钥|口令|验证码|凭据)\s*[:=：])",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(text.TrimStart(), @"(?i)^(?:[=+@-]|cmd(?:\s|$)|powershell(?:\s|$)|pwsh(?:\s|$)|bash(?:\s|$)|sh\s|sudo\s|curl\s|wget\s|python(?:\d)?\s|node\s|invoke-|start-process\b|remove-item\b|reg\s|rundll32\b)", RegexOptions.CultureInvariant)
            || text.IndexOfAny(['`', '\u001b', '|', ';', '&', '<', '>']) >= 0
            || Regex.IsMatch(target, @"(?i)password|passcode|credential|secret|token|api[ _-]?key|密码|口令|密钥|凭据|验证码|安全码|银行卡|信用卡|终端|控制台|命令|devtools|开发者工具", RegexOptions.CultureInvariant))
            throw Error("text_not_supported", "视觉输入仅支持短篇单行普通文字，不输入命令、网址、密码或凭据。");
    }

    internal static DesktopVisualBounds MapBounds(DesktopVisualSnapshot snapshot, DesktopVisualBounds? bounds)
    {
        if (bounds is null || bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0
            || (long)bounds.X + bounds.Width > snapshot.ImageWidth
            || (long)bounds.Y + bounds.Height > snapshot.ImageHeight)
            throw Error("bounds", "模型提出的位置超出本次窗口图像，未移动鼠标。");
        var left = (int)((long)bounds.X * snapshot.SourceWidth / snapshot.ImageWidth);
        var top = (int)((long)bounds.Y * snapshot.SourceHeight / snapshot.ImageHeight);
        var right = (int)(((long)(bounds.X + bounds.Width) * snapshot.SourceWidth + snapshot.ImageWidth - 1) / snapshot.ImageWidth);
        var bottom = (int)(((long)(bounds.Y + bounds.Height) * snapshot.SourceHeight + snapshot.ImageHeight - 1) / snapshot.ImageHeight);
        return new(left, top, right - left, bottom - top);
    }

    internal static (byte[] Png, int Width, int Height, string ContentFingerprint) Prepare(
        ReadOnlyMemory<byte> png, int expectedWidth, int expectedHeight, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequirePng(png.Span, expectedWidth, expectedHeight);
        var copy = png.ToArray();
        try
        {
            using var stream = new MemoryStream(copy, writable: false);
            using var decoded = new Bitmap(stream);
            if (decoded.Width != expectedWidth || decoded.Height != expectedHeight)
                throw Error("geometry", "实际图像与窗口尺寸不一致，未执行操作。");
            using var source = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
            // DrawImageUnscaled still accounts for image/device DPI. Copy exact
            // decoded BGRA rows instead: even a one-pixel corner change must
            // remain in the source fingerprint and at its source coordinate.
            CopyPixels(decoded, source, token);
            source.SetResolution(96, 96);
            var fingerprint = Fingerprint(source, token);
            var (width, height) = Dimensions(source.Width, source.Height);
            using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            image.SetResolution(96, 96);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.PageUnit = GraphicsUnit.Pixel;
                graphics.PageScale = 1;
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height),
                    new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            }
            using var output = new MemoryStream();
            try
            {
                image.Save(output, ImageFormat.Png);
                token.ThrowIfCancellationRequested();
                if (output.Length > MaximumImageBytes) throw Error("image_too_large", "窗口图像过大，未发送或操作。");
                return (output.ToArray(), width, height, fingerprint);
            }
            finally { if (output.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan()); }
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    internal static (int Width, int Height) Dimensions(int width, int height)
    {
        var scale = Math.Min(1d, MaximumImageSide / (double)Math.Max(width, height));
        return (Math.Max(2, (int)Math.Floor(width * scale)), Math.Max(2, (int)Math.Floor(height * scale)));
    }

    private static void CopyPixels(Bitmap decoded, Bitmap destination, CancellationToken token)
    {
        var rectangle = new Rectangle(0, 0, decoded.Width, decoded.Height);
        var row = new byte[checked(decoded.Width * 4)];
        BitmapData? source = null;
        BitmapData? target = null;
        try
        {
            source = decoded.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            target = destination.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (var y = 0; y < decoded.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(IntPtr.Add(source.Scan0, checked(y * source.Stride)), row, 0, row.Length);
                Marshal.Copy(row, 0, IntPtr.Add(target.Scan0, checked(y * target.Stride)), row.Length);
            }
        }
        finally
        {
            if (target is not null) destination.UnlockBits(target);
            if (source is not null) decoded.UnlockBits(source);
            CryptographicOperations.ZeroMemory(row);
        }
    }

    private static string Fingerprint(Bitmap bitmap, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> dimensions = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, bitmap.Width);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], bitmap.Height);
        hash.AppendData(dimensions);
        var row = new byte[checked(bitmap.Width * 4)];
        var bits = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(IntPtr.Add(bits.Scan0, checked(y * bits.Stride)), row, 0, row.Length);
                hash.AppendData(row);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { bitmap.UnlockBits(bits); CryptographicOperations.ZeroMemory(row); }
    }

    private static void RequirePng(ReadOnlySpan<byte> bytes, int width, int height)
    {
        if (width < 2 || height < 2 || (long)width * height > MaximumSourcePixels || bytes.Length < 24
            || bytes.Length > MaximumSourcePixels * 4L + 1048576
            || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4)) != width
            || BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4)) != height)
            throw Error("invalid_image", "单窗口图像格式或尺寸不可靠，未执行操作。");
    }

    private static bool IsInteger(double value) => double.IsFinite(value) && value == Math.Truncate(value)
        && value >= int.MinValue && value <= int.MaxValue;
    private static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}
