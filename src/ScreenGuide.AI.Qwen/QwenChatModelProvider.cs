using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenGuide.AI.Core;

namespace ScreenGuide.AI.Qwen;

public sealed class QwenChatModelProvider : IChatModelProvider
{
    public const string ProviderId = "qwen";
    public const string DefaultModelId = "qwen3.7-plus";
    public const string DataDestination = "https://dashscope.aliyuncs.com";

    private static readonly Uri ChatCompletionsEndpoint = new(
        "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
        UriKind.Absolute);
    private static readonly Uri ModelPermissionsEndpoint = new(
        "https://dashscope.aliyuncs.com/api/v1/models/permissions?model=qwen3.7-plus&authorization_scope=AUTHORIZED&action=INFERENCE&page_no=1&page_size=1",
        UriKind.Absolute);

    private readonly IProviderCredentialStore _credentialStore;
    private readonly HttpClient _httpClient;
    private readonly QwenProviderOptions _options;
    private readonly ConcurrentDictionary<Guid, ActiveCall> _activeCalls = new();
    private int _disposed;

    public QwenChatModelProvider(IProviderCredentialStore credentialStore)
        : this(credentialStore, CreateProductionHandler(), null)
    {
    }

    public QwenChatModelProvider(
        IProviderCredentialStore credentialStore,
        HttpMessageHandler messageHandler,
        QwenProviderOptions? options = null)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        ArgumentNullException.ThrowIfNull(messageHandler);
        _options = ValidateOptions(options ?? QwenProviderOptions.Default);
        _httpClient = new HttpClient(messageHandler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public ChatProviderDescriptor Descriptor { get; } = new(
        ProviderId,
        "千问",
        DataDestination,
        SendsDataOffDevice: true,
        [
            new ChatModelDescriptor(
                DefaultModelId,
                "千问 3.7 Plus",
                ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput | ChatModelCapabilities.Vision)
        ],
        ChatProviderCredentialKind.ApiKey,
        ChatProviderWorkloads.OrdinaryChat);

    public Task<ChatModelResponse> CompleteAsync(
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback = null,
        CancellationToken cancellationToken = default) =>
        CompleteCoreAsync(request, streamCallback, cancellationToken);

    public Task<ChatProviderHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default) =>
        CheckHealthCoreAsync(cancellationToken);

    public async Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        if (_activeCalls.TryGetValue(turnId, out var active))
        {
            active.Cancel();
            await active.Completed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var activeCalls = _activeCalls.Values.ToArray();
        foreach (var active in activeCalls)
        {
            active.Cancel();
        }

        if (activeCalls.Length > 0)
        {
            await Task.WhenAll(activeCalls.Select(active => active.Completed)).ConfigureAwait(false);
        }

        _httpClient.Dispose();
    }

