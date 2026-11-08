using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ScreenGuide.Agent.Codex;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Memories;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class DesktopApiDispatcher(
    LocalTaskEntryService taskEntry,
    DesktopHostState hostState,
    DesktopHostOptions options,
    CodexDiagnosticsService codexDiagnostics,
    ProjectInspector projectInspector,
    DesktopActionEntryService desktopActions,
    AssistantCommandService assistantCommands,
    PointerRegionUnderstandingService pointerRegions,
    ConversationService conversations,
    SessionCoordinator sessions,
    BridgeProtocolService bridge,
    AiSettingsService aiSettings,
    MemoryService memories,
    IHostApplicationLifetime applicationLifetime)
{
    private const int MaximumProjectionBytes = 512 * 1024;

    public async Task<DesktopApiResponse> DispatchAsync(
        DesktopApiRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.ProtocolVersion < DesktopProtocolVersion.MinimumSupported
                || request.ProtocolVersion > DesktopProtocolVersion.Current)
            {
                return new DesktopApiResponse(
                    request.RequestId,
                    false,
                    Error: new DesktopApiError(
                        "protocol_version_unsupported",
                        "Desktop Client 与 Desktop Host 版本不兼容。",
                        $"Requested protocol version: {request.ProtocolVersion}; supported: "
                        + $"{DesktopProtocolVersion.MinimumSupported}-{DesktopProtocolVersion.Current}."));
            }

            var payload = request.Method switch
            {
                DesktopApiMethods.Ping or DesktopApiMethods.GetSystemStatus =>
                    DesktopProtocolJson.ToElement(await SystemStatusAsync(cancellationToken)
                        .ConfigureAwait(false)),
                DesktopApiMethods.GetDashboard =>
                    DesktopProtocolJson.ToElement(await DashboardAsync(cancellationToken)
                        .ConfigureAwait(false)),
                DesktopApiMethods.ListProjects =>
                    DesktopProtocolJson.ToElement(await ListProjectsAsync(cancellationToken)
                        .ConfigureAwait(false)),
                DesktopApiMethods.AddProject =>
                    DesktopProtocolJson.ToElement(await AddProjectAsync(
                        Deserialize<AddProjectRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.RevokeProject =>
                    DesktopProtocolJson.ToElement(await RevokeProjectAsync(
                        Deserialize<RevokeProjectRequestDto>(request).ProjectId,
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.ListTasks =>
                    DesktopProtocolJson.ToElement(await ListTasksAsync(
                        Deserialize<ListTasksRequest>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.GetTask =>
                    DesktopProtocolJson.ToElement(await GetTaskAsync(
                        Deserialize<TaskIdRequest>(request).TaskId,
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.CreateTask =>
                    DesktopProtocolJson.ToElement(await CreateTaskAsync(
                        Deserialize<CreateTaskRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.CancelTask =>
                    DesktopProtocolJson.ToElement(await CancelTaskAsync(
                        Deserialize<CancelTaskRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.ContinueTask =>
                    DesktopProtocolJson.ToElement(await ContinueTaskAsync(
                        Deserialize<ContinueTaskRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.ClearHistory =>
                    DesktopProtocolJson.ToElement(await ClearHistoryAsync(
                        Deserialize<ClearHistoryRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.ListDesktopApplications =>
                    DesktopProtocolJson.ToElement(desktopActions.GetApplications()),
                DesktopApiMethods.ExecuteDesktopAction =>
                    DesktopProtocolJson.ToElement(await desktopActions.ExecuteAsync(
                        Deserialize<ExecuteDesktopActionRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.PlanAssistantCommand =>
                    DesktopProtocolJson.ToElement(await assistantCommands.PlanAsync(
                        Deserialize<PlanAssistantCommandRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.ExecuteAssistantCommand =>
                    DesktopProtocolJson.ToElement(await assistantCommands.ExecuteAsync(
                        Deserialize<ExecuteAssistantCommandRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.CancelWindowObservation =>
                    DesktopProtocolJson.ToElement(assistantCommands.CancelWindowObservation(
                        Deserialize<CancelWindowObservationRequestDto>(request).OperationId)),
                DesktopApiMethods.PreparePointerRegion =>
                    DesktopProtocolJson.ToElement(MapPointerAnchor(pointerRegions.Prepare(
                        Deserialize<PreparePointerRegionRequestDto>(request).Confirmed,
                        Deserialize<PreparePointerRegionRequestDto>(request).AuthorizationSource))),
                DesktopApiMethods.ReadPointerRegion =>
                    DesktopProtocolJson.ToElement(MapPointerRegionResult(
                        await pointerRegions.ReadAsync(
                            Deserialize<PointerRegionRequestDto>(request).AnchorId,
                            cancellationToken).ConfigureAwait(false))),
                DesktopApiMethods.CancelPointerRegion =>
                    DesktopProtocolJson.ToElement(pointerRegions.Cancel(
                        Deserialize<PointerRegionRequestDto>(request).AnchorId)),
                DesktopApiMethods.ListConversations =>
                    DesktopProtocolJson.ToElement(await ListConversationsAsync(cancellationToken)
                        .ConfigureAwait(false)),
                DesktopApiMethods.GetConversation =>
                    DesktopProtocolJson.ToElement(await GetConversationAsync(
                        Deserialize<ConversationIdRequestDto>(request).ConversationId,
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.CreateConversation =>
                    DesktopProtocolJson.ToElement(await CreateConversationAsync(
                        Deserialize<CreateConversationRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.SendConversationMessage =>
                    DesktopProtocolJson.ToElement(await SendConversationMessageAsync(
                        Deserialize<SendConversationMessageRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.CancelConversationTurn =>
                    DesktopProtocolJson.ToElement(await CancelConversationTurnAsync(
                        Deserialize<CancelConversationTurnRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.GetCurrentSession =>
                    DesktopProtocolJson.ToElement(MapSession(
                        await sessions.GetCurrentAsync(cancellationToken).ConfigureAwait(false))),
                DesktopApiMethods.StartNewSession =>
                    DesktopProtocolJson.ToElement(MapSession(
                        await sessions.StartNewAsync(
                            Deserialize<StartNewSessionRequestDto>(request).Title,
                            cancellationToken).ConfigureAwait(false))!),
                DesktopApiMethods.SetCurrentSession =>
                    DesktopProtocolJson.ToElement(MapSession(
                        await sessions.SetCurrentAsync(
                            Deserialize<SessionIdRequestDto>(request).SessionId,
                            cancellationToken).ConfigureAwait(false))!),
                DesktopApiMethods.SubmitSessionInput =>
                    DesktopProtocolJson.ToElement(await SubmitSessionInputAsync(
                        Deserialize<SessionInputRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.ProvideSessionProject =>
                    DesktopProtocolJson.ToElement(MapSession(
                        await ProvideSessionProjectAsync(
                            Deserialize<ProvideSessionProjectRequestDto>(request),
                            cancellationToken).ConfigureAwait(false))!),
                DesktopApiMethods.ProvideSessionFile =>
                    DesktopProtocolJson.ToElement(MapSession(
                        await ProvideSessionFileAsync(
                            Deserialize<ProvideSessionFileRequestDto>(request),
                            cancellationToken).ConfigureAwait(false))!),
                DesktopApiMethods.RespondSessionWindowConsent =>
                    DesktopProtocolJson.ToElement(MapSession(
                        await RespondSessionWindowConsentAsync(
                            Deserialize<SessionWindowConsentRequestDto>(request),
                            cancellationToken).ConfigureAwait(false))!),
                DesktopApiMethods.RetrySessionTurn =>
                    DesktopProtocolJson.ToElement(MapSession(
                        await RetrySessionTurnAsync(
                            Deserialize<CancelSessionTurnRequestDto>(request),
                            cancellationToken).ConfigureAwait(false))!),
                DesktopApiMethods.ConfirmSessionTurn =>
                    DesktopProtocolJson.ToElement(MapSession(
                        await ConfirmSessionTurnAsync(
                            Deserialize<SessionTurnConfirmationRequestDto>(request),
                            cancellationToken).ConfigureAwait(false))!),
                DesktopApiMethods.ConfirmMemoryOutbound =>
                    DesktopProtocolJson.ToElement(MapSession(
                        await ConfirmMemoryOutboundAsync(
                            Deserialize<SessionMemoryOutboundConsentRequestDto>(request),
                            cancellationToken).ConfigureAwait(false))!),
                DesktopApiMethods.ConfirmPointerAnswer =>
                    DesktopProtocolJson.ToElement(MapSession(
                        await ConfirmPointerAnswerAsync(
                            Deserialize<SessionPointerAnswerConsentRequestDto>(request),
                            cancellationToken).ConfigureAwait(false))!),
                DesktopApiMethods.CancelSessionTurn =>
                    DesktopProtocolJson.ToElement(MapSession(
                        await CancelSessionTurnAsync(
                            Deserialize<CancelSessionTurnRequestDto>(request),
                            cancellationToken).ConfigureAwait(false))!),
                DesktopApiMethods.WaitForSessionUpdate =>
                    DesktopProtocolJson.ToElement(await WaitForSessionUpdateAsync(
                        Deserialize<WaitForSessionUpdateRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.GetSessionMessagesPage =>
                    DesktopProtocolJson.ToElement(await GetSessionMessagesPageAsync(
                        Deserialize<SessionMessagesPageRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.GetSessionTurn =>
                    DesktopProtocolJson.ToElement(await GetSessionTurnAsync(
                        Deserialize<SessionTurnGetRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.BridgeHandshake =>
                    DesktopProtocolJson.ToElement(bridge.Handshake(request.Payload)),
                DesktopApiMethods.BridgeCancel =>
                    DesktopProtocolJson.ToElement(await bridge.CancelAsync(request.Payload, cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.BridgeSnapshotGet =>
                    DesktopProtocolJson.ToElement(await bridge.GetSnapshotAsync(
                        request.Payload,
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.GetAiSettings =>
                    DesktopProtocolJson.ToElement(await aiSettings.GetAsync(cancellationToken)
                        .ConfigureAwait(false)),
                DesktopApiMethods.SetChatRoute =>
                    DesktopProtocolJson.ToElement(await aiSettings.SetChatRouteAsync(
                        Deserialize<SetChatRouteRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.SetProviderCredential =>
                    DesktopProtocolJson.ToElement(await aiSettings.SetProviderCredentialAsync(
                        Deserialize<SetProviderCredentialRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.DeleteProviderCredential =>
                    DesktopProtocolJson.ToElement(await aiSettings.DeleteProviderCredentialAsync(
                        Deserialize<ProviderIdRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.CheckAiProviderHealth =>
                    DesktopProtocolJson.ToElement(await aiSettings.CheckProviderHealthAsync(
                        Deserialize<ProviderIdRequestDto>(request),
                        cancellationToken).ConfigureAwait(false)),
                DesktopApiMethods.ListMemories =>
                    DesktopProtocolJson.ToElement((await memories.ListAsync(cancellationToken)
                        .ConfigureAwait(false)).Select(MapMemory).ToArray()),
                DesktopApiMethods.PreviewMemories =>
                    DesktopProtocolJson.ToElement(MapMemoryPreview(await PreviewMemoriesAsync(
                        Deserialize<MemoryPreviewRequestDto>(request),
                        cancellationToken).ConfigureAwait(false))),
                DesktopApiMethods.GetMemory =>
                    DesktopProtocolJson.ToElement(MapMemory(await memories.GetAsync(
                        Deserialize<MemoryIdRequestDto>(request).MemoryId,
                        cancellationToken).ConfigureAwait(false))),
                DesktopApiMethods.CreateMemory =>
                    DesktopProtocolJson.ToElement(MapMemory(await CreateMemoryAsync(
                        Deserialize<CreateMemoryRequestDto>(request),
                        cancellationToken).ConfigureAwait(false))),
                DesktopApiMethods.UpdateMemory =>
                    DesktopProtocolJson.ToElement(MapMemory(await UpdateMemoryAsync(
                        Deserialize<UpdateMemoryRequestDto>(request),
                        cancellationToken).ConfigureAwait(false))),
                DesktopApiMethods.SetMemoryEnabled =>
                    DesktopProtocolJson.ToElement(MapMemory(await SetMemoryEnabledAsync(
                        Deserialize<SetMemoryEnabledRequestDto>(request),
                        cancellationToken).ConfigureAwait(false))),
                DesktopApiMethods.DeleteMemory =>
                    DesktopProtocolJson.ToElement(MapMemory(await DeleteMemoryAsync(
                        Deserialize<DeleteMemoryRequestDto>(request),
                        cancellationToken).ConfigureAwait(false))),
                DesktopApiMethods.Shutdown => Shutdown(),
                _ => throw new NotSupportedException("当前 Desktop Host 不支持这个操作。")
            };
            return new DesktopApiResponse(request.RequestId, true, payload);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DesktopApiResponse(
                request.RequestId,
                false,
                Error: DesktopApiErrors.FromException(exception));
        }
    }

    private async Task<SystemStatusDto> SystemStatusAsync(CancellationToken cancellationToken)
    {
        var state = hostState.Snapshot;
        var codex = await codexDiagnostics.CheckAsync(cancellationToken).ConfigureAwait(false);
        var schemaVersion = await taskEntry.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false);
        return new SystemStatusDto(
            state.IsStarted,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.2.0",
            ProcessStartTime.Value,
            state.LocalDevice?.Id.ToString("D") ?? string.Empty,
            state.IsStarted ? "Ready" : "Offline",
            options.DataDirectory,
            options.LogsDirectory,
            new CodexStatusDto(
                codex.IsInstalled,
                codex.IsCompatible,
                codex.Version,
                codex.Message),
            DesktopProtocolVersion.Current,
            schemaVersion);
    }

    private async Task<DashboardDto> DashboardAsync(CancellationToken cancellationToken)
    {
        var system = await SystemStatusAsync(cancellationToken).ConfigureAwait(false);
        var projects = await taskEntry.GetAuthorizedProjectsAsync(cancellationToken)
            .ConfigureAwait(false);
        var tasks = await ListTasksAsync(new ListTasksRequest(), cancellationToken)
            .ConfigureAwait(false);
        var today = DateTimeOffset.Now.Date;
        var active = tasks.Where(task => IsStatus(task.Status, "Pending", "Running", "CancellationRequested"))
            .ToArray();
        var waiting = tasks.Where(task => IsStatus(task.Status, "WaitingForUser")).ToArray();
        return new DashboardDto(
            system,
            projects.Count,
            active.Length,
            waiting.Length,
            tasks.Count(task => task.CompletedAtUtc?.ToLocalTime().Date == today
                                && IsStatus(task.Status, "Succeeded")),
            tasks.Count(task => IsStatus(task.Status, "Failed", "Interrupted")),
            active,
            waiting,
            tasks.Take(8).ToArray());
    }

    private async Task<ProjectDto[]> ListProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = await taskEntry.GetProjectsAsync(true, cancellationToken)
            .ConfigureAwait(false);
        var tasks = await taskEntry.GetTasksAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var results = new List<ProjectDto>();
        foreach (var project in projects)
        {
            var inspection = await projectInspector.InspectAsync(project.RootPath, cancellationToken)
                .ConfigureAwait(false);
            var projectTasks = tasks.Where(task => task.ProjectId == project.Id).ToArray();
            results.Add(new ProjectDto(
                project.Id,
                project.Name,
                project.RootPath,
                project.AuthorizationState.ToString(),
                inspection.IsGitRepository,
                inspection.HasChanges,
                inspection.Summary,
                projectTasks.Length,
                projectTasks.Any(DesktopApiMapper.IsActive),
                project.AuthorizedAtUtc));
        }

        return results.ToArray();
    }

    private async Task<ProjectDto> AddProjectAsync(
        AddProjectRequestDto request,
        CancellationToken cancellationToken)
    {
        var project = await taskEntry.AddProjectAsync(
            request.RootPath,
            request.Name,
            cancellationToken).ConfigureAwait(false);
        var inspection = await projectInspector.InspectAsync(project.RootPath, cancellationToken)
            .ConfigureAwait(false);
        return new ProjectDto(
            project.Id,
            project.Name,
            project.RootPath,
            project.AuthorizationState.ToString(),
            inspection.IsGitRepository,
            inspection.HasChanges,
            inspection.Summary,
            0,
            false,
            project.AuthorizedAtUtc);
    }

    private async Task<ProjectDto> RevokeProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await taskEntry.RevokeProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        var inspection = await projectInspector.InspectAsync(project.RootPath, cancellationToken)
            .ConfigureAwait(false);
        return new ProjectDto(
            project.Id,
            project.Name,
            project.RootPath,
            project.AuthorizationState.ToString(),
            inspection.IsGitRepository,
            inspection.HasChanges,
            inspection.Summary,
            0,
            false,
            project.AuthorizedAtUtc);
    }

    private async Task<int> ClearHistoryAsync(
        ClearHistoryRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!request.Confirmed)
        {
            throw new InvalidOperationException("清理历史记录需要用户确认。");
        }

        return await taskEntry.ClearTerminalHistoryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TaskSummaryDto[]> ListTasksAsync(
        ListTasksRequest request,
        CancellationToken cancellationToken)
    {
        var tasks = await taskEntry.GetTasksAsync(request.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<TaskSummaryDto>();
        foreach (var task in tasks)
        {
            if (!string.IsNullOrWhiteSpace(request.Status)
                && !string.Equals(task.Status.ToString(), request.Status, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    (await taskEntry.GetTaskDetailsAsync(task.Id, cancellationToken).ConfigureAwait(false))
                    ?.Evidence?.VerificationStatus.ToString(),
                    request.Status,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var details = await taskEntry.GetTaskDetailsAsync(task.Id, cancellationToken)
                .ConfigureAwait(false);
            if (details is not null)
            {
                results.Add(DesktopApiMapper.Summary(details.Task, details.Project, details.Evidence));
            }
        }

        return results.ToArray();
    }

    private async Task<TaskDetailsDto?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var details = await taskEntry.GetTaskDetailsAsync(taskId, cancellationToken).ConfigureAwait(false);
        return details is null ? null : DesktopApiMapper.Details(details);
    }

    private async Task<CommandResultDto> CreateTaskAsync(
        CreateTaskRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await taskEntry.CreateTaskAsync(
            new CreateLocalTaskRequest(
                request.ProjectId,
                request.Instruction,
                request.Title,
                request.WorkingDirectoryRelativePath,
                request.IdempotencyKey),
            cancellationToken).ConfigureAwait(false);
        return new CommandResultDto(result.TaskId, result.WasDuplicate);
    }

    private async Task<CommandResultDto> CancelTaskAsync(
        CancelTaskRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await taskEntry.CancelTaskAsync(
            request.TaskId,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        return new CommandResultDto(result.TaskId, result.WasDuplicate);
    }

    private async Task<CommandResultDto> ContinueTaskAsync(
        ContinueTaskRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await taskEntry.ContinueTaskAsync(
            request.TaskId,
            request.Response,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        return new CommandResultDto(result.TaskId, result.WasDuplicate);
    }

    private async Task<ConversationSummaryDto[]> ListConversationsAsync(
        CancellationToken cancellationToken) =>
        (await conversations.GetConversationsAsync(cancellationToken).ConfigureAwait(false))
        .Select(MapConversation)
        .ToArray();

    private async Task<ConversationDetailsDto?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var details = await conversations.GetDetailsAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);
        return details is null
            ? null
            : new ConversationDetailsDto(
                MapConversation(details.Conversation),
                details.Messages.Select(message => new ConversationMessageDto(
                    message.Id,
                    message.SequenceNumber,
                    message.Role.ToString(),
                    message.Content,
                    message.CreatedAtUtc)).ToArray(),
                details.Turns.Select(turn => new ConversationTurnDto(
                    turn.Id,
                    turn.SequenceNumber,
                    turn.Status.ToString(),
                    turn.StartedAtUtc,
                    turn.CompletedAtUtc,
                    turn.FailureMessage)).ToArray());
    }

    private async Task<ConversationSummaryDto> CreateConversationAsync(
        CreateConversationRequestDto request,
        CancellationToken cancellationToken) =>
        MapConversation(await conversations.CreateAsync(request.Title, cancellationToken)
            .ConfigureAwait(false));

    private async Task<ConversationCommandResultDto> SendConversationMessageAsync(
        SendConversationMessageRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await conversations.SendAsync(
                request.ConversationId,
                request.Message,
                request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        return new ConversationCommandResultDto(
            result.ConversationId,
            result.TurnId,
            result.WasDuplicate);
    }

    private async Task<bool> CancelConversationTurnAsync(
        CancelConversationTurnRequestDto request,
        CancellationToken cancellationToken)
    {
        await conversations.CancelAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<SessionTurnCommandResultDto> SubmitSessionInputAsync(
        SessionInputRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await sessions.SubmitAsync(
                request.SessionId,
                request.Text,
                request.InputModality,
                request.IdempotencyKey,
                cancellationToken,
                request.ExpectedIntentKind,
                request.ExpectedTarget,
                request.MemoryItems?.Select(item =>
                    new MemoryOutboundItemReference(item.MemoryId, item.ExpectedVersion)).ToArray(),
                request.PointerAnchorId)
            .ConfigureAwait(false);
        return new SessionTurnCommandResultDto(result.SessionId, result.TurnId, result.WasDuplicate);
    }

    private Task<LocalSessionSnapshot> ProvideSessionProjectAsync(
        ProvideSessionProjectRequestDto request,
        CancellationToken cancellationToken) =>
        sessions.ProvideProjectAsync(
            request.SessionId,
            request.TurnId,
            request.ProjectId,
            cancellationToken);

    private Task<LocalSessionSnapshot> ProvideSessionFileAsync(
        ProvideSessionFileRequestDto request,
        CancellationToken cancellationToken) =>
        sessions.ProvideFileAsync(
            request.SessionId,
            request.TurnId,
            request.FilePath,
            cancellationToken);

    private Task<LocalSessionSnapshot> RespondSessionWindowConsentAsync(
        SessionWindowConsentRequestDto request,
        CancellationToken cancellationToken) =>
        sessions.RespondWindowConsentAsync(
            request.SessionId,
            request.TurnId,
            request.Granted,
            cancellationToken,
            request.ConsentId);

    private Task<LocalSessionSnapshot> RetrySessionTurnAsync(
        CancelSessionTurnRequestDto request,
        CancellationToken cancellationToken) =>
        sessions.RetryTurnAsync(
            request.SessionId,
            request.TurnId,
            cancellationToken);

    private Task<LocalSessionSnapshot> ConfirmSessionTurnAsync(
        SessionTurnConfirmationRequestDto request,
        CancellationToken cancellationToken) =>
        sessions.ConfirmTurnAsync(
            request.SessionId,
            request.TurnId,
            request.Confirmed,
            cancellationToken);

    private Task<LocalSessionSnapshot> ConfirmMemoryOutboundAsync(
        SessionMemoryOutboundConsentRequestDto request,
        CancellationToken cancellationToken) =>
        sessions.ConfirmMemoryOutboundAsync(
            request.SessionId,
            request.TurnId,
            request.ConsentId,
            request.Confirmed,
            cancellationToken);

    private Task<LocalSessionSnapshot> ConfirmPointerAnswerAsync(
        SessionPointerAnswerConsentRequestDto request,
        CancellationToken cancellationToken) =>
        sessions.ConfirmPointerAnswerAsync(
            request.SessionId,
            request.TurnId,
            request.ConsentId,
            request.PreviewHash,
            request.Confirmed,
            cancellationToken);

    private Task<LocalSessionSnapshot> CancelSessionTurnAsync(
        CancelSessionTurnRequestDto request,
        CancellationToken cancellationToken) =>
        sessions.CancelTurnAsync(
            request.SessionId,
            request.TurnId,
            cancellationToken);

    private async Task<SessionProjectionUpdateDto> WaitForSessionUpdateAsync(
        WaitForSessionUpdateRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.WaitMilliseconds is < 0 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request.WaitMilliseconds));
        }

        if (request.CoordinatorInstanceId is null
            || request.CoordinatorStartedAtUtc is null
            || request.SessionId is null)
        {
            var bootstrap = MapSession(await sessions.WaitForChangeAsync(
                    request.KnownChangeVersion,
                    TimeSpan.FromMilliseconds(request.WaitMilliseconds),
                    cancellationToken)
                .ConfigureAwait(false));
            return EnsureProjectionBudget(new SessionProjectionUpdateDto(
                bootstrap is null ? "NoChange" : "Bootstrap",
                bootstrap?.ChangeVersion ?? request.KnownChangeVersion,
                bootstrap?.CoordinatorInstanceId ?? string.Empty,
                bootstrap?.CoordinatorStartedAtUtc ?? default,
                bootstrap?.SessionId,
                [],
                [],
                bootstrap?.Messages.LastOrDefault()?.SequenceNumber ?? 0,
                Bootstrap: bootstrap));
        }

        var update = await sessions.WaitForProjectionAsync(
                request.CoordinatorInstanceId,
                request.CoordinatorStartedAtUtc.Value,
                request.SessionId.Value,
                request.KnownChangeVersion,
                request.KnownMessageSequenceNumber,
                TimeSpan.FromMilliseconds(request.WaitMilliseconds),
                cancellationToken)
            .ConfigureAwait(false);
        return EnsureProjectionBudget(new SessionProjectionUpdateDto(
            update.Kind.ToString(),
            update.ChangeVersion,
            update.CoordinatorInstanceId,
            update.CoordinatorStartedAtUtc,
            update.SessionId,
            update.TurnUpserts.Select(MapSessionTurn).ToArray(),
            update.MessageUpserts.Select(MapConversationMessage).ToArray(),
            update.LastMessageSequenceNumber,
            update.ResetReason));
    }

    private async Task<SessionMessagesPageDto> GetSessionMessagesPageAsync(
        SessionMessagesPageRequestDto request,
        CancellationToken cancellationToken)
    {
        var page = await sessions.GetMessagesPageAsync(
                request.SessionId,
                request.BeforeSequenceNumber,
                request.PageSize,
                cancellationToken)
            .ConfigureAwait(false);
        var response = new SessionMessagesPageDto(
            page.CoordinatorInstanceId,
            page.CoordinatorStartedAtUtc,
            page.SessionId,
            page.Page.Items.Select(MapConversationMessage).ToArray(),
            page.Page.NextBeforeSequenceNumber,
            page.Page.HasMore);
        if (response.Messages.Any(message => !FitsProjectionBudget(response with
            {
                Messages = [message],
                NextBeforeSequenceNumber = message.SequenceNumber,
                HasMore = true
            })))
        {
            throw new SessionProjectionException(
                "session_projection_item_too_large",
                "单条会话内容过大，无法安全显示。");
        }

        if (FitsProjectionBudget(response))
        {
            return response;
        }

        for (var skip = 1; skip < response.Messages.Count; skip++)
        {
            var retained = response.Messages.Skip(skip).ToArray();
            var reduced = response with
            {
                Messages = retained,
                NextBeforeSequenceNumber = retained[0].SequenceNumber,
                HasMore = true
            };
            if (FitsProjectionBudget(reduced))
            {
                return reduced;
            }
        }

        throw new SessionProjectionException(
            "session_projection_item_too_large",
            "单条会话内容过大，无法安全显示。");
    }

    private async Task<SessionTurnDetailsDto> GetSessionTurnAsync(
        SessionTurnGetRequestDto request,
        CancellationToken cancellationToken)
    {
        var details = await sessions.GetTurnDetailsAsync(
                request.SessionId,
                request.TurnId,
                cancellationToken)
            .ConfigureAwait(false);
        var response = new SessionTurnDetailsDto(
            details.CoordinatorInstanceId,
            details.CoordinatorStartedAtUtc,
            details.SessionId,
            MapSessionTurn(details.Turn));
        if (!FitsProjectionBudget(response))
        {
            throw new SessionProjectionException(
                "session_projection_item_too_large",
                "单条会话内容过大，无法安全显示。");
        }

        return response;
    }

    private static SessionProjectionUpdateDto EnsureProjectionBudget(SessionProjectionUpdateDto response)
    {
        if (FitsProjectionBudget(response))
        {
            return response;
        }

        if (response.TurnUpserts.Any(turn => !FitsProjectionBudget(response with
            {
                TurnUpserts = [turn],
                MessageUpserts = [],
                LastMessageSequenceNumber = 0
            })))
        {
            throw new SessionProjectionException(
                "session_projection_item_too_large",
                "单条会话内容过大，无法安全显示。");
        }

        if (response.MessageUpserts.Any(message => !FitsProjectionBudget(response with
            {
                TurnUpserts = [],
                MessageUpserts = [message],
                LastMessageSequenceNumber = message.SequenceNumber
            })))
        {
            throw new SessionProjectionException(
                "session_projection_item_too_large",
                "单条会话内容过大，无法安全显示。");
        }

        for (var take = response.MessageUpserts.Count - 1; take > 0; take--)
        {
            var retained = response.MessageUpserts.Take(take).ToArray();
            var reduced = response with
            {
                MessageUpserts = retained,
                LastMessageSequenceNumber = retained[^1].SequenceNumber
            };
            if (FitsProjectionBudget(reduced))
            {
                return reduced;
            }
        }

        var reset = response with
        {
            Kind = "ResetRequired",
            TurnUpserts = [],
            MessageUpserts = [],
            LastMessageSequenceNumber = 0,
            ResetReason = "projection_budget"
        };
        if (FitsProjectionBudget(reset))
        {
            return reset;
        }

        throw new SessionProjectionException(
            "session_projection_item_too_large",
            "单条会话内容过大，无法安全显示。");
    }

    private static bool FitsProjectionBudget<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, DesktopProtocolJson.Options).Length
        <= MaximumProjectionBytes;

    private static SessionSnapshotDto FitSnapshotBudget(SessionSnapshotDto response)
    {
        if (FitsProjectionBudget(response))
        {
            return response;
        }

        for (var skip = 1; skip < response.Messages.Count; skip++)
        {
            var reduced = response with
            {
                Messages = response.Messages.Skip(skip).ToArray(),
                HasEarlierMessages = true
            };
            if (FitsProjectionBudget(reduced))
            {
                return reduced;
            }
        }

        throw new SessionProjectionException(
            "session_projection_item_too_large",
            "单条会话内容过大，无法安全显示。");
    }

    private SessionSnapshotDto? MapSession(LocalSessionSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var projectedTurns = snapshot.Turns
            .Concat(snapshot.ActiveTurns)
            .GroupBy(turn => turn.Id)
            .Select(group => group.OrderByDescending(turn => turn.Version).First())
            .ToArray();
        var activeProjectedTurns = projectedTurns
            .Where(turn => !SessionTurnPhases.IsTerminal(turn.Phase))
            .ToArray();
        var active = snapshot.ActiveTurns.Select(MapSessionTurn).ToArray();
        var foreground = activeProjectedTurns
            .Where(turn => SessionTurnPhases.IsForegroundWork(turn.Phase))
            .OrderBy(turn => turn.SequenceNumber)
            .LastOrDefault();
        var status = foreground?.Phase.ToString()
                     ?? activeProjectedTurns.OrderBy(turn => turn.SequenceNumber).LastOrDefault()?.Phase.ToString()
                     ?? projectedTurns.OrderBy(turn => turn.SequenceNumber).LastOrDefault()?.Phase.ToString()
                     ?? "Ready";
        var response = new SessionSnapshotDto(
            snapshot.ChangeVersion,
            snapshot.CoordinatorInstanceId,
            snapshot.CoordinatorStartedAtUtc,
            snapshot.Session.Id,
            snapshot.Session.ConversationId,
            snapshot.Session.Title,
            status,
            snapshot.Session.SelectedProjectId,
            snapshot.SelectedProjectName,
            foreground is null ? null : MapSessionTurn(foreground),
            active,
            snapshot.Turns.Select(MapSessionTurn).ToArray(),
            snapshot.Messages.Select(MapConversationMessage).ToArray(),
            snapshot.MemoryOutboundConsents.Select(consent => new MemoryOutboundConsentDto(
                consent.ConsentId,
                consent.TurnId,
                MemoryOutboundConsentState.WaitingForMemoryOutboundConsent.ToString(),
                consent.ProviderId,
                consent.ModelId,
                consent.DestinationOrigin,
                consent.ProjectId,
                consent.ProjectId == snapshot.Session.SelectedProjectId
                    ? snapshot.SelectedProjectName
                    : null,
                consent.Items.Select(item => new MemoryOutboundPreparedItemDto(
                    item.Id,
                    item.Version,
                    item.Category.ToString(),
                    item.Scope.ToString(),
                    item.Title,
                    item.Body,
                    item.CharacterCount)).ToArray(),
                consent.Items.Count,
                consent.TotalCharacters,
                consent.PreparedAtUtc,
                consent.ExpiresAtUtc,
                consent.ManifestHash)).ToArray(),
            snapshot.HasEarlierMessages,
            snapshot.PointerAnswerConsents.Select(consent => new PointerAnswerConsentDto(
                consent.ConsentId,
                consent.TurnId,
                consent.Anchor.AnchorId,
                consent.ProviderId,
                consent.ModelId,
                consent.DestinationOrigin,
                consent.PromptId,
                consent.PromptVersion,
                consent.PromptContentHash,
                consent.Question,
                consent.OcrText,
                consent.Question.Length,
                consent.OcrText.Length,
                consent.OcrLineCount,
                consent.RegionWidth,
                consent.RegionHeight,
                consent.RegionSource,
                consent.PreviewHash,
                consent.PreparedAtUtc,
                consent.ExpiresAtUtc,
                SendsImage: false)).ToArray());
        return FitSnapshotBudget(response);
    }

    private UnifiedSessionTurnDto MapSessionTurn(SessionTurnRecord turn) => new(
        turn.Id,
        turn.SequenceNumber,
        turn.InputText,
        turn.InputModality,
        turn.WorkKind.ToString(),
        turn.Phase.ToString(),
        turn.MissingContext.ToString(),
        turn.IntentKind,
        turn.ConversationTurnId,
        turn.TaskId,
        turn.ProjectId,
        turn.FilePath,
        turn.WindowHandle,
        turn.WindowTitle,
        turn.RequiresConfirmation,
        turn.CancellationRequested,
        sessions.GetDesktopTaskConsentDescription(turn) ?? turn.ResultSummary,
        turn.FailureMessage,
        turn.CreatedAtUtc,
        turn.UpdatedAtUtc,
        turn.CompletedAtUtc,
        turn.ExpectedIntentKind,
        turn.ExpectedTarget,
        turn.PlanTarget,
        turn.MemoryOutboundState.ToString(),
        turn.MemoryOutbound?.ConsentId,
        turn.MemoryOutbound?.ManifestHash,
        sessions.GetWindowConsent(turn));

    private static ConversationMessageDto MapConversationMessage(ConversationMessageRecord message) => new(
        message.Id,
        message.SequenceNumber,
        message.Role.ToString(),
        message.Content,
        message.CreatedAtUtc);

    private static ConversationSummaryDto MapConversation(ConversationRecord conversation) => new(
        conversation.Id,
        conversation.Title,
        conversation.Status.ToString(),
        conversation.CreatedAtUtc,
        conversation.UpdatedAtUtc,
        conversation.LastMessageAtUtc,
        conversation.FailureMessage);

    private async Task<MemoryItem> CreateMemoryAsync(
        CreateMemoryRequestDto request,
        CancellationToken cancellationToken) =>
        await memories.CreateAsync(
            MemoryDraft.Create(
                ParseCategory(request.Category),
                ParseScope(request.Scope, request.ProjectId),
                request.Title,
                request.Body,
                request.ExpiresAtUtc,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

    private Task<MemoryPreviewResult> PreviewMemoriesAsync(
        MemoryPreviewRequestDto request,
        CancellationToken cancellationToken) =>
        memories.PreviewAsync(request.Query, request.ProjectId, cancellationToken);

    private async Task<MemoryItem> UpdateMemoryAsync(
        UpdateMemoryRequestDto request,
        CancellationToken cancellationToken) =>
        await memories.UpdateAsync(
            request.MemoryId,
            request.ExpectedVersion,
            MemoryDraft.Create(
                ParseCategory(request.Category),
                ParseScope(request.Scope, request.ProjectId),
                request.Title,
                request.Body,
                request.ExpiresAtUtc,
                DateTimeOffset.UtcNow),
            cancellationToken: cancellationToken).ConfigureAwait(false);

    private Task<MemoryItem> SetMemoryEnabledAsync(
        SetMemoryEnabledRequestDto request,
        CancellationToken cancellationToken) =>
        memories.SetEnabledAsync(
            request.MemoryId,
            request.Enabled,
            request.ExpectedVersion,
            cancellationToken: cancellationToken);

    private Task<MemoryItem> DeleteMemoryAsync(
        DeleteMemoryRequestDto request,
        CancellationToken cancellationToken) =>
        memories.DeleteAsync(
            request.MemoryId,
            request.ExpectedVersion,
            request.Confirmed,
            cancellationToken: cancellationToken);

    private static MemoryCategory ParseCategory(string value) => value switch
    {
        "UserFact" => MemoryCategory.UserFact,
        "UserPreference" => MemoryCategory.UserPreference,
        "ProjectNote" => MemoryCategory.ProjectNote,
        "Decision" => MemoryCategory.Decision,
        _ => throw new MemoryServiceException(
            MemoryServiceErrorCodes.InvalidRequest,
            "长期记忆类别无效。")
    };

    private static MemoryScope ParseScope(string value, Guid? projectId) => value switch
    {
        "Global" => MemoryScope.Create(MemoryScopeKind.Global, projectId),
        "Project" => MemoryScope.Create(MemoryScopeKind.Project, projectId),
        _ => throw new MemoryServiceException(
            MemoryServiceErrorCodes.InvalidRequest,
            "长期记忆作用域无效。")
    };

    private static MemoryDto MapMemory(MemoryItem item) => new(
        item.Metadata.Id,
        item.Metadata.Category.ToString(),
        item.Metadata.Scope.Kind.ToString(),
        item.Metadata.Scope.ProjectId,
        item.Metadata.Status.ToString(),
        "用户明确保存",
        item.Title,
        item.Body,
        item.Metadata.CreatedAtUtc,
        item.Metadata.UpdatedAtUtc,
        item.Metadata.ExpiresAtUtc,
        item.Metadata.Confidence,
        item.Metadata.Version);

    private static MemoryPreviewResponseDto MapMemoryPreview(MemoryPreviewResult result) => new(
        result.Matches.Select(match => new MemoryPreviewMatchDto(
            MapMemory(match.Item),
            match.Score,
            match.Explanations.ToArray())).ToArray(),
        result.CandidateCount,
        result.SelectedCount,
        result.TotalCharacters);

    private static PointerAnchorDto MapPointerAnchor(PointerAnchor anchor) => new(
        anchor.AnchorId,
        anchor.PhysicalScreenX,
        anchor.PhysicalScreenY,
        anchor.NormalizedX,
        anchor.NormalizedY,
        new PointerWindowIdentityDto(
            anchor.Window.Target.WindowHandle,
            anchor.Window.Target.WindowTitle,
            anchor.Window.Target.ProcessName,
            anchor.Window.Target.ProcessId,
            anchor.Window.Target.ProcessStartTimeUtc,
            anchor.Window.Bounds.Left,
            anchor.Window.Bounds.Top,
            anchor.Window.Bounds.Width,
            anchor.Window.Bounds.Height,
            anchor.Window.DpiX,
            anchor.Window.DpiY),
        anchor.CapturedAtUtc);

    private static PointerRegionOcrResultDto MapPointerRegionResult(PointerRegionOcrResult result) => new(
        result.AnchorId,
        result.Text,
        result.CharacterCount,
        result.LineCount,
        result.RegionWidth,
        result.RegionHeight,
        result.RegionSource,
        result.DiagnosticCode);

    private JsonElement Shutdown()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            applicationLifetime.StopApplication();
        });
        return DesktopProtocolJson.ToElement(true);
    }

    private static T Deserialize<T>(DesktopApiRequest request) =>
        request.Payload.Deserialize<T>(DesktopProtocolJson.Options)
        ?? throw new InvalidDataException("请求内容无效。");

    private static bool IsStatus(string actual, params string[] expected) =>
        expected.Contains(actual, StringComparer.OrdinalIgnoreCase);

    private static class ProcessStartTime
    {
        public static DateTimeOffset Value { get; } = DateTimeOffset.UtcNow;
    }
}

internal static class DesktopApiErrors
{
    public static DesktopApiError FromException(Exception exception)
    {
        var (code, userMessage) = exception switch
        {
            BridgeProtocolException bridgeException =>
                (bridgeException.Code, bridgeException.Message),
            PointerRegionException pointerException =>
                (pointerException.Code, pointerException.Message),
            PointerAnswerException pointerAnswerException =>
                (pointerAnswerException.Code, pointerAnswerException.Message),
            WindowIdentityException windowIdentityException =>
                (windowIdentityException.Code, windowIdentityException.Message),
            DesktopSearchException searchException =>
                (searchException.Code, searchException.Message),
            ApplicationSearchWorkflowException workflowException =>
                (workflowException.Code, workflowException.Message),
            SessionProjectionException projectionException =>
                (projectionException.Code, projectionException.Message),
            MemoryServiceException memoryException =>
                (memoryException.Code, memoryException.Message),
            MemoryValidationException =>
                (MemoryServiceErrorCodes.InvalidRequest, "长期记忆请求不符合要求。"),
            InstalledApplicationResolutionException applicationException =>
                (applicationException.Code, applicationException.Message),
            ChatModelException chatModelException =>
                ($"ai_{SensitiveDataSanitizer.DiagnosticCode(chatModelException.Error.Code, "provider_error")}",
                    SafeAiMessage(chatModelException.Error.UserMessage)),
            ProviderCredentialStoreException credentialStoreException =>
                ($"ai_{SensitiveDataSanitizer.DiagnosticCode(credentialStoreException.Code, "credential_error")}",
                    "AI Provider 密钥无法安全处理，请重新设置后再试。"),
            CodexTaskStartException =>
                ("codex_start_failed", "Codex 没有成功启动。"),
            UnauthorizedAccessException when ContainsAny(
                exception.Message,
                "桌面", "应用", "操作", "确认", "清单") =>
                ("desktop_action_not_authorized", "这次桌面操作没有获得明确授权，已安全停止。"),
            UnauthorizedAccessException => ("project_not_authorized", "这个项目没有授权，无法执行任务。"),
            DirectoryNotFoundException => ("project_missing", "项目目录不存在，请重新选择项目。"),
            FileNotFoundException when ContainsAny(exception.Message, "文件", "选择") =>
                ("file_missing", "刚才选择的文件已经不存在，请重新选择。"),
            FileNotFoundException => ("codex_not_found", "没有找到 Codex，请先安装并登录 Codex。"),
            NotSupportedException when exception.Message.Contains("版本", StringComparison.Ordinal) =>
                ("codex_version_incompatible", "当前 Codex 版本尚未通过兼容验证。"),
            InvalidOperationException when exception.Message.Contains("不是 Git", StringComparison.Ordinal) =>
                ("project_not_git", "选择的目录不是 Git 项目。"),
            InvalidOperationException when ContainsAny(exception.Message, "登录", "login", "authentication", "401") =>
                ("codex_login_required", "Codex 登录已经失效。"),
            InvalidOperationException when ContainsAny(exception.Message, "网络", "network", "connection") =>
                ("network_unavailable", "当前网络不可用，Codex 无法继续。"),
            InvalidOperationException when ContainsAny(
                exception.Message, "搜索框", "输入框", "目标窗口", "控件结构", "切到前台") =>
                ("desktop_target_unavailable", "没有找到可安全操作的目标窗口或输入框。"),
            InvalidOperationException when ContainsAny(exception.Message, "操作计划", "操作确认", "超时", "失效") =>
                ("action_plan_expired", "这次确认已经失效，请重新说出或输入指令。"),
            InvalidDataException => ("data_invalid", "本地任务数据无法读取。"),
            InvalidOperationException => ("operation_invalid", FriendlyInvalidOperation(exception.Message)),
            ArgumentException => ("input_invalid", "输入内容不符合要求，请检查后重试。"),
            _ when exception.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) =>
                ("database_unavailable", "本地任务数据库无法打开。"),
            _ => ("host_error", "Desktop Host 执行操作时遇到错误。")
        };
        return new DesktopApiError(
            code,
            SensitiveDataSanitizer.Redact(userMessage),
            SensitiveDataSanitizer.ExceptionType(exception));
    }

    private static string FriendlyInvalidOperation(string message) =>
        message.Contains("不是 Git 项目", StringComparison.Ordinal)
            ? "请选择一个 Git 项目目录。"
            : message.Contains("正在运行", StringComparison.Ordinal)
                ? "项目仍有任务正在运行，完成或取消任务后再删除授权。"
                : message.Contains("WaitingForUser", StringComparison.Ordinal)
            ? "任务当前不需要补充指令。"
            : message.Contains("任务不存在", StringComparison.Ordinal)
                ? "没有找到这个任务。"
                : message.Contains("项目不存在", StringComparison.Ordinal)
                    ? "没有找到这个项目。"
                    : "当前状态下不能执行这个操作。";

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string SafeAiMessage(string? message)
    {
        var redacted = SensitiveDataSanitizer.Redact(message);
        return string.IsNullOrWhiteSpace(redacted)
            ? "AI Provider 暂时无法完成请求，请稍后再试。"
            : redacted.Length <= 300 ? redacted : redacted[..300];
    }
}
