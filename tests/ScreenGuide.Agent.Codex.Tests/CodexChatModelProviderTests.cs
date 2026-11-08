using ScreenGuide.AI.Core;
using ScreenGuide.Agent.Codex;
using ScreenGuide.FakeCodexCli;
using System.Reflection;
using System.Text.Json;

namespace ScreenGuide.Agent.Codex.Tests;

public sealed class CodexChatModelProviderTests
{
    [Fact]
    public async Task ImagesCannotBeDroppedIntoAPlainTextCliRequestEvenUnderTheTestOnlyPolicy()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();
        using var image = new ChatImage(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP4/x8AAwAB//wl3FEAAAAASUVORK5CYII="));
        var error = await Assert.ThrowsAsync<ChatModelException>(() => provider.CompleteAsync(Request(
            Guid.NewGuid(), "system", new ChatMessage(ChatMessageRole.User, "synthetic", Images: [image]))));
        Assert.Equal("chat.vision_not_supported", error.Error.Code);
        Assert.False(Directory.Exists(environment.DataDirectory));
        Assert.False(image.IsDisposed);
    }

    [Fact]
    public async Task ProductionPolicyFailsClosedBeforeCodexCliOrFilesystemAccess()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"screen-guide-codex-chat-production-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(root, "must-not-create");
        var privateProject = Path.Combine(root, "private-user-project");
        const string privateRequest = "PRIVATE_CHAT_SENTINEL 请读取用户项目并调用工具";
        await using var provider = new CodexChatModelProvider(new CodexChatOptions(
            dataDirectory,
            Path.Combine(root, "missing-codex.exe")));

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(
                Guid.NewGuid(),
                "只使用请求传入的系统提示。",
                new ChatMessage(
                    ChatMessageRole.User,
                    $"{privateRequest} {privateProject}"))));
        var exposed = JsonSerializer.Serialize(new
        {
            exception.Message,
            exception.Error
        });

        Assert.Equal("codex", exception.ProviderId);
        Assert.Equal("codex-default", exception.ModelId);
        Assert.Equal(ChatModelErrorKind.PolicyDisabled, exception.Error.Kind);
        Assert.Equal("codex.policy_disabled", exception.Error.Code);
        Assert.False(exception.Error.IsRetryable);
        Assert.Equal(
            "出于安全原因，当前版本暂不提供 Codex 普通聊天。你可以改用其他已配置的聊天服务；编程任务不受影响。",
            exception.Error.UserMessage);
        Assert.DoesNotContain(privateRequest, exposed, StringComparison.Ordinal);
        Assert.DoesNotContain(privateProject, exposed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, exposed, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ProductionHealthDoesNotProbeOrAdvertiseCodexCliAvailability()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = new CodexChatModelProvider(new CodexChatOptions(
            environment.DataDirectory,
            Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe")));

        var health = await provider.CheckHealthAsync();

        Assert.Equal("codex", health.ProviderId);
        Assert.Equal(ChatProviderHealthState.PolicyDisabled, health.State);
        Assert.True(health.IsConfigured);
        Assert.Equal(
            "出于安全原因，Codex 普通聊天当前已停用；编程任务不受影响。",
            health.Message);
        Assert.DoesNotContain(environment.RootDirectory, health.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(environment.DataDirectory));
    }

    [Fact]
    public async Task DescriptorDeclaresHonestChatModelAndDataDestination()
    {
        using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();

        var descriptor = provider.Descriptor;
        var model = Assert.Single(descriptor.Models);

        Assert.Equal("codex", descriptor.ProviderId);
        Assert.Equal("codex-default", model.ModelId);
        Assert.True(descriptor.SendsDataOffDevice);
        Assert.Contains("Codex", descriptor.DataDestination, StringComparison.Ordinal);
        Assert.Equal(ChatModelCapabilities.None, model.Capabilities);
    }

    [Fact]
    public async Task SendsRequestSystemPromptAndCompleteMessageHistoryOnEveryTurn()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();
        var updates = new List<ChatStreamUpdate>();

        var systemPromptResponse = await provider.CompleteAsync(
            Request(
                Guid.NewGuid(),
                "ECHO_CHAT_INPUT",
                new ChatMessage(ChatMessageRole.User, "这次问题本身没有测试标记。")),
            (update, _) =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            });
        var historyResponse = await provider.CompleteAsync(
            Request(
                Guid.NewGuid(),
                "只使用请求传入的系统提示。",
                new ChatMessage(ChatMessageRole.User, "ECHO_CHAT_INPUT"),
                new ChatMessage(ChatMessageRole.Assistant, "这是较早一轮回答。"),
                new ChatMessage(ChatMessageRole.User, "继续刚才那个。")));

        Assert.Equal("已收到对话正文", systemPromptResponse.Text);
        Assert.Equal("已收到对话正文", historyResponse.Text);
        var update = Assert.Single(updates);
        Assert.True(update.IsFinal);
        Assert.Equal(systemPromptResponse.Text, update.DeltaText);
        Assert.Equal(ChatFinishReason.Stop, systemPromptResponse.FinishReason);
        Assert.Null(systemPromptResponse.Usage);
        Assert.Equal("codex", systemPromptResponse.Metadata.ProviderId);
        Assert.Equal("codex-default", systemPromptResponse.Metadata.ModelId);
    }

    [Fact]
    public async Task CancelByTurnIdTerminatesUnderlyingProcessTreeAndRejectsLateResult()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();
        var turnId = Guid.NewGuid();
        var marker = Path.Combine(environment.RootDirectory, "cancelled-child-must-not-survive.txt");
        var running = provider.CompleteAsync(
            Request(
                turnId,
                "只使用请求传入的系统提示。",
                new ChatMessage(
                    ChatMessageRole.User,
                    $"TEST_LONG_RUNNING\nMARKER={marker}")));
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        await provider.CancelAsync(turnId);
        var exception = await Assert.ThrowsAsync<ChatModelException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(ChatModelErrorKind.Cancelled, exception.Error.Kind);
        Assert.Equal("codex.cancelled", exception.Error.Code);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task OverallRequestTimeoutReturnsTypedTimeoutAndTerminatesProcessTree()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider(TimeSpan.FromMilliseconds(500));
        var marker = Path.Combine(environment.RootDirectory, "timed-out-child-must-not-survive.txt");

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(
                    Guid.NewGuid(),
                    "只使用请求传入的系统提示。",
                    new ChatMessage(
                        ChatMessageRole.User,
                        $"TEST_LONG_RUNNING\nMARKER={marker}")))
                .WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(ChatModelErrorKind.Timeout, exception.Error.Kind);
        Assert.Equal("codex.timeout", exception.Error.Code);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task DifferentTurnsCanRunConcurrentlyWithoutCrossCancellation()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();
        var slowTurn = Guid.NewGuid();
        var fastTurn = Guid.NewGuid();
        var marker = Path.Combine(environment.RootDirectory, "concurrent-child.txt");
        var slow = provider.CompleteAsync(
            Request(
                slowTurn,
                "只使用请求传入的系统提示。",
                new ChatMessage(
                    ChatMessageRole.User,
                    $"TEST_LONG_RUNNING\nMARKER={marker}")));
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var fast = await provider.CompleteAsync(
                Request(
                    fastTurn,
                    "只使用请求传入的系统提示。",
                    new ChatMessage(ChatMessageRole.User, "ECHO_CHAT_INPUT")))
            .WaitAsync(TimeSpan.FromSeconds(5));
        await provider.CancelAsync(slowTurn);
        var slowFailure = await Assert.ThrowsAsync<ChatModelException>(
            () => slow.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("已收到对话正文", fast.Text);
        Assert.Equal(ChatModelErrorKind.Cancelled, slowFailure.Error.Kind);
    }

    [Fact]
    public async Task ProviderFailuresReturnTypedSafeErrorWithoutRequestOrPathLeakage()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();
        const string secret = "PRIVATE_HISTORY_SENTINEL";
        var privatePath = Path.Combine(environment.RootDirectory, "private-project");

        var exception = await Assert.ThrowsAsync<ChatModelException>(() => provider.CompleteAsync(
            Request(
                Guid.NewGuid(),
                $"TEST_FAILURE {secret}",
                new ChatMessage(ChatMessageRole.User, privatePath))));

        Assert.Equal("codex", exception.ProviderId);
        Assert.Equal("codex-default", exception.ModelId);
        Assert.Equal(ChatModelErrorKind.Unavailable, exception.Error.Kind);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(privatePath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic failure", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StderrAndTypedExceptionNeverExposeSystemPromptHistoryCredentialOrPath()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();
        const string systemPrompt = "SYSTEM_PROMPT_PRIVATE_SENTINEL";
        const string history = "ASSISTANT_HISTORY_PRIVATE_SENTINEL";
        const string fakeKey = "sk-codex-provider-canary-never-persist";
        var privatePath = Path.Combine(environment.RootDirectory, "private-project");

        var exception = await Assert.ThrowsAsync<ChatModelException>(() => provider.CompleteAsync(
            Request(
                Guid.NewGuid(),
                systemPrompt,
                new ChatMessage(ChatMessageRole.Assistant, history),
                new ChatMessage(
                    ChatMessageRole.User,
                    $"TEST_STDERR_PRIVATE_DATA {fakeKey} {privatePath}"))));
        var exposed = string.Join(
            '\n',
            exception.Message,
            exception.Error.UserMessage,
            exception.Error.Code,
            JsonSerializer.Serialize(exception.Error));

        Assert.Equal(ChatModelErrorKind.Unavailable, exception.Error.Kind);
        Assert.DoesNotContain(systemPrompt, exposed, StringComparison.Ordinal);
        Assert.DoesNotContain(history, exposed, StringComparison.Ordinal);
        Assert.DoesNotContain(fakeKey, exposed, StringComparison.Ordinal);
        Assert.DoesNotContain(privatePath, exposed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LargeStderrStreamIsDrainedWithoutRetainingTheWholePayload()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider(TimeSpan.FromSeconds(20));
        var turnId = Guid.NewGuid();
        var marker = Path.Combine(environment.RootDirectory, "large-stderr-written.txt");
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);
        var running = provider.CompleteAsync(Request(
            turnId,
            "只使用请求传入的系统提示。",
            new ChatMessage(
                ChatMessageRole.User,
                $"TEST_LARGE_STDERR_STREAM\nMARKER={marker}")));

        try
        {
            await WaitUntilAsync(() => File.Exists(marker), TimeSpan.FromSeconds(10));
            var retainedBytes = GC.GetTotalMemory(forceFullCollection: true) - baselineBytes;

            Assert.True(
                retainedBytes < 24L * 1024 * 1024,
                $"stderr 排空不应保留整段内容；当前额外保留 {retainedBytes} 字节。");
        }
        finally
        {
            await provider.CancelAsync(turnId);
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // The memory assertion owns the verdict; this only guarantees process cleanup.
            }
        }
    }

    [Fact]
    public async Task UnterminatedProtocolLineIsRejectedAtTheLimitWithoutWaitingForNewlineOrExit()
    {
        var attempts = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var environment = ChatProviderEnvironment.Create();
            await using var provider = environment.CreateProvider();
            var turnId = Guid.NewGuid();
            var running = provider.CompleteAsync(Request(
                turnId,
                "只使用请求传入的系统提示。",
                new ChatMessage(ChatMessageRole.User, "TEST_OVERSIZED_PROTOCOL_LINE_NO_NEWLINE")));

            try
            {
                var exception = await Assert.ThrowsAsync<ChatModelException>(
                    () => running.WaitAsync(TimeSpan.FromSeconds(3)));

                Assert.Equal(ChatModelErrorKind.InvalidResponse, exception.Error.Kind);
                Assert.Equal("codex.invalid_response", exception.Error.Code);
            }
            finally
            {
                await provider.CancelAsync(turnId);
                try
                {
                    await running.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // The assertion above owns the verdict; this only guarantees process cleanup on red.
                }
            }
        });

        await Task.WhenAll(attempts);
    }

    [Fact]
    public async Task ProtocolLineAtTheExistingLimitRemainsAcceptedWithWindowsLineEnding()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();

        var response = await provider.CompleteAsync(Request(
            Guid.NewGuid(),
            "只使用请求传入的系统提示。",
            new ChatMessage(ChatMessageRole.User, "TEST_PROTOCOL_LINE_AT_LIMIT")))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("这是安全的本机对话回答", response.Text);
    }

    [Fact]
    public async Task HealthUsesCodexCapabilityProbeWithoutExposingExecutablePath()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();

        var health = await provider.CheckHealthAsync();

        Assert.Equal("codex", health.ProviderId);
        Assert.Equal(ChatProviderHealthState.Healthy, health.State);
        Assert.True(health.IsConfigured);
        Assert.DoesNotContain(environment.RootDirectory, health.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownModelIsRejectedAsTypedModelNotFoundError()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();

        var exception = await Assert.ThrowsAsync<ChatModelException>(() => provider.CompleteAsync(
            new ChatModelRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "not-a-codex-chat-model",
                "system",
                [new ChatMessage(ChatMessageRole.User, "hello")])));

        Assert.Equal(ChatModelErrorKind.ModelNotFound, exception.Error.Kind);
    }

    [Fact]
    public async Task OversizedInputAndToolMessagesAreRejectedBeforeStartingCodex()
    {
        await using var environment = ChatProviderEnvironment.Create();
        await using var provider = environment.CreateProvider();

        var oversized = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(
                Guid.NewGuid(),
                "system",
                new ChatMessage(ChatMessageRole.User, new string('x', 200_001)))));
        var toolMessage = await Assert.ThrowsAsync<ChatModelException>(() =>
            provider.CompleteAsync(Request(
                Guid.NewGuid(),
                "system",
                new ChatMessage(ChatMessageRole.Tool, "tool output"))));

        Assert.Equal(ChatModelErrorKind.InvalidRequest, oversized.Error.Kind);
        Assert.Equal("codex.input_too_large", oversized.Error.Code);
        Assert.Equal(ChatModelErrorKind.InvalidRequest, toolMessage.Error.Kind);
        Assert.Equal("codex.tool_messages_not_supported", toolMessage.Error.Code);
    }

    private static ChatModelRequest Request(
        Guid turnId,
        string systemPrompt,
        params ChatMessage[] messages) =>
        new(
            Guid.NewGuid(),
            turnId,
            "codex-default",
            systemPrompt,
            messages);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("测试条件未在期限内出现。");
    }

    private sealed class ChatProviderEnvironment : IAsyncDisposable, IDisposable
    {
        private ChatProviderEnvironment(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            DataDirectory = Path.Combine(rootDirectory, "data");
        }

        public string RootDirectory { get; }

        public string DataDirectory { get; }

        public static ChatProviderEnvironment Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"screen-guide-codex-chat-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new ChatProviderEnvironment(root);
        }

        public CodexChatModelProvider CreateProvider(TimeSpan? requestTimeout = null)
        {
            var options = new CodexChatOptions(
                DataDirectory,
                Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe"))
            {
                ChatRequestTimeout = requestTimeout ?? TimeSpan.FromMinutes(2)
            };
            var constructor = typeof(CodexChatModelProvider)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return parameters.Length == 2
                        && parameters[0].ParameterType == typeof(CodexChatOptions)
                        && parameters[1].ParameterType.IsEnum;
                });
            var policyType = constructor.GetParameters()[1].ParameterType;
            var testPolicy = Enum.Parse(policyType, "TestOnlyAllowLocalCodexCli");
            return (CodexChatModelProvider)constructor.Invoke([options, testPolicy]);
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
