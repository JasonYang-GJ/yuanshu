using System.Text;
using System.Text.RegularExpressions;

namespace ScreenGuide.AI.Core;

public enum UniversalIntentKind
{
    Conversation,
    OpenApplication,
    OpenWebsite,
    OpenFile,
    SearchForeground,
    DescribeForeground,
    CodingTask,
    Unsupported,
    ApplicationSearch,
    FindDesktopFile,
    DesktopTask
}

public enum IntentPlanReadiness
{
    Ready,
    NeedsContext,
    Unsupported
}

public sealed record ForegroundApplicationContext(
    long WindowHandle,
    string WindowTitle,
    string ProcessName,
    IReadOnlyList<string>? ApplicationNames = null);

public sealed record IntentPlanningContext(
    string? SelectedFilePath = null,
    Guid? SelectedProjectId = null,
    string? SelectedProjectName = null,
    ForegroundApplicationContext? ForegroundApplication = null,
    bool ForegroundObservationConsent = false,
    UniversalIntentKind? ExplicitUserIntent = null,
    bool PreferInstalledApplication = false);

public sealed record IntentPlan(
    Guid Id,
    UniversalIntentKind Kind,
    IntentPlanReadiness Readiness,
    string OriginalText,
    string UserSummary,
    string? Target = null,
    string? Capability = null,
    bool RequiresConfirmation = false,
    string? ConfirmationText = null,
    string? MissingContext = null,
    DateTimeOffset? ExpiresAtUtc = null,
    string? PreferredApplicationName = null,
    ApplicationSearchRequest? ApplicationSearch = null);

public interface IIntentPlanner
{
    IntentPlan Plan(string text, IntentPlanningContext context);
}

/// <summary>
/// V0.2 安全子集的确定性规划器。它只识别明确、低风险的电脑动作；
/// 其他内容保持为普通咨询，不能从模型输出、网页或窗口文字推导操作授权。
/// </summary>
public sealed partial class DeterministicIntentPlanner(TimeProvider timeProvider) : IIntentPlanner
{
    private static readonly string[] UnsafeApplicationRequestTokens =
    [
        "以管理员身份",
        "管理员权限",
        "提升权限",
        "runas",
        "执行命令",
        "运行命令",
        "命令行"
    ];

