using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Windows;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class VisualSearchImageReaderTests
{
    [Theory]
    [InlineData(-1, 0, 10, 10)]
    [InlineData(0, -1, 10, 10)]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, 0)]
    [InlineData(480, 0, 30, 20)]
    [InlineData(int.MaxValue, 0, int.MaxValue, 20)]
    public async Task ReadRejectsInvalidRegionBeforeOcr(int x, int y, int width, int height)
    {
        var png = Image([]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new VisualSearchImageReader()
            .ReadAsync(png, new(x, y, width, height), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectsLightOrDarkControlFromImageAndReturnsObservedLabelCoordinates(bool dark)
    {
        var field = new VisualSearchRect(37, 53, 170, 34);
        var label = new VisualSearchRect(98, 63, 32, 15);
        var reader = Reader((_, region, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>(
            region == field ? [new("搜 索", label)] : []));
        Assert.Equal(new VisualSearchField(field, label), await reader.FindUniqueSearchFieldAsync(Image([field], dark), default));
    }

    [Fact]
    public async Task MultipleRealImageCandidatesWithMatchingLabelsAreAmbiguous()
    {
        var fields = new[] { new VisualSearchRect(20, 40, 170, 34), new VisualSearchRect(240, 40, 170, 34) };
        var reader = Reader((_, region, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>(
            [new("Search", new(region.X + 30, region.Y + 8, 50, 17))]));
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => reader.FindUniqueSearchFieldAsync(Image(fields), default));
        Assert.Equal(DesktopSearchErrorCodes.CandidateAmbiguous, error.Code);
    }

    [Theory]
    [InlineData(72)]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public async Task SearchFieldPixelCoordinatesDoNotScaleWithPngDpi(int dpi)
    {
        var field = new VisualSearchRect(37, 53, 170, 34);
        var label = new VisualSearchRect(98, 63, 32, 15);
        var reader = Reader((_, region, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>(
            region == field ? [new("搜索", label)] : []));
        Assert.Equal(new VisualSearchField(field, label),
            await reader.FindUniqueSearchFieldAsync(Image([field], dpi: dpi), default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("已输入的关键词")]
    [InlineData("搜索结果")]
    [InlineData("Search settings")]
    public async Task MissingOrNonplaceholderTextNeverAuthorizesAField(string text)
    {
        var field = new VisualSearchRect(50, 50, 200, 36);
        var reader = Reader((_, region, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>(
            [new(text, new(region.X + 20, region.Y + 9, 60, 18))]));
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => reader.FindUniqueSearchFieldAsync(Image([field]), default));
        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
    }

    [Fact]
    public async Task LabelOutsideDetectedControlIsRejected()
    {
        var reader = Reader((_, _, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>([new("搜索", new(300, 50, 30, 20))]));
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => reader.FindUniqueSearchFieldAsync(
            Image([new(50, 50, 200, 36)]), default));
        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
    }

    [Fact]
    public async Task DoesNotInspectControlsBelowTopThirdOfWindow()
    {
        var called = false;
        var reader = Reader((_, _, _) => { called = true; return Task.FromResult<IReadOnlyList<VisualSearchText>>([]); });
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => reader.FindUniqueSearchFieldAsync(
            Image([new(50, 300, 200, 36)]), default));
        Assert.False(called);
        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
    }

    [Fact]
    public async Task CancellationPreventsDecodeAndOcr()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = new VisualSearchImageReader();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync([], new(0, 0, 1, 1), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.FindUniqueSearchFieldAsync([], cancellation.Token));
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("0")]
    [InlineData("〇")]
    public void SearchLabelMayExcludeOneSeparatedMagnifierToItsLeft(string glyph)
    {
        var line = new VisualSearchText($"{glyph} 搜 索", new(30, 20, 49, 14));
        VisualSearchText[] words = [new(glyph, new(30, 20, 12, 14)),
            new("搜", new(50, 20, 14, 14)), new("索", new(65, 20, 14, 14))];
        var observations = VisualSearchImageReader.ObserveLine(line, words, includeSearchLabels: true);

        Assert.Equal(line, observations[0]);
        Assert.Equal(new VisualSearchText("搜索", new(50, 20, 29, 14)), Assert.Single(observations.Skip(1)));
    }

    [Theory]
    [InlineData("|", 47)]
    [InlineData("丨", 47)]
    [InlineData("|", 82)]
    [InlineData("丨", 82)]
    public void ThinCaretMayBeImmediatelyBesideExactPlaceholder(string glyph, int x)
    {
        var line = new VisualSearchText($"0 {glyph} 搜索", new(30, 18, 53, 18));
        VisualSearchText[] words = [new("0", new(30, 20, 12, 14)),
            new("搜索", new(50, 20, 30, 14)), new(glyph, new(x, 18, 1, 18))];
        var observations = VisualSearchImageReader.ObserveLine(line, words, includeSearchLabels: true);

        Assert.Equal(new VisualSearchText("搜索", new(50, 20, 30, 14)), Assert.Single(observations.Skip(1)));
    }

    [Theory]
    [InlineData("Q", 85, 20, 12, 14)] // Magnifier after the label.
    [InlineData("0", 85, 20, 12, 14)]
    [InlineData("〇", 85, 20, 12, 14)]
    [InlineData("0", 63, 20, 5, 14)] // A glyph between the two label characters.
    [InlineData("|", 63, 18, 1, 18)]
    [InlineData("丨", 63, 18, 1, 18)]
    [InlineData("0", 40, 20, 10, 14)] // No separation from a numeric prefix.
    [InlineData("Q", 30, 50, 12, 14)] // On a different baseline.
    [InlineData("|", 30, 18, 1, 18)] // A distant vertical stroke.
    [InlineData("|", 83, 18, 10, 18)] // A wide character rather than a caret.
    [InlineData("O", 30, 20, 12, 14)] // Unverified glyph.
    [InlineData("结果", 30, 20, 12, 14)]
    public void OtherTextOrGlyphsInWrongPositionsCannotBecomeAPlaceholder(string glyph, int x, int y, int width, int height)
    {
        var line = new VisualSearchText($"搜 {glyph} 索", new(20, 18, 90, 50));
        VisualSearchText[] words = [new("搜", new(50, 20, 10, 14)),
            new("索", new(70, 20, 10, 14)), new(glyph, new(x, y, width, height))];

        Assert.Equal(line, Assert.Single(VisualSearchImageReader.ObserveLine(line, words, includeSearchLabels: true)));
    }

    [Theory]
    [InlineData("0搜索")]
    [InlineData("搜索结果")]
    [InlineData("Search settings")]
    [InlineData("已输入的关键词")]
    public void ExistingQueryOrAttachedContextDoesNotProduceASearchLabel(string text)
    {
        var line = new VisualSearchText(text, new(50, 20, 90, 14));
        Assert.Equal(line, Assert.Single(VisualSearchImageReader.ObserveLine(line, [line], includeSearchLabels: true)));
    }

    [Fact]
    public void InputAndResultsReadbackRetainsTheFullObservedText()
    {
        VisualSearchText[] words = [new("0", new(30, 20, 12, 14)), new("搜索", new(50, 20, 30, 14))];
        var line = new VisualSearchText("0 搜索", new(30, 20, 50, 14)) { Words = words };

        var observed = Assert.Single(VisualSearchImageReader.ObserveLine(line, words, includeSearchLabels: false));
        Assert.Equal(line, observed);
        Assert.Same(words, observed.Words);
    }

    [Theory]
    [InlineData(1, 26.25, 23.25, 40.75, 37.75)]
    [InlineData(2, 52.5, 46.5, 81.5, 75.5)]
    public void RawAndEnhancedWordBoundsMapBackToWindowPixels(double scale, double left, double top, double right, double bottom)
    {
        var word = VisualSearchImageReader.MapWord("测试", new(137, 41, 180, 35), scale, left, top, right, bottom);

        Assert.Equal("测试", word.Text);
        Assert.Equal(new VisualSearchRect(147, 48, 15, 15), word.Bounds);
        Assert.Empty(word.Words);
    }

    [Fact]
    public void WordMappingRetainsObservedPaddingOverlapInsteadOfMovingItInsideTheField()
    {
        var word = VisualSearchImageReader.MapWord("0", new(137, 41, 180, 35), 1, 15.5, 15.5, 20.25, 20.25);

        Assert.Equal("0", word.Text);
        Assert.Equal(new VisualSearchRect(136, 40, 6, 6), word.Bounds);
    }

    [Fact]
    public void CurrentControlMayInsetByFourPixelsOnEveryEdge()
    {
        var original = new VisualSearchRect(37, 53, 170, 34);
        Assert.True(VisualSearchImageReader.HasMatchingControlRegion(Image([new(41, 57, 162, 26)]), original, default));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void CurrentControlTranslationRequiresEveryEdgeWithinFourPixels(int shift, bool expected)
    {
        var original = new VisualSearchRect(37, 53, 170, 34);
        Assert.Equal(expected, VisualSearchImageReader.HasMatchingControlRegion(
            Image([new(original.X + shift, original.Y, original.Width, original.Height)]), original, default));
    }

    [Fact]
    public void TwoCurrentRegionsWithinTheOriginalBoundaryToleranceAreRejected()
    {
        using var bitmap = new Bitmap(500, 600);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.Clear(Color.FromArgb(130, 130, 130));
        using var first = new SolidBrush(Color.FromArgb(249, 249, 249));
        using var second = new SolidBrush(Color.FromArgb(190, 190, 190));
        // Two distinct connected backgrounds, each with 75% fill and 120x16 bounds.
        // Their vertical extents are 36..52 and 44..60, both within 4 px of 40..56.
        graphics.FillRectangle(first, 20, 36, 120, 12);
        graphics.FillRectangle(second, 20, 48, 120, 12);
        graphics.FillRectangle(first, 20, 48, 1, 4);
        graphics.FillRectangle(first, 139, 48, 1, 4);
        graphics.FillRectangle(second, 21, 44, 1, 4);
        graphics.FillRectangle(second, 138, 44, 1, 4);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);

        Assert.False(VisualSearchImageReader.HasMatchingControlRegion(output.ToArray(), new(20, 40, 120, 16), default));
    }

    [Theory]
    [InlineData(false, 37, 53)]
    [InlineData(true, 37, 53)]
    [InlineData(false, 210, 110)]
    [InlineData(true, 210, 110)]
    public async Task ExistingTextRequiresACurrentMagnifierAndReturnsCurrentImageCoordinates(bool dark, int x, int y)
    {
        var field = new VisualSearchRect(x, y, 180, 34);
        var reader = Reader((_, region, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>([Content(region)]));

        var found = await reader.FindUniqueSearchFieldAsync(GlyphImage([field], "glass", dark), default);
        Assert.Equal(field, found.Bounds);
        Assert.Equal(Content(field).Words[1].Bounds, found.LabelBounds);
    }

    [Theory]
    [InlineData("circle", false)]
    [InlineData("circle", true)]
    [InlineData("zero", false)]
    [InlineData("zero", true)]
    [InlineData("Q", false)]
    [InlineData("Q", true)]
    [InlineData("wrong_handle", false)]
    [InlineData("wrong_handle", true)]
    public async Task OrdinaryCircularCharactersDoNotIdentifySearchFields(string glyph, bool dark)
    {
        var field = new VisualSearchRect(37, 53, 180, 34);
        var reader = Reader((_, region, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>([Content(region)]));
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() =>
            reader.FindUniqueSearchFieldAsync(GlyphImage([field], glyph, dark), default));
        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
    }

    [Fact]
    public async Task ARealMagnifierOutsideTheLeftIconSlotIsRejected()
    {
        var field = new VisualSearchRect(37, 53, 180, 34);
        var reader = Reader((_, region, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>([Content(region)]));
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() =>
            reader.FindUniqueSearchFieldAsync(GlyphImage([field], "glass", iconShift: 45), default));
        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARealMagnifierNeedsNonemptyTextClearlySeparatedFromIt(bool touchingIcon)
    {
        var field = new VisualSearchRect(37, 53, 180, 34);
        var reader = Reader((_, region, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>(
            touchingIcon ? [Content(region, contentOffset: 26)] : []));
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() =>
            reader.FindUniqueSearchFieldAsync(GlyphImage([field], "glass"), default));
        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
    }

    [Fact]
    public async Task TwoFilledFieldsWithRealMagnifiersAreAmbiguous()
    {
        VisualSearchRect[] fields = [new(20, 40, 180, 34), new(250, 40, 180, 34)];
        var reader = Reader((_, region, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>([Content(region)]));
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() =>
            reader.FindUniqueSearchFieldAsync(GlyphImage(fields, "glass"), default));
        Assert.Equal(DesktopSearchErrorCodes.CandidateAmbiguous, error.Code);
    }

    [Fact]
    public async Task ExactPlaceholderWinsBeforeTheFilledFieldFallback()
    {
        VisualSearchRect[] fields = [new(20, 40, 180, 34), new(250, 40, 180, 34)];
        var reader = Reader((_, region, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>(
            region == fields[0] ? [new("搜索", new(region.X + 45, region.Y + 9, 30, 16))] : [Content(region)]));
        Assert.Equal(fields[0], (await reader.FindUniqueSearchFieldAsync(GlyphImage(fields, "glass"), default)).Bounds);
    }

    [Fact]
    public async Task MultipleExactPlaceholdersStayAmbiguousEvenWithAValidFilledField()
    {
        VisualSearchRect[] fields = [new(20, 40, 180, 34), new(250, 40, 180, 34), new(20, 110, 180, 34)];
        var reader = Reader((_, region, _) => Task.FromResult<IReadOnlyList<VisualSearchText>>(
            region == fields[2] ? [Content(region)] : [new("Search", new(region.X + 45, region.Y + 9, 40, 16))]));
        var error = await Assert.ThrowsAsync<DesktopSearchException>(() =>
            reader.FindUniqueSearchFieldAsync(GlyphImage(fields, "glass"), default));
        Assert.Equal(DesktopSearchErrorCodes.CandidateAmbiguous, error.Code);
    }

    private static VisualSearchText Content(VisualSearchRect field, int contentOffset = 37) =>
        new("0 demo", new(field.X + 8, field.Y + 8, contentOffset + 60 - 8, 18))
        {
            Words = [new("0", new(field.X + 8, field.Y + 8, 20, 18)),
                new("demo", new(field.X + contentOffset, field.Y + 9, 60, 16))]
        };

    private static byte[] GlyphImage(IReadOnlyList<VisualSearchRect> fields, string glyph, bool dark = false, int iconShift = 0)
    {
        using var bitmap = new Bitmap(500, 600);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.Clear(dark ? Color.FromArgb(25, 25, 25) : Color.FromArgb(225, 225, 225));
        using var background = new SolidBrush(dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(249, 249, 249));
        using var pen = new Pen(dark ? Color.FromArgb(195, 195, 195) : Color.FromArgb(115, 115, 115), 1.4f);
        foreach (var field in fields)
        {
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.FillRectangle(background, field.X, field.Y, field.Width, field.Height);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var cx = field.X + field.Height / 2f + iconShift;
            var cy = field.Y + field.Height / 2f - 2;
            if (glyph == "zero") graphics.DrawEllipse(pen, cx - 4, cy - 7, 8, 14);
            else graphics.DrawEllipse(pen, cx - 6, cy - 6, 12, 12);
            if (glyph == "glass") graphics.DrawLine(pen, cx + 4.2f, cy + 4.2f, cx + 9, cy + 9);
            if (glyph == "Q") graphics.DrawLine(pen, cx + 1, cy + 1, cx + 9, cy + 9);
            if (glyph == "wrong_handle") graphics.DrawLine(pen, cx - 4.2f, cy + 4.2f, cx - 9, cy + 9);
        }
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static VisualSearchImageReader Reader(
        Func<byte[], VisualSearchRect, CancellationToken, Task<IReadOnlyList<VisualSearchText>>> read) => new(read);

    private static byte[] Image(IReadOnlyList<VisualSearchRect> fields, bool dark = false, int dpi = 96)
    {
        using var bitmap = new Bitmap(500, 600);
        bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(dark ? Color.FromArgb(25, 25, 25) : Color.FromArgb(225, 225, 225));
        using var brush = new SolidBrush(dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(249, 249, 249));
        foreach (var field in fields) graphics.FillRectangle(brush, field.X, field.Y, field.Width, field.Height);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
}
