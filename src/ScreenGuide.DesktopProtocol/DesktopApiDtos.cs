namespace ScreenGuide.DesktopProtocol;

public sealed record EmptyRequest;

public sealed record TaskIdRequest(Guid TaskId);

public sealed record ListTasksRequest(Guid? ProjectId = null, string? Status = null);

public sealed record CreateTaskRequestDto(
    Guid ProjectId,
    string Instruction,
    string? Title = null,
    string WorkingDirectoryRelativePath = ".",
    string? IdempotencyKey = null);

public sealed record ContinueTaskRequestDto(
    Guid TaskId,
    string Response,
    string? IdempotencyKey = null);

public sealed record CancelTaskRequestDto(Guid TaskId, string? IdempotencyKey = null);

public sealed record AddProjectRequestDto(string RootPath, string? Name = null);

public sealed record RevokeProjectRequestDto(Guid ProjectId);

public sealed record ClearHistoryRequestDto(bool Confirmed);

public sealed record DesktopApplicationDto(string Id, string DisplayName);

public sealed record ExecuteDesktopActionRequestDto(
    string ActionKind,
    string Target,
    bool Confirmed,
    string? IdempotencyKey = null,
    long? WindowHandle = null,
    string? WindowTitle = null,
    string? ApplicationId = null,
    string AuthorizationSource = "VisibleConfirmation");

public sealed record DesktopActionResultDto(
    Guid CommandId,
    Guid? InvocationId,
    bool Succeeded,
    string Message,
    bool WasDuplicate,
    int? ProcessId = null);

public sealed record PlanAssistantCommandRequestDto(
    string Text,
    string? SelectedFilePath = null,
    Guid? SelectedProjectId = null,
    bool ForegroundObservationConsent = false,
    string InputModality = "Text");

public sealed record ForegroundApplicationDto(
    long WindowHandle,
    string WindowTitle,
    string ProcessName,
    DateTimeOffset ObservedAtUtc);

public sealed record AssistantIntentPlanDto(
    Guid PlanId,
    string IntentKind,
    string Readiness,
    string UserSummary,
    bool RequiresConfirmation,
    string? ConfirmationText,
    string? MissingContext,
    DateTimeOffset? ExpiresAtUtc,
    ForegroundApplicationDto? ForegroundApplication = null,
    string? CanonicalTarget = null);

public sealed record ExecuteAssistantCommandRequestDto(
    Guid PlanId,
    bool Confirmed,
    string? IdempotencyKey = null,
    string AuthorizationSource = "VisibleConfirmation");

public sealed record CancelWindowObservationRequestDto(string OperationId);

public sealed record PreparePointerRegionRequestDto(
    bool Confirmed,
    string AuthorizationSource = "VisibleConfirmation");

public sealed record PointerWindowIdentityDto(
    long WindowHandle,
    string WindowTitle,
    string ProcessName,
    int ProcessId,
    DateTimeOffset ProcessStartTimeUtc,
    int WindowLeft,
    int WindowTop,
    int WindowWidth,
    int WindowHeight,
    uint DpiX,
    uint DpiY);

public sealed record PointerAnchorDto(
    Guid AnchorId,
    int PhysicalScreenX,
    int PhysicalScreenY,
    double NormalizedX,
    double NormalizedY,
    PointerWindowIdentityDto Window,
    DateTimeOffset CapturedAtUtc);

public sealed record PointerRegionRequestDto(Guid AnchorId);

public sealed record PointerRegionOcrResultDto(
    Guid AnchorId,
    string Text,
    int CharacterCount,
    int LineCount,
    int RegionWidth,
    int RegionHeight,
    string RegionSource,
    string DiagnosticCode)
{
    public override string ToString() =>
        $"PointerRegionOcrResultDto {{ AnchorId = {AnchorId}, CharacterCount = {CharacterCount}, LineCount = {LineCount}, RegionWidth = {RegionWidth}, RegionHeight = {RegionHeight}, RegionSource = {RegionSource}, DiagnosticCode = {DiagnosticCode}, Text = [REDACTED] }}";
}

public sealed record AssistantCommandResultDto(
    string IntentKind,
    string Status,
    string UserSummary,
    string VerificationStatus,
    Guid? TaskId = null,
    Guid? ConversationId = null,
    Guid? CommandId = null,
    bool WasDuplicate = false,
    AssistantActionEvidenceDto? Evidence = null);

