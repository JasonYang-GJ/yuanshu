using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed record SessionUiPresentation(
    string Title,
    string ProjectText,
    string StatusText,
    string StatusTone,
    string OriginalRequest,
    string DetailText,
    bool ShowStop,
    bool ShowProjectPicker,
    bool ShowFilePicker,
    bool ShowWindowRetry,
    bool ShowWindowConsent,
    bool ShowConfirmation,
    bool ShowContextCard,
    bool ShowResult,
    string PrimaryActionText,
    string CancelActionText);

public static class SessionUiPresenter
{
    public static SessionUiPresentation Present(SessionSnapshotDto? snapshot)
    {
        if (snapshot is null)
        {
            return new SessionUiPresentation(
                "还没有开始话题",
                "未选择项目",
                "直接说出你想聊或想做的事情",
                "Neutral",
                string.Empty,
                string.Empty,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                string.Empty,
                "取消");
        }

        var turn = snapshot.ForegroundTurn
                   ?? snapshot.ActiveTurns.OrderBy(item => item.SequenceNumber).LastOrDefault()
                   ?? snapshot.Turns.OrderBy(item => item.SequenceNumber).LastOrDefault();
        var phase = turn?.Phase ?? "Ready";
        var status = PhaseText(phase);
        if (phase == "Executing" && turn?.IntentKind is "ApplicationSearch" or "DesktopTask"
            && !string.IsNullOrWhiteSpace(turn.ResultSummary))
            status = (turn.ResultSummary, "Busy");
        var waitingProject = phase == "WaitingForProject";
        var waitingFile = phase == "WaitingForFile";
        var waitingWindow = phase == "WaitingForWindow";
        var waitingConsent = phase == "WaitingForWindowConsent";
        var visualSearchConsent = waitingConsent && turn?.IntentKind is "ApplicationSearch" or "SearchForeground";
        var resultReadConsent = visualSearchConsent && turn?.WindowConsent?.Purpose == "ResultRead";
        var desktopTaskConsent = waitingConsent && turn?.IntentKind == "DesktopTask";
        var desktopTaskStep = desktopTaskConsent && turn?.WindowConsent?.Purpose == "TaskAction";
        if (desktopTaskConsent) status = (desktopTaskStep ? "请确认当前这一步的具体操作" : "请允许本次软件任务读取必要窗口文字", "Waiting");
        if (visualSearchConsent) status = (resultReadConsent
            ? "需要你允许只读核对临时窗口中的搜索结果"
            : "需要你允许查看窗口并执行这次搜索", "Waiting");
        var waitingConfirmation = phase == "WaitingForConfirmation";
        var context = waitingProject || waitingFile || waitingWindow || waitingConsent || waitingConfirmation;
        var terminal = phase is "Completed" or "Cancelled" or "Failed" or "Interrupted";
        var windowTitle = turn?.WindowConsent?.WindowTitle ?? turn?.WindowTitle;
        var target = string.IsNullOrWhiteSpace(windowTitle)
            ? string.Empty
            : $"\n目标窗口：{windowTitle}";
        var detail = turn is null
            ? "当前话题会在应用重启后继续保留。"
            : $"{ResultText(turn) ?? string.Empty}{target}".Trim();
        return new SessionUiPresentation(
            snapshot.Title,
            snapshot.SelectedProjectName is null
                ? "未选择项目"
                : $"当前项目：{snapshot.SelectedProjectName}",
            status.Text,
            status.Tone,
            turn?.InputText ?? string.Empty,
            detail,
            visualSearchConsent || desktopTaskConsent || phase is "Understanding" or "Responding" or "Executing" or "ObservingWindow",
            waitingProject,
            waitingFile,
            waitingWindow,
            waitingConsent,
            waitingConfirmation,
            context,
            terminal || phase == "ProgrammingTask" || phase == "WaitingForUser",
            desktopTaskStep ? "仅确认当前这一步" : desktopTaskConsent ? "允许本次任务读取并操作这个窗口"
                : resultReadConsent ? "仅允许读取这次搜索结果"
                : visualSearchConsent ? "允许查看并搜索这一次" : PrimaryActionText(phase),
            waitingConsent ? "拒绝并结束" : "取消这条请求");
    }

    public static bool ShouldApply(SessionSnapshotDto? current, SessionSnapshotDto? incoming) =>
        incoming is null
            ? current is null
            : current is null
              || (string.Equals(
                      current.CoordinatorInstanceId,
                      incoming.CoordinatorInstanceId,
                      StringComparison.Ordinal)
                  ? incoming.ChangeVersion >= current.ChangeVersion
                  : incoming.CoordinatorStartedAtUtc > current.CoordinatorStartedAtUtc);

    public static bool MatchesConfirmedTarget(
        UnifiedSessionTurnDto turn,
        string expectedIntentKind,
        string expectedTarget) =>
        !string.IsNullOrWhiteSpace(expectedTarget)
        && string.Equals(turn.ExpectedIntentKind, expectedIntentKind, StringComparison.Ordinal)
        && string.Equals(turn.IntentKind, expectedIntentKind, StringComparison.Ordinal)
        && string.Equals(turn.ExpectedTarget, expectedTarget, StringComparison.Ordinal)
        && string.Equals(turn.PlanTarget, expectedTarget, StringComparison.Ordinal);

    public static string? ResultText(UnifiedSessionTurnDto turn) =>
        turn.Phase == "Failed" && !string.IsNullOrWhiteSpace(turn.FailureMessage)
            ? turn.FailureMessage : turn.ResultSummary ?? turn.FailureMessage;

    private static (string Text, string Tone) PhaseText(string phase) => phase switch
    {
        "Understanding" => ("正在理解你的意思…", "Busy"),
        "Responding" => ("正在回答，你可以随时插话", "Busy"),
        "WaitingForProject" => ("还需要选择一个已授权项目，选择后会继续刚才的请求", "Waiting"),
        "WaitingForFile" => ("还需要选择一个文件，选择后会继续刚才的请求", "Waiting"),
        "WaitingForWindow" => ("请切换到要查看的窗口，然后继续", "Waiting"),
        "WaitingForWindowConsent" => ("需要你允许查看这一个窗口", "Waiting"),
        "WaitingForConfirmation" => ("准备就绪，等待你确认这一次", "Waiting"),
        "WaitingForMemoryOutboundConsent" => ("请查看完整记忆内容并确认是否只发送这一次", "Waiting"),
        "WaitingForPointerAnswerConsent" => ("请查看完整问题与区域文字，确认是否只发送这一次", "Waiting"),
        "Executing" => ("正在执行这一次已确认的操作…", "Busy"),
        "ObservingWindow" => ("正在查看你允许的单个窗口…", "Busy"),
        "ProgrammingTask" => ("编程任务正在后台运行，你可以继续说话", "Busy"),
        "WaitingForUser" => ("编程任务需要你补充信息", "Waiting"),
        "Completed" => ("已完成", "Success"),
        "Cancelled" => ("已取消，没有继续执行", "Neutral"),
        "Failed" => ("处理失败，当前话题仍可继续", "Error"),
        "Interrupted" => ("上次任务因元枢关闭而中断，没有自动重试", "Waiting"),
        _ => ("可以继续刚才的话题", "Neutral")
    };

    private static string PrimaryActionText(string phase) => phase switch
    {
        "WaitingForProject" => "选择并继续",
        "WaitingForFile" => "选择文件",
        "WaitingForWindow" => "我已切换",
        "WaitingForWindowConsent" => "允许查看这一个窗口",
        "WaitingForConfirmation" => "确认这一次",
        _ => string.Empty
    };
}
