using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ScreenGuide.DesktopClient.Services;

public sealed record AssistantIslandSnapshot(
    int ProtocolVersion,
    string InstanceId,
    long Revision,
    string ControlToken,
    string Phase,
    string Status,
    string Transcript,
    string Detail,
    bool MicrophoneOn,
    bool CanStart,
    bool CanPause,
    bool CanStop,
    bool NeedsAttention,
    string UpdatedAtUtc);

/// <summary>
/// Ephemeral UI projection for the one Island process launched by this Client.
/// It owns neither microphone authorization nor Session/Turn state.
/// </summary>
public sealed class AssistantIslandBridge : IAsyncDisposable
{
    internal const int MaximumFrameBytes = 32768;
    private const int ListenerCount = 4;
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    {
        "idle", "listening", "transcribing", "recognized", "working", "speaking", "waiting",
        "success", "error", "standby", "paused", "stopping"
    };
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "start", "pause", "stop", "management", "memory", "exit"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly object _sync = new();
    private readonly Func<string, string, Task<bool>> _onCommand;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<PresentationWaiter> _presentationWaiters = new();
    private readonly HashSet<long> _readRevisions = new();
    private readonly Queue<long> _readRevisionOrder = new();
    private readonly HashSet<long> _presentedRevisions = new();
    private readonly Queue<long> _presentedRevisionOrder = new();
    private readonly Task[] _backgroundTasks;
    private TaskCompletionSource<bool> _ready = NewCompletion();
    private AssistantIslandSnapshot _snapshot;
    private Process? _peer;
    private int _peerProcessId;
    private long _lastVisibleTimestamp;
    private bool _handshakeComplete;
    private bool _visible;
    private bool _disposed;

    public AssistantIslandBridge(Func<string, string, Task<bool>> onCommand)
    {
        _onCommand = onCommand ?? throw new ArgumentNullException(nameof(onCommand));
        PipeName = "Yuanshu.Assistant." + Guid.NewGuid().ToString("N");
        InstanceId = Guid.NewGuid().ToString("D");
        _snapshot = new(1, InstanceId, 0, Guid.NewGuid().ToString("D"), "paused", "语音已暂停",
            string.Empty, string.Empty, false, true, false, false, false, DateTimeOffset.UtcNow.ToString("O"));
        _backgroundTasks = Enumerable.Range(0, ListenerCount).Select(_ => ListenAsync()).Append(WatchVisibilityAsync()).ToArray();
    }

    public string PipeName { get; }
    public string InstanceId { get; }
    public event Action? VisibilityLost;

    public bool IsVisible
    {
        get
        {
            ExpireVisibility();
            lock (_sync) return _visible && !_disposed;
        }
    }

