using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ScreenGuide.Skills.Windows;

internal enum ApplicationRegistrationSource
{
    AppsFolder,
    Shortcut,
    AppPath,
    Platform
}

internal sealed record ApplicationRegistration(
    string DisplayName,
    string Target,
    ApplicationRegistrationSource Source,
    string? Arguments = null,
    bool RequiresElevation = false,
    string? PreferredId = null);

internal sealed record ShortcutResolution(
    string Target,
    string? Arguments = null,
    bool RequiresElevation = false);

public static class InstalledApplicationErrorCodes
{
    public const string NotFound = "application_not_found";
    public const string Ambiguous = "application_ambiguous";
    public const string TargetNotAllowed = "application_target_not_allowed";
    public const string TargetChanged = "application_target_changed";
}

public sealed class InstalledApplicationResolutionException(
    string code,
    string message,
    IReadOnlyList<string>? suggestedNames = null) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<string> SuggestedNames { get; } = suggestedNames ?? [];
}

public interface IInstalledApplicationCatalog
{
    IReadOnlyList<string> GetNamesForWindow(ForegroundWindowSnapshot window) => [];
    IReadOnlyList<KnownDesktopApplication> GetApplications();

    KnownDesktopApplication? FindById(string id);

    KnownDesktopApplication? FindByDisplayName(string displayName);

    KnownDesktopApplication ResolveByDisplayName(string displayName) =>
        FindByDisplayName(displayName)
        ?? throw new InstalledApplicationResolutionException(
            InstalledApplicationErrorCodes.NotFound,
            "没有找到可安全打开的已安装应用，请检查名称后重试。");

    KnownDesktopApplication ResolveApplicationPreference(string displayName) =>
        ResolveByDisplayName(displayName);

    KnownDesktopApplication? FindBrowser(string browserName);
}

