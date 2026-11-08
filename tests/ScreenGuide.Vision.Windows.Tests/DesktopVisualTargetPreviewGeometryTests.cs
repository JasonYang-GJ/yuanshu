using ScreenGuide.Vision.Windows;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class DesktopVisualTargetPreviewGeometryTests
{
    [Theory]
    [InlineData(0, 0, 650, 650, 0, 0, 1920, 1040, true)]
    [InlineData(0, 0, 1920, 1040, 0, 0, 1920, 1040, true)]
    [InlineData(-1920, -400, -1270, 250, -1920, -400, 0, 640, true)]
    [InlineData(-1, 20, 649, 670, 0, 0, 1920, 1040, false)]
    [InlineData(20, -1, 670, 649, 0, 0, 1920, 1040, false)]
    [InlineData(1271, 20, 1921, 670, 0, 0, 1920, 1040, false)]
    [InlineData(20, 391, 670, 1041, 0, 0, 1920, 1040, false)]
    [InlineData(2000, 20, 2650, 670, 0, 0, 1920, 1040, false)]
    [InlineData(0, 0, 0, 650, 0, 0, 1920, 1040, false)]
    [InlineData(0, 0, 650, 0, 0, 0, 1920, 1040, false)]
    [InlineData(10, 10, 9, 650, 0, 0, 1920, 1040, false)]
    [InlineData(10, 10, 650, 9, 0, 0, 1920, 1040, false)]
    [InlineData(0, 0, 650, 650, 0, 0, 0, 1040, false)]
    [InlineData(0, 0, 650, 650, 0, 0, 1920, 0, false)]
    [InlineData(0, 0, 650, 650, 1920, 0, 0, 1040, false)]
    [InlineData(0, 0, 650, 650, 0, 1040, 1920, 0, false)]
    [InlineData(int.MinValue, 0, int.MinValue + 650, 650, int.MinValue, 0, int.MinValue + 1920, 1040, true)]
    [InlineData(int.MaxValue - 650, 0, int.MaxValue, 650, int.MaxValue - 1920, 0, int.MaxValue, 1040, true)]
    public void RequiresCompleteRectangleInsideOneNonemptyWorkArea(int left, int top, int right, int bottom,
        int workLeft, int workTop, int workRight, int workBottom, bool expected) =>
        Assert.Equal(expected, WindowsDesktopVisualTargetPreview.IsInsideVisibleWorkArea(
            left, top, right, bottom, workLeft, workTop, workRight, workBottom));

    [Fact]
    public void AWindowAcrossAdjacentMonitorsIsNotVisibleOnEitherSingleMonitor()
    {
        Assert.False(WindowsDesktopVisualTargetPreview.IsInsideVisibleWorkArea(1700, 100, 2350, 750, 0, 0, 1920, 1040));
        Assert.False(WindowsDesktopVisualTargetPreview.IsInsideVisibleWorkArea(1700, 100, 2350, 750, 1920, 0, 3840, 1040));
    }

    [Fact]
    public void NewlyReducedWorkAreaAndRemovedDisplayInvalidateEarlierVisibleGeometry()
    {
        Assert.True(WindowsDesktopVisualTargetPreview.IsInsideVisibleWorkArea(20, 20, 670, 670, 0, 0, 1920, 1040));
        Assert.False(WindowsDesktopVisualTargetPreview.IsInsideVisibleWorkArea(20, 20, 670, 670, 0, 0, 1920, 600));
        Assert.False(WindowsDesktopVisualTargetPreview.IsInsideVisibleWorkArea(20, 20, 670, 670, 0, 0, 0, 0));
    }
}
