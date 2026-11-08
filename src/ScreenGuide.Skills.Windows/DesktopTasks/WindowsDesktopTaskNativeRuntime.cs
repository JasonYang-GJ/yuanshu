using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

internal sealed class WindowsDesktopTaskNativeRuntime : IDesktopTaskNativeRuntime
{
    public long ForegroundWindowHandle => Native.GetForegroundWindow().ToInt64();

    internal DesktopTaskWindow ResolveLaunchedWindow(long handle, int openedProcessId)
    {
        if (openedProcessId <= 0) throw DesktopTaskSafety.Error("window_unavailable", "启动结果缺少明确进程身份。");
        var foreground = ReadMetadata(handle);
        if (foreground.Window.ProcessId == openedProcessId) return ReadWindow(handle);
        if (!DesktopTaskWindowBinding.IsTrustedFrame(foreground))
            throw DesktopTaskSafety.Error("window_unavailable", "当前窗口不属于已启动的应用或可信系统宿主。");
        return ResolveFrameContent(foreground, openedProcessId);
    }

    private DesktopTaskWindow ResolveFrameContent(DesktopTaskNativeWindowMetadata frame, int? openedProcessId)
    {
        var (children, truncated) = ReadDirectCoreWindows(frame.Window.Handle);
        var bound = openedProcessId is { } processId
            ? DesktopTaskWindowBinding.ResolveChild(frame, children, processId, truncated)
            : DesktopTaskWindowBinding.ResolveSingleContent(frame, children, truncated);
        var verified = ReadWindow(bound.Handle);
        if (!DesktopTaskSafety.SameWindow(bound, verified)
            || !DesktopTaskWindowBinding.SameMetadata(frame, ReadMetadata(frame.Window.Handle)))
            throw DesktopTaskSafety.Error("window_changed", "解析期间应用内容窗或系统宿主身份发生变化。");
        return verified;
    }

    private static (IReadOnlyList<DesktopTaskNativeWindowMetadata> Children, bool Truncated) ReadDirectCoreWindows(long handle)
    {
        var children = new List<DesktopTaskNativeWindowMetadata>();
        var seen = new HashSet<long>();
        var child = Native.GetWindow(new IntPtr(handle), 5);
        var count = 0;
        while (child != IntPtr.Zero && count++ < DesktopTaskWindowBinding.MaximumFrameChildren)
        {
            if (!seen.Add(child.ToInt64())) throw DesktopTaskSafety.Error("window_changed", "系统宿主的直接子窗口关系不稳定。");
            var className = new StringBuilder(256);
            _ = Native.GetClassName(child, className, className.Capacity);
            if (className.ToString() == "Windows.UI.Core.CoreWindow") children.Add(ReadMetadata(child.ToInt64()));
            child = Native.GetWindow(child, 2);
        }
        return (children, child != IntPtr.Zero);
    }

    public DesktopTaskWindow ReadWindow(long handle)
    {
        var content = ReadMetadata(handle);
        if (DesktopTaskWindowBinding.IsTrustedFrame(content)) return ResolveFrameContent(content, null);
        var frame = content.RootHandle == handle ? null : ReadMetadata(content.RootHandle);
        var bound = DesktopTaskWindowBinding.Bind(content, frame);
        // Verify both process identities and the direct parent relationship again
        // before exposing a binding; no UIA content is read in this path.
        if (!DesktopTaskWindowBinding.SameMetadata(content, ReadMetadata(handle))
            || frame is not null && !DesktopTaskWindowBinding.SameMetadata(frame, ReadMetadata(frame.Window.Handle)))
            throw DesktopTaskSafety.Error("window_changed", "绑定期间窗口或系统宿主身份发生变化，流程已停止。");
        return bound;
    }