/// <summary>
/// Reads only Windows' explicit application registration locations. It never scans the disk.
/// Shortcut and App Paths entries are resolved and validated before they enter the catalog.
/// </summary>
public sealed class InstalledApplicationCatalog : IInstalledApplicationCatalog
{
    public IReadOnlyList<string> GetNamesForWindow(ForegroundWindowSnapshot window)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(window.ProcessId);
            if (new DateTimeOffset(process.StartTime.ToUniversalTime()) != window.ProcessStartTimeUtc) return [];
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) return [];
            return GetApplications().Where(app => string.Equals(app.LaunchTarget, path, StringComparison.OrdinalIgnoreCase))
                .SelectMany(app => new[] { app.DisplayName }.Concat(app.RegisteredNames ?? []))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return []; }
    }

    private static readonly KnownDesktopApplication[] SafeSettings =
    [
        Settings("windows-settings", "Windows 设置", "ms-settings:",
            "设置", "设置主页", "Settings", "Settings home"),
        Settings("windows-settings-display", "显示设置", "ms-settings:display",
            "显示", "Display settings"),
        Settings("windows-settings-sound", "声音设置", "ms-settings:sound",
            "声音", "Sound settings"),
        Settings("windows-settings-bluetooth", "蓝牙和设备", "ms-settings:bluetooth",
            "蓝牙设置", "设备设置", "Bluetooth and devices", "Bluetooth & devices"),
        Settings("windows-settings-network", "网络状态", "ms-settings:network-status",
            "网络设置", "网络和Internet", "Network status"),
        Settings("windows-settings-apps", "已安装的应用", "ms-settings:appsfeatures",
            "应用和功能", "Installed apps"),
        Settings("windows-settings-storage", "存储设置", "ms-settings:storagesense",
            "存储", "Storage"),
        Settings("windows-settings-about", "系统信息", "ms-settings:about",
            "关于系统", "System information")
    ];

    private static readonly IReadOnlySet<string> SafeSettingsTargets =
        SafeSettings.Select(item => item.LaunchTarget)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] BlockedSettingsTokens =
    [
        "账户设置", "帐户设置", "登录选项", "登录设置", "Windows安全", "安全中心",
        "恢复设置", "系统恢复", "激活设置", "开发者设置", "开发人员设置",
        "Windows更新", "系统更新", "Account settings", "Sign-in options",
        "Windows Security", "Recovery settings", "Activation settings",
        "Developer settings", "Windows Update"
    ];

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

    private static readonly string[] UnsafeApplicationReferenceExtensions =
    [
        ".exe", ".com", ".cmd", ".bat", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".msc", ".msi", ".msix", ".scr",
        ".dll", ".cpl", ".lnk", ".url"
    ];

    private static readonly IReadOnlyDictionary<string, ApplicationAliasRule> SafeAliases =
        new Dictionary<string, ApplicationAliasRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["谷歌浏览器"] = new(["GoogleChrome"], ["chrome.exe"]),
            ["Chrome"] = new(["GoogleChrome"], ["chrome.exe"]),
            ["微软浏览器"] = new(["MicrosoftEdge"], ["msedge.exe"]),
            ["Edge"] = new(["MicrosoftEdge"], ["msedge.exe"]),
            ["夸克"] = new(["夸克浏览器", "Quark"], ["quark.exe"]),
            ["夸克浏览器"] = new(["夸克浏览器", "Quark"], ["quark.exe"]),
            ["剪映"] = new(["剪映专业版"], ["jianying.exe", "JianyingPro.exe"]),
            ["网易云"] = new(["网易云音乐"], ["cloudmusic.exe"]),
            ["斯蒂姆"] = new(["Steam"], ["steam.exe"])
        };

    private readonly object _sync = new();
    private readonly Func<IReadOnlyList<ApplicationRegistration>> _discover;
    private readonly Func<string, ShortcutResolution?> _resolveShortcut;
    private IReadOnlyList<KnownDesktopApplication>? _applications;

    public InstalledApplicationCatalog()
        : this(DiscoverRegistrations, ResolveShortcut)
    {
    }

    internal InstalledApplicationCatalog(
        Func<IReadOnlyList<ApplicationRegistration>> discover,
        Func<string, ShortcutResolution?>? resolveShortcut = null)
    {
        ArgumentNullException.ThrowIfNull(discover);
        _discover = discover;
        _resolveShortcut = resolveShortcut ?? ResolveShortcut;
    }

    public IReadOnlyList<KnownDesktopApplication> GetApplications() =>
        GetApplications(out _);

    private IReadOnlyList<KnownDesktopApplication> GetApplications(out bool discoveredNow)
    {
        // A concurrent list request may finish discovery while this caller waits for the lock.
        // Compare the published immutable snapshots so that completed work is still fresh for
        // this request. A failed build never publishes a new snapshot or satisfies this check.
        var observedSnapshot = Volatile.Read(ref _applications);
        lock (_sync)
        {
            var applications = _applications ??= BuildApplications(_discover(), _resolveShortcut);
            discoveredNow = !ReferenceEquals(observedSnapshot, applications);
            return applications;
        }
    }

    public KnownDesktopApplication? FindById(string id)
    {
        var normalized = id?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var application = GetApplications().FirstOrDefault(item =>
            string.Equals(item.Id, normalized, StringComparison.Ordinal));
        if (application is not null)
        {
            return application;
        }

        var refreshed = Refresh();
        return refreshed.FirstOrDefault(item =>
            string.Equals(item.Id, normalized, StringComparison.Ordinal));
    }

    public KnownDesktopApplication? FindByDisplayName(string displayName)
    {
        try
        {
            return ResolveByDisplayName(displayName);
        }
        catch (InstalledApplicationResolutionException)
        {
            return null;
        }
    }

    public KnownDesktopApplication ResolveByDisplayName(string displayName) =>
        ResolveByDisplayName(displayName, refreshNewSnapshot: true);

    public KnownDesktopApplication ResolveApplicationPreference(string displayName) =>
        ResolveByDisplayName(displayName, refreshNewSnapshot: false);

    private KnownDesktopApplication ResolveByDisplayName(string displayName, bool refreshNewSnapshot)
    {
        if (ContainsUnsafeApplicationRequestSyntax(displayName))
        {
            throw new InstalledApplicationResolutionException(
                InstalledApplicationErrorCodes.TargetNotAllowed,
                "应用名称包含路径、命令、可执行文件或提权表达，本次没有打开任何程序。");
        }

        var requested = NormalizeDisplayName(displayName);
        if (requested.Length == 0)
        {
            throw NotFound();
        }

        if (BlockedSettingsTokens.Any(token =>
                requested.Contains(NormalizeDisplayName(token), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InstalledApplicationResolutionException(
                InstalledApplicationErrorCodes.TargetNotAllowed,
                "这个 Windows 设置页面不在当前语音直达白名单中，请在设置中手动打开。");
        }

        var discoveredNow = false;
        try
        {
            return ResolveCore(GetApplications(out discoveredNow), requested);
        }
        catch (InstalledApplicationResolutionException exception)
            when (exception.Code == InstalledApplicationErrorCodes.NotFound
                  && (refreshNewSnapshot || !discoveredNow))
        {
            // A Start Menu shortcut or App Paths registration may have changed since startup.
            // Refresh exactly once for this command path; ambiguity and policy denial never retry.
            // A whole-name planning preference already read in a newly built snapshot needs no
            // second discovery. An older snapshot still refreshes so a newly installed literal
            // application name cannot be mistaken for an app plus a search instruction.
            return ResolveCore(Refresh(), requested);
        }
    }

    internal static KnownDesktopApplication? SelectUniqueApplication(
        IReadOnlyList<KnownDesktopApplication> exact)
    {
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var distinctTargets = exact
            .GroupBy(item => CanonicalTarget(item.LaunchTarget), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (distinctTargets.Length == 1)
        {
            return distinctTargets[0];
        }

        // Preserve the legacy safety behavior for callers that hand this helper a raw
        // executable plus its unresolved shortcut registration.
        var executables = distinctTargets.Where(item =>
            item.LaunchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && Path.IsPathFullyQualified(item.LaunchTarget)
            && File.Exists(item.LaunchTarget)).ToArray();
        return executables.Length == 1 ? executables[0] : null;
    }

    public KnownDesktopApplication? FindBrowser(string browserName)
    {
        try
        {
            var application = ResolveByDisplayName(browserName);
            var executable = Path.GetFileName(application.LaunchTarget);
            return executable.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)
                   || executable.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
                ? application
                : null;
        }
        catch (InstalledApplicationResolutionException)
        {
            return null;
        }
    }

    public static bool IsAllowedLaunchTarget(KnownDesktopApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (SafeSettingsTargets.Contains(application.LaunchTarget))
        {
            return application.Id.StartsWith("windows-settings", StringComparison.Ordinal);
        }

        if (TryNormalizeAppsFolderTarget(application.LaunchTarget, out _))
        {
            return true;
        }

        return TryNormalizeExecutableTarget(application.LaunchTarget, out _);
    }

    public static string CreateTargetBinding(KnownDesktopApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var canonical = CanonicalTarget(application.LaunchTarget);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{application.Id}\n{canonical}"));
        return Convert.ToHexString(hash);
    }

    public static bool TargetBindingMatches(
        KnownDesktopApplication application,
        string? expectedBinding)
    {
        if (string.IsNullOrWhiteSpace(expectedBinding) || expectedBinding.Length != 64)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromHexString(expectedBinding);
            var current = Convert.FromHexString(CreateTargetBinding(application));
            return CryptographicOperations.FixedTimeEquals(expected, current);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private IReadOnlyList<KnownDesktopApplication> Refresh()
    {
        lock (_sync)
        {
            _applications = BuildApplications(_discover(), _resolveShortcut);
            return _applications;
        }
    }

    private static KnownDesktopApplication ResolveCore(
        IReadOnlyList<KnownDesktopApplication> applications,
        string requested)
    {
        var exact = MatchByName(applications, requested, static (name, query) =>
            string.Equals(name, query, StringComparison.OrdinalIgnoreCase));
        if (exact.Length > 0)
        {
            return RequireUnique(exact);
        }

        // Only remove a leading conversational location/possessive after checking
        // literal registered names. Never extract a name from arbitrary surrounding text.
        // Longer desktop qualifiers precede "我的" so the remaining name is complete.
        foreach (var qualifier in new[]
                 {
                     "我的电脑桌面上的", "我电脑桌面上的", "电脑桌面上的",
                     "我见到桌面上的", "我看到桌面上的", "我看见桌面上的",
                     "我的桌面上的", "我桌面上的", "桌面上的",
                     "我的桌面的", "我桌面的", "桌面的", "桌面上",
                     "我电脑上的", "我电脑里的", "我的电脑上的", "我的电脑里的",
                     "电脑上的", "电脑里的", "我的"
                 })
        {
            if (requested.StartsWith(qualifier, StringComparison.Ordinal) && requested.Length > qualifier.Length)
            {
                requested = requested[qualifier.Length..].Trim();
                var unqualifiedExact = MatchByName(applications, requested, static (name, query) =>
                    string.Equals(name, query, StringComparison.OrdinalIgnoreCase));
                if (unqualifiedExact.Length > 0) return RequireUnique(unqualifiedExact);
                break;
            }
        }

        foreach (var suffix in new[] { "这个软件", "这个应用", "客户端", "软件", "应用" })
        {
            if (requested.EndsWith(suffix, StringComparison.Ordinal) && requested.Length > suffix.Length)
            {
                requested = requested[..^suffix.Length];
                var unqualifiedExact = MatchByName(applications, requested, static (name, query) =>
                    string.Equals(name, query, StringComparison.OrdinalIgnoreCase));
                if (unqualifiedExact.Length > 0) return RequireUnique(unqualifiedExact);
                break;
            }
        }

        if (SafeAliases.TryGetValue(requested, out var alias))
        {
            var aliased = applications.Where(item => alias.Matches(item)).ToArray();
            if (aliased.Length > 0)
            {
                return RequireUnique(aliased);
            }
        }

        // Settings pages are reachable only through their exact fixed names/aliases above.
        var ordinaryApplications = applications.Where(item =>
            !item.Id.StartsWith("windows-settings", StringComparison.Ordinal)).ToArray();
        var prefix = ordinaryApplications.Where(item => ApplicationNames(item)
            .Any(name => MatchesPartialName(name, requested, prefixOnly: true))).ToArray();
        if (prefix.Length > 0)
        {
            return RequireUnique(prefix);
        }

        if (requested.Length >= 2)
        {
            var contains = ordinaryApplications.Where(item => ApplicationNames(item)
                .Any(name => MatchesPartialName(name, requested, prefixOnly: false))).ToArray();
            if (contains.Length > 0)
            {
                return RequireUnique(contains);
            }
        }

        // Suggestions are explanation only: never return a guessed application,
        // bind its ID, or treat speech similarity as permission to execute it.
        var suggestions = requested.Length is >= 4 and <= 80 && requested.All(char.IsAsciiLetterOrDigit)
            ? ordinaryApplications.Where(item => ApplicationNames(item)
                    .Any(name => IsOneEditApart(requested, NormalizeDisplayName(name))))
                .Select(item => item.DisplayName)
                .Where(name => name.Length is > 0 and <= 80 && !name.Any(char.IsControl))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(3).ToArray()
            : [];
        throw suggestions.Length == 0 ? NotFound() : new InstalledApplicationResolutionException(
            InstalledApplicationErrorCodes.NotFound,
            $"没有找到名称准确匹配的应用。你可能想打开：{string.Join("、", suggestions)}。"
            + "请按完整名称重新说打开指令；本次没有打开任何程序。", suggestions);
    }

    private static bool MatchesPartialName(string name, string requested, bool prefixOnly)
    {
        var raw = name.Trim().Normalize(NormalizationForm.FormKC);
        var normalized = new StringBuilder(raw.Length);
        var positions = new List<int>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (!IsNameCharacter(raw[i])) continue;
            normalized.Append(raw[i]);
            positions.Add(i);
        }
        var candidate = normalized.ToString();
        for (var start = candidate.IndexOf(requested, StringComparison.OrdinalIgnoreCase);
             start >= 0;
             start = candidate.IndexOf(requested, start + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (prefixOnly && start != 0) return false;
            var first = positions[start];
            var last = positions[start + requested.Length - 1];
            // Preserve boundaries before normalizing punctuation/spaces away.
            // "Chrome" can match "Google Chrome"; "STEM" cannot match "System".
            if (char.IsAsciiLetterOrDigit(requested[0]) && first > 0
                && char.IsAsciiLetterOrDigit(raw[first - 1])) continue;
            if (char.IsAsciiLetterOrDigit(requested[^1]) && last + 1 < raw.Length
                && char.IsAsciiLetterOrDigit(raw[last + 1])) continue;
            return true;
        }
        return false;
    }

    private static bool IsOneEditApart(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1 || !right.All(char.IsAsciiLetterOrDigit)) return false;
        var i = 0;
        var j = 0;
        var edits = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.ToUpperInvariant(left[i]) == char.ToUpperInvariant(right[j])) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (left.Length >= right.Length) i++;
            if (right.Length >= left.Length) j++;
        }
        return edits + left.Length - i + right.Length - j == 1;
    }

    private static KnownDesktopApplication[] MatchByName(
        IEnumerable<KnownDesktopApplication> applications,
        string requested,
        Func<string, string, bool> predicate) =>
        applications.Where(item => ApplicationNames(item)
                .Any(name => predicate(NormalizeDisplayName(name), requested)))
            .ToArray();

    private static IEnumerable<string> ApplicationNames(KnownDesktopApplication application) =>
        application.RegisteredNames is { Count: > 0 }
            ? application.RegisteredNames
            : [application.DisplayName];

    private static KnownDesktopApplication RequireUnique(
        IReadOnlyList<KnownDesktopApplication> applications)
    {
        var distinct = applications
            .GroupBy(item => CanonicalTarget(item.LaunchTarget), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return distinct.Length switch
        {
            1 => distinct[0],
            > 1 => throw new InstalledApplicationResolutionException(
                InstalledApplicationErrorCodes.Ambiguous,
                "识别到多个不同程序，请说出更完整的应用名称；本次没有打开任何程序。"),
            _ => throw NotFound()
        };
    }

    private static InstalledApplicationResolutionException NotFound() =>
        new(
            InstalledApplicationErrorCodes.NotFound,
            "没有找到可安全打开的已安装应用，请检查名称后重试。");

    private static IReadOnlyList<KnownDesktopApplication> BuildApplications(
        IReadOnlyList<ApplicationRegistration> registrations,
        Func<string, ShortcutResolution?> shortcutResolver)
    {
        var byTarget = new Dictionary<string, ApplicationBuilder>(StringComparer.OrdinalIgnoreCase);
        var byTrustedIdentity = new Dictionary<string, ApplicationBuilder>(StringComparer.Ordinal);
        foreach (var setting in SafeSettings)
        {
            AddResolved(
                byTarget,
                byTrustedIdentity,
                setting.Id,
                setting.DisplayName,
                setting.LaunchTarget,
                setting.RegisteredNames ?? [setting.DisplayName],
                trustedIdentity: setting.Id,
                preferIdentity: true);
        }

        foreach (var registration in registrations)
        {
            if (!TryResolveRegistration(registration, shortcutResolver, out var target))
            {
                continue;
            }

            var trustedIdentity = registration.PreferredId
                                  ?? TrustedBuiltInIdentity(target);
            AddResolved(
                byTarget,
                byTrustedIdentity,
                trustedIdentity ?? "installed-" + StableId(CanonicalTarget(target)),
                registration.DisplayName.Trim(),
                target,
                [registration.DisplayName.Trim()],
                trustedIdentity,
                preferIdentity: trustedIdentity is not null);
        }

        return byTarget.Values
            .Distinct()
            .Select(builder => builder.Build())
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryResolveRegistration(
        ApplicationRegistration registration,
        Func<string, ShortcutResolution?> shortcutResolver,
        out string target)
    {
        target = string.Empty;
        var displayName = registration.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length is < 1 or > 120 || IsUninstaller(displayName, registration.Target))
        {
            return false;
        }

        var resolved = registration.Source == ApplicationRegistrationSource.Shortcut
            ? shortcutResolver(registration.Target)
            : new ShortcutResolution(
                registration.Target,
                registration.Arguments,
                registration.RequiresElevation);
        if (resolved is null
            || resolved.RequiresElevation
            || !string.IsNullOrWhiteSpace(resolved.Arguments)
            || IsUninstaller(displayName, resolved.Target))
        {
            return false;
        }

        if ((registration.Source is ApplicationRegistrationSource.AppsFolder or
             ApplicationRegistrationSource.Shortcut) &&
            TryNormalizeAppsFolderRegistrationTarget(resolved.Target, out target))
        {
            return true;
        }

        if (registration.Source == ApplicationRegistrationSource.Platform
            && SafeSettingsTargets.Contains(resolved.Target))
        {
            target = SafeSettingsTargets.Single(item =>
                string.Equals(item, resolved.Target, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        return TryNormalizeExecutableTarget(resolved.Target, out target);
    }

    private static void AddResolved(
        IDictionary<string, ApplicationBuilder> applications,
        IDictionary<string, ApplicationBuilder> trustedApplications,
        string id,
        string displayName,
        string target,
        IReadOnlyList<string> registeredNames,
        string? trustedIdentity = null,
        bool preferIdentity = false)
    {
        var canonical = CanonicalTarget(target);
        if (!string.IsNullOrWhiteSpace(trustedIdentity)
            && trustedApplications.TryGetValue(trustedIdentity, out var trustedBuilder))
        {
            trustedBuilder.Merge(id, displayName, registeredNames, preferIdentity);
            applications[canonical] = trustedBuilder;
            return;
        }

        if (!applications.TryGetValue(canonical, out var builder))
        {
            builder = new ApplicationBuilder(
                id,
                displayName,
                target,
                registeredNames,
                preferIdentity);
            applications[canonical] = builder;
        }
        else
        {
            builder.Merge(id, displayName, registeredNames, preferIdentity);
        }

        if (!string.IsNullOrWhiteSpace(trustedIdentity))
        {
            trustedApplications[trustedIdentity] = builder;
        }
    }

    private static string? TrustedBuiltInIdentity(string target)
    {
        if (TryNormalizeAppsFolderTarget(target, out var appsFolder))
        {
            return TrustedPackagedApplicationIdentity(
                appsFolder["shell:AppsFolder\\".Length..]);
        }

        if (!Path.IsPathFullyQualified(target))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(target);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (IsExactWindowsExecutable(fullPath, "explorer.exe"))
        {
            return "file-explorer";
        }

        if (IsExactWindowsExecutable(fullPath, "notepad.exe"))
        {
            return "notepad";
        }

        if (IsExactWindowsExecutable(fullPath, "calc.exe"))
        {
            return "calculator";
        }

        if (IsExactWindowsExecutable(fullPath, "mspaint.exe"))
        {
            return "paint";
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return null;
        }

        var windowsApps = Path.Combine(programFiles, "WindowsApps");
        var relative = Path.GetRelativePath(windowsApps, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relative))
        {
            return null;
        }

        var packageDirectory = relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return TrustedPackagedApplicationIdentity(packageDirectory ?? string.Empty);
    }

    private static bool IsExactWindowsExecutable(string fullPath, string executableName)
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
        };
        return candidates.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path, executableName))
            .Any(path => string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TrustedPackagedApplicationIdentity(string packageIdentity)
    {
        if (packageIdentity.StartsWith(
                "Microsoft.WindowsNotepad_",
                StringComparison.OrdinalIgnoreCase))
        {
            return "notepad";
        }

        if (packageIdentity.StartsWith(
                "Microsoft.WindowsCalculator_",
                StringComparison.OrdinalIgnoreCase))
        {
            return "calculator";
        }

        if (packageIdentity.StartsWith("Microsoft.Paint_", StringComparison.OrdinalIgnoreCase)
            || packageIdentity.StartsWith(
                "Microsoft.MSPaint_",
                StringComparison.OrdinalIgnoreCase))
        {
            return "paint";
        }

        return null;
    }

    private static IReadOnlyList<ApplicationRegistration> DiscoverRegistrations()
    {
        var registrations = new List<ApplicationRegistration>();
        AddPlatformExecutable(registrations, "file-explorer", "文件资源管理器",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        AddPlatformExecutable(registrations, "notepad", "记事本",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"));
        AddPlatformExecutable(registrations, "calculator", "计算器",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "calc.exe"));
        AddPlatformExecutable(registrations, "paint", "画图",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mspaint.exe"));

        ReadAppsFolder(registrations);
        foreach (var (root, recursive) in ShortcutRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var shortcut in Directory.EnumerateFiles(
                             root,
                             "*.lnk",
                             new EnumerationOptions
                             {
                                 RecurseSubdirectories = recursive,
                                 IgnoreInaccessible = true,
                                 AttributesToSkip = FileAttributes.ReparsePoint
                             }))
                {
                    registrations.Add(new ApplicationRegistration(
                        Path.GetFileNameWithoutExtension(shortcut).Trim(),
                        shortcut,
                        ApplicationRegistrationSource.Shortcut));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // A protected Start Menu subtree must not break the safe catalog.
            }
            catch (IOException)
            {
                // A shortcut can disappear while Windows updates the Start Menu.
            }
        }

        ReadAppPaths(Registry.CurrentUser, registrations);
        ReadAppPaths(Registry.LocalMachine, registrations);
        return registrations;
    }

    private static void AddPlatformExecutable(
        ICollection<ApplicationRegistration> registrations,
        string id,
        string displayName,
        string target)
    {
        if (File.Exists(target))
        {
            registrations.Add(new ApplicationRegistration(
                displayName,
                target,
                ApplicationRegistrationSource.Platform,
                PreferredId: id));
        }
    }

    private static IEnumerable<(string Root, bool Recursive)> ShortcutRoots()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), true);
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), true);
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false);
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false);
    }

    private static void ReadAppsFolder(ICollection<ApplicationRegistration> registrations)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        object? shell = null;
        object? folder = null;
        object? items = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null || (shell = Activator.CreateInstance(shellType)) is null)
            {
                return;
            }

            dynamic shellObject = shell;
            folder = shellObject.NameSpace("shell:AppsFolder");
            if (folder is null)
            {
                return;
            }

            dynamic folderObject = folder;
            items = folderObject.Items();
            dynamic itemCollection = items;
            for (var index = 0; index < (int)itemCollection.Count; index++)
            {
                object? rawItem = null;
                try
                {
                    rawItem = itemCollection.Item(index);
                    if (rawItem is null)
                    {
                        continue;
                    }

                    dynamic item = rawItem;
                    var displayName = (item.Name as string)?.Trim();
                    var shellPath = (item.Path as string)?.Trim();
                    var executable = (item.ExtendedProperty(
                        "System.Link.TargetParsingPath") as string)?.Trim();
                    string? launchTarget;
                    if (TryNormalizeAppsFolderRegistrationTarget(
                            shellPath ?? string.Empty,
                            out var shellApplication))
                    {
                        launchTarget = shellApplication;
                    }
                    else if (TryNormalizeAppsFolderRegistrationTarget(
                                 executable ?? string.Empty,
                                 out var executableApplication))
                    {
                        launchTarget = executableApplication;
                    }
                    else
                    {
                        launchTarget = !string.IsNullOrWhiteSpace(executable)
                            ? executable
                            : shellPath;
                    }
                    if (!string.IsNullOrWhiteSpace(displayName)
                        && !string.IsNullOrWhiteSpace(launchTarget))
                    {
                        registrations.Add(new ApplicationRegistration(
                            displayName,
                            launchTarget,
                            ApplicationRegistrationSource.AppsFolder));
                    }
                }
                finally
                {
                    ReleaseComObject(rawItem);
                }
            }
        }
        catch (COMException)
        {
            // AppsFolder discovery is optional; shortcuts and App Paths remain available.
        }
        catch (UnauthorizedAccessException)
        {
            // Windows can restrict individual shell namespaces.
        }
        finally
        {
            ReleaseComObject(items);
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
    }

    private static void ReadAppPaths(
        RegistryKey hive,
        ICollection<ApplicationRegistration> registrations)
    {
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        try
        {
            using var root = hive.OpenSubKey(appPaths);
            if (root is null)
            {
                return;
            }

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                if (subKey?.GetValue(null) is not string target
                    || string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                registrations.Add(new ApplicationRegistration(
                    Path.GetFileNameWithoutExtension(subKeyName),
                    target,
                    ApplicationRegistrationSource.AppPath));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Registry discovery is optional; fixed settings and shortcuts remain available.
        }
        catch (System.Security.SecurityException)
        {
            // Registry discovery is optional.
        }
    }

    private static ShortcutResolution? ResolveShortcut(string shortcutPath)
    {
        if (!OperatingSystem.IsWindows()
            || !shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(shortcutPath)
            || IsReparsePoint(shortcutPath))
        {
            return null;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null || (shell = Activator.CreateInstance(shellType)) is null)
            {
                return null;
            }

            dynamic shellObject = shell;
            shortcut = shellObject.CreateShortcut(shortcutPath);
            dynamic shortcutObject = shortcut;
            var target = (shortcutObject.TargetPath as string)?.Trim();
            var arguments = (shortcutObject.Arguments as string)?.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            if (string.Equals(Path.GetFileName(target), "explorer.exe",
                    StringComparison.OrdinalIgnoreCase)
                && TryNormalizeAppsFolderRegistrationTarget(
                    arguments ?? string.Empty,
                    out var appsFolder))
            {
                return new ShortcutResolution(
                    appsFolder,
                    RequiresElevation: ShortcutRequiresElevation(shortcutPath));
            }

            return new ShortcutResolution(
                target,
                arguments,
                ShortcutRequiresElevation(shortcutPath));
        }
        catch (COMException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static bool ShortcutRequiresElevation(string shortcutPath)
    {
        const uint runAsUserFlag = 0x00002000;
        Span<byte> header = stackalloc byte[24];
        try
        {
            using var stream = File.OpenRead(shortcutPath);
            stream.ReadExactly(header);

            return (BitConverter.ToUInt32(header[20..24]) & runAsUserFlag) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryNormalizeExecutableTarget(string value, out string target)
    {
        target = string.Empty;
        var candidate = (value ?? string.Empty).Trim();
        if (candidate.Length is < 5 or > 1024
            || candidate.Contains('\0')
            || candidate.Contains('\r')
            || candidate.Contains('\n'))
        {
            return false;
        }

        if (candidate.StartsWith('"') && candidate.EndsWith('"') && candidate.Count(c => c == '"') == 2)
        {
            candidate = candidate[1..^1];
        }
        else if (candidate.Contains('"'))
        {
            return false;
        }

        if (!Path.IsPathFullyQualified(candidate)
            || candidate.StartsWith(@"\\", StringComparison.Ordinal)
            || !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!File.Exists(fullPath)
                || IsReparsePoint(fullPath)
                || !TryVerifyNonElevatedExecutable(fullPath))
            {
                return false;
            }

            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrWhiteSpace(root)
                && new DriveInfo(root).DriveType == DriveType.Network)
            {
                return false;
            }

            target = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryNormalizeAppsFolderTarget(string value, out string target)
    {
        target = string.Empty;
        const string prefix = "shell:AppsFolder\\";
        var candidate = (value ?? string.Empty).Trim().Trim('"');
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var aumid = candidate[prefix.Length..];
        if (aumid.Length is < 3 or > 256
            || aumid.Any(character =>
                !(char.IsLetterOrDigit(character)
                  || character is '.' or '_' or '-' or '!')))
        {
            return false;
        }

        target = prefix + aumid;
        return true;
    }

    private static bool TryNormalizeAppsFolderRegistrationTarget(
        string value,
        out string target)
    {
        if (TryNormalizeAppsFolderTarget(value, out target))
        {
            return true;
        }

        return TryNormalizeAppsFolderTarget($"shell:AppsFolder\\{(value ?? string.Empty).Trim()}", out target);
    }

    private static bool TryVerifyNonElevatedExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var sidecar = path + ".manifest";
        if (File.Exists(sidecar))
        {
            try
            {
                if (IsReparsePoint(sidecar)
                    || new FileInfo(sidecar).Length > MaximumManifestBytes
                    || ManifestRequestsElevation(File.ReadAllBytes(sidecar)))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        var module = LoadLibraryEx(path, IntPtr.Zero,
            LoadLibraryAsDataFile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            for (var resourceId = 1; resourceId <= 3; resourceId++)
            {
                var resource = FindResource(module, new IntPtr(resourceId), new IntPtr(RtManifest));
                if (resource == IntPtr.Zero)
                {
                    continue;
                }

                var size = SizeofResource(module, resource);
                if (size == 0 || size > MaximumManifestBytes)
                {
                    return false;
                }

                var loaded = LoadResource(module, resource);
                var pointer = loaded == IntPtr.Zero ? IntPtr.Zero : LockResource(loaded);
                if (pointer == IntPtr.Zero)
                {
                    return false;
                }

                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, (int)size);
                if (ManifestRequestsElevation(bytes))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            _ = FreeLibrary(module);
        }
    }

    private static bool ManifestRequestsElevation(byte[] bytes)
    {
        var utf8 = Encoding.UTF8.GetString(bytes);
        var unicode = Encoding.Unicode.GetString(bytes);
        return ContainsElevationLevel(utf8) || ContainsElevationLevel(unicode);
    }

    private static bool ContainsElevationLevel(string manifest) =>
        manifest.Contains("requireAdministrator", StringComparison.OrdinalIgnoreCase)
        || manifest.Contains("highestAvailable", StringComparison.OrdinalIgnoreCase);

    private static bool IsUninstaller(string displayName, string target)
    {
        var loweredName = displayName.ToLowerInvariant();
        var fileName = Path.GetFileName(target ?? string.Empty).ToLowerInvariant();
        return loweredName.Contains("uninstall", StringComparison.Ordinal)
               || loweredName.Contains("卸载", StringComparison.Ordinal)
               || fileName.StartsWith("unins", StringComparison.Ordinal)
               || fileName.Contains("uninstall", StringComparison.Ordinal);
    }

    private static bool ContainsUnsafeApplicationRequestSyntax(string value)
    {
        var candidate = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);
        if (candidate.Length == 0)
        {
            return false;
        }

        return candidate.IndexOfAny(['\\', '/', ':']) >= 0
               || candidate.IndexOfAny(['|', '&', ';', '<', '>', '`']) >= 0
               || UnsafeApplicationReferenceExtensions.Any(extension =>
                   candidate.Contains(extension, StringComparison.OrdinalIgnoreCase))
               || UnsafeApplicationRequestTokens.Any(token =>
                   candidate.Contains(token, StringComparison.OrdinalIgnoreCase))
               || candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .Any(part => part.StartsWith("--", StringComparison.Ordinal)
                                || part.Length > 1
                                && part[0] == '-'
                                && char.IsLetterOrDigit(part[1]));
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent)
                    || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }

            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string CanonicalTarget(string value)
    {
        if (TryNormalizeAppsFolderTarget(value, out var appsFolder))
        {
            return appsFolder.ToUpperInvariant();
        }

        if (SafeSettingsTargets.Contains(value))
        {
            return value.ToLowerInvariant();
        }

        try
        {
            return Path.GetFullPath(value).ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            return value.ToUpperInvariant();
        }
    }

    private static string NormalizeDisplayName(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (IsNameCharacter(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsNameCharacter(char character) => char.IsLetterOrDigit(character)
        || CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private static string StableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static KnownDesktopApplication Settings(
        string id,
        string displayName,
        string target,
        params string[] aliases) =>
        new(id, displayName, target, [displayName, .. aliases]);

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;
    private const int RtManifest = 24;
    private const uint MaximumManifestBytes = 1024 * 1024;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(
        string fileName,
        IntPtr file,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    private sealed record ApplicationAliasRule(
        IReadOnlyList<string> DisplayNames,
        IReadOnlyList<string> ExecutableNames)
    {
        public bool Matches(KnownDesktopApplication application) =>
            ApplicationNames(application).Any(name =>
                DisplayNames.Any(expected =>
                    string.Equals(
                        NormalizeDisplayName(name),
                        NormalizeDisplayName(expected),
                        StringComparison.OrdinalIgnoreCase)))
            || ExecutableNames.Any(expected =>
                string.Equals(
                    Path.GetFileName(application.LaunchTarget),
                    expected,
                    StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ApplicationBuilder
    {
        private readonly HashSet<string> _names = new(StringComparer.CurrentCultureIgnoreCase);
        private bool _preferred;

        public ApplicationBuilder(
            string id,
            string displayName,
            string target,
            IEnumerable<string> names,
            bool preferred)
        {
            Id = id;
            DisplayName = displayName;
            Target = target;
            _preferred = preferred;
            foreach (var name in names)
            {
                _names.Add(name);
            }
        }

        public string Id { get; private set; }

        public string DisplayName { get; private set; }

        public string Target { get; }

        public void Merge(
            string id,
            string displayName,
            IEnumerable<string> names,
            bool preferred)
        {
            foreach (var name in names)
            {
                _names.Add(name);
            }

            if (preferred && !_preferred)
            {
                Id = id;
                DisplayName = displayName;
                _preferred = true;
            }
        }

        public KnownDesktopApplication Build() =>
            new(
                Id,
                DisplayName,
                Target,
                _names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray());
    }
}
