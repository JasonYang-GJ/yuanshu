using System.Text.Json;
using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class HotMusicPlanningTests
{
    [Theory]
    [InlineData("桌面上的网易云然后搜索最近最热的一首歌播放", "最近最热的一首歌")]
    [InlineData("打开网易云并搜索一首热门歌曲并播放", "一首热门歌曲")]
    [InlineData("在网易云里找一首热门歌曲并播放", "一首热门歌曲")]
    [InlineData("打开网易云然后找一首热门歌曲并播放", "一首热门歌曲")]
    [InlineData("桌面上的网易云然后找最近最热的一首歌播放", "最近最热的一首歌")]
    [InlineData("在网易云里搜索最近最热的歌，然后播放第一首", "最近最热的歌")]
    public void ExplicitHotSongPlaybackBindsChartAndOneFollowUp(string text, string query)
    {
        var plan = new DeterministicIntentPlanner(TimeProvider.System).Plan(text, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.ApplicationSearch, plan.Kind);
        Assert.Equal(IntentPlanReadiness.Ready, plan.Readiness);
        Assert.Equal(query, plan.ApplicationSearch!.Query);
        Assert.Equal("PlayFirst", plan.ApplicationSearch.FollowUp);
        Assert.Null(plan.ApplicationSearch.LatestMusicArtist);
        var request = JsonSerializer.SerializeToElement(plan.ApplicationSearch);
        Assert.Equal("NetEaseHotChart", request.GetProperty("MusicChart").GetString());
        Assert.Contains("网易云热歌榜", plan.UserSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("在网易云里搜索最近最热的歌")]
    [InlineData("在网易云里找一首热门歌曲")]
    public void ChartSearchDoesNotInventPlayback(string text)
    {
        var plan = new DeterministicIntentPlanner(TimeProvider.System).Plan(text, new IntentPlanningContext());
        Assert.Equal("NetEaseHotChart", plan.ApplicationSearch!.MusicChart);
        Assert.Equal("None", plan.ApplicationSearch.FollowUp);
    }

    [Theory]
    [InlineData("在网易云里搜索“最近最热的一首歌播放”", "最近最热的一首歌播放", "None")]
    [InlineData("在网易云里搜索“热门歌曲”，然后播放第一首", "热门歌曲", "PlayFirst")]
    public void QuotedWordsRemainLiteralWithoutChartAuthority(string text, string query, string followUp)
    {
        var plan = new DeterministicIntentPlanner(TimeProvider.System).Plan(text, new IntentPlanningContext());
        Assert.Equal(query, plan.ApplicationSearch!.Query);
        Assert.Equal(followUp, plan.ApplicationSearch.FollowUp);
        Assert.Null(plan.ApplicationSearch.MusicChart);
    }

    [Theory]
    [InlineData("不要打开网易云并搜索一首热门歌曲并播放")]
    [InlineData("为什么在网易云里找一首热门歌曲并播放")]
    [InlineData("“桌面上的网易云然后搜索最近最热的一首歌播放”")]
    [InlineData("在网易云里找一首热门歌曲并播放，然后发送给朋友")]
    [InlineData("在网易云里找一首热门歌曲并播放，然后删除")]
    [InlineData("在网易云里搜索最近最热的一首歌播放第二首")]
    [InlineData("在网易云里搜索热门歌曲，然后播放第二首")]
    [InlineData("在网易云里搜索热门歌曲，然后打开第一条")]
    [InlineData("在网易云里找一首热门歌曲并播放\n打开微信")]
    [InlineData("在网易云里找周杰伦")]
    public void AmbiguousOrExpandedRequestsNeverAuthorizeAChartWorkflow(string text)
    {
        var plan = new DeterministicIntentPlanner(TimeProvider.System).Plan(text, new IntentPlanningContext());
        Assert.Null(plan.ApplicationSearch);
        Assert.False(plan.Readiness == IntentPlanReadiness.Ready && plan.RequiresConfirmation);
    }
}
