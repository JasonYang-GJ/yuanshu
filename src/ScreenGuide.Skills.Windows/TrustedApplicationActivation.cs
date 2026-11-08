using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenGuide.Skills.Windows;

internal sealed record ApplicationProcessLaunch(int ProcessId, string? ProcessName);

internal sealed record ApplicationProcessSnapshot(
    int ProcessId,
    string ProcessName,
    DateTimeOffset ProcessStartTimeUtc,
    bool IsRunning,
    string? ExecutablePath,
    string? PackageFamilyName,
    string? PackageFullName,
    string? ApplicationUserModelId)
{
    public ApplicationProcessSnapshot? ParentProcess { get; init; }
}

internal sealed record ApplicationWindowSnapshot(
    IntPtr Handle,
    string Title,
    ApplicationProcessSnapshot Process)
{
    public string ClassName { get; init; } = "";
    public uint ExtendedStyle { get; init; }
}

internal interface IApplicationActivationRuntime
{
    DateTimeOffset UtcNow { get; }

    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp);

    ApplicationProcessLaunch? Start(ProcessStartInfo startInfo);

    IReadOnlyList<ApplicationWindowSnapshot> EnumerateVisibleWindows();

    ApplicationWindowSnapshot? ReadWindow(IntPtr handle);

    bool TryActivate(IntPtr handle, TimeSpan timeout, CancellationToken cancellationToken);

    void Wait(TimeSpan delay, CancellationToken cancellationToken);
}

internal static class TrustedApplicationActivation
{
    private const string CalculatorId = "calculator";
    private const string CalculatorProcessName = "CalculatorApp";
    private const string CalculatorPackageFamily = "Microsoft.WindowsCalculator_8wekyb3d8bbwe";
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static VisibleDesktopLaunchResult Open(
        KnownDesktopApplication application,
        IApplicationActivationRuntime runtime,
        TimeSpan windowDiscoveryTimeout,
        CancellationToken cancellationToken,
        Action? launchInitiated = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(runtime);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(application.Id, CalculatorId, StringComparison.Ordinal))
        {
            if (!IsTrustedCalculatorLauncher(application.LaunchTarget))
            {
                throw TargetChanged("计算器启动目标不再是受信任的 Windows 系统启动器，本次没有打开程序。");
            }

            var opened = OpenTrustedCalculator(
                application,
                runtime,
                windowDiscoveryTimeout,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            launchInitiated?.Invoke();
            return opened;
        }

        return OpenSingleProcessApplication(
            application,
            runtime,
            windowDiscoveryTimeout,
            cancellationToken, launchInitiated);
    }

    private static VisibleDesktopLaunchResult OpenTrustedCalculator(
        KnownDesktopApplication application,
        IApplicationActivationRuntime runtime,
        TimeSpan windowDiscoveryTimeout,
        CancellationToken cancellationToken)
    {
        var before = runtime.EnumerateVisibleWindows()
            .Select(item => item.Handle)
            .ToHashSet();
        var attemptStartedAtUtc = runtime.UtcNow;
        var attemptTimestamp = runtime.GetTimestamp();
        var started = runtime.Start(new ProcessStartInfo(application.LaunchTarget)
        {
            UseShellExecute = true
        });
        if (started is null)
        {
            throw TargetChanged("Windows 没有返回可验证的计算器启动信息，本次操作已安全停止。");
        }

        while (runtime.GetElapsedTime(attemptTimestamp) < windowDiscoveryTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = runtime.EnumerateVisibleWindows()
                .Where(item => !before.Contains(item.Handle))
                .Where(item => item.Process.ProcessStartTimeUtc >= attemptStartedAtUtc)
                .Where(item => IsApplicationWindowCandidate(application, item))
                .Where(IsTrustedCalculatorWindow)
                .ToArray();
            if (candidates.Length > 1)
            {
                throw new InstalledApplicationResolutionException(
                    InstalledApplicationErrorCodes.Ambiguous,
                    "识别到多个可信计算器窗口，无法唯一绑定本次启动；本次操作已安全停止。");
            }

            if (candidates is [var candidate]
                && TryActivateBoundWindow(
                    candidate,
                    runtime,
                    attemptStartedAtUtc,
                    before,
                    requireCalculatorIdentity: true,
                    cancellationToken, application))
            {
                return new VisibleDesktopLaunchResult(
                    candidate.Process.ProcessId,
                    candidate.Handle.ToInt64(),
                    candidate.Title);
            }

            runtime.Wait(PollInterval, cancellationToken);
        }

        throw TargetChanged(
            "计算器可能已经启动，但没有找到与本次操作绑定的可信 Windows Calculator 窗口；本次操作已安全停止。");
    }

