using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class VisualSearchVoiceConsentGateTests
{
    [Theory]
    [InlineData("允许这次视觉搜索", "Search", true)]
    [InlineData("拒绝这次视觉搜索", "Search", false)]
    [InlineData("允许这次搜索", "Search", true)]
    [InlineData("拒绝这次搜索", "Search", false)]
    [InlineData("允许读取这个临时窗口", "ResultRead", true)]
    [InlineData("拒绝读取这个临时窗口", "ResultRead", false)]
    [InlineData("允许这次读取", "ResultRead", true)]
    [InlineData("拒绝这次读取", "ResultRead", false)]
    [InlineData(" 允许 读取这个临时窗口。", "ResultRead", true)]
    public void OnlyTheDisplayedPurposeSpecificPhraseCanAnswer(string phrase, string purpose, bool granted)
    {
        var snapshot = Snapshot(purpose);
        var gate = new VisualSearchVoiceConsentGate();
        var binding = gate.Begin(snapshot, 7)!;
        Assert.Null(gate.TryConsume(phrase, snapshot, 7, 12, true));
        Assert.False(gate.Arm(binding, snapshot, 7, 12, visiblyPresented: false));
        Assert.Null(gate.TryConsume(phrase, snapshot, 7, 12, true));
        Assert.True(gate.Arm(binding, snapshot, 7, 12, visiblyPresented: true));
        Assert.Null(gate.TryConsume(phrase, snapshot, 7, 12, visible: false));
        var response = gate.TryConsume(phrase, snapshot, 7, 12, true);
        Assert.Equal(new VisualSearchVoiceConsentResponse(binding, granted), response);
        Assert.Null(gate.Begin(snapshot, 7)); // A repaint cannot grant the nonce twice.
        Assert.False(gate.Arm(binding, snapshot, 7, 13, true));
        Assert.Null(gate.TryConsume(phrase, snapshot, 7, 12, true));
    }

    [Theory]
    [InlineData("好")]
    [InlineData("同意")]
    [InlineData("允许")]
    [InlineData("不允许读取这个临时窗口")]
    [InlineData("不要拒绝这次视觉搜索")]
    [InlineData("界面上写着允许读取这个临时窗口")]
    [InlineData("允许读取这个临时窗口然后发送消息")]
    [InlineData("允许读取那个临时窗口")]
    [InlineData("允许读取这个临时窗口吗")]
    [InlineData("原是读取这个临时窗口")]
    [InlineData("允许这次读取然后发送")]
    [InlineData("不允许这次读取")]
    public void OrdinaryConversationNegationAndAdditionalActionsNeverGrant(string phrase)
    {
        Assert.False(VisualSearchVoiceConsentGate.TryParse(phrase, out _, out _));
        var snapshot = Snapshot();
        var gate = Armed(snapshot);
        Assert.Null(gate.TryConsume(phrase, snapshot, 7, 12, true));
    }

    [Theory]
    [InlineData("Search", "允许读取这个临时窗口")]
    [InlineData("Search", "拒绝读取这个临时窗口")]
    [InlineData("Search", "允许这次读取")]
    [InlineData("Search", "拒绝这次读取")]
    [InlineData("ResultRead", "允许这次视觉搜索")]
    [InlineData("ResultRead", "拒绝这次视觉搜索")]
    [InlineData("ResultRead", "允许这次搜索")]
    [InlineData("ResultRead", "拒绝这次搜索")]
    public void AResponseCannotCrossApproveOrRejectAnotherPurpose(string purpose, string phrase)
    {
        var snapshot = Snapshot(purpose);
        Assert.Null(Armed(snapshot).TryConsume(phrase, snapshot, 7, 12, true));
    }

    [Fact]
    public void BufferedOrStoppedMicrophoneAudioCannotApproveEvenAfterThePromptWasDisplayed()
    {
        var snapshot = Snapshot();
        var gate = Armed(snapshot);
        Assert.Null(gate.TryConsume("允许读取这个临时窗口", snapshot, 7, 11, true));
        Assert.Null(gate.TryConsume("允许读取这个临时窗口", snapshot, 8, 12, true));
        Assert.Null(gate.TryConsume("允许读取这个临时窗口", snapshot, null, 12, true));
        Assert.NotNull(gate.TryConsume("允许读取这个临时窗口", snapshot, 7, 12, true));
    }

    [Theory]
    [InlineData("coordinator")]
    [InlineData("restart-time")]
    [InlineData("session")]
    [InlineData("turn")]
    [InlineData("nonce")]
    [InlineData("window")]
    [InlineData("title")]
    [InlineData("purpose")]
    [InlineData("cancelled")]
    [InlineData("executing")]
    [InlineData("describe")]
    [InlineData("no-consent")]
    public void ChangedAuthorityRejectsOldAudioAndLatePresentationAcknowledgement(string change)
    {
        var snapshot = Snapshot();
        var gate = Armed(snapshot);
        var old = VisualSearchVoiceConsentBinding.From(snapshot)!;
        var turn = snapshot.ForegroundTurn!;
        var changed = change switch
        {
            "coordinator" => snapshot with { CoordinatorInstanceId = "restarted-host" },
            "restart-time" => snapshot with { CoordinatorStartedAtUtc = snapshot.CoordinatorStartedAtUtc.AddSeconds(1) },
            "session" => snapshot with { SessionId = Guid.NewGuid() },
            "turn" => WithTurn(snapshot, turn with { Id = Guid.NewGuid() }),
            "nonce" => WithTurn(snapshot, turn with { WindowConsent = turn.WindowConsent! with { ConsentId = Guid.NewGuid() } }),
            "window" => WithTurn(snapshot, turn with { WindowConsent = turn.WindowConsent! with { WindowHandle = 43 } }),
            "title" => WithTurn(snapshot, turn with { WindowConsent = turn.WindowConsent! with { WindowTitle = "另一窗口" } }),
            "purpose" => WithTurn(snapshot, turn with { WindowConsent = turn.WindowConsent! with { Purpose = "Search" } }),
            "cancelled" => WithTurn(snapshot, turn with { CancellationRequested = true }),
            "executing" => WithTurn(snapshot, turn with { Phase = "Executing" }),
            "describe" => WithTurn(snapshot, turn with { IntentKind = "DescribeForeground" }),
            "no-consent" => WithTurn(snapshot, turn with { WindowConsent = null }),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        Assert.Null(gate.TryConsume("允许读取这个临时窗口", changed, 7, 12, true));
        gate.Begin(changed, 7);
        Assert.False(gate.Arm(old, changed, 7, 12, true));
        Assert.Null(gate.TryConsume("允许读取这个临时窗口", changed, 7, 12, true));
    }

    [Fact]
    public void StopOrReplacementPreventsRepaintFromRearmingTheSameNonceInThatMicrophoneRun()
    {
        var snapshot = Snapshot();
        var gate = Armed(snapshot);
        gate.Revoke(snapshot, 7);
        Assert.Null(gate.Begin(snapshot, 7));
        Assert.False(gate.Arm(VisualSearchVoiceConsentBinding.From(snapshot)!, snapshot, 7, 13, true));
        Assert.Null(gate.TryConsume("允许读取这个临时窗口", snapshot, 7, 12, true));
        var next = WithTurn(snapshot, snapshot.ForegroundTurn! with
            { WindowConsent = snapshot.ForegroundTurn!.WindowConsent! with { ConsentId = Guid.NewGuid() } });
        var binding = gate.Begin(next, 7)!;
        Assert.NotNull(binding);
        Assert.Null(gate.TryConsume("允许读取这个临时窗口", next, 7, 12, true));
        Assert.True(gate.Arm(binding, next, 7, 14, true));
        Assert.NotNull(gate.TryConsume("允许读取这个临时窗口", next, 7, 14, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalSpeechCannotRearmAConsumedOrRevokedNonce(bool consumed)
    {
        var snapshot = Snapshot();
        var gate = Armed(snapshot);
        var binding = VisualSearchVoiceConsentBinding.From(snapshot)!;
        if (consumed) Assert.NotNull(gate.TryConsume("允许这次读取", snapshot, 7, 12, true));
        else gate.Revoke(snapshot, 7);
        for (var repaint = 0; repaint < 3; repaint++)
        {
            gate.SuspendRecognition();
            Assert.False(gate.Arm(binding, snapshot, 7, 13, true));
            Assert.Null(gate.Begin(snapshot, 7));
            Assert.False(gate.Arm(binding, snapshot, 7, 14, true));
            Assert.Null(gate.TryConsume("允许这次读取", snapshot, 7, 12, true));
            Assert.Null(gate.TryConsume("允许这次读取", snapshot, 7, 14, true));
        }
    }

    [Fact]
    public void UnansweredConsentAfterSpeechNeedsVisiblePreparationAndFreshAudioAgain()
    {
        var snapshot = Snapshot();
        var gate = Armed(snapshot);
        var binding = VisualSearchVoiceConsentBinding.From(snapshot)!;
        gate.SuspendRecognition();
        Assert.Null(gate.TryConsume("允许这次读取", snapshot, 7, 12, true));
        Assert.False(gate.Arm(binding, snapshot, 7, 13, true));
        Assert.Equal(binding, gate.Begin(snapshot, 7));
        Assert.Null(gate.TryConsume("允许这次读取", snapshot, 7, 12, true));
        Assert.False(gate.Arm(binding, snapshot, 7, 13, visiblyPresented: false));
        Assert.True(gate.Arm(binding, snapshot, 7, 13, visiblyPresented: true));
        Assert.Null(gate.TryConsume("允许这次读取", snapshot, 7, 12, true));
        Assert.NotNull(gate.TryConsume("允许这次读取", snapshot, 7, 13, true));
        gate.SuspendRecognition();
        Assert.Null(gate.Begin(snapshot, 7));
    }

    [Fact]
    public void RestartedMicrophoneNeedsItsOwnVisibleAcknowledgementAndFreshRecognition()
    {
        var snapshot = Snapshot();
        var gate = Armed(snapshot);
        gate.Revoke(snapshot, null);
        var binding = gate.Begin(snapshot, 8)!;
        Assert.Null(gate.TryConsume("允许读取这个临时窗口", snapshot, 8, 12, true));
        Assert.True(gate.Arm(binding, snapshot, 8, 20, true));
        Assert.Null(gate.TryConsume("允许读取这个临时窗口", snapshot, 7, 12, true));
        Assert.NotNull(gate.TryConsume("允许读取这个临时窗口", snapshot, 8, 20, true));
    }

    [Fact]
    public void SearchAndResultUseDifferentNoncesWithoutChangingOriginalTurnOrQuery()
    {
        var search = Snapshot("Search");
        var gate = Armed(search);
        var first = gate.TryConsume("允许这次视觉搜索", search, 7, 12, true)!;
        var result = WithTurn(search, search.ForegroundTurn! with
            { WindowConsent = new(Guid.NewGuid(), "ResultRead", 44, "测试应用的临时窗口") });
        var second = gate.Begin(result, 7)!;
        Assert.Equal(first.Binding.TurnId, second.TurnId);
        Assert.NotEqual(first.Binding.ConsentId, second.ConsentId);
        Assert.Equal(search.ForegroundTurn!.InputText, result.ForegroundTurn!.InputText);
        Assert.Null(gate.TryConsume("允许读取这个临时窗口", result, 7, 12, true));
        Assert.True(gate.Arm(second, result, 7, 13, true));
        Assert.Null(gate.TryConsume("允许这次视觉搜索", result, 7, 13, true));
        Assert.NotNull(gate.TryConsume("允许读取这个临时窗口", result, 7, 13, true));
    }

    [Theory]
    [InlineData("允", true)]
    [InlineData("允许读取这个临时", true)]
    [InlineData("拒绝这次视觉", true)]
    [InlineData("", false)]
    [InlineData("不允许", false)]
    [InlineData("允许读取这个临时窗口然后", false)]
    public void ReservedControlPrefixesAreRecognized(string partial, bool expected) =>
        Assert.Equal(expected, VisualSearchVoiceConsentGate.IsConsentPrefix(partial));

    [Theory]
    [InlineData("原是读取这个临时窗口")]
    [InlineData("原是这次视觉搜索")]
    [InlineData("允许这次识别搜索")]
    [InlineData("拒绝这次识别搜索")]
    [InlineData("允许这次阅读搜索")]
    [InlineData("允许这次收索")]
    [InlineData("允许这次读")]
    [InlineData("不要允许这次读取")]
    [InlineData("不允许读取这个临时窗口")]
    public void UnclearPermissionShapeCanOnlyRequestClarification(string text)
    {
        Assert.True(VisualSearchVoiceConsentGate.IsUnclearConsentReply(text));
        Assert.False(VisualSearchVoiceConsentGate.TryParse(text, out _, out _));
        var snapshot = Snapshot();
        var gate = Armed(snapshot);
        Assert.Null(gate.TryConsume(text, snapshot, 7, 12, true));
        // Asking to repeat does not consume or replace the pending nonce.
        Assert.NotNull(gate.TryConsume("允许这次读取", snapshot, 7, 12, true));
    }

    [Theory]
    [InlineData("打开临时窗口")]
    [InlineData("打开微信")]
    [InlineData("搜索允许这次读取")]
    [InlineData("搜索这次读取")]
    [InlineData("搜索这次视觉搜索")]
    [InlineData("打开这次搜索")]
    [InlineData("允许这次搜索然后发送消息")]
    [InlineData("允许这次搜索删除")]
    [InlineData("在浏览器搜索允许读取这个临时窗口")]
    [InlineData("允许这次读取然后发送消息")]
    [InlineData("请停止")]
    [InlineData("停止搜索")]
    [InlineData("请问这个窗口是什么")]
    public void NewTasksStopsAndUnrelatedQuestionsAreNotUnclearPermissionReplies(string text)
    {
        Assert.False(VisualSearchVoiceConsentGate.IsUnclearConsentReply(text));
        Assert.False(VisualSearchVoiceConsentGate.TryParse(text, out _, out _));
    }

    [Theory]
    [InlineData("原是", true)]
    [InlineData("原是读取这个临时窗口", true)]
    [InlineData("打开微信", true)]
    [InlineData("不要允许这次读取", true)]
    [InlineData("停", false)]
    [InlineData("停一下", false)]
    [InlineData("停止", false)]
    [InlineData("请停止", false)]
    [InlineData("停止搜索", false)]
    [InlineData("麻烦取消这次搜索", false)]
    [InlineData("退下", false)]
    public void PendingConsentDefersUncertainPartialsButNeverExplicitInterruptions(string text, bool defer)
    {
        Assert.Equal(defer, VisualSearchVoiceConsentGate.ShouldDeferPartial(text, Snapshot()));
        Assert.False(VisualSearchVoiceConsentGate.ShouldDeferPartial(text, null));
    }

    private static VisualSearchVoiceConsentGate Armed(SessionSnapshotDto snapshot)
    {
        var gate = new VisualSearchVoiceConsentGate();
        Assert.True(gate.Arm(gate.Begin(snapshot, 7)!, snapshot, 7, 12, true));
        return gate;
    }

    private static SessionSnapshotDto WithTurn(SessionSnapshotDto snapshot, UnifiedSessionTurnDto turn) =>
        snapshot with { ForegroundTurn = turn, ActiveTurns = [turn], Turns = [turn] };

    private static SessionSnapshotDto Snapshot(string purpose = "ResultRead")
    {
        var now = new DateTimeOffset(2026, 10, 22, 1, 0, 0, TimeSpan.Zero);
        var turn = new UnifiedSessionTurnDto(Guid.NewGuid(), 1, "在测试应用里搜索示例", "Voice", "AssistantCommand",
            "WaitingForWindowConsent", "None", "ApplicationSearch", null, null, null, null, 18, "测试应用",
            false, false, "这次只读取明确窗口核对搜索结果。", null, now, now, null,
            WindowConsent: new(Guid.NewGuid(), purpose, 42, "测试应用的临时窗口"));
        return new(3, "test-host", now, Guid.NewGuid(), Guid.NewGuid(), "测试话题", turn.Phase,
            null, null, turn, [turn], [turn], []);
    }
}