public sealed record AssistantActionEvidenceDto(
    Guid EvidenceId,
    string VerificationStatus,
    string UserSummary,
    IReadOnlyList<string> VerifiedFacts,
    IReadOnlyList<string> UnverifiedFacts,
    DateTimeOffset GeneratedAtUtc);

public sealed record ConversationIdRequestDto(Guid ConversationId);

public sealed record CreateConversationRequestDto(string? Title = null);

public sealed record SendConversationMessageRequestDto(
    Guid ConversationId,
    string Message,
    string? IdempotencyKey = null);

public sealed record CancelConversationTurnRequestDto(Guid ConversationId);

public sealed record ConversationCommandResultDto(
    Guid ConversationId,
    Guid TurnId,
    bool WasDuplicate);

public sealed record ConversationSummaryDto(
    Guid Id,
    string Title,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastMessageAtUtc,
    string? FailureMessage);

public sealed record ConversationMessageDto(
    Guid Id,
    long SequenceNumber,
    string Role,
    string Content,
    DateTimeOffset CreatedAtUtc);

public sealed record ConversationTurnDto(
    Guid Id,
    int SequenceNumber,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureMessage);

public sealed record ConversationDetailsDto(
    ConversationSummaryDto Summary,
    IReadOnlyList<ConversationMessageDto> Messages,
    IReadOnlyList<ConversationTurnDto> Turns);

public sealed record SessionIdRequestDto(Guid SessionId);

public sealed record StartNewSessionRequestDto(string? Title = null);

public sealed record MemoryOutboundItemReferenceDto(Guid MemoryId, int ExpectedVersion);

public sealed record SessionInputRequestDto(
    string Text,
    string InputModality = "Text",
    string? IdempotencyKey = null,
    Guid? SessionId = null,
    string? ExpectedIntentKind = null,
    string? ExpectedTarget = null,
    IReadOnlyList<MemoryOutboundItemReferenceDto>? MemoryItems = null,
    Guid? PointerAnchorId = null)
{
    public override string ToString() =>
        $"SessionInputRequestDto {{ SessionId = {SessionId}, InputModality = {InputModality}, MemoryItemCount = {MemoryItems?.Count ?? 0}, PointerAnchorId = {PointerAnchorId}, Text = [REDACTED] }}";
}

public sealed record ProvideSessionProjectRequestDto(
    Guid SessionId,
    Guid TurnId,
    Guid ProjectId);

public sealed record ProvideSessionFileRequestDto(
    Guid SessionId,
    Guid TurnId,
    string FilePath);

public sealed record SessionWindowConsentRequestDto(
    Guid SessionId,
    Guid TurnId,
    bool Granted,
    Guid? ConsentId = null);

public sealed record SessionWindowConsentDto(
    Guid ConsentId,
    string Purpose,
    long WindowHandle,
    string WindowTitle);

public sealed record SessionTurnConfirmationRequestDto(
    Guid SessionId,
    Guid TurnId,
    bool Confirmed);

public sealed record SessionMemoryOutboundConsentRequestDto(
    Guid SessionId,
    Guid TurnId,
    Guid ConsentId,
    bool Confirmed);

public sealed record SessionPointerAnswerConsentRequestDto(
    Guid SessionId,
    Guid TurnId,
    Guid ConsentId,
    string PreviewHash,
    bool Confirmed);

public sealed record CancelSessionTurnRequestDto(
    Guid SessionId,
    Guid TurnId);

public sealed record WaitForSessionUpdateRequestDto(
    long KnownChangeVersion,
    int WaitMilliseconds = 20_000,
    string? CoordinatorInstanceId = null,
    DateTimeOffset? CoordinatorStartedAtUtc = null,
    Guid? SessionId = null,
    long KnownMessageSequenceNumber = 0);

public sealed record SessionProjectionCursorDto(
    string CoordinatorInstanceId,
    DateTimeOffset CoordinatorStartedAtUtc,
    Guid SessionId,
    long KnownChangeVersion,
    long KnownMessageSequenceNumber,
    int WaitMilliseconds = 20_000);

