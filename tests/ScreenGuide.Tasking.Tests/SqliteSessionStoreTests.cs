using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.Tasking.Tests;

public sealed class SqliteSessionStoreTests
{
    [Theory]
    [InlineData("ApplicationSearch")]
    [InlineData("SearchForeground")]
    public async Task RestartInterruptsAnInFlightVisualSearchConsentWithoutMakingItRetryable(string intent)
    {
        await using var environment = await SessionStoreEnvironment.CreateAsync();
        var session = await environment.CreateSessionAsync("视觉搜索重启边界");
        var registration = await environment.Store.StartTurnAsync(session.Id, "搜索资料", "Voice",
            "visual-consent-restart", CreateReadyRoute(environment.Now), environment.Now);
        await environment.Store.UpdateTurnAsync(registration.Turn with
        {
            Phase = SessionTurnPhase.WaitingForWindowConsent,
            MissingContext = SessionMissingContext.WindowConsent,
            IntentKind = intent,
            OperationId = "original-command",
            FailureCode = "visual_search_consent_required",
            ConfirmationGranted = false,
            WindowHandle = 1234,
            WindowTitle = "测试窗口"
        }, registration.Turn.Version, environment.Now);
        await environment.Store.RecoverInterruptedAsync(environment.Now.AddMinutes(1));
        var recovered = await environment.Store.GetTurnAsync(registration.Turn.Id);
        Assert.Equal(SessionTurnPhase.Interrupted, recovered!.Phase);
        Assert.Equal("host_restarted", recovered.FailureCode);
        Assert.False(recovered.ConfirmationGranted);
    }

    [Fact]
    public async Task NewTurnPersistsReadyFrozenRouteAcrossAStoreRestart()
    {
        await using var environment = await SessionStoreEnvironment.CreateAsync();
        var session = await environment.CreateSessionAsync("冻结路由");
        var frozenAt = environment.Now.AddMilliseconds(250);
        var route = new SessionTurnFrozenRoute
        {
            Status = SessionTurnRouteStatus.Ready,
            ProviderId = "provider-a",
            ModelId = "model-a",
            DataDestination = "provider-a isolated destination",
            SendsDataOffDevice = false,
            FrozenAtUtc = frozenAt
        };

        var registration = await environment.Store.StartTurnAsync(
            session.Id,
            "保存本轮路由。",
            "Text",
            "persist-ready-route",
            route,
            environment.Now);

        await using var reopened = new SqliteSessionStore(environment.DatabasePath);
        await reopened.InitializeAsync();
        var stored = await reopened.GetTurnAsync(registration.Turn.Id);

        Assert.True(registration.Accepted);
        Assert.Equal(route, registration.Turn.FrozenRoute);
        Assert.Equal(route, stored?.FrozenRoute);
    }

    [Fact]
    public async Task DuplicateKeepsTheFirstFrozenRouteAndANewTurnGetsTheNewRoute()
    {
        await using var environment = await SessionStoreEnvironment.CreateAsync();
        var session = await environment.CreateSessionAsync("幂等冻结路由");
        var routeA = CreateReadyRoute(environment.Now) with
        {
            ProviderId = "provider-a",
            ModelId = "model-a"
        };
        var routeB = CreateReadyRoute(environment.Now.AddMinutes(1)) with
        {
            ProviderId = "provider-b",
            ModelId = "model-b"
        };

        var first = await environment.Store.StartTurnAsync(
            session.Id,
            "第一次请求",
            "Text",
            "frozen-route-idempotency",
            routeA,
            environment.Now);
        var duplicate = await environment.Store.StartTurnAsync(
            session.Id,
            "第一次请求",
            "Text",
            "frozen-route-idempotency",
            routeB,
            environment.Now.AddMinutes(1));
        var next = await environment.Store.StartTurnAsync(
            session.Id,
            "第二次请求",
            "Text",
            "frozen-route-next-turn",
            routeB,
            environment.Now.AddMinutes(1));

        Assert.True(first.Accepted);
        Assert.False(duplicate.Accepted);
        Assert.Equal(first.Turn.Id, duplicate.Turn.Id);
        Assert.Equal(routeA, duplicate.Turn.FrozenRoute);
        Assert.True(next.Accepted);
        Assert.Equal(routeB, next.Turn.FrozenRoute);
        Assert.Equal(2, (await environment.Store.GetTurnsAsync(session.Id)).Count);
    }

