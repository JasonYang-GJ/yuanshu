namespace ScreenGuide.Vision.Windows.Tests;

public sealed class NativeSearchCaretTests
{
    private static readonly VisualSearchWindowBounds Window = new(100, 200, 640, 400);
    private static readonly VisualSearchRect Field = new(30, 30, 200, 30);

    [Theory]
    [InlineData(180, 240, 182, 242)]
    [InlineData(180, 236, 181, 254)]
    public void VisibleNativeCaretMayBeOnlyTwoPixelsHigh(int left, int top, int right, int bottom) =>
        Assert.True(Inside(left, top, right, bottom));

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(4u)]
    public void MissingVisibleCaretFlagCannotAuthorizeInput(uint flags) =>
        Assert.False(Inside(180, 240, 182, 242, flags));

    [Theory]
    [InlineData(0, 42, 42)]
    [InlineData(42, 0, 42)]
    [InlineData(42, 42, 0)]
    [InlineData(42, 43, 42)]
    [InlineData(42, 42, 43)]
    [InlineData(42, 43, 43)]
    public void CaretAndFocusMustBothBelongToExactNonzeroWindow(long window, long caretRoot, long focusRoot) =>
        Assert.False(WindowsVisualSearchInputDriver.IsCaretInsideSearchField(1, window, caretRoot, focusRoot,
            Window, Field, 180, 240, 182, 242));

    [Theory]
    [InlineData(180, 240, 180, 242)]
    [InlineData(180, 240, 182, 240)]
    [InlineData(182, 240, 180, 242)]
    [InlineData(180, 242, 182, 240)]
    [InlineData(130, 240, 132, 242)]
    [InlineData(180, 230, 182, 232)]
    [InlineData(328, 240, 330, 242)]
    [InlineData(180, 258, 182, 260)]
    [InlineData(10, 240, 12, 242)]
    [InlineData(180, 300, 182, 302)]
    public void EmptyReversedTouchingOrOutsideCaretIsRejected(int left, int top, int right, int bottom) =>
        Assert.False(Inside(left, top, right, bottom));

    [Theory]
    [InlineData(-1, 30, 200, 30)]
    [InlineData(30, -1, 200, 30)]
    [InlineData(30, 30, 0, 30)]
    [InlineData(30, 30, 200, 0)]
    [InlineData(600, 30, 200, 30)]
    [InlineData(30, 390, 200, 30)]
    [InlineData(int.MaxValue, 30, int.MaxValue, 30)]
    public void FieldMustBeInsideWindowWithoutOverflow(int x, int y, int width, int height) =>
        Assert.False(WindowsVisualSearchInputDriver.IsCaretInsideSearchField(1, 42, 42, 42,
            Window, new(x, y, width, height), 180, 240, 182, 242));

    [Fact]
    public void NegativeMonitorCoordinatesRemainValid()
    {
        var window = new VisualSearchWindowBounds(-1920, -200, 640, 400);
        Assert.True(WindowsVisualSearchInputDriver.IsCaretInsideSearchField(1, 42, 42, 42,
            window, Field, -1840, -160, -1838, -158));
    }

    [Fact]
    public void ClientCoordinatesAreNotMistakenForPhysicalScreenCoordinates() =>
        Assert.False(Inside(80, 40, 82, 42));

    [Theory]
    [InlineData(0, 400)]
    [InlineData(640, 0)]
    [InlineData(-640, 400)]
    public void InvalidWindowBoundsAreRejected(int width, int height) =>
        Assert.False(WindowsVisualSearchInputDriver.IsCaretInsideSearchField(1, 42, 42, 42,
            Window with { Width = width, Height = height }, Field, 180, 240, 182, 242));

    [Fact]
    public void ScreenOriginArithmeticDoesNotWrapIntoAnotherMonitor() =>
        Assert.False(WindowsVisualSearchInputDriver.IsCaretInsideSearchField(1, 42, 42, 42,
            Window with { X = int.MaxValue - 10 }, Field, int.MinValue + 80, 240, int.MinValue + 82, 242));

    private static bool Inside(int left, int top, int right, int bottom, uint flags = 1) =>
        WindowsVisualSearchInputDriver.IsCaretInsideSearchField(flags, 42, 42, 42,
            Window, Field, left, top, right, bottom);
}