public sealed record SessionProjectionUpdateDto(
    string Kind,
    long ChangeVersion,
    string CoordinatorInstanceId,
    DateTimeOffset CoordinatorStartedAtUtc,
    Guid? SessionId,
    IReadOnlyList<UnifiedSessionTurnDto> TurnUpserts,
    IReadOnlyList<ConversationMessageDto> MessageUpserts,
    long LastMessageSequenceNumber,
    string? ResetReason = null,
    SessionSnapshotDto? Bootstrap = null);

public sealed record SessionMessagesPageRequestDto(
    Guid SessionId,
    long? BeforeSequenceNumber,
    int PageSize = 50);

public sealed record SessionMessagesPageDto(
    string CoordinatorInstanceId,
    DateTimeOffset CoordinatorStartedAtUtc,
    Guid SessionId,
    IReadOnlyList<ConversationMessageDto> Messages,
    long? NextBeforeSequenceNumber,
    bool HasMore);

public sealed record SessionTurnGetRequestDto(Guid SessionId, Guid TurnId);

public sealed record SessionTurnDetailsDto(
    string CoordinatorInstanceId,
    DateTimeOffset CoordinatorStartedAtUtc,
    Guid SessionId,
    UnifiedSessionTurnDto Turn);

public sealed record SessionTurnCommandResultDto(
    Guid SessionId,
    Guid TurnId,
    bool WasDuplicate);

public sealed record UnifiedSessionTurnDto(
    Guid Id,
    int SequenceNumber,
    string InputText,
    string InputModality,
    string WorkKind,
    string Phase,
    string MissingContext,
    string? IntentKind,
    Guid? ConversationTurnId,
    Guid? TaskId,
    Guid? ProjectId,
    string? FilePath,
    long? WindowHandle,
    string? WindowTitle,
    bool RequiresConfirmation,
    bool CancellationRequested,
    string? ResultSummary,
    string? FailureMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ExpectedIntentKind = null,
    string? ExpectedTarget = null,
    string? PlanTarget = null,
    string MemoryOutboundState = "None",
    Guid? MemoryOutboundConsentId = null,
    string? MemoryOutboundManifestHash = null,
    SessionWindowConsentDto? WindowConsent = null);

public sealed record MemoryOutboundPreparedItemDto(
    Guid MemoryId,
    int Version,
    string Category,
    string Scope,
    string Title,
    string Body,
    int CharacterCount)
{
    public override string ToString() =>
        $"MemoryOutboundPreparedItemDto {{ MemoryId = {MemoryId}, Version = {Version}, Category = {Category}, Scope = {Scope}, CharacterCount = {CharacterCount}, Content = [REDACTED] }}";
}

public sealed record MemoryOutboundConsentDto(
    Guid ConsentId,
    Guid TurnId,
    string State,
    string ProviderId,
    string ModelId,
    string DestinationOrigin,
    Guid? ProjectId,
    string? ProjectName,
    IReadOnlyList<MemoryOutboundPreparedItemDto> Items,
    int ItemCount,
    int TotalCharacters,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string ManifestHash)
{
    public override string ToString() =>
        $"MemoryOutboundConsentDto {{ ConsentId = {ConsentId}, TurnId = {TurnId}, State = {State}, ProviderId = {ProviderId}, ModelId = {ModelId}, DestinationOrigin = {DestinationOrigin}, ProjectId = {ProjectId}, ItemCount = {ItemCount}, TotalCharacters = {TotalCharacters}, Content = [REDACTED] }}";
}

public sealed record PointerAnswerConsentDto(
    Guid ConsentId,
    Guid TurnId,
    Guid AnchorId,
    string ProviderId,
    string ModelId,
    string DestinationOrigin,
    string PromptId,
    string PromptVersion,
    string PromptContentHash,
    string Question,
    string OcrText,
    int QuestionCharacterCount,
    int OcrCharacterCount,
    int OcrLineCount,
    int RegionWidth,
    int RegionHeight,
    string RegionSource,
    string PreviewHash,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool SendsImage = false)
{
    public override string ToString() =>
        $"PointerAnswerConsentDto {{ ConsentId = {ConsentId}, TurnId = {TurnId}, AnchorId = {AnchorId}, ProviderId = {ProviderId}, ModelId = {ModelId}, DestinationOrigin = {DestinationOrigin}, PromptId = {PromptId}, PromptVersion = {PromptVersion}, QuestionCharacterCount = {QuestionCharacterCount}, OcrCharacterCount = {OcrCharacterCount}, OcrLineCount = {OcrLineCount}, RegionWidth = {RegionWidth}, RegionHeight = {RegionHeight}, RegionSource = {RegionSource}, PreviewHash = {PreviewHash}, SendsImage = {SendsImage}, Content = [REDACTED] }}";
}