    private static VisibleDesktopLaunchResult OpenSingleProcessApplication(
        KnownDesktopApplication application,
        IApplicationActivationRuntime runtime,
        TimeSpan windowDiscoveryTimeout,
        CancellationToken cancellationToken,
        Action? launchInitiated)
    {
        var appsFolderTarget = application.LaunchTarget.StartsWith(
            "shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);
        var settingsTarget = application.LaunchTarget.StartsWith(
            "ms-settings:", StringComparison.OrdinalIgnoreCase);
        var processName = appsFolderTarget
            ? null
            : settingsTarget
                ? "SystemSettings"
                : ProcessNameFromTarget(application.LaunchTarget);
        var startInfo = appsFolderTarget
            ? new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { application.LaunchTarget }
            }
            : new ProcessStartInfo(application.LaunchTarget) { UseShellExecute = true };
        var existing = EnumerateMatching(runtime, processName, appsFolderTarget, application)
            .Where(item => settingsTarget || MatchesActivationTarget(application, item.Process))
            .Where(item => IsApplicationWindowCandidate(application, item))
            .ToArray();
        RequireUniqueSteamWindow(application, existing);
        if (!appsFolderTarget && !settingsTarget && existing is [var alreadyOpen]
            && alreadyOpen.Handle != IntPtr.Zero && alreadyOpen.Process.IsRunning
            && alreadyOpen.Process.ProcessId > 0 && alreadyOpen.Process.ProcessStartTimeUtc != default)
        {
            // An exact executable with one visible owned window can be reused immediately.
            // Do not start a second process and wait two seconds for a title change that a
            // single-instance app may never make. Keep live identity checks on both sides
            // of activation, and stop instead of relaunching if that identity changes.
            if (!TryActivateBoundWindow(alreadyOpen, runtime, DateTimeOffset.MinValue,
                    new HashSet<IntPtr>(), requireCalculatorIdentity: false, cancellationToken, application))
                throw TargetChanged("已打开的软件窗口发生变化或无法确认激活，本次操作已停止。");
            cancellationToken.ThrowIfCancellationRequested();
            launchInitiated?.Invoke();
            return new(alreadyOpen.Process.ProcessId, alreadyOpen.Handle.ToInt64(), alreadyOpen.Title);
        }
        var before = existing.ToDictionary(item => item.Handle, item => item.Title);
        var started = runtime.Start(startInfo);
        if (started is not null && !appsFolderTarget)
        {
            processName ??= started.ProcessName;
        }

        if (started is null && processName is null && !appsFolderTarget)
        {
            throw TargetChanged("Windows 没有返回可验证的应用启动信息，本次操作已安全停止。");
        }

        var notified = started is { ProcessId: > 0 };
        if (notified)
        {
            cancellationToken.ThrowIfCancellationRequested();
            launchInitiated?.Invoke();
        }

        var attemptTimestamp = runtime.GetTimestamp();
        while (runtime.GetElapsedTime(attemptTimestamp) < windowDiscoveryTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windows = EnumerateMatching(runtime, processName, appsFolderTarget, application)
                .Where(item => settingsTarget || MatchesActivationTarget(application, item.Process))
                .Where(item => IsApplicationWindowCandidate(application, item)).ToArray();
            RequireUniqueSteamWindow(application, windows);
            var candidate = windows.FirstOrDefault(item => !before.ContainsKey(item.Handle))
                            ?? (!appsFolderTarget
                                ? windows.FirstOrDefault(item =>
                                    before.TryGetValue(item.Handle, out var previousTitle)
                                    && !string.Equals(
                                        previousTitle,
                                        item.Title,
                                        StringComparison.Ordinal))
                                : null);
            if (!appsFolderTarget
                && candidate is null
                && runtime.GetElapsedTime(attemptTimestamp) >= TimeSpan.FromSeconds(2))
            {
                candidate = windows.FirstOrDefault();
            }

            if (candidate is not null)
            {
                var activated = TryActivateBoundWindow(
                    candidate,
                    runtime,
                    DateTimeOffset.MinValue,
                    new HashSet<IntPtr>(),
                    requireCalculatorIdentity: false,
                    cancellationToken, application);
                if (!activated && IsSteamApplication(application))
                    throw TargetChanged("Steam 主窗口或启动进程身份发生变化，本次操作已停止。");
                if (!activated) { runtime.Wait(PollInterval, cancellationToken); continue; }
                if (!notified)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    launchInitiated?.Invoke();
                }
                return new VisibleDesktopLaunchResult(
                    candidate.Process.ProcessId,
                    candidate.Handle.ToInt64(),
                    candidate.Title);
            }

            runtime.Wait(PollInterval, cancellationToken);
        }

