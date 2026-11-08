using ScreenGuide.Vision.Windows;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class VisualSearchInputReadbackTests
{
    private static readonly VisualSearchRect Field = new(100, 50, 200, 30);

    [Theory]
    [InlineData("0", "0", true)]
    [InlineData("Q", "×", true)]
    [InlineData("〇", "x", true)]
    [InlineData("1", "0", false)]
    [InlineData("0", "1", false)]
    public void RemovesOnlyObservedEdgeIcons(string left, string right, bool expected)
    {
        var text = new VisualSearchText(left + "示例词" + right, Field)
        { Words = [new(left, new(108, 57, 14, 16)), new("示例词", new(138, 57, 48, 16)), new(right, new(278, 57, 14, 16))] };
        Assert.Equal(expected, WindowsVisualSearchAutomation.MatchesInput(text, "示例词", Field));
    }

    [Theory]
    [InlineData(125, 14)]
    [InlineData(140, 10)]
    [InlineData(98, 14)]
    public void AZeroInTheTextOrOutsideFieldIsNotAnIcon(int x, int width)
    {
        var text = new VisualSearchText("0示例词", Field)
        { Words = [new("0", new(x, 57, width, 16)), new("示例词", new(150, 57, 48, 16))] };
        Assert.False(WindowsVisualSearchAutomation.MatchesInput(text, "示例词", Field));
    }

    [Fact]
    public void QuerySuffixOrPrefixIsNeverRemovedWithoutWordGeometry()
    {
        Assert.False(WindowsVisualSearchAutomation.MatchesInput(new("0示例词0", Field), "示例词", Field));
        Assert.False(WindowsVisualSearchAutomation.MatchesInput(new("示例词副本", Field), "示例词", Field));
        Assert.True(WindowsVisualSearchAutomation.MatchesInput(new("0示例词0", Field), "0示例词0", Field));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("Q")]
    [InlineData("〇")]
    public void AdjacentLeadingCharacterIsNotAnIcon(string character)
    {
        var text = new VisualSearchText(character + "示例词", Field)
        { Words = [new(character, new(108, 57, 8, 16)), new("示例词", new(117, 57, 48, 16))] };
        Assert.False(WindowsVisualSearchAutomation.MatchesInput(text, "示例词", Field));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("×")]
    [InlineData("x")]
    public void AdjacentTrailingCharacterIsNotAnIcon(string character)
    {
        var text = new VisualSearchText("示例词" + character, Field)
        { Words = [new("示例词", new(229, 57, 48, 16)), new(character, new(278, 57, 8, 16))] };
        Assert.False(WindowsVisualSearchAutomation.MatchesInput(text, "示例词", Field));
    }

    [Theory]
    [InlineData(35)]
    [InlineData(75)]
    public void AWordOutsideTheInputRowCannotCompleteTheQuery(int y)
    {
        var text = new VisualSearchText("0示例词", Field)
        { Words = [new("0", new(108, 57, 8, 16)), new("示例词", new(140, y, 48, 16))] };
        Assert.False(WindowsVisualSearchAutomation.MatchesInput(text, "示例词", Field));
    }
}
