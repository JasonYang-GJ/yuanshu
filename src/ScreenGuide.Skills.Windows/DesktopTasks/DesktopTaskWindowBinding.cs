using System.IO;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

internal sealed record DesktopTaskNativeWindowMetadata(DesktopTaskWindow Window, long ParentHandle, long RootHandle, bool IsChild);

/// <summary>Only the system UWP frame/content relationship extends the top-level contract.</summary>
internal static class DesktopTaskWindowBinding
{
    internal const int MaximumFrameChildren = 64;
    internal static string TrustedFramePath => Path.Combine(Environment.SystemDirectory, "ApplicationFrameHost.exe");

    internal static bool IsTrustedFrame(DesktopTaskNativeWindowMetadata frame) =>
        frame.RootHandle == frame.Window.Handle && !frame.IsChild && frame.Window.FrameIdentity is null
        && frame.Window.ClassName == "ApplicationFrameWindow"
        && string.Equals(frame.Window.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
        && string.Equals(frame.Window.ExecutablePath, TrustedFramePath, StringComparison.OrdinalIgnoreCase);

    internal static DesktopTaskWindow Bind(DesktopTaskNativeWindowMetadata content, DesktopTaskNativeWindowMetadata? frame)
    {
        var window = content.Window;
        if (window.FrameIdentity is not null)
            throw DesktopTaskSafety.Error("window_unavailable", "窗口原始元数据包含不支持的嵌套宿主。");
        if (content.RootHandle == window.Handle && !content.IsChild)
        {
            if (frame is not null || window.ClassName == "ApplicationFrameWindow"
                || string.Equals(window.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                throw InvalidFrame();
            return window;
        }
        if (!content.IsChild || window.ClassName != "Windows.UI.Core.CoreWindow" || frame is null
            || content.ParentHandle != content.RootHandle || content.RootHandle != frame.Window.Handle
            || frame.Window.ProcessId == window.ProcessId || !IsTrustedFrame(frame))
            throw InvalidFrame();
        DesktopTaskSafety.RequireWindow(window);
        DesktopTaskSafety.RequireWindow(frame.Window);
        return window with { FrameIdentity = new(frame.Window.Handle, frame.Window.ProcessId,
            frame.Window.ProcessStartedAtUtc, frame.Window.ExecutablePath, frame.Window.ClassName) };
    }

    internal static DesktopTaskWindow ResolveChild(DesktopTaskNativeWindowMetadata frame,
        IReadOnlyList<DesktopTaskNativeWindowMetadata> children, int openedProcessId, bool truncated)
    {
        if (!IsTrustedFrame(frame) || openedProcessId <= 0 || truncated || children.Count > MaximumFrameChildren)
            throw InvalidFrame();
        var matches = children.Where(child => child.Window.ProcessId == openedProcessId
            && child.Window.ClassName == "Windows.UI.Core.CoreWindow").ToArray();
        if (matches.Length != 1) throw InvalidFrame();
        return Bind(matches[0], frame);
    }

    internal static DesktopTaskWindow ResolveSingleContent(DesktopTaskNativeWindowMetadata frame,
        IReadOnlyList<DesktopTaskNativeWindowMetadata> children, bool truncated)
    {
        if (!IsTrustedFrame(frame) || truncated || children.Count > MaximumFrameChildren) throw InvalidFrame();
        var contents = children.Where(child => child.Window.ClassName == "Windows.UI.Core.CoreWindow").ToArray();
        // Without a separately opened application PID, no content sibling may
        // be selected by guessing which one the user intended.
        if (contents.Length != 1) throw InvalidFrame();
        return Bind(contents[0], frame);
    }

    internal static bool SameMetadata(DesktopTaskNativeWindowMetadata expected, DesktopTaskNativeWindowMetadata actual) =>
        DesktopTaskSafety.SameWindow(expected.Window, actual.Window) && expected.ParentHandle == actual.ParentHandle
        && expected.RootHandle == actual.RootHandle && expected.IsChild == actual.IsChild;

    private static DesktopTaskException InvalidFrame() => DesktopTaskSafety.Error("window_unavailable",
        "目标不是独立窗口，也不是受系统宿主直接承载的可信应用内容窗口。");
}

/// <summary>
/// Resolves only native window/process metadata after an application launch.
/// It neither reads controls nor activates a window or grants task authority.
/// </summary>
public static class WindowsDesktopTaskWindowResolver
{
    public static DesktopTaskWindow ResolveLaunchedWindow(long handle, int openedProcessId) =>
        new WindowsDesktopTaskNativeRuntime().ResolveLaunchedWindow(handle, openedProcessId);
}
