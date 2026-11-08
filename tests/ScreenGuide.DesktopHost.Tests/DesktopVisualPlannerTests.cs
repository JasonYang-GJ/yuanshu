using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopVisualPlannerTests
{
    // A synthetic 1x1 grayscale/alpha PNG with verified CRCs and zlib payload.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP4/x8AAwAB//wl3FEAAAAASUVORK5CYII=";
    private const string PrivateGoal = "USER_VISUAL_GOAL_SENTINEL";
    private const string PrivateOcr = "OCR_PRIVATE_SENTINEL 绿色方案";

    [Theory]
    [InlineData("qwen", ChatModelCapabilities.Vision | ChatModelCapabilities.JsonObjectOutput, ChatResponseFormatKind.JsonObject)]
    [InlineData("other", ChatModelCapabilities.Vision, ChatResponseFormatKind.Text)]
    public async Task SendsActualImageWithFrozenRouteAndHashedPromptWithoutPersistingWindowContents(
        string providerId, ChatModelCapabilities capabilities, ChatResponseFormatKind format)
    {
        using var observation = Observation();
        var provider = new FakeProvider(providerId, capabilities, (request, _) =>
        {
            var image = Assert.Single(Assert.Single(request.Messages).Images!);
            Assert.False(image.IsDisposed);
            Assert.Equal(1, image.Width);
            Assert.Equal(1, image.Height);
            Assert.Equal(Convert.FromBase64String(Png), image.CopyPngBytes());
            return Task.FromResult(Response(providerId, Proposal(request)));
        });
        var fallback = new FakeProvider("unused", capabilities, (_, _) => throw new InvalidOperationException("No fallback is authorized."));
        var audit = new InvocationStore();
        var planner = await Planner(audit, provider, fallback);
        var turn = Guid.NewGuid();
        var route = Route(provider);
        var result = await planner.PlanAsync(turn, route, PrivateGoal, observation, ["executed: 点击合成目标"]);

        Assert.Equal(observation.Snapshot.Id, result.SnapshotId);
        Assert.Equal(DesktopVisualActionKind.Wait, result.Action);
        var request = Assert.Single(provider.Requests);
        Assert.Empty(fallback.Requests);
        Assert.Equal(turn, request.TurnId);
        Assert.Equal(route.ModelId, request.ModelId);
        Assert.Null(request.Options);
        Assert.Null(request.Metadata);
        Assert.Equal(format, request.ResponseFormat?.Kind);
        Assert.Equal("desktop.visual-step", request.Prompt?.PromptId);
        Assert.Equal("2", request.Prompt?.Version);
        var prompt = Assert.IsType<ChatPromptReference>(request.Prompt);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.SystemPrompt))), request.Prompt?.ContentSha256);
        Assert.Contains("不可信", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("不能", request.SystemPrompt, StringComparison.Ordinal);
        var message = Assert.Single(request.Messages);
        Assert.Equal(ChatMessageRole.User, message.Role);
        Assert.True(Assert.Single(message.Images!).IsDisposed);
        Assert.Equal(Convert.FromBase64String(Png), observation.PngBytes.ToArray());

        using var input = JsonDocument.Parse(message.Content);
        Assert.Equal(new[] { "instruction", "snapshotId", "imageWidth", "imageHeight", "coordinateSpace", "ocrText", "executedSteps" }.Order(StringComparer.Ordinal),
            input.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("normalized1000", input.RootElement.GetProperty("coordinateSpace").GetString());
        Assert.Equal(PrivateGoal, input.RootElement.GetProperty("instruction").GetString());
        Assert.Equal(PrivateOcr, input.RootElement.GetProperty("ocrText").GetString());
        Assert.Equal(observation.Snapshot.Id, input.RootElement.GetProperty("snapshotId").GetGuid());
        Assert.Equal(1, input.RootElement.GetProperty("imageWidth").GetInt32());
        Assert.Equal(1, input.RootElement.GetProperty("imageHeight").GetInt32());
        Assert.Equal("executed: 点击合成目标", input.RootElement.GetProperty("executedSteps")[0].GetString());
        Assert.DoesNotContain(Png, message.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_NATIVE", message.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("87654321", message.Content, StringComparison.Ordinal);

        var record = Assert.Single(audit.Records.Values);
        Assert.Equal(request.RequestId, record.Id);
        Assert.Equal(turn, record.SessionTurnId);
        Assert.Null(record.ConversationTurnId);
        Assert.Equal(AiInvocationPurpose.DesktopTaskPlanning, record.Purpose);
        Assert.Equal(AiInvocationStatus.Succeeded, record.Status);
        Assert.Equal(route.ProviderId, record.ProviderId);
        Assert.Equal(route.ModelId, record.ModelId);
        Assert.Equal(route.DataDestination, record.DataDestination);
        Assert.Equal(prompt.PromptId, record.PromptId);
        Assert.Equal(prompt.Version, record.PromptVersion);
        Assert.Equal(prompt.ContentSha256, record.PromptContentHash);
        Assert.Equal(new AiTokenUsage(12, 8, 20), record.Usage);
        Assert.Equal("synthetic-request-id", record.ProviderRequestId);
        Assert.Equal(record.Id, Assert.Single(audit.Completed));
        var auditJson = JsonSerializer.Serialize(record);
        foreach (var secret in new[] { PrivateGoal, PrivateOcr, Png, "PRIVATE_NATIVE", "executed:" })
            Assert.DoesNotContain(secret, auditJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("changed-bytes")]
    [InlineData("invalid-png")]
    [InlineData("oversized-png")]
    [InlineData("snapshot-dimension")]
    [InlineData("snapshot-id")]
    public async Task InvalidImageOrSnapshotFailsBeforeAnyProviderOrAudit(string variant)
    {
        var bytes = Convert.FromBase64String(Png);
        var snapshot = Snapshot(bytes);
        switch (variant)
        {
            case "hash": snapshot = snapshot with { ImageSha256 = new string('0', 64) }; break;
            case "changed-bytes": bytes[0] = 0; break;
            case "invalid-png": bytes[0] = 0; snapshot = Snapshot(bytes); break;
            case "oversized-png": bytes = new byte[ChatImage.MaximumEncodedBytes + 1]; snapshot = Snapshot(bytes); break;
            case "snapshot-dimension": snapshot = snapshot with { ImageWidth = 0 }; break;
            case "snapshot-id": snapshot = snapshot with { Id = Guid.Empty }; break;
        }
        using var observation = new DesktopVisualObservation(snapshot, bytes);
        var provider = NeverCalledProvider();
        var audit = new InvocationStore();
        var planner = await Planner(audit, provider);
        var error = await Record.ExceptionAsync(() => planner.PlanAsync(Guid.NewGuid(), Route(provider), PrivateGoal, observation, []));
        Assert.NotNull(error);
        Assert.DoesNotContain(Png, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(provider.Requests);
        Assert.Empty(audit.Records);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public async Task PngDimensionsMustMatchTheSnapshotCoordinateSpaceBeforeProviderUse(int width, int height)
    {
        var bytes = Convert.FromBase64String(Png);
        using var observation = new DesktopVisualObservation(Snapshot(bytes) with { ImageWidth = width, ImageHeight = height }, bytes);
        var provider = NeverCalledProvider();
        var audit = new InvocationStore();
        var planner = await Planner(audit, provider);
        var error = await Assert.ThrowsAsync<DesktopTaskException>(() =>
            planner.PlanAsync(Guid.NewGuid(), Route(provider), PrivateGoal, observation, []));
        Assert.Equal("desktop_visual_input_invalid", error.Code);
        Assert.Empty(provider.Requests);
        Assert.Empty(audit.Records);
    }

    [Theory]
    [InlineData("instruction")]
    [InlineData("history-count")]
    [InlineData("history-length")]
    [InlineData("ocr")]
    public async Task InputBudgetsFailBeforeProviderOrAudit(string variant)
    {
        var instruction = variant == "instruction" ? new string('x', DesktopTaskLimits.MaximumInstructionLength + 1) : PrivateGoal;
        IReadOnlyList<string> history = variant switch
        {
            "history-count" => Enumerable.Repeat("executed: 合成步骤", 13).ToArray(),
            "history-length" => [new string('x', 601)],
            _ => []
        };
        var bytes = Convert.FromBase64String(Png);
        using var observation = new DesktopVisualObservation(Snapshot(bytes) with
        { OcrText = variant == "ocr" ? new string('x', 16001) : PrivateOcr }, bytes);
        var provider = NeverCalledProvider();
        var audit = new InvocationStore();
        var planner = await Planner(audit, provider);
        var error = await Assert.ThrowsAsync<DesktopTaskException>(() =>
            planner.PlanAsync(Guid.NewGuid(), Route(provider), instruction, observation, history));
        Assert.Equal("desktop_visual_input_invalid", error.Code);
        Assert.Empty(provider.Requests);
        Assert.Empty(audit.Records);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotReadImagesOrCreateAnInvocation()
    {
        using var observation = Observation();
        observation.Dispose();
        var provider = NeverCalledProvider();
        var audit = new InvocationStore();
        var planner = await Planner(audit, provider);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planner.PlanAsync(Guid.NewGuid(), Route(provider),
            PrivateGoal, observation, [], new CancellationToken(true)));
        Assert.Empty(provider.Requests);
        Assert.Empty(audit.Records);
    }

    [Fact]
    public async Task CancellationCallsFrozenProviderAndDiscardsItsLateSuccessfulProposal()
    {
        using var observation = Observation();
        var response = new TaskCompletionSource<ChatModelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider("qwen", ChatModelCapabilities.Vision, (_, _) => response.Task);
        var fallback = NeverCalledProvider("other");
        var audit = new InvocationStore();
        var planner = await Planner(audit, provider, fallback);
        using var cancellation = new CancellationTokenSource();
        var turn = Guid.NewGuid();
        var running = planner.PlanAsync(turn, Route(provider), PrivateGoal, observation, [], cancellation.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(turn, Assert.Single(provider.CancelledTurns));
        Assert.Equal(AiInvocationStatus.Cancelled, Assert.Single(audit.Records.Values).Status);
        Assert.True(Assert.Single(Assert.Single(provider.Requests).Messages[0].Images!).IsDisposed);
        response.SetResult(Response("qwen", Proposal(Assert.Single(provider.Requests))));
        await provider.Returned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(running.IsCanceled);
        Assert.Empty(audit.Completed);
        Assert.Empty(fallback.Requests);
        Assert.Equal(AiInvocationStatus.Cancelled, Assert.Single(audit.Records.Values).Status);
        Assert.Equal(Convert.FromBase64String(Png), observation.PngBytes.ToArray());
    }

    [Theory]
    [InlineData(AiInvocationStatus.Cancelled)]
    [InlineData(AiInvocationStatus.Interrupted)]
    public async Task EarlierTerminalStatusRejectsLateProposalEvenWithoutCancellationToken(AiInvocationStatus status)
    {
        using var observation = Observation();
        var response = new TaskCompletionSource<ChatModelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider("qwen", ChatModelCapabilities.Vision, (_, _) => response.Task);
        var audit = new InvocationStore();
        var planner = await Planner(audit, provider);
        var running = planner.PlanAsync(Guid.NewGuid(), Route(provider), PrivateGoal, observation, []);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var original = Assert.Single(audit.Records.Values);
        await audit.FailAsync(original.Id, status, "earlier_terminal", DateTimeOffset.UtcNow);
        response.SetResult(Response("qwen", Proposal(Assert.Single(provider.Requests))));
        var error = await Assert.ThrowsAsync<DesktopTaskException>(() => running.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("desktop_visual_planning_superseded", error.Code);
        Assert.Equal(status, audit.Records[original.Id].Status);
        Assert.Equal("earlier_terminal", audit.Records[original.Id].FailureCode);
    }

    [Theory]
    [InlineData("raw-text")]
    [InlineData("unknown-authority")]
    [InlineData("stale-snapshot")]
    [InlineData("missing-coordinate-space")]
    [InlineData("pixel-coordinate-space")]
    public async Task InvalidOutputReturnsNoProposalAndNeverPersistsRawContent(string variant)
    {
        const string raw = "RAW_PRIVATE_VISUAL_OUTPUT";
        using var observation = Observation();
        var provider = new FakeProvider("qwen", ChatModelCapabilities.Vision, (request, _) =>
        {
            var json = JsonNode.Parse(Proposal(request))!.AsObject();
            json["reason"] = raw;
            if (variant == "unknown-authority") json["authorized"] = true;
            if (variant == "stale-snapshot") json["snapshotId"] = Guid.NewGuid();
            if (variant == "missing-coordinate-space") json.Remove("coordinateSpace");
            if (variant == "pixel-coordinate-space") json["coordinateSpace"] = "pixels";
            return Task.FromResult(Response("qwen", variant == "raw-text" ? raw : json.ToJsonString()));
        });
        var audit = new InvocationStore();
        var planner = await Planner(audit, provider);
        var error = await Assert.ThrowsAsync<DesktopTaskException>(() =>
            planner.PlanAsync(Guid.NewGuid(), Route(provider), PrivateGoal, observation, []));
        Assert.Equal("desktop_visual_proposal_invalid", error.Code);
        Assert.DoesNotContain(raw, error.ToString(), StringComparison.Ordinal);
        var record = Assert.Single(audit.Records.Values);
        Assert.Equal(AiInvocationStatus.Failed, record.Status);
        Assert.Equal(error.Code, record.FailureCode);
        Assert.DoesNotContain(raw, JsonSerializer.Serialize(record), StringComparison.Ordinal);
        Assert.Empty(audit.Completed);
    }

    [Theory]
    [InlineData(ChatFinishReason.Length)]
    [InlineData(ChatFinishReason.ToolCall)]
    [InlineData(ChatFinishReason.ContentFilter)]
    public async Task IncompleteImageResponseCannotProduceAProposal(ChatFinishReason finish)
    {
        using var observation = Observation();
        var provider = new FakeProvider("qwen", ChatModelCapabilities.Vision,
            (request, _) => Task.FromResult(Response("qwen", Proposal(request)) with { FinishReason = finish }));
        var audit = new InvocationStore();
        var planner = await Planner(audit, provider);
        var error = await Assert.ThrowsAsync<DesktopTaskException>(() =>
            planner.PlanAsync(Guid.NewGuid(), Route(provider), PrivateGoal, observation, []));
        Assert.Equal("desktop_visual_planning_incomplete", error.Code);
        Assert.Equal(AiInvocationStatus.Failed, Assert.Single(audit.Records.Values).Status);
        Assert.Empty(audit.Completed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderAndUnexpectedErrorsAreSanitizedWithoutRetryOrFallback(bool classified)
    {
        const string privateError = "RAW_PRIVATE_PROVIDER_MESSAGE";
        using var observation = Observation();
        var provider = new FakeProvider("qwen", ChatModelCapabilities.Vision, (_, _) =>
            classified ? throw new ChatModelException("qwen", "model-a",
                new ChatModelError(ChatModelErrorKind.RateLimited, "limited", privateError))
                : throw new InvalidOperationException(privateError));
        var fallback = NeverCalledProvider("other");
        var audit = new InvocationStore();
        var planner = await Planner(audit, provider, fallback);
        var error = await Assert.ThrowsAsync<DesktopTaskException>(() =>
            planner.PlanAsync(Guid.NewGuid(), Route(provider), PrivateGoal, observation, []));
        Assert.Single(provider.Requests);
        Assert.Empty(fallback.Requests);
        Assert.DoesNotContain(privateError, error.ToString(), StringComparison.Ordinal);
        var record = Assert.Single(audit.Records.Values);
        Assert.Equal(AiInvocationStatus.Failed, record.Status);
        Assert.Equal(classified ? "limited" : "desktop_visual_planning_failed", record.FailureCode);
        Assert.DoesNotContain(privateError, JsonSerializer.Serialize(record), StringComparison.Ordinal);
        Assert.Empty(audit.Completed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MissingFrozenOrRegisteredVisionFailsWithoutSendingImage(bool frozenVision, bool registeredVision)
    {
        using var observation = Observation();
        var provider = new FakeProvider("qwen", registeredVision ? ChatModelCapabilities.Vision : ChatModelCapabilities.None,
            (_, _) => throw new InvalidOperationException("Images must not reach this provider."));
        var audit = new InvocationStore();
        var planner = await Planner(audit, provider);
        var route = Route(provider) with { Capabilities = frozenVision ? ChatModelCapabilities.Vision : ChatModelCapabilities.None };
        var error = await Assert.ThrowsAsync<DesktopTaskException>(() =>
            planner.PlanAsync(Guid.NewGuid(), route, PrivateGoal, observation, []));
        Assert.Equal("desktop_visual_planning_provider_error", error.Code);
        Assert.Empty(provider.Requests);
        Assert.Equal("chat.vision_not_supported", Assert.Single(audit.Records.Values).FailureCode);
        Assert.Empty(audit.Completed);
    }

    private static DesktopVisualObservation Observation()
    {
        var bytes = Convert.FromBase64String(Png);
        return new(Snapshot(bytes), bytes);
    }

    private static DesktopVisualSnapshot Snapshot(byte[] bytes) => new(Guid.NewGuid(),
        new DesktopTaskWindow(87654321, 123456, "PRIVATE_NATIVE_PROCESS", DateTimeOffset.UtcNow,
            @"C:\PRIVATE_NATIVE_PATH\app.exe", "PRIVATE_NATIVE_CLASS", "PRIVATE_NATIVE_TITLE"),
        new DesktopTaskBounds(-87654321, 12345, 2, 2), 2, 2, 1, 1, 192, "PRIVATE_NATIVE_FINGERPRINT",
        Convert.ToHexString(SHA256.HashData(bytes)), PrivateOcr, DateTimeOffset.UtcNow);

    private static string Proposal(ChatModelRequest request)
    {
        using var input = JsonDocument.Parse(Assert.Single(request.Messages).Content);
        return JsonSerializer.Serialize(new
        {
            snapshotId = input.RootElement.GetProperty("snapshotId").GetGuid(),
            coordinateSpace = "normalized1000",
            action = "Wait", bounds = (object?)null, text = (string?)null,
            target = "合成画面", reason = "等待合成画面更新", evidenceText = (string?)null
        });
    }

    private static ChatModelResponse Response(string provider, string text) => new(text, ChatFinishReason.Stop,
        new ChatModelUsage(12, 8, 20), new ChatProviderMetadata(provider, "model-a", "synthetic-request-id", "test destination"));

    private static FrozenChatModelRoute Route(FakeProvider provider) => new(provider.Descriptor.ProviderId, "model-a",
        provider.Descriptor.Models[0].Capabilities, null, provider.Descriptor.DataDestination, true);

    private static FakeProvider NeverCalledProvider(string id = "qwen") => new(id, ChatModelCapabilities.Vision,
        (_, _) => throw new InvalidOperationException("Provider must not be called."));

    private static async Task<ModelDesktopVisualPlanner> Planner(InvocationStore audit, params FakeProvider[] providers)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScreenGuide.slnx"))) directory = directory.Parent;
        var prompts = await PromptRegistry.LoadAsync(Path.Combine(directory!.FullName, "prompts", "runtime"));
        return new(new ModelRouter(new ChatProviderRegistry(providers), new FrozenSettings()), prompts, audit, TimeProvider.System);
    }

    private sealed class FrozenSettings : IAiSettingsStore
    {
        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Must use frozen route.");
        public Task SaveAsync(AiSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeProvider(string id, ChatModelCapabilities capabilities,
        Func<ChatModelRequest, CancellationToken, Task<ChatModelResponse>> handler) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(id, "Test provider", "test destination", true,
            [new ChatModelDescriptor("model-a", "Test model", capabilities)]);
        public List<ChatModelRequest> Requests { get; } = [];
        public List<Guid> CancelledTurns { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChatModelResponse> CompleteAsync(ChatModelRequest request, ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Started.TrySetResult();
            try { return await handler(request, cancellationToken); }
            finally { Returned.TrySetResult(); }
        }
        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
        {
            CancelledTurns.Add(turnId);
            return Task.CompletedTask;
        }
        public Task<ChatProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InvocationStore : IAiInvocationStore
    {
        public Dictionary<Guid, AiInvocationRecord> Records { get; } = [];
        public List<Guid> Completed { get; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(AiInvocationRecord invocation, CancellationToken cancellationToken = default)
        {
            Records.Add(invocation.Id, invocation);
            return Task.CompletedTask;
        }
        public Task<AiInvocationTransitionResult> CompleteAsync(Guid id, string finishReason, AiTokenUsage? usage,
            string? providerRequestId, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default)
        {
            Completed.Add(id);
            return Task.FromResult(Transition(id, AiInvocationStatus.Succeeded, completedAtUtc, null, finishReason, usage, providerRequestId));
        }
        public Task<AiInvocationTransitionResult> FailAsync(Guid id, AiInvocationStatus status, string failureCode,
            DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(Transition(id, status, completedAtUtc, failureCode));
        private AiInvocationTransitionResult Transition(Guid id, AiInvocationStatus status, DateTimeOffset at,
            string? code, string? finishReason = null, AiTokenUsage? usage = null, string? providerRequestId = null)
        {
            var current = Records[id];
            var disposition = current.Status == AiInvocationStatus.Running ? AiInvocationTransitionDisposition.Applied
                : current.Status == status ? AiInvocationTransitionDisposition.AlreadyInRequestedTerminal
                : AiInvocationTransitionDisposition.RejectedByExistingTerminal;
            if (disposition == AiInvocationTransitionDisposition.Applied)
                Records[id] = current = current with { Status = status, CompletedAtUtc = at, FailureCode = code,
                    FinishReason = finishReason, Usage = usage, ProviderRequestId = providerRequestId };
            return new(status, disposition, current);
        }
        public Task<AiInvocationRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Records.GetValueOrDefault(id));
        public Task<IReadOnlyList<AiInvocationRecord>> GetForSessionTurnAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiInvocationRecord>>(Records.Values.Where(record => record.SessionTurnId == id).ToArray());
        public Task<IReadOnlyList<AiInvocationRecord>> GetForConversationTurnAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiInvocationRecord>>(Records.Values.Where(record => record.ConversationTurnId == id).ToArray());
        public Task<AiInvocationRecoveryResult> InterruptRunningAsync(DateTimeOffset at, string failureCode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
