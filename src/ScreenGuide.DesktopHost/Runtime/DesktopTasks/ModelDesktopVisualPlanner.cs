using System.Globalization;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>Proposes one step from the consented window image, never an executable authority.</summary>
public sealed class ModelDesktopVisualPlanner(
    ModelRouter router,
    PromptRegistry prompts,
    IAiInvocationStore invocations,
    TimeProvider timeProvider) : IDesktopVisualStepPlanner
{
    private const string PromptId = "desktop.visual-step";
    private const string PromptVersion = "2";
    private static readonly JsonSerializerOptions InputJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public async Task<DesktopVisualProposal> PlanAsync(Guid turnId, FrozenChatModelRoute route,
        string instruction, DesktopVisualObservation observation, IReadOnlyList<string> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(history);
        if (turnId == Guid.Empty) throw new ArgumentException("任务 Turn 标识不能为空。", nameof(turnId));
        cancellationToken.ThrowIfCancellationRequested();
        var input = BuildInput(instruction, observation, history);
        using var image = CreateImage(observation);
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
                [new ChatMessage(ChatMessageRole.User, input, Images: [image])],
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
                throw new DesktopTaskException("desktop_visual_planning_incomplete", "模型未完整返回下一步，因此没有执行。");

            var proposal = StrictDesktopVisualProposalParser.Parse(response.StructuredJson ?? response.Text, observation.Snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            var terminal = await invocations.CompleteAsync(invocationId, response.FinishReason.ToString(),
                response.Usage is null ? null : new AiTokenUsage(response.Usage.InputTokens ?? 0,
                    response.Usage.OutputTokens ?? 0, response.Usage.TotalTokens ?? 0),
                response.Metadata.ProviderRequestId, timeProvider.GetUtcNow(), CancellationToken.None)
                .ConfigureAwait(false);
            if (!terminal.RequestedStatusWon)
                throw new DesktopTaskException("desktop_visual_planning_superseded", "这次规划已经停止或失效，没有执行迟到的步骤。");
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
                SensitiveDataSanitizer.DiagnosticCode(exception.Error.Code, "desktop_visual_planning_provider_error"))
                .ConfigureAwait(false);
            if (cancelled) throw new OperationCanceledException("模型已停止本次桌面步骤规划。", cancellationToken);
            throw new DesktopTaskException("desktop_visual_planning_provider_error", "当前模型未能规划下一步，本次没有继续操作。");
        }
        catch (DesktopTaskException exception)
        {
            await FailAsync(invocationId, AiInvocationStatus.Failed, exception.Code).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await FailAsync(invocationId, AiInvocationStatus.Failed, "desktop_visual_planning_failed").ConfigureAwait(false);
            throw new DesktopTaskException("desktop_visual_planning_failed", "下一步规划没有完成，本次没有继续操作。");
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

    private static string BuildInput(string instruction, DesktopVisualObservation observation, IReadOnlyList<string> history)
    {
        var snapshot = observation.Snapshot;
        if (string.IsNullOrWhiteSpace(instruction) || instruction.Length > DesktopTaskLimits.MaximumInstructionLength
            || snapshot.Id == Guid.Empty || snapshot.ImageWidth is < 1 or > 2048 || snapshot.ImageHeight is < 1 or > 2048
            || snapshot.OcrText.Length > 16000 || observation.PngBytes.Length is < 1 or > 4194304
            || history.Count > 12 || history.Any(s => s is null || s.Length > 600)
            || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(observation.PngBytes.Span)) != snapshot.ImageSha256)
            throw new DesktopTaskException("desktop_visual_input_invalid", "窗口图片或上下文不符合本次识图要求，未发送。");
        return JsonSerializer.Serialize(new
        {
            instruction,
            snapshotId = snapshot.Id,
            coordinateSpace = "normalized1000",
            imageWidth = snapshot.ImageWidth,
            imageHeight = snapshot.ImageHeight,
            ocrText = snapshot.OcrText,
            executedSteps = history
        }, InputJsonOptions);
    }

    private static ChatImage CreateImage(DesktopVisualObservation observation)
    {
        ChatImage? image = null;
        try
        {
            image = new ChatImage(observation.PngBytes.Span);
            if (image.Width != observation.Snapshot.ImageWidth || image.Height != observation.Snapshot.ImageHeight)
                throw new ArgumentException("Image dimensions do not match the coordinate snapshot.");
            return image;
        }
        catch (Exception exception) when (exception is ArgumentException or ChatModelException or ObjectDisposedException)
        {
            image?.Dispose();
            throw new DesktopTaskException("desktop_visual_input_invalid", "窗口图片或坐标尺寸不符合本次识图要求，未发送。");
        }
    }
}

public interface IDesktopVisualStepPlanner
{
    Task<DesktopVisualProposal> PlanAsync(Guid turnId, FrozenChatModelRoute route, string instruction,
        DesktopVisualObservation observation, IReadOnlyList<string> history, CancellationToken cancellationToken = default);
}
