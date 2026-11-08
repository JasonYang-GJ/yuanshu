using System.IO;
using ScreenGuide.Voice.Windows;

namespace ScreenGuide.DesktopClient.Services;

public enum VoiceSessionState { Off, Starting, Listening, HearingSpeech, Stopping, Faulted, CleanupFailed }

public sealed record VoiceSessionStatus(VoiceSessionState State, string Message)
{
    public bool CanStart => State is VoiceSessionState.Off or VoiceSessionState.Faulted;
    public bool CanStop => State is VoiceSessionState.Starting or VoiceSessionState.Listening or VoiceSessionState.HearingSpeech;
}

/// <summary>Owns this run's explicit microphone consent and preserves lifecycle state across UI refreshes.</summary>
public sealed class VoiceSessionController : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IContinuousVoiceListener _voice;
    private readonly TimeSpan _startupTimeout;
    private Attempt? _attempt;
    private Task? _stopTask;
    private bool _disposed;
    private long _generation;
    private long _stopVersion;
    private VoiceSessionStatus _status = new(VoiceSessionState.Off, "语音未开启");

    public VoiceSessionController(IContinuousVoiceListener voice, TimeSpan? startupTimeout = null)
    {
        _voice = voice;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30);
        _voice.StateChanged += OnVoiceStateChanged;
    }

    public event EventHandler<VoiceSessionStatus>? StateChanged;
    public VoiceSessionStatus Status { get { lock (_sync) return _status; } }
    // A stop also revokes a launch still waiting for its visible surface/initialization.
    public long StopVersion { get { lock (_sync) return _stopVersion; } }
    public bool IsListening => Status.State is VoiceSessionState.Listening or VoiceSessionState.HearingSpeech;
    public long? ListeningGeneration
    {
        get
        {
            lock (_sync) return _attempt is not null && !_attempt.StopRequested
                && !_attempt.Cancellation.IsCancellationRequested && IsListening ? _generation : null;
        }
    }

    public Task StartAsync(CancellationToken lifetime = default)
    {
        Attempt attempt;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_status.State == VoiceSessionState.CleanupFailed) return Task.CompletedTask;
            if (_stopTask is { IsCompleted: false }) return _stopTask;
            if (_attempt is not null) return _attempt.Completion.Task;
            attempt = new Attempt(CancellationTokenSource.CreateLinkedTokenSource(lifetime));
            _attempt = attempt;
            _generation++;
            _status = new(VoiceSessionState.Starting, "正在启动语音…");
        }
        Notify();
        _ = RunStartAsync(attempt);
        return attempt.Completion.Task;
    }

    private async Task RunStartAsync(Attempt attempt)
    {
        Task? starting = null;
        string? error = null;
        try
        {
            starting = Task.Run(() => _voice.StartAsync(attempt.Cancellation.Token));
            await starting.WaitAsync(_startupTimeout, attempt.Cancellation.Token).ConfigureAwait(false);
            attempt.Cancellation.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (ReferenceEquals(_attempt, attempt) && !attempt.StopRequested)
                    _status = new(VoiceSessionState.Listening, "正在聆听");
            }
            Notify();
        }
        catch (TimeoutException)
        {
            error = "语音启动超时，请检查麦克风后重试。";
            attempt.Cancellation.Cancel();
        }
        catch (OperationCanceledException) when (attempt.Cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            error = exception switch
            {
                FileNotFoundException => "本地语音模型不完整，请检查语音模型后重试。",
                UnauthorizedAccessException => "无法使用麦克风，请检查 Windows 麦克风权限后重试。",
                DllNotFoundException or BadImageFormatException => "本地语音组件无法加载，请修复安装后重试。",
                _ => "语音未能启动，请检查麦克风和本地语音模型后重试。"
            };
        }
        finally
        {
            if (error is not null || attempt.Cancellation.IsCancellationRequested)
            {
                attempt.Cancellation.Cancel();
                var released = false;
                lock (_sync)
                {
                    if (ReferenceEquals(_attempt, attempt))
                        _status = new(VoiceSessionState.Stopping,
                            error is null ? "正在停止语音…" : "语音启动未完成，正在取消；你仍可打字使用。");
                }
                Notify();
                // Native model loading cannot be interrupted. Revoke consent immediately and
                // wait off the UI thread; a late completion is always stopped before any retry.
                if (starting is not null)
                {
                    try { await starting.ConfigureAwait(false); } catch { }
                }
                var cleanupFailed = false;
                try { await Task.Run(() => _voice.StopAsync()).ConfigureAwait(false); }
                catch
                {
                    cleanupFailed = attempt.CleanupFailed = true;
                    error = "语音未能完全停止，请关闭元枢后重试。";
                }
                lock (_sync)
                {
                    if (ReferenceEquals(_attempt, attempt) && !attempt.StopRequested)
                    {
                        _attempt = null;
                        released = true;
                        _status = cleanupFailed
                            ? new(VoiceSessionState.CleanupFailed, error!)
                            : error is null
                            ? new(VoiceSessionState.Off, "语音已停止")
                            : new(VoiceSessionState.Faulted, error);
                    }
                }
                if (released) attempt.Cancellation.Dispose();
                Notify();
            }
            attempt.Completion.TrySetResult();
        }
    }

    public Task StopAsync() => StopAsync(null);

    private Task StopAsync(string? failure)
    {
        Attempt? attempt;
        TaskCompletionSource completion;
        lock (_sync)
        {
            _stopVersion++;
            if (_stopTask is { IsCompleted: false }) return _stopTask;
            attempt = _attempt;
            if (attempt is null) return Task.CompletedTask;
            attempt.StopRequested = true;
            _status = new(VoiceSessionState.Stopping, "正在停止语音…");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }
        attempt.Cancellation.Cancel();
        Notify();
        _ = FinishStopAsync(attempt, completion, failure);
        return completion.Task;
    }

    private async Task FinishStopAsync(Attempt attempt, TaskCompletionSource completion, string? failure)
    {
        await attempt.Completion.Task.ConfigureAwait(false);
        var cleanupFailed = attempt.CleanupFailed;
        if (cleanupFailed) failure = "语音未能完全停止，请关闭元枢后重试。";
        try { await Task.Run(() => _voice.StopAsync()).ConfigureAwait(false); }
        catch { cleanupFailed = true; failure = "语音未能完全停止，请关闭元枢后重试。"; }
        lock (_sync)
        {
            if (ReferenceEquals(_attempt, attempt))
            {
                _attempt = null;
                _status = cleanupFailed
                    ? new(VoiceSessionState.CleanupFailed, failure!)
                    : failure is null
                    ? new(VoiceSessionState.Off, "语音已停止")
                    : new(VoiceSessionState.Faulted, failure);
            }
        }
        attempt.Cancellation.Dispose();
        completion.TrySetResult();
        Notify();
    }

    private void OnVoiceStateChanged(object? sender, ContinuousVoiceStatus status)
    {
        lock (_sync)
        {
            if (_attempt is null || _attempt.StopRequested || _attempt.Cancellation.IsCancellationRequested) return;
            if (status.State is ContinuousVoiceState.Listening or ContinuousVoiceState.HearingSpeech)
                _status = new(status.State == ContinuousVoiceState.Listening
                    ? VoiceSessionState.Listening : VoiceSessionState.HearingSpeech, status.Message);
        }
        if (status.State == ContinuousVoiceState.Faulted) _ = StopAsync(status.Message);
        else if (status.State == ContinuousVoiceState.Stopped) _ = StopAsync();
        else Notify();
    }

    private void Notify() => StateChanged?.Invoke(this, Status);

    public async ValueTask DisposeAsync()
    {
        lock (_sync) _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _voice.StateChanged -= OnVoiceStateChanged;
    }

    private sealed class Attempt(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StopRequested { get; set; }
        public bool CleanupFailed { get; set; }
    }
}
