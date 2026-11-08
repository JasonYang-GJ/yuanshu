using System.Runtime.InteropServices;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.Vision.Windows;

internal sealed record VisualSearchWindowBounds(int X, int Y, int Width, int Height);
internal sealed record VisualSearchClickReceipt(uint LastInputTime, long FocusHandle);

internal interface IVisualSearchInputDriver
{
    void Activate(long window, CancellationToken token);
    void RequireForeground(long window);
    VisualSearchWindowBounds GetBounds(long window);
    bool HasSearchCaret(long window, VisualSearchWindowBounds bounds, VisualSearchRect field);
    VisualSearchClickReceipt Click(long window, VisualSearchWindowBounds bounds, VisualSearchRect field, Action validate, CancellationToken token);
    void ReplaceText(long window, string query, VisualSearchClickReceipt click, Action validate, CancellationToken token);
}

internal sealed class WindowsVisualSearchInputDriver : IVisualSearchInputDriver
{
    public void Activate(long window, CancellationToken token)
        => ActivateWindow(window, token, VisibleWindowActivation.TryActivate, RequireForeground);

    internal static void ActivateWindow(long window, CancellationToken token,
        Func<IntPtr, TimeSpan, CancellationToken, bool> activate, Action<long> requireForeground)
    {
        token.ThrowIfCancellationRequested();
        if (!activate(new(window), TimeSpan.FromSeconds(2), token)) throw Changed();
        token.ThrowIfCancellationRequested();
        requireForeground(window);
    }

    public void RequireForeground(long window)
    {
        if (GetForegroundWindow().ToInt64() != window) throw Changed();
    }

