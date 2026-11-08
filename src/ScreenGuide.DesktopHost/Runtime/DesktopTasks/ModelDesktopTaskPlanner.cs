using System.Globalization;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>Proposes one step from the consented text snapshot, never an executable authority.</summary>
public sealed class ModelDesktopTaskPlanner(
    ModelRouter router,
    PromptRegistry prompts,
    IAiInvocationStore invocations,
    TimeProvider timeProvider) : IDesktopTaskStepPlanner
{
    private const string PromptId = "desktop.task-step";
    private const string PromptVersion = "2";
    private static readonly JsonSerializerOptions InputJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public async Task<DesktopTaskProposal> PlanAsync(Guid turnId, FrozenChatModelRoute route,
        string instruction, DesktopTaskSnapshot snapshot, IReadOnlyList<string> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(history);
        if (turnId == Guid.Empty) throw new ArgumentException("任务 Turn 标识不能为空。", nameof(turnId));
        cancellationToken.ThrowIfCancellationRequested();
        var input = BuildInput(instruction, snapshot, history);
        var prompt = prompts.GetRequired(PromptId, PromptVersion, route.ProviderId);
        var invocationId = Guid.NewGuid();
        await invocations.StartAsync(new AiInvocationRecord
        {
            Id = invocationId,
            SessionTurnId = turnId,
            Purpose = AiInvocationPurpose.DesktopTaskPlanning,
            ProviderId = route.ProviderId,
            ModelId = route.ModelId,
            PromptId = prompt.PromptId,
            PromptVersion = prompt.Version,
            PromptContentHash = prompt.ContentSha256,
            DataDestination = route.DataDestination,
            Status = AiInvocationStatus.Running,
            StartedAtUtc = timeProvider.GetUtcNow()
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var responseTask = router.CompleteAsync(route, new ChatModelRequest(
                invocationId, turnId, route.ModelId, prompt.Content,
                [new ChatMessage(ChatMessageRole.User, input.Json)],
                ResponseFormat: (route.Capabilities & ChatModelCapabilities.JsonObjectOutput) != 0
                    ? ChatResponseFormat.JsonObject : ChatResponseFormat.Text,
                Prompt: new ChatPromptReference(prompt.PromptId, prompt.Version, prompt.ContentSha256)),
                cancellationToken: cancellationToken);
            var cancellationTask = CancelProviderWhenRequestedAsync(turnId, responseTask, cancellationToken);
            ChatModelResponse response;
            try
            {
                response = await responseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await cancellationTask.ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (response.FinishReason == ChatFinishReason.Cancelled)
                throw new OperationCanceledException("模型已停止本次桌面步骤规划。", cancellationToken);
            if (response.FinishReason != ChatFinishReason.Stop)
                throw new DesktopTaskException("desktop_task_planning_incomplete", "模型未完整返回下一步，因此没有执行。");

            var projectedProposal = StrictDesktopTaskProposalParser.Parse(
                response.StructuredJson ?? response.Text, input.Snapshot);
            var proposal = projectedProposal with
            {
                TargetId = projectedProposal.TargetId is { } target ? input.NativeIds[target] : null,
                EvidenceTargetId = projectedProposal.EvidenceTargetId is { } evidence ? input.NativeIds[evidence] : null
            };
            cancellationToken.ThrowIfCancellationRequested();
            var terminal = await invocations.CompleteAsync(invocationId, response.FinishReason.ToString(),
                response.Usage is null ? null : new AiTokenUsage(response.Usage.InputTokens ?? 0,
                    response.Usage.OutputTokens ?? 0, response.Usage.TotalTokens ?? 0),
                response.Metadata.ProviderRequestId, timeProvider.GetUtcNow(), CancellationToken.None)
                .ConfigureAwait(false);
            if (!terminal.RequestedStatusWon)
                throw new DesktopTaskException("desktop_task_planning_superseded", "这次规划已经停止或失效，没有执行迟到的步骤。");
            cancellationToken.ThrowIfCancellationRequested();
            return proposal;
        }
        catch (OperationCanceledException)
        {
            await FailAsync(invocationId, AiInvocationStatus.Cancelled, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch (ChatModelException exception)
        {
            var cancelled = exception.Error.Kind == ChatModelErrorKind.Cancelled;
            await FailAsync(invocationId, cancelled ? AiInvocationStatus.Cancelled : AiInvocationStatus.Failed,
                SensitiveDataSanitizer.DiagnosticCode(exception.Error.Code, "desktop_task_planning_provider_error"))
                .ConfigureAwait(false);
            if (cancelled) throw new OperationCanceledException("模型已停止本次桌面步骤规划。", cancellationToken);
            throw new DesktopTaskException("desktop_task_planning_provider_error", "当前模型未能规划下一步，本次没有继续操作。");
        }
        catch (DesktopTaskException exception)
        {
            await FailAsync(invocationId, AiInvocationStatus.Failed, exception.Code).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await FailAsync(invocationId, AiInvocationStatus.Failed, "desktop_task_planning_failed").ConfigureAwait(false);
            throw new DesktopTaskException("desktop_task_planning_failed", "下一步规划没有完成，本次没有继续操作。");
        }
    }

    private async Task CancelProviderWhenRequestedAsync(Guid turnId, Task response, CancellationToken cancellationToken)
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => requested.TrySetResult());
        await Task.WhenAny(response, requested.Task).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await router.CancelAsync(turnId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // The caller still stops and records cancellation. A failed cancel must not authorize a late step.
            }
            _ = ObserveLateCompletionAsync(response);
        }
    }

    private static async Task ObserveLateCompletionAsync(Task response)
    {
        try { await response.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { }
    }

    private Task<AiInvocationTransitionResult> FailAsync(Guid id, AiInvocationStatus status, string code) =>
        invocations.FailAsync(id, status, code, timeProvider.GetUtcNow(), CancellationToken.None);

    private static ModelInput BuildInput(string instruction, DesktopTaskSnapshot snapshot, IReadOnlyList<string> history)
    {
        if (string.IsNullOrWhiteSpace(instruction) || instruction.Length > DesktopTaskLimits.MaximumInstructionLength
            || snapshot.Id == Guid.Empty || snapshot.Controls is null
            || snapshot.Controls.Count > DesktopTaskLimits.MaximumControls || history.Count > DesktopTaskLimits.MaximumSteps
            || history.Any(item => item is null || item.Length > 500))
            throw InputInvalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long textLength = 0;
        foreach (var control in snapshot.Controls)
        {
            if (control is null || string.IsNullOrWhiteSpace(control.Id) || control.Id.Length > 128
                || control.Id != control.Id.Trim() || control.Id.Any(char.IsControl) || !ids.Add(control.Id)
                || control.Actions is null || control.Actions.Count > 10
                || control.Actions.Any(action => !Enum.IsDefined(action)
                    || action is DesktopTaskActionKind.Wait or DesktopTaskActionKind.Ask or DesktopTaskActionKind.Finish)
                || control.Actions.Distinct().Count() != control.Actions.Count)
                throw InputInvalid();
            if (!DesktopTaskSemanticState.TryProject(control.State, out _)) throw InputInvalid();
            foreach (var field in new[] { control.Name, control.AutomationId, control.ControlType,
                         control.ParentName, control.Value, control.State })
            {
                if (field is null || field.Length > DesktopTaskLimits.MaximumValueLength) throw InputInvalid();
                textLength += field.Length;
            }
        }
        if (textLength > DesktopTaskLimits.MaximumControlText) throw InputInvalid();
        // Validate the original IDs and all text before projecting. A unique simple automation
        // ID avoids a second, unrelated ordinal for the model to copy. Neither kind grants authority.
        var uniqueAutomationIds = snapshot.Controls
            .Where(control => control.AutomationId.Length <= 120 && IsAsciiIdentifier(control.AutomationId))
            .GroupBy(control => control.AutomationId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var nativeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var projectedControls = snapshot.Controls.Select((control, index) =>
        {
            var alias = ControlAlias(control, index, uniqueAutomationIds);
            nativeIds.Add(alias, control.Id);
            return control with { Id = alias };
        }).ToArray();
        var projectedSnapshot = snapshot with { Controls = projectedControls };
        // Deliberately project fields: do not serialize the window, native runtime IDs, geometry or paths.
        var json = JsonSerializer.Serialize(new
        {
            instruction,
            snapshotId = snapshot.Id,
            truncated = snapshot.Truncated,
            controls = projectedSnapshot.Controls.Select(control => new
            {
                id = control.Id,
                name = control.Name,
                automationId = control.AutomationId,
                type = control.ControlType,
                parentName = control.ParentName,
                value = control.Value,
                state = ProjectState(control.State),
                actions = control.Actions.Select(action => action.ToString()).ToArray()
            }).ToArray(),
            history
        }, InputJsonOptions);
        if (json.Length > 80000) throw InputInvalid();
        return new ModelInput(json, projectedSnapshot, nativeIds);
    }

    private static string ControlAlias(DesktopTaskControl control, int index, ISet<string> uniqueAutomationIds)
    {
        if (uniqueAutomationIds.Contains(control.AutomationId)) return "a_" + control.AutomationId;
        var prefix = "c" + (index + 1).ToString("D3", CultureInfo.InvariantCulture) + "_";
        var hint = IsAsciiIdentifier(control.ControlType) ? control.ControlType : "control";
        return prefix + hint[..Math.Min(hint.Length, 128 - prefix.Length)];
    }

    private static bool IsAsciiIdentifier(string value) => value.Length > 0
        && value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            or >= '0' and <= '9' or '_');

    private sealed record ModelInput(string Json, DesktopTaskSnapshot Snapshot,
        IReadOnlyDictionary<string, string> NativeIds);

    private static string ProjectState(string state) => DesktopTaskSemanticState.TryProject(state, out var projected)
        ? projected : throw InputInvalid();

    private static DesktopTaskException InputInvalid() => new("desktop_task_planning_input_invalid",
        "本次任务或窗口文字超过规划范围，或控件快照不完整；请缩小任务后重试。");
}
