using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ScreenGuide.Skills.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ScreenGuide.Vision.Windows;

public sealed record VisualSearchRect(int X, int Y, int Width, int Height);
public sealed record VisualSearchText(string Text, VisualSearchRect Bounds)
{
    public IReadOnlyList<VisualSearchText> Words { get; init; } = [];
}
public sealed record VisualSearchField(VisualSearchRect Bounds, VisualSearchRect LabelBounds);

/// <summary>Reads an already consented image in memory. It never captures, clicks, types, or retains images.</summary>
public sealed class VisualSearchImageReader
{
    private const int Padding = 16;
    private readonly Func<byte[], VisualSearchRect, CancellationToken, Task<IReadOnlyList<VisualSearchText>>>? _readLabels;

    public VisualSearchImageReader() { }

    // OCR is an external Windows service; deterministic boundary tests supply only its observations.
    internal VisualSearchImageReader(
        Func<byte[], VisualSearchRect, CancellationToken, Task<IReadOnlyList<VisualSearchText>>> readLabels)
        => _readLabels = readLabels;

    public async Task<IReadOnlyList<VisualSearchText>> ReadAsync(byte[] png, VisualSearchRect region, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var source = Decode(png);
        ValidateRegion(region, source.Width, source.Height);
        return await ReadRegionAsync(source, region, ct, includeSearchLabels: false).ConfigureAwait(false);
    }

    public async Task<VisualSearchField> FindUniqueSearchFieldAsync(byte[] png, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var source = Decode(png);
        // Deliberately exclude conversation bodies, documents, and lower content areas.
        var topHeight = Math.Min(280, source.Height / 3);
        var candidates = FindControlRegions(source, topHeight, ct);
        if (candidates.Count > 32) throw Ambiguous();
        var fields = new List<VisualSearchField>();
        var observations = new List<(VisualSearchRect Region, IReadOnlyList<VisualSearchText> Lines)>();
        foreach (var region in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var lines = _readLabels is null
                ? await ReadRegionAsync(source, region, ct, includeSearchLabels: true).ConfigureAwait(false)
                : await _readLabels(png, region, ct).ConfigureAwait(false);
            observations.Add((region, lines));
            var labels = lines.Where(line => IsExactSearchLabel(line.Text)
                && Contains(region, line.Bounds) && line.Bounds.Width > 0 && line.Bounds.Height > 0
                && region.Width >= line.Bounds.Width * 2L + 16).ToArray();
            if (labels.Length == 0) continue;
            // The same label may be observed in both raw and contrast passes.
            if (labels.Any(label => !SameLocation(label.Bounds, labels[0].Bounds))) throw Ambiguous();
            fields.Add(new VisualSearchField(region, labels[0].Bounds));
        }
        ct.ThrowIfCancellationRequested();
        // Exact placeholders take priority, including their ambiguity. A filled field
        // needs fresh pixel evidence of a magnifier, never a remembered location or OCR "0".
        if (fields.Count == 0)
        {
            foreach (var (region, lines) in observations)
            {
                ct.ThrowIfCancellationRequested();
                var icon = FindMagnifier(source, region, ct);
                if (icon is null) continue;
                var content = FindSeparatedContent(region, icon, lines);
                if (content is not null) fields.Add(new(region, content));
            }
        }
        ct.ThrowIfCancellationRequested();
        return fields.Count switch
        {
            1 => fields[0],
            > 1 => throw Ambiguous(),
            _ => throw NotFound()
        };
    }

