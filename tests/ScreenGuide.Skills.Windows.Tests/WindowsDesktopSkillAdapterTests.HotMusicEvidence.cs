using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed partial class WindowsDesktopSkillAdapterTests
{
    [Theory]
    [InlineData(ApplicationSearchFollowUp.None)]
    [InlineData(ApplicationSearchFollowUp.OpenFirst)]
    [InlineData(ApplicationSearchFollowUp.PlayFirst)]
    public async Task OrdinarySearchRejectsUnexpectedLatestMusicEvidence(ApplicationSearchFollowUp followUp)
    {
        var catalog = new FakeCatalog();
        var app = catalog.FindById("test-app")!;
        var workflow = new ApplicationSearchWorkflow(app.Id, "合成曲", followUp);
        var invoked = followUp != ApplicationSearchFollowUp.None;
        var execution = new RecordingWorkflow
        {
            Result = new(true, true, true, false, invoked, invoked, "完成")
            {
                LatestMusic = new("合成歌手", "合成曲", new(2026, 11, 1), ["合成曲"],
                    "合成歌手 合成曲", true, ["https://music.apple.com/example"])
            }
        };
        var adapter = new WindowsDesktopSkillAdapter(new RecordingLauncher(), catalog, new RecordingAutomation(), execution);

        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => adapter.StartAsync(Request(
            WindowsDesktopCapabilities.ApplicationSearchWorkflow,
            new("SearchApplication", app.Id, Argument: workflow.Query,
                ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(app), ApplicationSearch: workflow))));

        Assert.Equal("application_search_incomplete", error.Code);
        Assert.Equal(1, execution.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("chart")]
    [InlineData("source")]
    [InlineData("song")]
    [InlineData("artist")]
    [InlineData("query")]
    [InlineData("ordinary")]
    [InlineData("latest")]
    public async Task HotMusicEvidenceCannotBeReplacedByGenericSuccessFlags(string change)
    {
        var catalog = new FakeCatalog();
        var app = catalog.FindById("test-app")!;
        var chart = change is not ("ordinary" or "latest");
        var workflow = new ApplicationSearchWorkflow(app.Id, "最近最热的一首歌",
            ApplicationSearchFollowUp.PlayFirst,
            LatestMusicArtist: change == "latest" ? "合成歌手" : null,
            MusicChart: chart ? ApplicationSearchWorkflow.NetEaseHotChart : null);
        var song = EvidenceSong();
        var evidence = new ApplicationSearchWorkflowResult(true, true, true, false, true, true, "完成")
        {
            HotMusic = change switch
            {
                "missing" => null,
                "chart" => song with { ChartId = 1 },
                "source" => song with { SourceUri = "https://example.invalid/chart" },
                "song" => song with { SongId = 0 },
                "artist" => song with { Artists = [] },
                "query" => song with { SearchQuery = "另一首歌" },
                _ => song
            },
            LatestMusic = change == "latest"
                ? new("合成歌手", "合成曲", new(2026, 11, 1), ["合成曲"], "合成歌手 合成曲", true, ["https://music.apple.com/example"])
                : null
        };
        var execution = new RecordingWorkflow { Result = evidence };
        var adapter = new WindowsDesktopSkillAdapter(new RecordingLauncher(), catalog, new RecordingAutomation(), execution);
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => adapter.StartAsync(Request(
            WindowsDesktopCapabilities.ApplicationSearchWorkflow,
            new("SearchApplication", app.Id, Argument: workflow.Query,
                ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(app), ApplicationSearch: workflow))));
        Assert.Equal("application_search_incomplete", error.Code);
        Assert.Equal(1, execution.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidHotMusicEvidenceRetainsTheResolvedIdentity(bool play)
    {
        var catalog = new FakeCatalog();
        var app = catalog.FindById("test-app")!;
        var workflow = new ApplicationSearchWorkflow(app.Id, "最近最热的一首歌",
            play ? ApplicationSearchFollowUp.PlayFirst : ApplicationSearchFollowUp.None,
            MusicChart: ApplicationSearchWorkflow.NetEaseHotChart);
        var song = EvidenceSong();
        var execution = new RecordingWorkflow { Result = new(true, true, true, false, play, play, "完成") { HotMusic = song } };
        var adapter = new WindowsDesktopSkillAdapter(new RecordingLauncher(), catalog, new RecordingAutomation(), execution);
        var start = await adapter.StartAsync(Request(WindowsDesktopCapabilities.ApplicationSearchWorkflow,
            new("SearchApplication", app.Id, Argument: workflow.Query,
                ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(app), ApplicationSearch: workflow)));
        var final = await adapter.GetFinalResultAsync(start.Run);
        Assert.True(final!.Succeeded);
        var receipt = System.Text.Json.JsonSerializer.Deserialize<ApplicationSearchWorkflowResult>(final.DataJson!);
        Assert.Equal(song.SongId, receipt!.HotMusic!.SongId);
        Assert.Equal(song.SearchQuery, receipt.HotMusic.SearchQuery);
        Assert.Equal(1, execution.Calls);
    }

    private static HotMusicResolution EvidenceSong() => new(
        3778678, "热歌榜", DateTimeOffset.Parse("2026-11-02T00:00:00Z"),
        DateTimeOffset.Parse("2026-11-03T00:00:00Z"), 123456789, "合成曲",
        [new(1001, "合成歌手")], "合成歌手 合成曲", "https://music.163.com/discover/toplist?id=3778678");
}
