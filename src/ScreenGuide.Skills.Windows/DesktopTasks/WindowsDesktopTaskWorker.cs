using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

/// <summary>
/// Synchronous private worker entry point. Host supplies authorization and owns the
/// short-lived process, deadline and cancellation; this class never steals focus.
/// </summary>
public sealed class WindowsDesktopTaskWorker
{
    private readonly IDesktopTaskNativeRuntime _runtime;
    public WindowsDesktopTaskWorker() : this(new WindowsDesktopTaskNativeRuntime()) { }
    internal WindowsDesktopTaskWorker(IDesktopTaskNativeRuntime runtime) => _runtime = runtime;

    public DesktopTaskWorkerResponse Execute(DesktopTaskWorkerRequest request)
    {
        try
        {
            if (request is null || request.WindowHandle == 0)
                throw DesktopTaskSafety.Error("invalid_request", "桌面任务请求缺少明确窗口。");
            if (request.Operation == "Bind")
            {
                var window = _runtime.ReadWindow(request.WindowHandle);
                DesktopTaskSafety.RequireWindow(window);
                return new(true, Window: window);
            }
            if (request.Window is not { } expected || expected.Handle != request.WindowHandle)
                throw DesktopTaskSafety.Error("invalid_request", "桌面任务窗口与绑定目标不一致。");
            return request.Operation switch
            {
                "Observe" => new(true, Snapshot: Observe(expected)),
                "Execute" when request.Control is not null && request.Action is not null =>
                    new(true, Receipt: Perform(expected, request.Control, request.Action, request.ReviewedContextFingerprint)),
                _ => throw DesktopTaskSafety.Error("invalid_request", "不支持这类桌面任务请求。")
            };
        }
        catch (DesktopTaskException exception)
        {
            return new(false, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException
            or ArgumentException or UnauthorizedAccessException or Win32Exception or COMException or NotSupportedException)
        {
            return new(false, "desktop_task_automation_unavailable", "目标控件当前不可用，流程已停止；已经发生的操作不会自动撤销。");
        }
    }

    private DesktopTaskSnapshot Observe(DesktopTaskWindow expected)
    {
        var window = RequireWindow(expected);
        using var excelObservation = ExcelDesktopTaskCell.BeginObservation();
        var root = _runtime.Root(window.Handle);
        var id = Guid.NewGuid();
        var controls = new List<DesktopTaskControl>();
        var traversal = new TraversalState();
        var remainingText = DesktopTaskSafety.MaximumText;
        foreach (var element in Walk(root, traversal, () => RequireWindow(expected)))
        {
            RequireWindow(expected);
            var metadata = element.Metadata();
            if (!DesktopTaskSafety.Usable(metadata) || !_runtime.BelongsToWindow(element, root, window.Handle)) continue;
            var details = element.Details(DesktopTaskSafety.MaximumValue);
            var control = DesktopTaskSafety.SnapshotControl(id, controls.Count, metadata, details, out var clipped);
            var size = DesktopTaskSafety.TextLength(control);
            if (size > remainingText) { traversal.Truncated = true; break; }
            if (control.Actions.Count == 0 && string.IsNullOrWhiteSpace(control.Name) && string.IsNullOrWhiteSpace(control.Value)) continue;
            controls.Add(control);
            remainingText -= size;
            traversal.Truncated |= clipped;
        }
        var final = RequireWindow(expected);
        return new(id, final, controls, traversal.Truncated);
    }

    private DesktopTaskActionReceipt Perform(DesktopTaskWindow window, DesktopTaskControl expected, DesktopTaskProposal action,
        string? reviewedContextFingerprint)
    {
        if (reviewedContextFingerprint is not { Length: 64 }
            || !reviewedContextFingerprint.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))
            throw DesktopTaskSafety.Error("invalid_context", "动作缺少本机审核过的完整窗口快照指纹，未执行操作。");
        if (expected.RuntimeId is null || expected.Actions is null || expected.Bounds is null
            || expected.Name is null || expected.AutomationId is null || expected.ParentName is null
            || expected.ControlType is null || expected.State is null || expected.Value is null
            || !DesktopTaskSafety.IsSnapshotControlId(expected.Id, action.SnapshotId)
            || action.TargetId != expected.Id || expected.RuntimeId.Length is < 1 or > 64
            || action.Action is DesktopTaskActionKind.Wait or DesktopTaskActionKind.Ask or DesktopTaskActionKind.Finish)
            throw DesktopTaskSafety.Error("invalid_target", "动作没有绑定当前快照中的可靠控件。");
        if (action.Action == DesktopTaskActionKind.SetText)
        {
            DesktopTaskSafety.RequireText(action.Text);
            if (expected.ControlType == "DataItem") DesktopTaskGridCell.RequirePlainText(action.Text);
        }
        else if (action.Text is not null)
            throw DesktopTaskSafety.Error("invalid_request", "这个控件动作不接受附加输入文字。");
        RequireWindow(window);
        var root = _runtime.Root(window.Handle);
        var traversal = new TraversalState();
        var found = new List<IDesktopTaskNativeElement>();
        foreach (var element in Walk(root, traversal, () => RequireWindow(window)))
        {
            var metadata = element.Metadata();
            if (metadata.RuntimeId.SequenceEqual(expected.RuntimeId)) found.Add(element);
        }
        if (found.Count != 1 || traversal.DuplicateRuntimeId)
            throw DesktopTaskSafety.Error("control_changed", "无法唯一重新找到已确认的控件，请重新观察窗口。");
        var target = found[0];
        RequireControl(window, root, target, expected, action);
        // The last boundary check precedes the only native action. No retries after
        // a native call, including failures where the provider may have acted.
        RequireControl(window, root, target, expected, action);
        // A stable button is insufficient: other fields or selection may have
        // changed since Host reviewed the step. The native driver invokes this
        // callback after acquiring its Pattern and before its final call boundary.
        target.Perform(action.Action, action.Text, expected, () =>
        {
            var current = Observe(window);
            if (!string.Equals(reviewedContextFingerprint, DesktopTaskContextFingerprint.Compute(current), StringComparison.Ordinal))
                throw DesktopTaskSafety.Error("context_changed", "审核后窗口内容、选中对象或状态发生变化，未执行动作，请重新观察。");
        });
        RequireWindow(window);
        if (action.Action == DesktopTaskActionKind.SetText)
        {
            var metadata = target.Metadata();
            if (!DesktopTaskSafety.Usable(metadata) || !_runtime.BelongsToWindow(target, root, window.Handle))
                throw DesktopTaskSafety.Error("control_changed", "文字已提交，但目标控件发生变化，无法确认输入结果。");
            var details = target.Details(DesktopTaskSafety.MaximumValue);
            var actual = DesktopTaskSafety.SnapshotControl(action.SnapshotId, 0, metadata, details, out var clipped);
            if (clipped || !DesktopTaskSafety.SameControl(expected, actual) || actual.Value != action.Text)
                throw DesktopTaskSafety.Error("value_not_verified", "文字已提交，但未准确读回相同内容，流程已停止。");
            RequireWindow(window);
            return new(true, "已填写目标文本框并准确读回输入内容；需要重新观察确认后续任务结果。");
        }
        return new(false, "已调用目标控件支持的操作；需要重新观察窗口确认实际结果，尚未判定任务完成。");
    }

