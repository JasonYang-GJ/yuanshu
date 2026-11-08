using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

// These interfaces keep the worker's authority checks independent of UIA/COM.
// A worker is short-lived and owns no durable session or authorization state.
internal interface IDesktopTaskNativeRuntime
{
    DesktopTaskWindow ReadWindow(long handle);
    long ForegroundWindowHandle { get; }
    IDesktopTaskNativeElement Root(long handle);
    bool BelongsToWindow(IDesktopTaskNativeElement element, IDesktopTaskNativeElement root, long handle);
}

internal sealed record DesktopTaskElementMetadata(int[] RuntimeId, string Name, string AutomationId,
    string ControlType, string ParentName, DesktopTaskBounds Bounds, bool Enabled, bool Offscreen,
    bool Password, int ProcessId, long NativeHandle);

internal sealed record DesktopTaskElementDetails(string Value, bool IsReadOnly, string State,
    IReadOnlyList<DesktopTaskActionKind> Actions, bool TextTruncated = false, bool IsStructuredCell = false);

internal interface IDesktopTaskNativeElement
{
    DesktopTaskElementMetadata Metadata();
    DesktopTaskElementDetails Details(int maximumTextLength);
    IEnumerable<IDesktopTaskNativeElement> Children();
    void Perform(DesktopTaskActionKind action, string? text, DesktopTaskControl expected, Action verifyContext);
}
