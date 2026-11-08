using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using ScreenGuide.Vision.Windows;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class DesktopVisualImageDpiTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void PreparationKeepsEverySourcePixelAtItsOriginalCoordinateAcrossDpiMetadata(int dpi)
    {
        using var source = Pattern(100, 80, dpi);
        var png = Encode(source);
        var prepared = DesktopVisualImage.Prepare(png, source.Width, source.Height, CancellationToken.None);
        try
        {
            using var stream = new MemoryStream(prepared.Png);
            using var actual = new Bitmap(stream);
            Assert.Equal((source.Width, source.Height), (actual.Width, actual.Height));
            for (var y = 0; y < source.Height; y++)
                for (var x = 0; x < source.Width; x++)
                    Assert.True(source.GetPixel(x, y).ToArgb() == actual.GetPixel(x, y).ToArgb(),
                        $"PNG DPI {dpi}: source pixel ({x}, {y}) moved or changed.");
            Assert.Equal(PixelFingerprint(source), prepared.ContentFingerprint);
        }
        finally { CryptographicOperations.ZeroMemory(png); CryptographicOperations.ZeroMemory(prepared.Png); }
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void SourceFingerprintIncludesBottomRightPixelsAcrossDpiMetadata(int dpi)
    {
        using var source = Pattern(100, 80, dpi);
        var firstPng = Encode(source);
        source.SetPixel(source.Width - 1, source.Height - 1, Color.Magenta);
        var changedPng = Encode(source);
        var first = DesktopVisualImage.Prepare(firstPng, source.Width, source.Height, CancellationToken.None);
        var changed = DesktopVisualImage.Prepare(changedPng, source.Width, source.Height, CancellationToken.None);
        try
        {
            Assert.NotEqual(first.ContentFingerprint, changed.ContentFingerprint);
            Assert.Equal(PixelFingerprint(source), changed.ContentFingerprint);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(firstPng); CryptographicOperations.ZeroMemory(changedPng);
            CryptographicOperations.ZeroMemory(first.Png); CryptographicOperations.ZeroMemory(changed.Png);
        }
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void DownscalingUsesWholePixelRectangleRegardlessOfDpiMetadata(int dpi)
    {
        using var source = Pattern(3200, 200, dpi);
        var png = Encode(source);
        var prepared = DesktopVisualImage.Prepare(png, source.Width, source.Height, CancellationToken.None);
        try
        {
            Assert.Equal((1600, 100), (prepared.Width, prepared.Height));
            using var stream = new MemoryStream(prepared.Png);
            using var actual = new Bitmap(stream);
            // Solid corner patches remain visible after the intended 2:1 resize.
            Assert.Equal(Color.Red.ToArgb(), actual.GetPixel(4, 4).ToArgb());
            Assert.Equal(Color.Lime.ToArgb(), actual.GetPixel(actual.Width - 5, 4).ToArgb());
            Assert.Equal(Color.Blue.ToArgb(), actual.GetPixel(4, actual.Height - 5).ToArgb());
            Assert.Equal(Color.Yellow.ToArgb(), actual.GetPixel(actual.Width - 5, actual.Height - 5).ToArgb());
            Assert.Equal(PixelFingerprint(source), prepared.ContentFingerprint);
        }
        finally { CryptographicOperations.ZeroMemory(png); CryptographicOperations.ZeroMemory(prepared.Png); }
    }

    [Fact]
    public void ChangingOnlyDpiMetadataNeverChangesPixelFingerprintOrPreparedPng()
    {
        var fingerprints = new List<string>();
        var images = new List<string>();
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        {
            using var source = Pattern(100, 80, dpi);
            var png = Encode(source);
            var prepared = DesktopVisualImage.Prepare(png, source.Width, source.Height, CancellationToken.None);
            try
            {
                fingerprints.Add(prepared.ContentFingerprint);
                images.Add(Convert.ToHexString(SHA256.HashData(prepared.Png)));
            }
            finally { CryptographicOperations.ZeroMemory(png); CryptographicOperations.ZeroMemory(prepared.Png); }
        }
        Assert.Single(fingerprints.Distinct(StringComparer.Ordinal));
        Assert.Single(images.Distinct(StringComparer.Ordinal));
    }

    private static Bitmap Pattern(int width, int height, int dpi)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(dpi, dpi);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                bitmap.SetPixel(x, y, Color.Red);
                bitmap.SetPixel(width - 1 - x, y, Color.Lime);
                bitmap.SetPixel(x, height - 1 - y, Color.Blue);
                bitmap.SetPixel(width - 1 - x, height - 1 - y, Color.Yellow);
            }
        bitmap.SetPixel(width / 2, height / 2, Color.Black);
        return bitmap;
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    // Independent coordinate-based oracle: no DrawImage or DPI-based rendering.
    private static string PixelFingerprint(Bitmap source)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> dimensions = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, source.Width);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], source.Height);
        hash.AppendData(dimensions);
        var row = new byte[source.Width * 4];
        try
        {
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var color = source.GetPixel(x, y);
                    row[x * 4] = color.B; row[x * 4 + 1] = color.G;
                    row[x * 4 + 2] = color.R; row[x * 4 + 3] = color.A;
                }
                hash.AppendData(row);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(row); }
    }
}
