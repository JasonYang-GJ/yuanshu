using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.Qwen;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopV01.RealAcceptanceRunner;

public sealed record R4QwenModelEvidence(
    string ExpectedProviderId,
    string ExpectedModelId,
    string? SettingsProviderId,
    string? SettingsModelId,
    IReadOnlyList<string> FrozenRouteModelIds,
    IReadOnlyList<string> InvocationProviderIds,
    IReadOnlyList<string> InvocationModelIds)
{
    public bool IsCompleteMatch =>
        string.Equals(ExpectedProviderId, QwenChatModelProvider.ProviderId, StringComparison.Ordinal)
        && string.Equals(ExpectedModelId, QwenChatModelProvider.DefaultModelId, StringComparison.Ordinal)
        && string.Equals(SettingsProviderId, ExpectedProviderId, StringComparison.Ordinal)
        && string.Equals(SettingsModelId, ExpectedModelId, StringComparison.Ordinal)
        && FrozenRouteModelIds.Count == 2
        && FrozenRouteModelIds.All(item => string.Equals(item, ExpectedModelId, StringComparison.Ordinal))
        && InvocationProviderIds.Count == 2
        && InvocationProviderIds.All(item => string.Equals(item, ExpectedProviderId, StringComparison.Ordinal))
        && InvocationModelIds.Count == 2
        && InvocationModelIds.All(item => string.Equals(item, ExpectedModelId, StringComparison.Ordinal));
}

public sealed record R4QwenValidationResult
{
    public required string Mode { get; init; }

    public required string Stage { get; init; }

    public required bool Passed { get; init; }

    public string? ErrorCode { get; init; }

    public string? ProviderDiagnosticCode { get; init; }

    public required R4BuildIdentityEvidence BuildIdentity { get; init; }

    public required string LaunchCommand { get; init; }

    public required string ProviderId { get; init; }

    public required string ModelId { get; init; }

    public string? SettingsProviderId { get; init; }

    public string? SettingsModelId { get; init; }

    public R4QwenModelEvidence? ModelEvidence { get; init; }

    public bool HealthExecuted { get; init; }

    public bool HealthPassed { get; init; }

    public string? HealthState { get; init; }

    public string? HealthMessageCategory { get; init; }

    public R4HealthResponseShapeEvidence? HealthResponseShapeEvidence { get; init; }

    public bool OrdinaryExecuted { get; init; }

    public bool OrdinaryPassed { get; init; }

    public bool OrdinaryReplyExact { get; init; }

    public bool CancellationExecuted { get; init; }

    public bool CancellationPassed { get; init; }

    public R3CancellationTerminalStateEvidence? CancellationTerminalEvidence { get; init; }

    public int CancellationRequestCount { get; init; }

    public int CancellationDeltaCount { get; init; }

    public int? CancellationDeltaCountAtCompletion { get; init; }

    public bool LateDeltaRejected { get; init; }

    public bool LateFinalRejected { get; init; }

    public bool LateSuccessRejected { get; init; }

    public bool SuccessfulUsageRecorded { get; init; }

    public bool SuccessfulProviderRequestIdRecorded { get; init; }

    public bool CancellationUsageAbsent { get; init; }

    public bool CancellationProviderRequestIdAbsent { get; init; }

    public int TotalRequestCount { get; init; }

    public int HealthRequestCount { get; init; }

    public int ModelRequestCount { get; init; }

    public int HttpTotalRequestCount { get; init; }

    public int HttpHealthRequestCount { get; init; }

    public int HttpModelRequestCount { get; init; }

    public IReadOnlyList<int> HttpStatusCodes { get; init; } = [];

    public int DeepSeekCallCount { get; init; }

    public int CodexChatCallCount { get; init; }

    public int? SessionCount { get; init; }

    public int? ConversationCount { get; init; }

    public int? AiInvocationCount { get; init; }

    public bool NoAutomaticRetry { get; init; }

    public bool NoFallback { get; init; }

    public bool NoResend { get; init; }

    public long ElapsedMilliseconds { get; init; }

