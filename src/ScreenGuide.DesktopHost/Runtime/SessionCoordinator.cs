using System.Collections.Concurrent;
using System.Text.Json;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Vision.Abstractions;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record SessionSubmitResult(Guid SessionId, Guid TurnId, bool WasDuplicate);

public interface ISessionMemoryConsentPublicationObserver
{
    Task BeforePublishAsync(Guid turnId, Guid consentId, CancellationToken cancellationToken);

    Task AfterPublishAsync(Guid turnId, Guid consentId, CancellationToken cancellationToken);
}

/// <summary>
/// Desktop Host 中唯一的当前会话与前台 Turn 协调入口。
/// 它只协调状态和取消，不拥有任何项目、文件、窗口或电脑动作授权。
/// </summary>
public sealed partial class SessionCoordinator
{
    private const int MaximumInputLength = 20_000;
    private readonly ISessionStore sessionStore;
    private readonly SessionProjectionService projections;
    private readonly SessionOperationGateRegistry operationGates;
    private readonly ConversationService conversations;
    private readonly AssistantCommandService assistantCommands;
    private readonly LocalTaskEntryService tasks;
    private readonly SessionTaskStateSynchronizer taskStateSynchronizer;
    private readonly DesktopHostState hostState;
    private readonly ModelRouter modelRouter;
    private readonly PromptRegistry prompts;
    private readonly MemoryService memories;
    private readonly PointerRegionUnderstandingService pointerRegions;
    private readonly IEnumerable<ISessionMemoryConsentPublicationObserver> memoryConsentPublicationObservers;
    private readonly IForegroundWindowContextProvider foregroundWindows;
    private readonly TimeProvider timeProvider;
    private readonly ApplicationSearchProgressChannel applicationSearchProgress;
    private readonly VisualSearchConsentChannel visualSearchConsent;
    private readonly DesktopTaskInteractionChannel desktopTaskInteractions;
    private readonly IVisualSearchResultWindowVerifier? visualSearchResultWindows;
    private readonly SemaphoreSlim _currentSessionGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ActiveSessionWork> _activeWork = new();
    private readonly ConcurrentDictionary<Guid, ActiveTaskMonitor> _taskMonitors = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<MemoryOutboundItemReference>>
        _requestedMemorySelections = new();
    private readonly ConcurrentDictionary<Guid, MemoryOutboundPreparedConsent>
        _preparedMemoryConsents = new();
    private readonly ConcurrentDictionary<Guid, Guid> _requestedPointerAnchors = new();
    private readonly ConcurrentDictionary<Guid, PointerAnswerPreparedConsent>
        _preparedPointerAnswerConsents = new();

    internal SessionCoordinator(
        ISessionStore sessionStore,
        SessionProjectionService projections,
        SessionOperationGateRegistry operationGates,
        ConversationService conversations,
        AssistantCommandService assistantCommands,
        LocalTaskEntryService tasks,
        SessionTaskStateSynchronizer taskStateSynchronizer,
        DesktopHostState hostState,
        ModelRouter modelRouter,
        PromptRegistry prompts,
        MemoryService memories,
        PointerRegionUnderstandingService pointerRegions,
        IEnumerable<ISessionMemoryConsentPublicationObserver> memoryConsentPublicationObservers,
        IForegroundWindowContextProvider foregroundWindows,
        TimeProvider timeProvider,
        ApplicationSearchProgressChannel? applicationSearchProgress = null,
        VisualSearchConsentChannel? visualSearchConsent = null,
        IVisualSearchResultWindowVerifier? visualSearchResultWindows = null,
        DesktopTaskInteractionChannel? desktopTaskInteractions = null)
    {
        this.sessionStore = sessionStore;
        this.projections = projections;
        this.operationGates = operationGates;
        this.conversations = conversations;
        this.assistantCommands = assistantCommands;
        this.tasks = tasks;
        this.taskStateSynchronizer = taskStateSynchronizer;
        this.hostState = hostState;
        this.modelRouter = modelRouter;
        this.prompts = prompts;
        this.memories = memories;
        this.pointerRegions = pointerRegions;
        this.memoryConsentPublicationObservers = memoryConsentPublicationObservers;
        this.foregroundWindows = foregroundWindows;
        this.timeProvider = timeProvider;
        this.applicationSearchProgress = applicationSearchProgress ?? new ApplicationSearchProgressChannel();
        this.visualSearchConsent = visualSearchConsent ?? new VisualSearchConsentChannel();
        this.visualSearchResultWindows = visualSearchResultWindows;
        this.desktopTaskInteractions = desktopTaskInteractions ?? new DesktopTaskInteractionChannel();
    }

    public async Task<LocalSessionSnapshot?> GetCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        var current = await sessionStore.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        return current is null ? null : await BuildSnapshotAsync(current, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalSessionSnapshot> StartNewAsync(
        string? title,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        await _currentSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await StartNewCoreAsync(title, cancellationToken).ConfigureAwait(false);
            return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _currentSessionGate.Release();
        }
    }

