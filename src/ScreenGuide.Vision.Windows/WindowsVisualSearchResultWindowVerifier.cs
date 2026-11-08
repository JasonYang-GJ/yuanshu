using System.Runtime.InteropServices;
using System.Text;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Vision.Windows;

internal interface IVisualSearchResultWindows : IVisualSearchResultWindowVerifier
{
    ForegroundWindowSnapshot? Find(ForegroundWindowSnapshot origin, VisualSearchWindowBounds bounds, VisualSearchRect field);
}

/// <summary>Metadata-only binding for a single directly owned search dropdown. Never captures or activates.</summary>
public sealed class WindowsVisualSearchResultWindowVerifier : IVisualSearchResultWindows
{
    private readonly WindowsWindowCaptureTargetVerifier _identity = new();

    public void Verify(ForegroundWindowSnapshot originWindow, ForegroundWindowSnapshot resultWindow)
    {
        if (originWindow.WindowHandle == resultWindow.WindowHandle
            || originWindow.ProcessId != resultWindow.ProcessId
            || originWindow.ProcessStartTimeUtc != resultWindow.ProcessStartTimeUtc
            || !string.Equals(originWindow.ProcessName, resultWindow.ProcessName, StringComparison.OrdinalIgnoreCase)
            || GetWindow(new(resultWindow.WindowHandle), 4).ToInt64() != originWindow.WindowHandle
            || !IsWindowVisible(new(resultWindow.WindowHandle))) throw Changed();
        _identity.Verify(Target(originWindow));
        _identity.Verify(Target(resultWindow));
        if (GetWindow(new(resultWindow.WindowHandle), 4).ToInt64() != originWindow.WindowHandle
            || !IsWindowVisible(new(resultWindow.WindowHandle))) throw Changed();
    }

    ForegroundWindowSnapshot? IVisualSearchResultWindows.Find(ForegroundWindowSnapshot origin,
        VisualSearchWindowBounds bounds, VisualSearchRect field)
    {
        _identity.Verify(Target(origin));
        var candidates = new List<ForegroundWindowSnapshot>();
        var input = new WindowsVisualSearchInputDriver();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindow(handle, 4).ToInt64() != origin.WindowHandle) return true;
            GetWindowThreadProcessId(handle, out var process);
            if (process != origin.ProcessId) return true;
            try
            {
                var geometry = input.GetBounds(handle.ToInt64());
                if (!IsAdjacentDropdown(bounds, field, geometry)) return true;
                var title = new StringBuilder(512);
                GetWindowText(handle, title, title.Capacity);
                var candidate = origin with { WindowHandle = handle.ToInt64(), WindowTitle = title.ToString().Trim() };
                Verify(origin, candidate);
                candidates.Add(candidate);
            }
            catch (Exception exception) when (exception is WindowIdentityException or DesktopSearchException) { }
            return true;
        }, IntPtr.Zero);
        if (candidates.Count > 1) throw new DesktopSearchException(DesktopSearchErrorCodes.CandidateAmbiguous,
            "搜索框附近出现多个直属浮窗，无法确定结果窗口，因此没有读取它们。");
        return candidates.SingleOrDefault();
    }

    internal static bool IsAdjacentDropdown(VisualSearchWindowBounds main, VisualSearchRect field, VisualSearchWindowBounds popup)
    {
        var x = (long)popup.X - main.X;
        var y = (long)popup.Y - main.Y;
        return popup.Width >= field.Width / 2 && popup.Width <= 1000 && popup.Height >= field.Height
            && popup.Height <= 800 && x >= 0 && y >= 0
            && x + popup.Width <= main.Width && y + popup.Height <= main.Height
            && Math.Abs(x - field.X) <= field.Height * 2L
            && Math.Abs(y - field.Y - field.Height) <= field.Height;
    }

    internal static WindowCaptureTarget Target(ForegroundWindowSnapshot window) => new(window.WindowHandle,
        window.WindowTitle, window.ProcessName, window.ProcessId, window.ProcessStartTimeUtc, DateTimeOffset.UtcNow);
    private static WindowIdentityException Changed() => new(WindowIdentityErrorCodes.Changed,
        "本次结果浮窗已关闭或归属发生变化；不会读取其它窗口，也不会重复输入。");
    private delegate bool Callback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
}
