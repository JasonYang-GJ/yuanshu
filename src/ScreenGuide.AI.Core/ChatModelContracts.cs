using System.Text.Json.Serialization;

namespace ScreenGuide.AI.Core;

public enum ChatMessageRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record ChatMessage(
    ChatMessageRole Role,
    string Content,
    string? Name = null,
    [property: JsonIgnore] IReadOnlyList<ChatImage>? Images = null);

public sealed record ChatPromptReference(
    string PromptId,
    string Version,
    string ContentSha256);

public sealed record ChatModelOptions(
    double? Temperature = null,
    int? MaxOutputTokens = null,
    double? TopP = null,
    long? Seed = null);

public enum ChatResponseFormatKind
{
    Text,
    JsonObject,
    JsonSchema
}

public sealed record ChatResponseFormat(
    ChatResponseFormatKind Kind,
    string? SchemaName = null,
    string? SchemaJson = null)
{
    public static ChatResponseFormat Text { get; } = new(ChatResponseFormatKind.Text);

    public static ChatResponseFormat JsonObject { get; } = new(ChatResponseFormatKind.JsonObject);

    public static ChatResponseFormat JsonSchema(string schemaName, string schemaJson) =>
        new(ChatResponseFormatKind.JsonSchema, schemaName, schemaJson);
}

public sealed record ChatModelRequest(
    Guid RequestId,
    Guid TurnId,
    string ModelId,
    string SystemPrompt,
    IReadOnlyList<ChatMessage> Messages,
    ChatModelOptions? Options = null,
    ChatResponseFormat? ResponseFormat = null,
    ChatPromptReference? Prompt = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ChatStreamUpdate(
    long SequenceNumber,
    string DeltaText,
    bool IsFinal = false);

public delegate ValueTask ChatModelStreamCallback(
    ChatStreamUpdate update,
    CancellationToken cancellationToken);

public enum ChatFinishReason
{
    Stop,
    Length,
    ToolCall,
    ContentFilter,
    Cancelled,
    Error,
    Unknown
}

public sealed record ChatModelUsage(
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    long? CachedInputTokens = null,
    long? ReasoningTokens = null);

public sealed record ChatProviderMetadata(
    string ProviderId,
    string ModelId,
    string? ProviderRequestId,
    string DataDestination,
    IReadOnlyDictionary<string, string>? Additional = null);

public sealed record ChatModelResponse(
    string Text,
    ChatFinishReason FinishReason,
    ChatModelUsage? Usage,
    ChatProviderMetadata Metadata,
    string? StructuredJson = null);

[Flags]
public enum ChatModelCapabilities
{
    None = 0,
    Streaming = 1 << 0,
    ToolCalling = 1 << 1,
    Vision = 1 << 2,
    JsonObjectOutput = 1 << 3,
    StructuredOutput = JsonObjectOutput,
    Reasoning = 1 << 4,
    JsonSchemaOutput = 1 << 5
}

public sealed record ChatModelDescriptor(
    string ModelId,
    string DisplayName,
    ChatModelCapabilities Capabilities,
    int? ContextWindowTokens = null);

public enum ChatProviderCredentialKind
{
    None,
    ApiKey
}

[Flags]
public enum ChatProviderWorkloads
{
    None = 0,
    OrdinaryChat = 1 << 0,
    ProgrammingAgent = 1 << 1
}

public sealed record ChatProviderDescriptor(
    string ProviderId,
    string DisplayName,
    string DataDestination,
    bool SendsDataOffDevice,
    IReadOnlyList<ChatModelDescriptor> Models,
    ChatProviderCredentialKind CredentialKind = ChatProviderCredentialKind.None,
    ChatProviderWorkloads SupportedWorkloads = ChatProviderWorkloads.OrdinaryChat);

public enum ChatProviderHealthState
{
    NotChecked,
    NotConfigured,
    Healthy,
    Degraded,
    Unavailable,
    PolicyDisabled
}

public sealed record ChatProviderHealth(
    string ProviderId,
    ChatProviderHealthState State,
    bool IsConfigured,
    string Message,
    DateTimeOffset CheckedAtUtc);

public enum ChatModelErrorKind
{
    Unauthorized,
    InsufficientBalance,
    InvalidRequest,
    RateLimited,
    Unavailable,
    Timeout,
    Network,
    ModelNotFound,
    InvalidResponse,
    Cancelled,
    Unknown,
    Configuration,
    Authorization,
    PolicyDisabled
}

public sealed record ChatModelError
{
    public ChatModelError(
        ChatModelErrorKind kind,
        string code,
        string userMessage,
        TimeSpan? RetryAfter = null)
    {
        Kind = kind;
        Code = NormalizeDiagnosticCode(code);
        UserMessage = userMessage;
        IsRetryable = kind is
            ChatModelErrorKind.RateLimited or
            ChatModelErrorKind.Timeout or
            ChatModelErrorKind.Network or
            ChatModelErrorKind.Unavailable;
        this.RetryAfter = kind is ChatModelErrorKind.RateLimited or ChatModelErrorKind.Unavailable
            && RetryAfter is { } delay
            && delay > TimeSpan.Zero
            && delay <= TimeSpan.FromHours(24)
                ? delay
                : null;
    }

    public ChatModelError(
        ChatModelErrorKind kind,
        string code,
        string userMessage,
        bool IsRetryable,
        TimeSpan? RetryAfter = null)
        : this(kind, code, userMessage, RetryAfter)
    {
    }

    public ChatModelErrorKind Kind { get; }

    public string Code { get; }

    public string UserMessage { get; }

    public bool IsRetryable { get; }

    public TimeSpan? RetryAfter { get; }

    private static string NormalizeDiagnosticCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80)
        {
            return "component.invalid_diagnostic_code";
        }

        foreach (var character in value)
        {
            var isAsciiLetter = character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
            var isAsciiDigit = character is >= '0' and <= '9';
            if (!isAsciiLetter && !isAsciiDigit && character is not ('.' or '_' or '-'))
            {
                return "component.invalid_diagnostic_code";
            }
        }

        return value;
    }
}

public sealed class ChatModelException : Exception
{
    public ChatModelException(string providerId, string? modelId, ChatModelError error)
        : base(error?.UserMessage)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("Provider ID 不能为空。", nameof(providerId));
        }

        ProviderId = providerId.Trim();
        ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public string ProviderId { get; }

    public string? ModelId { get; }

    public ChatModelError Error { get; }
}

public interface IChatModelProvider : IAsyncDisposable
{
    ChatProviderDescriptor Descriptor { get; }

    Task<ChatModelResponse> CompleteAsync(
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback = null,
        CancellationToken cancellationToken = default);

    Task<ChatProviderHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        Guid turnId,
        CancellationToken cancellationToken = default);
}
