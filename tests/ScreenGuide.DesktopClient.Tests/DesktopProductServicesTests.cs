using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class DesktopProductServicesTests
{
    private const string CanaryApiKey = "sk-yuanshuStage2CANARY1234567890";

    [Fact]
    public void NotificationPlannerUsesOnlySystemSummaryForAllowedStates()
    {
        var tasks = new[]
        {
            Task("Running", "任务正在执行。"),
            Task("Succeeded", "系统证据确认：修改 1 个文件，测试全部通过。"),
            Task("WaitingForUser", "任务需要你的决定才能继续。"),
            Task("Failed", "任务执行失败，请打开任务查看原因。")
        };

        var notifications = NotificationPlanner.Plan(tasks, new HashSet<string>());

        Assert.Equal(3, notifications.Count);
        Assert.DoesNotContain(notifications, item => item.Message.Contains("raw", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notifications, item => item.Kind == "Completed");
        Assert.Contains(notifications, item => item.Kind == "WaitingForUser");
        Assert.Contains(notifications, item => item.Kind == "Failed");
    }

    [Fact]
    public void NotificationPlannerDoesNotRepeatDeliveredEvent()
    {
        var task = Task("Succeeded", "任务已通过系统验证。");
        var first = Assert.Single(NotificationPlanner.Plan([task], new HashSet<string>()));

        var repeated = NotificationPlanner.Plan([task], new HashSet<string> { first.Key });

        Assert.Empty(repeated);
    }

    [Theory]
    [InlineData(false, "Running", TrayVisualState.Error)]
    [InlineData(true, "WaitingForUser", TrayVisualState.WaitingForUser)]
    [InlineData(true, "Running", TrayVisualState.Working)]
    [InlineData(true, "Succeeded", TrayVisualState.Idle)]
    public void TrayStateReflectsHostAndTaskState(
        bool hostOnline,
        string taskStatus,
        TrayVisualState expected)
    {
        Assert.Equal(expected, TrayStateResolver.Resolve(hostOnline, [Task(taskStatus, "摘要")]));
    }

    [Fact]
    public async Task ClientSettingsPersistOutsideRepositoryDataModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-client-settings-{Guid.NewGuid():N}");
        try
        {
            var store = new DesktopClientSettingsStore(root);
            var expected = new DesktopClientSettings
            {
                StartWithWindows = true,
                RunInBackground = true,
                CloseToTray = true,
                NotificationsEnabled = false,
                OnboardingCompleted = true,
                DeliveredNotificationKeys = ["one"]
            };

            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();

            Assert.Equal(expected.StartWithWindows, actual.StartWithWindows);
            Assert.Equal(expected.NotificationsEnabled, actual.NotificationsEnabled);
            Assert.Contains("one", actual.DeliveredNotificationKeys);
            Assert.StartsWith(root, store.SettingsPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("codex_not_found", "安装")]
    [InlineData("codex_version_incompatible", "0.147.0")]
    [InlineData("codex_login_required", "登录")]
    [InlineData("network_unavailable", "网络")]
    [InlineData("project_missing", "重新选择")]
    [InlineData("project_not_git", "Git")]
    [InlineData("project_not_authorized", "授权")]
    [InlineData("desktop_action_not_authorized", "确认")]
    [InlineData("input_invalid", "https://")]
    [InlineData("database_unavailable", "数据")]
    public void ErrorMapperProvidesPlainLanguageRecoveryAction(string code, string expected)
    {
        var error = UserFacingErrorMapper.Map(new DesktopApiException(
            new DesktopApiError(code, "普通用户错误", "technical")));

        Assert.Equal("普通用户错误", error.Message);
        Assert.Contains(expected, error.SuggestedAction);
        Assert.Equal(code, error.TechnicalDetail);
    }

    [Fact]
    public async Task FirstLaunchStateChangesOnlyAfterOnboardingCompletionIsSaved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-onboarding-{Guid.NewGuid():N}");
        try
        {
            var store = new DesktopClientSettingsStore(root);
            Assert.False((await store.LoadAsync()).OnboardingCompleted);

            await store.SaveAsync(new DesktopClientSettings { OnboardingCompleted = true });

            Assert.True((await store.LoadAsync()).OnboardingCompleted);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SecondClientInstanceSignalsExistingCurrentUserInstance()
    {
        const string variable = "SCREEN_GUIDE_PIPE_NAME";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, $"ScreenGuide.DesktopClient.Tests.{Guid.NewGuid():N}");
        try
        {
            using var first = DesktopClientSingleInstance.TryAcquire();
            Assert.NotNull(first);
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.SetActivationHandler(() => activated.TrySetResult());

            var second = await System.Threading.Tasks.Task.Run(DesktopClientSingleInstance.TryAcquire);
            Assert.Null(second);
            Assert.True(await DesktopClientSingleInstance.TrySignalExistingAsync());
            await activated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    public async Task ReopeningPreservesVoiceIntentAndManagementNeverGrantsIt(
        bool management, bool voice, bool expectedManagement, bool expectedVoice)
    {
        const string variable = "SCREEN_GUIDE_PIPE_NAME";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, $"ScreenGuide.Activation.Tests.{Guid.NewGuid():N}");
        try
        {
            using var first = DesktopClientSingleInstance.TryAcquire();
            Assert.NotNull(first);
            var activated = new TaskCompletionSource<DesktopActivationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            first.SetActivationRequestHandler(request => activated.TrySetResult(request));
            Assert.True(await DesktopClientSingleInstance.TrySignalExistingAsync(
                showManagement: management, startListening: voice));
            var received = await activated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new DesktopActivationRequest(expectedManagement, expectedVoice), received);
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Fact]
    public async Task ReopeningWhileStartupLoadsRetainsTheExplicitVoiceIntent()
    {
        const string variable = "SCREEN_GUIDE_PIPE_NAME";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, $"ScreenGuide.Activation.Tests.{Guid.NewGuid():N}");
        try
        {
            using var first = DesktopClientSingleInstance.TryAcquire();
            Assert.NotNull(first);
            Assert.True(await DesktopClientSingleInstance.TrySignalExistingAsync(startListening: true));
            var activated = new TaskCompletionSource<DesktopActivationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            first.SetActivationRequestHandler(request => activated.TrySetResult(request));
            var received = await activated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new DesktopActivationRequest(false, true), received);
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Fact]
    public void HostStartupFailureMapsToPlainDatabaseRecoveryMessage()
    {
        var error = UserFacingErrorMapper.Map(new DesktopHostStartupException(
            "database_unavailable",
            "本地任务数据无法打开。",
            "SqliteException: malformed"));

        Assert.Contains("数据", error.Message, StringComparison.Ordinal);
        Assert.Contains("备份", error.SuggestedAction, StringComparison.Ordinal);
        Assert.Equal("database_unavailable", error.TechnicalDetail);
    }

    [Fact]
    public void UserFacingTechnicalDetailsDoNotEchoProviderResponseOrCredential()
    {
        const string rawMarker = "RAW_PROVIDER_UI_RESPONSE";
        var apiError = UserFacingErrorMapper.Map(new DesktopApiException(
            new DesktopApiError(
                "provider_unavailable",
                "AI 服务暂时不可用。",
                $"{rawMarker}: {CanaryApiKey}")));
        var localError = UserFacingErrorMapper.Map(
            new InvalidOperationException($"{rawMarker}: {CanaryApiKey}"));
        var untrustedCodeError = UserFacingErrorMapper.Map(new DesktopApiException(
            new DesktopApiError(
                $"provider unavailable {CanaryApiKey}",
                "AI 服务暂时不可用。",
                "ignored")));

        Assert.Equal("provider_unavailable", apiError.TechnicalDetail);
        Assert.Equal("InvalidOperationException", localError.TechnicalDetail);
        Assert.Equal("host_error", untrustedCodeError.TechnicalDetail);
        Assert.DoesNotContain(rawMarker, apiError.TechnicalDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryApiKey, apiError.TechnicalDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(rawMarker, localError.TechnicalDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryApiKey, localError.TechnicalDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryApiKey, untrustedCodeError.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientCrashFileDoesNotPersistProviderResponseOrCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-client-crash-{Guid.NewGuid():N}");
        const string rawMarker = "RAW_PROVIDER_CLIENT_RESPONSE";
        try
        {
            DesktopClientFailureRecorder.Record(
                new InvalidOperationException($"{rawMarker}: {CanaryApiKey}"),
                root);
            var payload = File.ReadAllText(Path.Combine(
                root,
                "state",
                DesktopClientFailureRecorder.FileName));

            Assert.Contains("InvalidOperationException", payload, StringComparison.Ordinal);
            Assert.DoesNotContain(rawMarker, payload, StringComparison.Ordinal);
            Assert.DoesNotContain(CanaryApiKey, payload, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void NotificationActivationRoutesToTheTaskThatWasActuallyShown()
    {
        var router = new NotificationActivationRouter();
        Assert.False(router.TryActivate(out _));
        var taskId = Guid.NewGuid();

        router.MarkShown(taskId);

        Assert.True(router.TryActivate(out var activated));
        Assert.Equal(taskId, activated);
    }

    [Fact]
    public void VoiceFirstHomeRequiresVisibleConsentAndExposesStop()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml"));
        var onboarding = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ScreenGuide.DesktopClient",
            "OnboardingWindow.xaml"));

        Assert.Contains("VoiceStateText", mainWindow, StringComparison.Ordinal);
        Assert.Contains("语音未开启", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"StartVoice\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"StopVoice\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("元枢打开后会自动聆听", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("DashboardTaskTextBox", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("PushToTalkButton", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("发送文字", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("OnboardingPushToTalkButton", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstTaskInstructionTextBox", onboarding, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ScreenGuide.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("ScreenGuide repository root was not found.");
    }

    private static TaskSummaryDto Task(string status, string? summary) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Project",
            "Task",
            status,
            status == "Succeeded" ? "Verified" : null,
            summary,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            status is "Running" or "WaitingForUser" ? null : DateTimeOffset.UtcNow,
            null);
}