    public IReadOnlyList<R3SafeResponseShapeEvidence> ResponseShapeEvidence { get; init; } = [];
}

public sealed record R4QwenExecutionOutcome(
    bool Passed,
    string StructuredEvidenceJson,
    R4QwenValidationResult Result);

internal sealed record R4CancellationTurnEvidence(
    AiInvocationRecord Invocation,
    R3CancellationTerminalStateEvidence TerminalEvidence,
    int CancellationRequestCount,
    int DeltaCount,
    int DeltaCountAtCancellationCompletion,
    bool LateDeltaRejected,
    bool LateFinalRejected,
    bool LateSuccessRejected);

public static class R4QwenRunnerExecution
{
    public const string OrdinaryCanary =
        "R4-QWEN-ORDINARY-CANARY: reply with exactly QWEN-OK.";
    public const string CancellationCanary =
        "R4-QWEN-CANCEL-LATE-CANARY: write 100 numbered short lines and do not summarize.";

    public static async Task<R4QwenExecutionOutcome> ExecuteAsync(
        IDesktopApiClient api,
        IHost host,
        R4QwenBudgetedChatModelProvider provider,
        R4QwenValidationOptions options,
        R4BuildIdentityEvidence buildIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(buildIdentity);
        var stage = "guard";
        var mode = options.HealthOnly ? "health-only" : "full";
        string? settingsProviderId = null;
        string? settingsModelId = null;
        string? healthState = null;
        string? healthMessageCategory = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!options.IsValid
                || options.Budget is null
                || options.ExpectedProviderId != QwenChatModelProvider.ProviderId
                || options.ExpectedModelId != QwenChatModelProvider.DefaultModelId)
            {
                throw new R4ValidationFailureException("r4_execution_options_invalid");
            }

            using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            totalTimeout.CancelAfter(options.Budget.TotalTimeout);
            var token = totalTimeout.Token;

            stage = "settings";
            var settings = await RequireQwenConfigurationAsync(api, token).ConfigureAwait(false);
            settingsProviderId = settings.CurrentChatRoute.ProviderId;
            settingsModelId = settings.CurrentChatRoute.ModelId;
            RequireExactRoute(settingsProviderId, settingsModelId);

            stage = "health";
            var health = await api.CheckAiProviderHealthAsync(
                    new ProviderIdRequestDto(QwenChatModelProvider.ProviderId),
                    token)
                .ConfigureAwait(false);
            healthState = health.State;
            healthMessageCategory = SafeHealthMessageCategory(health);
            if (string.Equals(health.State, "NotConfigured", StringComparison.Ordinal))
            {
                throw new R4ValidationFailureException(
                    "r4_credential_not_configured",
                    "qwen.not_configured");
            }

            if (!string.Equals(health.State, "Healthy", StringComparison.Ordinal))
            {
                throw new R4ValidationFailureException(
                    "r4_health_failed",
                    $"qwen.health.{healthMessageCategory}");
            }

            if (options.HealthOnly)
            {
                var healthHttp = host.Services.GetRequiredService<R4QwenHttpEvidence>();
                var healthProbe = host.Services.GetRequiredService<R4ForbiddenProviderProbe>();
                RequireHealthOnlyCounts(provider, healthHttp, healthProbe, options.Budget);
                stopwatch.Stop();
                var healthOnlyResult = new R4QwenValidationResult
                {
                    Mode = mode,
                    Stage = "complete",
                    Passed = true,
                    BuildIdentity = buildIdentity,
                    LaunchCommand = R4QwenLaunchCommand.CreateHealthOnly(
                        buildIdentity.ExpectedCommitSha),
                    ProviderId = QwenChatModelProvider.ProviderId,
                    ModelId = QwenChatModelProvider.DefaultModelId,
                    SettingsProviderId = settingsProviderId,
                    SettingsModelId = settingsModelId,
                    HealthExecuted = true,
                    HealthPassed = true,
                    HealthState = healthState,
                    HealthMessageCategory = healthMessageCategory,
                    HealthResponseShapeEvidence = healthHttp.HealthResponseShape,
                    TotalRequestCount = provider.TotalRequestCount,
                    HealthRequestCount = provider.HealthRequestCount,
                    ModelRequestCount = provider.ModelRequestCount,
                    HttpTotalRequestCount = healthHttp.TotalRequests,
                    HttpHealthRequestCount = healthHttp.HealthRequests,
                    HttpModelRequestCount = healthHttp.ModelRequests,
                    HttpStatusCodes = healthHttp.StatusCodes,
                    DeepSeekCallCount = healthProbe.DeepSeekCalls,
                    CodexChatCallCount = healthProbe.CodexChatCalls,
                    SessionCount = 0,
                    ConversationCount = 0,
                    AiInvocationCount = 0,
                    NoAutomaticRetry = options.Budget.NoAutomaticRetry,
                    NoFallback = options.Budget.NoFallback,
                    NoResend = options.Budget.NoResend,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                };
                return new R4QwenExecutionOutcome(
                    true,
                    JsonSerializer.Serialize(healthOnlyResult),
                    healthOnlyResult);
            }

