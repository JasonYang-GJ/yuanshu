using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ScreenGuide.AI.Core;

/// <summary>One bounded in-memory PNG. Callers own its lifetime; diagnostics never expose its bytes.</summary>
public sealed class ChatImage : IDisposable
{
    public const int MaximumEncodedBytes = 4 * 1024 * 1024;
    public const int MaximumDimension = 2048;
    private readonly object _gate = new();
    private byte[]? _png;

    public ChatImage(ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.Length is < 57 or > MaximumEncodedBytes)
            throw new ArgumentException("图像必须是大小受限的完整 PNG。", nameof(pngBytes));
        var owned = pngBytes.ToArray();
        try
        {
            (Width, Height) = ValidatePng(owned);
            EncodedByteCount = owned.Length;
            _png = owned;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(owned);
            throw;
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int EncodedByteCount { get; }
    public bool IsDisposed { get { lock (_gate) return _png is null; } }

    /// <summary>Returns an independent copy. The caller should clear it after use.</summary>
    public byte[] CopyPngBytes()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_png is null, this);
            return _png.ToArray();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_png is null) return;
            CryptographicOperations.ZeroMemory(_png);
            _png = null;
        }
    }

    public override string ToString() => $"ChatImage(PNG {Width}x{Height}, {EncodedByteCount} bytes, disposed={IsDisposed})";

    private static (int Width, int Height) ValidatePng(ReadOnlySpan<byte> png)
    {
        static ArgumentException Invalid() => new("图像不是当前支持的完整 PNG，或尺寸超出范围。", "pngBytes");
        if (!png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw Invalid();
        var offset = 8;
        var chunks = 0;
        var width = 0;
        var height = 0;
        var hasData = false;
        var dataEnded = false;
        while (offset <= png.Length - 12)
        {
            if (++chunks > 4096) throw Invalid();
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            if (length > png.Length - offset - 12) throw Invalid();
            var kind = png.Slice(offset + 4, 4);
            foreach (var character in kind)
                if (character is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')) throw Invalid();
            if (chunks == 1)
            {
                if (!kind.SequenceEqual("IHDR"u8) || length != 13) throw Invalid();
                var header = png.Slice(offset + 8, 13);
                var imageWidth = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                var imageHeight = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
                if (imageWidth is 0 or > MaximumDimension || imageHeight is 0 or > MaximumDimension
                    || header[10] != 0 || header[11] != 0 || header[12] > 1) throw Invalid();
                var bitDepth = header[8];
                var validDepth = header[9] switch
                {
                    0 => bitDepth is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => bitDepth is 8 or 16,
                    3 => bitDepth is 1 or 2 or 4 or 8,
                    _ => false
                };
                if (!validDepth) throw Invalid();
                width = (int)imageWidth;
                height = (int)imageHeight;
            }
            else if (kind.SequenceEqual("IHDR"u8)) throw Invalid();
            else if (kind.SequenceEqual("IDAT"u8))
            {
                if (dataEnded) throw Invalid();
                hasData |= length > 0;
            }
            else
            {
                if (hasData) dataEnded = true;
                if (kind.SequenceEqual("IEND"u8))
                {
                    if (!hasData || length != 0 || offset + 12 != png.Length) throw Invalid();
                    return (width, height);
                }
                // PNG's only other standard critical chunk is the optional palette.
                if ((kind[0] & 32) == 0 && !kind.SequenceEqual("PLTE"u8)) throw Invalid();
            }
            offset += checked((int)length + 12);
        }
        throw Invalid();
    }
}

/// <summary>Freezes message collections and rejects unsupported image input before provider side effects.</summary>
public static class ChatImageInputs
{
    public static ChatModelRequest Prepare(ChatModelRequest request, string providerId, bool supportsImages)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Leave existing missing-message errors to each provider, while never
        // retaining a mutable collection that could add an image after validation.
        if (request.Messages is null) return request;
        if (request.Messages.Count > 4096) throw Invalid(providerId, request.ModelId);
        var messages = request.Messages.ToArray();
        if (messages.Length > 4096) throw Invalid(providerId, request.ModelId);
        var imageCount = 0;
        for (var index = 0; index < messages.Length; index++)
        {
            var message = messages[index];
            if (message?.Images is not { } inputs) continue;
            if (inputs.Count > 1) throw Invalid(providerId, request.ModelId);
            var images = inputs.ToArray();
            if (images.Length > 1) throw Invalid(providerId, request.ModelId);
            foreach (var image in images)
            {
                if (++imageCount > 1 || message.Role != ChatMessageRole.User || image is null || image.IsDisposed)
                    throw Invalid(providerId, request.ModelId);
            }
            messages[index] = message with { Images = Array.AsReadOnly(images) };
        }
        if (imageCount > 0 && !supportsImages)
            throw new ChatModelException(providerId, request.ModelId, new ChatModelError(
                ChatModelErrorKind.InvalidRequest, "chat.vision_not_supported",
                "当前选定模型没有可用的识图能力，因此没有发送图片；请明确选择支持识图的模型后重试。"));
        return request with { Messages = Array.AsReadOnly(messages) };
    }

    private static ChatModelException Invalid(string providerId, string? modelId) => new(providerId, modelId,
        new ChatModelError(ChatModelErrorKind.InvalidRequest, "chat.image_input_invalid",
            "本次图像输入无效或超过范围，没有发送图片。"));
}
