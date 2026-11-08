using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class SessionFrozenRouteTests
{
    [Fact]
    public async Task SameTurnSemanticAndConversationUsePersistedRouteAndOriginalSessionTurnId()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var settings = new MutableSettingsStore(Route("provider-a", "model-a"));
        var providerA = new SwitchingRecordingProvider(
            "provider-a",
            "model-a",
            async request =>
            {
                if (string.Equals(request.Prompt?.PromptId, "intent.semantic", StringComparison.Ordinal))
                {
                    await settings.SaveAsync(Route("provider-b", "model-b"));
                    return """
                        {
                          "kind": "Conversation",
                          "target": null,
                          "confidence": 0.95,
                          "isAmbiguous": false,
                          "missingContext": "None"
                        }
                        """;
                }

                return "来自 A 的回答";
            });
        var providerB = new SwitchingRecordingProvider(
            "provider-b",
            "model-b",
            _ => Task.FromResult("不应调用 B"));
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(settings);
            services.AddSingleton(new ChatProviderRegistry([providerA, providerB]));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
        var session = await coordinator.StartNewAsync("同一 Turn 冻结路由");

        var submitted = await coordinator.SubmitAsync(
            session.Session.Id,
            "处理刚才那个",
            "Text",
            "same-turn-semantic-chat-route");
        var completed = await WaitForPhaseAsync(
            store,
            submitted.TurnId,
            SessionTurnPhase.Completed);
        var turnInvocations = await invocations.GetForSessionTurnAsync(submitted.TurnId);
        await host.StopAsync();

        Assert.Equal("provider-a", completed.FrozenRoute?.ProviderId);
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(2, providerA.Requests.Count);
        Assert.All(providerA.Requests, request => Assert.Equal(submitted.TurnId, request.TurnId));
        Assert.Contains(providerA.Requests, request => request.Prompt?.PromptId == "intent.semantic");
        Assert.Contains(providerA.Requests, request => request.Prompt?.PromptId == "chat.general");
        Assert.Empty(providerB.Requests);
        Assert.Equal(2, turnInvocations.Count);
        Assert.All(turnInvocations, invocation =>
            Assert.Equal(submitted.TurnId, invocation.SessionTurnId));
        var semanticInvocation = Assert.Single(turnInvocations, invocation =>
            invocation.Purpose == AiInvocationPurpose.SemanticIntent);
        Assert.Null(semanticInvocation.ConversationTurnId);
        var conversationInvocation = Assert.Single(turnInvocations, invocation =>
            invocation.Purpose == AiInvocationPurpose.Conversation);
        Assert.Equal(completed.ConversationTurnId, conversationInvocation.ConversationTurnId);
        Assert.Equal(
            conversationInvocation.Id,
            Assert.Single(await invocations.GetForConversationTurnAsync(
                conversationInvocation.ConversationTurnId!.Value)).Id);
    }

    [Fact]
    public async Task ContextContinuationReusesPersistedIntentAndNeverReadsOrRefreezesSettings()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var settings = new MutableSettingsStore(Route("provider-a", "model-a"));
        var providerA = new SwitchingRecordingProvider(
            "provider-a",
            "model-a",
            _ => Task.FromResult("""
                {
                  "kind": "CodingTask",
                  "target": null,
                  "confidence": 0.95,
                  "isAmbiguous": false,
                  "missingContext": "Project"
                }
                """));
        var providerB = new SwitchingRecordingProvider(
            "provider-b",
            "model-b",
            _ => Task.FromResult("不应调用 B"));
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(settings);
            services.AddSingleton(new ChatProviderRegistry([providerA, providerB]));
        });
        await host.StartAsync();
        var (_, project, _) = await environment.SeedProjectsAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var session = await coordinator.StartNewAsync("补上下文不重冻");

        var submitted = await coordinator.SubmitAsync(
            session.Session.Id,
            "处理刚才那个",
            "Text",
            "context-does-not-refreeze");
        var waiting = await WaitForPhaseAsync(
            store,
            submitted.TurnId,
            SessionTurnPhase.WaitingForProject);
        await settings.SaveAsync(Route("provider-b", "model-b"));

        var continued = await coordinator.ProvideProjectAsync(
            session.Session.Id,
            submitted.TurnId,
            project.Id);
        var continuedTurn = continued.Turns.Single(turn => turn.Id == submitted.TurnId);
        await coordinator.CancelTurnAsync(session.Session.Id, submitted.TurnId);
        await host.StopAsync();

        Assert.Equal(SessionTurnPhase.WaitingForConfirmation, continuedTurn.Phase);
        Assert.Equal(waiting.FrozenRoute, continuedTurn.FrozenRoute);
        Assert.Equal(1, settings.LoadCount);
        var semanticRequest = Assert.Single(providerA.Requests);
        Assert.Equal("intent.semantic", semanticRequest.Prompt?.PromptId);
        Assert.Equal(submitted.TurnId, semanticRequest.TurnId);
        Assert.Empty(providerB.Requests);
    }

    [Fact]
    public async Task UnavailableFrozenRouteFailsOrdinaryChatButDoesNotCallProvider()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new MetadataOnlyProvider("provider-a", "model-a");
        var settings = new MutableSettingsStore(Route(" ", " "));
        var semantic = new CountingSemanticSuggester();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(settings);
            services.AddSingleton(new ChatProviderRegistry([provider]));
            services.AddSingleton<ISemanticIntentSuggester>(semantic);
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
        var session = await coordinator.StartNewAsync("普通聊天失败关闭");

        var submitted = await coordinator.SubmitAsync(
            session.Session.Id,
            "处理刚才那个",
            "Text",
            "unavailable-route-chat");
        var failed = await WaitForPhaseAsync(store, submitted.TurnId, SessionTurnPhase.Failed);
        Assert.NotNull(failed.ConversationTurnId);
        Assert.Empty(await invocations.GetForConversationTurnAsync(failed.ConversationTurnId.Value));
        await host.StopAsync();

        Assert.Equal(SessionTurnRouteStatus.Unavailable, failed.FrozenRoute?.Status);
        Assert.Equal("ai_settings_invalid", failed.FrozenRoute?.FailureCode);
        Assert.Equal("ai_settings_invalid", failed.FailureCode);
        Assert.Contains("没有发送", failed.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, semantic.CallCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(119)]
    public async Task PointerAnswerWaitsForExactPreviewAndOneConfirmationAllowsOnlyOneTextOnlyRequest(int readingSeconds)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var capture = new FixedPointerCapture();
        const string question = "这个按钮是什么意思？";
        const string ocr = "保存并继续";
        var provider = new SwitchingRecordingProvider(
            "qwen",
            "qwen3.7-plus",
            _ => Task.FromResult("它表示保存当前内容并继续下一步。"),
            "https://qwen.example/v1/chat");
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(
                new MutableSettingsStore(Route("qwen", "qwen3.7-plus")));
            services.AddSingleton(new ChatProviderRegistry([provider]));
            services.AddSingleton<IPointerDesktopProbe>(new FixedPointerProbe(PointerSnapshot()));
            services.AddSingleton<IPointerRegionCaptureService>(capture);
            services.AddSingleton<ILocalOcrTextExtractor>(new FixedPointerOcr(ocr));
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var conversations = host.Services.GetRequiredService<ScreenGuide.Core.Conversations.IConversationStore>();
        var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
        var taskStore = host.Services.GetRequiredService<ILocalTaskStore>();
        var session = await coordinator.StartNewAsync("指针回答确认");
        var anchor = await client.PreparePointerRegionAsync(new PreparePointerRegionRequestDto(true));

        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            question,
            "Text",
            "pointer-answer-success",
            session.Session.Id,
            PointerAnchorId: anchor.AnchorId));
        _ = await WaitForPhaseAsync(
            store,
            submitted.TurnId,
            SessionTurnPhase.WaitingForPointerAnswerConsent);
        var previewSnapshot = await client.GetCurrentSessionAsync();
        var preview = Assert.Single(previewSnapshot!.PointerAnswerConsents!);

        Assert.Equal(question, preview.Question);
        Assert.Equal(ocr, preview.OcrText);
        Assert.Equal("qwen", preview.ProviderId);
        Assert.Equal("qwen3.7-plus", preview.ModelId);
        Assert.Equal("https://qwen.example", preview.DestinationOrigin);
        Assert.Equal("window.pointer.answer", preview.PromptId);
        Assert.Equal("1", preview.PromptVersion);
        Assert.False(preview.SendsImage);
        Assert.DoesNotContain(question, preview.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ocr, preview.ToString(), StringComparison.Ordinal);
        Assert.Empty(provider.Requests);
        Assert.Empty(await invocations.GetForSessionTurnAsync(submitted.TurnId));

        Assert.Equal(environment.TimeProvider.GetUtcNow().AddSeconds(120), preview.ExpiresAtUtc);
        environment.TimeProvider.UtcNow = environment.TimeProvider.GetUtcNow().AddSeconds(readingSeconds);

        var confirmationResults = await Task.WhenAll(
            TryConfirmPointerAnswerAsync(
                client,
                session.Session.Id,
                submitted.TurnId,
                preview.ConsentId,
                preview.PreviewHash),
            TryConfirmPointerAnswerAsync(
                new DesktopApiClient(environment.Options.PipeName),
                session.Session.Id,
                submitted.TurnId,
                preview.ConsentId,
                preview.PreviewHash));
        Assert.Single(confirmationResults, code => code is null);
        Assert.Single(
            confirmationResults,
            code => code == PointerAnswerErrorCodes.ConsentStale);
        var completed = await WaitForPhaseAsync(store, submitted.TurnId, SessionTurnPhase.Completed);
        Assert.Equal(1, capture.Count); // Reading/confirming never captures pixels again.
        var providerRequest = Assert.Single(provider.Requests);
        Assert.Equal("window.pointer.answer", providerRequest.Prompt?.PromptId);
        Assert.Equal(2, providerRequest.Messages.Count);
        Assert.StartsWith("{\"type\":\"POINTER_REGION_TEXT_CONTEXT_V1\"", providerRequest.Messages[0].Content, StringComparison.Ordinal);
        using (var contextDocument = JsonDocument.Parse(providerRequest.Messages[0].Content))
        {
            Assert.Equal(ocr, contextDocument.RootElement.GetProperty("ocrText").GetString());
        }
        Assert.Equal(question, providerRequest.Messages[1].Content);
        Assert.Single(await invocations.GetForSessionTurnAsync(submitted.TurnId));

        Assert.Single(provider.Requests);

        var messages = await conversations.GetMessagesAsync(session.Session.ConversationId);
        Assert.Contains(messages, message => message.Content == question);
        Assert.DoesNotContain(messages, message => message.Content.Contains(ocr, StringComparison.Ordinal));
        var pointerAudit = Assert.Single(
            await taskStore.GetAuditLogAsync(),
            item => item.Action == "ConversationTurnStarted"
                && item.DetailsJson?.Contains("pointerAnswer", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(question, pointerAudit.DetailsJson!, StringComparison.Ordinal);
        Assert.DoesNotContain(ocr, pointerAudit.DetailsJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("SerializedContext", pointerAudit.DetailsJson!, StringComparison.Ordinal);
        Assert.Contains(
            PointerAnswerOutboundContract.HashText(question),
            pointerAudit.DetailsJson!,
            StringComparison.Ordinal);
        Assert.Contains(
            PointerAnswerOutboundContract.HashText(ocr),
            pointerAudit.DetailsJson!,
            StringComparison.Ordinal);
        Assert.NotNull(completed.ConversationTurnId);
        await host.StopAsync();
    }

    [Fact]
    public async Task PointerAnswerTamperedDeclinedOrStoppedPreviewFailsBeforeInvocationOrProvider()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var probe = new FixedPointerProbe(PointerSnapshot());
        var provider = new SwitchingRecordingProvider(
            "deepseek",
            "deepseek-chat",
            _ => Task.FromResult("不应发送"),
            "https://deepseek.example/v1/chat");
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(
                new MutableSettingsStore(Route("deepseek", "deepseek-chat")));
            services.AddSingleton(new ChatProviderRegistry([provider]));
            services.AddSingleton<IPointerDesktopProbe>(probe);
            services.AddSingleton<IPointerRegionCaptureService>(new FixedPointerCapture());
            services.AddSingleton<ILocalOcrTextExtractor>(new FixedPointerOcr("PRIVATE_OCR_SENTINEL"));
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
        var session = await coordinator.StartNewAsync("失效确认");

        var firstAnchor = await client.PreparePointerRegionAsync(new PreparePointerRegionRequestDto(true));
        var first = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "第一个问题",
            IdempotencyKey: "pointer-answer-tampered",
            SessionId: session.Session.Id,
            PointerAnchorId: firstAnchor.AnchorId));
        _ = await WaitForPhaseAsync(store, first.TurnId, SessionTurnPhase.WaitingForPointerAnswerConsent);
        var firstPreview = Assert.Single((await client.GetCurrentSessionAsync())!.PointerAnswerConsents!);
        _ = await client.ConfirmPointerAnswerAsync(
            session.Session.Id,
            first.TurnId,
            firstPreview.ConsentId,
            new string('0', 64),
            confirmed: true);
        _ = await WaitForPhaseAsync(store, first.TurnId, SessionTurnPhase.Failed);

        var secondAnchor = await client.PreparePointerRegionAsync(new PreparePointerRegionRequestDto(true));
        var second = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "第二个问题",
            IdempotencyKey: "pointer-answer-declined",
            SessionId: session.Session.Id,
            PointerAnchorId: secondAnchor.AnchorId));
        _ = await WaitForPhaseAsync(store, second.TurnId, SessionTurnPhase.WaitingForPointerAnswerConsent);
        var secondPreview = Assert.Single((await client.GetCurrentSessionAsync())!.PointerAnswerConsents!);
        probe.Snapshot = probe.Snapshot with { ForegroundWindowHandle = 99 }; // Refusal from Client needs no capture authority.
        _ = await client.ConfirmPointerAnswerAsync(
            session.Session.Id,
            second.TurnId,
            secondPreview.ConsentId,
            secondPreview.PreviewHash,
            confirmed: false);
        _ = await WaitForPhaseAsync(store, second.TurnId, SessionTurnPhase.Cancelled);

        probe.Snapshot = PointerSnapshot();
        var thirdAnchor = await client.PreparePointerRegionAsync(new PreparePointerRegionRequestDto(true));
        var third = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "第三个问题",
            IdempotencyKey: "pointer-answer-stopped",
            SessionId: session.Session.Id,
            PointerAnchorId: thirdAnchor.AnchorId));
        _ = await WaitForPhaseAsync(store, third.TurnId, SessionTurnPhase.WaitingForPointerAnswerConsent);
        Assert.Single((await client.GetCurrentSessionAsync())!.PointerAnswerConsents!);
        _ = await client.CancelSessionTurnAsync(session.Session.Id, third.TurnId);
        _ = await WaitForPhaseAsync(store, third.TurnId, SessionTurnPhase.Cancelled);
        Assert.Empty((await client.GetCurrentSessionAsync())!.PointerAnswerConsents!);

        Assert.Empty(provider.Requests);
        Assert.Empty(await invocations.GetForSessionTurnAsync(first.TurnId));
        Assert.Empty(await invocations.GetForSessionTurnAsync(second.TurnId));
        Assert.Empty(await invocations.GetForSessionTurnAsync(third.TurnId));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("clock-rollback")]
    [InlineData("target-change")]
    public async Task PointerAnswerInvalidSnapshotFailsBeforeInvocationOrProvider(string invalidation)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var probe = new FixedPointerProbe(PointerSnapshot());
        var capture = new FixedPointerCapture();
        var provider = new SwitchingRecordingProvider(
            "provider-a",
            "model-a",
            _ => Task.FromResult("不应发送"),
            "https://provider-a.example/v1/chat");
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(new MutableSettingsStore(Route("provider-a", "model-a")));
            services.AddSingleton(new ChatProviderRegistry([provider]));
            services.AddSingleton<IPointerDesktopProbe>(probe);
            services.AddSingleton<IPointerRegionCaptureService>(capture);
            services.AddSingleton<ILocalOcrTextExtractor>(new FixedPointerOcr("OCR"));
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
        var session = await coordinator.StartNewAsync("精确过期");
        var anchor = await client.PreparePointerRegionAsync(new PreparePointerRegionRequestDto(true));
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "这里是什么？",
            IdempotencyKey: "pointer-answer-exact-expiry",
            SessionId: session.Session.Id,
            PointerAnchorId: anchor.AnchorId));
        _ = await WaitForPhaseAsync(store, submitted.TurnId, SessionTurnPhase.WaitingForPointerAnswerConsent);
        var preview = Assert.Single((await client.GetCurrentSessionAsync())!.PointerAnswerConsents!);

        var preparedAt = environment.TimeProvider.UtcNow;
        environment.TimeProvider.UtcNow = invalidation switch
        {
            "expiry" => preview.ExpiresAtUtc,
            "clock-rollback" => preparedAt.AddSeconds(-1),
            _ => preparedAt.AddSeconds(30)
        };
        if (invalidation == "target-change") probe.Snapshot = probe.Snapshot with { ForegroundWindowHandle = 99 };
        _ = await client.ConfirmPointerAnswerAsync(
            session.Session.Id,
            submitted.TurnId,
            preview.ConsentId,
            preview.PreviewHash,
            confirmed: true);
        var failed = await WaitForPhaseAsync(store, submitted.TurnId, SessionTurnPhase.Failed);

        Assert.Equal(PointerAnswerErrorCodes.ConsentStale, failed.FailureCode);
        Assert.Equal(1, capture.Count);
        Assert.Empty(provider.Requests);
        Assert.Empty(await invocations.GetForSessionTurnAsync(submitted.TurnId));
        await host.StopAsync();
    }

    [Fact]
    public async Task SelectedMemoryWaitsForVisiblePerTurnConsentAndMutationsFailBeforeInvocationOrProvider()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new SwitchingRecordingProvider(
            "provider-a",
            "model-a",
            _ => Task.FromResult("已按确认记忆回答"),
            "https://provider-a.example/v1/chat");
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(
                new MutableSettingsStore(Route("provider-a", "model-a")));
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var memoryService = host.Services.GetRequiredService<MemoryService>();
        var sessionStore = host.Services.GetRequiredService<ISessionStore>();
        var conversationStore = host.Services.GetRequiredService<ScreenGuide.Core.Conversations.IConversationStore>();
        var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
        var memory = await memoryService.CreateAsync(MemoryDraft.Create(
            MemoryCategory.UserPreference,
            MemoryScope.Global,
            "称呼偏好",
            "请叫我小元",
            null,
            DateTimeOffset.UtcNow));
        var session = await coordinator.StartNewAsync("逐 Turn 记忆确认");

        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "处理刚才那个",
            "Text",
            "memory-consent-success",
            session.Session.Id,
            MemoryItems:
            [
                new MemoryOutboundItemReferenceDto(memory.Metadata.Id, memory.Metadata.Version)
            ]));
        var waiting = await WaitForPhaseAsync(
            sessionStore,
            submitted.TurnId,
            SessionTurnPhase.WaitingForMemoryOutboundConsent);
        var preparedSnapshot = await client.GetCurrentSessionAsync();
        var preparedDto = Assert.Single(preparedSnapshot!.MemoryOutboundConsents!);
        var prepared = Assert.Single((await coordinator.GetCurrentAsync())!.MemoryOutboundConsents);

        Assert.Equal(MemoryOutboundConsentState.WaitingForMemoryOutboundConsent, waiting.MemoryOutboundState);
        Assert.Equal("4", prepared.PromptVersion);
        Assert.Equal(prepared.ManifestHash, preparedDto.ManifestHash);
        Assert.Equal("请叫我小元", Assert.Single(prepared.Items).Body);
        Assert.Equal("请叫我小元", Assert.Single(preparedDto.Items).Body);
        Assert.Equal("https://provider-a.example", preparedDto.DestinationOrigin);
        Assert.DoesNotContain("请叫我小元", preparedDto.ToString(), StringComparison.Ordinal);
        Assert.Empty(provider.Requests);
        Assert.Empty(await invocations.GetForSessionTurnAsync(submitted.TurnId));

        _ = await client.ConfirmMemoryOutboundAsync(
            session.Session.Id,
            submitted.TurnId,
            prepared.ConsentId,
            confirmed: true);
        var completed = await WaitForPhaseAsync(
            sessionStore,
            submitted.TurnId,
            SessionTurnPhase.Completed);

        Assert.Equal(MemoryOutboundConsentState.Consumed, completed.MemoryOutboundState);
        var providerRequest = Assert.Single(provider.Requests);
        Assert.Equal("4", providerRequest.Prompt?.Version);
        Assert.Equal(prepared.PromptContentHash, providerRequest.Prompt?.ContentSha256);
        Assert.Equal(2, providerRequest.Messages.Count);
        Assert.StartsWith("{\"type\":\"USER_SELECTED_MEMORY_CONTEXT_V1\"", providerRequest.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal("处理刚才那个", providerRequest.Messages[1].Content);
        Assert.Equal(prepared.ConsentId, Assert.Single(
            await invocations.GetForSessionTurnAsync(submitted.TurnId)).MemoryOutbound?.ConsentId);
        var duplicateConsent = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ConfirmMemoryOutboundAsync(
                session.Session.Id,
                submitted.TurnId,
                prepared.ConsentId,
                confirmed: true));
        Assert.Equal(MemoryOutboundErrorCodes.ConsentStale, duplicateConsent.Error.Code);
        Assert.Single(provider.Requests);
        var persistedConversationTurn = Assert.Single(
            await conversationStore.GetTurnsAsync(session.Session.ConversationId),
            item => item.Id == completed.ConversationTurnId);
        Assert.True(persistedConversationTurn.MemoryDerived);
        Assert.Equal(prepared.ManifestHash, persistedConversationTurn.MemoryOutbound?.ManifestHash);
        Assert.Equal("https://provider-a.example", persistedConversationTurn.MemoryOutbound?.DestinationOrigin);

        var staleMemory = await memoryService.CreateAsync(MemoryDraft.Create(
            MemoryCategory.Decision,
            MemoryScope.Global,
            "要改变的记忆",
            "初始正文",
            null,
            DateTimeOffset.UtcNow));
        var staleSubmission = await coordinator.SubmitAsync(
            session.Session.Id,
            "第二个问题",
            "Text",
            "memory-consent-stale",
            memoryItems: [new MemoryOutboundItemReference(staleMemory.Metadata.Id, staleMemory.Metadata.Version)]);
        _ = await WaitForPhaseAsync(
            sessionStore,
            staleSubmission.TurnId,
            SessionTurnPhase.WaitingForMemoryOutboundConsent);
        var stalePrepared = Assert.Single((await coordinator.GetCurrentAsync())!.MemoryOutboundConsents);
        _ = await memoryService.UpdateAsync(
            staleMemory.Metadata.Id,
            staleMemory.Metadata.Version,
            MemoryDraft.Create(
                MemoryCategory.Decision,
                MemoryScope.Global,
                "要改变的记忆",
                "确认后已变化",
                null,
                DateTimeOffset.UtcNow));

        _ = await coordinator.ConfirmMemoryOutboundAsync(
            session.Session.Id,
            staleSubmission.TurnId,
            stalePrepared.ConsentId,
            confirmed: true);
        var failed = await WaitForPhaseAsync(
            sessionStore,
            staleSubmission.TurnId,
            SessionTurnPhase.Failed);
        await host.StopAsync();

        Assert.Equal(MemoryOutboundErrorCodes.ConsentStale, failed.FailureCode);
        Assert.Single(provider.Requests);
        Assert.Empty(await invocations.GetForSessionTurnAsync(staleSubmission.TurnId));
        foreach (var file in Directory.EnumerateFiles(
                     environment.Options.DataDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes("称呼偏好"), bytes);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes("请叫我小元"), bytes);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes("USER_SELECTED_MEMORY_CONTEXT_V1"), bytes);
        }
    }

    [Fact]
    public async Task ProjectionWaitResetsWhenForegroundMemoryConsentAppearsAndClears()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new SwitchingRecordingProvider(
            "provider-a",
            "model-a",
            _ => Task.FromResult("不应调用"),
            "https://provider-a.example/v1/chat");
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(
                new MutableSettingsStore(Route("provider-a", "model-a")));
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var memoryService = host.Services.GetRequiredService<MemoryService>();
        var sessionStore = host.Services.GetRequiredService<ISessionStore>();
        var memory = await memoryService.CreateAsync(MemoryDraft.Create(
            MemoryCategory.UserPreference,
            MemoryScope.Global,
            "投影刷新标题",
            "投影刷新正文",
            null,
            environment.TimeProvider.GetUtcNow()));
        var bootstrap = await client.StartNewSessionAsync("投影确认刷新");

        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "需要确认的记忆输入",
            "Text",
            "memory-projection-reset",
            bootstrap.SessionId,
            MemoryItems:
            [
                new MemoryOutboundItemReferenceDto(memory.Metadata.Id, memory.Metadata.Version)
            ]));
        _ = await WaitForPhaseAsync(
            sessionStore,
            submitted.TurnId,
            SessionTurnPhase.WaitingForMemoryOutboundConsent);

        var preparedUpdate = await client.WaitForSessionProjectionAsync(new SessionProjectionCursorDto(
            bootstrap.CoordinatorInstanceId,
            bootstrap.CoordinatorStartedAtUtc,
            bootstrap.SessionId,
            bootstrap.ChangeVersion,
            bootstrap.Messages.LastOrDefault()?.SequenceNumber ?? 0,
            WaitMilliseconds: 2_000));
        Assert.Equal("ResetRequired", preparedUpdate.Kind);
        var preparedSnapshot = await client.GetCurrentSessionAsync();
        var prepared = Assert.Single(preparedSnapshot!.MemoryOutboundConsents!);
        Assert.Equal("投影刷新正文", Assert.Single(prepared.Items).Body);

        _ = await client.ConfirmMemoryOutboundAsync(
            bootstrap.SessionId,
            submitted.TurnId,
            prepared.ConsentId,
            confirmed: false);
        var clearedUpdate = await client.WaitForSessionProjectionAsync(new SessionProjectionCursorDto(
            preparedSnapshot.CoordinatorInstanceId,
            preparedSnapshot.CoordinatorStartedAtUtc,
            preparedSnapshot.SessionId,
            preparedSnapshot.ChangeVersion,
            preparedSnapshot.Messages.LastOrDefault()?.SequenceNumber ?? 0,
            WaitMilliseconds: 2_000));
        Assert.Equal("ResetRequired", clearedUpdate.Kind);
        var clearedSnapshot = await client.GetCurrentSessionAsync();
        Assert.Empty(clearedSnapshot!.MemoryOutboundConsents ?? []);
        Assert.Empty(provider.Requests);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("replacement-input")]
    [InlineData("new-topic")]
    [InlineData("session-switch")]
    public async Task ReplacingForegroundWorkImmediatelyRemovesTheOldMemoryConsentFromIpc(
        string interruption)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new SwitchingRecordingProvider(
            "provider-a",
            "model-a",
            _ => Task.FromResult("不应调用"),
            "https://provider-a.example/v1/chat");
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(
                new MutableSettingsStore(Route("provider-a", "model-a")));
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var memoryService = host.Services.GetRequiredService<MemoryService>();
        var sessionStore = host.Services.GetRequiredService<ISessionStore>();
        var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
        const string oldTitle = "应立即清除的标题";
        const string oldBody = "应立即清除的正文";
        var oldMemory = await memoryService.CreateAsync(MemoryDraft.Create(
            MemoryCategory.UserPreference,
            MemoryScope.Global,
            oldTitle,
            oldBody,
            null,
            environment.TimeProvider.GetUtcNow()));
        var replacementMemory = await memoryService.CreateAsync(MemoryDraft.Create(
            MemoryCategory.Decision,
            MemoryScope.Global,
            "替换请求标题",
            "替换请求正文",
            null,
            environment.TimeProvider.GetUtcNow()));
        var originalSession = await coordinator.StartNewAsync("临时记忆确认清理");
        Guid? alternateSessionId = null;
        if (string.Equals(interruption, "session-switch", StringComparison.Ordinal))
        {
            alternateSessionId = (await client.StartNewSessionAsync("切换目标会话")).SessionId;
            _ = await client.SetCurrentSessionAsync(originalSession.Session.Id);
        }

        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "等待确认的旧输入",
            "Text",
            $"memory-cleanup-{interruption}",
            originalSession.Session.Id,
            MemoryItems:
            [
                new MemoryOutboundItemReferenceDto(oldMemory.Metadata.Id, oldMemory.Metadata.Version)
            ]));
        _ = await WaitForPhaseAsync(
            sessionStore,
            submitted.TurnId,
            SessionTurnPhase.WaitingForMemoryOutboundConsent);
        var prepared = Assert.Single((await coordinator.GetCurrentAsync())!.MemoryOutboundConsents);

        SessionSnapshotDto snapshot;
        if (string.Equals(interruption, "replacement-input", StringComparison.Ordinal))
        {
            var replacement = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
                "替换旧输入",
                "Text",
                "memory-cleanup-replacement-new-turn",
                originalSession.Session.Id,
                MemoryItems:
                [
                    new MemoryOutboundItemReferenceDto(
                        replacementMemory.Metadata.Id,
                        replacementMemory.Metadata.Version)
                ]));
            _ = await WaitForPhaseAsync(
                sessionStore,
                replacement.TurnId,
                SessionTurnPhase.WaitingForMemoryOutboundConsent);
            snapshot = (await client.GetCurrentSessionAsync())!;
        }
        else if (string.Equals(interruption, "new-topic", StringComparison.Ordinal))
        {
            _ = await client.StartNewSessionAsync("新话题");
            snapshot = await client.SetCurrentSessionAsync(originalSession.Session.Id);
        }
        else
        {
            snapshot = await client.SetCurrentSessionAsync(alternateSessionId!.Value);
            snapshot = await client.SetCurrentSessionAsync(originalSession.Session.Id);
        }

        Assert.DoesNotContain(
            snapshot.MemoryOutboundConsents ?? [],
            item => item.TurnId == submitted.TurnId);
        Assert.DoesNotContain(
            oldTitle,
            (snapshot.MemoryOutboundConsents ?? [])
                .SelectMany(item => item.Items)
                .SelectMany(item => new[] { item.Title, item.Body }));
        Assert.DoesNotContain(
            oldBody,
            (snapshot.MemoryOutboundConsents ?? [])
                .SelectMany(item => item.Items)
                .SelectMany(item => new[] { item.Title, item.Body }));
        var cancelled = await sessionStore.GetTurnAsync(submitted.TurnId);
        Assert.Equal(SessionTurnPhase.Cancelled, cancelled?.Phase);
        var stale = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ConfirmMemoryOutboundAsync(
                originalSession.Session.Id,
                submitted.TurnId,
                prepared.ConsentId,
                confirmed: true));
        Assert.Equal(MemoryOutboundErrorCodes.ConsentStale, stale.Error.Code);
        Assert.Empty(provider.Requests);
        Assert.Empty(await invocations.GetForSessionTurnAsync(submitted.TurnId));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("replacement-input")]
    [InlineData("new-topic")]
    [InlineData("session-switch")]
    public async Task CancellationWinningBeforeConsentPublicationPreventsLateRepublish(
        string interruption)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var observer = new BlockingMemoryConsentPublicationObserver();
        var provider = new SwitchingRecordingProvider(
            "provider-a",
            "model-a",
            _ => Task.FromResult("不应调用"),
            "https://provider-a.example/v1/chat");
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(
                new MutableSettingsStore(Route("provider-a", "model-a")));
            services.AddSingleton(new ChatProviderRegistry([provider]));
            services.AddSingleton<ISessionMemoryConsentPublicationObserver>(observer);
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var memoryService = host.Services.GetRequiredService<MemoryService>();
        var sessionStore = host.Services.GetRequiredService<ISessionStore>();
        var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
        const string oldTitle = "并发清理标题";
        const string oldBody = "并发清理正文";
        var oldMemory = await memoryService.CreateAsync(MemoryDraft.Create(
            MemoryCategory.UserPreference,
            MemoryScope.Global,
            oldTitle,
            oldBody,
            null,
            environment.TimeProvider.GetUtcNow()));
        var replacementMemory = await memoryService.CreateAsync(MemoryDraft.Create(
            MemoryCategory.Decision,
            MemoryScope.Global,
            "替换请求标题",
            "替换请求正文",
            null,
            environment.TimeProvider.GetUtcNow()));
        var originalSession = await coordinator.StartNewAsync("发布竞态清理");
        Guid? alternateSessionId = null;
        if (string.Equals(interruption, "session-switch", StringComparison.Ordinal))
        {
            alternateSessionId = (await client.StartNewSessionAsync("并发切换目标")).SessionId;
            _ = await client.SetCurrentSessionAsync(originalSession.Session.Id);
        }

        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "尚未发布确认的旧输入",
            "Text",
            $"memory-publication-race-{interruption}",
            originalSession.Session.Id,
            MemoryItems:
            [
                new MemoryOutboundItemReferenceDto(oldMemory.Metadata.Id, oldMemory.Metadata.Version)
            ]));
        await observer.BeforePublishReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        async Task<SessionSnapshotDto> InterruptAsync()
        {
            if (string.Equals(interruption, "replacement-input", StringComparison.Ordinal))
            {
                _ = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
                    "替换并发旧输入",
                    "Text",
                    "memory-publication-race-replacement-new-turn",
                    originalSession.Session.Id,
                    MemoryItems:
                    [
                        new MemoryOutboundItemReferenceDto(
                            replacementMemory.Metadata.Id,
                            replacementMemory.Metadata.Version)
                    ]));
                return (await client.GetCurrentSessionAsync())!;
            }

            return string.Equals(interruption, "new-topic", StringComparison.Ordinal)
                ? await client.StartNewSessionAsync("并发新话题")
                : await client.SetCurrentSessionAsync(alternateSessionId!.Value);
        }

        var interruptionTask = InterruptAsync();
        await observer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observer.ReleasePublication();
        _ = await interruptionTask;

        var snapshot = string.Equals(interruption, "replacement-input", StringComparison.Ordinal)
            ? (await client.GetCurrentSessionAsync())!
            : await client.SetCurrentSessionAsync(originalSession.Session.Id);
        Assert.Equal(0, observer.AfterPublishForBlockedTurn);
        Assert.DoesNotContain(
            snapshot.MemoryOutboundConsents ?? [],
            item => item.TurnId == submitted.TurnId);
        Assert.DoesNotContain(
            oldTitle,
            (snapshot.MemoryOutboundConsents ?? [])
                .SelectMany(item => item.Items)
                .SelectMany(item => new[] { item.Title, item.Body }));
        Assert.DoesNotContain(
            oldBody,
            (snapshot.MemoryOutboundConsents ?? [])
                .SelectMany(item => item.Items)
                .SelectMany(item => new[] { item.Title, item.Body }));
        var cancelled = await sessionStore.GetTurnAsync(submitted.TurnId);
        Assert.Equal(SessionTurnPhase.Cancelled, cancelled?.Phase);
        Assert.DoesNotContain(snapshot.ActiveTurns, item => item.Id == submitted.TurnId);
        var stale = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ConfirmMemoryOutboundAsync(
                originalSession.Session.Id,
                submitted.TurnId,
                observer.ConsentId,
                confirmed: true));
        Assert.Equal(MemoryOutboundErrorCodes.ConsentStale, stale.Error.Code);
        Assert.Empty(provider.Requests);
        Assert.Empty(await invocations.GetForSessionTurnAsync(submitted.TurnId));
        await host.StopAsync();
    }

    [Fact]
    public async Task ExactMemoryConsentExpiryFailsBeforeInvocationOrProvider()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new SwitchingRecordingProvider(
            "provider-a",
            "model-a",
            _ => Task.FromResult("不应调用"),
            "https://provider-a.example/v1/chat");
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(
                new MutableSettingsStore(Route("provider-a", "model-a")));
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var memoryService = host.Services.GetRequiredService<MemoryService>();
        var sessionStore = host.Services.GetRequiredService<ISessionStore>();
        var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
        var memory = await memoryService.CreateAsync(MemoryDraft.Create(
            MemoryCategory.UserFact,
            MemoryScope.Global,
            "精确到期",
            "到期时不得发送",
            null,
            environment.TimeProvider.GetUtcNow()));
        var session = await coordinator.StartNewAsync("精确到期边界");
        var submitted = await coordinator.SubmitAsync(
            session.Session.Id,
            "等待到期",
            "Text",
            "memory-consent-exact-expiry",
            memoryItems:
            [
                new MemoryOutboundItemReference(memory.Metadata.Id, memory.Metadata.Version)
            ]);
        _ = await WaitForPhaseAsync(
            sessionStore,
            submitted.TurnId,
            SessionTurnPhase.WaitingForMemoryOutboundConsent);
        var prepared = Assert.Single((await coordinator.GetCurrentAsync())!.MemoryOutboundConsents);
        environment.TimeProvider.UtcNow = prepared.ExpiresAtUtc;

        _ = await coordinator.ConfirmMemoryOutboundAsync(
            session.Session.Id,
            submitted.TurnId,
            prepared.ConsentId,
            confirmed: true);
        var failed = await WaitForPhaseAsync(
            sessionStore,
            submitted.TurnId,
            SessionTurnPhase.Failed);

        Assert.Equal(MemoryOutboundErrorCodes.ConsentStale, failed.FailureCode);
        Assert.Empty(provider.Requests);
        Assert.Empty(await invocations.GetForSessionTurnAsync(submitted.TurnId));
        await host.StopAsync();
    }

    [Fact]
    public async Task RestartInterruptsPreparedMemoryConsentWithoutProviderInvocationOrReplay()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new SwitchingRecordingProvider(
            "provider-a",
            "model-a",
            _ => Task.FromResult("不应调用"),
            "https://provider-a.example/v1/chat");
        Guid turnId;
        using (var firstHost = environment.BuildHost(services =>
               {
                   services.AddSingleton<IAiSettingsStore>(
                       new MutableSettingsStore(Route("provider-a", "model-a")));
                   services.AddSingleton(new ChatProviderRegistry([provider]));
               }))
        {
            await firstHost.StartAsync();
            var coordinator = firstHost.Services.GetRequiredService<SessionCoordinator>();
            var memoryService = firstHost.Services.GetRequiredService<MemoryService>();
            var memory = await memoryService.CreateAsync(MemoryDraft.Create(
                MemoryCategory.UserFact,
                MemoryScope.Global,
                "重启检查",
                "不能自动重放",
                null,
                DateTimeOffset.UtcNow));
            var session = await coordinator.StartNewAsync("重启不发送");
            var submitted = await coordinator.SubmitAsync(
                session.Session.Id,
                "等待确认",
                "Text",
                "memory-restart-interrupt",
                memoryItems:
                [
                    new MemoryOutboundItemReference(memory.Metadata.Id, memory.Metadata.Version)
                ]);
            turnId = submitted.TurnId;
            _ = await WaitForPhaseAsync(
                firstHost.Services.GetRequiredService<ISessionStore>(),
                turnId,
                SessionTurnPhase.WaitingForMemoryOutboundConsent);
            Assert.Empty(provider.Requests);
            await firstHost.StopAsync();
        }

        using (var restartedHost = environment.BuildHost(services =>
               {
                   services.AddSingleton<IAiSettingsStore>(
                       new MutableSettingsStore(Route("provider-a", "model-a")));
                   services.AddSingleton(new ChatProviderRegistry([provider]));
               }))
        {
            await restartedHost.StartAsync();
            var recovered = await restartedHost.Services
                .GetRequiredService<ISessionStore>()
                .GetTurnAsync(turnId);
            Assert.NotNull(recovered);
            Assert.Equal(SessionTurnPhase.Interrupted, recovered.Phase);
            Assert.Equal(MemoryOutboundConsentState.Interrupted, recovered.MemoryOutboundState);
            Assert.Empty(provider.Requests);
            Assert.Empty(await restartedHost.Services
                .GetRequiredService<IAiInvocationStore>()
                .GetForSessionTurnAsync(turnId));
            await restartedHost.StopAsync();
        }
    }

    [Fact]
    public async Task UnverifiableMemoryDestinationFailsBeforeConversationInvocationOrProvider()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new SwitchingRecordingProvider(
            "provider-a",
            "model-a",
            _ => Task.FromResult("不应调用"),
            "http://provider-a.example/v1/chat");
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(
                new MutableSettingsStore(Route("provider-a", "model-a")));
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var memory = await host.Services.GetRequiredService<MemoryService>().CreateAsync(
            MemoryDraft.Create(
                MemoryCategory.UserFact,
                MemoryScope.Global,
                "安全去向",
                "HTTP 不可发送",
                null,
                DateTimeOffset.UtcNow));
        var session = await coordinator.StartNewAsync("拒绝不安全去向");

        var submitted = await coordinator.SubmitAsync(
            session.Session.Id,
            "检查去向",
            "Text",
            "memory-destination-fail-closed",
            memoryItems:
            [
                new MemoryOutboundItemReference(memory.Metadata.Id, memory.Metadata.Version)
            ]);
        var failed = await WaitForPhaseAsync(
            host.Services.GetRequiredService<ISessionStore>(),
            submitted.TurnId,
            SessionTurnPhase.Failed);

        Assert.Equal(MemoryOutboundErrorCodes.DestinationUnverifiable, failed.FailureCode);
        Assert.Empty(provider.Requests);
        Assert.Empty(await host.Services.GetRequiredService<IAiInvocationStore>()
            .GetForSessionTurnAsync(submitted.TurnId));
        await host.StopAsync();
    }

    [Fact]
    public async Task NewTurnsReadSettingsOnceAndADuplicateKeepsTheFirstFrozenRoute()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var providerA = new MetadataOnlyProvider("provider-a", "model-a");
        var providerB = new MetadataOnlyProvider("provider-b", "model-b");
        var settings = new MutableSettingsStore(Route("provider-a", "model-a"));
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(settings);
            services.AddSingleton(new ChatProviderRegistry([providerA, providerB]));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var session = await coordinator.StartNewAsync("冻结路由幂等");

        var first = await coordinator.SubmitAsync(
            session.Session.Id,
            "启动“记事本”",
            "Text",
            "frozen-route-duplicate",
            expectedIntentKind: "OpenApplication",
            expectedTarget: "notepad");
        var firstTurn = await WaitForPhaseAsync(
            store,
            first.TurnId,
            SessionTurnPhase.WaitingForConfirmation);

        await settings.SaveAsync(Route("provider-b", "model-b"));
        var duplicate = await coordinator.SubmitAsync(
            session.Session.Id,
            "启动“记事本”",
            "Text",
            "frozen-route-duplicate",
            expectedIntentKind: "OpenApplication",
            expectedTarget: "notepad");
        var duplicateTurn = await store.GetTurnAsync(duplicate.TurnId);

        await coordinator.CancelTurnAsync(session.Session.Id, first.TurnId);
        var second = await coordinator.SubmitAsync(
            session.Session.Id,
            "启动“记事本”",
            "Text",
            "frozen-route-new-turn",
            expectedIntentKind: "OpenApplication",
            expectedTarget: "notepad");
        var secondTurn = await WaitForPhaseAsync(
            store,
            second.TurnId,
            SessionTurnPhase.WaitingForConfirmation);
        await coordinator.CancelTurnAsync(session.Session.Id, second.TurnId);
        await host.StopAsync();

        Assert.False(first.WasDuplicate);
        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(first.TurnId, duplicate.TurnId);
        Assert.Equal("provider-a", firstTurn.FrozenRoute?.ProviderId);
        Assert.Equal("model-a", firstTurn.FrozenRoute?.ModelId);
        Assert.Equal(firstTurn.FrozenRoute, duplicateTurn?.FrozenRoute);
        Assert.Equal("provider-b", secondTurn.FrozenRoute?.ProviderId);
        Assert.Equal("model-b", secondTurn.FrozenRoute?.ModelId);
        Assert.Equal(2, settings.LoadCount);
        Assert.Equal(0, providerA.CompleteCount);
        Assert.Equal(0, providerB.CompleteCount);
    }

    [Fact]
    public async Task InvalidAiSettingsDoNotBlockDeterministicContextCompletionOrRefreezeTheTurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new MetadataOnlyProvider("provider-a", "model-a");
        var settings = new MutableSettingsStore(Route(" ", " "));
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(settings);
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await host.StartAsync();
        var (_, project, _) = await environment.SeedProjectsAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var session = await coordinator.StartNewAsync("无效设置下补上下文");

        var submitted = await coordinator.SubmitAsync(
            session.Session.Id,
            "把刚才那个问题处理好",
            "ProgrammingTask",
            "invalid-settings-context");
        var waiting = await WaitForPhaseAsync(
            store,
            submitted.TurnId,
            SessionTurnPhase.WaitingForProject);
        var selected = await coordinator.ProvideProjectAsync(
            session.Session.Id,
            submitted.TurnId,
            project.Id);
        var continued = selected.Turns.Single(turn => turn.Id == submitted.TurnId);
        await coordinator.CancelTurnAsync(session.Session.Id, submitted.TurnId);
        await host.StopAsync();

        Assert.Equal(SessionTurnRouteStatus.Unavailable, waiting.FrozenRoute?.Status);
        Assert.Equal("ai_settings_invalid", waiting.FrozenRoute?.FailureCode);
        Assert.Equal(waiting.FrozenRoute, continued.FrozenRoute);
        Assert.Equal(SessionTurnPhase.WaitingForConfirmation, continued.Phase);
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Fact]
    public async Task CancellationWhileResolvingTheRouteDoesNotAcceptATurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new MetadataOnlyProvider("provider-a", "model-a");
        var settings = new BlockingSettingsStore(Route("provider-a", "model-a"));
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(settings);
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var session = await coordinator.StartNewAsync("解析前取消");
        using var cancellation = new CancellationTokenSource();

        var submitting = coordinator.SubmitAsync(
            session.Session.Id,
            "启动“记事本”",
            "Text",
            "cancel-route-resolution",
            cancellation.Token,
            expectedIntentKind: "OpenApplication",
            expectedTarget: "notepad");
        await settings.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submitting);
        Assert.Empty(await store.GetTurnsAsync(session.Session.Id));
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
        await host.StopAsync();
    }

    [Fact]
    public async Task ConcurrentDuplicateSubmissionsFreezeAndPersistExactlyOnce()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new MetadataOnlyProvider("provider-a", "model-a");
        var settings = new MutableSettingsStore(Route("provider-a", "model-a"));
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(settings);
            services.AddSingleton(new ChatProviderRegistry([provider]));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var session = await coordinator.StartNewAsync("并发幂等冻结");

        // A fixed website keeps this idempotency test independent of real Start Menu/registry scans.
        // The Turn is cancelled before confirmation; no browser or network request is executed.
        var submissions = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            coordinator.SubmitAsync(
                session.Session.Id,
                "打开https://example.com/",
                "Text",
                "concurrent-frozen-route",
                expectedIntentKind: "OpenWebsite",
                expectedTarget: "https://example.com/")));
        var turnId = submissions.Select(result => result.TurnId).Distinct().Single();
        var turn = await WaitForPhaseAsync(
            store,
            turnId,
            SessionTurnPhase.WaitingForConfirmation);
        await coordinator.CancelTurnAsync(session.Session.Id, turnId);
        await host.StopAsync();

        Assert.Single(submissions, result => !result.WasDuplicate);
        Assert.Equal(15, submissions.Count(result => result.WasDuplicate));
        Assert.Single(await store.GetTurnsAsync(session.Session.Id));
        Assert.Equal(SessionTurnRouteStatus.Ready, turn.FrozenRoute?.Status);
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Fact]
    public async Task ExplicitPointerTurnCancellationStopsAcceptedProviderBeforeDelayedConfirmResponseIsObserved()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SwitchingRecordingProvider("provider-a", "model-a", _ => Task.FromResult("unused"),
            "https://provider-a.example/v1/chat", async (_, token) =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return "late result must never appear";
            });
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IAiSettingsStore>(new MutableSettingsStore(Route("provider-a", "model-a")));
            services.AddSingleton(new ChatProviderRegistry([provider]));
            services.AddSingleton<IPointerDesktopProbe>(new FixedPointerProbe(PointerSnapshot()));
            services.AddSingleton<IPointerRegionCaptureService>(new FixedPointerCapture());
            services.AddSingleton<ILocalOcrTextExtractor>(new FixedPointerOcr("synthetic"));
        });
        await host.StartAsync();
        IDesktopApiClient api = new DesktopApiClient(environment.Options.PipeName);
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var session = await coordinator.StartNewAsync("取消已接受的指针回答");
        var anchor = await api.PreparePointerRegionAsync(new PreparePointerRegionRequestDto(true));
        var submitted = await api.SubmitSessionInputAsync(new SessionInputRequestDto("合成问题",
            SessionId: session.Session.Id, PointerAnchorId: anchor.AnchorId));
        await WaitForPhaseAsync(store, submitted.TurnId, SessionTurnPhase.WaitingForPointerAnswerConsent);
        var preview = Assert.Single((await api.GetCurrentSessionAsync())!.PointerAnswerConsents!);
        async Task<SessionSnapshotDto> DelayedResponse()
        {
            var response = await api.ConfirmPointerAnswerAsync(session.Session.Id, submitted.TurnId,
                preview.ConsentId, preview.PreviewHash, true);
            await releaseResponse.Task;
            return response;
        }
        var pendingResponse = DelayedResponse();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pendingResponse.IsCompleted);
            await api.CancelSessionTurnAsync(session.Session.Id, submitted.TurnId);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForPhaseAsync(store, submitted.TurnId, SessionTurnPhase.Cancelled);
            Assert.Single(provider.Requests);
        }
        finally { releaseResponse.TrySetResult(); await pendingResponse; await host.StopAsync(); }
    }

    private static AiSettings Route(string providerId, string modelId) =>
        new(new ChatModelRoute(providerId, modelId));

    private static PointerDesktopSnapshot PointerSnapshot() => new(
        new PointerWindowIdentity(
            new WindowCaptureTarget(
                42,
                "目标窗口",
                "fake",
                7,
                new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 17, 9, 59, 0, TimeSpan.Zero)),
            new PixelBounds(100, 100, 800, 600),
            96,
            96),
        42,
        500,
        400,
        42,
        7,
        false,
        new PixelBounds(450, 350, 100, 80));

    private static async Task<SessionTurnRecord> WaitForPhaseAsync(
        ISessionStore store,
        Guid turnId,
        SessionTurnPhase phase)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var turn = await store.GetTurnAsync(turnId);
            if (turn?.Phase == phase)
            {
                return turn;
            }

            if (turn is not null && SessionTurnPhases.IsTerminal(turn.Phase))
            {
                throw new InvalidOperationException(
                    $"Turn 在到达 {phase} 前进入终态 {turn.Phase}：{turn.FailureCode} {turn.FailureMessage}");
            }

            await Task.Delay(20);
        }

        var lastTurn = await store.GetTurnAsync(turnId);
        if (lastTurn?.Phase == phase) return lastTurn;
        throw new TimeoutException(
            $"Turn 未在预期时间内进入 {phase}；实际阶段：{lastTurn?.Phase}；错误码：{lastTurn?.FailureCode}。");
    }

    private static async Task<string?> TryConfirmPointerAnswerAsync(
        IDesktopApiClient client,
        Guid sessionId,
        Guid turnId,
        Guid consentId,
        string previewHash)
    {
        try
        {
            _ = await client.ConfirmPointerAnswerAsync(
                sessionId,
                turnId,
                consentId,
                previewHash,
                confirmed: true);
            return null;
        }
        catch (DesktopApiException exception)
        {
            return exception.Error.Code;
        }
    }

    private sealed class MutableSettingsStore(AiSettings current) : IAiSettingsStore
    {
        private AiSettings _current = current;

        public int LoadCount { get; private set; }

        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult(_current);
        }

        public Task SaveAsync(
            AiSettings settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedPointerProbe(PointerDesktopSnapshot snapshot) : IPointerDesktopProbe
    {
        public PointerDesktopSnapshot Snapshot { get; set; } = snapshot;
        public PointerDesktopSnapshot CaptureCurrent() => Snapshot;

        public PointerDesktopSnapshot ObserveAt(int screenX, int screenY) => Snapshot with
        {
            ScreenX = screenX,
            ScreenY = screenY
        };
    }

    private sealed class FixedPointerCapture : IPointerRegionCaptureService
    {
        public int Count;
        public Task<CapturedPointerRegion> CaptureAsync(
            WindowCaptureTarget target,
            PixelBounds windowBounds,
            PixelBounds regionBounds,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return Task.FromResult(new CapturedPointerRegion([1], 320, 120, "fake"));
        }
    }

    private sealed class FixedPointerOcr(string text) : ILocalOcrTextExtractor
    {
        public Task<string> ExtractAsync(
            ReadOnlyMemory<byte> pngBytes,
            CancellationToken cancellationToken = default) => Task.FromResult(text);
    }

    private sealed class BlockingSettingsStore(AiSettings settings) : IAiSettingsStore
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCount { get; private set; }

        public async Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return settings;
        }

        public Task SaveAsync(
            AiSettings value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingMemoryConsentPublicationObserver
        : ISessionMemoryConsentPublicationObserver
    {
        private Guid? _blockedTurnId;

        public TaskCompletionSource BeforePublishReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource PublicationReleased { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid ConsentId { get; private set; }

        public int AfterPublishForBlockedTurn { get; private set; }

        public async Task BeforePublishAsync(
            Guid turnId,
            Guid consentId,
            CancellationToken cancellationToken)
        {
            if (_blockedTurnId is not null)
            {
                return;
            }

            _blockedTurnId = turnId;
            ConsentId = consentId;
            using var registration = cancellationToken.Register(
                () => CancellationObserved.TrySetResult());
            BeforePublishReached.TrySetResult();
            await PublicationReleased.Task;
        }

        public Task AfterPublishAsync(
            Guid turnId,
            Guid consentId,
            CancellationToken cancellationToken)
        {
            if (turnId == _blockedTurnId && consentId == ConsentId)
            {
                AfterPublishForBlockedTurn++;
            }

            return Task.CompletedTask;
        }

        public void ReleasePublication() => PublicationReleased.TrySetResult();
    }

    private sealed class MetadataOnlyProvider(string providerId, string modelId) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            $"{providerId} isolated destination",
            SendsDataOffDevice: false,
            [new ChatModelDescriptor(modelId, modelId, ChatModelCapabilities.None)]);

        public int CompleteCount { get; private set; }

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            throw new InvalidOperationException("冻结路由解析不得执行 Provider。");
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("冻结路由解析不得执行健康检查。");

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SwitchingRecordingProvider(
        string providerId,
        string modelId,
        Func<ChatModelRequest, Task<string>> response,
        string? dataDestination = null,
        Func<ChatModelRequest, CancellationToken, Task<string>>? cancellableResponse = null) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            dataDestination ?? $"{providerId} isolated destination",
            SendsDataOffDevice: false,
            [new ChatModelDescriptor(modelId, modelId, ChatModelCapabilities.None)]);

        public List<ChatModelRequest> Requests { get; } = [];

        public async Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var text = cancellableResponse is null
                ? await response(request)
                : await cancellableResponse(request, cancellationToken);
            return new ChatModelResponse(
                text,
                ChatFinishReason.Stop,
                null,
                new ChatProviderMetadata(
                    Descriptor.ProviderId,
                    request.ModelId,
                    null,
                    Descriptor.DataDestination),
                StructuredJson: request.Prompt?.PromptId == "intent.semantic" ? text : null);
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("冻结路由执行不得调用健康检查。");

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingSemanticSuggester : ISemanticIntentSuggester
    {
        public int CallCount { get; private set; }

        public Task<SemanticIntentSuggestion?> SuggestAsync(
            Guid sessionTurnId,
            FrozenChatModelRoute frozenRoute,
            string text,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("Unavailable 路由不得进入语义模型。");
        }
    }
}
