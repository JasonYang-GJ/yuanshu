using System.Diagnostics;
using System.IO;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

public sealed class ApplicationSearchWorkflowException(string code, string userMessage)
    : InvalidOperationException(userMessage)
{
    public string Code { get; } = code;
}

public sealed record ApplicationSearchWorkflowResult(
    bool ApplicationVerified,
    bool QuerySubmitted,
    bool ResultsVerified,
    bool NoResults,
    bool FollowUpInvoked,
    bool FollowUpVerified,
    string Summary)
{
    public LatestMusicResolution? LatestMusic { get; init; }
    public HotMusicResolution? HotMusic { get; init; }
    public string SubmissionMode { get; init; } = "Enter";
}

public interface IApplicationSearchWorkflowAutomation
{
    Task<ApplicationSearchWorkflowResult> ExecuteAsync(
        KnownDesktopApplication application,
        string expectedApplicationTargetBinding,
        ApplicationSearchWorkflow workflow,
        CancellationToken cancellationToken = default);
}

internal enum SearchResultPageState { Unverified, Results, Empty }
internal sealed record SearchResultItem(string Identity, string Title, object NativeReference);
internal sealed record SearchResultPage(
    SearchResultPageState State, string Query, string Identity, IReadOnlyList<SearchResultItem> Items);

internal interface IApplicationSearchWorkflowDriver
{
    ForegroundWindowSnapshot Open(KnownDesktopApplication application, CancellationToken cancellationToken);
    ForegroundWindowSnapshot Open(KnownDesktopApplication application, CancellationToken cancellationToken, Action launchInitiated)
    {
        var window = Open(application, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        launchInitiated();
        return window;
    }
    ForegroundWindowSnapshot RequireBound(KnownDesktopApplication application, ForegroundWindowSnapshot window, CancellationToken cancellationToken);
    void PrepareSearch(ForegroundWindowSnapshot window, CancellationToken cancellationToken) { }
    void Search(ForegroundWindowSnapshot window, string query, CancellationToken cancellationToken);
    void Search(ForegroundWindowSnapshot window, string query, Action validateTarget, CancellationToken cancellationToken)
    {
        validateTarget();
        Search(window, query, cancellationToken);
    }
    SearchResultPage ReadResults(ForegroundWindowSnapshot window, string query, CancellationToken cancellationToken);
    void Invoke(ForegroundWindowSnapshot window, SearchResultPage page, ApplicationSearchFollowUp followUp, Action validateAtInvoke, CancellationToken cancellationToken);
    bool VerifyFollowUp(ForegroundWindowSnapshot window, SearchResultItem item, ApplicationSearchFollowUp followUp, CancellationToken cancellationToken);
}

public sealed class WindowsApplicationSearchWorkflowAutomation : IApplicationSearchWorkflowAutomation
{
    private readonly IInstalledApplicationCatalog _applications;
    private readonly Func<IApplicationSearchWorkflowDriver> _createDriver;
    private readonly TimeSpan _verificationTimeout;
    private readonly ILatestMusicResolver _latestMusic;
    private readonly IApplicationSearchProgressSink? _progress;
    private readonly IVisualSearchAutomation? _visualSearch;
    private readonly IHotMusicResolver _hotMusic;
    private readonly INetEaseSongPlayback _musicPlayback;

    public WindowsApplicationSearchWorkflowAutomation(
        IDesktopProcessLauncher launcher,
        IInstalledApplicationCatalog applications,
        IReliableDesktopAutomation automation,
        IForegroundWindowContextProvider foregroundWindows,
        ILatestMusicResolver? latestMusic = null,
        IApplicationSearchProgressSink? progress = null,
        IVisualSearchAutomation? visualSearch = null,
        IHotMusicResolver? hotMusic = null,
        INetEaseSongPlayback? musicPlayback = null)
    {
        _applications = applications;
        _createDriver = () => new WindowsSearchResultDriver(launcher, automation, foregroundWindows);
        _verificationTimeout = TimeSpan.FromSeconds(12);
        _latestMusic = latestMusic ?? new LatestMusicResolver();
        _progress = progress;
        _visualSearch = visualSearch;
        _hotMusic = hotMusic ?? new HotMusicResolver();
        _musicPlayback = musicPlayback ?? new NetEaseSongPlayback();
    }

