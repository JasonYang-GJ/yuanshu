using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class SearchFieldLanguageTests
{
    private readonly DeterministicIntentPlanner _planner = new(TimeProvider.System);
    private static readonly IntentPlanningContext Foreground = new(
        ForegroundApplication: new(42, "测试窗口", "fixture"));

    [Theory]
    [InlineData("在星河聊天搜索框搜索资料助手", "星河聊天", "资料助手")]
    [InlineData("在星河聊天的搜索框里搜索资料助手", "星河聊天", "资料助手")]
    [InlineData("帮我在蓝海工具的搜索栏里查找天气", "蓝海工具", "天气")]
    [InlineData("在Nova Desk的输入框中搜索版本说明", "Nova Desk", "版本说明")]
    [InlineData("在星河聊天里搜索资料助手", "星河聊天", "资料助手")]
    [InlineData("在星河聊天的搜索框里搜索“发送文件，然后删除副本”", "星河聊天", "发送文件,然后删除副本")]
    [InlineData("在“星河搜索框”里搜索资料助手", "星河搜索框", "资料助手")]
    public void NamedSearchFieldKeepsTheNamedApplicationWorkflow(string text, string application, string query)
    {
        var plan = _planner.Plan(text, Foreground);

        Assert.Equal(UniversalIntentKind.ApplicationSearch, plan.Kind);
        Assert.Equal(IntentPlanReadiness.Ready, plan.Readiness);
        Assert.Equal(new ApplicationSearchRequest(application, query), plan.ApplicationSearch);
        Assert.True(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("在星河搜索框里搜索资料助手", "星河搜索框", "资料助手")]
    [InlineData("在Nova输入框中搜索版本说明", "Nova输入框", "版本说明")]
    [InlineData("在星河搜索栏里面搜索资料助手", "星河搜索栏", "资料助手")]
    [InlineData("在星河地址栏内搜索资料助手", "星河地址栏", "资料助手")]
    [InlineData("在新星文字框里查找版本说明", "新星文字框", "版本说明")]
    public void UnquotedCompleteApplicationNamesKeepTheirFieldLikeSuffix(string text, string application, string query)
    {
        var plan = _planner.Plan(text, Foreground);

        Assert.Equal(UniversalIntentKind.ApplicationSearch, plan.Kind);
        Assert.Equal(new ApplicationSearchRequest(application, query), plan.ApplicationSearch);
        Assert.True(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("在当前窗口的搜索框搜索资料助手", "资料助手")]
    [InlineData("在当前窗口的搜索框里搜索资料助手", "资料助手")]
    [InlineData("在搜索框搜索资料助手", "资料助手")]
    [InlineData("在这个应用的输入框里查找版本说明", "版本说明")]
    [InlineData("在当前窗口搜索资料助手", "资料助手")]
    [InlineData("在里面搜索资料助手", "资料助手")]
    [InlineData("请在这个软件里面帮我搜一下版本说明", "版本说明")]
    [InlineData("在当前窗口的搜索框搜索“修改代码”", "修改代码")]
    [InlineData("在当前窗口搜索“打开文件，然后删除副本”", "打开文件,然后删除副本")]
    [InlineData("在里面搜索\"打开文件，然后删除副本\"", "打开文件,然后删除副本")]
    [InlineData("在搜索框搜索「打开文件，然后删除副本」", "打开文件,然后删除副本")]
    [InlineData("搜索框架设计", "框架设计")]
    [InlineData("在当前窗口搜索栏杆设计", "栏杆设计")]
    [InlineData("在当前窗口搜索框架设计", "框架设计")]
    [InlineData("在当前窗口的搜索框搜索框架设计", "框架设计")]
    public void ContextualSearchFieldKeepsTheExactQueryAndForegroundIdentity(string text, string query)
    {
        var plan = _planner.Plan(text, Foreground);

        Assert.Equal(UniversalIntentKind.SearchForeground, plan.Kind);
        Assert.Equal(IntentPlanReadiness.Ready, plan.Readiness);
        Assert.Equal(query, plan.Target);
        Assert.Null(plan.ApplicationSearch);
        Assert.True(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("在当前窗口的搜索框搜索资料助手然后发送消息")]
    [InlineData("在里面搜索资料助手，然后删除文件")]
    [InlineData("在搜索框搜索资料助手并打开第一条")]
    [InlineData("在当前窗口搜索“资料助手”然后发送消息")]
    [InlineData("在当前窗口搜索“资料助手")]
    [InlineData("在星河聊天的搜索框搜索资料助手然后发送消息")]
    [InlineData("在星河聊天搜索框里搜索“资料助手”然后删除记录")]
    public void UnquotedExtraActionsAndBrokenQuotesNeverProduceASearchAction(string text)
    {
        var plan = _planner.Plan(text, Foreground);

        Assert.Equal(IntentPlanReadiness.Unsupported, plan.Readiness);
        Assert.Null(plan.ApplicationSearch);
        Assert.False(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("不要在星河聊天的搜索框搜索资料助手")]
    [InlineData("如何在当前窗口的搜索框搜索资料助手")]
    [InlineData("你能在星河聊天搜索框搜索资料助手吗")]
    [InlineData("“在当前窗口的搜索框搜索资料助手”")]
    [InlineData("在当前窗口的搜索框")]
    public void NegationDiscussionAndQuotedInstructionsNeverAuthorizeSearch(string text)
    {
        var plan = _planner.Plan(text, Foreground);

        Assert.False(plan.Readiness == IntentPlanReadiness.Ready && plan.RequiresConfirmation);
        Assert.Null(plan.ApplicationSearch);
    }

    [Theory]
    [InlineData("在当前窗口的搜索框搜索资料助手")]
    [InlineData("在搜索框搜索资料助手")]
    [InlineData("在里面搜索资料助手")]
    public void ContextualSearchStillNeedsATrustedForegroundWindow(string text)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());

        Assert.Equal(UniversalIntentKind.SearchForeground, plan.Kind);
        Assert.Equal(IntentPlanReadiness.NeedsContext, plan.Readiness);
        Assert.Equal("前台应用", plan.MissingContext);
    }

    [Theory]
    [InlineData("在当前窗口的搜索框搜索")]
    [InlineData("在星河聊天的搜索框搜索")]
    [InlineData("在当前窗口的搜索框搜索资料\n助手")]
    [InlineData("在星河聊天的搜索框搜索资料\t助手")]
    public void EmptyOrControlBearingSearchIsRejected(string text)
    {
        Assert.Equal(IntentPlanReadiness.Unsupported, _planner.Plan(text, Foreground).Readiness);
    }
}