    private static DesktopTaskNativeWindowMetadata ReadMetadata(long handle)
    {
        var hwnd = new IntPtr(handle);
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
            throw DesktopTaskSafety.Error("window_unavailable", "目标窗口不存在或已失效。");
        _ = Native.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId is 0 or > int.MaxValue)
            throw DesktopTaskSafety.Error("window_unavailable", "无法识别目标窗口所属进程。");
        using var process = Process.GetProcessById((int)processId);
        var started = new DateTimeOffset(process.StartTime.ToUniversalTime());
        var path = process.MainModule?.FileName;
        if (path is null || !Path.IsPathFullyQualified(path))
            throw DesktopTaskSafety.Error("window_unavailable", "无法核实目标程序路径。");
        var title = new StringBuilder(512);
        _ = Native.GetWindowText(hwnd, title, title.Capacity);
        var className = new StringBuilder(256);
        _ = Native.GetClassName(hwnd, className, className.Capacity);
        _ = Native.GetWindowThreadProcessId(hwnd, out var afterId);
        if (!Native.IsWindow(hwnd) || process.HasExited || afterId != processId
            || new DateTimeOffset(process.StartTime.ToUniversalTime()) != started)
            throw DesktopTaskSafety.Error("window_changed", "窗口所属进程已变化，流程已停止。");
        return new(new(handle, (int)processId, process.ProcessName, started, Path.GetFullPath(path), className.ToString(), title.ToString()),
            Native.GetAncestor(hwnd, 1).ToInt64(), Native.GetAncestor(hwnd, 2).ToInt64(),
            (Native.GetWindowLong(hwnd, -16) & 0x40000000) != 0);
    }

    public IDesktopTaskNativeElement Root(long handle)
    {
        var window = ReadWindow(handle);
        var root = AutomationElement.FromHandle(new IntPtr(handle))
            ?? throw DesktopTaskSafety.Error("automation_unavailable", "目标窗口没有提供可读取的控件接口。");
        return new WindowsDesktopTaskNativeElement(root, root, window, this);
    }

    public bool BelongsToWindow(IDesktopTaskNativeElement element, IDesktopTaskNativeElement root, long handle) =>
        element is WindowsDesktopTaskNativeElement native && root is WindowsDesktopTaskNativeElement nativeRoot
        && native.RootHandle == handle && nativeRoot.RootHandle == handle
        && DesktopTaskSafety.SameWindow(native.BoundWindow, nativeRoot.BoundWindow)
        && Native.Belongs(native.Element, nativeRoot.Element, nativeRoot.BoundWindow);

    internal static class Native
    {
        internal static bool Belongs(AutomationElement element, AutomationElement root, DesktopTaskWindow window)
        {
            var hwnd = new IntPtr(window.Handle);
            if (root.Current.NativeWindowHandle != window.Handle || root.Current.ProcessId != window.ProcessId || !IsBoundStructure(window)) return false;
            var handle = element.Current.NativeWindowHandle;
            // Native descendants must remain under the content HWND itself.
            // Frame siblings and the ApplicationFrameHost root are not authorized.
            if (handle != 0 && handle != window.Handle && !IsChild(hwnd, new IntPtr(handle))) return false;
            var rootId = root.GetRuntimeId();
            var cursor = element;
            for (var depth = 0; cursor is not null && depth < 64; depth++)
            {
                if (cursor.GetRuntimeId().SequenceEqual(rootId)) return true;
                cursor = TreeWalker.RawViewWalker.GetParent(cursor);
            }
            return false;
        }

        private static bool IsBoundStructure(DesktopTaskWindow window)
        {
            var hwnd = new IntPtr(window.Handle);
            if (!IsWindow(hwnd)) return false;
            _ = GetWindowThreadProcessId(hwnd, out var processId);
            if (processId != window.ProcessId) return false;
            if (window.FrameIdentity is not { } frame)
                return GetAncestor(hwnd, 2) == hwnd && (GetWindowLong(hwnd, -16) & 0x40000000) == 0;
            var parent = new IntPtr(frame.Handle);
            if (!IsWindow(parent) || (GetWindowLong(hwnd, -16) & 0x40000000) == 0
                || GetAncestor(hwnd, 1) != parent || GetAncestor(hwnd, 2) != parent || GetAncestor(parent, 2) != parent)
                return false;
            _ = GetWindowThreadProcessId(parent, out var frameProcessId);
            return frameProcessId == frame.ProcessId;
        }

        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder text, int length);
    }
}