    private static VisualSearchRect? FindSeparatedContent(VisualSearchRect field, VisualSearchRect icon,
        IReadOnlyList<VisualSearchText> lines)
    {
        var contentLeft = Math.Max(field.X + field.Height, icon.X + icon.Width + Math.Max(3, field.Height / 8));
        var contentRight = field.X + field.Width - field.Height;
        var observations = new List<VisualSearchRect>();
        foreach (var line in lines)
        {
            // Word geometry is mandatory: a merged icon/query word is not separated evidence.
            var words = line.Words.Where(word => !string.IsNullOrWhiteSpace(word.Text)
                && word.Bounds.Width > 0 && Contains(field, word.Bounds) && word.Bounds.X >= contentLeft
                && word.Bounds.X + (long)word.Bounds.Width <= contentRight
                && word.Bounds.Height >= 3 && word.Bounds.Height <= field.Height * .8
                && Math.Abs(word.Bounds.Y + word.Bounds.Height / 2d - (icon.Y + icon.Height / 2d)) <= field.Height / 4d).ToArray();
            if (words.Length == 0 || !words.Any(word => word.Text.Any(char.IsLetterOrDigit))) continue;
            var left = words.Min(word => word.Bounds.X);
            var top = words.Min(word => word.Bounds.Y);
            var right = words.Max(word => word.Bounds.X + word.Bounds.Width);
            var bottom = words.Max(word => word.Bounds.Y + word.Bounds.Height);
            observations.Add(new(left, top, right - left, bottom - top));
        }
        return observations.Count > 0 && observations.All(bounds => SameLocation(bounds, observations[0]))
            ? observations[0] : null;
    }