    /// <summary>Bind exactly once, before accepting any peer data. Hold its OS handle against PID reuse.</summary>
    public void SetPeerProcess(int processId)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_peerProcessId == processId) return;
            if (_peerProcessId != 0) throw new InvalidOperationException("assistant_peer_already_bound");
            var process = Process.GetProcessById(processId);
            try
            {
                _ = process.SafeHandle;
                if (process.HasExited) throw new InvalidOperationException("assistant_peer_exited");
                _peer = process;
                _peerProcessId = processId;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
    }

    /// <summary>Publish bounded in-memory text; the server supplies instance, revision and timestamp.</summary>
    public long Publish(AssistantIslandSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Phases.Contains(snapshot.Phase)) throw new ArgumentException("invalid_phase", nameof(snapshot));
        if (!Guid.TryParseExact(snapshot.ControlToken, "D", out var controlToken))
            throw new ArgumentException("invalid_control_token", nameof(snapshot));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var normalized = snapshot with
            {
                ProtocolVersion = 1,
                InstanceId = InstanceId,
                Revision = _snapshot.Revision,
                ControlToken = controlToken.ToString("D"),
                Status = BoundText(snapshot.Status, 160, singleLine: true),
                Transcript = BoundText(snapshot.Transcript, 2000),
                Detail = BoundText(snapshot.Detail, 4000),
                UpdatedAtUtc = _snapshot.UpdatedAtUtc
            };
            if (normalized == _snapshot) return _snapshot.Revision;
            _snapshot = normalized with
            {
                Revision = checked(_snapshot.Revision + 1),
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            return _snapshot.Revision;
        }
    }

    public Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        ExpireVisibility();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _ready.Task.WaitAsync(cancellationToken);
        }
    }

    public Task WaitForPresentedAsync(long revision, CancellationToken cancellationToken)
    {
        ExpireVisibility();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (revision < 0 || revision > _snapshot.Revision) throw new ArgumentOutOfRangeException(nameof(revision));
            if (_visible && _presentedRevisions.Contains(revision)) return Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (_presentationWaiters.Count >= 128) throw new InvalidOperationException("too_many_presentation_waiters");
            var waiter = new PresentationWaiter(revision, NewCompletion());
            _presentationWaiters.Add(waiter);
            return AwaitPresentationAsync(waiter, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _visible = false;
            _snapshot = _snapshot with { Transcript = string.Empty, Detail = string.Empty, MicrophoneOn = false };
            _ready.TrySetCanceled();
            foreach (var waiter in _presentationWaiters) waiter.Completion.TrySetCanceled();
            _presentationWaiters.Clear();
        }
        _stopping.Cancel();
        await Task.WhenAll(_backgroundTasks).ConfigureAwait(false);
        _peer?.Dispose();
        _stopping.Dispose();
    }

    private async Task AwaitPresentationAsync(PresentationWaiter waiter, CancellationToken cancellationToken)
    {
        try { await waiter.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { lock (_sync) _presentationWaiters.Remove(waiter); }
    }

    private async Task ListenAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, ListenerCount,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    MaximumFrameBytes, MaximumFrameBytes);
                await pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                requestTimeout.CancelAfter(RequestTimeout);
                var cancellationToken = requestTimeout.Token;
                if (!IsExpectedPeer(pipe))
                {
                    await WriteAsync(pipe, Failure("peer_rejected"), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                IslandResponse response;
                try
                {
                    var request = await ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                    response = await HandleAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException) { response = Failure("invalid_frame"); }
                catch (JsonException) { response = Failure("invalid_json"); }
                await WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // Transport failures contain no captured text and do not grant readiness or authority.
                if (!_stopping.IsCancellationRequested)
                {
                    try { await Task.Delay(50, _stopping.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
        }
    }

    private bool IsExpectedPeer(NamedPipeServerStream pipe)
    {
        lock (_sync)
        {
            if (_disposed || _peerProcessId == 0 || !PeerIsAlive()) return false;
            return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId)
                && processId == (uint)_peerProcessId;
        }
    }

    private bool PeerIsAlive()
    {
        try { return _peer is not null && !_peer.HasExited; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private async Task<IslandResponse> HandleAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Failure("invalid_request");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!fields.Add(property.Name)) return Failure("duplicate_field");
        if (!ReadInteger(root, "protocolVersion", out var protocolVersion) || protocolVersion != 1)
            return Failure("unsupported_protocol");
        if (!ReadString(root, "operation", out var operation)) return Failure("invalid_request");
        ExpireVisibility();
        switch (operation)
        {
            case "snapshot":
                if (!HasExactly(fields, "protocolVersion", "operation")) return Failure("invalid_request");
                lock (_sync) return SuccessfulSnapshot();
            case "heartbeat":
            {
                if (!HasExactly(fields, "protocolVersion", "operation", "instanceId", "visible")
                    || !ReadString(root, "instanceId", out var heartbeatInstance)
                    || !root.TryGetProperty("visible", out var heartbeatVisible)
                    || heartbeatVisible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Failure("invalid_request");
                bool heartbeatLost;
                lock (_sync)
                {
                    if (_disposed) return Failure("bridge_closed");
                    if (!StringComparer.Ordinal.Equals(heartbeatInstance, InstanceId)) return Failure("instance_mismatch");
                    // A heartbeat may maintain an already presented, visible
                    // surface, but cannot establish or restore presentation.
                    if (!_handshakeComplete || heartbeatVisible.GetBoolean() && !_visible)
                        return Failure("presentation_required");
                    heartbeatLost = SetVisible(heartbeatVisible.GetBoolean());
                    if (_visible) _lastVisibleTimestamp = Stopwatch.GetTimestamp();
                }
                if (heartbeatLost) NotifyVisibilityLost();
                // Do not return/register a snapshot, a presented revision, or Ready.
                return new(true, null, null);
            }
            case "presented":
                if (!HasExactly(fields, "protocolVersion", "operation", "instanceId", "revision", "visible")
                    || !ReadString(root, "instanceId", out var instanceId)
                    || !ReadInteger(root, "revision", out var revision)
                    || !root.TryGetProperty("visible", out var visibleValue)
                    || visibleValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Failure("invalid_request");
                bool notifyLost;
                IslandResponse response;
                lock (_sync)
                {
                    if (_disposed) return Failure("bridge_closed");
                    if (!StringComparer.Ordinal.Equals(instanceId, InstanceId)) return Failure("instance_mismatch");
                    if (revision < 0 || revision > _snapshot.Revision) return Failure("invalid_revision");
                    if (!_readRevisions.Contains(revision)) return Failure("snapshot_required");
                    notifyLost = SetVisible(visibleValue.GetBoolean());
                    if (_visible)
                    {
                        _handshakeComplete = true;
                        _lastVisibleTimestamp = Stopwatch.GetTimestamp();
                        RememberRevision(revision, _presentedRevisions, _presentedRevisionOrder);
                        _ready.TrySetResult(true);
                        foreach (var waiter in _presentationWaiters.Where(waiter => waiter.Revision == revision).ToArray())
                        {
                            waiter.Completion.TrySetResult(true);
                            _presentationWaiters.Remove(waiter);
                        }
                    }
                    response = SuccessfulSnapshot();
                }
                if (notifyLost) NotifyVisibilityLost();
                return response;
            case "command":
                if (!HasExactly(fields, "protocolVersion", "operation", "instanceId", "controlToken", "action")
                    || !ReadString(root, "instanceId", out instanceId)
                    || !ReadString(root, "controlToken", out var controlToken)
                    || !ReadString(root, "action", out var action)) return Failure("invalid_request");
                if (!Actions.Contains(action)) return Failure("invalid_action");
                lock (_sync)
                {
                    if (_disposed) return Failure("bridge_closed");
                    if (!StringComparer.Ordinal.Equals(instanceId, InstanceId)) return Failure("instance_mismatch");
                    if (!StringComparer.Ordinal.Equals(controlToken, _snapshot.ControlToken)) return Failure("stale_control");
                    if (!_handshakeComplete) return Failure("presentation_required");
                    if (action == "start" && !_visible) return Failure("island_not_visible");
                }
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // The UI must recheck this token when dequeuing the command. It remains the authority.
                    var invocation = _onCommand(action, controlToken);
                    _ = invocation.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    if (!await invocation.WaitAsync(cancellationToken).ConfigureAwait(false)) return Failure("command_rejected");
                    lock (_sync) return SuccessfulSnapshot();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { return Failure("command_failed"); }
            default:
                return Failure("invalid_operation");
        }
    }

    private IslandResponse SuccessfulSnapshot()
    {
        if (_disposed) return Failure("bridge_closed");
        RememberRevision(_snapshot.Revision, _readRevisions, _readRevisionOrder);
        return new(true, null, _snapshot);
    }

    private bool SetVisible(bool visible)
    {
        if (_visible == visible) return false;
        _visible = visible;
        if (visible) return false;
        _presentedRevisions.Clear();
        _presentedRevisionOrder.Clear();
        _ready = NewCompletion();
        return true;
    }

    private async Task WatchVisibilityAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false)) ExpireVisibility();
        }
        catch (OperationCanceledException) { }
    }

    private void ExpireVisibility()
    {
        bool notifyLost;
        lock (_sync)
        {
            notifyLost = !_disposed && _visible
                && (Stopwatch.GetElapsedTime(_lastVisibleTimestamp) >= VisibilityTimeout || !PeerIsAlive())
                && SetVisible(false);
        }
        if (notifyLost) NotifyVisibilityLost();
    }

    private void NotifyVisibilityLost()
    {
        foreach (Action handler in VisibilityLost?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { handler(); }
            catch (Exception) { /* A presentation event must not kill the transport or leak private exception data. */ }
        }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumFrameBytes) throw new InvalidDataException();
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteAsync(Stream stream, IslandResponse response, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        if (payload.Length > MaximumFrameBytes) payload = JsonSerializer.SerializeToUtf8Bytes(Failure("invalid_frame"), JsonOptions);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BoundText(string? value, int limit, bool singleLine = false)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var count = Math.Min(value.Length, limit);
        if (char.IsHighSurrogate(value[count - 1])) count--;
        var bounded = new StringBuilder(count);
        for (var index = 0; index < count; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < count && char.IsLowSurrogate(value[index + 1]))
                { bounded.Append(character).Append(value[++index]); }
                else bounded.Append('\uFFFD');
            }
            else if (char.IsLowSurrogate(character)) bounded.Append('\uFFFD');
            else
            {
                var directionalControl = character is '\u061C' or '\u200E' or '\u200F'
                    or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\uFEFF';
                var excludedControl = char.IsControl(character)
                    && (singleLine || character is not ('\r' or '\n' or '\t'));
                bounded.Append(directionalControl || excludedControl ? ' ' : character);
            }
        }
        return bounded.ToString();
    }

    private static bool ReadString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()!;
        return true;
    }

    private static bool ReadInteger(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static bool HasExactly(HashSet<string> fields, params string[] names) => fields.SetEquals(names);
    private static void RememberRevision(long revision, HashSet<long> revisions, Queue<long> order)
    {
        if (!revisions.Add(revision)) return;
        order.Enqueue(revision);
        if (order.Count > 128) revisions.Remove(order.Dequeue());
    }
    private static TaskCompletionSource<bool> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IslandResponse Failure(string error) => new(false, error, null);
    private sealed record IslandResponse(bool Ok, string? Error, AssistantIslandSnapshot? Snapshot);
    private sealed record PresentationWaiter(long Revision, TaskCompletionSource<bool> Completion);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