            var invocations = host.Services.GetRequiredService<IAiInvocationStore>();
            var sessions = host.Services.GetRequiredService<ISessionStore>();
            var conversations = host.Services.GetRequiredService<IConversationStore>();
            var expectedPrompt = host.Services.GetRequiredService<PromptRegistry>()
                .GetRequired("chat.general", "4", QwenChatModelProvider.ProviderId);
            var session = await api.StartNewSessionAsync("R4 Qwen isolated acceptance", token)
                .ConfigureAwait(false);

            stage = "ordinary";
            var ordinaryObservation = provider.PrepareNextCall(R4QwenCallMode.Ordinary);
            var ordinary = await api.SubmitSessionInputAsync(
                    new SessionInputRequestDto(
                        OrdinaryCanary,
                        "Text",
                        $"r4-qwen-ordinary-{Guid.NewGuid():N}",
                        session.SessionId),
                    token)
                .ConfigureAwait(false);
            var ordinarySnapshot = await WaitForSessionTurnAsync(
                    api,
                    ordinary.TurnId,
                    ["Completed", "Failed", "Cancelled", "Interrupted"],
                    options.Budget.TotalTimeout,
                    token)
                .ConfigureAwait(false);
            await ordinaryObservation.Terminal.WaitAsync(token).ConfigureAwait(false);
            var ordinaryTurn = ordinarySnapshot.Turns.Single(item => item.Id == ordinary.TurnId);
            if (ordinaryTurn.Phase != "Completed")
            {
                var failedInvocation = (await invocations.GetForSessionTurnAsync(
                        ordinary.TurnId,
                        token)
                    .ConfigureAwait(false))
                    .SingleOrDefault(item => item.Purpose == AiInvocationPurpose.Conversation);
                var persistedTurn = await sessions.GetTurnAsync(ordinary.TurnId, token)
                    .ConfigureAwait(false);
                throw new R4ValidationFailureException(
                    persistedTurn?.FailureCode ?? "r4_ordinary_failed",
                    failedInvocation?.FailureCode);
            }

            var ordinaryInvocation = RequireConversationInvocation(
                await invocations.GetForSessionTurnAsync(ordinary.TurnId, token).ConfigureAwait(false),
                provider,
                ordinaryTurn,
                expectedPrompt,
                AiInvocationStatus.Succeeded,
                requireUsage: true,
                requireProviderRequestId: true);
            var ordinaryReplyExact = string.Equals(
                ordinaryTurn.ResultSummary?.Trim(),
                "QWEN-OK",
                StringComparison.Ordinal);
            if (!ordinaryReplyExact
                || ordinaryObservation.DeltaCount < 1
                || !ordinaryObservation.FinalUpdateObserved
                || !ordinaryObservation.ResponseReturned)
            {
                throw new R4ValidationFailureException("r4_ordinary_response_mismatch");
            }

            var assistantMessageIdsBeforeCancellation = ordinarySnapshot.Messages
                .Where(message => message.Role == "Assistant")
                .Select(message => message.Id)
                .ToHashSet();

