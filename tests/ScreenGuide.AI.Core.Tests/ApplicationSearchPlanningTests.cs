using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class ApplicationSearchPlanningTests
{
    private readonly DeterministicIntentPlanner _planner = new(TimeProvider.System);

    [Theory]
    [InlineData("打开网易云，搜索最近周杰伦的新歌", "最近周杰伦的新歌", "周杰伦")]
    [InlineData("打开网易云,搜索最近周杰伦的新歌", "最近周杰伦的新歌", "周杰伦")]
    [InlineData("在网易云里搜周杰伦最近出的歌", "周杰伦最近出的歌", "周杰伦")]
    [InlineData("在网易云里搜周杰伦最近发布的新歌", "周杰伦最近发布的新歌", "周杰伦")]
    [InlineData("在网易云里搜周杰伦的新歌", "周杰伦的新歌", "周杰伦")]
    [InlineData("在网易云里搜最近发布的周杰伦的新歌", "最近发布的周杰伦的新歌", "周杰伦")]
    [InlineData("在网易云里搜 Taylor Swift 最近发行的单曲", "Taylor Swift 最近发行的单曲", "Taylor Swift")]
    [InlineData("就是请帮我打开网易云然后搜索最近周杰伦的新歌", "最近周杰伦的新歌", "周杰伦")]
    [InlineData("嗯，那个，请帮我打开网易云然后搜索周杰伦最近出的歌", "周杰伦最近出的歌", "周杰伦")]
    [InlineData("呃，在网易云里搜周杰伦的新歌", "周杰伦的新歌", "周杰伦")]
    [InlineData("在网易云里搜周杰伦刚刚发布的新歌", "周杰伦刚刚发布的新歌", "周杰伦")]
    public void RecentSongsPhrasesExecuteTheNamedApplicationAndRequestReleaseEvidence(
        string text, string query, string artist)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.ApplicationSearch, plan.Kind);
        Assert.Equal(IntentPlanReadiness.Ready, plan.Readiness);
        Assert.Equal("网易云", plan.ApplicationSearch!.ApplicationName);
        Assert.Equal(query, plan.ApplicationSearch.Query);
        Assert.Equal(artist, plan.ApplicationSearch.LatestMusicArtist);
        Assert.Contains("发行时间", plan.UserSummary);
    }

    [Theory]
    [InlineData("最近周杰伦的新歌")]
    [InlineData("周杰伦最近出的歌")]
    [InlineData("周杰伦最新的歌")]
    [InlineData("周杰伦的新歌")]
    public void BareMusicFragmentRequestsAnExplicitActionAndApplicationWithoutOrdinaryChat(string text)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.Unsupported, plan.Kind);
        Assert.Equal(IntentPlanReadiness.Unsupported, plan.Readiness);
        Assert.Null(plan.ApplicationSearch);
        Assert.Null(plan.Capability);
        Assert.False(plan.RequiresConfirmation);
        Assert.Contains("软件", plan.UserSummary);
        Assert.Contains("搜索", plan.UserSummary);
    }

    [Theory]
    [InlineData("我喜欢周杰伦的新歌")]
    [InlineData("你觉得周杰伦的新歌怎么样")]
    [InlineData("周杰伦最近出的歌叫什么")]
    [InlineData("周杰伦最近出的歌的歌词")]
    [InlineData("不是最近周杰伦的新歌")]
    [InlineData("不要打开网易云搜索最近周杰伦的新歌")]
    [InlineData("就是不要打开网易云搜索最近周杰伦的新歌")]
    [InlineData("那个如何打开网易云搜索最近周杰伦的新歌")]
    [InlineData("“最近周杰伦的新歌”")]
    public void MusicalDiscussionAndNegationNeverBecomeExecutionOrCommandClarification(string text)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.Conversation, plan.Kind);
        Assert.Null(plan.ApplicationSearch);
        Assert.False(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("呃我见到桌面上的网易云然后搜索")]
    [InlineData("呃我见到桌面上的网易云然后搜索最近周杰伦的新歌")]
    [InlineData("我看到电脑上的抖音，然后搜索美食")]
    public void MistakenObservationVerbBeforeSearchRequestsAnExplicitCommandWithoutAuthority(string text)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.Unsupported, plan.Kind);
        Assert.Null(plan.ApplicationSearch);
        Assert.Null(plan.Capability);
        Assert.False(plan.RequiresConfirmation);
        Assert.Contains("完整指令", plan.UserSummary);
    }

    [Fact]
    public void SpokenLeadInPreservesTrustedWholeApplicationResolution()
    {
        const string text = "那个请帮我打开我的微信搜索助手";
        Assert.True(DeterministicIntentPlanner.TryGetApplicationPreference(text, out var applicationName));
        Assert.Equal("我的微信搜索助手", applicationName);
        var plan = _planner.Plan(text, new IntentPlanningContext(PreferInstalledApplication: true));
        Assert.Equal(UniversalIntentKind.OpenApplication, plan.Kind);
        Assert.Equal("我的微信搜索助手", plan.Target);
        Assert.Null(plan.ApplicationSearch);
    }

    [Theory]
    [InlineData("周杰伦最新的歌", "周杰伦")]
    [InlineData("周杰伦的最新歌曲", "周杰伦")]
    [InlineData("林俊杰最新发布的单曲", "林俊杰")]
    [InlineData("Taylor Swift 最新发行的歌", "Taylor Swift")]
    [InlineData("陈奕迅最新发行的新歌", "陈奕迅")]
    [InlineData("和田光司最新歌曲", "和田光司")]
    [InlineData("Of Monsters and Men 最新发行的歌曲", "Of Monsters and Men")]
    public void LatestSongsRequestPreservesArtistAndRequestsReleaseDateEvidence(string query, string artist)
    {
        var plan = _planner.Plan("打开网易云并搜索" + query, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.ApplicationSearch, plan.Kind);
        Assert.Equal(query, plan.ApplicationSearch!.Query);
        Assert.Equal(artist, plan.ApplicationSearch.LatestMusicArtist);
        Assert.Contains("发行时间", plan.UserSummary);
    }

    [Theory]
    [InlineData("“周杰伦最新的歌”")]
    [InlineData("“最新的歌”")]
    [InlineData("\"Taylor Swift 最新发行的歌曲\"")]
    [InlineData("周杰伦最新的歌的歌词")]
    [InlineData("周杰伦最新的歌叫什么名字")]
    [InlineData("不是周杰伦最新的歌")]
    [InlineData("不要周杰伦最新的歌")]
    [InlineData("不是周杰伦和林俊杰最新的歌")]
    [InlineData("“他们最新的歌”")]
    [InlineData("“周杰伦和林俊杰最新的歌”")]
    [InlineData("歌名是他们最新的歌")]
    [InlineData("“最近周杰伦的新歌”")]
    [InlineData("\"周杰伦最近出的歌\"")]
    [InlineData("不是最近周杰伦的新歌")]
    [InlineData("最近不要周杰伦的新歌")]
    [InlineData("周杰伦最近出的歌的歌词")]
    [InlineData("周杰伦新歌叫什么名字")]
    [InlineData("歌名是最近周杰伦的新歌")]
    public void LiteralQueriesNeverInventAnArtistOrReleaseOrdering(string query)
    {
        var plan = _planner.Plan("打开网易云并搜索" + query, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.ApplicationSearch, plan.Kind);
        Assert.Null(plan.ApplicationSearch!.LatestMusicArtist);
        Assert.DoesNotContain("发行时间", plan.UserSummary);
    }

    [Theory]
    [InlineData("周杰伦和林俊杰最新的歌")]
    [InlineData("周杰伦、林俊杰最新发布的歌")]
    [InlineData("周杰伦,林俊杰最新发行的歌曲")]
    [InlineData("他们最新的歌")]
    [InlineData("这位歌手最新的歌曲")]
    [InlineData("谁最新的歌")]
    [InlineData("最近周杰伦和林俊杰的新歌")]
    [InlineData("周杰伦与林俊杰最近出的歌")]
    [InlineData("他们的新歌")]
    [InlineData("最近这位歌手的新歌")]
    public void AmbiguousLatestArtistRequestsOneSpecificArtistInsteadOfKeywordSearch(string query)
    {
        var plan = _planner.Plan("打开网易云并搜索" + query, new IntentPlanningContext());
        Assert.Equal(IntentPlanReadiness.Unsupported, plan.Readiness);
        Assert.Equal(UniversalIntentKind.Unsupported, plan.Kind);
        Assert.Null(plan.ApplicationSearch);
        Assert.Contains("具体、单一的歌手", plan.UserSummary);
    }

    [Theory]
    [InlineData("最新的歌")]
    [InlineData("最新歌曲")]
    [InlineData("最新发布的歌曲")]
    [InlineData("最新单曲")]
    [InlineData("最新发行的新歌")]
    [InlineData("最近的新歌")]
    [InlineData("最近出的歌")]
    [InlineData("新歌")]
    [InlineData("刚刚发布的新歌")]
    public void LatestMusicWithoutArtistRequestsTheMissingArtistInsteadOfSearchingTheWords(string query)
    {
        var plan = _planner.Plan("打开网易云并搜索" + query, new IntentPlanningContext());
        Assert.Equal(IntentPlanReadiness.Unsupported, plan.Readiness);
        Assert.Equal(UniversalIntentKind.Unsupported, plan.Kind);
        Assert.Null(plan.ApplicationSearch);
        Assert.Contains("补充歌手", plan.UserSummary);
    }

    [Fact]
    public void LatestMusicMeaningAndAnExplicitFollowUpAreBoundIndependently()
    {
        var plan = _planner.Plan("在网易云里搜索林俊杰最新的歌曲，然后播放第一首", new IntentPlanningContext());
        Assert.Equal(new ApplicationSearchRequest("网易云", "林俊杰最新的歌曲", "PlayFirst", "林俊杰"),
            plan.ApplicationSearch);
    }

    [Theory]
    [InlineData("桌面上的网易云然后搜索周杰伦最新的歌", "桌面上的网易云", "周杰伦最新的歌", "周杰伦")]
    [InlineData("桌面上的抖音，然后搜索美食", "桌面上的抖音", "美食", null)]
    [InlineData("我的 Blender 然后搜索材质", "我的 Blender", "材质", null)]
    [InlineData("给我给我打开桌面上的网易云，并搜索周杰伦最新的歌", "桌面上的网易云", "周杰伦最新的歌", "周杰伦")]
    [InlineData("打开桌面上的网易云，并搜索周杰伦最新的歌", "桌面上的网易云", "周杰伦最新的歌", "周杰伦")]
    [InlineData("打开我桌面上的抖音，然后搜索美食", "我桌面上的抖音", "美食", null)]
    public void SpokenApplicationThenSearchDoesNotFallThroughToOrdinaryChat(
        string text, string application, string query, string? artist)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.ApplicationSearch, plan.Kind);
        Assert.Equal(new ApplicationSearchRequest(application, query, LatestMusicArtist: artist), plan.ApplicationSearch);
        Assert.True(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("打开我桌面上的搜索计划.txt")]
    [InlineData("打开桌面的歌曲搜索清单")]
    [InlineData("打开文件搜索结果.txt")]
    [InlineData("打开桌面上的“笔记然后搜索说明.txt”")]
    [InlineData("打开桌面文件笔记然后搜索说明.txt")]
    [InlineData("打开桌面上的笔记.txt，然后搜索说明")]
    [InlineData("打开桌面上的网易云然后搜索清单.txt")]
    public void SearchWordInsideExplicitFileNameCannotBecomeAnApplicationWorkflow(string text)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.OpenFile, plan.Kind);
        Assert.Null(plan.ApplicationSearch);
    }

    [Theory]
    [InlineData("打开我的网易云，然后搜索周杰伦")]
    [InlineData("帮我打开我的抖音并搜索 AI 编程")]
    [InlineData("在我的网易云里搜索晴天")]
    public void ExplicitNamedSearchCreatesOneBoundWorkflow(string text)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal("ApplicationSearch", plan.Kind.ToString());
        Assert.Equal(IntentPlanReadiness.Ready, plan.Readiness);
        Assert.True(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("打开我的网易云，然后搜索周杰伦", "我的网易云", "周杰伦", "None")]
    [InlineData("打开网易云并搜索晴天，然后播放第一首", "网易云", "晴天", "PlayFirst")]
    [InlineData("帮我在抖音里面搜索 AI 编程并打开第一条", "抖音", "AI 编程", "OpenFirst")]
    [InlineData("在我的新星工具里搜索“打开文件，然后播放第一首”", "我的新星工具", "打开文件,然后播放第一首", "None")]
    [InlineData("打开我的网易云，然后搜索“晴天”，播放第一首", "我的网易云", "晴天", "PlayFirst")]
    [InlineData("在网易云里搜索“最近最热的歌”", "网易云", "最近最热的歌", "None")]
    [InlineData("打开桌面上的网易云，然后搜索清单.txt", "桌面上的网易云", "清单.txt", "None")]
    public void PreservesRequestAndNeverInventsAnUnrequestedFollowUp(string text, string application, string query, string action)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.ApplicationSearch, plan.Kind);
        Assert.Equal(new ApplicationSearchRequest(application, query, action), plan.ApplicationSearch);
    }

    [Theory]
    [InlineData("不要打开网易云然后搜索晴天")]
    [InlineData("你能在抖音里搜索吗")]
    [InlineData("为什么打开网易云搜索不到歌")]
    [InlineData("打开网易云并搜索晴天，然后发送给朋友")]
    [InlineData("打开网易云并搜索晴天，然后删除第一条")]
    [InlineData("打开网易云并搜索晴天，然后播放第二首")]
    [InlineData("打开网易云并搜索“晴天”然后打开微信")]
    [InlineData("打开C:\\bad.exe然后搜索天气")]
    [InlineData("打开网易云 --admin并搜索天气")]
    [InlineData("打开网易云并搜索晴天\r\n播放第一首")]
    [InlineData("打开网易云并搜索")]
    [InlineData("打开网易云并搜索“晴天")]
    [InlineData("不要把桌面上的网易云然后搜索周杰伦最新的歌")]
    [InlineData("如何实现桌面上的网易云然后搜索周杰伦最新的歌")]
    [InlineData("如果说桌面上的网易云然后搜索周杰伦最新的歌会怎样")]
    [InlineData("“桌面上的网易云然后搜索周杰伦最新的歌”")]
    [InlineData("桌面上的网易云然后搜索晴天，然后发送给朋友")]
    [InlineData("桌面上的网易云然后搜索晴天\n然后删除第一条")]
    public void AmbiguousUnsafeAndUnsupportedPhrasesGrantNoWorkflow(string text)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.NotEqual(UniversalIntentKind.ApplicationSearch, plan.Kind);
        Assert.Null(plan.ApplicationSearch);
        Assert.False(plan.Readiness == IntentPlanReadiness.Ready && plan.RequiresConfirmation);
    }

    [Fact]
    public void LongQueryIsRejected()
    {
        var plan = _planner.Plan("打开网易云并搜索" + new string('歌', 201), new IntentPlanningContext());
        Assert.Equal(IntentPlanReadiness.Unsupported, plan.Readiness);
    }

    [Theory]
    [InlineData("桌面上的网易云然后搜索晴\n天")]
    [InlineData("我的抖音然后搜索美\t食")]
    public void OmittedOpenVerbStillRejectsControlCharactersBeforeWhitespaceNormalization(string text)
    {
        Assert.Equal(IntentPlanReadiness.Unsupported,
            _planner.Plan(text, new IntentPlanningContext()).Readiness);
    }

    [Theory]
    [InlineData(UniversalIntentKind.CodingTask)]
    [InlineData(UniversalIntentKind.OpenFile)]
    [InlineData(UniversalIntentKind.DescribeForeground)]
    public void ExplicitEntryIsNotReplacedByWordsInsideItsTask(UniversalIntentKind explicitIntent)
    {
        var plan = _planner.Plan("打开网易云并搜索晴天", new IntentPlanningContext(ExplicitUserIntent: explicitIntent));
        Assert.Equal(explicitIntent, plan.Kind);
        Assert.Null(plan.ApplicationSearch);
    }

    [Fact]
    public void TrustedWholeApplicationResolutionWinsOverSearchWordsInItsName()
    {
        var plan = _planner.Plan("打开我的微信搜索助手", new IntentPlanningContext(PreferInstalledApplication: true));
        Assert.Equal(UniversalIntentKind.OpenApplication, plan.Kind);
        Assert.Equal("我的微信搜索助手", plan.Target);
        Assert.Null(plan.ApplicationSearch);
    }

    [Theory]
    [InlineData("打开搜索助手", "搜索助手")]
    [InlineData("打开搜狗输入法", "搜狗输入法")]
    public void SearchWordAtStartOfApplicationNameRemainsAnApplication(string text, string name)
    {
        var plan = _planner.Plan(text, new IntentPlanningContext());
        Assert.Equal(UniversalIntentKind.OpenApplication, plan.Kind);
        Assert.Equal(name, plan.Target);
    }
}
