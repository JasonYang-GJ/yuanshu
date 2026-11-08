using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Conversations;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Tests;

public sealed partial class ApplicationSearchWorkflowHostTests
{
    private const string SearchText = "打开网易云音乐并搜索晴天";

    [Fact]
    public async Task HotChartPlaybackKeepsFullCanonicalTargetInTheOriginalSessionTurn()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = CreateRegistrationCatalog(environment.RootDirectory, "网易云音乐");
        var automation = new RecordingWorkflow();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IInstalledApplicationCatalog>(catalog);
            services.AddSingleton<IApplicationSearchWorkflowAutomation>(automation);
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("热榜歌曲范围");
        var input = await client.SubmitSessionInputAsync(new(
            "桌面上的网易云然后搜索最近最热的一首歌播放", "Text", "chart-scope", session.SessionId));
        var waiting = await WaitForPhaseAsync(client, input.TurnId, "WaitingForConfirmation");
        var application = catalog.ResolveByDisplayName("网易云音乐");
        var expected = new ApplicationSearchWorkflow(application.Id, "最近最热的一首歌",
            ApplicationSearchFollowUp.PlayFirst, MusicChart: ApplicationSearchWorkflow.NetEaseHotChart)
            .ToCanonicalTarget(InstalledApplicationCatalog.CreateTargetBinding(application));
        var turn = Assert.Single(waiting.Turns);
        Assert.Equal(input.TurnId, turn.Id);
        Assert.Equal("ApplicationSearch", turn.IntentKind);
        Assert.Equal(expected, turn.PlanTarget);
        Assert.Empty(automation.Calls);
        await client.CancelSessionTurnAsync(session.SessionId, input.TurnId);
        await host.StopAsync();
    }

    [Fact]
    public async Task RealSessionProjectionPublishesExecutionStepsBeforeCompletion()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        ProgressWorkflow? workflow = null;
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IInstalledApplicationCatalog>(CreateRegistrationCatalog(environment.RootDirectory, "网易云音乐"));
            services.AddSingleton<IApplicationSearchWorkflowAutomation>(sp => workflow = new(sp.GetRequiredService<IApplicationSearchProgressSink>()));
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("显示执行进度");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "progress", session.SessionId));
        try
        {
            await workflow!.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var executing = await client.GetCurrentSessionAsync();
            var turn = Assert.Single(executing!.Turns);
            Assert.Equal("Executing", turn.Phase);
            Assert.Equal("正在搜索…", turn.ResultSummary);
            Assert.Equal("ApplicationSearch", turn.IntentKind);
        }
        finally { workflow!.Release.TrySetResult(); }
        await WaitForPhaseAsync(client, input.TurnId, "Completed");
        await host.StopAsync();
    }

    [Fact]
    public async Task CancelledExecutionCannotPublishLateProgressOrReplaceTerminalState()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        ProgressWorkflow? workflow = null;
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IInstalledApplicationCatalog>(CreateRegistrationCatalog(environment.RootDirectory, "网易云音乐"));
            services.AddSingleton<IApplicationSearchWorkflowAutomation>(sp => workflow = new(sp.GetRequiredService<IApplicationSearchProgressSink>()) { ReportLate = true });
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("停止后不再更新步骤");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "progress-cancel", session.SessionId));
        await workflow!.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancellation = client.CancelSessionTurnAsync(session.SessionId, input.TurnId);
        await workflow.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workflow.Release.TrySetResult();
        await cancellation;
        var stopped = await client.GetCurrentSessionAsync();
        Assert.Equal("Cancelled", Assert.Single(stopped!.Turns).Phase);
        Assert.DoesNotContain("正在检查搜索结果", stopped.Turns[0].ResultSummary ?? "");
        await host.StopAsync();
    }

    [Theory]
    [InlineData("桌面上的网易云然后搜索周杰伦最新的歌", "Voice", "周杰伦")]
    [InlineData("桌面上的网易云然后搜索周杰伦最新的歌", "Text", "周杰伦")]
    [InlineData("给我打开桌面上的网易云，并搜索周杰伦最新的歌", "Voice", "周杰伦")]
    [InlineData("给我打开桌面上的网易云，并搜索周杰伦最新的歌", "Text", "周杰伦")]
    [InlineData("桌面上的网易云然后搜索“周杰伦最新的歌”", "Voice", null)]
    [InlineData("桌面上的网易云然后搜索“周杰伦最新的歌”", "Text", null)]
    public async Task NaturalDesktopSearchExecutesTheBoundWorkflowInsteadOfAskingChatHowToDoIt(
        string text, string modality, string? artist)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = CreateRegistrationCatalog(environment.RootDirectory, "网易云音乐");
        var automation = new RecordingWorkflow();
        var provider = new RecordingConversationProvider();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IInstalledApplicationCatalog>(catalog);
            services.AddSingleton<IApplicationSearchWorkflowAutomation>(automation);
            services.AddSingleton<IConversationProvider>(provider);
            services.AddSingleton<IDesktopProcessLauncher>(new RejectingLauncher());
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("自然语音应用搜索");
        var input = await client.SubmitSessionInputAsync(new(text, modality, "natural-search", session.SessionId));
        var application = catalog.ResolveByDisplayName("网易云音乐");
        var expected = new ApplicationSearchWorkflow(application.Id, "周杰伦最新的歌", LatestMusicArtist: artist)
            .ToCanonicalTarget(InstalledApplicationCatalog.CreateTargetBinding(application));
        if (modality == "Text")
        {
            var waiting = await WaitForPhaseAsync(client, input.TurnId, "WaitingForConfirmation");
            Assert.Equal(expected, Assert.Single(waiting.Turns).PlanTarget);
            Assert.Empty(automation.Calls);
            Assert.Equal(0, provider.Calls);
            await client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true);
        }
        var completed = await WaitForPhaseAsync(client, input.TurnId, "Completed");
        var turn = Assert.Single(completed.Turns);

        Assert.Equal("ApplicationSearch", turn.IntentKind);
        Assert.Equal("DesktopAction", turn.WorkKind);
        Assert.Equal(expected, turn.PlanTarget);
        var operation = Assert.Single(automation.Calls);
        Assert.Equal(application.Id, operation.ApplicationId);
        Assert.Equal("周杰伦最新的歌", operation.Query);
        Assert.Equal(artist, operation.LatestMusicArtist);
        Assert.Equal(ApplicationSearchFollowUp.None, operation.FollowUp);
        Assert.Equal(0, provider.Calls);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("就是请帮我打开网易云然后搜索最近周杰伦的新歌", "最近周杰伦的新歌")]
    [InlineData("打开网易云，搜索最近周杰伦的新歌", "最近周杰伦的新歌")]
    [InlineData("在网易云里搜周杰伦最近出的歌", "周杰伦最近出的歌")]
    public async Task RecentMusicCommandReachesTheBoundWorkflowWithoutAnOrdinaryChatInvocation(
        string text, string query)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = CreateRegistrationCatalog(environment.RootDirectory, "网易云音乐");
        var workflow = new RecordingWorkflow();
        var provider = new RecordingConversationProvider();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IInstalledApplicationCatalog>(catalog);
            services.AddSingleton<IApplicationSearchWorkflowAutomation>(workflow);
            services.AddSingleton<IConversationProvider>(provider);
            services.AddSingleton<IDesktopProcessLauncher>(new RejectingLauncher());
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("执行最近新歌搜索");
        var input = await client.SubmitSessionInputAsync(new(text, "Voice", "recent-music-command", session.SessionId));
        var completed = await WaitForPhaseAsync(client, input.TurnId, "Completed");
        var application = catalog.ResolveByDisplayName("网易云音乐");
        var expected = new ApplicationSearchWorkflow(application.Id, query, LatestMusicArtist: "周杰伦")
            .ToCanonicalTarget(InstalledApplicationCatalog.CreateTargetBinding(application));

        var turn = Assert.Single(completed.Turns);
        Assert.Equal("ApplicationSearch", turn.IntentKind);
        Assert.Equal("DesktopAction", turn.WorkKind);
        Assert.Equal(expected, turn.PlanTarget);
        var operation = Assert.Single(workflow.Calls);
        Assert.Equal(application.Id, operation.ApplicationId);
        Assert.Equal(query, operation.Query);
        Assert.Equal("周杰伦", operation.LatestMusicArtist);
        Assert.Equal(ApplicationSearchFollowUp.None, operation.FollowUp);
        Assert.Equal(0, provider.Calls);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("不要打开桌面上的网易云，也不要搜索周杰伦最新的歌")]
    [InlineData("如何打开桌面上的网易云然后搜索周杰伦最新的歌？")]
    [InlineData("如果我说桌面上的网易云然后搜索周杰伦最新的歌，你会怎么做？")]
    [InlineData("解释一下“桌面上的网易云然后搜索周杰伦最新的歌”是什么意思")]
    public async Task DiscussingOrNegatingANaturalSearchNeverAuthorizesDesktopOperations(string text)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingWorkflow();
        var provider = new RecordingConversationProvider();
        using var host = environment.BuildHost(services =>
        {
            Register(services, new(), automation);
            services.AddSingleton<IConversationProvider>(provider);
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("讨论不是执行授权");
        var input = await client.SubmitSessionInputAsync(new(text, "Voice", "not-a-command", session.SessionId));
        var completed = await WaitForPhaseAsync(client, input.TurnId, "Completed");

        Assert.Equal("Conversation", Assert.Single(completed.Turns).IntentKind);
        Assert.Empty(automation.Calls);
        Assert.Equal(1, provider.Calls);
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false, "application_not_found")]
    [InlineData(true, "application_ambiguous")]
    public async Task UnresolvedNaturalSearchFailsWithoutLaunchingOrFallingBackToChat(
        bool ambiguous, string expectedError)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = ambiguous
            ? CreateRegistrationCatalog(environment.RootDirectory, "网易云音乐", "网易云音乐")
            : CreateRegistrationCatalog(environment.RootDirectory, "另一个软件");
        var automation = new RecordingWorkflow();
        var provider = new RecordingConversationProvider();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IInstalledApplicationCatalog>(catalog);
            services.AddSingleton<IApplicationSearchWorkflowAutomation>(automation);
            services.AddSingleton<IConversationProvider>(provider);
            services.AddSingleton<IDesktopProcessLauncher>(new RejectingLauncher());
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("搜索目标必须明确");
        var input = await client.SubmitSessionInputAsync(new(
            "桌面上的网易云然后搜索周杰伦最新的歌", "Voice", "unresolved-natural-search", session.SessionId));
        await WaitForPhaseAsync(client, input.TurnId, "Failed");
        var persisted = await host.Services.GetRequiredService<ISessionStore>().GetTurnAsync(input.TurnId);

        Assert.Equal(expectedError, persisted!.FailureCode);
        Assert.Empty(automation.Calls);
        Assert.Equal(0, provider.Calls);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("桌面上的网易云然后搜索", "搜索内容")]
    [InlineData("桌面上的网易云然后搜索最新的歌", "补充歌手")]
    [InlineData("最近周杰伦的新歌", "完整指令")]
    [InlineData("呃我见到桌面上的网易云然后搜索", "完整指令")]
    [InlineData("呃我见到桌面上的网易云然后搜索最近周杰伦的新歌", "完整指令")]
    public async Task NaturalSearchWithMissingContextReportsItWithoutCallingChat(string text, string message)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingWorkflow();
        var provider = new RecordingConversationProvider();
        using var host = environment.BuildHost(services =>
        {
            Register(services, new(), automation);
            services.AddSingleton<IConversationProvider>(provider);
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("搜索缺少内容");
        var input = await client.SubmitSessionInputAsync(new(
            text, "Voice", "missing-search-query", session.SessionId));
        var failed = await WaitForPhaseAsync(client, input.TurnId, "Failed");

        Assert.Equal("Unsupported", Assert.Single(failed.Turns).IntentKind);
        Assert.Contains(message, Assert.Single(failed.Turns).ResultSummary);
        Assert.Empty(automation.Calls);
        Assert.Equal(0, provider.Calls);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("打开微信搜索助手", "微信搜索助手")]
    [InlineData("给我给我打开微信搜索助手", "微信搜索助手")]
    [InlineData("打开我的微信搜索助手", "微信搜索助手")]
    [InlineData("打开我的微信搜索助手软件", "微信搜索助手")]
    [InlineData("打开微信搜索", "微信搜索")]
    [InlineData("打开我的微信搜索", "微信搜索")]
    [InlineData("打开微信搜索再发现", "微信搜索再发现")]
    [InlineData("打开微信搜索并发现", "微信搜索并发现")]
    [InlineData("打开我的微信搜索再发现", "微信搜索再发现")]
    public async Task WholeRegisteredApplicationNameWinsOverAnImplicitSearchSplit(string text, string registeredName)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = CreateRegistrationCatalog(environment.RootDirectory, "微信", registeredName);
        var automation = new RecordingWorkflow();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IInstalledApplicationCatalog>(catalog);
            services.AddSingleton<IApplicationSearchWorkflowAutomation>(automation);
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(new(text));

        Assert.Equal("OpenApplication", plan.IntentKind);
        Assert.Equal(catalog.ResolveByDisplayName(registeredName).Id, plan.CanonicalTarget);
        Assert.Empty(automation.Calls);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("打开我的微信搜索助手", "微信搜索助手")]
    [InlineData("打开微信搜索", "微信搜索")]
    [InlineData("打开我的微信搜索再发现", "微信搜索再发现")]
    [InlineData("给我给我打开微信搜索助手", "微信搜索助手")]
    public async Task ConfirmingWholeRegisteredApplicationNamePreservesOpenApplicationIntent(string text, string registeredName)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = CreateRegistrationCatalog(environment.RootDirectory, "微信", registeredName);
        var workflow = new RecordingWorkflow();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IInstalledApplicationCatalog>(catalog);
            services.AddSingleton<IApplicationSearchWorkflowAutomation>(workflow);
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("完整应用名称确认");
        var input = await client.SubmitSessionInputAsync(new(text, "Text", "literal-app-confirm", session.SessionId));
        await WaitForPhaseAsync(client, input.TurnId, "WaitingForConfirmation");

        await client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true);
        var completed = await WaitForPhaseAsync(client, input.TurnId, "Completed");
        var application = catalog.ResolveByDisplayName(registeredName);

        Assert.Equal("OpenApplication", completed.Turns.Single().IntentKind);
        Assert.Equal(application.Id, completed.Turns.Single().PlanTarget);
        Assert.Equal(application.LaunchTarget, Assert.Single(launcher.Targets));
        Assert.Empty(workflow.Calls);
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationConfirmationCannotAcquireANewIdOrLaunchBinding(bool keepSameId)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = new MutableCatalog();
        var workflow = new RecordingWorkflow();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
        {
            Register(services, catalog, workflow);
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("应用确认不能换目标");
        var input = await client.SubmitSessionInputAsync(new("打开网易云音乐", "Text", "app-confirm-target", session.SessionId));
        var waiting = await WaitForPhaseAsync(client, input.TurnId, "WaitingForConfirmation");
        var expectedTarget = waiting.Turns.Single().PlanTarget;
        catalog.Current = keepSameId
            ? catalog.Current with { LaunchTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe") }
            : catalog.Current with { Id = "replacement-music" };

        await client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true);
        var failed = await WaitForPhaseAsync(client, input.TurnId, "Failed");

        Assert.Equal(expectedTarget, failed.Turns.Single().PlanTarget);
        Assert.Empty(launcher.Targets);
        Assert.Empty(workflow.Calls);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("打开微信并搜索助手", "微信", "助手")]
    [InlineData("打开微信，然后搜索助手", "微信", "助手")]
    [InlineData("打开网易云搜索晴天", "网易云音乐", "晴天")]
    public async Task ExplicitSequenceOrAbsentWholeNameRetainsTheSearchWorkflow(
        string text, string applicationName, string query)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = CreateRegistrationCatalog(environment.RootDirectory, "微信", "微信搜索助手", "网易云音乐");
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IInstalledApplicationCatalog>(catalog));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(new(text));
        var application = catalog.ResolveByDisplayName(applicationName);

        Assert.Equal("ApplicationSearch", plan.IntentKind);
        Assert.Equal(new ApplicationSearchWorkflow(application.Id, query)
            .ToCanonicalTarget(InstalledApplicationCatalog.CreateTargetBinding(application)), plan.CanonicalTarget);
        await host.StopAsync();
    }

    [Fact]
    public async Task FreshApplicationSearchPlanDiscoversRegistrationsOnlyOnce()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var executable = Path.Combine(environment.RootDirectory, "registered-music.exe");
        File.Copy(Environment.ProcessPath!, executable);
        var discoveries = 0;
        var catalog = new InstalledApplicationCatalog(() =>
        {
            discoveries++;
            return [new ApplicationRegistration("网易云音乐", executable, ApplicationRegistrationSource.AppPath)];
        });
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IInstalledApplicationCatalog>(catalog));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(new("打开网易云，搜索周杰伦最新的歌"));

        Assert.Equal("ApplicationSearch", plan.IntentKind);
        Assert.Equal(1, discoveries);
        await host.StopAsync();
    }

    [Fact]
    public async Task CachedApplicationPreferenceDiscoversANewWholeNameBeforeChoosingSearch()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var executable = Path.Combine(environment.RootDirectory, "registered-chat.exe");
        var literalExecutable = Path.Combine(environment.RootDirectory, "registered-search-assistant.exe");
        File.Copy(Environment.ProcessPath!, executable);
        File.Copy(Environment.ProcessPath!, literalExecutable);
        var installed = false;
        var discoveries = 0;
        var catalog = new InstalledApplicationCatalog(() =>
        {
            discoveries++;
            var chat = new ApplicationRegistration("微信", executable, ApplicationRegistrationSource.AppPath);
            var literal = new ApplicationRegistration("微信搜索助手", literalExecutable, ApplicationRegistrationSource.AppPath);
            return installed ? [chat, literal] : [chat];
        });
        _ = catalog.GetApplications();
        installed = true;
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IInstalledApplicationCatalog>(catalog));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(new("打开微信搜索助手"));

        Assert.Equal("OpenApplication", plan.IntentKind);
        Assert.Equal(catalog.ResolveByDisplayName("微信搜索助手").Id, plan.CanonicalTarget);
        Assert.Equal(2, discoveries);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("打开微信搜索助手")]
    [InlineData("打开我的微信搜索助手")]
    public async Task AmbiguousWholeRegisteredNameCannotFallBackToSearchingAShorterApp(string text)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = CreateRegistrationCatalog(environment.RootDirectory, "微信", "微信搜索助手", "微信搜索助手");
        var automation = new RecordingWorkflow();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IInstalledApplicationCatalog>(catalog);
            services.AddSingleton<IApplicationSearchWorkflowAutomation>(automation);
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);

        var failure = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.PlanAssistantCommandAsync(new(text)));

        Assert.Equal("application_ambiguous", failure.Error.Code);
        Assert.Empty(automation.Calls);
        await host.StopAsync();
    }

    [Fact]
    public async Task RejectedWholeApplicationNameCannotFallBackToAnImplicitWorkflow()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = CreateRegistrationCatalog(environment.RootDirectory, "账户设置搜索助手");
        var automation = new RecordingWorkflow();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IInstalledApplicationCatalog>(catalog);
            services.AddSingleton<IApplicationSearchWorkflowAutomation>(automation);
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var failure = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.PlanAssistantCommandAsync(new("打开账户设置搜索助手")));

        Assert.Equal("application_target_not_allowed", failure.Error.Code);
        Assert.Empty(automation.Calls);
        await host.StopAsync();
    }

    [Fact]
    public async Task PlanningFreezesTheWholeWorkflowWithoutLaunchingAnything()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = new MutableCatalog();
        var automation = new RecordingWorkflow();
        using var host = environment.BuildHost(services => Register(services, catalog, automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(new(SearchText));

        Assert.Equal("ApplicationSearch", plan.IntentKind);
        Assert.True(plan.RequiresConfirmation);
        Assert.Null(plan.ForegroundApplication);
        Assert.Equal(new ApplicationSearchWorkflow(catalog.Current.Id, "晴天")
            .ToCanonicalTarget(InstalledApplicationCatalog.CreateTargetBinding(catalog.Current)), plan.CanonicalTarget);
        Assert.Empty(automation.Calls);
        await host.StopAsync();
    }

    [Fact]
    public async Task TextCannotSpoofVoiceAuthorizationAndRawDesktopIpcCannotCreateWorkflow()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingWorkflow();
        using var host = environment.BuildHost(services => Register(services, new(), automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var plan = await client.PlanAssistantCommandAsync(new(SearchText));

        var unauthorized = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(new(plan.PlanId, false, "spoof-voice", "ExplicitVoice")));
        var raw = await Assert.ThrowsAsync<DesktopApiException>(() => client.ExecuteDesktopActionAsync(
            new("SearchApplication", "fixture-music", true, "raw-workflow")));

        Assert.Equal("desktop_action_not_authorized", unauthorized.Error.Code);
        Assert.Equal("desktop_action_not_authorized", raw.Error.Code);
        Assert.Empty(automation.Calls);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("打开网易云音乐并搜索晴天", ApplicationSearchFollowUp.None)]
    [InlineData("打开网易云音乐并搜索晴天，然后打开第一条结果", ApplicationSearchFollowUp.OpenFirst)]
    [InlineData("打开网易云音乐并搜索晴天，然后播放第一首", ApplicationSearchFollowUp.PlayFirst)]
    public async Task ExplicitVoiceExecutesOnePreciselyBoundWorkflow(string text, ApplicationSearchFollowUp followUp)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = new MutableCatalog();
        var automation = new RecordingWorkflow();
        using var host = environment.BuildHost(services => Register(services, catalog, automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var plan = await client.PlanAssistantCommandAsync(new(text, InputModality: "Voice"));
        var result = await client.ExecuteAssistantCommandAsync(new(plan.PlanId, false, "one-voice-workflow", "ExplicitVoice"));
        var repeated = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(new(plan.PlanId, false, "one-voice-workflow", "ExplicitVoice")));

        Assert.Equal("Completed", result.Status);
        Assert.Equal("WorkflowVerified", result.VerificationStatus);
        Assert.Empty(result.Evidence!.UnverifiedFacts);
        var call = Assert.Single(automation.Calls);
        Assert.Equal(catalog.Current.Id, call.ApplicationId);
        Assert.Equal("晴天", call.Query);
        Assert.Equal(followUp, call.FollowUp);
        Assert.Equal("action_plan_expired", repeated.Error.Code);
        await host.StopAsync();
    }

    [Fact]
    public async Task ARepeatedCommandIdCannotExecuteThroughAFreshPlan()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingWorkflow();
        using var host = environment.BuildHost(services => Register(services, new(), automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var first = await client.PlanAssistantCommandAsync(new(SearchText));
        var next = await client.PlanAssistantCommandAsync(new(SearchText));
        await client.ExecuteAssistantCommandAsync(new(first.PlanId, true, "duplicate-workflow"));
        var duplicate = await client.ExecuteAssistantCommandAsync(new(next.PlanId, true, "duplicate-workflow"));

        Assert.True(duplicate.WasDuplicate);
        Assert.Equal("Failed", duplicate.Status);
        Assert.Single(automation.Calls);
        await host.StopAsync();
    }

    [Fact]
    public async Task ChangedApplicationBindingIsRejectedBeforeAnyNativeOperation()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = new MutableCatalog();
        var automation = new RecordingWorkflow();
        using var host = environment.BuildHost(services => Register(services, catalog, automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var plan = await client.PlanAssistantCommandAsync(new(SearchText));
        catalog.Current = catalog.Current with
        {
            LaunchTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")
        };

        var failure = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(new(plan.PlanId, true, "changed-binding")));

        Assert.Equal("application_target_changed", failure.Error.Code);
        Assert.Empty(automation.Calls);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Voice")]
    public async Task AllStepsStayInTheOriginalTurnAndDuplicateInputCannotReplayThem(string modality)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingWorkflow();
        using var host = environment.BuildHost(services => Register(services, new(), automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("同一个请求完成搜索");
        var request = new SessionInputRequestDto(SearchText, modality, "one-session-workflow", session.SessionId);
        var submitted = await client.SubmitSessionInputAsync(request);
        if (modality == "Text")
        {
            await WaitForPhaseAsync(client, submitted.TurnId, "WaitingForConfirmation");
            Assert.Empty(automation.Calls);
            await client.ConfirmSessionTurnAsync(session.SessionId, submitted.TurnId, true);
        }
        var completed = await WaitForPhaseAsync(client, submitted.TurnId, "Completed");
        var duplicate = await client.SubmitSessionInputAsync(request);

        Assert.Equal(submitted.TurnId, duplicate.TurnId);
        Assert.Single(completed.Turns);
        Assert.Equal("ApplicationSearch", completed.Turns.Single().IntentKind);
        Assert.Equal("DesktopAction", completed.Turns.Single().WorkKind);
        Assert.Single(automation.Calls);
        await host.StopAsync();
    }

    [Fact]
    public async Task RapidReplacementCancelsTheOriginalBeforeStartingTheNextWorkflow()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new ReplacingWorkflow();
        using var host = environment.BuildHost(services => Register(services, new(), automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("替换搜索请求");
        var first = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "replace-first", session.SessionId));
        await automation.First.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var nextTask = client.SubmitSessionInputAsync(new("打开网易云音乐并搜索稻香", "Voice", "replace-next", session.SessionId));
        await automation.First.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, automation.Calls);
        automation.First.Release.TrySetResult();
        var next = await nextTask;
        var completed = await WaitForPhaseAsync(client, next.TurnId, "Completed");

        Assert.Equal("Cancelled", completed.Turns.Single(t => t.Id == first.TurnId).Phase);
        Assert.Equal(2, automation.Calls);
        Assert.Equal("稻香", automation.LastQuery);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Application")]
    [InlineData("Query")]
    [InlineData("FollowUp")]
    [InlineData("LatestArtist")]
    [InlineData("LatestRemoved")]
    [InlineData("ChartAdded")]
    [InlineData("ChartRemoved")]
    public async Task ConfirmationCannotAuthorizeAReplannedDifferentWorkflow(string changedPart)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var catalog = new MutableCatalog();
        var automation = new RecordingWorkflow();
        var planner = new MutablePlanner(environment.TimeProvider);
        using var host = environment.BuildHost(services =>
        {
            Register(services, catalog, automation);
            services.AddSingleton<IIntentPlanner>(planner);
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("搜索确认范围");
        var requestText = changedPart == "ChartRemoved" ? "打开网易云音乐并搜索热门歌曲"
            : changedPart.StartsWith("Latest", StringComparison.Ordinal)
                ? "打开网易云音乐并搜索周杰伦最新的歌" : SearchText;
        var submitted = await client.SubmitSessionInputAsync(new(requestText, "Text", "confirm-scope", session.SessionId));
        var waiting = await WaitForPhaseAsync(client, submitted.TurnId, "WaitingForConfirmation");
        var originalTarget = waiting.Turns.Single(t => t.Id == submitted.TurnId).PlanTarget;
        if (changedPart == "Application") catalog.Current = catalog.Current with { Id = "different-music" };
        else planner.Change = changedPart;

        await client.ConfirmSessionTurnAsync(session.SessionId, submitted.TurnId, true);
        var failed = await WaitForPhaseAsync(client, submitted.TurnId, "Failed");
        var turn = failed.Turns.Single(t => t.Id == submitted.TurnId);

        Assert.Equal(originalTarget, turn.PlanTarget);
        Assert.Contains("确认后发生变化", turn.ResultSummary);
        Assert.Empty(automation.Calls);
        Assert.Single(failed.Turns);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Voice")]
    [InlineData("Text")]
    public async Task NativeVerificationFailureKeepsPartialResultVisibleAndFailsTheSameTurn(string modality)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingWorkflow
        {
            Error = new ApplicationSearchWorkflowException("application_search_results_unverified",
                "搜索已经提交，但尚未确认结果加载；没有打开或播放任何结果。")
        };
        using var host = environment.BuildHost(services => Register(services, new(), automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("结果核验失败");
        var input = await client.SubmitSessionInputAsync(new(SearchText, modality, "unverified-search", session.SessionId));
        if (modality == "Text")
        {
            await WaitForPhaseAsync(client, input.TurnId, "WaitingForConfirmation");
            await client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true);
        }
        var failed = await WaitForPhaseAsync(client, input.TurnId, "Failed");

        Assert.Contains("搜索已经提交", failed.Turns.Single().ResultSummary);
        Assert.Contains("没有打开或播放", failed.Turns.Single().ResultSummary);
        Assert.Equal(failed.Turns.Single().FailureMessage, failed.Turns.Single().ResultSummary);
        var persisted = await host.Services.GetRequiredService<ISessionStore>().GetTurnAsync(input.TurnId);
        Assert.Equal("application_search_results_unverified", persisted!.FailureCode);
        Assert.Single(automation.Calls);
        await host.StopAsync();
    }

    [Fact]
    public async Task UnverifiedNativeSuccessCannotBeReportedAsCompleted()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingWorkflow { ReturnUnverified = true };
        using var host = environment.BuildHost(services => Register(services, new(), automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var plan = await client.PlanAssistantCommandAsync(new(SearchText));

        await Assert.ThrowsAsync<DesktopApiException>(() => client.ExecuteAssistantCommandAsync(
            new(plan.PlanId, true, "reject-unverified-success")));
        Assert.Single(automation.Calls);
        await host.StopAsync();
    }

    [Fact]
    public async Task VerifiedEmptyResultsCompleteSearchButNeverClaimAnyResultWasOpened()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new RecordingWorkflow { NoResults = true };
        using var host = environment.BuildHost(services => Register(services, new(), automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var search = await client.PlanAssistantCommandAsync(new(SearchText));
        var completed = await client.ExecuteAssistantCommandAsync(new(search.PlanId, true, "empty-search"));
        Assert.Equal("Completed", completed.Status);
        Assert.Contains(completed.Evidence!.VerifiedFacts, fact => fact.Contains("没有匹配结果", StringComparison.Ordinal));

        var play = await client.PlanAssistantCommandAsync(new(SearchText + "，然后播放第一首"));
        await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ExecuteAssistantCommandAsync(new(play.PlanId, true, "empty-cannot-play")));
        Assert.Equal(2, automation.Calls.Count);
        await host.StopAsync();
    }

    [Fact]
    public async Task StopCancelsTheNativeWorkflowAndRejectsLateSuccess()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var automation = new BlockingWorkflow();
        using var host = environment.BuildHost(services => Register(services, new(), automation));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("停止应用搜索");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "cancel-search", session.SessionId));
        await automation.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancel = client.CancelSessionTurnAsync(session.SessionId, input.TurnId);
        await automation.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        automation.Release.TrySetResult();
        await cancel;
        var stopped = await WaitForPhaseAsync(client, input.TurnId, "Cancelled");

        Assert.Single(stopped.Turns);
        Assert.DoesNotContain("迟到完成", stopped.Turns.Single().ResultSummary ?? string.Empty);
        Assert.Empty(stopped.ActiveTurns);
        await host.StopAsync();
    }

    [Fact]
    public async Task RestartInterruptsTheWorkflowAndNeverReplaysIt()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var blocked = new BlockingWorkflow();
        var firstHost = environment.BuildHost(services => Register(services, new(), blocked));
        await firstHost.StartAsync();
        var firstClient = new DesktopApiClient(environment.Options.PipeName);
        var session = await firstClient.StartNewSessionAsync("重启应用搜索");
        var input = await firstClient.SubmitSessionInputAsync(new(SearchText, "Voice", "restart-search", session.SessionId));
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        firstHost.Dispose();

        var nextWorkflow = new RecordingWorkflow();
        using var nextHost = environment.BuildHost(services => Register(services, new(), nextWorkflow));
        await nextHost.StartAsync();
        var nextClient = new DesktopApiClient(environment.Options.PipeName);
        var restored = await nextClient.SetCurrentSessionAsync(session.SessionId);
        Assert.Equal("Interrupted", restored.Turns.Single(t => t.Id == input.TurnId).Phase);
        blocked.Release.TrySetResult();
        await Task.Delay(100);
        restored = (await nextClient.GetCurrentSessionAsync())!;
        Assert.Equal("Interrupted", restored.Turns.Single(t => t.Id == input.TurnId).Phase);
        Assert.Empty(nextWorkflow.Calls);
        await nextHost.StopAsync();
    }

    private static void Register(IServiceCollection services, MutableCatalog catalog,
        IApplicationSearchWorkflowAutomation automation)
    {
        services.AddSingleton<IInstalledApplicationCatalog>(catalog);
        services.AddSingleton<IApplicationSearchWorkflowAutomation>(automation);
        services.AddSingleton<IDesktopProcessLauncher>(new RejectingLauncher());
    }

    private static InstalledApplicationCatalog CreateRegistrationCatalog(string root, params string[] names)
    {
        var registrations = names.Select((name, index) =>
        {
            var executable = Path.Combine(root, $"registered-fixture-{index}.exe");
            File.Copy(Environment.ProcessPath!, executable);
            return new ApplicationRegistration(name, executable, ApplicationRegistrationSource.AppPath);
        }).ToArray();
        return new InstalledApplicationCatalog(() => registrations);
    }

    private static async Task<SessionSnapshotDto> WaitForPhaseAsync(
        IDesktopApiClient client, Guid turnId, string phase)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await client.GetCurrentSessionAsync();
            var turn = snapshot?.Turns.SingleOrDefault(t => t.Id == turnId);
            if (turn?.Phase == phase) return snapshot!;
            if (turn?.Phase is "Completed" or "Failed" or "Cancelled" or "Interrupted")
                throw new InvalidOperationException($"Expected {phase}, got {turn.Phase}: {turn.ResultSummary}");
            await Task.Delay(20);
        }
        var last = (await client.GetCurrentSessionAsync())?.Turns.SingleOrDefault(t => t.Id == turnId);
        throw new TimeoutException($"Workflow did not reach {phase}; last phase={last?.Phase}, intent={last?.IntentKind}: {last?.ResultSummary}");
    }

    private sealed class MutableCatalog : IInstalledApplicationCatalog
    {
        public KnownDesktopApplication Current { get; set; } =
            new("fixture-music", "网易云音乐", Environment.ProcessPath!);
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [Current];
        public KnownDesktopApplication? FindById(string id) => id == Current.Id ? Current : null;
        public KnownDesktopApplication? FindByDisplayName(string name) =>
            !string.IsNullOrWhiteSpace(name)
            && Current.DisplayName.StartsWith(name.Trim(), StringComparison.OrdinalIgnoreCase)
                ? Current : null;
        public KnownDesktopApplication? FindBrowser(string name) => null;
    }

    private sealed class RecordingLauncher : IDesktopProcessLauncher
    {
        public List<string> Targets { get; } = [];
        public int? Start(string target) => throw new InvalidOperationException("Only a bound visible launch is allowed.");
        public VisibleDesktopLaunchResult OpenApplicationVisible(string target)
        {
            Targets.Add(target);
            return new(null, 9001, "应用确认测试窗口");
        }
        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? target, Uri website) =>
            throw new InvalidOperationException("No website launch is allowed.");
    }

    private sealed class RecordingConversationProvider : IConversationProvider
    {
        private int _calls;
        public string ProviderId => "application-search-chat-probe";
        public int Calls => Volatile.Read(ref _calls);
        public Task<ConversationProviderResult> SendAsync(ConversationProviderRequest request,
            Func<string, int, Task>? started = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            return Task.FromResult(new ConversationProviderResult(
                ConversationProviderOutcome.Succeeded, "search-chat-probe-thread",
                "只讨论操作，不执行。", $"search-chat-probe-{call}", 0));
        }
        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RejectingLauncher : IDesktopProcessLauncher
    {
        public int? Start(string target) => throw UnexpectedLaunch();
        public VisibleDesktopLaunchResult OpenApplicationVisible(string target) => throw UnexpectedLaunch();
        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? target, Uri website) => throw UnexpectedLaunch();
        private static Exception UnexpectedLaunch() => new InvalidOperationException(
            "This workflow fixture must use its injected automation; standalone native launch is forbidden.");
    }

    private sealed class MutablePlanner(TimeProvider clock) : IIntentPlanner
    {
        public string? Change { get; set; }
        public IntentPlan Plan(string text, IntentPlanningContext context)
        {
            var plan = new DeterministicIntentPlanner(clock).Plan(text, context);
            if (plan.ApplicationSearch is not { } search) return plan;
            return plan with { ApplicationSearch = Change switch
            {
                "Query" => search with { Query = "另一首歌" },
                "FollowUp" => search with { FollowUp = "PlayFirst" },
                "LatestArtist" => search with { LatestMusicArtist = "林俊杰" },
                "LatestRemoved" => search with { LatestMusicArtist = null },
                "ChartAdded" => search with { MusicChart = ApplicationSearchWorkflow.NetEaseHotChart },
                "ChartRemoved" => search with { MusicChart = null },
                _ => search
            } };
        }
    }

    private sealed class ProgressWorkflow(IApplicationSearchProgressSink progress) : IApplicationSearchWorkflowAutomation
    {
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReportLate { get; init; }
        public async Task<ApplicationSearchWorkflowResult> ExecuteAsync(KnownDesktopApplication application,
            string expectedBinding, ApplicationSearchWorkflow workflow, CancellationToken cancellationToken = default)
        {
            using var cancellation = cancellationToken.Register(() => Cancelled.TrySetResult());
            await progress.ReportAsync(new(ApplicationSearchStage.Searching, application.DisplayName), cancellationToken);
            Reported.TrySetResult();
            await Release.Task;
            if (ReportLate) await progress.ReportAsync(new(ApplicationSearchStage.VerifyingResults, application.DisplayName), CancellationToken.None);
            return new(true, true, true, false, false, false, "已完成搜索。");
        }
    }

    private sealed class RecordingWorkflow : IApplicationSearchWorkflowAutomation
    {
        public List<ApplicationSearchWorkflow> Calls { get; } = [];
        public Exception? Error { get; init; }
        public bool ReturnUnverified { get; init; }
        public bool NoResults { get; init; }
        public ApplicationSearchWorkflowResult? ResultOverride { get; init; }
        public Task<ApplicationSearchWorkflowResult> ExecuteAsync(KnownDesktopApplication application,
            string expectedBinding, ApplicationSearchWorkflow workflow, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(InstalledApplicationCatalog.TargetBindingMatches(application, expectedBinding));
            Calls.Add(workflow);
            if (Error is not null) throw Error;
            if (ResultOverride is not null) return Task.FromResult(ResultOverride);
            var followUp = workflow.FollowUp != ApplicationSearchFollowUp.None;
            var latest = workflow.LatestMusicArtist is { } artist
                ? new LatestMusicResolution(artist, "测试新曲", new DateOnly(2026, 9, 1), ["测试新曲"],
                    artist + " 测试新曲", true, ["https://music.apple.com/tw/album/example/123"])
                : null;
            return Task.FromResult(new ApplicationSearchWorkflowResult(
                true, true, !ReturnUnverified, NoResults, followUp, followUp,
                NoResults ? "已确认没有匹配结果。" : "已完成并验证搜索。") { LatestMusic = latest });
        }
    }

    private sealed class BlockingWorkflow : IApplicationSearchWorkflowAutomation
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ApplicationSearchWorkflowResult> ExecuteAsync(KnownDesktopApplication application,
            string expectedBinding, ApplicationSearchWorkflow workflow, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            await Release.Task;
            // Deliberately ignore cancellation to exercise Host terminal-state arbitration.
            return new(true, true, true, false, false, false, "迟到完成");
        }
    }

    private sealed class ReplacingWorkflow : IApplicationSearchWorkflowAutomation
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string? LastQuery { get; private set; }
        public BlockingWorkflow First { get; } = new();
        public async Task<ApplicationSearchWorkflowResult> ExecuteAsync(KnownDesktopApplication application,
            string expectedBinding, ApplicationSearchWorkflow workflow, CancellationToken cancellationToken = default)
        {
            LastQuery = workflow.Query;
            if (Interlocked.Increment(ref _calls) == 1)
                return await First.ExecuteAsync(application, expectedBinding, workflow, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, true, true, false, false, false, "已核对新搜索结果。");
        }
    }
}
