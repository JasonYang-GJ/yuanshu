using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ScreenGuide.AI.Core;

namespace ScreenGuide.Agent.Codex;

/// <summary>
/// Ordinary chat adapter for the locally installed Codex CLI. This is deliberately
/// separate from <see cref="CodexConnector"/>, which remains the coding-task path.
/// </summary>
public sealed class CodexChatModelProvider : IChatModelProvider
{
    public const string ProviderId = "codex";
    public const string DefaultModelId = "codex-default";

    private const int MaximumInputCharacters = 200_000;
    private const int MaximumMessageCount = 1_000;
    private const int MaximumProtocolLineCharacters = 300_000;
    private const int MaximumReplyCharacters = 100_000;
    private const string DataDestination =
        "OpenAI Codex 云端服务（通过本机 Codex CLI 和当前登录账号）";

    private enum ExecutionPolicy
    {
        ProductionDisabled,
        TestOnlyAllowLocalCodexCli
    }

    private static readonly ChatProviderDescriptor ProviderDescriptor = new(
        ProviderId,
        "Codex",
        DataDestination,
        SendsDataOffDevice: true,
        [
            new ChatModelDescriptor(
                DefaultModelId,
                "Codex 默认聊天模型",
                ChatModelCapabilities.None)
        ]);

    private readonly CodexChatOptions _options;
    private readonly ExecutionPolicy _executionPolicy;
    private readonly CodexCapabilityProbe _capabilityProbe;
    private readonly SemaphoreSlim _capabilityGate = new(1, 1);
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ActiveChatCall> _active = new();
    private CodexCapability? _capability;
    private bool _disposed;

    public CodexChatModelProvider(CodexChatOptions options)
        : this(options, ExecutionPolicy.ProductionDisabled)
    {
    }

    private CodexChatModelProvider(
        CodexChatOptions options,
        ExecutionPolicy executionPolicy)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _executionPolicy = executionPolicy;
        if (options.ChatRequestTimeout <= TimeSpan.Zero
            || options.ChatRequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Codex 普通聊天整体超时必须大于零且不超过十分钟。");
        }

