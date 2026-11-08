using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopTaskStepPolicyTests
{
    [Theory]
    [InlineData("发送", "发送消息", "金额", "10", "正文", "测试消息")]
    [InlineData("保存", "保存文件", "收件人", "测试联系人", "正文", "测试消息")]
    [InlineData("付款", "付款给测试商家", "收款方", "测试商家", "正文", "测试消息")]
    [InlineData("删除", "删除文件", "收件人", "测试联系人", "正文", "测试消息")]
    public void ConsequentialActionRejectsFieldsFromAnotherEffectCategory(
        string action, string instruction, string targetName, string targetValue, string contentName, string contentValue)
    {
        var button = Button(action);
        var snapshot = Snapshot(button, Field(2, targetName, targetValue), Field(3, contentName, contentValue));

        AssertEffectRejected("desktop_task_effect_unidentified", instruction, snapshot, button);
    }

    [Theory]
    [InlineData("发送", "发送消息", "收件人", "测试联系人", "正文", "测试消息")]
    [InlineData("保存", "保存文件", "文件名", "synthetic-review.txt", "说明", "测试文件")]
    [InlineData("付款", "付款给测试商家", "收款方", "测试商家", "金额", "10")]
    [InlineData("授权", "为测试账号授权", "账号", "synthetic-account", "权限", "只读")]
    public void MatchingEffectContextStillRequiresVisibleStepConfirmation(
        string action, string instruction, string targetName, string targetValue, string contentName, string contentValue)
    {
        var button = Button(action);
        var snapshot = Snapshot(button, Field(2, targetName, targetValue), Field(3, contentName, contentValue));
        var proposal = Invoke(snapshot, button);

        DesktopTaskStepPolicy.RequireEffectContext(instruction, snapshot, button, proposal);

        Assert.True(DesktopTaskStepPolicy.RequiresStepConfirmation(button, proposal));
    }

    [Theory]
    [InlineData("发送", "发送消息", "收件人", "测试联系人", "正文")]
    [InlineData("付款", "付款给测试商家", "收款方", "测试商家", "金额")]
    public void IdentifiedRecipientWithoutContentOrAmountDoesNotAuthorizeEffect(
        string action, string instruction, string targetName, string targetValue, string contentName)
    {
        var button = Button(action);
        var snapshot = Snapshot(button, Field(2, targetName, targetValue), Field(3, contentName, ""));

        AssertEffectRejected("desktop_task_effect_unidentified", instruction, snapshot, button);
    }

    [Fact]
    public void SelectedItemIdentifiesDeletionButDoesNotIdentifySaveDestination()
    {
        var selected = Field(2, "synthetic-review.txt", "") with
        {
            ControlType = "ListItem", State = "selected=True", Actions = [DesktopTaskActionKind.Select]
        };
        var delete = Button("删除");
        var deleteSnapshot = Snapshot(delete, selected);
        DesktopTaskStepPolicy.RequireEffectContext("删除选中的测试文件", deleteSnapshot, delete, Invoke(deleteSnapshot, delete));
        Assert.True(DesktopTaskStepPolicy.RequiresStepConfirmation(delete, Invoke(deleteSnapshot, delete)));

        var save = Button("保存");
        AssertEffectRejected("desktop_task_effect_unidentified", "保存测试文件", Snapshot(save, selected), save);
    }

    [Fact]
    public void TruncatedObservationRejectsEvenApparentlyCompleteEffectContext()
    {
        var button = Button("发送");
        var snapshot = Snapshot(button, Field(2, "收件人", "测试联系人"), Field(3, "正文", "测试消息")) with { Truncated = true };

        AssertEffectRejected("desktop_task_effect_unidentified", "发送消息", snapshot, button);
    }

    [Fact]
    public void UnknownButtonRequiresSeparateConfirmation()
    {
        var button = Button("预览");
        var snapshot = Snapshot(button);
        var proposal = Invoke(snapshot, button);

        DesktopTaskStepPolicy.RequireEffectContext("预览内容", snapshot, button, proposal);

        Assert.True(DesktopTaskStepPolicy.RequiresStepConfirmation(button, proposal));
    }

    [Theory]
    [InlineData("填写正文为“发送消息”")]
    [InlineData("填写正文为\"发送消息\"")]
    [InlineData("填写正文为'发送消息'")]
    [InlineData("填写正文为‘发送消息’")]
    [InlineData("填写正文，不要发送消息")]
    [InlineData("填写正文，禁止发送消息")]
    [InlineData("填写正文，不要自动发送消息")]
    [InlineData("Fill the body. Do not send the message.")]
    [InlineData("Fill the body. Don't send the message.")]
    [InlineData("填写正文，不要把消息发送出去")]
    [InlineData("填写正文，不要帮我发送消息")]
    public void QuotedTextOrNegatedActionDoesNotAuthorizeSending(string instruction)
    {
        var button = Button("发送 send");
        var snapshot = Snapshot(button, Field(2, "收件人", "测试联系人"), Field(3, "正文", "测试消息"));

        AssertEffectRejected("desktop_task_outside_goal", instruction, snapshot, button);
    }

    [Theory]
    [InlineData("发送正文为“请不要发送广告”的消息")]
    [InlineData("不要删除测试文件，发送消息给测试联系人")]
    public void ExplicitSendingOutsideQuotedContentAndUnrelatedProhibitionRemainsAllowed(string instruction)
    {
        var button = Button("发送");
        var snapshot = Snapshot(button, Field(2, "收件人", "测试联系人"), Field(3, "正文", "测试消息"));

        DesktopTaskStepPolicy.RequireEffectContext(instruction, snapshot, button, Invoke(snapshot, button));
    }

    [Theory]
    [InlineData("结果", "发送失败", "", "发送")]
    [InlineData("发送失败", "发送成功", "", "发送成功")]
    [InlineData("发送失败", "", "selected=True", "selected=True")]
    [InlineData("结果", "正在发送，准备完成", "", "完成")]
    [InlineData("结果", "Send failed", "", "Send")]
    public void FailureOrPendingTextInWholeResultRejectsCherryPickedSuccessEvidence(
        string name, string value, string state, string evidence)
    {
        var before = Status("结果", "", "");
        var after = Status(name, value, state);

        Assert.False(HasCompletionEvidence("发送测试消息", before, after, evidence));
    }

    [Theory]
    [InlineData("发送消息")]
    [InlineData("保存文件")]
    [InlineData("支付订单")]
    [InlineData("提交表单")]
    [InlineData("发布内容")]
    [InlineData("搜索测试资料")]
    [InlineData("Send the message")]
    [InlineData("Save the file")]
    public void ChangedSelectionAloneDoesNotProveConsequentialTaskCompletion(string instruction)
    {
        Assert.False(HasCompletionEvidence(instruction,
            Status("测试项目", "", "selected=False"), Status("测试项目", "", "selected=True"), "selected=True"));
    }

    [Fact]
    public void ChangedSelectionCanProveSelectionTaskCompletion()
    {
        Assert.True(HasCompletionEvidence("选择测试项目",
            Status("测试项目", "", "selected=False"), Status("测试项目", "", "selected=True"), "selected=True"));
    }

    [Theory]
    [InlineData("发送消息", "发送成功")]
    [InlineData("保存文件", "文件已保存")]
    [InlineData("支付订单", "支付成功")]
    public void ChangedVisibleOutcomeCanProveCompletion(string instruction, string outcome)
    {
        Assert.True(HasCompletionEvidence(instruction, Status("结果", "", ""), Status("结果", outcome, ""), outcome));
    }

    [Theory]
    [InlineData("发送测试消息", false)]
    [InlineData("保存测试消息", false)]
    [InlineData("填写正文为测试消息", true)]
    public void InputReadbackProvesFillingButNotSendingOrSaving(string instruction, bool expected)
    {
        Assert.Equal(expected, HasCompletionEvidence(instruction, Field(2, "正文", ""), Field(2, "正文", "测试消息"), "测试消息"));
    }

    [Fact]
    public void ChangedTechnicalAvailabilityIsNotCompletionEvidence()
    {
        Assert.False(HasCompletionEvidence("选择测试项目",
            Status("测试项目", "", "enabled=False"), Status("测试项目", "", "enabled=True"), "enabled=True"));
    }

    [Fact]
    public void ChangedSelectionCannotReuseUnchangedSuccessLabelAsProof()
    {
        Assert.False(HasCompletionEvidence("发送消息",
            Status("发送成功", "", "selected=False"), Status("发送成功", "", "selected=True"), "发送成功"));
    }

    [Fact]
    public void CompletionRequiresExecutedActionAndFreshBoundObservation()
    {
        var initial = Snapshot(Status("结果", "", ""));
        var latest = Snapshot(Status("结果", "发送成功", ""));
        var finish = Finish(latest, "发送成功");

        Assert.False(DesktopTaskStepPolicy.HasCompletionEvidence("发送消息", initial, latest, finish, 0));
        Assert.False(DesktopTaskStepPolicy.HasCompletionEvidence("发送消息", initial, latest, finish with { SnapshotId = initial.Id }, 1));
        Assert.False(DesktopTaskStepPolicy.HasCompletionEvidence("发送消息", latest, latest, finish, 1));
    }

    private static void AssertEffectRejected(string code, string instruction, DesktopTaskSnapshot snapshot, DesktopTaskControl button)
    {
        var exception = Assert.Throws<DesktopTaskException>(() =>
            DesktopTaskStepPolicy.RequireEffectContext(instruction, snapshot, button, Invoke(snapshot, button)));
        Assert.Equal(code, exception.Code);
    }

    private static bool HasCompletionEvidence(string instruction, DesktopTaskControl before, DesktopTaskControl after, string evidence)
    {
        var initial = Snapshot(before);
        var latest = Snapshot(after);
        return DesktopTaskStepPolicy.HasCompletionEvidence(instruction, initial, latest, Finish(latest, evidence), 1);
    }

    private static DesktopTaskControl Button(string name) => new("button", [1], name, "", "Button", "", "", "enabled=True", false,
        new(10, 10, 80, 30), [DesktopTaskActionKind.Invoke]);

    private static DesktopTaskControl Field(int id, string name, string value) => new($"control-{id}", [id], name, "", "Edit", "", value,
        "enabled=True", false, new(10, 50 * id, 180, 30), [DesktopTaskActionKind.SetText]);

    private static DesktopTaskControl Status(string name, string value, string state) => new("result", [9], name, "", "Text", "", value,
        state, true, new(10, 100, 180, 30), []);

    private static DesktopTaskSnapshot Snapshot(params DesktopTaskControl[] controls) => new(Guid.NewGuid(),
        new(100, 200, "synthetic-fixture", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            @"C:\Synthetic\fixture.exe", "SyntheticWindow", "测试软件"), controls, false);

    private static DesktopTaskProposal Invoke(DesktopTaskSnapshot snapshot, DesktopTaskControl button) =>
        new(snapshot.Id, DesktopTaskActionKind.Invoke, button.Id, null, "执行本次任务中的一步");

    private static DesktopTaskProposal Finish(DesktopTaskSnapshot snapshot, string evidence) =>
        new(snapshot.Id, DesktopTaskActionKind.Finish, null, null, "核对结果", snapshot.Controls[0].Id, evidence);
}
