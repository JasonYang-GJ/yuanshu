using ScreenGuide.Core.Memories;

namespace ScreenGuide.Core.Ai;

public enum AiInvocationPurpose
{
    Conversation,
    SemanticIntent,
    DesktopTaskPlanning
}

public enum AiInvocationStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted
}

public enum AiInvocationTransitionDisposition
{
    Applied,
    AlreadyInRequestedTerminal,
    RejectedByExistingTerminal
}

public sealed record AiTokenUsage(long InputTokens, long OutputTokens, long TotalTokens);

public sealed record AiInvocationTransitionResult(
    AiInvocationStatus RequestedStatus,
    AiInvocationTransitionDisposition Disposition,
    AiInvocationRecord Current)
{
    public bool RequestedStatusWon => Current.Status == RequestedStatus;
}

public sealed record AiInvocationRecoveryResult(
    IReadOnlyList<Guid> InterruptedInvocationIds);

public sealed class AiInvocationNotFoundException(Guid invocationId) : Exception(
    "AI 调用记录不存在，无法完成终态仲裁。")
{
    public Guid InvocationId { get; } = invocationId;
}

public sealed record AiInvocationRecord
{
    public required Guid Id { get; init; }

    public Guid? SessionTurnId { get; init; }

    public Guid? ConversationTurnId { get; init; }

    public required AiInvocationPurpose Purpose { get; init; }

    public required string ProviderId { get; init; }

    public required string ModelId { get; init; }

    public required string PromptId { get; init; }

    public required string PromptVersion { get; init; }

    public required string PromptContentHash { get; init; }

    public required string DataDestination { get; init; }

    public AiInvocationStatus Status { get; init; } = AiInvocationStatus.Running;

    public required DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public string? FinishReason { get; init; }

    public AiTokenUsage? Usage { get; init; }

    public string? ProviderRequestId { get; init; }

    public string? FailureCode { get; init; }

    public MemoryOutboundAuditMetadata? MemoryOutbound { get; init; }
}