public sealed record SessionSnapshotDto(
    long ChangeVersion,
    string CoordinatorInstanceId,
    DateTimeOffset CoordinatorStartedAtUtc,
    Guid SessionId,
    Guid ConversationId,
    string Title,
    string Status,
    Guid? SelectedProjectId,
    string? SelectedProjectName,
    UnifiedSessionTurnDto? ForegroundTurn,
    IReadOnlyList<UnifiedSessionTurnDto> ActiveTurns,
    IReadOnlyList<UnifiedSessionTurnDto> Turns,
    IReadOnlyList<ConversationMessageDto> Messages,
    IReadOnlyList<MemoryOutboundConsentDto>? MemoryOutboundConsents = null,
    bool HasEarlierMessages = false,
    IReadOnlyList<PointerAnswerConsentDto>? PointerAnswerConsents = null);

public sealed record CommandResultDto(Guid TaskId, bool WasDuplicate);

public sealed record CodexStatusDto(
    bool IsInstalled,
    bool IsCompatible,
    string? Version,
    string Message);

public sealed record SystemStatusDto(
    bool HostOnline,
    string HostVersion,
    DateTimeOffset StartedAtUtc,
    string DeviceId,
    string DatabaseStatus,
    string DataDirectory,
    string LogsDirectory,
    CodexStatusDto Codex,
    int ProtocolVersion = DesktopProtocolVersion.Current,
    int DatabaseSchemaVersion = 0);

public sealed record DashboardDto(
    SystemStatusDto System,
    int AuthorizedProjectCount,
    int ActiveTaskCount,
    int WaitingTaskCount,
    int CompletedTodayCount,
    int FailedTaskCount,
    IReadOnlyList<TaskSummaryDto> ActiveTasks,
    IReadOnlyList<TaskSummaryDto> WaitingTasks,
    IReadOnlyList<TaskSummaryDto> RecentTasks);

public sealed record ProjectDto(
    Guid Id,
    string Name,
    string RootPath,
    string AuthorizationState,
    bool IsGitRepository,
    bool HasGitChanges,
    string GitStatus,
    int RecentTaskCount,
    bool HasActiveTask,
    DateTimeOffset AuthorizedAtUtc,
    Guid? AuthorizationId = null);

public sealed record MemoryIdRequestDto(Guid MemoryId);

public sealed record MemoryPreviewRequestDto(string Query, Guid? ProjectId)
{
    public override string ToString() =>
        $"MemoryPreviewRequestDto {{ ProjectId = {ProjectId}, Query = [REDACTED] }}";
}

public sealed record CreateMemoryRequestDto(
    string Category,
    string Scope,
    Guid? ProjectId,
    string Title,
    string Body,
    DateTimeOffset? ExpiresAtUtc)
{
    public override string ToString() =>
        $"CreateMemoryRequestDto {{ Category = {Category}, Scope = {Scope}, ProjectId = {ProjectId}, Content = [REDACTED], ExpiresAtUtc = {ExpiresAtUtc} }}";
}

public sealed record UpdateMemoryRequestDto(
    Guid MemoryId,
    int ExpectedVersion,
    string Category,
    string Scope,
    Guid? ProjectId,
    string Title,
    string Body,
    DateTimeOffset? ExpiresAtUtc)
{
    public override string ToString() =>
        $"UpdateMemoryRequestDto {{ MemoryId = {MemoryId}, ExpectedVersion = {ExpectedVersion}, Category = {Category}, Scope = {Scope}, ProjectId = {ProjectId}, Content = [REDACTED], ExpiresAtUtc = {ExpiresAtUtc} }}";
}

public sealed record SetMemoryEnabledRequestDto(
    Guid MemoryId,
    int ExpectedVersion,
    bool Enabled);

public sealed record DeleteMemoryRequestDto(
    Guid MemoryId,
    int ExpectedVersion,
    bool Confirmed);

