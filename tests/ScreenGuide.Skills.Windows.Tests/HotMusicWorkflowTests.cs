using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class HotMusicWorkflowTests
{
    private static readonly KnownDesktopApplication App = new("fixture-app", "合成播放器", Environment.ProcessPath!);
    private static readonly ForegroundWindowSnapshot Window = new(42, "合成播放器", "cloudmusic", 123,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
    private static readonly HotMusicResolution Song = new(3778678, "热歌榜", DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, 123456, "合成歌曲", [new(987, "合成歌手")], "合成歌手 合成歌曲", HotMusicResolver.ChartSourceUri);

    [Theory]
    [InlineData(ApplicationSearchFollowUp.None)]
    [InlineData(ApplicationSearchFollowUp.PlayFirst)]
    public async Task ChartSelectionFlowsThroughExactSearchAndSingleSongPlayback(ApplicationSearchFollowUp followUp)
    {
        var setup = new Fixture();
        var result = await setup.Execute(followUp);
        Assert.True(result.ApplicationVerified && result.QuerySubmitted && result.ResultsVerified);
        Assert.Equal(Song, result.HotMusic);
        Assert.Equal(Song.SearchQuery, setup.Driver.SubmittedQuery);
        Assert.Equal(1, setup.Driver.Searches);
        Assert.Equal(followUp == ApplicationSearchFollowUp.PlayFirst ? 1 : 0, setup.Player.Calls);
        Assert.Equal(followUp == ApplicationSearchFollowUp.PlayFirst, result.FollowUpVerified);
        Assert.Equal([ApplicationSearchStage.Opening, ApplicationSearchStage.Opened,
            ApplicationSearchStage.ResolvingHotMusic, ApplicationSearchStage.Searching, ApplicationSearchStage.VerifyingResults],
            setup.Progress.Stages.Take(5));
        Assert.DoesNotContain(ApplicationSearchStage.ResolvingMusic, setup.Progress.Stages);
        if (followUp == ApplicationSearchFollowUp.PlayFirst)
            Assert.Equal(ApplicationSearchStage.PlayingMusic, setup.Progress.Stages[^1]);
    }

    [Fact]
    public async Task UnmatchedResultCannotPlayEvenWhenPublicChartProvidedASongId()
    {
        var setup = new Fixture();
        setup.Driver.ResultTitle = "另一首歌";
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => setup.Execute());
        Assert.Equal("hot_music_result_unverified", error.Code);
        Assert.Equal(0, setup.Player.Calls);
    }

    [Fact]
    public async Task ChangedRegisteredTargetAfterSearchPreventsPlayback()
    {
        var setup = new Fixture();
        setup.Driver.AfterRead = () => setup.Catalog.Current = App with { Id = "different-app" };
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => setup.Execute());
        Assert.Equal("application_search_target_changed", error.Code);
        Assert.Equal(0, setup.Player.Calls);
    }

    [Fact]
    public async Task CancellationAtPlaybackCannotPublishLateSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var setup = new Fixture();
        setup.Player.AfterInvoke = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Execute(token: cancellation.Token));
        Assert.Equal(1, setup.Player.Calls);
    }

    [Fact]
    public async Task PreviewIsReportedAsPreviewRatherThanFullPlayback()
    {
        var setup = new Fixture();
        setup.Player.Preview = true;
        var result = await setup.Execute();
        Assert.Contains("开始试听", result.Summary);
    }

    private sealed class Fixture
    {
        public Catalog Catalog { get; } = new();
        public Driver Driver { get; } = new();
        public Player Player { get; } = new();
        public Progress Progress { get; } = new();
        public Task<ApplicationSearchWorkflowResult> Execute(ApplicationSearchFollowUp followUp = ApplicationSearchFollowUp.PlayFirst,
            CancellationToken token = default) => new WindowsApplicationSearchWorkflowAutomation(Catalog, Driver,
                TimeSpan.Zero, progress: Progress, hotMusic: new Resolver(), musicPlayback: Player).ExecuteAsync(App,
                InstalledApplicationCatalog.CreateTargetBinding(App), new(App.Id, "最近最热的一首歌", followUp,
                    MusicChart: ApplicationSearchWorkflow.NetEaseHotChart), token);
    }
    private sealed class Catalog : IInstalledApplicationCatalog
    {
        public KnownDesktopApplication Current { get; set; } = App;
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [Current];
        public KnownDesktopApplication? FindById(string id) => id == Current.Id ? Current : null;
        public KnownDesktopApplication? FindByDisplayName(string name) => name == Current.DisplayName ? Current : null;
        public KnownDesktopApplication? FindBrowser(string name) => null;
    }
    private sealed class Resolver : IHotMusicResolver
    {
        public Task<HotMusicResolution> ResolveAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Song); }
    }
    private sealed class Player : INetEaseSongPlayback
    {
        public int Calls { get; private set; }
        public bool Preview { get; set; }
        public Action? AfterInvoke { get; set; }
        public Task<MusicPlaybackResult> PlayAsync(KnownDesktopApplication application, ForegroundWindowSnapshot window,
            HotMusicResolution song, Action validateTarget, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); validateTarget(); Assert.Equal(Song, song);
            Assert.Equal(Window, window); Assert.Equal(App, application); Calls++; AfterInvoke?.Invoke();
            return Task.FromResult(new MusicPlaybackResult(true, Preview));
        }
    }
    private sealed class Driver : IApplicationSearchWorkflowDriver
    {
        public int Searches { get; private set; }
        public string? SubmittedQuery { get; private set; }
        public string ResultTitle { get; set; } = Song.Title;
        public Action? AfterRead { get; set; }
        public ForegroundWindowSnapshot Open(KnownDesktopApplication app, CancellationToken token) => Window;
        public ForegroundWindowSnapshot RequireBound(KnownDesktopApplication app, ForegroundWindowSnapshot window, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return window; }
        public void Search(ForegroundWindowSnapshot window, string query, CancellationToken token)
        { token.ThrowIfCancellationRequested(); SubmittedQuery = query; Searches++; }
        public SearchResultPage ReadResults(ForegroundWindowSnapshot window, string query, CancellationToken token)
        { AfterRead?.Invoke(); return new(SearchResultPageState.Results, query, "fixture-page", [new("fixture-card", ResultTitle, new object())]); }
        public void Invoke(ForegroundWindowSnapshot window, SearchResultPage page, ApplicationSearchFollowUp followUp,
            Action validateAtInvoke, CancellationToken token) => throw new InvalidOperationException("A card cannot replace the selected chart song.");
        public bool VerifyFollowUp(ForegroundWindowSnapshot window, SearchResultItem item, ApplicationSearchFollowUp followUp,
            CancellationToken token) => throw new InvalidOperationException("Use exact song playback verification.");
    }
    private sealed class Progress : IApplicationSearchProgressSink
    {
        public List<ApplicationSearchStage> Stages { get; } = [];
        public Task ReportAsync(ApplicationSearchProgress progress, CancellationToken token = default)
        { Stages.Add(progress.Stage); return Task.CompletedTask; }
    }
}
