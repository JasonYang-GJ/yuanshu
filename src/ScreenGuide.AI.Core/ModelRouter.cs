using System.Collections.Concurrent;

namespace ScreenGuide.AI.Core;

public sealed record FrozenChatModelRoute(
    string ProviderId,
    string ModelId,
    ChatModelCapabilities Capabilities,
    int? ContextWindowTokens,
    string DataDestination,
    bool SendsDataOffDevice);

public enum ChatRouteResolutionStatus
{
    Ready,
    Unavailable
}

public sealed record DefaultChatRouteResolution(
    ChatRouteResolutionStatus Status,
    string? ProviderId,
    string? ModelId,
    string? DataDestination,
    bool? SendsDataOffDevice,
    string? FailureCode);

public sealed class ModelRouter(
    ChatProviderRegistry providers,
    IAiSettingsStore settingsStore)
{
    private readonly ConcurrentDictionary<Guid, IChatModelProvider> _activeTurns = new();

    public async Task<DefaultChatRouteResolution> ResolveDefaultChatRouteAsync(
        CancellationToken cancellationToken = default)
    {
        AiSettings? settings;
        try
        {
            settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return Unavailable("ai_settings_invalid");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return Unavailable("ai_settings_unreadable");
        }

        if (settings?.DefaultChatRoute is null
            || string.IsNullOrWhiteSpace(settings.DefaultChatRoute.ProviderId)
            || string.IsNullOrWhiteSpace(settings.DefaultChatRoute.ModelId))
        {
            return Unavailable("ai_settings_invalid");
        }

        var providerId = settings.DefaultChatRoute.ProviderId.Trim();
        var modelId = settings.DefaultChatRoute.ModelId.Trim();
        var provider = providers.Providers.SingleOrDefault(item =>
            string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return new DefaultChatRouteResolution(
                ChatRouteResolutionStatus.Unavailable,
                providerId,
                modelId,
                null,
                null,
                "configured_chat_provider_not_found");
        }

        var model = provider.Models.SingleOrDefault(item =>
            string.Equals(item.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return new DefaultChatRouteResolution(
                ChatRouteResolutionStatus.Unavailable,
                providerId,
                modelId,
                null,
                null,
                "configured_chat_model_not_found");
        }

        if (string.IsNullOrWhiteSpace(provider.DataDestination))
        {
            return new DefaultChatRouteResolution(
                ChatRouteResolutionStatus.Unavailable,
                provider.ProviderId,
                model.ModelId,
                null,
                null,
                "configured_chat_route_invalid");
        }

        return new DefaultChatRouteResolution(
            ChatRouteResolutionStatus.Ready,
            provider.ProviderId,
            model.ModelId,
            provider.DataDestination,
            provider.SendsDataOffDevice,
            null);
    }

    private static DefaultChatRouteResolution Unavailable(string failureCode) => new(
        ChatRouteResolutionStatus.Unavailable,
        null,
        null,
        null,
        null,
        failureCode);

    public FrozenChatModelRoute RestoreFrozenChatRoute(
        string providerId,
        string modelId,
        string dataDestination,
        bool sendsDataOffDevice)
    {
        ChatProviderRegistration registration;
        try
        {
            registration = providers.GetRequired(providerId, modelId);
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            throw FrozenRouteInvalid(providerId, modelId);
        }

        var descriptor = registration.ProviderDescriptor;
        if (!string.Equals(descriptor.ProviderId, providerId, StringComparison.Ordinal)
            || !string.Equals(registration.Model.ModelId, modelId, StringComparison.Ordinal)
            || !string.Equals(descriptor.DataDestination, dataDestination, StringComparison.Ordinal)
            || descriptor.SendsDataOffDevice != sendsDataOffDevice)
        {
            throw FrozenRouteInvalid(providerId, modelId);
        }

        return new FrozenChatModelRoute(
            descriptor.ProviderId,
            registration.Model.ModelId,
            registration.Model.Capabilities,
            registration.Model.ContextWindowTokens,
            descriptor.DataDestination,
            descriptor.SendsDataOffDevice);
    }

    private static ChatModelException FrozenRouteInvalid(string providerId, string? modelId) =>
        new(
            string.IsNullOrWhiteSpace(providerId) ? "model-router" : providerId,
            string.IsNullOrWhiteSpace(modelId) ? null : modelId,
            new ChatModelError(
                ChatModelErrorKind.InvalidRequest,
                "frozen_chat_route_invalid",
                "这条消息保存的 AI 路由已经与当前程序不一致，因此没有发送。请重新发送一条新消息。",
                IsRetryable: false));

    public async Task<FrozenChatModelRoute> FreezeDefaultChatRouteAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        ChatProviderRegistration registration;
        try
        {
            registration = providers.GetRequired(
                settings.DefaultChatRoute.ProviderId,
                settings.DefaultChatRoute.ModelId);
        }
        catch (KeyNotFoundException)
        {
            throw new ChatModelException(
                settings.DefaultChatRoute.ProviderId,
                settings.DefaultChatRoute.ModelId,
                new ChatModelError(
                    ChatModelErrorKind.ModelNotFound,
                    "configured_chat_model_not_found",
                    "默认聊天模型不可用，请在设置中重新选择 Provider 和模型。",
                    IsRetryable: false));
        }

        return new FrozenChatModelRoute(
            registration.Provider.Descriptor.ProviderId,
            registration.Model.ModelId,
            registration.Model.Capabilities,
            registration.Model.ContextWindowTokens,
            registration.Provider.Descriptor.DataDestination,
            registration.Provider.Descriptor.SendsDataOffDevice);
    }

    public async Task<ChatModelResponse> CompleteAsync(
        FrozenChatModelRoute route,
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(route.ModelId, request.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("请求模型与已冻结的模型路由不一致。");
        }

        var registration = providers.GetRequired(route.ProviderId, route.ModelId);
        request = ChatImageInputs.Prepare(request, route.ProviderId,
            route.Capabilities.HasFlag(ChatModelCapabilities.Vision)
                && registration.Model.Capabilities.HasFlag(ChatModelCapabilities.Vision));
        if (!_activeTurns.TryAdd(request.TurnId, registration.Provider))
        {
            throw new InvalidOperationException("同一个 Turn 已经有正在运行的模型请求。");
        }

        try
        {
            return await registration.Provider.CompleteAsync(
                    request,
                    streamCallback,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _activeTurns.TryRemove(request.TurnId, out _);
        }
    }

    public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _activeTurns.TryGetValue(turnId, out var provider)
            ? provider.CancelAsync(turnId, cancellationToken)
            : Task.CompletedTask;
    }
}