        _capabilityProbe = new CodexCapabilityProbe(new CodexExecutableLocator(options.ExecutablePath));
    }

    public ChatProviderDescriptor Descriptor => ProviderDescriptor;

    public async Task<ChatModelResponse> CompleteAsync(
        ChatModelRequest request,
        ChatModelStreamCallback? streamCallback = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_executionPolicy != ExecutionPolicy.TestOnlyAllowLocalCodexCli)
        {
            throw DisabledBySecurityPolicyError();
        }

        request = ChatImageInputs.Prepare(request, ProviderId, supportsImages: false);
        ValidateRequest(request);
        if (cancellationToken.IsCancellationRequested)
        {
            throw CancelledError();
        }

        var active = new ActiveChatCall();
        if (!_active.TryAdd(request.TurnId, active))
        {
            throw InvalidRequestError("这个 Turn 已有一个 Codex 聊天调用正在运行。", "duplicate_turn");
        }

        using var timeoutCancellation = new CancellationTokenSource(_options.ChatRequestTimeout);
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        using var cancellationRegistration = cancellationToken.Register(
            static state => RequestCancellation(
                (ActiveChatCall)state!,
                ChatTerminationKind.User),
            active);
        using var timeoutRegistration = timeoutCancellation.Token.Register(
            static state => RequestCancellation(
                (ActiveChatCall)state!,
                ChatTerminationKind.Timeout),
            active);

        Process? process = null;
        WindowsProcessJob? job = null;
        Task stderrTask = Task.CompletedTask;
        var processStarted = false;
        try
        {
            var capability = await EnsureCapabilityAsync(requestLifetime.Token).ConfigureAwait(false);
            var schemaPath = await EnsureSchemaAsync(requestLifetime.Token).ConfigureAwait(false);
            var workspace = Path.Combine(_options.DataDirectory, "chat-model-workspace");
            Directory.CreateDirectory(workspace);

            process = CreateProcess(capability, workspace, schemaPath);
            job = new WindowsProcessJob();
            if (!process.Start())
            {
                throw new InvalidOperationException("Codex 聊天进程无法启动。");
            }

            processStarted = true;

            try
            {
                job.Assign(process);
            }
            catch
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            active.Attach(process, job);

            stderrTask = DrainStandardErrorAsync(process.StandardError);
            var input = SerializeRequest(request);
            await process.StandardInput.WriteAsync(input.AsMemory(), CancellationToken.None)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            process.StandardInput.Close();

            var terminalReceived = false;
            var providerFailed = false;
            string? providerRequestId = null;
            string? providerMessageId = null;
            string? finalMessage = null;

            var protocolReader = new BoundedProtocolLineReader(
                process.StandardOutput,
                MaximumProtocolLineCharacters);
            while (await protocolReader.ReadLineAsync(CancellationToken.None)
                       .ConfigureAwait(false) is { } line)
            {
                if (!CodexJsonLineParser.TryParse(line, out var protocolEvent, out _)
                    || protocolEvent is null
                    || active.IsCancellationRequested)
                {
                    continue;
                }

                switch (protocolEvent.Type)
                {
                    case "thread.started":
                        providerRequestId = protocolEvent.ThreadId;
                        break;

                    case "item.started":
                    case "item.updated":
                    case "item.completed":
                        if (!string.IsNullOrWhiteSpace(protocolEvent.AgentMessageText))
                        {
                            finalMessage = protocolEvent.AgentMessageText;
                            providerMessageId = protocolEvent.ItemId;
                        }

                        break;

                    case "turn.failed":
                        terminalReceived = true;
                        providerFailed = true;
                        break;

                    case "turn.completed":
                        terminalReceived = true;
                        break;
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            if (active.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                throw TerminationError(active);
            }

            if (providerFailed || process.ExitCode != 0)
            {
                throw ProviderUnavailableError();
            }

            if (!terminalReceived)
            {
                throw InvalidResponseError("Codex 没有返回权威完成事件。");
            }

            if (!TryParseReply(finalMessage, out var reply))
            {
                throw InvalidResponseError("Codex 没有返回可用的聊天回答。");
            }

            if (!active.TryClaimSuccess())
            {
                throw TerminationError(active);
            }

            if (streamCallback is not null)
            {
                await streamCallback(
                        new ChatStreamUpdate(1, reply, IsFinal: true),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new ChatModelResponse(
                reply,
                ChatFinishReason.Stop,
                Usage: null,
                new ChatProviderMetadata(
                    ProviderId,
                    DefaultModelId,
                    providerRequestId,
                    DataDestination,
                    providerMessageId is null
                        ? null
                        : new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["providerMessageId"] = providerMessageId,
                            ["codexCliVersion"] = capability.Version
                        }));
        }
        catch (ChatModelException) when (active.IsCancellationRequested)
        {
            throw TerminationError(active);
        }
        catch (ChatModelException)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            requestLifetime.IsCancellationRequested || active.IsCancellationRequested)
        {
            throw TerminationError(active);
        }
        catch (Exception) when (active.IsCancellationRequested)
        {
            throw TerminationError(active);
        }
        catch (FileNotFoundException)
        {
            throw ProviderUnavailableError("codex.not_found", "未找到可用的 Codex，请先完成 Codex 安装和登录。");
        }
        catch (CodexVersionCompatibilityException)
        {
            throw ProviderUnavailableError(
                "codex.version_unsupported",
                "当前 Codex 版本尚未通过元枢兼容性验证。");
        }
        catch (NotSupportedException)
        {
            throw ProviderUnavailableError(
                "codex.version_unrecognized",
                "当前 Codex 版本无法识别或尚未通过兼容性验证。");
        }
        catch (Exception)
        {
            throw ProviderUnavailableError();
        }
        finally
        {
            try
            {
                if (job is not null && job.HasActiveProcesses)
                {
                    job.Terminate();
                }
            }
            catch
            {
                // Job disposal remains the final process-tree termination boundary.
            }
            finally
            {
                job?.Dispose();
                try
                {
                    if (processStarted && process is not null)
                    {
                        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    await stderrTask.ConfigureAwait(false);
                }
                finally
                {
                    process?.Dispose();
                    _active.TryRemove(request.TurnId, out _);
                }
            }
        }
    }

    public async Task<ChatProviderHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_executionPolicy != ExecutionPolicy.TestOnlyAllowLocalCodexCli)
        {
            return new ChatProviderHealth(
                ProviderId,
                ChatProviderHealthState.PolicyDisabled,
                IsConfigured: true,
                "出于安全原因，Codex 普通聊天当前已停用；编程任务不受影响。",
                DateTimeOffset.UtcNow);
        }

        try
        {
            _ = await EnsureCapabilityAsync(cancellationToken).ConfigureAwait(false);
            return new ChatProviderHealth(
                ProviderId,
                ChatProviderHealthState.Healthy,
                IsConfigured: true,
                "Codex CLI 已安装且版本兼容；账号状态会在实际请求时验证。",
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ChatProviderHealth(
                ProviderId,
                ChatProviderHealthState.Unavailable,
                IsConfigured: false,
                "Codex 当前不可用，请检查安装、兼容版本和登录状态。",
                DateTimeOffset.UtcNow);
        }
    }

    public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_active.TryGetValue(turnId, out var active))
        {
            RequestCancellation(active, ChatTerminationKind.User);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var active in _active.Values)
        {
            RequestCancellation(active, ChatTerminationKind.User);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!_active.IsEmpty && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        _capabilityGate.Dispose();
        _schemaGate.Dispose();
    }

    private async Task<CodexCapability> EnsureCapabilityAsync(CancellationToken cancellationToken)
    {
        if (_capability is not null)
        {
            return _capability;
        }

        await _capabilityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _capability ??= await _capabilityProbe.ProbeAsync(cancellationToken)
                .ConfigureAwait(false);
            return _capability;
        }
        finally
        {
            _capabilityGate.Release();
        }
    }

    private async Task<string> EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            var path = Path.Combine(_options.DataDirectory, "conversation-output.schema.json");
            const string schema = """
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["reply"],
                  "properties": {
                    "reply": { "type": "string", "minLength": 1 }
                  }
                }
                """;
            if (!File.Exists(path)
                || !string.Equals(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                    schema,
                    StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(
                        path,
                        schema,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return path;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private Process CreateProcess(
        CodexCapability capability,
        string workspace,
        string schemaPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = capability.ExecutablePath,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--skip-git-repo-check");
        startInfo.ArgumentList.Add("--ignore-user-config");
        startInfo.ArgumentList.Add("--ignore-rules");
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add("read-only");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add("approval_policy=\"never\"");
        startInfo.ArgumentList.Add("--cd");
        startInfo.ArgumentList.Add(workspace);
        startInfo.ArgumentList.Add("--output-schema");
        startInfo.ArgumentList.Add(schemaPath);
        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(_options.Model);
        }

        startInfo.ArgumentList.Add("-");
        return new Process { StartInfo = startInfo };
    }

    private static string SerializeRequest(ChatModelRequest request)
    {
        var builder = new StringBuilder();
        AppendBlock(builder, "SYSTEM_PROMPT", request.SystemPrompt);
        for (var index = 0; index < request.Messages.Count; index++)
        {
            var message = request.Messages[index];
            var name = JsonSerializer.Serialize(message.Name);
            AppendBlock(
                builder,
                $"MESSAGE {index} ROLE={RoleName(message.Role)} NAME={name}",
                message.Content);
        }

        return builder.ToString();
    }

    private static async Task DrainStandardErrorAsync(TextReader reader)
    {
        var buffer = new char[4_096];
        try
        {
            while (await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false) > 0)
            {
                Array.Clear(buffer);
            }
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static void AppendBlock(StringBuilder builder, string header, string content)
    {
        builder.Append(header)
            .Append(" LENGTH=")
            .Append(content.Length)
            .AppendLine();
        builder.Append(content).AppendLine();
    }

    private static string RoleName(ChatMessageRole role) => role switch
    {
        ChatMessageRole.System => "system",
        ChatMessageRole.User => "user",
        ChatMessageRole.Assistant => "assistant",
        ChatMessageRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未知消息角色。")
    };

    private static bool TryParseReply(string? value, out string reply)
    {
        reply = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumProtocolLineCharacters)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (!document.RootElement.TryGetProperty("reply", out var element)
                || element.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(element.GetString()))
            {
                return false;
            }

            reply = element.GetString()!.Trim();
            if (reply.Length > MaximumReplyCharacters)
            {
                reply = string.Empty;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateRequest(ChatModelRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ModelId, DefaultModelId, StringComparison.Ordinal))
        {
            throw new ChatModelException(
                ProviderId,
                request.ModelId,
                new ChatModelError(
                    ChatModelErrorKind.ModelNotFound,
                    "codex.model_not_found",
                    "Codex 没有这个聊天模型。"));
        }

        if (request.RequestId == Guid.Empty
            || request.TurnId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.SystemPrompt)
            || request.Messages is null
            || request.Messages.Count == 0
            || request.Messages.Any(message => message is null || string.IsNullOrWhiteSpace(message.Content)))
        {
            throw InvalidRequestError("聊天请求缺少必要内容。", "codex.invalid_request");
        }

        if (request.Messages.Count > MaximumMessageCount
            || request.Messages.Any(message => message.Role == ChatMessageRole.Tool))
        {
            throw request.Messages.Any(message => message.Role == ChatMessageRole.Tool)
                ? InvalidRequestError(
                    "当前 Codex 普通聊天不支持工具消息。",
                    "codex.tool_messages_not_supported")
                : InvalidRequestError(
                    "聊天历史消息数量超过安全上限。",
                    "codex.input_too_large");
        }

        long inputCharacters = request.SystemPrompt.Length;
        foreach (var message in request.Messages)
        {
            inputCharacters += message.Content.Length;
            if (inputCharacters > MaximumInputCharacters)
            {
                throw InvalidRequestError(
                    "聊天历史内容超过安全上限，请新建一个会话后继续。",
                    "codex.input_too_large");
            }
        }

        if (request.Options is not null
            || request.ResponseFormat is { Kind: not ChatResponseFormatKind.Text })
        {
            throw InvalidRequestError(
                "当前 Codex 普通聊天适配器不支持这组模型选项或输出格式。",
                "codex.unsupported_options");
        }
    }

    private static void RequestCancellation(
        ActiveChatCall active,
        ChatTerminationKind kind) => active.RequestCancellation(kind);

    private static ChatModelException TerminationError(ActiveChatCall active) =>
        active.TerminationKind == ChatTerminationKind.Timeout
            ? TimeoutError()
            : CancelledError();

    private static ChatModelException CancelledError() => new(
        ProviderId,
        DefaultModelId,
        new ChatModelError(
            ChatModelErrorKind.Cancelled,
            "codex.cancelled",
            "回答已停止。"));

    private static ChatModelException TimeoutError() => new(
        ProviderId,
        DefaultModelId,
        new ChatModelError(
            ChatModelErrorKind.Timeout,
            "codex.timeout",
            "Codex 回答超时，已停止。"));

    private static ChatModelException InvalidRequestError(string message, string code) => new(
        ProviderId,
        DefaultModelId,
        new ChatModelError(
            ChatModelErrorKind.InvalidRequest,
            code,
            message));

    private static ChatModelException InvalidResponseError(string message) => new(
        ProviderId,
        DefaultModelId,
        new ChatModelError(
            ChatModelErrorKind.InvalidResponse,
            "codex.invalid_response",
            message));

    private static ChatModelException ProviderUnavailableError(
        string code = "codex.unavailable",
        string message = "Codex 当前无法完成回答，请稍后重试或检查登录状态。") => new(
        ProviderId,
        DefaultModelId,
        new ChatModelError(
            ChatModelErrorKind.Unavailable,
            code,
            message));

    private static ChatModelException DisabledBySecurityPolicyError() => new(
        ProviderId,
        DefaultModelId,
        new ChatModelError(
            ChatModelErrorKind.PolicyDisabled,
            "codex.policy_disabled",
            "出于安全原因，当前版本暂不提供 Codex 普通聊天。你可以改用其他已配置的聊天服务；编程任务不受影响。"));

    private sealed class BoundedProtocolLineReader(
        TextReader reader,
        int maximumLineCharacters)
    {
        private readonly char[] _buffer = new char[4_096];
        private int _bufferLength;
        private int _bufferPosition;
        private bool _skipLeadingLineFeed;

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_skipLeadingLineFeed)
            {
                if (_bufferPosition == _bufferLength)
                {
                    _bufferLength = await reader.ReadAsync(_buffer, cancellationToken)
                        .ConfigureAwait(false);
                    _bufferPosition = 0;
                    if (_bufferLength == 0)
                    {
                        _skipLeadingLineFeed = false;
                        return null;
                    }
                }

                if (_buffer[_bufferPosition] == '\n')
                {
                    _bufferPosition++;
                }

                _skipLeadingLineFeed = false;
            }

            StringBuilder? line = null;
            while (true)
            {
                if (_bufferPosition == _bufferLength)
                {
                    _bufferLength = await reader.ReadAsync(_buffer, cancellationToken)
                        .ConfigureAwait(false);
                    _bufferPosition = 0;
                    if (_bufferLength == 0)
                    {
                        return line?.ToString();
                    }
                }

                var carriageReturn = Array.IndexOf(
                    _buffer,
                    '\r',
                    _bufferPosition,
                    _bufferLength - _bufferPosition);
                var lineFeed = Array.IndexOf(
                    _buffer,
                    '\n',
                    _bufferPosition,
                    _bufferLength - _bufferPosition);
                var terminator = carriageReturn < 0
                    ? lineFeed
                    : lineFeed < 0 ? carriageReturn : Math.Min(carriageReturn, lineFeed);
                var segmentEnd = terminator >= 0 ? terminator : _bufferLength;
                var segmentLength = segmentEnd - _bufferPosition;
                if ((long)(line?.Length ?? 0) + segmentLength > maximumLineCharacters)
                {
                    throw InvalidResponseError("Codex 返回的数据超过安全上限。");
                }

                if (line is null && terminator >= 0)
                {
                    var result = new string(_buffer, _bufferPosition, segmentLength);
                    ConsumeTerminator(terminator);
                    return result;
                }

                line ??= new StringBuilder(
                    Math.Min(maximumLineCharacters, Math.Max(256, segmentLength * 2)));
                line.Append(_buffer, _bufferPosition, segmentLength);
                _bufferPosition = segmentEnd;
                if (terminator < 0)
                {
                    continue;
                }

                ConsumeTerminator(terminator);
                return line.ToString();
            }
        }

        private void ConsumeTerminator(int terminator)
        {
            var isCarriageReturn = _buffer[terminator] == '\r';
            _bufferPosition = terminator + 1;
            if (!isCarriageReturn)
            {
                return;
            }

            if (_bufferPosition < _bufferLength)
            {
                if (_buffer[_bufferPosition] == '\n')
                {
                    _bufferPosition++;
                }

                return;
            }

            _skipLeadingLineFeed = true;
        }
    }

    private enum ChatTerminationKind
    {
        None,
        User,
        Timeout
    }

    private sealed class ActiveChatCall
    {
        private readonly object _gate = new();
        private WindowsProcessJob? _job;
        private ChatTerminationKind _terminationKind;
        private bool _successClaimed;

        public bool IsCancellationRequested
        {
            get
            {
                lock (_gate)
                {
                    return _terminationKind != ChatTerminationKind.None;
                }
            }
        }

        public ChatTerminationKind TerminationKind
        {
            get
            {
                lock (_gate)
                {
                    return _terminationKind;
                }
            }
        }

        public void Attach(Process process, WindowsProcessJob job)
        {
            ArgumentNullException.ThrowIfNull(process);
            ArgumentNullException.ThrowIfNull(job);
            lock (_gate)
            {
                _job = job;
                if (_terminationKind != ChatTerminationKind.None)
                {
                    TryTerminate(job);
                }
            }
        }

        public void RequestCancellation(ChatTerminationKind kind)
        {
            lock (_gate)
            {
                if (_successClaimed)
                {
                    return;
                }

                if (_terminationKind == ChatTerminationKind.None)
                {
                    _terminationKind = kind;
                }

                if (_job is not null)
                {
                    TryTerminate(_job);
                }
            }
        }

        public bool TryClaimSuccess()
        {
            lock (_gate)
            {
                if (_terminationKind != ChatTerminationKind.None)
                {
                    return false;
                }

                _successClaimed = true;
                return true;
            }
        }

        private static void TryTerminate(WindowsProcessJob job)
        {
            try
            {
                job.Terminate();
            }
            catch
            {
                // Job disposal remains the final process-tree cleanup boundary.
            }
        }
    }
}
