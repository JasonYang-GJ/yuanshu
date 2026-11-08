using System.Runtime.InteropServices;
using System.Text.Json;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Security;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record DesktopTaskPlan(Guid TurnId, string Instruction, FrozenChatModelRoute Route,
    KnownDesktopApplication? Application, string? ApplicationBinding, ForegroundWindowSnapshot? Window)
{
    public string CanonicalTarget => "desktop-task:" + DesktopTaskStepPolicy.Hash(JsonSerializer.Serialize(new
    {
        TurnId, Instruction, ApplicationId = Application?.Id, ApplicationBinding,
        WindowHandle = Window?.WindowHandle, WindowPid = Window?.ProcessId, WindowStarted = Window?.ProcessStartTimeUtc,
        Route.ProviderId, Route.ModelId, Route.DataDestination
    }));
}

public interface IDesktopTaskExecutor
{
    Task<AssistantCommandResultDto> ExecuteAsync(DesktopTaskPlan plan, string operationId, CancellationToken cancellationToken);
}

internal sealed class DesktopTaskExecutionService(IDesktopTaskAutomation automation,
    IDesktopTaskStepPlanner planner, DesktopTaskInteractionChannel interactions,
    IForegroundWindowContextProvider foreground, IInstalledApplicationCatalog applications,
    DesktopActionEntryService desktopActions, CapabilityPolicyEngine policy, ILocalTaskStore store,
    DesktopHostState hostState, TimeProvider timeProvider, IDesktopTaskWindowActivator activator,
    IDesktopVisualTaskExecutor? visualTasks = null) : IDesktopTaskExecutor
{
    private static readonly SkillDescriptor Descriptor = new("windows.desktop-task", "1", "软件任务步骤", true,
        new HashSet<string>(StringComparer.Ordinal) { "desktop.task.step" });

    public async Task<AssistantCommandResultDto> ExecuteAsync(DesktopTaskPlan plan, string operationId,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(8));
        var token = budget.Token;
        var window = await PrepareWindowAsync(plan, operationId, token);
        if (visualTasks is not null && DesktopTaskRequest.TryParse(plan.Instruction, out var parsed) && parsed!.PrefersVisual)
            return await visualTasks.ExecuteAsync(plan, window, operationId, token);
        var consentDescription = $"任务：{plan.Instruction}\n目标：{window.Title}\n允许元枢读取这个窗口提供的控件名称、状态和必要文字，发送到 {plan.Route.ProviderId} / {plan.Route.ModelId}（{plan.Route.DataDestination}），分步完成本次任务？不会截图；密码字段不读取。普通填写、选择、滚动和已识别的计算器运算可自动继续；其他按钮调用、覆盖及敏感操作会单独显示本步确认。切换到其他软件或说停止会停止。";
        await interactions.RequestAsync(new(window, "TaskObservation", consentDescription,
            DesktopTaskStepPolicy.Hash(plan.CanonicalTarget + JsonSerializer.Serialize(window))), token);
        await ResumeOwnedWindowAsync(window, token);
        var initial = await automation.ObserveAsync(window, token);
        var snapshot = initial;
        var history = new List<string>();
        var actions = 0;
        var unchanged = 0;
        var waits = 0;
        string? lastAction = null;
        var repeatedAction = 0;
        for (var step = 0; step < DesktopTaskLimits.MaximumSteps; step++)
        {
            token.ThrowIfCancellationRequested();
            await interactions.ReportAsync($"正在读取第 {step + 1} 步的界面状态…", token);
            DesktopTaskProposal proposal;
            try
            {
                proposal = await planner.PlanAsync(plan.TurnId, plan.Route, plan.Instruction, snapshot, history, token);
            }
            catch (DesktopTaskException exception) when (actions > 0 && !token.IsCancellationRequested)
            {
                // A rejected next proposal cannot undo earlier native calls.
                // Preserve the failure code and make partial progress explicit.
                throw new DesktopTaskException(exception.Code,
                    $"此前已执行 {actions} 步，任务尚未完成。后续规划已停止：{exception.Message}");
            }
            token.ThrowIfCancellationRequested();
            if (proposal.SnapshotId != snapshot.Id) throw Failure("stale_proposal", "操作建议对应的界面已经过期。");
            if (proposal.Action == DesktopTaskActionKind.Ask)
            {
                if (actions == 0 && visualTasks is not null && (plan.Route.Capabilities & ChatModelCapabilities.Vision) != 0)
                    return await visualTasks.ExecuteAsync(plan, window, operationId, token);
                throw Failure("needs_context", "需要你补充或亲自处理：" + (proposal.Text ?? proposal.Reason));
            }
            if (proposal.Action == DesktopTaskActionKind.Finish)
            {
                if (!DesktopTaskStepPolicy.HasCompletionEvidence(plan.Instruction, initial, snapshot, proposal, actions))
                    throw Failure("result_unverified", $"已执行 {actions} 步，但没有足够的真实界面结果证明整个任务完成，已停止。");
                // Re-read after the model reply. Navigation/user changes during planning
                // cannot turn an old snapshot into a current completion receipt.
                var fresh = await automation.ObserveAsync(window, token);
                var evidence = snapshot.Controls.Single(c => c.Id == proposal.EvidenceTargetId);
                var current = fresh.Controls.SingleOrDefault(c => c.RuntimeId.SequenceEqual(evidence.RuntimeId));
                if (current is null || current.Name != evidence.Name || current.Value != evidence.Value || current.State != evidence.State
                    || current.AutomationId != evidence.AutomationId || current.ControlType != evidence.ControlType || current.ParentName != evidence.ParentName)
                    throw Failure("result_changed", "核对结果时界面又发生变化，本次不能标记为完成。");
                token.ThrowIfCancellationRequested();
                return new("DesktopTask", "Completed", $"已执行 {actions} 步并核对界面结果：{proposal.EvidenceText}", "DesktopTaskVerified");
            }
            if (proposal.Action == DesktopTaskActionKind.Wait)
            {
                if (++waits > 3) throw Failure("no_progress", "软件持续没有准备好，已停止等待。");
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                snapshot = await automation.ObserveAsync(window, token);
                history.Add("等待软件就绪并重新读取。");
                continue;
            }
            waits = 0;
            var control = snapshot.Controls.SingleOrDefault(c => c.Id == proposal.TargetId)
                ?? throw Failure("unknown_control", "没有找到本次快照中的目标控件。");
            var actionIdentity = JsonSerializer.Serialize(new { control.RuntimeId, proposal.Action, proposal.Text });
            repeatedAction = actionIdentity == lastAction ? repeatedAction + 1 : 1;
            lastAction = actionIdentity;
            if (repeatedAction > 3 && proposal.Action is not (DesktopTaskActionKind.ScrollDown or DesktopTaskActionKind.ScrollUp))
                throw Failure("repeated_action", "模型连续重复同一个控件步骤，已停止以便你核对当前结果。没有继续重复操作。");
            var binding = DesktopTaskStepPolicy.Hash(DesktopTaskStepPolicy.ActionBinding(plan.TurnId, window, control, proposal)
                + StateFingerprint(snapshot));
            DesktopTaskStepPolicy.RequireEffectContext(plan.Instruction, snapshot, control, proposal);
            if (DesktopTaskStepPolicy.RequiresStepConfirmation(control, proposal, window))
            {
                var description = $"任务：{plan.Instruction}\n第 {step + 1} 步：{ActionName(proposal.Action)}「{control.Name}」\n软件窗口：{snapshot.Window.Title}\n";
                if (proposal.Action == DesktopTaskActionKind.SetText)
                {
                    description += $"原内容：{control.Value}\n将填写：{proposal.Text}\n";
                    description += "本步仅填写上述控件，不点击提交或保存。仅确认当前显示的这一步；拒绝会结束任务。";
                }
                else
                {
                    var context = snapshot.Controls.Where(c => c.Value.Length > 0
                        && (c.Actions.Contains(DesktopTaskActionKind.SetText)
                            || c.Value != c.Name && c.ControlType != "Document"
                            || System.Text.RegularExpressions.Regex.IsMatch(c.Name + " " + c.ParentName,
                                "收件人|联系人|接收方|正文|内容|消息|文件|路径|金额|商品|订单|账号|账户|recipient|contact|body|message|file|path|amount|order|account",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                    .Select(c => $"{c.Name}：{c.Value}")
                    .Concat(snapshot.Controls.Where(c => c.State.Contains("selected=True", StringComparison.OrdinalIgnoreCase))
                        .Select(c => $"已选择：{c.Name}"))
                    .Distinct(StringComparer.Ordinal);
                    description += "当前输入和选择：\n" + string.Join("\n", context)
                        + "\n这个步骤可能修改内容或触发软件功能。仅确认当前显示的这一步；拒绝会结束任务。";
                }
                await interactions.RequestAsync(new(snapshot.Window, "TaskAction", description, binding), token);
                await ResumeOwnedWindowAsync(window, token);
                var reviewed = await automation.ObserveAsync(window, token);
                if (StateFingerprint(reviewed) != StateFingerprint(snapshot))
                    throw Failure("review_context_changed", "确认期间当前内容、选择对象或控件发生变化，本步未执行，请重新下达任务。");
            }
            await interactions.ReportAsync($"正在执行第 {step + 1} 步：{ActionName(proposal.Action)}「{control.Name}」", token);
            _ = await ExecuteStepAsync(plan, window, snapshot, control, proposal, binding, step, operationId, token);
            actions++;
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            var next = await automation.ObserveAsync(window, token);
            if (StateFingerprint(next) == StateFingerprint(snapshot)) unchanged++; else unchanged = 0;
            if (unchanged >= 2) throw Failure("no_progress", "连续操作后界面没有可验证的变化，已停止重复尝试。");
            history.Add(JsonSerializer.Serialize(new
            {
                step = step + 1, action = proposal.Action.ToString(),
                target = control.Name[..Math.Min(control.Name.Length, 100)],
                identifier = control.AutomationId[..Math.Min(control.AutomationId.Length, 100)],
                // Invoke receipts cannot directly prove the overall result. The
                // call did happen; marking it "unverified" invites the planner
                // to repeat cumulative actions such as calculator digits.
                status = "executed"
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            snapshot = next;
        }
        throw Failure("step_limit", "已达到本次任务的步骤上限，尚未确认全部完成；请检查当前结果再继续。");
    }

    private async Task<DesktopTaskWindow> PrepareWindowAsync(DesktopTaskPlan plan, string operationId, CancellationToken token)
    {
        var selected = plan.Window;
        DesktopTaskWindow? launchedWindow = null;
        if (plan.Application is { } app)
        {
            var current = applications.FindById(app.Id) ?? throw Failure("target_missing", "原来指定的软件已不在应用目录中。");
            if (InstalledApplicationCatalog.CreateTargetBinding(current) != plan.ApplicationBinding)
                throw Failure("target_changed", "指定软件的登记目标已变化，未执行。");
            await interactions.ReportAsync($"正在打开{app.DisplayName}…", token);
            var opened = await desktopActions.ExecuteBoundApplicationAsync(new("OpenApplication", app.Id, true,
                operationId + "-open", AuthorizationSource: "ExplicitTask"), plan.ApplicationBinding!, token);
            if (!opened.Succeeded) throw Failure("open_failed", opened.Message);
            launchedWindow = WindowsDesktopTaskWindowResolver.ResolveLaunchedWindow(GetForegroundWindow().ToInt64(),
                opened.ProcessId ?? throw Failure("target_changed", "打开的软件没有返回可核实的进程身份。"));
            launchedWindow = await WaitForContentHostAsync(launchedWindow, token);
            selected = foreground.ResolveWindow(launchedWindow.Handle);
            if (selected is null || selected.ProcessId != opened.ProcessId)
                throw Failure("target_changed", "打开的软件窗口未能与任务目标绑定。");
        }
        if (selected is null) throw Failure("window_missing", "请先切换到要操作的软件，再下达任务。");
        var bound = await automation.BindAsync(selected.WindowHandle, token);
        if (bound.FrameIdentity is { } frame && frame.Handle == selected.WindowHandle)
        {
            if (frame.ProcessId != selected.ProcessId || frame.ProcessStartedAtUtc != selected.ProcessStartTimeUtc
                || bound.Title != selected.WindowTitle)
                throw Failure("target_changed", "指定窗口的系统宿主或内容标题已变化。");
            selected = foreground.ResolveWindow(bound.Handle)
                ?? throw Failure("target_changed", "无法重新核实系统宿主中的应用内容窗口。");
        }
        if (bound.ProcessId != selected.ProcessId || bound.ProcessStartedAtUtc != selected.ProcessStartTimeUtc)
            throw Failure("target_changed", "任务开始前窗口身份已变化。");
        if (launchedWindow is not null && !SameWindow(launchedWindow, bound))
            throw Failure("target_changed", "软件启动交接后窗口或承载窗口已变化。");
        return bound;
    }

    private async Task<DesktopTaskWindow> WaitForContentHostAsync(DesktopTaskWindow initial, CancellationToken token)
    {
        // A newly launched UWP content HWND can briefly exist before Windows
        // attaches its frame. Settle this metadata-only handoff BEFORE consent.
        if (initial.ClassName != "Windows.UI.Core.CoreWindow" || initial.FrameIdentity is not null) return initial;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), token);
            var current = await automation.BindAsync(initial.Handle, token);
            if (!SameWindow(initial with { FrameIdentity = current.FrameIdentity }, current))
                throw Failure("target_changed", "等待软件窗口就绪时应用身份发生变化。");
            if (current.FrameIdentity is not null) return current;
        }
        throw Failure("window_not_ready", "软件的内容窗口尚未与系统宿主稳定连接，请稍后重试。");
    }

    private async Task ResumeOwnedWindowAsync(DesktopTaskWindow expected, CancellationToken token)
    {
        var actual = await automation.BindAsync(expected.Handle, token);
        if (!SameWindow(expected, actual)) throw Failure("target_changed", "授权对应的软件窗口已经变化。");
        // Only this explicit consent continuation may restore its exact bound target.
        // Ordinary iteration never activates a window after the user switches away.
        await activator.ResumeAsync(expected, token);
    }

    internal static bool SameWindow(DesktopTaskWindow a, DesktopTaskWindow b) => a.Handle == b.Handle
        && a.ProcessId == b.ProcessId && a.ProcessStartedAtUtc == b.ProcessStartedAtUtc
        && string.Equals(a.ExecutablePath, b.ExecutablePath, StringComparison.OrdinalIgnoreCase) && a.ClassName == b.ClassName
        && a.FrameIdentity == b.FrameIdentity;

    private async Task<DesktopTaskActionReceipt> ExecuteStepAsync(DesktopTaskPlan plan, DesktopTaskWindow window,
        DesktopTaskSnapshot snapshot, DesktopTaskControl control, DesktopTaskProposal proposal, string binding,
        int step, string operationId, CancellationToken token)
    {
        var device = hostState.Snapshot.LocalDevice ?? throw Failure("host_stopped", "元枢服务尚未启动。");
        var now = timeProvider.GetUtcNow();
        var command = new CommandRecord
        {
            Id = Guid.NewGuid(), SourceDeviceId = device.Id, CommandType = CommandType.ExecuteDesktopAction,
            IdempotencyKey = $"{operationId}-step-{step}", ReceivedAtUtc = now, ExpiresAtUtc = now.AddMinutes(2),
            Status = CommandStatus.Received,
            PayloadJson = JsonSerializer.Serialize(new { actionKind = "DesktopTaskStep", turnId = plan.TurnId,
                step, action = proposal.Action.ToString(), binding, taskTarget = plan.CanonicalTarget })
        };
        var registration = await store.RegisterCommandAsync(command, token);
        if (!registration.Accepted) throw Failure("duplicate_step", "这一步已经处理过，不会重复执行。");
        var scope = new DesktopTaskStepAuthorization(plan.TurnId, snapshot.Id,
            DesktopTaskStepPolicy.Hash(JsonSerializer.Serialize(window)), binding);
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
            var receipt = await automation.ExecuteAsync(window, control, proposal, token, StateFingerprint(snapshot));
            token.ThrowIfCancellationRequested();
            if (proposal.Action == DesktopTaskActionKind.SetText && !receipt.Verified)
                throw Failure("write_unverified", "输入后的实际文字未通过核对，已停止。");
            await store.CompleteCommandAsync(command.Id, CommandStatus.Processed, timeProvider.GetUtcNow(), null, CancellationToken.None);
            return receipt;
        }
        catch
        {
            await store.CompleteCommandAsync(command.Id, CommandStatus.Failed, timeProvider.GetUtcNow(),
                token.IsCancellationRequested ? "cancelled" : "desktop_task_step_failed", CancellationToken.None);
            throw;
        }
    }

    private static string StateFingerprint(DesktopTaskSnapshot snapshot) => DesktopTaskContextFingerprint.Compute(snapshot);
    private static string ActionName(DesktopTaskActionKind action) => action switch
    {
        DesktopTaskActionKind.Invoke => "点击", DesktopTaskActionKind.SetText => "填写",
        DesktopTaskActionKind.Select => "选择", DesktopTaskActionKind.Expand => "展开",
        DesktopTaskActionKind.Collapse => "收起", DesktopTaskActionKind.ScrollUp => "向上滚动",
        DesktopTaskActionKind.ScrollDown => "向下滚动", _ => "操作"
    };
    private static DesktopTaskException Failure(string code, string message) => new("desktop_task_" + code, message);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}

public interface IDesktopTaskWindowActivator
{
    Task ResumeAsync(DesktopTaskWindow window, CancellationToken cancellationToken);
}

internal sealed class DesktopTaskWindowActivator : IDesktopTaskWindowActivator
{
    public Task ResumeAsync(DesktopTaskWindow window, CancellationToken cancellationToken)
    {
        if (!VisibleWindowActivation.TryActivate(new IntPtr(window.FrameIdentity?.Handle ?? window.Handle), TimeSpan.FromSeconds(2), cancellationToken))
            throw new DesktopTaskException("desktop_task_focus_changed", "无法恢复已确认的目标窗口，任务已停止。");
        return Task.CompletedTask;
    }
}