public sealed record MemoryDto(
    Guid Id,
    string Category,
    string Scope,
    Guid? ProjectId,
    string Status,
    string Source,
    string? Title,
    string? Body,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    double Confidence,
    int Version)
{
    public override string ToString() =>
        $"MemoryDto {{ Id = {Id}, Category = {Category}, Scope = {Scope}, ProjectId = {ProjectId}, Status = {Status}, Source = {Source}, Content = [REDACTED], UpdatedAtUtc = {UpdatedAtUtc}, ExpiresAtUtc = {ExpiresAtUtc}, Version = {Version} }}";
}

public sealed record MemoryPreviewMatchDto(
    MemoryDto Item,
    int Score,
    string[] Explanations)
{
    public override string ToString() =>
        $"MemoryPreviewMatchDto {{ MemoryId = {Item.Id}, Score = {Score}, Explanations = [{string.Join(",", Explanations)}], Content = [REDACTED] }}";
}

public sealed record MemoryPreviewResponseDto(
    MemoryPreviewMatchDto[] Items,
    int CandidateCount,
    int SelectedCount,
    int TotalCharacters)
{
    public override string ToString() =>
        $"MemoryPreviewResponseDto {{ CandidateCount = {CandidateCount}, SelectedCount = {SelectedCount}, TotalCharacters = {TotalCharacters}, Content = [REDACTED] }}";
}

public sealed record TaskSummaryDto(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string Title,
    string Status,
    string? VerificationStatus,
    string? UserSummary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureMessage,
    string Phase = "Planning");

public sealed record TaskEventDto(
    long SequenceNumber,
    string EventType,
    string? FromStatus,
    string? ToStatus,
    string Source,
    DateTimeOffset OccurredAtUtc,
    string Message,
    string? FromPhase = null,
    string? ToPhase = null);

public sealed record ResourceScopeDto(
    Guid Id,
    string ScopeType,
    Guid? ResourceId,
    string ScopeValue,
    string AccessMode,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc);

public sealed record SkillInvocationDto(
    Guid Id,
    int SequenceNumber,
    string SkillId,
    string SkillVersion,
    string Capability,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureMessage);

public sealed record TaskAttemptDto(
    int AttemptNumber,
    string Operation,
    string Status,
    int? ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int? ExitCode);

public sealed record DecisionRequestDto(Guid Id, string Question, DateTimeOffset CreatedAtUtc);

public sealed record EvidenceFileDto(
    string RelativePath,
    string ChangeType,
    bool HadPreExistingChanges,
    bool MixedWithPreExistingChanges,
    int? AddedLines,
    int? DeletedLines,
    bool IsBinary);

public sealed record TestCommandDto(
    string Command,
    int? ExitCode,
    string Status,
    int? TotalTests,
    int? PassedTests,
    int? FailedTests,
    int? SkippedTests);

public sealed record TaskEvidenceDto(
    string VerificationStatus,
    string UserSummary,
    bool AgentClaimContradictedByEvidence,
    string AgentClaimStatus,
    string? AgentFinalExplanation,
    bool IsGitRepository,
    int AddedFileCount,
    int ModifiedFileCount,
    int DeletedFileCount,
    int? AddedLineCount,
    int? DeletedLineCount,
    bool DiffStatVerified,
    IReadOnlyList<EvidenceFileDto> ChangedFiles,
    string TestStatus,
    bool HasRealTestEvidence,
    int? TotalTests,
    int? PassedTests,
    int? FailedTests,
    int? SkippedTests,
    IReadOnlyList<TestCommandDto> TestCommands,
    string? CodexVersion,
    bool CodexVersionVerified,
    string CompatibilityDecision,
    IReadOnlyList<string> VerificationReasons,
    DateTimeOffset GeneratedAtUtc);

public sealed record TaskDetailsDto(
    TaskSummaryDto Summary,
    ProjectDto Project,
    string Instruction,
    string WorkingDirectoryRelativePath,
    string? ThreadId,
    string? ConnectorVersion,
    int CurrentAttempt,
    IReadOnlyList<TaskAttemptDto> Attempts,
    DecisionRequestDto? PendingDecision,
    IReadOnlyList<TaskEventDto> Events,
    TaskEvidenceDto? Evidence,
    IReadOnlyList<ResourceScopeDto>? ResourceScopes = null,
    IReadOnlyList<SkillInvocationDto>? SkillInvocations = null);