    [Fact]
    public async Task UpdateTurnCannotReplaceThePersistedFrozenRoute()
    {
        await using var environment = await SessionStoreEnvironment.CreateAsync();
        var session = await environment.CreateSessionAsync("路由不可变");
        var routeA = CreateReadyRoute(environment.Now);
        var routeB = CreateReadyRoute(environment.Now.AddMinutes(1)) with
        {
            ProviderId = "provider-b",
            ModelId = "model-b"
        };
        var registration = await environment.Store.StartTurnAsync(
            session.Id,
            "更新状态但不更新路由",
            "Text",
            "immutable-frozen-route",
            routeA,
            environment.Now);

        var updated = await environment.Store.UpdateTurnAsync(
            registration.Turn with
            {
                FrozenRoute = routeB,
                Phase = SessionTurnPhase.WaitingForUser,
                MissingContext = SessionMissingContext.UserInput
            },
            registration.Turn.Version,
            environment.Now.AddSeconds(1));

        Assert.Equal(SessionTurnPhase.WaitingForUser, updated.Phase);
        Assert.Equal(routeA, updated.FrozenRoute);
    }

    [Fact]
    public async Task UnavailableFrozenRoutePersistsWithoutBlockingTurnAcceptance()
    {
        await using var environment = await SessionStoreEnvironment.CreateAsync();
        var session = await environment.CreateSessionAsync("不可用路由");
        var route = new SessionTurnFrozenRoute
        {
            Status = SessionTurnRouteStatus.Unavailable,
            ProviderId = "missing-provider",
            ModelId = "missing-model",
            FrozenAtUtc = environment.Now,
            FailureCode = "configured_chat_provider_not_found"
        };

        var registration = await environment.Store.StartTurnAsync(
            session.Id,
            "确定性工作仍可登记",
            "Text",
            "unavailable-frozen-route",
            route,
            environment.Now);

        Assert.True(registration.Accepted);
        Assert.Equal(route, (await environment.Store.GetTurnAsync(registration.Turn.Id))?.FrozenRoute);
    }

    [Fact]
    public async Task InvalidFrozenRouteFailsWithoutPersistingAPartialTurn()
    {
        await using var environment = await SessionStoreEnvironment.CreateAsync();
        var session = await environment.CreateSessionAsync("原子失败");
        var invalidRoute = new SessionTurnFrozenRoute
        {
            Status = SessionTurnRouteStatus.Ready,
            ProviderId = "provider-a",
            ModelId = "model-a",
            FrozenAtUtc = environment.Now
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            environment.Store.StartTurnAsync(
                session.Id,
                "不能留下半条记录",
                "Text",
                "invalid-route-atomicity",
                invalidRoute,
                environment.Now));

        Assert.Contains("Ready 冻结路由", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await environment.Store.GetTurnsAsync(session.Id));
    }

    [Fact]
    public async Task CurrentSessionAndProjectContextSurviveAStoreRestart()
    {
        await using var environment = await SessionStoreEnvironment.CreateAsync();
        var session = await environment.CreateSessionAsync("统一会话");
        await environment.Store.SetSelectedProjectAsync(session.Id, environment.Project.Id, environment.Now);

        await using var reopened = new SqliteSessionStore(environment.DatabasePath);
        await reopened.InitializeAsync();
        var current = await reopened.GetCurrentSessionAsync();

        Assert.Equal(session.Id, current?.Id);
        Assert.Equal(session.ConversationId, current?.ConversationId);
        Assert.Equal(environment.Project.Id, current?.SelectedProjectId);
        Assert.True(current?.IsCurrent);
    }

