using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;
using System.Windows;

namespace ScreenGuide.DesktopClient;

public partial class App : System.Windows.Application
{
    private TrayIconService? _tray;
    private DesktopClientSingleInstance? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            RuntimeDataProfile.ApplyForCurrentProcess();
            var launch = DesktopLaunchOptions.Parse(e.Args);
            _singleInstance = DesktopClientSingleInstance.TryAcquire();
            if (_singleInstance is null)
            {
                if (!launch.Background)
                    await DesktopClientSingleInstance.TrySignalExistingAsync(
                        showManagement: !launch.ShowAssistant, startListening: launch.StartListening);
                Shutdown();
                return;
            }

            DesktopClientFailureRecorder.Clear();
            DesktopClientFailureRecorder.RecordStage("starting-host");
            IDesktopApiClient apiClient = new DesktopApiClient();
            var hostProcessManager = new DesktopHostProcessManager(apiClient);
            await hostProcessManager.EnsureRunningAsync();
            DesktopClientFailureRecorder.RecordStage("loading-settings");
            var settingsStore = new DesktopClientSettingsStore();
            var settings = await settingsStore.LoadAsync();
            DesktopClientFailureRecorder.RecordStage($"settings-loaded:onboarding={settings.OnboardingCompleted}");
            Guid? onboardingTaskId = null;
            var openConversationAfterOnboarding = false;
            if (!settings.OnboardingCompleted)
            {
                var onboarding = new OnboardingWindow(apiClient, settingsStore, settings);
                if (onboarding.ShowDialog() != true || !onboarding.Completed)
                {
                    Shutdown();
                    return;
                }

                onboardingTaskId = onboarding.CreatedTaskId;
                openConversationAfterOnboarding = onboarding.StartWithConversation;
                settings = await settingsStore.LoadAsync();
            }

            var startupService = new WindowsStartupService();
            DesktopClientFailureRecorder.RecordStage("creating-tray");
            _tray = new TrayIconService();
            var window = new MainWindow(
                apiClient,
                hostProcessManager,
                settingsStore,
                startupService,
                _tray,
                settings);
            DesktopClientFailureRecorder.RecordStage("main-window-created");
            MainWindow = window;
            if (launch.UseIsland)
                window.ConfigureIslandSurface(BuiltInIsland.ResolveExecutable(AppContext.BaseDirectory,
                    Environment.GetEnvironmentVariable("SCREEN_GUIDE_ISLAND_PATH")));
            _singleInstance.SetActivationRequestHandler(request => Dispatcher.BeginInvoke(
                new Action(async () => await window.ActivateFromLaunchAsync(request))));
            _tray.OpenRequested += window.ShowAssistantSurface;
            _tray.ManagementRequested += window.OpenDashboardFromTray;
            _tray.IslandSettingsRequested += async () => await window.OpenIslandSettingsFromTrayAsync();
            _tray.NewTaskRequested += window.OpenNewTaskFromTray;
            _tray.CurrentTaskRequested += window.OpenCurrentTaskFromTray;
            _tray.CancelCurrentTaskRequested += async () => await window.CancelCurrentTaskFromTrayAsync();
            _tray.SettingsRequested += window.OpenSettingsFromTray;
            _tray.NotificationClicked += async taskId => await window.OpenTaskFromNotificationAsync(taskId);
            _tray.ExitRequested += async () => await window.ExitApplicationAsync();
            // Every entry initializes the same built-in surface. Management and background
            // entries must never start listening as a side effect of that initialization.
            await window.ShowAssistantAsync(launch.ShowAssistant && launch.StartListening
                && onboardingTaskId is null && !openConversationAfterOnboarding);
            if (launch.ShowAssistant && onboardingTaskId is null && !openConversationAfterOnboarding)
            {
                DesktopClientFailureRecorder.RecordStage("assistant-shown");
            }
            else if (!launch.Background || !settings.RunInBackground)
            {
                window.Show();
                DesktopClientFailureRecorder.RecordStage("main-window-shown");
            }

            await window.InitializeClientAsync();

            if (onboardingTaskId is { } taskId)
            {
                window.Show();
                await window.OpenTaskFromNotificationAsync(taskId);
            }
            else if (openConversationAfterOnboarding)
            {
                window.Show();
                window.OpenConversationCenterFromOnboarding();
            }
        }
        catch (Exception exception)
        {
            DesktopClientFailureRecorder.Record(exception);
            var error = UserFacingErrorMapper.Map(exception);
            MessageBox.Show(
                $"{error.Message}\n\n{error.SuggestedAction}",
                "元枢本地任务",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

}
