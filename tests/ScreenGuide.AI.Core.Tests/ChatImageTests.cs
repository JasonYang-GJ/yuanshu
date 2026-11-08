using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class ChatImageTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP4/x8AAwAB//wl3FEAAAAASUVORK5CYII=";

    [Fact]
    public void OwnsDefensiveCopiesAndDoesNotExposeBytesInDiagnosticsOrSerialization()
    {
        var original = Convert.FromBase64String(Png);
        var expected = original.ToArray();
        using var image = new ChatImage(original);
        Array.Fill(original, (byte)0);
        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(expected.Length, image.EncodedByteCount);
        Assert.Equal(expected, image.CopyPngBytes());
        var outgoingCopy = image.CopyPngBytes();
        Array.Fill(outgoingCopy, (byte)0);
        Assert.Equal(expected, image.CopyPngBytes());
        Assert.DoesNotContain(Png, image.ToString(), StringComparison.Ordinal);
        var message = new ChatMessage(ChatMessageRole.User, "synthetic", Images: [image]);
        Assert.Equal("{\"Role\":1,\"Content\":\"synthetic\",\"Name\":null}", JsonSerializer.Serialize(message));
        Assert.DoesNotContain("data:image", message.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DisposeZerosOwnedStorageAndRejectsReuseWithoutOwningOutgoingCopies()
    {
        var image = new ChatImage(Convert.FromBase64String(Png));
        var owned = (byte[])typeof(ChatImage).GetField("_png", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(image)!;
        var outgoingCopy = image.CopyPngBytes();
        image.Dispose();
        image.Dispose();
        Assert.True(image.IsDisposed);
        Assert.All(owned, value => Assert.Equal(0, value));
        Assert.Equal(Convert.FromBase64String(Png), outgoingCopy);
        Assert.Throws<ObjectDisposedException>(() => image.CopyPngBytes());
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("header")]
    [InlineData("zero-width")]
    [InlineData("large-width")]
    [InlineData("large-height")]
    [InlineData("bit-depth")]
    [InlineData("color-type")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("chunk-length")]
    [InlineData("critical-chunk")]
    [InlineData("missing-data")]
    [InlineData("oversized")]
    public void RejectsMalformedOrUnboundedPng(string variant)
    {
        var bytes = Convert.FromBase64String(Png);
        switch (variant)
        {
            case "signature": bytes[0] = 0; break;
            case "header": bytes[12] = (byte)'X'; break;
            case "zero-width": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 0); break;
            case "large-width": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 2049); break;
            case "large-height": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), uint.MaxValue); break;
            case "bit-depth": bytes[24] = 3; break;
            case "color-type": bytes[25] = 7; break;
            case "truncated": bytes = bytes[..^1]; break;
            case "trailing": bytes = [.. bytes, 1]; break;
            case "chunk-length": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(33, 4), uint.MaxValue); break;
            case "critical-chunk": bytes[37] = (byte)'Z'; break;
            case "missing-data": bytes = [.. bytes[..33], .. bytes[^12..]]; break;
            case "oversized": bytes = new byte[ChatImage.MaximumEncodedBytes + 1]; break;
        }
        Assert.Throws<ArgumentException>(() => new ChatImage(bytes));
    }

    [Fact]
    public void FreezesCollectionsWithoutDroppingImageOrChangingText()
    {
        using var image = new ChatImage(Convert.FromBase64String(Png));
        List<ChatImage> images = [image];
        List<ChatMessage> messages = [new(ChatMessageRole.User, "image task", Images: images)];
        var request = Request(messages);
        var prepared = ChatImageInputs.Prepare(request, "test", supportsImages: true);
        images.Clear();
        messages[0] = new(ChatMessageRole.Assistant, "changed");
        var sent = Assert.Single(prepared.Messages);
        Assert.Equal("image task", sent.Content);
        Assert.Same(image, Assert.Single(sent.Images!));
        Assert.False(image.IsDisposed);
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("system")]
    [InlineData("tool")]
    [InlineData("two-same-message")]
    [InlineData("two-messages")]
    [InlineData("null-image")]
    [InlineData("disposed")]
    public void RejectsInvalidImageRequestsBeforeProviderUse(string variant)
    {
        using var image = new ChatImage(Convert.FromBase64String(Png));
        var messages = new List<ChatMessage> { new(ChatMessageRole.User, "look", Images: [image]) };
        switch (variant)
        {
            case "assistant": messages[0] = messages[0] with { Role = ChatMessageRole.Assistant }; break;
            case "system": messages[0] = messages[0] with { Role = ChatMessageRole.System }; break;
            case "tool": messages[0] = messages[0] with { Role = ChatMessageRole.Tool }; break;
            case "two-same-message": messages[0] = messages[0] with { Images = [image, image] }; break;
            case "two-messages": messages.Add(messages[0]); break;
            case "null-image": messages[0] = messages[0] with { Images = [null!] }; break;
            case "disposed": image.Dispose(); break;
        }
        var error = Assert.Throws<ChatModelException>(() => ChatImageInputs.Prepare(Request(messages), "test", true));
        Assert.Equal("chat.image_input_invalid", error.Error.Code);
        Assert.DoesNotContain(Png, error.ToString(), StringComparison.Ordinal);
    }

    private static ChatModelRequest Request(IReadOnlyList<ChatMessage> messages) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "test", "system", messages);
}
