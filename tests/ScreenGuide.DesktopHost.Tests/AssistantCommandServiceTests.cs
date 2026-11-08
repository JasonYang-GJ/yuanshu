using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class AssistantCommandServiceTests
{
    [Theory]
    [InlineData("打开抖音", "application_not_found", false)]
    [InlineData("打开我的抖音", "application_not_found", true)]
    [InlineData("打开抖音", "application_ambiguous", true)]
    [InlineData("打开抖音", "application_target_not_allowed", true)]
    public async Task OnlyAbsentUnqualifiedApplicationMayFallBackToKnownWebsite(string text, string code, bool rejects)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IInstalledApplicationCatalog>(new RejectingApplicationCatalog(code)));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        if (rejects)
        {
            var error = await Assert.ThrowsAsync<DesktopApiException>(() =>
                client.PlanAssistantCommandAsync(new PlanAssistantCommandRequestDto(text)));
            Assert.Equal(code, error.Error.Code);
        }
        else
        {
            var plan = await client.PlanAssistantCommandAsync(new PlanAssistantCommandRequestDto(text));
            Assert.Equal("OpenWebsite", plan.IntentKind);
            Assert.Equal("https://www.douyin.com/", plan.CanonicalTarget);
        }
        await host.StopAsync();
    }

    [Theory]
    [InlineData("打开抖音", "OpenApplication")]
    [InlineData("打开我的抖音", "OpenApplication")]
    [InlineData("打开抖音网站", "OpenWebsite")]
    [InlineData("用 Google Chrome 打开抖音", "OpenWebsite")]
    public async Task InstalledApplicationTakesPrecedenceUnlessWebsiteIsExplicit(string text, string expectedKind)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var application = new KnownDesktopApplication("fixture-douyin", "抖音",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), ["抖音", "我的抖音"]);
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IInstalledApplicationCatalog>(new StaticApplicationCatalog(application)));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var plan = await client.PlanAssistantCommandAsync(new PlanAssistantCommandRequestDto(text));
        Assert.Equal(expectedKind, plan.IntentKind);
        if (expectedKind == "OpenApplication") Assert.Equal(application.Id, plan.CanonicalTarget);
        else Assert.Equal("https://www.douyin.com/", plan.CanonicalTarget);
        await host.StopAsync();
    }

    [Fact]
    public async Task SafeSettingsUsesBoundOneShotVisibleApplicationPath()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        var settings = new KnownDesktopApplication(
            "windows-settings-display",
            "显示设置",
            "ms-settings:display",
            ["显示设置", "显示"]);
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
            services.AddSingleton<IInstalledApplicationCatalog>(
                new StaticApplicationCatalog(settings));
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("打开显示设置"));
        var completed = await client.ExecuteAssistantCommandAsync(
            new ExecuteAssistantCommandRequestDto(plan.PlanId, true, "settings-display-once"));
        var repeated = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(
                new ExecuteAssistantCommandRequestDto(plan.PlanId, true, "settings-display-once")));
        await host.StopAsync();

        Assert.Equal(settings.Id, plan.CanonicalTarget);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal("ExecutionVerified", completed.VerificationStatus);
        Assert.Equal([settings.LaunchTarget], launcher.Targets);
        Assert.Equal("action_plan_expired", repeated.Error.Code);
    }

    [Theory]
    [InlineData("打开同名工具", "application_ambiguous")]
    [InlineData("打开 Windows 更新", "application_target_not_allowed")]
    public async Task UnsafeApplicationResolutionFailsClosedWithStableError(
        string command,
        string expectedCode)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
            services.AddSingleton<IInstalledApplicationCatalog>(
                new RejectingApplicationCatalog(expectedCode));
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var failure = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.PlanAssistantCommandAsync(new PlanAssistantCommandRequestDto(command)));
        await host.StopAsync();

        Assert.Equal(expectedCode, failure.Error.Code);
        Assert.Empty(launcher.Targets);
    }

    [Theory]
    [InlineData(@"打开以管理员身份运行C:\Gate4A\admin.exe")]
    [InlineData(@"打开以管理员身份运行 C:\Gate4A\admin.exe")]
    [InlineData(@"打开 C:\Gate4A\admin.exe")]
    [InlineData("打开 admin.exe")]
    [InlineData("打开 cmd /c calc")]
    [InlineData("打开 powershell.exe")]
    [InlineData("打开 \"C:\\Gate4A\\admin.exe\" --unsafe")]
    [InlineData("打开“admin.exe” --unsafe")]
    [InlineData("打开执行命令时使用运行")]
    public async Task DangerousCompoundApplicationPhraseCreatesNoActionPlanOrExecution(
        string request)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        var catalog = new RunDialogMisbindingCatalog();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
            services.AddSingleton<IInstalledApplicationCatalog>(catalog);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto(request));
        await host.StopAsync();

        var plannedActionCount = plan.Readiness == "Ready" && plan.RequiresConfirmation ? 1 : 0;
        Assert.Equal(
            ("Unsupported", "Unsupported", (string?)null, 0, 0),
            (plan.IntentKind,
                plan.Readiness,
                plan.CanonicalTarget,
                plannedActionCount,
                launcher.Targets.Count));
    }

    [Fact]
    public async Task ApplicationTargetChangedAfterPlanFailsBeforeLaunch()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        var changedPath = Path.Combine(Path.GetTempPath(), $"changed-host-{Guid.NewGuid():N}.exe");
        File.Copy(Environment.ProcessPath!, changedPath);
        try
        {
            var original = new KnownDesktopApplication(
                "bound-app",
                "绑定应用",
                Environment.ProcessPath!);
            var changed = original with { LaunchTarget = changedPath };
            using var host = environment.BuildHost(services =>
            {
                services.AddSingleton<IDesktopProcessLauncher>(launcher);
                services.AddSingleton<IInstalledApplicationCatalog>(
                    new ChangingApplicationCatalog(original, changed));
            });
            await host.StartAsync();
            IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

            var plan = await client.PlanAssistantCommandAsync(
                new PlanAssistantCommandRequestDto("打开绑定应用"));
            var failure = await Assert.ThrowsAsync<DesktopApiException>(() =>
                client.ExecuteAssistantCommandAsync(
                    new ExecuteAssistantCommandRequestDto(plan.PlanId, true, "changed-target")));
            await host.StopAsync();

            Assert.Equal(original.Id, plan.CanonicalTarget);
            Assert.Equal("application_target_changed", failure.Error.Code);
            Assert.Empty(launcher.Targets);
        }
        finally
        {
            File.Delete(changedPath);
        }
    }

    [Fact]
    public async Task ApplicationPlanRequiresVisibleConfirmationAndRunsOnlyOnce()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IDesktopProcessLauncher>(launcher));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var rejectedPlan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("打开记事本"));
        var rejected = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(
                new ExecuteAssistantCommandRequestDto(rejectedPlan.PlanId, false)));
        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("打开记事本"));
        var completed = await client.ExecuteAssistantCommandAsync(
            new ExecuteAssistantCommandRequestDto(plan.PlanId, true, "assistant-open-notepad"));
        var repeated = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(
                new ExecuteAssistantCommandRequestDto(plan.PlanId, true, "assistant-open-notepad")));
        await host.StopAsync();

        Assert.Equal("Ready", plan.Readiness);
        Assert.Equal("OpenApplication", plan.IntentKind);
        Assert.Equal("notepad", plan.CanonicalTarget);
        Assert.True(plan.RequiresConfirmation);
        Assert.Equal("desktop_action_not_authorized", rejected.Error.Code);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal("ExecutionVerified", completed.VerificationStatus);
        Assert.NotNull(completed.Evidence);
        Assert.Contains(completed.Evidence.UnverifiedFacts, fact =>
            fact.Contains("完全加载", StringComparison.Ordinal));
        Assert.Equal("action_plan_expired", repeated.Error.Code);
        Assert.Equal(
            [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")],
            launcher.Targets);
    }

    [Fact]
    public async Task ForegroundSearchRequiresVisibleConfirmationAndRunsOnlyOnce()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingSearchAutomation();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(new FixedForegroundProvider());
            services.AddSingleton<IReliableDesktopAutomation>(automation);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var rejectedPlan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("在当前窗口搜索天气"));
        var rejected = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(
                new ExecuteAssistantCommandRequestDto(rejectedPlan.PlanId, false)));
        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("在当前窗口搜索天气"));
        var completed = await client.ExecuteAssistantCommandAsync(
            new ExecuteAssistantCommandRequestDto(plan.PlanId, true, "search-foreground-once"));
        var repeated = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(
                new ExecuteAssistantCommandRequestDto(plan.PlanId, true, "search-foreground-once")));
        await host.StopAsync();

        Assert.Equal("Ready", plan.Readiness);
        Assert.Equal("SearchForeground", plan.IntentKind);
        Assert.Equal("天气", plan.CanonicalTarget);
        Assert.True(plan.RequiresConfirmation);
        Assert.Equal("desktop_action_not_authorized", rejected.Error.Code);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal("ExecutionVerified", completed.VerificationStatus);
        Assert.Contains(completed.Evidence!.VerifiedFacts,
            fact => fact.Contains("可靠识别的搜索框", StringComparison.Ordinal));
        Assert.DoesNotContain(completed.Evidence.VerifiedFacts,
            fact => fact.Contains("Enter", StringComparison.Ordinal));
        Assert.Contains(completed.Evidence.UnverifiedFacts,
            fact => fact.Contains("搜索结果页面", StringComparison.Ordinal));
        Assert.Equal("action_plan_expired", repeated.Error.Code);
        Assert.Equal(1, automation.SearchCallCount);
        Assert.Equal("天气", automation.LastQuery);
    }

    [Fact]
    public async Task OrdinaryQuestionPlansConversationWithoutComputerAuthorization()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("请解释一下什么是文件夹"));
        await host.StopAsync();

        Assert.Equal("Conversation", plan.IntentKind);
        Assert.Equal("Ready", plan.Readiness);
        Assert.False(plan.RequiresConfirmation);
        Assert.Contains("不操作电脑", plan.UserSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompoundBrowserSearchPlansOneBoundWorkflowWithoutExecuting()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
            services.AddSingleton<IInstalledApplicationCatalog>(new BrowserCatalog());
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("打开谷歌浏览器界面并搜索打开抖音"));
        await host.StopAsync();

        Assert.Equal("ApplicationSearch", plan.IntentKind);
        Assert.Equal("Ready", plan.Readiness);
        Assert.True(plan.RequiresConfirmation);
        Assert.NotNull(plan.CanonicalTarget);
        Assert.Empty(launcher.Targets);
    }

    [Fact]
    public async Task ExplicitLowRiskVoiceCommandExecutesWithoutClickConfirmation()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
            services.AddSingleton<IInstalledApplicationCatalog>(new BrowserCatalog());
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto(
                "打开谷歌浏览器",
                InputModality: "Voice"));
        var result = await client.ExecuteAssistantCommandAsync(
            new ExecuteAssistantCommandRequestDto(
                plan.PlanId,
                Confirmed: false,
                IdempotencyKey: "explicit-voice-open-chrome",
                AuthorizationSource: "ExplicitVoice"));
        await host.StopAsync();

        Assert.Equal("Completed", result.Status);
        Assert.Equal("ExecutionVerified", result.VerificationStatus);
        Assert.Equal(Environment.ProcessPath, launcher.VisibleApplicationTarget);
    }

    [Fact]
    public async Task VoiceAuthorizationDoesNotBypassFileOrHighRiskBoundaries()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var path = Path.Combine(Path.GetTempPath(), $"voice-boundary-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "fake test data");
        try
        {
            var plan = await client.PlanAssistantCommandAsync(
                new PlanAssistantCommandRequestDto(
                    "打开这个文件",
                    SelectedFilePath: path,
                    InputModality: "Voice"));

            var exception = await Assert.ThrowsAsync<DesktopApiException>(() =>
                client.ExecuteAssistantCommandAsync(
                    new ExecuteAssistantCommandRequestDto(
                        plan.PlanId,
                        Confirmed: false,
                        AuthorizationSource: "ExplicitVoice")));

            Assert.Equal("desktop_action_not_authorized", exception.Error.Code);
        }
        finally
        {
            File.Delete(path);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task MissingSelectedFileIsRejectedBeforePlanning()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var exception = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.PlanAssistantCommandAsync(new PlanAssistantCommandRequestDto(
                "打开这个文件",
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt"))));
        await host.StopAsync();

        Assert.Equal("file_missing", exception.Error.Code);
    }

    [Fact]
    public async Task StopWindowObservationCancelsHostWorkAndClearsFrame()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var captureBytes = new byte[] { 4, 3, 2, 1 };
        var vision = new BlockingVisionProvider();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IForegroundWindowContextProvider>(
                new FixedForegroundProvider());
            services.AddSingleton<IWindowCaptureService>(
                new FixedCaptureService(captureBytes));
            services.AddSingleton<IWindowVisionProvider>(vision);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        const string operationId = "window-observation-cancel-test";
        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto(
                "我现在打开的是什么界面",
                ForegroundObservationConsent: true));

        var execution = client.ExecuteAssistantCommandAsync(
            new ExecuteAssistantCommandRequestDto(plan.PlanId, true, operationId));
        await vision.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancellationAccepted = await client.CancelWindowObservationAsync(operationId);
        await Assert.ThrowsAsync<DesktopApiException>(() => execution);
        await host.StopAsync();

        Assert.True(cancellationAccepted);
        Assert.True(vision.WasCancelled);
        Assert.All(captureBytes, value => Assert.Equal(0, value));
    }

    private sealed class RecordingLauncher : IDesktopProcessLauncher
    {
        public List<string> Targets { get; } = [];

        public string? VisibleBrowserTarget { get; private set; }

        public Uri? VisibleWebsite { get; private set; }

        public string? VisibleApplicationTarget { get; private set; }

        public int? Start(string target)
        {
            Targets.Add(target);
            return 99;
        }

        public VisibleDesktopLaunchResult OpenApplicationVisible(string applicationLaunchTarget)
        {
            VisibleApplicationTarget = applicationLaunchTarget;
            Targets.Add(applicationLaunchTarget);
            return new VisibleDesktopLaunchResult(99, 76, "测试应用");
        }

        public VisibleDesktopLaunchResult OpenWebsiteVisible(
            string? browserLaunchTarget,
            Uri website)
        {
            VisibleBrowserTarget = browserLaunchTarget;
            VisibleWebsite = website;
            Targets.Add(website.AbsoluteUri);
            return new VisibleDesktopLaunchResult(99, 77, "测试浏览器");
        }
    }

    private sealed class BrowserCatalog : IInstalledApplicationCatalog
    {
        private static readonly KnownDesktopApplication Chrome =
            new("chrome-app", "Google Chrome", Environment.ProcessPath!);

        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [Chrome];

        public KnownDesktopApplication? FindById(string id) =>
            id == Chrome.Id ? Chrome : null;

        public KnownDesktopApplication? FindByDisplayName(string displayName) =>
            string.Equals(displayName, Chrome.DisplayName, StringComparison.OrdinalIgnoreCase)
                ? Chrome
                : null;

        public KnownDesktopApplication ResolveByDisplayName(string displayName) =>
            displayName.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("谷歌", StringComparison.Ordinal)
                ? Chrome
                : throw new InstalledApplicationResolutionException(
                    InstalledApplicationErrorCodes.NotFound,
                    "没有找到浏览器。");

        public KnownDesktopApplication? FindBrowser(string browserName) =>
            browserName.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
            || browserName.Contains("谷歌", StringComparison.Ordinal)
                ? Chrome
                : null;
    }

    private sealed class RunDialogMisbindingCatalog : IInstalledApplicationCatalog
    {
        private static readonly KnownDesktopApplication RunDialog = new(
            "windows-run-dialog",
            "运行",
            "shell:AppsFolder\\Microsoft.Windows.Shell.RunDialog",
            ["运行"]);

        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [RunDialog];

        public KnownDesktopApplication? FindById(string id) =>
            string.Equals(id, RunDialog.Id, StringComparison.Ordinal) ? RunDialog : null;

        public KnownDesktopApplication? FindByDisplayName(string displayName) =>
            displayName.Contains("运行", StringComparison.Ordinal) ? RunDialog : null;

        public KnownDesktopApplication ResolveByDisplayName(string displayName) =>
            FindByDisplayName(displayName)
            ?? throw new InstalledApplicationResolutionException(
                InstalledApplicationErrorCodes.NotFound,
                "没有找到应用。");

        public KnownDesktopApplication? FindBrowser(string browserName) => null;
    }

    private sealed class StaticApplicationCatalog(KnownDesktopApplication application)
        : IInstalledApplicationCatalog
    {
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [application];

        public KnownDesktopApplication? FindById(string id) =>
            id == application.Id ? application : null;

        public KnownDesktopApplication? FindByDisplayName(string displayName) =>
            application.RegisteredNames?.Contains(displayName, StringComparer.OrdinalIgnoreCase) == true
                ? application
                : null;

        public KnownDesktopApplication ResolveByDisplayName(string displayName) =>
            FindByDisplayName(displayName)
            ?? throw new InstalledApplicationResolutionException(
                InstalledApplicationErrorCodes.NotFound,
                "没有找到应用。");

        public KnownDesktopApplication? FindBrowser(string browserName) => null;
    }

    private sealed class RejectingApplicationCatalog(string code) : IInstalledApplicationCatalog
    {
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [];

        public KnownDesktopApplication? FindById(string id) => null;

        public KnownDesktopApplication? FindByDisplayName(string displayName) => null;

        public KnownDesktopApplication ResolveByDisplayName(string displayName) =>
            throw new InstalledApplicationResolutionException(
                code,
                code == InstalledApplicationErrorCodes.Ambiguous
                    ? "识别到多个不同程序。"
                    : "这个设置页面不允许直接打开。");

        public KnownDesktopApplication? FindBrowser(string browserName) => null;
    }

    private sealed class ChangingApplicationCatalog(
        KnownDesktopApplication original,
        KnownDesktopApplication changed) : IInstalledApplicationCatalog
    {
        private int _resolutionCount;

        public IReadOnlyList<KnownDesktopApplication> GetApplications() =>
            _resolutionCount == 0 ? [original] : [changed];

        public KnownDesktopApplication? FindById(string id) =>
            id == original.Id ? (_resolutionCount == 0 ? original : changed) : null;

        public KnownDesktopApplication? FindByDisplayName(string displayName)
        {
            var application = _resolutionCount++ == 0 ? original : changed;
            return string.Equals(displayName, original.DisplayName, StringComparison.Ordinal)
                ? application
                : null;
        }

        public KnownDesktopApplication ResolveByDisplayName(string displayName)
        {
            var application = _resolutionCount++ == 0 ? original : changed;
            return application;
        }

        public KnownDesktopApplication? FindBrowser(string browserName) => null;
    }

    private sealed class FixedForegroundProvider : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot GetLastExternalWindow() =>
            new(72, "自动测试窗口", "testhost", 7200, TestProcessStart, DateTimeOffset.UtcNow);

        private static readonly DateTimeOffset TestProcessStart =
            new(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingSearchAutomation : IReliableDesktopAutomation
    {
        public int SearchCallCount { get; private set; }

        public string? LastQuery { get; private set; }

        public DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query)
        {
            SearchCallCount++;
            LastQuery = query;
            return new DesktopAutomationResult(
                true,
                "已确认内容写入唯一识别的搜索框并提交；搜索结果页面尚未验证。");
        }

        public DesktopAutomationResult Describe(ForegroundWindowSnapshot expectedWindow) =>
            new(true, "不应调用");
    }

    private sealed class FixedCaptureService(byte[] bytes) : IWindowCaptureService
    {
        public Task<CapturedWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapturedWindowFrame(bytes, 100, 60, "mock"));
    }

    private sealed class BlockingVisionProvider : IWindowVisionProvider
    {
        public string ProviderId => "blocking-test";
        public bool SendsImageOffDevice => false;
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCancelled { get; private set; }

        public async Task<WindowVisionResult> AnalyzeAsync(
            WindowVisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }

            throw new InvalidOperationException("Unreachable");
        }
    }
}