    private static VisualSearchRect? FindMagnifier(Bitmap source, VisualSearchRect field, CancellationToken ct)
    {
        var side = field.Height;
        var margin = Math.Max(2, side / 12);
        var tones = new int[256];
        for (var y = margin; y < side - margin; y++)
        for (var x = margin; x < side - margin; x++) tones[Luminance(source.GetPixel(field.X + x, field.Y + y))]++;
        var background = Array.IndexOf(tones, tones.Max());
        var ink = new bool[side * side];
        var visited = new bool[ink.Length];
        try
        {
            for (var y = margin; y < side - margin; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (var x = margin; x < side - margin; x++)
                {
                    var color = source.GetPixel(field.X + x, field.Y + y);
                    ink[y * side + x] = Math.Abs(Luminance(color) - background) >= 32
                        && Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B)) <= 30;
                }
            }
            var matches = new List<VisualSearchRect>();
            var queue = new Queue<int>();
            for (var seed = 0; seed < ink.Length; seed++)
            {
                if (!ink[seed] || visited[seed]) continue;
                ct.ThrowIfCancellationRequested();
                var component = new List<int>();
                visited[seed] = true;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    var p = queue.Dequeue();
                    component.Add(p);
                    var px = p % side; var py = p / side;
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var x = px + dx; var y = py + dy;
                        if (x < 0 || y < 0 || x >= side || y >= side) continue;
                        var next = y * side + x;
                        if (!ink[next] || visited[next]) continue;
                        visited[next] = true;
                        queue.Enqueue(next);
                    }
                }
                var left = component.Min(p => p % side); var right = component.Max(p => p % side);
                var top = component.Min(p => p / side); var bottom = component.Max(p => p / side);
                var width = right - left + 1; var height = bottom - top + 1;
                if (width < 7 || height < 7 || width > side * .8 || height > side * .8
                    || left <= margin || top <= margin || right >= side - margin - 1 || bottom >= side - margin - 1
                    || Math.Abs((top + bottom) / 2d - side / 2d) > side / 4d) continue;
                if (HasRingAndHandle(ink, side, component, left, top, width, height))
                    matches.Add(new(field.X + left, field.Y + top, width, height));
            }
            return matches.Count == 1 ? matches[0] : null;
        }
        finally
        {
            Array.Clear(ink, 0, ink.Length);
            Array.Clear(visited, 0, visited.Length);
        }
    }

    private static bool HasRingAndHandle(bool[] ink, int stride, IReadOnlyList<int> component,
        int left, int top, int width, int height)
    {
        // Flood the outside of the component's box. A true closed ring leaves one
        // enclosed background area; an open stroke, solid disk, or slashed zero does not.
        var outside = new bool[width * height];
        var queue = new Queue<int>();
        bool IsInk(int p) => ink[(top + p / width) * stride + left + p % width];
        void Add(int p)
        {
            if (outside[p] || IsInk(p)) return;
            outside[p] = true;
            queue.Enqueue(p);
        }
        for (var x = 0; x < width; x++) { Add(x); Add((height - 1) * width + x); }
        for (var y = 0; y < height; y++) { Add(y * width); Add(y * width + width - 1); }
        void Flood()
        {
            while (queue.Count > 0)
            {
                var p = queue.Dequeue(); var x = p % width; var y = p / width;
                if (x > 0) Add(p - 1);
                if (x + 1 < width) Add(p + 1);
                if (y > 0) Add(p - width);
                if (y + 1 < height) Add(p + width);
            }
        }
        Flood();
        var holes = Enumerable.Range(0, outside.Length).Where(p => !outside[p] && !IsInk(p)).ToArray();
        if (holes.Length < 9) return false;
        Add(holes[0]);
        Flood();
        if (holes.Any(p => !outside[p])) return false;
        var holeLeft = holes.Min(p => p % width); var holeRight = holes.Max(p => p % width);
        var holeTop = holes.Min(p => p / width); var holeBottom = holes.Max(p => p / width);
        var holeWidth = holeRight - holeLeft + 1; var holeHeight = holeBottom - holeTop + 1;
        if (holeWidth < 3 || holeHeight < 3 || holeWidth < holeHeight * .75 || holeHeight < holeWidth * .75) return false;
        var cx = left + (holeLeft + holeRight) / 2d;
        var cy = top + (holeTop + holeBottom) / 2d;
        var radii = component.Where(p => p % stride <= cx || p / stride <= cy)
            .Select(p => Math.Sqrt(Math.Pow(p % stride - cx, 2) + Math.Pow(p / stride - cy, 2))).Order().ToArray();
        if (radii.Length < 12) return false;
        var radius = radii[radii.Length / 2];
        if (radius < 3 || holes.Length < radius * radius * .8) return false;
        var sectors = new bool[16];
        var tail = 0;
        var longest = 0d;
        foreach (var p in component)
        {
            var dx = p % stride - cx; var dy = p / stride - cy;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            // Ordinary Q has ink crossing its inner circle. No such diagonal is allowed.
            if (distance < radius * .68) return false;
            if (distance > radius * 1.30)
            {
                if (dx < radius * .65 || dy < radius * .65 || dx > dy * 2 || dy > dx * 2
                    || distance > radius * 2.5) return false;
                tail++;
                longest = Math.Max(longest, distance);
            }
            else if (Math.Abs(distance - radius) <= Math.Max(1.25, radius * .25))
            {
                var sector = Math.Clamp((int)((Math.Atan2(dy, dx) + Math.PI) / (Math.PI * 2) * sectors.Length), 0, sectors.Length - 1);
                sectors[sector] = true;
            }
            else return false;
        }
        return sectors.Count(present => present) >= 14 && tail >= 2 && longest >= radius * 1.55;
    }

    internal static bool HasMatchingControlRegion(byte[] png, VisualSearchRect original, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var source = Decode(png);
        ValidateRegion(original, source.Width, source.Height);
        var candidates = FindControlRegions(source, Math.Min(280, source.Height / 3), ct);
        var matches = candidates.Count(region => Math.Abs((long)region.X - original.X) <= 4
            && Math.Abs((long)region.Y - original.Y) <= 4
            && Math.Abs(region.X + (long)region.Width - original.X - original.Width) <= 4
            && Math.Abs(region.Y + (long)region.Height - original.Y - original.Height) <= 4);
        ct.ThrowIfCancellationRequested();
        return matches == 1;
    }

    private static async Task<IReadOnlyList<VisualSearchText>> ReadRegionAsync(
        Bitmap source, VisualSearchRect region, CancellationToken ct, bool includeSearchLabels)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? throw NotFound();
        var results = new List<VisualSearchText>();
        // The 2x threshold pass recovers small low-contrast placeholders; raw color
        // is retained as a second observation for text that thresholding could erase.
        foreach (var enhanced in new[] { true, false })
        {
            ct.ThrowIfCancellationRequested();
            var requestedScale = enhanced ? 2d : 1d;
            var scale = Math.Min(requestedScale, OcrEngine.MaxImageDimension /
                (double)Math.Max(region.Width + Padding * 2L, region.Height + Padding * 2L));
            using var image = Prepare(source, region, enhanced, scale, ct);
            using var png = new MemoryStream();
            image.Save(png, ImageFormat.Png);
            var bytes = png.ToArray();
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
                    await writer.FlushAsync().AsTask(ct).ConfigureAwait(false);
                    writer.DetachStream();
                }
                stream.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied).AsTask(ct).ConfigureAwait(false);
                var recognized = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
                foreach (var line in recognized.Lines)
                {
                    if (line.Words.Count == 0 || string.IsNullOrWhiteSpace(line.Text)) continue;
                    var left = Math.Max(region.X, (int)Math.Floor(line.Words.Min(w => w.BoundingRect.Left) / scale - Padding + region.X));
                    var top = Math.Max(region.Y, (int)Math.Floor(line.Words.Min(w => w.BoundingRect.Top) / scale - Padding + region.Y));
                    var right = Math.Min(region.X + region.Width, (int)Math.Ceiling(line.Words.Max(w => w.BoundingRect.Right) / scale - Padding + region.X));
                    var bottom = Math.Min(region.Y + region.Height, (int)Math.Ceiling(line.Words.Max(w => w.BoundingRect.Bottom) / scale - Padding + region.Y));
                    if (right <= left || bottom <= top) continue;
                    var words = line.Words.Select(word => MapWord(word.Text, region, scale,
                        word.BoundingRect.Left, word.BoundingRect.Top, word.BoundingRect.Right, word.BoundingRect.Bottom)).ToArray();
                    var result = new VisualSearchText(line.Text, new(left, top, right - left, bottom - top)) { Words = words };
                    foreach (var observation in ObserveLine(result, words, includeSearchLabels))
                    {
                        if (!results.Any(existing => Normalize(existing.Text).Equals(Normalize(observation.Text),
                            StringComparison.OrdinalIgnoreCase) && SameLocation(existing.Bounds, observation.Bounds)))
                            results.Add(observation);
                    }
                }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        ct.ThrowIfCancellationRequested();
        return results;
    }

    internal static VisualSearchText MapWord(string text, VisualSearchRect region, double scale,
        double left, double top, double right, double bottom)
    {
        var x = (int)Math.Floor(left / scale - Padding + region.X);
        var y = (int)Math.Floor(top / scale - Padding + region.Y);
        var r = (int)Math.Ceiling(right / scale - Padding + region.X);
        var b = (int)Math.Ceiling(bottom / scale - Padding + region.Y);
        return new(text, new(x, y, r - x, b - y));
    }

    // Only candidate discovery may separate a known icon/caret from an exact placeholder.
    // Input and results readback must retain the complete OCR line as evidence.
    internal static IReadOnlyList<VisualSearchText> ObserveLine(VisualSearchText line,
        IReadOnlyList<VisualSearchText> words, bool includeSearchLabels)
    {
        if (!includeSearchLabels || words.Count < 2 || words.Any(word => word.Bounds.Width <= 0 || word.Bounds.Height <= 0))
            return [line];
        static bool IsLabelWord(VisualSearchText word) => Normalize(word.Text) is "搜" or "索" or "搜索"
            || Normalize(word.Text).Equals("Search", StringComparison.OrdinalIgnoreCase);
        var labelWords = words.Where(IsLabelWord).ToArray();
        var labelText = string.Concat(labelWords.Select(word => Normalize(word.Text)));
        var others = words.Where(word => !IsLabelWord(word)).ToArray();
        if (!IsExactSearchLabel(labelText) || others.Length is < 1 or > 2) return [line];
        var left = labelWords.Min(word => word.Bounds.X);
        var top = labelWords.Min(word => word.Bounds.Y);
        var right = labelWords.Max(word => word.Bounds.X + word.Bounds.Width);
        var bottom = labelWords.Max(word => word.Bounds.Y + word.Bounds.Height);
        var bounds = new VisualSearchRect(left, top, right - left, bottom - top);
        static bool SameBaseline(VisualSearchRect first, VisualSearchRect second) =>
            Math.Min(first.Y + (long)first.Height, second.Y + (long)second.Height) - Math.Max(first.Y, second.Y)
                >= Math.Min(first.Height, second.Height) * .5;
        for (var index = 1; index < labelWords.Length; index++)
        {
            var previous = labelWords[index - 1].Bounds;
            var current = labelWords[index].Bounds;
            if (current.X < previous.X || current.X - (previous.X + (long)previous.Width) > bounds.Height
                || !SameBaseline(previous, current)) return [line];
        }
        var icons = 0;
        var carets = 0;
        foreach (var other in others)
        {
            var token = other.Bounds;
            if (!SameBaseline(token, bounds)) return [line];
            var gapBefore = bounds.X - (token.X + (long)token.Width);
            var gapAfter = token.X - (bounds.X + (long)bounds.Width);
            switch (Normalize(other.Text))
            {
                case "Q" or "0" or "〇":
                    // A separated icon is on the left, never a suffix or a character within the label.
                    if (++icons > 1 || gapBefore < Math.Max(2, bounds.Height / 5)
                        || gapBefore > bounds.Height * 2L || token.Width > bounds.Height * 1.5)
                        return [line];
                    break;
                case "|" or "丨":
                    // A caret is narrow and immediately beside either end of the placeholder.
                    var gap = gapBefore >= 0 ? gapBefore : gapAfter;
                    if (++carets > 1 || token.Width * 3L > token.Height || gap < 0
                        || gap > Math.Max(2, bounds.Height / 2)) return [line];
                    break;
                default:
                    return [line];
            }
        }
        return [line, new VisualSearchText(labelText, bounds)];
    }

    private static Bitmap Prepare(Bitmap source, VisualSearchRect region, bool enhanced, double scale, CancellationToken ct)
    {
        using var padded = new Bitmap(region.Width + Padding * 2, region.Height + Padding * 2, PixelFormat.Format32bppArgb);
        padded.SetResolution(96, 96);
        using (var graphics = Graphics.FromImage(padded))
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.Clear(Color.White);
            graphics.DrawImage(source, new Rectangle(Padding, Padding, region.Width, region.Height),
                new Rectangle(region.X, region.Y, region.Width, region.Height), GraphicsUnit.Pixel);
        }
        if (enhanced)
        {
            var histogram = new int[256];
            for (var y = 0; y < region.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (var x = 0; x < region.Width; x++) histogram[Luminance(padded.GetPixel(x + Padding, y + Padding))]++;
            }
            var background = Array.IndexOf(histogram, histogram.Max());
            for (var y = 0; y < region.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (var x = 0; x < region.Width; x++)
                {
                    var value = Luminance(padded.GetPixel(x + Padding, y + Padding));
                    if (background < 128) value = 255 - Math.Clamp(value - background, 0, 255);
                    padded.SetPixel(x + Padding, y + Padding, value < 220 ? Color.Black : Color.White);
                }
            }
        }
        var scaled = new Bitmap(Math.Max(1, (int)Math.Floor(padded.Width * scale)),
            Math.Max(1, (int)Math.Floor(padded.Height * scale)), PixelFormat.Format32bppArgb);
        scaled.SetResolution(96, 96);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.Clear(Color.White);
            graphics.InterpolationMode = enhanced ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(padded, new Rectangle(0, 0, scaled.Width, scaled.Height),
                0, 0, padded.Width, padded.Height, GraphicsUnit.Pixel);
        }
        return scaled;
    }

    private static List<VisualSearchRect> FindControlRegions(Bitmap source, int height, CancellationToken ct)
    {
        var regions = new List<VisualSearchRect>();
        if (height < 16) return regions;
        var width = source.Width;
        var pixels = new byte[checked(width * height * 4)];
        var locked = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++) Marshal.Copy(locked.Scan0 + y * locked.Stride, pixels, y * width * 4, width * 4);
        }
        finally { source.UnlockBits(locked); }
        try
        {
            var visited = new bool[width * height];
            var queue = new Queue<int>();
            bool Neutral(int p) => Math.Max(pixels[p * 4], Math.Max(pixels[p * 4 + 1], pixels[p * 4 + 2]))
                - Math.Min(pixels[p * 4], Math.Min(pixels[p * 4 + 1], pixels[p * 4 + 2])) <= 24;
            for (var y = 0; y < height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    var seed = y * width + x;
                    if (visited[seed] || !Neutral(seed)) continue;
                    var tone = pixels[seed * 4 + 1];
                    if (tone is > 110 and < 180) continue;
                    visited[seed] = true;
                    queue.Enqueue(seed);
                    int left = x, right = x, top = y, bottom = y, count = 0;
                    while (queue.Count > 0)
                    {
                        var p = queue.Dequeue();
                        if ((++count & 4095) == 0) ct.ThrowIfCancellationRequested();
                        var px = p % width; var py = p / width;
                        left = Math.Min(left, px); right = Math.Max(right, px);
                        top = Math.Min(top, py); bottom = Math.Max(bottom, py);
                        if (px > 0) Add(p - 1);
                        if (px + 1 < width) Add(p + 1);
                        if (py > 0) Add(p - width);
                        if (py + 1 < height) Add(p + width);
                    }
                    var w = right - left + 1; var h = bottom - top + 1;
                    if (w >= 100 && h is >= 16 and <= 96 && w >= h * 2 && w <= h * 24
                        && count >= w * (double)h * .72 && top > 0 && bottom + 1 < height)
                        regions.Add(new(left, top, w, h));

                    void Add(int p)
                    {
                        if (visited[p] || !Neutral(p) || Math.Abs(pixels[p * 4 + 1] - tone) > 6) return;
                        visited[p] = true;
                        queue.Enqueue(p);
                    }
                }
            }
            return regions;
        }
        finally { CryptographicOperations.ZeroMemory(pixels); }
    }

    private static Bitmap Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        using var stream = new MemoryStream(png, writable: false);
        using var decoded = new Bitmap(stream);
        if (decoded.Width <= 0 || decoded.Height <= 0 || decoded.Width * (long)decoded.Height > 32_000_000)
            throw new ArgumentException("图像尺寸不在本机搜索读取范围内。", nameof(png));
        var result = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
        result.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(result);
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.Clear(Color.White);
        graphics.DrawImage(decoded, new Rectangle(0, 0, result.Width, result.Height),
            0, 0, decoded.Width, decoded.Height, GraphicsUnit.Pixel);
        return result;
    }

    private static void ValidateRegion(VisualSearchRect region, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
            || region.X + (long)region.Width > width || region.Y + (long)region.Height > height)
            throw new ArgumentOutOfRangeException(nameof(region), "读取区域必须完整处于本次窗口图像内。");
    }

    private static int Luminance(Color value) => (value.R * 299 + value.G * 587 + value.B * 114) / 1000;
    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    private static bool IsExactSearchLabel(string text) => Normalize(text) is "搜索"
        || Normalize(text).Equals("Search", StringComparison.OrdinalIgnoreCase);
    private static bool Contains(VisualSearchRect outer, VisualSearchRect inner) => inner.X >= outer.X && inner.Y >= outer.Y
        && inner.X + (long)inner.Width <= outer.X + (long)outer.Width && inner.Y + (long)inner.Height <= outer.Y + (long)outer.Height;
    private static bool SameLocation(VisualSearchRect first, VisualSearchRect second)
    {
        var intersection = Math.Max(0L, Math.Min(first.X + (long)first.Width, second.X + (long)second.Width) - Math.Max(first.X, second.X))
            * Math.Max(0L, Math.Min(first.Y + (long)first.Height, second.Y + (long)second.Height) - Math.Max(first.Y, second.Y));
        return intersection >= Math.Min(first.Width * (long)first.Height, second.Width * (long)second.Height) * .65;
    }
    private static DesktopSearchException NotFound() => new(DesktopSearchErrorCodes.CandidateNotFound,
        "没有找到唯一、清晰的搜索输入区域；没有点击或输入。");
    private static DesktopSearchException Ambiguous() => new(DesktopSearchErrorCodes.CandidateAmbiguous,
        "发现多个可能的搜索区域，无法可靠确定目标；没有点击或输入。");
}
