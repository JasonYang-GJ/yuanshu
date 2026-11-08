using ScreenGuide.DesktopClient.Services;
using ScreenGuide.Voice.Windows;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class VoiceSessionControllerTests
{
    [Fact]
    public async Task StopBeforeListenerExistsRevokesPendingLaunchWithoutStartingCapture()
    {
        var voice = new ControlledVoice();
        await using var session = new VoiceSessionController(voice);
        var launchVersion = session.StopVersion;
        await session.StopAsync();
        Assert.NotEqual(launchVersion, session.StopVersion);
        Assert.Equal(0, voice.StartCalls);
        Assert.False(session.IsListening);

        var nextLaunchVersion = session.StopVersion;
        await session.StartAsync();
        await session.StartAsync();
        Assert.Equal(nextLaunchVersion, session.StopVersion);
        Assert.Equal(1, voice.StartCalls);
    }

    [Fact]
    public async Task OpeningAndRefreshingNeverGrantsMicrophoneConsent()
    {
        var voice = new ControlledVoice();
        await using var session = new VoiceSessionController(voice);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(VoiceSessionState.Off, session.Status.State);
            Assert.Equal("语音未开启", session.Status.Message);
            Assert.True(session.Status.CanStart);
            Assert.False(session.Status.CanStop);
        }
        Assert.Equal(0, voice.StartCalls);
    }

    [Fact]
    public async Task ExplicitStartAndStopAreIdempotentAndLateListeningIsRejected()
    {
        var voice = new ControlledVoice();
        await using var session = new VoiceSessionController(voice);
        await session.StartAsync();
        await session.StartAsync();
        Assert.Equal(1, voice.StartCalls);
        Assert.True(session.IsListening);
        await session.StopAsync();
        await session.StopAsync();
        voice.Emit(ContinuousVoiceState.Listening, "late");
        Assert.Equal(VoiceSessionState.Off, session.Status.State);
        Assert.Equal("语音已停止", session.Status.Message);
        Assert.False(voice.IsListening);
    }

    [Fact]
    public async Task StopAndRestartInvalidatesTheListeningGenerationOfQueuedRecognition()
    {
        var voice = new ControlledVoice();
        await using var session = new VoiceSessionController(voice);
        Assert.Null(session.ListeningGeneration);
        await session.StartAsync();
        var queuedGeneration = session.ListeningGeneration;
        Assert.NotNull(queuedGeneration);
        var stopping = session.StopAsync();
        Assert.Null(session.ListeningGeneration);
        await stopping;
        await session.StartAsync();
        Assert.NotNull(session.ListeningGeneration);
        Assert.NotEqual(queuedGeneration, session.ListeningGeneration);
    }

    [Fact]
    public async Task FailedMicrophoneCleanupRequiresProcessRestartAndRejectsAnotherStart()
    {
        var voice = new ControlledVoice { StopFailure = new InvalidOperationException("driver still owns capture") };
        await using var session = new VoiceSessionController(voice);
        await session.StartAsync();
        await session.StopAsync();
        Assert.Equal(VoiceSessionState.CleanupFailed, session.Status.State);
        Assert.Contains("关闭元枢后重试", session.Status.Message);
        Assert.False(session.Status.CanStart);
        await session.StartAsync();
        Assert.Equal(1, voice.StartCalls);
    }

    [Fact]
    public async Task FailedStartupStaysFailedAcrossRefreshesAndCanBeRetried()
    {
        var voice = new ControlledVoice { Failure = new FileNotFoundException("private-path") };
        await using var session = new VoiceSessionController(voice);
        await session.StartAsync();
        var failure = session.Status;
        Assert.Equal(VoiceSessionState.Faulted, failure.State);
        Assert.Contains("语音模型不完整", failure.Message);
        Assert.DoesNotContain("private-path", failure.Message);
        for (var i = 0; i < 5; i++) Assert.Equal(failure, session.Status);
        Assert.True(failure.CanStart);
        voice.Failure = null;
        await session.StartAsync();
        Assert.True(session.IsListening);
        Assert.Equal(2, voice.StartCalls);
    }

    [Fact]
    public async Task StopDuringInitializationReturnsWithoutBlockingAndSuppressesLateStart()
    {
        var voice = new ControlledVoice { StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var session = new VoiceSessionController(voice);
        var start = session.StartAsync();
        await voice.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = session.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.Equal(VoiceSessionState.Stopping, session.Status.State);
        Assert.False(session.Status.CanStart);
        Assert.False(session.IsListening);
        Assert.Same(stop, session.StartAsync());
        voice.StartGate.SetResult(); // Fake deliberately ignores cancellation and reports a late start.
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(voice.IsListening);
        Assert.Equal(VoiceSessionState.Off, session.Status.State);
        Assert.Equal(1, voice.StartCalls);
    }

    [Fact]
    public async Task StartupTimeoutRevokesConsentAndOnlyAllowsRetryAfterLateInitializationIsStopped()
    {
        var voice = new ControlledVoice { StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var session = new VoiceSessionController(voice, TimeSpan.FromMilliseconds(30));
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, status) => { if (status.State == VoiceSessionState.Stopping) stopping.TrySetResult(); };
        var start = session.StartAsync();
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.Status.CanStart);
        Assert.Contains("正在取消", session.Status.Message);
        voice.StartGate.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(voice.IsListening);
        Assert.Equal(VoiceSessionState.Faulted, session.Status.State);
        Assert.Contains("启动超时", session.Status.Message);
        Assert.True(session.Status.CanStart);
    }

    [Fact]
    public async Task ClosingDuringInitializationNeverRetainsLateListening()
    {
        var voice = new ControlledVoice { StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var lifetime = new CancellationTokenSource();
        await using var session = new VoiceSessionController(voice);
        var start = session.StartAsync(lifetime.Token);
        await voice.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.Cancel();
        var dispose = session.DisposeAsync().AsTask();
        voice.StartGate.SetResult();
        await Task.WhenAll(start, dispose).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(voice.IsListening);
        Assert.False(session.IsListening);
        Assert.Throws<ObjectDisposedException>(() => { _ = session.StartAsync(); });
    }

    [Fact]
    public async Task MicrophoneFaultRemainsVisibleAfterCleanup()
    {
        var voice = new ControlledVoice();
        await using var session = new VoiceSessionController(voice);
        await session.StartAsync();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, status) => { if (status.State == VoiceSessionState.Faulted) failed.TrySetResult(); };
        voice.Emit(ContinuousVoiceState.Faulted, "麦克风连接已中断");
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("麦克风连接已中断", session.Status.Message);
        Assert.False(voice.IsListening);
        Assert.True(session.Status.CanStart);
    }

    private sealed class ControlledVoice : IContinuousVoiceListener
    {
        public bool IsListening { get; private set; }
        public long RecognitionGeneration { get; private set; }
        public Task<long> ResetRecognitionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsListening) throw new OperationCanceledException();
            return Task.FromResult(++RecognitionGeneration);
        }
        public int StartCalls { get; private set; }
        public Exception? Failure { get; set; }
        public Exception? StopFailure { get; set; }
        public TaskCompletionSource? StartGate { get; set; }
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CS0067
        public event EventHandler<VoicePartialText>? PartialTextChanged;
        public event EventHandler<VoiceUtterance>? UtteranceRecognized;
#pragma warning restore CS0067
        public event EventHandler<ContinuousVoiceStatus>? StateChanged;
        public VoiceCapabilityReport GetCapabilityReport() => new(true, "fake", 1, false, "ready");
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            RecognitionGeneration++;
            StartCalls++;
            StartEntered.TrySetResult();
            if (StartGate is not null) await StartGate.Task;
            if (Failure is not null) throw Failure;
            IsListening = true;
            Emit(ContinuousVoiceState.Listening, "正在聆听");
        }
        public Task StopAsync()
        {
            if (StopFailure is not null) throw StopFailure;
            IsListening = false;
            Emit(ContinuousVoiceState.Stopped, "Stopped");
            return Task.CompletedTask;
        }
        public void Emit(ContinuousVoiceState state, string message) => StateChanged?.Invoke(this, new(state, message));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
