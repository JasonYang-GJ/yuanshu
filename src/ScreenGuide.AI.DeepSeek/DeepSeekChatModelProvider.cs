using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenGuide.AI.Core;

namespace ScreenGuide.AI.DeepSeek;

public sealed class DeepSeekChatModelProvider : IChatModelProvider
{
    public const string ProviderId = "deepseek";
    public const string FlashModelId = "deepseek-v4-flash";
    public const string ProModelId = "deepseek-v4-pro";
    public const string DataDestination = "https://api.deepseek.com";

    private static readonly Uri ChatCompletionsEndpoint =
        new("https://api.deepseek.com/chat/completions", UriKind.Absolute);
    private static readonly Uri ModelsEndpoint =
        new("https://api.deepseek.com/models", UriKind.Absolute);

    private static readonly ChatModelCapabilities ModelCapabilities =
        ChatModelCapabilities.Streaming |
        ChatModelCapabilities.JsonObjectOutput |
        ChatModelCapabilities.Reasoning;

    private readonly IProviderCredentialStore _credentialStore;
    private readonly HttpClient _httpClient;
    private readonly DeepSeekProviderOptions _options;
    private readonly ConcurrentDictionary<Guid, ActiveCall> _activeCalls = new();
    private int _disposed;

    public DeepSeekChatModelProvider(IProviderCredentialStore credentialStore)
        : this(credentialStore, CreateProductionHandler(), null)
    {
    }

    public DeepSeekChatModelProvider(
        IProviderCredentialStore credentialStore,
        HttpMessageHandler messageHandler,
        DeepSeekProviderOptions? options = null)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        ArgumentNullException.ThrowIfNull(messageHandler);
        _options = ValidateOptions(options ?? DeepSeekProviderOptions.Default);
        _httpClient = new HttpClient(messageHandler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public ChatProviderDescriptor Descriptor { get; } = new(
        ProviderId,
        "DeepSeek",
        DataDestination,
        SendsDataOffDevice: true,
        [
            new ChatModelDescriptor(
                FlashModelId,
                "DeepSeek V4 Flash",
                ModelCapabilities,
                ContextWindowTokens: 1_000_000),
            new ChatModelDescriptor(
                ProModelId,
                "DeepSeek V4 Pro",
                ModelCapabilities,
                ContextWindowTokens: 1_000_000)
        ],
        ChatProviderCredentialKind.ApiKey);

    public Task<ChatModelResponse> CompleteAsync(
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback = null,
        CancellationToken cancellationToken = default) =>
        CompleteCoreAsync(request, streamCallback, cancellationToken);

    public Task<ChatProviderHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default) =>
        CheckHealthCoreAsync(cancellationToken);

    public async Task CancelAsync(
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        if (_activeCalls.TryGetValue(turnId, out var active))
        {
            active.Cancel();
            await active.Completed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var active in _activeCalls.Values)
        {
            active.Cancel();
        }

        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private static HttpMessageHandler CreateProductionHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    };

    private async Task<ChatProviderHealth> CheckHealthCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var checkedAt = DateTimeOffset.UtcNow;
        var lease = await _credentialStore.OpenLeaseAsync(ProviderId, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null || lease.Secret.IsEmpty)
        {
            lease?.Dispose();
            return new ChatProviderHealth(
                ProviderId,
                ChatProviderHealthState.NotConfigured,
                IsConfigured: false,
                "DeepSeek 尚未配置 API Key。",
                checkedAt);
        }

