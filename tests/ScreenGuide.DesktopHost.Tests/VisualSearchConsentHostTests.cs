using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class VisualSearchConsentHostTests
{
    private const string SearchText = "在星河聊天里搜索资料助手";
    private static readonly ForegroundWindowSnapshot Window = new(5412, "星河聊天测试窗口", "fixture", 123,
        new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero), new(2026, 8, 17, 10, 0, 0, TimeSpan.Zero));
    private static readonly ForegroundWindowSnapshot Popup = Window with { WindowHandle = 5413, WindowTitle = "" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResultPopupNeedsANewIdAndKeepsTheOriginalWindowQueryTurnAndCommand(bool foreground)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var verifier = new ResultWindowVerifier();
        PopupWorkflow? workflow = null;
        using var host = environment.BuildHost(services => RegisterPopup(services, verifier,
            sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("主窗口和临时结果分别许可");
        var input = await client.SubmitSessionInputAsync(new(foreground ? "在当前窗口搜索资料助手" : SearchText,
            "Voice", "two-consents", session.SessionId));
        var first = (await WaitForPhaseAsync(client, input.TurnId, "WaitingForWindowConsent")).Turns.Single();
        var searchConsent = Assert.IsType<SessionWindowConsentDto>(first.WindowConsent);
        Assert.Equal("Search", searchConsent.Purpose);
        Assert.Equal(Window.WindowHandle, searchConsent.WindowHandle);
        var commandId = await ReadCommandAsync(environment, input.TurnId);
        await Assert.ThrowsAsync<DesktopApiException>(() => client.RespondSessionWindowConsentAsync(
            session.SessionId, input.TurnId, true));
        Assert.Equal(0, workflow!.Writes);
        await client.RespondSessionWindowConsentAsync(session.SessionId, input.TurnId, true,
            consentId: searchConsent.ConsentId);
        var second = await WaitForPopupAsync(client, input.TurnId);
        var popupConsent = Assert.IsType<SessionWindowConsentDto>(second.WindowConsent);
        Assert.NotEqual(searchConsent.ConsentId, popupConsent.ConsentId);
        Assert.Equal("ResultRead", popupConsent.Purpose);
        Assert.Equal(Popup.WindowHandle, popupConsent.WindowHandle);
        Assert.Equal("星河聊天测试窗口的临时窗口", popupConsent.WindowTitle);
        Assert.Equal(Window.WindowHandle, second.WindowHandle);
        Assert.Equal(Window.WindowTitle, second.WindowTitle);
        Assert.Equal(first.PlanTarget, second.PlanTarget);
        Assert.Equal(commandId, await ReadCommandAsync(environment, input.TurnId));
        Assert.Equal(1, workflow.Writes);
        Assert.Equal(0, workflow.Reads);
        Assert.Contains("不会点击、输入", second.ResultSummary);
        foreach (var stale in new Guid?[] { null, searchConsent.ConsentId, Guid.NewGuid() })
        {
            await Assert.ThrowsAsync<DesktopApiException>(() => client.RespondSessionWindowConsentAsync(
                session.SessionId, input.TurnId, true, consentId: stale));
            await Assert.ThrowsAsync<DesktopApiException>(() => client.RespondSessionWindowConsentAsync(
                session.SessionId, input.TurnId, false, consentId: stale));
        }
        Assert.Equal(0, workflow.Reads);
        var afterStale = (await client.GetCurrentSessionAsync())!.Turns.Single();
        Assert.Equal(popupConsent.ConsentId, afterStale.WindowConsent!.ConsentId);
        await client.RespondSessionWindowConsentAsync(session.SessionId, input.TurnId, true,
            consentId: popupConsent.ConsentId);
        var completed = (await WaitForPhaseAsync(client, input.TurnId, "Completed")).Turns.Single();
        Assert.Null(completed.WindowConsent);
        Assert.Equal(first.PlanTarget, completed.PlanTarget);
        Assert.Equal(Window.WindowHandle, completed.WindowHandle);
        Assert.Equal(1, workflow.Writes);
        Assert.Equal(1, workflow.Reads);
        Assert.True(verifier.Calls >= 3);
        Assert.Equal(commandId, await ReadCommandAsync(environment, input.TurnId));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("Reject")]
    [InlineData("WindowChanged")]
    public async Task AStoppedRejectedOrChangedPopupNeverGetsRead(string boundary)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var verifier = new ResultWindowVerifier();
        PopupWorkflow? workflow = null;
        using var host = environment.BuildHost(services => RegisterPopup(services, verifier,
            sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("临时结果撤销边界");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "popup-cancel", session.SessionId));
        await WaitForPhaseAsync(client, input.TurnId, "WaitingForWindowConsent");
        await RespondAsync(client, session.SessionId, input.TurnId, true);
        await WaitForPopupAsync(client, input.TurnId);
        if (boundary == "Stop") await client.CancelSessionTurnAsync(session.SessionId, input.TurnId);
        else
        {
            if (boundary == "WindowChanged") verifier.CurrentPopup = Popup with { ProcessId = 124 };
            await RespondAsync(client, session.SessionId, input.TurnId, boundary != "Reject");
        }
        await WaitForPhaseAsync(client, input.TurnId, boundary == "WindowChanged" ? "Failed" : "Cancelled");
        Assert.Equal(1, workflow!.Writes);
        Assert.Equal(0, workflow.Reads);
        await host.StopAsync();
    }

    [Fact]
    public async Task AResultReadCannotSubstituteTheOriginalQuery()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var verifier = new ResultWindowVerifier();
        PopupWorkflow? workflow = null;
        using var host = environment.BuildHost(services => RegisterPopup(services, verifier,
            sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>()) { ResultQuery = "其他资料" }));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("结果读取保持原词");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "popup-query", session.SessionId));
        var original = (await WaitForPhaseAsync(client, input.TurnId, "WaitingForWindowConsent")).Turns.Single();
        await RespondAsync(client, session.SessionId, input.TurnId, true);
        var failed = (await WaitForPhaseAsync(client, input.TurnId, "Failed")).Turns.Single();
        Assert.Equal(original.PlanTarget, failed.PlanTarget);
        Assert.Equal(0, workflow!.Reads);
        Assert.Equal(0, verifier.Calls);
        await host.StopAsync();
    }

    [Fact]
    public async Task RestartDropsThePopupIdAndDoesNotRepeatTheOriginalInput()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        PopupWorkflow? original = null;
        var firstHost = environment.BuildHost(services => RegisterPopup(services, new(),
            sp => original = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await firstHost.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("结果许可等待期间重启");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "popup-restart", session.SessionId));
        await WaitForPhaseAsync(client, input.TurnId, "WaitingForWindowConsent");
        await RespondAsync(client, session.SessionId, input.TurnId, true);
        var waiting = await WaitForPopupAsync(client, input.TurnId);
        var oldCoordinator = firstHost.Services.GetRequiredService<SessionCoordinator>();
        firstHost.Dispose();
        PopupWorkflow? next = null;
        using var nextHost = environment.BuildHost(services => RegisterPopup(services, new(),
            sp => next = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await nextHost.StartAsync();
        var nextClient = new DesktopApiClient(environment.Options.PipeName);
        var restored = (await nextClient.SetCurrentSessionAsync(session.SessionId)).Turns.Single();
        Assert.Equal("Interrupted", restored.Phase);
        Assert.Null(restored.WindowConsent);
        Assert.Equal(Window.WindowHandle, restored.WindowHandle);
        await Assert.ThrowsAsync<DesktopApiException>(() => nextClient.RespondSessionWindowConsentAsync(
            session.SessionId, input.TurnId, true, consentId: waiting.WindowConsent!.ConsentId));
        Assert.Equal(0, next!.Writes);
        Assert.Equal(1, original!.Writes);
        Assert.Equal(0, original.Reads);
        await oldCoordinator.StopAsync();
        await nextHost.StopAsync();
    }

    [Theory]
    [InlineData("Voice")]
    [InlineData("Text")]
    public async Task ConsentResumesTheOriginalTurnAndCommandExactlyOnce(string modality)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var windows = new MutableWindows();
        ConsentWorkflow? workflow = null;
        using var host = environment.BuildHost(services => Register(services, windows,
            sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("同一次搜索许可");
        var input = await client.SubmitSessionInputAsync(new(SearchText, modality, "visual", session.SessionId));
        if (modality == "Text")
        {
            await WaitForPhaseAsync(client, input.TurnId, "WaitingForConfirmation");
            // Confirm must return the visible pause; waiting for execution here would deadlock the UI.
            await client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        var paused = await WaitForPhaseAsync(client, input.TurnId, "WaitingForWindowConsent");
        var original = Assert.Single(paused.Turns);
        Assert.Contains("识别并点击搜索框", original.ResultSummary);
        Assert.Contains("不会打开结果、发送消息", original.ResultSummary);
        Assert.Equal(0, workflow!.Writes);
        var command = await ReadCommandAsync(environment, input.TurnId);
        Assert.NotNull(command);
        await RespondAsync(client, session.SessionId, input.TurnId, true);
        var completed = await WaitForPhaseAsync(client, input.TurnId, "Completed");
        var result = Assert.Single(completed.Turns);
        Assert.Equal(original.PlanTarget, result.PlanTarget);
        Assert.Equal(original.Id, result.Id);
        Assert.Equal(command, await ReadCommandAsync(environment, input.TurnId));
        Assert.Equal(1, workflow.Calls);
        Assert.Equal(1, workflow.Writes);
        await Assert.ThrowsAsync<DesktopApiException>(() =>
            RespondAsync(client, session.SessionId, input.TurnId, true));
        Assert.Equal(1, workflow.Writes);
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectOrStopWhileWaitingNeverWrites(bool stop)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        ConsentWorkflow? workflow = null;
        using var host = environment.BuildHost(services => Register(services, new(),
            sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("等待时停止");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "cancel", session.SessionId));
        await WaitForPhaseAsync(client, input.TurnId, "WaitingForWindowConsent");
        if (stop) await client.CancelSessionTurnAsync(session.SessionId, input.TurnId);
        else await RespondAsync(client, session.SessionId, input.TurnId, false);
        await WaitForPhaseAsync(client, input.TurnId, "Cancelled");
        await workflow!.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, workflow.Writes);
        await Assert.ThrowsAsync<DesktopApiException>(() =>
            RespondAsync(client, session.SessionId, input.TurnId, true));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Process")]
    [InlineData("Title")]
    [InlineData("Missing")]
    public async Task ChangedWindowCannotUseEarlierConsent(string change)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var windows = new MutableWindows();
        ConsentWorkflow? workflow = null;
        using var host = environment.BuildHost(services => Register(services, windows,
            sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("窗口身份变化");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "changed", session.SessionId));
        await WaitForPhaseAsync(client, input.TurnId, "WaitingForWindowConsent");
        windows.Current = change switch
        {
            "Process" => Window with { ProcessId = 124 },
            "Title" => Window with { WindowTitle = "其他窗口" },
            _ => null
        };
        await RespondAsync(client, session.SessionId, input.TurnId, true);
        await WaitForPhaseAsync(client, input.TurnId, "Failed");
        Assert.Equal(0, workflow!.Writes);
        await host.StopAsync();
    }

    [Fact]
    public async Task AnExecutorCannotSubstituteItsOwnSearchQuery()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        ConsentWorkflow? workflow = null;
        using var host = environment.BuildHost(services => Register(services, new(),
            sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>()) { SubstituteQuery = "其他资料" }));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("搜索词绑定");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "wrong-query", session.SessionId));
        await WaitForPhaseAsync(client, input.TurnId, "Failed");
        Assert.Equal(0, workflow!.Writes);
        var persisted = await host.Services.GetRequiredService<ISessionStore>().GetTurnAsync(input.TurnId);
        Assert.NotEqual("visual_search_consent_required", persisted!.FailureCode);
        await host.StopAsync();
    }

    [Fact]
    public async Task ReplacementInputCancelsTheWaitAndRequiresASeparatePermission()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        ConsentWorkflow? workflow = null;
        using var host = environment.BuildHost(services => Register(services, new(),
            sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("新输入撤销旧许可");
        var first = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "first", session.SessionId));
        await WaitForPhaseAsync(client, first.TurnId, "WaitingForWindowConsent");
        var next = await client.SubmitSessionInputAsync(new("在星河聊天里搜索今日资料", "Voice", "next", session.SessionId));
        await WaitForPhaseAsync(client, first.TurnId, "Cancelled");
        await WaitForPhaseAsync(client, next.TurnId, "WaitingForWindowConsent");
        Assert.Equal(0, workflow!.Writes);
        await RespondAsync(client, session.SessionId, next.TurnId, true);
        await WaitForPhaseAsync(client, next.TurnId, "Completed");
        Assert.Equal(2, workflow.Calls);
        Assert.Equal(1, workflow.Writes);
        await host.StopAsync();
    }

    [Fact]
    public async Task StopAfterPermissionRejectsAnExecutorsLateSuccess()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        ConsentWorkflow? workflow = null;
        using var host = environment.BuildHost(services => Register(services, new(),
            sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>()) { BlockAfterPermission = true }));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("迟到结果不能覆盖停止");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "late", session.SessionId));
        await WaitForPhaseAsync(client, input.TurnId, "WaitingForWindowConsent");
        await RespondAsync(client, session.SessionId, input.TurnId, true);
        await workflow!.Permitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancellation = client.CancelSessionTurnAsync(session.SessionId, input.TurnId);
        await workflow.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workflow.Release.TrySetResult();
        await cancellation;
        var stopped = await WaitForPhaseAsync(client, input.TurnId, "Cancelled");
        Assert.DoesNotContain("迟到成功", stopped.Turns.Single().ResultSummary ?? "");
        await host.StopAsync();
    }

    [Fact]
    public async Task ConcurrentPermissionsCanResumeOnlyOnce()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        ConsentWorkflow? workflow = null;
        using var host = environment.BuildHost(services => Register(services, new(),
            sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("并发确认");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "parallel", session.SessionId));
        await WaitForPhaseAsync(client, input.TurnId, "WaitingForWindowConsent");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try { await RespondAsync(client, session.SessionId, input.TurnId, true); return true; }
            catch (DesktopApiException) { return false; }
        }));
        await WaitForPhaseAsync(client, input.TurnId, "Completed");
        Assert.Single(results, result => result);
        Assert.Equal(1, workflow!.Writes);
        await host.StopAsync();
    }

    [Fact]
    public async Task ExpiredPermissionCannotResumeTheSearch()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        ConsentWorkflow? workflow = null;
        using var host = environment.BuildHost(services => Register(services, new(),
            sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("过期许可");
        var input = await client.SubmitSessionInputAsync(new(SearchText, "Voice", "expired", session.SessionId));
        await WaitForPhaseAsync(client, input.TurnId, "WaitingForWindowConsent");
        environment.TimeProvider.UtcNow = environment.TimeProvider.UtcNow.AddMinutes(3);
        await RespondAsync(client, session.SessionId, input.TurnId, true);
        await WaitForPhaseAsync(client, input.TurnId, "Failed");
        Assert.Equal(0, workflow!.Writes);
        await host.StopAsync();
    }

    [Fact]
    public async Task HostRestartInterruptsWaitingConsentAndNeverReplaysTheCommand()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        ConsentWorkflow? original = null;
        var firstHost = environment.BuildHost(services => Register(services, new(),
            sp => original = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await firstHost.StartAsync();
        var firstClient = new DesktopApiClient(environment.Options.PipeName);
        var session = await firstClient.StartNewSessionAsync("许可等待期间重启");
        var input = await firstClient.SubmitSessionInputAsync(new(SearchText, "Voice", "restart", session.SessionId));
        await WaitForPhaseAsync(firstClient, input.TurnId, "WaitingForWindowConsent");
        var oldCoordinator = firstHost.Services.GetRequiredService<SessionCoordinator>();
        firstHost.Dispose(); // Simulate a Host restart without the graceful shutdown protocol.
        ConsentWorkflow? next = null;
        using var nextHost = environment.BuildHost(services => Register(services, new(),
            sp => next = new(sp.GetRequiredService<IVisualSearchConsent>())));
        await nextHost.StartAsync();
        var nextClient = new DesktopApiClient(environment.Options.PipeName);
        var restored = await nextClient.SetCurrentSessionAsync(session.SessionId);
        Assert.Equal("Interrupted", restored.Turns.Single().Phase);
        await Assert.ThrowsAsync<DesktopApiException>(() =>
            RespondAsync(nextClient, session.SessionId, input.TurnId, true));
        Assert.Equal(0, next!.Calls);
        Assert.Equal(0, original!.Writes);
        await oldCoordinator.StopAsync(); // Retire the fake in-process execution left by the crash simulation.
        Assert.Equal("Interrupted", (await nextClient.GetCurrentSessionAsync())!.Turns.Single().Phase);
        await nextHost.StopAsync();
    }

    [Fact]
    public async Task ForegroundSearchUsesTheSameVisibleConsentChannel()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        ConsentWorkflow? workflow = null;
        using var host = environment.BuildHost(services =>
        {
            Register(services, new(), sp => workflow = new(sp.GetRequiredService<IVisualSearchConsent>()));
            services.AddSingleton<IReliableDesktopAutomation, NoNativeSearch>();
            services.AddSingleton<IVisualSearchAutomation>(sp => new ForegroundFallback(sp.GetRequiredService<IVisualSearchConsent>()));
        });
        await host.StartAsync();
        var client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("当前窗口搜索许可");
        var input = await client.SubmitSessionInputAsync(new("在当前窗口搜索资料助手", "Voice", "foreground", session.SessionId));
        var waiting = await WaitForPhaseAsync(client, input.TurnId, "WaitingForWindowConsent");
        Assert.Equal("SearchForeground", waiting.Turns.Single().IntentKind);
        await RespondAsync(client, session.SessionId, input.TurnId, true);
        await WaitForPhaseAsync(client, input.TurnId, "Completed");
        Assert.Equal(0, workflow!.Calls);
        await host.StopAsync();
    }

    private static void Register(IServiceCollection services, MutableWindows windows,
        Func<IServiceProvider, ConsentWorkflow> workflow)
    {
        services.AddSingleton<IInstalledApplicationCatalog, Catalog>();
        services.AddSingleton<IForegroundWindowContextProvider>(windows);
        services.AddSingleton<IApplicationSearchWorkflowAutomation>(workflow);
    }

    private static void RegisterPopup(IServiceCollection services, ResultWindowVerifier verifier,
        Func<IServiceProvider, PopupWorkflow> workflow)
    {
        services.AddSingleton<IInstalledApplicationCatalog, Catalog>();
        services.AddSingleton<IForegroundWindowContextProvider>(new MutableWindows());
        services.AddSingleton<IVisualSearchResultWindowVerifier>(verifier);
        services.AddSingleton<IReliableDesktopAutomation, NoNativeSearch>();
        services.AddSingleton(workflow);
        services.AddSingleton<IApplicationSearchWorkflowAutomation>(sp => sp.GetRequiredService<PopupWorkflow>());
        services.AddSingleton<IVisualSearchAutomation>(sp => sp.GetRequiredService<PopupWorkflow>());
    }

    private static async Task<UnifiedSessionTurnDto> WaitForPopupAsync(DesktopApiClient client, Guid turnId)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until)
        {
            var turn = (await client.GetCurrentSessionAsync())?.Turns.SingleOrDefault(item => item.Id == turnId);
            if (turn?.WindowConsent?.Purpose == "ResultRead") return turn;
            if (turn?.Phase is "Failed" or "Completed" or "Cancelled" or "Interrupted")
                throw new InvalidOperationException($"Expected popup consent, got {turn.Phase}: {turn.ResultSummary}");
            await Task.Delay(20);
        }
        throw new TimeoutException("No result-window consent was published.");
    }

    private static async Task<string?> ReadCommandAsync(DesktopHostTestEnvironment environment, Guid turnId)
    {
        await using var connection = new SqliteConnection($"Data Source={environment.Options.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM commands WHERE idempotency_key = $key";
        command.Parameters.AddWithValue("$key", $"session-action-{turnId:N}");
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<SessionSnapshotDto> RespondAsync(DesktopApiClient client, Guid sessionId,
        Guid turnId, bool granted)
    {
        var snapshot = await client.GetCurrentSessionAsync();
        var consent = snapshot?.Turns.SingleOrDefault(turn => turn.Id == turnId)?.WindowConsent;
        return await client.RespondSessionWindowConsentAsync(sessionId, turnId, granted, consentId: consent?.ConsentId);
    }

    private static async Task<SessionSnapshotDto> WaitForPhaseAsync(DesktopApiClient client, Guid turnId, string phase)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until)
        {
            var snapshot = await client.GetCurrentSessionAsync();
            var turn = snapshot?.Turns.SingleOrDefault(item => item.Id == turnId);
            if (turn?.Phase == phase) return snapshot!;
            if (turn?.Phase is "Failed" or "Completed" or "Cancelled" or "Interrupted")
                throw new InvalidOperationException($"Expected {phase}, got {turn.Phase}: {turn.ResultSummary}");
            await Task.Delay(20);
        }
        throw new TimeoutException($"Turn did not reach {phase}.");
    }

    private sealed class MutableWindows : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot? Current { get; set; } = Window;
        public ForegroundWindowSnapshot? GetLastExternalWindow() => Current;
    }

    private sealed class Catalog : IInstalledApplicationCatalog
    {
        private readonly KnownDesktopApplication _app = new("fixture-search", "星河聊天", Environment.ProcessPath!);
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [_app];
        public KnownDesktopApplication? FindById(string id) => id == _app.Id ? _app : null;
        public KnownDesktopApplication? FindByDisplayName(string name) => name == _app.DisplayName ? _app : null;
        public KnownDesktopApplication? FindBrowser(string name) => null;
    }

    private sealed class ConsentWorkflow(IVisualSearchConsent consent) : IApplicationSearchWorkflowAutomation
    {
        public int Calls;
        public int Writes;
        public string? SubstituteQuery { get; init; }
        public bool BlockAfterPermission { get; init; }
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Permitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ApplicationSearchWorkflowResult> ExecuteAsync(KnownDesktopApplication application,
            string expectedApplicationTargetBinding, ApplicationSearchWorkflow workflow, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            try { await consent.RequestAsync(Window, SubstituteQuery ?? workflow.Query, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
            Permitted.TrySetResult();
            if (BlockAfterPermission) await Release.Task; // Deliberately ignore cancellation to test terminal arbitration.
            else cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Writes);
            return new(true, true, true, false, false, false, "迟到成功或正常完成") { SubmissionMode = "LiveFilter" };
        }
    }

    private sealed class ForegroundFallback(IVisualSearchConsent consent) : IVisualSearchAutomation
    {
        public async Task<ApplicationSearchWorkflowResult> SearchAsync(ForegroundWindowSnapshot window, string query,
            CancellationToken cancellationToken = default, Action? validateTarget = null)
        {
            await consent.RequestAsync(window, query, cancellationToken);
            return new(true, true, true, false, false, false, "已验证当前窗口搜索") { SubmissionMode = "LiveFilter" };
        }
    }

    private sealed class ResultWindowVerifier : IVisualSearchResultWindowVerifier
    {
        public ForegroundWindowSnapshot? CurrentPopup { get; set; } = Popup;
        public int Calls;
        public void Verify(ForegroundWindowSnapshot originWindow, ForegroundWindowSnapshot resultWindow)
        {
            Interlocked.Increment(ref Calls);
            WindowIdentityContract.RequireMatch(Window, originWindow);
            WindowIdentityContract.RequireMatch(resultWindow, CurrentPopup);
        }
    }

    private sealed class PopupWorkflow(IVisualSearchConsent consent)
        : IApplicationSearchWorkflowAutomation, IVisualSearchAutomation
    {
        public int Writes;
        public int Reads;
        public string? ResultQuery { get; init; }
        public Task<ApplicationSearchWorkflowResult> ExecuteAsync(KnownDesktopApplication application,
            string expectedApplicationTargetBinding, ApplicationSearchWorkflow workflow, CancellationToken cancellationToken = default) =>
            SearchAsync(Window, workflow.Query, cancellationToken);
        public async Task<ApplicationSearchWorkflowResult> SearchAsync(ForegroundWindowSnapshot window, string query,
            CancellationToken cancellationToken = default, Action? validateTarget = null)
        {
            await consent.RequestAsync(window, query, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Writes);
            await consent.RequestResultReadAsync(window, Popup, ResultQuery ?? query, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Reads);
            return new(true, true, true, false, false, false, "已只读核对临时结果窗口") { SubmissionMode = "LiveFilter" };
        }
    }

    private sealed class NoNativeSearch : IReliableDesktopAutomation
    {
        public DesktopAutomationResult Search(ForegroundWindowSnapshot window, string query) =>
            throw new DesktopSearchException(DesktopSearchErrorCodes.CandidateNotFound, "无结构化搜索框");
        public DesktopAutomationResult Describe(ForegroundWindowSnapshot window) => throw new NotSupportedException();
    }
}