    private static readonly IReadOnlyDictionary<string, string> KnownWebsites =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["百度"] = "https://www.baidu.com/",
            ["必应"] = "https://www.bing.com/",
            ["B站"] = "https://www.bilibili.com/",
            ["哔哩哔哩"] = "https://www.bilibili.com/",
            ["知乎"] = "https://www.zhihu.com/",
            ["抖音"] = "https://www.douyin.com/",
            ["GitHub"] = "https://github.com/"
        };

    public IntentPlan Plan(string text, IntentPlanningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var originalText = text ?? string.Empty;
        ParsedDesktopFileRequest? desktopFileRequest = null;
        var namedDesktopFile = context.ExplicitUserIntent is null or UniversalIntentKind.OpenFile or UniversalIntentKind.FindDesktopFile
            && DesktopFileRequest.TryParse(originalText, out desktopFileRequest);
        if (namedDesktopFile && (originalText.Any(char.IsControl)
            || !DesktopFileRequest.IsValidName(desktopFileRequest!.Name)))
            return Unsupported(originalText, "请说出要查找或打开的桌面文件名，最多 200 个字符，不能包含路径或控制字符。");
        if (ContainsDisallowedControl(originalText)
            && (LooksLikeForegroundSearch(RemoveControlsForClassification(originalText).Trim())
                || IsCompoundOpenAndSearch(RemoveControlsForClassification(originalText))
                || ApplicationSearchRequest.TryParse(RemoveControlsForClassification(originalText).Trim(), out _, out _)))
        {
            return Unsupported(
                "[invalid-search-input]",
                "搜索内容包含不允许的控制字符，因此没有创建操作计划。");
        }

        var normalized = ApplicationSearchRequest.NormalizeSpokenActionLeadIn(Normalize(text));
        if (normalized.Length == 0)
        {
            return Unsupported(text ?? string.Empty, "请说出或输入你希望电脑完成的事情。");
        }

        var explicitPlan = context.ExplicitUserIntent switch
        {
            UniversalIntentKind.CodingTask => PlanCodingTask(normalized, context),
            UniversalIntentKind.FindDesktopFile when namedDesktopFile
                && desktopFileRequest!.Mode == DesktopFileRequestMode.Find => PlanFindDesktopFile(originalText.Trim(), desktopFileRequest.Name),
            UniversalIntentKind.FindDesktopFile => Unsupported(normalized, "这次查找的桌面文件名已失效，请重新说明。"),
            UniversalIntentKind.OpenFile when namedDesktopFile
                && desktopFileRequest!.Mode == DesktopFileRequestMode.Find => PlanFindDesktopFile(originalText.Trim(), desktopFileRequest.Name),
            UniversalIntentKind.OpenFile => PlanOpenFile(normalized, context),
            UniversalIntentKind.DescribeForeground => PlanForegroundDescription(normalized, context),
            _ => null
        };
        if (explicitPlan is not null) return explicitPlan;

        if (!context.PreferInstalledApplication && context.ExplicitUserIntent is null or UniversalIntentKind.DesktopTask
            && DesktopTaskRequest.TryParse(normalized, out var desktopTask))
        {
            if (desktopTask!.UsesCurrentWindow && context.ForegroundApplication is null)
                return NeedsContext(normalized, UniversalIntentKind.DesktopTask,
                    "请切换到要操作的软件，再继续这次任务。", "前台应用");
            return Ready(normalized, UniversalIntentKind.DesktopTask,
                "准备在你指定的软件中分步完成任务；读取窗口内容前会显示本次授权。",
                desktopTask.ApplicationName, "desktop.task.step", false);
        }

        // Only the Host can set this after resolving a complete installed application name.
        // A registered name such as “微信搜索助手” must win over splitting on 搜索.
        if (context.PreferInstalledApplication && TryParseApplication(normalized, out var completeApplicationName))
        {
            if (ContainsUnsafeApplicationRequestSyntax(completeApplicationName))
                return Unsupported(normalized, "应用名称包含路径、命令或提权表达，因此没有创建操作计划。");
            return Ready(normalized, UniversalIntentKind.OpenApplication,
                $"打开“{completeApplicationName}”", completeApplicationName, "desktop.application.open", true,
                $"确认打开“{completeApplicationName}”。");
        }

        // Explicit file targets can themselves contain 搜索. Do not reinterpret a file
        // name as an application plus a search request.
        if (namedDesktopFile)
            return desktopFileRequest!.Mode == DesktopFileRequestMode.Find
                ? PlanFindDesktopFile(originalText.Trim(), desktopFileRequest.Name)
                : PlanOpenFile(normalized, context);
        if (Regex.IsMatch(normalized, "^(?:请帮我|帮我|给我|请)?(?:打开|查看)(?:这个|指定的|所选的)?文件", RegexOptions.CultureInvariant))
            return PlanOpenFile(normalized, context);

        if (!SearchRegex().IsMatch(normalized)
            && ApplicationSearchRequest.TryParse(normalized, out var applicationSearch, out var searchError))
        {
            if (applicationSearch is null || searchError is not null)
                return Unsupported(normalized, searchError ?? "请明确搜索的软件和内容。");
            if (ContainsUnsafeApplicationRequestSyntax(applicationSearch.ApplicationName))
                return Unsupported(normalized, "应用名称包含路径、命令或提权表达，因此没有创建操作计划。");
            var followUpText = applicationSearch.FollowUp switch
            {
                "OpenFirst" => "，并打开第一条可确认的媒体结果",
                "PlayFirst" when applicationSearch.MusicChart == "NetEaseHotChart" => "，并播放同一首已核实的榜首歌曲",
                "PlayFirst" => "，并播放第一首可确认的媒体结果",
                _ => string.Empty
            };
            var applicationSearchText = applicationSearch.MusicChart == "NetEaseHotChart"
                ? "按当前网易云热歌榜选择榜首歌曲，并搜索已核实的具体曲目"
                : applicationSearch.LatestMusicArtist is { } artist
                    ? $"按发行时间查找“{artist}”最新发布的歌曲，并搜索有日期依据的结果"
                    : $"搜索“{applicationSearch.Query}”";
            var summary = $"打开“{applicationSearch.ApplicationName}”并{applicationSearchText}{followUpText}";
            return Ready(normalized, UniversalIntentKind.ApplicationSearch, summary,
                applicationSearch.ApplicationName, "desktop.application.search", true,
                $"确认{summary}。") with { ApplicationSearch = applicationSearch };
        }

        // Parse the search envelope before inspecting its query for words such as
        // 修改代码 or 打开文件. Quoted search text never grants a second action.
        var namedSearch = SearchRegex().IsMatch(normalized)
            ? Match.Empty : ApplicationSearchRequest.MatchNamedSearch(normalized);
        var requestedApplication = namedSearch.Success ? namedSearch.Groups["app"].Value.Trim() : null;
        var searchText = namedSearch.Success ? "搜索" + namedSearch.Groups["query"].Value : normalized;
        if (TryParseForegroundSearch(searchText, out var query))
        {
            if (context.ForegroundApplication is null
                || requestedApplication is not null && !MatchesForegroundApplication(requestedApplication, context.ForegroundApplication))
            {
                return NeedsContext(normalized, UniversalIntentKind.SearchForeground,
                    requestedApplication is null ? "没有识别到你刚才使用的软件，请先切回目标软件。"
                        : $"请先切到“{requestedApplication}”的窗口，再继续这次搜索；不会在其他软件里输入。", "前台应用");
            }

            return Ready(normalized, UniversalIntentKind.SearchForeground,
                $"在“{context.ForegroundApplication.WindowTitle}”的可靠搜索框中搜索“{query}”",
                query, "desktop.search.submit", true,
                $"确认只在“{context.ForegroundApplication.WindowTitle}”中可靠识别的搜索框输入并提交“{query}”。");
        }

        if (LooksLikeForegroundSearch(normalized) || namedSearch.Success)
        {
            return Unsupported(normalized,
                "请提供 1 到 200 个字符的搜索内容，不要包含控制字符或附加操作；需要按原文搜索时请使用完整引号。");
        }

        if (IsCodingRequest(normalized))
        {
            return PlanCodingTask(normalized, context);
        }

        if (IsExplicitFileRequest(normalized) || DesktopFileRequest.TryGetName(normalized, out _))
        {
            return PlanOpenFile(normalized, context);
        }

        var localApplicationRequest = TryGetApplicationPreference(normalized, out var requestedApplicationName)
            && (context.PreferInstalledApplication || IsExplicitLocalApplicationName(requestedApplicationName));
        if (!localApplicationRequest && TryParseWebsite(normalized, out var website, out var websiteName))
        {
            if (TryParseBrowserHint(normalized, out var browserName))
            {
                return Ready(normalized, UniversalIntentKind.OpenWebsite,
                    $"用{browserName}打开{websiteName}，并把浏览器窗口显示在前台",
                    website, "browser.open.visible", true,
                    $"确认用{browserName}在新的可见窗口中打开{websiteName}。",
                    preferredApplicationName: browserName);
            }

            return Ready(normalized, UniversalIntentKind.OpenWebsite,
                $"用默认浏览器打开{websiteName}，并把浏览器窗口显示在前台",
                website, "browser.open", true,
                $"确认用默认浏览器打开 {websiteName}，并将窗口显示在前台。");
        }

        if (IsDescribeForegroundRequest(normalized))
        {
            return PlanForegroundDescription(normalized, context);
        }

        if (TryParseApplication(normalized, out var applicationName))
        {
            if (ContainsUnsafeApplicationRequestSyntax(applicationName))
            {
                return Unsupported(
                    normalized,
                    "应用名称包含路径、命令或提权表达，因此没有创建操作计划。");
            }

            return Ready(normalized, UniversalIntentKind.OpenApplication,
                $"打开“{applicationName}”", applicationName, "desktop.application.open", true,
                $"确认打开“{applicationName}”。");
        }

        // A speech segment may contain only the requested music, without the
        // action/application. Ask for that missing instruction rather than send
        // it to ordinary chat or infer permission from an earlier conversation.
        if (ApplicationSearchRequest.TryGetIncompleteSearchClarification(normalized, out var clarification))
            return Unsupported(normalized, clarification!);

        return Ready(normalized, UniversalIntentKind.Conversation,
            "回答你的问题，不操作电脑", normalized, "conversation.reply", false);
    }

    private IntentPlan Ready(
        string text,
        UniversalIntentKind kind,
        string summary,
        string? target,
        string capability,
        bool confirmation,
        string? confirmationText = null,
        string? preferredApplicationName = null) =>
        new(Guid.NewGuid(), kind, IntentPlanReadiness.Ready, text, summary, target,
            capability, confirmation, confirmationText,
            ExpiresAtUtc: timeProvider.GetUtcNow().AddMinutes(2),
            PreferredApplicationName: preferredApplicationName);

    private IntentPlan NeedsContext(
        string text,
        UniversalIntentKind kind,
        string summary,
        string missingContext) =>
        new(Guid.NewGuid(), kind, IntentPlanReadiness.NeedsContext, text, summary,
            MissingContext: missingContext,
            ExpiresAtUtc: timeProvider.GetUtcNow().AddMinutes(2));

    private IntentPlan Unsupported(string text, string summary) =>
        new(Guid.NewGuid(), UniversalIntentKind.Unsupported, IntentPlanReadiness.Unsupported,
            text, summary, ExpiresAtUtc: timeProvider.GetUtcNow().AddMinutes(2));

    private IntentPlan PlanCodingTask(string text, IntentPlanningContext context) =>
        context.SelectedProjectId is null
            ? NeedsContext(text, UniversalIntentKind.CodingTask,
                "这是一个编程任务，请先选择已授权项目。", "已授权项目")
            : Ready(text, UniversalIntentKind.CodingTask,
                $"让编程助手在“{context.SelectedProjectName ?? "所选项目"}”中处理这个任务",
                context.SelectedProjectId.Value.ToString("D"), "coding.execute", true,
                $"确认后，编程助手只会在“{context.SelectedProjectName ?? "所选项目"}”的授权范围内执行。 ");

    private IntentPlan PlanOpenFile(string text, IntentPlanningContext context)
    {
        if (context.SelectedFilePath is null)
        {
            return NeedsContext(
                text,
                UniversalIntentKind.OpenFile,
                "请先选择这一次要处理的文件。",
                "文件");
        }

        var fileName = Path.GetFileName(context.SelectedFilePath);
        return Ready(text, UniversalIntentKind.OpenFile,
            $"用默认应用打开“{fileName}”", context.SelectedFilePath,
            "file.open", true, $"确认打开你刚刚选择的文件“{fileName}”。");
    }

    private IntentPlan PlanFindDesktopFile(string text, string name) =>
        new(Guid.NewGuid(), UniversalIntentKind.FindDesktopFile, IntentPlanReadiness.Ready,
            text, $"查找桌面上名称匹配“{name}”的文件，只列出结果，不打开文件。", name,
            ExpiresAtUtc: timeProvider.GetUtcNow().AddMinutes(2));

    private IntentPlan PlanForegroundDescription(string text, IntentPlanningContext context)
    {
        if (context.ForegroundApplication is null)
        {
            return NeedsContext(text, UniversalIntentKind.DescribeForeground,
                "没有识别到你正在询问的窗口。", "前台窗口");
        }

        if (!context.ForegroundObservationConsent)
        {
            return NeedsContext(text, UniversalIntentKind.DescribeForeground,
                "读取当前窗口前，需要你明确同意本次查看。", "本次窗口查看同意");
        }

        return Ready(text, UniversalIntentKind.DescribeForeground,
            $"查看并识别“{context.ForegroundApplication.WindowTitle}”这个窗口",
            context.ForegroundApplication.WindowHandle.ToString(), "desktop.window.describe", true,
            $"确认仅查看“{context.ForegroundApplication.WindowTitle}”这个窗口。画面只在本机内存中识别，不会截取整个桌面、不会保存截图，也不会发送到云端。 ");
    }

    private static string Normalize(string? text) =>
        Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ");

    private static bool IsCodingRequest(string text) =>
        (text.Contains("代码", StringComparison.OrdinalIgnoreCase)
         || text.Contains("项目", StringComparison.OrdinalIgnoreCase)
         || text.Contains("bug", StringComparison.OrdinalIgnoreCase)
         || text.Contains("测试", StringComparison.OrdinalIgnoreCase)
         || text.Contains("编译", StringComparison.OrdinalIgnoreCase))
        && (text.Contains("修改", StringComparison.OrdinalIgnoreCase)
            || text.Contains("修复", StringComparison.OrdinalIgnoreCase)
            || text.Contains("检查", StringComparison.OrdinalIgnoreCase)
            || text.Contains("运行", StringComparison.OrdinalIgnoreCase)
            || text.Contains("重构", StringComparison.OrdinalIgnoreCase));

    private static bool IsOpenFileRequest(string text) =>
        text.Contains("打开", StringComparison.OrdinalIgnoreCase)
        || text.Contains("查看", StringComparison.OrdinalIgnoreCase);

    private static bool IsExplicitFileRequest(string text) =>
        text.Contains("文件", StringComparison.OrdinalIgnoreCase)
        && IsOpenFileRequest(text);

    private static bool IsDescribeForegroundRequest(string text) =>
        (text.Contains("这个窗口", StringComparison.Ordinal)
         || text.Contains("这个页面", StringComparison.Ordinal)
         || text.Contains("当前窗口", StringComparison.Ordinal)
         || text.Contains("现在打开", StringComparison.Ordinal)
         || text.Contains("现在的界面", StringComparison.Ordinal)
         || text.Contains("当前界面", StringComparison.Ordinal)
         || text.Contains("屏幕上", StringComparison.Ordinal))
        && (text.Contains("看看", StringComparison.Ordinal)
            || text.Contains("看一下", StringComparison.Ordinal)
            || text.Contains("是什么", StringComparison.Ordinal)
            || text.Contains("告诉我", StringComparison.Ordinal)
            || text.Contains("读取", StringComparison.Ordinal)
            || text.Contains("识别", StringComparison.Ordinal));

    private static bool TryParseWebsite(string text, out string website, out string displayName)
    {
        website = string.Empty;
        displayName = string.Empty;
        var match = UrlRegex().Match(text);
        if (match.Success
            && Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            website = uri.AbsoluteUri;
            displayName = uri.Host;
            return text.Contains("打开", StringComparison.Ordinal)
                   || text.Contains("访问", StringComparison.Ordinal)
                   || text.Equals(match.Value, StringComparison.OrdinalIgnoreCase);
        }

        var named = NamedWebsiteRequestRegex().Match(text);
        if (!named.Success) return false;
        var name = named.Groups["name"].Value.Trim();
        foreach (var suffix in new[] { "的官方网站", "官方网站", "的官网", "网页版", "网站", "网页", "官网" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length].Trim();
                if (name.StartsWith("我的", StringComparison.Ordinal)) name = name[2..].Trim();
                break;
            }
        }
        if (KnownWebsites.TryGetValue(name, out var knownWebsite))
        {
            website = knownWebsite;
            displayName = name;
            return true;
        }
        return false;
    }

    // Host may resolve this name against the trusted local catalog before choosing
    // a named website. This is a parsing hint, never target identity or authority.
    public static bool TryGetApplicationPreference(string text, out string applicationName)
    {
        applicationName = string.Empty;
        var normalized = ApplicationSearchRequest.NormalizeSpokenActionLeadIn(Normalize(text));
        return !UrlRegex().IsMatch(normalized)
            && !new[] { "网站", "网页", "官网", "网址", "浏览器" }.Any(word => normalized.Contains(word, StringComparison.Ordinal))
            && TryParseApplication(normalized, out applicationName)
            && !ContainsUnsafeApplicationRequestSyntax(applicationName);
    }

    private static bool IsExplicitLocalApplicationName(string name) =>
        new[] { "我的", "我电脑", "电脑上", "电脑里" }.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))
        || new[] { "软件", "应用", "客户端" }.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));

    private static bool TryParseApplication(string text, out string applicationName)
    {
        applicationName = string.Empty;
        var match = OpenApplicationRegex().Match(text);
        if (!match.Success) match = ReversedOpenApplicationRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var value = match.Groups[1].Value.Trim(' ', '。', '！', '!', '？', '?', '"');
        if (value.Length is < 1 or > 80 || value.Contains("文件", StringComparison.Ordinal))
        {
            return false;
        }

        applicationName = value;
        return true;
    }

    private static bool TryParseBrowserHint(string text, out string browserName)
    {
        if (text.Contains("谷歌浏览器", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
        {
            browserName = "Google Chrome";
            return true;
        }

        if (text.Contains("微软浏览器", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Edge", StringComparison.OrdinalIgnoreCase))
        {
            browserName = "Microsoft Edge";
            return true;
        }

        browserName = string.Empty;
        return false;
    }

    private static bool ContainsUnsafeApplicationRequestSyntax(string applicationName) =>
        applicationName.IndexOfAny(['\\', '/', ':']) >= 0
        || applicationName.IndexOfAny(['|', '&', ';', '<', '>', '`']) >= 0
        || UnsafeExecutableOrScriptReferenceRegex().IsMatch(applicationName)
        || CommandArgumentRegex().IsMatch(applicationName)
        || UnsafeApplicationRequestTokens.Any(token =>
            applicationName.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseForegroundSearch(string text, out string query)
    {
        query = string.Empty;
        var match = SearchRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        return ApplicationSearchRequest.TryParseQuery(match.Groups["query"].Value,
            allowFollowUp: false, out query, out _, out _);
    }

    private static bool ContainsDisallowedControl(string text) =>
        text.Any(char.IsControl);

    private static string RemoveControlsForClassification(string text) =>
        new(text.Where(character => !char.IsControl(character)).ToArray());

    private static bool LooksLikeForegroundSearch(string text) =>
        SearchRegex().IsMatch(text) || ApplicationSearchRequest.MatchNamedSearch(text).Success;

    private static bool MatchesForegroundApplication(string requested, ForegroundApplicationContext foreground)
    {
        if (requested is "" or "当前窗口" or "这个窗口" or "当前软件" or "这个软件" or "当前应用" or "这个应用"
            or "当前页面" or "这个页面" or "浏览器" or "当前浏览器") return true;
        return string.Equals(requested, foreground.ProcessName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested, foreground.WindowTitle, StringComparison.OrdinalIgnoreCase)
            || (foreground.ApplicationNames?.Any(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private static bool IsCompoundOpenAndSearch(string text) =>
        (text.Contains("打开", StringComparison.Ordinal)
         || text.Contains("启动", StringComparison.Ordinal)
         || text.Contains("运行", StringComparison.Ordinal))
        && (text.Contains("搜索", StringComparison.Ordinal)
            || text.Contains("查找", StringComparison.Ordinal)
            || text.Contains("搜一下", StringComparison.Ordinal));

    [GeneratedRegex(@"https://[^\s，。！？]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^(?:请帮我|帮我|给我|麻烦你|请)?\s*(?:(?:用|使用|在)\s*(?:Google Chrome|Chrome|谷歌浏览器|谷歌|Microsoft Edge|Edge|微软浏览器|默认浏览器|浏览器)(?:里面|里|中)?\s*)?(?:打开|访问|进入)(?:一下)?\s*[“"" ]?(?<name>.+?)[”""]?[。！!？?]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedWebsiteRequestRegex();

    [GeneratedRegex(@"^(?:(?:请帮我|帮我|给我|麻烦你|麻烦|请)\s*){0,3}(?:打开|启动|运行)(?:一下)?\s*“?(.+?)”?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenApplicationRegex();

    [GeneratedRegex(@"^(?:请帮我|帮我|给我|麻烦你|麻烦|请)?把\s*“?(.+?)”?\s*(?:打开|启动)(?:一下)?[。！!？?]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReversedOpenApplicationRegex();

    [GeneratedRegex(@"(?:^|[\s\""'“”])[^\s\""'“”]*\.(?:exe|com|cmd|bat|ps1|psm1|vbs|vbe|js|jse|wsf|wsh|msc|msi|msix|scr|dll|cpl|lnk|url)(?:$|[\s\""'“”])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeExecutableOrScriptReferenceRegex();

    [GeneratedRegex(@"(?:^|\s)(?:--?[A-Za-z0-9?]|/[A-Za-z0-9?])",
        RegexOptions.CultureInvariant)]
    private static partial Regex CommandArgumentRegex();

    // A field after a window name needs a following search verb. Otherwise
    // “在当前窗口搜索框架设计” must retain 框架设计 as the query.
    [GeneratedRegex(@"^(?:请帮我|帮我|请|给我|麻烦你|麻烦)?\s*(?:在\s*(?:(?:当前|这个|刚才的)?(?:窗口|软件|应用|页面|浏览器)(?:(?:的\s*)?"
        + ApplicationSearchRequest.SearchFieldPattern + @"(?=(?:里面|里|中|内)?\s*(?:请帮我|帮我|给我)?"
        + ApplicationSearchRequest.SearchVerbPattern + @"))?|" + ApplicationSearchRequest.SearchFieldPattern
        + @")(?:里面|里|中|内)?\s*)?(?:请帮我|帮我|给我)?" + ApplicationSearchRequest.SearchVerbPattern + @"\s*(?<query>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchRegex();
}