    public VisualSearchWindowBounds GetBounds(long window)
    {
        using var dpi = new PhysicalCoordinates();
        if (DwmGetWindowAttribute(new(window), 9, out var rect, Marshal.SizeOf<Rect>()) != 0
            || rect.Right <= rect.Left || rect.Bottom <= rect.Top) throw Changed();
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public bool HasSearchCaret(long window, VisualSearchWindowBounds bounds, VisualSearchRect field)
    {
        using var dpi = new PhysicalCoordinates();
        if (!dpi.IsActive || window == 0) return false;
        try
        {
            if (GetForegroundWindow().ToInt64() != window || GetBounds(window) != bounds) return false;
            var thread = GetWindowThreadProcessId(new(window), out _);
            var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
            if (thread == 0 || !GetGUIThreadInfo(thread, ref info)
                || info.Focus == IntPtr.Zero || info.Caret == IntPtr.Zero
                || info.CaretBounds.Right <= info.CaretBounds.Left
                || info.CaretBounds.Bottom <= info.CaretBounds.Top) return false;

            // rcCaret belongs to hwndCaret's client area, not the main window's client
            // area. Convert both corners under the same physical-coordinate context.
            var topLeft = new Point { X = info.CaretBounds.Left, Y = info.CaretBounds.Top };
            var bottomRight = new Point { X = info.CaretBounds.Right, Y = info.CaretBounds.Bottom };
            if (!ClientToScreen(info.Caret, ref topLeft) || !ClientToScreen(info.Caret, ref bottomRight)
                || !IsCaretInsideSearchField(info.Flags, window,
                    GetAncestor(info.Caret, 2).ToInt64(), GetAncestor(info.Focus, 2).ToInt64(), bounds, field,
                    topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y)) return false;

            // A focus/caret/window change during conversion invalidates this observation.
            var current = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
            return GetGUIThreadInfo(thread, ref current)
                && current.Focus == info.Focus && current.Caret == info.Caret
                && (current.Flags & 1) != 0
                && current.CaretBounds.Left == info.CaretBounds.Left && current.CaretBounds.Top == info.CaretBounds.Top
                && current.CaretBounds.Right == info.CaretBounds.Right && current.CaretBounds.Bottom == info.CaretBounds.Bottom
                && GetAncestor(current.Caret, 2).ToInt64() == window
                && GetAncestor(current.Focus, 2).ToInt64() == window
                && GetForegroundWindow().ToInt64() == window && GetBounds(window) == bounds;
        }
        catch (DesktopSearchException)
        {
            return false;
        }
    }

    internal static bool IsCaretInsideSearchField(uint flags, long window, long caretRoot, long focusRoot,
        VisualSearchWindowBounds bounds, VisualSearchRect field, int left, int top, int right, int bottom)
    {
        const uint GuiCaretBlinking = 1;
        if ((flags & GuiCaretBlinking) == 0 || window == 0 || caretRoot != window || focusRoot != window
            || bounds.Width <= 0 || bounds.Height <= 0 || field.X < 0 || field.Y < 0
            || field.Width <= 0 || field.Height <= 0
            || (long)field.X + field.Width > bounds.Width || (long)field.Y + field.Height > bounds.Height
            || right <= left || bottom <= top) return false;
        var fieldLeft = (long)bounds.X + field.X;
        var fieldTop = (long)bounds.Y + field.Y;
        // Native carets may be only 2 px high. Require a nonempty rectangle wholly
        // inside the identified field, without imposing image-blink shape heuristics.
        return left > fieldLeft && top > fieldTop
            && right < fieldLeft + field.Width && bottom < fieldTop + field.Height;
    }

    public VisualSearchClickReceipt Click(long window, VisualSearchWindowBounds bounds, VisualSearchRect field,
        Action validate, CancellationToken token)
    {
        using var dpi = new PhysicalCoordinates();
        Check();
        var point = new Point { X = bounds.X + field.X + field.Width / 2, Y = bounds.Y + field.Y + field.Height / 2 };
        if (GetAncestor(WindowFromPoint(point), 2).ToInt64() != window) throw Changed();
        WithReleasedMouseButtons(() =>
        {
            if (!SetPhysicalCursorPos(point.X, point.Y)) throw Changed();
        }, GetAsyncKeyState);
        Check();
        if (!GetCursorPos(out var actual) || actual.X != point.X || actual.Y != point.Y
            || GetAncestor(WindowFromPoint(actual), 2).ToInt64() != window) throw Changed();
        WithReleasedMouseButtons(() =>
            Send(new Input { Type = 0, Data = new Union { Mouse = new Mouse { Flags = 2 } } }), GetAsyncKeyState);
        try { if (token.WaitHandle.WaitOne(60)) token.ThrowIfCancellationRequested(); }
        finally { Send(new Input { Type = 0, Data = new Union { Mouse = new Mouse { Flags = 4 } } }); }
        Check();
        var last = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        var info = ReadFocus(window);
        if (!GetLastInputInfo(ref last)) throw Changed();
        return new(last.Time, info.Focus.ToInt64());
        void Check()
        {
            token.ThrowIfCancellationRequested();
            validate();
            RequireForeground(window);
            RequireNoModifiers();
            if (GetBounds(window) != bounds) throw Changed();
        }
    }

    public void ReplaceText(long window, string query, VisualSearchClickReceipt click, Action validate, CancellationToken token)
    {
        Check();
        RequireNoModifiers();
        var last = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref last) || last.Time != click.LastInputTime) throw Changed();
        // One bounded input batch after the caret check. No Enter/Tab, clipboard, or retry.
        var input = new List<Input> { Key(0x11), Key(0x41), Key(0x41, 2), Key(0x11, 2) };
        foreach (var character in query) { input.Add(Unicode(character)); input.Add(Unicode(character, 2)); }
        Check();
        try { Send(input.ToArray()); }
        finally { if ((GetAsyncKeyState(0x11) & 0x8000) != 0) Send(Key(0x11, 2)); }
        Check();
        void Check()
        {
            token.ThrowIfCancellationRequested();
            validate();
            RequireForeground(window);
            if (ReadFocus(window).Focus.ToInt64() != click.FocusHandle) throw Changed();
        }
    }

    private static GuiInfo ReadFocus(long window)
    {
        var thread = GetWindowThreadProcessId(new(window), out _);
        var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.Focus == IntPtr.Zero
            || GetAncestor(info.Focus, 2).ToInt64() != window) throw Changed();
        return info;
    }

    private static Input Key(ushort key, uint flags = 0) => new()
    { Type = 1, Data = new Union { Keyboard = new Keyboard { VirtualKey = key, Flags = flags } } };
    private static Input Unicode(char character, uint flags = 0) => new()
    { Type = 1, Data = new Union { Keyboard = new Keyboard { Scan = character, Flags = 4 | flags } } };
    private static void Send(params Input[] input)
    {
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length) throw Changed();
    }
    private static void RequireNoModifiers()
    {
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0)) throw Changed();
    }
    internal static void WithReleasedMouseButtons(Action action, Func<int, short> keyState)
    {
        // Moving a pointer while the user holds a button can drag/drop unrelated content.
        // Check again immediately before our own down event, but never block its paired up.
        if (new[] { 0x01, 0x02, 0x04, 0x05, 0x06 }.Any(key => (keyState(key) & 0x8000) != 0))
            throw new DesktopSearchException(DesktopSearchErrorCodes.FocusChanged,
                "鼠标按钮仍处于按下状态，已停止本次搜索操作。");
        action();
    }
    private static DesktopSearchException Changed() => new(DesktopSearchErrorCodes.FocusChanged,
        "搜索窗口、鼠标目标或键盘焦点变化，已停止输入。");

    private sealed class PhysicalCoordinates : IDisposable
    {
        private readonly IntPtr _previous = SetThreadDpiAwarenessContext(new(-4));
        public bool IsActive => _previous != IntPtr.Zero;
        public void Dispose() { if (_previous != IntPtr.Zero) SetThreadDpiAwarenessContext(_previous); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    { public uint Size, Flags; public IntPtr Active, Focus, Capture, Menu, MoveSize, Caret; public Rect CaretBounds; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Explicit)] private struct Union
    { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse
    { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard
    { public ushort VirtualKey, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect rect, int size);
}
