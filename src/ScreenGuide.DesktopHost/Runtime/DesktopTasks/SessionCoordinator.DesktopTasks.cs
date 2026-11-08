using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed partial class SessionCoordinator
{
    private const string DesktopTaskConsentCode = "desktop_task_consent_required";
    private async Task<SessionTurnRecord> RequireTurnAsync(Guid turnId, CancellationToken cancellationToken) =>
        await sessionStore.GetTurnAsync(turnId, cancellationToken)
            ?? throw new InvalidOperationException("当前任务记录已经不存在。");
    private sealed record PendingDesktopTaskConsent(Guid Id, DesktopTaskInteraction Request,
        string OperationId, string PlanTarget, long TurnVersion, DateTimeOffset ExpiresAtUtc)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task RequestDesktopTaskConsentAsync(Guid turnId, string operationId, AssistantIntentPlanDto plan,
        DesktopTaskInteraction request, CancellationToken operationCancellation, CancellationToken requestCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationCancellation, requestCancellation);
        PendingDesktopTaskConsent pending;
        ActiveSessionWork active;
        await using (var gate = await operationGates.AcquireTurnAsync(turnId, linked.Token))
        {
            var current = await RequireTurnAsync(turnId, linked.Token);
            var session = await sessionStore.GetSessionAsync(current.SessionId, linked.Token);
            if (session?.IsCurrent != true || current.Phase != SessionTurnPhase.Executing
                || current.IntentKind != "DesktopTask" || current.CancellationRequested
                || current.OperationId != operationId || current.PlanTarget != plan.CanonicalTarget
                || string.IsNullOrWhiteSpace(plan.CanonicalTarget) || !MatchesPlannedExpectation(current)
                || !_activeWork.TryGetValue(turnId, out active!) || active.Cancellation.IsCancellationRequested
                || request.Purpose is not ("TaskObservation" or "TaskAction")
                || request.BindingHash.Length != 64
                || active.DesktopTaskConsent is { Completion.Task.IsCompleted: false })
                throw new UnauthorizedAccessException("本次任务、目标或步骤许可已经变化。");
            RequireDesktopTaskWindow(request.Window);
            pending = new(Guid.NewGuid(), request, operationId, plan.CanonicalTarget,
                current.Version + 1, timeProvider.GetUtcNow().AddMinutes(2));
            active.DesktopTaskConsent = pending;
            var waiting = await TransitionAsync(turnId, value => value with
            {
                Phase = SessionTurnPhase.WaitingForWindowConsent,
                MissingContext = SessionMissingContext.WindowConsent,
                WindowHandle = request.Window.Handle, WindowTitle = request.Window.Title,
                WindowProcessName = request.Window.ProcessName, WindowProcessId = request.Window.ProcessId,
                WindowProcessStartTimeUtc = request.Window.ProcessStartedAtUtc,
                RequiresConfirmation = true, ConfirmationGranted = false,
                ResultSummary = request.Purpose == "TaskObservation" ? "请确认本次窗口任务许可。" : "请核对并确认当前这一步。",
                FailureCode = DesktopTaskConsentCode, FailureMessage = null
            }, linked.Token);
            if (waiting.Phase != SessionTurnPhase.WaitingForWindowConsent || waiting.Version != pending.TurnVersion)
                throw new OperationCanceledException(linked.Token);
            active.VisiblePause.TrySetResult();
        }
        try { await pending.Completion.Task.WaitAsync(TimeSpan.FromMinutes(2), linked.Token); }
        catch (TimeoutException) { throw new DesktopTaskException("desktop_task_consent_expired", "本次任务许可已超时，未继续执行。"); }
        linked.Token.ThrowIfCancellationRequested();
        var resumed = await RequireTurnAsync(turnId, linked.Token);
        if (resumed.Phase != SessionTurnPhase.Executing || resumed.CancellationRequested
            || resumed.OperationId != operationId || resumed.PlanTarget != pending.PlanTarget
            || !resumed.ConfirmationGranted || !ReferenceEquals(active.DesktopTaskConsent, pending))
            throw new UnauthorizedAccessException("本次步骤确认已经撤销。");
        RequireDesktopTaskWindow(request.Window);
    }

    // Called under the same Turn gate used by Stop/replacement. Only releases the
    // original waiting execution; never re-plans or replays earlier steps.
    private async Task<ActiveSessionWork> RespondDesktopTaskConsentAsync(SessionTurnRecord turn,
        bool granted, Guid? consentId, CancellationToken cancellationToken)
    {
        if (!_activeWork.TryGetValue(turn.Id, out var active) || active.DesktopTaskConsent is not { } pending
            || consentId is null || consentId == Guid.Empty || consentId != pending.Id
            || turn.Version != pending.TurnVersion || turn.OperationId != pending.OperationId
            || turn.PlanTarget != pending.PlanTarget || active.Cancellation.IsCancellationRequested
            || timeProvider.GetUtcNow() >= pending.ExpiresAtUtc || pending.Completion.Task.IsCompleted)
            throw new UnauthorizedAccessException("这条步骤许可已失效，请查看当前请求。");
        if (!granted)
        {
            await TransitionAsync(turn.Id, current => current with
            {
                Phase = SessionTurnPhase.Cancelled, MissingContext = SessionMissingContext.None,
                CancellationRequested = true, ConfirmationGranted = false,
                ResultSummary = "你没有确认这一步，任务已结束，先前已发生的操作不会重放。",
                FailureCode = "desktop_task_consent_rejected", CompletedAtUtc = timeProvider.GetUtcNow()
            }, cancellationToken);
            CancelActiveWork(active);
            return active;
        }
        try
        {
            RequireDesktopTaskWindow(pending.Request.Window);
            var resumed = await TransitionAsync(turn.Id, current => current with
            {
                Phase = SessionTurnPhase.Executing, MissingContext = SessionMissingContext.None,
                ConfirmationGranted = true, FailureCode = null, FailureMessage = null,
                ResultSummary = pending.Request.Purpose == "TaskObservation"
                    ? "已获得本次窗口任务许可，正在读取控件并规划。" : "已确认这一具体步骤，正在重新核对控件。"
            }, cancellationToken);
            if (resumed.Phase != SessionTurnPhase.Executing || resumed.CancellationRequested)
                throw new OperationCanceledException(active.Cancellation.Token);
            pending.Completion.TrySetResult();
        }
        catch (DesktopTaskException exception) { pending.Completion.TrySetException(exception); }
        return active;
    }

    private SessionWindowConsentDto? GetDesktopTaskConsent(SessionTurnRecord turn)
    {
        if (turn.Phase != SessionTurnPhase.WaitingForWindowConsent || turn.FailureCode != DesktopTaskConsentCode
            || !_activeWork.TryGetValue(turn.Id, out var active) || active.Cancellation.IsCancellationRequested
            || active.DesktopTaskConsent is not { } pending || turn.Version != pending.TurnVersion
            || turn.OperationId != pending.OperationId || turn.PlanTarget != pending.PlanTarget
            || pending.Completion.Task.IsCompleted || timeProvider.GetUtcNow() >= pending.ExpiresAtUtc) return null;
        return new(pending.Id, pending.Request.Purpose, pending.Request.Window.Handle, pending.Request.Window.Title);
    }

    // The complete review text belongs only to this live consent, never SQLite.
    internal string? GetDesktopTaskConsentDescription(SessionTurnRecord turn) =>
        GetDesktopTaskConsent(turn) is not null && _activeWork.TryGetValue(turn.Id, out var active)
            ? active.DesktopTaskConsent?.Request.Description : null;

    private void RequireDesktopTaskWindow(DesktopTaskWindow expected)
    {
        var actual = foregroundWindows.ResolveWindow(expected.Handle);
        if (actual is null || actual.ProcessId != expected.ProcessId
            || actual.ProcessStartTimeUtc != expected.ProcessStartedAtUtc
            || !string.Equals(actual.ProcessName, expected.ProcessName, StringComparison.OrdinalIgnoreCase))
            throw new DesktopTaskException("desktop_task_window_changed", "本次任务绑定的软件窗口已变化，已停止。");
    }

    private async Task PublishDesktopTaskProgressAsync(Guid turnId, string operationId, string description,
        CancellationToken operationCancellation, CancellationToken reportCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationCancellation, reportCancellation);
        await using var gate = await operationGates.AcquireTurnAsync(turnId, linked.Token);
        var current = await RequireTurnAsync(turnId, linked.Token);
        if (current.Phase != SessionTurnPhase.Executing || current.IntentKind != "DesktopTask"
            || current.OperationId != operationId || current.CancellationRequested) throw new OperationCanceledException(linked.Token);
        await TransitionAsync(turnId, value => value with
        { ResultSummary = description[..Math.Min(description.Length, 500)] }, linked.Token);
    }
}