    private void RequireControl(DesktopTaskWindow window, IDesktopTaskNativeElement root, IDesktopTaskNativeElement target,
        DesktopTaskControl expected, DesktopTaskProposal action)
    {
        RequireWindow(window);
        var metadata = target.Metadata();
        if (!DesktopTaskSafety.Usable(metadata) || !_runtime.BelongsToWindow(target, root, window.Handle))
            throw DesktopTaskSafety.Error("control_changed", "目标控件已变化或不属于当前授权窗口，已停止操作。");
        var details = target.Details(DesktopTaskSafety.MaximumValue);
        var actual = DesktopTaskSafety.SnapshotControl(action.SnapshotId, 0, metadata, details, out var clipped);
        if (action.Action == DesktopTaskActionKind.SetText && clipped
            || !DesktopTaskSafety.SameControl(expected, actual) || !actual.Actions.Contains(action.Action))
            throw DesktopTaskSafety.Error("control_changed", "控件身份、状态或支持的操作发生变化，请重新观察窗口。");
        if (action.Action == DesktopTaskActionKind.SetText && (actual.IsReadOnly
            || !DesktopTaskSafety.IsTextInputType(metadata.ControlType, details.IsStructuredCell)
            || actual.Value != expected.Value))
            throw DesktopTaskSafety.Error("value_changed", "文本框内容或只读状态发生变化，未覆盖新的内容。");
        RequireWindow(window);
    }

    private DesktopTaskWindow RequireWindow(DesktopTaskWindow expected)
    {
        DesktopTaskSafety.RequireWindow(expected);
        var current = _runtime.ReadWindow(expected.Handle);
        DesktopTaskSafety.RequireWindow(current);
        if (!DesktopTaskSafety.SameWindow(expected, current))
            throw DesktopTaskSafety.Error("window_changed", "绑定窗口或进程身份发生变化，流程已停止。");
        if (_runtime.ForegroundWindowHandle != DesktopTaskSafety.ForegroundHandle(expected))
            throw DesktopTaskSafety.Error("foreground_changed", "你已切换到其他窗口，桌面任务已停止，未切回或继续操作。");
        return current;
    }

    private static IEnumerable<IDesktopTaskNativeElement> Walk(IDesktopTaskNativeElement root, TraversalState state,
        Func<DesktopTaskWindow> validateWindow)
    {
        var queue = new Queue<(IDesktopTaskNativeElement Element, int Depth)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0)
        {
            if (visited++ >= DesktopTaskSafety.MaximumControls) { state.Truncated = true; yield break; }
            var (element, depth) = queue.Dequeue();
            _ = validateWindow();
            var metadata = element.Metadata();
            if (!seen.Add(string.Join('.', metadata.RuntimeId))) { state.DuplicateRuntimeId = true; state.Truncated = true; continue; }
            // Do not inspect values or traverse the subtree of sensitive controls.
            if (DesktopTaskSafety.Sensitive(metadata) || metadata.Offscreen) continue;
            yield return element;
            // Scrollbar arrow/page buttons are transient implementation details,
            // not separate task targets. Keep the scrollbar itself observable;
            // scrolling uses only an element's declared ScrollPattern actions.
            if (metadata.ControlType == "ScrollBar") continue;
            if (depth >= DesktopTaskSafety.MaximumDepth) { state.Truncated = true; continue; }
            using var children = element.Children().GetEnumerator();
            while (true)
            {
                _ = validateWindow();
                if (!children.MoveNext()) break;
                if (visited + queue.Count >= DesktopTaskSafety.MaximumControls) { state.Truncated = true; break; }
                queue.Enqueue((children.Current, depth + 1));
            }
        }
    }

    private sealed class TraversalState
    {
        public bool Truncated { get; set; }
        public bool DuplicateRuntimeId { get; set; }
    }
}
