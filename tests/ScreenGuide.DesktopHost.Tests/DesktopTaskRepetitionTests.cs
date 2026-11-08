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

public sealed class DesktopTaskRepetitionTests
{
    private const string Instruction = "在当前窗口点击按钮并查看结果";

    [Theory]
    [InlineData("Voice")]
    [InlineData("Text")]
    public async Task FourthRepeatedInvokeStopsBeforeConsentOrExecutionDespiteChangingResults(string modality)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new RepetitionRig();
        var planner = new RepetitionPlanner();
        using var host = environment.BuildHost(services => Register(services, rig, planner));
        Assert.IsType<DesktopTaskExecutionService>(host.Services.GetRequiredService<IDesktopTaskExecutor>());
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("重复点击停止边界");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, modality, "repeat", session.SessionId));
        var observation = await WaitFor(client, submitted.TurnId,
            turn => turn.WindowConsent?.Purpose == "TaskObservation", "observation consent");
        Assert.Equal("DesktopTask", observation.IntentKind);
        Assert.Equal(0, rig.Observations);
        Assert.Empty(planner.Calls);
        Assert.Empty(rig.Executed);
        await Confirm(client, session.SessionId, observation);

        var nonces = new HashSet<Guid> { observation.WindowConsent!.ConsentId };
        var reviewed = new List<UnifiedSessionTurnDto>();
        for (var step = 0; step < 3; step++)
        {
            var action = await WaitFor(client, submitted.TurnId,
                turn => turn.Phase == "WaitingForWindowConsent" && turn.WindowConsent is { Purpose: "TaskAction" } consent
                    && !nonces.Contains(consent.ConsentId), $"action consent {step + 1}");
            Assert.Equal(submitted.TurnId, action.Id);
            Assert.Equal(step, rig.Executed.Count);
            Assert.Equal(step, (await ReadCommands(environment, submitted.TurnId)).Count);
            Assert.Contains("二", action.ResultSummary);
            Assert.True(nonces.Add(action.WindowConsent!.ConsentId));
            reviewed.Add(action);
            await Confirm(client, session.SessionId, action);
        }

        var failed = await WaitFor(client, submitted.TurnId, turn => turn.Phase == "Failed", "repetition failure");
        Assert.Null(failed.WindowConsent);
        Assert.Null(failed.ConversationTurnId);
        Assert.Equal("desktop_task_repeated_action",
            (await host.Services.GetRequiredService<ISessionStore>().GetTurnAsync(submitted.TurnId))!.FailureCode);
        Assert.Equal(new[] { 0, 0, 0 }, rig.Executed);
        Assert.Equal(3, rig.ResultValue);
        var commands = await ReadCommands(environment, submitted.TurnId);
        Assert.Equal(new[] { 0, 1, 2 }, commands.OrderBy(command => command.Step).Select(command => command.Step));
        Assert.All(commands, command => { Assert.Equal("Invoke", command.Action); Assert.Equal("Processed", command.Status); });
        var calls = planner.Calls;
        Assert.Equal(4, calls.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, calls.Select(call => call.ResultValue));
        Assert.Equal(new[] { 0, 1, 2, 3 }, calls.Select(call => call.History.Count));
        Assert.Equal(4, calls.Select(call => call.SnapshotId).Distinct().Count());
        Assert.All(calls, call =>
        {
            Assert.Equal(submitted.TurnId, call.TurnId);
            Assert.Equal(Instruction, call.Instruction);
            Assert.Equal(calls[0].Route, call.Route);
            AssertHistory(call.History, Enumerable.Repeat(0, call.History.Count).ToArray());
        });
        foreach (var oldConsent in reviewed)
            await Assert.ThrowsAsync<DesktopApiException>(() => Confirm(client, session.SessionId, oldConsent));
        Assert.Equal(3, rig.Executed.Count);
        Assert.Equal(3, (await ReadCommands(environment, submitted.TurnId)).Count);
        Assert.Single((await client.GetCurrentSessionAsync())!.Turns);
        await host.StopAsync();
    }

    [Fact]
    public async Task FourDifferentInvokeTargetsCompleteWithoutBeingMistakenForRepetition()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var rig = new RepetitionRig();
        var planner = new RepetitionPlanner([0, 1, 2, 3]);
        using var host = environment.BuildHost(services => Register(services, rig, planner));
        Assert.IsType<DesktopTaskExecutionService>(host.Services.GetRequiredService<IDesktopTaskExecutor>());
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("不同目标连续操作");
        var submitted = await client.SubmitSessionInputAsync(new(Instruction, "Text", "different", session.SessionId));
        var observation = await WaitFor(client, submitted.TurnId,
            turn => turn.WindowConsent?.Purpose == "TaskObservation", "observation consent");
        await Confirm(client, session.SessionId, observation);
        var nonces = new HashSet<Guid> { observation.WindowConsent!.ConsentId };
        for (var step = 0; step < 4; step++)
        {
            var action = await WaitFor(client, submitted.TurnId,
                turn => turn.Phase == "WaitingForWindowConsent" && turn.WindowConsent is { Purpose: "TaskAction" } consent
                    && !nonces.Contains(consent.ConsentId), $"different target {step + 1}");
            Assert.Equal(step, rig.Executed.Count);
            Assert.True(nonces.Add(action.WindowConsent!.ConsentId));
            await Confirm(client, session.SessionId, action);
        }
        var completed = await WaitFor(client, submitted.TurnId, turn => turn.Phase == "Completed", "completion");
        Assert.Null(completed.WindowConsent);
        Assert.Equal(new[] { 0, 1, 2, 3 }, rig.Executed);
        Assert.Equal(4, rig.ResultValue);
        Assert.Equal(5, planner.Calls.Count);
        AssertHistory(planner.Calls[^1].History, [0, 1, 2, 3]);
        var commands = await ReadCommands(environment, submitted.TurnId);
        Assert.Equal(4, commands.Count);
        Assert.All(commands, command => Assert.Equal("Processed", command.Status));
        Assert.Single((await client.GetCurrentSessionAsync())!.Turns);
        await host.StopAsync();
    }

    private static void AssertHistory(IReadOnlyList<string> history, IReadOnlyList<int> expectedTargets)
    {
        Assert.Equal(expectedTargets.Count, history.Count);
        for (var index = 0; index < history.Count; index++)
        {
            var item = history[index];
            using var json = JsonDocument.Parse(item);
            Assert.Equal(new[] { "action", "identifier", "status", "step", "target" },
                json.RootElement.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal(index + 1, json.RootElement.GetProperty("step").GetInt32());
            Assert.Equal("Invoke", json.RootElement.GetProperty("action").GetString());
            Assert.Equal(ButtonName(expectedTargets[index]), json.RootElement.GetProperty("target").GetString());
            Assert.Equal(ButtonId(expectedTargets[index]), json.RootElement.GetProperty("identifier").GetString());
            Assert.Equal("executed", json.RootElement.GetProperty("status").GetString());
            Assert.DoesNotContain(RepetitionRig.Window.ProcessId.ToString(), item);
            Assert.DoesNotContain(RepetitionRig.Window.Handle.ToString(), item);
            Assert.DoesNotContain("812345", item); // Native runtime-ID sentinel.
            Assert.DoesNotContain(RepetitionRig.Window.ExecutablePath, item);
            Assert.DoesNotContain("fixture-private-path.exe", item);
            Assert.DoesNotContain("pid=", item);
            Assert.DoesNotContain("hwnd=", item);
            Assert.DoesNotContain("metadata=", item);
            Assert.DoesNotContain("结果值", item); // Observed result text is not history.
        }
    }

    private static void Register(IServiceCollection services, RepetitionRig rig, RepetitionPlanner planner)
    {
        services.AddSingleton<IDesktopTaskAutomation>(rig);
        services.AddSingleton<IForegroundWindowContextProvider>(rig);
        services.AddSingleton<IDesktopTaskWindowActivator>(rig);
        services.AddSingleton<IDesktopTaskStepPlanner>(planner);
    }
    private static Task<SessionSnapshotDto> Confirm(DesktopApiClient client, Guid sessionId, UnifiedSessionTurnDto turn) =>
        client.RespondSessionWindowConsentAsync(sessionId, turn.Id, true, consentId: turn.WindowConsent!.ConsentId);
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
    private static async Task<IReadOnlyList<StepCommand>> ReadCommands(DesktopHostTestEnvironment environment, Guid turnId)
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

    private static string ButtonId(int index) => index == 0 ? "num2Button" : $"nextButton{index}";
    private static string ButtonName(int index) => index == 0 ? "二" : $"按钮{index}";
    private sealed record PlannerCall(Guid TurnId, FrozenChatModelRoute Route, string Instruction,
        Guid SnapshotId, int ResultValue, IReadOnlyList<string> History);
    private sealed class RepetitionPlanner(int[]? targets = null) : IDesktopTaskStepPlanner
    {
        private readonly object gate = new();
        private readonly List<PlannerCall> calls = [];
        public IReadOnlyList<PlannerCall> Calls { get { lock (gate) return calls.ToArray(); } }
        public Task<DesktopTaskProposal> PlanAsync(Guid turnId, FrozenChatModelRoute route, string instruction,
            DesktopTaskSnapshot snapshot, IReadOnlyList<string> history, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = snapshot.Controls.Single(control => control.AutomationId == "result");
            var resultValue = int.Parse(result.Value[3..]);
            lock (gate) calls.Add(new(turnId, route, instruction, snapshot.Id, resultValue, history.ToArray()));
            if (targets is not null && resultValue >= targets.Length)
                return Task.FromResult(new DesktopTaskProposal(snapshot.Id, DesktopTaskActionKind.Finish, null, null,
                    "目标步骤已执行并看到结果", result.Id, result.Value));
            var target = snapshot.Controls.Single(control => control.AutomationId == ButtonId(targets is null ? 0 : targets[resultValue]));
            return Task.FromResult(new DesktopTaskProposal(snapshot.Id, DesktopTaskActionKind.Invoke, target.Id, null, "点击目标按钮"));
        }
    }

    private sealed class RepetitionRig : IDesktopTaskAutomation, IForegroundWindowContextProvider, IDesktopTaskWindowActivator
    {
        internal static readonly DesktopTaskWindow Window = new(654321, 718263, "fixture",
            new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero), @"C:\Synthetic-Repetition\fixture-private-path.exe", "SyntheticTaskWindow", "重复操作测试窗口");
        private readonly object gate = new();
        private int resultValue;
        private int observations;
        private readonly List<int> executed = [];
        private readonly Dictionary<Guid, string> contexts = [];
        public int Observations => Volatile.Read(ref observations);
        public int ResultValue { get { lock (gate) return resultValue; } }
        public IReadOnlyList<int> Executed { get { lock (gate) return executed.ToArray(); } }
        public ForegroundWindowSnapshot? GetLastExternalWindow() => new(Window.Handle, Window.Title,
            Window.ProcessName, Window.ProcessId, Window.ProcessStartedAtUtc, DateTimeOffset.UtcNow);
        public ForegroundWindowSnapshot? ResolveWindow(long handle) => handle == Window.Handle ? GetLastExternalWindow() : null;
        public Task<DesktopTaskWindow> BindAsync(long handle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Window.Handle, handle);
            return Task.FromResult(Window);
        }
        public Task ResumeAsync(DesktopTaskWindow expected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Window, expected);
            return Task.CompletedTask;
        }
        public Task<DesktopTaskSnapshot> ObserveAsync(DesktopTaskWindow expected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Window, expected);
            lock (gate)
            {
                Interlocked.Increment(ref observations);
                var snapshot = Snapshot();
                contexts.Add(snapshot.Id, DesktopTaskContextFingerprint.Compute(snapshot));
                return Task.FromResult(snapshot);
            }
        }
        public Task<DesktopTaskActionReceipt> ExecuteAsync(DesktopTaskWindow expected, DesktopTaskControl control,
            DesktopTaskProposal proposal, CancellationToken cancellationToken, string? reviewedContextFingerprint = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Window, expected);
            lock (gate)
            {
                Assert.NotNull(reviewedContextFingerprint);
                Assert.True(contexts.TryGetValue(proposal.SnapshotId, out var proposed));
                Assert.Equal(proposed, reviewedContextFingerprint);
                var fresh = Snapshot();
                Assert.Equal(DesktopTaskContextFingerprint.Compute(fresh), reviewedContextFingerprint);
                var current = fresh.Controls.Single(item => item.AutomationId == control.AutomationId);
                Assert.Equal(control.RuntimeId, current.RuntimeId);
                Assert.Equal(control.Name, current.Name);
                Assert.Equal(control.State, current.State);
                Assert.Equal(control.Id, proposal.TargetId);
                Assert.Equal(DesktopTaskActionKind.Invoke, proposal.Action);
                Assert.Contains(proposal.Action, current.Actions);
                executed.Add(control.RuntimeId[1]);
                resultValue++; // Prevents the unrelated no-progress guard from ending the scenario.
                // Invoke really happened, while only the following observation
                // can establish its result. History must not invite a retry.
                return Task.FromResult(new DesktopTaskActionReceipt(false, "已调用；下一次观察核对效果"));
            }
        }
        private DesktopTaskSnapshot Snapshot()
        {
            var id = Guid.NewGuid();
            var state = $"enabled=True;offscreen=False;pid={Window.ProcessId};hwnd={Window.Handle};metadata=synthetic";
            var controls = Enumerable.Range(0, 4).Select(index => new DesktopTaskControl($"{id:N}:{index}",
                [812345, index], ButtonName(index), ButtonId(index), "Button", "合成按钮区", "", state, true,
                new(10, index * 30, 80, 25), [DesktopTaskActionKind.Invoke])).ToList();
            controls.Add(new($"{id:N}:4", [812345, 99], "结果", "result", "Text", "合成按钮区",
                "结果值" + resultValue, state, true, new(100, 10, 100, 25), []));
            return new(id, Window, controls, false);
        }
    }
}
