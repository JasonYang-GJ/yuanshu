using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenGuide.Agent.Abstractions;
using ScreenGuide.Agent.Codex;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.DeepSeek;
using ScreenGuide.AI.Qwen;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Security;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Evidence;
using ScreenGuide.Persistence.Runtime;
using ScreenGuide.Persistence.Sqlite;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;
using ScreenGuide.Vision.Windows;

namespace ScreenGuide.DesktopHost.Configuration;

public static class DesktopHostFactory
{
    public static IHost Build(
        string[] args,
        DesktopHostOptions? options = null,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = Host.CreateApplicationBuilder(args);
        var resolvedOptions = options ?? DesktopHostOptions.FromEnvironment();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new RollingFileLoggerProvider(resolvedOptions.LogsDirectory));

        builder.Services.AddSingleton(resolvedOptions);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ILocalTaskStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new SqliteTaskStore(hostOptions.DatabasePath);
        });
        builder.Services.AddSingleton<IConversationStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new SqliteConversationStore(hostOptions.DatabasePath);
        });
        builder.Services.AddSingleton<ISessionStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new SqliteSessionStore(hostOptions.DatabasePath);
        });
        builder.Services.AddSingleton<IAiInvocationStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new SqliteAiInvocationStore(hostOptions.DatabasePath);
        });
        builder.Services.AddSingleton<IProviderCredentialStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new WindowsDpapiCredentialStore(
                hostOptions.SecretsDirectory,
                services.GetRequiredService<TimeProvider>());
        });
        builder.Services.AddSingleton<IAiSettingsStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new FileAiSettingsStore(
                hostOptions.AiSettingsPath,
                new AiSettings(new ChatModelRoute(
                    DeepSeekChatModelProvider.ProviderId,
                    DeepSeekChatModelProvider.ProModelId)));
        });
        builder.Services.AddSingleton<IMemoryStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new SqliteMemoryStore(hostOptions.DatabasePath);
        });
        builder.Services.AddSingleton<IMemoryContentProtector, WindowsDpapiMemoryContentProtector>();
        builder.Services.AddSingleton(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return PromptRegistry.LoadAsync(hostOptions.PromptRegistryDirectory)
                .GetAwaiter()
                .GetResult();
        });
        builder.Services.AddSingleton<TaskCancellationRegistry>();
        builder.Services.AddSingleton<TaskCancellationService>();
        builder.Services.AddSingleton<TaskRecoveryService>();
        builder.Services.AddSingleton<TaskStateChangeHub>();
        builder.Services.AddSingleton(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new EvidenceOptions(hostOptions.EvidenceDataDirectory);
        });
        builder.Services.AddSingleton<GitEvidenceCollector>();
        builder.Services.AddSingleton<TaskEvidenceService>();
        builder.Services.AddSingleton(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return hostOptions.CodexExecutablePath is null
                ? CodexConnectorOptions.FromDataDirectory(hostOptions.CodexDataDirectory)
                : new CodexConnectorOptions(
                    hostOptions.CodexDataDirectory,
                    hostOptions.CodexExecutablePath);
        });
        builder.Services.AddSingleton(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return hostOptions.CodexExecutablePath is null
                ? CodexChatOptions.FromDataDirectory(hostOptions.CodexDataDirectory)
                : new CodexChatOptions(
                    hostOptions.CodexDataDirectory,
                    hostOptions.CodexExecutablePath);
        });
        builder.Services.AddSingleton<CodexConnector>();
        builder.Services.AddSingleton<CodexConversationProvider>();
        builder.Services.AddSingleton(services => new CodexChatModelProvider(
            services.GetRequiredService<CodexChatOptions>()));
        builder.Services.AddSingleton<DeepSeekChatModelProvider>();
        builder.Services.AddSingleton<QwenChatModelProvider>();
        builder.Services.AddSingleton<IChatModelProvider>(services =>
            services.GetRequiredService<CodexChatModelProvider>());
        builder.Services.AddSingleton<IChatModelProvider>(services =>
            services.GetRequiredService<DeepSeekChatModelProvider>());
        builder.Services.AddSingleton<IChatModelProvider>(services =>
            services.GetRequiredService<QwenChatModelProvider>());
        builder.Services.AddSingleton(services => new ChatProviderRegistry(
            services.GetServices<IChatModelProvider>()));
        builder.Services.AddSingleton<ModelRouter>();
        builder.Services.AddSingleton<AiSettingsService>();
        builder.Services.AddSingleton<MemoryService>();
        builder.Services.AddSingleton<ModelSemanticIntentSuggester>();
        builder.Services.AddSingleton<IDesktopTaskStepPlanner, ModelDesktopTaskPlanner>();
        builder.Services.AddSingleton<IDesktopVisualStepPlanner, ModelDesktopVisualPlanner>();
        builder.Services.AddSingleton<IDesktopVisualAutomation, DesktopVisualWorkerClient>();
        builder.Services.AddSingleton<IDesktopVisualTargetPreview, WindowsDesktopVisualTargetPreview>();
        builder.Services.AddSingleton<IDesktopVisualTaskExecutor, DesktopVisualTaskExecutionService>();
        builder.Services.AddSingleton<IDesktopTaskAutomation, DesktopTaskWorkerClient>();
        builder.Services.AddSingleton<IDesktopTaskWindowActivator, DesktopTaskWindowActivator>();
        builder.Services.AddSingleton<DesktopTaskInteractionChannel>();
        builder.Services.AddSingleton<IDesktopTaskExecutor, DesktopTaskExecutionService>();
        builder.Services.AddSingleton<ISemanticIntentSuggester>(services =>
            services.GetRequiredService<ModelSemanticIntentSuggester>());
        builder.Services.AddSingleton<CodexDiagnosticsService>();
        builder.Services.AddSingleton<IAgentConnector>(services =>
            services.GetRequiredService<CodexConnector>());
        builder.Services.AddSingleton<CapabilityPolicyEngine>();
        builder.Services.AddSingleton<IIntentPlanner, DeterministicIntentPlanner>();
        builder.Services.AddSingleton<CodexSkillAdapter>();
        builder.Services.AddSingleton<ISkillAdapter>(services =>
            services.GetRequiredService<CodexSkillAdapter>());
        builder.Services.AddSingleton<IDesktopProcessLauncher, DesktopProcessLauncher>();
        builder.Services.AddSingleton<IInstalledApplicationCatalog, InstalledApplicationCatalog>();
        builder.Services.AddSingleton<IReliableDesktopAutomation, WindowsUiAutomationService>();
        builder.Services.AddSingleton<ForegroundWindowTracker>();
        builder.Services.AddSingleton<IForegroundWindowContextProvider>(services =>
            services.GetRequiredService<ForegroundWindowTracker>());
        builder.Services.AddSingleton<ILatestMusicResolver, LatestMusicResolver>();
        builder.Services.AddSingleton<ApplicationSearchProgressChannel>();
        builder.Services.AddSingleton<IApplicationSearchProgressSink>(services => services.GetRequiredService<ApplicationSearchProgressChannel>());
        builder.Services.AddSingleton<VisualSearchConsentChannel>();
        builder.Services.AddSingleton<IVisualSearchConsent>(services => services.GetRequiredService<VisualSearchConsentChannel>());
        builder.Services.AddSingleton<IVisualSearchResultWindowVerifier, WindowsVisualSearchResultWindowVerifier>();
        builder.Services.AddSingleton<IApplicationSearchWorkflowAutomation, WindowsApplicationSearchWorkflowAutomation>();
        builder.Services.AddSingleton<ISensitiveWindowPolicy, WindowsSensitiveWindowPolicy>();
        builder.Services.AddSingleton<IWindowCaptureTargetVerifier, WindowsWindowCaptureTargetVerifier>();
        builder.Services.AddSingleton<WindowsGraphicsCaptureBackend>();
        builder.Services.AddSingleton<PrintWindowCaptureBackend>();
        builder.Services.AddSingleton<IExactWindowCaptureBackend, ResilientExactWindowCaptureBackend>();
        builder.Services.AddSingleton<IWindowCaptureService, WindowsSingleWindowCaptureService>();
        builder.Services.AddSingleton<IPointerDesktopProbe, WindowsPointerDesktopProbe>();
        builder.Services.AddSingleton<IPointerRegionCaptureService, WindowsPointerRegionCaptureService>();
        builder.Services.AddSingleton<ILocalOcrTextExtractor, WindowsLocalOcrTextExtractor>();
        builder.Services.AddSingleton<VisualSearchImageReader>();
        builder.Services.AddSingleton<IVisualSearchAutomation, WindowsVisualSearchAutomation>();
        builder.Services.AddSingleton<IWindowVisionProvider, WindowsLocalWindowVisionProvider>();
        builder.Services.AddSingleton<WindowUnderstandingService>();
        builder.Services.AddSingleton<PointerRegionUnderstandingService>();
        builder.Services.AddSingleton<WindowsDesktopSkillAdapter>();
        builder.Services.AddSingleton<ISkillAdapter>(services =>
            services.GetRequiredService<WindowsDesktopSkillAdapter>());
        builder.Services.AddSingleton<SkillAdapterRegistry>();
        builder.Services.AddSingleton<TaskSkillRouter>();
        builder.Services.AddSingleton<LocalDeviceInitializer>();
        builder.Services.AddSingleton<AgentConnectorRegistry>();
        builder.Services.AddSingleton<AgentTaskExecutionService>();
        builder.Services.AddSingleton<LocalTaskEntryService>();
        builder.Services.AddSingleton<ILocalTaskStateReader>(services =>
            services.GetRequiredService<LocalTaskEntryService>());
        builder.Services.AddSingleton<SessionTaskStateSynchronizer>();
        builder.Services.AddSingleton<RoutedConversationProvider>();
        builder.Services.AddSingleton<IConversationProvider>(services =>
            services.GetRequiredService<RoutedConversationProvider>());
        builder.Services.AddSingleton<ConversationService>();
        builder.Services.AddSingleton<SessionProjectionService>();
        builder.Services.AddSingleton<SessionOperationGateRegistry>();
        builder.Services.AddSingleton(services => new SessionCoordinator(
            services.GetRequiredService<ISessionStore>(),
            services.GetRequiredService<SessionProjectionService>(),
            services.GetRequiredService<SessionOperationGateRegistry>(),
            services.GetRequiredService<ConversationService>(),
            services.GetRequiredService<AssistantCommandService>(),
            services.GetRequiredService<LocalTaskEntryService>(),
            services.GetRequiredService<SessionTaskStateSynchronizer>(),
            services.GetRequiredService<DesktopHostState>(),
            services.GetRequiredService<ModelRouter>(),
            services.GetRequiredService<PromptRegistry>(),
            services.GetRequiredService<MemoryService>(),
            services.GetRequiredService<PointerRegionUnderstandingService>(),
            services.GetServices<ISessionMemoryConsentPublicationObserver>(),
            services.GetRequiredService<IForegroundWindowContextProvider>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ApplicationSearchProgressChannel>(),
            services.GetRequiredService<VisualSearchConsentChannel>(),
            services.GetRequiredService<IVisualSearchResultWindowVerifier>(),
            services.GetRequiredService<DesktopTaskInteractionChannel>()));
        builder.Services.AddSingleton<BridgeServerInstance>();
        builder.Services.AddSingleton<BridgeSnapshotProjection>();
        builder.Services.AddSingleton<BridgeProtocolService>();
        builder.Services.AddSingleton<DesktopActionEntryService>();
        builder.Services.AddSingleton<AssistantCommandService>();
        builder.Services.AddSingleton<IDesktopFileCatalog, DesktopFileCatalog>();
        builder.Services.AddSingleton<ProjectInspector>();
        builder.Services.AddSingleton<DesktopHostState>();
        builder.Services.AddSingleton<DesktopHostRuntime>();
        builder.Services.AddSingleton<RuntimeDataMaintenance>();
        builder.Services.AddHostedService<DesktopHostHostedService>();
        builder.Services.AddSingleton<DesktopApiDispatcher>();
        builder.Services.AddHostedService<DesktopIpcHostedService>();
        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }
}
