using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class DesktopTaskVoiceConsentTests
{
    [Theory]
    [InlineData("TaskObservation", "允许这次任务")]
    [InlineData("TaskAction", "确认这一步")]
    public void UnpresentedPermissionRevokedForManualReviewCannotArmFromLatePresentation(string purpose, string phrase)
    {
        var snapshot = Snapshot(purpose);
        var gate = new VisualSearchVoiceConsentGate();
        var binding = gate.Begin(snapshot, 3)!;
        Assert.False(gate.Arm(binding, snapshot, 3, 5, false));
        // The presentation deadline marks this exact permission manual-only.
        gate.Revoke(snapshot, 3);
        Assert.Null(gate.Begin(snapshot, 3));
        Assert.False(gate.Arm(binding, snapshot, 3, 6, true));
        Assert.Null(gate.TryConsume(phrase, snapshot, 3, 6, true));
        Assert.Equal("WaitingForWindowConsent", snapshot.ForegroundTurn!.Phase);
        Assert.Equal(binding.ConsentId, snapshot.ForegroundTurn.WindowConsent!.ConsentId);

        var next = snapshot with { ForegroundTurn = snapshot.ForegroundTurn with
        { WindowConsent = snapshot.ForegroundTurn.WindowConsent with { ConsentId = Guid.NewGuid() } } };
        var nextBinding = gate.Begin(next, 3)!;
        Assert.False(gate.Arm(binding, next, 3, 7, true));
        Assert.True(gate.Arm(nextBinding, next, 3, 7, true));
        Assert.NotNull(gate.TryConsume(phrase, next, 3, 7, true));
    }

    [Theory]
    [InlineData("TaskObservation", "允许这次任务", true)]
    [InlineData("TaskObservation", "拒绝这次任务", false)]
    [InlineData("TaskAction", "确认这一步", true)]
    [InlineData("TaskAction", "拒绝这一步", false)]
    public void OnlyDisplayedExactTaskPurposeCanResumeOnce(string purpose, string phrase, bool granted)
    {
        var snapshot = Snapshot(purpose);
        var gate = new VisualSearchVoiceConsentGate();
        var binding = Assert.IsType<VisualSearchVoiceConsentBinding>(gate.Begin(snapshot, 3));
        Assert.Null(gate.TryConsume(phrase, snapshot, 3, 5, true));
        Assert.False(gate.Arm(binding, snapshot, 3, 5, false));
        Assert.True(gate.Arm(binding, snapshot, 3, 5, true));
        Assert.Null(gate.TryConsume(phrase, snapshot, 3, 4, true));
        Assert.Null(gate.TryConsume(phrase, snapshot, 4, 5, true));
        Assert.Null(gate.TryConsume(phrase, snapshot, 3, 5, false));
        Assert.Equal(granted, gate.TryConsume(phrase, snapshot, 3, 5, true)!.Granted);
        Assert.Null(gate.TryConsume(phrase, snapshot, 3, 5, true));
        Assert.Null(gate.Begin(snapshot, 3));
    }

    [Theory]
    [InlineData("TaskAction", "允许这次任务")]
    [InlineData("TaskObservation", "确认这一步")]
    [InlineData("TaskAction", "允许这次搜索")]
    [InlineData("TaskAction", "确认这一步然后发送")]
    [InlineData("TaskAction", "不要确认这一步")]
    [InlineData("TaskAction", "好")]
    [InlineData("TaskAction", "界面上写着确认这一步")]
    public void OtherPurposesOrdinarySpeechAndAdditionalActionsNeverAuthorize(string purpose, string phrase)
    {
        var snapshot = Snapshot(purpose);
        var gate = new VisualSearchVoiceConsentGate();
        Assert.True(gate.Arm(gate.Begin(snapshot, 1)!, snapshot, 1, 2, true));
        Assert.Null(gate.TryConsume(phrase, snapshot, 1, 2, true));
    }

    [Fact]
    public void StoppedOrChangedStepCannotAcceptBufferedPermission()
    {
        var snapshot = Snapshot("TaskAction");
        var gate = new VisualSearchVoiceConsentGate();
        var binding = gate.Begin(snapshot, 1)!;
        Assert.True(gate.Arm(binding, snapshot, 1, 2, true));
        var changed = snapshot with { ForegroundTurn = snapshot.ForegroundTurn! with
        { WindowConsent = snapshot.ForegroundTurn.WindowConsent! with { ConsentId = Guid.NewGuid() } } };
        Assert.Null(gate.TryConsume("确认这一步", changed, 1, 2, true));
        gate.Revoke(snapshot, 1);
        Assert.Null(gate.TryConsume("确认这一步", snapshot, 1, 2, true));
        Assert.False(gate.Arm(binding, snapshot, 1, 3, true));
        Assert.False(VisualSearchVoiceConsentGate.ShouldDeferPartial("停止", snapshot));
    }

    [Theory]
    [InlineData("TaskObservation", "允许本次任务读取并操作这个窗口")]
    [InlineData("TaskAction", "仅确认当前这一步")]
    public void UiShowsSpecificTaskAuthorityAndStop(string purpose, string action)
    {
        var view = SessionUiPresenter.Present(Snapshot(purpose));
        Assert.True(view.ShowStop);
        Assert.True(view.ShowWindowConsent);
        Assert.Equal(action, view.PrimaryActionText);
        Assert.DoesNotContain("搜索", view.StatusText);
        Assert.Contains("本次", view.DetailText);
    }

    [Fact]
    public void FailedTaskShowsActualFailureInsteadOfItsLastExecutingProgress()
    {
        var snapshot = Snapshot("TaskAction");
        var turn = snapshot.ForegroundTurn! with
        {
            Phase = "Failed", ResultSummary = "正在执行第 1 步：填写正文",
            FailureMessage = "窗口内容已经变化，本步没有执行。", WindowConsent = null
        };
        var view = SessionUiPresenter.Present(snapshot with { ForegroundTurn = turn, Turns = [turn], ActiveTurns = [] });
        Assert.Contains(turn.FailureMessage, view.DetailText);
        Assert.DoesNotContain("正在执行", view.DetailText);
        Assert.False(view.ShowStop);
        Assert.False(view.ShowWindowConsent);
    }

    private static SessionSnapshotDto Snapshot(string purpose)
    {
        var now = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        var turn = new UnifiedSessionTurnDto(Guid.NewGuid(), 1, "在当前窗口输入测试内容", "Voice", "DesktopAction",
            "WaitingForWindowConsent", "WindowConsent", "DesktopTask", null, null, null, null, 18, "合成测试窗口",
            false, false, "本次窗口文字会发送至所选模型，确认只授权显示的任务或步骤。", null, now, now, null,
            WindowConsent: new(Guid.NewGuid(), purpose, 18, "合成测试窗口"));
        return new(3, "test-host", now, Guid.NewGuid(), Guid.NewGuid(), "测试", turn.Phase,
            null, null, turn, [turn], [turn], []);
    }
}
