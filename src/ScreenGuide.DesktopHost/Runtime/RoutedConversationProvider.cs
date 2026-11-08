using System.Collections.Concurrent;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>
/// Provider-neutral Stage 2 conversation boundary. It executes only the route
/// persisted with the originating Session Turn. Conversation history remains the
/// source of truth; provider-side threads are never required for continuity.
/// </summary>
public sealed class RoutedConversationProvider(
    IConversationStore conversations,
    ModelRouter router,
    PromptRegistry prompts,
    IAiInvocationStore invocations,
    TimeProvider timeProvider) : IConversationProvider
{
    private const string ChatPromptId = "chat.general";
    internal const string ChatPromptVersion = "4";
    private const string PointerPromptId = "window.pointer.answer";
    private const string PointerPromptVersion = "1";
    private const int MaximumHistoryCharacters = 200_000;
    private readonly ConcurrentDictionary<Guid, ActiveRoute> _activeConversations = new();

    public string ProviderId => "model-router";

    public async Task<ConversationProviderResult> SendAsync(
        ConversationProviderRequest request,
        Func<string, int, Task>? started = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionTurnId is null || request.FrozenRoute is null)
        {
            return Failed(
                "chat_route_not_frozen",
                "这条历史消息没有保存可执行的 AI 路由，因此没有发送。请重新发送一条新消息。");
        }

        if (request.FrozenRoute.Status == SessionTurnRouteStatus.Unavailable)
        {
            return Failed(
                SensitiveDataSanitizer.DiagnosticCode(
                    request.FrozenRoute.FailureCode,
                    "chat_route_unavailable"),
                "这条消息保存的 AI 路由暂不可用，因此没有发送。请检查 AI 设置后重新发送。");
        }

        FrozenChatModelRoute route;
        try
        {
            if (request.FrozenRoute.Status != SessionTurnRouteStatus.Ready
                || string.IsNullOrWhiteSpace(request.FrozenRoute.ProviderId)
                || string.IsNullOrWhiteSpace(request.FrozenRoute.ModelId)
                || string.IsNullOrWhiteSpace(request.FrozenRoute.DataDestination)
                || request.FrozenRoute.SendsDataOffDevice is null)
            {
                return Failed(
                    "frozen_chat_route_invalid",
                    "这条消息保存的 AI 路由不完整，因此没有发送。请重新发送一条新消息。");
            }

            route = router.RestoreFrozenChatRoute(
                request.FrozenRoute.ProviderId,
                request.FrozenRoute.ModelId,
                request.FrozenRoute.DataDestination,
                request.FrozenRoute.SendsDataOffDevice.Value);
        }
        catch (ChatModelException)
        {
            return Failed(
                "frozen_chat_route_invalid",
                "这条消息保存的 AI 路由与当前注册信息不一致，因此没有发送。请重新发送一条新消息。");
        }

        if (request.MemoryOutbound is not null && request.PointerAnswer is not null)
        {
            return Failed(
                PointerAnswerErrorCodes.ContentInvalid,
                "同一条消息不能同时携带记忆和指针区域上下文，因此没有发送。");
        }

        var turns = await conversations.GetTurnsAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false);
        if (request.PointerAnswer is null && !HistoryOriginMatches(turns, route))
        {
            return Failed(
                MemoryOutboundErrorCodes.DerivedHistoryRouteMismatch,
                "这段对话包含由另一条 AI 路由生成的记忆相关回答。为避免跨服务泄露，请新建话题后再发送。");
        }

        var promptId = request.PointerAnswer is null ? ChatPromptId : PointerPromptId;
        var promptVersion = request.PointerAnswer is not null
            ? PointerPromptVersion
            : ChatPromptVersion;
        var prompt = prompts.GetRequired(promptId, promptVersion, route.ProviderId);
        if (request.MemoryOutbound is { } memoryOutbound
            && !ValidateMemoryOutbound(memoryOutbound, route, prompt))
        {
            return Failed(
                MemoryOutboundErrorCodes.ConsentStale,
                "记忆出站确认与当前冻结路由或 Prompt 不一致，因此没有发送。请重新检查并确认。");
        }

        if (request.PointerAnswer is { } pointerAnswer
            && !ValidatePointerAnswer(request, pointerAnswer, route, prompt, timeProvider.GetUtcNow()))
        {
            return Failed(
                PointerAnswerErrorCodes.ConsentStale,
                "指针区域出站确认与当前冻结路由、Prompt 或文本不一致，因此没有发送。请重新检查并确认。");
        }

        IReadOnlyList<ChatMessage> messages;
        if (request.PointerAnswer is { } confirmedPointer)
        {
            messages =
            [
                new ChatMessage(ChatMessageRole.User, confirmedPointer.SerializedContext),
                new ChatMessage(ChatMessageRole.User, request.Message)
            ];
        }
        else
        {
            var history = await conversations.GetMessagesAsync(request.ConversationId, cancellationToken)
                .ConfigureAwait(false);
            messages = BuildHistory(request, history);
        }
        var invocationId = Guid.NewGuid();
        var invocation = new AiInvocationRecord
        {
            Id = invocationId,
            SessionTurnId = request.SessionTurnId.Value,
            ConversationTurnId = request.ConversationTurnId,
            Purpose = AiInvocationPurpose.Conversation,
            ProviderId = route.ProviderId,
            ModelId = route.ModelId,
            PromptId = prompt.PromptId,
            PromptVersion = prompt.Version,
            PromptContentHash = prompt.ContentSha256,
            DataDestination = route.DataDestination,
            Status = AiInvocationStatus.Running,
            StartedAtUtc = timeProvider.GetUtcNow(),
            MemoryOutbound = request.MemoryOutbound?.Audit
        };
        await invocations.StartAsync(invocation, cancellationToken).ConfigureAwait(false);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new ActiveRoute(request.SessionTurnId.Value, linkedCancellation);
        if (!_activeConversations.TryAdd(request.ConversationId, active))
        {
            await invocations.FailAsync(
                    invocationId,
                    AiInvocationStatus.Failed,
                    "conversation_already_active",
                    timeProvider.GetUtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException("这个会话仍有一条消息正在处理。");
        }

        try
        {
            var response = await router.CompleteAsync(
                    route,
                    new ChatModelRequest(
                        invocationId,
                        request.SessionTurnId.Value,
                        route.ModelId,
                        prompt.Content,
                        messages,
                        ResponseFormat: ChatResponseFormat.Text,
                        Prompt: new ChatPromptReference(
                            prompt.PromptId,
                            prompt.Version,
                            prompt.ContentSha256)),
                    cancellationToken: linkedCancellation.Token)
                .ConfigureAwait(false);

            if (linkedCancellation.IsCancellationRequested
                || response.FinishReason == ChatFinishReason.Cancelled)
            {
                var cancelled = await MarkFailedAsync(
                        invocationId,
                        AiInvocationStatus.Cancelled,
                        "cancelled")
                    .ConfigureAwait(false);
                return ResolveTerminal(cancelled, Cancelled());
            }

            if (response.FinishReason == ChatFinishReason.Error
                || string.IsNullOrWhiteSpace(response.Text))
            {
                var failed = await MarkFailedAsync(
                        invocationId,
                        AiInvocationStatus.Failed,
                        "invalid_provider_response")
                    .ConfigureAwait(false);
                return ResolveTerminal(
                    failed,
                    Failed(
                        "invalid_provider_response",
                        "这个 AI 服务没有返回可用回答，请稍后再试。"));
            }

            var terminal = await invocations.CompleteAsync(
                    invocationId,
                    response.FinishReason.ToString(),
                    response.Usage is null
                        ? null
                        : new AiTokenUsage(
                            response.Usage.InputTokens ?? 0,
                            response.Usage.OutputTokens ?? 0,
                            response.Usage.TotalTokens ?? 0),
                    response.Metadata.ProviderRequestId,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return ResolveTerminal(
                terminal,
                new ConversationProviderResult(
                    ConversationProviderOutcome.Succeeded,
                    ExternalThreadId: null,
                    response.Text.Trim(),
                    response.Metadata.ProviderRequestId,
                    ProcessId: null));
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            var cancelled = await MarkFailedAsync(
                    invocationId,
                    AiInvocationStatus.Cancelled,
                    "cancelled")
                .ConfigureAwait(false);
            return ResolveTerminal(cancelled, Cancelled());
        }
        catch (ChatModelException exception)
        {
            var diagnosticCode = SensitiveDataSanitizer.DiagnosticCode(
                exception.Error.Code,
                "chat_provider_error");
            var status = exception.Error.Kind == ChatModelErrorKind.Cancelled
                ? AiInvocationStatus.Cancelled
                : AiInvocationStatus.Failed;
            var terminal = await MarkFailedAsync(invocationId, status, diagnosticCode)
                .ConfigureAwait(false);
            var requestedResult = status == AiInvocationStatus.Cancelled
                ? Cancelled()
                : Failed(
                    StableFailureCode(exception.Error.Kind),
                    StableFailureMessage(exception.Error.Kind));
            return ResolveTerminal(terminal, requestedResult);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            var failed = await MarkFailedAsync(
                    invocationId,
                    AiInvocationStatus.Failed,
                    "chat_provider_error")
                .ConfigureAwait(false);
            return ResolveTerminal(
                failed,
                Failed("chat_provider_error", "这次回答没有成功，请稍后再试。"));
        }
        finally
        {
            if (_activeConversations.TryGetValue(request.ConversationId, out var current)
                && ReferenceEquals(current, active))
            {
                _activeConversations.TryRemove(request.ConversationId, out _);
            }
        }
    }

    public async Task CancelAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_activeConversations.TryGetValue(conversationId, out var active))
        {
            return;
        }

        await router.CancelAsync(active.TurnId, CancellationToken.None).ConfigureAwait(false);
        active.Cancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        var active = _activeConversations.Values.ToArray();
        foreach (var item in active)
        {
            await router.CancelAsync(item.TurnId, CancellationToken.None).ConfigureAwait(false);
            item.Cancellation.Cancel();
        }

        _activeConversations.Clear();
    }

    private Task<AiInvocationTransitionResult> MarkFailedAsync(
        Guid invocationId,
        AiInvocationStatus status,
        string code) =>
        invocations.FailAsync(
            invocationId,
            status,
            code,
            timeProvider.GetUtcNow(),
            CancellationToken.None);

    private static IReadOnlyList<ChatMessage> BuildHistory(
        ConversationProviderRequest request,
        IReadOnlyList<ConversationMessageRecord> history)
    {
        if (history.Count == 0
            || history[^1].Role != ConversationMessageRole.User
            || !string.Equals(history[^1].Content, request.Message, StringComparison.Ordinal))
        {
            throw new InvalidDataException("当前对话历史与正在处理的消息不一致。");
        }

        var characterCount = 0;
        var memoryOffset = request.MemoryOutbound is null ? 0 : 1;
        var messages = new ChatMessage[history.Count + memoryOffset];
        for (var index = 0; index < history.Count; index++)
        {
            var item = history[index];
            if (item.ConversationId != request.ConversationId)
            {
                throw new InvalidDataException("对话历史包含其他会话的消息。");
            }

            characterCount = checked(characterCount + item.Content.Length);
            if (characterCount > MaximumHistoryCharacters)
            {
                throw new ChatModelException(
                    "model-router",
                    null,
                    new ChatModelError(
                        ChatModelErrorKind.InvalidRequest,
                        "conversation_context_too_large",
                        "当前会话内容过长，请新建一个会话后继续。",
                        IsRetryable: false));
            }

            var targetIndex = index;
            if (request.MemoryOutbound is { } memoryOutbound && index == history.Count - 1)
            {
                characterCount = checked(characterCount + memoryOutbound.SerializedContext.Length);
                if (characterCount > MaximumHistoryCharacters)
                {
                    throw new ChatModelException(
                        "model-router",
                        null,
                        new ChatModelError(
                            ChatModelErrorKind.InvalidRequest,
                            "conversation_context_too_large",
                            "当前会话内容过长，请新建一个会话后继续。",
                            IsRetryable: false));
                }

                messages[index] = new ChatMessage(
                    ChatMessageRole.User,
                    memoryOutbound.SerializedContext);
                targetIndex++;
            }

            messages[targetIndex] = new ChatMessage(
                item.Role switch
                {
                    ConversationMessageRole.User => ChatMessageRole.User,
                    ConversationMessageRole.Assistant => ChatMessageRole.Assistant,
                    ConversationMessageRole.System => ChatMessageRole.System,
                    _ => throw new InvalidDataException("对话消息角色无效。")
                },
                item.Content);
        }

        return messages;
    }

    private static bool ValidateMemoryOutbound(
        MemoryOutboundEnvelope envelope,
        FrozenChatModelRoute route,
        PromptDefinition prompt)
    {
        if (string.IsNullOrWhiteSpace(envelope.SerializedContext)
            || envelope.SerializedContext.Length > MemoryOutboundLimits.MaximumSerializedContextCharacters
            || envelope.Audit.ItemCount is < MemoryOutboundLimits.MinimumItems or > MemoryOutboundLimits.MaximumItems
            || envelope.Audit.Items.Count != envelope.Audit.ItemCount
            || envelope.Audit.TotalCharacters > MemoryOutboundLimits.MaximumContentCharacters
            || !string.Equals(envelope.Audit.ProviderId, route.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(envelope.Audit.ModelId, route.ModelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(envelope.Audit.PromptId, prompt.PromptId, StringComparison.Ordinal)
            || !string.Equals(envelope.Audit.PromptVersion, prompt.Version, StringComparison.Ordinal)
            || !string.Equals(envelope.Audit.PromptContentHash, prompt.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return string.Equals(
                envelope.Audit.DestinationOrigin,
                MemoryOutboundContract.NormalizeHttpsOrigin(route.DataDestination),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (MemoryValidationException)
        {
            return false;
        }
    }

    private static bool ValidatePointerAnswer(
        ConversationProviderRequest request,
        PointerAnswerEnvelope envelope,
        FrozenChatModelRoute route,
        PromptDefinition prompt,
        DateTimeOffset nowUtc)
    {
        var audit = envelope.Audit;
        if (request.SessionTurnId is null
            || audit.SessionTurnId != request.SessionTurnId
            || audit.ConsentId == Guid.Empty
            || audit.AnchorId == Guid.Empty
            || audit.ConsumedAtUtc is null
            || nowUtc < audit.PreparedAtUtc
            || nowUtc >= audit.ExpiresAtUtc
            || string.IsNullOrWhiteSpace(envelope.SerializedContext)
            || envelope.SerializedContext.Length > PointerAnswerOutboundContract.MaximumSerializedContextCharacters
            || audit.QuestionCharacterCount != request.Message.Length
            || audit.OcrCharacterCount is < 0 or > PointerAnswerOutboundContract.MaximumOcrCharacters
            || audit.OcrLineCount < 0
            || audit.RegionWidth <= 0
            || audit.RegionHeight <= 0
            || (audit.RegionSource != "uia-element" && audit.RegionSource != "pointer-centered")
            || !string.Equals(audit.QuestionHash, PointerAnswerOutboundContract.HashText(request.Message), StringComparison.Ordinal)
            || !string.Equals(audit.ContextHash, PointerAnswerOutboundContract.HashText(envelope.SerializedContext), StringComparison.Ordinal)
            || !PointerAnswerOutboundContract.ContextMatchesAudit(envelope.SerializedContext, audit)
            || !string.Equals(audit.ProviderId, route.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(audit.ModelId, route.ModelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(audit.PromptId, prompt.PromptId, StringComparison.Ordinal)
            || !string.Equals(audit.PromptVersion, prompt.Version, StringComparison.Ordinal)
            || !string.Equals(audit.PromptContentHash, prompt.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return string.Equals(
                audit.DestinationOrigin,
                PointerAnswerOutboundContract.NormalizeHttpsOrigin(route.DataDestination),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HistoryOriginMatches(
        IReadOnlyList<ConversationTurnRecord> turns,
        FrozenChatModelRoute route)
    {
        var derived = turns.Where(turn => turn.MemoryDerived).ToArray();
        if (derived.Length == 0)
        {
            return true;
        }

        string origin;
        try
        {
            origin = MemoryOutboundContract.NormalizeHttpsOrigin(route.DataDestination);
        }
        catch (MemoryValidationException)
        {
            return false;
        }

        return derived.All(turn => turn.MemoryOutbound is { } audit
            && string.Equals(audit.ProviderId, route.ProviderId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(audit.ModelId, route.ModelId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(audit.DestinationOrigin, origin, StringComparison.OrdinalIgnoreCase));
    }

    private static ConversationProviderResult Cancelled() => new(
        ConversationProviderOutcome.Cancelled,
        ExternalThreadId: null,
        Reply: null,
        ProviderMessageId: null,
        ProcessId: null,
        "cancelled",
        "回答已停止。");

    private static ConversationProviderResult Interrupted(string? failureCode = null) => new(
        ConversationProviderOutcome.Interrupted,
        ExternalThreadId: null,
        Reply: null,
        ProviderMessageId: null,
        ProcessId: null,
        SensitiveDataSanitizer.DiagnosticCode(failureCode, "host_restarted"),
        "主程序已重新启动，这次回答已中断。请重新发送。");

    private static ConversationProviderResult Failed(string code, string message) => new(
        ConversationProviderOutcome.Failed,
        ExternalThreadId: null,
        Reply: null,
        ProviderMessageId: null,
        ProcessId: null,
        code,
        message);

    private static ConversationProviderResult ResolveTerminal(
        AiInvocationTransitionResult transition,
        ConversationProviderResult requestedResult)
    {
        if (transition.RequestedStatusWon)
        {
            return requestedResult;
        }

        return transition.Current.Status switch
        {
            AiInvocationStatus.Cancelled => Cancelled(),
            AiInvocationStatus.Interrupted => Interrupted(transition.Current.FailureCode),
            AiInvocationStatus.Failed => Failed(
                "chat_provider_error",
                "这次回答没有成功，请稍后再试。"),
            AiInvocationStatus.Succeeded => requestedResult,
            _ => Failed(
                "invocation_terminal_conflict",
                "这次回答的状态无法确认，请重新发送。")
        };
    }

    private static string StableFailureCode(ChatModelErrorKind kind) => kind switch
    {
        ChatModelErrorKind.InvalidRequest => "invalid_request",
        ChatModelErrorKind.Configuration => "configuration",
        ChatModelErrorKind.Unauthorized => "unauthorized",
        ChatModelErrorKind.Authorization => "authorization",
        ChatModelErrorKind.PolicyDisabled => "disabled_by_security_policy",
        ChatModelErrorKind.ModelNotFound => "model_not_found",
        ChatModelErrorKind.RateLimited => "rate_limited",
        ChatModelErrorKind.InsufficientBalance => "insufficient_balance",
        ChatModelErrorKind.Timeout => "timeout",
        ChatModelErrorKind.Cancelled => "cancelled",
        ChatModelErrorKind.Network => "network",
        ChatModelErrorKind.InvalidResponse => "invalid_response",
        ChatModelErrorKind.Unavailable => "unavailable",
        _ => "chat_provider_error"
    };

    private static string StableFailureMessage(ChatModelErrorKind kind) => kind switch
    {
        ChatModelErrorKind.Configuration =>
            "这个 AI 服务尚未配置，请先在设置中完成配置。",
        ChatModelErrorKind.Unauthorized =>
            "这个 AI 服务的凭据无效或已失效，请在设置中重新填写。",
        ChatModelErrorKind.Authorization =>
            "这个 AI 服务拒绝了当前账户的访问权限，请检查账户授权。",
        ChatModelErrorKind.PolicyDisabled =>
            "出于安全原因，当前版本暂不提供 Codex 普通聊天。你可以改用其他已配置的聊天服务；编程任务不受影响。",
        ChatModelErrorKind.ModelNotFound =>
            "所选 AI 模型当前不可用，请在设置中重新选择。",
        ChatModelErrorKind.RateLimited =>
            "这个 AI 服务当前请求过多，请稍后再试。",
        ChatModelErrorKind.InsufficientBalance =>
            "这个 AI 服务的账户余额或计费状态不可用，请检查账户。",
        ChatModelErrorKind.Timeout =>
            "这个 AI 服务回答超时，请稍后重新发送。",
        ChatModelErrorKind.Network =>
            "现在无法连接这个 AI 服务，请检查网络后重试。",
        ChatModelErrorKind.InvalidRequest =>
            "这个 AI 服务无法处理本次请求，请检查输入和模型设置。",
        ChatModelErrorKind.InvalidResponse =>
            "这个 AI 服务没有返回可用回答，请稍后再试。",
        ChatModelErrorKind.Unavailable =>
            "这个 AI 服务暂时不可用，请稍后再试。",
        _ => "这次回答没有成功，请稍后再试。"
    };

    private sealed record ActiveRoute(Guid TurnId, CancellationTokenSource Cancellation);
}
