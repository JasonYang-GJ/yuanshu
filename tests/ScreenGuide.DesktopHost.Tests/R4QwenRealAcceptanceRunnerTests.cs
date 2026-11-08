using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.Qwen;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.DesktopV01.RealAcceptanceRunner;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class R4QwenRealAcceptanceRunnerTests
{
    [Fact]
    public void ExactQwenRealAcceptanceArgumentsProduceTheFixedThreeRequestPlan()
    {
        var options = R4QwenValidationOptions.Parse(ValidArguments());

        Assert.True(options.Requested);
        Assert.True(options.IsValid);
        Assert.Null(options.ErrorCode);
        Assert.Equal("qwen", options.ExpectedProviderId);
        Assert.Equal("qwen3.7-plus", options.ExpectedModelId);
        Assert.Equal("ec5c727f143d0f22e347621c10585ebe948b1cda", options.ExpectedCommitSha);
        Assert.Equal(3, options.Budget?.MaxTotalRequests);
        Assert.Equal(2, options.Budget?.MaxModelRequests);
        Assert.Equal(32, options.Budget?.OrdinaryMaxOutputTokens);
        Assert.Equal(256, options.Budget?.CancellationMaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(600), options.Budget?.TotalTimeout);
        Assert.True(options.Budget?.NoAutomaticRetry);
        Assert.True(options.Budget?.NoFallback);
        Assert.True(options.Budget?.NoResend);
        Assert.Equal(
            "ScreenGuide.DesktopV01.RealAcceptanceRunner.exe --stage2-r4-qwen --real-provider --expected-provider=qwen --expected-model=qwen3.7-plus --expected-sha=ec5c727f143d0f22e347621c10585ebe948b1cda --max-total-requests=3 --max-model-requests=2 --ordinary-max-output-tokens=32 --cancellation-max-output-tokens=256 --total-timeout-seconds=600 --no-automatic-retry --no-fallback --no-resend",
            R4QwenLaunchCommand.Create(options.ExpectedCommitSha!));
    }

    [Fact]
    public void ExactHealthOnlyArgumentsProduceOneGetAndZeroModelBudget()
    {
        var options = R4QwenValidationOptions.Parse(HealthOnlyArguments());

        Assert.True(options.Requested);
        Assert.True(options.IsValid);
        Assert.True(options.HealthOnly);
        Assert.Equal(1, options.Budget?.MaxTotalRequests);
        Assert.Equal(0, options.Budget?.MaxModelRequests);
        Assert.Equal(0, options.Budget?.OrdinaryMaxOutputTokens);
        Assert.Equal(0, options.Budget?.CancellationMaxOutputTokens);
        Assert.Equal(
            "ScreenGuide.DesktopV01.RealAcceptanceRunner.exe --stage2-r4-qwen --real-provider --health-only --expected-provider=qwen --expected-model=qwen3.7-plus --expected-sha=38feaa9f4a227644b97fccfe98018ab517f01d1f --max-total-requests=1 --max-model-requests=0 --total-timeout-seconds=600 --no-automatic-retry --no-fallback --no-resend",
            R4QwenLaunchCommand.CreateHealthOnly(options.ExpectedCommitSha!));
    }

    [Theory]
    [MemberData(nameof(InvalidHealthOnlyArgumentSets))]
    public void HealthOnlyArgumentConflictsFailClosed(
        string[] arguments,
        string expectedErrorCode)
    {
        var options = R4QwenValidationOptions.Parse(arguments);

        Assert.True(options.Requested);
        Assert.False(options.IsValid);
        Assert.Equal(expectedErrorCode, options.ErrorCode);
        Assert.Null(options.Budget);
    }

    [Fact]
    public void QwenModeIsDisabledByDefault()
    {
        var options = R4QwenValidationOptions.Parse([]);

        Assert.False(options.Requested);
        Assert.False(options.IsValid);
        Assert.Equal("real_provider_disabled", options.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(InvalidArgumentSets))]
    public void MissingRepeatedOrChangedQwenGatesFailClosed(string[] arguments)
    {
        var options = R4QwenValidationOptions.Parse(arguments);

        Assert.True(options.Requested);
        Assert.False(options.IsValid);
        Assert.NotNull(options.ErrorCode);
        Assert.Null(options.ExpectedProviderId);
        Assert.Null(options.ExpectedModelId);
        Assert.Null(options.Budget);
    }

    [Fact]
    public void MissingExpectedCommitIdentityFailsClosed()
    {
        var arguments = ValidArguments()
            .Where(item => !item.StartsWith("--expected-sha=", StringComparison.Ordinal))
            .ToArray();

        var options = R4QwenValidationOptions.Parse(arguments);

        Assert.True(options.Requested);
        Assert.False(options.IsValid);
        Assert.Equal("r4_expected_sha_missing", options.ErrorCode);
    }

    [Fact]
    public async Task FreshIsolationMaterializesOnlyTheExactQwenRoute()
    {
        using var directory = new TestOwnedDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings", "ai-settings.json");

        var evidence = await R4IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
            settingsPath);

        Assert.Equal("qwen", evidence.ProviderId);
        Assert.Equal("qwen3.7-plus", evidence.ModelId);
        var json = await File.ReadAllTextAsync(settingsPath);
        Assert.Contains("\"providerId\": \"qwen\"", json, StringComparison.Ordinal);
        Assert.Contains("\"modelId\": \"qwen3.7-plus\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingNonQwenIsolationFailsClosedWithoutBeingOverwritten()
    {
        using var directory = new TestOwnedDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings", "ai-settings.json");
        using (var store = new FileAiSettingsStore(
                   settingsPath,
                   new AiSettings(new ChatModelRoute("codex", "codex-default"))))
        {
            await store.SaveAsync(new AiSettings(new ChatModelRoute(
                "deepseek",
                "deepseek-v4-pro")));
        }

        var before = await File.ReadAllBytesAsync(settingsPath);

        var exception = await Assert.ThrowsAsync<R4ValidationFailureException>(() =>
            R4IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(settingsPath));

        Assert.Equal(R4IsolatedAiSettingsMaterializer.MismatchErrorCode, exception.ErrorCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(settingsPath));
    }

    [Fact]
    public void OfflineCompositionUsesTheBudgetedQwenProviderAndForbiddenProviderProbes()
    {
        using var directory = new TestOwnedDirectory();
        var hostOptions = new DesktopHostOptions(
            directory.Path,
            pipeName: $"ScreenGuide.R4.Qwen.Composition.{Guid.NewGuid():N}");
        var validation = R4QwenValidationOptions.Parse(ValidArguments());
        var credentials = new FakeCredentialStore("FAKE_QWEN_SECRET_SENTINEL");
        var transport = new RejectingTransport();

        using var host = R4QwenRunnerHostComposition.BuildOffline(
            hostOptions,
            validation,
            credentials,
            transport);

        var registry = host.Services.GetRequiredService<ChatProviderRegistry>();
        var qwen = registry.GetProviderRequired(QwenChatModelProvider.ProviderId);
        var probe = host.Services.GetRequiredService<R4ForbiddenProviderProbe>();

        Assert.IsType<R4QwenBudgetedChatModelProvider>(qwen);
        Assert.Equal(0, probe.CodexChatCalls);
        Assert.Equal(0, probe.DeepSeekCalls);
        Assert.Equal(0, credentials.OpenLeaseCount);
        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task FrozenRequestForAnotherModelFailsBeforeInnerProviderAndHttp()
    {
        using var directory = new TestOwnedDirectory();
        var hostOptions = new DesktopHostOptions(
            directory.Path,
            pipeName: $"ScreenGuide.R4.Qwen.WrongModel.{Guid.NewGuid():N}");
        var validation = R4QwenValidationOptions.Parse(ValidArguments());
        var credentials = new FakeCredentialStore("FAKE_UNUSED_SECRET");
        var transport = new RejectingTransport();
        using var host = R4QwenRunnerHostComposition.BuildOffline(
            hostOptions,
            validation,
            credentials,
            transport);
        var provider = host.Services.GetRequiredService<R4QwenBudgetedChatModelProvider>();
        _ = provider.PrepareNextCall(R4QwenCallMode.Ordinary);

        var exception = await Assert.ThrowsAsync<R4ValidationFailureException>(() =>
            provider.CompleteAsync(new ChatModelRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "deepseek-v4-pro",
                "fake-system",
                [new ChatMessage(ChatMessageRole.User, "fake-user")],
                new ChatModelOptions(MaxOutputTokens: 32))));

        Assert.Equal("r4_expected_route_mismatch", exception.ErrorCode);
        Assert.Equal(0, provider.TotalRequestCount);
        Assert.Equal(0, provider.ModelRequestCount);
        Assert.Equal(0, credentials.OpenLeaseCount);
        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task SharedOfflineRunnerPathProvesHealthOrdinaryAndCancellationWithoutSecretDisclosure()
    {
        const string credentialSentinel = "FAKE_QWEN_SECRET_SENTINEL";
        const string reasoningSentinel = "PRIVATE_QWEN_REASONING_MUST_NOT_ESCAPE";
        using var directory = new TestOwnedDirectory();
        var hostOptions = new DesktopHostOptions(
            Path.Combine(directory.Path, "isolated-data"),
            pipeName: $"ScreenGuide.R4.Qwen.Execution.{Guid.NewGuid():N}");
        var validation = R4QwenValidationOptions.Parse(ValidArguments());
        var credentials = new FakeCredentialStore(credentialSentinel);
        var transport = new OfflineAcceptanceTransport(reasoningSentinel);
        _ = await R4IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
            hostOptions.AiSettingsPath);
        using var host = R4QwenRunnerHostComposition.BuildOffline(
            hostOptions,
            validation,
            credentials,
            transport);
        var stopped = false;
        await host.StartAsync();
        try
        {
            var api = new DesktopApiClient(hostOptions.PipeName, TimeSpan.FromSeconds(5));
            Assert.True(await api.PingAsync());
            var provider = host.Services.GetRequiredService<R4QwenBudgetedChatModelProvider>();
            var buildIdentity = new R4BuildIdentityEvidence(
                validation.ExpectedCommitSha!,
                validation.ExpectedCommitSha,
                $"0.3.0+{validation.ExpectedCommitSha}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var outcome = await R4QwenRunnerExecution.ExecuteAsync(
                api,
                host,
                provider,
                validation,
                buildIdentity,
                timeout.Token);

            Assert.True(outcome.Passed, outcome.StructuredEvidenceJson);
            Assert.True(outcome.Result.Passed);
            Assert.Equal("full", outcome.Result.Mode);
            Assert.Equal("complete", outcome.Result.Stage);
            Assert.True(outcome.Result.BuildIdentity.IsExactMatch);
            Assert.Equal("qwen", outcome.Result.SettingsProviderId);
            Assert.Equal("qwen3.7-plus", outcome.Result.SettingsModelId);
            Assert.True(outcome.Result.ModelEvidence?.IsCompleteMatch);
            Assert.True(outcome.Result.HealthPassed);
            Assert.Equal("Healthy", outcome.Result.HealthState);
            Assert.Equal("healthy", outcome.Result.HealthMessageCategory);
            Assert.True(outcome.Result.HealthResponseShapeEvidence?.ParseValid);
            Assert.True(outcome.Result.HealthResponseShapeEvidence?.ExactModelMatch);
            Assert.True(outcome.Result.OrdinaryPassed);
            Assert.True(outcome.Result.OrdinaryReplyExact);
            Assert.True(outcome.Result.CancellationPassed);
            Assert.True(outcome.Result.CancellationTerminalEvidence?.IsCancelled);
            Assert.Equal(1, outcome.Result.CancellationRequestCount);
            Assert.True(outcome.Result.LateDeltaRejected);
            Assert.True(outcome.Result.LateFinalRejected);
            Assert.True(outcome.Result.LateSuccessRejected);
            Assert.True(outcome.Result.SuccessfulUsageRecorded);
            Assert.True(outcome.Result.SuccessfulProviderRequestIdRecorded);
            Assert.True(outcome.Result.CancellationUsageAbsent);
            Assert.True(outcome.Result.CancellationProviderRequestIdAbsent);
            Assert.Equal(3, outcome.Result.TotalRequestCount);
            Assert.Equal(1, outcome.Result.HealthRequestCount);
            Assert.Equal(2, outcome.Result.ModelRequestCount);
            Assert.Equal(3, outcome.Result.HttpTotalRequestCount);
            Assert.Equal(1, outcome.Result.HttpHealthRequestCount);
            Assert.Equal(2, outcome.Result.HttpModelRequestCount);
            Assert.Equal(0, outcome.Result.DeepSeekCallCount);
            Assert.Equal(0, outcome.Result.CodexChatCallCount);
            Assert.Equal(1, outcome.Result.SessionCount);
            Assert.Equal(1, outcome.Result.ConversationCount);
            Assert.Equal(2, outcome.Result.AiInvocationCount);
            Assert.True(outcome.Result.NoAutomaticRetry);
            Assert.True(outcome.Result.NoFallback);
            Assert.True(outcome.Result.NoResend);
            Assert.Equal(3, transport.SendCount);
            Assert.Equal(1, transport.HealthRequests);
            Assert.Equal(2, transport.ModelRequests);
            Assert.True(transport.OrdinaryPayloadMatched);
            Assert.True(transport.CancellationPayloadMatched);
            Assert.True(transport.CancellationObserved);
            Assert.Equal(3, credentials.OpenLeaseCount);
            Assert.Equal(3, credentials.DisposedLeaseCount);
            var responseShapes = outcome.Result.ResponseShapeEvidence;
            Assert.Equal(2, responseShapes.Count);
            Assert.True(responseShapes[0].ReasoningContentAppeared);
            Assert.Equal(reasoningSentinel.Length, responseShapes[0].ReasoningContentCharacterCount);
            Assert.True(responseShapes[0].DoneAppeared);
            Assert.True(responseShapes[1].ContentAppeared);
            Assert.False(responseShapes[1].DoneAppeared);
            Assert.DoesNotContain(credentialSentinel, outcome.StructuredEvidenceJson, StringComparison.Ordinal);
            Assert.DoesNotContain(reasoningSentinel, outcome.StructuredEvidenceJson, StringComparison.Ordinal);
            Assert.DoesNotContain(R4QwenRunnerExecution.OrdinaryCanary, outcome.StructuredEvidenceJson, StringComparison.Ordinal);
            Assert.DoesNotContain(R4QwenRunnerExecution.CancellationCanary, outcome.StructuredEvidenceJson, StringComparison.Ordinal);
            Assert.DoesNotContain("FAKE-QWEN-REQUEST-ID", outcome.StructuredEvidenceJson, StringComparison.Ordinal);
            Assert.DoesNotContain("FAKE-HEALTH-ID", outcome.StructuredEvidenceJson, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", outcome.StructuredEvidenceJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("data:", outcome.StructuredEvidenceJson, StringComparison.Ordinal);

            var snapshot = await api.GetCurrentSessionAsync(timeout.Token);
            Assert.NotNull(snapshot);
            Assert.Equal(["Completed", "Cancelled"], snapshot!.Turns.Select(item => item.Phase).ToArray());
            var conversationTurns = await host.Services.GetRequiredService<IConversationStore>()
                .GetTurnsAsync(snapshot.ConversationId, timeout.Token);
            Assert.Equal(
                [ConversationTurnStatus.Succeeded, ConversationTurnStatus.Cancelled],
                conversationTurns.Select(item => item.Status).ToArray());
            var invocations = new List<AiInvocationRecord>();
            foreach (var turn in snapshot.Turns)
            {
                invocations.AddRange(await host.Services.GetRequiredService<IAiInvocationStore>()
                    .GetForSessionTurnAsync(turn.Id, timeout.Token));
            }

            Assert.Equal(
                [AiInvocationStatus.Succeeded, AiInvocationStatus.Cancelled],
                invocations.Where(item => item.Purpose == AiInvocationPurpose.Conversation)
                    .Select(item => item.Status)
                    .ToArray());

            var prompts = host.Services.GetRequiredService<PromptRegistry>();
            var expectedPrompt = prompts.GetRequired("chat.general", "4", "qwen");
            var historicalPrompt = prompts.GetRequired("chat.general", "1", "qwen");
            foreach (var invocation in invocations.Where(item => item.Purpose == AiInvocationPurpose.Conversation))
            {
                Assert.Equal("chat.general", invocation.PromptId);
                Assert.Equal("4", invocation.PromptVersion);
                Assert.Equal(expectedPrompt.ContentSha256, invocation.PromptContentHash);
                var turn = snapshot.Turns.Single(item => item.Id == invocation.SessionTurnId);
                var succeeded = invocation.Status == AiInvocationStatus.Succeeded;
                var invalidPromptRecords = new[]
                {
                    invocation with { PromptId = "intent.semantic" },
                    invocation with { PromptVersion = "1" },
                    invocation with { PromptVersion = "2" },
                    invocation with { PromptVersion = "3" },
                    invocation with
                    {
                        PromptVersion = "1",
                        PromptContentHash = historicalPrompt.ContentSha256
                    },
                    invocation with { PromptContentHash = historicalPrompt.ContentSha256 },
                    invocation with { PromptContentHash = new string('0', 64) }
                };
                foreach (var invalid in invalidPromptRecords)
                {
                    var exception = Assert.Throws<R4ValidationFailureException>(() =>
                        R4QwenRunnerExecution.RequireConversationInvocation(
                            [invalid], provider, turn, expectedPrompt, invocation.Status,
                            requireUsage: succeeded,
                            requireProviderRequestId: succeeded));
                    Assert.Equal("r4_invocation_mismatch", exception.ErrorCode);
                }
            }

            await host.StopAsync(timeout.Token);
            stopped = true;
            Assert.All(
                Directory.EnumerateFiles(hostOptions.DataDirectory, "*", SearchOption.AllDirectories),
                path => Assert.False(File.ReadAllBytes(path).AsSpan().IndexOf(
                    Encoding.UTF8.GetBytes(credentialSentinel)) >= 0));
            Assert.All(
                Directory.EnumerateFiles(hostOptions.DataDirectory, "*", SearchOption.AllDirectories),
                path => Assert.False(File.ReadAllBytes(path).AsSpan().IndexOf(
                    Encoding.UTF8.GetBytes(reasoningSentinel)) >= 0));
            Assert.False(Directory.Exists(hostOptions.SecretsDirectory));
        }
        finally
        {
            if (!stopped)
            {
                await host.StopAsync();
            }
        }
    }

    [Theory]
    [InlineData(FakeCredentialMode.Missing)]
    [InlineData(FakeCredentialMode.StatusUnavailable)]
    public async Task MissingOrUnavailableCredentialFailsBeforeSessionAndHttp(
        FakeCredentialMode mode)
    {
        using var directory = new TestOwnedDirectory();
        var hostOptions = new DesktopHostOptions(
            Path.Combine(directory.Path, "isolated-data"),
            pipeName: $"ScreenGuide.R4.Qwen.NoCredential.{Guid.NewGuid():N}");
        var validation = R4QwenValidationOptions.Parse(ValidArguments());
        var credentials = new FakeCredentialStore("FAKE_UNUSED_SECRET", mode);
        var transport = new RejectingTransport();
        _ = await R4IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
            hostOptions.AiSettingsPath);
        using var host = R4QwenRunnerHostComposition.BuildOffline(
            hostOptions,
            validation,
            credentials,
            transport);
        await host.StartAsync();
        try
        {
            var api = new DesktopApiClient(hostOptions.PipeName, TimeSpan.FromSeconds(5));
            var provider = host.Services.GetRequiredService<R4QwenBudgetedChatModelProvider>();
            var buildIdentity = new R4BuildIdentityEvidence(
                validation.ExpectedCommitSha!,
                validation.ExpectedCommitSha,
                $"0.3.0+{validation.ExpectedCommitSha}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            var outcome = await R4QwenRunnerExecution.ExecuteAsync(
                api,
                host,
                provider,
                validation,
                buildIdentity,
                timeout.Token);

            Assert.False(outcome.Passed);
            Assert.Equal("settings", outcome.Result.Stage);
            Assert.Equal("r4_credential_not_configured", outcome.Result.ErrorCode);
            Assert.Equal("qwen.not_configured", outcome.Result.ProviderDiagnosticCode);
            Assert.Equal(0, outcome.Result.TotalRequestCount);
            Assert.Equal(0, outcome.Result.HttpTotalRequestCount);
            Assert.Equal(0, transport.SendCount);
            Assert.Equal(0, credentials.OpenLeaseCount);
            var snapshot = await api.GetCurrentSessionAsync();
            Assert.Null(snapshot);
            Assert.DoesNotContain("FAKE_UNUSED_SECRET", outcome.StructuredEvidenceJson, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(FakeCredentialMode.LeaseUnavailable)]
    [InlineData(FakeCredentialMode.EmptyLease)]
    public async Task UnavailableOrEmptyLeaseFailsHealthBeforeHttpWithoutFallback(
        FakeCredentialMode mode)
    {
        using var directory = new TestOwnedDirectory();
        var hostOptions = new DesktopHostOptions(
            Path.Combine(directory.Path, "isolated-data"),
            pipeName: $"ScreenGuide.R4.Qwen.NoLease.{Guid.NewGuid():N}");
        var validation = R4QwenValidationOptions.Parse(ValidArguments());
        var credentials = new FakeCredentialStore("FAKE_UNUSED_SECRET", mode);
        var transport = new RejectingTransport();
        _ = await R4IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
            hostOptions.AiSettingsPath);
        using var host = R4QwenRunnerHostComposition.BuildOffline(
            hostOptions,
            validation,
            credentials,
            transport);
        await host.StartAsync();
        try
        {
            var api = new DesktopApiClient(hostOptions.PipeName, TimeSpan.FromSeconds(5));
            var provider = host.Services.GetRequiredService<R4QwenBudgetedChatModelProvider>();
            var buildIdentity = new R4BuildIdentityEvidence(
                validation.ExpectedCommitSha!,
                validation.ExpectedCommitSha,
                $"0.3.0+{validation.ExpectedCommitSha}");

            var outcome = await R4QwenRunnerExecution.ExecuteAsync(
                api,
                host,
                provider,
                validation,
                buildIdentity);

            Assert.False(outcome.Passed);
            Assert.Equal("health", outcome.Result.Stage);
            Assert.Equal("r4_credential_not_configured", outcome.Result.ErrorCode);
            Assert.Equal("qwen.not_configured", outcome.Result.ProviderDiagnosticCode);
            Assert.Equal(1, outcome.Result.TotalRequestCount);
            Assert.Equal(0, outcome.Result.HttpTotalRequestCount);
            Assert.Equal(0, outcome.Result.ModelRequestCount);
            Assert.Equal(0, transport.SendCount);
            Assert.Equal(1, credentials.OpenLeaseCount);
            Assert.Equal(mode == FakeCredentialMode.EmptyLease ? 1 : 0, credentials.DisposedLeaseCount);
            Assert.Equal(0, outcome.Result.DeepSeekCallCount);
            Assert.Equal(0, outcome.Result.CodexChatCallCount);
            Assert.DoesNotContain("FAKE_UNUSED_SECRET", outcome.StructuredEvidenceJson, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task OrdinaryFailureStopsBeforeCancellationAndPreservesSafeProviderDiagnostic()
    {
        using var directory = new TestOwnedDirectory();
        var hostOptions = new DesktopHostOptions(
            Path.Combine(directory.Path, "isolated-data"),
            pipeName: $"ScreenGuide.R4.Qwen.OrdinaryFailure.{Guid.NewGuid():N}");
        var validation = R4QwenValidationOptions.Parse(ValidArguments());
        var credentials = new FakeCredentialStore("FAKE_QWEN_SECRET_SENTINEL");
        var transport = new OrdinaryFailureTransport();
        _ = await R4IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
            hostOptions.AiSettingsPath);
        using var host = R4QwenRunnerHostComposition.BuildOffline(
            hostOptions,
            validation,
            credentials,
            transport);
        await host.StartAsync();
        try
        {
            var api = new DesktopApiClient(hostOptions.PipeName, TimeSpan.FromSeconds(5));
            var provider = host.Services.GetRequiredService<R4QwenBudgetedChatModelProvider>();
            var buildIdentity = new R4BuildIdentityEvidence(
                validation.ExpectedCommitSha!,
                validation.ExpectedCommitSha,
                $"0.3.0+{validation.ExpectedCommitSha}");

            var outcome = await R4QwenRunnerExecution.ExecuteAsync(
                api,
                host,
                provider,
                validation,
                buildIdentity);

            Assert.False(outcome.Passed);
            Assert.Equal("ordinary", outcome.Result.Stage);
            Assert.Equal("invalid_response", outcome.Result.ErrorCode);
            Assert.Equal("qwen.stream_empty", outcome.Result.ProviderDiagnosticCode);
            Assert.Equal(2, outcome.Result.TotalRequestCount);
            Assert.Equal(2, outcome.Result.HttpTotalRequestCount);
            Assert.Equal(1, outcome.Result.ModelRequestCount);
            Assert.False(outcome.Result.CancellationExecuted);
            Assert.Equal(0, outcome.Result.DeepSeekCallCount);
            Assert.Equal(0, outcome.Result.CodexChatCallCount);
            Assert.Equal(2, transport.SendCount);
            Assert.DoesNotContain("FAKE_QWEN_SECRET_SENTINEL", outcome.StructuredEvidenceJson, StringComparison.Ordinal);
            Assert.DoesNotContain("data:", outcome.StructuredEvidenceJson, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Http200HealthFailurePreservesSafeStateDiagnosticAndResponseShape()
    {
        const string responseMessageSentinel = "PRIVATE_HEALTH_MESSAGE_MUST_NOT_ESCAPE";
        const string requestIdSentinel = "PRIVATE_HEALTH_REQUEST_ID_MUST_NOT_ESCAPE";
        using var directory = new TestOwnedDirectory();
        var hostOptions = new DesktopHostOptions(
            Path.Combine(directory.Path, "isolated-data"),
            pipeName: $"ScreenGuide.R4.Qwen.HealthEvidence.{Guid.NewGuid():N}");
        var validation = R4QwenValidationOptions.Parse(ValidArguments());
        var credentials = new FakeCredentialStore("FAKE_QWEN_SECRET_SENTINEL");
        var transport = new HealthFixtureTransport($$"""
            {"success":true,"code":null,"message":"{{responseMessageSentinel}}","request_id":"{{requestIdSentinel}}"}
            """);
        _ = await R4IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
            hostOptions.AiSettingsPath);
        using var host = R4QwenRunnerHostComposition.BuildOffline(
            hostOptions,
            validation,
            credentials,
            transport);
        await host.StartAsync();
        try
        {
            var api = new DesktopApiClient(hostOptions.PipeName, TimeSpan.FromSeconds(5));
            var provider = host.Services.GetRequiredService<R4QwenBudgetedChatModelProvider>();
            var buildIdentity = new R4BuildIdentityEvidence(
                validation.ExpectedCommitSha!,
                validation.ExpectedCommitSha,
                $"0.3.0+{validation.ExpectedCommitSha}");

            var outcome = await R4QwenRunnerExecution.ExecuteAsync(
                api,
                host,
                provider,
                validation,
                buildIdentity);

            Assert.False(outcome.Passed);
            Assert.Equal("health", outcome.Result.Stage);
            Assert.Equal("r4_health_failed", outcome.Result.ErrorCode);
            Assert.Equal("qwen.health.permission_unavailable", outcome.Result.ProviderDiagnosticCode);
            Assert.Equal("Unavailable", outcome.Result.HealthState);
            Assert.Equal("permission_unavailable", outcome.Result.HealthMessageCategory);
            Assert.False(outcome.Result.HealthPassed);
            Assert.Equal(1, outcome.Result.HttpTotalRequestCount);
            Assert.Equal(0, outcome.Result.HttpModelRequestCount);
            var shape = Assert.IsType<R4HealthResponseShapeEvidence>(
                outcome.Result.HealthResponseShapeEvidence);
            Assert.Equal(200, shape.HttpStatus);
            Assert.Equal("application_json", shape.ContentTypeCategory);
            Assert.True(shape.BodyByteCount > 0);
            Assert.Equal("object", shape.RootJsonKind);
            Assert.True(shape.ParseValid);
            Assert.False(shape.SizeLimitExceeded);
            Assert.False(shape.Truncated);
            Assert.Equal(new R4JsonFieldEvidence(true, "true", "true", null), shape.Success);
            Assert.Equal(new R4JsonFieldEvidence(true, "null", "null", null), shape.Code);
            Assert.Equal(new R4JsonFieldEvidence(false, "missing", null, null), shape.Output);
            Assert.False(shape.ExactlyOneModel);
            Assert.False(shape.ExactModelMatch);
            Assert.DoesNotContain(responseMessageSentinel, outcome.StructuredEvidenceJson, StringComparison.Ordinal);
            Assert.DoesNotContain(requestIdSentinel, outcome.StructuredEvidenceJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"message\":", outcome.StructuredEvidenceJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("request_id", outcome.StructuredEvidenceJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData("authorized-model", true)]
    [InlineData("default-workspace-empty", true)]
    [InlineData("wrong-model", false)]
    public async Task HealthOnlyExecutionStopsAfterOneGetWithoutSessionOrModelWork(
        string responseShape,
        bool healthy)
    {
        using var directory = new TestOwnedDirectory();
        var hostOptions = new DesktopHostOptions(
            Path.Combine(directory.Path, "isolated-data"),
            pipeName: $"ScreenGuide.R4.Qwen.HealthOnly.{Guid.NewGuid():N}");
        var validation = R4QwenValidationOptions.Parse(HealthOnlyArguments());
        var credentials = new FakeCredentialStore("FAKE_QWEN_SECRET_SENTINEL");
        var transport = new HealthFixtureTransport(
            responseShape switch
            {
                "authorized-model" => OfficialHealthJson,
                "default-workspace-empty" =>
                    """{"success":true,"code":null,"output":{"total":0,"page_no":1,"page_size":1,"permissions":[]}}""",
                "wrong-model" =>
                    """{"success":true,"code":null,"output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen-other","permissions":{"inference":true}}]}}""",
                _ => throw new ArgumentOutOfRangeException(nameof(responseShape))
            });
        _ = await R4IsolatedAiSettingsMaterializer.MaterializeAndVerifyAsync(
            hostOptions.AiSettingsPath);
        using var host = R4QwenRunnerHostComposition.BuildOffline(
            hostOptions,
            validation,
            credentials,
            transport);
        await host.StartAsync();
        try
        {
            var api = new DesktopApiClient(hostOptions.PipeName, TimeSpan.FromSeconds(5));
            var provider = host.Services.GetRequiredService<R4QwenBudgetedChatModelProvider>();
            var buildIdentity = new R4BuildIdentityEvidence(
                validation.ExpectedCommitSha!,
                validation.ExpectedCommitSha,
                $"0.3.0+{validation.ExpectedCommitSha}");

            var outcome = await R4QwenRunnerExecution.ExecuteAsync(
                api,
                host,
                provider,
                validation,
                buildIdentity);

            Assert.Equal(healthy, outcome.Passed);
            Assert.Equal("health-only", outcome.Result.Mode);
            Assert.Equal(healthy ? "complete" : "health", outcome.Result.Stage);
            Assert.Equal(healthy ? null : "r4_health_failed", outcome.Result.ErrorCode);
            Assert.Equal(healthy ? null : "qwen.health.permission_unavailable", outcome.Result.ProviderDiagnosticCode);
            Assert.True(outcome.Result.HealthExecuted);
            Assert.Equal(healthy, outcome.Result.HealthPassed);
            Assert.Equal(healthy ? "Healthy" : "Unavailable", outcome.Result.HealthState);
            Assert.Equal(healthy ? "healthy" : "permission_unavailable", outcome.Result.HealthMessageCategory);
            Assert.False(outcome.Result.OrdinaryExecuted);
            Assert.False(outcome.Result.CancellationExecuted);
            Assert.Equal(1, outcome.Result.TotalRequestCount);
            Assert.Equal(1, outcome.Result.HealthRequestCount);
            Assert.Equal(0, outcome.Result.ModelRequestCount);
            Assert.Equal(1, outcome.Result.HttpTotalRequestCount);
            Assert.Equal(1, outcome.Result.HttpHealthRequestCount);
            Assert.Equal(0, outcome.Result.HttpModelRequestCount);
            Assert.Equal(0, outcome.Result.DeepSeekCallCount);
            Assert.Equal(0, outcome.Result.CodexChatCallCount);
            Assert.Equal(0, outcome.Result.SessionCount);
            Assert.Equal(0, outcome.Result.ConversationCount);
            Assert.Equal(0, outcome.Result.AiInvocationCount);
            Assert.True(outcome.Result.NoAutomaticRetry);
            Assert.True(outcome.Result.NoFallback);
            Assert.True(outcome.Result.NoResend);
            Assert.True(outcome.Result.HealthResponseShapeEvidence?.ParseValid);
            Assert.Equal(1, transport.SendCount);
            Assert.Null(await api.GetCurrentSessionAsync());
            Assert.Empty(await host.Services.GetRequiredService<ISessionStore>().GetSessionsAsync());
            Assert.Empty(await host.Services.GetRequiredService<IConversationStore>().GetConversationsAsync());
            Assert.DoesNotContain("FAKE_QWEN_SECRET_SENTINEL", outcome.StructuredEvidenceJson, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE_HEALTH", outcome.StructuredEvidenceJson, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [MemberData(nameof(HealthShapeFixtures))]
    public async Task HealthShapeEvidenceClassifiesOfflineFixturesWithoutChangingProviderResult(
        HealthShapeFixture fixture)
    {
        using var directory = new TestOwnedDirectory();
        var hostOptions = new DesktopHostOptions(
            directory.Path,
            pipeName: $"ScreenGuide.R4.Qwen.HealthShape.{Guid.NewGuid():N}");
        var validation = R4QwenValidationOptions.Parse(ValidArguments());
        var credentials = new FakeCredentialStore("FAKE_QWEN_SECRET_SENTINEL");
        var transport = new HealthFixtureTransport(fixture.Body);
        using var host = R4QwenRunnerHostComposition.BuildOffline(
            hostOptions,
            validation,
            credentials,
            transport);
        var provider = host.Services.GetRequiredService<R4QwenBudgetedChatModelProvider>();

        var health = await provider.CheckHealthAsync();

        Assert.Equal(fixture.ExpectedHealthState, health.State.ToString());
        Assert.Equal(1, provider.TotalRequestCount);
        Assert.Equal(1, transport.SendCount);
        Assert.Equal(1, credentials.OpenLeaseCount);
        Assert.Equal(1, credentials.DisposedLeaseCount);
        var evidence = Assert.IsType<R4HealthResponseShapeEvidence>(
            host.Services.GetRequiredService<R4QwenHttpEvidence>().HealthResponseShape);
        Assert.Equal(200, evidence.HttpStatus);
        Assert.Equal("application_json", evidence.ContentTypeCategory);
        Assert.True(evidence.BodyByteCount > 0);
        Assert.Equal(fixture.ParseValid ? "object" : "unknown", evidence.RootJsonKind);
        Assert.Equal(fixture.ParseValid, evidence.ParseValid);
        Assert.Equal(fixture.SizeLimitExceeded, evidence.SizeLimitExceeded);
        Assert.Equal(fixture.Truncated, evidence.Truncated);
        Assert.Equal(fixture.OutputKind, evidence.Output.Kind);
        Assert.Equal(fixture.PermissionsCount, evidence.PermissionsCount);
        Assert.Equal(fixture.ExactlyOneModel, evidence.ExactlyOneModel);
        Assert.Equal(fixture.ExactModelMatch, evidence.ExactModelMatch);
        Assert.Equal(fixture.InferenceKind, evidence.Inference.Kind);
        Assert.Equal(fixture.InferenceCategory, evidence.Inference.ValueCategory);
        Assert.Equal(fixture.ExpectedTotal, evidence.Total.NumericValue);
        Assert.Equal(fixture.ExpectedPageNo, evidence.PageNo.NumericValue);
        Assert.Equal(fixture.ExpectedPageSize, evidence.PageSize.NumericValue);
        var serialized = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain("PRIVATE_HEALTH", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("qwen-unknown-model", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("request_id", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"message\":", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonemptyHealthCodeIsCategorizedWithoutRecordingItsValue()
    {
        const string codeSentinel = "PRIVATE_HEALTH_CODE_MUST_NOT_ESCAPE";
        using var directory = new TestOwnedDirectory();
        var hostOptions = new DesktopHostOptions(
            directory.Path,
            pipeName: $"ScreenGuide.R4.Qwen.HealthCode.{Guid.NewGuid():N}");
        var validation = R4QwenValidationOptions.Parse(ValidArguments());
        var credentials = new FakeCredentialStore("FAKE_QWEN_SECRET_SENTINEL");
        var transport = new HealthFixtureTransport(
            $$"""{"success":false,"code":"{{codeSentinel}}","message":"PRIVATE_HEALTH_MESSAGE"}""");
        using var host = R4QwenRunnerHostComposition.BuildOffline(
            hostOptions,
            validation,
            credentials,
            transport);
        var provider = host.Services.GetRequiredService<R4QwenBudgetedChatModelProvider>();

        var health = await provider.CheckHealthAsync();

        Assert.Equal("Unavailable", health.State.ToString());
        var evidence = Assert.IsType<R4HealthResponseShapeEvidence>(
            host.Services.GetRequiredService<R4QwenHttpEvidence>().HealthResponseShape);
        Assert.Equal(new R4JsonFieldEvidence(true, "false", "false", null), evidence.Success);
        Assert.Equal(new R4JsonFieldEvidence(true, "string", "nonempty", null), evidence.Code);
        var serialized = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain(codeSentinel, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_HEALTH_MESSAGE", serialized, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> InvalidArgumentSets
    {
        get
        {
            var valid = ValidArguments();
            return new TheoryData<string[]>
            {
                valid.Where(item => item != "--real-provider").ToArray(),
                valid.Concat(["--stage2-r4-qwen"]).ToArray(),
                valid.Where(item => !item.StartsWith("--expected-provider=", StringComparison.Ordinal)).ToArray(),
                valid.Concat(["--expected-provider=qwen"]).ToArray(),
                Replace(valid, "--expected-provider=qwen", "--expected-provider=deepseek"),
                valid.Where(item => !item.StartsWith("--expected-model=", StringComparison.Ordinal)).ToArray(),
                valid.Concat(["--expected-model=qwen3.7-plus"]).ToArray(),
                Replace(valid, "--expected-model=qwen3.7-plus", "--expected-model=deepseek-v4-pro"),
                Replace(valid, "--max-total-requests=3", "--max-total-requests=4"),
                Replace(valid, "--max-model-requests=2", "--max-model-requests=3"),
                Replace(valid, "--ordinary-max-output-tokens=32", "--ordinary-max-output-tokens=33"),
                Replace(valid, "--cancellation-max-output-tokens=256", "--cancellation-max-output-tokens=257"),
                Replace(valid, "--total-timeout-seconds=600", "--total-timeout-seconds=601"),
                valid.Where(item => item != "--no-automatic-retry").ToArray(),
                valid.Where(item => item != "--no-fallback").ToArray(),
                valid.Where(item => item != "--no-resend").ToArray()
            };
        }
    }

    public static TheoryData<string[], string> InvalidHealthOnlyArgumentSets
    {
        get
        {
            var valid = HealthOnlyArguments();
            return new TheoryData<string[], string>
            {
                { valid.Where(item => item != "--health-only").ToArray(), "r4_health_only_required" },
                { valid.Concat(["--health-only"]).ToArray(), "r4_health_only_mode_repeated" },
                { Replace(valid, "--max-total-requests=1", "--max-total-requests=3"), "r4_health_only_budget_out_of_range" },
                { Replace(valid, "--max-model-requests=0", "--max-model-requests=2"), "r4_health_only_budget_out_of_range" },
                { Replace(valid, "--max-model-requests=0", "--max-model-requests=1"), "r4_health_only_budget_out_of_range" },
                { valid.Concat(["--ordinary-max-output-tokens=32"]).ToArray(), "r4_health_only_token_arguments_forbidden" },
                { valid.Concat(["--cancellation-max-output-tokens=256"]).ToArray(), "r4_health_only_token_arguments_forbidden" },
                { valid.Concat(["--cancellation-only"]).ToArray(), "r4_real_provider_mode_conflict" },
                { valid.Where(item => item != "--no-automatic-retry").ToArray(), "r4_real_provider_budget_missing" },
                { valid.Concat(["--no-automatic-retry"]).ToArray(), "r4_real_provider_budget_missing" },
                { valid.Where(item => item != "--no-fallback").ToArray(), "r4_real_provider_budget_missing" },
                { valid.Concat(["--no-fallback"]).ToArray(), "r4_real_provider_budget_missing" },
                { valid.Where(item => item != "--no-resend").ToArray(), "r4_real_provider_budget_missing" },
                { valid.Concat(["--no-resend"]).ToArray(), "r4_real_provider_budget_missing" }
            };
        }
    }

    public static TheoryData<HealthShapeFixture> HealthShapeFixtures =>
        new()
        {
            new HealthShapeFixture(
                "official-success",
                OfficialHealthJson,
                "Healthy",
                ParseValid: true,
                SizeLimitExceeded: false,
                Truncated: false,
                OutputKind: "object",
                PermissionsCount: 1,
                ExactlyOneModel: true,
                ExactModelMatch: true,
                InferenceKind: "true",
                InferenceCategory: "true",
                ExpectedTotal: 1,
                ExpectedPageNo: 1,
                ExpectedPageSize: 1),
            new HealthShapeFixture(
                "missing-output",
                """{"success":true,"code":null,"message":"PRIVATE_HEALTH_MESSAGE","request_id":"PRIVATE_HEALTH_ID"}""",
                "Unavailable", true, false, false, "missing", null, false, false, "missing", null, null, null, null),
            new HealthShapeFixture(
                "wrong-kinds",
                """{"success":"yes","code":7,"output":[]}""",
                "Unavailable", true, false, false, "array", null, false, false, "missing", null, null, null, null),
            new HealthShapeFixture(
                "paging-mismatch",
                PermissionHealthJson(total: 2, pageNo: 2, pageSize: 3),
                "Unavailable", true, false, false, "object", 1, true, true, "true", "true", 2, 2, 3),
            new HealthShapeFixture(
                "empty-permissions",
                """{"success":true,"code":null,"output":{"total":0,"page_no":1,"page_size":1,"permissions":[]}}""",
                "Healthy", true, false, false, "object", 0, false, false, "missing", null, 0, 1, 1),
            new HealthShapeFixture(
                "multiple-permissions",
                """{"success":true,"code":null,"output":{"total":2,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":true}},{"model":"qwen3.7-plus","permissions":{"inference":true}}]}}""",
                "Unavailable", true, false, false, "object", 2, false, false, "missing", null, 2, 1, 1),
            new HealthShapeFixture(
                "model-mismatch",
                """{"success":true,"code":null,"output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen-unknown-model","permissions":{"inference":true}}]}}""",
                "Unavailable", true, false, false, "object", 1, true, false, "true", "true", 1, 1, 1),
            new HealthShapeFixture(
                "missing-inference",
                """{"success":true,"code":null,"output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{}}]}}""",
                "Unavailable", true, false, false, "object", 1, true, true, "missing", null, 1, 1, 1),
            new HealthShapeFixture(
                "false-inference",
                """{"success":true,"code":null,"output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":false}}]}}""",
                "Unavailable", true, false, false, "object", 1, true, true, "false", "false", 1, 1, 1),
            new HealthShapeFixture(
                "legacy-data-array",
                """{"success":true,"code":null,"total":1,"page_no":1,"page_size":1,"data":[{"model":"qwen3.7-plus"}]}""",
                "Unavailable", true, false, false, "missing", null, false, false, "missing", null, null, null, null),
            new HealthShapeFixture(
                "malformed",
                """{"success":true,"code":null,"output":{""",
                "Unavailable", false, false, false, "missing", null, false, false, "missing", null, null, null, null),
            new HealthShapeFixture(
                "oversized",
                "{\"success\":true,\"code\":null,\"padding\":\"" + new string('X', R4HealthResponseShapeCollector.MaxEvidenceBytes) + "\"}",
                "Unavailable", false, true, true, "missing", null, false, false, "missing", null, null, null, null)
        };

    private const string OfficialHealthJson =
        """{"success":true,"code":null,"message":"PRIVATE_HEALTH_MESSAGE","request_id":"PRIVATE_HEALTH_ID","output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","name":"Qwen 3.7 Plus","permissions":{"inference":true,"fine_tune":false,"deploy":false}}]}}""";

    private static string PermissionHealthJson(int total, int pageNo, int pageSize) =>
        "{\"success\":true,\"code\":null,\"output\":{\"total\":"
        + total
        + ",\"page_no\":"
        + pageNo
        + ",\"page_size\":"
        + pageSize
        + ",\"permissions\":[{\"model\":\"qwen3.7-plus\",\"permissions\":{\"inference\":true}}]}}";

    public sealed record HealthShapeFixture(
        string Name,
        string Body,
        string ExpectedHealthState,
        bool ParseValid,
        bool SizeLimitExceeded,
        bool Truncated,
        string OutputKind,
        int? PermissionsCount,
        bool ExactlyOneModel,
        bool ExactModelMatch,
        string InferenceKind,
        string? InferenceCategory,
        int? ExpectedTotal,
        int? ExpectedPageNo,
        int? ExpectedPageSize)
    {
        public override string ToString() => Name;
    }

    private static string[] ValidArguments() =>
    [
        "--stage2-r4-qwen",
        "--real-provider",
        "--expected-provider=qwen",
        "--expected-model=qwen3.7-plus",
        "--expected-sha=ec5c727f143d0f22e347621c10585ebe948b1cda",
        "--max-total-requests=3",
        "--max-model-requests=2",
        "--ordinary-max-output-tokens=32",
        "--cancellation-max-output-tokens=256",
        "--total-timeout-seconds=600",
        "--no-automatic-retry",
        "--no-fallback",
        "--no-resend"
    ];

    private static string[] HealthOnlyArguments() =>
    [
        "--stage2-r4-qwen",
        "--real-provider",
        "--health-only",
        "--expected-provider=qwen",
        "--expected-model=qwen3.7-plus",
        "--expected-sha=38feaa9f4a227644b97fccfe98018ab517f01d1f",
        "--max-total-requests=1",
        "--max-model-requests=0",
        "--total-timeout-seconds=600",
        "--no-automatic-retry",
        "--no-fallback",
        "--no-resend"
    ];

    private static string[] Replace(string[] source, string oldValue, string newValue) =>
        source.Select(item => item == oldValue ? newValue : item).ToArray();

    private sealed class TestOwnedDirectory : IDisposable
    {
        private readonly string _marker;

        public TestOwnedDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ScreenGuide.R4.Qwen.Runner.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            _marker = System.IO.Path.Combine(Path, ".screen-guide-r4-test-owned");
            File.WriteAllText(_marker, "owned");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(_marker)
                && System.IO.Path.GetFullPath(Path).StartsWith(
                    System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()),
                    StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    public enum FakeCredentialMode
    {
        Valid,
        Missing,
        StatusUnavailable,
        LeaseUnavailable,
        EmptyLease
    }

    private sealed class FakeCredentialStore(
        string secret,
        FakeCredentialMode mode = FakeCredentialMode.Valid) : IProviderCredentialStore
    {
        public int OpenLeaseCount { get; private set; }

        public int DisposedLeaseCount { get; private set; }

        public Task<ProviderCredentialStatus> GetStatusAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            if (mode == FakeCredentialMode.StatusUnavailable
                && string.Equals(providerId, QwenChatModelProvider.ProviderId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("FAKE_CREDENTIAL_STATUS_UNAVAILABLE");
            }

            return Task.FromResult(new ProviderCredentialStatus(
                providerId,
                mode != FakeCredentialMode.Missing
                && string.Equals(providerId, QwenChatModelProvider.ProviderId, StringComparison.Ordinal)
                    ? ProviderCredentialState.Configured
                    : ProviderCredentialState.Missing,
                null));
        }

        public Task SetAsync(
            string providerId,
            ReadOnlyMemory<char> value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IProviderCredentialLease?> OpenLeaseAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(providerId, QwenChatModelProvider.ProviderId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<IProviderCredentialLease?>(null);
            }

            OpenLeaseCount++;
            if (mode == FakeCredentialMode.LeaseUnavailable)
            {
                throw new InvalidOperationException("FAKE_CREDENTIAL_LEASE_UNAVAILABLE");
            }

            if (mode == FakeCredentialMode.EmptyLease)
            {
                return ValueTask.FromResult<IProviderCredentialLease?>(new FakeLease(
                    string.Empty,
                    () => DisposedLeaseCount++));
            }

            return ValueTask.FromResult<IProviderCredentialLease?>(new FakeLease(
                secret,
                () => DisposedLeaseCount++));
        }

        public Task<bool> DeleteAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private sealed class FakeLease(string secret, Action disposed) : IProviderCredentialLease
        {
            private char[]? _secret = secret.ToCharArray();

            public ReadOnlyMemory<char> Secret => _secret ?? [];

            public void Dispose()
            {
                if (_secret is { } value)
                {
                    Array.Clear(value);
                    _secret = null;
                    disposed();
                }
            }
        }
    }

    private sealed class RejectingTransport : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            throw new InvalidOperationException("HTTP must not run during composition.");
        }
    }

    private sealed class OfflineAcceptanceTransport(string reasoningSentinel) : HttpMessageHandler
    {
        private int _modelRequests;

        public int SendCount { get; private set; }

        public int HealthRequests { get; private set; }

        public int ModelRequests => Volatile.Read(ref _modelRequests);

        public bool OrdinaryPayloadMatched { get; private set; }

        public bool CancellationPayloadMatched { get; private set; }

        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            Assert.Equal("Bearer FAKE_QWEN_SECRET_SENTINEL", request.Headers.Authorization?.ToString());
            if (request.Method == HttpMethod.Get)
            {
                HealthRequests++;
                Assert.Equal(
                    "https://dashscope.aliyuncs.com/api/v1/models/permissions?model=qwen3.7-plus&authorization_scope=AUTHORIZED&action=INFERENCE&page_no=1&page_size=1",
                    request.RequestUri?.AbsoluteUri);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"success":true,"code":null,"message":"","request_id":"FAKE-HEALTH-ID","output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","name":"Qwen 3.7 Plus","permissions":{"inference":true,"fine_tune":false,"deploy":false}}]}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
                request.RequestUri?.AbsoluteUri);
            var modelRequestNumber = Interlocked.Increment(ref _modelRequests);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal("qwen3.7-plus", root.GetProperty("model").GetString());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            Assert.Equal(1, root.GetProperty("n").GetInt32());
            Assert.False(root.GetProperty("enable_thinking").GetBoolean());
            Assert.False(root.GetProperty("preserve_thinking").GetBoolean());
            Assert.Equal("none", root.GetProperty("tool_choice").GetString());
            Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
            Assert.False(root.GetProperty("enable_search").GetBoolean());
            Assert.False(root.GetProperty("enable_code_interpreter").GetBoolean());
            Assert.False(root.TryGetProperty("temperature", out _));
            Assert.False(root.TryGetProperty("top_p", out _));
            Assert.False(root.TryGetProperty("tools", out _));
            Assert.False(root.TryGetProperty("reasoning", out _));
            var lastMessage = root.GetProperty("messages").EnumerateArray().Last();
            Assert.Equal("user", lastMessage.GetProperty("role").GetString());
            if (modelRequestNumber == 1)
            {
                Assert.Equal(32, root.GetProperty("max_completion_tokens").GetInt32());
                Assert.Equal(
                    R4QwenRunnerExecution.OrdinaryCanary,
                    lastMessage.GetProperty("content").GetString());
                OrdinaryPayloadMatched = true;
                return SseResponse($$$"""
                    data: {"id":"FAKE-QWEN-REQUEST-ID","choices":[{"delta":{"reasoning_content":"{{{reasoningSentinel}}}"},"finish_reason":null}]}

                    data: {"id":"FAKE-QWEN-REQUEST-ID","choices":[{"delta":{"content":"QWEN-"},"finish_reason":null}]}

                    data: {"id":"FAKE-QWEN-REQUEST-ID","choices":[{"delta":{"content":"OK"},"finish_reason":"stop"}]}

                    data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}

                    data: [DONE]

                    """);
            }

            Assert.Equal(2, modelRequestNumber);
            Assert.Equal(256, root.GetProperty("max_completion_tokens").GetInt32());
            Assert.Equal(
                R4QwenRunnerExecution.CancellationCanary,
                lastMessage.GetProperty("content").GetString());
            CancellationPayloadMatched = true;
            var stream = new CancellationSseStream(() => CancellationObserved = true);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentType = new("text/event-stream");
            return response;
        }

        private static HttpResponseMessage SseResponse(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class OrdinaryFailureTransport : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"success":true,"code":null,"message":"","request_id":"FAKE-HEALTH-ID","output":{"total":1,"page_no":1,"page_size":1,"permissions":[{"model":"qwen3.7-plus","permissions":{"inference":true}}]}}""",
                        Encoding.UTF8,
                        "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            });
        }
    }

    private sealed class HealthFixtureTransport(string body) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CancellationSseStream(Action cancellationObserved) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes("""
            data: {"choices":[{"delta":{"content":"1\n"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"2\n"},"finish_reason":null}]}

            """);
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _position);
                _prefix.AsSpan(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            throw new NotSupportedException("Synchronous blocking reads are not supported.");
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _position);
                _prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved();
                throw;
            }
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
