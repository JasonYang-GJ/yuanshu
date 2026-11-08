using System.Globalization;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class BridgeSnapshotProjection(
    BridgeServerInstance serverInstance,
    SessionCoordinator sessions,
    LocalTaskEntryService tasks,
    TimeProvider timeProvider)
{
    private const int MaximumPresentations = 32;

    public async Task<BridgeSnapshotDto> BuildAsync(CancellationToken cancellationToken)
    {
        var sessionTask = sessions.GetCurrentAsync(cancellationToken);
        var tasksTask = tasks.GetTasksAsync(cancellationToken: cancellationToken);
        await Task.WhenAll(sessionTask, tasksTask).ConfigureAwait(false);
        var session = await sessionTask.ConfigureAwait(false);
        var currentTasks = await tasksTask.ConfigureAwait(false);
        var presentations = new List<BridgePresentationDto>(MaximumPresentations);
        var associatedTaskIds = new HashSet<Guid>();

        if (session is not null)
        {
            var activeTurns = session.Turns
                .Concat(session.ActiveTurns)
                .Where(turn => !SessionTurnPhases.IsTerminal(turn.Phase))
                .DistinctBy(turn => turn.Id)
                .OrderBy(turn => turn.SequenceNumber);
            foreach (var turn in activeTurns)
            {
                if (presentations.Count == MaximumPresentations)
                {
                    break;
                }
                if (turn.TaskId is { } taskId)
                {
                    associatedTaskIds.Add(taskId);
                }
                presentations.Add(MapTurn(session.Session.Id, turn));
            }
        }

        foreach (var task in currentTasks
                     .Where(IsActive)
                     .Where(task => !associatedTaskIds.Contains(task.Id))
                     .OrderByDescending(task => task.UpdatedAtUtc)
                     .ThenBy(task => task.Id))
        {
            if (presentations.Count == MaximumPresentations)
            {
                break;
            }
            presentations.Add(MapTask(task));
        }

        return new BridgeSnapshotDto(
            BridgeProtocolService.ProtocolVersion,
            serverInstance.Id,
            Guid.NewGuid().ToString("D"),
            0,
            FormatTimestamp(timeProvider.GetUtcNow()),
            "yuanshu.core",
            presentations);
    }

    private BridgePresentationDto MapTurn(Guid sessionId, SessionTurnRecord turn)
    {
        var display = TurnDisplay(turn.Phase);
        if (turn.Phase == SessionTurnPhase.WaitingForWindowConsent && turn.IntentKind == "DesktopTask")
            display = sessions.GetWindowConsent(turn)?.Purpose == "TaskAction"
                ? new("WaitingUser", "需要确认这一步", "请核对当前软件、操作和内容后确认这一具体步骤。", true)
                : new("WaitingUser", "需要本次任务许可", "请确认读取指定窗口文字并交给当前模型完成本次任务。", true);
        if (turn.Phase == SessionTurnPhase.WaitingForWindowConsent
            && turn.IntentKind is "ApplicationSearch" or "SearchForeground")
            display = sessions.GetWindowConsent(turn)?.Purpose == "ResultRead"
                ? new("WaitingUser", "需要只读结果许可",
                    "请返回元枢主窗口确认：只读取新临时窗口核对结果，不会点击、输入或发送消息。", true)
                : new("WaitingUser", "需要视觉搜索许可",
                    "请返回元枢主窗口确认：仅查看目标窗口并完成这次搜索，不打开结果或发送消息。", true);
        var wirePhase = turn.Phase is SessionTurnPhase.WaitingForMemoryOutboundConsent
            or SessionTurnPhase.WaitingForPointerAnswerConsent
            ? "WaitingUser"
            : turn.Phase.ToString();
        return new BridgePresentationDto(
            $"turn:{turn.Id:D}",
            "turn",
            sessionId.ToString("D"),
            turn.Id.ToString("D"),
            turn.TaskId?.ToString("D"),
            turn.Version,
            wirePhase,
            display.IslandKind,
            display.Title,
            display.Subtitle,
            wirePhase,
            SessionTurnPhases.IsTerminal(turn.Phase),
            display.RequiresUserAction,
            FormatTimestamp(turn.UpdatedAtUtc),
            []);
    }

    private static BridgePresentationDto MapTask(AgentTask task)
    {
        var display = TaskDisplay(task.Status);
        return new BridgePresentationDto(
            $"task:{task.Id:D}",
            "task",
            null,
            null,
            task.Id.ToString("D"),
            task.Version,
            task.Status.ToString(),
            display.IslandKind,
            display.Title,
            display.Subtitle,
            task.Phase.ToString(),
            !IsActive(task),
            display.RequiresUserAction,
            FormatTimestamp(task.UpdatedAtUtc),
            []);
    }

    private static BridgeDisplay TurnDisplay(SessionTurnPhase phase) => phase switch
    {
        SessionTurnPhase.Understanding =>
            new("Thinking", "正在理解", "元枢正在理解你的请求。", false),
        SessionTurnPhase.Responding =>
            new("Thinking", "正在组织回答", "元枢正在准备回应。", false),
        SessionTurnPhase.WaitingForProject =>
            new("WaitingUser", "需要选择项目", "请返回元枢主窗口继续。", true),
        SessionTurnPhase.WaitingForFile =>
            new("WaitingUser", "需要选择文件", "请返回元枢主窗口继续。", true),
        SessionTurnPhase.WaitingForWindow =>
            new("WaitingUser", "需要选择窗口", "请返回元枢主窗口继续。", true),
        SessionTurnPhase.WaitingForWindowConsent =>
            new("WaitingUser", "需要窗口许可", "请返回元枢主窗口继续。", true),
        SessionTurnPhase.WaitingForMemoryOutboundConsent =>
            new("WaitingUser", "需要你的确认", "请返回元枢主窗口继续。", true),
        SessionTurnPhase.WaitingForPointerAnswerConsent =>
            new("WaitingUser", "需要你的确认", "请返回元枢主窗口继续。", true),
        SessionTurnPhase.WaitingForConfirmation =>
            new("WaitingUser", "需要你的确认", "请返回元枢主窗口继续。", true),
        SessionTurnPhase.WaitingForUser =>
            new("WaitingUser", "需要你的决定", "请返回元枢主窗口继续。", true),
        SessionTurnPhase.Executing =>
            new("Executing", "正在执行", "元枢正在处理当前请求。", false),
        SessionTurnPhase.ObservingWindow =>
            new("Executing", "正在观察窗口", "元枢正在处理当前请求。", false),
        SessionTurnPhase.ProgrammingTask =>
            new("Executing", "正在执行编程任务", "元枢正在处理当前请求。", false),
        SessionTurnPhase.Completed =>
            new("Success", "处理完成", "元枢已完成当前请求。", false),
        SessionTurnPhase.Cancelled =>
            new("Success", "任务已取消", "当前请求已经停止。", false),
        SessionTurnPhase.Failed or SessionTurnPhase.Interrupted =>
            new("Error", "处理未完成", "请返回元枢主窗口查看。", false),
        _ => new("Thinking", "正在处理", "元枢正在处理当前请求。", false)
    };

    private static BridgeDisplay TaskDisplay(AgentTaskStatus status) => status switch
    {
        AgentTaskStatus.WaitingForUser =>
            new("WaitingUser", "任务需要你的决定", "请返回元枢主窗口继续。", true),
        AgentTaskStatus.Succeeded =>
            new("Success", "任务已完成", "元枢已完成任务。", false),
        AgentTaskStatus.Cancelled =>
            new("Success", "任务已取消", "任务已经停止。", false),
        AgentTaskStatus.Failed or AgentTaskStatus.Interrupted =>
            new("Error", "任务未完成", "请返回元枢主窗口查看。", false),
        _ => new("Executing", "任务正在执行", "元枢正在处理任务。", false)
    };

    private static bool IsActive(AgentTask task) => task.Status is
        AgentTaskStatus.Pending or
        AgentTaskStatus.Running or
        AgentTaskStatus.WaitingForUser or
        AgentTaskStatus.CancellationRequested;

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private sealed record BridgeDisplay(
        string IslandKind,
        string Title,
        string Subtitle,
        bool RequiresUserAction);
}
