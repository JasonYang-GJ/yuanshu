using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace ScreenGuide.DesktopProtocol;

public interface IDesktopApiClient
{
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    Task<SystemStatusDto> GetSystemStatusAsync(CancellationToken cancellationToken = default);

    Task<DashboardDto> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(CancellationToken cancellationToken = default);

    Task<ProjectDto> AddProjectAsync(
        AddProjectRequestDto request,
        CancellationToken cancellationToken = default);

    Task<ProjectDto> RevokeProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskSummaryDto>> ListTasksAsync(
        Guid? projectId = null,
        string? status = null,
        CancellationToken cancellationToken = default);

    Task<TaskDetailsDto?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<CommandResultDto> CreateTaskAsync(
        CreateTaskRequestDto request,
        CancellationToken cancellationToken = default);

    Task<CommandResultDto> CancelTaskAsync(
        Guid taskId,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task<CommandResultDto> ContinueTaskAsync(
        Guid taskId,
        string response,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task<int> ClearHistoryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DesktopApplicationDto>> ListDesktopApplicationsAsync(
        CancellationToken cancellationToken = default);

    Task<DesktopActionResultDto> ExecuteDesktopActionAsync(
        ExecuteDesktopActionRequestDto request,
        CancellationToken cancellationToken = default);

    Task<AssistantIntentPlanDto> PlanAssistantCommandAsync(
        PlanAssistantCommandRequestDto request,
        CancellationToken cancellationToken = default);

    Task<AssistantCommandResultDto> ExecuteAssistantCommandAsync(
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken = default);

    Task<bool> CancelWindowObservationAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    Task<PointerAnchorDto> PreparePointerRegionAsync(
        PreparePointerRegionRequestDto request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<PointerAnchorDto>(
            new NotSupportedException("此客户端尚未实现本机指针区域准备。"));

    Task<PointerRegionOcrResultDto> ReadPointerRegionAsync(
        Guid anchorId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<PointerRegionOcrResultDto>(
            new NotSupportedException("此客户端尚未实现本机指针区域读取。"));

    Task<bool> CancelPointerRegionAsync(
        Guid anchorId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(
            new NotSupportedException("此客户端尚未实现本机指针区域取消。"));

    Task<IReadOnlyList<ConversationSummaryDto>> ListConversationsAsync(
        CancellationToken cancellationToken = default);

    Task<ConversationDetailsDto?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<ConversationSummaryDto> CreateConversationAsync(
        string? title = null,
        CancellationToken cancellationToken = default);

    Task<ConversationCommandResultDto> SendConversationMessageAsync(
        Guid conversationId,
        string message,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task CancelConversationTurnAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<SessionSnapshotDto?> GetCurrentSessionAsync(CancellationToken cancellationToken = default);

    Task<SessionSnapshotDto> StartNewSessionAsync(
        string? title = null,
        CancellationToken cancellationToken = default);

    Task<SessionSnapshotDto> SetCurrentSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<SessionTurnCommandResultDto> SubmitSessionInputAsync(
        SessionInputRequestDto request,
        CancellationToken cancellationToken = default);

    Task<SessionSnapshotDto> ProvideSessionProjectAsync(
        Guid sessionId,
        Guid turnId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<SessionSnapshotDto> ProvideSessionFileAsync(
        Guid sessionId,
        Guid turnId,
        string filePath,
        CancellationToken cancellationToken = default);

    Task<SessionSnapshotDto> RespondSessionWindowConsentAsync(
        Guid sessionId,
        Guid turnId,
        bool granted,
        CancellationToken cancellationToken = default,
        Guid? consentId = null);

    Task<SessionSnapshotDto> RetrySessionTurnAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default);

    Task<SessionSnapshotDto> ConfirmSessionTurnAsync(
        Guid sessionId,
        Guid turnId,
        bool confirmed,
        CancellationToken cancellationToken = default);

    Task<SessionSnapshotDto> ConfirmMemoryOutboundAsync(
        Guid sessionId,
        Guid turnId,
        Guid consentId,
        bool confirmed,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SessionSnapshotDto>(
            new NotSupportedException("此客户端尚未实现长期记忆出站确认。"));

    Task<SessionSnapshotDto> ConfirmPointerAnswerAsync(
        Guid sessionId,
        Guid turnId,
        Guid consentId,
        string previewHash,
        bool confirmed,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SessionSnapshotDto>(
            new NotSupportedException("此客户端尚未实现指针区域文字出站确认。"));

    Task<SessionSnapshotDto> CancelSessionTurnAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default);

    Task<SessionSnapshotDto?> WaitForSessionUpdateAsync(
        long knownChangeVersion,
        int waitMilliseconds = 20_000,
        CancellationToken cancellationToken = default);

    Task<SessionProjectionUpdateDto> WaitForSessionProjectionAsync(
        SessionProjectionCursorDto cursor,
        CancellationToken cancellationToken = default);

    Task<SessionMessagesPageDto> GetSessionMessagesPageAsync(
        Guid sessionId,
        long? beforeSequenceNumber,
        int pageSize = 50,
        CancellationToken cancellationToken = default);

    Task<SessionTurnDetailsDto> GetSessionTurnAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default);

    Task<AiSettingsDto> GetAiSettingsAsync(CancellationToken cancellationToken = default);

    Task<AiSettingsDto> SetChatRouteAsync(
        SetChatRouteRequestDto request,
        CancellationToken cancellationToken = default);

    Task<AiProviderCredentialStatusDto> SetProviderCredentialAsync(
        SetProviderCredentialRequestDto request,
        CancellationToken cancellationToken = default);

    Task<AiProviderCredentialStatusDto> DeleteProviderCredentialAsync(
        ProviderIdRequestDto request,
        CancellationToken cancellationToken = default);

    Task<AiProviderHealthDto> CheckAiProviderHealthAsync(
        ProviderIdRequestDto request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryDto>> ListMemoriesAsync(
        CancellationToken cancellationToken = default);

    Task<MemoryPreviewResponseDto> PreviewMemoriesAsync(
        MemoryPreviewRequestDto request,
        CancellationToken cancellationToken = default);

    Task<MemoryDto> GetMemoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken = default);

    Task<MemoryDto> CreateMemoryAsync(
        CreateMemoryRequestDto request,
        CancellationToken cancellationToken = default);

    Task<MemoryDto> UpdateMemoryAsync(
        UpdateMemoryRequestDto request,
        CancellationToken cancellationToken = default);

    Task<MemoryDto> SetMemoryEnabledAsync(
        SetMemoryEnabledRequestDto request,
        CancellationToken cancellationToken = default);

    Task<MemoryDto> DeleteMemoryAsync(
        DeleteMemoryRequestDto request,
        CancellationToken cancellationToken = default);

    Task ShutdownHostAsync(CancellationToken cancellationToken = default);
}

public sealed class DesktopApiClient(
    string? pipeName = null,
    TimeSpan? connectTimeout = null) : IDesktopApiClient
{
    private readonly string _pipeName = pipeName
        ?? Environment.GetEnvironmentVariable("SCREEN_GUIDE_PIPE_NAME")
        ?? DesktopIpcEndpoint.CurrentUserPipeName();
    private readonly TimeSpan _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await CallAsync<EmptyRequest, SystemStatusDto>(
                DesktopApiMethods.Ping,
                new EmptyRequest(),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return false;
        }
        catch (DesktopApiException exception) when (
            string.Equals(exception.Error.Code, "protocol_version_unsupported", StringComparison.Ordinal))
        {
            return false;
        }
    }

    public Task<SystemStatusDto> GetSystemStatusAsync(CancellationToken cancellationToken = default) =>
        CallAsync<EmptyRequest, SystemStatusDto>(
            DesktopApiMethods.GetSystemStatus,
            new EmptyRequest(),
            cancellationToken);

    public Task<DashboardDto> GetDashboardAsync(CancellationToken cancellationToken = default) =>
        CallAsync<EmptyRequest, DashboardDto>(
            DesktopApiMethods.GetDashboard,
            new EmptyRequest(),
            cancellationToken);

    public async Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(
        CancellationToken cancellationToken = default) =>
        await CallAsync<EmptyRequest, ProjectDto[]>(
            DesktopApiMethods.ListProjects,
            new EmptyRequest(),
            cancellationToken).ConfigureAwait(false);

    public Task<ProjectDto> AddProjectAsync(
        AddProjectRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<AddProjectRequestDto, ProjectDto>(
            DesktopApiMethods.AddProject,
            request,
            cancellationToken);

    public Task<ProjectDto> RevokeProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        CallAsync<RevokeProjectRequestDto, ProjectDto>(
            DesktopApiMethods.RevokeProject,
            new RevokeProjectRequestDto(projectId),
            cancellationToken);

    public async Task<IReadOnlyList<TaskSummaryDto>> ListTasksAsync(
        Guid? projectId = null,
        string? status = null,
        CancellationToken cancellationToken = default) =>
        await CallAsync<ListTasksRequest, TaskSummaryDto[]>(
            DesktopApiMethods.ListTasks,
            new ListTasksRequest(projectId, status),
            cancellationToken).ConfigureAwait(false);

    public Task<TaskDetailsDto?> GetTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        CallAsync<TaskIdRequest, TaskDetailsDto?>(
            DesktopApiMethods.GetTask,
            new TaskIdRequest(taskId),
            cancellationToken);

    public Task<CommandResultDto> CreateTaskAsync(
        CreateTaskRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<CreateTaskRequestDto, CommandResultDto>(
            DesktopApiMethods.CreateTask,
            request,
            cancellationToken);

    public Task<CommandResultDto> CancelTaskAsync(
        Guid taskId,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        CallAsync<CancelTaskRequestDto, CommandResultDto>(
            DesktopApiMethods.CancelTask,
            new CancelTaskRequestDto(taskId, idempotencyKey),
            cancellationToken);

    public Task<CommandResultDto> ContinueTaskAsync(
        Guid taskId,
        string response,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        CallAsync<ContinueTaskRequestDto, CommandResultDto>(
            DesktopApiMethods.ContinueTask,
            new ContinueTaskRequestDto(taskId, response, idempotencyKey),
            cancellationToken);

    public Task<int> ClearHistoryAsync(CancellationToken cancellationToken = default) =>
        CallAsync<ClearHistoryRequestDto, int>(
            DesktopApiMethods.ClearHistory,
            new ClearHistoryRequestDto(true),
            cancellationToken);

    public async Task<IReadOnlyList<DesktopApplicationDto>> ListDesktopApplicationsAsync(
        CancellationToken cancellationToken = default) =>
        await CallAsync<EmptyRequest, DesktopApplicationDto[]>(
            DesktopApiMethods.ListDesktopApplications,
            new EmptyRequest(),
            cancellationToken).ConfigureAwait(false);

    public Task<DesktopActionResultDto> ExecuteDesktopActionAsync(
        ExecuteDesktopActionRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<ExecuteDesktopActionRequestDto, DesktopActionResultDto>(
            DesktopApiMethods.ExecuteDesktopAction,
            request,
            cancellationToken);

    public Task<AssistantIntentPlanDto> PlanAssistantCommandAsync(
        PlanAssistantCommandRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<PlanAssistantCommandRequestDto, AssistantIntentPlanDto>(
            DesktopApiMethods.PlanAssistantCommand,
            request,
            cancellationToken);

    public Task<AssistantCommandResultDto> ExecuteAssistantCommandAsync(
        ExecuteAssistantCommandRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<ExecuteAssistantCommandRequestDto, AssistantCommandResultDto>(
            DesktopApiMethods.ExecuteAssistantCommand,
            request,
            cancellationToken);

    public Task<bool> CancelWindowObservationAsync(
        string operationId,
        CancellationToken cancellationToken = default) =>
        CallAsync<CancelWindowObservationRequestDto, bool>(
            DesktopApiMethods.CancelWindowObservation,
            new CancelWindowObservationRequestDto(operationId),
            cancellationToken);

    public async Task<IReadOnlyList<ConversationSummaryDto>> ListConversationsAsync(
        CancellationToken cancellationToken = default) =>
        await CallAsync<EmptyRequest, ConversationSummaryDto[]>(
            DesktopApiMethods.ListConversations,
            new EmptyRequest(),
            cancellationToken).ConfigureAwait(false);

    public Task<ConversationDetailsDto?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        CallAsync<ConversationIdRequestDto, ConversationDetailsDto?>(
            DesktopApiMethods.GetConversation,
            new ConversationIdRequestDto(conversationId),
            cancellationToken);

    public Task<ConversationSummaryDto> CreateConversationAsync(
        string? title = null,
        CancellationToken cancellationToken = default) =>
        CallAsync<CreateConversationRequestDto, ConversationSummaryDto>(
            DesktopApiMethods.CreateConversation,
            new CreateConversationRequestDto(title),
            cancellationToken);

    public Task<ConversationCommandResultDto> SendConversationMessageAsync(
        Guid conversationId,
        string message,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        CallAsync<SendConversationMessageRequestDto, ConversationCommandResultDto>(
            DesktopApiMethods.SendConversationMessage,
            new SendConversationMessageRequestDto(conversationId, message, idempotencyKey),
            cancellationToken);

    public Task CancelConversationTurnAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        CallAsync<CancelConversationTurnRequestDto, bool>(
            DesktopApiMethods.CancelConversationTurn,
            new CancelConversationTurnRequestDto(conversationId),
            cancellationToken);

    public Task<SessionSnapshotDto?> GetCurrentSessionAsync(
        CancellationToken cancellationToken = default) =>
        CallAsync<EmptyRequest, SessionSnapshotDto?>(
            DesktopApiMethods.GetCurrentSession,
            new EmptyRequest(),
            cancellationToken);

    public Task<SessionSnapshotDto> StartNewSessionAsync(
        string? title = null,
        CancellationToken cancellationToken = default) =>
        CallAsync<StartNewSessionRequestDto, SessionSnapshotDto>(
            DesktopApiMethods.StartNewSession,
            new StartNewSessionRequestDto(title),
            cancellationToken);

    public Task<SessionSnapshotDto> SetCurrentSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        CallAsync<SessionIdRequestDto, SessionSnapshotDto>(
            DesktopApiMethods.SetCurrentSession,
            new SessionIdRequestDto(sessionId),
            cancellationToken);

    public Task<SessionTurnCommandResultDto> SubmitSessionInputAsync(
        SessionInputRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<SessionInputRequestDto, SessionTurnCommandResultDto>(
            DesktopApiMethods.SubmitSessionInput,
            request,
            cancellationToken);

    public Task<SessionSnapshotDto> ProvideSessionProjectAsync(
        Guid sessionId,
        Guid turnId,
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        CallAsync<ProvideSessionProjectRequestDto, SessionSnapshotDto>(
            DesktopApiMethods.ProvideSessionProject,
            new ProvideSessionProjectRequestDto(sessionId, turnId, projectId),
            cancellationToken);

    public Task<SessionSnapshotDto> ProvideSessionFileAsync(
        Guid sessionId,
        Guid turnId,
        string filePath,
        CancellationToken cancellationToken = default) =>
        CallAsync<ProvideSessionFileRequestDto, SessionSnapshotDto>(
            DesktopApiMethods.ProvideSessionFile,
            new ProvideSessionFileRequestDto(sessionId, turnId, filePath),
            cancellationToken);

    public Task<SessionSnapshotDto> RespondSessionWindowConsentAsync(
        Guid sessionId,
        Guid turnId,
        bool granted,
        CancellationToken cancellationToken = default,
        Guid? consentId = null) =>
        CallAsync<SessionWindowConsentRequestDto, SessionSnapshotDto>(
            DesktopApiMethods.RespondSessionWindowConsent,
            new SessionWindowConsentRequestDto(sessionId, turnId, granted, consentId),
            cancellationToken);

    public Task<SessionSnapshotDto> RetrySessionTurnAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default) =>
        CallAsync<CancelSessionTurnRequestDto, SessionSnapshotDto>(
            DesktopApiMethods.RetrySessionTurn,
            new CancelSessionTurnRequestDto(sessionId, turnId),
            cancellationToken);

    public Task<SessionSnapshotDto> ConfirmSessionTurnAsync(
        Guid sessionId,
        Guid turnId,
        bool confirmed,
        CancellationToken cancellationToken = default) =>
        CallAsync<SessionTurnConfirmationRequestDto, SessionSnapshotDto>(
            DesktopApiMethods.ConfirmSessionTurn,
            new SessionTurnConfirmationRequestDto(sessionId, turnId, confirmed),
            cancellationToken);

    public Task<PointerAnchorDto> PreparePointerRegionAsync(
        PreparePointerRegionRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<PreparePointerRegionRequestDto, PointerAnchorDto>(
            DesktopApiMethods.PreparePointerRegion,
            request,
            cancellationToken);

    public Task<PointerRegionOcrResultDto> ReadPointerRegionAsync(
        Guid anchorId,
        CancellationToken cancellationToken = default) =>
        CallAsync<PointerRegionRequestDto, PointerRegionOcrResultDto>(
            DesktopApiMethods.ReadPointerRegion,
            new PointerRegionRequestDto(anchorId),
            cancellationToken);

    public Task<bool> CancelPointerRegionAsync(
        Guid anchorId,
        CancellationToken cancellationToken = default) =>
        CallAsync<PointerRegionRequestDto, bool>(
            DesktopApiMethods.CancelPointerRegion,
            new PointerRegionRequestDto(anchorId),
            cancellationToken);

    public Task<SessionSnapshotDto> ConfirmMemoryOutboundAsync(
        Guid sessionId,
        Guid turnId,
        Guid consentId,
        bool confirmed,
        CancellationToken cancellationToken = default) =>
        CallAsync<SessionMemoryOutboundConsentRequestDto, SessionSnapshotDto>(
            DesktopApiMethods.ConfirmMemoryOutbound,
            new SessionMemoryOutboundConsentRequestDto(sessionId, turnId, consentId, confirmed),
            cancellationToken);

    public Task<SessionSnapshotDto> ConfirmPointerAnswerAsync(
        Guid sessionId,
        Guid turnId,
        Guid consentId,
        string previewHash,
        bool confirmed,
        CancellationToken cancellationToken = default) =>
        CallAsync<SessionPointerAnswerConsentRequestDto, SessionSnapshotDto>(
            DesktopApiMethods.ConfirmPointerAnswer,
            new SessionPointerAnswerConsentRequestDto(
                sessionId,
                turnId,
                consentId,
                previewHash,
                confirmed),
            cancellationToken);

    public Task<SessionSnapshotDto> CancelSessionTurnAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default) =>
        CallAsync<CancelSessionTurnRequestDto, SessionSnapshotDto>(
            DesktopApiMethods.CancelSessionTurn,
            new CancelSessionTurnRequestDto(sessionId, turnId),
            cancellationToken);

    public async Task<SessionSnapshotDto?> WaitForSessionUpdateAsync(
        long knownChangeVersion,
        int waitMilliseconds = 20_000,
        CancellationToken cancellationToken = default)
    {
        var update = await CallAsync<WaitForSessionUpdateRequestDto, SessionProjectionUpdateDto>(
            DesktopApiMethods.WaitForSessionUpdate,
            new WaitForSessionUpdateRequestDto(knownChangeVersion, waitMilliseconds),
            cancellationToken).ConfigureAwait(false);
        return update.Bootstrap;
    }

    public Task<SessionProjectionUpdateDto> WaitForSessionProjectionAsync(
        SessionProjectionCursorDto cursor,
        CancellationToken cancellationToken = default) =>
        CallAsync<WaitForSessionUpdateRequestDto, SessionProjectionUpdateDto>(
            DesktopApiMethods.WaitForSessionUpdate,
            new WaitForSessionUpdateRequestDto(
                cursor.KnownChangeVersion,
                cursor.WaitMilliseconds,
                cursor.CoordinatorInstanceId,
                cursor.CoordinatorStartedAtUtc,
                cursor.SessionId,
                cursor.KnownMessageSequenceNumber),
            cancellationToken);

    public Task<SessionMessagesPageDto> GetSessionMessagesPageAsync(
        Guid sessionId,
        long? beforeSequenceNumber,
        int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        CallAsync<SessionMessagesPageRequestDto, SessionMessagesPageDto>(
            DesktopApiMethods.GetSessionMessagesPage,
            new SessionMessagesPageRequestDto(sessionId, beforeSequenceNumber, pageSize),
            cancellationToken);

    public Task<SessionTurnDetailsDto> GetSessionTurnAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default) =>
        CallAsync<SessionTurnGetRequestDto, SessionTurnDetailsDto>(
            DesktopApiMethods.GetSessionTurn,
            new SessionTurnGetRequestDto(sessionId, turnId),
            cancellationToken);

    public Task<AiSettingsDto> GetAiSettingsAsync(
        CancellationToken cancellationToken = default) =>
        CallAsync<EmptyRequest, AiSettingsDto>(
            DesktopApiMethods.GetAiSettings,
            new EmptyRequest(),
            cancellationToken);

    public Task<AiSettingsDto> SetChatRouteAsync(
        SetChatRouteRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<SetChatRouteRequestDto, AiSettingsDto>(
            DesktopApiMethods.SetChatRoute,
            request,
            cancellationToken);

    public Task<AiProviderCredentialStatusDto> SetProviderCredentialAsync(
        SetProviderCredentialRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<SetProviderCredentialRequestDto, AiProviderCredentialStatusDto>(
            DesktopApiMethods.SetProviderCredential,
            request,
            cancellationToken);

    public Task<AiProviderCredentialStatusDto> DeleteProviderCredentialAsync(
        ProviderIdRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<ProviderIdRequestDto, AiProviderCredentialStatusDto>(
            DesktopApiMethods.DeleteProviderCredential,
            request,
            cancellationToken);

    public Task<AiProviderHealthDto> CheckAiProviderHealthAsync(
        ProviderIdRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<ProviderIdRequestDto, AiProviderHealthDto>(
            DesktopApiMethods.CheckAiProviderHealth,
            request,
            cancellationToken);

    public async Task<IReadOnlyList<MemoryDto>> ListMemoriesAsync(
        CancellationToken cancellationToken = default) =>
        await CallAsync<EmptyRequest, MemoryDto[]>(
            DesktopApiMethods.ListMemories,
            new EmptyRequest(),
            cancellationToken).ConfigureAwait(false);

    public Task<MemoryPreviewResponseDto> PreviewMemoriesAsync(
        MemoryPreviewRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<MemoryPreviewRequestDto, MemoryPreviewResponseDto>(
            DesktopApiMethods.PreviewMemories,
            request,
            cancellationToken);

    public Task<MemoryDto> GetMemoryAsync(
        Guid memoryId,
        CancellationToken cancellationToken = default) =>
        CallAsync<MemoryIdRequestDto, MemoryDto>(
            DesktopApiMethods.GetMemory,
            new MemoryIdRequestDto(memoryId),
            cancellationToken);

    public Task<MemoryDto> CreateMemoryAsync(
        CreateMemoryRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<CreateMemoryRequestDto, MemoryDto>(
            DesktopApiMethods.CreateMemory,
            request,
            cancellationToken);

    public Task<MemoryDto> UpdateMemoryAsync(
        UpdateMemoryRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<UpdateMemoryRequestDto, MemoryDto>(
            DesktopApiMethods.UpdateMemory,
            request,
            cancellationToken);

    public Task<MemoryDto> SetMemoryEnabledAsync(
        SetMemoryEnabledRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<SetMemoryEnabledRequestDto, MemoryDto>(
            DesktopApiMethods.SetMemoryEnabled,
            request,
            cancellationToken);

    public Task<MemoryDto> DeleteMemoryAsync(
        DeleteMemoryRequestDto request,
        CancellationToken cancellationToken = default) =>
        CallAsync<DeleteMemoryRequestDto, MemoryDto>(
            DesktopApiMethods.DeleteMemory,
            request,
            cancellationToken);

    public Task ShutdownHostAsync(CancellationToken cancellationToken = default) =>
        CallAsync<EmptyRequest, bool>(
            DesktopApiMethods.Shutdown,
            new EmptyRequest(),
            cancellationToken);

    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string method,
        TRequest payload,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Desktop Host 连接超时。");
        }

        var requestId = Guid.NewGuid().ToString("N");
        await DesktopIpcFraming.WriteAsync(
            pipe,
            new DesktopApiRequest(
                requestId,
                method,
                DesktopProtocolJson.ToElement(payload)),
            cancellationToken).ConfigureAwait(false);
        var response = await DesktopIpcFraming.ReadAsync<DesktopApiResponse>(
            pipe,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(requestId, response.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Desktop Host 返回了不匹配的响应。");
        }

        if (response.ProtocolVersion < DesktopProtocolVersion.MinimumSupported
            || response.ProtocolVersion > DesktopProtocolVersion.Current)
        {
            throw new NotSupportedException($"Desktop Host 协议版本 {response.ProtocolVersion} 不受支持。");
        }

        if (!response.Success)
        {
            throw new DesktopApiException(
                response.Error ?? new DesktopApiError("unknown", "Desktop Host 请求失败。"));
        }

        if (response.Payload is null)
        {
            return default!;
        }

        return response.Payload.Value.Deserialize<TResponse>(DesktopProtocolJson.Options)!;
    }
}
