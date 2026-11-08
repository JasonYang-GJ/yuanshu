using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class AssistantIslandBridgeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task UnboundPipeRejectsPeerWithoutReturningProjection()
    {
        await using var bridge = new AssistantIslandBridge((_, _) => Task.FromResult(true));
        Assert.Matches("^Yuanshu\\.Assistant\\.[0-9a-f]{32}$", bridge.PipeName);
        Assert.False(bridge.IsVisible);
        var response = await ExchangeRawAsync(bridge.PipeName, null);
        AssertFailure(response, "peer_rejected");
    }

    [Fact]
    public async Task RealPipeRejectsDifferentProcessEvenWhenItRunsAsSameUser()
    {
        // This child only waits for a newline; it owns no UI, microphone, provider or project data.
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"[Console]::ReadLine() | Out-Null\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        try
        {
            await using var bridge = new AssistantIslandBridge((_, _) => throw new InvalidOperationException("must_not_run"));
            bridge.SetPeerProcess(child.Id);
            var response = await ExchangeRawAsync(bridge.PipeName, null);
            AssertFailure(response, "peer_rejected");
            Assert.False(bridge.IsVisible);
            Assert.Throws<InvalidOperationException>(() => bridge.SetPeerProcess(Environment.ProcessId));
        }
        finally
        {
            await child.StandardInput.WriteLineAsync();
            child.StandardInput.Close();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync(); }
        }
    }

    [Fact]
    public async Task BoundProcessReadsPausedSnapshotWithServerOwnedIdentityAndBoundedText()
    {
        await using var bridge = BoundBridge();
        var initial = await ReadSnapshotAsync(bridge);
        Assert.Equal(0, initial.Revision);
        Assert.Equal("paused", initial.Phase);
        Assert.False(initial.MicrophoneOn);
        Assert.Empty(initial.Transcript);
        Assert.Empty(initial.Detail);
        var text = string.Concat(Enumerable.Repeat("中\0\"\\\n😀", 3000));
        var revision = bridge.Publish(initial with
        {
            ProtocolVersion = 99, InstanceId = Guid.NewGuid().ToString(), Revision = long.MaxValue,
            Phase = "recognized", Status = text, Transcript = text, Detail = text, UpdatedAtUtc = "untrusted"
        });
        var response = await ExchangeAsync(bridge, new { protocolVersion = 1, operation = "snapshot" });
        Assert.True(response.GetProperty("ok").GetBoolean());
        var snapshot = Snapshot(response);
        Assert.Equal(1, revision);
        Assert.Equal(1, snapshot.ProtocolVersion);
        Assert.Equal(bridge.InstanceId, snapshot.InstanceId);
        Assert.Equal(revision, snapshot.Revision);
        Assert.InRange(snapshot.Status.Length, 1, 160);
        Assert.InRange(snapshot.Transcript.Length, 1, 2000);
        Assert.InRange(snapshot.Detail.Length, 1, 4000);
        Assert.DoesNotContain('\0', snapshot.Detail);
        Assert.DoesNotContain("untrusted", snapshot.UpdatedAtUtc);
        Assert.True(DateTimeOffset.TryParse(snapshot.UpdatedAtUtc, out _));
        Assert.True(Encoding.UTF8.GetByteCount(response.GetRawText()) <= AssistantIslandBridge.MaximumFrameBytes);
        Assert.Throws<ArgumentException>(() => bridge.Publish(snapshot with { Phase = "executing_shell" }));
        Assert.Throws<ArgumentException>(() => bridge.Publish(snapshot with { ControlToken = "arbitrary" }));
    }

    [Fact]
    public async Task ProjectionRemovesDirectionalOverridesButPreservesOrdinaryUnicodeAndLineBreaks()
    {
        await using var bridge = BoundBridge();
        var snapshot = await ReadSnapshotAsync(bridge);
        var text = "中文\r\n\t👩\u200D💻\u200C\u2060\u061C\u200E\u200F\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069\uFEFF";
        bridge.Publish(snapshot with { Status = text, Transcript = text, Detail = text });
        var actual = await ReadSnapshotAsync(bridge);
        Assert.DoesNotContain('\r', actual.Status);
        Assert.DoesNotContain('\n', actual.Status);
        Assert.DoesNotContain('\t', actual.Status);
        Assert.StartsWith("中文\r\n\t👩\u200D💻\u200C\u2060", actual.Transcript);
        Assert.Equal(actual.Transcript, actual.Detail);
        foreach (var character in "\u061C\u200E\u200F\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069\uFEFF")
            Assert.DoesNotContain(character, actual.Detail);
    }

    [Theory]
    [InlineData("[]", "invalid_request")]
    [InlineData("{", "invalid_json")]
    [InlineData("{\"protocolVersion\":2,\"operation\":\"snapshot\"}", "unsupported_protocol")]
    [InlineData("{\"protocolVersion\":\"1\",\"operation\":\"snapshot\"}", "unsupported_protocol")]
    [InlineData("{\"protocolVersion\":1,\"protocolVersion\":1,\"operation\":\"snapshot\"}", "duplicate_field")]
    [InlineData("{\"protocolVersion\":1,\"operation\":\"snapshot\",\"operation\":\"command\"}", "duplicate_field")]
    [InlineData("{\"protocolVersion\":1,\"operation\":\"snapshot\",\"action\":\"start\"}", "invalid_request")]
    [InlineData("{\"protocolVersion\":1,\"operation\":\"Snapshot\"}", "invalid_operation")]
    [InlineData("{\"protocolVersion\":1,\"operation\":\"execute\"}", "invalid_operation")]
    [InlineData("{\"protocolVersion\":1,\"Operation\":\"snapshot\"}", "invalid_request")]
    [InlineData("{\"protocolVersion\":1,\"operation\":null}", "invalid_request")]
    [InlineData("{\"protocolVersion\":1,\"operation\":\"snapshot\",\"extra\":{\"text\":\"private\"}}", "invalid_request")]
    public async Task StrictWireSchemaRejectsMalformedUntrustedRequests(string request, string error)
    {
        var called = false;
        await using var bridge = BoundBridge((_, _) => { called = true; return Task.FromResult(true); });
        AssertFailure(await ExchangeRawAsync(bridge.PipeName, Encoding.UTF8.GetBytes(request)), error);
        Assert.False(called);
        Assert.False(bridge.IsVisible);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(32769)]
    public async Task InvalidFrameLengthIsRejectedBeforeAllocatingPayload(int length)
    {
        await using var bridge = BoundBridge();
        AssertFailure(await ExchangeRawAsync(bridge.PipeName, Array.Empty<byte>(), length), "invalid_frame");
        Assert.Equal("paused", (await ReadSnapshotAsync(bridge)).Phase);
    }

    [Fact]
    public async Task ReadThenActuallyPresentedHandshakeIsRequiredBeforeCommand()
    {
        var calls = new List<string>();
        await using var bridge = BoundBridge((action, _) => { calls.Add(action); return Task.FromResult(true); });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ready = bridge.WaitForReadyAsync(cancellation.Token);
        AssertFailure(await PresentAsync(bridge, 0, true), "snapshot_required");
        var snapshot = await ReadSnapshotAsync(bridge);
        AssertFailure(await CommandAsync(bridge, snapshot, "start"), "presentation_required");
        Assert.True((await PresentAsync(bridge, 0, false)).GetProperty("ok").GetBoolean());
        Assert.False(ready.IsCompleted);
        AssertFailure(await CommandAsync(bridge, snapshot, "start"), "presentation_required");
        Assert.True((await PresentAsync(bridge, 0, true)).GetProperty("ok").GetBoolean());
        await ready;
        Assert.True(bridge.IsVisible);
        Assert.True((await CommandAsync(bridge, snapshot, "start")).GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { "start" }, calls);
    }

    [Fact]
    public async Task FutureUnreadAndWrongInstanceReceiptsCannotCompleteWait()
    {
        await using var bridge = BoundBridge();
        var snapshot = await ReadSnapshotAsync(bridge);
        await PresentAsync(bridge, snapshot.Revision, true);
        var revision = bridge.Publish(snapshot with { Phase = "recognized", Transcript = "打开我的音乐" });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var presented = bridge.WaitForPresentedAsync(revision, cancellation.Token);
        AssertFailure(await PresentAsync(bridge, revision + 1, true), "invalid_revision");
        AssertFailure(await PresentAsync(bridge, revision, true), "snapshot_required");
        await ReadSnapshotAsync(bridge);
        AssertFailure(await PresentAsync(bridge, revision, true, Guid.NewGuid().ToString()), "instance_mismatch");
        await PresentAsync(bridge, 0, true);
        Assert.False(presented.IsCompleted);
        await PresentAsync(bridge, revision, true);
        await presented;
        Assert.True((await PresentAsync(bridge, 0, true)).GetProperty("ok").GetBoolean());
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = bridge.WaitForPresentedAsync(revision + 1, CancellationToken.None); });
    }

    [Fact]
    public async Task NewerPresentationCannotStandInForUnpresentedRecognizedText()
    {
        await using var bridge = BoundBridge();
        var snapshot = await ReadSnapshotAsync(bridge);
        var recognized = bridge.Publish(snapshot with { Phase = "recognized", Transcript = "第一句" });
        await ReadSnapshotAsync(bridge);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = bridge.WaitForPresentedAsync(recognized, cancellation.Token);
        var newer = bridge.Publish(snapshot with { Phase = "transcribing", Transcript = "后来的另一句" });
        await ReadSnapshotAsync(bridge);
        await PresentAsync(bridge, newer, true);
        Assert.False(waiting.IsCompleted);
        await PresentAsync(bridge, recognized, true);
        await waiting;
    }

    [Fact]
    public async Task ReadingLaterVersionDoesNotAuthorizeAcknowledgingSkippedVersion()
    {
        await using var bridge = BoundBridge();
        var snapshot = await ReadSnapshotAsync(bridge);
        var skipped = bridge.Publish(snapshot with { Phase = "recognized", Transcript = "从未读出的内容" });
        var later = bridge.Publish(snapshot with { Phase = "transcribing", Transcript = "后来才读出的内容" });
        await ReadSnapshotAsync(bridge);
        AssertFailure(await PresentAsync(bridge, skipped, true), "snapshot_required");
        Assert.False(bridge.IsVisible);
        Assert.True((await PresentAsync(bridge, later, true)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task IdenticalNormalizedPublishKeepsExactRevisionAndTimestamp()
    {
        await using var bridge = BoundBridge();
        var snapshot = await ReadSnapshotAsync(bridge);
        var revision = bridge.Publish(snapshot with { Phase = "recognized", Transcript = "明确显示后才提交" });
        var first = await ReadSnapshotAsync(bridge);
        Assert.Equal(revision, bridge.Publish(first with
        {
            ProtocolVersion = 500, InstanceId = "ignored", Revision = long.MaxValue, UpdatedAtUtc = "ignored"
        }));
        var second = await ReadSnapshotAsync(bridge);
        Assert.Equal(first, second);
        await PresentAsync(bridge, revision, true);
        await bridge.WaitForPresentedAsync(revision, CancellationToken.None);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("pause")]
    [InlineData("stop")]
    [InlineData("management")]
    [InlineData("memory")]
    [InlineData("exit")]
    public async Task OnlyAllowlistedControlsWithCurrentTokenReachOwner(string action)
    {
        var calls = new List<(string Action, string Token)>();
        await using var bridge = BoundBridge((name, token) => { calls.Add((name, token)); return Task.FromResult(true); });
        var old = await ReadSnapshotAsync(bridge);
        await PresentAsync(bridge, 0, true);
        var fresh = old with { ControlToken = Guid.NewGuid().ToString() };
        bridge.Publish(fresh);
        AssertFailure(await CommandAsync(bridge, old, action), "stale_control");
        AssertFailure(await CommandAsync(bridge, fresh with { InstanceId = Guid.NewGuid().ToString() }, action), "instance_mismatch");
        AssertFailure(await CommandAsync(bridge, fresh, "confirm_file"), "invalid_action");
        Assert.True((await CommandAsync(bridge, fresh, action)).GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { (action, fresh.ControlToken) }, calls);
    }

    [Fact]
    public async Task OwnerCanRejectStaleQueuedControlWithoutRepeatingIt()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? currentToken = null;
        var calls = 0;
        await using var bridge = BoundBridge(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult(true);
            await release.Task;
            return token == currentToken;
        });
        var snapshot = await ReadSnapshotAsync(bridge);
        currentToken = snapshot.ControlToken;
        await PresentAsync(bridge, 0, true);
        var pending = CommandAsync(bridge, snapshot, "start");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        currentToken = Guid.NewGuid().ToString();
        bridge.Publish(snapshot with { ControlToken = currentToken });
        release.TrySetResult(true);
        AssertFailure(await pending, "command_rejected");
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task VisibilityTimeoutAndHiddenReceiptRevokeReadyOnceAndForbidStarting()
    {
        var lost = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lostCount = 0;
        await using var bridge = BoundBridge();
        bridge.VisibilityLost += () => { Interlocked.Increment(ref lostCount); lost.TrySetResult(true); };
        var snapshot = await ReadSnapshotAsync(bridge);
        await PresentAsync(bridge, 0, true);
        await bridge.WaitForReadyAsync(CancellationToken.None);
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.False(bridge.IsVisible);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var readyAgain = bridge.WaitForReadyAsync(cancellation.Token);
        Assert.False(readyAgain.IsCompleted);
        AssertFailure(await CommandAsync(bridge, snapshot, "start"), "island_not_visible");
        Assert.True((await CommandAsync(bridge, snapshot, "pause")).GetProperty("ok").GetBoolean());
        await PresentAsync(bridge, 0, false);
        Assert.Equal(1, lostCount);
        await PresentAsync(bridge, 0, true);
        await readyAgain;
        Assert.True(bridge.IsVisible);
        await PresentAsync(bridge, 0, false);
        Assert.False(bridge.IsVisible);
        Assert.Equal(2, lostCount);
        await PresentAsync(bridge, 0, false);
        Assert.Equal(2, lostCount);
    }

    [Fact]
    public async Task HeartbeatCannotReplaceTheInitialReadAndPresentedHandshake()
    {
        var calls = 0;
        await using var bridge = BoundBridge((_, _) => { calls++; return Task.FromResult(true); });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ready = bridge.WaitForReadyAsync(cancellation.Token);

        AssertFailure(await HeartbeatAsync(bridge, true), "presentation_required");
        Assert.False(bridge.IsVisible);
        Assert.False(ready.IsCompleted);
        AssertFailure(await PresentAsync(bridge, 0, true), "snapshot_required");

        var snapshot = await ReadSnapshotAsync(bridge);
        AssertFailure(await HeartbeatAsync(bridge, true), "presentation_required");
        AssertFailure(await HeartbeatAsync(bridge, false), "presentation_required");
        Assert.False(bridge.IsVisible);
        Assert.False(ready.IsCompleted);
        AssertFailure(await CommandAsync(bridge, snapshot, "start"), "presentation_required");
        Assert.Equal(0, calls);

        Assert.True((await PresentAsync(bridge, snapshot.Revision, true)).GetProperty("ok").GetBoolean());
        await ready;
        Assert.True(bridge.IsVisible);
    }

    [Theory]
    [InlineData("{\"protocolVersion\":1,\"operation\":\"heartbeat\",\"instanceId\":\"INSTANCE\",\"visible\":\"false\"}", "invalid_request")]
    [InlineData("{\"protocolVersion\":1,\"operation\":\"heartbeat\",\"instanceId\":\"INSTANCE\",\"visible\":null}", "invalid_request")]
    [InlineData("{\"protocolVersion\":1,\"operation\":\"heartbeat\",\"instanceId\":\"INSTANCE\"}", "invalid_request")]
    [InlineData("{\"protocolVersion\":1,\"operation\":\"heartbeat\",\"instanceId\":null,\"visible\":false}", "invalid_request")]
    [InlineData("{\"protocolVersion\":1,\"operation\":\"heartbeat\",\"instanceId\":\"INSTANCE\",\"visible\":false,\"revision\":0}", "invalid_request")]
    [InlineData("{\"protocolVersion\":1,\"operation\":\"heartbeat\",\"instanceId\":\"INSTANCE\",\"visible\":true,\"visible\":false}", "duplicate_field")]
    public async Task MalformedHeartbeatCannotRevokeVisibilityOrReadNewProjection(string request, string error)
    {
        await using var bridge = BoundBridge();
        var snapshot = await ReadSnapshotAsync(bridge);
        await PresentAsync(bridge, snapshot.Revision, true);
        var revision = bridge.Publish(snapshot with { Phase = "recognized", Transcript = "尚未读取的新内容" });
        var wire = request.Replace("INSTANCE", bridge.InstanceId, StringComparison.Ordinal);

        AssertFailure(await ExchangeRawAsync(bridge.PipeName, Encoding.UTF8.GetBytes(wire)), error);
        Assert.True(bridge.IsVisible);
        Assert.True(bridge.WaitForReadyAsync(CancellationToken.None).IsCompletedSuccessfully);
        AssertFailure(await PresentAsync(bridge, revision, true), "snapshot_required");
    }

    [Fact]
    public async Task WrongInstanceHeartbeatCannotHideTheBoundIslandOrReadNewProjection()
    {
        var lost = 0;
        await using var bridge = BoundBridge();
        bridge.VisibilityLost += () => Interlocked.Increment(ref lost);
        var snapshot = await ReadSnapshotAsync(bridge);
        await PresentAsync(bridge, snapshot.Revision, true);
        var revision = bridge.Publish(snapshot with { Phase = "recognized", Transcript = "本实例的新内容" });

        AssertFailure(await HeartbeatAsync(bridge, false, Guid.NewGuid().ToString("D")), "instance_mismatch");
        Assert.True(bridge.IsVisible);
        Assert.Equal(0, Volatile.Read(ref lost));
        Assert.True(bridge.WaitForReadyAsync(CancellationToken.None).IsCompletedSuccessfully);
        AssertFailure(await PresentAsync(bridge, revision, true), "snapshot_required");
    }

    [Fact]
    public async Task HeartbeatsKeepVisibilityAliveWithoutPresentingNewRevisionAndExpireWhenStopped()
    {
        var lost = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lostCount = 0;
        await using var bridge = BoundBridge();
        bridge.VisibilityLost += () => { Interlocked.Increment(ref lostCount); lost.TrySetResult(true); };
        var snapshot = await ReadSnapshotAsync(bridge);
        await PresentAsync(bridge, snapshot.Revision, true);
        await bridge.WaitForReadyAsync(CancellationToken.None);
        var revision = bridge.Publish(snapshot with { Phase = "recognized", Transcript = "必须实际呈现后才能继续" });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presented = bridge.WaitForPresentedAsync(revision, cancellation.Token);
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(3400))
        {
            var response = await HeartbeatAsync(bridge, true);
            Assert.True(response.GetProperty("ok").GetBoolean());
            Assert.Equal(JsonValueKind.Null, response.GetProperty("snapshot").ValueKind);
            Assert.True(bridge.IsVisible);
            Assert.False(presented.IsCompleted);
            await Task.Delay(200);
        }

        Assert.Equal(0, Volatile.Read(ref lostCount));
        Assert.True(bridge.IsVisible);
        Assert.True(bridge.WaitForReadyAsync(cancellation.Token).IsCompletedSuccessfully);
        AssertFailure(await PresentAsync(bridge, revision, true), "snapshot_required");
        Assert.Equal(revision, (await ReadSnapshotAsync(bridge)).Revision);
        Assert.True((await HeartbeatAsync(bridge, true)).GetProperty("ok").GetBoolean());
        Assert.False(presented.IsCompleted);

        // Once heartbeats stop, the existing three-second visibility lease still expires.
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.False(bridge.IsVisible);
        Assert.Equal(1, Volatile.Read(ref lostCount));
        var readyAgain = bridge.WaitForReadyAsync(cancellation.Token);
        var oldPresentation = bridge.WaitForPresentedAsync(snapshot.Revision, cancellation.Token);
        Assert.False(readyAgain.IsCompleted);
        Assert.False(oldPresentation.IsCompleted);
        Assert.False(presented.IsCompleted);
        AssertFailure(await CommandAsync(bridge, snapshot, "start"), "island_not_visible");

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readyAgain);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldPresentation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => presented);
    }

    [Fact]
    public async Task HiddenHeartbeatRevokesReadinessAndPresentationUntilAnotherPresentedReceipt()
    {
        var lost = 0;
        await using var bridge = BoundBridge();
        bridge.VisibilityLost += () => Interlocked.Increment(ref lost);
        var snapshot = await ReadSnapshotAsync(bridge);
        await PresentAsync(bridge, snapshot.Revision, true);
        await bridge.WaitForReadyAsync(CancellationToken.None);
        await bridge.WaitForPresentedAsync(snapshot.Revision, CancellationToken.None);

        var response = await HeartbeatAsync(bridge, false);
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("snapshot").ValueKind);
        Assert.False(bridge.IsVisible);
        Assert.Equal(1, Volatile.Read(ref lost));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var readyAgain = bridge.WaitForReadyAsync(cancellation.Token);
        var presentedAgain = bridge.WaitForPresentedAsync(snapshot.Revision, cancellation.Token);
        Assert.False(readyAgain.IsCompleted);
        Assert.False(presentedAgain.IsCompleted);
        AssertFailure(await CommandAsync(bridge, snapshot, "start"), "island_not_visible");

        Assert.True((await HeartbeatAsync(bridge, false)).GetProperty("ok").GetBoolean());
        Assert.Equal(1, Volatile.Read(ref lost));
        AssertFailure(await HeartbeatAsync(bridge, true), "presentation_required");
        Assert.False(bridge.IsVisible);
        Assert.False(readyAgain.IsCompleted);
        Assert.False(presentedAgain.IsCompleted);
        AssertFailure(await CommandAsync(bridge, snapshot, "start"), "island_not_visible");

        Assert.True((await PresentAsync(bridge, snapshot.Revision, true)).GetProperty("ok").GetBoolean());
        await readyAgain;
        await presentedAgain;
        Assert.Equal(1, Volatile.Read(ref lost));
    }

    [Fact]
    public async Task CancelledPresentationWaitDoesNotExecuteOrPoisonLaterReceipts()
    {
        var calls = 0;
        await using var bridge = BoundBridge((_, _) => { calls++; return Task.FromResult(true); });
        var snapshot = await ReadSnapshotAsync(bridge);
        var revision = bridge.Publish(snapshot with { Phase = "recognized", Transcript = "只做转写测试" });
        await ReadSnapshotAsync(bridge);
        using var cancellation = new CancellationTokenSource();
        var pending = bridge.WaitForPresentedAsync(revision, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await PresentAsync(bridge, revision, true);
        await bridge.WaitForPresentedAsync(revision, CancellationToken.None);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExceptionDetailsNeverCrossWireAndDisposalCancelsWaiters()
    {
        var bridge = BoundBridge((_, _) => throw new InvalidOperationException("private transcript C:\\secret\\path"));
        var snapshot = await ReadSnapshotAsync(bridge);
        await PresentAsync(bridge, 0, true);
        var response = await CommandAsync(bridge, snapshot, "management");
        AssertFailure(response, "command_failed");
        Assert.DoesNotContain("private", response.GetRawText());
        Assert.DoesNotContain("secret", response.GetRawText());
        var revision = bridge.Publish(snapshot with { Phase = "recognized" });
        var pending = bridge.WaitForPresentedAsync(revision, CancellationToken.None);
        await bridge.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(bridge.IsVisible);
        Assert.Throws<ObjectDisposedException>(() => bridge.Publish(snapshot));
    }

    private static AssistantIslandBridge BoundBridge(Func<string, string, Task<bool>>? callback = null)
    {
        var bridge = new AssistantIslandBridge(callback ?? ((_, _) => Task.FromResult(true)));
        bridge.SetPeerProcess(Environment.ProcessId);
        return bridge;
    }

    private static async Task<AssistantIslandSnapshot> ReadSnapshotAsync(AssistantIslandBridge bridge) =>
        Snapshot(await ExchangeAsync(bridge, new { protocolVersion = 1, operation = "snapshot" }));

    private static AssistantIslandSnapshot Snapshot(JsonElement response) =>
        response.GetProperty("snapshot").Deserialize<AssistantIslandSnapshot>(JsonOptions)!;

    private static Task<JsonElement> PresentAsync(AssistantIslandBridge bridge, long revision, bool visible, string? instanceId = null) =>
        ExchangeAsync(bridge, new { protocolVersion = 1, operation = "presented", instanceId = instanceId ?? bridge.InstanceId, revision, visible });

    private static Task<JsonElement> HeartbeatAsync(AssistantIslandBridge bridge, bool visible, string? instanceId = null) =>
        ExchangeAsync(bridge, new { protocolVersion = 1, operation = "heartbeat", instanceId = instanceId ?? bridge.InstanceId, visible });

    private static Task<JsonElement> CommandAsync(AssistantIslandBridge bridge, AssistantIslandSnapshot snapshot, string action) =>
        ExchangeAsync(bridge, new { protocolVersion = 1, operation = "command", instanceId = snapshot.InstanceId, controlToken = snapshot.ControlToken, action });

    private static Task<JsonElement> ExchangeAsync(AssistantIslandBridge bridge, object request) =>
        ExchangeRawAsync(bridge.PipeName, JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions));

    private static async Task<JsonElement> ExchangeRawAsync(string pipeName, byte[]? request, int? declaredLength = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        var header = new byte[4];
        if (request is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength ?? request.Length);
            await pipe.WriteAsync(header, timeout.Token);
            if (request.Length > 0) await pipe.WriteAsync(request, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }
        await pipe.ReadExactlyAsync(header, timeout.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(length, 1, AssistantIslandBridge.MaximumFrameBytes);
        var bytes = new byte[length];
        await pipe.ReadExactlyAsync(bytes, timeout.Token);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static void AssertFailure(JsonElement response, string error)
    {
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal(error, response.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("snapshot").ValueKind);
    }
}