        using var timeout = new CancellationTokenSource(_options.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response;
            using (lease)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    new string(lease.Secret.Span));
                try
                {
                    response = await _httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            linked.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    request.Headers.Authorization = null;
                }
            }

            using (response)
            {
                if ((response.RequestMessage?.RequestUri is { } effectiveUri
                     && !Uri.Equals(effectiveUri, ModelsEndpoint))
                    || (int)response.StatusCode is >= 300 and < 400)
                {
                    return new ChatProviderHealth(
                        ProviderId,
                        ChatProviderHealthState.Unavailable,
                        IsConfigured: true,
                        "DeepSeek 返回了不安全的跳转，连接已停止。",
                        checkedAt);
                }

                if (response.IsSuccessStatusCode)
                {
                    return new ChatProviderHealth(
                        ProviderId,
                        ChatProviderHealthState.Healthy,
                        IsConfigured: true,
                        "DeepSeek 已配置并可连接。",
                        checkedAt);
                }

                _ = await ReadBoundedErrorAsync(response.Content, linked.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                return new ChatProviderHealth(
                    ProviderId,
                    status switch
                    {
                        402 or 429 => ChatProviderHealthState.Degraded,
                        _ => ChatProviderHealthState.Unavailable
                    },
                    IsConfigured: true,
                    status switch
                    {
                        401 => "DeepSeek API Key 无效或已失效。",
                        403 => "DeepSeek API Key 没有访问所选能力的权限。",
                        402 => "DeepSeek 账户余额不足或计费状态不可用。",
                        429 => "DeepSeek 当前请求过多，请稍后再检查。",
                        >= 500 => "DeepSeek 服务暂时不可用。",
                        _ => "DeepSeek 配置当前无法验证。"
                    },
                    checkedAt);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return new ChatProviderHealth(
                ProviderId,
                ChatProviderHealthState.Unavailable,
                IsConfigured: true,
                "连接 DeepSeek 超时。",
                checkedAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new ChatProviderHealth(
                ProviderId,
                ChatProviderHealthState.Unavailable,
                IsConfigured: true,
                "现在无法连接 DeepSeek。",
                checkedAt);
        }
    }

    private async Task<ChatModelResponse> CompleteCoreAsync(
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        request = ChatImageInputs.Prepare(request, ProviderId, supportsImages: false);
        ValidateRequest(request);
        var active = new ActiveCall();
        if (!_activeCalls.TryAdd(request.TurnId, active))
        {
            active.Dispose();
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidRequest,
                "deepseek.turn_already_active",
                "这条请求仍在处理中，请等待或先停止回答。");
        }

        using var timeout = new CancellationTokenSource(_options.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            active.Token,
            timeout.Token);
        try
        {
            var response = await ExecuteCoreAsync(request, streamCallback, linked.Token)
                .ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return response;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || active.IsCancellationRequested)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.Cancelled,
                "deepseek.cancelled",
                "DeepSeek 回答已停止。");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.Timeout,
                "deepseek.timeout",
                "DeepSeek 回答超时，这次请求已经安全结束，可以重新发送。");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.Network,
                "deepseek.network_error",
                "现在无法连接 DeepSeek，请检查网络后重试。");
        }
        finally
        {
            _activeCalls.TryRemove(new KeyValuePair<Guid, ActiveCall>(request.TurnId, active));
            active.Complete();
            active.Dispose();
        }
    }

    private async Task<ChatModelResponse> ExecuteCoreAsync(
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback,
        CancellationToken cancellationToken)
    {
        var lease = await _credentialStore.OpenLeaseAsync(ProviderId, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null || lease.Secret.IsEmpty)
        {
            lease?.Dispose();
            throw Error(
                request.ModelId,
                ChatModelErrorKind.Configuration,
                "deepseek.not_configured",
                "DeepSeek 尚未配置 API Key，请先在设置中完成配置。");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint);
        var streaming = streamCallback is not null;
        httpRequest.Content = new StringContent(
            BuildRequestJson(request, streaming),
            Encoding.UTF8,
            "application/json");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            streaming ? "text/event-stream" : "application/json"));

        HttpResponseMessage response;
        using (lease)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                new string(lease.Secret.Span));
            try
            {
                response = await _httpClient.SendAsync(
                        httpRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                httpRequest.Headers.Authorization = null;
            }
        }

        using (response)
        {
            if (response.RequestMessage?.RequestUri is { } effectiveUri
                && !Uri.Equals(effectiveUri, ChatCompletionsEndpoint))
            {
                throw Error(
                    request.ModelId,
                    ChatModelErrorKind.InvalidResponse,
                    "deepseek.redirect_rejected",
                    "DeepSeek 返回了不安全的跳转，本次请求已停止。");
            }

            var numericStatus = (int)response.StatusCode;
            if (numericStatus is >= 300 and < 400)
            {
                throw Error(
                    request.ModelId,
                    ChatModelErrorKind.InvalidResponse,
                    "deepseek.redirect_rejected",
                    "DeepSeek 返回了不安全的跳转，本次请求已停止。");
            }

            if (!response.IsSuccessStatusCode)
            {
                var details = await ReadBoundedErrorAsync(response.Content, cancellationToken)
                    .ConfigureAwait(false);
                throw MapHttpError(request.ModelId, response, details);
            }

            if (streaming)
            {
                return await ParseStreamingResponseAsync(
                        request,
                        response.Content,
                        streamCallback!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var json = await ReadBoundedCompletionBodyAsync(
                    request.ModelId,
                    response.Content,
                    cancellationToken)
                .ConfigureAwait(false);
            return ParseNonStreamingResponse(request, json);
        }
    }

    private async Task<string> ReadBoundedCompletionBodyAsync(
        string modelId,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var maximumBodyCharacters = (int)Math.Min(
            int.MaxValue - 1L,
            (long)_options.MaxOutputCharacters * 8L + 65_536L);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        var result = new StringBuilder(Math.Min(maximumBodyCharacters, 16_384));
        var buffer = new char[Math.Min(maximumBodyCharacters + 1, 4096)];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToString();
            }

            if ((long)result.Length + read > maximumBodyCharacters)
            {
                throw Error(
                    modelId,
                    ChatModelErrorKind.InvalidResponse,
                    "deepseek.response_body_too_large",
                    "DeepSeek 返回的数据过大，已停止读取。");
            }

            result.Append(buffer, 0, read);
        }
    }

    private async Task<ProviderErrorDetails> ReadBoundedErrorAsync(
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return new ProviderErrorDetails(null, null);
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: Math.Min(_options.MaxErrorBodyCharacters, 4096),
            leaveOpen: false);
        var buffer = new char[_options.MaxErrorBodyCharacters];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length == 0)
        {
            return new ProviderErrorDetails(null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return new ProviderErrorDetails(null, null);
            }

            return new ProviderErrorDetails(
                error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null,
                error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null);
        }
        catch (JsonException)
        {
            return new ProviderErrorDetails(null, null);
        }
    }

    private static ChatModelException MapHttpError(
        string modelId,
        HttpResponseMessage response,
        ProviderErrorDetails details)
    {
        var status = (int)response.StatusCode;
        if (status == 404 || (status is 400 or 422 && IsModelError(details)))
        {
            return Error(
                modelId,
                ChatModelErrorKind.ModelNotFound,
                "deepseek.model_not_found",
                "所选 DeepSeek 模型当前不可用，请在设置中重新选择。");
        }

        return status switch
        {
            400 => Error(
                modelId,
                ChatModelErrorKind.InvalidRequest,
                "deepseek.bad_request",
                "DeepSeek 无法处理这次请求，请检查输入和模型设置。"),
            401 => Error(
                modelId,
                ChatModelErrorKind.Unauthorized,
                "deepseek.unauthorized",
                "DeepSeek 的 API Key 无效或已失效，请在设置中重新填写。"),
            402 => Error(
                modelId,
                ChatModelErrorKind.InsufficientBalance,
                "deepseek.insufficient_balance",
                "DeepSeek 账户余额不足或计费状态不可用，请检查账户。"),
            403 => Error(
                modelId,
                ChatModelErrorKind.Authorization,
                "deepseek.authorization_denied",
                "DeepSeek 的 API Key 没有访问所选能力的权限，请检查账户授权。"),
            408 => Error(
                modelId,
                ChatModelErrorKind.Timeout,
                "deepseek.request_timeout",
                "DeepSeek 回答超时，这次请求已经安全结束，可以重新发送。"),
            422 => Error(
                modelId,
                ChatModelErrorKind.InvalidRequest,
                "deepseek.unprocessable_request",
                "DeepSeek 无法理解这次请求，请调整输入后重试。"),
            429 => Error(
                modelId,
                ChatModelErrorKind.RateLimited,
                "deepseek.rate_limited",
                "DeepSeek 当前请求过多，请稍后重试并检查账户状态。",
                retryAfter: ReadRetryAfter(response)),
            503 => Error(
                modelId,
                ChatModelErrorKind.Unavailable,
                "deepseek.unavailable",
                "DeepSeek 服务暂时不可用，请稍后重试。",
                retryAfter: ReadRetryAfter(response)),
            >= 500 => Error(
                modelId,
                ChatModelErrorKind.Unavailable,
                "deepseek.server_error",
                "DeepSeek 服务暂时异常，请稍后重试。"),
            _ => Error(
                modelId,
                ChatModelErrorKind.Unknown,
                "deepseek.http_error",
                "DeepSeek 没有完成这次回答，请稍后重试。")
        };
    }

    private static bool IsModelError(ProviderErrorDetails details) =>
        details.Code?.Contains("model", StringComparison.OrdinalIgnoreCase) == true
        && (details.Code.Contains("not_found", StringComparison.OrdinalIgnoreCase)
            || details.Code.Contains("not-found", StringComparison.OrdinalIgnoreCase)
            || details.Code.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        || details.Message?.Contains("model not found", StringComparison.OrdinalIgnoreCase) == true
        || details.Message?.Contains("model does not exist", StringComparison.OrdinalIgnoreCase) == true;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private static string BuildRequestJson(ChatModelRequest request, bool stream)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            });
        }

        foreach (var message in request.Messages)
        {
            if (message.Role == ChatMessageRole.Tool)
            {
                throw Error(
                    request.ModelId,
                    ChatModelErrorKind.InvalidRequest,
                    "deepseek.tool_messages_not_supported",
                    "当前 DeepSeek 普通聊天不支持工具消息。");
            }

            messages.Add(new JsonObject
            {
                ["role"] = message.Role switch
                {
                    ChatMessageRole.System => "system",
                    ChatMessageRole.User => "user",
                    ChatMessageRole.Assistant => "assistant",
                    _ => throw new ArgumentOutOfRangeException(nameof(message.Role))
                },
                ["content"] = message.Content,
                ["name"] = string.IsNullOrWhiteSpace(message.Name) ? null : message.Name
            });
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["stream"] = stream
        };
        if (stream)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        if (request.Options?.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.Options?.MaxOutputTokens is { } maxTokens)
        {
            body["max_tokens"] = maxTokens;
        }

        if (request.Options?.TopP is { } topP)
        {
            body["top_p"] = topP;
        }

        if (request.Options?.Seed is { } seed)
        {
            body["seed"] = seed;
        }

        var responseFormat = request.ResponseFormat ?? ChatResponseFormat.Text;
        if (responseFormat.Kind == ChatResponseFormatKind.JsonSchema)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidRequest,
                "deepseek.json_schema_not_supported",
                "DeepSeek 当前不支持 JSON Schema 输出，请改用 JSON Object。");
        }

        if (responseFormat.Kind == ChatResponseFormatKind.JsonObject)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        return body.ToJsonString();
    }

    private ChatModelResponse ParseNonStreamingResponse(
        ChatModelRequest request,
        string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var choice = root.GetProperty("choices")[0];
            var text = choice.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new JsonException("Missing assistant content.");
            }

            if (text.Length > _options.MaxOutputCharacters)
            {
                throw Error(
                    request.ModelId,
                    ChatModelErrorKind.InvalidResponse,
                    "deepseek.output_too_large",
                    "DeepSeek 返回的内容过长，已停止读取。");
            }

            string? structuredJson = null;
            if ((request.ResponseFormat ?? ChatResponseFormat.Text).Kind == ChatResponseFormatKind.JsonObject)
            {
                using var structured = JsonDocument.Parse(text);
                if (structured.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("Structured response is not an object.");
                }

                structuredJson = text;
            }

            var usage = TryReadUsage(root);
            return new ChatModelResponse(
                text,
                MapFinishReason(choice.TryGetProperty("finish_reason", out var finish)
                    ? finish.GetString()
                    : null),
                usage,
                new ChatProviderMetadata(
                    ProviderId,
                    request.ModelId,
                    root.TryGetProperty("id", out var id)
                        ? NormalizeProviderRequestId(id.GetString())
                        : null,
                    DataDestination),
                structuredJson);
        }
        catch (ChatModelException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidResponse,
                "deepseek.invalid_response",
                "DeepSeek 返回了无法读取的结果，请稍后重试。");
        }
    }

    private async Task<ChatModelResponse> ParseStreamingResponseAsync(
        ChatModelRequest request,
        HttpContent content,
        ChatModelStreamCallback streamCallback,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        var text = new StringBuilder();
        long sequence = 0;
        string? providerRequestId = null;
        var finishReason = ChatFinishReason.Unknown;
        ChatModelUsage? usage = null;
        var receivedDone = false;

        var lineReader = new BoundedSseLineReader(
            reader,
            request.ModelId,
            _options.MaxSseResponseCharacters);
        while (await lineReader.ReadLineAsync(
                   _options.MaxSseEventCharacters,
                   cancellationToken)
               .ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0 || line.StartsWith(':'))
            {
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].TrimStart();
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                receivedDone = true;
                break;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    providerRequestId ??= NormalizeProviderRequestId(id.GetString());
                }

                usage = TryReadUsage(root) ?? usage;
                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finish)
                    && finish.ValueKind == JsonValueKind.String)
                {
                    finishReason = MapFinishReason(finish.GetString());
                }

                if (!choice.TryGetProperty("delta", out var delta)
                    || !delta.TryGetProperty("content", out var contentElement)
                    || contentElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var deltaText = contentElement.GetString() ?? string.Empty;
                if (deltaText.Length == 0)
                {
                    continue;
                }

                if ((long)text.Length + deltaText.Length > _options.MaxOutputCharacters)
                {
                    throw Error(
                        request.ModelId,
                        ChatModelErrorKind.InvalidResponse,
                        "deepseek.output_too_large",
                        "DeepSeek 返回的内容过长，已停止读取。");
                }

                text.Append(deltaText);
                await streamCallback(
                        new ChatStreamUpdate(++sequence, deltaText),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ChatModelException)
            {
                throw;
            }
            catch (JsonException)
            {
                throw Error(
                    request.ModelId,
                    ChatModelErrorKind.InvalidResponse,
                    "deepseek.invalid_stream_event",
                    "DeepSeek 返回了无法读取的流式结果，请稍后重试。");
            }
        }

        if (!receivedDone)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidResponse,
                "deepseek.stream_incomplete",
                "DeepSeek 的流式回答意外中断，请重新发送。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var finalText = text.ToString();
        if (string.IsNullOrWhiteSpace(finalText))
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidResponse,
                "deepseek.stream_empty",
                "DeepSeek 没有返回回答内容，请重新发送。");
        }

        string? structuredJson = null;
        if ((request.ResponseFormat ?? ChatResponseFormat.Text).Kind == ChatResponseFormatKind.JsonObject)
        {
            try
            {
                using var structured = JsonDocument.Parse(finalText);
                if (structured.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException();
                }

                structuredJson = finalText;
            }
            catch (JsonException)
            {
                throw Error(
                    request.ModelId,
                    ChatModelErrorKind.InvalidResponse,
                    "deepseek.invalid_json_object",
                    "DeepSeek 没有返回有效的 JSON Object，请重试。");
            }
        }

        await streamCallback(
                new ChatStreamUpdate(++sequence, string.Empty, IsFinal: true),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return new ChatModelResponse(
            finalText,
            finishReason,
            usage,
            new ChatProviderMetadata(
                ProviderId,
                request.ModelId,
                providerRequestId,
                DataDestination),
            structuredJson);
    }

    private static ChatModelUsage? TryReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ChatModelUsage(
            ReadInt64(usage, "prompt_tokens"),
            ReadInt64(usage, "completion_tokens"),
            ReadInt64(usage, "total_tokens"),
            ReadInt64(usage, "prompt_cache_hit_tokens"),
            usage.TryGetProperty("completion_tokens_details", out var details)
                ? ReadInt64(details, "reasoning_tokens")
                : null);
    }

    private static long? ReadInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number)
            ? number
            : null;

    private static ChatFinishReason MapFinishReason(string? reason) => reason switch
    {
        "stop" => ChatFinishReason.Stop,
        "length" => ChatFinishReason.Length,
        "tool_calls" => ChatFinishReason.ToolCall,
        "content_filter" => ChatFinishReason.ContentFilter,
        "insufficient_system_resource" => ChatFinishReason.Error,
        _ => ChatFinishReason.Unknown
    };

    private static string? NormalizeProviderRequestId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80)
        {
            return null;
        }

        foreach (var character in value)
        {
            if (character > 0x7f
                || !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            {
                return null;
            }
        }

        return value;
    }

    private static void ValidateModel(string modelId)
    {
        if (!string.Equals(modelId, FlashModelId, StringComparison.Ordinal)
            && !string.Equals(modelId, ProModelId, StringComparison.Ordinal))
        {
            throw Error(
                modelId,
                ChatModelErrorKind.ModelNotFound,
                "deepseek.model_not_registered",
                "所选 DeepSeek 模型不可用，请在设置中重新选择。");
        }
    }

    private void ValidateRequest(ChatModelRequest request)
    {
        ValidateModel(request.ModelId);
        if (request.RequestId == Guid.Empty || request.TurnId == Guid.Empty)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidRequest,
                "deepseek.request_identity_invalid",
                "这次聊天请求无效，请重新发送。");
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidRequest,
                "deepseek.messages_missing",
                "聊天内容不能为空。");
        }

        if (request.Messages.Count > _options.MaxMessageCount)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidRequest,
                "deepseek.message_count_exceeded",
                "这次对话包含的消息过多，请新建话题后重试。");
        }

        long characters = request.SystemPrompt?.Length ?? 0;
        foreach (var message in request.Messages)
        {
            if (message is null || message.Content is null)
            {
                throw Error(
                    request.ModelId,
                    ChatModelErrorKind.InvalidRequest,
                    "deepseek.message_invalid",
                    "聊天内容无效，请重新发送。");
            }

            if (message.Role == ChatMessageRole.Tool)
            {
                throw Error(
                    request.ModelId,
                    ChatModelErrorKind.InvalidRequest,
                    "deepseek.tool_messages_not_supported",
                    "当前 DeepSeek 普通聊天不支持工具消息。");
            }

            characters += message.Content.Length + (message.Name?.Length ?? 0);
            if (characters > _options.MaxInputCharacters)
            {
                throw Error(
                    request.ModelId,
                    ChatModelErrorKind.InvalidRequest,
                    "deepseek.input_too_large",
                    "这次发送的内容过长，请缩短后重试。");
            }
        }

        if ((request.ResponseFormat ?? ChatResponseFormat.Text).Kind == ChatResponseFormatKind.JsonSchema)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidRequest,
                "deepseek.json_schema_not_supported",
                "DeepSeek 当前不支持 JSON Schema 输出，请改用 JSON Object。");
        }

        if (request.Options?.Temperature is { } temperature && (temperature < 0 || temperature > 2)
            || request.Options?.TopP is { } topP && (topP <= 0 || topP > 1)
            || request.Options?.MaxOutputTokens is { } maxTokens && maxTokens <= 0)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidRequest,
                "deepseek.options_invalid",
                "聊天模型参数无效，请恢复默认设置后重试。");
        }
    }

    private static DeepSeekProviderOptions ValidateOptions(DeepSeekProviderOptions options)
    {
        if (options.RequestTimeout <= TimeSpan.Zero
            || options.RequestTimeout > TimeSpan.FromMinutes(10)
            || options.MaxMessageCount <= 0
            || options.MaxMessageCount > 4_096
            || options.MaxInputCharacters <= 0
            || options.MaxInputCharacters > 4_000_000
            || options.MaxOutputCharacters <= 0
            || options.MaxOutputCharacters > 2_000_000
            || options.MaxErrorBodyCharacters <= 0
            || options.MaxErrorBodyCharacters > 65_536
            || options.MaxSseEventCharacters <= 0
            || options.MaxSseEventCharacters > 1_000_000
            || options.MaxSseResponseCharacters <= 0
            || options.MaxSseResponseCharacters > 16_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "DeepSeek Provider 限制超出安全范围。");
        }

        return options;
    }

    private static ChatModelException Error(
        string? modelId,
        ChatModelErrorKind kind,
        string code,
        string message,
        TimeSpan? retryAfter = null) =>
        new(ProviderId, modelId, new ChatModelError(kind, code, message, retryAfter));

    private sealed class ActiveCall : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token => _cancellation.Token;

        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public Task Completed => _completed.Task;

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The request reached a terminal state between lookup and cancellation.
            }
        }

        public void Complete() => _completed.TrySetResult();

        public void Dispose() => _cancellation.Dispose();
    }

    private sealed class BoundedSseLineReader(
        StreamReader reader,
        string modelId,
        int maximumResponseCharacters)
    {
        private readonly char[] _buffer = new char[4096];
        private int _bufferLength;
        private int _bufferPosition;
        private long _responseCharacters;

        public async ValueTask<string?> ReadLineAsync(
            int maximumCharacters,
            CancellationToken cancellationToken)
        {
            StringBuilder? line = null;
            while (true)
            {
                if (_bufferPosition == _bufferLength)
                {
                    var remaining = maximumResponseCharacters - _responseCharacters;
                    var readLength = (int)Math.Min(_buffer.Length, remaining + 1);
                    _bufferLength = await reader.ReadAsync(
                            _buffer.AsMemory(0, readLength),
                            cancellationToken)
                        .ConfigureAwait(false);
                    _bufferPosition = 0;
                    if (_bufferLength == 0)
                    {
                        return line?.ToString();
                    }

                    _responseCharacters += _bufferLength;
                    if (_responseCharacters > maximumResponseCharacters)
                    {
                        throw Error(
                            modelId,
                            ChatModelErrorKind.InvalidResponse,
                            "deepseek.stream_response_too_large",
                            "DeepSeek 返回的流式数据总量过大，已停止读取。");
                    }
                }

                var newline = Array.IndexOf(
                    _buffer,
                    '\n',
                    _bufferPosition,
                    _bufferLength - _bufferPosition);
                var segmentEnd = newline >= 0 ? newline : _bufferLength;
                var segmentLength = segmentEnd - _bufferPosition;
                var currentLength = line?.Length ?? 0;
                if ((long)currentLength + segmentLength > maximumCharacters)
                {
                    throw Error(
                        modelId,
                        ChatModelErrorKind.InvalidResponse,
                        "deepseek.stream_event_too_large",
                        "DeepSeek 返回的单段流式数据过大，已停止读取。");
                }

                if (line is null && newline >= 0)
                {
                    var directLength = segmentLength;
                    if (directLength > 0 && _buffer[_bufferPosition + directLength - 1] == '\r')
                    {
                        directLength--;
                    }

                    var result = new string(_buffer, _bufferPosition, directLength);
                    _bufferPosition = newline + 1;
                    return result;
                }

                line ??= new StringBuilder(Math.Min(maximumCharacters, Math.Max(256, segmentLength * 2)));
                line.Append(_buffer, _bufferPosition, segmentLength);
                _bufferPosition = segmentEnd;
                if (newline < 0)
                {
                    continue;
                }

                _bufferPosition++;
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line.Length--;
                }

                return line.ToString();
            }
        }
    }

    private sealed record ProviderErrorDetails(string? Code, string? Message);
}
