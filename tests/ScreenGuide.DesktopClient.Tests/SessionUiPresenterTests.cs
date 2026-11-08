using System.Text.Json;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class SessionUiPresenterTests
{
    [Fact]
    public void ResultReadConsentShowsTheNewWindowAndDoesNotOfferAnotherSearch()
    {
        var snapshot = Snapshot("WaitingForWindowConsent");
        var id = Guid.NewGuid();
        var turn = snapshot.Turns.Single() with
        {
            IntentKind = "ApplicationSearch",
            WindowConsent = new(id, "ResultRead", 42, "测试应用的临时窗口"),
            ResultSummary = "只读取这个窗口核对结果，不会点击、输入或发送消息。"
        };
        var presentation = SessionUiPresenter.Present(snapshot with
            { ForegroundTurn = turn, Turns = [turn], ActiveTurns = [turn] });
        Assert.Equal("需要你允许只读核对临时窗口中的搜索结果", presentation.StatusText);
        Assert.Equal("仅允许读取这次搜索结果", presentation.PrimaryActionText);
        Assert.Contains("目标窗口：测试应用的临时窗口", presentation.DetailText);
        Assert.DoesNotContain("目标窗口：阶段一窗口", presentation.DetailText);
        Assert.True(presentation.ShowStop);
        Assert.True(presentation.ShowWindowConsent);
        var serialized = DesktopProtocolJson.ToElement(turn);
        var roundTrip = serialized.Deserialize<UnifiedSessionTurnDto>(DesktopProtocolJson.Options)!;
        Assert.Equal(id, roundTrip.WindowConsent!.ConsentId);
        Assert.Equal(42, roundTrip.WindowConsent.WindowHandle);
        Assert.Equal(turn.WindowHandle, roundTrip.WindowHandle);
    }

    [Theory]
    [InlineData("ApplicationSearch")]
    [InlineData("SearchForeground")]
    public void VisualSearchConsentNamesTheActionAndKeepsStopAvailable(string intent)
    {
        var snapshot = Snapshot("WaitingForWindowConsent");
        var turn = snapshot.Turns.Single() with { IntentKind = intent,
            ResultSummary = "仅查看本窗口，点击搜索框并输入资料；不打开结果或发送消息。" };
        var presentation = SessionUiPresenter.Present(snapshot with
            { ForegroundTurn = turn, Turns = [turn], ActiveTurns = [turn] });
        Assert.Equal("需要你允许查看窗口并执行这次搜索", presentation.StatusText);
        Assert.Equal("允许查看并搜索这一次", presentation.PrimaryActionText);
        Assert.True(presentation.ShowStop);
        Assert.True(presentation.ShowWindowConsent);
        Assert.Contains("不打开结果或发送消息", presentation.DetailText);
    }

    [Theory]
    [InlineData("正在打开网易云音乐…")]
    [InlineData("正在查找最近发行的歌曲…")]
    [InlineData("正在搜索…")]
    [InlineData("正在检查搜索结果…")]
    public void ExecutingSearchShowsTheReportedStep(string step)
    {
        var snapshot = Snapshot("Executing");
        var turn = snapshot.Turns.Single() with { IntentKind = "ApplicationSearch", ResultSummary = step };
        snapshot = snapshot with { ForegroundTurn = turn, Turns = [turn], ActiveTurns = [turn] };
        var presentation = SessionUiPresenter.Present(snapshot);
        Assert.Equal(step, presentation.StatusText);
        Assert.True(presentation.ShowStop);
        Assert.Contains(step, presentation.DetailText);
    }

    [Theory]
    [InlineData("Understanding", "正在理解", true)]
    [InlineData("Responding", "随时插话", true)]
    [InlineData("WaitingForProject", "选择一个已授权项目", false)]
    [InlineData("WaitingForFile", "选择一个文件", false)]
    [InlineData("WaitingForWindowConsent", "允许查看", false)]
    [InlineData("ProgrammingTask", "后台运行", false)]
    [InlineData("WaitingForMemoryOutboundConsent", "查看完整记忆内容", false)]
    [InlineData("WaitingForPointerAnswerConsent", "完整问题与区域文字", false)]
    [InlineData("Cancelled", "已取消", false)]
    public void MapsOneCoordinatorPhaseToOneUserFacingState(
        string phase,
        string expectedText,
        bool expectedStop)
    {
        var presentation = SessionUiPresenter.Present(Snapshot(phase));

        Assert.Contains(expectedText, presentation.StatusText, StringComparison.Ordinal);
        Assert.Equal(expectedStop, presentation.ShowStop);
        Assert.Equal("帮我继续刚才的请求", presentation.OriginalRequest);
    }

    [Fact]
    public void WaitingContextKeepsTheOriginalRequestAndShowsOnlyItsOwnControls()
    {
        var project = SessionUiPresenter.Present(Snapshot("WaitingForProject"));
        var file = SessionUiPresenter.Present(Snapshot("WaitingForFile"));
        var consent = SessionUiPresenter.Present(Snapshot("WaitingForWindowConsent"));

        Assert.True(project.ShowProjectPicker);
        Assert.False(project.ShowFilePicker);
        Assert.True(file.ShowFilePicker);
        Assert.False(file.ShowProjectPicker);
        Assert.True(consent.ShowWindowConsent);
        Assert.Contains("目标窗口：阶段一窗口", consent.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public void OlderUpdateCannotReplaceANewerSnapshotEvenWhenItNamesAnotherSession()
    {
        var newer = Snapshot("Responding") with
        {
            ChangeVersion = 8,
            CoordinatorInstanceId = "host-instance-a",
            CoordinatorStartedAtUtc = new DateTimeOffset(2026, 8, 24, 1, 0, 0, TimeSpan.Zero)
        };
        var older = newer with { ChangeVersion = 7 };
        var otherSession = older with { SessionId = Guid.NewGuid() };

        Assert.False(SessionUiPresenter.ShouldApply(newer, older));
        Assert.False(SessionUiPresenter.ShouldApply(newer, otherSession));
    }

    [Fact]
    public void ANewHostInstanceCanReplaceAHigherVersionSnapshotAfterRestart()
    {
        var beforeRestart = Snapshot("Responding") with
        {
            ChangeVersion = 80,
            CoordinatorInstanceId = "host-instance-before-restart",
            CoordinatorStartedAtUtc = new DateTimeOffset(2026, 8, 24, 1, 0, 0, TimeSpan.Zero)
        };
        var afterRestart = Snapshot("Interrupted") with
        {
            ChangeVersion = 2,
            CoordinatorInstanceId = "host-instance-after-restart",
            CoordinatorStartedAtUtc = new DateTimeOffset(2026, 8, 24, 1, 1, 0, TimeSpan.Zero)
        };

        Assert.True(SessionUiPresenter.ShouldApply(beforeRestart, afterRestart));
        Assert.False(SessionUiPresenter.ShouldApply(afterRestart, beforeRestart));
    }

    [Fact]
    public void StaleNullSnapshotCannotClearAnExistingSession()
    {
        var current = Snapshot("Responding") with { ChangeVersion = 8 };

        Assert.False(SessionUiPresenter.ShouldApply(current, null));
        Assert.True(SessionUiPresenter.ShouldApply(null, null));
    }

    [Fact]
    public void ApplicationConfirmationCannotBeReusedForAWebsitePlan()
    {
        var selectedApplication = Snapshot("WaitingForConfirmation").Turns.Single() with
        {
            IntentKind = "OpenApplication",
            ExpectedIntentKind = "OpenApplication",
            ExpectedTarget = "installed-github-desktop",
            PlanTarget = "installed-github-desktop",
            ResultSummary = "确认打开“GitHub Desktop”。"
        };
        var substitutedWebsite = selectedApplication with
        {
            IntentKind = "OpenWebsite",
            PlanTarget = "https://github.com/",
            ResultSummary = "确认用默认浏览器打开 GitHub。"
        };

        Assert.True(SessionUiPresenter.MatchesConfirmedTarget(
            selectedApplication,
            "OpenApplication",
            "installed-github-desktop"));
        Assert.False(SessionUiPresenter.MatchesConfirmedTarget(
            substitutedWebsite,
            "OpenApplication",
            "installed-github-desktop"));
    }

    [Theory]
    [InlineData("app-visual-studio", "app-visual-studio-code")]
    [InlineData("https://example.com/", "https://evil-example.com/")]
    public void SimilarLookingTargetsAreComparedByExactStructuredIdentity(
        string confirmedTarget,
        string plannedTarget)
    {
        var turn = Snapshot("WaitingForConfirmation").Turns.Single() with
        {
            IntentKind = confirmedTarget.StartsWith("https://", StringComparison.Ordinal)
                ? "OpenWebsite"
                : "OpenApplication",
            ExpectedIntentKind = confirmedTarget.StartsWith("https://", StringComparison.Ordinal)
                ? "OpenWebsite"
                : "OpenApplication",
            ExpectedTarget = confirmedTarget,
            PlanTarget = plannedTarget,
            ResultSummary = $"确认打开 {plannedTarget}"
        };

        Assert.False(SessionUiPresenter.MatchesConfirmedTarget(
            turn,
            turn.ExpectedIntentKind!,
            confirmedTarget));
    }

    private static SessionSnapshotDto Snapshot(string phase)
    {
        var sessionId = Guid.NewGuid();
        var turn = new UnifiedSessionTurnDto(
            Guid.NewGuid(),
            1,
            "帮我继续刚才的请求",
            "Text",
            "Conversation",
            phase,
            "None",
            "Conversation",
            null,
            null,
            null,
            null,
            18,
            "阶段一窗口",
            false,
            false,
            "阶段一窗口",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null);
        return new SessionSnapshotDto(
            3,
            "host-instance-test",
            new DateTimeOffset(2026, 8, 24, 1, 0, 0, TimeSpan.Zero),
            sessionId,
            Guid.NewGuid(),
            "阶段一会话",
            phase,
            null,
            null,
            turn,
            [turn],
            [turn],
            []);
    }
}
