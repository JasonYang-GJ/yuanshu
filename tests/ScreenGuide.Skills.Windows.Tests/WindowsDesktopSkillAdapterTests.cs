using System.Text.Json;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed partial class WindowsDesktopSkillAdapterTests
{
    [Fact]
    public async Task ApplicationWorkflowReturnsOnlyStructuredVerifiedEvidence()
    {
        var catalog = new FakeCatalog();
        var app = catalog.FindById("test-app")!;
        var workflow = new ApplicationSearchWorkflow(app.Id, "示例音乐");
        var execution = new RecordingWorkflow();
        var adapter = new WindowsDesktopSkillAdapter(new RecordingLauncher(), catalog, new RecordingAutomation(), execution);
        var start = await adapter.StartAsync(Request(WindowsDesktopCapabilities.ApplicationSearchWorkflow,
            new("SearchApplication", app.Id, Argument: workflow.Query,
                ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(app), ApplicationSearch: workflow)));
        var final = await adapter.GetFinalResultAsync(start.Run);
        Assert.True(final!.Succeeded);
        var data = JsonSerializer.Deserialize<ApplicationSearchWorkflowResult>(final.DataJson!);
        Assert.True(data!.ResultsVerified);
        Assert.False(data.FollowUpInvoked);
        Assert.Equal(1, execution.Calls);
        Assert.DoesNotContain(workflow.Query, final.DataJson);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("app")]
    [InlineData("query")]
    [InlineData("binding")]
    [InlineData("missing-workflow")]
    public async Task MismatchedWorkflowInputNeverExecutes(string change)
    {
        var catalog = new FakeCatalog();
        var app = catalog.FindById("test-app")!;
        var workflow = new ApplicationSearchWorkflow(app.Id, "示例音乐");
        var input = new WindowsDesktopActionInput("SearchApplication", app.Id, Argument: workflow.Query,
            ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(app), ApplicationSearch: workflow);
        input = change switch
        {
            "kind" => input with { ActionKind = "OpenApplication" },
            "app" => input with { Target = "other" },
            "query" => input with { Argument = "other" },
            "binding" => input with { ApplicationTargetBinding = new string('0', 64) },
            _ => input with { ApplicationSearch = null }
        };
        var execution = new RecordingWorkflow();
        var adapter = new WindowsDesktopSkillAdapter(new RecordingLauncher(), catalog, new RecordingAutomation(), execution);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.StartAsync(Request(WindowsDesktopCapabilities.ApplicationSearchWorkflow, input)));
        Assert.Equal(0, execution.Calls);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("submission")]
    [InlineData("results")]
    [InlineData("unexpected-action")]
    [InlineData("play-unverified")]
    public async Task IncompleteOrUnauthorizedFollowUpCannotReturnSucceeded(string change)
    {
        var catalog = new FakeCatalog();
        var app = catalog.FindById("test-app")!;
        var workflow = new ApplicationSearchWorkflow(app.Id, "示例音乐",
            change == "play-unverified" ? ApplicationSearchFollowUp.PlayFirst : ApplicationSearchFollowUp.None);
        var good = new ApplicationSearchWorkflowResult(true, true, true, false, false, false, "已验证结果列表。");
        var result = change switch
        {
            "application" => good with { ApplicationVerified = false },
            "submission" => good with { QuerySubmitted = false },
            "results" => good with { ResultsVerified = false },
            "unexpected-action" => good with { FollowUpInvoked = true, FollowUpVerified = true },
            _ => good with { FollowUpInvoked = true }
        };
        var adapter = new WindowsDesktopSkillAdapter(new RecordingLauncher(), catalog, new RecordingAutomation(), new RecordingWorkflow { Result = result });
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => adapter.StartAsync(Request(
            WindowsDesktopCapabilities.ApplicationSearchWorkflow,
            new("SearchApplication", app.Id, Argument: workflow.Query,
                ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(app), ApplicationSearch: workflow))));
        Assert.Equal("application_search_incomplete", error.Code);
    }

    [Fact]
    public async Task OnlyCatalogApplicationIdCanLaunch()
    {
        var launcher = new RecordingLauncher();
        var adapter = CreateAdapter(launcher);
        var application = new FakeCatalog().FindById("test-app")!;

        var result = await adapter.StartAsync(Request(
            WindowsDesktopCapabilities.OpenApplication,
            new WindowsDesktopActionInput(
                "OpenApplication",
                "test-app",
                ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(application))));
        var denied = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.StartAsync(Request(
                WindowsDesktopCapabilities.OpenApplication,
                new WindowsDesktopActionInput("OpenApplication", "unknown"))));

        Assert.Equal(SkillExecutionStatus.Succeeded, result.Status);
        Assert.Equal([Environment.ProcessPath!], launcher.Targets);
        Assert.Contains("清单", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixedSafeSettingsTargetCanLaunchButArbitrarySettingsUriCannot()
    {
        var launcher = new RecordingLauncher();
        var safe = new KnownDesktopApplication(
            "windows-settings-display",
            "显示设置",
            "ms-settings:display");
        var unsafeTarget = new KnownDesktopApplication(
            "windows-settings-update",
            "Windows 更新",
            "ms-settings:windowsupdate");

        var safeAdapter = new WindowsDesktopSkillAdapter(
            launcher,
            new SingleApplicationCatalog(safe),
            new RecordingAutomation());
        var unsafeAdapter = new WindowsDesktopSkillAdapter(
            launcher,
            new SingleApplicationCatalog(unsafeTarget),
            new RecordingAutomation());

        var result = await safeAdapter.StartAsync(Request(
            WindowsDesktopCapabilities.OpenApplication,
            new WindowsDesktopActionInput(
                "OpenApplication",
                safe.Id,
                ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(safe))));
        var unsafeError = await Assert.ThrowsAsync<InstalledApplicationResolutionException>(() =>
            unsafeAdapter.StartAsync(Request(
            WindowsDesktopCapabilities.OpenApplication,
            new WindowsDesktopActionInput(
                "OpenApplication",
                unsafeTarget.Id,
                ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(unsafeTarget)))));

        Assert.Equal(SkillExecutionStatus.Succeeded, result.Status);
        Assert.Equal(InstalledApplicationErrorCodes.TargetNotAllowed, unsafeError.Code);
        Assert.Equal(["ms-settings:display"], launcher.Targets);
    }

    [Fact]
    public async Task UnsafeOrChangedCatalogTargetNeverReachesLauncher()
    {
        var launcher = new RecordingLauncher();
        var original = new KnownDesktopApplication("bound-app", "绑定应用", Environment.ProcessPath!);
        var changedPath = Path.Combine(Path.GetTempPath(), $"changed-{Guid.NewGuid():N}.exe");
        await File.WriteAllBytesAsync(changedPath, []);
        try
        {
            var changed = original with { LaunchTarget = changedPath };
            var adapter = new WindowsDesktopSkillAdapter(
                launcher,
                new ChangedApplicationCatalog(original, changed),
                new RecordingAutomation());

            var error = await Assert.ThrowsAsync<InstalledApplicationResolutionException>(() =>
                adapter.StartAsync(Request(
                WindowsDesktopCapabilities.OpenApplication,
                new WindowsDesktopActionInput(
                    "OpenApplication",
                    original.Id,
                    ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(original)))));

            Assert.Equal(InstalledApplicationErrorCodes.TargetChanged, error.Code);
            Assert.Empty(launcher.Targets);
        }
        finally
        {
            File.Delete(changedPath);
        }
    }

    [Fact]
    public async Task CatalogCannotSubstituteDifferentApplicationIdAfterAuthorization()
    {
        var launcher = new RecordingLauncher();
        var listed = new KnownDesktopApplication(
            "expected-app",
            "期望应用",
            Environment.ProcessPath!);
        var substituted = listed with { Id = "different-app" };
        var adapter = new WindowsDesktopSkillAdapter(
            launcher,
            new ChangedApplicationCatalog(listed, substituted),
            new RecordingAutomation());

        var error = await Assert.ThrowsAsync<InstalledApplicationResolutionException>(() =>
            adapter.StartAsync(Request(
                WindowsDesktopCapabilities.OpenApplication,
                new WindowsDesktopActionInput(
                    "OpenApplication",
                    listed.Id,
                    ApplicationTargetBinding:
                    InstalledApplicationCatalog.CreateTargetBinding(listed)))));

        Assert.Equal(InstalledApplicationErrorCodes.TargetChanged, error.Code);
        Assert.Empty(launcher.Targets);
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData(@"\\server\share\network.exe")]
    [InlineData("shell:Downloads")]
    [InlineData("ms-settings:windowsupdate")]
    public async Task UnsafeCatalogTargetsAreRejectedBeforeLaunch(string launchTarget)
    {
        var launcher = new RecordingLauncher();
        var application = new KnownDesktopApplication("unsafe-app", "不安全应用", launchTarget);
        var adapter = new WindowsDesktopSkillAdapter(
            launcher,
            new SingleApplicationCatalog(application),
            new RecordingAutomation());

        var error = await Assert.ThrowsAsync<InstalledApplicationResolutionException>(() =>
            adapter.StartAsync(Request(
            WindowsDesktopCapabilities.OpenApplication,
            new WindowsDesktopActionInput(
                "OpenApplication",
                application.Id,
                ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(application)))));

        Assert.Equal(InstalledApplicationErrorCodes.TargetNotAllowed, error.Code);
        Assert.Empty(launcher.Targets);
    }

    [Fact]
    public async Task MissingVisibleWindowVerificationFailsInsteadOfReportingSuccess()
    {
        var application = new KnownDesktopApplication(
            "visible-app",
            "可见应用",
            Environment.ProcessPath!);
        var adapter = new WindowsDesktopSkillAdapter(
            new FailingVisibleLauncher(),
            new SingleApplicationCatalog(application),
            new RecordingAutomation());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.StartAsync(Request(
            WindowsDesktopCapabilities.OpenApplication,
            new WindowsDesktopActionInput(
                "OpenApplication",
                application.Id,
                ApplicationTargetBinding: InstalledApplicationCatalog.CreateTargetBinding(application)))));

        Assert.Contains("窗口", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpWebsiteNeverLaunches()
    {
        var launcher = new RecordingLauncher();
        var adapter = CreateAdapter(launcher);

        await Assert.ThrowsAsync<ArgumentException>(() => adapter.StartAsync(Request(
            WindowsDesktopCapabilities.OpenWebsiteSecure,
            new WindowsDesktopActionInput("OpenWebsite", "http://example.com"))));

        Assert.Empty(launcher.Targets);
    }

    [Fact]
    public async Task ExplicitExistingFileOpensExactPath()
    {
        var launcher = new RecordingLauncher();
        var adapter = CreateAdapter(launcher);
        var path = Path.Combine(Path.GetTempPath(), $"sg-file-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "fake test data");
        try
        {
            var result = await adapter.StartAsync(Request(
                WindowsDesktopCapabilities.OpenFile,
                new WindowsDesktopActionInput("OpenFile", path)));

            Assert.Equal(SkillExecutionStatus.Succeeded, result.Status);
            Assert.Equal(Path.GetFullPath(path), Assert.Single(launcher.Targets));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SearchRequiresHostTrustedWindowIdentityAndUsesAutomation()
    {
        var automation = new RecordingAutomation();
        var adapter = CreateAdapter(new RecordingLauncher(), automation);
        var identity = new ForegroundWindowSnapshot(
            42,
            "测试窗口",
            "test-process",
            420,
            new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);

        await adapter.StartAsync(Request(
            WindowsDesktopCapabilities.SearchForeground,
            new WindowsDesktopActionInput(
                "SearchForeground",
                "测试内容",
                42,
                "测试窗口",
                WindowIdentity: identity)));
        var missingIdentity = await Assert.ThrowsAsync<WindowIdentityException>(() => adapter.StartAsync(Request(
            WindowsDesktopCapabilities.SearchForeground,
            new WindowsDesktopActionInput("SearchForeground", "不能执行"))));

        Assert.Equal(identity, automation.WindowIdentity);
        Assert.Equal("测试内容", automation.Query);
        Assert.Equal(WindowIdentityErrorCodes.Missing, missingIdentity.Code);
    }

    [Theory]
    [InlineData("天气\r\n明天")]
    [InlineData("天气\t明天")]
    [InlineData("天气\u0001明天")]
    public async Task SearchRejectsControlCharactersAtAdapterBoundary(string query)
    {
        var automation = new RecordingAutomation();
        var adapter = CreateAdapter(new RecordingLauncher(), automation);
        var identity = new ForegroundWindowSnapshot(
            42,
            "测试窗口",
            "test-process",
            420,
            new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<DesktopSearchException>(() => adapter.StartAsync(Request(
            WindowsDesktopCapabilities.SearchForeground,
            new WindowsDesktopActionInput(
                "SearchForeground",
                query,
                42,
                "测试窗口",
                WindowIdentity: identity))));

        Assert.Equal(DesktopSearchErrorCodes.InvalidQuery, error.Code);
        Assert.Equal(0, automation.SearchCallCount);
    }

    [Fact]
    public async Task ExplicitBrowserWebsiteUsesVisibleBrowserLaunch()
    {
        var launcher = new RecordingLauncher();
        var adapter = CreateAdapter(launcher);

        var result = await adapter.StartAsync(Request(
            WindowsDesktopCapabilities.OpenWebsiteInApplication,
            new WindowsDesktopActionInput(
                "OpenWebsiteInApplication",
                "chrome-app",
                Argument: "https://www.douyin.com/")));

        Assert.Equal(SkillExecutionStatus.Succeeded, result.Status);
        Assert.Equal(Environment.ProcessPath, launcher.VisibleBrowserTarget);
        Assert.Equal("https://www.douyin.com/", launcher.VisibleWebsite?.AbsoluteUri);
    }

    private static WindowsDesktopSkillAdapter CreateAdapter(
        RecordingLauncher launcher,
        RecordingAutomation? automation = null) =>
        new(launcher, new FakeCatalog(), automation ?? new RecordingAutomation());

    private static SkillInvocationRequest Request(
        string capability,
        WindowsDesktopActionInput input) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "windows.safe-launch",
            capability,
            JsonSerializer.Serialize(input),
            [new SkillResourceScope("Test", null, "explicit-target", "Execute")],
            SkillAuthorizationOrigin.ExplicitUser,
            Guid.NewGuid());

    private sealed class RecordingWorkflow : IApplicationSearchWorkflowAutomation
    {
        public int Calls { get; private set; }
        public ApplicationSearchWorkflowResult Result { get; init; } = new(true, true, true, false, false, false, "已验证结果列表。");
        public Task<ApplicationSearchWorkflowResult> ExecuteAsync(KnownDesktopApplication application, string expectedApplicationTargetBinding,
            ApplicationSearchWorkflow workflow, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCatalog : IInstalledApplicationCatalog
    {
        private static readonly KnownDesktopApplication Application =
            new("test-app", "测试应用", Environment.ProcessPath!);
        private static readonly KnownDesktopApplication Chrome =
            new("chrome-app", "Google Chrome", Environment.ProcessPath!);

        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [Application, Chrome];

        public KnownDesktopApplication? FindById(string id) =>
            id == Application.Id ? Application : id == Chrome.Id ? Chrome : null;

        public KnownDesktopApplication? FindByDisplayName(string displayName) =>
            displayName == Application.DisplayName ? Application : null;

        public KnownDesktopApplication? FindBrowser(string browserName) =>
            browserName.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ? Chrome : null;
    }

    private sealed class SingleApplicationCatalog(KnownDesktopApplication application)
        : IInstalledApplicationCatalog
    {
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [application];

        public KnownDesktopApplication? FindById(string id) =>
            id == application.Id ? application : null;

        public KnownDesktopApplication? FindByDisplayName(string displayName) => application;

        public KnownDesktopApplication? FindBrowser(string browserName) => null;
    }

    private sealed class ChangedApplicationCatalog(
        KnownDesktopApplication listed,
        KnownDesktopApplication resolved) : IInstalledApplicationCatalog
    {
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [listed];

        public KnownDesktopApplication? FindById(string id) =>
            id == listed.Id ? resolved : null;

        public KnownDesktopApplication? FindByDisplayName(string displayName) => listed;

        public KnownDesktopApplication? FindBrowser(string browserName) => null;
    }

    private sealed class RecordingLauncher : IDesktopProcessLauncher
    {
        public List<string> Targets { get; } = [];

        public string? VisibleBrowserTarget { get; private set; }

        public Uri? VisibleWebsite { get; private set; }

        public int? Start(string target)
        {
            Targets.Add(target);
            return 123;
        }

        public VisibleDesktopLaunchResult OpenApplicationVisible(string applicationLaunchTarget)
        {
            Targets.Add(applicationLaunchTarget);
            return new VisibleDesktopLaunchResult(123, 41, "测试应用");
        }

        public VisibleDesktopLaunchResult OpenWebsiteVisible(
            string? browserLaunchTarget,
            Uri website)
        {
            VisibleBrowserTarget = browserLaunchTarget;
            VisibleWebsite = website;
            Targets.Add(website.AbsoluteUri);
            return new VisibleDesktopLaunchResult(123, 42, "测试浏览器");
        }
    }

    private sealed class RecordingAutomation : IReliableDesktopAutomation
    {
        public int SearchCallCount { get; private set; }

        public ForegroundWindowSnapshot? WindowIdentity { get; private set; }

        public string? Query { get; private set; }

        public DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query)
        {
            SearchCallCount++;
            WindowIdentity = expectedWindow;
            Query = query;
            return new DesktopAutomationResult(true, "已提交");
        }

        public DesktopAutomationResult Describe(ForegroundWindowSnapshot expectedWindow) =>
            new(true, "已读取");
    }

    private sealed class FailingVisibleLauncher : IDesktopProcessLauncher
    {
        public int? Start(string target) => throw new InvalidOperationException("不应调用普通启动。");

        public VisibleDesktopLaunchResult OpenApplicationVisible(string applicationLaunchTarget) =>
            throw new InvalidOperationException("应用启动后没有验证到可见窗口。");

        public VisibleDesktopLaunchResult OpenWebsiteVisible(
            string? browserLaunchTarget,
            Uri website) => throw new InvalidOperationException("不应调用浏览器启动。");
    }
}
