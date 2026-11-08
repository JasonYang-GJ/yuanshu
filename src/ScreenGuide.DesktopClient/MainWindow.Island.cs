using System.Diagnostics;
using System.Windows;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient;

public partial class MainWindow
{
    private bool _islandRequested;
    private string? _islandExecutable;
    private AssistantIslandBridge? _islandBridge;
    private Process? _islandProcess;
    private IslandChildLifetime? _islandLifetime;
    private bool _islandRecovering;
    private bool _islandActivationPending;
    private string _islandVoiceText = string.Empty;
    private string? _islandPhaseOverride;
    private long _islandFinalRevision;
    private string _islandControlIdentity = string.Empty;
    private string _islandControlToken = Guid.NewGuid().ToString("D");

    public void ConfigureIslandSurface(string? executable)
    {
        _islandRequested = true;
        _islandExecutable = executable;
    }

    private Task OpenIslandControlsAsync() => ActivateIslandAsync(showSettings: false);

    public Task OpenIslandSettingsFromTrayAsync() => ActivateIslandAsync(showSettings: true);

    private async Task ActivateIslandAsync(bool showSettings)
    {
        if (_forceClose || _islandActivationPending || _islandBridge is not { IsVisible: true }
            || _islandProcess is not { HasExited: false } || _islandExecutable is null) return;
        _islandActivationPending = true;
        try
        {
            // The native single-instance route only opens the requested surface.
            // It neither restarts the owned Island nor grants microphone consent.
            var launch = new ProcessStartInfo(_islandExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_islandExecutable)!
            };
            launch.ArgumentList.Add(showSettings ? "--assistant-settings" : "--show-controls");
            using var activation = Process.Start(launch);
            if (activation is not null)
                await activation.WaitForExitAsync(_lifetime.Token).WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_forceClose) ShowError("灵动岛面板未能打开，请从元枢托盘菜单重试。", exception);
        }
        finally { _islandActivationPending = false; }
    }

    private async Task<bool> TryShowIslandAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_islandExecutable)
                || !Path.IsPathFullyQualified(_islandExecutable)
                || !File.Exists(_islandExecutable)
                || !string.Equals(Path.GetExtension(_islandExecutable), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("灵动岛组件未找到。");
            var bridge = new AssistantIslandBridge(HandleIslandCommandAsync);
            _islandBridge = bridge;
            bridge.VisibilityLost += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_islandBridge, bridge))
                    _ = RecoverFromIslandLossAsync("灵动岛连接已中断，语音已暂停；重新打开元枢可重试连接。");
            }));
            RenderIslandSurface();
            var launch = new ProcessStartInfo(_islandExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_islandExecutable)!
            };
            launch.ArgumentList.Add("--assistant-bridge");
            launch.Environment["YUANSHU_ASSISTANT_PIPE"] = bridge.PipeName;
            launch.Environment["YUANSHU_CORE_PIPE"] =
                Environment.GetEnvironmentVariable("SCREEN_GUIDE_PIPE_NAME") ?? DesktopIpcEndpoint.CurrentUserPipeName();
            var process = Process.Start(launch) ?? throw new InvalidOperationException("灵动岛未能启动。");
            _islandProcess = process;
            _islandLifetime = new IslandChildLifetime(process);
            bridge.SetPeerProcess(process.Id);
            _ = ObserveIslandExitAsync(process, bridge);
            await bridge.WaitForReadyAsync(_lifetime.Token).WaitAsync(TimeSpan.FromSeconds(20), _lifetime.Token);
            if (_forceClose || !ReferenceEquals(_islandBridge, bridge) || !bridge.IsVisible || process.HasExited)
                return false;
            Title = "元枢 · 管理";
            ReturnToAssistantButton.Content = "返回灵动岛";
            ReturnToAssistantButton.Visibility = Visibility.Visible;
            _tray.SetIslandAvailable(true);
            _assistantWindow?.Hide();
            Hide();
            RenderIslandSurface();
            return true;
        }
        catch (Exception exception) when (!_forceClose && !_lifetime.IsCancellationRequested)
        {
            await RecoverFromIslandLossAsync("灵动岛未能连接，麦克风没有自动开启。请重新打开元枢重试。", exception);
            return false;
        }
    }

    private async Task ObserveIslandExitAsync(Process process, AssistantIslandBridge bridge)
    {
        try { await process.WaitForExitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (InvalidOperationException) { return; }
        if (_forceClose || _lifetime.IsCancellationRequested) return;
        await Dispatcher.InvokeAsync(async () =>
        {
            if (ReferenceEquals(_islandBridge, bridge))
                await RecoverFromIslandLossAsync("灵动岛已关闭，语音已暂停。重新打开元枢可恢复顶部助手。");
        }).Task.Unwrap();
    }

    private async Task RecoverFromIslandLossAsync(string message, Exception? exception = null)
    {
        if (_forceClose || _islandRecovering) return;
        _islandRecovering = true;
        try
        {
            // Revoke microphone and queued work before any asynchronous window cleanup.
            CancelPendingVoiceInput();
            var stopping = _voiceSession.StopAsync();
            _islandRequested = false;
            _islandVoiceText = string.Empty;
            _islandPhaseOverride = null;
            // Recovery stays visible even if a native model load is still unwinding.
            ShowAssistantSurface();
            ShowError(message, exception ?? new InvalidOperationException("助手显示连接不可用。"));
            await stopping;
            await CloseIslandSurfaceAsync();
        }
        finally { _islandRecovering = false; }
    }

    private async Task CloseIslandSurfaceAsync()
    {
        var bridge = _islandBridge;
        var process = _islandProcess;
        var lifetime = _islandLifetime;
        _islandLifetime = null;
        _islandBridge = null;
        _islandProcess = null;
        _tray.SetIslandAvailable(false);
        if (bridge is not null) await bridge.DisposeAsync();
        if (process is null) { lifetime?.Dispose(); return; }
        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException)
                {
                    // This handle is only our freshly launched presentation child,
                    // never a discovered user's app or a standalone Island instance.
                    if (!process.HasExited) process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
        }
        catch (InvalidOperationException) { }
        finally { lifetime?.Dispose(); process.Dispose(); }
    }

    private Task<bool> HandleIslandCommandAsync(string action, string expectedToken) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (_forceClose || _islandBridge is not { IsVisible: true }) return false;
            RenderIslandSurface(); // Refresh authority after crossing the UI queue.
            if (!string.Equals(expectedToken, _islandControlToken, StringComparison.Ordinal)) return false;
            switch (action)
            {
                case "start" when _voiceSession.Status.CanStart:
                    CancelPendingVoiceInput();
                    _ = ObserveIslandControlAsync(_voiceSession.StartAsync(_lifetime.Token));
                    break;
                case "pause" when _voiceSession.Status.CanStop:
                    CancelPendingVoiceInput();
                    _ = ObserveIslandControlAsync(_voiceSession.StopAsync());
                    break;
                case "stop":
                    CancelPendingVoiceInput();
                    _ = ObserveIslandControlAsync(CancelCurrentSessionTurnAsync());
                    break;
                case "management": OpenDashboardFromTray(); break;
                case "memory": OpenAssistantMemories(); break;
                case "exit":
                    // Let the transport acknowledge the explicit click before its owner exits.
                    _ = Dispatcher.BeginInvoke(new Action(() => _ = ExitApplicationAsync()));
                    break;
                default: return false;
            }
            RenderIslandSurface();
            return true;
        }).Task;

    private async Task ObserveIslandControlAsync(Task operation)
    {
        // The wire reply accepts the click. Completion remains visible in the
        // original voice/Session state, so loading a model cannot block heartbeats.
        try { await operation; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_forceClose) ShowError("操作未能完成，请查看当前状态后重试。", exception);
        }
        finally { if (!_forceClose) RenderIslandSurface(); }
    }

    private void PresentIslandPartial(string text)
    {
        if (_islandBridge is null || IsAssistantSpeechEcho(text)) return;
        _islandVoiceText = text;
        _islandPhaseOverride = "transcribing";
        RenderIslandSurface();
    }

    private void PresentIslandFinal(string text)
    {
        if (_islandBridge is null) return;
        _islandVoiceText = text;
        _islandPhaseOverride = "recognized";
        _islandFinalRevision = RenderIslandSurface();
    }

    private async Task AwaitIslandFinalPresentationAsync(InputOperation operation, CancellationToken cancellationToken)
    {
        if (!_islandRequested) return;
        var bridge = _islandBridge;
        // Lock the projection only after the original input operation is stable.
        // A Session update while waiting for earlier registration can change controls.
        var revision = _islandFinalRevision = RenderIslandSurface();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, operation.Cancellation, _lifetime.Token);
        try
        {
            if (bridge is null || revision <= 0) throw new InvalidOperationException("最终转写尚未显示。");
            await bridge.WaitForPresentedAsync(revision, linked.Token).WaitAsync(TimeSpan.FromSeconds(3), linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!IsInputOperationCurrent(operation)) throw new OperationCanceledException(linked.Token);
            if (!ReferenceEquals(_islandBridge, bridge) || !bridge.IsVisible)
                throw new InvalidOperationException("助手显示连接已改变。");
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
        {
            await RecoverFromIslandLossAsync("灵动岛未能显示这次指令，语音已暂停，这次指令没有执行。", exception);
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private long RenderIslandSurface()
    {
        var bridge = _islandBridge;
        if (bridge is null) return 0;
        var voice = _voiceSession.Status;
        var turn = _currentSession?.ForegroundTurn ?? _currentSession?.Turns.LastOrDefault();
        var ui = SessionUiPresenter.Present(_currentSession);
        var speaking = _speechCancellation is { IsCancellationRequested: false };
        var waiting = turn?.Phase is "WaitingForProject" or "WaitingForFile" or "WaitingForWindow"
            or "WaitingForWindowConsent" or "WaitingForConfirmation" or "WaitingForUser"
            or "WaitingForMemoryOutboundConsent" or "WaitingForPointerAnswerConsent";
        var active = ui.ShowStop || _voiceCommandBusy;
        var localReply = GetLocalVoiceReply(active || waiting || turn?.Phase is "Failed" or "Interrupted"
            || _voiceReception.Mode == VoiceReceptionMode.Standby
            || voice.State is VoiceSessionState.Faulted or VoiceSessionState.CleanupFailed
            || ErrorBanner.Visibility == Visibility.Visible);
        var phase = _voiceSession.IsListening ? "listening" : "paused";
        var status = _voiceSession.IsListening ? "正在聆听" : "语音已暂停";
        var detail = _voiceSession.IsListening ? "直接说出你想做的事。" : "点击开始语音，或到管理窗口打字。";
        if (voice.State == VoiceSessionState.Starting) { phase = "idle"; status = "正在开启语音"; detail = voice.Message; }
        if (voice.State == VoiceSessionState.Stopping) { phase = "paused"; status = "正在暂停语音"; detail = voice.Message; }
        if (!_voiceSession.IsListening || _voiceReception.Mode == VoiceReceptionMode.Standby)
        {
            _islandVoiceText = string.Empty;
            _islandPhaseOverride = null;
        }
        if (turn is not null && turn.UpdatedAtUtc >= _clientOpenedAt && !active)
        {
            phase = turn.Phase is "Failed" or "Interrupted" ? "error" : turn.Phase == "Completed" ? "success" : phase;
            detail = ui.DetailText ?? detail;
            status = ui.StatusText;
        }
        if (localReply is not null)
        {
            phase = "listening";
            status = "正在聆听";
            detail = localReply;
        }
        if (active)
        {
            phase = "working";
            status = ui.ShowStop ? ui.StatusText : "正在处理";
            detail = turn is { Phase: "Executing", IntentKind: "ApplicationSearch" }
                ? ui.DetailText ?? "正在执行你的请求。" : "正在执行你的请求。";
        }
        if (waiting)
        {
            phase = "waiting";
            status = "需要你确认";
            detail = (string.IsNullOrWhiteSpace(ui.DetailText) ? ui.StatusText : ui.DetailText)
                + "\n打开管理窗口继续处理。";
        }
        if (speaking && (!_localCallSpeech || !active && !waiting))
        { phase = "speaking"; status = "正在回答"; detail = _activeSpeechText ?? detail; }
        if (_islandPhaseOverride is { } reception && _voiceSession.IsListening)
        {
            phase = reception;
            status = reception == "transcribing" ? "正在听你说话" : "已听到，准备执行";
            detail = reception == "transcribing" ? "说完后我会按这句话处理。" : "正在显示这次指令…";
        }
        if (_voiceReception.Mode == VoiceReceptionMode.Standby && _voiceSession.IsListening && !active && !waiting && !speaking)
        { phase = "standby"; status = "本地待命"; detail = "麦克风开启，仅在本机等待“元枢”或“你好元枢”。"; }
        if (ErrorBanner.Visibility == Visibility.Visible)
        { phase = "error"; status = "需要检查"; detail = ErrorMessageText.Text; }
        if (voice.State is VoiceSessionState.Faulted or VoiceSessionState.CleanupFailed)
        { phase = "error"; status = "语音需要检查"; detail = voice.Message; }
        if (_islandRequested)
        {
            _visualConsentRenderedBinding = GetDisplayableVisualConsent();
            if (_visualConsentRenderedBinding is { } consent)
            {
                phase = "waiting";
                if (_visualConsentUnavailable == consent)
                {
                    status = "需要手动确认";
                    detail = VisualSearchVoiceConsentGate.ManualConfirmationPrompt;
                    _visualConsentRenderedBinding = null;
                }
                else
                {
                    status = consent.Purpose switch
                    {
                        "TaskObservation" => "需要本次软件任务许可",
                        "TaskAction" => "需要确认当前这一步",
                        "ResultRead" => "需要只读结果许可",
                        _ => "需要这次视觉搜索许可"
                    };
                    detail = (string.IsNullOrWhiteSpace(ui.DetailText) ? ui.StatusText : ui.DetailText)
                        + "\n" + VisualConsentPrompt(consent);
                }
            }
        }
        if (_forceClose) { phase = "stopping"; status = "正在退出"; detail = "正在关闭麦克风和本机服务…"; }
        var identity = $"{_voiceSession.ListeningGeneration}|{voice.CanStart}|{voice.CanStop}|{_currentSession?.CoordinatorInstanceId}|{_currentSession?.SessionId}|{turn?.Id}|{turn?.WindowConsent?.ConsentId}|{_replacementInputVersion}";
        if (_islandControlIdentity != identity)
        {
            _islandControlIdentity = identity;
            _islandControlToken = Guid.NewGuid().ToString("D");
        }
        return bridge.Publish(new AssistantIslandSnapshot(1, bridge.InstanceId, 0, _islandControlToken,
            phase, status, active && _islandPhaseOverride is null ? turn?.InputText ?? _islandVoiceText : _islandVoiceText,
            detail, _voiceSession.IsListening,
            voice.CanStart && !_forceClose, voice.CanStop && !_forceClose,
            (active || waiting || speaking || _islandPhaseOverride is not null) && !_forceClose,
            waiting, DateTimeOffset.UtcNow.ToString("O")));
    }
}