    private static HttpMessageHandler CreateProductionHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
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
                "千问尚未配置 API Key。",
                checkedAt);
        }

        using var timeout = new CancellationTokenSource(_options.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelPermissionsEndpoint);
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
                if (!HasExactFinalUri(response, ModelPermissionsEndpoint)
                    || (int)response.StatusCode is >= 300 and < 400)
                {
                    return Health(
                        ChatProviderHealthState.Unavailable,
                        true,
                        "千问返回了不安全的跳转，连接已停止。",
                        checkedAt);
                }

                if (response.IsSuccessStatusCode)
                {
                    var successfulPermission = await ReadHealthPermissionAsync(response.Content, linked.Token)
                        .ConfigureAwait(false);
                    return Health(
                        successfulPermission.IsAuthorized
                            ? ChatProviderHealthState.Healthy
                            : successfulPermission.IsBalanceIssue
                                ? ChatProviderHealthState.Degraded
                                : ChatProviderHealthState.Unavailable,
                        true,
                        successfulPermission.IsAuthorized
                            ? "千问已配置并具有所选模型的推理权限。"
                            : successfulPermission.IsBalanceIssue
                                ? "千问账户余额不足或计费状态不可用。"
                                : "千问当前没有返回所选模型的有效推理权限。",
                        checkedAt);
                }

                var failurePermission = await ReadHealthPermissionAsync(response.Content, linked.Token)
                    .ConfigureAwait(false);
                var status = (int)response.StatusCode;
                return Health(
                    status is 402 or 429 || failurePermission.IsBalanceIssue
                        ? ChatProviderHealthState.Degraded
                        : ChatProviderHealthState.Unavailable,
                    true,
                    status switch
                    {
                        401 => "千问 API Key 无效或已失效。",
                        403 => "千问 API Key 没有访问所选模型的权限。",
                        402 => "千问账户余额不足或计费状态不可用。",
                        429 => "千问当前请求过多，请稍后再检查。",
                        >= 500 => "千问服务暂时不可用。",
                        _ => "千问配置当前无法验证。"
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
            return Health(ChatProviderHealthState.Unavailable, true, "连接千问超时。", checkedAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Health(ChatProviderHealthState.Unavailable, true, "现在无法连接千问。", checkedAt);
        }
    }

    private async Task<HealthPermissionEvidence> ReadHealthPermissionAsync(
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return default;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: Math.Min(_options.MaxErrorBodyCharacters, 4096),
            leaveOpen: false);
        var buffer = new char[_options.MaxErrorBodyCharacters + 1];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (length == 0 || length > _options.MaxErrorBodyCharacters)
            {
                return default;
            }

            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var balanceIssue = IsExplicitBalanceIssue(root);
            if (!root.TryGetProperty("success", out var success)
                || success.ValueKind is not JsonValueKind.True
                || !HasEmptyOrNullCode(root))
            {
                return new HealthPermissionEvidence(false, balanceIssue);
            }

            if (!root.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Object
                || !TryReadExactInt32(output, "page_no", 1)
                || !TryReadExactInt32(output, "page_size", 1)
                || !output.TryGetProperty("permissions", out var permissions)
                || permissions.ValueKind != JsonValueKind.Array)
            {
                return new HealthPermissionEvidence(false, balanceIssue);
            }

            if (TryReadExactInt32(output, "total", 0)
                && permissions.GetArrayLength() == 0)
            {
                return new HealthPermissionEvidence(true, false);
            }

            if (!TryReadExactInt32(output, "total", 1)
                || permissions.GetArrayLength() != 1)
            {
                return new HealthPermissionEvidence(false, balanceIssue);
            }

            var permission = permissions[0];
            if (permission.ValueKind != JsonValueKind.Object
                || !HasExactString(permission, "model", DefaultModelId)
                || !permission.TryGetProperty("permissions", out var permissionFlags)
                || permissionFlags.ValueKind != JsonValueKind.Object
                || !permissionFlags.TryGetProperty("inference", out var inference)
                || inference.ValueKind != JsonValueKind.True)
            {
                return new HealthPermissionEvidence(false, balanceIssue);
            }

            return new HealthPermissionEvidence(true, false);
        }
        catch (JsonException)
        {
            return default;
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static bool TryReadExactInt32(JsonElement element, string propertyName, int expected) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var actual)
        && actual == expected;

    private static bool HasExactString(JsonElement element, string propertyName, string expected) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool HasEmptyOrNullCode(JsonElement element) =>
        element.TryGetProperty("code", out var code)
        && (code.ValueKind == JsonValueKind.Null
            || code.ValueKind == JsonValueKind.String && code.GetString() is "");

    private static bool IsExplicitBalanceIssue(JsonElement root)
    {
        string? code = null;
        string? message = null;
        if (root.TryGetProperty("code", out var codeElement)
            && codeElement.ValueKind == JsonValueKind.String)
        {
            code = codeElement.GetString();
        }

        if (root.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String)
        {
            message = messageElement.GetString();
        }

        if (root.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("code", out codeElement)
                && codeElement.ValueKind == JsonValueKind.String)
            {
                code = codeElement.GetString();
            }

            if (error.TryGetProperty("message", out messageElement)
                && messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString();
            }
        }

        return IsBalanceValue(code) || IsBalanceValue(message);
    }

    private static bool IsBalanceValue(string? value) =>
        Contains(value, "balance")
        || Contains(value, "insufficient")
        || Contains(value, "arrearage");

    private async Task<ChatModelResponse> CompleteCoreAsync(
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        request = ChatImageInputs.Prepare(request, ProviderId, supportsImages: true);
        ValidateRequest(request);
        var active = new ActiveCall();
        if (!_activeCalls.TryAdd(request.TurnId, active))
        {
            active.Dispose();
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidRequest,
                "qwen.turn_already_active",
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
                "qwen.cancelled",
                "千问回答已停止。");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.Timeout,
                "qwen.timeout",
                "千问回答超时，这次请求已经安全结束，可以重新发送。");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.Network,
                "qwen.network_error",
                "现在无法连接千问，请检查网络后重试。");
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
        // Copy image bytes before opening the short-lived credential lease.
        // Caller disposal or serialization failure must not strand a lease.
        var requestJson = BuildRequestJson(request);
        var lease = await _credentialStore.OpenLeaseAsync(ProviderId, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null || lease.Secret.IsEmpty)
        {
            lease?.Dispose();
            throw Error(
                request.ModelId,
                ChatModelErrorKind.Configuration,
                "qwen.not_configured",
                "千问尚未配置 API Key，请先在设置中完成配置。");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint);
        httpRequest.Content = new StringContent(
            requestJson,
            Encoding.UTF8,
            "application/json");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

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
            if (!HasExactFinalUri(response, ChatCompletionsEndpoint)
                || (int)response.StatusCode is >= 300 and < 400)
            {
                throw Error(
                    request.ModelId,
                    ChatModelErrorKind.InvalidResponse,
                    "qwen.redirect_rejected",
                    "千问返回了不安全的跳转，本次请求已停止。");
            }

            if (!response.IsSuccessStatusCode)
            {
                var details = await ReadBoundedErrorAsync(response.Content, cancellationToken)
                    .ConfigureAwait(false);
                throw MapHttpError(request.ModelId, response, details);
            }

            return await ParseStreamingResponseAsync(
                    request,
                    response.Content,
                    streamCallback,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private string BuildRequestJson(ChatModelRequest request)
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
            messages.Add(new JsonObject
            {
                ["role"] = message.Role switch
                {
                    ChatMessageRole.System => "system",
                    ChatMessageRole.User => "user",
                    ChatMessageRole.Assistant => "assistant",
                    _ => throw new InvalidOperationException("Qwen tool messages were not validated.")
                },
                ["content"] = BuildMessageContent(message)
            });
        }

        var root = new JsonObject
        {
            ["model"] = DefaultModelId,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["n"] = 1,
            ["enable_thinking"] = false,
            ["preserve_thinking"] = false,
            ["tool_choice"] = "none",
            ["parallel_tool_calls"] = false,
            ["enable_search"] = false,
            ["enable_code_interpreter"] = false
        };

        if (request.Options?.MaxOutputTokens is { } maxOutputTokens)
        {
            root["max_completion_tokens"] = maxOutputTokens;
        }

        if (request.Options?.Temperature is { } temperature)
        {
            root["temperature"] = temperature;
        }

        if (request.Options?.TopP is { } topP)
        {
            root["top_p"] = topP;
        }

        if ((request.ResponseFormat ?? ChatResponseFormat.Text).Kind == ChatResponseFormatKind.JsonObject)
        {
            root["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        return root.ToJsonString();
    }

    private static JsonNode BuildMessageContent(ChatMessage message)
    {
        if (message.Images is not { Count: > 0 }) return JsonValue.Create(message.Content)!;
        var image = message.Images[0];
        var copy = image.CopyPngBytes();
        try
        {
            return new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = message.Content },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(copy) }
                });
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    private async Task<ChatModelResponse> ParseStreamingResponseAsync(
        ChatModelRequest request,
        HttpContent content,
        ChatModelStreamCallback? streamCallback,
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
        long reasoningCharacters = 0;
        long sequence = 0;
        string? providerRequestId = null;
        var finishReason = ChatFinishReason.Unknown;
        ChatModelUsage? usage = null;
        var receivedDone = false;
        var lineReader = new BoundedSseLineReader(
            reader,
            request.ModelId,
            _options.MaxSseResponseCharacters);

        while (await lineReader.ReadLineAsync(_options.MaxSseEventCharacters, cancellationToken)
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
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
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
                    var value = finish.GetString();
                    if (string.Equals(value, "tool_calls", StringComparison.Ordinal))
                    {
                        throw ToolCallsRejected(request.ModelId);
                    }

                    finishReason = MapFinishReason(value);
                }

                if (!choice.TryGetProperty("delta", out var delta)
                    || delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls)
                    && toolCalls.ValueKind != JsonValueKind.Null
                    && (toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() > 0))
                {
                    throw ToolCallsRejected(request.ModelId);
                }

                if (delta.TryGetProperty("reasoning_content", out var reasoning)
                    && reasoning.ValueKind == JsonValueKind.String)
                {
                    reasoningCharacters += reasoning.GetString()?.Length ?? 0;
                    if (reasoningCharacters > _options.MaxReasoningCharacters)
                    {
                        throw Error(
                            request.ModelId,
                            ChatModelErrorKind.InvalidResponse,
                            "qwen.reasoning_too_large",
                            "千问返回的内部推理数据过大，已停止读取。");
                    }
                }

                if (!delta.TryGetProperty("content", out var contentElement)
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
                        "qwen.output_too_large",
                        "千问返回的内容过长，已停止读取。");
                }

                text.Append(deltaText);
                if (streamCallback is not null)
                {
                    await streamCallback(
                            new ChatStreamUpdate(++sequence, deltaText),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
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
                    "qwen.invalid_stream_event",
                    "千问返回了无法读取的流式结果，请稍后重试。");
            }
        }

        if (!receivedDone)
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidResponse,
                "qwen.stream_incomplete",
                "千问的流式回答意外中断，请重新发送。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var finalText = text.ToString();
        if (string.IsNullOrWhiteSpace(finalText))
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.InvalidResponse,
                "qwen.stream_empty",
                "千问没有返回回答内容，请重新发送。");
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
                    "qwen.invalid_json_object",
                    "千问没有返回有效的 JSON Object，请重试。");
            }
        }

        if (streamCallback is not null)
        {
            await streamCallback(
                    new ChatStreamUpdate(++sequence, string.Empty, IsFinal: true),
                    cancellationToken)
                .ConfigureAwait(false);
        }

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
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
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
                "qwen.model_not_found",
                "所选千问模型当前不可用，请在设置中重新选择。");
        }

        return status switch
        {
            400 or 422 => Error(
                modelId,
                ChatModelErrorKind.InvalidRequest,
                "qwen.bad_request",
                "千问无法处理这次请求，请检查输入和模型设置。"),
            401 => Error(
                modelId,
                ChatModelErrorKind.Unauthorized,
                "qwen.unauthorized",
                "千问的 API Key 无效或已失效，请在设置中重新填写。"),
            402 => Error(
                modelId,
                ChatModelErrorKind.InsufficientBalance,
                "qwen.insufficient_balance",
                "千问账户余额不足或计费状态不可用，请检查账户。"),
            403 => Error(
                modelId,
                ChatModelErrorKind.Authorization,
                "qwen.authorization_denied",
                "千问的 API Key 没有访问所选模型的权限。"),
            408 => Error(modelId, ChatModelErrorKind.Timeout, "qwen.request_timeout", "千问请求超时，请重试。"),
            429 => Error(modelId, ChatModelErrorKind.RateLimited, "qwen.rate_limited", "千问当前请求过多，请稍后重试。", ReadRetryAfter(response)),
            >= 500 => Error(modelId, ChatModelErrorKind.Unavailable, "qwen.service_unavailable", "千问服务暂时不可用，请稍后重试。", ReadRetryAfter(response)),
            _ => Error(modelId, ChatModelErrorKind.Unknown, "qwen.request_failed", "千问没有完成这次请求，请稍后重试。")
        };
    }

    private static bool IsModelError(ProviderErrorDetails details) =>
        Contains(details.Code, "model") || Contains(details.Message, "model");

    private static bool Contains(string? value, string expected) =>
        value?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delay
            && delay > TimeSpan.Zero
            && delay <= TimeSpan.FromHours(24))
        {
            return delay;
        }

        return null;
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
            usage.TryGetProperty("prompt_tokens_details", out var promptDetails)
                ? ReadInt64(promptDetails, "cached_tokens")
                : null,
            usage.TryGetProperty("completion_tokens_details", out var completionDetails)
                ? ReadInt64(completionDetails, "reasoning_tokens")
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

        return value.All(character => character <= 0x7f
                                      && (char.IsAsciiLetterOrDigit(character)
                                          || character is '.' or '_' or '-'))
            ? value
            : null;
    }

    private void ValidateRequest(ChatModelRequest request)
    {
        if (!string.Equals(request.ModelId, DefaultModelId, StringComparison.Ordinal))
        {
            throw Error(
                request.ModelId,
                ChatModelErrorKind.ModelNotFound,
                "qwen.model_not_registered",
                "所选千问模型不可用，请在设置中重新选择。");
        }

        if (request.RequestId == Guid.Empty || request.TurnId == Guid.Empty)
        {
            throw Error(request.ModelId, ChatModelErrorKind.InvalidRequest, "qwen.request_identity_invalid", "这次聊天请求无效，请重新发送。");
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw Error(request.ModelId, ChatModelErrorKind.InvalidRequest, "qwen.messages_missing", "聊天内容不能为空。");
        }

        if (request.Messages.Count > _options.MaxMessageCount)
        {
            throw Error(request.ModelId, ChatModelErrorKind.InvalidRequest, "qwen.message_count_exceeded", "这次对话包含的消息过多，请新建话题后重试。");
        }

        long characters = request.SystemPrompt?.Length ?? 0;
        foreach (var message in request.Messages)
        {
            if (message is null || message.Content is null)
            {
                throw Error(request.ModelId, ChatModelErrorKind.InvalidRequest, "qwen.message_invalid", "聊天内容无效，请重新发送。");
            }

            if (message.Role == ChatMessageRole.Tool || !string.IsNullOrWhiteSpace(message.Name))
            {
                throw Error(request.ModelId, ChatModelErrorKind.InvalidRequest, "qwen.tool_messages_not_supported", "当前千问普通聊天不支持工具消息。");
            }

            characters += message.Content.Length;
            if (characters > _options.MaxInputCharacters)
            {
                throw Error(request.ModelId, ChatModelErrorKind.InvalidRequest, "qwen.input_too_large", "这次发送的内容过长，请缩短后重试。");
            }
        }

        if ((request.ResponseFormat ?? ChatResponseFormat.Text).Kind == ChatResponseFormatKind.JsonSchema)
        {
            throw Error(request.ModelId, ChatModelErrorKind.InvalidRequest, "qwen.json_schema_not_supported", "千问当前不支持 JSON Schema 输出，请改用 JSON Object。");
        }

        var options = request.Options;
        if (options?.Temperature is { } temperature && (temperature < 0 || temperature > 2)
            || options?.TopP is { } topP && (topP <= 0 || topP > 1)
            || options?.MaxOutputTokens is { } maxTokens && maxTokens <= 0
            || options?.Seed is not null)
        {
            throw Error(request.ModelId, ChatModelErrorKind.InvalidRequest, "qwen.options_invalid", "聊天模型参数无效，请恢复默认设置后重试。");
        }

        if (options?.Temperature is not null && options.TopP is not null)
        {
            throw Error(request.ModelId, ChatModelErrorKind.InvalidRequest, "qwen.sampling_options_conflict", "千问不能同时设置 Temperature 和 Top P，请只保留一项。");
        }
    }

    private static QwenProviderOptions ValidateOptions(QwenProviderOptions options)
    {
        if (options.RequestTimeout <= TimeSpan.Zero
            || options.RequestTimeout > TimeSpan.FromMinutes(10)
            || options.MaxMessageCount <= 0
            || options.MaxMessageCount > 4096
            || options.MaxInputCharacters <= 0
            || options.MaxInputCharacters > 4_000_000
            || options.MaxOutputCharacters <= 0
            || options.MaxOutputCharacters > 2_000_000
            || options.MaxErrorBodyCharacters <= 0
            || options.MaxErrorBodyCharacters > 65_536
            || options.MaxSseEventCharacters <= 0
            || options.MaxSseEventCharacters > 1_000_000
            || options.MaxSseResponseCharacters <= 0
            || options.MaxSseResponseCharacters > 16_000_000
            || options.MaxReasoningCharacters <= 0
            || options.MaxReasoningCharacters > 4_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Qwen Provider 限制超出安全范围。");
        }

        return options;
    }

    private static ChatModelException ToolCallsRejected(string modelId) =>
        Error(
            modelId,
            ChatModelErrorKind.InvalidResponse,
            "qwen.tool_calls_rejected",
            "千问返回了未授权的工具调用，本次回答已停止。");

    private static ChatModelException Error(
        string? modelId,
        ChatModelErrorKind kind,
        string code,
        string message,
        TimeSpan? retryAfter = null) =>
        new(ProviderId, modelId, new ChatModelError(kind, code, message, retryAfter));

    private static ChatProviderHealth Health(
        ChatProviderHealthState state,
        bool configured,
        string message,
        DateTimeOffset checkedAt) =>
        new(ProviderId, state, configured, message, checkedAt);

    private static bool HasExactFinalUri(HttpResponseMessage response, Uri expected) =>
        response.RequestMessage?.RequestUri is not { } effectiveUri || Uri.Equals(effectiveUri, expected);

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
                        throw Error(modelId, ChatModelErrorKind.InvalidResponse, "qwen.stream_response_too_large", "千问返回的流式数据总量过大，已停止读取。");
                    }
                }

                var newline = Array.IndexOf(_buffer, '\n', _bufferPosition, _bufferLength - _bufferPosition);
                var segmentEnd = newline >= 0 ? newline : _bufferLength;
                var segmentLength = segmentEnd - _bufferPosition;
                var currentLength = line?.Length ?? 0;
                if ((long)currentLength + segmentLength > maximumCharacters)
                {
                    throw Error(modelId, ChatModelErrorKind.InvalidResponse, "qwen.stream_event_too_large", "千问返回的单段流式数据过大，已停止读取。");
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

    private readonly record struct HealthPermissionEvidence(bool IsAuthorized, bool IsBalanceIssue);

    private sealed record ProviderErrorDetails(string? Code, string? Message);
}
