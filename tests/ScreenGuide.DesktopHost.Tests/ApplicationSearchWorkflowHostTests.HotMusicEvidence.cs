using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Tests;

public sealed partial class ApplicationSearchWorkflowHostTests
{
    [Theory]
    [InlineData(ApplicationSearchFollowUp.None)]
    [InlineData(ApplicationSearchFollowUp.OpenFirst)]
    [InlineData(ApplicationSearchFollowUp.PlayFirst)]
    public void HostOrdinarySearchRejectsUnexpectedLatestMusicEvidence(ApplicationSearchFollowUp followUp)
    {
        var workflow = new ApplicationSearchWorkflow("fixture-music", "合成曲", followUp);
        var invoked = followUp != ApplicationSearchFollowUp.None;
        var receipt = new ApplicationSearchWorkflowResult(true, true, true, false, invoked, invoked, "完成")
            { LatestMusic = HostLatestEvidence() };

        var error = Assert.Throws<ApplicationSearchWorkflowException>(() =>
            DesktopActionEntryService.RequireWorkflowEvidence(JsonSerializer.Serialize(receipt), workflow));

        Assert.Equal("application_search_verification_failed", error.Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("chart-id")]
    [InlineData("chart-name")]
    [InlineData("source")]
    [InlineData("song-zero")]
    [InlineData("song-overflow")]
    [InlineData("title-empty")]
    [InlineData("title-control")]
    [InlineData("title-format")]
    [InlineData("title-whitespace")]
    [InlineData("title-too-long")]
    [InlineData("artists-null")]
    [InlineData("artists-empty")]
    [InlineData("artists-too-many")]
    [InlineData("artist-null")]
    [InlineData("artist-zero")]
    [InlineData("artist-overflow")]
    [InlineData("artist-duplicate")]
    [InlineData("artist-name-empty")]
    [InlineData("artist-name-format")]
    [InlineData("artist-name-too-long")]
    [InlineData("query-other")]
    [InlineData("query-reversed")]
    [InlineData("query-too-long")]
    [InlineData("checked-default")]
    [InlineData("updated-default")]
    [InlineData("updated-future")]
    [InlineData("updated-expired")]
    [InlineData("unexpected-latest")]
    public void HostRejectsHotMusicReceiptWithUnboundMetadata(string change)
    {
        var song = HostEvidenceSong();
        var altered = change switch
        {
            "chart-id" => song with { ChartId = 1 },
            "chart-name" => song with { ChartName = "飙升榜" },
            "source" => song with { SourceUri = "https://example.invalid/" },
            "song-zero" => song with { SongId = 0 },
            "song-overflow" => song with { SongId = long.MaxValue },
            "title-empty" => song with { Title = "" },
            "title-control" => song with { Title = "合成\n曲" },
            "title-format" => song with { Title = "合成\u200b曲" },
            "title-whitespace" => song with { Title = " 合成曲 " },
            "title-too-long" => song with { Title = new('歌', 201) },
            "artists-null" => song with { Artists = null! },
            "artists-empty" => song with { Artists = [] },
            "artists-too-many" => song with { Artists = Enumerable.Range(1, 17).Select(i => new HotMusicArtist(i, "歌手" + i)).ToArray() },
            "artist-null" => song with { Artists = [null!] },
            "artist-zero" => song with { Artists = [new(0, "合成歌手")] },
            "artist-overflow" => song with { Artists = [new(long.MaxValue, "合成歌手")] },
            "artist-duplicate" => song with { Artists = [new(1001, "合成歌手"), new(1001, "另一歌手")] },
            "artist-name-empty" => song with { Artists = [new(1001, "")] },
            "artist-name-format" => song with { Artists = [new(1001, "合成\u200b歌手")] },
            "artist-name-too-long" => song with { Artists = [new(1001, new('人', 81))] },
            "query-other" => song with { SearchQuery = "另一首歌" },
            "query-reversed" => song with { SearchQuery = "合成曲 合成歌手" },
            "query-too-long" => song with { SearchQuery = new('歌', 201) },
            "checked-default" => song with { CheckedAtUtc = default },
            "updated-default" => song with { ChartUpdatedAtUtc = default },
            "updated-future" => song with { ChartUpdatedAtUtc = song.CheckedAtUtc.AddSeconds(1) },
            "updated-expired" => song with { ChartUpdatedAtUtc = song.CheckedAtUtc.AddDays(-8) },
            _ => song
        };
        var receipt = HostChartReceipt() with
        {
            HotMusic = change == "missing" ? null : altered,
            LatestMusic = change == "unexpected-latest" ? HostLatestEvidence() : null
        };
        var error = Assert.Throws<ApplicationSearchWorkflowException>(() =>
            DesktopActionEntryService.RequireWorkflowEvidence(JsonSerializer.Serialize(receipt), HostChartWorkflow()));
        Assert.Equal("application_search_verification_failed", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostRejectsHotMusicInOrdinaryOrLatestSearch(bool latest)
    {
        var workflow = new ApplicationSearchWorkflow("fixture-music", "合成曲",
            LatestMusicArtist: latest ? "合成歌手" : null);
        var receipt = HostChartReceipt() with
        {
            FollowUpInvoked = false, FollowUpVerified = false,
            LatestMusic = latest ? HostLatestEvidence() : null
        };
        Assert.Throws<ApplicationSearchWorkflowException>(() =>
            DesktopActionEntryService.RequireWorkflowEvidence(JsonSerializer.Serialize(receipt), workflow));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void HostAcceptsResolvedChartEvidenceWithoutExpiringHistoricalReceipts(bool play, bool empty)
    {
        var workflow = HostChartWorkflow() with { FollowUp = play ? ApplicationSearchFollowUp.PlayFirst : ApplicationSearchFollowUp.None };
        var receipt = HostChartReceipt() with { FollowUpInvoked = play, FollowUpVerified = play, NoResults = empty };
        var evidence = DesktopActionEntryService.RequireWorkflowEvidence(JsonSerializer.Serialize(receipt), workflow);
        Assert.Equal(receipt.HotMusic!.SearchQuery, evidence.HotMusic!.SearchQuery);
        Assert.Equal(receipt.HotMusic.SongId, evidence.HotMusic.SongId);
    }

    [Fact]
    public async Task HotChartGenericSuccessCannotCompleteTheConfirmedHostCommand()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingWorkflow { ResultOverride = HostChartReceipt() with { HotMusic = null } };
        using var host = environment.BuildHost(services => Register(services, new(), automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var plan = await client.PlanAssistantCommandAsync(new("打开网易云音乐并搜索最近最热的一首歌播放"));
        await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(new(plan.PlanId, true, "reject-chart-without-resolution")));
        Assert.Equal(ApplicationSearchWorkflow.NetEaseHotChart, Assert.Single(automation.Calls).MusicChart);
        await host.StopAsync();
    }

    [Fact]
    public async Task VoiceHotChartCompletesOneOriginalTurnWithOneBoundWorkflow()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingWorkflow { ResultOverride = HostChartReceipt() };
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IInstalledApplicationCatalog>(CreateRegistrationCatalog(environment.RootDirectory, "网易云音乐"));
            services.AddSingleton<IApplicationSearchWorkflowAutomation>(automation);
            services.AddSingleton<IDesktopProcessLauncher>(new RejectingLauncher());
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("一次语音完成热榜歌曲");
        var input = await client.SubmitSessionInputAsync(new(
            "桌面上的网易云然后搜索最近最热的一首歌播放", "Voice", "chart-single-turn", session.SessionId));
        var completed = await WaitForPhaseAsync(client, input.TurnId, "Completed");
        var turn = Assert.Single(completed.Turns);
        Assert.Equal(input.TurnId, turn.Id);
        Assert.Equal("ApplicationSearch", turn.IntentKind);
        var executed = Assert.Single(automation.Calls);
        Assert.Equal(ApplicationSearchWorkflow.NetEaseHotChart, executed.MusicChart);
        Assert.Equal(ApplicationSearchFollowUp.PlayFirst, executed.FollowUp);
        Assert.Equal(HostChartReceipt().Summary, turn.ResultSummary);
        await host.StopAsync();
    }

    private static ApplicationSearchWorkflow HostChartWorkflow() => new("fixture-music", "最近最热的一首歌",
        ApplicationSearchFollowUp.PlayFirst, MusicChart: ApplicationSearchWorkflow.NetEaseHotChart);
    private static ApplicationSearchWorkflowResult HostChartReceipt() =>
        new(true, true, true, false, true, true, "完成") { HotMusic = HostEvidenceSong() };
    private static LatestMusicResolution HostLatestEvidence() =>
        new("合成歌手", "合成曲", new(2026, 11, 1), ["合成曲"], "合成歌手 合成曲", true, ["https://music.apple.com/example"]);
    private static HotMusicResolution HostEvidenceSong() => new(
        3778678, "热歌榜", DateTimeOffset.Parse("2026-11-02T00:00:00Z"),
        DateTimeOffset.Parse("2026-11-03T00:00:00Z"), 123456789, "合成曲",
        [new(1001, "合成歌手")], "合成歌手 合成曲", "https://music.163.com/discover/toplist?id=3778678");
}
