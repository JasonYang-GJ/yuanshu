using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class SpokenApplicationCorrectionTests
{
    private readonly DeterministicIntentPlanner _planner = new(TimeProvider.System);

    [Theory]
    [InlineData("我说的是打开Steam", "Steam")]
    [InlineData("我是说，打开Steam", "Steam")]
    [InlineData("我的意思是，请帮我打开星光播放器", "星光播放器")]
    [InlineData("不对，我说的是打开Steam", "Steam")]
    [InlineData("我说的是打开我桌面上的Steam", "我桌面上的Steam")]
    public void CorrectionWithExplicitActionKeepsApplicationPlanning(string text, string target)
    {
        var plan = _planner.Plan(text, new(PreferInstalledApplication: true));
        Assert.Equal(UniversalIntentKind.OpenApplication, plan.Kind);
        Assert.Equal(target, plan.Target);
        Assert.True(plan.RequiresConfirmation);
        Assert.True(DeterministicIntentPlanner.TryGetApplicationPreference(text, out var preference));
        Assert.Equal(target, preference);
    }

    [Theory]
    [InlineData("我说的是Steam")]
    [InlineData("我说的是不要打开Steam")]
    [InlineData("我的意思是如果打开Steam会怎么样")]
    [InlineData("我是说“打开Steam”是什么意思")]
    [InlineData("我没有说打开Steam")]
    public void CorrectionWithoutExplicitActionNeverGrantsApplicationAuthority(string text)
    {
        var plan = _planner.Plan(text, new());
        Assert.Equal(UniversalIntentKind.Conversation, plan.Kind);
        Assert.False(plan.RequiresConfirmation);
        Assert.False(DeterministicIntentPlanner.TryGetApplicationPreference(text, out _));
    }

    [Theory]
    [InlineData("我说的是打开我桌面上的报告.txt")]
    [InlineData("我的意思是打开桌面上的“报告.txt”")]
    public void CorrectedDesktopDocumentKeepsFileDiscovery(string text)
    {
        Assert.True(DesktopFileRequest.TryParse(text, out var request));
        Assert.Equal("报告.txt", request!.Name);
        Assert.Equal(UniversalIntentKind.OpenFile, _planner.Plan(text, new()).Kind);
    }
}
