using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ScreenGuide.Skills.Windows;

internal static class VisibleBrowserWindowLauncher
{
    private static readonly TimeSpan WindowDiscoveryTimeout = TimeSpan.FromSeconds(8);

    public static VisibleDesktopLaunchResult Open(string? browserLaunchTarget, Uri website)
    {
        ArgumentNullException.ThrowIfNull(website);
        var processName = string.IsNullOrWhiteSpace(browserLaunchTarget)
            ? ResolveDefaultBrowserProcessName()
            : ProcessNameFromTarget(browserLaunchTarget);
        var startInfo = string.IsNullOrWhiteSpace(browserLaunchTarget)
            ? new ProcessStartInfo(website.AbsoluteUri) { UseShellExecute = true }
            : BuildBrowserStartInfo(browserLaunchTarget, website);
        return StartAndRaise(startInfo, processName, "浏览器");
    }

    public static VisibleDesktopLaunchResult OpenApplication(string launchTarget)
    {
        if (string.IsNullOrWhiteSpace(launchTarget))
        {
            throw new ArgumentException("应用启动目标不能为空。", nameof(launchTarget));
        }

        var appsFolderTarget = launchTarget.StartsWith(
            "shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);
        var settingsTarget = launchTarget.StartsWith(
            "ms-settings:", StringComparison.OrdinalIgnoreCase);
        var startInfo = appsFolderTarget
            ? new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { launchTarget }
            }
            : new ProcessStartInfo(launchTarget) { UseShellExecute = true };
        return StartAndRaise(
            startInfo,
            appsFolderTarget ? null : settingsTarget ? "SystemSettings" : ProcessNameFromTarget(launchTarget),
            "应用",
            discoverAnyNewWindow: appsFolderTarget);
    }

    private static VisibleDesktopLaunchResult StartAndRaise(
        ProcessStartInfo startInfo,
        string? processName,
        string targetKind,
        bool discoverAnyNewWindow = false)
    {
        var before = EnumerateVisibleWindows(processName, discoverAnyNewWindow)
            .ToDictionary(item => item.Handle, item => item.Title);
        using var process = Process.Start(startInfo);
        if (process is not null && !discoverAnyNewWindow)
        {
            processName ??= TryGetProcessName(process);
        }

        if (process is null && processName is null && !discoverAnyNewWindow)
        {
            throw new InvalidOperationException($"Windows 没有返回可验证的{targetKind}启动信息。");
        }

        var stopwatch = Stopwatch.StartNew();
        WindowSnapshot? candidate = null;
        while (stopwatch.Elapsed < WindowDiscoveryTimeout)
        {
            var windows = EnumerateVisibleWindows(processName, discoverAnyNewWindow);
            candidate = windows.FirstOrDefault(item => !before.ContainsKey(item.Handle))
                        ?? (!discoverAnyNewWindow ? windows.FirstOrDefault(item =>
                            before.TryGetValue(item.Handle, out var previousTitle)
                            && !string.Equals(previousTitle, item.Title, StringComparison.Ordinal)) : null);
            if (!discoverAnyNewWindow
                && candidate is null
                && stopwatch.Elapsed >= TimeSpan.FromSeconds(2))
            {
                candidate = windows.FirstOrDefault();
            }

            if (candidate is not null
                && VisibleWindowActivation.TryActivate(candidate.Handle, TimeSpan.FromSeconds(2)))
            {
                return new VisibleDesktopLaunchResult(
                    candidate.ProcessId,
                    candidate.Handle.ToInt64(),
                    candidate.Title);
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException(
            $"{targetKind}可能已经启动，但元枢没有确认窗口显示在你眼前，因此本次操作不能标记为完成。");
    }

    private static ProcessStartInfo BuildBrowserStartInfo(string target, Uri website)
    {
        var info = new ProcessStartInfo(target) { UseShellExecute = true };
        info.ArgumentList.Add("--new-window");
        info.ArgumentList.Add(website.AbsoluteUri);
        return info;
    }

    private static string? TryGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ProcessNameFromTarget(string target)
    {
        var fileName = Path.GetFileName(target.Trim().Trim('"'));
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : null;
    }

    private static string? ResolveDefaultBrowserProcessName()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = choice?.GetValue("ProgId") as string;
            if (string.IsNullOrWhiteSpace(progId))
            {
                return null;
            }

            using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            var value = command?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var executable = ExtractExecutable(value);
            return executable is null ? null : Path.GetFileNameWithoutExtension(executable);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? ExtractExecutable(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? command[..(exe + 4)] : null;
    }

    private static IReadOnlyList<WindowSnapshot> EnumerateVisibleWindows(
        string? processName,
        bool anyProcess = false)
    {
        if (!anyProcess && string.IsNullOrWhiteSpace(processName))
        {
            return [];
        }

        var windows = new List<WindowSnapshot>();
        EnumWindows((handle, parameter) =>
        {
            if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            _ = GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0)
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (!anyProcess
                    && !string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            var title = GetTitle(handle);
            if (!string.IsNullOrWhiteSpace(title))
            {
                windows.Add(new WindowSnapshot(handle, (int)processId, title));
            }

            return true;
        }, IntPtr.Zero);
        return windows;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

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
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    private sealed record WindowSnapshot(IntPtr Handle, int ProcessId, string Title);
}

public static class VisibleWindowActivation
{
    public static bool TryActivate(IntPtr handle, TimeSpan timeout) =>
        TryActivate(handle, timeout, CancellationToken.None);

    public static bool TryActivate(
        IntPtr handle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle))
        {
            return false;
        }

        _ = ShowWindowAsync(handle, IsIconic(handle) ? SwRestore : SwShow);
        var restoreWatch = Stopwatch.StartNew();
        while (IsIconic(handle) && restoreWatch.Elapsed < TimeSpan.FromSeconds(1))
        {
            WaitOrCancel(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var foreground = GetForegroundWindow();
        if (foreground == handle)
        {
            return true;
        }

        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(handle, out _);
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        var attachedTarget = targetThread != 0 && targetThread != currentThread
                             && AttachThreadInput(currentThread, targetThread, true);
        var attachedForeground = foregroundThread != 0
                                 && foregroundThread != currentThread
                                 && foregroundThread != targetThread
                                 && AttachThreadInput(currentThread, foregroundThread, true);
        var visiblyRaised = false;
        try
        {
            visiblyRaised = SetWindowPos(handle, HwndTopMost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpShowWindow);
            _ = BringWindowToTop(handle);
            _ = SetForegroundWindow(handle);
            _ = SetWindowPos(handle, HwndNoTopMost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpShowWindow);
        }
        finally
        {
            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            }

            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThread, targetThread, false);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() == handle)
            {
                return true;
            }

            WaitOrCancel(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Windows may legally reject keyboard focus for a background process. A successful
        // temporary topmost raise plus a restored visible window is still directly visible
        // to the user, and we immediately remove topmost so the browser is not pinned forever.
        return visiblyRaised && IsWindowVisible(handle) && !IsIconic(handle);
    }

    private static void WaitOrCancel(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(delay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr handle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint source, uint target, bool attach);
}