        throw TargetChanged(
            "应用可能已经启动，但元枢没有确认可信窗口显示在你眼前，因此本次操作不能标记为完成。");
    }

    private static IReadOnlyList<ApplicationWindowSnapshot> EnumerateMatching(
        IApplicationActivationRuntime runtime,
        string? processName,
        bool anyProcess,
        KnownDesktopApplication application) =>
        runtime.EnumerateVisibleWindows()
            .Where(item => anyProcess || string.Equals(
                item.Process.ProcessName,
                processName,
                StringComparison.OrdinalIgnoreCase) || IsSteamHelperFor(application, item.Process))
            .ToArray();

    internal static bool IsInteractiveWindowStyle(uint extendedStyle) =>
        (extendedStyle & (0x00000080u | 0x08000000u)) == 0; // WS_EX_TOOLWINDOW / WS_EX_NOACTIVATE

    private static bool IsApplicationWindowCandidate(KnownDesktopApplication application, ApplicationWindowSnapshot window)
    {
        if (!IsInteractiveWindowStyle(window.ExtendedStyle)) return false;
        if (IsSteamApplication(application))
            return string.Equals(window.ClassName, "SDL_app", StringComparison.Ordinal)
                && string.Equals(window.Title, "Steam", StringComparison.Ordinal);
        // A registered app can expose titled, ownerless helper windows before its real
        // interface. CloudMusic's SystemHint/lyrics/mini-player are not its search window.
        // The executable/AUMID proof remains separate; this is only a narrower selector.
        return !Path.IsPathFullyQualified(application.LaunchTarget)
            || !string.Equals(Path.GetFileName(application.LaunchTarget), "cloudmusic.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(window.ClassName, "OrpheusBrowserHost", StringComparison.Ordinal);
    }

    private static bool IsSteamApplication(KnownDesktopApplication application) =>
        Path.IsPathFullyQualified(application.LaunchTarget)
        && string.Equals(Path.GetFileName(application.LaunchTarget), "steam.exe", StringComparison.OrdinalIgnoreCase);

    private static void RequireUniqueSteamWindow(KnownDesktopApplication application, ApplicationWindowSnapshot[] windows)
    {
        if (IsSteamApplication(application) && windows.Length > 1)
            throw new InstalledApplicationResolutionException(InstalledApplicationErrorCodes.Ambiguous,
                "发现多个可信 Steam 主窗口，无法唯一确定目标，本次没有切换窗口。");
    }

    // Opening-only handoff. Other skills retain MatchesRegisteredTarget's stricter
    // executable binding; a helper is not authority for application-internal actions.
    private static bool MatchesActivationTarget(KnownDesktopApplication application, ApplicationProcessSnapshot process) =>
        MatchesRegisteredTarget(application, process) || IsSteamHelperFor(application, process);

    private static bool IsSteamHelperFor(KnownDesktopApplication application, ApplicationProcessSnapshot process)
    {
        if (!IsSteamApplication(application) || !process.IsRunning || process.ProcessId <= 0
            || process.ProcessStartTimeUtc == default
            || !string.Equals(process.ProcessName, "steamwebhelper", StringComparison.OrdinalIgnoreCase)
            || process.ExecutablePath is not { } helperPath || !Path.IsPathFullyQualified(helperPath)
            || process.ParentProcess is not { IsRunning: true, ProcessId: > 0 } parent
            || parent.ProcessId == process.ProcessId || parent.ProcessStartTimeUtc == default
            || parent.ProcessStartTimeUtc > process.ProcessStartTimeUtc
            || !string.Equals(parent.ProcessName, "steam", StringComparison.OrdinalIgnoreCase)
            || !MatchesRegisteredTarget(application, parent)) return false;
        var root = Path.GetDirectoryName(Path.GetFullPath(application.LaunchTarget))!;
        return new[] { "cef.win64", "cef.win7x64" }.Any(layout => string.Equals(
            Path.GetFullPath(helperPath), Path.Combine(root, "bin", "cef", layout, "steamwebhelper.exe"),
            StringComparison.OrdinalIgnoreCase));
    }

    internal static bool MatchesRegisteredTarget(KnownDesktopApplication application, ApplicationProcessSnapshot process)
    {
        const string prefix = "shell:AppsFolder\\";
        if (application.LaunchTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(process.ApplicationUserModelId)
                && string.Equals(application.LaunchTarget[prefix.Length..], process.ApplicationUserModelId, StringComparison.OrdinalIgnoreCase);
        var exactExecutable = Path.IsPathFullyQualified(application.LaunchTarget)
            && !string.IsNullOrWhiteSpace(process.ExecutablePath)
            && string.Equals(Path.GetFullPath(application.LaunchTarget), Path.GetFullPath(process.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        if (exactExecutable) return true;

        // Windows 11's system notepad.exe is a launcher for the packaged Notepad.
        // Keep the authorized system target unchanged and verify the OS-reported
        // package identity together with its exact executable inside that package.
        return string.Equals(application.Id, "notepad", StringComparison.Ordinal)
            && IsTrustedSystemLauncher(application.LaunchTarget, "notepad.exe")
            && IsTrustedNotepadProcess(process);
    }

    private static bool IsTrustedNotepadProcess(ApplicationProcessSnapshot process)
    {
        if (!process.IsRunning || process.ProcessId <= 0 || process.ProcessStartTimeUtc == default
            || !string.Equals(process.ProcessName, "Notepad", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(process.PackageFamilyName, "Microsoft.WindowsNotepad_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(process.ApplicationUserModelId, "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(process.PackageFullName)
            || string.IsNullOrWhiteSpace(process.ExecutablePath)
            || !Path.IsPathFullyQualified(process.ExecutablePath))
        {
            return false;
        }

        var package = process.PackageFullName.Split('_');
        if (package.Length != 5
            || !string.Equals(package[0], "Microsoft.WindowsNotepad", StringComparison.OrdinalIgnoreCase)
            || !Version.TryParse(package[1], out var version) || version.Build < 0 || version.Revision < 0
            || package[2] is not ("x64" or "x86" or "arm64")
            || package[3].Length != 0
            || !string.Equals(package[4], "8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles)) return false;
        try
        {
            var expectedPath = Path.Combine(programFiles, "WindowsApps",
                process.PackageFullName, "Notepad", "Notepad.exe");
            return string.Equals(Path.GetFullPath(process.ExecutablePath), expectedPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryActivateBoundWindow(
        ApplicationWindowSnapshot candidate,
        IApplicationActivationRuntime runtime,
        DateTimeOffset attemptStartedAtUtc,
        IReadOnlySet<IntPtr> previousHandles,
        bool requireCalculatorIdentity,
        CancellationToken cancellationToken,
        KnownDesktopApplication application)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = runtime.ReadWindow(candidate.Handle);
        if (!Matches(candidate, current)
            || current is null
            || !current.Process.IsRunning
            || !IsApplicationWindowCandidate(application, current)
            || previousHandles.Contains(current.Handle)
            || current.Process.ProcessStartTimeUtc < attemptStartedAtUtc
            || requireCalculatorIdentity && !IsTrustedCalculatorWindow(current))
        {
            return false;
        }

        if (!runtime.TryActivate(current.Handle, TimeSpan.FromSeconds(2), cancellationToken))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var afterActivation = runtime.ReadWindow(current.Handle);
        return Matches(current, afterActivation)
               && afterActivation is not null
               && afterActivation.Process.IsRunning
               && IsApplicationWindowCandidate(application, afterActivation)
               && (!requireCalculatorIdentity || IsTrustedCalculatorWindow(afterActivation));
    }

    private static bool Matches(
        ApplicationWindowSnapshot expected,
        ApplicationWindowSnapshot? actual) =>
        actual is not null
        && expected.Handle == actual.Handle
        && string.Equals(expected.ClassName, actual.ClassName, StringComparison.Ordinal)
        && expected.Process.ProcessId == actual.Process.ProcessId
        && expected.Process.ProcessStartTimeUtc == actual.Process.ProcessStartTimeUtc
        && Equals(expected.Process.ParentProcess, actual.Process.ParentProcess)
        && string.Equals(
            expected.Process.ProcessName,
            actual.Process.ProcessName,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            expected.Process.ExecutablePath,
            actual.Process.ExecutablePath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            expected.Process.PackageFamilyName,
            actual.Process.PackageFamilyName,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            expected.Process.PackageFullName,
            actual.Process.PackageFullName,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            expected.Process.ApplicationUserModelId,
            actual.Process.ApplicationUserModelId,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedCalculatorWindow(ApplicationWindowSnapshot window)
    {
        var process = window.Process;
        if (window.Handle == IntPtr.Zero
            || string.IsNullOrWhiteSpace(window.Title)
            || !process.IsRunning
            || process.ProcessId <= 0
            || process.ProcessStartTimeUtc == default
            || !string.Equals(process.ProcessName, CalculatorProcessName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(process.PackageFamilyName, CalculatorPackageFamily, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(process.ApplicationUserModelId, CalculatorAumid, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(process.PackageFullName)
            || !process.PackageFullName.StartsWith(
                "Microsoft.WindowsCalculator_",
                StringComparison.OrdinalIgnoreCase)
            || !process.PackageFullName.EndsWith(
                "__8wekyb3d8bbwe",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(process.PackageFullName),
                process.PackageFullName,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            return false;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return false;
        }

        var packageRoot = Path.Combine(programFiles, "WindowsApps", process.PackageFullName);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(process.ExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }

        var relative = Path.GetRelativePath(packageRoot, fullPath);
        return !relative.StartsWith("..", StringComparison.Ordinal)
               && !Path.IsPathFullyQualified(relative)
               && string.Equals(
                   Path.GetFileName(fullPath),
                   "CalculatorApp.exe",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrustedCalculatorLauncher(string launchTarget) =>
        IsTrustedSystemLauncher(launchTarget, "calc.exe");

    private static bool IsTrustedSystemLauncher(string launchTarget, string executableName)
    {
        if (!Path.IsPathFullyQualified(launchTarget))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(launchTarget);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }

        return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
            }
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => Path.Combine(item, executableName))
            .Any(item => string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ProcessNameFromTarget(string target)
    {
        var fileName = Path.GetFileName(target.Trim().Trim('"'));
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : null;
    }

    private static InstalledApplicationResolutionException TargetChanged(string message) =>
        new(InstalledApplicationErrorCodes.TargetChanged, message);
}

internal sealed partial class WindowsApplicationActivationRuntime : IApplicationActivationRuntime
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        Stopwatch.GetElapsedTime(startingTimestamp);

    public ApplicationProcessLaunch? Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        string? processName;
        try
        {
            processName = process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            processName = null;
        }

        return new ApplicationProcessLaunch(process.Id, processName);
    }

    public IReadOnlyList<ApplicationWindowSnapshot> EnumerateVisibleWindows()
    {
        var windows = new List<ApplicationWindowSnapshot>();
        EnumWindows((handle, _) =>
        {
            var snapshot = ReadWindow(handle);
            if (snapshot is not null)
            {
                windows.Add(snapshot);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public ApplicationWindowSnapshot? ReadWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero
            || !IsWindow(handle)
            || !IsWindowVisible(handle)
            || GetWindow(handle, GwOwner) != IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var title = GetTitle(handle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var className = new StringBuilder(256);
        if (GetClassName(handle, className, className.Capacity) == 0) return null;
        Marshal.SetLastPInvokeError(0);
        var style = IntPtr.Size == 8
            ? GetWindowLongPtr(handle, -20).ToInt64()
            : GetWindowLong(handle, -20);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) return null;
        var extendedStyle = unchecked((uint)style);
        if (!TrustedApplicationActivation.IsInteractiveWindowStyle(extendedStyle)) return null;

        var process = ReadProcess((int)processId);
        return process is null ? null : new ApplicationWindowSnapshot(handle, title, process)
            { ClassName = className.ToString(), ExtendedStyle = extendedStyle };
    }

    public bool TryActivate(IntPtr handle, TimeSpan timeout, CancellationToken cancellationToken) =>
        VisibleWindowActivation.TryActivate(handle, timeout, cancellationToken);

    public void Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(delay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static ApplicationProcessSnapshot? ReadProcess(int processId, bool includeParent = true)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            var processStartTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
            {
                return new ApplicationProcessSnapshot(
                    processId,
                    processName,
                    processStartTimeUtc,
                    !process.HasExited,
                    null,
                    null,
                    null,
                    null);
            }

            try
            {
                // Bind metadata read through Process and the native handle to one
                // process instance, including parent snapshots sampled recursively.
                if (!GetProcessTimes(handle, out var creation, out _, out _, out _)
                    || DateTimeOffset.FromFileTime(creation) != processStartTimeUtc) return null;
                var snapshot = new ApplicationProcessSnapshot(
                    processId,
                    processName,
                    processStartTimeUtc,
                    !process.HasExited,
                    ReadImagePath(handle),
                    ReadAppModelValue(handle, GetPackageFamilyName),
                    ReadAppModelValue(handle, GetPackageFullName),
                    ReadAppModelValue(handle, GetApplicationUserModelId));
                if (includeParent && string.Equals(processName, "steamwebhelper", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot = snapshot with { ParentProcess = ReadParentIdentity(handle, processId, processStartTimeUtc) };
                }
                return process.HasExited ? null : snapshot;
            }
            finally
            {
                _ = CloseHandle(handle);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? ReadImagePath(IntPtr processHandle)
    {
        var capacity = 32768u;
        var builder = new StringBuilder((int)capacity);
        return QueryFullProcessImageName(processHandle, 0, builder, ref capacity)
            ? builder.ToString()
            : null;
    }

    private static string? ReadAppModelValue(IntPtr processHandle, AppModelQuery query)
    {
        var length = 0u;
        var first = query(processHandle, ref length, null);
        if (first != ErrorInsufficientBuffer || length == 0)
        {
            return null;
        }

        var builder = new StringBuilder((int)length);
        return query(processHandle, ref length, builder) == 0
            ? builder.ToString().TrimEnd('\0')
            : null;
    }

    private static string GetTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(Math.Min(length + 1, 1024));
        _ = GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private const uint GwOwner = 4;

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

    private delegate int AppModelQuery(IntPtr processHandle, ref uint length, StringBuilder? value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder name, int capacity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        uint flags,
        StringBuilder executableName,
        ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(
        IntPtr processHandle,
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(
        IntPtr processHandle,
        ref uint packageFullNameLength,
        StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(
        IntPtr processHandle,
        ref uint applicationUserModelIdLength,
        StringBuilder? applicationUserModelId);
}
