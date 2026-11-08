using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class ModelRouterTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ImagesRequireBothFrozenAndRegisteredVisionCapability(bool frozenVision, bool registeredVision)
    {
        var provider = new RecordingProvider("provider-a", "model-a", capabilities:
            registeredVision ? ChatModelCapabilities.Vision : ChatModelCapabilities.None);
        var settings = new MutableSettingsStore(new AiSettings(new ChatModelRoute("provider-a", "model-a")));
        var router = new ModelRouter(new ChatProviderRegistry([provider]), settings);
        var frozen = (await router.FreezeDefaultChatRouteAsync()) with
        { Capabilities = frozenVision ? ChatModelCapabilities.Vision : ChatModelCapabilities.None };
        using var image = new ChatImage(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP4/x8AAwAB//wl3FEAAAAASUVORK5CYII="));
        var request = new ChatModelRequest(Guid.NewGuid(), Guid.NewGuid(), "model-a", "system",
            [new(ChatMessageRole.User, "synthetic image", Images: [image])]);
        if (frozenVision && registeredVision)
        {
            await router.CompleteAsync(frozen, request);
            Assert.Equal(1, provider.CompleteCount);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ChatModelException>(() => router.CompleteAsync(frozen, request));
            Assert.Equal("chat.vision_not_supported", error.Error.Code);
            Assert.Equal(0, provider.CompleteCount);
        }
        Assert.False(image.IsDisposed);
        Assert.Equal(1, settings.LoadCount);
    }

    [Fact]
    public void RestoreFrozenRouteUsesOnlyPersistedMetadataAndDoesNotReadCurrentSettings()
    {
        var providerA = new RecordingProvider("provider-a", "model-a");
        var providerB = new RecordingProvider("provider-b", "model-b");
        var settings = new MutableSettingsStore(new AiSettings(
            new ChatModelRoute("provider-b", "model-b")));
        var router = new ModelRouter(
            new ChatProviderRegistry([providerA, providerB]),
            settings);

        var route = router.RestoreFrozenChatRoute(
            "provider-a",
            "model-a",
            "api.provider-a.example",
            sendsDataOffDevice: true);

        Assert.Equal("provider-a", route.ProviderId);
        Assert.Equal("model-a", route.ModelId);
        Assert.Equal(ChatModelCapabilities.Streaming, route.Capabilities);
        Assert.Equal(0, settings.LoadCount);
        Assert.Equal(0, providerA.CompleteCount);
        Assert.Equal(0, providerB.CompleteCount);
    }

    [Fact]
    public void RestoreFrozenRouteUsesRegistrySnapshotWithoutTouchingProviderAgain()
    {
        var provider = new DescriptorGuardProvider("provider-a", "model-a");
        var settings = new MutableSettingsStore(new AiSettings(
            new ChatModelRoute("provider-b", "model-b")));
        var registry = new ChatProviderRegistry([provider]);
        var descriptorReadsAfterRegistration = provider.DescriptorReadCount;
        provider.ThrowOnDescriptorRead = true;
        var router = new ModelRouter(registry, settings);

        var route = router.RestoreFrozenChatRoute(
            "provider-a",
            "model-a",
            "api.provider-a.example",
            sendsDataOffDevice: true);

        Assert.Equal("provider-a", route.ProviderId);
        Assert.Equal("model-a", route.ModelId);
        Assert.Equal(ChatModelCapabilities.Streaming, route.Capabilities);
        Assert.Equal(descriptorReadsAfterRegistration, provider.DescriptorReadCount);
        Assert.Equal(0, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Theory]
    [InlineData("wrong destination", true)]
    [InlineData("api.provider-a.example", false)]
    public void RestoreFrozenRouteRejectsRegistryMetadataMismatchWithoutCallingProvider(
        string dataDestination,
        bool sendsDataOffDevice)
    {
        var provider = new RecordingProvider("provider-a", "model-a");
        var settings = new MutableSettingsStore(new AiSettings(
            new ChatModelRoute("provider-a", "model-a")));
        var router = new ModelRouter(new ChatProviderRegistry([provider]), settings);

        var exception = Assert.Throws<ChatModelException>(() =>
            router.RestoreFrozenChatRoute(
                "provider-a",
                "model-a",
                dataDestination,
                sendsDataOffDevice));

        Assert.Equal("frozen_chat_route_invalid", exception.Error.Code);
        Assert.Equal(0, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Theory]
    [InlineData("missing-provider", "model-a")]
    [InlineData("provider-a", "missing-model")]
    public void RestoreFrozenRouteRejectsMissingProviderOrModelWithoutReadingSettings(
        string providerId,
        string modelId)
    {
        var provider = new RecordingProvider("provider-a", "model-a");
        var settings = new MutableSettingsStore(new AiSettings(
            new ChatModelRoute("provider-a", "model-a")));
        var router = new ModelRouter(new ChatProviderRegistry([provider]), settings);

        var exception = Assert.Throws<ChatModelException>(() =>
            router.RestoreFrozenChatRoute(
                providerId,
                modelId,
                "api.provider-a.example",
                sendsDataOffDevice: true));

        Assert.Equal("frozen_chat_route_invalid", exception.Error.Code);
        Assert.Equal(0, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Theory]
    [InlineData(" ", "model-a")]
    [InlineData("provider-a", " ")]
    public void RestoreFrozenRouteMapsBlankProviderOrModelToStableSafetyError(
        string providerId,
        string modelId)
    {
        var provider = new RecordingProvider("provider-a", "model-a");
        var settings = new MutableSettingsStore(new AiSettings(
            new ChatModelRoute("provider-a", "model-a")));
        var router = new ModelRouter(new ChatProviderRegistry([provider]), settings);

        var exception = Assert.Throws<ChatModelException>(() =>
            router.RestoreFrozenChatRoute(
                providerId,
                modelId,
                "api.provider-a.example",
                sendsDataOffDevice: true));

        Assert.Equal("frozen_chat_route_invalid", exception.Error.Code);
        Assert.Equal(0, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Fact]
    public async Task FrozenRouteKeepsItsProviderWhenTheDefaultChanges()
    {
        var providerA = new RecordingProvider("provider-a", "model-a");
        var providerB = new RecordingProvider("provider-b", "model-b");
        var settings = new MutableSettingsStore(new AiSettings(
            new ChatModelRoute("provider-a", "model-a")));
        var router = new ModelRouter(
            new ChatProviderRegistry([providerA, providerB]),
            settings);
        var frozenA = await router.FreezeDefaultChatRouteAsync();
        await settings.SaveAsync(new AiSettings(
            new ChatModelRoute("provider-b", "model-b")));

        var first = await router.CompleteAsync(frozenA, Request("model-a"));
        var frozenB = await router.FreezeDefaultChatRouteAsync();
        var second = await router.CompleteAsync(frozenB, Request("model-b"));

        Assert.Equal("provider-a", first.Metadata.ProviderId);
        Assert.Equal("provider-b", second.Metadata.ProviderId);
        Assert.Equal(1, providerA.CompleteCount);
        Assert.Equal(1, providerB.CompleteCount);
        Assert.Equal("provider-a", frozenA.ProviderId);
        Assert.Equal("provider-b", frozenB.ProviderId);
    }

    [Fact]
    public async Task MissingDefaultModelFailsWithoutSilentlyChoosingAnotherProvider()
    {
        var providerA = new RecordingProvider("provider-a", "model-a");
        var providerB = new RecordingProvider("provider-b", "model-b");
        var router = new ModelRouter(
            new ChatProviderRegistry([providerA, providerB]),
            new MutableSettingsStore(new AiSettings(
                new ChatModelRoute("provider-a", "missing-model"))));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            router.FreezeDefaultChatRouteAsync());

        Assert.Equal(ChatModelErrorKind.ModelNotFound, exception.Error.Kind);
        Assert.Equal("provider-a", exception.ProviderId);
        Assert.Equal(0, providerA.CompleteCount);
        Assert.Equal(0, providerB.CompleteCount);
    }

    [Fact]
    public async Task ResolveMissingDefaultModelReturnsUnavailableWithoutCallingAProvider()
    {
        var providerA = new RecordingProvider("provider-a", "model-a");
        var providerB = new RecordingProvider("provider-b", "model-b");
        var settings = new MutableSettingsStore(new AiSettings(
            new ChatModelRoute("provider-a", "missing-model")));
        var router = new ModelRouter(
            new ChatProviderRegistry([providerA, providerB]),
            settings);

        var route = await router.ResolveDefaultChatRouteAsync();

        Assert.Equal(ChatRouteResolutionStatus.Unavailable, route.Status);
        Assert.Equal("provider-a", route.ProviderId);
        Assert.Equal("missing-model", route.ModelId);
        Assert.Null(route.DataDestination);
        Assert.Null(route.SendsDataOffDevice);
        Assert.Equal("configured_chat_model_not_found", route.FailureCode);
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, providerA.CompleteCount);
        Assert.Equal(0, providerB.CompleteCount);
    }

    [Fact]
    public async Task ResolveInvalidSettingsReturnsStableUnavailableWithoutCallingAProvider()
    {
        var provider = new RecordingProvider("provider-a", "model-a");
        var settings = new MutableSettingsStore(new AiSettings(new ChatModelRoute(" ", " ")));
        var router = new ModelRouter(new ChatProviderRegistry([provider]), settings);

        var route = await router.ResolveDefaultChatRouteAsync();

        Assert.Equal(ChatRouteResolutionStatus.Unavailable, route.Status);
        Assert.Null(route.ProviderId);
        Assert.Null(route.ModelId);
        Assert.Null(route.DataDestination);
        Assert.Null(route.SendsDataOffDevice);
        Assert.Equal("ai_settings_invalid", route.FailureCode);
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Fact]
    public async Task ResolveUnreadableSettingsReturnsStableUnavailableWithoutCallingAProvider()
    {
        var provider = new RecordingProvider("provider-a", "model-a");
        var settings = new ThrowingSettingsStore(new IOException("test settings read failure"));
        var router = new ModelRouter(new ChatProviderRegistry([provider]), settings);

        var route = await router.ResolveDefaultChatRouteAsync();

        Assert.Equal(ChatRouteResolutionStatus.Unavailable, route.Status);
        Assert.Null(route.ProviderId);
        Assert.Null(route.ModelId);
        Assert.Null(route.DataDestination);
        Assert.Null(route.SendsDataOffDevice);
        Assert.Equal("ai_settings_unreadable", route.FailureCode);
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Fact]
    public void InvalidRegistryMetadataIsRejectedBeforeRouterCanObserveProvider()
    {
        var provider = new RecordingProvider("provider-a", "model-a", " ");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ChatProviderRegistry([provider]));

        Assert.Contains("描述信息无效", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Fact]
    public async Task ResolveReadyRouteReadsSettingsOnceAndReturnsRegistryMetadataOnly()
    {
        var provider = new RecordingProvider("provider-a", "model-a");
        var settings = new MutableSettingsStore(new AiSettings(
            new ChatModelRoute("provider-a", "model-a")));
        var router = new ModelRouter(new ChatProviderRegistry([provider]), settings);

        var route = await router.ResolveDefaultChatRouteAsync();

        Assert.Equal(ChatRouteResolutionStatus.Ready, route.Status);
        Assert.Equal("provider-a", route.ProviderId);
        Assert.Equal("model-a", route.ModelId);
        Assert.Equal("api.provider-a.example", route.DataDestination);
        Assert.True(route.SendsDataOffDevice);
        Assert.Null(route.FailureCode);
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Fact]
    public async Task ResolveMissingProviderReturnsUnavailableWithoutChoosingAnotherProvider()
    {
        var provider = new RecordingProvider("provider-a", "model-a");
        var router = new ModelRouter(
            new ChatProviderRegistry([provider]),
            new MutableSettingsStore(new AiSettings(
                new ChatModelRoute("missing-provider", "model-a"))));

        var route = await router.ResolveDefaultChatRouteAsync();

        Assert.Equal(ChatRouteResolutionStatus.Unavailable, route.Status);
        Assert.Equal("missing-provider", route.ProviderId);
        Assert.Equal("model-a", route.ModelId);
        Assert.Null(route.DataDestination);
        Assert.Null(route.SendsDataOffDevice);
        Assert.Equal("configured_chat_provider_not_found", route.FailureCode);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Fact]
    public async Task ResolvePropagatesOnlyTheCallersCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new RecordingProvider("provider-a", "model-a");
        var router = new ModelRouter(
            new ChatProviderRegistry([provider]),
            new ThrowingSettingsStore(new OperationCanceledException(cancellation.Token)));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            router.ResolveDefaultChatRouteAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, provider.CompleteCount);
    }

    [Fact]
    public async Task CancellationIsForwardedToTheProviderFrozenForThatTurn()
    {
        var provider = new BlockingProvider("provider-a", "model-a");
        var router = new ModelRouter(
            new ChatProviderRegistry([provider]),
            new MutableSettingsStore(new AiSettings(
                new ChatModelRoute("provider-a", "model-a"))));
        var route = await router.FreezeDefaultChatRouteAsync();
        var request = Request("model-a");
        var running = router.CompleteAsync(route, request);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await router.CancelAsync(request.TurnId);
        var response = await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(request.TurnId, provider.CancelledTurnId);
        Assert.Equal(ChatFinishReason.Cancelled, response.FinishReason);
    }

    private static ChatModelRequest Request(string modelId) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        modelId,
        "system",
        [new ChatMessage(ChatMessageRole.User, "你好")]);

    private sealed class MutableSettingsStore(AiSettings current) : IAiSettingsStore
    {
        private AiSettings _current = current;

        public int LoadCount { get; private set; }

        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(_current);
        }

        public Task SaveAsync(
            AiSettings settings,
            CancellationToken cancellationToken = default)
        {
            _current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSettingsStore(Exception exception) : IAiSettingsStore
    {
        public int LoadCount { get; private set; }

        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromException<AiSettings>(exception);
        }

        public Task SaveAsync(
            AiSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingProvider(
        string providerId,
        string modelId,
        string? dataDestination = null,
        ChatModelCapabilities capabilities = ChatModelCapabilities.Streaming) : IChatModelProvider
    {
        public ChatProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            dataDestination ?? $"api.{providerId}.example",
            true,
            [new ChatModelDescriptor(modelId, modelId, capabilities)]);

        public int CompleteCount { get; private set; }

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            return Task.FromResult(new ChatModelResponse(
                Descriptor.ProviderId,
                ChatFinishReason.Stop,
                null,
                new ChatProviderMetadata(
                    Descriptor.ProviderId,
                    request.ModelId,
                    null,
                    Descriptor.DataDestination)));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DescriptorGuardProvider(
        string providerId,
        string modelId) : IChatModelProvider
    {
        private readonly ChatProviderDescriptor _descriptor = new(
            providerId,
            providerId,
            $"api.{providerId}.example",
            true,
            [new ChatModelDescriptor(modelId, modelId, ChatModelCapabilities.Streaming)]);

        public int DescriptorReadCount { get; private set; }

        public bool ThrowOnDescriptorRead { get; set; }

        public int CompleteCount { get; private set; }

        public ChatProviderDescriptor Descriptor
        {
            get
            {
                DescriptorReadCount++;
                if (ThrowOnDescriptorRead)
                {
                    throw new InvalidOperationException(
                        "Provider descriptor must not be read after registry construction.");
                }

                return _descriptor;
            }
        }

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            throw new InvalidOperationException("Provider completion must not be called.");
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Provider health must not be called.");

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Provider cancellation must not be called.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingProvider(string providerId, string modelId) : IChatModelProvider
    {
        private readonly TaskCompletionSource _cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChatProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            $"api.{providerId}.example",
            true,
            [new ChatModelDescriptor(modelId, modelId, ChatModelCapabilities.None)]);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid? CancelledTurnId { get; private set; }

        public async Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await _cancelled.Task.WaitAsync(cancellationToken);
            return new ChatModelResponse(
                string.Empty,
                ChatFinishReason.Cancelled,
                null,
                new ChatProviderMetadata(
                    Descriptor.ProviderId,
                    request.ModelId,
                    null,
                    Descriptor.DataDestination));
        }

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
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
}
