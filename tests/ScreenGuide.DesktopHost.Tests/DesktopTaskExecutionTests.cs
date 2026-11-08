using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopTaskExecutionTests
{
    private const string Instruction = "在当前窗口输入你好，然后选择选项并点击预览";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextOverwriteShowsExactOldAndNewValuesButStillBindsUnshownContext(bool changeBackground)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new DesktopRig();
        rig.ReplaceEntry("旧正文\n完整第二行");
        rig.ReplaceBackground("无关背景_SENTINEL");
        using var host = environment.BuildHost(services => Register(services, rig, new()));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("精确覆盖确认");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, "Text", "overwrite-review", session.SessionId));
        var observation = await WaitForConsent(client, submitted.TurnId, "TaskObservation");
        await Respond(client, session.SessionId, observation, true);
        var overwrite = await WaitForConsent(client, submitted.TurnId, "TaskAction");
        Assert.Empty(rig.Executed);
        Assert.Contains("控件正文_SENTINEL", overwrite.ResultSummary);
        Assert.Contains("原内容：旧正文\n完整第二行", overwrite.ResultSummary);
        Assert.Contains("将填写：你好", overwrite.ResultSummary);
        Assert.DoesNotContain("无关背景_SENTINEL", overwrite.ResultSummary);
        Assert.DoesNotContain("当前输入和选择", overwrite.ResultSummary);
        if (changeBackground) rig.ReplaceBackground("确认期间背景变化");
        await Respond(client, session.SessionId, overwrite, true);
        if (changeBackground)
        {
            await WaitForPhase(client, submitted.TurnId, "Failed");
            Assert.Empty(rig.Executed);
        }
        else
        {
            var preview = await WaitForConsent(client, submitted.TurnId, "TaskAction");
            Assert.NotEqual(overwrite.WindowConsent!.ConsentId, preview.WindowConsent!.ConsentId);
            Assert.Contains("当前输入和选择", preview.ResultSummary);
            Assert.Contains("无关背景_SENTINEL", preview.ResultSummary);
            Assert.Contains("你好", preview.ResultSummary);
            Assert.Equal(new[] { DesktopTaskActionKind.SetText, DesktopTaskActionKind.Select }, rig.Executed);
            await Respond(client, session.SessionId, preview, false);
            await WaitForPhase(client, submitted.TurnId, "Cancelled");
        }
        await host.StopAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RejectedLaterPlanningReportsEarlierActionsWithoutRetryOrSuccess(int completedSteps)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new DesktopRig();
        var planner = new StepPlanner { RejectAfterSteps = completedSteps };
        using var host = environment.BuildHost(services => Register(services, rig, planner));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("规划失败时保留实际进度");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, "Text", "partial-planning-failure", session.SessionId));
        var consent = await WaitForConsent(client, submitted.TurnId, "TaskObservation");
        await Respond(client, session.SessionId, consent, true);
        var failed = await WaitForPhase(client, submitted.TurnId, "Failed");
        Assert.Equal(completedSteps, rig.Executed.Count);
        Assert.Equal(completedSteps + 1, planner.Calls.Count);
        var commands = await ReadStepCommands(environment, submitted.TurnId);
        Assert.Equal(completedSteps, commands.Count);
        Assert.All(commands, command => Assert.Equal("Processed", command.Status));
        if (completedSteps > 0)
            Assert.StartsWith("此前已执行 1 步，任务尚未完成。后续规划已停止：", failed.FailureMessage);
        else
            Assert.Equal("模型步骤未通过校验，没有执行本步。", failed.FailureMessage);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Voice")]
    [InlineData("Text")]
    [InlineData("TextFrame")]
    public async Task ThreeStepsUseOneTurnAndSeparateObservationAndActionConsents(string modality)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new DesktopRig(modality == "TextFrame");
        var planner = new StepPlanner();
        using var host = environment.BuildHost(services => Register(services, rig, planner));
        Assert.IsType<DesktopTaskExecutionService>(host.Services.GetRequiredService<IDesktopTaskExecutor>());
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("通用任务三步执行");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, modality == "TextFrame" ? "Text" : modality, "three-steps", session.SessionId));
        var observation = await WaitForConsent(client, submitted.TurnId, "TaskObservation");
        Assert.Equal("DesktopTask", observation.IntentKind);
        Assert.Equal(0, rig.Observations);
        Assert.Empty(planner.Calls);
        Assert.Empty(rig.Executed);
        Assert.Empty(await ReadStepCommands(environment, submitted.TurnId));
        await Assert.ThrowsAsync<DesktopApiException>(() => client.RespondSessionWindowConsentAsync(
            session.SessionId, submitted.TurnId, true));
        Assert.Equal(0, rig.Observations);

        await Respond(client, session.SessionId, observation, true);
        var action = await WaitForConsent(client, submitted.TurnId, "TaskAction");
        Assert.Equal(observation.Id, action.Id);
        Assert.Equal(observation.PlanTarget, action.PlanTarget);
        Assert.NotEqual(observation.WindowConsent!.ConsentId, action.WindowConsent!.ConsentId);
        Assert.Equal(new[] { DesktopTaskActionKind.SetText, DesktopTaskActionKind.Select }, rig.Executed);
        Assert.Equal(3, planner.Calls.Count);
        Assert.Contains("你好", action.ResultSummary);
        Assert.Contains("预览", action.ResultSummary);
        Assert.DoesNotContain("metadata=", action.ResultSummary);
        Assert.DoesNotContain("pid=", action.ResultSummary);
        await using (var connection = new SqliteConnection($"Data Source={environment.Options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT result_summary FROM session_turns WHERE id = $id";
            query.Parameters.AddWithValue("$id", submitted.TurnId.ToString("D"));
            var persisted = Assert.IsType<string>(await query.ExecuteScalarAsync());
            Assert.Equal("请核对并确认当前这一步。", persisted);
            Assert.DoesNotContain("你好", persisted);
        }
        foreach (var stale in new Guid?[] { null, observation.WindowConsent.ConsentId, Guid.NewGuid() })
        {
            await Assert.ThrowsAsync<DesktopApiException>(() => client.RespondSessionWindowConsentAsync(
                session.SessionId, submitted.TurnId, true, consentId: stale));
            await Assert.ThrowsAsync<DesktopApiException>(() => client.RespondSessionWindowConsentAsync(
                session.SessionId, submitted.TurnId, false, consentId: stale));
        }
        Assert.Equal(2, rig.Executed.Count);
        await Respond(client, session.SessionId, action, true);
        var completed = await WaitForPhase(client, submitted.TurnId, "Completed");
        Assert.Equal(submitted.TurnId, completed.Id);
        Assert.Equal(observation.PlanTarget, completed.PlanTarget);
        Assert.Null(completed.WindowConsent);
        Assert.Null(completed.ConversationTurnId);
        Assert.Single((await client.GetCurrentSessionAsync())!.Turns);
        Assert.Equal(new[] { DesktopTaskActionKind.SetText, DesktopTaskActionKind.Select, DesktopTaskActionKind.Invoke }, rig.Executed);
        Assert.Equal(3, rig.ReviewedContexts.Count);
        Assert.All(rig.ReviewedContexts, fingerprint => Assert.Equal(64, fingerprint.Length));
        Assert.Equal(4, planner.Calls.Count);
        Assert.All(planner.Calls, call =>
        {
            Assert.Equal(submitted.TurnId, call.TurnId);
            Assert.Equal(Instruction, call.Instruction);
            Assert.Equal(planner.Calls[0].Route, call.Route);
            Assert.All(call.History, item =>
            {
                using var prior = JsonDocument.Parse(item);
                Assert.Equal(new[] { "action", "identifier", "status", "step", "target" },
                    prior.RootElement.EnumerateObject().Select(p => p.Name).Order());
                Assert.Equal("executed", prior.RootElement.GetProperty("status").GetString());
            });
        });
        Assert.Equal(3, planner.Calls[^1].History.Count);
        var commands = await ReadStepCommands(environment, submitted.TurnId);
        Assert.Equal(3, commands.Count);
        Assert.All(commands, command => Assert.Equal("Processed", command.Status));
        Assert.Equal(new[] { "SetText", "Select", "Invoke" }, commands.OrderBy(command => command.Step).Select(command => command.Action));
        await Assert.ThrowsAsync<DesktopApiException>(() => Respond(client, session.SessionId, action, true));
        Assert.Equal(3, rig.Executed.Count);
        Assert.Equal(3, (await ReadStepCommands(environment, submitted.TurnId)).Count);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("TaskObservation", false)]
    [InlineData("TaskObservation", true)]
    [InlineData("TaskAction", false)]
    [InlineData("TaskAction", true)]
    public async Task RejectOrStopCannotRunAnyLaterStep(string boundary, bool stop)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new DesktopRig();
        var planner = new StepPlanner();
        using var host = environment.BuildHost(services => Register(services, rig, planner));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("任务许可拒绝或停止");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, "Voice", "cancel", session.SessionId));
        var waiting = await WaitForConsent(client, submitted.TurnId, "TaskObservation");
        if (boundary == "TaskAction")
        {
            await Respond(client, session.SessionId, waiting, true);
            waiting = await WaitForConsent(client, submitted.TurnId, "TaskAction");
        }
        var expectedActions = boundary == "TaskAction" ? 2 : 0;
        Assert.Equal(expectedActions, rig.Executed.Count);
        if (stop) await client.CancelSessionTurnAsync(session.SessionId, submitted.TurnId);
        else await Respond(client, session.SessionId, waiting, false);
        await WaitForPhase(client, submitted.TurnId, "Cancelled");
        await Assert.ThrowsAsync<DesktopApiException>(() => Respond(client, session.SessionId, waiting, true));
        Assert.Equal(expectedActions, rig.Executed.Count);
        Assert.DoesNotContain(DesktopTaskActionKind.Invoke, rig.Executed);
        Assert.Equal(expectedActions, (await ReadStepCommands(environment, submitted.TurnId)).Count);
        if (boundary == "TaskObservation")
        {
            Assert.Equal(0, rig.Observations);
            Assert.Empty(planner.Calls);
        }
        await host.StopAsync();
    }

    [Fact]
    public async Task ReplacementCancelsOriginalTaskAndCannotReuseItsConsent()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new DesktopRig();
        var planner = new StepPlanner();
        using var host = environment.BuildHost(services => Register(services, rig, planner));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("新指令取代旧任务");
        var first = await client.SubmitSessionInputAsync(new(Instruction, "Voice", "first", session.SessionId));
        await Respond(client, session.SessionId, await WaitForConsent(client, first.TurnId, "TaskObservation"), true);
        var oldAction = await WaitForConsent(client, first.TurnId, "TaskAction");
        var observations = rig.Observations;
        var next = await client.SubmitSessionInputAsync(new("在当前窗口输入下一段文字", "Voice", "next", session.SessionId));
        await WaitForPhase(client, first.TurnId, "Cancelled");
        var newObservation = await WaitForConsent(client, next.TurnId, "TaskObservation");
        Assert.NotEqual(oldAction.WindowConsent!.ConsentId, newObservation.WindowConsent!.ConsentId);
        await Assert.ThrowsAsync<DesktopApiException>(() => Respond(client, session.SessionId, oldAction, true));
        await Assert.ThrowsAsync<DesktopApiException>(() => client.RespondSessionWindowConsentAsync(
            session.SessionId, next.TurnId, true, consentId: oldAction.WindowConsent.ConsentId));
        Assert.Equal(2, rig.Executed.Count);
        Assert.Equal(observations, rig.Observations);
        Assert.Equal(3, planner.Calls.Count);
        Assert.Empty(await ReadStepCommands(environment, next.TurnId));
        await client.CancelSessionTurnAsync(session.SessionId, next.TurnId);
        await host.StopAsync();
    }

    [Fact]
    public async Task ContentChangedDuringStepConfirmationCannotExecuteReviewedAction()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new DesktopRig();
        using var host = environment.BuildHost(services => Register(services, rig, new()));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("确认期间正文变化");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, "Voice", "content-changed", session.SessionId));
        await Respond(client, session.SessionId, await WaitForConsent(client, submitted.TurnId, "TaskObservation"), true);
        var action = await WaitForConsent(client, submitted.TurnId, "TaskAction");
        rig.ReplaceEntry("用户修改了正文");
        await Respond(client, session.SessionId, action, true);
        await WaitForPhase(client, submitted.TurnId, "Failed");
        var persisted = await host.Services.GetRequiredService<ISessionStore>().GetTurnAsync(submitted.TurnId);
        Assert.Equal("desktop_task_review_context_changed", persisted!.FailureCode);
        Assert.Equal(2, rig.Executed.Count);
        Assert.DoesNotContain(DesktopTaskActionKind.Invoke, rig.Executed);
        Assert.Equal(2, (await ReadStepCommands(environment, submitted.TurnId)).Count);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("TaskObservation", "Process")]
    [InlineData("TaskAction", "Process")]
    [InlineData("TaskAction", "Creation")]
    [InlineData("TaskAction", "Path")]
    [InlineData("TaskAction", "Class")]
    public async Task ChangedWindowIdentityInvalidatesPendingPermission(string boundary, string change)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new DesktopRig();
        var planner = new StepPlanner();
        using var host = environment.BuildHost(services => Register(services, rig, planner));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("窗口身份变化");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, "Voice", "window-changed", session.SessionId));
        var waiting = await WaitForConsent(client, submitted.TurnId, "TaskObservation");
        if (boundary == "TaskAction")
        {
            await Respond(client, session.SessionId, waiting, true);
            waiting = await WaitForConsent(client, submitted.TurnId, "TaskAction");
        }
        rig.ChangeWindow(change);
        await Respond(client, session.SessionId, waiting, true);
        await WaitForPhase(client, submitted.TurnId, "Failed");
        Assert.Equal(boundary == "TaskAction" ? 2 : 0, rig.Executed.Count);
        Assert.DoesNotContain(DesktopTaskActionKind.Invoke, rig.Executed);
        if (boundary == "TaskObservation")
        {
            Assert.Empty(planner.Calls);
            Assert.Equal(0, rig.Observations);
        }
        await host.StopAsync();
    }

    [Fact]
    public async Task RestartInterruptsActionConsentAndNeverReplaysCompletedSteps()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var original = new DesktopRig();
        using var firstHost = environment.BuildHost(services => Register(services, original, new()));
        await firstHost.StartAsync();
        var firstClient = new DesktopApiClient(environment.Options.PipeName);
        var session = await firstClient.StartNewSessionAsync("任务步骤等待期间重启");
        var submitted = await firstClient.SubmitSessionInputAsync(new(Instruction, "Voice", "restart", session.SessionId));
        await Respond(firstClient, session.SessionId, await WaitForConsent(firstClient, submitted.TurnId, "TaskObservation"), true);
        var action = await WaitForConsent(firstClient, submitted.TurnId, "TaskAction");
        var oldCoordinator = firstHost.Services.GetRequiredService<SessionCoordinator>();
        firstHost.Dispose(); // Same abrupt-restart simulation as VisualSearchConsentHostTests.
        var next = new DesktopRig();
        var nextPlanner = new StepPlanner();
        using var nextHost = environment.BuildHost(services => Register(services, next, nextPlanner));
        await nextHost.StartAsync();
        var nextClient = new DesktopApiClient(environment.Options.PipeName);
        var restored = Assert.Single((await nextClient.SetCurrentSessionAsync(session.SessionId)).Turns);
        Assert.Equal(submitted.TurnId, restored.Id);
        Assert.Equal("Interrupted", restored.Phase);
        Assert.Null(restored.WindowConsent);
        await Assert.ThrowsAsync<DesktopApiException>(() => Respond(nextClient, session.SessionId, action, true));
        Assert.Equal(2, original.Executed.Count);
        Assert.DoesNotContain(DesktopTaskActionKind.Invoke, original.Executed);
        Assert.Empty(next.Executed);
        Assert.Equal(0, next.Observations);
        Assert.Empty(nextPlanner.Calls);
        Assert.Equal(2, (await ReadStepCommands(environment, submitted.TurnId)).Count);
        await oldCoordinator.StopAsync();
        Assert.Equal("Interrupted", (await WaitForPhase(nextClient, submitted.TurnId, "Interrupted")).Phase);
        await nextHost.StopAsync();
    }

    [Fact]
    public void CompletionNeedsActionFreshChangedEvidenceAndCannotUseTechnicalState()
    {
        var initial = PolicySnapshot("", "enabled=True;selected=False");
        var unchanged = initial with { Id = Guid.NewGuid() };
        Assert.False(DesktopTaskStepPolicy.HasCompletionEvidence("填写你好", initial, unchanged,
            Finish(unchanged, "enabled=True"), 1));
        var changed = PolicySnapshot("你好", "enabled=True;selected=True");
        Assert.False(DesktopTaskStepPolicy.HasCompletionEvidence("填写你好", initial, changed,
            Finish(changed, "你好"), 0));
        Assert.False(DesktopTaskStepPolicy.HasCompletionEvidence("填写你好", initial, initial,
            Finish(initial, "enabled=True"), 1));
        Assert.False(DesktopTaskStepPolicy.HasCompletionEvidence("选择选项", initial, changed,
            Finish(changed, "enabled=True"), 1));
        Assert.True(DesktopTaskStepPolicy.HasCompletionEvidence("填写你好", initial, changed,
            Finish(changed, "你好"), 1));
        Assert.True(DesktopTaskStepPolicy.HasCompletionEvidence("选择选项", initial, changed,
            Finish(changed, "selected=True"), 1));
    }

    [Theory]
    [InlineData("填写内容然后提交")]
    [InlineData("输入内容并保存")]
    [InlineData("搜索资料")]
    public void TextStillInAnInputCannotProveSubmissionOrSaving(string instruction)
    {
        var initial = PolicySnapshot("", "enabled=True");
        var latest = PolicySnapshot("你好", "enabled=True");
        Assert.False(DesktopTaskStepPolicy.HasCompletionEvidence(instruction, initial, latest,
            Finish(latest, "你好"), 1));
        var completedText = instruction.Contains("保存", StringComparison.Ordinal) ? "保存成功"
            : instruction.Contains("搜索", StringComparison.Ordinal) ? "已找到资料" : "提交成功";
        latest = latest with { Controls = [latest.Controls[0] with
        {
            Value = completedText, ControlType = "Text", Actions = []
        }] };
        Assert.True(DesktopTaskStepPolicy.HasCompletionEvidence(instruction, initial, latest,
            Finish(latest, completedText), 1));
    }

    private static DesktopTaskSnapshot PolicySnapshot(string value, string state) => new(Guid.NewGuid(), DesktopRig.InitialWindow,
        [new DesktopTaskControl("result", [7, 99], "结果字段", "result", "Edit", "表单", value, state, false,
            new(10, 10, 100, 30), [DesktopTaskActionKind.SetText])], false);
    private static DesktopTaskProposal Finish(DesktopTaskSnapshot snapshot, string evidence) => new(snapshot.Id,
        DesktopTaskActionKind.Finish, null, null, "已看到结果", snapshot.Controls[0].Id, evidence);

    private static void Register(IServiceCollection services, DesktopRig rig, StepPlanner planner)
    {
        services.AddSingleton<IDesktopTaskAutomation>(rig);
        services.AddSingleton<IForegroundWindowContextProvider>(rig);
        services.AddSingleton<IDesktopTaskWindowActivator>(rig);
        services.AddSingleton<IDesktopTaskStepPlanner>(planner);
    }

    private static Task<SessionSnapshotDto> Respond(DesktopApiClient client, Guid sessionId,
        UnifiedSessionTurnDto turn, bool granted) => client.RespondSessionWindowConsentAsync(
            sessionId, turn.Id, granted, consentId: turn.WindowConsent!.ConsentId);

    private static Task<UnifiedSessionTurnDto> WaitForConsent(DesktopApiClient client, Guid turnId, string purpose) =>
        WaitFor(client, turnId, turn => turn.Phase == "WaitingForWindowConsent" && turn.WindowConsent?.Purpose == purpose, purpose);
    private static Task<UnifiedSessionTurnDto> WaitForPhase(DesktopApiClient client, Guid turnId, string phase) =>
        WaitFor(client, turnId, turn => turn.Phase == phase, phase);
    private static async Task<UnifiedSessionTurnDto> WaitFor(DesktopApiClient client, Guid turnId,
        Func<UnifiedSessionTurnDto, bool> predicate, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var turn = (await client.GetCurrentSessionAsync())?.Turns.SingleOrDefault(item => item.Id == turnId);
            if (turn is not null && predicate(turn)) return turn;
            if (turn?.Phase is "Completed" or "Failed" or "Cancelled" or "Interrupted")
                throw new InvalidOperationException($"Expected {expected}; got {turn.Phase}: {turn.ResultSummary} / {turn.FailureMessage}");
            await Task.Delay(20);
        }
        throw new TimeoutException($"Turn did not reach {expected}.");
    }

    private sealed record StepCommand(int Step, string Action, string Status);
    private static async Task<IReadOnlyList<StepCommand>> ReadStepCommands(DesktopHostTestEnvironment environment, Guid turnId)
    {
        await using var connection = new SqliteConnection($"Data Source={environment.Options.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json, status FROM commands WHERE idempotency_key LIKE $prefix";
        command.Parameters.AddWithValue("$prefix", $"session-action-{turnId:N}-step-%");
        var result = new List<StepCommand>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            using var payload = JsonDocument.Parse(reader.GetString(0));
            Assert.Equal("DesktopTaskStep", payload.RootElement.GetProperty("actionKind").GetString());
            Assert.Equal(turnId, payload.RootElement.GetProperty("turnId").GetGuid());
            result.Add(new(payload.RootElement.GetProperty("step").GetInt32(),
                payload.RootElement.GetProperty("action").GetString()!, reader.GetString(1)));
        }
        return result;
    }

    private sealed record PlannerCall(Guid TurnId, FrozenChatModelRoute Route, string Instruction, IReadOnlyList<string> History);
    private sealed class StepPlanner : IDesktopTaskStepPlanner
    {
        public int? RejectAfterSteps { get; init; }
        private readonly object gate = new();
        private readonly List<PlannerCall> calls = [];
        public IReadOnlyList<PlannerCall> Calls { get { lock (gate) return calls.ToArray(); } }
        public Task<DesktopTaskProposal> PlanAsync(Guid turnId, FrozenChatModelRoute route, string instruction,
            DesktopTaskSnapshot snapshot, IReadOnlyList<string> history, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) calls.Add(new(turnId, route, instruction, history.ToArray()));
            if (RejectAfterSteps is { } limit && history.Count >= limit)
                throw new DesktopTaskException("desktop_task_proposal_invalid", "模型步骤未通过校验，没有执行本步。");
            var entry = snapshot.Controls.Single(control => control.AutomationId == "entry");
            var option = snapshot.Controls.Single(control => control.AutomationId == "option");
            var preview = snapshot.Controls.Single(control => control.AutomationId == "preview");
            var result = snapshot.Controls.Single(control => control.AutomationId == "result");
            var proposal = entry.Value != "你好"
                ? new DesktopTaskProposal(snapshot.Id, DesktopTaskActionKind.SetText, entry.Id, "你好", "填写指定文字")
                : !option.State.Contains("selected=True", StringComparison.Ordinal)
                    ? new(snapshot.Id, DesktopTaskActionKind.Select, option.Id, null, "选择指定选项")
                    : result.Value.Length == 0
                        ? new(snapshot.Id, DesktopTaskActionKind.Invoke, preview.Id, null, "打开预览核对内容")
                        : new(snapshot.Id, DesktopTaskActionKind.Finish, null, null, "预览显示填写和选择结果", result.Id, result.Value);
            return Task.FromResult(proposal);
        }
    }

    private sealed class DesktopRig : IDesktopTaskAutomation, IForegroundWindowContextProvider, IDesktopTaskWindowActivator
    {
        internal static readonly DesktopTaskWindow InitialWindow = new(6412, 123, "fixture",
            new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero), @"C:\synthetic\fixture.exe", "SyntheticTaskWindow", "通用任务测试窗口");
        private readonly object gate = new();
        private DesktopTaskWindow window = InitialWindow;
        public DesktopRig(bool useFrame = false)
        {
            if (useFrame) window = window with { FrameIdentity = new(9412, 456,
                window.ProcessStartedAtUtc, Path.Combine(Environment.SystemDirectory, "ApplicationFrameHost.exe"), "ApplicationFrameWindow") };
        }
        private string entry = "";
        private string background = "";
        private bool selected;
        private bool previewed;
        private int observations;
        private readonly List<DesktopTaskActionKind> executed = [];
        private readonly Dictionary<Guid, string> observedContexts = [];
        private readonly List<string> reviewedContexts = [];
        public int Observations => Volatile.Read(ref observations);
        public IReadOnlyList<DesktopTaskActionKind> Executed { get { lock (gate) return executed.ToArray(); } }
        public IReadOnlyList<string> ReviewedContexts { get { lock (gate) return reviewedContexts.ToArray(); } }
        public ForegroundWindowSnapshot? GetLastExternalWindow()
        {
            if (window.FrameIdentity is { } frame)
                return new(frame.Handle, window.Title, "ApplicationFrameHost", frame.ProcessId, frame.ProcessStartedAtUtc, DateTimeOffset.UtcNow);
            lock (gate) return new(window.Handle, window.Title, window.ProcessName, window.ProcessId,
                window.ProcessStartedAtUtc, DateTimeOffset.UtcNow);
        }
        public ForegroundWindowSnapshot? ResolveWindow(long handle)
        {
            if (handle == window.Handle)
                return new(window.Handle, window.Title, window.ProcessName, window.ProcessId, window.ProcessStartedAtUtc, DateTimeOffset.UtcNow);
            var current = GetLastExternalWindow();
            return current?.WindowHandle == handle ? current : null;
        }
        public Task<DesktopTaskWindow> BindAsync(long handle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (handle != window.Handle && handle != window.FrameIdentity?.Handle) throw Changed();
                return Task.FromResult(window);
            }
        }
        public Task ResumeAsync(DesktopTaskWindow expected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) RequireWindow(expected);
            return Task.CompletedTask;
        }
        public Task<DesktopTaskSnapshot> ObserveAsync(DesktopTaskWindow expected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                RequireWindow(expected);
                Interlocked.Increment(ref observations);
                var snapshot = Snapshot();
                observedContexts.Add(snapshot.Id, DesktopTaskContextFingerprint.Compute(snapshot));
                return Task.FromResult(snapshot);
            }
        }
        public Task<DesktopTaskActionReceipt> ExecuteAsync(DesktopTaskWindow expected, DesktopTaskControl control,
            DesktopTaskProposal proposal, CancellationToken cancellationToken, string? reviewedContextFingerprint = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                RequireWindow(expected);
                Assert.NotNull(reviewedContextFingerprint);
                Assert.True(observedContexts.TryGetValue(proposal.SnapshotId, out var proposedContext));
                Assert.Equal(proposedContext, reviewedContextFingerprint);
                var fresh = Snapshot();
                if (DesktopTaskContextFingerprint.Compute(fresh) != reviewedContextFingerprint) throw Changed();
                reviewedContexts.Add(reviewedContextFingerprint);
                var current = fresh.Controls.Single(item => item.AutomationId == control.AutomationId);
                if (!current.RuntimeId.SequenceEqual(control.RuntimeId) || current.Name != control.Name
                    || current.Value != control.Value || current.State != control.State
                    || proposal.TargetId != control.Id || !current.Actions.Contains(proposal.Action)) throw Changed();
                switch (proposal.Action)
                {
                    case DesktopTaskActionKind.SetText: entry = proposal.Text!; break;
                    case DesktopTaskActionKind.Select: selected = true; break;
                    case DesktopTaskActionKind.Invoke: previewed = true; break;
                    default: throw new InvalidOperationException("Unexpected test action.");
                }
                executed.Add(proposal.Action);
                return Task.FromResult(new DesktopTaskActionReceipt(true, "测试界面回读一致"));
            }
        }
        public void ReplaceEntry(string value) { lock (gate) entry = value; }
        public void ReplaceBackground(string value) { lock (gate) background = value; }
        public void ChangeWindow(string change)
        {
            lock (gate) window = change switch
            {
                "Process" => window with { ProcessId = window.ProcessId + 1 },
                "Creation" => window with { ProcessStartedAtUtc = window.ProcessStartedAtUtc.AddSeconds(1) },
                "Path" => window with { ExecutablePath = @"C:\synthetic\other.exe" },
                "Class" => window with { ClassName = "OtherWindowClass" },
                _ => throw new ArgumentException("Unknown identity change.")
            };
        }
        private DesktopTaskSnapshot Snapshot()
        {
            var id = Guid.NewGuid();
            DesktopTaskControl Control(int index, string automationId, string name, string type, string value,
                string state, params DesktopTaskActionKind[] actions) => new($"{id:N}:{index}", [7, index], name,
                    automationId, type, "任务表单", value, "enabled=True;offscreen=False;" + state, false,
                    new(10, index * 30, 100, 25), actions);
            List<DesktopTaskControl> controls =
            [
                Control(1, "entry", "控件正文_SENTINEL", "Edit", entry, "", DesktopTaskActionKind.SetText),
                Control(2, "option", "选项", "RadioButton", "", "selected=" + selected, DesktopTaskActionKind.Select),
                Control(3, "preview", "预览", "Button", "", "", DesktopTaskActionKind.Invoke),
                Control(4, "result", "预览结果", "Text", previewed ? $"预览：{entry}，选项已选中" : "", "")
            ];
            if (background.Length > 0)
                controls.Add(Control(5, "background", "其它单元格", "Edit", background, "", DesktopTaskActionKind.SetText));
            return new(id, window, controls, false);
        }
        private void RequireWindow(DesktopTaskWindow expected)
        {
            if (!DesktopTaskExecutionService.SameWindow(window, expected)) throw Changed();
        }
        private static DesktopTaskException Changed() => new("desktop_task_window_changed", "测试窗口或控件已经变化。");
    }
}