    internal WindowsApplicationSearchWorkflowAutomation(
        IInstalledApplicationCatalog applications,
        IApplicationSearchWorkflowDriver driver,
        TimeSpan verificationTimeout,
        ILatestMusicResolver? latestMusic = null,
        IApplicationSearchProgressSink? progress = null,
        IVisualSearchAutomation? visualSearch = null,
        IHotMusicResolver? hotMusic = null,
        INetEaseSongPlayback? musicPlayback = null)
    {
        _applications = applications;
        _createDriver = () => driver;
        _verificationTimeout = verificationTimeout;
        _latestMusic = latestMusic ?? new LatestMusicResolver();
        _progress = progress;
        _visualSearch = visualSearch;
        _hotMusic = hotMusic ?? new HotMusicResolver();
        _musicPlayback = musicPlayback ?? new NetEaseSongPlayback();
    }

    public async Task<ApplicationSearchWorkflowResult> ExecuteAsync(
        KnownDesktopApplication application, string expectedApplicationTargetBinding,
        ApplicationSearchWorkflow workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(workflow);
        workflow.Validate();
        var driver = _createDriver();
        var query = DesktopSearchQuery.NormalizeAndValidate(workflow.Query);
        RequireApplication();
        ForegroundWindowSnapshot window = null!;
        LatestMusicResolution? latestMusic = null;
        HotMusicResolution? hotMusic = null;
        Task<LatestMusicResolution>? lookup = null;
        Task<HotMusicResolution>? hotLookup = null;
        using var lookupCancellation = workflow.LatestMusicArtist is null && workflow.MusicChart is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lookupDuringLaunch = (workflow.LatestMusicArtist is not null || workflow.MusicChart is not null)
            && Path.IsPathFullyQualified(application.LaunchTarget)
            && string.Equals(Path.GetFileName(application.LaunchTarget), "cloudmusic.exe", StringComparison.OrdinalIgnoreCase);
        try
        {
            await ReportAsync(ApplicationSearchStage.Opening).ConfigureAwait(false);
            // Only anonymous metadata overlaps the wait for a visible window. Desktop
            // activation stays on this execution path and retains all its target checks.
            window = lookupDuringLaunch
                ? driver.Open(application, cancellationToken, StartLookup)
                : driver.Open(application, cancellationToken);
            RequireWindow();
            await ReportAsync(ApplicationSearchStage.Opened).ConfigureAwait(false);
            if (workflow.LatestMusicArtist is not null)
            {
                if (!NetEaseSearchPageReader.Applies(window))
                    throw Failure("latest_music_application_unsupported", "按发行日期查歌目前支持网易云音乐；这个应用尚未接入发行资料核对。");
                await ReportAsync(ApplicationSearchStage.ResolvingMusic).ConfigureAwait(false);
                StartLookup();
                // While public metadata is in flight, prepare the same visible app's
                // accessibility input on this one desktop execution path. This is read-only;
                // Search still validates the current target and input before any write.
                driver.PrepareSearch(window, cancellationToken);
                latestMusic = await lookup!.ConfigureAwait(false);
                // The network response supplies metadata only, never window or action authority.
                RequireWindow();
                query = DesktopSearchQuery.NormalizeAndValidate(latestMusic.SearchQuery);
            }
            if (workflow.MusicChart is not null)
            {
                if (!NetEaseSearchPageReader.Applies(window))
                    throw Failure("hot_music_application_unsupported", "热歌榜连续播放目前支持网易云音乐。");
                await ReportAsync(ApplicationSearchStage.ResolvingHotMusic).ConfigureAwait(false);
                StartLookup();
                driver.PrepareSearch(window, cancellationToken);
                hotMusic = await hotLookup!.ConfigureAwait(false);
                RequireWindow();
                query = DesktopSearchQuery.NormalizeAndValidate(hotMusic.SearchQuery);
            }
        }
        finally
        {
            if (lookup is not null || hotLookup is not null)
            {
                await lookupCancellation!.CancelAsync().ConfigureAwait(false);
                try { if (lookup is not null) await lookup.ConfigureAwait(false); } catch { /* Observe without replacing the original failure. */ }
                try { if (hotLookup is not null) await hotLookup.ConfigureAwait(false); } catch { /* Observe without replacing the original failure. */ }
            }
        }
        await ReportAsync(ApplicationSearchStage.Searching).ConfigureAwait(false);
        RequireWindow();
        try
        {
            driver.Search(window, query, RequireWindow, cancellationToken);
        }
        catch (DesktopSearchException exception) when (
            exception.Code == DesktopSearchErrorCodes.CandidateNotFound
            && _visualSearch is not null && workflow.FollowUp == ApplicationSearchFollowUp.None
            && workflow.LatestMusicArtist is null && workflow.MusicChart is null)
        {
            RequireWindow();
            var visual = await _visualSearch.SearchAsync(window, query, cancellationToken, RequireWindow)
                .ConfigureAwait(false);
            RequireWindow();
            if (!visual.ApplicationVerified || !visual.QuerySubmitted || !visual.ResultsVerified
                || visual.FollowUpInvoked || visual.FollowUpVerified || visual.SubmissionMode != "LiveFilter")
                throw Failure("application_search_incomplete", "视觉搜索没有完成必要核对，不能标记为成功。");
            return visual;
        }
        RequireWindow();
        await ReportAsync(ApplicationSearchStage.VerifyingResults).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        SearchResultPage page;
        do
        {
            RequireWindow();
            page = driver.ReadResults(window, query, cancellationToken);
            RequireWindow();
            if (page.State != SearchResultPageState.Unverified
                && string.Equals(page.Query, query, StringComparison.Ordinal)) break;
            // Briefly check a newly submitted page more often; retain the same deadline
            // and all target/query/result checks, then return to the normal cadence.
            var interval = timer.Elapsed < TimeSpan.FromMilliseconds(1500) ? 75 : 200;
            await Task.Delay(TimeSpan.FromMilliseconds(interval), cancellationToken).ConfigureAwait(false);
        } while (timer.Elapsed < _verificationTimeout);

        if (page.State == SearchResultPageState.Unverified || page.Query != query)
            throw Failure("application_search_results_unverified", "已提交搜索，但还无法确认对应的结果页面；没有继续打开或播放。");
        if (page.State == SearchResultPageState.Empty)
        {
            if (workflow.FollowUp != ApplicationSearchFollowUp.None)
                throw Failure("application_search_no_results", "已确认搜索没有结果，因此没有继续打开或播放。");
            return new(true, true, true, true, false, false,
                latestMusic is null ? "已打开应用、提交搜索，并确认没有匹配的结果。" : DescribeLatest(latestMusic, true))
                { LatestMusic = latestMusic, HotMusic = hotMusic };
        }
        if (page.Items.Count == 0)
            throw Failure("application_search_results_unverified", "已提交搜索，但结果列表暂时无法可靠读取；没有继续操作。");
        if (workflow.FollowUp == ApplicationSearchFollowUp.None)
            return new(true, true, true, false, false, false,
                hotMusic is not null ? $"已搜索网易云热歌榜第 1 名《{hotMusic.Title}》。"
                    : latestMusic is null ? "已打开应用、提交搜索，并确认当前显示该关键词的结果列表。" : DescribeLatest(latestMusic, false))
                { LatestMusic = latestMusic, HotMusic = hotMusic };

        if (hotMusic is not null)
        {
            if (workflow.FollowUp != ApplicationSearchFollowUp.PlayFirst)
                throw Failure("hot_music_followup_unsupported", "已完成热歌搜索；本次热榜后续动作仅支持明确要求播放。");
            if (!page.Items.Any(item => string.Equals(item.Title.Trim().Normalize(), hotMusic.Title.Trim().Normalize(), StringComparison.Ordinal)))
                throw Failure("hot_music_result_unverified", "搜索结果未核对到本次榜单歌曲，尚未发起播放。");
            RequireWindow();
            await ReportAsync(ApplicationSearchStage.PlayingMusic).ConfigureAwait(false);
            var played = await _musicPlayback.PlayAsync(application, window, hotMusic, RequireWindow, cancellationToken)
                .ConfigureAwait(false);
            RequireWindow();
            if (!played.Verified)
                throw Failure("music_playback_not_verified", "尚未核对到本次歌曲正在播放。");
            return new(true, true, true, false, true, true,
                $"已在网易云{(played.IsPreview ? "开始试听" : "播放")}《{hotMusic.Title}》—{string.Join("、", hotMusic.Artists.Select(artist => artist.Name))}（热歌榜第 1 名）。")
                { HotMusic = hotMusic };
        }

        // A dated catalog match cannot authorize the first UI card: it could be a cover.
        if (latestMusic is not null)
            throw Failure("latest_music_followup_unverified", DescribeLatest(latestMusic, false)
                + "\n还不能把桌面第一项与该原唱曲目可靠绑定，因此没有继续打开或播放。");

        // Re-observe before invoking: a reordered first result is a changed target, not a replacement.
        RequireWindow();
        var current = driver.ReadResults(window, query, cancellationToken);
        RequireSameFirst(page, current);
        driver.Invoke(window, page, workflow.FollowUp, RequireWindow, cancellationToken);
        timer.Restart();
        do
        {
            RequireWindow();
            if (driver.VerifyFollowUp(window, page.Items[0], workflow.FollowUp, cancellationToken))
            {
                RequireWindow();
                return new(true, true, true, false, true, true,
                    workflow.FollowUp == ApplicationSearchFollowUp.PlayFirst
                        ? "已打开应用、完成搜索，并确认第一项媒体结果正在播放。"
                        : "已打开应用、完成搜索，并确认第一项媒体结果已打开。");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        } while (timer.Elapsed < _verificationTimeout);
        throw Failure("application_search_followup_unverified", "搜索结果已确认，并已请求后续操作，但无法确认最终打开或播放状态；不会重复执行。");

        void StartLookup()
        {
            RequireApplication();
            if (workflow.MusicChart is not null)
                hotLookup ??= _hotMusic.ResolveAsync(lookupCancellation!.Token);
            else
                lookup ??= _latestMusic.ResolveAsync(workflow.LatestMusicArtist!, lookupCancellation!.Token);
        }
        void RequireApplication()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentApplication = _applications.FindById(workflow.ApplicationId);
            if (workflow.ApplicationId != application.Id || currentApplication is null
                || !InstalledApplicationCatalog.TargetBindingMatches(application, expectedApplicationTargetBinding)
                || !InstalledApplicationCatalog.TargetBindingMatches(currentApplication, expectedApplicationTargetBinding)
                || !InstalledApplicationCatalog.IsAllowedLaunchTarget(currentApplication))
                throw Failure("application_search_target_changed", "应用目标已经变化，本次搜索流程已停止。");
        }
        void RequireWindow()
        {
            RequireApplication();
            window = driver.RequireBound(application, window, cancellationToken);
        }
        Task ReportAsync(ApplicationSearchStage stage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _progress?.ReportAsync(new(stage, application.DisplayName), cancellationToken) ?? Task.CompletedTask;
        }
    }

