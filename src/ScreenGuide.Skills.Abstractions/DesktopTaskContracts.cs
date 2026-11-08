namespace ScreenGuide.Skills.Abstractions;

public static class DesktopTaskLimits
{
    public const int MaximumControls = 256;
    public const int MaximumControlText = 20000;
    public const int MaximumValueLength = 1500;
    public const int MaximumInstructionLength = 4000;
    public const int MaximumSteps = 24;
}

public enum DesktopTaskActionKind { Invoke, SetText, Select, Expand, Collapse, ScrollUp, ScrollDown, Wait, Ask, Finish }

public sealed record DesktopTaskWindow(long Handle, int ProcessId, string ProcessName,
    DateTimeOffset ProcessStartedAtUtc, string ExecutablePath, string ClassName, string Title,
    DesktopTaskFrameIdentity? FrameIdentity = null);

public sealed record DesktopTaskFrameIdentity(long Handle, int ProcessId, DateTimeOffset ProcessStartedAtUtc,
    string ExecutablePath, string ClassName);

public sealed record DesktopTaskBounds(double X, double Y, double Width, double Height);

public sealed record DesktopTaskControl(string Id, int[] RuntimeId, string Name, string AutomationId,
    string ControlType, string ParentName, string Value, string State, bool IsReadOnly,
    DesktopTaskBounds Bounds, IReadOnlyList<DesktopTaskActionKind> Actions);

public sealed record DesktopTaskSnapshot(Guid Id, DesktopTaskWindow Window,
    IReadOnlyList<DesktopTaskControl> Controls, bool Truncated);

// Model proposals contain only snapshot-local IDs, never native handles or paths.
public sealed record DesktopTaskProposal(Guid SnapshotId, DesktopTaskActionKind Action, string? TargetId,
    string? Text, string Reason, string? EvidenceTargetId = null, string? EvidenceText = null);

public sealed record DesktopTaskActionReceipt(bool Verified, string Summary);

public interface IDesktopTaskAutomation
{
    Task<DesktopTaskWindow> BindAsync(long windowHandle, CancellationToken cancellationToken);
    Task<DesktopTaskSnapshot> ObserveAsync(DesktopTaskWindow window, CancellationToken cancellationToken);
    Task<DesktopTaskActionReceipt> ExecuteAsync(DesktopTaskWindow window, DesktopTaskControl control,
        DesktopTaskProposal action, CancellationToken cancellationToken, string? reviewedContextFingerprint = null);
}

public sealed class DesktopTaskException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

// Private anonymous-pipe worker messages. They are not a network/desktop IPC endpoint.
public sealed record DesktopTaskWorkerRequest(string Operation, long WindowHandle,
    DesktopTaskWindow? Window = null, DesktopTaskControl? Control = null, DesktopTaskProposal? Action = null,
    string? ReviewedContextFingerprint = null);

public sealed record DesktopTaskWorkerResponse(bool Succeeded, string? ErrorCode = null, string? Message = null,
    DesktopTaskWindow? Window = null, DesktopTaskSnapshot? Snapshot = null, DesktopTaskActionReceipt? Receipt = null);

public static class DesktopTaskContextFingerprint
{
    public static string Compute(DesktopTaskSnapshot snapshot) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            snapshot.Window.Title, snapshot.Truncated,
            Controls = snapshot.Controls.Select(c => new
            { c.RuntimeId, c.Name, c.AutomationId, c.ControlType, c.ParentName, c.Value, c.State, c.IsReadOnly, c.Bounds, c.Actions })
        })));
}

public sealed record DesktopTaskStepAuthorization(Guid TurnId, Guid SnapshotId, string WindowBinding, string ActionBinding)
{
    public string ToScopeValue()
    {
        if (TurnId == Guid.Empty || SnapshotId == Guid.Empty || !IsHash(WindowBinding) || !IsHash(ActionBinding))
            throw new ArgumentException("任务步骤缺少精确作用域。");
        return $"{TurnId:N}:{SnapshotId:N}:{WindowBinding}:{ActionBinding}";
    }
    private static bool IsHash(string value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}
