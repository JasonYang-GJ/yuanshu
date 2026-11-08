using System.Runtime.InteropServices;
using System.Windows.Automation;
using ScreenGuide.Skills.Abstractions;
using static ScreenGuide.Skills.Windows.WindowsApplicationSearchWorkflowAutomation;

namespace ScreenGuide.Skills.Windows;

/// <summary>
/// Reads only the bound application's accessibility tree. Coordinates, screenshots,
/// browser debugging endpoints and model-produced selectors are never action authority.
/// </summary>
internal sealed class WindowsSearchResultDriver(
    IDesktopProcessLauncher launcher,
    IReliableDesktopAutomation automation,
    IForegroundWindowContextProvider foregroundWindows,
    Func<IntPtr, ApplicationWindowSnapshot?>? readWindow = null,
    Func<long>? readForegroundWindow = null,
    Func<long, IReadOnlyList<DesktopSearchControl>>? discoverControls = null,
    TimeProvider? readinessClock = null,
    Action<TimeSpan, CancellationToken>? readinessWait = null,
    Func<long, CancellationToken, NativeSearchPageObservation>? readNativeSearchPage = null,
    Func<long, IReadOnlyList<DesktopSearchControl>>? discoverResultInputs = null) : IApplicationSearchWorkflowDriver
{
    private readonly Func<IntPtr, ApplicationWindowSnapshot?> _readWindow =
        readWindow ?? new WindowsApplicationActivationRuntime().ReadWindow;
    private readonly Func<long> _readForegroundWindow = readForegroundWindow ?? (() => GetForegroundWindow().ToInt64());
    private readonly WindowsDesktopSearchAutomationDriver _search = new();
    private readonly Func<long, IReadOnlyList<DesktopSearchControl>>? _discoverResultInputs = discoverResultInputs;
    private readonly Func<long, IReadOnlyList<DesktopSearchControl>>? _discoverControls = discoverControls;
    private readonly TimeProvider _readinessClock = readinessClock ?? TimeProvider.System;
    private readonly Action<TimeSpan, CancellationToken> _readinessWait = readinessWait ?? WaitForReadiness;
    private readonly Func<long, CancellationToken, NativeSearchPageObservation> _readNativeSearchPage =
        readNativeSearchPage ?? NetEaseSearchPageReader.Read;
    private KnownDesktopApplication? _openedApplication;
    private ForegroundWindowSnapshot? _openedWindow;
    private NativeSearchPageObservation? _beforeNativeSearch;
    private DouyinSearchPageObservation? _beforeDouyinSearch;
    private bool _nativeInputPrepared;
    private DesktopSearchControl? _preparedSearchLocator;
    private Func<ForegroundWindowSnapshot, string, CancellationToken, bool>? _submittedInputReadback;

    public ForegroundWindowSnapshot Open(KnownDesktopApplication application, CancellationToken cancellationToken)
        => OpenCore(application, cancellationToken, null);

    public ForegroundWindowSnapshot Open(KnownDesktopApplication application, CancellationToken cancellationToken, Action launchInitiated)
        => OpenCore(application, cancellationToken, launchInitiated);

    private ForegroundWindowSnapshot OpenCore(KnownDesktopApplication application, CancellationToken cancellationToken, Action? launchInitiated)
    {
        _openedApplication = null;
        _openedWindow = null;
        _nativeInputPrepared = false;
        _preparedSearchLocator = null;
        _submittedInputReadback = null;
        cancellationToken.ThrowIfCancellationRequested();
        var opened = launchInitiated is null ? launcher.OpenApplicationVisible(application, cancellationToken)
            : launcher.OpenApplicationVisible(application, cancellationToken, launchInitiated);
        var window = foregroundWindows.ResolveWindow(opened.WindowHandle)
            ?? throw Failure("application_search_window_missing", "应用窗口尚未就绪，未输入搜索内容。");
        var boundWindow = RequireBound(application, window, cancellationToken);
        _openedApplication = application;
        _openedWindow = boundWindow;
        return boundWindow;
    }

    public ForegroundWindowSnapshot RequireBound(KnownDesktopApplication application, ForegroundWindowSnapshot window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var native = _readWindow(new IntPtr(window.WindowHandle));
        var current = foregroundWindows.ResolveWindow(window.WindowHandle);
        if (native is null || current is null || !native.Process.IsRunning
            || window.ProcessId <= 0 || window.ProcessStartTimeUtc == default
            || current.WindowHandle != window.WindowHandle || native.Handle.ToInt64() != window.WindowHandle
            || current.ProcessId != window.ProcessId || current.ProcessStartTimeUtc != window.ProcessStartTimeUtc
            || native.Process.ProcessId != window.ProcessId || native.Process.ProcessStartTimeUtc != window.ProcessStartTimeUtc
            || !string.Equals(current.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(native.Process.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase)
            || !MatchesApplication(application, native.Process)
            || _readForegroundWindow() != window.WindowHandle)
            throw Failure("application_search_window_changed", "目标应用或前台窗口发生变化，搜索流程已停止。");
        cancellationToken.ThrowIfCancellationRequested();
        // The same application can change its title after navigation. PID + creation time +
        // HWND + executable/AUMID remain bound; the updated title is used by the existing search guard.
        return current;
    }

    internal static bool MatchesApplication(KnownDesktopApplication application, ApplicationProcessSnapshot process) =>
        TrustedApplicationActivation.MatchesRegisteredTarget(application, process);

    public void PrepareSearch(ForegroundWindowSnapshot window, CancellationToken cancellationToken)
    {
        _nativeInputPrepared = false;
        _preparedSearchLocator = null;
        var (application, openedWindow) = RequireOpenedWindow(window, cancellationToken);
        var current = RequireBound(application, openedWindow, cancellationToken);
        if (!NetEaseSearchPageReader.Applies(current)) return;
        var ready = WaitForNativeSearchInput(application, openedWindow, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _preparedSearchLocator = ready.Locator;
        _nativeInputPrepared = true;
    }

    public void Search(ForegroundWindowSnapshot window, string query, CancellationToken cancellationToken)
        => SearchCore(window, query, null, cancellationToken);

    public void Search(ForegroundWindowSnapshot window, string query, Action validateTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validateTarget);
        SearchCore(window, query, validateTarget, cancellationToken);
    }

    private void SearchCore(ForegroundWindowSnapshot window, string query, Action? validateTarget,
        CancellationToken cancellationToken)
    {
        _submittedInputReadback = null;
        var prepared = _nativeInputPrepared;
        var locator = _preparedSearchLocator;
        _nativeInputPrepared = false; // Preparation applies to one search, never future turns.
        _preparedSearchLocator = null;
        var (application, openedWindow) = RequireOpenedWindow(window, cancellationToken);
        var searchWindow = RequireBound(application, openedWindow, cancellationToken);
        var requiresNativeReadiness = NetEaseSearchPageReader.Applies(searchWindow);
        if (requiresNativeReadiness)
        {
            if (!prepared)
            {
                var ready = WaitForNativeSearchInput(application, openedWindow, cancellationToken);
                searchWindow = ready.Window;
                locator = ready.Locator;
            }
            _beforeNativeSearch = _readNativeSearchPage(searchWindow.WindowHandle, cancellationToken);
        }
        if (DouyinSearchPageReader.Applies(searchWindow))
            _beforeDouyinSearch = DouyinSearchPageReader.Read(searchWindow.WindowHandle, cancellationToken);
        void RefreshBeforeInput()
        {
            // Reading a starting app's result metadata can outlast a title transition.
            searchWindow = ValidateSearchWindow(cancellationToken);
        }
        ForegroundWindowSnapshot ValidateSearchWindow(CancellationToken boundaryToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            boundaryToken.ThrowIfCancellationRequested();
            // The production workflow rechecks its exact ApplicationId and catalog target
            // binding at every input boundary. Independently retain this Open's HWND,
            // process lifetime, registered native path and foreground authority here.
            validateTarget?.Invoke();
            return RequireBound(application, openedWindow, boundaryToken);
        }
        DesktopAutomationResult Submit()
        {
            if (automation is IBoundApplicationSearchAutomation boundAutomation)
                return boundAutomation.SearchBoundApplication(searchWindow, query,
                    requiresNativeReadiness ? locator : null, ValidateSearchWindow, cancellationToken);
            return requiresNativeReadiness && automation is IPreparedNativeSearchAutomation nativeAutomation
                ? nativeAutomation.SearchWithPreparationLocator(searchWindow, query, locator, cancellationToken)
                : automation.Search(searchWindow, query, cancellationToken);
        }
        DesktopAutomationResult result;
        if (requiresNativeReadiness)
        {
            RefreshBeforeInput();
            // All startup retries above are read-only. Once actual search begins, even a
            // discovery failure stops this native workflow instead of trying input again.
            result = Submit();
        }
        else result = SubmitWithDiscoveryRetry(Submit, RefreshBeforeInput, cancellationToken);
        if (!result.Verified)
            throw Failure("application_search_submission_unverified", "无法确认搜索提交，已停止后续操作。");
        _submittedInputReadback = result.VerifySubmittedInput;
    }

    private (KnownDesktopApplication Application, ForegroundWindowSnapshot Window) RequireOpenedWindow(
        ForegroundWindowSnapshot window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var application = _openedApplication;
        var openedWindow = _openedWindow;
        if (application is null || openedWindow is null)
            throw Failure("application_search_window_missing", "尚未确认本次打开的应用窗口，未输入搜索内容。");
        if (!WindowIdentityContract.Matches(openedWindow, window with { WindowTitle = openedWindow.WindowTitle }))
            throw Failure("application_search_window_changed", "目标应用窗口发生变化，未输入搜索内容。");
        return (application, openedWindow);
    }

    private (ForegroundWindowSnapshot Window, DesktopSearchControl Locator) WaitForNativeSearchInput(KnownDesktopApplication application,
        ForegroundWindowSnapshot openedWindow, CancellationToken cancellationToken)
    {
        var started = _readinessClock.GetTimestamp();
        var budget = TimeSpan.FromSeconds(45);
        var pollInterval = TimeSpan.FromMilliseconds(100);
        var readControls = _discoverControls ?? _search.CreateNativePreparationProbe();
        DesktopSearchControlSnapshot? previous = null;
        // UIA/COM discovery is synchronous: the budget bounds the start of each probe,
        // not an in-flight COM call. Cancellation and target identity are checked again
        // immediately after it returns, before any page read or input can proceed.
        while (_readinessClock.GetElapsedTime(started) < budget)
        {
            RequireBound(application, openedWindow, cancellationToken);
            IReadOnlyList<DesktopSearchControl> controls;
            try { controls = readControls(openedWindow.WindowHandle); }
            catch (Exception exception) when (IsVolatile(exception)) { controls = []; }
            var currentWindow = RequireBound(application, openedWindow, cancellationToken);
            var candidate = UniqueReadyNativeInput(controls, openedWindow.WindowHandle);
            if (candidate is not null && previous is not null
                && candidate.Snapshot.OwnerWindowHandle == previous.OwnerWindowHandle
                && candidate.Snapshot.NativeSearchIdentity == previous.NativeSearchIdentity
                && candidate.Snapshot.NativeSearchUsesContainer == previous.NativeSearchUsesContainer) return (currentWindow, candidate);
            previous = candidate?.Snapshot;
            var remaining = budget - _readinessClock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) break;
            _readinessWait(remaining < pollInterval ? remaining : pollInterval, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw Failure("application_search_input_not_ready",
            "网易云音乐的搜索框尚未稳定就绪，暂时无法唯一确认，本次没有输入或提交搜索。请稍后再试。");
    }

    private static DesktopSearchControl? UniqueReadyNativeInput(
        IReadOnlyList<DesktopSearchControl> controls, long windowHandle)
    {
        var unique = new List<DesktopSearchControl>();
        foreach (var group in controls.GroupBy(control => string.IsNullOrEmpty(control.Snapshot.NativeSearchIdentity)
                     ? "uia:" + control.Snapshot.RuntimeId : "native:" + control.Snapshot.NativeSearchIdentity))
        {
            var first = group.First().Snapshot;
            // Multiple proxies may refer to one input, but conflicting evidence must
            // survive deduplication. Never discard a password/identity mismatch proxy.
            if (group.Any(control => (control.Snapshot with { RuntimeId = first.RuntimeId }) != first)) return null;
            unique.Add(group.First());
        }
        var reliable = unique.Where(control => SafeForegroundSearchExecutor.IsReliableCandidate(control.Snapshot, windowHandle)).ToArray();
        return reliable.Length == 1 && reliable[0].Snapshot.HasNativeSearchSemantics
            && !string.IsNullOrEmpty(reliable[0].Snapshot.NativeSearchIdentity) && !reliable[0].Snapshot.NativeSearchContainerPending
            ? reliable[0] : null;
    }

    private static void WaitForReadiness(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(delay)) cancellationToken.ThrowIfCancellationRequested();
    }

    internal static DesktopAutomationResult SubmitWithDiscoveryRetry(Func<DesktopAutomationResult> submit,
        Action validateBeforeAttempt, CancellationToken cancellationToken)
    {
        DesktopAutomationResult result;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            validateBeforeAttempt();
            try { result = submit(); break; }
            catch (DesktopSearchException exception) when (
                exception.Code == DesktopSearchErrorCodes.CandidateNotFound && attempt < 2)
            {
                // This error is raised strictly before Focus/SetValue/Enter. Chromium may expose
                // its accessibility tree asynchronously on the first local accessibility request.
                // Never retry ambiguity, writes, submission or any later verification failure.
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250)))
                    cancellationToken.ThrowIfCancellationRequested();
            }
        }
        return result;
    }

    public SearchResultPage ReadResults(ForegroundWindowSnapshot window, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Re-read the exact input actually submitted, not a fresh same-named replacement.
            // This receipt cannot write and is scoped to this driver's current Search.
            // Failure retains the receipt and never falls back to a newly discovered control.
            if (_submittedInputReadback is { } readSubmitted)
            {
                if (!readSubmitted(window, query, cancellationToken)) return Unverified(query);
            }
            else
            {
                // Compatibility path for drivers without a native submitted-input receipt.
                var inputs = (_discoverResultInputs ?? _search.Discover)(window.WindowHandle)
                .Where(control => SafeForegroundSearchExecutor.IsReliableCandidate(control.Snapshot, window.WindowHandle))
                .GroupBy(control => string.IsNullOrEmpty(control.Snapshot.NativeSearchIdentity)
                    ? control.Snapshot.RuntimeId : control.Snapshot.NativeSearchIdentity)
                .Select(group => group.First()).ToArray();
                if (inputs.Length != 1 || inputs[0].Snapshot.Value != query) return Unverified(query);
            }
            if (NetEaseSearchPageReader.Applies(window))
            {
                var observation = _readNativeSearchPage(window.WindowHandle, cancellationToken);
                if (_beforeNativeSearch is not null && NetEaseSearchPageReader.IsVerifiedResultForQuery(_beforeNativeSearch, observation, query))
                    return new(SearchResultPageState.Results, query, observation.PageIdentity, observation.Songs);
                return Unverified(query);
            }
            if (DouyinSearchPageReader.Applies(window))
            {
                var observation = DouyinSearchPageReader.Read(window.WindowHandle, cancellationToken);
                if (_beforeDouyinSearch is not null && DouyinSearchPageReader.IsVerifiedResultForQuery(_beforeDouyinSearch, observation, query))
                    return new(SearchResultPageState.Results, query, observation.PageIdentity, observation.Videos);
                return Unverified(query);
            }
            var roots = Roots(window.WindowHandle);
            var nodes = roots.SelectMany(root => Descendants(root, cancellationToken)).ToArray();
            // A window-level heading can update before its old results disappear. Require the
            // actual result container to expose both this query and a completed loading state.
            // Apps without this contract need an independently verified native adapter.
            var containers = nodes.Where(node => IsReadyQueryContainer(node, query)).ToArray();
            var pages = new List<SearchResultPage>();
            foreach (var container in containers)
            {
                var items = Children(container, cancellationToken)
                    .Where(IsResultRow).ToArray();
                if (items.Length == 0)
                {
                    if (Descendants(container, cancellationToken).Any(node => IsEmptyMessage(Name(node))))
                        pages.Add(new(SearchResultPageState.Empty, query, Identity(container), []));
                    continue;
                }
                var root = roots.FirstOrDefault(candidate => IsDescendant(container, candidate));
                if (root is null) continue;
                var media = HasMediaSemantics(container, cancellationToken);
                var records = items.Select(item => new SearchResultItem(Identity(item), ResultTitle(item, cancellationToken),
                    new ResultToken(root, container, item, window.WindowHandle, media, MediaIdentity(item)))).ToArray();
                // A missing first title is not permission to skip to a later result.
                if (records.Any(record => string.IsNullOrWhiteSpace(record.Title))) continue;
                pages.Add(new(SearchResultPageState.Results, query, Identity(container), records));
            }
            if (pages.Count == 1) return pages[0];
            return Unverified(query);
        }
        catch (Exception exception) when (IsVolatile(exception)) { return Unverified(query); }
    }

    public void Invoke(ForegroundWindowSnapshot window, SearchResultPage page, ApplicationSearchFollowUp followUp,
        Action validateAtInvoke, CancellationToken cancellationToken)
    {
        if (followUp is not (ApplicationSearchFollowUp.OpenFirst or ApplicationSearchFollowUp.PlayFirst)
            || page.Items.Count == 0 || page.Items[0].NativeReference is not ResultToken token || !token.IsMedia
            || string.IsNullOrEmpty(token.MediaIdentity))
            throw Unsupported();
        cancellationToken.ThrowIfCancellationRequested();
        RequireToken(token, page.Items[0]);
        var controls = Descendants(token.Item, cancellationToken).Prepend(token.Item)
            .Where(item => ActionNameMatches(Name(item), page.Items[0].Title, followUp))
            .Where(item => item.Current.IsEnabled && !item.Current.IsOffscreen && !item.Current.IsPassword
                && item.TryGetCurrentPattern(InvokePattern.Pattern, out _)).ToArray();
        if (controls.Length != 1) throw Unsupported();
        var control = controls[0];
        var identity = Identity(control);
        var actionName = Name(control);
        validateAtInvoke();
        RequireSameFirst(page, ReadResults(window, page.Query, cancellationToken));
        RequireToken(token, page.Items[0]);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDescendant(control, token.Item) || identity != Identity(control) || actionName != Name(control)
            || !control.Current.IsEnabled || control.Current.IsOffscreen || control.Current.IsPassword
            || !control.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) throw Unsupported();
        validateAtInvoke();
        cancellationToken.ThrowIfCancellationRequested();
        ((InvokePattern)pattern).Invoke();
    }

    public bool VerifyFollowUp(ForegroundWindowSnapshot window, SearchResultItem item, ApplicationSearchFollowUp followUp, CancellationToken cancellationToken)
    {
        if (item.NativeReference is not ResultToken token || string.IsNullOrEmpty(token.MediaIdentity)) return false;
        try
        {
            foreach (var root in Roots(window.WindowHandle))
            foreach (var section in Descendants(root, cancellationToken).Where(node => IsMediaDestination(Name(node), followUp)))
            {
                // A same-titled song/video already on screen is not proof that this result opened.
                if (MediaIdentity(section) != token.MediaIdentity) continue;
                var contents = Descendants(section, cancellationToken).ToArray();
                if (!contents.Any(node => Name(node) == item.Title)) continue;
                if (followUp == ApplicationSearchFollowUp.OpenFirst) return true;
                if (contents.Any(node => Name(node) is "暂停" or "暂停播放" or "Pause"
                    && node.Current.IsEnabled && !node.Current.IsOffscreen
                    && node.TryGetCurrentPattern(InvokePattern.Pattern, out _))) return true;
            }
        }
        catch (Exception exception) when (IsVolatile(exception)) { }
        return false;
    }

    internal static bool IsQueryHeading(string name, string query) => name == $"搜索结果：{query}"
        || name == $"搜索结果: {query}" || name == $"“{query}”的搜索结果" || name == $"{query} 的搜索结果"
        || name == $"搜索“{query}”" || name == $"搜索 \"{query}\"" || name == $"Search results for {query}";

    internal static bool IsEmptyMessage(string name) => name is "暂无搜索结果" or "未找到相关结果"
        or "没有找到相关结果" or "没有匹配的结果" or "No results found";

    private static bool IsReadyQueryContainer(AutomationElement element, string query)
    {
        var type = element.Current.ControlType;
        return (type == ControlType.List || type == ControlType.DataGrid || type == ControlType.Table)
            && IsReadyQueryContainer(Name(element), element.Current.ItemStatus ?? string.Empty, query);
    }

    internal static bool IsReadyQueryContainer(string name, string itemStatus, string query) =>
        (name == $"搜索结果：{query}" || name == $"歌曲搜索结果：{query}" || name == $"视频搜索结果：{query}"
            || name == $"Search results for {query}" || name == $"Media search results for {query}")
        && itemStatus is "加载完成" or "Complete" or "Loaded";

    private static bool IsResultRow(AutomationElement element) => element.Current.ControlType == ControlType.ListItem
        || element.Current.ControlType == ControlType.DataItem;

    private static bool HasMediaSemantics(AutomationElement container, CancellationToken cancellationToken) =>
        Name(container).StartsWith("歌曲搜索结果：", StringComparison.Ordinal)
        || Name(container).StartsWith("视频搜索结果：", StringComparison.Ordinal)
        || Name(container).StartsWith("Media search results for ", StringComparison.Ordinal)
        || Descendants(container, cancellationToken).Any(node => node.Current.ControlType == ControlType.HeaderItem
            && Name(node) is "歌曲" or "歌曲标题" or "视频标题" or "Song" or "Video");

    private static string ResultTitle(AutomationElement item, CancellationToken cancellationToken)
    {
        var name = Name(item);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        var titles = Descendants(item, cancellationToken)
            .Where(node => node.Current.ControlType == ControlType.Text || node.Current.ControlType == ControlType.Hyperlink)
            .Select(Name).Where(value => value.Length > 0 && value is not ("播放" or "打开" or "查看"))
            .Distinct(StringComparer.Ordinal).ToArray();
        return titles.Length == 1 ? titles[0] : string.Empty;
    }

    internal static bool ActionNameMatches(string name, string title, ApplicationSearchFollowUp followUp) => followUp switch
    {
        ApplicationSearchFollowUp.PlayFirst => name is "播放" or "Play" || name == $"播放 {title}" || name == $"播放“{title}”",
        ApplicationSearchFollowUp.OpenFirst => name is "打开" or "查看" or "Open" || name == $"打开 {title}" || name == $"打开“{title}”",
        _ => false
    };

    private static bool IsMediaDestination(string name, ApplicationSearchFollowUp followUp) => followUp switch
    {
        ApplicationSearchFollowUp.PlayFirst => name is "正在播放" or "播放器" or "Now playing" or "Media player",
        ApplicationSearchFollowUp.OpenFirst => name is "歌曲详情" or "视频详情" or "媒体详情" or "Media details",
        _ => false
    };

    private static void RequireToken(ResultToken token, SearchResultItem item)
    {
        if (GetAncestor(new IntPtr(token.Root.Current.NativeWindowHandle), 2).ToInt64() != token.WindowHandle
            || !IsDescendant(token.Container, token.Root) || !IsDescendant(token.Item, token.Container)
            || Identity(token.Item) != item.Identity || MediaIdentity(token.Item) != token.MediaIdentity
            || !token.Item.Current.IsEnabled || token.Item.Current.IsOffscreen)
            throw Failure("application_search_result_changed", "搜索结果控件已经变化，没有继续操作。");
    }

    private static AutomationElement[] Roots(long window)
    {
        var legacy = LegacyBrowserSearchSemantics.Open(window);
        return legacy.Count == 0 ? [AutomationElement.FromHandle(new IntPtr(window))]
            : legacy.Select(item => AutomationElement.FromHandle(new IntPtr(item.Handle))).ToArray();
    }

    private static IEnumerable<AutomationElement> Descendants(AutomationElement root, CancellationToken cancellationToken)
    {
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        var remaining = 1500;
        while (queue.Count > 0 && remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (parent, depth) = queue.Dequeue();
            if (depth >= 24) continue;
            foreach (var child in Children(parent, cancellationToken))
            {
                if (--remaining < 0) yield break;
                if (child.Current.IsPassword || child.Current.IsOffscreen) continue;
                yield return child;
                queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static IEnumerable<AutomationElement> Children(AutomationElement root, CancellationToken cancellationToken)
    {
        var child = TreeWalker.ControlViewWalker.GetFirstChild(root);
        for (var count = 0; child is not null && count < 300; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return child;
            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
        }
    }

    private static bool IsDescendant(AutomationElement child, AutomationElement root)
    {
        var rootId = Identity(root);
        AutomationElement? current = child;
        for (var depth = 0; current is not null && depth < 128; depth++)
        {
            if (Identity(current) == rootId) return true;
            current = TreeWalker.RawViewWalker.GetParent(current);
        }
        return false;
    }

    private static string Name(AutomationElement element)
    {
        var value = element.Current.Name ?? string.Empty;
        return value.Length <= 512 ? value.Trim() : string.Empty;
    }
    private static string Identity(AutomationElement element) => string.Join(".", element.GetRuntimeId());
    private static SearchResultPage Unverified(string query) => new(SearchResultPageState.Unverified, query, "", []);
    private static bool IsVolatile(Exception exception) => exception is ElementNotAvailableException or COMException
        or InvalidOperationException or ArgumentException;
    private static ApplicationSearchWorkflowException Unsupported() => Failure("application_search_followup_unsupported",
        "搜索结果已经确认，但没有找到能可靠执行的媒体打开或播放控件；没有点击其他内容。");
    private static string MediaIdentity(AutomationElement element) => MediaIdentity(element.Current.ItemType ?? string.Empty);
    internal static string MediaIdentity(string value)
    {
        // ItemType must explicitly expose an immutable media identity, not a row index,
        // title, playback timer or automation control ID. Unknown providers stay unsupported.
        const string prefix = "media-id:";
        return value.StartsWith(prefix, StringComparison.Ordinal) && value.Length is > 9 and <= 200
            && !value.Any(char.IsControl) && value == value.Trim() ? value : string.Empty;
    }
    private sealed record ResultToken(AutomationElement Root, AutomationElement Container, AutomationElement Item, long WindowHandle, bool IsMedia, string MediaIdentity);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