    private static string DescribeLatest(LatestMusicResolution release, bool noResults)
    {
        var title = string.IsNullOrWhiteSpace(release.MatchedTitle) ? release.TrackTitles.FirstOrDefault() ?? release.ReleaseTitle : release.MatchedTitle;
        var summary = $"已在网易云搜索{release.Artist}《{title}》"
            + (noResults ? "，当前没有匹配结果。" : "，结果页已显示。")
            + $"\n所属最新发行作品《{release.ReleaseTitle}》：{release.ReleaseDate:yyyy-MM-dd}（{release.CatalogScope}）。"
            + (release.PlatformVerificationUnavailable ? "\n暂未核实网易云是否收录原唱。"
                : release.PlatformMatchFound ? "" : "\n网易云目录未核到对应原唱，当前结果可能是翻唱或同名歌曲。")
            + (release.FirstReleaseVerified ? "" : "\n此日期为作品发行日期，非每首歌的首次发行证明。");
        return summary;
    }

    internal static void RequireSameFirst(SearchResultPage expected, SearchResultPage current)
    {
        if (current.State != SearchResultPageState.Results || expected.Query != current.Query
            || expected.Identity != current.Identity || expected.Items.Count == 0 || current.Items.Count == 0
            || expected.Items[0].Identity != current.Items[0].Identity || expected.Items[0].Title != current.Items[0].Title)
            throw Failure("application_search_result_changed", "第一项搜索结果已经变化，因此没有继续操作。");
    }

    internal static ApplicationSearchWorkflowException Failure(string code, string message) => new(code, message);
}