    [Fact]
    public async Task WaitingTurnKeepsTheOriginalRequestAndCanResumeAfterRestart()
    {
        await using var environment = await SessionStoreEnvironment.CreateAsync();
        var session = await environment.CreateSessionAsync("等待上下文");
        var registration = await environment.Store.StartTurnAsync(
            session.Id,
            "帮我修改一下这个项目",
            "Voice",
            "stage1-waiting-project",
            CreateReadyRoute(environment.Now),
            environment.Now);
        var turn = registration.Turn;
        var waiting = await environment.Store.UpdateTurnAsync(
            turn with
            {
                WorkKind = SessionWorkKind.CodingTask,
                IntentKind = "CodingTask",
                ExpectedIntentKind = "DesktopAction",
                ExpectedTarget = "app:notepad",
                PlanTarget = "notepad.exe",
                Phase = SessionTurnPhase.WaitingForProject,
                MissingContext = SessionMissingContext.Project
            },
            turn.Version,
            environment.Now.AddSeconds(1));

        await using var reopened = new SqliteSessionStore(environment.DatabasePath);
        await reopened.InitializeAsync();
        var stored = await reopened.GetTurnAsync(waiting.Id);

        Assert.Equal("帮我修改一下这个项目", stored?.InputText);
        Assert.Equal(SessionTurnPhase.WaitingForProject, stored?.Phase);
        Assert.Equal(SessionMissingContext.Project, stored?.MissingContext);
        Assert.Equal("DesktopAction", stored?.ExpectedIntentKind);
        Assert.Equal("app:notepad", stored?.ExpectedTarget);
        Assert.Equal("notepad.exe", stored?.PlanTarget);
        Assert.Equal(waiting.Version, stored?.Version);
    }

    private static SessionTurnFrozenRoute CreateReadyRoute(DateTimeOffset frozenAtUtc) => new()
    {
        Status = SessionTurnRouteStatus.Ready,
        ProviderId = "provider-a",
        ModelId = "model-a",
        DataDestination = "provider-a isolated destination",
        SendsDataOffDevice = false,
        FrozenAtUtc = frozenAtUtc
    };

    private sealed class SessionStoreEnvironment : IAsyncDisposable
    {
        private SessionStoreEnvironment(
            string root,
            string databasePath,
            SqliteSessionStore store,
            SqliteConversationStore conversations,
            DeviceRecord device,
            ProjectRecord project)
        {
            Root = root;
            DatabasePath = databasePath;
            Store = store;
            Conversations = conversations;
            Device = device;
            Project = project;
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public SqliteSessionStore Store { get; }

        public SqliteConversationStore Conversations { get; }

        public DeviceRecord Device { get; }

        public ProjectRecord Project { get; }

        public DateTimeOffset Now { get; } = new(2026, 8, 24, 1, 0, 0, TimeSpan.Zero);

        public static async Task<SessionStoreEnvironment> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"screen-guide-session-{Guid.NewGuid():N}");
            var projectRoot = Path.Combine(root, "project");
            var databasePath = Path.Combine(root, "state", "tasking.db");
            Directory.CreateDirectory(projectRoot);
            await using var taskStore = new SqliteTaskStore(databasePath);
            await taskStore.InitializeAsync();
            var now = new DateTimeOffset(2026, 8, 24, 1, 0, 0, TimeSpan.Zero);
            var device = new DeviceRecord
            {
                Id = Guid.NewGuid(),
                DisplayName = "Session Test Host",
                DeviceType = DeviceType.WindowsHost,
                TrustState = DeviceTrustState.Local,
                CreatedAtUtc = now,
                LastSeenAtUtc = now
            };
            await taskStore.UpsertDeviceAsync(device);
            var project = new ProjectRecord
            {
                Id = Guid.NewGuid(),
                Name = "Session Project",
                RootPath = projectRoot,
                AuthorizationState = ProjectAuthorizationState.Authorized,
                AuthorizedByDeviceId = device.Id,
                AuthorizedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            await taskStore.SetProjectAuthorizationAsync(project);
            var conversations = new SqliteConversationStore(databasePath);
            await conversations.InitializeAsync();
            var store = new SqliteSessionStore(databasePath);
            await store.InitializeAsync();
            return new SessionStoreEnvironment(root, databasePath, store, conversations, device, project);
        }

        public async Task<SessionRecord> CreateSessionAsync(string title)
        {
            var conversation = new ConversationRecord
            {
                Id = Guid.NewGuid(),
                CreatedByDeviceId = Device.Id,
                Title = title,
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now
            };
            await Conversations.CreateConversationAsync(conversation);
            var session = new SessionRecord
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                CreatedByDeviceId = Device.Id,
                Title = title,
                IsCurrent = true,
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
                LastActiveAtUtc = Now
            };
            await Store.CreateSessionAsync(session);
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
