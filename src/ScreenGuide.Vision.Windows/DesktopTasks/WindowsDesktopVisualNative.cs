using System.Runtime.InteropServices;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.Vision.Windows;

internal sealed class WindowsDesktopVisualNative : IDesktopVisualNative
{
    public DesktopTaskWindow RequireWindow(DesktopTaskWindow expected)
    {
        if (expected is null) throw Changed();
        // Bind reads process/window metadata and applies the shared blocked-window
        // policy, without reading UIA controls or creating a second authority.
        var response = new WindowsDesktopTaskWorker().Execute(new("Bind", expected.Handle));
        if (!response.Succeeded || response.Window is not { } current)
            throw new DesktopTaskException(response.ErrorCode ?? "desktop_visual_window_changed", response.Message ?? "目标窗口不可用。");
        if (current with { Title = expected.Title } != expected
            || !IsWindowVisible(new(expected.Handle)) || IsIconic(new(expected.FrameIdentity?.Handle ?? expected.Handle))
            || GetForegroundWindow().ToInt64() != (expected.FrameIdentity?.Handle ?? expected.Handle))
            throw Changed();
        return current;
    }

    public DesktopVisualGeometry ReadGeometry(DesktopTaskWindow window)
    {
        using var dpi = new PhysicalCoordinates();
        _ = RequireWindow(window);
        if (DwmGetWindowAttribute(new(window.Handle), 9, out var rect, Marshal.SizeOf<Rect>()) != 0)
            throw Changed();
        var bounds = new DesktopTaskBounds(rect.Left, rect.Top, (long)rect.Right - rect.Left, (long)rect.Bottom - rect.Top);
        var scale = GetDpiForWindow(new(window.Handle));
        if (scale > int.MaxValue) throw Changed();
        DesktopVisualImage.RequireGeometry(bounds, (int)scale);
        return new(bounds, (int)scale);
    }

