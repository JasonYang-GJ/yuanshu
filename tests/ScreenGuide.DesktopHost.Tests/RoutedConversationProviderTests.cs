using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class RoutedConversationProviderTests
{
    [Theory]
    [InlineData("provider-a", "model-a", "你好")]
    [InlineData("provider-b", "model-b", "请详细解释月亮的形成")]
    [InlineData("provider-a", "model-a", "打开我桌面上的 Steam")]
    public async Task OrdinaryChatUsesConciseCurrentQuestionPromptWithoutChangingHistoryOrProviderOptions(
        string providerId, string modelId, string input)
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "上一条是写一段关于蓝鹭的故事");
        history.Add(ConversationMessageRole.Assistant, "蓝鹭飞过一条小河。");
        history.Add(ConversationMessageRole.User, input);
        var provider = new RecordingChatProvider(providerId, modelId);
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(new ChatProviderRegistry([provider]),
                new MutableAiSettingsStore(providerId, modelId)),
            await LoadRepositoryPromptsAsync(), invocations, TimeProvider.System);

        var result = await routed.SendAsync(Request(conversationId, input, provider));

        Assert.Equal(ConversationProviderOutcome.Succeeded, result.Outcome);
        var request = Assert.Single(provider.Requests);
        Assert.NotNull(request.Prompt);
        Assert.Equal("4", request.Prompt.Version);
        Assert.Contains("默认只给一句直接答案", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("一般控制在 60 个中文字以内", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("明确要求详细解释、步骤、代码、创作", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("只回答本条用户输入", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("不要续写旧文本", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("不要做未经请求的心理分析", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("当前请求只允许进行文字回答", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("这次普通聊天没有执行任何电脑操作", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("这些能力说明不授予本聊天模型动作权限", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("打开 Steam", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("有限的多步控件任务", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(["上一条是写一段关于蓝鹭的故事", "蓝鹭飞过一条小河。", input],
            request.Messages.Select(item => item.Content));
        Assert.Null(request.Options);
        Assert.Equal(request.Prompt.Version, Assert.Single(invocations.Started).PromptVersion);
        Assert.Equal(request.Prompt.ContentSha256, Assert.Single(invocations.Started).PromptContentHash);
    }

    [Fact]
    public async Task PersistsSessionAndConversationTurnIdsForOrdinaryChatInvocation()
    {
        var conversationId = Guid.NewGuid();
        var conversationTurnId = Guid.NewGuid();
        var sessionTurnId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "记录关联");
        var provider = new RecordingChatProvider("provider-a", "model-a");
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(
                new ChatProviderRegistry([provider]),
                new MutableAiSettingsStore("provider-a", "model-a")),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);

        var result = await routed.SendAsync(new ConversationProviderRequest(
            conversationId,
            conversationTurnId,
            "记录关联",
            ExternalThreadId: null,
            sessionTurnId,
            Frozen(provider)));

        Assert.Equal(ConversationProviderOutcome.Succeeded, result.Outcome);
        var invocation = Assert.Single(invocations.Started);
        Assert.Equal(sessionTurnId, invocation.SessionTurnId);
        Assert.Equal(conversationTurnId, invocation.ConversationTurnId);
    }

    [Fact]
    public async Task PersistedRoutesAtoBtoA_ignore_current_settings_and_keep_supplier_neutral_history()
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        var providerA = new RecordingChatProvider("provider-a", "model-a");
        var providerB = new RecordingChatProvider("provider-b", "model-b");
        var settings = new MutableAiSettingsStore("provider-b", "model-b");
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(new ChatProviderRegistry([providerA, providerB]), settings),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);

        history.Add(ConversationMessageRole.User, "甲代表蓝鹭");
        var first = await routed.SendAsync(Request(conversationId, "甲代表蓝鹭", providerA));
        history.Add(ConversationMessageRole.Assistant, first.Reply!);

        history.Add(ConversationMessageRole.User, "刚才那个甲代表什么？");
        var second = await routed.SendAsync(Request(conversationId, "刚才那个甲代表什么？", providerB));
        history.Add(ConversationMessageRole.Assistant, second.Reply!);

        history.Add(ConversationMessageRole.User, "继续，并引用第二个回答");
        var third = await routed.SendAsync(Request(conversationId, "继续，并引用第二个回答", providerA));

        Assert.Equal(ConversationProviderOutcome.Succeeded, third.Outcome);
        Assert.Equal(2, providerA.Requests.Count);
        Assert.Single(providerB.Requests);
        Assert.Equal(
            ["甲代表蓝鹭", "provider-a 回答 1", "刚才那个甲代表什么？"],
            providerB.Requests[0].Messages.Select(item => item.Content).ToArray());
        Assert.Equal(
            [
                "甲代表蓝鹭",
                "provider-a 回答 1",
                "刚才那个甲代表什么？",
                "provider-b 回答 1",
                "继续，并引用第二个回答"
            ],
            providerA.Requests[1].Messages.Select(item => item.Content).ToArray());
        Assert.Equal(["provider-a", "provider-b", "provider-a"],
            invocations.Started.Select(item => item.ProviderId).ToArray());
        Assert.All(invocations.Started, item =>
        {
            Assert.Equal("chat.general", item.PromptId);
            Assert.Equal("4", item.PromptVersion);
            Assert.Equal(AiInvocationPurpose.Conversation, item.Purpose);
        });
        Assert.Equal(3, invocations.Completed.Count);
        Assert.Equal(0, settings.LoadCount);
    }

    [Fact]
    public async Task Cancel_targets_the_provider_frozen_for_the_active_conversation_turn()
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "请给一个很长的回答");
        var provider = new BlockingChatProvider();
        var settings = new MutableAiSettingsStore("provider-a", "model-a");
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(new ChatProviderRegistry([provider]), settings),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);

        var sessionTurnId = Guid.NewGuid();
        var running = routed.SendAsync(Request(
            conversationId,
            "请给一个很长的回答",
            provider,
            sessionTurnId));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await routed.CancelAsync(conversationId);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ConversationProviderOutcome.Cancelled, result.Outcome);
        Assert.Equal(sessionTurnId, provider.ActiveTurnId);
        Assert.Equal(provider.ActiveTurnId, provider.CancelledTurnId);
        Assert.DoesNotContain("旧回答", result.Reply ?? string.Empty, StringComparison.Ordinal);
        Assert.Single(invocations.Failed);
        Assert.Equal(AiInvocationStatus.Cancelled, invocations.Failed[0].Status);
    }

    [Theory]
    [InlineData(
        AiInvocationStatus.Interrupted,
        "host_restarted",
        ConversationProviderOutcome.Interrupted)]
    [InlineData(
        AiInvocationStatus.Cancelled,
        "cancelled",
        ConversationProviderOutcome.Cancelled)]
    public async Task LateProviderSuccessCannotReturnBodyAfterAnEarlierTerminalState(
        AiInvocationStatus winningStatus,
        string winningFailureCode,
        ConversationProviderOutcome expectedOutcome)
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "等待迟到回答");
        var provider = new DelayedChatProvider();
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(
                new ChatProviderRegistry([provider]),
                new MutableAiSettingsStore("provider-a", "model-a")),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);

        var running = routed.SendAsync(Request(
            conversationId,
            "等待迟到回答",
            provider));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var invocation = Assert.Single(invocations.Started);
        await invocations.FailAsync(
            invocation.Id,
            winningStatus,
            winningFailureCode,
            DateTimeOffset.UtcNow);

        provider.AllowResponse.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Null(result.Reply);
        Assert.Equal(winningFailureCode, result.FailureCode);
        Assert.Equal(winningStatus, (await invocations.GetAsync(invocation.Id))?.Status);
    }

    [Fact]
    public async Task LateProviderFailurePreservesEarlierInterruptedOutcome()
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "等待迟到失败");
        var provider = new DelayedChatProvider(failAfterRelease: true);
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(
                new ChatProviderRegistry([provider]),
                new MutableAiSettingsStore("provider-a", "model-a")),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);

        var running = routed.SendAsync(Request(
            conversationId,
            "等待迟到失败",
            provider));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var invocation = Assert.Single(invocations.Started);
        await invocations.FailAsync(
            invocation.Id,
            AiInvocationStatus.Interrupted,
            "host_restarted",
            DateTimeOffset.UtcNow);

        provider.AllowResponse.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ConversationProviderOutcome.Interrupted, result.Outcome);
        Assert.Null(result.Reply);
        Assert.Equal("host_restarted", result.FailureCode);
        Assert.Equal(AiInvocationStatus.Interrupted,
            (await invocations.GetAsync(invocation.Id))?.Status);
    }

    [Fact]
    public async Task Provider_failure_returns_only_the_typed_safe_message()
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "你好");
        var provider = new FailingChatProvider();
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(
                new ChatProviderRegistry([provider]),
                new MutableAiSettingsStore("provider-a", "model-a")),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);

        var result = await routed.SendAsync(Request(conversationId, "你好", provider));

        Assert.Equal(ConversationProviderOutcome.Failed, result.Outcome);
        Assert.Equal("rate_limited", result.FailureCode);
        Assert.Equal("这个 AI 服务当前请求过多，请稍后再试。", result.FailureMessage);
        Assert.DoesNotContain("RAW_SECRET", result.FailureMessage, StringComparison.Ordinal);
        Assert.Equal("provider-a.rate_limited", Assert.Single(invocations.Failed).FailureCode);
    }

    [Fact]
    public async Task MaliciousProviderErrorCannotCrossTheSensitiveOuterBoundary()
    {
        const string canaries = "Bearer fake-token API_KEY=sk-abcdefghijklmnop C:\\Users\\person\\private.txt "
                                + "PROMPT_CANARY CONVERSATION_CANARY RAW_RESPONSE_CANARY at Secret.Stack()";
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "你好");
        var provider = new FailingChatProvider(new ChatModelError(
            ChatModelErrorKind.Authorization,
            canaries,
            canaries));
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(
                new ChatProviderRegistry([provider]),
                new MutableAiSettingsStore("provider-a", "model-a")),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);

        var result = await routed.SendAsync(Request(conversationId, "你好", provider));

        Assert.Equal(ConversationProviderOutcome.Failed, result.Outcome);
        Assert.Equal("authorization", result.FailureCode);
        Assert.Equal("这个 AI 服务拒绝了当前账户的访问权限，请检查账户授权。", result.FailureMessage);
        var exposed = $"{result}|{Assert.Single(invocations.Failed).FailureCode}";
        Assert.DoesNotContain("fake-token", exposed, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijklmnop", exposed, StringComparison.Ordinal);
        Assert.DoesNotContain("person", exposed, StringComparison.Ordinal);
        Assert.DoesNotContain("PROMPT_CANARY", exposed, StringComparison.Ordinal);
        Assert.DoesNotContain("CONVERSATION_CANARY", exposed, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW_RESPONSE_CANARY", exposed, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret.Stack", exposed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderFailureNeverFallsBackOrResendsToAnotherProvider()
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "NO_FALLBACK_CANARY");
        var selected = new FailingChatProvider();
        var other = new RecordingChatProvider("provider-b", "model-b");
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(
                new ChatProviderRegistry([selected, other]),
                new MutableAiSettingsStore("provider-b", "model-b")),
            await LoadRepositoryPromptsAsync(),
            new RecordingInvocationStore(),
            TimeProvider.System);

        var result = await routed.SendAsync(
            Request(conversationId, "NO_FALLBACK_CANARY", selected));

        Assert.Equal(ConversationProviderOutcome.Failed, result.Outcome);
        Assert.Equal(1, selected.CompleteCalls);
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task MissingSessionRouteContextFailsClosedBeforePromptInvocationOrProvider()
    {
        var conversationId = Guid.NewGuid();
        var provider = new RecordingChatProvider("provider-a", "model-a");
        var invocations = new RecordingInvocationStore();
        var (emptyPrompts, promptDirectory) = await LoadEmptyPromptRegistryAsync();
        var routed = new RoutedConversationProvider(
            new HistoryStore(conversationId),
            new ModelRouter(
                new ChatProviderRegistry([provider]),
                new MutableAiSettingsStore("provider-a", "model-a")),
            emptyPrompts,
            invocations,
            TimeProvider.System);
        try
        {
            var result = await routed.SendAsync(new ConversationProviderRequest(
                conversationId,
                Guid.NewGuid(),
                "ROUTE_CONTEXT_CANARY",
                null));

            Assert.Equal(ConversationProviderOutcome.Failed, result.Outcome);
            Assert.Equal("chat_route_not_frozen", result.FailureCode);
            Assert.Empty(provider.Requests);
            Assert.Empty(invocations.Started);
        }
        finally
        {
            Directory.Delete(promptDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UnavailableAndInvalidFrozenRoutesFailClosedWithoutProviderOrInvocation()
    {
        var conversationId = Guid.NewGuid();
        var provider = new RecordingChatProvider("provider-a", "model-a");
        var settings = new MutableAiSettingsStore("provider-a", "model-a");
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            new HistoryStore(conversationId),
            new ModelRouter(new ChatProviderRegistry([provider]), settings),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);
        var sessionTurnId = Guid.NewGuid();
        var unavailable = new SessionTurnFrozenRoute
        {
            Status = SessionTurnRouteStatus.Unavailable,
            FrozenAtUtc = DateTimeOffset.UtcNow,
            FailureCode = "ai_settings_invalid"
        };
        var invalid = Frozen(provider) with { DataDestination = "mismatched destination" };

        var unavailableResult = await routed.SendAsync(new ConversationProviderRequest(
            conversationId,
            Guid.NewGuid(),
            "unavailable",
            null,
            sessionTurnId,
            unavailable));
        var invalidResult = await routed.SendAsync(new ConversationProviderRequest(
            conversationId,
            Guid.NewGuid(),
            "invalid",
            null,
            Guid.NewGuid(),
            invalid));

        Assert.Equal("ai_settings_invalid", unavailableResult.FailureCode);
        Assert.Equal("frozen_chat_route_invalid", invalidResult.FailureCode);
        Assert.Empty(provider.Requests);
        Assert.Empty(invocations.Started);
        Assert.Equal(0, settings.LoadCount);
    }

    [Fact]
    public async Task UserSelectedMemoryUsesConcisePromptAndAnEphemeralJsonUserMessageBeforeCurrentInput()
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "当前问题");
        var provider = new RecordingChatProvider(
            "provider-a",
            "model-a",
            "https://provider-a.example/v1/chat");
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(
                new ChatProviderRegistry([provider]),
                new MutableAiSettingsStore("provider-a", "model-a")),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);
        var memoryId = Guid.NewGuid();
        const string context = "{\"type\":\"USER_SELECTED_MEMORY_CONTEXT_V1\",\"items\":[{\"category\":\"UserPreference\",\"scope\":\"Global\",\"title\":\"称呼\",\"body\":\"叫我小元\"}]}";
        var audit = MemoryAudit(provider, memoryId);

        var result = await routed.SendAsync(new ConversationProviderRequest(
            conversationId,
            Guid.NewGuid(),
            "当前问题",
            null,
            Guid.NewGuid(),
            Frozen(provider),
            new MemoryOutboundEnvelope(context, audit)));

        Assert.Equal(ConversationProviderOutcome.Succeeded, result.Outcome);
        var request = Assert.Single(provider.Requests);
        Assert.Equal("4", request.Prompt?.Version);
        Assert.Equal(audit.PromptContentHash, request.Prompt?.ContentSha256);
        Assert.Contains("默认只给一句直接答案", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal([context, "当前问题"], request.Messages.Select(message => message.Content));
        Assert.All(request.Messages, message => Assert.Equal(ChatMessageRole.User, message.Role));
        var invocation = Assert.Single(invocations.Started);
        Assert.Equal(audit.ConsentId, invocation.MemoryOutbound?.ConsentId);
        Assert.Equal(memoryId, Assert.Single(invocation.MemoryOutbound!.Items).MemoryId);
        Assert.DoesNotContain(memoryId.ToString("D"), context, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("2", "82A414F8A3E829F957BA77614A993C908119D8C6A499C1B4717E2139DF656B67")]
    [InlineData("3", "08A2DF50AAEBB677D727299C7C5B745DAF261BA06E9B2C66070125560F588013")]
    [InlineData("4", "08A2DF50AAEBB677D727299C7C5B745DAF261BA06E9B2C66070125560F588013")]
    public async Task MemoryConsentForPreviousPromptVersionOrHashFailsBeforeInvocationOrProvider(
        string claimedVersion, string claimedHash)
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "当前问题");
        var provider = new RecordingChatProvider("provider-a", "model-a", "https://provider-a.example/v1/chat");
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(new ChatProviderRegistry([provider]),
                new MutableAiSettingsStore("provider-a", "model-a")),
            await LoadRepositoryPromptsAsync(), invocations, TimeProvider.System);
        var audit = MemoryAudit(provider, Guid.NewGuid()) with
        {
            PromptVersion = claimedVersion,
            PromptContentHash = claimedHash
        };
        var result = await routed.SendAsync(new ConversationProviderRequest(
            conversationId, Guid.NewGuid(), "当前问题", null, Guid.NewGuid(), Frozen(provider),
            new MemoryOutboundEnvelope("{\"type\":\"USER_SELECTED_MEMORY_CONTEXT_V1\"}", audit)));

        Assert.Equal(ConversationProviderOutcome.Failed, result.Outcome);
        Assert.Equal(MemoryOutboundErrorCodes.ConsentStale, result.FailureCode);
        Assert.Empty(provider.Requests);
        Assert.Empty(invocations.Started);
    }

    [Theory]
    [InlineData("deepseek", "deepseek-chat")]
    [InlineData("qwen", "qwen3.7-plus")]
    public async Task ConfirmedPointerAnswerUsesOneProviderNeutralTextOnlyRequestWithoutConversationHistory(
        string providerId,
        string modelId)
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "OLD_USER_SENTINEL");
        history.Add(ConversationMessageRole.Assistant, "OLD_ASSISTANT_SENTINEL");
        history.Add(ConversationMessageRole.User, "这里是什么按钮？");
        var provider = new RecordingChatProvider(
            providerId,
            modelId,
            $"https://{providerId}.example/v1/chat");
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(
                new ChatProviderRegistry([provider]),
                new MutableAiSettingsStore(providerId, modelId)),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);
        const string ocr = "保存并继续";
        var context = PointerAnswerOutboundContract.SerializeContext(
            ocr,
            lineCount: 1,
            regionWidth: 320,
            regionHeight: 120,
            regionSource: "uia-element");
        var audit = PointerAudit(provider, "这里是什么按钮？", ocr, context);

        var result = await routed.SendAsync(new ConversationProviderRequest(
            conversationId,
            Guid.NewGuid(),
            "这里是什么按钮？",
            null,
            audit.SessionTurnId,
            Frozen(provider),
            MemoryOutbound: null,
            PointerAnswer: new PointerAnswerEnvelope(context, audit)));

        Assert.Equal(ConversationProviderOutcome.Succeeded, result.Outcome);
        var request = Assert.Single(provider.Requests);
        Assert.Equal("window.pointer.answer", request.Prompt?.PromptId);
        Assert.Equal("1", request.Prompt?.Version);
        Assert.Equal([context, "这里是什么按钮？"], request.Messages.Select(item => item.Content));
        Assert.All(request.Messages, item => Assert.Equal(ChatMessageRole.User, item.Role));
        Assert.DoesNotContain(request.Messages, item => item.Content.Contains("OLD_", StringComparison.Ordinal));
        var invocation = Assert.Single(invocations.Started);
        Assert.Null(invocation.MemoryOutbound);
        Assert.DoesNotContain(ocr, invocation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("这里是什么按钮？", invocation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ocr, audit.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryDerivedHistoryWithAnotherOriginFailsBeforePromptInvocationOrProvider()
    {
        var conversationId = Guid.NewGuid();
        var history = new HistoryStore(conversationId);
        history.Add(ConversationMessageRole.User, "旧问题");
        history.Add(ConversationMessageRole.Assistant, "旧回答");
        history.AddDerivedTurn(MemoryAudit(
            new RecordingChatProvider("provider-a", "model-a", "https://a.example"),
            Guid.NewGuid()));
        history.Add(ConversationMessageRole.User, "继续");
        var providerB = new RecordingChatProvider(
            "provider-b",
            "model-b",
            "https://b.example/v1/chat");
        var invocations = new RecordingInvocationStore();
        var routed = new RoutedConversationProvider(
            history,
            new ModelRouter(
                new ChatProviderRegistry([providerB]),
                new MutableAiSettingsStore("provider-b", "model-b")),
            await LoadRepositoryPromptsAsync(),
            invocations,
            TimeProvider.System);

        var result = await routed.SendAsync(Request(conversationId, "继续", providerB));

        Assert.Equal(ConversationProviderOutcome.Failed, result.Outcome);
        Assert.Equal(MemoryOutboundErrorCodes.DerivedHistoryRouteMismatch, result.FailureCode);
        Assert.Empty(providerB.Requests);
        Assert.Empty(invocations.Started);
    }

    private static ConversationProviderRequest Request(
        Guid conversationId,
        string message,
        IChatModelProvider provider,
        Guid? sessionTurnId = null) =>
        new(
            conversationId,
            Guid.NewGuid(),
            message,
            null,
            sessionTurnId ?? Guid.NewGuid(),
            Frozen(provider));

    private static SessionTurnFrozenRoute Frozen(IChatModelProvider provider) => new()
    {
        Status = SessionTurnRouteStatus.Ready,
        ProviderId = provider.Descriptor.ProviderId,
        ModelId = provider.Descriptor.Models[0].ModelId,
        DataDestination = provider.Descriptor.DataDestination,
        SendsDataOffDevice = provider.Descriptor.SendsDataOffDevice,
        FrozenAtUtc = DateTimeOffset.UtcNow
    };

    private static MemoryOutboundAuditMetadata MemoryAudit(
        IChatModelProvider provider,
        Guid memoryId)
    {
        var now = DateTimeOffset.UtcNow;
        return new MemoryOutboundAuditMetadata(
            Guid.NewGuid(),
            now,
            now.AddMinutes(10),
            now,
            provider.Descriptor.ProviderId,
            provider.Descriptor.Models[0].ModelId,
            MemoryOutboundContract.NormalizeHttpsOrigin(provider.Descriptor.DataDestination),
            "chat.general",
            "4",
            "8AB0FCC9572BA621CC38598CFEC62AFC448B0D2FC20F8EFEF29A6F9DA799C394",
            null,
            [new MemoryOutboundItemReference(memoryId, 1)],
            1,
            9,
            new string('B', 64));
    }

    private static PointerAnswerAuditMetadata PointerAudit(
        IChatModelProvider provider,
        string question,
        string ocr,
        string context)
    {
        var now = DateTimeOffset.UtcNow;
        return new PointerAnswerAuditMetadata(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            now,
            now.AddMinutes(1),
            now,
            provider.Descriptor.ProviderId,
            provider.Descriptor.Models[0].ModelId,
            PointerAnswerOutboundContract.NormalizeHttpsOrigin(provider.Descriptor.DataDestination),
            "window.pointer.answer",
            "1",
            "8136B489DBE4B9FBF5CEDEA9DA0106257A15706414EDD5EC20C704F79E5DF6F8",
            PointerAnswerOutboundContract.HashText(question),
            PointerAnswerOutboundContract.HashText(ocr),
            PointerAnswerOutboundContract.HashText(context),
            new string('A', 64),
            new string('B', 64),
            question.Length,
            ocr.Length,
            1,
            320,
            120,
            "uia-element");
    }

    private static Task<PromptRegistry> LoadRepositoryPromptsAsync() =>
        PromptRegistry.LoadAsync(Path.Combine(FindRepositoryRoot(), "prompts", "runtime"));

    private static async Task<(PromptRegistry Registry, string Directory)> LoadEmptyPromptRegistryAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"empty-prompts-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "registry.json"),
            "{\"schemaVersion\":1,\"prompts\":[]}");
        return (await PromptRegistry.LoadAsync(directory), directory);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ScreenGuide.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
               ?? throw new DirectoryNotFoundException("找不到测试仓库根目录。");
    }

    private sealed class MutableAiSettingsStore(string providerId, string modelId) : IAiSettingsStore
    {
        private AiSettings _settings = new(new ChatModelRoute(providerId, modelId));

        public int LoadCount { get; private set; }

        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(_settings);
        }

        public Task SaveAsync(AiSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingChatProvider(
        string providerId,
        string modelId,
        string? dataDestination = null) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            dataDestination ?? $"{providerId} test destination",
            true,
            [new ChatModelDescriptor(modelId, modelId, ChatModelCapabilities.None)]);

        public List<ChatModelRequest> Requests { get; } = [];

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatModelResponse(
                $"{providerId} 回答 {Requests.Count}",
                ChatFinishReason.Stop,
                new ChatModelUsage(10, 5, 15),
                new ChatProviderMetadata(providerId, modelId, $"request-{Requests.Count}", Descriptor.DataDestination)));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingChatProvider : IChatModelProvider
    {
        private readonly TaskCompletionSource _cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChatProviderDescriptor Descriptor { get; } = new(
            "provider-a",
            "Provider A",
            "Provider A test destination",
            true,
            [new ChatModelDescriptor("model-a", "Model A", ChatModelCapabilities.None)]);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid? ActiveTurnId { get; private set; }

        public Guid? CancelledTurnId { get; private set; }

        public async Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            ActiveTurnId = request.TurnId;
            Started.TrySetResult();
            await _cancelled.Task.WaitAsync(cancellationToken);
            return new ChatModelResponse(
                "旧回答",
                ChatFinishReason.Cancelled,
                null,
                new ChatProviderMetadata("provider-a", request.ModelId, null, Descriptor.DataDestination));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
        {
            CancelledTurnId = turnId;
            _cancelled.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _cancelled.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedChatProvider(bool failAfterRelease = false) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            "provider-a",
            "Provider A",
            "Provider A test destination",
            true,
            [new ChatModelDescriptor("model-a", "Model A", ChatModelCapabilities.None)]);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowResponse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await AllowResponse.Task.WaitAsync(cancellationToken);
            if (failAfterRelease)
            {
                throw new ChatModelException(
                    Descriptor.ProviderId,
                    request.ModelId,
                    new ChatModelError(
                        ChatModelErrorKind.Unavailable,
                        "late_provider_failure",
                        "迟到失败不应覆盖已有终态。",
                        IsRetryable: true));
            }

            return new ChatModelResponse(
                "LATE_ASSISTANT_BODY",
                ChatFinishReason.Stop,
                new ChatModelUsage(4, 3, 7),
                new ChatProviderMetadata(
                    Descriptor.ProviderId,
                    request.ModelId,
                    "late-provider-request",
                    Descriptor.DataDestination));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            AllowResponse.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingChatProvider(ChatModelError? error = null) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            "provider-a",
            "Provider A",
            "Provider A test destination",
            true,
            [new ChatModelDescriptor("model-a", "Model A", ChatModelCapabilities.None)]);

        public int CompleteCalls { get; private set; }

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            throw new ChatModelException(
                "provider-a",
                request.ModelId,
                error ?? new ChatModelError(
                    ChatModelErrorKind.RateLimited,
                    "provider-a.rate_limited",
                    "RAW_SECRET"));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingInvocationStore : IAiInvocationStore
    {
        private readonly Dictionary<Guid, AiInvocationRecord> _records = [];

        public List<AiInvocationRecord> Started { get; } = [];

        public List<(Guid Id, string FinishReason)> Completed { get; } = [];

        public List<(Guid Id, AiInvocationStatus Status, string FailureCode)> Failed { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartAsync(AiInvocationRecord invocation, CancellationToken cancellationToken = default)
        {
            Started.Add(invocation);
            _records.Add(invocation.Id, invocation);
            return Task.CompletedTask;
        }

        public Task<AiInvocationTransitionResult> CompleteAsync(
            Guid invocationId,
            string finishReason,
            AiTokenUsage? usage,
            string? providerRequestId,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Completed.Add((invocationId, finishReason));
            return Task.FromResult(Transition(
                invocationId,
                AiInvocationStatus.Succeeded,
                completedAtUtc,
                finishReason,
                usage,
                providerRequestId,
                failureCode: null));
        }

        public Task<AiInvocationTransitionResult> FailAsync(
            Guid invocationId,
            AiInvocationStatus status,
            string failureCode,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Failed.Add((invocationId, status, failureCode));
            return Task.FromResult(Transition(
                invocationId,
                status,
                completedAtUtc,
                finishReason: null,
                usage: null,
                providerRequestId: null,
                failureCode));
        }

        public Task<AiInvocationRecoveryResult> InterruptRunningAsync(
            DateTimeOffset interruptedAtUtc,
            string failureCode,
            CancellationToken cancellationToken = default)
        {
            var interrupted = _records.Values
                .Where(record => record.Status == AiInvocationStatus.Running)
                .Select(record => record.Id)
                .ToArray();
            foreach (var invocationId in interrupted)
            {
                _ = Transition(
                    invocationId,
                    AiInvocationStatus.Interrupted,
                    interruptedAtUtc,
                    finishReason: null,
                    usage: null,
                    providerRequestId: null,
                    failureCode);
            }

            return Task.FromResult(new AiInvocationRecoveryResult(interrupted));
        }

        public Task<AiInvocationRecord?> GetAsync(Guid invocationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_records.GetValueOrDefault(invocationId));

        public Task<IReadOnlyList<AiInvocationRecord>> GetForConversationTurnAsync(
            Guid conversationTurnId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiInvocationRecord>>(
                _records.Values.Where(record =>
                    record.ConversationTurnId == conversationTurnId).ToArray());

        public Task<IReadOnlyList<AiInvocationRecord>> GetForSessionTurnAsync(
            Guid sessionTurnId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiInvocationRecord>>(
                _records.Values.Where(record =>
                    record.SessionTurnId == sessionTurnId).ToArray());

        private AiInvocationTransitionResult Transition(
            Guid invocationId,
            AiInvocationStatus requestedStatus,
            DateTimeOffset completedAtUtc,
            string? finishReason,
            AiTokenUsage? usage,
            string? providerRequestId,
            string? failureCode)
        {
            if (!_records.TryGetValue(invocationId, out var current))
            {
                throw new AiInvocationNotFoundException(invocationId);
            }

            var disposition = current.Status == AiInvocationStatus.Running
                ? AiInvocationTransitionDisposition.Applied
                : current.Status == requestedStatus
                    ? AiInvocationTransitionDisposition.AlreadyInRequestedTerminal
                    : AiInvocationTransitionDisposition.RejectedByExistingTerminal;
            if (disposition == AiInvocationTransitionDisposition.Applied)
            {
                current = current with
                {
                    Status = requestedStatus,
                    CompletedAtUtc = completedAtUtc,
                    FinishReason = finishReason,
                    Usage = usage,
                    ProviderRequestId = providerRequestId,
                    FailureCode = failureCode
                };
                _records[invocationId] = current;
            }

            return new AiInvocationTransitionResult(requestedStatus, disposition, current);
        }
    }

    private sealed class HistoryStore(Guid conversationId) : IConversationStore
    {
        private readonly List<ConversationMessageRecord> _messages = [];
        private readonly List<ConversationTurnRecord> _turns = [];

        public void Add(ConversationMessageRole role, string content) => _messages.Add(new ConversationMessageRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SequenceNumber = _messages.Count + 1,
            Role = role,
            Content = content,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        public Task<IReadOnlyList<ConversationMessageRecord>> GetMessagesAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationMessageRecord>>(_messages.ToArray());

        public void AddDerivedTurn(MemoryOutboundAuditMetadata memoryOutbound) => _turns.Add(new ConversationTurnRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SequenceNumber = _turns.Count + 1,
            UserMessageId = _messages[0].Id,
            AssistantMessageId = _messages[1].Id,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = ConversationTurnStatus.Succeeded,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            MemoryDerived = true,
            MemoryOutbound = memoryOutbound
        });

        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConversationRecord?> GetConversationAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationRecord>> GetConversationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationTurnRecord>> GetTurnsAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurnRecord>>(_turns.ToArray());
        public Task<ConversationTurnRegistration> StartTurnAsync(Guid id, Guid turnId, string message, string key, DateTimeOffset startedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RecordProviderStartedAsync(Guid id, Guid turnId, string thread, int processId, DateTimeOffset startedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteTurnAsync(Guid id, Guid turnId, string reply, string? providerMessageId, DateTimeOffset completedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FailTurnAsync(Guid id, Guid turnId, ConversationTurnStatus status, string code, string message, DateTimeOffset completedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConversationRecoveryResult> RecoverInterruptedAsync(DateTimeOffset recoveredAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
