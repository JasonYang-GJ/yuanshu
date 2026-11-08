using System.Net;
using System.Text;
using System.Text.Json;
using ScreenGuide.AI.Core;
using ScreenGuide.DesktopV01.RealAcceptanceRunner;
using Xunit;

namespace ScreenGuide.AI.DeepSeek.Tests;

public sealed class DeepSeekChatModelProviderTests
{
    [Fact]
    public async Task ImagesAreRejectedBeforeCredentialsAndHttpInsteadOfBeingSilentlyDiscarded()
    {
        using var image = new ChatImage(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP4/x8AAwAB//wl3FEAAAAASUVORK5CYII="));
        var credentials = new TestCredentialStore("synthetic-key");
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Must not send."));
        await using var provider = new DeepSeekChatModelProvider(credentials, handler);
        var request = new ChatModelRequest(Guid.NewGuid(), Guid.NewGuid(), DeepSeekChatModelProvider.FlashModelId,
            "system", [new(ChatMessageRole.User, "synthetic", Images: [image])]);
        var error = await Assert.ThrowsAsync<ChatModelException>(() => provider.CompleteAsync(request));
        Assert.Equal("chat.vision_not_supported", error.Error.Code);
        Assert.Equal(0, credentials.OpenLeaseCount);
        Assert.Equal(0, handler.SendCount);
        Assert.False(image.IsDisposed);
    }

    [Fact]
    public async Task DescriptorExposesOnlyTheFixedDeepSeekDestinationAndOfficialModels()
    {
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore(),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.Equal("deepseek", provider.Descriptor.ProviderId);
        Assert.Equal("DeepSeek", provider.Descriptor.DisplayName);
        Assert.Equal("https://api.deepseek.com", provider.Descriptor.DataDestination);
        Assert.True(provider.Descriptor.SendsDataOffDevice);
        Assert.Equal(ChatProviderCredentialKind.ApiKey, provider.Descriptor.CredentialKind);
        Assert.Equal(
            ["deepseek-v4-flash", "deepseek-v4-pro"],
            provider.Descriptor.Models.Select(model => model.ModelId).ToArray());
        Assert.All(provider.Descriptor.Models, model =>
        {
            Assert.Equal(1_000_000, model.ContextWindowTokens);
            Assert.Equal(
                ChatModelCapabilities.Streaming
                | ChatModelCapabilities.JsonObjectOutput
                | ChatModelCapabilities.Reasoning,
                model.Capabilities);
        });
    }

    [Fact]
    public async Task NonStreamingCompletionUsesCredentialLeaseAndMapsJsonObjectUsageAndFinishReason()
    {
        const string secret = "ds-test-secret-never-log";
        var credentialStore = new TestCredentialStore(secret);
        HttpMethod? observedMethod = null;
        Uri? observedUri = null;
        string? observedAuthorization = null;
        string? observedBody = null;
        await using var provider = new DeepSeekChatModelProvider(
            credentialStore,
            new StubHttpMessageHandler(async (request, cancellationToken) =>
            {
                observedMethod = request.Method;
                observedUri = request.RequestUri;
                observedAuthorization = request.Headers.Authorization?.ToString();
                observedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse("""
                    {
                      "id": "req-deepseek-1",
                      "model": "deepseek-v4-flash",
                      "choices": [
                        {
                          "message": { "role": "assistant", "content": "{\"answer\":\"你好\"}" },
                          "finish_reason": "stop"
                        }
                      ],
                      "usage": {
                        "prompt_tokens": 12,
                        "completion_tokens": 5,
                        "total_tokens": 17,
                        "prompt_cache_hit_tokens": 3,
                        "completion_tokens_details": { "reasoning_tokens": 2 }
                      }
                    }
                    """);
            }));

        var response = await provider.CompleteAsync(new ChatModelRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DeepSeekChatModelProvider.FlashModelId,
            "你是元枢。",
            [
                new ChatMessage(ChatMessageRole.User, "第一轮"),
                new ChatMessage(ChatMessageRole.Assistant, "上一轮回答"),
                new ChatMessage(ChatMessageRole.User, "继续")
            ],
            new ChatModelOptions(Temperature: 0.2, MaxOutputTokens: 512, TopP: 0.9, Seed: 7),
            ChatResponseFormat.JsonObject,
            new ChatPromptReference("chat.general", "2", "ABC123")));

        Assert.Equal(HttpMethod.Post, observedMethod);
        Assert.Equal("https://api.deepseek.com/chat/completions", observedUri?.AbsoluteUri);
        Assert.Equal($"Bearer {secret}", observedAuthorization);
        Assert.True(credentialStore.LastLeaseDisposed);
        using var body = JsonDocument.Parse(observedBody!);
        Assert.Equal("deepseek-v4-flash", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(4, body.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.DoesNotContain(secret, observedBody, StringComparison.Ordinal);

        Assert.Equal("{\"answer\":\"你好\"}", response.Text);
        Assert.Equal(response.Text, response.StructuredJson);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(new ChatModelUsage(12, 5, 17, 3, 2), response.Usage);
        Assert.Equal("deepseek", response.Metadata.ProviderId);
        Assert.Equal("deepseek-v4-flash", response.Metadata.ModelId);
        Assert.Equal("req-deepseek-1", response.Metadata.ProviderRequestId);
        Assert.Equal("https://api.deepseek.com", response.Metadata.DataDestination);
    }

    [Fact]
    public async Task JsonSchemaIsRejectedBeforeCredentialOrNetworkAccess()
    {
        var credentials = new TestCredentialStore("ds-never-open");
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Network must not be reached."));
        await using var provider = new DeepSeekChatModelProvider(credentials, handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(
                responseFormat: ChatResponseFormat.JsonSchema(
                    "answer-v1",
                    "{\"type\":\"object\"}"))));

        Assert.Equal(ChatModelErrorKind.InvalidRequest, exception.Error.Kind);
        Assert.Equal("deepseek.json_schema_not_supported", exception.Error.Code);
        Assert.Equal(0, credentials.OpenLeaseCount);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task MissingCredentialAndUnknownModelNeverReachTheNetwork()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Network must not be reached."));
        await using var provider = new DeepSeekChatModelProvider(new TestCredentialStore(), handler);

        var missing = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));
        var unknown = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(modelId: "deepseek-unknown")));

        Assert.Equal(ChatModelErrorKind.Configuration, missing.Error.Kind);
        Assert.Equal("deepseek.not_configured", missing.Error.Code);
        Assert.Equal(ChatModelErrorKind.ModelNotFound, unknown.Error.Kind);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task InputLimitsAreEnforcedBeforeCredentialOrNetworkAccess()
    {
        var credentials = new TestCredentialStore("ds-never-open");
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Network must not be reached."));
        await using var provider = new DeepSeekChatModelProvider(
            credentials,
            handler,
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 2,
                MaxInputCharacters: 10,
                MaxOutputCharacters: 20,
                MaxErrorBodyCharacters: 16));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(messages:
                [new ChatMessage(ChatMessageRole.User, "12345678901")])));

        Assert.Equal(ChatModelErrorKind.InvalidRequest, exception.Error.Kind);
        Assert.Equal("deepseek.input_too_large", exception.Error.Code);
        Assert.Equal(0, credentials.OpenLeaseCount);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task StreamingCompletionIgnoresKeepAliveRecognizesDoneAndMapsUsage()
    {
        const string sse = """
            : keep-alive

            data: {"id":"req-stream-1","choices":[{"delta":{"content":"你"},"finish_reason":null}]}

            data:{"id":"req-stream-1","choices":[{"delta":{"content":"好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":2,"total_tokens":9}}

            data:[DONE]

            """;
        string? observedBody = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            observedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });
        var streamCredentials = new TestCredentialStore("ds-stream-test");
        await using var provider = new DeepSeekChatModelProvider(
            streamCredentials,
            handler);
        var updates = new List<ChatStreamUpdate>();

        var response = await provider.CompleteAsync(
            Request(),
            (update, _) =>
            {
                Assert.True(
                    streamCredentials.LastLeaseDisposed,
                    "响应头到达后应在处理 SSE 内容前释放凭据租约。");
                updates.Add(update);
                return ValueTask.CompletedTask;
            });

        using var body = JsonDocument.Parse(observedBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(["你", "好", ""], updates.Select(update => update.DeltaText).ToArray());
        Assert.Equal([1L, 2L, 3L], updates.Select(update => update.SequenceNumber).ToArray());
        Assert.False(updates[0].IsFinal);
        Assert.False(updates[1].IsFinal);
        Assert.True(updates[2].IsFinal);
        Assert.Equal("你好", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(new ChatModelUsage(7, 2, 9), response.Usage);
        Assert.Equal("req-stream-1", response.Metadata.ProviderRequestId);
    }

    [Fact]
    public async Task R3OfflineSseFixtureContentOnlyProducesContentAndMetadata()
    {
        var observation = await ObserveOfflineSseFixtureAsync(
            """
            data: {"id":"fixture-content","choices":[{"delta":{"content":"fixture-answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}

            data: [DONE]

            """);

        Assert.True(observation.ResponseReturned);
        Assert.Equal(1, observation.NonEmptyDeltaCount);
        Assert.Equal(1, observation.ValidProtocolDeltaCount);
        Assert.True(observation.FinalUpdateObserved);
        Assert.Equal(ChatFinishReason.Stop, observation.FinishReason);
        Assert.Null(observation.ErrorCode);
        Assert.True(observation.HasUsage);
        Assert.True(observation.HasProviderRequestId);
        Assert.False(observation.RestrictedDataLeaked);
        Assert.Equal(1, observation.FakeHandlerSendCount);
        Assert.Equal(2, observation.ShapeEvidence.SseEventCount);
        Assert.Equal(1, observation.ShapeEvidence.DeltaCount);
        Assert.True(observation.ShapeEvidence.ContentAppeared);
        Assert.Equal("fixture-answer".Length, observation.ShapeEvidence.ContentCharacterCount);
        Assert.False(observation.ShapeEvidence.ReasoningContentAppeared);
        Assert.Equal(0, observation.ShapeEvidence.ReasoningContentCharacterCount);
        Assert.True(observation.ShapeEvidence.DoneAppeared);
        Assert.True(observation.ShapeEvidence.FinishReasonAppeared);
        Assert.Equal("stop", observation.ShapeEvidence.FinishReasonCategory);
        Assert.Null(observation.ShapeEvidence.StableErrorKind);
        Assert.Null(observation.ShapeEvidence.ProviderDiagnosticCode);
        Assert.DoesNotContain(
            "fixture-answer",
            JsonSerializer.Serialize(observation.ShapeEvidence),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task R3OfflineSseFixtureReasoningOnlyIsClassifiedAsStreamEmptyWithoutLeakage()
    {
        const string reasoningSentinel = "fixture-private-reasoning";
        var observation = await ObserveOfflineSseFixtureAsync(
            """
            data: {"id":"fixture-reasoning","choices":[{"delta":{"reasoning_content":"__REASONING__"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}

            data: [DONE]

            """.Replace("__REASONING__", reasoningSentinel, StringComparison.Ordinal),
            reasoningSentinel);

        Assert.False(observation.ResponseReturned);
        Assert.Equal(0, observation.NonEmptyDeltaCount);
        Assert.Equal(1, observation.ValidProtocolDeltaCount);
        Assert.False(observation.FinalUpdateObserved);
        Assert.Null(observation.FinishReason);
        Assert.Equal("deepseek.stream_empty", observation.ErrorCode);
        Assert.False(observation.HasUsage);
        Assert.False(observation.HasProviderRequestId);
        Assert.False(observation.RestrictedDataLeaked);
        Assert.Equal(1, observation.FakeHandlerSendCount);
        Assert.Equal(2, observation.ShapeEvidence.SseEventCount);
        Assert.Equal(1, observation.ShapeEvidence.DeltaCount);
        Assert.False(observation.ShapeEvidence.ContentAppeared);
        Assert.Equal(0, observation.ShapeEvidence.ContentCharacterCount);
        Assert.True(observation.ShapeEvidence.ReasoningContentAppeared);
        Assert.Equal(
            reasoningSentinel.Length,
            observation.ShapeEvidence.ReasoningContentCharacterCount);
        Assert.True(observation.ShapeEvidence.DoneAppeared);
        Assert.True(observation.ShapeEvidence.FinishReasonAppeared);
        Assert.Equal("stop", observation.ShapeEvidence.FinishReasonCategory);
        Assert.Equal("invalid_response", observation.ShapeEvidence.StableErrorKind);
        Assert.Equal(
            "deepseek.stream_empty",
            observation.ShapeEvidence.ProviderDiagnosticCode);
        var evidenceJson = JsonSerializer.Serialize(observation.ShapeEvidence);
        Assert.DoesNotContain(reasoningSentinel, evidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-fake-credential", evidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("你好", evidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", evidenceJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", evidenceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R3OfflineSseFixtureReasoningAndContentDeliversOnlyContent()
    {
        const string reasoningSentinel = "fixture-hidden-reasoning";
        var observation = await ObserveOfflineSseFixtureAsync(
            """
            data: {"id":"fixture-mixed","choices":[{"delta":{"reasoning_content":"__REASONING__"},"finish_reason":null}]}

            data: {"id":"fixture-mixed","choices":[{"delta":{"content":"fixture-answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}

            data: [DONE]

            """.Replace("__REASONING__", reasoningSentinel, StringComparison.Ordinal),
            reasoningSentinel);

        Assert.True(observation.ResponseReturned);
        Assert.Equal(1, observation.NonEmptyDeltaCount);
        Assert.Equal(2, observation.ValidProtocolDeltaCount);
        Assert.True(observation.FinalUpdateObserved);
        Assert.Equal(ChatFinishReason.Stop, observation.FinishReason);
        Assert.Null(observation.ErrorCode);
        Assert.True(observation.HasUsage);
        Assert.True(observation.HasProviderRequestId);
        Assert.False(observation.RestrictedDataLeaked);
        Assert.Equal(1, observation.FakeHandlerSendCount);
        Assert.Equal(3, observation.ShapeEvidence.SseEventCount);
        Assert.Equal(2, observation.ShapeEvidence.DeltaCount);
        Assert.True(observation.ShapeEvidence.ContentAppeared);
        Assert.Equal("fixture-answer".Length, observation.ShapeEvidence.ContentCharacterCount);
        Assert.True(observation.ShapeEvidence.ReasoningContentAppeared);
        Assert.Equal(
            reasoningSentinel.Length,
            observation.ShapeEvidence.ReasoningContentCharacterCount);
        Assert.True(observation.ShapeEvidence.DoneAppeared);
        Assert.True(observation.ShapeEvidence.FinishReasonAppeared);
        Assert.Equal("stop", observation.ShapeEvidence.FinishReasonCategory);
        Assert.Null(observation.ShapeEvidence.StableErrorKind);
        Assert.Null(observation.ShapeEvidence.ProviderDiagnosticCode);
        var evidenceJson = JsonSerializer.Serialize(observation.ShapeEvidence);
        Assert.DoesNotContain(reasoningSentinel, evidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-answer", evidenceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R3OfflineSseFixtureDoneWithoutContentIsClassifiedAsStreamEmpty()
    {
        var observation = await ObserveOfflineSseFixtureAsync("data: [DONE]\n");

        Assert.False(observation.ResponseReturned);
        Assert.Equal(0, observation.NonEmptyDeltaCount);
        Assert.Equal(0, observation.ValidProtocolDeltaCount);
        Assert.False(observation.FinalUpdateObserved);
        Assert.Null(observation.FinishReason);
        Assert.Equal("deepseek.stream_empty", observation.ErrorCode);
        Assert.False(observation.HasUsage);
        Assert.False(observation.HasProviderRequestId);
        Assert.False(observation.RestrictedDataLeaked);
        Assert.Equal(1, observation.FakeHandlerSendCount);
        Assert.Equal(1, observation.ShapeEvidence.SseEventCount);
        Assert.Equal(0, observation.ShapeEvidence.DeltaCount);
        Assert.False(observation.ShapeEvidence.ContentAppeared);
        Assert.Equal(0, observation.ShapeEvidence.ContentCharacterCount);
        Assert.False(observation.ShapeEvidence.ReasoningContentAppeared);
        Assert.Equal(0, observation.ShapeEvidence.ReasoningContentCharacterCount);
        Assert.True(observation.ShapeEvidence.DoneAppeared);
        Assert.False(observation.ShapeEvidence.FinishReasonAppeared);
        Assert.Null(observation.ShapeEvidence.FinishReasonCategory);
        Assert.Equal("invalid_response", observation.ShapeEvidence.StableErrorKind);
        Assert.Equal(
            "deepseek.stream_empty",
            observation.ShapeEvidence.ProviderDiagnosticCode);
    }

    [Fact]
    public async Task R3OfflineSseFixtureEmptyDeltaIsClassifiedAsStreamEmpty()
    {
        var observation = await ObserveOfflineSseFixtureAsync(
            """
            data: {"id":"fixture-empty","choices":[{"delta":{"content":""},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":0,"total_tokens":2}}

            data: [DONE]

            """);

        Assert.False(observation.ResponseReturned);
        Assert.Equal(0, observation.NonEmptyDeltaCount);
        Assert.Equal(0, observation.ValidProtocolDeltaCount);
        Assert.False(observation.FinalUpdateObserved);
        Assert.Null(observation.FinishReason);
        Assert.Equal("deepseek.stream_empty", observation.ErrorCode);
        Assert.False(observation.HasUsage);
        Assert.False(observation.HasProviderRequestId);
        Assert.False(observation.RestrictedDataLeaked);
        Assert.Equal(1, observation.FakeHandlerSendCount);
        Assert.Equal(2, observation.ShapeEvidence.SseEventCount);
        Assert.Equal(1, observation.ShapeEvidence.DeltaCount);
        Assert.True(observation.ShapeEvidence.ContentAppeared);
        Assert.Equal(0, observation.ShapeEvidence.ContentCharacterCount);
        Assert.False(observation.ShapeEvidence.ReasoningContentAppeared);
        Assert.Equal(0, observation.ShapeEvidence.ReasoningContentCharacterCount);
        Assert.True(observation.ShapeEvidence.DoneAppeared);
        Assert.True(observation.ShapeEvidence.FinishReasonAppeared);
        Assert.Equal("stop", observation.ShapeEvidence.FinishReasonCategory);
        Assert.Equal("invalid_response", observation.ShapeEvidence.StableErrorKind);
        Assert.Equal(
            "deepseek.stream_empty",
            observation.ShapeEvidence.ProviderDiagnosticCode);
    }

    [Fact]
    public async Task R3OfflineSseFixtureMalformedEventHasDistinctSafeError()
    {
        const string rawEventSentinel = "fixture-raw-event-must-not-leak";
        var observation = await ObserveOfflineSseFixtureAsync(
            $"data: {{\"marker\":\"{rawEventSentinel}\"\n",
            rawEventSentinel);

        Assert.False(observation.ResponseReturned);
        Assert.Equal(0, observation.NonEmptyDeltaCount);
        Assert.Equal(0, observation.ValidProtocolDeltaCount);
        Assert.False(observation.FinalUpdateObserved);
        Assert.Null(observation.FinishReason);
        Assert.Equal("deepseek.invalid_stream_event", observation.ErrorCode);
        Assert.False(observation.HasUsage);
        Assert.False(observation.HasProviderRequestId);
        Assert.False(observation.RestrictedDataLeaked);
        Assert.Equal(1, observation.FakeHandlerSendCount);
        Assert.Equal(1, observation.ShapeEvidence.SseEventCount);
        Assert.Equal(0, observation.ShapeEvidence.DeltaCount);
        Assert.False(observation.ShapeEvidence.ContentAppeared);
        Assert.False(observation.ShapeEvidence.ReasoningContentAppeared);
        Assert.False(observation.ShapeEvidence.DoneAppeared);
        Assert.False(observation.ShapeEvidence.FinishReasonAppeared);
        Assert.Null(observation.ShapeEvidence.FinishReasonCategory);
        Assert.Equal("invalid_response", observation.ShapeEvidence.StableErrorKind);
        Assert.Equal(
            "deepseek.invalid_stream_event",
            observation.ShapeEvidence.ProviderDiagnosticCode);
        var evidenceJson = JsonSerializer.Serialize(observation.ShapeEvidence);
        Assert.DoesNotContain(rawEventSentinel, evidenceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelAsyncCancelsTheBoundTurnAndRejectsALateSuccessfulResponse()
    {
        var turnId = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLateSuccess = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => cancellationObserved.TrySetResult());
            started.TrySetResult();
            await allowLateSuccess.Task;
            return JsonResponse("""
                {
                  "id":"late-success",
                  "choices":[{"message":{"content":"不应出现"},"finish_reason":"stop"}]
                }
                """);
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-cancel-test"),
            handler);

        var completion = provider.CompleteAsync(Request(turnId: turnId));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancellation = provider.CancelAsync(turnId);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        allowLateSuccess.TrySetResult();
        await cancellation.WaitAsync(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<ChatModelException>(() => completion);

        Assert.Equal(ChatModelErrorKind.Cancelled, exception.Error.Kind);
        Assert.Equal("deepseek.cancelled", exception.Error.Code);
        Assert.DoesNotContain("ds-cancel-test", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("不应出现", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestTimeoutCancelsHttpAndMapsToTimeoutInsteadOfUserCancellation()
    {
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => cancellationObserved.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-timeout-test"),
            handler,
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromMilliseconds(50),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 1_000,
                MaxErrorBodyCharacters: 100));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ChatModelErrorKind.Timeout, exception.Error.Kind);
        Assert.Equal("deepseek.timeout", exception.Error.Code);
        Assert.True(exception.Error.IsRetryable);
    }

    [Theory]
    [InlineData(400, ChatModelErrorKind.InvalidRequest, "deepseek.bad_request", false)]
    [InlineData(401, ChatModelErrorKind.Unauthorized, "deepseek.unauthorized", false)]
    [InlineData(402, ChatModelErrorKind.InsufficientBalance, "deepseek.insufficient_balance", false)]
    [InlineData(403, ChatModelErrorKind.Authorization, "deepseek.authorization_denied", false)]
    [InlineData(422, ChatModelErrorKind.InvalidRequest, "deepseek.unprocessable_request", false)]
    [InlineData(429, ChatModelErrorKind.RateLimited, "deepseek.rate_limited", true)]
    [InlineData(500, ChatModelErrorKind.Unavailable, "deepseek.server_error", true)]
    [InlineData(503, ChatModelErrorKind.Unavailable, "deepseek.unavailable", true)]
    public async Task HttpFailuresMapToStableSafeErrors(
        int statusCode,
        ChatModelErrorKind expectedKind,
        string expectedCode,
        bool retryable)
    {
        const string secret = "ds-http-failure-secret";
        var rawBody = "{\"error\":{\"code\":\"provider_error\",\"message\":\""
                      + secret
                      + new string('x', 2_000)
                      + "\"}}";
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new StringContent(rawBody, Encoding.UTF8, "application/json")
            };
            if (statusCode is 429 or 503)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(3));
            }

            return response;
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore(secret),
            handler,
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 1_000,
                MaxErrorBodyCharacters: 64));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));

        Assert.Equal(expectedKind, exception.Error.Kind);
        Assert.Equal(expectedCode, exception.Error.Code);
        Assert.Equal(retryable, exception.Error.IsRetryable);
        Assert.Equal(statusCode is 429 or 503 ? TimeSpan.FromSeconds(3) : null, exception.Error.RetryAfter);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("provider_error", exception.ToString(), StringComparison.Ordinal);
        Assert.True(exception.ToString().Length < 2_000);
    }

    [Theory]
    [InlineData(401, "model_not_found", "model not found", ChatModelErrorKind.Unauthorized, "deepseek.unauthorized")]
    [InlineData(403, "invalid_model", "model does not exist", ChatModelErrorKind.Authorization, "deepseek.authorization_denied")]
    [InlineData(402, "model_not_found", "model not found", ChatModelErrorKind.InsufficientBalance, "deepseek.insufficient_balance")]
    [InlineData(429, "invalid_model", "model does not exist", ChatModelErrorKind.RateLimited, "deepseek.rate_limited")]
    [InlineData(500, "model_not_found", "model not found", ChatModelErrorKind.Unavailable, "deepseek.server_error")]
    [InlineData(503, "invalid_model", "model does not exist", ChatModelErrorKind.Unavailable, "deepseek.unavailable")]
    [InlineData(404, "model_not_found", "model not found", ChatModelErrorKind.ModelNotFound, "deepseek.model_not_found")]
    [InlineData(400, "invalid_model", "model does not exist", ChatModelErrorKind.ModelNotFound, "deepseek.model_not_found")]
    [InlineData(422, "model_not_found", "model not found", ChatModelErrorKind.ModelNotFound, "deepseek.model_not_found")]
    [InlineData(400, "invalid_request", "bad payload", ChatModelErrorKind.InvalidRequest, "deepseek.bad_request")]
    [InlineData(422, "invalid_request", "bad payload", ChatModelErrorKind.InvalidRequest, "deepseek.unprocessable_request")]
    public async Task AuthoritativeHttpStatusWinsWhenProviderBodySuggestsAConflictingModelError(
        int statusCode,
        string providerCode,
        string providerMessage,
        ChatModelErrorKind expectedKind,
        string expectedDiagnosticCode)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(
            (HttpStatusCode)statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    error = new { code = providerCode, message = providerMessage }
                }),
                Encoding.UTF8,
                "application/json")
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-status-precedence-test"),
            handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));

        Assert.Equal(expectedKind, exception.Error.Kind);
        Assert.Equal(expectedDiagnosticCode, exception.Error.Code);
    }

    [Theory]
    [InlineData(400, "3")]
    [InlineData(429, "0")]
    [InlineData(429, "86401")]
    [InlineData(503, "0")]
    [InlineData(503, "86401")]
    public async Task InvalidOrUntrustedRetryAfterIsDiscarded(int statusCode, string retryAfter)
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)statusCode);
            Assert.True(response.Headers.TryAddWithoutValidation("Retry-After", retryAfter));
            return response;
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-retry-after-test"),
            handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));

        Assert.Null(exception.Error.RetryAfter);
    }

    [Fact]
    public async Task MaliciousProviderRequestIdIsNotReturnedAsMetadata()
    {
        const string maliciousId = "Bearer fake-key C:\\\\Users\\\\person\\\\tasking.db prompt stack trace";
        var handler = new StubHttpMessageHandler(_ => JsonResponse($$"""
            {"id":"{{maliciousId}}","choices":[{"message":{"content":"安全回答"},"finish_reason":"stop"}]}
            """));
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-request-id-test"),
            handler);

        var response = await provider.CompleteAsync(Request());

        Assert.Null(response.Metadata.ProviderRequestId);
        Assert.DoesNotContain("fake-key", response.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelErrorAndRedirectAreRejectedWithoutFollowingAnotherDestination()
    {
        var modelHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"model_not_found\",\"message\":\"missing\"}}",
                Encoding.UTF8,
                "application/json")
        });
        await using var modelProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-model-test"),
            modelHandler);
        var modelError = await Assert.ThrowsAsync<ChatModelException>(() =>
            modelProvider.CompleteAsync(Request()));

        var redirectHandler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.invalid/steal");
            return response;
        });
        await using var redirectProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-redirect-test"),
            redirectHandler);
        var redirectError = await Assert.ThrowsAsync<ChatModelException>(() =>
            redirectProvider.CompleteAsync(Request()));

        Assert.Equal(ChatModelErrorKind.ModelNotFound, modelError.Error.Kind);
        Assert.Equal("deepseek.model_not_found", modelError.Error.Code);
        Assert.Equal(ChatModelErrorKind.InvalidResponse, redirectError.Error.Kind);
        Assert.Equal("deepseek.redirect_rejected", redirectError.Error.Code);
        Assert.Equal(1, redirectHandler.SendCount);
    }

    [Fact]
    public async Task NetworkAndMalformedSuccessNeverExposeRawExceptionOrBody()
    {
        const string secret = "ds-network-secret";
        var networkProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore(secret),
            new StubHttpMessageHandler((_, _) =>
                throw new HttpRequestException($"socket failed Authorization: Bearer {secret}")));
        await using (networkProvider)
        {
            var network = await Assert.ThrowsAsync<ChatModelException>(() =>
                networkProvider.CompleteAsync(Request()));
            Assert.Equal(ChatModelErrorKind.Network, network.Error.Kind);
            Assert.DoesNotContain(secret, network.ToString(), StringComparison.Ordinal);
        }

        await using var malformedProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore(secret),
            new StubHttpMessageHandler(_ => JsonResponse($"not-json-{secret}")));
        var malformed = await Assert.ThrowsAsync<ChatModelException>(() =>
            malformedProvider.CompleteAsync(Request()));
        Assert.Equal(ChatModelErrorKind.InvalidResponse, malformed.Error.Kind);
        Assert.DoesNotContain(secret, malformed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthWithoutCredentialIsNotConfiguredAndDoesNotUseNetwork()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Network must not be reached."));
        await using var provider = new DeepSeekChatModelProvider(new TestCredentialStore(), handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal("deepseek", health.ProviderId);
        Assert.Equal(ChatProviderHealthState.NotConfigured, health.State);
        Assert.False(health.IsConfigured);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task HealthUsesFixedModelsEndpointAndPerRequestAuthorization()
    {
        const string secret = "ds-health-secret";
        HttpMethod? observedMethod = null;
        Uri? observedUri = null;
        string? observedAuthorization = null;
        var credentials = new TestCredentialStore(secret);
        var handler = new StubHttpMessageHandler(request =>
        {
            observedMethod = request.Method;
            observedUri = request.RequestUri;
            observedAuthorization = request.Headers.Authorization?.ToString();
            return JsonResponse("{\"data\":[]}");
        });
        await using var provider = new DeepSeekChatModelProvider(credentials, handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(ChatProviderHealthState.Healthy, health.State);
        Assert.True(health.IsConfigured);
        Assert.Equal(HttpMethod.Get, observedMethod);
        Assert.Equal("https://api.deepseek.com/models", observedUri?.AbsoluteUri);
        Assert.Equal($"Bearer {secret}", observedAuthorization);
        Assert.True(credentials.LastLeaseDisposed);
    }

    [Theory]
    [InlineData(401, ChatProviderHealthState.Unavailable)]
    [InlineData(403, ChatProviderHealthState.Unavailable)]
    [InlineData(402, ChatProviderHealthState.Degraded)]
    [InlineData(429, ChatProviderHealthState.Degraded)]
    [InlineData(500, ChatProviderHealthState.Unavailable)]
    [InlineData(503, ChatProviderHealthState.Unavailable)]
    public async Task HealthMapsProviderFailureWithoutThrowingOrLeakingBody(
        int statusCode,
        ChatProviderHealthState expectedState)
    {
        const string secret = "ds-health-error-secret";
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new StringContent(
                $"{{\"error\":{{\"message\":\"{secret}\"}}}}",
                Encoding.UTF8,
                "application/json")
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore(secret),
            handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(expectedState, health.State);
        Assert.True(health.IsConfigured);
        Assert.DoesNotContain(secret, health.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRequestOpensANewCredentialLeaseAndUsesTheRotatedKey()
    {
        var credentials = new QueueCredentialStore("ds-key-one", "ds-key-two");
        var authorizations = new List<string?>();
        var handler = new StubHttpMessageHandler(request =>
        {
            authorizations.Add(request.Headers.Authorization?.ToString());
            return JsonResponse("""
                {"id":"rotation","choices":[{"message":{"content":"好"},"finish_reason":"stop"}]}
                """);
        });
        await using var provider = new DeepSeekChatModelProvider(credentials, handler);

        _ = await provider.CompleteAsync(Request());
        _ = await provider.CompleteAsync(Request());

        Assert.Equal(["Bearer ds-key-one", "Bearer ds-key-two"], authorizations);
        Assert.Equal(2, credentials.DisposedLeaseCount);
    }

    [Fact]
    public async Task DuplicateActiveTurnIsRejectedAndCallerCancellationCancelsHttp()
    {
        var turnId = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-duplicate-test"),
            handler);
        using var callerCancellation = new CancellationTokenSource();
        var first = provider.CompleteAsync(Request(turnId: turnId), cancellationToken: callerCancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var duplicate = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(turnId: turnId)));
        callerCancellation.Cancel();
        var cancelled = await Assert.ThrowsAsync<ChatModelException>(() => first);

        Assert.Equal("deepseek.turn_already_active", duplicate.Error.Code);
        Assert.Equal(ChatModelErrorKind.Cancelled, cancelled.Error.Kind);
    }

    [Fact]
    public async Task IncompleteSseInvalidJsonObjectAndOversizedOutputAreSafeFailures()
    {
        var incompleteHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data:{\"choices\":[{\"delta\":{\"content\":\"半截\"}}]}\n",
                Encoding.UTF8,
                "text/event-stream")
        });
        await using var incompleteProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-incomplete-test"),
            incompleteHandler);
        var incomplete = await Assert.ThrowsAsync<ChatModelException>(() =>
            incompleteProvider.CompleteAsync(Request(), (_, _) => ValueTask.CompletedTask));

        await using var invalidJsonProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-json-test"),
            new StubHttpMessageHandler(_ => JsonResponse("""
                {"choices":[{"message":{"content":"[]"},"finish_reason":"stop"}]}
                """)));
        var invalidJson = await Assert.ThrowsAsync<ChatModelException>(() =>
            invalidJsonProvider.CompleteAsync(Request(responseFormat: ChatResponseFormat.JsonObject)));

        await using var oversizedProvider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-output-test"),
            new StubHttpMessageHandler(_ => JsonResponse("""
                {"choices":[{"message":{"content":"123456"},"finish_reason":"stop"}]}
                """)),
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 5,
                MaxErrorBodyCharacters: 100));
        var oversized = await Assert.ThrowsAsync<ChatModelException>(() =>
            oversizedProvider.CompleteAsync(Request()));

        Assert.Equal("deepseek.stream_incomplete", incomplete.Error.Code);
        Assert.Equal(ChatModelErrorKind.InvalidResponse, invalidJson.Error.Kind);
        Assert.Equal("deepseek.output_too_large", oversized.Error.Code);
    }

    [Fact]
    public async Task DoneWithoutAssistantContentIsInvalidAndNeverPublishesFinalUpdate()
    {
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-empty-stream-test"),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data:[DONE]\n", Encoding.UTF8, "text/event-stream")
            }));
        var updates = new List<ChatStreamUpdate>();

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(
                Request(),
                (update, _) =>
                {
                    updates.Add(update);
                    return ValueTask.CompletedTask;
                }));

        Assert.Equal(ChatModelErrorKind.InvalidResponse, exception.Error.Kind);
        Assert.Equal("deepseek.stream_empty", exception.Error.Code);
        Assert.DoesNotContain(updates, update => update.IsFinal);
    }

    [Theory]
    [InlineData("stop", ChatFinishReason.Stop)]
    [InlineData("length", ChatFinishReason.Length)]
    [InlineData("content_filter", ChatFinishReason.ContentFilter)]
    [InlineData("insufficient_system_resource", ChatFinishReason.Error)]
    [InlineData("future_reason", ChatFinishReason.Unknown)]
    public async Task FinishReasonsAreMappedWithoutGuessing(
        string providerReason,
        ChatFinishReason expected)
    {
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-finish-test"),
            new StubHttpMessageHandler(_ => JsonResponse(
                $$"""
                {"choices":[{"message":{"content":"结果"},"finish_reason":"{{providerReason}}"}]}
                """)));

        var response = await provider.CompleteAsync(Request());

        Assert.Equal(expected, response.FinishReason);
    }

    [Fact]
    public async Task CancelAsyncStopsAnActiveSseReadForTheExactTurn()
    {
        var turnId = Guid.NewGuid();
        var slowStream = new BlockingSseStream(
            "data:{\"id\":\"slow\",\"choices\":[{\"delta\":{\"content\":\"第一段\"}}]}\n\n");
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(slowStream)
        });
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-slow-stream-test"),
            handler);
        var updates = new List<ChatStreamUpdate>();
        var completion = provider.CompleteAsync(
            Request(turnId: turnId),
            (update, _) =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            });
        await slowStream.FirstChunkRead.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => updates.Any(update => update.DeltaText == "第一段"),
            TimeSpan.FromSeconds(2));

        await provider.CancelAsync(turnId).WaitAsync(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<ChatModelException>(() => completion);

        await slowStream.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ChatModelErrorKind.Cancelled, exception.Error.Kind);
        Assert.DoesNotContain(updates, update => update.IsFinal);
    }

    [Fact]
    public async Task OversizedNonStreamingResponseIsRejectedBeforeReadingTheWholeBody()
    {
        var content = new StringContent(new string('x', 70_000), Encoding.UTF8, "application/json");
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-large-body-test"),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            }),
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 5,
                MaxErrorBodyCharacters: 100));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));

        Assert.Equal(ChatModelErrorKind.InvalidResponse, exception.Error.Kind);
        Assert.Equal("deepseek.response_body_too_large", exception.Error.Code);
    }

    [Fact]
    public async Task OversizedSseEventIsRejectedBeforeTheWholeLineIsBuffered()
    {
        var stream = new RepeatingSseLineStream(totalLength: 100_000);
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-large-sse-test"),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            }),
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 1_000,
                MaxErrorBodyCharacters: 100,
                MaxSseEventCharacters: 128));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(), (_, _) => ValueTask.CompletedTask));

        Assert.Equal(ChatModelErrorKind.InvalidResponse, exception.Error.Kind);
        Assert.Equal("deepseek.stream_event_too_large", exception.Error.Code);
        Assert.True(stream.BytesRead < stream.TotalLength);
    }

    [Fact]
    public async Task OversizedSseResponseIsRejectedEvenWhenEveryEventIsSmall()
    {
        var stream = new RepeatingPatternStream(": keep-alive\n"u8.ToArray(), totalLength: 100_000);
        await using var provider = new DeepSeekChatModelProvider(
            new TestCredentialStore("ds-large-sse-response-test"),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            }),
            new DeepSeekProviderOptions(
                RequestTimeout: TimeSpan.FromSeconds(5),
                MaxMessageCount: 10,
                MaxInputCharacters: 1_000,
                MaxOutputCharacters: 1_000,
                MaxErrorBodyCharacters: 100,
                MaxSseEventCharacters: 128,
                MaxSseResponseCharacters: 256));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(), (_, _) => ValueTask.CompletedTask));

        Assert.Equal(ChatModelErrorKind.InvalidResponse, exception.Error.Kind);
        Assert.Equal("deepseek.stream_response_too_large", exception.Error.Code);
        Assert.True(stream.BytesRead < stream.TotalLength);
    }

    [Fact]
    public async Task EmptyCredentialLeasesAreDisposedWithoutUsingTheNetwork()
    {
        var completionCredentials = new TestCredentialStore(string.Empty);
        var completionHandler = new StubHttpMessageHandler(_ =>
            throw new Xunit.Sdk.XunitException("Empty credentials must not reach the network."));
        await using var completionProvider = new DeepSeekChatModelProvider(
            completionCredentials,
            completionHandler);

        var completionError = await Assert.ThrowsAsync<ChatModelException>(() =>
            completionProvider.CompleteAsync(Request()));

        var healthCredentials = new TestCredentialStore(string.Empty);
        var healthHandler = new StubHttpMessageHandler(_ =>
            throw new Xunit.Sdk.XunitException("Empty credentials must not reach the network."));
        await using var healthProvider = new DeepSeekChatModelProvider(
            healthCredentials,
            healthHandler);
        var health = await healthProvider.CheckHealthAsync();

        Assert.Equal("deepseek.not_configured", completionError.Error.Code);
        Assert.True(completionCredentials.LastLeaseDisposed);
        Assert.Equal(ChatProviderHealthState.NotConfigured, health.State);
        Assert.True(healthCredentials.LastLeaseDisposed);
        Assert.Equal(0, completionHandler.SendCount);
        Assert.Equal(0, healthHandler.SendCount);
    }

    [Fact]
    public void UnreasonableProviderLimitsAreRejectedAtConstruction()
    {
        var defaults = DeepSeekProviderOptions.Default;
        DeepSeekProviderOptions[] invalidOptions =
        [
            defaults with { RequestTimeout = TimeSpan.FromHours(1) },
            defaults with { MaxMessageCount = int.MaxValue },
            defaults with { MaxInputCharacters = int.MaxValue },
            defaults with { MaxOutputCharacters = int.MaxValue },
            defaults with { MaxErrorBodyCharacters = int.MaxValue },
            defaults with { MaxSseEventCharacters = int.MaxValue },
            defaults with { MaxSseResponseCharacters = int.MaxValue }
        ];

        foreach (var options in invalidOptions)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new DeepSeekChatModelProvider(
                    new TestCredentialStore("ds-never-used"),
                    new StubHttpMessageHandler(_ =>
                        throw new Xunit.Sdk.XunitException("Invalid options must not reach the network.")),
                    options));
        }
    }

    private static async Task<OfflineSseFixtureObservation> ObserveOfflineSseFixtureAsync(
        string sse,
        params string[] restrictedSentinels)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        var responseShapes = new R3SafeResponseShapeCollector();
        await using var provider = new R3BudgetedChatModelProvider(
            new DeepSeekChatModelProvider(
                new TestCredentialStore("fixture-fake-credential"),
                new R3SafeResponseShapeTrackingHandler(handler, responseShapes)),
            DeepSeekChatModelProvider.ProModelId,
            new R3DeepSeekValidationBudget(
                MaxRequests: 1,
                MaxOutputTokens: 64,
                TotalTimeout: TimeSpan.FromSeconds(30),
                NoAutomaticRetry: true,
                NoFallback: true),
            responseShapes);
        var updates = new List<ChatStreamUpdate>();
        var callObservation = provider.PrepareNextCall(
            R3ValidationCallMode.StreamingCancellation);
        ChatModelResponse? response = null;
        ChatModelException? failure = null;
        try
        {
            response = await provider.CompleteAsync(
                Request(modelId: DeepSeekChatModelProvider.ProModelId),
                (update, _) =>
                {
                    updates.Add(update);
                    return ValueTask.CompletedTask;
                });
        }
        catch (ChatModelException exception)
        {
            failure = exception;
        }

        var surfacedText = string.Join(
            "\n",
            updates.Select(update => update.DeltaText)
                .Append(response?.Text ?? string.Empty)
                .Append(failure?.ToString() ?? string.Empty));
        return new OfflineSseFixtureObservation(
            ResponseReturned: response is not null,
            NonEmptyDeltaCount: updates.Count(update => !string.IsNullOrEmpty(update.DeltaText)),
            ValidProtocolDeltaCount: callObservation.DeltaCount,
            FinalUpdateObserved: updates.Any(update => update.IsFinal),
            FinishReason: response?.FinishReason,
            ErrorCode: failure?.Error.Code,
            HasUsage: response?.Usage is not null,
            HasProviderRequestId: !string.IsNullOrWhiteSpace(response?.Metadata.ProviderRequestId),
            RestrictedDataLeaked: restrictedSentinels.Any(sentinel =>
                surfacedText.Contains(sentinel, StringComparison.Ordinal)),
            FakeHandlerSendCount: handler.SendCount,
            ShapeEvidence: Assert.Single(provider.ResponseShapeEvidence));
    }

    private sealed record OfflineSseFixtureObservation(
        bool ResponseReturned,
        int NonEmptyDeltaCount,
        int ValidProtocolDeltaCount,
        bool FinalUpdateObserved,
        ChatFinishReason? FinishReason,
        string? ErrorCode,
        bool HasUsage,
        bool HasProviderRequestId,
        bool RestrictedDataLeaked,
        int FakeHandlerSendCount,
        R3SafeResponseShapeEvidence ShapeEvidence);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The expected test condition was not reached.");
    }

    [Fact]
    public void R3ValidationHarnessRequiresExplicitRealProviderAndEveryBudgetControl()
    {
        var disabled = R3DeepSeekValidationOptions.Parse([]);
        Assert.False(disabled.Requested);
        Assert.False(disabled.IsValid);
        Assert.False(disabled.CancellationOnly);
        Assert.Equal("real_provider_disabled", disabled.ErrorCode);

        var unbudgeted = R3DeepSeekValidationOptions.Parse(["--stage2-r3-deepseek"]);
        Assert.True(unbudgeted.Requested);
        Assert.False(unbudgeted.IsValid);
        Assert.Equal("real_provider_approval_missing", unbudgeted.ErrorCode);

        var budgetMissing = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--real-provider",
            "--expected-model=deepseek-v4-flash"
        ]);
        Assert.True(budgetMissing.Requested);
        Assert.False(budgetMissing.IsValid);
        Assert.Equal("real_provider_budget_missing", budgetMissing.ErrorCode);

        var approved = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--real-provider",
            "--expected-model=deepseek-v4-pro",
            "--max-requests=4",
            "--max-output-tokens=64",
            "--total-timeout-seconds=120",
            "--no-automatic-retry",
            "--no-fallback"
        ]);

        Assert.True(approved.Requested);
        Assert.True(approved.IsValid);
        Assert.False(approved.CancellationOnly);
        Assert.Equal(DeepSeekChatModelProvider.ProModelId, approved.ExpectedModelId);
        Assert.Null(approved.ErrorCode);
        Assert.Equal(4, approved.Budget!.MaxRequests);
        Assert.Equal(64, approved.Budget.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(120), approved.Budget.TotalTimeout);
        Assert.True(approved.Budget.NoAutomaticRetry);
        Assert.True(approved.Budget.NoFallback);
        Assert.True(approved.ExecutionPlan.RunHealth);
        Assert.True(approved.ExecutionPlan.RunOrdinaryChat);
        Assert.True(approved.ExecutionPlan.RunStreamingSuccess);
        Assert.True(approved.ExecutionPlan.RunStreamingCancellation);
        Assert.Equal(4, approved.ExecutionPlan.ExpectedProviderRequests);
    }

    [Fact]
    public void R3CancellationOnlyModeIsExplicitAndAcceptsOneRequestBudget()
    {
        var options = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--cancellation-only",
            "--real-provider",
            "--expected-model=deepseek-v4-flash",
            "--max-requests=1",
            "--max-output-tokens=256",
            "--total-timeout-seconds=600",
            "--no-automatic-retry",
            "--no-fallback"
        ]);

        Assert.True(options.Requested);
        Assert.True(options.IsValid);
        Assert.True(options.CancellationOnly);
        Assert.Equal(DeepSeekChatModelProvider.FlashModelId, options.ExpectedModelId);
        Assert.Null(options.ErrorCode);
        Assert.Equal(1, options.Budget!.MaxRequests);
        Assert.Equal(256, options.Budget.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(600), options.Budget.TotalTimeout);
        Assert.True(options.Budget.NoAutomaticRetry);
        Assert.True(options.Budget.NoFallback);
    }

    [Fact]
    public void R3ValidationHarnessRejectsMissingExpectedModelAtTheArgumentGate()
    {
        var options = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--real-provider",
            "--max-requests=4",
            "--max-output-tokens=64",
            "--total-timeout-seconds=120",
            "--no-automatic-retry",
            "--no-fallback"
        ]);

        Assert.True(options.Requested);
        Assert.False(options.IsValid);
        Assert.Equal("real_provider_expected_model_missing", options.ErrorCode);
        Assert.Null(options.ExpectedModelId);
        Assert.Null(options.Budget);
    }

    [Fact]
    public void R3ValidationHarnessRejectsRepeatedExpectedModelAtTheArgumentGate()
    {
        var options = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--real-provider",
            "--expected-model=deepseek-v4-pro",
            "--expected-model=deepseek-v4-flash",
            "--max-requests=4",
            "--max-output-tokens=64",
            "--total-timeout-seconds=120",
            "--no-automatic-retry",
            "--no-fallback"
        ]);

        Assert.True(options.Requested);
        Assert.False(options.IsValid);
        Assert.Equal("real_provider_expected_model_repeated", options.ErrorCode);
        Assert.Null(options.ExpectedModelId);
        Assert.Null(options.Budget);
    }

    [Fact]
    public void R3ValidationHarnessRejectsUnsupportedExpectedModelAtTheArgumentGate()
    {
        var options = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--real-provider",
            "--expected-model=deepseek-v3",
            "--max-requests=4",
            "--max-output-tokens=64",
            "--total-timeout-seconds=120",
            "--no-automatic-retry",
            "--no-fallback"
        ]);

        Assert.True(options.Requested);
        Assert.False(options.IsValid);
        Assert.Equal("real_provider_expected_model_invalid", options.ErrorCode);
        Assert.Null(options.ExpectedModelId);
        Assert.Null(options.Budget);
    }

    [Fact]
    public void R3SettingsModelMismatchFailsBeforeHealthChatOrProviderExecution()
    {
        var healthCalls = 0;
        var chatCalls = 0;
        var providerCalls = 0;

        var exception = Assert.Throws<R3ValidationFailureException>(() =>
        {
            R3ExpectedModelGate.RequireSettingsMatch(
                DeepSeekChatModelProvider.ProModelId,
                DeepSeekChatModelProvider.FlashModelId);
            healthCalls++;
            chatCalls++;
            providerCalls++;
        });

        Assert.Equal("r3_expected_model_mismatch", exception.Code);
        Assert.Equal(DeepSeekChatModelProvider.ProModelId, exception.ExpectedModelId);
        Assert.Equal(DeepSeekChatModelProvider.FlashModelId, exception.ActualModelId);
        Assert.Equal("settings", exception.ModelEvidenceLayer);
        Assert.Equal(0, healthCalls);
        Assert.Equal(0, chatCalls);
        Assert.Equal(0, providerCalls);
    }

    [Fact]
    public async Task R3FreshIsolationMaterializesAndReadsBackTheExpectedProRoute()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ScreenGuide.R3.IsolatedSettings.Tests.{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(root, "settings", "ai-settings.json");
        try
        {
            var options = R3DeepSeekValidationOptions.Parse(
            [
                "--stage2-r3-deepseek",
                "--real-provider",
                "--expected-model=deepseek-v4-pro",
                "--max-requests=4",
                "--max-output-tokens=64",
                "--total-timeout-seconds=120",
                "--no-automatic-retry",
                "--no-fallback"
            ]);
            Assert.True(options.IsValid);

            var evidence = await R3IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
                settingsPath,
                options.ExpectedModelId!);

            Assert.Equal(DeepSeekChatModelProvider.ProviderId, evidence.ProviderId);
            Assert.Equal(DeepSeekChatModelProvider.ProModelId, evidence.ModelId);
            using var store = new FileAiSettingsStore(
                settingsPath,
                new AiSettings(new ChatModelRoute("codex", "codex-default")));
            var settings = await store.LoadAsync();
            Assert.Equal(DeepSeekChatModelProvider.ProviderId, settings.DefaultChatRoute.ProviderId);
            Assert.Equal(DeepSeekChatModelProvider.ProModelId, settings.DefaultChatRoute.ModelId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task R3MissingIsolatedSettingsFailClosedBeforeSessionOrProviderWork()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ScreenGuide.R3.IsolatedSettings.Tests.{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(root, "settings", "ai-settings.json");
        var sessionCalls = 0;
        var providerCalls = 0;

        var exception = await Assert.ThrowsAsync<R3ValidationFailureException>(async () =>
        {
            _ = await R3IsolatedAiSettingsMaterializer.LoadAndVerifyAsync(
                settingsPath,
                DeepSeekChatModelProvider.ProModelId);
            sessionCalls++;
            providerCalls++;
        });

        Assert.Equal(R3IsolatedAiSettingsMaterializer.MissingErrorCode, exception.Code);
        Assert.Equal("settings", exception.ModelEvidenceLayer);
        Assert.Equal(0, sessionCalls);
        Assert.Equal(0, providerCalls);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("codex", "codex-default")]
    [InlineData("deepseek", DeepSeekChatModelProvider.FlashModelId)]
    [InlineData("deepseek", "deepseek-unsupported")]
    public async Task R3ExistingIsolatedRouteMismatchFailsClosedWithoutBeingOverwritten(
        string providerId,
        string modelId)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ScreenGuide.R3.IsolatedSettings.Tests.{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(root, "settings", "ai-settings.json");
        try
        {
            using (var store = new FileAiSettingsStore(
                       settingsPath,
                       new AiSettings(new ChatModelRoute("codex", "codex-default"))))
            {
                await store.SaveAsync(new AiSettings(new ChatModelRoute(providerId, modelId)));
            }

            var before = await File.ReadAllTextAsync(settingsPath);
            var exception = await Assert.ThrowsAsync<R3ValidationFailureException>(() =>
                R3IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
                    settingsPath,
                    DeepSeekChatModelProvider.ProModelId));
            var after = await File.ReadAllTextAsync(settingsPath);

            Assert.Equal(R3ExpectedModelGate.MismatchErrorCode, exception.Code);
            Assert.Equal("settings", exception.ModelEvidenceLayer);
            Assert.Equal(before, after);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task R3InvalidIsolatedSettingsFailClosedWithASafeStableCode()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ScreenGuide.R3.IsolatedSettings.Tests.{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(root, "settings", "ai-settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            await File.WriteAllTextAsync(settingsPath, "{ malformed");

            var exception = await Assert.ThrowsAsync<R3ValidationFailureException>(() =>
                R3IsolatedAiSettingsMaterializer.LoadAndVerifyAsync(
                    settingsPath,
                    DeepSeekChatModelProvider.ProModelId));

            Assert.Equal(R3IsolatedAiSettingsMaterializer.InvalidErrorCode, exception.Code);
            Assert.Equal("settings", exception.ModelEvidenceLayer);
            Assert.DoesNotContain("malformed", exception.Message);
            Assert.DoesNotContain(settingsPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task R3IsolatedSettingsRequireNoCredentialStoreAndUseOnlyCanonicalRouteShape()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ScreenGuide.R3.IsolatedSettings.Tests.{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(root, "settings", "ai-settings.json");
        try
        {
            _ = await R3IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
                settingsPath,
                DeepSeekChatModelProvider.ProModelId);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            var rootProperties = document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var settings = document.RootElement.GetProperty("settings");
            var settingsProperties = settings.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var route = settings.GetProperty("defaultChatRoute");
            var routeProperties = route.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var serialized = document.RootElement.GetRawText();

            Assert.Equal(["schemaVersion", "settings"], rootProperties);
            Assert.Equal(["defaultChatRoute"], settingsProperties);
            Assert.Equal(["modelId", "providerId"], routeProperties);
            Assert.Equal("deepseek", route.GetProperty("providerId").GetString());
            Assert.Equal(
                DeepSeekChatModelProvider.ProModelId,
                route.GetProperty("modelId").GetString());
            Assert.DoesNotContain("apiKey", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authorization", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential", serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task R3IsolatedCredentialWiringObtainsAndDisposesTheFakeSecureLease()
    {
        const string sentinel = "fake-r3-lease-sentinel";
        var source = new TestCredentialStore(sentinel);
        var credentialStore = R3SecureCredentialLeaseBinding.CreateReadOnly(source);
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "id": "fake-r3-response",
              "choices": [
                {
                  "message": { "role": "assistant", "content": "ok" },
                  "finish_reason": "stop"
                }
              ]
            }
            """));
        await using var provider = new DeepSeekChatModelProvider(credentialStore, handler);

        var response = await provider.CompleteAsync(Request(
            modelId: DeepSeekChatModelProvider.ProModelId));

        Assert.Equal("ok", response.Text);
        Assert.Equal(1, source.OpenLeaseCount);
        Assert.True(source.LastLeaseDisposed);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public void R3CanonicalCredentialRootIsDerivedInternallyFromCurrentUserLocalData()
    {
        var localApplicationData = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "R3-Canonical-LocalAppData",
            Guid.NewGuid().ToString("N")));

        var root = R3CanonicalCredentialStoreRoot.Resolve(localApplicationData);

        Assert.Equal(
            Path.Combine(localApplicationData, "ScreenGuide", "V01", "secrets"),
            root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-local-app-data")]
    [InlineData("\\\\server\\share\\LocalAppData")]
    [InlineData("\\\\?\\C:\\LocalAppData")]
    [InlineData("\\\\.\\C:\\LocalAppData")]
    public void R3CanonicalCredentialRootRejectsMissingNonLocalAndDevicePaths(string source)
    {
        var exception = Assert.Throws<R3ValidationFailureException>(() =>
            R3CanonicalCredentialStoreRoot.Resolve(source));

        Assert.Equal("r3_canonical_credential_root_unavailable", exception.Code);
    }

    [Fact]
    public async Task R3MissingSecureLeaseFailsClosedBeforeHttpWithoutRetryOrFallback()
    {
        var source = new TestCredentialStore();
        var credentialStore = R3SecureCredentialLeaseBinding.CreateReadOnly(source);
        var handler = new StubHttpMessageHandler(_ =>
            throw new Xunit.Sdk.XunitException("Missing lease must not reach HTTP."));
        await using var inner = new DeepSeekChatModelProvider(credentialStore, handler);
        await using var provider = new R3BudgetedChatModelProvider(
            inner,
            DeepSeekChatModelProvider.ProModelId,
            new R3DeepSeekValidationBudget(
                MaxRequests: 1,
                MaxOutputTokens: 64,
                TotalTimeout: TimeSpan.FromSeconds(30),
                NoAutomaticRetry: true,
                NoFallback: true));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(modelId: DeepSeekChatModelProvider.ProModelId)));

        Assert.Equal(ChatModelErrorKind.Configuration, exception.Error.Kind);
        Assert.Equal("deepseek.not_configured", exception.Error.Code);
        Assert.Equal(1, source.OpenLeaseCount);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(1, provider.RequestCount);
        Assert.Single(provider.RequestIdentities);
    }

    [Fact]
    public async Task R3UnavailableSecureLeaseFailsClosedWithoutLeakingOrSendingHttp()
    {
        const string sentinel = "fake-unavailable-credential-sentinel";
        var source = new ThrowingCredentialStore(sentinel);
        var credentialStore = R3SecureCredentialLeaseBinding.CreateReadOnly(source);
        var handler = new StubHttpMessageHandler(_ =>
            throw new Xunit.Sdk.XunitException("Unavailable lease must not reach HTTP."));
        await using var inner = new DeepSeekChatModelProvider(credentialStore, handler);
        await using var provider = new R3BudgetedChatModelProvider(
            inner,
            DeepSeekChatModelProvider.ProModelId,
            new R3DeepSeekValidationBudget(
                MaxRequests: 1,
                MaxOutputTokens: 64,
                TotalTimeout: TimeSpan.FromSeconds(30),
                NoAutomaticRetry: true,
                NoFallback: true));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(modelId: DeepSeekChatModelProvider.ProModelId)));

        Assert.Equal(ChatModelErrorKind.Configuration, exception.Error.Kind);
        Assert.Equal("deepseek.not_configured", exception.Error.Code);
        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, source.OpenLeaseCount);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(1, provider.RequestCount);
        Assert.Single(provider.RequestIdentities);
    }

    [Fact]
    public async Task R3ExpiredSecureLeaseFailsClosedAndDisposesWithoutLeakingOrSendingHttp()
    {
        const string sentinel = "fake-expired-credential-sentinel";
        var source = new ExpiredCredentialStore(sentinel);
        var credentialStore = R3SecureCredentialLeaseBinding.CreateReadOnly(source);
        var handler = new StubHttpMessageHandler(_ =>
            throw new Xunit.Sdk.XunitException("Expired lease must not reach HTTP."));
        await using var inner = new DeepSeekChatModelProvider(credentialStore, handler);
        await using var provider = new R3BudgetedChatModelProvider(
            inner,
            DeepSeekChatModelProvider.ProModelId,
            new R3DeepSeekValidationBudget(
                MaxRequests: 1,
                MaxOutputTokens: 64,
                TotalTimeout: TimeSpan.FromSeconds(30),
                NoAutomaticRetry: true,
                NoFallback: true));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(modelId: DeepSeekChatModelProvider.ProModelId)));

        Assert.Equal(ChatModelErrorKind.Configuration, exception.Error.Kind);
        Assert.Equal("deepseek.not_configured", exception.Error.Code);
        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, source.OpenLeaseCount);
        Assert.True(source.LastLeaseDisposed);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(1, provider.RequestCount);
        Assert.Single(provider.RequestIdentities);
    }

    [Fact]
    public async Task R3CredentialLeaseBindingCannotWriteOrDeleteTheSecureSource()
    {
        var source = new TestCredentialStore("fake-r3-lease-sentinel");
        var credentialStore = R3SecureCredentialLeaseBinding.CreateReadOnly(source);

        var setFailure = await Assert.ThrowsAsync<R3ValidationFailureException>(() =>
            credentialStore.SetAsync("deepseek", "replacement".AsMemory()));
        var deleteFailure = await Assert.ThrowsAsync<R3ValidationFailureException>(() =>
            credentialStore.DeleteAsync("deepseek"));

        Assert.Equal("r3_credential_store_read_only", setFailure.Code);
        Assert.Equal("r3_credential_store_read_only", deleteFailure.Code);
        Assert.Equal(0, source.SetCount);
        Assert.Equal(0, source.DeleteCount);
    }

    [Fact]
    public async Task R3FakeCredentialNeverPersistsInIsolatedArtifactsArgumentsOrEvidence()
    {
        const string sentinel = "fake-r3-lease-sentinel";
        var isolatedRoot = Path.Combine(
            Path.GetTempPath(),
            $"ScreenGuide.R3.CredentialPersistence.Tests.{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(isolatedRoot, "settings", "ai-settings.json");
        try
        {
            var settingsEvidence =
                await R3IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
                    settingsPath,
                    DeepSeekChatModelProvider.ProModelId);
            var source = new TestCredentialStore(sentinel);
            var credentialStore = R3SecureCredentialLeaseBinding.CreateReadOnly(source);
            var handler = new StubHttpMessageHandler(_ => JsonResponse("""
                {
                  "id": "fake-r3-response",
                  "choices": [
                    {
                      "message": { "role": "assistant", "content": "ok" },
                      "finish_reason": "stop"
                    }
                  ]
                }
                """));
            await using var provider = new DeepSeekChatModelProvider(credentialStore, handler);

            var response = await provider.CompleteAsync(Request(
                modelId: DeepSeekChatModelProvider.ProModelId));
            var safeEvidence = JsonSerializer.Serialize(new
            {
                SettingsProviderId = settingsEvidence.ProviderId,
                SettingsModelId = settingsEvidence.ModelId,
                response.FinishReason,
                ResponseProviderId = response.Metadata.ProviderId,
                ResponseModelId = response.Metadata.ModelId
            });
            var arguments = new[] { "--stage2-r3-deepseek", "--expected-model=deepseek-v4-pro" };
            var isolatedFiles = Directory.EnumerateFiles(
                    isolatedRoot,
                    "*",
                    SearchOption.AllDirectories)
                .ToArray();

            Assert.Equal([settingsPath], isolatedFiles);
            Assert.DoesNotContain(sentinel, safeEvidence, StringComparison.Ordinal);
            Assert.DoesNotContain(arguments, argument =>
                argument.Contains(sentinel, StringComparison.Ordinal));
            Assert.All(isolatedFiles, path =>
                Assert.DoesNotContain(
                    sentinel,
                    File.ReadAllText(path),
                    StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(isolatedRoot, "secrets")));
            Assert.False(Directory.Exists(Path.Combine(isolatedRoot, "state")));
            Assert.False(Directory.Exists(Path.Combine(isolatedRoot, "logs")));
            Assert.False(Directory.Exists(Path.Combine(isolatedRoot, "evidence")));
            Assert.Equal(1, source.OpenLeaseCount);
            Assert.True(source.LastLeaseDisposed);
        }
        finally
        {
            if (Directory.Exists(isolatedRoot))
            {
                Directory.Delete(isolatedRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(DeepSeekChatModelProvider.FlashModelId)]
    [InlineData(DeepSeekChatModelProvider.ProModelId)]
    public void R3SettingsModelGateAcceptsAnExactSupportedMatch(string modelId)
    {
        R3ExpectedModelGate.RequireMatch(modelId, modelId);
    }

    [Fact]
    public void R3InvocationModelMismatchCannotSatisfyTheExpectedModelAuditGate()
    {
        var exception = Assert.Throws<R3ValidationFailureException>(() =>
            R3ExpectedModelGate.RequireInvocationMatch(
                DeepSeekChatModelProvider.ProModelId,
                DeepSeekChatModelProvider.FlashModelId));

        Assert.Equal("r3_expected_model_mismatch", exception.Code);
        Assert.Equal(DeepSeekChatModelProvider.ProModelId, exception.ExpectedModelId);
        Assert.Equal(DeepSeekChatModelProvider.FlashModelId, exception.ActualModelId);
        Assert.Equal("invocation", exception.ModelEvidenceLayer);
    }

    [Theory]
    [InlineData(DeepSeekChatModelProvider.FlashModelId)]
    [InlineData(DeepSeekChatModelProvider.ProModelId)]
    public void R3ExpectedModelGateAcceptsAllThreeMatchingEvidenceLayers(string modelId)
    {
        R3ExpectedModelGate.RequireSettingsMatch(modelId, modelId);
        R3ExpectedModelGate.RequireFrozenRouteMatch(modelId, modelId);
        R3ExpectedModelGate.RequireInvocationMatch(modelId, modelId);
    }

    [Theory]
    [InlineData(DeepSeekChatModelProvider.FlashModelId)]
    [InlineData(DeepSeekChatModelProvider.ProModelId)]
    public void R3ExpectedModelEvidenceKeepsAllThreeActualLayersDistinct(string modelId)
    {
        var evidence = new R3ExpectedModelEvidence(
            ExpectedModelId: modelId,
            SettingsModelId: modelId,
            FrozenRouteModelIds: [modelId],
            InvocationModelIds: [modelId]);

        Assert.Equal(modelId, evidence.ExpectedModelId);
        Assert.Equal(modelId, evidence.SettingsModelId);
        Assert.Equal([modelId], evidence.FrozenRouteModelIds);
        Assert.Equal([modelId], evidence.InvocationModelIds);
        Assert.True(evidence.IsCompleteMatch);
    }

    [Fact]
    public void R3ExpectedModelEvidenceCannotReportPassForAnInvocationMismatch()
    {
        var evidence = new R3ExpectedModelEvidence(
            ExpectedModelId: DeepSeekChatModelProvider.ProModelId,
            SettingsModelId: DeepSeekChatModelProvider.ProModelId,
            FrozenRouteModelIds: [DeepSeekChatModelProvider.ProModelId],
            InvocationModelIds: [DeepSeekChatModelProvider.FlashModelId]);

        Assert.False(evidence.IsCompleteMatch);
    }

    [Fact]
    public void R3FailureEvidencePreservesStreamEmptyWithoutExposingSensitiveText()
    {
        const string sensitiveText = "fake-response-body-must-not-appear";
        var tracker = new R3RunEvidenceTracker(DeepSeekChatModelProvider.ProModelId);
        tracker.BeginConfiguration();
        tracker.RecordSettingsModel(DeepSeekChatModelProvider.ProModelId);
        tracker.BeginHealth();
        tracker.BeginOrdinaryChat();
        tracker.BeginStreamingSuccess();
        var failure = new ChatModelException(
            DeepSeekChatModelProvider.ProviderId,
            DeepSeekChatModelProvider.ProModelId,
            new ChatModelError(
                ChatModelErrorKind.InvalidResponse,
                "deepseek.stream_empty",
                sensitiveText));
        var responseShape = new R3SafeResponseShapeEvidence(
            SseEventCount: 2,
            DeltaCount: 1,
            ContentAppeared: false,
            ContentCharacterCount: 0,
            ReasoningContentAppeared: true,
            ReasoningContentCharacterCount: 24,
            DoneAppeared: true,
            FinishReasonAppeared: true,
            FinishReasonCategory: "stop",
            StableErrorKind: "invalid_response",
            ProviderDiagnosticCode: "deepseek.stream_empty");

        var evidence = R3FailureEvidenceReporter.Create(
            failure,
            tracker,
            requestCount: 3,
            frozenRouteModelIds: [DeepSeekChatModelProvider.ProModelId],
            responseShapeEvidence: [responseShape]);
        var json = JsonSerializer.Serialize(evidence);

        Assert.Equal("streaming", evidence.FailureStage);
        Assert.Equal("invalid_response", evidence.ErrorCode);
        Assert.Equal("deepseek.stream_empty", evidence.ProviderDiagnosticCode);
        Assert.Equal(DeepSeekChatModelProvider.ProModelId, evidence.ExpectedModelId);
        Assert.Equal(DeepSeekChatModelProvider.ProModelId, evidence.ActualModelId);
        Assert.Equal(3, evidence.RequestCount);
        Assert.True(evidence.HealthExecuted);
        Assert.True(evidence.OrdinaryChatExecuted);
        Assert.True(evidence.StreamingSuccessExecuted);
        Assert.False(evidence.StreamingCancellationExecuted);
        Assert.Equal(DeepSeekChatModelProvider.ProModelId, evidence.ModelEvidence.SettingsModelId);
        Assert.Equal(
            [DeepSeekChatModelProvider.ProModelId],
            evidence.ModelEvidence.FrozenRouteModelIds);
        Assert.Equal([responseShape], evidence.ResponseShapeEvidence);
        Assert.Contains("\"ReasoningContentCharacterCount\":24", json, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveText, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void R3FailureEvidenceKeepsPublicAndProviderCodesInSeparateFields()
    {
        var tracker = new R3RunEvidenceTracker(DeepSeekChatModelProvider.ProModelId);
        tracker.BeginConfiguration();
        tracker.RecordSettingsModel(DeepSeekChatModelProvider.ProModelId);
        tracker.BeginStreamingCancellation();
        tracker.RecordInvocationModel(DeepSeekChatModelProvider.ProModelId);
        var failure = new R3ValidationFailureException(
            "invalid_response",
            "deepseek.stream_empty");

        var evidence = R3FailureEvidenceReporter.Create(
            failure,
            tracker,
            requestCount: 1,
            frozenRouteModelIds: [DeepSeekChatModelProvider.ProModelId]);

        Assert.Equal("cancellation", evidence.FailureStage);
        Assert.Equal("invalid_response", evidence.ErrorCode);
        Assert.Equal("deepseek.stream_empty", evidence.ProviderDiagnosticCode);
        Assert.Equal(DeepSeekChatModelProvider.ProModelId, evidence.ActualModelId);
        Assert.Equal(
            [DeepSeekChatModelProvider.ProModelId],
            evidence.ModelEvidence.InvocationModelIds);
        Assert.True(evidence.StreamingCancellationExecuted);
    }

    [Fact]
    public void R3FinalResultPreservesAllThreeCancelledTerminalStates()
    {
        var terminalEvidence = new R3CancellationTerminalStateEvidence(
            SessionTurnState: "Cancelled",
            ConversationTurnState: "Cancelled",
            AiInvocationState: "Cancelled");
        R3CancellationTerminalStateGate.RequireCancelled(terminalEvidence);

        var result = new R3DeepSeekValidationResult(
            Stage: "complete",
            Passed: true,
            ProviderId: DeepSeekChatModelProvider.ProviderId,
            ModelId: DeepSeekChatModelProvider.ProModelId,
            Requests: 1,
            HealthPassed: false,
            OrdinaryChatPassed: false,
            StreamingPassed: false,
            StreamingDeltaCount: 0,
            CancellationPassed: true,
            CancellationDeltaCount: 2,
            LateDeltaRejected: true,
            LateFinalRejected: true,
            LateSuccessRejected: true,
            AuditPassed: true,
            NoAutomaticRetry: true,
            NoFallback: true,
            ElapsedMilliseconds: 1,
            ErrorCode: null)
        {
            CancellationTerminalEvidence = terminalEvidence
        };
        var json = JsonSerializer.Serialize(result);

        Assert.Same(terminalEvidence, result.CancellationTerminalEvidence);
        Assert.Contains("\"SessionTurnState\":\"Cancelled\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ConversationTurnState\":\"Cancelled\"", json, StringComparison.Ordinal);
        Assert.Contains("\"AiInvocationState\":\"Cancelled\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Completed", "Cancelled", "Cancelled")]
    [InlineData("Cancelled", "Succeeded", "Cancelled")]
    [InlineData("Cancelled", "Cancelled", "Failed")]
    public void R3CancellationGateRejectsEachNonCancelledTerminalStateIndependently(
        string sessionTurnState,
        string conversationTurnState,
        string aiInvocationState)
    {
        var exception = Assert.Throws<R3ValidationFailureException>(() =>
            R3CancellationTerminalStateGate.RequireCancelled(new(
                sessionTurnState,
                conversationTurnState,
                aiInvocationState)));

        Assert.Equal("r3_cancellation_terminal_mismatch", exception.Code);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void R3CancellationOnlyModeRejectsMoreThanOneRequestBeforeExecution(
        int maxRequests)
    {
        var options = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--cancellation-only",
            "--real-provider",
            "--expected-model=deepseek-v4-flash",
            $"--max-requests={maxRequests}",
            "--max-output-tokens=256",
            "--total-timeout-seconds=600",
            "--no-automatic-retry",
            "--no-fallback"
        ]);

        Assert.True(options.Requested);
        Assert.False(options.IsValid);
        Assert.True(options.CancellationOnly);
        Assert.Equal("real_provider_budget_out_of_range", options.ErrorCode);
        Assert.Null(options.Budget);
    }

    [Fact]
    public void R3CancellationOnlyPlanSkipsReusableChecksAndRunsOneCancellation()
    {
        var options = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--cancellation-only",
            "--real-provider",
            "--expected-model=deepseek-v4-flash",
            "--max-requests=1",
            "--max-output-tokens=128",
            "--total-timeout-seconds=120",
            "--no-automatic-retry",
            "--no-fallback"
        ]);

        var plan = options.ExecutionPlan;

        Assert.False(plan.RunHealth);
        Assert.False(plan.RunOrdinaryChat);
        Assert.False(plan.RunStreamingSuccess);
        Assert.True(plan.RunStreamingCancellation);
        Assert.Equal(1, plan.ExpectedProviderRequests);
    }

    [Theory]
    [InlineData("--max-requests=5", "--max-output-tokens=256")]
    [InlineData("--max-requests=8", "--max-output-tokens=256")]
    [InlineData("--max-requests=4", "--max-output-tokens=257")]
    [InlineData("--max-requests=4", "--max-output-tokens=1024")]
    public void R3ValidationHarnessRejectsBudgetsAboveTheApprovedRealProviderLimits(
        string requestsArgument,
        string outputTokensArgument)
    {
        var options = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--real-provider",
            "--expected-model=deepseek-v4-flash",
            requestsArgument,
            outputTokensArgument,
            "--total-timeout-seconds=120",
            "--no-automatic-retry",
            "--no-fallback"
        ]);

        Assert.True(options.Requested);
        Assert.False(options.IsValid);
        Assert.Equal("real_provider_budget_out_of_range", options.ErrorCode);
        Assert.Null(options.Budget);
    }

    [Theory]
    [InlineData(5, 256)]
    [InlineData(8, 256)]
    [InlineData(4, 257)]
    [InlineData(4, 1024)]
    public async Task R3BudgetedProviderRejectsInternalBudgetsAboveTheApprovedLimits(
        int maxRequests,
        int maxOutputTokens)
    {
        await using var inner = new RecordingChatModelProvider();

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new R3BudgetedChatModelProvider(
                inner,
                DeepSeekChatModelProvider.FlashModelId,
                new R3DeepSeekValidationBudget(
                    maxRequests,
                    maxOutputTokens,
                    TimeSpan.FromSeconds(120),
                    NoAutomaticRetry: true,
                    NoFallback: true)));

        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task R3BudgetedProviderRejectsFrozenRouteModelDriftBeforeReserveOrInnerCall()
    {
        await using var inner = new RecordingChatModelProvider();
        await using var provider = new R3BudgetedChatModelProvider(
            inner,
            DeepSeekChatModelProvider.ProModelId,
            new R3DeepSeekValidationBudget(
                MaxRequests: 1,
                MaxOutputTokens: 64,
                TotalTimeout: TimeSpan.FromSeconds(30),
                NoAutomaticRetry: true,
                NoFallback: true));

        var exception = await Assert.ThrowsAsync<R3ValidationFailureException>(() =>
            provider.CompleteAsync(Request(
                modelId: DeepSeekChatModelProvider.FlashModelId)));

        Assert.Equal("r3_expected_model_mismatch", exception.Code);
        Assert.Equal(DeepSeekChatModelProvider.ProModelId, exception.ExpectedModelId);
        Assert.Equal(DeepSeekChatModelProvider.FlashModelId, exception.ActualModelId);
        Assert.Equal("frozen_route", exception.ModelEvidenceLayer);
        Assert.Equal(0, provider.RequestCount);
        Assert.Empty(provider.RequestIdentities);
        Assert.Empty(inner.Requests);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 256)]
    public async Task R3ValidationHarnessAcceptsTheApprovedBudgetBoundaries(
        int maxRequests,
        int maxOutputTokens)
    {
        var options = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--real-provider",
            "--expected-model=deepseek-v4-flash",
            $"--max-requests={maxRequests}",
            $"--max-output-tokens={maxOutputTokens}",
            "--total-timeout-seconds=120",
            "--no-automatic-retry",
            "--no-fallback"
        ]);

        Assert.True(options.IsValid);
        Assert.Equal(maxRequests, options.Budget!.MaxRequests);
        Assert.Equal(maxOutputTokens, options.Budget.MaxOutputTokens);

        await using var inner = new RecordingChatModelProvider();
        await using var provider = new R3BudgetedChatModelProvider(
            inner,
            options.ExpectedModelId!,
            options.Budget);
        _ = await provider.CompleteAsync(Request(
            options: new ChatModelOptions(MaxOutputTokens: 999)));

        Assert.Equal(maxOutputTokens, Assert.Single(inner.Requests).Options?.MaxOutputTokens);
    }

    [Fact]
    public async Task R3ValidationHarnessCapsOutputTokensAndRejectsRequestsBeyondTheApprovedBudget()
    {
        await using var inner = new RecordingChatModelProvider();
        await using var provider = new R3BudgetedChatModelProvider(
            inner,
            DeepSeekChatModelProvider.FlashModelId,
            new R3DeepSeekValidationBudget(
                MaxRequests: 1,
                MaxOutputTokens: 17,
                TotalTimeout: TimeSpan.FromSeconds(30),
                NoAutomaticRetry: true,
                NoFallback: true));

        _ = await provider.CompleteAsync(Request(
            options: new ChatModelOptions(MaxOutputTokens: 999)));

        Assert.Equal(17, Assert.Single(inner.Requests).Options?.MaxOutputTokens);
        var exception = await Assert.ThrowsAsync<R3ValidationBudgetExceededException>(() =>
            provider.CompleteAsync(Request(turnId: Guid.NewGuid())));
        Assert.Equal("r3_request_budget_exhausted", exception.Code);
        Assert.Single(inner.Requests);
        Assert.Equal(1, provider.RequestCount);
    }

    [Fact]
    public async Task R3CancellationOnlyModeUsesOneInvocationAndOneCancelWithoutLateOutput()
    {
        var options = R3DeepSeekValidationOptions.Parse(
        [
            "--stage2-r3-deepseek",
            "--cancellation-only",
            "--real-provider",
            "--expected-model=deepseek-v4-flash",
            "--max-requests=1",
            "--max-output-tokens=32",
            "--total-timeout-seconds=30",
            "--no-automatic-retry",
            "--no-fallback"
        ]);
        await using var inner = new CancellableStreamingChatModelProvider();
        await using var provider = new R3BudgetedChatModelProvider(
            inner,
            options.ExpectedModelId!,
            options.Budget!);
        var turnId = Guid.NewGuid();
        var observation = provider.PrepareNextCall(R3ValidationCallMode.StreamingCancellation);

        var completion = provider.CompleteAsync(Request(turnId: turnId));
        await observation.AtLeastTwoDeltas.WaitAsync(TimeSpan.FromSeconds(2));
        await provider.CancelAsync(turnId);
        var exception = await Assert.ThrowsAsync<ChatModelException>(() => completion);
        await observation.Terminal.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ChatModelErrorKind.Cancelled, exception.Error.Kind);
        Assert.Equal(2, observation.DeltaCount);
        Assert.Equal(2, observation.DeltaCountWhenCancellationCompleted);
        Assert.False(observation.FinalUpdateObserved);
        Assert.False(observation.ResponseReturned);
        Assert.Equal(1, provider.RequestCount);
        Assert.Single(provider.RequestIdentities);
        Assert.Equal(1, inner.CompleteCount);
        Assert.Equal(0, inner.HealthCount);
        Assert.Equal(1, inner.CancelCount);
    }

    [Theory]
    [InlineData("Cancelled", "deepseek.cancelled", false, false, true)]
    [InlineData("Cancelled", "cancelled", false, false, false)]
    [InlineData("Cancelled", "unrelated.cancelled", false, false, false)]
    [InlineData("Succeeded", "deepseek.cancelled", false, false, false)]
    [InlineData("Failed", "deepseek.cancelled", false, false, false)]
    [InlineData("Cancelled", "deepseek.cancelled", true, false, false)]
    [InlineData("Cancelled", "deepseek.cancelled", false, true, false)]
    public void R3CancellationAuditGateKeepsBusinessTerminalAndProviderDiagnosticSeparate(
        string terminalState,
        string diagnosticCode,
        bool hasUsage,
        bool hasProviderRequestId,
        bool expected)
    {
        var evidence = new R3DeepSeekCancellationAuditEvidence(
            terminalState,
            diagnosticCode,
            hasUsage,
            hasProviderRequestId);

        Assert.Equal(expected, R3DeepSeekCancellationAuditGate.IsSatisfied(evidence));
    }

    [Fact]
    public async Task R3CancellationPreconditionCancelsExactlyOnceWhenTwoDeltasArriveFirst()
    {
        var terminal = new TaskCompletionSource<R3CancellationTerminalEvidence>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCount = 0;

        var result = await R3CancellationPreconditionCoordinator.WaitAsync(
            Task.CompletedTask,
            terminal.Task,
            timeout.Task,
            () =>
            {
                cancelCount++;
                return Task.CompletedTask;
            });

        Assert.Equal(R3CancellationPreconditionDisposition.CancelRequested, result.Disposition);
        Assert.Equal(1, cancelCount);
        Assert.Null(result.TerminalEvidence);
    }

    [Fact]
    public async Task R3CancellationPreconditionReturnsEarlyFailedEvidenceWithoutCancelling()
    {
        var result = await RunTerminalFirstAsync(new R3CancellationTerminalEvidence(
            "Failed",
            "Failed",
            "Failed",
            "invalid_response",
            "deepseek.stream_empty"));

        Assert.Equal(R3CancellationPreconditionDisposition.TerminalFailed, result.Result.Disposition);
        Assert.Equal(0, result.CancelCount);
        Assert.Equal("invalid_response", result.Result.TerminalEvidence?.PublicFailureCode);
        Assert.Equal("deepseek.stream_empty", result.Result.TerminalEvidence?.ProviderDiagnosticCode);
    }

    [Fact]
    public async Task R3CancellationPreconditionRejectsEarlySuccessWithoutCancelling()
    {
        var result = await RunTerminalFirstAsync(new R3CancellationTerminalEvidence(
            "Completed",
            "Succeeded",
            "Succeeded",
            PublicFailureCode: null,
            ProviderDiagnosticCode: null));

        Assert.Equal(
            R3CancellationPreconditionDisposition.TerminalSucceeded,
            result.Result.Disposition);
        Assert.Equal(0, result.CancelCount);
    }

    [Fact]
    public async Task R3CancellationPreconditionAcceptsAnAlreadyCancelledTerminalForVerification()
    {
        var result = await RunTerminalFirstAsync(new R3CancellationTerminalEvidence(
            "Cancelled",
            "Cancelled",
            "Cancelled",
            "cancelled",
            "deepseek.cancelled"));

        Assert.Equal(
            R3CancellationPreconditionDisposition.TerminalCancelled,
            result.Result.Disposition);
        Assert.Equal(0, result.CancelCount);
    }

    [Fact]
    public async Task R3CancellationPreconditionTimesOutOnlyWithoutDeltasOrTerminalState()
    {
        var deltas = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<R3CancellationTerminalEvidence>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCount = 0;

        var result = await R3CancellationPreconditionCoordinator.WaitAsync(
            deltas.Task,
            terminal.Task,
            Task.CompletedTask,
            () =>
            {
                cancelCount++;
                return Task.CompletedTask;
            });

        Assert.Equal(R3CancellationPreconditionDisposition.TimedOut, result.Disposition);
        Assert.Equal(0, cancelCount);
    }

    private static async Task<(
        R3CancellationPreconditionResult Result,
        int CancelCount)> RunTerminalFirstAsync(R3CancellationTerminalEvidence evidence)
    {
        var deltas = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<R3CancellationTerminalEvidence>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCount = 0;
        var completion = R3CancellationPreconditionCoordinator.WaitAsync(
            deltas.Task,
            terminal.Task,
            timeout.Task,
            () =>
            {
                cancelCount++;
                return Task.CompletedTask;
            });

        terminal.SetResult(evidence);
        timeout.SetResult();
        return (await completion, cancelCount);
    }

    private static ChatModelRequest Request(
        string modelId = DeepSeekChatModelProvider.FlashModelId,
        IReadOnlyList<ChatMessage>? messages = null,
        ChatResponseFormat? responseFormat = null,
        Guid? turnId = null,
        ChatModelOptions? options = null) =>
        new(
            Guid.NewGuid(),
            turnId ?? Guid.NewGuid(),
            modelId,
            string.Empty,
            messages ?? [new ChatMessage(ChatMessageRole.User, "你好")],
            Options: options,
            ResponseFormat: responseFormat);

    private sealed class RecordingChatModelProvider : IChatModelProvider
    {
        public List<ChatModelRequest> Requests { get; } = [];

        public ChatProviderDescriptor Descriptor { get; } = new(
            DeepSeekChatModelProvider.ProviderId,
            "DeepSeek",
            DeepSeekChatModelProvider.DataDestination,
            SendsDataOffDevice: true,
            [new ChatModelDescriptor(
                DeepSeekChatModelProvider.FlashModelId,
                "DeepSeek V4 Flash",
                ChatModelCapabilities.Streaming)]);

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatModelResponse(
                "ok",
                ChatFinishReason.Stop,
                new ChatModelUsage(1, 1, 2),
                new ChatProviderMetadata(
                    Descriptor.ProviderId,
                    request.ModelId,
                    "request-id",
                    Descriptor.DataDestination)));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatProviderHealth(
                Descriptor.ProviderId,
                ChatProviderHealthState.Healthy,
                IsConfigured: true,
                "ok",
                DateTimeOffset.UtcNow));

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellableStreamingChatModelProvider : IChatModelProvider
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChatProviderDescriptor Descriptor { get; } = new(
            DeepSeekChatModelProvider.ProviderId,
            "DeepSeek",
            DeepSeekChatModelProvider.DataDestination,
            SendsDataOffDevice: true,
            [new ChatModelDescriptor(
                DeepSeekChatModelProvider.FlashModelId,
                "DeepSeek V4 Flash",
                ChatModelCapabilities.Streaming)]);

        public int CompleteCount { get; private set; }

        public int HealthCount { get; private set; }

        public int CancelCount { get; private set; }

        public async Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            Assert.NotNull(streamCallback);
            try
            {
                await streamCallback(new ChatStreamUpdate(1, "一"), _cancellation.Token);
                await streamCallback(new ChatStreamUpdate(2, "二"), _cancellation.Token);
                await Task.Delay(Timeout.InfiniteTimeSpan, _cancellation.Token);
                throw new InvalidOperationException("Cancellation was not observed.");
            }
            catch (OperationCanceledException)
            {
                throw new ChatModelException(
                    Descriptor.ProviderId,
                    request.ModelId,
                    new ChatModelError(
                        ChatModelErrorKind.Cancelled,
                        "deepseek.cancelled",
                        "cancelled"));
            }
            finally
            {
                _completed.TrySetResult();
            }
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default)
        {
            HealthCount++;
            throw new NotSupportedException();
        }

        public async Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
        {
            CancelCount++;
            _cancellation.Cancel();
            await _completed.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _cancellation.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            : this((request, _) => Task.FromResult(responseFactory(request)))
        {
        }

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) =>
            _responseFactory = responseFactory;

        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return _responseFactory(request, cancellationToken);
        }
    }

    private sealed class TestCredentialStore(string? secret = null) : IProviderCredentialStore
    {
        public bool LastLeaseDisposed { get; private set; }

        public int OpenLeaseCount { get; private set; }

        public int SetCount { get; private set; }

        public int DeleteCount { get; private set; }

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                secret is null ? ProviderCredentialState.Missing : ProviderCredentialState.Configured,
                secret is null ? null : DateTimeOffset.UtcNow));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default)
        {
            SetCount++;
            return Task.CompletedTask;
        }

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            OpenLeaseCount++;
            return ValueTask.FromResult<IProviderCredentialLease?>(
                secret is null ? null : new TestLease(secret, () => LastLeaseDisposed = true));
        }

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return Task.FromResult(false);
        }

        private sealed class TestLease(string secret, Action disposed) : IProviderCredentialLease
        {
            public ReadOnlyMemory<char> Secret { get; } = secret.ToCharArray();

            public void Dispose() => disposed();
        }
    }

    private sealed class ThrowingCredentialStore(string sentinel) : IProviderCredentialStore
    {
        public int OpenLeaseCount { get; private set; }

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                ProviderCredentialState.Configured,
                DateTimeOffset.UtcNow));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            OpenLeaseCount++;
            throw new InvalidOperationException(sentinel);
        }

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class ExpiredCredentialStore(string sentinel) : IProviderCredentialStore
    {
        public int OpenLeaseCount { get; private set; }

        public bool LastLeaseDisposed { get; private set; }

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                ProviderCredentialState.Configured,
                DateTimeOffset.UtcNow));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            OpenLeaseCount++;
            return ValueTask.FromResult<IProviderCredentialLease?>(
                new ExpiredLease(sentinel, () => LastLeaseDisposed = true));
        }

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        private sealed class ExpiredLease(string sentinel, Action disposed)
            : IProviderCredentialLease
        {
            public ReadOnlyMemory<char> Secret =>
                throw new ObjectDisposedException("expired-lease", sentinel);

            public void Dispose() => disposed();
        }
    }

    private sealed class QueueCredentialStore(params string[] secrets) : IProviderCredentialStore
    {
        private readonly Queue<string> _secrets = new(secrets);

        public int DisposedLeaseCount { get; private set; }

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                ProviderCredentialState.Configured,
                DateTimeOffset.UtcNow));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IProviderCredentialLease?>(
                new QueueLease(_secrets.Dequeue(), () => DisposedLeaseCount++));

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        private sealed class QueueLease(string secret, Action disposed) : IProviderCredentialLease
        {
            public ReadOnlyMemory<char> Secret { get; } = secret.ToCharArray();

            public void Dispose() => disposed();
        }
    }

    private sealed class BlockingSseStream(string firstChunk) : Stream
    {
        private readonly byte[] _firstChunk = Encoding.UTF8.GetBytes(firstChunk);
        private bool _sent;

        public Task FirstChunkRead => _firstChunkRead.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        private readonly TaskCompletionSource _firstChunkRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                _firstChunk.CopyTo(buffer);
                _firstChunkRead.TrySetResult();
                return _firstChunk.Length;
            }

            using var registration = cancellationToken.Register(
                () => _cancellationObserved.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RepeatingSseLineStream(int totalLength) : Stream
    {
        private static readonly byte[] Prefix = "data:"u8.ToArray();
        private int _position;

        public int BytesRead { get; private set; }

        public int TotalLength { get; } = totalLength;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => TotalLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, TotalLength - _position);
            if (count <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var destination = buffer.Span[..count];
            destination.Fill((byte)'x');
            for (var index = 0; index < count && _position + index < Prefix.Length; index++)
            {
                destination[index] = Prefix[_position + index];
            }

            _position += count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RepeatingPatternStream(byte[] pattern, int totalLength) : Stream
    {
        private int _position;

        public int BytesRead { get; private set; }

        public int TotalLength { get; } = totalLength;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => TotalLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, TotalLength - _position);
            if (count <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var destination = buffer.Span[..count];
            for (var index = 0; index < count; index++)
            {
                destination[index] = pattern[(_position + index) % pattern.Length];
            }

            _position += count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
