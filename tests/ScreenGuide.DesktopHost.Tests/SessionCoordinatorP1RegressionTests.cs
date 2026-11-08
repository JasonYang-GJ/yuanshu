using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Persistence.Sqlite;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class SessionCoordinatorP1RegressionTests
{
    [Fact]
    public Task SubmitRacingStartNewNeverRunsTwoForegroundProviders() =>
        VerifySubmitAndSessionSwitchNeverOverlapAsync(useExistingSession: false);

    [Fact]
    public Task SubmitRacingSetCurrentNeverRunsTwoForegroundProviders() =>
        VerifySubmitAndSessionSwitchNeverOverlapAsync(useExistingSession: true);

    [Fact]
    public async Task StructuredApplicationTargetMustMatchExactlyBeforeConfirmation()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        // This contract tests exact structured IDs, not the machine's installed apps.
        // Use the test executable as a valid, non-elevated target; the launcher stays unused.
        var application = new KnownDesktopApplication("notepad", "记事本", environment.Options.CodexExecutablePath!);
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
            services.AddSingleton<IInstalledApplicationCatalog>(new SingleApplicationCatalog(application));
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("结构化应用目标");

        var matching = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "启动“记事本”",
            "Text",
            "structured-app-match",
            session.SessionId,
            "OpenApplication",
            "notepad"));
        var waiting = await WaitForTurnPhaseAsync(
            client,
            matching.TurnId,
            "WaitingForConfirmation");
        var matchingTurn = waiting.Turns.Single(turn => turn.Id == matching.TurnId);
        await client.CancelSessionTurnAsync(session.SessionId, matching.TurnId);

        var mismatched = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "启动“记事本”",
            "Text",
            "structured-app-mismatch",
            session.SessionId,
            "OpenApplication",
            "calculator"));
        var failed = await WaitForTurnPhaseAsync(client, mismatched.TurnId, "Failed");
        var mismatchedTurn = failed.Turns.Single(turn => turn.Id == mismatched.TurnId);
        await host.StopAsync();

        Assert.Equal("notepad", matchingTurn.ExpectedTarget);
        Assert.Equal("notepad", matchingTurn.PlanTarget);
        Assert.Equal("calculator", mismatchedTurn.ExpectedTarget);
        Assert.Equal("notepad", mismatchedTurn.PlanTarget);
        Assert.Contains("目标不一致", mismatchedTurn.ResultSummary, StringComparison.Ordinal);
        Assert.Empty(launcher.Targets);
    }

    [Fact]
    public async Task StructuredWebsiteTargetRejectsASimilarLookingHost()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var launcher = new RecordingLauncher();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IDesktopProcessLauncher>(launcher));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("结构化网站目标");

        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "打开https://evil-example.com/",
            "Text",
            "structured-website-mismatch",
            session.SessionId,
            "OpenWebsite",
            "https://example.com/"));
        var failed = await WaitForTurnPhaseAsync(client, submitted.TurnId, "Failed");
        var turn = failed.Turns.Single(item => item.Id == submitted.TurnId);
        await host.StopAsync();

        Assert.Equal("https://example.com/", turn.ExpectedTarget);
        Assert.Equal("https://evil-example.com/", turn.PlanTarget);
        Assert.Empty(launcher.Targets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileConfirmationRacingReplacementNeverExecutesTheCancelledAction(
        bool startNewTopic)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var planner = new BlockingIntentPlanner(environment.TimeProvider);
        var launcher = new RecordingLauncher();
        BlockingSessionStore? sessionStore = null;
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IConversationProvider>(new ImmediateConversationProvider());
            services.AddSingleton<IIntentPlanner>(planner);
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
            services.AddSingleton<ISessionStore>(_ =>
                sessionStore = new BlockingSessionStore(
                    new SqliteSessionStore(environment.Options.DatabasePath)));
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("确认竞争测试");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "帮我打开这个文件",
            "Text",
            "p1-confirm-file",
            session.SessionId));
        await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForFile");
        var selectedFile = Path.Combine(environment.RootDirectory, "p1-confirm-file.txt");
        await File.WriteAllTextAsync(selectedFile, "safe regression fixture");
        var selected = await client.ProvideSessionFileAsync(
            session.SessionId,
            submitted.TurnId,
            selectedFile);
        Assert.Equal(
            "WaitingForConfirmation",
            selected.Turns.Single(turn => turn.Id == submitted.TurnId).Phase);

        sessionStore = Assert.IsType<BlockingSessionStore>(
            host.Services.GetRequiredService<ISessionStore>());
        planner.Arm(context => context.SelectedFilePath is not null);
        var confirmation = client.ConfirmSessionTurnAsync(
            session.SessionId,
            submitted.TurnId,
            confirmed: true);

        try
        {
            await planner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            sessionStore.ArmActiveTurnScan(session.SessionId);
            Task replacement = startNewTopic
                ? client.StartNewSessionAsync("确认后立即切换的新话题")
                : client.SubmitSessionInputAsync(new SessionInputRequestDto(
                    "停，先回答新的问题",
                    "Text",
                    "p1-confirm-replacement",
                    session.SessionId));
            await sessionStore.ActiveTurnScanEntered.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(50);
            planner.Release();

            await confirmation.WaitAsync(TimeSpan.FromSeconds(5));
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            var original = await client.SetCurrentSessionAsync(session.SessionId);
            var oldTurn = original.Turns.Single(turn => turn.Id == submitted.TurnId);

            Assert.Equal("Cancelled", oldTurn.Phase);
            Assert.Empty(launcher.Targets);
        }
        finally
        {
            planner.Release();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WindowConsentRacingSecondInputNeverObservesTheCancelledWindow()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var planner = new BlockingIntentPlanner(environment.TimeProvider);
        var capture = new CountingCaptureService();
        var vision = new CountingVisionProvider();
        BlockingSessionStore? sessionStore = null;
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IConversationProvider>(new ImmediateConversationProvider());
            services.AddSingleton<IIntentPlanner>(planner);
            services.AddSingleton<IForegroundWindowContextProvider>(new FixedForegroundProvider());
            services.AddSingleton<IWindowCaptureService>(capture);
            services.AddSingleton<IWindowVisionProvider>(vision);
            services.AddSingleton<ISessionStore>(_ =>
                sessionStore = new BlockingSessionStore(
                    new SqliteSessionStore(environment.Options.DatabasePath)));
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var session = await client.StartNewSessionAsync("窗口授权竞争测试");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "看看这个窗口是什么",
            "Text",
            "p1-window-consent",
            session.SessionId));
        await WaitForTurnPhaseAsync(client, submitted.TurnId, "WaitingForWindowConsent");

        sessionStore = Assert.IsType<BlockingSessionStore>(
            host.Services.GetRequiredService<ISessionStore>());
        planner.Arm(context => context.ForegroundObservationConsent);
        var consent = client.RespondSessionWindowConsentAsync(
            session.SessionId,
            submitted.TurnId,
            granted: true);

        try
        {
            await planner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            sessionStore.ArmActiveTurnScan(session.SessionId);
            var replacement = client.SubmitSessionInputAsync(new SessionInputRequestDto(
                "停，不看窗口了，先回答新的问题",
                "Text",
                "p1-window-replacement",
                session.SessionId));
            await sessionStore.ActiveTurnScanEntered.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(50);
            planner.Release();

            await consent.WaitAsync(TimeSpan.FromSeconds(5));
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            var final = await client.GetCurrentSessionAsync();
            var oldTurn = final!.Turns.Single(turn => turn.Id == submitted.TurnId);

            Assert.Equal("Cancelled", oldTurn.Phase);
            Assert.Equal(0, capture.CallCount);
            Assert.Equal(0, vision.CallCount);
        }
        finally
        {
            planner.Release();
            await host.StopAsync();
        }
    }

    private static async Task VerifySubmitAndSessionSwitchNeverOverlapAsync(bool useExistingSession)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new ConcurrentConversationProvider();
        BlockingSessionStore? sessionStore = null;
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IConversationProvider>(provider);
            services.AddSingleton<ISessionStore>(_ =>
                sessionStore = new BlockingSessionStore(
                    new SqliteSessionStore(environment.Options.DatabasePath)));
        });
        await host.StartAsync();
        var coordinator = host.Services.GetRequiredService<SessionCoordinator>();

        Guid? switchTarget = null;
        if (useExistingSession)
        {
            switchTarget = (await coordinator.StartNewAsync("预先存在的目标会话")
                .WaitAsync(TimeSpan.FromSeconds(5))).Session.Id;
        }

        var oldCurrent = await coordinator.StartNewAsync("原当前会话")
            .WaitAsync(TimeSpan.FromSeconds(5));
        sessionStore = Assert.IsType<BlockingSessionStore>(
            host.Services.GetRequiredService<ISessionStore>());
        sessionStore.ArmStartTurn("p1-delayed-old-submit");
        var delayedOldSubmit = coordinator.SubmitAsync(
            oldCurrent.Session.Id,
            "原会话中被延迟的请求",
            "Text",
            "p1-delayed-old-submit",
            CancellationToken.None);

        try
        {
            await sessionStore.StartTurnEntered.WaitAsync(TimeSpan.FromSeconds(3));
            Task<LocalSessionSnapshot> switchOperation = useExistingSession
                ? coordinator.SetCurrentAsync(switchTarget!.Value)
                : coordinator.StartNewAsync("并发创建的新会话");
            await Task.Delay(100);
            Assert.False(
                switchOperation.IsCompleted,
                $"提交尚未登记时，当前会话已经提前切换。Store 调用：{sessionStore.DescribeCalls()}");

            sessionStore.ReleaseStartTurn();
            await delayedOldSubmit.WaitAsync(TimeSpan.FromSeconds(5));
            var switchFinished = await Task.WhenAny(switchOperation, Task.Delay(5_000));
            Assert.True(
                ReferenceEquals(switchFinished, switchOperation),
                $"提交登记完成后，当前会话仍未切换。Store 调用：{sessionStore.DescribeCalls()}");
            var newCurrentId = (await switchOperation).Session.Id;
            await coordinator.SubmitAsync(
                    newCurrentId,
                    "新当前会话中的请求",
                    "Text",
                    "p1-current-submit",
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForConditionAsync(
                () => provider.ActiveCount == 1,
                "新当前会话的 Provider 没有启动。 ");

            Assert.True(
                provider.MaximumConcurrent <= 1,
                $"切换当前会话后仍同时运行了 {provider.MaximumConcurrent} 个前台 Provider。 ");
        }
        finally
        {
            sessionStore.ReleaseStartTurn();
            provider.ReleaseAll();
            try
            {
                await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Keep a product race assertion from being hidden by test cleanup.
            }
        }
    }

    private static async Task<SessionSnapshotDto> WaitForTurnPhaseAsync(
        IDesktopApiClient client,
        Guid turnId,
        string expectedPhase)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        string? lastPhase = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await client.GetCurrentSessionAsync();
            lastPhase = snapshot?.Turns.SingleOrDefault(turn => turn.Id == turnId)?.Phase;
            if (lastPhase == expectedPhase)
            {
                return snapshot!;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"统一会话请求没有进入 {expectedPhase}；最后阶段：{lastPhase ?? "未找到请求"}。 ");
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, string timeoutMessage)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(timeoutMessage);
    }

    private sealed class BlockingIntentPlanner(TimeProvider timeProvider) : IIntentPlanner
    {
        private readonly DeterministicIntentPlanner _inner = new(timeProvider);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private Func<IntentPlanningContext, bool>? _predicate;
        private int _blocked;

        public TaskCompletionSource Entered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(Func<IntentPlanningContext, bool> predicate)
        {
            _predicate = predicate;
            _release.Reset();
            Interlocked.Exchange(ref _blocked, 0);
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public IntentPlan Plan(string text, IntentPlanningContext context)
        {
            if (_predicate?.Invoke(context) == true
                && Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
            {
                Entered.TrySetResult();
                if (!_release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("测试没有释放被阻塞的意图规划。 ");
                }
            }

            return _inner.Plan(text, context);
        }

        public void Release() => _release.Set();
    }

    private sealed class BlockingSessionStore(ISessionStore inner) : ISessionStore
    {
        private readonly object _gate = new();
        private readonly ConcurrentQueue<string> _calls = new();
        private string? _blockedStartTurnKey;
        private Guid? _observedActiveSessionId;
        private TaskCompletionSource _startTurnEntered = NewSignal();
        private TaskCompletionSource _releaseStartTurn = NewSignal();
        private TaskCompletionSource _activeTurnScanEntered = NewSignal();

        public Task StartTurnEntered => _startTurnEntered.Task;

        public Task ActiveTurnScanEntered => _activeTurnScanEntered.Task;

        public string DescribeCalls() => string.Join(" -> ", _calls);

        public void ArmStartTurn(string idempotencyKey)
        {
            lock (_gate)
            {
                _blockedStartTurnKey = idempotencyKey;
                _startTurnEntered = NewSignal();
                _releaseStartTurn = NewSignal();
            }
        }

        public void ReleaseStartTurn()
        {
            lock (_gate)
            {
                _releaseStartTurn.TrySetResult();
            }
        }

        public void ArmActiveTurnScan(Guid sessionId)
        {
            lock (_gate)
            {
                _observedActiveSessionId = sessionId;
                _activeTurnScanEntered = NewSignal();
            }
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task CreateSessionAsync(
            SessionRecord session,
            CancellationToken cancellationToken = default)
        {
            _calls.Enqueue($"create:{session.Id:N}");
            return inner.CreateSessionAsync(session, cancellationToken);
        }

        public Task<SessionRecord?> GetSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            inner.GetSessionAsync(sessionId, cancellationToken);

        public Task<SessionRecord?> GetSessionByConversationAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default) =>
            inner.GetSessionByConversationAsync(conversationId, cancellationToken);

        public Task<SessionRecord?> GetCurrentSessionAsync(CancellationToken cancellationToken = default) =>
            inner.GetCurrentSessionAsync(cancellationToken);

        public Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetSessionsAsync(cancellationToken);

        public Task<SessionRecord> SetCurrentSessionAsync(
            Guid sessionId,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default) =>
            inner.SetCurrentSessionAsync(sessionId, changedAtUtc, cancellationToken);

        public Task<SessionRecord> SetSelectedProjectAsync(
            Guid sessionId,
            Guid? projectId,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default) =>
            inner.SetSelectedProjectAsync(sessionId, projectId, changedAtUtc, cancellationToken);

        public async Task<SessionTurnRegistration> StartTurnAsync(
            Guid sessionId,
            string inputText,
            string inputModality,
            string idempotencyKey,
            SessionTurnFrozenRoute frozenRoute,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Task? release = null;
            lock (_gate)
            {
                if (string.Equals(idempotencyKey, _blockedStartTurnKey, StringComparison.Ordinal))
                {
                    _blockedStartTurnKey = null;
                    _startTurnEntered.TrySetResult();
                    release = _releaseStartTurn.Task;
                }
            }

            if (release is not null)
            {
                _calls.Enqueue($"start-blocked:{sessionId:N}");
                await release.WaitAsync(cancellationToken);
                _calls.Enqueue($"start-released:{sessionId:N}");
            }

            return await inner.StartTurnAsync(
                sessionId,
                inputText,
                inputModality,
                idempotencyKey,
                frozenRoute,
                startedAtUtc,
                cancellationToken);
        }

        public Task<SessionTurnRecord> UpdateTurnAsync(
            SessionTurnRecord turn,
            long expectedVersion,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default) =>
            inner.UpdateTurnAsync(turn, expectedVersion, changedAtUtc, cancellationToken);

        public Task<SessionTurnRecord?> GetTurnAsync(
            Guid turnId,
            CancellationToken cancellationToken = default) =>
            inner.GetTurnAsync(turnId, cancellationToken);

        public Task<IReadOnlyList<SessionTurnRecord>> GetTurnsAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            inner.GetTurnsAsync(sessionId, cancellationToken);

        public async Task<IReadOnlyList<SessionTurnRecord>> GetActiveTurnsAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            _calls.Enqueue($"active:{sessionId:N}");
            TaskCompletionSource? signal = null;
            lock (_gate)
            {
                if (_observedActiveSessionId == sessionId)
                {
                    _observedActiveSessionId = null;
                    signal = _activeTurnScanEntered;
                }
            }

            var turns = await inner.GetActiveTurnsAsync(sessionId, cancellationToken);
            signal?.TrySetResult();
            return turns;
        }

        public Task<SessionRecoveryResult> RecoverInterruptedAsync(
            DateTimeOffset recoveredAtUtc,
            CancellationToken cancellationToken = default) =>
            inner.RecoverInterruptedAsync(recoveredAtUtc, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ConcurrentConversationProvider : IConversationProvider
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _maximumConcurrent;

        public string ProviderId => "p1-concurrent-provider";

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TwoActive { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaximumConcurrent => Volatile.Read(ref _maximumConcurrent);

        public int ActiveCount => Volatile.Read(ref _active);

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            FirstStarted.TrySetResult();
            if (active >= 2)
            {
                TwoActive.TrySetResult();
            }

            try
            {
                if (started is not null)
                {
                    await started($"thread-{request.ConversationId:N}", 9700 + active);
                }

                await _release.Task.WaitAsync(cancellationToken);
                return new ConversationProviderResult(
                    ConversationProviderOutcome.Succeeded,
                    $"thread-{request.ConversationId:N}",
                    "并发测试回答",
                    $"message-{request.TurnId:N}",
                    9700 + active);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void ReleaseAll() => _release.TrySetResult();

        public ValueTask DisposeAsync()
        {
            ReleaseAll();
            return ValueTask.CompletedTask;
        }

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maximumConcurrent);
                if (candidate <= observed
                    || Interlocked.CompareExchange(ref _maximumConcurrent, candidate, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    private sealed class ImmediateConversationProvider : IConversationProvider
    {
        public string ProviderId => "p1-immediate-provider";

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            if (started is not null)
            {
                await started("thread-p1-immediate", 9801);
            }

            return new ConversationProviderResult(
                ConversationProviderOutcome.Succeeded,
                "thread-p1-immediate",
                "新的请求已完成",
                $"message-{request.TurnId:N}",
                9801);
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleApplicationCatalog(KnownDesktopApplication application) : IInstalledApplicationCatalog
    {
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [application];
        public KnownDesktopApplication? FindById(string id) =>
            string.Equals(id, application.Id, StringComparison.Ordinal) ? application : null;
        public KnownDesktopApplication? FindByDisplayName(string displayName) =>
            string.Equals(displayName, application.DisplayName, StringComparison.Ordinal) ? application : null;
        public KnownDesktopApplication? FindBrowser(string browserName) => null;
    }

    private sealed class RecordingLauncher : IDesktopProcessLauncher
    {
        public ConcurrentQueue<string> Targets { get; } = new();

        public int? Start(string target)
        {
            Targets.Enqueue(target);
            return 9901;
        }

        public VisibleDesktopLaunchResult OpenApplicationVisible(string applicationLaunchTarget)
        {
            Targets.Enqueue(applicationLaunchTarget);
            return new VisibleDesktopLaunchResult(9902, 9903, "测试应用");
        }

        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? browserLaunchTarget, Uri website)
        {
            Targets.Enqueue(website.AbsoluteUri);
            return new VisibleDesktopLaunchResult(9904, 9905, "测试浏览器");
        }
    }

    private sealed class FixedForegroundProvider : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot GetLastExternalWindow() =>
            new(
                9951,
                "P1 测试窗口",
                "p1-window",
                99510,
                new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero),
                DateTimeOffset.UtcNow);
    }

    private sealed class CountingCaptureService : IWindowCaptureService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CapturedWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new CapturedWindowFrame([1, 2, 3], 1, 1, "P1FakeCapture"));
        }
    }

    private sealed class CountingVisionProvider : IWindowVisionProvider
    {
        private int _callCount;

        public string ProviderId => "p1-counting-vision";

        public bool SendsImageOffDevice => false;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<WindowVisionResult> AnalyzeAsync(
            WindowVisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new WindowVisionResult(
                "不应执行到这里",
                "High",
                ["test"],
                []));
        }
    }
}