    public uint Click(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds,
        bool doubleClick, Action validate, CancellationToken token)
    {
        using var dpi = new PhysicalCoordinates();
        var point = TargetPoint(geometry, sourceBounds);
        CheckBoundary(window, geometry, point, validate, token);
        // Down/up pairs enter the OS input queue in ONE call. No held button
        // crosses an await, cancellation callback, delay, or subsequent request.
        var events = BuildClickEvents(doubleClick);
        SendPaired(events);
        var last = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref last)) throw Changed();
        return last.Time;
    }

    public DesktopVisualFocus RequireCaret(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds)
    {
        using var dpi = new PhysicalCoordinates();
        _ = RequireWindow(window);
        if (ReadGeometry(window) != geometry) throw Changed();
        var thread = GetWindowThreadProcessId(new(window.Handle), out _);
        var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info) || info.Focus == IntPtr.Zero || info.Caret == IntPtr.Zero
            || !BelongsToWindow(window, info.Focus) || !BelongsToWindow(window, info.Caret)) throw CaretChanged();
        var topLeft = new Point { X = info.CaretBounds.Left, Y = info.CaretBounds.Top };
        var bottomRight = new Point { X = info.CaretBounds.Right, Y = info.CaretBounds.Bottom };
        if (!ClientToScreen(info.Caret, ref topLeft) || !ClientToScreen(info.Caret, ref bottomRight)
            || !IsCaretWithin(info.Flags, geometry.Bounds, sourceBounds, topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y))
            throw CaretChanged();
        var again = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        if (!GetGUIThreadInfo(thread, ref again) || again.Focus != info.Focus || again.Caret != info.Caret
            || again.CaretBounds != info.CaretBounds || (again.Flags & 1) == 0) throw CaretChanged();
        var last = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref last)) throw Changed();
        _ = RequireWindow(window);
        if (ReadGeometry(window) != geometry) throw Changed();
        return new(info.Focus.ToInt64(), last.Time);
    }

    public void TypeText(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds,
        DesktopVisualFocus focus, string text, Action validate, CancellationToken token)
    {
        using var dpi = new PhysicalCoordinates();
        DesktopVisualImage.RequireText(text, string.Empty);
        token.ThrowIfCancellationRequested();
        validate();
        RequireReleasedInputs();
        if (RequireCaret(window, geometry, sourceBounds) != focus) throw CaretChanged();
        // Unicode text only. No clipboard, selection replacement, Enter, Tab,
        // system key, or key modifier is accepted by this input contract.
        var events = BuildTextEvents(text);
        token.ThrowIfCancellationRequested();
        validate();
        RequireReleasedInputs();
        if (RequireCaret(window, geometry, sourceBounds) != focus) throw CaretChanged();
        SendPaired(events);
    }

    public void Scroll(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds sourceBounds,
        bool up, Action validate, CancellationToken token)
    {
        using var dpi = new PhysicalCoordinates();
        var point = TargetPoint(geometry, sourceBounds);
        CheckBoundary(window, geometry, point, validate, token);
        // One wheel notch. The next step must observe its actual effect.
        SendPaired([new Input { Type = 0, Data = new Union { Mouse = new Mouse { Flags = 0x0800, MouseData = unchecked((uint)(up ? 120 : -120)) } } }]);
    }

    public void PreparePointer(DesktopTaskWindow window, DesktopVisualGeometry geometry, DesktopVisualBounds bounds,
        Action validate, CancellationToken token)
    {
        using var dpi = new PhysicalCoordinates();
        var point = TargetPoint(geometry, bounds);
        CheckBoundary(window, geometry, point, validate, token, requireCursor: false);
        // Check held buttons again immediately before moving: a user drag is
        // never repurposed as our own click or scroll.
        RequireReleasedInputs();
        if (!SetPhysicalCursorPos(point.X, point.Y)) throw Changed();
        CheckBoundary(window, geometry, point, validate, token);
    }

    private static Point TargetPoint(DesktopVisualGeometry geometry, DesktopVisualBounds bounds)
    {
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0
            || (long)bounds.X + bounds.Width > geometry.Bounds.Width || (long)bounds.Y + bounds.Height > geometry.Bounds.Height)
            throw Changed();
        return new Point
        {
            X = checked((int)geometry.Bounds.X + bounds.X + bounds.Width / 2),
            Y = checked((int)geometry.Bounds.Y + bounds.Y + bounds.Height / 2)
        };
    }

    private void CheckBoundary(DesktopTaskWindow window, DesktopVisualGeometry geometry, Point point,
        Action validate, CancellationToken token, bool requireCursor = true)
    {
        token.ThrowIfCancellationRequested();
        validate();
        RequireReleasedInputs();
        if (RequireWindow(window) != window || ReadGeometry(window) != geometry
            || !BelongsToWindow(window, WindowFromPoint(point))) throw Changed();
        if (requireCursor && (!GetCursorPos(out var current) || current.X != point.X || current.Y != point.Y)) throw Changed();
    }

    private static bool BelongsToWindow(DesktopTaskWindow window, IntPtr handle)
    {
        var root = new IntPtr(window.Handle);
        return handle != IntPtr.Zero && (handle == root || IsChild(root, handle))
            && GetAncestor(handle, 2).ToInt64() == (window.FrameIdentity?.Handle ?? window.Handle);
    }

    internal static bool IsCaretWithin(uint flags, DesktopTaskBounds geometry, DesktopVisualBounds field,
        int left, int top, int right, int bottom) => (flags & 1) != 0
        && field.X >= 0 && field.Y >= 0 && field.Width > 0 && field.Height > 0
        && (long)field.X + field.Width <= geometry.Width && (long)field.Y + field.Height <= geometry.Height
        && right > left && bottom > top
        && left > geometry.X + field.X && top > geometry.Y + field.Y
        && right < geometry.X + field.X + field.Width && bottom < geometry.Y + field.Y + field.Height;

    internal static void RequireReleasedInputs(Func<int, short>? state = null)
    {
        state ??= GetAsyncKeyState;
        if (new[] { 0x01, 0x02, 0x04, 0x05, 0x06, 0x10, 0x11, 0x12, 0x5B, 0x5C }
            .Any(key => (state(key) & 0x8000) != 0))
            throw DesktopVisualImage.Error("user_input", "检测到你正在按键或拖动鼠标，已停止本次视觉输入。");
    }

    internal static Input[] BuildClickEvents(bool doubleClick) => Enumerable.Range(0, doubleClick ? 2 : 1)
        .SelectMany(_ => new[] { new Input { Type = 0, Data = new Union { Mouse = new Mouse { Flags = 2 } } },
            new Input { Type = 0, Data = new Union { Mouse = new Mouse { Flags = 4 } } } }).ToArray();

    internal static Input[] BuildTextEvents(string text) => text.SelectMany(character => new[]
    {
        new Input { Type = 1, Data = new Union { Keyboard = new Keyboard { Scan = character, Flags = 4 } } },
        new Input { Type = 1, Data = new Union { Keyboard = new Keyboard { Scan = character, Flags = 6 } } }
    }).ToArray();

    private static void SendPaired(Input[] events)
    {
        var sent = SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
        if (sent == events.Length) return;
        // If the OS reported a partial pair, release only the exact last down
        // event we inserted. Never retry the action or release a user's keys.
        if (sent > 0 && sent < events.Length)
        {
            var last = events[sent - 1];
            if (last.Type == 0 && last.Data.Mouse.Flags == 2)
                _ = SendInput(1, [new Input { Type = 0, Data = new Union { Mouse = new Mouse { Flags = 4 } } }], Marshal.SizeOf<Input>());
            else if (last.Type == 1 && last.Data.Keyboard.Flags == 4)
            {
                last.Data.Keyboard.Flags = 6;
                _ = SendInput(1, [last], Marshal.SizeOf<Input>());
            }
        }
        throw DesktopVisualImage.Error("partial_input", "系统没有完整接收本次输入，已停止；不会重试已经发生的操作。");
    }

    private static DesktopTaskException Changed() => DesktopVisualImage.Error("window_changed", "窗口、鼠标位置或键盘焦点变化，已停止视觉输入。");
    private static DesktopTaskException CaretChanged() => DesktopVisualImage.Error("caret_unverified", "未能确认原生输入光标位于指定区域内，因此没有输入文字。");

    private sealed class PhysicalCoordinates : IDisposable
    {
        private readonly IntPtr _previous = SetThreadDpiAwarenessContext(new(-4));
        public PhysicalCoordinates()
        {
            if (_previous == IntPtr.Zero) throw DesktopVisualImage.Error("dpi", "无法建立物理像素坐标，未移动鼠标。");
        }
        public void Dispose() => _ = SetThreadDpiAwarenessContext(_previous);
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Rect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    { public uint Size, Flags; public IntPtr Active, Focus, Capture, Menu, MoveSize, Caret; public Rect CaretBounds; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct Union
    { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct Mouse
    { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct Keyboard
    { public ushort VirtualKey, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect rect, int size);
}