internal sealed class WindowsDesktopTaskNativeElement(AutomationElement element, AutomationElement root,
    DesktopTaskWindow window, WindowsDesktopTaskNativeRuntime runtime)
    : IDesktopTaskNativeElement
{
    internal AutomationElement Element => element;
    internal long RootHandle => window.Handle;
    internal DesktopTaskWindow BoundWindow => window;

    public DesktopTaskElementMetadata Metadata()
    {
        RequireBelongs();
        var current = element.Current;
        var password = current.IsPassword;
        var parent = element.GetRuntimeId().SequenceEqual(root.GetRuntimeId()) ? null : TreeWalker.ControlViewWalker.GetParent(element);
        var rectangle = current.BoundingRectangle;
        return new(element.GetRuntimeId(), current.Name ?? string.Empty, current.AutomationId ?? string.Empty,
            current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal),
            parent?.Current.Name ?? string.Empty,
            new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height), current.IsEnabled, current.IsOffscreen,
            password, current.ProcessId, current.NativeWindowHandle);
    }

    public DesktopTaskElementDetails Details(int maximumTextLength)
    {
        var metadata = Metadata();
        if (!DesktopTaskSafety.Usable(metadata))
            throw DesktopTaskSafety.Error("sensitive_control", "该控件不可读取或不支持自动操作。");
        if (WordDesktopTaskBody.IsCandidate(window, metadata))
            return WordDesktopTaskBody.Read(element, root, window, maximumTextLength);
        if (ExcelDesktopTaskCell.IsExcelDataItem(window, metadata))
            return ExcelDesktopTaskCell.IsCandidate(window, metadata)
                ? ExcelDesktopTaskCell.Read(element, root, window, maximumTextLength)
                : new(string.Empty, true, string.Empty, []);
        var actions = new List<DesktopTaskActionKind>();
        var states = new List<string>();
        var value = string.Empty;
        var readOnly = true;
        var clipped = false;
        var gridBinding = string.Empty;
        var isStructuredCell = metadata.ControlType == "DataItem"
            && DesktopTaskGridCell.TryReadBinding(element, root, window, out gridBinding);
        if (isStructuredCell) states.Add("gridBinding=" + gridBinding);
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawValue))
        {
            var pattern = (ValuePattern)rawValue;
            readOnly = pattern.Current.IsReadOnly;
            // Never read a Value/TextPattern before the fresh password/sensitivity check.
            value = pattern.Current.Value ?? string.Empty;
            if (!readOnly && DesktopTaskSafety.IsTextInputType(metadata.ControlType, isStructuredCell)) actions.Add(DesktopTaskActionKind.SetText);
        }
        else if (element.TryGetCurrentPattern(TextPattern.Pattern, out var rawText)
            && !IsWordPageTextMirror(metadata))
        {
            value = ((TextPattern)rawText).DocumentRange.GetText(maximumTextLength + 1) ?? string.Empty;
        }
        if (value.Length > maximumTextLength) { value = value[..maximumTextLength]; clipped = true; }
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out _)) actions.Add(DesktopTaskActionKind.Invoke);
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var rawSelection))
        {
            actions.Add(DesktopTaskActionKind.Select);
            states.Add("selected=" + ((SelectionItemPattern)rawSelection).Current.IsSelected);
        }
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var rawExpansion))
        {
            var expansion = ((ExpandCollapsePattern)rawExpansion).Current.ExpandCollapseState;
            states.Add("expansion=" + expansion);
            if (expansion != ExpandCollapseState.LeafNode)
            {
                actions.Add(DesktopTaskActionKind.Expand);
                actions.Add(DesktopTaskActionKind.Collapse);
            }
        }
        if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var rawScroll))
        {
            var scroll = ((ScrollPattern)rawScroll).Current;
            states.Add("vertical=" + scroll.VerticalScrollPercent.ToString("R", CultureInfo.InvariantCulture));
            states.Add("horizontal=" + scroll.HorizontalScrollPercent.ToString("R", CultureInfo.InvariantCulture));
            if (scroll.VerticallyScrollable)
            {
                if (scroll.VerticalScrollPercent > 0) actions.Add(DesktopTaskActionKind.ScrollUp);
                if (scroll.VerticalScrollPercent < 100) actions.Add(DesktopTaskActionKind.ScrollDown);
            }
        }
        return new(value, readOnly, string.Join(';', states), actions, clipped, isStructuredCell);
    }

    private bool IsWordPageTextMirror(DesktopTaskElementMetadata metadata)
    {
        if (!WordDesktopTaskBody.IsPageContainer(window, metadata)) return false;
        // Word's page TextPattern can temporarily return empty text while its
        // native body Range is held. Only this precisely bound duplicate is
        // omitted; the body still reads the complete main story through COM.
        // Keep the page's identity, geometry, states and actions in the snapshot.
        var children = new List<DesktopTaskElementMetadata>();
        var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
        while (child is not null)
        {
            if (children.Count >= DesktopTaskSafety.MaximumControls) return false;
            RequireBelongs();
            var parent = TreeWalker.ControlViewWalker.GetParent(child);
            if (parent is null || !parent.GetRuntimeId().SequenceEqual(metadata.RuntimeId)) return false;
            // Metadata() independently checks that every child belongs to this
            // same bound window; matching labels or process IDs are insufficient.
            children.Add(new WindowsDesktopTaskNativeElement(child, root, window, runtime).Metadata());
            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
        }
        RequireBelongs();
        return WordDesktopTaskBody.IsPageTextMirror(window, metadata, children);
    }

    public IEnumerable<IDesktopTaskNativeElement> Children()
    {
        RequireBelongs();
        var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
        for (var count = 0; child is not null && count <= DesktopTaskSafety.MaximumControls; count++)
        {
            yield return new WindowsDesktopTaskNativeElement(child, root, window, runtime);
            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
        }
    }

    public void Perform(DesktopTaskActionKind action, string? text, DesktopTaskControl expected, Action verifyContext)
    {
        RequireActionBoundary(action, expected);
        switch (action)
        {
            case DesktopTaskActionKind.Invoke:
                var invoke = Pattern<InvokePattern>(InvokePattern.Pattern);
                verifyContext();
                RequireActionBoundary(action, expected);
                invoke.Invoke();
                break;
            case DesktopTaskActionKind.SetText:
                DesktopTaskSafety.RequireText(text);
                if (ExcelDesktopTaskCell.IsExcelDataItem(window, Metadata()))
                {
                    ExcelDesktopTaskCell.SetText(element, root, window, expected, text!, verifyContext,
                        () => RequireActionBoundary(action, expected));
                    break;
                }
                var gridBinding = string.Empty;
                var isStructuredCell = expected.ControlType == "DataItem"
                    && DesktopTaskGridCell.TryReadBinding(element, root, window, out gridBinding);
                if (!DesktopTaskSafety.IsTextInputType(expected.ControlType, isStructuredCell)) throw Unsupported();
                if (isStructuredCell)
                {
                    DesktopTaskGridCell.RequirePlainText(text);
                    DesktopTaskGridCell.RequireBinding(expected.State, gridBinding);
                }
                if (WordDesktopTaskBody.IsCandidate(window, Metadata()))
                {
                    WordDesktopTaskBody.SetText(element, root, window, expected, text!, verifyContext,
                        () => RequireActionBoundary(action, expected));
                    break;
                }
                var value = Pattern<ValuePattern>(ValuePattern.Pattern);
                if (value.Current.IsReadOnly || value.Current.Value != expected.Value)
                    throw DesktopTaskSafety.Error("value_changed", "文本框内容或只读状态已变化，未覆盖新内容。");
                verifyContext();
                if (isStructuredCell)
                {
                    if (!DesktopTaskGridCell.TryReadBinding(element, root, window, out var currentBinding)) throw Unsupported();
                    DesktopTaskGridCell.RequireBinding(expected.State, currentBinding);
                }
                RequireActionBoundary(action, expected);
                value.SetValue(text!);
                break;
            case DesktopTaskActionKind.Select:
                var selection = Pattern<SelectionItemPattern>(SelectionItemPattern.Pattern);
                verifyContext();
                RequireActionBoundary(action, expected);
                selection.Select();
                break;
            case DesktopTaskActionKind.Expand:
            case DesktopTaskActionKind.Collapse:
                var expansion = Pattern<ExpandCollapsePattern>(ExpandCollapsePattern.Pattern);
                if (expansion.Current.ExpandCollapseState == ExpandCollapseState.LeafNode) throw Unsupported();
                verifyContext();
                RequireActionBoundary(action, expected);
                if (action == DesktopTaskActionKind.Expand) expansion.Expand(); else expansion.Collapse();
                break;
            case DesktopTaskActionKind.ScrollUp:
            case DesktopTaskActionKind.ScrollDown:
                var scroll = Pattern<ScrollPattern>(ScrollPattern.Pattern);
                if (!scroll.Current.VerticallyScrollable) throw Unsupported();
                verifyContext();
                RequireActionBoundary(action, expected);
                scroll.Scroll(ScrollAmount.NoAmount,
                    action == DesktopTaskActionKind.ScrollUp ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement);
                break;
            default: throw Unsupported();
        }
    }

    private T Pattern<T>(AutomationPattern pattern) where T : class =>
        element.TryGetCurrentPattern(pattern, out var value) && value is T typed ? typed : throw Unsupported();

    private void RequireActionBoundary(DesktopTaskActionKind action, DesktopTaskControl expected)
    {
        var currentWindow = runtime.ReadWindow(window.Handle);
        DesktopTaskSafety.RequireWindow(currentWindow);
        if (!DesktopTaskSafety.SameWindow(window, currentWindow))
            throw DesktopTaskSafety.Error("window_changed", "调用前应用窗口或系统宿主身份发生变化，未执行动作。");
        RequireBelongs();
        if (WindowsDesktopTaskNativeRuntime.Native.GetForegroundWindow().ToInt64() != DesktopTaskSafety.ForegroundHandle(window))
            throw DesktopTaskSafety.Error("foreground_changed", "前台窗口已切换，流程已停止，没有切回窗口。");
        var metadata = Metadata();
        if (!metadata.Enabled || !DesktopTaskSafety.Usable(metadata)) throw Unsupported();
        var details = Details(DesktopTaskSafety.MaximumValue);
        var actual = DesktopTaskSafety.SnapshotControl(Guid.Empty, 0, metadata, details, out var clipped);
        if (!DesktopTaskSafety.SameControl(expected, actual) || !actual.Actions.Contains(action)
            || action == DesktopTaskActionKind.SetText && (clipped || actual.IsReadOnly
                || !DesktopTaskSafety.IsTextInputType(metadata.ControlType, details.IsStructuredCell) || actual.Value != expected.Value))
            throw DesktopTaskSafety.Error("control_changed", "调用前控件身份、状态或原有内容发生变化，未执行动作。");
        if (WindowsDesktopTaskNativeRuntime.Native.GetForegroundWindow().ToInt64() != DesktopTaskSafety.ForegroundHandle(window))
            throw DesktopTaskSafety.Error("foreground_changed", "前台窗口已切换，流程已停止，没有切回窗口。");
    }

    private void RequireBelongs()
    {
        if (!WindowsDesktopTaskNativeRuntime.Native.Belongs(element, root, window))
            throw DesktopTaskSafety.Error("control_changed", "控件不再属于原来绑定的窗口，流程已停止。");
    }

    private static DesktopTaskException Unsupported() => DesktopTaskSafety.Error("action_not_supported", "控件没有可靠提供所需操作，未使用猜测点击或键盘替代。");
}