            stage = "cancellation";
            var cancellation = await ExecuteCancellationAsync(
                    api,
                    sessions,
                    conversations,
                    invocations,
                    provider,
                    expectedPrompt,
                    options.Budget,
                    session.SessionId,
                    assistantMessageIdsBeforeCancellation,
                    token)
                .ConfigureAwait(false);

            stage = "audit";
            var allInvocations = new[] { ordinaryInvocation, cancellation.Invocation };
            var modelEvidence = new R4QwenModelEvidence(
                QwenChatModelProvider.ProviderId,
                QwenChatModelProvider.DefaultModelId,
                settingsProviderId,
                settingsModelId,
                provider.RequestIdentities.Select(item => item.ModelId).ToArray(),
                allInvocations.Select(item => item.ProviderId).ToArray(),
                allInvocations.Select(item => item.ModelId).ToArray());
            if (!modelEvidence.IsCompleteMatch)
            {
                throw new R4ValidationFailureException("r4_expected_route_mismatch");
            }

            var http = host.Services.GetRequiredService<R4QwenHttpEvidence>();
            var probe = host.Services.GetRequiredService<R4ForbiddenProviderProbe>();
            RequireExactCounts(provider, http, probe, options.Budget);
            stopwatch.Stop();
            var result = new R4QwenValidationResult
            {
                Mode = mode,
                Stage = "complete",
                Passed = true,
                BuildIdentity = buildIdentity,
                LaunchCommand = R4QwenLaunchCommand.Create(buildIdentity.ExpectedCommitSha),
                ProviderId = QwenChatModelProvider.ProviderId,
                ModelId = QwenChatModelProvider.DefaultModelId,
                SettingsProviderId = settingsProviderId,
                SettingsModelId = settingsModelId,
                ModelEvidence = modelEvidence,
                HealthExecuted = true,
                HealthPassed = true,
                HealthState = healthState,
                HealthMessageCategory = healthMessageCategory,
                HealthResponseShapeEvidence = http.HealthResponseShape,
                OrdinaryExecuted = true,
                OrdinaryPassed = true,
                OrdinaryReplyExact = true,
                CancellationExecuted = true,
                CancellationPassed = true,
                CancellationTerminalEvidence = cancellation.TerminalEvidence,
                CancellationRequestCount = cancellation.CancellationRequestCount,
                CancellationDeltaCount = cancellation.DeltaCount,
                CancellationDeltaCountAtCompletion = cancellation.DeltaCountAtCancellationCompletion,
                LateDeltaRejected = cancellation.LateDeltaRejected,
                LateFinalRejected = cancellation.LateFinalRejected,
                LateSuccessRejected = cancellation.LateSuccessRejected,
                SuccessfulUsageRecorded = ordinaryInvocation.Usage is not null,
                SuccessfulProviderRequestIdRecorded = !string.IsNullOrWhiteSpace(
                    ordinaryInvocation.ProviderRequestId),
                CancellationUsageAbsent = cancellation.Invocation.Usage is null,
                CancellationProviderRequestIdAbsent = string.IsNullOrWhiteSpace(
                    cancellation.Invocation.ProviderRequestId),
                TotalRequestCount = provider.TotalRequestCount,
                HealthRequestCount = provider.HealthRequestCount,
                ModelRequestCount = provider.ModelRequestCount,
                HttpTotalRequestCount = http.TotalRequests,
                HttpHealthRequestCount = http.HealthRequests,
                HttpModelRequestCount = http.ModelRequests,
                HttpStatusCodes = http.StatusCodes,
                DeepSeekCallCount = probe.DeepSeekCalls,
                CodexChatCallCount = probe.CodexChatCalls,
                SessionCount = 1,
                ConversationCount = 1,
                AiInvocationCount = allInvocations.Length,
                NoAutomaticRetry = options.Budget.NoAutomaticRetry,
                NoFallback = options.Budget.NoFallback,
                NoResend = options.Budget.NoResend,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                ResponseShapeEvidence = provider.ResponseShapeEvidence
            };
            return new R4QwenExecutionOutcome(
                true,
                JsonSerializer.Serialize(result),
                result);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var http = host.Services.GetService<R4QwenHttpEvidence>();
            var probe = host.Services.GetService<R4ForbiddenProviderProbe>();
            var result = new R4QwenValidationResult
            {
                Mode = mode,
                Stage = stage,
                Passed = false,
                ErrorCode = SafeErrorCode(exception),
                ProviderDiagnosticCode = SafeDiagnosticCode(exception),
                BuildIdentity = buildIdentity,
                LaunchCommand = options.HealthOnly
                    ? R4QwenLaunchCommand.CreateHealthOnly(buildIdentity.ExpectedCommitSha)
                    : R4QwenLaunchCommand.Create(buildIdentity.ExpectedCommitSha),
                ProviderId = QwenChatModelProvider.ProviderId,
                ModelId = QwenChatModelProvider.DefaultModelId,
                SettingsProviderId = settingsProviderId,
                SettingsModelId = settingsModelId,
                HealthExecuted = stage is not "guard" and not "settings",
                HealthState = healthState,
                HealthMessageCategory = healthMessageCategory,
                HealthResponseShapeEvidence = http?.HealthResponseShape,
                OrdinaryExecuted = stage is "ordinary" or "cancellation" or "audit",
                CancellationExecuted = stage is "cancellation" or "audit",
                TotalRequestCount = provider.TotalRequestCount,
                HealthRequestCount = provider.HealthRequestCount,
                ModelRequestCount = provider.ModelRequestCount,
                HttpTotalRequestCount = http?.TotalRequests ?? 0,
                HttpHealthRequestCount = http?.HealthRequests ?? 0,
                HttpModelRequestCount = http?.ModelRequests ?? 0,
                HttpStatusCodes = http?.StatusCodes ?? [],
                DeepSeekCallCount = probe?.DeepSeekCalls ?? 0,
                CodexChatCallCount = probe?.CodexChatCalls ?? 0,
                SessionCount = options.HealthOnly ? 0 : null,
                ConversationCount = options.HealthOnly ? 0 : null,
                AiInvocationCount = options.HealthOnly ? 0 : null,
                NoAutomaticRetry = options.Budget?.NoAutomaticRetry == true,
                NoFallback = options.Budget?.NoFallback == true,
                NoResend = options.Budget?.NoResend == true,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                ResponseShapeEvidence = provider.ResponseShapeEvidence
            };
            return new R4QwenExecutionOutcome(
                false,
                JsonSerializer.Serialize(result),
                result);
        }
    }

    private static async Task<R4CancellationTurnEvidence> ExecuteCancellationAsync(
        IDesktopApiClient api,
        ISessionStore sessions,
        IConversationStore conversations,
        IAiInvocationStore invocations,
        R4QwenBudgetedChatModelProvider provider,
        PromptDefinition expectedPrompt,
        R4QwenValidationBudget budget,
        Guid sessionId,
        IReadOnlySet<Guid> assistantMessageIdsBeforeCancellation,
        CancellationToken cancellationToken)
    {
        var observation = provider.PrepareNextCall(R4QwenCallMode.Cancellation);
        var submitted = await api.SubmitSessionInputAsync(
                new SessionInputRequestDto(
                    CancellationCanary,
                    "Text",
                    $"r4-qwen-cancel-{Guid.NewGuid():N}",
                    sessionId),
                cancellationToken)
            .ConfigureAwait(false);
        var terminalTask = WaitForTerminalEvidenceAsync(
            api,
            sessions,
            conversations,
            invocations,
            observation.Terminal,
            submitted.TurnId,
            budget.TotalTimeout,
            cancellationToken);
        var completed = await Task.WhenAny(
                observation.AtLeastTwoDeltas,
                terminalTask,
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken))
            .ConfigureAwait(false);
        var cancellationRequestCount = 0;
        if (completed == observation.AtLeastTwoDeltas)
        {
            await observation.AtLeastTwoDeltas.ConfigureAwait(false);
            if (Interlocked.Increment(ref cancellationRequestCount) != 1)
            {
                throw new R4ValidationFailureException("r4_cancellation_request_count_mismatch");
            }

            _ = await api.CancelSessionTurnAsync(
                    sessionId,
                    submitted.TurnId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (completed == terminalTask)
        {
            var early = await terminalTask.ConfigureAwait(false);
            throw new R4ValidationFailureException(
                early.SessionState switch
                {
                    "Failed" => early.PublicFailureCode ?? "r4_cancellation_provider_failed",
                    "Completed" => "r4_cancellation_precondition_succeeded",
                    "Cancelled" => "r4_cancellation_precondition_cancelled",
                    "Interrupted" => "r4_cancellation_precondition_interrupted",
                    _ => "r4_cancellation_terminal_mismatch"
                },
                early.ProviderDiagnosticCode);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new R4ValidationFailureException("r4_cancellation_precondition_timeout");
        }

        var terminal = await terminalTask.ConfigureAwait(false);
        var terminalEvidence = new R3CancellationTerminalStateEvidence(
            terminal.SessionState,
            terminal.ConversationState,
            terminal.InvocationState);
        R3CancellationTerminalStateGate.RequireCancelled(terminalEvidence);
        var snapshot = await WaitForSessionTurnAsync(
                api,
                submitted.TurnId,
                ["Cancelled"],
                budget.TotalTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var turn = snapshot.Turns.Single(item => item.Id == submitted.TurnId);
        var invocation = RequireConversationInvocation(
            await invocations.GetForSessionTurnAsync(submitted.TurnId, cancellationToken)
                .ConfigureAwait(false),
            provider,
            turn,
            expectedPrompt,
            AiInvocationStatus.Cancelled,
            requireUsage: false,
            requireProviderRequestId: false);
        if (!string.Equals(invocation.FailureCode, "qwen.cancelled", StringComparison.Ordinal))
        {
            throw new R4ValidationFailureException(
                "r4_cancellation_audit_mismatch",
                invocation.FailureCode);
        }

        var deltaCountAtCompletion = observation.DeltaCountWhenCancellationCompleted;
        var lateDeltaRejected = deltaCountAtCompletion == observation.DeltaCount;
        var lateFinalRejected = !observation.FinalUpdateObserved;
        var lateSuccessRejected = !observation.ResponseReturned
                                  && turn.Phase == "Cancelled"
                                  && snapshot.Messages
                                      .Where(message => message.Role == "Assistant")
                                      .All(message => assistantMessageIdsBeforeCancellation.Contains(message.Id));
        if (!lateDeltaRejected || !lateFinalRejected || !lateSuccessRejected)
        {
            throw new R4ValidationFailureException("r4_cancellation_late_result");
        }

        return new R4CancellationTurnEvidence(
            invocation,
            terminalEvidence,
            cancellationRequestCount,
            observation.DeltaCount,
            deltaCountAtCompletion
            ?? throw new R4ValidationFailureException("r4_cancellation_completion_missing"),
            lateDeltaRejected,
            lateFinalRejected,
            lateSuccessRejected);
    }

    internal static AiInvocationRecord RequireConversationInvocation(
        IReadOnlyList<AiInvocationRecord> records,
        R4QwenBudgetedChatModelProvider provider,
        UnifiedSessionTurnDto turn,
        PromptDefinition expectedPrompt,
        AiInvocationStatus expectedStatus,
        bool requireUsage,
        bool requireProviderRequestId)
    {
        var invocation = records.Single(record => record.Purpose == AiInvocationPurpose.Conversation);
        var identity = provider.RequestIdentities.Single(item => item.TurnId == turn.Id);
        var matches = invocation.Id == identity.RequestId
                      && invocation.SessionTurnId == turn.Id
                      && invocation.ConversationTurnId == turn.ConversationTurnId
                      && invocation.ProviderId == QwenChatModelProvider.ProviderId
                      && invocation.ModelId == QwenChatModelProvider.DefaultModelId
                      && identity.ModelId == QwenChatModelProvider.DefaultModelId
                      && invocation.DataDestination == QwenChatModelProvider.DataDestination
                      && invocation.Status == expectedStatus
                      && invocation.PromptId == expectedPrompt.PromptId
                      && invocation.PromptVersion == expectedPrompt.Version
                      && invocation.PromptContentHash == expectedPrompt.ContentSha256
                      && invocation.StartedAtUtc != default
                      && invocation.CompletedAtUtc is not null
                      && (requireUsage ? invocation.Usage is not null : invocation.Usage is null)
                      && (requireProviderRequestId
                          ? !string.IsNullOrWhiteSpace(invocation.ProviderRequestId)
                          : string.IsNullOrWhiteSpace(invocation.ProviderRequestId));
        if (!matches)
        {
            throw new R4ValidationFailureException("r4_invocation_mismatch");
        }

        return invocation;
    }

    private static async Task<R3CancellationTerminalEvidence> WaitForTerminalEvidenceAsync(
        IDesktopApiClient api,
        ISessionStore sessions,
        IConversationStore conversations,
        IAiInvocationStore invocations,
        Task providerTerminal,
        Guid sessionTurnId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var snapshot = await WaitForSessionTurnAsync(
                api,
                sessionTurnId,
                ["Failed", "Cancelled", "Completed", "Interrupted"],
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        await providerTerminal.WaitAsync(cancellationToken).ConfigureAwait(false);
        var turn = snapshot.Turns.Single(item => item.Id == sessionTurnId);
        var persistedTurn = await sessions.GetTurnAsync(sessionTurnId, cancellationToken)
                                  .ConfigureAwait(false)
                              ?? throw new R4ValidationFailureException(
                                  "r4_cancellation_session_turn_missing");
        var conversationTurn = turn.ConversationTurnId is { } conversationTurnId
            ? (await conversations.GetTurnsAsync(snapshot.ConversationId, cancellationToken)
                    .ConfigureAwait(false))
                .SingleOrDefault(item => item.Id == conversationTurnId)
            : null;
        var invocation = (await invocations.GetForSessionTurnAsync(
                sessionTurnId,
                cancellationToken)
            .ConfigureAwait(false))
            .SingleOrDefault(item => item.Purpose == AiInvocationPurpose.Conversation);
        return new R3CancellationTerminalEvidence(
            turn.Phase,
            conversationTurn?.Status.ToString() ?? "Missing",
            invocation?.Status.ToString() ?? "Missing",
            persistedTurn.FailureCode ?? conversationTurn?.FailureCode,
            invocation?.FailureCode,
            invocation?.ModelId);
    }

    private static async Task<AiSettingsDto> RequireQwenConfigurationAsync(
        IDesktopApiClient api,
        CancellationToken cancellationToken)
    {
        var settings = await api.GetAiSettingsAsync(cancellationToken).ConfigureAwait(false);
        var provider = settings.Providers.Single(item =>
            item.ProviderId == QwenChatModelProvider.ProviderId);
        if (provider.ConfigurationState != "Configured")
        {
            throw new R4ValidationFailureException(
                "r4_credential_not_configured",
                "qwen.not_configured");
        }

        RequireExactRoute(
            settings.CurrentChatRoute.ProviderId,
            settings.CurrentChatRoute.ModelId);
        return settings;
    }

    private static async Task<SessionSnapshotDto> WaitForSessionTurnAsync(
        IDesktopApiClient api,
        Guid turnId,
        string[] expectedPhases,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        SessionSnapshotDto? snapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot ??= await api.GetCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
            var turn = snapshot?.Turns.SingleOrDefault(item => item.Id == turnId);
            if (turn is not null && expectedPhases.Contains(turn.Phase))
            {
                return snapshot!;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            snapshot = await api.WaitForSessionUpdateAsync(
                    snapshot?.ChangeVersion ?? -1,
                    (int)Math.Clamp(remaining.TotalMilliseconds, 1, 5_000),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new R4ValidationFailureException("r4_session_turn_timeout");
    }

    private static void RequireExactRoute(string providerId, string modelId)
    {
        if (providerId != QwenChatModelProvider.ProviderId
            || modelId != QwenChatModelProvider.DefaultModelId)
        {
            throw new R4ValidationFailureException("r4_expected_route_mismatch");
        }
    }

    private static void RequireExactCounts(
        R4QwenBudgetedChatModelProvider provider,
        R4QwenHttpEvidence http,
        R4ForbiddenProviderProbe probe,
        R4QwenValidationBudget budget)
    {
        if (provider.TotalRequestCount != budget.MaxTotalRequests
            || provider.HealthRequestCount != 1
            || provider.ModelRequestCount != budget.MaxModelRequests
            || http.TotalRequests != budget.MaxTotalRequests
            || http.HealthRequests != 1
            || http.ModelRequests != budget.MaxModelRequests
            || probe.DeepSeekCalls != 0
            || probe.CodexChatCalls != 0)
        {
            throw new R4ValidationFailureException("r4_request_count_mismatch");
        }
    }

    private static void RequireHealthOnlyCounts(
        R4QwenBudgetedChatModelProvider provider,
        R4QwenHttpEvidence http,
        R4ForbiddenProviderProbe probe,
        R4QwenValidationBudget budget)
    {
        if (budget.MaxTotalRequests != 1
            || budget.MaxModelRequests != 0
            || provider.TotalRequestCount != 1
            || provider.HealthRequestCount != 1
            || provider.ModelRequestCount != 0
            || http.TotalRequests != 1
            || http.HealthRequests != 1
            || http.ModelRequests != 0
            || probe.DeepSeekCalls != 0
            || probe.CodexChatCalls != 0)
        {
            throw new R4ValidationFailureException("r4_health_only_request_count_mismatch");
        }
    }

    private static string SafeErrorCode(Exception exception) => exception switch
    {
        R4ValidationFailureException failure => failure.ErrorCode,
        ChatModelException chat => R3FailureEvidenceReporter.StableFailureCode(chat.Error.Kind),
        OperationCanceledException => "r4_total_timeout",
        _ => "r4_validation_failed"
    };

    private static string? SafeDiagnosticCode(Exception exception)
    {
        var value = exception switch
        {
            R4ValidationFailureException failure => failure.ProviderDiagnosticCode,
            ChatModelException chat => chat.Error.Code,
            _ => null
        };
        return !string.IsNullOrWhiteSpace(value)
               && value.Length <= 128
               && value.All(character =>
                   char.IsAsciiLetterOrDigit(character)
                   || character is '.' or '_' or '-')
            ? value
            : null;
    }

    private static string SafeHealthMessageCategory(AiProviderHealthDto health)
    {
        if (string.Equals(health.State, "Healthy", StringComparison.Ordinal))
        {
            return "healthy";
        }

        if (string.Equals(health.State, "NotConfigured", StringComparison.Ordinal))
        {
            return "not_configured";
        }

        var message = health.SafeMessage;
        if (message.Contains("有效推理权限", StringComparison.Ordinal))
        {
            return "permission_unavailable";
        }

        if (message.Contains("余额", StringComparison.Ordinal)
            || message.Contains("计费", StringComparison.Ordinal))
        {
            return "billing_unavailable";
        }

        if (message.Contains("API Key 无效", StringComparison.Ordinal)
            || message.Contains("API Key 已失效", StringComparison.Ordinal))
        {
            return "credential_invalid";
        }

        if (message.Contains("没有访问", StringComparison.Ordinal))
        {
            return "permission_denied";
        }

        if (message.Contains("请求过多", StringComparison.Ordinal))
        {
            return "rate_limited";
        }

        if (message.Contains("超时", StringComparison.Ordinal))
        {
            return "timeout";
        }

        if (message.Contains("无法连接", StringComparison.Ordinal))
        {
            return "network_unavailable";
        }

        if (message.Contains("不安全的跳转", StringComparison.Ordinal))
        {
            return "redirect_rejected";
        }

        if (message.Contains("服务暂时不可用", StringComparison.Ordinal))
        {
            return "service_unavailable";
        }

        return string.Equals(health.State, "Degraded", StringComparison.Ordinal)
            ? "degraded"
            : "unavailable";
    }
}