    private async Task<SessionRecord> StartNewCoreAsync(
        string? title,
        CancellationToken cancellationToken)
    {
        var host = RequireStartedHost();
        var previous = await sessionStore.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            await CancelForegroundWorkAsync(
                    previous,
                    "已开始新话题，上一条前台请求已停止。",
                    excludedTurnId: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var conversation = await conversations.CreateAsync(title, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var session = new SessionRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            CreatedByDeviceId = host.LocalDevice!.Id,
            Title = conversation.Title,
            IsCurrent = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastActiveAtUtc = now
        };
        await sessionStore.CreateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        PublishChange();
        return session;
    }

    public async Task<LocalSessionSnapshot> SetCurrentAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        await _currentSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await SetCurrentCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _currentSessionGate.Release();
        }
    }

    private async Task<SessionRecord> SetCurrentCoreAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var previous = await sessionStore.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (previous is not null && previous.Id != sessionId)
        {
            await CancelForegroundWorkAsync(
                    previous,
                    "已切换话题，上一条前台请求已停止。",
                    excludedTurnId: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var session = await sessionStore.SetCurrentSessionAsync(
                sessionId,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        PublishChange();
        return session;
    }

    public async Task<SessionSubmitResult> SubmitAsync(
        Guid? requestedSessionId,
        string text,
        string inputModality,
        string? idempotencyKey,
        CancellationToken cancellationToken = default,
        string? expectedIntentKind = null,
        string? expectedTarget = null,
        IReadOnlyList<MemoryOutboundItemReference>? memoryItems = null,
        Guid? pointerAnchorId = null)
    {
        RequireStartedHost();
        var normalized = NormalizeInput(text);
        var normalizedMemoryItems = NormalizeMemorySelection(memoryItems);
        if (normalizedMemoryItems.Count > 0 && pointerAnchorId is not null)
        {
            throw new ArgumentException("同一 Turn 不能同时发送长期记忆和指针区域文字。", nameof(pointerAnchorId));
        }
        if (pointerAnchorId == Guid.Empty)
        {
            throw new ArgumentException("指针锚点标识无效。", nameof(pointerAnchorId));
        }
        var expectation = NormalizeActionExpectation(expectedIntentKind, expectedTarget);
        await _currentSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selected = requestedSessionId is { } sessionId
                ? await sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                : await sessionStore.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
            SessionRecord session;
            if (selected is null)
            {
                session = await StartNewCoreAsync(BuildTitle(normalized), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!selected.IsCurrent)
            {
                session = await SetCurrentCoreAsync(selected.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                session = selected;
            }

            // Selecting the current Session and registering its foreground Turn form one
            // linearized operation. Releasing this gate earlier lets New Topic switch away
            // before the Turn becomes visible, which can create two foreground Sessions.
            var gate = await operationGates.AcquireSessionAsync(session.Id, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var normalizedIdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                    ? Guid.NewGuid().ToString("N")
                    : idempotencyKey.Trim();
                var existingTurn = (await sessionStore.GetTurnsAsync(session.Id, cancellationToken)
                        .ConfigureAwait(false))
                    .SingleOrDefault(turn => string.Equals(
                        turn.IdempotencyKey,
                        normalizedIdempotencyKey,
                        StringComparison.Ordinal));
                if (existingTurn is not null)
                {
                    if (!MatchesExpectedAction(
                            existingTurn,
                            expectation.IntentKind,
                            expectation.Target))
                    {
                        throw new InvalidOperationException(
                            "同一个请求编号不能改成另一个电脑操作目标。 ");
                    }

                    if (!MemorySelectionMatches(existingTurn, normalizedMemoryItems))
                    {
                        throw new InvalidOperationException(
                            "同一个请求编号不能改成另一组长期记忆。");
                    }

                    if (!PointerAnchorMatches(existingTurn, pointerAnchorId))
                    {
                        throw new InvalidOperationException(
                            "同一个请求编号不能改成另一个指针锚点。");
                    }

                    return new SessionSubmitResult(session.Id, existingTurn.Id, true);
                }

                var resolvedRoute = await modelRouter.ResolveDefaultChatRouteAsync(cancellationToken)
                    .ConfigureAwait(false);
                var startedAtUtc = timeProvider.GetUtcNow();
                var registration = await sessionStore.StartTurnAsync(
                        session.Id,
                        normalized,
                        NormalizeModality(inputModality),
                        normalizedIdempotencyKey,
                        ToFrozenRoute(resolvedRoute, startedAtUtc),
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                var registeredTurn = registration.Turn;
                if (!registration.Accepted)
                {
                    if (!MatchesExpectedAction(
                            registeredTurn,
                            expectation.IntentKind,
                            expectation.Target))
                    {
                        throw new InvalidOperationException(
                            "同一个请求编号不能改成另一个电脑操作目标。 ");
                    }

                    return new SessionSubmitResult(session.Id, registration.Turn.Id, true);
                }

                if (normalizedMemoryItems.Count > 0)
                {
                    _requestedMemorySelections[registeredTurn.Id] = normalizedMemoryItems;
                }
                if (pointerAnchorId is { } anchorId)
                {
                    _requestedPointerAnchors[registeredTurn.Id] = anchorId;
                }

                if (expectation.IntentKind is not null)
                {
                    registeredTurn = await sessionStore.UpdateTurnAsync(
                            registeredTurn with
                            {
                                ExpectedIntentKind = expectation.IntentKind,
                                ExpectedTarget = expectation.Target
                            },
                            registeredTurn.Version,
                            timeProvider.GetUtcNow(),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await CancelForegroundWorkAsync(
                        session,
                        "新输入已取代上一条请求。",
                        registration.Turn.Id,
                        cancellationToken)
                    .ConfigureAwait(false);

                var active = new ActiveSessionWork(registeredTurn.Id);
                if (!_activeWork.TryAdd(registeredTurn.Id, active))
                {
                    throw new InvalidOperationException("同一个会话请求已经在处理中。");
                }

                active.SetCompletion(Task.Run(
                    () => RunInitialTurnAsync(session, registeredTurn, active.Cancellation.Token),
                    CancellationToken.None));
                PublishChange(session.Id, registeredTurn);
                return new SessionSubmitResult(session.Id, registeredTurn.Id, false);
            }
            finally
            {
                await gate.DisposeAsync();
            }
        }
        finally
        {
            _currentSessionGate.Release();
        }
    }

    private static SessionTurnFrozenRoute ToFrozenRoute(
        DefaultChatRouteResolution route,
        DateTimeOffset frozenAtUtc) => new()
    {
        Status = route.Status == ChatRouteResolutionStatus.Ready
            ? SessionTurnRouteStatus.Ready
            : SessionTurnRouteStatus.Unavailable,
        ProviderId = route.ProviderId,
        ModelId = route.ModelId,
        DataDestination = route.DataDestination,
        SendsDataOffDevice = route.SendsDataOffDevice,
        FrozenAtUtc = frozenAtUtc,
        FailureCode = route.FailureCode
    };

    public async Task<LocalSessionSnapshot> ProvideProjectAsync(
        Guid sessionId,
        Guid turnId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var project = (await tasks.GetAuthorizedProjectsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == projectId)
            ?? throw new UnauthorizedAccessException("所选项目没有授权或已经失效。 ");
        var gate = await operationGates.AcquireTurnAsync(turnId, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var (session, turn) = await RequireWaitingTurnAsync(
                    sessionId,
                    turnId,
                    SessionTurnPhase.WaitingForProject,
                    cancellationToken)
                .ConfigureAwait(false);
            session = await sessionStore.SetSelectedProjectAsync(
                    session.Id,
                    project.Id,
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            turn = await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        ProjectId = project.Id,
                        Phase = SessionTurnPhase.Understanding,
                        MissingContext = SessionMissingContext.None,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await PlanAndRouteAsync(
                    session,
                    turn,
                    observationConsent: false,
                    executeReadyPlan: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await gate.DisposeAsync();
        }
    }

    public async Task<LocalSessionSnapshot> ProvideFileAsync(
        Guid sessionId,
        Guid turnId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("请选择这一次要处理的文件。", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("刚才选择的文件已经不存在。", fullPath);
        }

        var gate = await operationGates.AcquireTurnAsync(turnId, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var (session, turn) = await RequireWaitingTurnAsync(
                    sessionId,
                    turnId,
                    SessionTurnPhase.WaitingForFile,
                    cancellationToken)
                .ConfigureAwait(false);
            turn = await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        FilePath = fullPath,
                        Phase = SessionTurnPhase.Understanding,
                        MissingContext = SessionMissingContext.None,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await PlanAndRouteAsync(
                    session,
                    turn,
                    observationConsent: false,
                    executeReadyPlan: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await gate.DisposeAsync();
        }
    }

    public async Task<LocalSessionSnapshot> RespondWindowConsentAsync(
        Guid sessionId,
        Guid turnId,
        bool granted,
        CancellationToken cancellationToken = default,
        Guid? consentId = null)
    {
        RequireStartedHost();
        var gate = await operationGates.AcquireTurnAsync(turnId, cancellationToken)
            .ConfigureAwait(false);
        SessionRecord session;
        ActiveSessionWork? active = null;
        try
        {
            var required = await RequireWaitingTurnAsync(
                    sessionId,
                    turnId,
                    SessionTurnPhase.WaitingForWindowConsent,
                    cancellationToken)
                .ConfigureAwait(false);
            session = required.Session;
            if (required.Turn.FailureCode == DesktopTaskConsentCode)
            {
                active = await RespondDesktopTaskConsentAsync(required.Turn, granted, consentId, cancellationToken);
            }
            else if (required.Turn.FailureCode == "visual_search_consent_required")
            {
                if (!_activeWork.TryGetValue(turnId, out active) || active.VisualConsent is not { } pending)
                    throw new InvalidOperationException("这次视觉搜索已中断，请重新下达指令；不会重放原来的操作。");
                if (consentId is null || consentId == Guid.Empty || consentId != pending.ConsentId
                    || required.Turn.Version != pending.TurnVersion)
                    throw new UnauthorizedAccessException("这个窗口许可已失效，请查看当前窗口请求；旧确认不会授权新窗口。");

                if (!granted)
                {
                    await TransitionAsync(turnId, current => current with
                    {
                        Phase = SessionTurnPhase.Cancelled,
                        MissingContext = SessionMissingContext.None,
                        CancellationRequested = true,
                        ConfirmationGranted = false,
                        ResultSummary = pending.Request.Purpose == VisualSearchConsentPurpose.ResultRead
                            ? "你没有同意查看这个临时窗口，结果尚未核对；没有继续点击或输入。"
                            : "你没有同意查看并搜索这个窗口，任务已安全结束。",
                        FailureCode = "window_consent_rejected",
                        FailureMessage = "未获得本次视觉搜索授权。",
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    }, cancellationToken).ConfigureAwait(false);
                    CancelActiveWork(active);
                }
                else
                {
                    try
                    {
                        if (active.Cancellation.IsCancellationRequested
                            || timeProvider.GetUtcNow() >= pending.ExpiresAtUtc
                            || required.Turn.OperationId != pending.OperationId
                            || required.Turn.PlanTarget != pending.PlanTarget
                            || !MatchesPlannedExpectation(required.Turn)
                            || !MatchesPersistedWindowIdentity(required.Turn))
                            throw new WindowIdentityException(WindowIdentityErrorCodes.Changed,
                                "目标窗口、搜索范围或本次许可已经变化，视觉搜索已停止；请重新下达指令。");
                        VerifyVisualSearchWindows(pending.Request);
                        await TransitionAsync(turnId, current => current with
                        {
                            Phase = SessionTurnPhase.Executing,
                            MissingContext = SessionMissingContext.None,
                            ConfirmationGranted = true,
                            FailureCode = null,
                            FailureMessage = null,
                            ResultSummary = pending.Request.Purpose == VisualSearchConsentPurpose.ResultRead
                                ? "正在本机只读核对这个临时窗口中的搜索结果；不会点击、输入或发送消息。"
                                : "正在本机查看已允许的窗口并完成这次搜索；不会打开结果或发送消息。"
                        }, cancellationToken).ConfigureAwait(false);
                        pending.Completion.TrySetResult();
                    }
                    catch (Exception exception) when (exception is WindowIdentityException or UnauthorizedAccessException)
                    {
                        pending.Completion.TrySetException(exception);
                    }
                }
            }
            else if (!granted)
            {
                await TransitionAsync(
                        required.Turn.Id,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Cancelled,
                            MissingContext = SessionMissingContext.None,
                            CancellationRequested = true,
                            ResultSummary = "你没有同意本次窗口查看，任务已安全结束。",
                            FailureCode = "window_consent_rejected",
                            FailureMessage = "未获得本次窗口查看授权。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                if (!MatchesPlannedExpectation(required.Turn))
                {
                    throw new UnauthorizedAccessException(
                        "实际操作目标与刚才确认的目标不一致，已拒绝执行。 ");
                }

                if (IsWindowBoundTurn(required.Turn)
                    && !MatchesPersistedWindowIdentity(required.Turn))
                {
                    await RequireFreshWindowConfirmationAsync(required.Turn, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    active = StartAuthorizedContinuation(
                        required.Session,
                        required.Turn,
                        observationConsent: true);
                }
            }
        }
        finally
        {
            await gate.DisposeAsync();
        }

        if (active is not null)
        {
            await active.WaitForVisiblePauseOrCompletionAsync(cancellationToken).ConfigureAwait(false);
        }

        return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalSessionSnapshot> RetryTurnAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var gate = await operationGates.AcquireTurnAsync(turnId, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var (session, turn) = await RequireWaitingTurnAsync(
                    sessionId,
                    turnId,
                    SessionTurnPhase.WaitingForWindow,
                    cancellationToken)
                .ConfigureAwait(false);
            turn = await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        Phase = SessionTurnPhase.Understanding,
                        MissingContext = SessionMissingContext.None,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await PlanAndRouteAsync(
                    session,
                    turn,
                    observationConsent: false,
                    executeReadyPlan: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await gate.DisposeAsync();
        }
    }

    public async Task<LocalSessionSnapshot> ConfirmTurnAsync(
        Guid sessionId,
        Guid turnId,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var gate = await operationGates.AcquireTurnAsync(turnId, cancellationToken)
            .ConfigureAwait(false);
        SessionRecord session;
        ActiveSessionWork? active = null;
        try
        {
            var required = await RequireWaitingTurnAsync(
                    sessionId,
                    turnId,
                    SessionTurnPhase.WaitingForConfirmation,
                    cancellationToken)
                .ConfigureAwait(false);
            session = required.Session;
            if (!confirmed)
            {
                await TransitionAsync(
                        required.Turn.Id,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Cancelled,
                            MissingContext = SessionMissingContext.None,
                            CancellationRequested = true,
                            ResultSummary = "你取消了这次操作，没有执行任何动作。",
                            FailureCode = "confirmation_rejected",
                            FailureMessage = "用户未确认本次操作。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                if (!MatchesPlannedExpectation(required.Turn))
                {
                    throw new UnauthorizedAccessException(
                        "实际操作目标与刚才确认的目标不一致，已拒绝执行。 ");
                }

                if (IsWindowBoundTurn(required.Turn)
                    && !MatchesPersistedWindowIdentity(required.Turn))
                {
                    await RequireFreshWindowConfirmationAsync(required.Turn, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    active = StartAuthorizedContinuation(
                        required.Session,
                        required.Turn,
                        observationConsent: false);
                }
            }
        }
        finally
        {
            await gate.DisposeAsync();
        }

        if (active is not null)
        {
            await active.WaitForVisiblePauseOrCompletionAsync(cancellationToken).ConfigureAwait(false);
        }

        return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalSessionSnapshot> CancelTurnAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default,
        long? expectedVersion = null)
    {
        RequireStartedHost();
        var gate = await operationGates.AcquireTurnAsync(turnId, cancellationToken)
            .ConfigureAwait(false);
        SessionRecord session;
        SessionTurnRecord turn;
        ActiveSessionWork? active = null;
        SessionTurnPhase phaseAtCancellation = default;
        Guid? taskIdAtCancellation = null;
        string? operationIdAtCancellation = null;
        try
        {
            session = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            turn = await RequireTurnAsync(session, turnId, cancellationToken).ConfigureAwait(false);
            if (expectedVersion is { } expected && turn.Version != expected)
                throw new InvalidOperationException("entity_version_mismatch");
            phaseAtCancellation = turn.Phase;
            taskIdAtCancellation = turn.TaskId;
            operationIdAtCancellation = turn.OperationId;
            if (SessionTurnPhases.IsTerminal(turn.Phase))
            {
                return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
            }

            _preparedMemoryConsents.TryRemove(turn.Id, out _);
            _requestedMemorySelections.TryRemove(turn.Id, out _);
            _preparedPointerAnswerConsents.TryRemove(turn.Id, out _);
            _requestedPointerAnchors.TryRemove(turn.Id, out _);

            if (_activeWork.TryGetValue(turn.Id, out active))
            {
                if (turn.Phase != SessionTurnPhase.Responding)
                {
                    CancelActiveWork(active);
                }
            }
            else
            {
                turn = await TransitionAsync(
                        turn.Id,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Cancelled,
                            MissingContext = SessionMissingContext.None,
                            CancellationRequested = true,
                            MemoryOutboundState = current.MemoryOutboundState is
                                MemoryOutboundConsentState.Prepared or
                                MemoryOutboundConsentState.WaitingForMemoryOutboundConsent or
                                MemoryOutboundConsentState.Committing
                                    ? MemoryOutboundConsentState.Cancelled
                                    : current.MemoryOutboundState,
                            ResultSummary = "这次请求已取消。",
                            FailureCode = "user_cancelled",
                            FailureMessage = "用户取消了当前请求。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await gate.DisposeAsync();
        }

        if (active is not null)
        {
            if (phaseAtCancellation == SessionTurnPhase.Responding)
            {
                try
                {
                    await conversations.CancelAsync(session.ConversationId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // The provider may have completed immediately before cancellation won the turn gate.
                    CancelActiveWork(active);
                }
            }

            if (!string.IsNullOrWhiteSpace(operationIdAtCancellation))
            {
                assistantCommands.CancelWindowObservation(operationIdAtCancellation);
            }

            await active.Completion.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken)
                .ConfigureAwait(false);
        }

        if (taskIdAtCancellation is { } taskId
            && phaseAtCancellation is SessionTurnPhase.ProgrammingTask or SessionTurnPhase.WaitingForUser)
        {
            await tasks.CancelTaskAsync(
                    taskId,
                    $"session-cancel-{turn.Id:N}",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var latest = await sessionStore.GetTurnAsync(turn.Id, cancellationToken).ConfigureAwait(false);
        if (latest is not null && !SessionTurnPhases.IsTerminal(latest.Phase))
        {
            await TryEndTurnAsync(
                    latest.Id,
                    SessionTurnPhase.Cancelled,
                    "user_cancelled",
                    "用户取消了当前请求。")
                .ConfigureAwait(false);
        }

        return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalSessionSnapshot?> WaitForChangeAsync(
        long knownChangeVersion,
        TimeSpan maximumWait,
        CancellationToken cancellationToken = default) =>
        await projections.WaitForChangeAsync(
                knownChangeVersion,
                maximumWait,
                _preparedMemoryConsents.Values.ToArray(),
                _preparedPointerAnswerConsents.Values.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<LocalSessionProjectionUpdate> WaitForProjectionAsync(
        string coordinatorInstanceId,
        DateTimeOffset coordinatorStartedAtUtc,
        Guid sessionId,
        long knownChangeVersion,
        long knownMessageSequenceNumber,
        TimeSpan maximumWait,
        CancellationToken cancellationToken = default) =>
        await projections.WaitForProjectionAsync(
                coordinatorInstanceId,
                coordinatorStartedAtUtc,
                sessionId,
                knownChangeVersion,
                knownMessageSequenceNumber,
                maximumWait,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<LocalSessionMessagesPage> GetMessagesPageAsync(
        Guid sessionId,
        long? beforeSequenceNumber,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        await projections.GetMessagesPageAsync(
                sessionId,
                beforeSequenceNumber,
                pageSize,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<LocalSessionTurnDetails> GetTurnDetailsAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default) =>
        await projections.GetTurnDetailsAsync(sessionId, turnId, cancellationToken)
            .ConfigureAwait(false);

    public async Task<SessionRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await sessionStore.RecoverInterruptedAsync(
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var turnId in result.InterruptedTurnIds)
        {
            await ReconcileRecoveredTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
        }

        if (result.InterruptedTurnIds.Count > 0)
        {
            PublishChange();
        }

        return result;
    }

    private async Task ReconcileRecoveredTurnAsync(
        Guid turnId,
        CancellationToken cancellationToken)
    {
        var turn = await sessionStore.GetTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
        if (turn is null)
        {
            return;
        }

        SessionTurnPhase? authoritativePhase = null;
        string? summary = turn.ResultSummary;
        string? failureCode = turn.FailureCode;
        string? failureMessage = turn.FailureMessage;
        DateTimeOffset? completedAt = turn.CompletedAtUtc;
        if (turn.ConversationTurnId is { } conversationTurnId)
        {
            var session = await sessionStore.GetSessionAsync(turn.SessionId, cancellationToken)
                .ConfigureAwait(false);
            var details = session is null
                ? null
                : await conversations.GetDetailsAsync(session.ConversationId, cancellationToken)
                    .ConfigureAwait(false);
            var conversationTurn = details?.Turns.SingleOrDefault(item => item.Id == conversationTurnId);
            if (conversationTurn is not null)
            {
                authoritativePhase = conversationTurn.Status switch
                {
                    ConversationTurnStatus.Succeeded => SessionTurnPhase.Completed,
                    ConversationTurnStatus.Cancelled => SessionTurnPhase.Cancelled,
                    ConversationTurnStatus.Failed => SessionTurnPhase.Failed,
                    _ => SessionTurnPhase.Interrupted
                };
                summary = conversationTurn.AssistantMessageId is { } assistantId
                    ? details!.Messages.SingleOrDefault(message => message.Id == assistantId)?.Content
                    : summary;
                failureCode = conversationTurn.FailureCode;
                failureMessage = conversationTurn.FailureMessage;
                completedAt = conversationTurn.CompletedAtUtc ?? completedAt;
            }
        }
        else if (turn.TaskId is { } taskId)
        {
            var details = await tasks.GetTaskDetailsAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (details is not null)
            {
                authoritativePhase = details.Task.Status switch
                {
                    AgentTaskStatus.Succeeded => SessionTurnPhase.Completed,
                    AgentTaskStatus.Cancelled => SessionTurnPhase.Cancelled,
                    AgentTaskStatus.Failed => SessionTurnPhase.Failed,
                    _ => SessionTurnPhase.Interrupted
                };
                summary = details.Evidence?.UserSummary ?? summary;
                failureCode = details.Task.FailureCode ?? failureCode;
                failureMessage = details.Task.FailureMessage ?? failureMessage;
                completedAt = details.Task.CompletedAtUtc ?? completedAt;
            }
        }

        if (authoritativePhase is null || authoritativePhase == turn.Phase)
        {
            return;
        }

        await sessionStore.UpdateTurnAsync(
                turn with
                {
                    Phase = authoritativePhase.Value,
                    MissingContext = SessionMissingContext.None,
                    ResultSummary = summary,
                    FailureCode = failureCode,
                    FailureMessage = failureMessage,
                    CompletedAtUtc = completedAt ?? timeProvider.GetUtcNow()
                },
                turn.Version,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _preparedMemoryConsents.Clear();
        _requestedMemorySelections.Clear();
        _preparedPointerAnswerConsents.Clear();
        _requestedPointerAnchors.Clear();
        var active = _activeWork.Values.ToArray();
        foreach (var work in active)
        {
            work.Cancellation.Cancel();
        }

        var current = await sessionStore.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            var turns = await sessionStore.GetActiveTurnsAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
            if (turns.Any(turn => turn.Phase == SessionTurnPhase.Responding))
            {
                try
                {
                    await conversations.CancelAsync(current.ConversationId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        if (active.Length > 0)
        {
            await Task.WhenAll(active.Select(item => item.Completion))
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
        }

        var monitors = _taskMonitors.Values.ToArray();
        foreach (var monitor in monitors)
        {
            monitor.Cancellation.Cancel();
        }

        if (monitors.Length > 0)
        {
            await Task.WhenAll(monitors.Select(item => item.Completion))
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RunInitialTurnAsync(
        SessionRecord session,
        SessionTurnRecord turn,
        CancellationToken cancellationToken)
    {
        try
        {
            await PlanAndRouteAsync(
                    session,
                    turn,
                    observationConsent: false,
                    executeReadyPlan: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryEndTurnAsync(
                    turn.Id,
                    SessionTurnPhase.Cancelled,
                    "cancelled",
                    "上一条请求已停止。")
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryEndTurnAsync(
                    turn.Id,
                    SessionTurnPhase.Failed,
                    FriendlyExceptionCode(exception, "session_request_failed"),
                    FriendlyException(exception))
                .ConfigureAwait(false);
        }
        finally
        {
            RemoveActiveWork(turn.Id);
        }
    }

    private ActiveSessionWork StartAuthorizedContinuation(
        SessionRecord session,
        SessionTurnRecord turn,
        bool observationConsent)
    {
        var active = new ActiveSessionWork(turn.Id);
        if (!_activeWork.TryAdd(turn.Id, active))
        {
            throw new InvalidOperationException("同一个会话请求已经在处理中。 ");
        }

        active.SetCompletion(Task.Run(async () =>
        {
            try
            {
                if (string.Equals(turn.IntentKind, nameof(UniversalIntentKind.OpenFile), StringComparison.Ordinal))
                    assistantCommands.ValidateFilePlanForContinuation(turn.PlanId);
                var prepared = await TransitionAsync(
                        turn.Id,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Understanding,
                            MissingContext = SessionMissingContext.None,
                            ConfirmationGranted = true,
                            FailureCode = null,
                            FailureMessage = null
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (SessionTurnPhases.IsTerminal(prepared.Phase))
                {
                    return;
                }

                await PlanAndRouteAsync(
                        session,
                        prepared,
                        observationConsent,
                        executeReadyPlan: true,
                        active.Cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
            {
                await TryEndTurnAsync(
                        turn.Id,
                        SessionTurnPhase.Cancelled,
                        "cancelled",
                        "这次请求已停止。")
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await TryEndTurnAsync(
                        turn.Id,
                        SessionTurnPhase.Failed,
                        FriendlyExceptionCode(exception, "session_execution_failed"),
                        FriendlyException(exception))
                    .ConfigureAwait(false);
            }
            finally
            {
                RemoveActiveWork(turn.Id);
            }
        }, CancellationToken.None));
        return active;
    }

    private async Task PlanAndRouteAsync(
        SessionRecord session,
        SessionTurnRecord turn,
        bool observationConsent,
        bool executeReadyPlan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var latest = await sessionStore.GetTurnAsync(turn.Id, CancellationToken.None).ConfigureAwait(false);
        if (latest is null || SessionTurnPhases.IsTerminal(latest.Phase))
        {
            return;
        }

        turn = latest;
        var semanticRoute = RestoreReadyRoute(turn.FrozenRoute);
        var memorySelectionRequested = _requestedMemorySelections.TryGetValue(
                turn.Id,
                out var requestedMemoryItems)
            && requestedMemoryItems.Count > 0;
        var pointerAnswerRequested = _requestedPointerAnchors.TryGetValue(
            turn.Id,
            out var requestedPointerAnchorId);
        var persistedIntent = memorySelectionRequested || pointerAnswerRequested
            ? UniversalIntentKind.Conversation
            : TryParsePersistedIntent(turn.IntentKind);
        var plan = await assistantCommands.PlanSessionAsync(
                new PlanAssistantCommandRequestDto(
                    turn.InputText,
                    turn.FilePath,
                    turn.ProjectId ?? session.SelectedProjectId,
                    observationConsent,
                    turn.InputModality),
                turn.Id,
                semanticRoute,
                persistedIntent,
                cancellationToken,
                confirmedPlanId: executeReadyPlan ? turn.PlanId : null)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!MatchesExpectedAction(turn, plan.IntentKind, plan.CanonicalTarget))
        {
            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = WorkKindFor(ParseIntentKind(plan.IntentKind)),
                        IntentKind = plan.IntentKind,
                        PlanId = null,
                        PlanTarget = plan.CanonicalTarget,
                        Phase = SessionTurnPhase.Failed,
                        MissingContext = SessionMissingContext.None,
                        ResultSummary = "安全检查发现实际计划与刚才确认的目标不一致，因此没有执行。",
                        FailureCode = "confirmed_target_mismatch",
                        FailureMessage = "结构化操作目标不一致。",
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var kind = ParseIntentKind(plan.IntentKind);
        if (executeReadyPlan
            && (kind is UniversalIntentKind.ApplicationSearch or UniversalIntentKind.OpenApplication
                || turn.IntentKind is nameof(UniversalIntentKind.ApplicationSearch) or nameof(UniversalIntentKind.OpenApplication))
            && (!string.Equals(turn.IntentKind, plan.IntentKind, StringComparison.Ordinal)
                || !string.Equals(turn.PlanTarget, plan.CanonicalTarget, StringComparison.Ordinal)))
        {
            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        PlanId = null,
                        Phase = SessionTurnPhase.Failed,
                        MissingContext = SessionMissingContext.None,
                        ConfirmationGranted = false,
                        ResultSummary = "应用、搜索内容或后续操作在确认后发生变化，本次没有执行；请重新下达指令。",
                        FailureCode = "confirmed_target_mismatch",
                        FailureMessage = "应用搜索计划与刚才确认的完整操作范围不一致。",
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }
        if (executeReadyPlan && kind == UniversalIntentKind.OpenApplication)
            assistantCommands.ValidateApplicationPlanForContinuation(turn.PlanId, plan.PlanId);
        var plannedWindow = kind is UniversalIntentKind.DescribeForeground
            or UniversalIntentKind.SearchForeground
            ? ResolvePlannedWindow(plan.ForegroundApplication)
            : null;

        turn = await TransitionAsync(
                turn.Id,
                current => current with
                {
                    IntentKind = plan.IntentKind,
                    PlanTarget = plan.CanonicalTarget
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (SessionTurnPhases.IsTerminal(turn.Phase))
        {
            return;
        }

        var workKind = WorkKindFor(kind);

        if (executeReadyPlan
            && kind is UniversalIntentKind.DescribeForeground or UniversalIntentKind.SearchForeground
            && !MatchesPersistedWindowIdentity(turn, plannedWindow))
        {
            var changedPhase = kind == UniversalIntentKind.DescribeForeground
                ? SessionTurnPhase.WaitingForWindowConsent
                : SessionTurnPhase.WaitingForConfirmation;
            var changedMissing = kind == UniversalIntentKind.DescribeForeground
                ? SessionMissingContext.WindowConsent
                : SessionMissingContext.Confirmation;
            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = workKind,
                        IntentKind = plan.IntentKind,
                        PlanId = null,
                        Phase = plannedWindow is null
                            ? SessionTurnPhase.WaitingForWindow
                            : changedPhase,
                        MissingContext = plannedWindow is null
                            ? SessionMissingContext.Window
                            : changedMissing,
                        WindowHandle = plannedWindow?.WindowHandle,
                        WindowTitle = plannedWindow?.WindowTitle,
                        WindowProcessName = plannedWindow?.ProcessName,
                        WindowProcessId = plannedWindow?.ProcessId,
                        WindowProcessStartTimeUtc = plannedWindow?.ProcessStartTimeUtc,
                        ConfirmationGranted = false,
                        RequiresConfirmation = true,
                        ResultSummary = plannedWindow is null
                            ? "目标窗口已经不可用，请切换到要操作的窗口后继续。"
                            : "前台窗口已经变化，请确认新的目标窗口后再继续。",
                        FailureCode = "window_target_changed",
                        FailureMessage = "授权后目标窗口发生变化。"
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(plan.Readiness, IntentPlanReadiness.NeedsContext.ToString(), StringComparison.Ordinal))
        {
            var (phase, missing) = MissingContextState(kind, plan);
            if (kind is UniversalIntentKind.DescribeForeground or UniversalIntentKind.SearchForeground
                && plannedWindow is null)
            {
                phase = SessionTurnPhase.WaitingForWindow;
                missing = SessionMissingContext.Window;
            }
            if (kind == UniversalIntentKind.CodingTask
                && (turn.ProjectId ?? session.SelectedProjectId) is not null)
            {
                session = await sessionStore.SetSelectedProjectAsync(
                        session.Id,
                        projectId: null,
                        timeProvider.GetUtcNow(),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = workKind,
                        IntentKind = plan.IntentKind,
                        PlanId = null,
                        Phase = phase,
                        MissingContext = missing,
                        ProjectId = kind == UniversalIntentKind.CodingTask
                            ? null
                            : current.ProjectId,
                        WindowHandle = plannedWindow?.WindowHandle,
                        WindowTitle = plannedWindow?.WindowTitle,
                        WindowProcessName = plannedWindow?.ProcessName,
                        WindowProcessId = plannedWindow?.ProcessId,
                        WindowProcessStartTimeUtc = plannedWindow?.ProcessStartTimeUtc,
                        RequiresConfirmation = phase == SessionTurnPhase.WaitingForWindowConsent,
                        ResultSummary = plan.UserSummary,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(plan.Readiness, IntentPlanReadiness.Ready.ToString(), StringComparison.Ordinal)
            || kind == UniversalIntentKind.Unsupported)
        {
            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = workKind,
                        IntentKind = plan.IntentKind,
                        Phase = SessionTurnPhase.Failed,
                        MissingContext = SessionMissingContext.None,
                        ResultSummary = plan.UserSummary,
                        FailureCode = "intent_unsupported",
                        FailureMessage = plan.UserSummary,
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        if (kind == UniversalIntentKind.Conversation)
        {
            if (pointerAnswerRequested)
            {
                await PreparePointerAnswerConsentAsync(
                        session,
                        turn,
                        requestedPointerAnchorId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (memorySelectionRequested)
            {
                await PrepareMemoryOutboundConsentAsync(
                        session,
                        turn,
                        requestedMemoryItems!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await RunConversationTurnAsync(
                        session,
                        turn,
                        memoryOutbound: null,
                        pointerAnswer: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        var directVoiceAction = IsDirectVoiceAction(kind, turn.InputModality);
        if (plan.RequiresConfirmation && !executeReadyPlan && !directVoiceAction)
        {
            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = workKind,
                        IntentKind = plan.IntentKind,
                        PlanId = plan.PlanId,
                        Phase = SessionTurnPhase.WaitingForConfirmation,
                        MissingContext = SessionMissingContext.Confirmation,
                        ProjectId = turn.ProjectId ?? session.SelectedProjectId,
                        FilePath = kind == UniversalIntentKind.OpenFile ? plan.CanonicalTarget : turn.FilePath,
                        WindowHandle = plannedWindow?.WindowHandle,
                        WindowTitle = plannedWindow?.WindowTitle,
                        WindowProcessName = plannedWindow?.ProcessName,
                        WindowProcessId = plannedWindow?.ProcessId,
                        WindowProcessStartTimeUtc = plannedWindow?.ProcessStartTimeUtc,
                        RequiresConfirmation = true,
                        ResultSummary = plan.ConfirmationText ?? plan.UserSummary,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        await ExecutePlanAsync(
                session,
                turn,
                plan,
                plannedWindow,
                directVoiceAction,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ExecutePlanAsync(
        SessionRecord session,
        SessionTurnRecord turn,
        AssistantIntentPlanDto plan,
        ForegroundWindowSnapshot? plannedWindow,
        bool directVoiceAction,
        CancellationToken cancellationToken)
    {
        var kind = ParseIntentKind(plan.IntentKind);
        var workKind = WorkKindFor(kind);
        var operationId = $"session-action-{turn.Id:N}";
        var runningPhase = kind == UniversalIntentKind.DescribeForeground
            ? SessionTurnPhase.ObservingWindow
            : SessionTurnPhase.Executing;
        var running = await TransitionAsync(
                turn.Id,
                current => current with
                {
                    WorkKind = workKind,
                    IntentKind = plan.IntentKind,
                    PlanId = plan.PlanId,
                    Phase = runningPhase,
                    MissingContext = SessionMissingContext.None,
                    OperationId = operationId,
                    ProjectId = current.ProjectId ?? session.SelectedProjectId,
                    WindowHandle = plannedWindow?.WindowHandle ?? current.WindowHandle,
                    WindowTitle = plannedWindow?.WindowTitle ?? current.WindowTitle,
                    WindowProcessName = plannedWindow?.ProcessName ?? current.WindowProcessName,
                    WindowProcessId = plannedWindow?.ProcessId ?? current.WindowProcessId,
                    WindowProcessStartTimeUtc = plannedWindow?.ProcessStartTimeUtc
                        ?? current.WindowProcessStartTimeUtc,
                    RequiresConfirmation = plan.RequiresConfirmation,
                    FailureCode = null,
                    FailureMessage = null
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (SessionTurnPhases.IsTerminal(running.Phase) || running.Phase != runningPhase)
        {
            return;
        }

        if (IsWindowBoundTurn(running) && !MatchesPersistedWindowIdentity(running))
        {
            await RequireFreshWindowConfirmationAsync(running, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        using var progressSubscription = applicationSearchProgress.Observe((progress, reportCancellation) =>
            PublishApplicationSearchProgressAsync(running.Id, operationId, progress, cancellationToken, reportCancellation));
        using var consentSubscription = visualSearchConsent.Observe((request, requestCancellation) =>
            RequestVisualSearchConsentAsync(running.Id, operationId, plan, request,
                cancellationToken, requestCancellation));
        using var desktopTaskSubscription = desktopTaskInteractions.Observe(
            (request, requestCancellation) => RequestDesktopTaskConsentAsync(running.Id, operationId, plan,
                request, cancellationToken, requestCancellation),
            (description, reportCancellation) => PublishDesktopTaskProgressAsync(running.Id, operationId,
                description, cancellationToken, reportCancellation));
        var result = await assistantCommands.ExecuteAsync(
                new ExecuteAssistantCommandRequestDto(
                    plan.PlanId,
                    Confirmed: !directVoiceAction,
                    operationId,
                    directVoiceAction ? "ExplicitVoice" : "VisibleConfirmation"),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.TaskId is { } taskId)
        {
            await TransitionAsync(
                    running.Id,
                    current => current with
                    {
                        TaskId = taskId,
                        Phase = SessionTurnPhase.ProgrammingTask,
                        ResultSummary = result.UserSummary,
                        CompletedAtUtc = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            StartTaskMonitor(turn.Id, taskId);
            return;
        }

        await using var desktopTaskCompletionGate = plan.IntentKind == "DesktopTask"
            ? await operationGates.AcquireTurnAsync(running.Id, CancellationToken.None).ConfigureAwait(false) : null;
        if (plan.IntentKind == "DesktopTask") cancellationToken.ThrowIfCancellationRequested();
        var succeeded = string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase);
        await TransitionAsync(
                running.Id,
                current => current with
                {
                    Phase = succeeded ? SessionTurnPhase.Completed : SessionTurnPhase.Failed,
                    ResultSummary = result.UserSummary,
                    FailureCode = succeeded ? null : "action_failed",
                    FailureMessage = succeeded ? null : result.UserSummary,
                    CompletedAtUtc = timeProvider.GetUtcNow()
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task RequestVisualSearchConsentAsync(Guid turnId, string operationId,
        AssistantIntentPlanDto plan, VisualSearchConsentRequest request,
        CancellationToken operationCancellation, CancellationToken requestCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationCancellation, requestCancellation);
        PendingVisualSearchConsent pending;
        ActiveSessionWork active;
        await using (var gate = await operationGates.AcquireTurnAsync(turnId, linked.Token).ConfigureAwait(false))
        {
            linked.Token.ThrowIfCancellationRequested();
            var current = await sessionStore.GetTurnAsync(turnId, linked.Token).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException("视觉搜索缺少当前会话请求。");
            var session = await sessionStore.GetSessionAsync(current.SessionId, linked.Token).ConfigureAwait(false);
            if (session?.IsCurrent != true || current.Phase != SessionTurnPhase.Executing
                || current.CancellationRequested || current.OperationId != operationId
                || current.PlanTarget != plan.CanonicalTarget || current.IntentKind != plan.IntentKind
                || !MatchesPlannedExpectation(current)
                || !_activeWork.TryGetValue(turnId, out active!)
                || !VisualSearchQueryMatches(plan, request.Query))
                throw new UnauthorizedAccessException("视觉搜索与本次明确确认的应用、搜索内容或执行范围不一致。");
            var isResultRead = request.Purpose == VisualSearchConsentPurpose.ResultRead;
            if (isResultRead)
            {
                if (active.VisualConsent is not { Request.Purpose: VisualSearchConsentPurpose.Search } original
                    || !original.Completion.Task.IsCompletedSuccessfully
                    || original.Request.Query != request.Query
                    || !WindowIdentityContract.Matches(original.Request.Window, request.OriginWindow)
                    || !MatchesPersistedWindowIdentity(current, request.OriginWindow))
                    throw new UnauthorizedAccessException("临时窗口读取必须继续同一次已允许的搜索，且只能请求一次。");
            }
            else if (active.VisualConsent is not null)
                throw new UnauthorizedAccessException("本次搜索许可已经使用，不能重复执行搜索。");
            VerifyVisualSearchWindows(request);
            if (current.IntentKind == nameof(UniversalIntentKind.SearchForeground)
                && !MatchesPersistedWindowIdentity(current, request.OriginWindow))
                throw new WindowIdentityException(WindowIdentityErrorCodes.Changed, "本次搜索窗口已经变化。");

            pending = new(Guid.NewGuid(), request, plan.CanonicalTarget!, operationId,
                timeProvider.GetUtcNow().AddMinutes(2), current.Version + 1);
            active.VisualConsent = pending;
            var origin = request.OriginWindow;
            var summary = isResultRead
                ? $"已输入“{request.Query}”。“{origin.WindowTitle}”出现了一个直属临时窗口（{pending.DisplayName}），是否允许元枢仅在本机查看这个窗口，核对本次搜索结果？不会点击、输入、打开结果、保存截图或发送画面到云端。许可仅限这一次核对，停止、窗口变化或元枢退出后失效。"
                : $"允许元枢仅在本机查看“{origin.WindowTitle}”这个窗口，识别并点击搜索框，输入“{request.Query}”并核对结果？不会打开结果、发送消息、保存截图或发送画面到云端。许可仅限这次搜索，停止、窗口变化或元枢退出后失效。";
            var waiting = await TransitionAsync(turnId, value => value with
            {
                Phase = SessionTurnPhase.WaitingForWindowConsent,
                MissingContext = SessionMissingContext.WindowConsent,
                WindowHandle = origin.WindowHandle,
                WindowTitle = origin.WindowTitle,
                WindowProcessName = origin.ProcessName,
                WindowProcessId = origin.ProcessId,
                WindowProcessStartTimeUtc = origin.ProcessStartTimeUtc,
                RequiresConfirmation = true,
                ConfirmationGranted = false,
                ResultSummary = summary,
                FailureCode = "visual_search_consent_required",
                FailureMessage = null
            }, linked.Token).ConfigureAwait(false);
            if (waiting.Phase != SessionTurnPhase.WaitingForWindowConsent || waiting.Version != pending.TurnVersion)
                throw new OperationCanceledException(linked.Token);
            active.VisiblePause.TrySetResult();
        }

        // Keep the original execution and its already registered Command alive. No frame
        // is captured while this task waits, and cancellation unblocks it immediately.
        await pending.Completion.Task.WaitAsync(TimeSpan.FromMinutes(2), linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        var resumed = await sessionStore.GetTurnAsync(turnId, linked.Token).ConfigureAwait(false);
        if (resumed is null || resumed.Phase != SessionTurnPhase.Executing || resumed.CancellationRequested
            || resumed.OperationId != operationId || resumed.PlanTarget != pending.PlanTarget
            || !resumed.ConfirmationGranted || !ReferenceEquals(active.VisualConsent, pending))
            throw new UnauthorizedAccessException("本次视觉搜索许可已撤销。");
        VerifyVisualSearchWindows(request);
    }

    private void VerifyVisualSearchWindows(VisualSearchConsentRequest request)
    {
        WindowIdentityContract.RequireMatch(request.OriginWindow,
            foregroundWindows.ResolveWindow(request.OriginWindow.WindowHandle));
        if (request.Purpose == VisualSearchConsentPurpose.ResultRead)
        {
            if (visualSearchResultWindows is null || request.Window.WindowHandle == request.OriginWindow.WindowHandle)
                throw new UnauthorizedAccessException("没有可核对的直属临时窗口，不能读取其他窗口。");
            visualSearchResultWindows.Verify(request.OriginWindow, request.Window);
        }
        else WindowIdentityContract.RequireMatch(request.OriginWindow, request.Window);
    }

    internal SessionWindowConsentDto? GetWindowConsent(SessionTurnRecord turn)
    {
        if (GetDesktopTaskConsent(turn) is { } taskConsent) return taskConsent;
        if (turn.Phase != SessionTurnPhase.WaitingForWindowConsent
            || turn.FailureCode != "visual_search_consent_required"
            || !_activeWork.TryGetValue(turn.Id, out var active)
            || active.Cancellation.IsCancellationRequested || active.VisualConsent is not { } pending
            || turn.Version != pending.TurnVersion || turn.OperationId != pending.OperationId
            || turn.PlanTarget != pending.PlanTarget) return null;
        return new(pending.ConsentId, pending.Request.Purpose.ToString(), pending.Request.Window.WindowHandle,
            pending.DisplayName);
    }

    private static bool VisualSearchQueryMatches(AssistantIntentPlanDto plan, string query)
    {
        if (query != DesktopSearchQuery.NormalizeAndValidate(query)) return false;
        if (plan.IntentKind == nameof(UniversalIntentKind.SearchForeground))
            return string.Equals(plan.CanonicalTarget, query, StringComparison.Ordinal);
        if (plan.IntentKind != nameof(UniversalIntentKind.ApplicationSearch) || plan.CanonicalTarget is null)
            return false;
        try
        {
            using var document = JsonDocument.Parse(plan.CanonicalTarget);
            var root = document.RootElement;
            return root.GetProperty("query").GetString() == query
                && root.GetProperty("followUp").GetString() == nameof(ApplicationSearchFollowUp.None)
                && !root.TryGetProperty("latestMusicArtist", out _)
                && !root.TryGetProperty("musicChart", out _)
                && !string.IsNullOrWhiteSpace(root.GetProperty("applicationId").GetString())
                && root.GetProperty("applicationTargetBinding").GetString() is { Length: 64 };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private async Task PublishApplicationSearchProgressAsync(Guid turnId, string operationId,
        ApplicationSearchProgress progress, CancellationToken operationCancellation, CancellationToken reportCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationCancellation, reportCancellation);
        await using var gate = await operationGates.AcquireTurnAsync(turnId, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        var current = await sessionStore.GetTurnAsync(turnId, linked.Token).ConfigureAwait(false);
        if (current is null || current.Phase != SessionTurnPhase.Executing || current.CancellationRequested
            || current.IntentKind != nameof(UniversalIntentKind.ApplicationSearch)
            || current.OperationId != operationId) return;
        var session = await sessionStore.GetSessionAsync(current.SessionId, linked.Token).ConfigureAwait(false);
        if (session?.IsCurrent != true) return;
        var application = progress.ApplicationName?.Trim() ?? "软件";
        if (application.Length is < 1 or > 120 || application.Any(char.IsControl)) application = "软件";
        var message = progress.Stage switch
        {
            ApplicationSearchStage.Opening => $"正在打开{application}…",
            ApplicationSearchStage.Opened => $"已打开{application}，准备搜索…",
            ApplicationSearchStage.ResolvingMusic => "正在查找最近发行的歌曲…",
            ApplicationSearchStage.ResolvingHotMusic => "正在读取网易云热歌榜…",
            ApplicationSearchStage.PlayingMusic => "正在播放并核对歌曲…",
            ApplicationSearchStage.PreparingSearch => "正在准备搜索框…",
            ApplicationSearchStage.Searching => "正在搜索…",
            ApplicationSearchStage.VerifyingResults => "正在检查搜索结果…",
            _ => throw new ArgumentOutOfRangeException(nameof(progress))
        };
        if (current.ResultSummary == message) return;
        await TransitionAsync(turnId, turn => turn with { ResultSummary = message }, linked.Token).ConfigureAwait(false);
    }

    private async Task PreparePointerAnswerConsentAsync(
        SessionRecord session,
        SessionTurnRecord turn,
        Guid anchorId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (turn.FrozenRoute is not
                {
                    Status: SessionTurnRouteStatus.Ready,
                    ProviderId: { Length: > 0 } providerId
                })
            {
                throw new PointerAnswerException(
                    turn.FrozenRoute?.FailureCode ?? "frozen_chat_route_invalid",
                    "当前普通聊天路由不可用，指针区域文字没有发送。");
            }

            var prompt = prompts.GetRequired("window.pointer.answer", "1", providerId);
            var result = await pointerRegions.ReadAsync(anchorId, cancellationToken)
                .ConfigureAwait(false);
            var prepared = PointerAnswerPreparedConsent.Create(
                turn,
                result,
                prompt,
                timeProvider.GetUtcNow());
            var gate = await operationGates.AcquireTurnAsync(turn.Id, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var latestSession = await sessionStore.GetSessionAsync(session.Id, cancellationToken)
                    .ConfigureAwait(false);
                var latestTurn = await sessionStore.GetTurnAsync(turn.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (latestSession is null
                    || latestTurn is null
                    || !CanPublishPointerAnswerConsent(latestSession, latestTurn, prepared, anchorId))
                {
                    throw new PointerAnswerException(
                        PointerAnswerErrorCodes.ConsentStale,
                        "指针区域出站预览已失效，请重新指向并检查。");
                }

                pointerRegions.RequireCurrent(prepared.Anchor);
                _preparedPointerAnswerConsents[turn.Id] = prepared;
                try
                {
                    _ = await TransitionAsync(
                            turn.Id,
                            current => current with
                            {
                                WorkKind = SessionWorkKind.Conversation,
                                IntentKind = "Conversation",
                                Phase = SessionTurnPhase.WaitingForPointerAnswerConsent,
                                MissingContext = SessionMissingContext.None,
                                RequiresConfirmation = true,
                                ResultSummary = "请检查本次将发送的完整问题、区域文字、AI 服务和 HTTPS 目的地后再确认；不会发送图片。",
                                FailureCode = null,
                                FailureMessage = null
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    _requestedPointerAnchors.TryRemove(turn.Id, out _);
                }
                catch
                {
                    _preparedPointerAnswerConsents.TryRemove(turn.Id, out _);
                    throw;
                }
            }
            finally
            {
                await gate.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _preparedPointerAnswerConsents.TryRemove(turn.Id, out _);
            _requestedPointerAnchors.TryRemove(turn.Id, out _);
            throw;
        }
        catch (PointerRegionException exception)
        {
            await EndPointerAnswerTurnAsync(turn.Id, SessionTurnPhase.Failed, exception.Code, exception.Message)
                .ConfigureAwait(false);
        }
        catch (PointerAnswerException exception)
        {
            await EndPointerAnswerTurnAsync(turn.Id, SessionTurnPhase.Failed, exception.Code, exception.Message)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await EndPointerAnswerTurnAsync(
                    turn.Id,
                    SessionTurnPhase.Failed,
                    PointerAnswerErrorCodes.CommitFailed,
                    "指针区域出站预览未能安全生成，没有发送。")
                .ConfigureAwait(false);
        }
        finally
        {
            _requestedPointerAnchors.TryRemove(turn.Id, out _);
        }
    }

    private bool CanPublishPointerAnswerConsent(
        SessionRecord session,
        SessionTurnRecord turn,
        PointerAnswerPreparedConsent prepared,
        Guid anchorId)
    {
        if (!session.IsCurrent
            || prepared.TurnId != turn.Id
            || prepared.TurnVersion != turn.Version
            || prepared.Anchor.AnchorId != anchorId
            || turn.Phase != SessionTurnPhase.Understanding
            || turn.CancellationRequested
            || !string.Equals(prepared.Question, turn.InputText, StringComparison.Ordinal)
            || !_requestedPointerAnchors.TryGetValue(turn.Id, out var requested)
            || requested != anchorId)
        {
            return false;
        }

        return PointerAnswerRouteAndPromptMatch(turn, prepared);
    }

    private bool PointerAnswerRouteAndPromptMatch(
        SessionTurnRecord turn,
        PointerAnswerPreparedConsent prepared)
    {
        if (turn.FrozenRoute is not
            {
                Status: SessionTurnRouteStatus.Ready,
                ProviderId: { Length: > 0 } providerId,
                ModelId: { Length: > 0 } modelId,
                DataDestination: { Length: > 0 } destination
            }
            || !string.Equals(providerId, prepared.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(modelId, prepared.ModelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(PointerAnswerOutboundContract.HashText(turn.InputText), prepared.QuestionHash, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var prompt = prompts.GetRequired("window.pointer.answer", "1", providerId);
            return string.Equals(
                    PointerAnswerOutboundContract.NormalizeHttpsOrigin(destination),
                    prepared.DestinationOrigin,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(prompt.PromptId, prepared.PromptId, StringComparison.Ordinal)
                && string.Equals(prompt.Version, prepared.PromptVersion, StringComparison.Ordinal)
                && string.Equals(prompt.ContentSha256, prepared.PromptContentHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task PrepareMemoryOutboundConsentAsync(
        SessionRecord session,
        SessionTurnRecord turn,
        IReadOnlyList<MemoryOutboundItemReference> selection,
        CancellationToken cancellationToken)
    {
        try
        {
            if (turn.FrozenRoute is not
                {
                    Status: SessionTurnRouteStatus.Ready,
                    ProviderId: { Length: > 0 } providerId,
                    ModelId: { Length: > 0 } modelId,
                    DataDestination: { Length: > 0 } destination
                })
            {
                await TransitionAsync(
                        turn.Id,
                        current => current with
                        {
                            WorkKind = SessionWorkKind.Conversation,
                            Phase = SessionTurnPhase.Failed,
                            MemoryOutboundState = MemoryOutboundConsentState.Invalidated,
                            FailureCode = turn.FrozenRoute?.FailureCode ?? "frozen_chat_route_invalid",
                            FailureMessage = "当前普通聊天路由不可用，所选记忆没有发送。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            var prompt = prompts.GetRequired("chat.general", RoutedConversationProvider.ChatPromptVersion, providerId);
            var request = new MemoryOutboundPreparationRequest(
                projections.CoordinatorInstanceGuid,
                session.Id,
                turn.Id,
                turn.Version,
                providerId,
                modelId,
                destination,
                prompt.PromptId,
                prompt.Version,
                prompt.ContentSha256,
                turn.ProjectId ?? session.SelectedProjectId,
                selection);
            var prepared = await memories.PrepareOutboundAsync(request, cancellationToken)
                .ConfigureAwait(false);
            foreach (var observer in memoryConsentPublicationObservers)
            {
                await observer.BeforePublishAsync(turn.Id, prepared.ConsentId, cancellationToken)
                    .ConfigureAwait(false);
            }

            var gate = await operationGates.AcquireTurnAsync(turn.Id, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var latestSession = await sessionStore.GetSessionAsync(session.Id, cancellationToken)
                    .ConfigureAwait(false);
                var latestTurn = await sessionStore.GetTurnAsync(turn.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (latestSession is null
                    || latestTurn is null
                    || !CanPublishPreparedConsent(latestSession, latestTurn, prepared, selection))
                {
                    throw new MemoryServiceException(
                        MemoryOutboundErrorCodes.ConsentStale,
                        "记忆出站确认已失效，请重新发送并检查。");
                }

                _preparedMemoryConsents[turn.Id] = prepared;
                try
                {
                    _ = await TransitionAsync(
                            turn.Id,
                            current => current with
                            {
                                WorkKind = SessionWorkKind.Conversation,
                                IntentKind = "Conversation",
                                Phase = SessionTurnPhase.WaitingForMemoryOutboundConsent,
                                MissingContext = SessionMissingContext.None,
                                MemoryOutboundState = MemoryOutboundConsentState.WaitingForMemoryOutboundConsent,
                                MemoryOutbound = prepared.ToAuditMetadata(),
                                ResultSummary = "请检查本次将发送的完整记忆内容、AI 服务和目的地后再确认。",
                                FailureCode = null,
                                FailureMessage = null
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    _requestedMemorySelections.TryRemove(turn.Id, out _);
                }
                catch
                {
                    _preparedMemoryConsents.TryRemove(turn.Id, out _);
                    throw;
                }
            }
            finally
            {
                await gate.DisposeAsync();
            }

            foreach (var observer in memoryConsentPublicationObservers)
            {
                await observer.AfterPublishAsync(turn.Id, prepared.ConsentId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _preparedMemoryConsents.TryRemove(turn.Id, out _);
            _requestedMemorySelections.TryRemove(turn.Id, out _);
            throw;
        }
        catch (MemoryServiceException exception)
        {
            _preparedMemoryConsents.TryRemove(turn.Id, out _);
            _requestedMemorySelections.TryRemove(turn.Id, out _);
            await TransitionAsync(
                    turn.Id,
                    current => current with
                    {
                        WorkKind = SessionWorkKind.Conversation,
                        Phase = SessionTurnPhase.Failed,
                        MemoryOutboundState = MemoryOutboundConsentState.Invalidated,
                        FailureCode = exception.Code,
                        FailureMessage = exception.Message,
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private bool CanPublishPreparedConsent(
        SessionRecord session,
        SessionTurnRecord turn,
        MemoryOutboundPreparedConsent prepared,
        IReadOnlyList<MemoryOutboundItemReference> selection)
    {
        if (!session.IsCurrent
            || prepared.CoordinatorInstanceId != projections.CoordinatorInstanceGuid
            || prepared.SessionId != session.Id
            || prepared.TurnId != turn.Id
            || prepared.TurnVersion != turn.Version
            || turn.Phase != SessionTurnPhase.Understanding
            || SessionTurnPhases.IsTerminal(turn.Phase)
            || turn.CancellationRequested
            || turn.MemoryOutboundState != MemoryOutboundConsentState.None
            || turn.MemoryOutbound is not null
            || prepared.ProjectId != (turn.ProjectId ?? session.SelectedProjectId)
            || !MemorySelectionMatches(turn, selection)
            || prepared.Items.Count != selection.Count
            || prepared.Items.Zip(selection).Any(pair =>
                pair.First.Id != pair.Second.MemoryId
                || pair.First.Version != pair.Second.ExpectedVersion))
        {
            return false;
        }

        if (turn.FrozenRoute is not
            {
                Status: SessionTurnRouteStatus.Ready,
                ProviderId: { Length: > 0 } providerId,
                ModelId: { Length: > 0 } modelId,
                DataDestination: { Length: > 0 } destination
            }
            || !string.Equals(providerId, prepared.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(modelId, prepared.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var prompt = prompts.GetRequired("chat.general", RoutedConversationProvider.ChatPromptVersion, providerId);
            return string.Equals(
                    MemoryOutboundContract.NormalizeHttpsOrigin(destination),
                    prepared.DestinationOrigin,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(prompt.PromptId, prepared.PromptId, StringComparison.Ordinal)
                && string.Equals(prompt.Version, prepared.PromptVersion, StringComparison.Ordinal)
                && string.Equals(
                    prompt.ContentSha256,
                    prepared.PromptContentHash,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task CommitAndRunMemoryConversationAsync(
        SessionRecord session,
        SessionTurnRecord committing,
        MemoryOutboundPreparedConsent prepared,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await memories.CommitOutboundAsync(prepared, cancellationToken)
                .ConfigureAwait(false);
            _preparedMemoryConsents.TryRemove(committing.Id, out _);
            _requestedMemorySelections.TryRemove(committing.Id, out _);
            var consumed = await TransitionAsync(
                    committing.Id,
                    current => current with
                    {
                        MemoryOutboundState = MemoryOutboundConsentState.Consumed,
                        MemoryOutbound = envelope.Audit,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            await RunConversationTurnAsync(
                    session,
                    consumed,
                    envelope,
                    pointerAnswer: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await EndMemoryTurnAsync(
                    committing.Id,
                    SessionTurnPhase.Cancelled,
                    MemoryOutboundConsentState.Cancelled,
                    MemoryOutboundErrorCodes.Cancelled,
                    "记忆出站已取消，没有复用本次确认。")
                .ConfigureAwait(false);
        }
        catch (MemoryServiceException exception)
        {
            await EndMemoryTurnAsync(
                    committing.Id,
                    SessionTurnPhase.Failed,
                    MemoryOutboundConsentState.Invalidated,
                    exception.Code,
                    exception.Message)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await EndMemoryTurnAsync(
                    committing.Id,
                    SessionTurnPhase.Failed,
                    MemoryOutboundConsentState.Invalidated,
                    MemoryOutboundErrorCodes.CommitFailed,
                    "记忆出站提交未能安全完成，没有发送。")
                .ConfigureAwait(false);
        }
        finally
        {
            _preparedMemoryConsents.TryRemove(committing.Id, out _);
            _requestedMemorySelections.TryRemove(committing.Id, out _);
            RemoveActiveWork(committing.Id);
            PublishChange();
        }
    }

    private async Task EndMemoryTurnAsync(
        Guid turnId,
        SessionTurnPhase phase,
        MemoryOutboundConsentState consentState,
        string code,
        string message)
    {
        try
        {
            await TransitionAsync(
                    turnId,
                    current => current with
                    {
                        Phase = phase,
                        MemoryOutboundState = consentState,
                        CancellationRequested = phase == SessionTurnPhase.Cancelled,
                        FailureCode = code,
                        FailureMessage = message,
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task RunConversationTurnAsync(
        SessionRecord session,
        SessionTurnRecord sessionTurn,
        MemoryOutboundEnvelope? memoryOutbound,
        PointerAnswerEnvelope? pointerAnswer,
        CancellationToken cancellationToken)
    {
        try
        {
            var responding = await TransitionAsync(
                    sessionTurn.Id,
                    turn => turn with
                    {
                        WorkKind = SessionWorkKind.Conversation,
                        IntentKind = "Conversation",
                        Phase = SessionTurnPhase.Responding,
                        MissingContext = SessionMissingContext.None
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            var sent = await conversations.SendSessionAsync(
                    session.ConversationId,
                    responding.Id,
                    responding.FrozenRoute,
                    responding.InputText,
                    $"session-conversation-{responding.Id:N}",
                    memoryOutbound,
                    pointerAnswer,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await TransitionAsync(
                    responding.Id,
                    turn => turn with { ConversationTurnId = sent.TurnId },
                    CancellationToken.None)
                .ConfigureAwait(false);
            await conversations.WaitForTurnAsync(sent.TurnId, CancellationToken.None).ConfigureAwait(false);
            var details = await conversations.GetDetailsAsync(session.ConversationId, CancellationToken.None)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("会话已经不存在。");
            var conversationTurn = details.Turns.Single(turn => turn.Id == sent.TurnId);
            var assistant = conversationTurn.AssistantMessageId is { } assistantId
                ? details.Messages.SingleOrDefault(message => message.Id == assistantId)
                : null;
            var phase = conversationTurn.Status switch
            {
                ConversationTurnStatus.Succeeded => SessionTurnPhase.Completed,
                ConversationTurnStatus.Cancelled => SessionTurnPhase.Cancelled,
                ConversationTurnStatus.Interrupted => SessionTurnPhase.Interrupted,
                _ => SessionTurnPhase.Failed
            };
            await TransitionAsync(
                    responding.Id,
                    turn => turn with
                    {
                        Phase = phase,
                        CancellationRequested = phase == SessionTurnPhase.Cancelled,
                        ResultSummary = assistant?.Content,
                        FailureCode = conversationTurn.FailureCode,
                        FailureMessage = conversationTurn.FailureMessage,
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryEndTurnAsync(
                    sessionTurn.Id,
                    SessionTurnPhase.Cancelled,
                    "cancelled",
                    "上一条回答已停止。")
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryEndTurnAsync(
                    sessionTurn.Id,
                    SessionTurnPhase.Failed,
                    "session_conversation_failed",
                    FriendlyException(exception))
                .ConfigureAwait(false);
        }
    }

    private async Task CancelForegroundWorkAsync(
        SessionRecord session,
        string reason,
        Guid? excludedTurnId,
        CancellationToken cancellationToken)
    {
        var turns = await sessionStore.GetActiveTurnsAsync(session.Id, cancellationToken)
            .ConfigureAwait(false);
        foreach (var turn in turns.Where(item =>
                     item.Id != excludedTurnId && SessionTurnPhases.IsForegroundWork(item.Phase)))
        {
            var gate = await operationGates.AcquireTurnAsync(turn.Id, cancellationToken)
                .ConfigureAwait(false);
            ActiveSessionWork? active = null;
            SessionTurnRecord? latest = null;
            SessionTurnPhase phaseAtCancellation = default;
            string? operationIdAtCancellation = null;
            try
            {
                _preparedMemoryConsents.TryRemove(turn.Id, out _);
                _requestedMemorySelections.TryRemove(turn.Id, out _);
                _preparedPointerAnswerConsents.TryRemove(turn.Id, out _);
                _requestedPointerAnchors.TryRemove(turn.Id, out _);
                latest = await sessionStore.GetTurnAsync(turn.Id, cancellationToken).ConfigureAwait(false);
                if (latest is null
                    || SessionTurnPhases.IsTerminal(latest.Phase)
                    || !SessionTurnPhases.IsForegroundWork(latest.Phase))
                {
                    continue;
                }

                phaseAtCancellation = latest.Phase;
                operationIdAtCancellation = latest.OperationId;
                if (_activeWork.TryGetValue(latest.Id, out active))
                {
                    if (latest.Phase != SessionTurnPhase.Responding)
                    {
                        // Confirm/consent and replacement share this Turn gate. Whichever
                        // wins first either never starts, or receives cancellation here.
                        CancelActiveWork(active);
                    }
                }
                else
                {
                    latest = await TransitionAsync(
                            latest.Id,
                            current => current with
                            {
                                Phase = SessionTurnPhase.Cancelled,
                                MissingContext = SessionMissingContext.None,
                                CancellationRequested = true,
                                FailureCode = "replaced_by_new_input",
                                FailureMessage = reason,
                                CompletedAtUtc = timeProvider.GetUtcNow()
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                await gate.DisposeAsync();
            }

            if (active is not null)
            {
                if (phaseAtCancellation == SessionTurnPhase.Responding)
                {
                    try
                    {
                        await conversations.CancelAsync(session.ConversationId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        CancelActiveWork(active);
                    }
                }

                if (!string.IsNullOrWhiteSpace(operationIdAtCancellation))
                {
                    assistantCommands.CancelWindowObservation(operationIdAtCancellation);
                }

                await active.Completion.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken)
                    .ConfigureAwait(false);
            }

            latest = await sessionStore.GetTurnAsync(turn.Id, cancellationToken).ConfigureAwait(false);
            if (latest is not null && !SessionTurnPhases.IsTerminal(latest.Phase))
            {
                await TryEndTurnAsync(
                        latest.Id,
                        SessionTurnPhase.Cancelled,
                        "replaced_by_new_input",
                        reason)
                    .ConfigureAwait(false);
            }
        }
    }

    private void StartTaskMonitor(Guid turnId, Guid taskId)
    {
        var monitor = new ActiveTaskMonitor(turnId, taskId);
        if (!_taskMonitors.TryAdd(turnId, monitor))
        {
            monitor.Cancellation.Dispose();
            return;
        }

        monitor.SetCompletion(Task.Run(
            () => MonitorTaskAsync(monitor),
            CancellationToken.None));
    }

    private async Task MonitorTaskAsync(ActiveTaskMonitor monitor)
    {
        try
        {
            await taskStateSynchronizer.RunAsync(
                    monitor.TurnId,
                    monitor.TaskId,
                    (update, _) => ApplyTaskStateAsync(monitor, update),
                    monitor.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (monitor.Cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _taskMonitors.TryRemove(monitor.TurnId, out _);
            monitor.Cancellation.Dispose();
        }
    }

    private async Task ApplyTaskStateAsync(
        ActiveTaskMonitor monitor,
        SessionTaskStateUpdate update)
    {
        if (update.TaskId != monitor.TaskId)
        {
            return;
        }

        var current = await sessionStore.GetTurnAsync(monitor.TurnId, CancellationToken.None)
            .ConfigureAwait(false);
        if (current is null
            || current.TaskId != monitor.TaskId
            || SessionTurnPhases.IsTerminal(current.Phase))
        {
            return;
        }

        var phase = Enum.Parse<SessionTurnPhase>(update.Phase, ignoreCase: false);
        if (SessionTurnPhases.IsTerminal(phase) && !update.Finalized)
        {
            return;
        }

        if (current.Phase == phase
            && string.Equals(current.ResultSummary, update.ResultSummary, StringComparison.Ordinal)
            && string.Equals(current.FailureCode, update.FailureCode, StringComparison.Ordinal))
        {
            return;
        }

        await TransitionAsync(
                current.Id,
                turn => turn with
                {
                    Phase = phase,
                    MissingContext = phase == SessionTurnPhase.WaitingForUser
                        ? SessionMissingContext.UserInput
                        : SessionMissingContext.None,
                    ResultSummary = update.ResultSummary ?? turn.ResultSummary,
                    FailureCode = update.FailureCode,
                    FailureMessage = update.FailureMessage,
                    CompletedAtUtc = SessionTurnPhases.IsTerminal(phase)
                        ? update.CompletedAtUtc ?? timeProvider.GetUtcNow()
                        : null
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<SessionRecord> RequireSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await sessionStore.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("会话已经不存在。 ");

    private async Task<SessionTurnRecord> RequireTurnAsync(
        SessionRecord session,
        Guid turnId,
        CancellationToken cancellationToken)
    {
        var turn = await sessionStore.GetTurnAsync(turnId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("会话请求已经不存在。 ");
        if (turn.SessionId != session.Id)
        {
            throw new UnauthorizedAccessException("不能为另一个会话补充信息或授权。 ");
        }

        return turn;
    }

    private async Task<(SessionRecord Session, SessionTurnRecord Turn)> RequireWaitingTurnAsync(
        Guid sessionId,
        Guid turnId,
        SessionTurnPhase requiredPhase,
        CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var turn = await RequireTurnAsync(session, turnId, cancellationToken).ConfigureAwait(false);
        if (turn.Phase != requiredPhase)
        {
            throw new InvalidOperationException("这条请求当前不接受这项补充或确认。 ");
        }

        return (session, turn);
    }

    private static UniversalIntentKind ParseIntentKind(string value) =>
        Enum.TryParse<UniversalIntentKind>(value, ignoreCase: false, out var kind)
            ? kind
            : UniversalIntentKind.Unsupported;

    private static UniversalIntentKind? TryParsePersistedIntent(string? value) =>
        Enum.TryParse<UniversalIntentKind>(value, ignoreCase: false, out var kind)
        && kind != UniversalIntentKind.Unsupported
            ? kind
            : null;

    private FrozenChatModelRoute? RestoreReadyRoute(SessionTurnFrozenRoute? frozenRoute)
    {
        if (frozenRoute?.Status != SessionTurnRouteStatus.Ready
            || string.IsNullOrWhiteSpace(frozenRoute.ProviderId)
            || string.IsNullOrWhiteSpace(frozenRoute.ModelId)
            || string.IsNullOrWhiteSpace(frozenRoute.DataDestination)
            || frozenRoute.SendsDataOffDevice is null)
        {
            return null;
        }

        try
        {
            return modelRouter.RestoreFrozenChatRoute(
                frozenRoute.ProviderId,
                frozenRoute.ModelId,
                frozenRoute.DataDestination,
                frozenRoute.SendsDataOffDevice.Value);
        }
        catch (ChatModelException exception) when (
            string.Equals(exception.Error.Code, "frozen_chat_route_invalid", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task<LocalSessionSnapshot> ConfirmPointerAnswerAsync(
        Guid sessionId,
        Guid turnId,
        Guid consentId,
        string previewHash,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var gate = await operationGates.AcquireTurnAsync(turnId, cancellationToken)
            .ConfigureAwait(false);
        SessionRecord session;
        try
        {
            (SessionRecord Session, SessionTurnRecord Turn) required;
            try
            {
                required = await RequireWaitingTurnAsync(
                        sessionId,
                        turnId,
                        SessionTurnPhase.WaitingForPointerAnswerConsent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                throw new PointerAnswerException(
                    PointerAnswerErrorCodes.ConsentStale,
                    "指针区域出站确认已失效，不能重复使用。",
                    exception);
            }

            session = required.Session;
            if (!_preparedPointerAnswerConsents.TryGetValue(turnId, out var prepared)
                || consentId == Guid.Empty
                || prepared.ConsentId != consentId
                || !string.Equals(prepared.PreviewHash, previewHash, StringComparison.Ordinal)
                || required.Turn.Version != prepared.TurnVersion + 1
                || required.Turn.Phase != SessionTurnPhase.WaitingForPointerAnswerConsent
                || !PointerAnswerRouteAndPromptMatch(required.Turn, prepared)
                || timeProvider.GetUtcNow() < prepared.PreparedAtUtc
                || timeProvider.GetUtcNow() >= prepared.ExpiresAtUtc)
            {
                await InvalidatePointerAnswerAsync(turnId, PointerAnswerErrorCodes.ConsentStale)
                    .ConfigureAwait(false);
                return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (confirmed) pointerRegions.RequireFrozenTextTargetCurrent(prepared.Anchor);
            }
            catch (PointerRegionException)
            {
                await InvalidatePointerAnswerAsync(turnId, PointerAnswerErrorCodes.ConsentStale)
                    .ConfigureAwait(false);
                return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
            }

            if (!confirmed)
            {
                _preparedPointerAnswerConsents.TryRemove(turnId, out _);
                _requestedPointerAnchors.TryRemove(turnId, out _);
                await TransitionAsync(
                        turnId,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Cancelled,
                            MissingContext = SessionMissingContext.None,
                            RequiresConfirmation = false,
                            CancellationRequested = true,
                            FailureCode = PointerAnswerErrorCodes.Cancelled,
                            FailureMessage = "你未同意发送区域文字，本次请求已取消。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
            }

            var committing = await TransitionAsync(
                    turnId,
                    current => current with
                    {
                        Phase = SessionTurnPhase.Understanding,
                        RequiresConfirmation = false,
                        ConfirmationGranted = true,
                        MissingContext = SessionMissingContext.None,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            _preparedPointerAnswerConsents.TryRemove(turnId, out _);
            _requestedPointerAnchors.TryRemove(turnId, out _);
            var active = new ActiveSessionWork(turnId);
            if (!_activeWork.TryAdd(turnId, active))
            {
                active.Cancellation.Dispose();
                throw new PointerAnswerException(
                    PointerAnswerErrorCodes.ConsentStale,
                    "这次确认已经被使用，不能重复发送。");
            }

            active.SetCompletion(Task.Run(
                () => CommitAndRunPointerAnswerAsync(
                    session,
                    committing,
                    prepared,
                    active.Cancellation.Token),
                CancellationToken.None));
            PublishChange();
        }
        finally
        {
            await gate.DisposeAsync();
        }

        return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task CommitAndRunPointerAnswerAsync(
        SessionRecord session,
        SessionTurnRecord committing,
        PointerAnswerPreparedConsent prepared,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = await sessionStore.GetTurnAsync(committing.Id, CancellationToken.None)
                .ConfigureAwait(false)
                ?? throw new PointerAnswerException(
                    PointerAnswerErrorCodes.ConsentStale,
                    "指针区域出站确认已失效。");
            if (SessionTurnPhases.IsTerminal(latest.Phase)
                || latest.CancellationRequested
                || !latest.ConfirmationGranted
                || !PointerAnswerRouteAndPromptMatch(latest, prepared)
                || timeProvider.GetUtcNow() < prepared.PreparedAtUtc
                || timeProvider.GetUtcNow() >= prepared.ExpiresAtUtc)
            {
                throw new PointerAnswerException(
                    PointerAnswerErrorCodes.ConsentStale,
                    "指针区域出站确认已失效，没有发送。");
            }

            pointerRegions.RequireFrozenTextTargetCurrent(prepared.Anchor);
            var envelope = prepared.CreateEnvelope(timeProvider.GetUtcNow());
            await RunConversationTurnAsync(
                    session,
                    latest,
                    memoryOutbound: null,
                    pointerAnswer: envelope,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await EndPointerAnswerTurnAsync(
                    committing.Id,
                    SessionTurnPhase.Cancelled,
                    PointerAnswerErrorCodes.Cancelled,
                    "指针区域出站已取消，没有复用本次确认。")
                .ConfigureAwait(false);
        }
        catch (PointerRegionException)
        {
            await EndPointerAnswerTurnAsync(
                    committing.Id,
                    SessionTurnPhase.Failed,
                    PointerAnswerErrorCodes.ConsentStale,
                    "指针或目标窗口已变化，区域文字没有发送。")
                .ConfigureAwait(false);
        }
        catch (PointerAnswerException exception)
        {
            await EndPointerAnswerTurnAsync(
                    committing.Id,
                    SessionTurnPhase.Failed,
                    exception.Code,
                    exception.Message)
                .ConfigureAwait(false);
        }
        catch
        {
            await EndPointerAnswerTurnAsync(
                    committing.Id,
                    SessionTurnPhase.Failed,
                    PointerAnswerErrorCodes.CommitFailed,
                    "指针区域出站提交未能安全完成，没有发送。")
                .ConfigureAwait(false);
        }
        finally
        {
            _preparedPointerAnswerConsents.TryRemove(committing.Id, out _);
            _requestedPointerAnchors.TryRemove(committing.Id, out _);
            RemoveActiveWork(committing.Id);
            PublishChange();
        }
    }

    private async Task InvalidatePointerAnswerAsync(Guid turnId, string code)
    {
        _preparedPointerAnswerConsents.TryRemove(turnId, out _);
        _requestedPointerAnchors.TryRemove(turnId, out _);
        await EndPointerAnswerTurnAsync(
                turnId,
                SessionTurnPhase.Failed,
                code,
                "指针区域出站确认已失效，请重新指向并检查。")
            .ConfigureAwait(false);
    }

    private async Task EndPointerAnswerTurnAsync(
        Guid turnId,
        SessionTurnPhase phase,
        string code,
        string message)
    {
        _preparedPointerAnswerConsents.TryRemove(turnId, out _);
        _requestedPointerAnchors.TryRemove(turnId, out _);
        try
        {
            await TransitionAsync(
                    turnId,
                    current => current with
                    {
                        Phase = phase,
                        RequiresConfirmation = false,
                        CancellationRequested = phase == SessionTurnPhase.Cancelled,
                        FailureCode = code,
                        FailureMessage = message,
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public async Task<LocalSessionSnapshot> ConfirmMemoryOutboundAsync(
        Guid sessionId,
        Guid turnId,
        Guid consentId,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        RequireStartedHost();
        var gate = await operationGates.AcquireTurnAsync(turnId, cancellationToken)
            .ConfigureAwait(false);
        SessionRecord session;
        try
        {
            (SessionRecord Session, SessionTurnRecord Turn) required;
            try
            {
                required = await RequireWaitingTurnAsync(
                        sessionId,
                        turnId,
                        SessionTurnPhase.WaitingForMemoryOutboundConsent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                throw new MemoryServiceException(
                    MemoryOutboundErrorCodes.ConsentStale,
                    "记忆出站确认已失效，不能重复使用。",
                    exception);
            }
            session = required.Session;
            if (!_preparedMemoryConsents.TryGetValue(turnId, out var prepared)
                || consentId == Guid.Empty
                || prepared.ConsentId != consentId
                || !PreparedConsentMatches(required.Session, required.Turn, prepared))
            {
                _preparedMemoryConsents.TryRemove(turnId, out _);
                await TransitionAsync(
                        turnId,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Failed,
                            MissingContext = SessionMissingContext.None,
                            MemoryOutboundState = MemoryOutboundConsentState.Invalidated,
                            FailureCode = MemoryOutboundErrorCodes.ConsentStale,
                            FailureMessage = "记忆出站确认已失效，请重新发送并检查。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
            }

            if (!confirmed)
            {
                _preparedMemoryConsents.TryRemove(turnId, out _);
                _requestedMemorySelections.TryRemove(turnId, out _);
                _preparedPointerAnswerConsents.TryRemove(turnId, out _);
                _requestedPointerAnchors.TryRemove(turnId, out _);
                await TransitionAsync(
                        turnId,
                        current => current with
                        {
                            Phase = SessionTurnPhase.Cancelled,
                            MissingContext = SessionMissingContext.None,
                            MemoryOutboundState = MemoryOutboundConsentState.Declined,
                            CancellationRequested = true,
                            FailureCode = MemoryOutboundErrorCodes.Cancelled,
                            FailureMessage = "你未同意发送所选记忆，本次请求已取消。",
                            CompletedAtUtc = timeProvider.GetUtcNow()
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
            }

            var committing = await TransitionAsync(
                    turnId,
                    current => current with
                    {
                        Phase = SessionTurnPhase.Understanding,
                        MemoryOutboundState = MemoryOutboundConsentState.Committing,
                        MissingContext = SessionMissingContext.None,
                        FailureCode = null,
                        FailureMessage = null
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            _preparedMemoryConsents.TryRemove(turnId, out _);
            _requestedMemorySelections.TryRemove(turnId, out _);
            var active = new ActiveSessionWork(turnId);
            if (!_activeWork.TryAdd(turnId, active))
            {
                active.Cancellation.Dispose();
                throw new InvalidOperationException("同一个会话请求已经在处理中。");
            }

            active.SetCompletion(Task.Run(
                () => CommitAndRunMemoryConversationAsync(
                    session,
                    committing,
                    prepared,
                    active.Cancellation.Token),
                CancellationToken.None));
            PublishChange();
        }
        finally
        {
            await gate.DisposeAsync();
        }

        return await BuildSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private static SessionWorkKind WorkKindFor(UniversalIntentKind kind) => kind switch
    {
        UniversalIntentKind.Conversation => SessionWorkKind.Conversation,
        UniversalIntentKind.CodingTask => SessionWorkKind.CodingTask,
        UniversalIntentKind.DescribeForeground => SessionWorkKind.WindowObservation,
        UniversalIntentKind.Unsupported => SessionWorkKind.Unknown,
        _ => SessionWorkKind.DesktopAction
    };

    private static (SessionTurnPhase Phase, SessionMissingContext Missing) MissingContextState(
        UniversalIntentKind kind,
        AssistantIntentPlanDto plan) => kind switch
        {
            UniversalIntentKind.CodingTask =>
                (SessionTurnPhase.WaitingForProject, SessionMissingContext.Project),
            UniversalIntentKind.OpenFile =>
                (SessionTurnPhase.WaitingForFile, SessionMissingContext.File),
            UniversalIntentKind.DescribeForeground when plan.ForegroundApplication is not null =>
                (SessionTurnPhase.WaitingForWindowConsent, SessionMissingContext.WindowConsent),
            UniversalIntentKind.DescribeForeground or UniversalIntentKind.SearchForeground or UniversalIntentKind.DesktopTask =>
                (SessionTurnPhase.WaitingForWindow, SessionMissingContext.Window),
            _ => (SessionTurnPhase.Failed, SessionMissingContext.None)
        };

    private static bool IsDirectVoiceAction(UniversalIntentKind kind, string inputModality) =>
        string.Equals(inputModality, "Voice", StringComparison.OrdinalIgnoreCase)
        && kind is UniversalIntentKind.OpenApplication
            or UniversalIntentKind.OpenWebsite
            or UniversalIntentKind.SearchForeground
            or UniversalIntentKind.ApplicationSearch;

    private void RemoveActiveWork(Guid turnId)
    {
        if (_activeWork.TryRemove(turnId, out var removed))
        {
            removed.Cancellation.Dispose();
        }
    }

    private static void CancelActiveWork(ActiveSessionWork active)
    {
        try
        {
            active.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The work reached a terminal state between lookup and cancellation.
        }
    }

    private async Task<SessionTurnRecord> TransitionAsync(
        Guid turnId,
        Func<SessionTurnRecord, SessionTurnRecord> transition,
        CancellationToken cancellationToken)
    {
        var current = await sessionStore.GetTurnAsync(turnId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("会话请求不存在。");
        if (SessionTurnPhases.IsTerminal(current.Phase))
        {
            return current;
        }

        var updated = await sessionStore.UpdateTurnAsync(
                transition(current),
                current.Version,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        PublishChange(updated.SessionId, updated);
        if (RequiresConsentProjectionReset(current, updated))
        {
            PublishChange(updated.SessionId);
        }

        return updated;
    }

    private static bool RequiresConsentProjectionReset(
        SessionTurnRecord before,
        SessionTurnRecord after) =>
        before.MemoryOutboundState != after.MemoryOutboundState
        || before.Phase == SessionTurnPhase.WaitingForMemoryOutboundConsent
        || after.Phase == SessionTurnPhase.WaitingForMemoryOutboundConsent
        || before.Phase == SessionTurnPhase.WaitingForPointerAnswerConsent
        || after.Phase == SessionTurnPhase.WaitingForPointerAnswerConsent;

    private async Task TryEndTurnAsync(
        Guid turnId,
        SessionTurnPhase phase,
        string failureCode,
        string failureMessage)
    {
        try
        {
            await TransitionAsync(
                    turnId,
                    turn => turn with
                    {
                        Phase = phase,
                        CancellationRequested = phase == SessionTurnPhase.Cancelled,
                        FailureCode = failureCode,
                        FailureMessage = failureMessage,
                        ResultSummary = phase == SessionTurnPhase.Failed
                            && turn.IntentKind is nameof(UniversalIntentKind.ApplicationSearch) or nameof(UniversalIntentKind.OpenApplication)
                                ? failureMessage
                                : turn.ResultSummary,
                        CompletedAtUtc = timeProvider.GetUtcNow()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (SessionTurnPhases.IsTerminal(phase))
            {
                _preparedMemoryConsents.TryRemove(turnId, out _);
                _requestedMemorySelections.TryRemove(turnId, out _);
                _preparedPointerAnswerConsents.TryRemove(turnId, out _);
                _requestedPointerAnchors.TryRemove(turnId, out _);
            }
        }
        catch
        {
        }
    }

    private async Task<LocalSessionSnapshot> BuildSnapshotAsync(
        SessionRecord session,
        CancellationToken cancellationToken) =>
        await projections.BuildSnapshotAsync(
                session,
                _preparedMemoryConsents.Values.ToArray(),
                _preparedPointerAnswerConsents.Values.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

    private DesktopHostSnapshot RequireStartedHost()
    {
        var snapshot = hostState.Snapshot;
        return snapshot.IsStarted && snapshot.LocalDevice is not null
            ? snapshot
            : throw new InvalidOperationException("Desktop Host 尚未完成启动。");
    }

    private void PublishChange(Guid? sessionId = null, SessionTurnRecord? turnUpsert = null) =>
        projections.RecordChange(sessionId, turnUpsert);

    private static string NormalizeInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("请输入想让元枢处理的内容。", nameof(text));
        }

        var value = text.Trim();
        if (value.Length > MaximumInputLength)
        {
            throw new ArgumentException($"单条消息不能超过 {MaximumInputLength} 个字符。", nameof(text));
        }

        return value;
    }

    private static IReadOnlyList<MemoryOutboundItemReference> NormalizeMemorySelection(
        IReadOnlyList<MemoryOutboundItemReference>? items)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        if (items.Count > MemoryOutboundLimits.MaximumItems
            || items.Select(item => item.MemoryId).Distinct().Count() != items.Count)
        {
            throw new ArgumentException("每个 Turn 只能有序选择 1 到 8 条不同的长期记忆。", nameof(items));
        }

        return items.Select(item => new MemoryOutboundItemReference(
            item.MemoryId,
            item.ExpectedVersion)).ToArray();
    }

    private bool MemorySelectionMatches(
        SessionTurnRecord turn,
        IReadOnlyList<MemoryOutboundItemReference> requested)
    {
        var existing = _requestedMemorySelections.GetValueOrDefault(turn.Id)
            ?? turn.MemoryOutbound?.Items
            ?? [];
        return existing.Count == requested.Count
            && existing.Zip(requested).All(pair =>
                pair.First.MemoryId == pair.Second.MemoryId
                && pair.First.ExpectedVersion == pair.Second.ExpectedVersion);
    }

    private bool PointerAnchorMatches(SessionTurnRecord turn, Guid? requestedAnchorId)
    {
        Guid? existing = _requestedPointerAnchors.TryGetValue(turn.Id, out var requested)
            ? requested
            : _preparedPointerAnswerConsents.GetValueOrDefault(turn.Id)?.Anchor.AnchorId;
        return existing == requestedAnchorId;
    }

    private bool PreparedConsentMatches(
        SessionRecord session,
        SessionTurnRecord turn,
        MemoryOutboundPreparedConsent prepared)
    {
        if (!string.Equals(
                prepared.CoordinatorInstanceId.ToString("N"),
                projections.CoordinatorInstanceId,
                StringComparison.OrdinalIgnoreCase)
            || prepared.SessionId != session.Id
            || prepared.TurnId != turn.Id
            || turn.Version != prepared.TurnVersion + 1
            || prepared.ProjectId != (turn.ProjectId ?? session.SelectedProjectId)
            || turn.MemoryOutbound?.ConsentId != prepared.ConsentId
            || !string.Equals(
                turn.MemoryOutbound.ManifestHash,
                prepared.ManifestHash,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (turn.FrozenRoute is not
            {
                Status: SessionTurnRouteStatus.Ready,
                ProviderId: { Length: > 0 } providerId,
                ModelId: { Length: > 0 } modelId,
                DataDestination: { Length: > 0 } destination
            }
            || !string.Equals(providerId, prepared.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(modelId, prepared.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var prompt = prompts.GetRequired("chat.general", RoutedConversationProvider.ChatPromptVersion, providerId);
            return string.Equals(
                    MemoryOutboundContract.NormalizeHttpsOrigin(destination),
                    prepared.DestinationOrigin,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(prompt.PromptId, prepared.PromptId, StringComparison.Ordinal)
                && string.Equals(prompt.Version, prepared.PromptVersion, StringComparison.Ordinal)
                && string.Equals(
                    prompt.ContentSha256,
                    prepared.PromptContentHash,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private ForegroundWindowSnapshot? ResolvePlannedWindow(ForegroundApplicationDto? planned)
    {
        if (planned is null)
        {
            return null;
        }

        var resolved = foregroundWindows.ResolveWindow(planned.WindowHandle);
        if (resolved is null
            || resolved.ProcessId <= 0
            || resolved.ProcessStartTimeUtc == default
            || resolved.WindowHandle != planned.WindowHandle
            || !string.Equals(
                resolved.ProcessName,
                planned.ProcessName,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(resolved.WindowTitle, planned.WindowTitle, StringComparison.Ordinal))
        {
            return null;
        }

        return resolved;
    }

    private static bool IsWindowBoundTurn(SessionTurnRecord turn) =>
        string.Equals(turn.IntentKind, UniversalIntentKind.DescribeForeground.ToString(), StringComparison.Ordinal)
        || string.Equals(turn.IntentKind, UniversalIntentKind.SearchForeground.ToString(), StringComparison.Ordinal);

    private bool MatchesPersistedWindowIdentity(
        SessionTurnRecord turn,
        ForegroundWindowSnapshot? resolved = null)
    {
        if (turn.WindowHandle is not { } windowHandle
            || turn.WindowProcessId is not { } processId
            || turn.WindowProcessStartTimeUtc is not { } processStartTimeUtc
            || string.IsNullOrWhiteSpace(turn.WindowProcessName)
            || string.IsNullOrWhiteSpace(turn.WindowTitle))
        {
            return false;
        }

        var expected = new ForegroundWindowSnapshot(
            windowHandle,
            turn.WindowTitle,
            turn.WindowProcessName,
            processId,
            processStartTimeUtc,
            DateTimeOffset.MinValue);
        return WindowIdentityContract.Matches(
            expected,
            resolved ?? foregroundWindows.ResolveWindow(windowHandle));
    }

    private async Task RequireFreshWindowConfirmationAsync(
        SessionTurnRecord turn,
        CancellationToken cancellationToken)
    {
        var candidate = foregroundWindows.GetLastExternalWindow();
        var current = candidate is null
            ? null
            : foregroundWindows.ResolveWindow(candidate.WindowHandle);
        if (candidate is null || !WindowIdentityContract.Matches(candidate, current))
        {
            current = null;
        }

        var describe = string.Equals(
            turn.IntentKind,
            UniversalIntentKind.DescribeForeground.ToString(),
            StringComparison.Ordinal);
        await TransitionAsync(
                turn.Id,
                value => value with
                {
                    PlanId = null,
                    Phase = current is null
                        ? SessionTurnPhase.WaitingForWindow
                        : describe
                            ? SessionTurnPhase.WaitingForWindowConsent
                            : SessionTurnPhase.WaitingForConfirmation,
                    MissingContext = current is null
                        ? SessionMissingContext.Window
                        : describe
                            ? SessionMissingContext.WindowConsent
                            : SessionMissingContext.Confirmation,
                    WindowHandle = current?.WindowHandle,
                    WindowTitle = current?.WindowTitle,
                    WindowProcessName = current?.ProcessName,
                    WindowProcessId = current?.ProcessId,
                    WindowProcessStartTimeUtc = current?.ProcessStartTimeUtc,
                    ConfirmationGranted = false,
                    RequiresConfirmation = true,
                    ResultSummary = current is null
                        ? "目标窗口已经不可用，请切换到要操作的窗口后继续。"
                        : "前台窗口已经变化（身份不再一致），请确认新的目标窗口后再继续。",
                    FailureCode = "window_identity_changed",
                    FailureMessage = "授权后目标窗口身份发生变化。",
                    CompletedAtUtc = null
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string NormalizeModality(string value) =>
        string.Equals(value, "Voice", StringComparison.OrdinalIgnoreCase)
            ? "Voice"
            : string.Equals(value, "ProgrammingTask", StringComparison.OrdinalIgnoreCase)
                ? "ProgrammingTask"
                : "Text";

    private static (string? IntentKind, string? Target) NormalizeActionExpectation(
        string? intentKind,
        string? target)
    {
        if (string.IsNullOrWhiteSpace(intentKind) && string.IsNullOrWhiteSpace(target))
        {
            return (null, null);
        }

        if (string.IsNullOrWhiteSpace(intentKind) || string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("电脑操作目标必须同时包含类型和精确目标。 ");
        }

        var normalizedKind = intentKind.Trim();
        var normalizedTarget = target.Trim();
        if (normalizedTarget.Length > 2_048)
        {
            throw new ArgumentException("电脑操作目标过长。 ");
        }

        if (string.Equals(normalizedKind, "OpenWebsite", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(normalizedTarget, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                throw new ArgumentException("网站目标必须是有效的 https 地址。 ");
            }

            return ("OpenWebsite", uri.AbsoluteUri);
        }

        if (!string.Equals(normalizedKind, "OpenApplication", StringComparison.Ordinal))
        {
            throw new ArgumentException("当前只允许为应用或网站绑定结构化操作目标。 ");
        }

        return ("OpenApplication", normalizedTarget);
    }

    private static bool MatchesExpectedAction(
        SessionTurnRecord turn,
        string? actualIntentKind,
        string? actualTarget) =>
        turn.ExpectedIntentKind is null && turn.ExpectedTarget is null
        || string.Equals(turn.ExpectedIntentKind, actualIntentKind, StringComparison.Ordinal)
        && string.Equals(turn.ExpectedTarget, actualTarget, StringComparison.Ordinal);

    private static bool MatchesPlannedExpectation(SessionTurnRecord turn) =>
        MatchesExpectedAction(turn, turn.IntentKind, turn.PlanTarget);

    private static string BuildTitle(string text) =>
        text.Length <= 36 ? text : text[..36] + "…";

    private static string FriendlyException(Exception exception) => exception switch
    {
        DesktopTaskException task => task.Message,
        ApplicationSearchWorkflowException or DesktopSearchException
            or InstalledApplicationResolutionException or WindowIdentityException =>
                DesktopApiErrors.FromException(exception).UserMessage,
        FileNotFoundException => "没有找到 Codex，请先安装并登录 Codex。",
        NotSupportedException => "当前 Codex 版本尚未通过兼容验证。",
        _ => "这次处理没有成功，请稍后重试。"
    };

    private static string FriendlyExceptionCode(Exception exception, string fallback) => exception switch
    {
        DesktopTaskException task => task.Code,
        ApplicationSearchWorkflowException or DesktopSearchException
            or InstalledApplicationResolutionException or WindowIdentityException =>
                DesktopApiErrors.FromException(exception).Code,
        _ => fallback
    };

    private sealed record PendingVisualSearchConsent(Guid ConsentId, VisualSearchConsentRequest Request, string PlanTarget,
        string OperationId, DateTimeOffset ExpiresAtUtc, long TurnVersion)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DisplayName => string.IsNullOrWhiteSpace(Request.Window.WindowTitle)
            ? $"{Request.OriginWindow.WindowTitle}的临时窗口" : Request.Window.WindowTitle;
    }

    private sealed class ActiveSessionWork(Guid turnId)
    {
        private readonly TaskCompletionSource<Task> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid TurnId { get; } = turnId;

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource VisiblePause { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingVisualSearchConsent? VisualConsent { get; set; }
        public PendingDesktopTaskConsent? DesktopTaskConsent { get; set; }

        public Task Completion => _completion.Task.Unwrap();

        public void SetCompletion(Task completion) => _completion.TrySetResult(completion);

        public async Task WaitForVisiblePauseOrCompletionAsync(CancellationToken cancellationToken)
        {
            var completed = Completion;
            var reached = await Task.WhenAny(completed, VisiblePause.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            await reached.ConfigureAwait(false);
        }
    }

    private sealed class ActiveTaskMonitor(Guid turnId, Guid taskId)
    {
        private readonly TaskCompletionSource<Task> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid TurnId { get; } = turnId;

        public Guid TaskId { get; } = taskId;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task Completion => _completion.Task.Unwrap();

        public void SetCompletion(Task completion) => _completion.TrySetResult(completion);
    }
}
