using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Security;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

public interface IDesktopVisualTaskExecutor
{
    Task<AssistantCommandResultDto> ExecuteAsync(DesktopTaskPlan plan, DesktopTaskWindow window,
        string operationId, CancellationToken cancellationToken);
}

internal sealed class DesktopVisualTaskExecutionService(IDesktopVisualAutomation automation,
    IDesktopTaskAutomation windows, IDesktopVisualStepPlanner planner, DesktopTaskInteractionChannel interactions,
    IDesktopTaskWindowActivator activator, CapabilityPolicyEngine policy, ILocalTaskStore store,
    DesktopHostState hostState, TimeProvider timeProvider, IDesktopVisualTargetPreview preview) : IDesktopVisualTaskExecutor
{
    private static readonly SkillDescriptor Descriptor = new("windows.desktop-task", "1", "软件任务步骤", true,
        new HashSet<string>(StringComparer.Ordinal) { "desktop.task.step" });

    public async Task<AssistantCommandResultDto> ExecuteAsync(DesktopTaskPlan plan, DesktopTaskWindow window,
        string operationId, CancellationToken cancellationToken)
    {
        var token = cancellationToken;
        token.ThrowIfCancellationRequested();
        if ((plan.Route.Capabilities & ChatModelCapabilities.Vision) == 0)
            throw Failure("model_unsupported", "当前模型没有启用图片理解，无法执行看图操作。请在模型设置中主动选择支持图片的模型后重新下达任务。");
        var description = $"任务：{plan.Instruction}\n目标窗口：{window.Title}\n允许元枢截图并识别这个窗口，将窗口图片和必要文字发送到 {plan.Route.ProviderId} / {plan.Route.ModelId}（{plan.Route.DataDestination}）来规划步骤？请先关闭窗口里的私人或敏感内容。图片仅临时用于本次任务，不保存。每次点击、输入或滚动都会显示本步确认；停止或切换软件会中止。";
        await interactions.RequestAsync(new(window, "TaskObservation", description,
            DesktopTaskStepPolicy.Hash("visual-image-consent:" + plan.CanonicalTarget + JsonSerializer.Serialize(window))), token);
        await ResumeAsync(window, token);
        DesktopVisualObservation? current = null;
        var actions = 0;
        try
        {
            current = await automation.ObserveAsync(window, token);
            var initial = current.Snapshot;
            var history = new List<string>();
            var waits = 0;
            var unchanged = 0;
            string? previousAction = null;
            var repeats = 0;
            for (var step = 0; step < 12; step++)
            {
                token.ThrowIfCancellationRequested();
                var snapshot = current.Snapshot;
                RequireObservation(window, snapshot);
                await interactions.ReportAsync($"正在看图规划第 {step + 1} 步…", token);
                var proposal = await planner.PlanAsync(plan.TurnId, plan.Route, plan.Instruction, current, history, token);
                token.ThrowIfCancellationRequested();
                if (proposal.SnapshotId != snapshot.Id) throw Failure("stale_proposal", "操作建议对应的画面已经过期。");
                if (proposal.Action == DesktopVisualActionKind.Ask)
                    throw Failure("needs_context", "需要你补充或亲自核对：" + proposal.Reason);
                if (proposal.Action == DesktopVisualActionKind.Finish)
                {
                    if (!HasCompletionEvidence(initial, snapshot, proposal, actions))
                        throw Failure("result_unverified", "没有足够的新界面文字证明任务完成，已停止并保留当前结果。");
                    using var fresh = await automation.ObserveAsync(window, token);
                    RequireObservation(window, fresh.Snapshot);
                    if (!SameFrame(snapshot, fresh.Snapshot) || !ContainsEvidence(fresh.Snapshot.OcrText, proposal.EvidenceText!))
                        throw Failure("result_changed", "再次核对时画面发生变化，本次不能标记为完成。");
                    // New OCR proves a changed result exists, not that an arbitrary
                    // application has fulfilled the user's whole goal. The user
                    // must judge that relationship until an app supplies a verifier.
                    var resultDescription = $"请核对本次任务结果。\n原任务：{plan.Instruction}\n窗口：{window.Title}\n实际执行：{actions} 步\n当前画面读到：{proposal.EvidenceText}\n上述文字不能单独证明整个任务完成。请查看软件中的实际结果；仅在原任务已完成时确认，否则拒绝并说明需要继续处理的部分。这个确认不会再点击软件。";
                    await interactions.RequestAsync(new(window, "TaskAction", resultDescription,
                        DesktopTaskStepPolicy.Hash("visual-result:" + bindingForResult(plan, fresh.Snapshot, proposal))), token);
                    await ResumeAsync(window, token);
                    using var final = await automation.ObserveAsync(window, token);
                    if (!SameFrame(fresh.Snapshot, final.Snapshot)) throw Failure("result_changed", "确认期间窗口结果变化，不能标记完成。");
                    token.ThrowIfCancellationRequested();
                    return new("DesktopTask", "Completed", $"已看图执行 {actions} 步，并由你核对确认任务结果：{proposal.EvidenceText}", "DesktopVisualResultConfirmed");
                }
                if (proposal.Action == DesktopVisualActionKind.Wait)
                {
                    if (++waits > 3) throw Failure("no_progress", "窗口持续没有准备好，已停止等待。");
                    await Task.Delay(TimeSpan.FromMilliseconds(800), token);
                    var ready = await automation.ObserveAsync(window, token);
                    current.Dispose(); current = ready;
                    history.Add("等待后重新看图；尚未执行新的输入。");
                    continue;
                }
                waits = 0;
                RequireAction(proposal);
                var actionIdentity = JsonSerializer.Serialize(new { proposal.Action, proposal.Bounds, proposal.Text });
                repeats = previousAction == actionIdentity ? repeats + 1 : 1;
                previousAction = actionIdentity;
                if (repeats > 3) throw Failure("repeated_action", "连续重复同一画面目标，已停止以便核对。");
                var binding = DesktopTaskStepPolicy.Hash(plan.CanonicalTarget + JsonSerializer.Serialize(snapshot) + JsonSerializer.Serialize(proposal));
                var region = RegionName(snapshot, proposal.Bounds!);
                var actionDescription = $"任务：{plan.Instruction}\n窗口：{window.Title}\n第 {step + 1} 步：{ActionName(proposal.Action)}「{proposal.Target}」（{region}）\n";
                if (proposal.Action == DesktopVisualActionKind.TypeText)
                    actionDescription += $"将在所选输入位置追加：{proposal.Text}\n";
                actionDescription += "请查看“元枢·看图目标确认”中的红框和十字中心，核对实际点击位置。这是模型对画面的推测。仅确认这一步；确认后会重新检查画面，变化则不执行。拒绝会结束本次任务。";
                using (var visibleTarget = await preview.ShowAsync(current, proposal, token))
                {
                    if (!visibleTarget.IsVisible) throw Failure("preview_unavailable", "精确目标预览没有完整显示，本步未执行。");
                    await interactions.RequestAsync(new(window, "TaskAction", actionDescription, binding), token);
                    token.ThrowIfCancellationRequested();
                    if (!visibleTarget.IsVisible) throw Failure("preview_unavailable", "精确目标预览已关闭或被遮挡，本步未执行。");
                }
                await ResumeAsync(window, token);
                await interactions.ReportAsync($"正在{ActionName(proposal.Action)}「{proposal.Target}」…", token);
                var receipt = await ExecuteStepAsync(plan, snapshot, proposal, binding, step, operationId, token);
                if (!receipt.InputSent) throw Failure("input_unverified", "没有收到实际输入的执行回执，已停止。");
                actions++;
                await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                var next = await automation.ObserveAsync(window, token);
                unchanged = next.Snapshot.ContentFingerprint == snapshot.ContentFingerprint ? unchanged + 1 : 0;
                current.Dispose(); current = next;
                if (unchanged >= 2) throw Failure("no_progress", "连续操作后画面没有变化，已停止重复尝试。");
                history.Add(JsonSerializer.Serialize(new { step = step + 1, action = proposal.Action.ToString(),
                    target = proposal.Target[..Math.Min(100, proposal.Target.Length)], status = "executed" },
                    new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            throw Failure("step_limit", "已达到本次看图操作的步骤上限，尚未核实整个任务完成。");
        }
        catch (DesktopTaskException exception) when (actions > 0 && !token.IsCancellationRequested)
        {
            throw new DesktopTaskException(exception.Code, $"此前已实际执行 {actions} 步，任务尚未完成。{exception.Message}");
        }
        finally { current?.Dispose(); }
    }

    private async Task ResumeAsync(DesktopTaskWindow expected, CancellationToken token)
    {
        var actual = await windows.BindAsync(expected.Handle, token);
        if (!DesktopTaskExecutionService.SameWindow(expected, actual)) throw Failure("window_changed", "授权的窗口身份发生变化。");
        await activator.ResumeAsync(expected, token);
    }

    private static void RequireObservation(DesktopTaskWindow window, DesktopVisualSnapshot snapshot)
    {
        if (!DesktopTaskExecutionService.SameWindow(window, snapshot.Window)) throw Failure("window_changed", "截图不属于授权窗口。");
        // Do not disclose a credential-entry screen or ask a model to operate it.
        if (Regex.IsMatch(snapshot.OcrText, @"密码|口令|验证码|恢复密钥|助记词|私钥|password|passcode|recovery\s*(?:key|phrase)|private\s*key|api\s*key", RegexOptions.IgnoreCase))
            throw Failure("sensitive_window", "当前画面包含凭据或验证内容，未发送图片，请先亲自完成或关闭这个界面。");
    }

    internal static void RequireAction(DesktopVisualProposal proposal)
    {
        if (proposal.Action is not (DesktopVisualActionKind.Click or DesktopVisualActionKind.DoubleClick
            or DesktopVisualActionKind.TypeText or DesktopVisualActionKind.ScrollDown or DesktopVisualActionKind.ScrollUp)
            || proposal.Bounds is null || string.IsNullOrWhiteSpace(proposal.Target))
            throw Failure("action_invalid", "模型没有提供可靠的画面目标。");
        // Reject known sensitive suggestions. This filter is NOT a semantic
        // guarantee about arbitrary pixels; the exact visual target preview and
        // separate user review remain necessary for every pointer action.
        if (Regex.IsMatch(proposal.Target, @"发送|发布|付款|支付|转账|购买|下单|删除|清空|卸载|格式化|密码|口令|验证码|安全设置|账号设置|账户设置|管理员|命令|终端|控制台|脚本|send|publish|submit|pay(?:ment)?|purchase|delete|remove|uninstall|password|terminal|console|powershell|cmd\b", RegexOptions.IgnoreCase))
            throw Failure("sensitive_action", "这个步骤涉及敏感操作或命令入口，当前看图模式不能核实所需对象和内容，请亲自处理。");
        if (proposal.Action == DesktopVisualActionKind.TypeText && (string.IsNullOrWhiteSpace(proposal.Text)
            || proposal.Text.Length > 500 || proposal.Text.Any(char.IsControl)
            || Regex.IsMatch(proposal.Text, @"https?://|file:|[A-Za-z]:\\|powershell|cmd(?:\.exe)?\b|Invoke-|Start-Process|Remove-Item|password|密码|验证码", RegexOptions.IgnoreCase)))
            throw Failure("text_rejected", "当前看图模式只允许向核实的输入位置追加单行普通文字。");
    }

    internal static bool HasCompletionEvidence(DesktopVisualSnapshot initial, DesktopVisualSnapshot current,
        DesktopVisualProposal proposal, int actions) => actions > 0 && proposal.Action == DesktopVisualActionKind.Finish
        && !string.IsNullOrWhiteSpace(proposal.EvidenceText) && Normalize(proposal.EvidenceText).Length >= 3
        && !Regex.IsMatch(Normalize(proposal.EvidenceText), @"^(?:成功|已完成|完成|操作成功|执行成功|done|success|completed)$", RegexOptions.IgnoreCase)
        && initial.ContentFingerprint != current.ContentFingerprint
        && ContainsEvidence(current.OcrText, proposal.EvidenceText) && !ContainsEvidence(initial.OcrText, proposal.EvidenceText);

    private static bool ContainsEvidence(string text, string evidence) => Normalize(text).Contains(Normalize(evidence), StringComparison.Ordinal);
    private static string bindingForResult(DesktopTaskPlan plan, DesktopVisualSnapshot snapshot, DesktopVisualProposal proposal) =>
        plan.CanonicalTarget + JsonSerializer.Serialize(snapshot) + JsonSerializer.Serialize(proposal);
    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    private static bool SameFrame(DesktopVisualSnapshot a, DesktopVisualSnapshot b) =>
        DesktopTaskExecutionService.SameWindow(a.Window, b.Window) && a.PhysicalBounds == b.PhysicalBounds
        && a.SourceWidth == b.SourceWidth && a.SourceHeight == b.SourceHeight && a.Dpi == b.Dpi
        && a.ContentFingerprint == b.ContentFingerprint;

    private static string RegionName(DesktopVisualSnapshot snapshot, DesktopVisualBounds bounds)
    {
        var horizontal = (bounds.X + bounds.Width / 2) * 3L / snapshot.ImageWidth;
        var vertical = (bounds.Y + bounds.Height / 2) * 3L / snapshot.ImageHeight;
        return "窗口" + (vertical == 0 ? "上部" : vertical == 1 ? "中部" : "下部")
            + (horizontal == 0 ? "偏左" : horizontal == 1 ? "居中" : "偏右");
    }
    private static string ActionName(DesktopVisualActionKind action) => action switch
    {
        DesktopVisualActionKind.Click => "点击", DesktopVisualActionKind.DoubleClick => "双击",
        DesktopVisualActionKind.TypeText => "点击并输入", DesktopVisualActionKind.ScrollUp => "向上滚动",
        DesktopVisualActionKind.ScrollDown => "向下滚动", _ => "操作"
    };

    private async Task<DesktopVisualReceipt> ExecuteStepAsync(DesktopTaskPlan plan, DesktopVisualSnapshot snapshot,
        DesktopVisualProposal proposal, string binding, int step, string operationId, CancellationToken token)
    {
        var device = hostState.Snapshot.LocalDevice ?? throw Failure("host_stopped", "元枢服务尚未启动。");
        var now = timeProvider.GetUtcNow();
        var command = new CommandRecord
        {
            Id = Guid.NewGuid(), SourceDeviceId = device.Id, CommandType = CommandType.ExecuteDesktopAction,
            IdempotencyKey = $"{operationId}-visual-step-{step}", ReceivedAtUtc = now, ExpiresAtUtc = now.AddMinutes(2),
            Status = CommandStatus.Received,
            PayloadJson = JsonSerializer.Serialize(new { actionKind = "DesktopVisualStep", turnId = plan.TurnId,
                step, action = proposal.Action.ToString(), binding, taskTarget = plan.CanonicalTarget })
        };
        var registration = await store.RegisterCommandAsync(command, token);
        if (!registration.Accepted) throw Failure("duplicate_step", "这一步已经处理过，不会重复执行。");
        var scope = new DesktopTaskStepAuthorization(plan.TurnId, snapshot.Id,
            DesktopTaskStepPolicy.Hash(JsonSerializer.Serialize(snapshot.Window)), binding);
        var decision = policy.AuthorizeOnce(Descriptor, new(Guid.Empty, Guid.NewGuid(), Descriptor.Id, "desktop.task.step",
            JsonSerializer.Serialize(scope), [new("DesktopTaskStep", null, scope.ToScopeValue(), "Execute")],
            SkillAuthorizationOrigin.ExplicitUser, command.Id));
        if (!decision.IsAllowed)
        {
            await store.CompleteCommandAsync(command.Id, CommandStatus.Rejected, timeProvider.GetUtcNow(), decision.Code, CancellationToken.None);
            throw new UnauthorizedAccessException(decision.UserMessage);
        }
        try
        {
            token.ThrowIfCancellationRequested();
            var receipt = await automation.ExecuteAsync(snapshot, proposal, token);
            token.ThrowIfCancellationRequested();
            if (!receipt.InputSent) throw Failure("input_unverified", "原生输入没有返回实际执行回执。");
            await store.CompleteCommandAsync(command.Id, CommandStatus.Processed, timeProvider.GetUtcNow(), null, CancellationToken.None);
            return receipt;
        }
        catch
        {
            await store.CompleteCommandAsync(command.Id, CommandStatus.Failed, timeProvider.GetUtcNow(),
                token.IsCancellationRequested ? "cancelled" : "desktop_visual_step_failed", CancellationToken.None);
            throw;
        }
    }
    private static DesktopTaskException Failure(string code, string message) => new("desktop_visual_" + code, message);
}
