using System.Net;
using System.Text;
using System.Text.Json;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.Qwen;

namespace ScreenGuide.AI.Qwen.Tests;

public sealed class QwenChatModelProviderTests
{
    [Fact]
    public async Task DescriptorExposesOnlyTheFixedQwenDestinationAndApprovedModel()
    {
        await using var provider = new QwenChatModelProvider(new MissingCredentialStore());

        Assert.Equal("qwen", provider.Descriptor.ProviderId);
        Assert.Equal("千问", provider.Descriptor.DisplayName);
        Assert.Equal("https://dashscope.aliyuncs.com", provider.Descriptor.DataDestination);
        Assert.True(provider.Descriptor.SendsDataOffDevice);
        Assert.Equal(ChatProviderCredentialKind.ApiKey, provider.Descriptor.CredentialKind);
        Assert.Equal(ChatProviderWorkloads.OrdinaryChat, provider.Descriptor.SupportedWorkloads);
        var model = Assert.Single(provider.Descriptor.Models);
        Assert.Equal("qwen3.7-plus", model.ModelId);
        Assert.Equal(
            ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput | ChatModelCapabilities.Vision,
            model.Capabilities);
    }

    [Fact]
    public async Task ImageCompletionSendsOneBoundedPngWithTextAndKeepsTheExactModelDestination()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP4/x8AAwAB//wl3FEAAAAASUVORK5CYII=";
        using var image = new ChatImage(Convert.FromBase64String(png));
        var credentials = new TestCredentialStore("synthetic-key");
        string? bodyText = null;
        var handler = new StubHttpMessageHandler(async (request, token) =>
        {
            Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            bodyText = await request.Content!.ReadAsStringAsync(token);
            return SseResponse("data: {\"choices\":[{\"delta\":{\"content\":\"seen\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        });
        await using var provider = new QwenChatModelProvider(credentials, handler);
        var response = await provider.CompleteAsync(Request() with
        { Messages = [new(ChatMessageRole.Assistant, "earlier text"), new(ChatMessageRole.User, "identify synthetic target", Images: [image])] });
        Assert.Equal("seen", response.Text);
        using var body = JsonDocument.Parse(bodyText!);
        Assert.Equal(QwenChatModelProvider.DefaultModelId, body.RootElement.GetProperty("model").GetString());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("earlier text", messages[0].GetProperty("content").GetString());
        var content = messages[1].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("identify synthetic target", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64," + png, content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(1, credentials.OpenLeaseCount);
        Assert.False(image.IsDisposed);
        Assert.DoesNotContain(png, response.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("two")]
    [InlineData("null")]
    [InlineData("disposed")]
    public async Task InvalidImagesFailBeforeCredentialOrHttp(string invalid)
    {
        using var image = new ChatImage(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP4/x8AAwAB//wl3FEAAAAASUVORK5CYII="));
        if (invalid == "disposed") image.Dispose();
        var credentials = new TestCredentialStore("synthetic-key");
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("Must not send."));
        await using var provider = new QwenChatModelProvider(credentials, handler);
        var error = await Assert.ThrowsAsync<ChatModelException>(() => provider.CompleteAsync(Request() with
        { Messages = [new(invalid == "assistant" ? ChatMessageRole.Assistant : ChatMessageRole.User, "synthetic",
            Images: invalid == "two" ? [image, image] : invalid == "null" ? [null!] : [image])] }));
        Assert.Equal("chat.image_input_invalid", error.Error.Code);
        Assert.Equal(0, credentials.OpenLeaseCount);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task CancelImageRequestStopsItsHttpCallWithoutFallbackOrDisposingTheCallersImage()
    {
        using var image = new ChatImage(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP4/x8AAwAB//wl3FEAAAAASUVORK5CYII="));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancelled call must not finish.");
        });
        await using var provider = new QwenChatModelProvider(new TestCredentialStore("synthetic-key"), handler);
        var turn = Guid.NewGuid();
        var pending = provider.CompleteAsync(Request(turnId: turn) with
        { Messages = [new(ChatMessageRole.User, "synthetic", Images: [image])] });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await provider.CancelAsync(turn);
        var error = await Assert.ThrowsAsync<ChatModelException>(() => pending);
        Assert.Equal(ChatModelErrorKind.Cancelled, error.Error.Kind);
        Assert.Equal(1, handler.SendCount);
        Assert.False(image.IsDisposed);
    }

    [Fact]
    public async Task CompletionUsesTheFixedEndpointAndExactSafeStreamingPayload()
    {
        const string secret = "qwen-fake-secret-never-persist";
        string? observedBody = null;
        string? observedAuthorization = null;
        Uri? observedUri = null;
        var credentials = new TestCredentialStore(secret);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            observedUri = request.RequestUri;
            observedAuthorization = request.Headers.Authorization?.ToString();
            observedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return SseResponse("""
                data: {"id":"qwen-request-1","choices":[{"delta":{"content":"{\"answer\":\"好\"}"},"finish_reason":"stop"}]}

                data: {"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":5,"total_tokens":17}}

                data: [DONE]

                """);
        });
        await using var provider = new QwenChatModelProvider(credentials, handler);

        var response = await provider.CompleteAsync(new ChatModelRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            QwenChatModelProvider.DefaultModelId,
            "你是元枢。",
            [
                new ChatMessage(ChatMessageRole.User, "第一轮"),
                new ChatMessage(ChatMessageRole.Assistant, "上一轮回答"),
                new ChatMessage(ChatMessageRole.User, "继续")
            ],
            new ChatModelOptions(Temperature: 0.2, MaxOutputTokens: 256),
            ChatResponseFormat.JsonObject));

        Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", observedUri?.AbsoluteUri);
        Assert.Equal($"Bearer {secret}", observedAuthorization);
        Assert.True(credentials.LastLeaseDisposed);
        Assert.DoesNotContain(secret, observedBody, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(observedBody!);
        var root = body.RootElement;
        Assert.Equal(
            [
                "enable_code_interpreter",
                "enable_search",
                "enable_thinking",
                "max_completion_tokens",
                "messages",
                "model",
                "n",
                "parallel_tool_calls",
                "preserve_thinking",
                "response_format",
                "stream",
                "stream_options",
                "temperature",
                "tool_choice"
            ],
            root.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("qwen3.7-plus", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(1, root.GetProperty("n").GetInt32());
        Assert.False(root.GetProperty("enable_thinking").GetBoolean());
        Assert.False(root.GetProperty("preserve_thinking").GetBoolean());
        Assert.Equal("none", root.GetProperty("tool_choice").GetString());
        Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(root.GetProperty("enable_search").GetBoolean());
        Assert.False(root.GetProperty("enable_code_interpreter").GetBoolean());
        Assert.Equal(256, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.Equal(4, root.GetProperty("messages").GetArrayLength());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("{\"answer\":\"好\"}", response.Text);
        Assert.Equal(response.Text, response.StructuredJson);
        Assert.Equal(new ChatModelUsage(12, 5, 17), response.Usage);
        Assert.Equal("qwen-request-1", response.Metadata.ProviderRequestId);
    }

    [Fact]
    public async Task MissingCredentialUnknownModelAndConflictingSamplingFailBeforeHttp()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("HTTP must not be reached."));
        var credentials = new TestCredentialStore();
        await using var provider = new QwenChatModelProvider(credentials, handler);

        var missing = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));
        var unknown = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(modelId: "qwen-other")));
        var conflicting = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(options: new ChatModelOptions(
                Temperature: 0.2,
                TopP: 0.9))));

        Assert.Equal("qwen.not_configured", missing.Error.Code);
        Assert.Equal("qwen.model_not_registered", unknown.Error.Code);
        Assert.Equal("qwen.sampling_options_conflict", conflicting.Error.Code);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(1, credentials.OpenLeaseCount);
    }

    [Fact]
    public async Task StreamingConsumesBoundedReasoningButPublishesOnlyContentAndUsage()
    {
        const string reasoningSentinel = "PRIVATE_REASONING_MUST_NOT_ESCAPE";
        const string sse = """
            data: {"id":"qwen-stream-1","choices":[{"delta":{"reasoning_content":"PRIVATE_REASONING_MUST_NOT_ESCAPE"},"finish_reason":null}]}

            data: {"id":"qwen-stream-1","choices":[{"delta":{"content":"你"},"finish_reason":null}]}

            data: {"id":"qwen-stream-1","choices":[{"delta":{"content":"好"},"finish_reason":"stop"}]}

            data: {"choices":[],"usage":{"prompt_tokens":7,"completion_tokens":2,"total_tokens":9,"completion_tokens_details":{"reasoning_tokens":4}}}

            data: [DONE]

            """;
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            new StubHttpMessageHandler((_, _) => Task.FromResult(SseResponse(sse))));
        var updates = new List<ChatStreamUpdate>();

        var response = await provider.CompleteAsync(
            Request(),
            (update, _) =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(["你", "好", ""], updates.Select(update => update.DeltaText).ToArray());
        Assert.Equal("你好", response.Text);
        Assert.DoesNotContain(reasoningSentinel, response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(updates, update => update.DeltaText.Contains(reasoningSentinel, StringComparison.Ordinal));
        Assert.Equal(new ChatModelUsage(7, 2, 9, null, 4), response.Usage);
        Assert.True(updates[^1].IsFinal);
    }

    [Theory]
    [InlineData(
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call-1\"}]},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n",
        "qwen.tool_calls_rejected")]
    [InlineData(
        "data: {not-json}\n\ndata: [DONE]\n\n",
        "qwen.invalid_stream_event")]
    [InlineData(
        "data: []\n\ndata: [DONE]\n\n",
        "qwen.invalid_stream_event")]
    [InlineData(
        "data: {\"choices\":[{\"delta\":{\"content\":\"\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
        "qwen.stream_empty")]
    [InlineData(
        "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n",
        "qwen.stream_incomplete")]
    public async Task UnsafeOrIncompleteSseFailsClosedWithStableSafeCode(
        string sse,
        string expectedCode)
    {
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            new StubHttpMessageHandler((_, _) => Task.FromResult(SseResponse(sse))));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(), (_, _) => ValueTask.CompletedTask));

        Assert.Equal(ChatModelErrorKind.InvalidResponse, exception.Error.Kind);
        Assert.Equal(expectedCode, exception.Error.Code);
        Assert.DoesNotContain(sse, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthUsesTheCompatibilityPermissionEndpointForTheDefaultWorkspaceAndAFreshCredentialLease()
    {
        var credentials = new TestCredentialStore("fake-qwen-key");
        var observed = new List<Uri?>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            observed.Add(request.RequestUri);
            return Task.FromResult(PermissionResponse());
        });
        await using var provider = new QwenChatModelProvider(credentials, handler);

        var first = await provider.CheckHealthAsync();
        var second = await provider.CheckHealthAsync();

        Assert.All(observed, uri => Assert.Equal(
            "https://dashscope.aliyuncs.com/api/v1/models/permissions?model=qwen3.7-plus&authorization_scope=AUTHORIZED&action=INFERENCE&page_no=1&page_size=1",
            uri?.AbsoluteUri));
        Assert.Equal(2, credentials.OpenLeaseCount);
        Assert.Equal(2, handler.SendCount);
        Assert.True(credentials.LastLeaseDisposed);
        Assert.Equal(ChatProviderHealthState.Healthy, first.State);
        Assert.Equal(ChatProviderHealthState.Healthy, second.State);
        Assert.DoesNotContain("FAKE_HEALTH_REQUEST_ID_MUST_NOT_ESCAPE", first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-qwen-key", first.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthAcceptsOfficialPermissionResponseWithEmptyCode()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(PermissionResponse(
            """{"success":true,"code":"","message":"","request_id":"FAKE_HEALTH_REQUEST_ID_MUST_NOT_ESCAPE","output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","name":"Qwen 3.7 Plus","permissions":{"inference":true,"fine_tune":false,"deploy":false}}]}}""")));
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(ChatProviderHealthState.Healthy, health.State);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task HealthAcceptsEmptyPermissionListForTheUnrestrictedDefaultWorkspace()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(PermissionResponse(
            """{"success":true,"code":null,"message":"","request_id":"FAKE_HEALTH_REQUEST_ID_MUST_NOT_ESCAPE","output":{"total":0,"page_no":1,"page_size":1,"permissions":[]}}""")));
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(ChatProviderHealthState.Healthy, health.State);
        Assert.True(health.IsConfigured);
        Assert.Equal(1, handler.SendCount);
        Assert.DoesNotContain("FAKE_HEALTH_REQUEST_ID_MUST_NOT_ESCAPE", health.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-qwen-key", health.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidPermissionResponses))]
    public async Task HealthFailsClosedForInvalidPermissionModelOrPaging(string json)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(PermissionResponse(json)));
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(ChatProviderHealthState.Unavailable, health.State);
        Assert.True(health.IsConfigured);
        Assert.Equal(1, handler.SendCount);
        Assert.DoesNotContain(json, health.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://dashscope.aliyuncs.com/api/v1/models?model=qwen3.7-plus&page_no=1&page_size=1", 200)]
    [InlineData("https://proxy.invalid/api/v1/models/permissions?model=qwen3.7-plus", 200)]
    [InlineData("https://workspace-123.cn-beijing.maas.aliyuncs.com/api/v1/models/permissions?model=qwen3.7-plus", 200)]
    [InlineData("https://dashscope.aliyuncs.com/api/v1/models/permissions?model=qwen3.7-plus&authorization_scope=AUTHORIZED&action=INFERENCE&page_no=1&page_size=1", 302)]
    public async Task HealthRejectsOldEndpointOtherHostAndRedirect(string finalUri, int statusCode)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            PermissionResponse(
                ValidPermissionJson,
                (HttpStatusCode)statusCode,
                new Uri(finalUri, UriKind.Absolute))));
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(ChatProviderHealthState.Unavailable, health.State);
        Assert.Equal(1, handler.SendCount);
    }

    [Theory]
    [InlineData(400, ChatProviderHealthState.Unavailable)]
    [InlineData(401, ChatProviderHealthState.Unavailable)]
    [InlineData(402, ChatProviderHealthState.Degraded)]
    [InlineData(403, ChatProviderHealthState.Unavailable)]
    [InlineData(404, ChatProviderHealthState.Unavailable)]
    [InlineData(408, ChatProviderHealthState.Unavailable)]
    [InlineData(429, ChatProviderHealthState.Degraded)]
    [InlineData(500, ChatProviderHealthState.Unavailable)]
    [InlineData(504, ChatProviderHealthState.Unavailable)]
    public async Task HealthMapsHttpStatusWithoutRetry(int statusCode, ChatProviderHealthState expected)
    {
        const string responseSentinel = "HEALTH_RESPONSE_MUST_NOT_ESCAPE";
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            PermissionResponse(
                $$"""{"success":false,"code":"remote","message":"{{responseSentinel}}"}""",
                (HttpStatusCode)statusCode)));
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(expected, health.State);
        Assert.Equal(1, handler.SendCount);
        Assert.DoesNotContain(responseSentinel, health.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthMapsExplicitBalanceFailureToDegraded()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            PermissionResponse("""{"success":false,"code":"Arrearage","message":"balance unavailable"}""")));
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(ChatProviderHealthState.Degraded, health.State);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task HealthMapsNetworkFailureToUnavailableWithoutRetry()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("NETWORK_SENTINEL_MUST_NOT_ESCAPE"));
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(ChatProviderHealthState.Unavailable, health.State);
        Assert.Equal(1, handler.SendCount);
        Assert.DoesNotContain("NETWORK_SENTINEL_MUST_NOT_ESCAPE", health.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthRejectsOversizedPermissionResponseWithoutLeakingIt()
    {
        const string responseSentinel = "OVERSIZED_PERMISSION_BODY_MUST_NOT_ESCAPE";
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            PermissionResponse(ValidPermissionJson + new string(' ', 256) + responseSentinel)));
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            handler,
            QwenProviderOptions.Default with { MaxErrorBodyCharacters = 128 });

        var health = await provider.CheckHealthAsync();

        Assert.Equal(ChatProviderHealthState.Unavailable, health.State);
        Assert.Equal(1, handler.SendCount);
        Assert.DoesNotContain(responseSentinel, health.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task HealthWithoutUsableCredentialIsNotConfiguredAndSendsNoHttp(string? secret)
    {
        var credentials = new TestCredentialStore(secret);
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("HTTP must not run."));
        await using var provider = new QwenChatModelProvider(credentials, handler);

        var health = await provider.CheckHealthAsync();

        Assert.Equal(ChatProviderHealthState.NotConfigured, health.State);
        Assert.False(health.IsConfigured);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(secret is not null, credentials.LastLeaseDisposed);
    }

    [Fact]
    public async Task HealthCancellationPropagatesAndClearsHeaderBeforeDisposingLease()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpRequestMessage? observedRequest = null;
        string? observedAuthorization = null;
        var credentials = new TestCredentialStore("fake-qwen-key");
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            observedRequest = request;
            observedAuthorization = request.Headers.Authorization?.ToString();
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return PermissionResponse();
        });
        await using var provider = new QwenChatModelProvider(credentials, handler);
        using var cancellation = new CancellationTokenSource();

        var check = provider.CheckHealthAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);

        Assert.Equal("Bearer fake-qwen-key", observedAuthorization);
        Assert.NotNull(observedRequest);
        Assert.Null(observedRequest.Headers.Authorization);
        Assert.True(credentials.LastLeaseDisposed);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task CancelAsyncAbortsTheExactActiveSseAndWaitsForItsTerminalState()
    {
        const string firstEvent = "data: {\"choices\":[{\"delta\":{\"content\":\"先\"},\"finish_reason\":null}]}\n\n";
        var stream = new BlockingSseStream(firstEvent);
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            handler);
        var turnId = Guid.NewGuid();
        var completion = provider.CompleteAsync(
            Request(turnId: turnId),
            (_, _) => ValueTask.CompletedTask);
        await stream.FirstChunkRead.WaitAsync(TimeSpan.FromSeconds(5));

        await provider.CancelAsync(turnId).WaitAsync(TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<ChatModelException>(() => completion);

        Assert.Equal(ChatModelErrorKind.Cancelled, exception.Error.Kind);
        Assert.Equal("qwen.cancelled", exception.Error.Code);
        Assert.True(stream.CancellationObserved.IsCompletedSuccessfully);
        Assert.Equal(1, handler.SendCount);
    }

    [Theory]
    [InlineData(400, "qwen.bad_request")]
    [InlineData(401, "qwen.unauthorized")]
    [InlineData(402, "qwen.insufficient_balance")]
    [InlineData(403, "qwen.authorization_denied")]
    [InlineData(408, "qwen.request_timeout")]
    [InlineData(429, "qwen.rate_limited")]
    [InlineData(500, "qwen.service_unavailable")]
    public async Task HttpFailuresMapOnceToStableSafeErrorsWithoutRetryOrLeakage(
        int statusCode,
        string expectedCode)
    {
        const string providerBodySentinel = "RAW_PROVIDER_BODY_MUST_NOT_ESCAPE";
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new StringContent(
                    $"{{\"error\":{{\"code\":\"remote\",\"message\":\"{providerBodySentinel}\"}}}}",
                    Encoding.UTF8,
                    "application/json")
            }));
        await using var provider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request()));

        Assert.Equal(expectedCode, exception.Error.Code);
        Assert.Equal(1, handler.SendCount);
        Assert.DoesNotContain(providerBodySentinel, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReasoningOnlyAndToolFinishNeverBecomeAssistantOutput()
    {
        const string reasoningSentinel = "REASONING_ONLY_PRIVATE_SENTINEL";
        var reasoningProvider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            new StubHttpMessageHandler((_, _) => Task.FromResult(SseResponse($$"""
                data: {"choices":[{"delta":{"reasoning_content":"{{reasoningSentinel}}"},"finish_reason":"length"}]}

                data: [DONE]

                """))));
        var toolProvider = new QwenChatModelProvider(
            new TestCredentialStore("fake-qwen-key"),
            new StubHttpMessageHandler((_, _) => Task.FromResult(SseResponse("""
                data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

                data: [DONE]

                """))));
        await using (reasoningProvider)
        await using (toolProvider)
        {
            var reasoning = await Assert.ThrowsAsync<ChatModelException>(() =>
                reasoningProvider.CompleteAsync(Request(), (_, _) => ValueTask.CompletedTask));
            var tool = await Assert.ThrowsAsync<ChatModelException>(() =>
                toolProvider.CompleteAsync(Request(), (_, _) => ValueTask.CompletedTask));

            Assert.Equal("qwen.stream_empty", reasoning.Error.Code);
            Assert.DoesNotContain(reasoningSentinel, reasoning.Message, StringComparison.Ordinal);
            Assert.Equal("qwen.tool_calls_rejected", tool.Error.Code);
        }
    }

    [Fact]
    public async Task EachOperationUsesASeparateLeaseAndNeverReusesAnotherProvidersCredential()
    {
        var credentials = new TestCredentialStore("fake-qwen-key");
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : SseResponse("""
                    data: {"choices":[{"delta":{"content":"好"},"finish_reason":"stop"}]}

                    data: [DONE]

                    """)));
        await using var provider = new QwenChatModelProvider(credentials, handler);

        _ = await provider.CompleteAsync(Request());
        _ = await provider.CompleteAsync(Request());
        _ = await provider.CheckHealthAsync();

        Assert.Equal(3, credentials.OpenLeaseCount);
        Assert.Equal(3, handler.SendCount);
        Assert.All(credentials.RequestedProviderIds, id => Assert.Equal("qwen", id));
    }

    private static ChatModelRequest Request(
        string modelId = QwenChatModelProvider.DefaultModelId,
        Guid? turnId = null,
        ChatModelOptions? options = null) => new(
            Guid.NewGuid(),
            turnId ?? Guid.NewGuid(),
            modelId,
            string.Empty,
            [new ChatMessage(ChatMessageRole.User, "你好")],
            Options: options);

    private sealed class MissingCredentialStore : IProviderCredentialStore
    {
        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                ProviderCredentialState.Missing,
                null));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IProviderCredentialLease?>(null);

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static HttpResponseMessage SseResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "text/event-stream")
    };

    private const string ValidPermissionJson =
        """{"success":true,"code":null,"message":"","request_id":"FAKE_HEALTH_REQUEST_ID_MUST_NOT_ESCAPE","output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","name":"Qwen 3.7 Plus","permissions":{"inference":true,"fine_tune":false,"deploy":false}}]}}""";

    private static HttpResponseMessage PermissionResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            ValidPermissionJson,
            Encoding.UTF8,
            "application/json")
    };

    private static HttpResponseMessage PermissionResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        Uri? finalUri = null) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
        RequestMessage = finalUri is null ? null : new HttpRequestMessage(HttpMethod.Get, finalUri)
    };

    public static TheoryData<string> InvalidPermissionResponses => new()
    {
        """{"success":true,"code":"","output":{"total":0,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":true}}]}}""",
        """{"success":true,"code":"","output":{"total":1,"page_no":1,"page_size":1,"permissions":[]}}""",
        """{"success":true,"code":"","output":{"total":2,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":true}},{"model":"qwen3.7-plus","permissions":{"inference":true}}]}}""",
        """{"success":true,"code":"","output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen-other","permissions":{"inference":true}}]}}""",
        """{"success":true,"code":"","output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus"}]}}""",
        """{"success":true,"code":"","output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":false}}]}}""",
        """{"success":true,"code":"","output":{"total":1,"page_no":2,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":true}}]}}""",
        """{"success":true,"code":"","output":{"total":1,"page_no":1,"page_size":2,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":true}}]}}""",
        """{"success":false,"code":"","output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":true}}]}}""",
        """{"success":true,"output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":true}}]}}""",
        """{"success":true,"code":"permission_denied","output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":true}}]}}""",
        """{"success":true,"code":" ","output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":true}}]}}""",
        """{"success":true,"code":"","output":{"total":1,"page_no":1,"page_size":1,"permissions":{}}}""",
        """{"success":true,"code":"","total":1,"page_no":1,"page_size":1,"data":[{"model":"qwen3.7-plus","permissions":{"inference":true}}]}""",
        "{"
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed class TestCredentialStore(string? secret = null) : IProviderCredentialStore
    {
        public bool LastLeaseDisposed { get; private set; }

        public int OpenLeaseCount { get; private set; }

        public List<string> RequestedProviderIds { get; } = [];

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCredentialStatus(
                providerId,
                secret is null ? ProviderCredentialState.Missing : ProviderCredentialState.Configured,
                secret is null ? null : DateTimeOffset.UtcNow));

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> value,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            OpenLeaseCount++;
            RequestedProviderIds.Add(providerId);
            return ValueTask.FromResult<IProviderCredentialLease?>(
                secret is null ? null : new Lease(secret, () => LastLeaseDisposed = true));
        }

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        private sealed class Lease(string secret, Action disposed) : IProviderCredentialLease
        {
            public ReadOnlyMemory<char> Secret { get; } = secret.ToCharArray();

            public void Dispose() => disposed();
        }
    }

    private sealed class BlockingSseStream(string firstChunk) : Stream
    {
        private readonly byte[] _firstChunk = Encoding.UTF8.GetBytes(firstChunk);
        private readonly TaskCompletionSource _firstChunkRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sent;

        public Task FirstChunkRead => _firstChunkRead.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

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
}
