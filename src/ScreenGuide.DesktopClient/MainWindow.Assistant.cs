using System.Windows;
using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient;

public partial class MainWindow
{
    private AssistantOrbWindow? _assistantWindow;
    private readonly DateTimeOffset _clientOpenedAt = DateTimeOffset.UtcNow;
    private Task<bool>? _assistantSurfaceInitialization;

    public async Task ActivateFromLaunchAsync(DesktopActivationRequest request)
    {
        if (_forceClose || _lifetime.IsCancellationRequested) return;
        try
        {
            if (request.ShowManagement) OpenDashboardFromTray();
            else await ShowAssistantAsync(request.StartListening);
        }
        catch (OperationCanceledException) when (_forceClose || _lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_forceClose) ShowError("元枢未能完成本次打开，请重试。", exception);
        }
    }

    public async Task ShowAssistantAsync(bool startListening)
    {
        if (_forceClose || _lifetime.IsCancellationRequested) return;
        var stopVersion = _voiceSession.StopVersion;
        var firstOpening = _assistantSurfaceInitialization is null;
        // Repeated launches share surface creation, never a second Island child or listener.
        var initialization = _assistantSurfaceInitialization ??= InitializeAssistantSurfaceAsync();
        bool ready;
        try { ready = await initialization; }
        catch
        {
            if (ReferenceEquals(_assistantSurfaceInitialization, initialization)) _assistantSurfaceInitialization = null;
            throw;
        }
        if (!ready && ReferenceEquals(_assistantSurfaceInitialization, initialization)) _assistantSurfaceInitialization = null;
        if (!ready || _forceClose || _lifetime.IsCancellationRequested) return;
        if (!firstOpening) ShowAssistantSurface();
        if (!startListening || stopVersion != _voiceSession.StopVersion) return;
        var visible = _islandRequested
            ? _islandBridge is { IsVisible: true } && _islandProcess is { HasExited: false }
            : _assistantWindow is { IsVisible: true };
        if (visible) await _voiceSession.StartAsync(_lifetime.Token);
    }

    private async Task<bool> InitializeAssistantSurfaceAsync()
    {
        if (_islandRequested)
        {
            var ready = await TryShowIslandAsync();
            await InitializeClientAsync();
            return ready;
        }
        ShowAssistantSurface();
        await InitializeClientAsync();
        return true;
    }

    public void ShowAssistantSurface()
    {
        if (_forceClose) return;
        if (_islandRequested && _islandBridge is { IsVisible: true })
        {
            Hide();
            RenderIslandSurface();
            _ = OpenIslandControlsAsync();
            return;
        }
        if (_assistantWindow is null)
        {
            _assistantWindow = new AssistantOrbWindow();
            _assistantWindow.ToggleVoiceRequested += async () =>
            {
                if (_voiceSession.Status.CanStop)
                {
                    CancelPendingVoiceInput();
                    await _voiceSession.StopAsync();
                }
                else if (_voiceSession.Status.CanStart)
                {
                    var stopVersion = _voiceSession.StopVersion;
                    await InitializeClientAsync();
                    if (!_forceClose && !_lifetime.IsCancellationRequested && _assistantWindow.IsVisible
                        && stopVersion == _voiceSession.StopVersion)
                        await _voiceSession.StartAsync(_lifetime.Token);
                }
            };
            _assistantWindow.StopRequestRequested += async () =>
            {
                CancelPendingVoiceInput();
                await CancelCurrentSessionTurnAsync();
            };
            _assistantWindow.DashboardRequested += OpenDashboardFromTray;
            _assistantWindow.MemoryRequested += OpenAssistantMemories;
            _assistantWindow.ExitRequested += async () => await ExitApplicationAsync();
            Title = "元枢 · 管理";
            ReturnToAssistantButton.Visibility = Visibility.Visible;
        }
        _assistantWindow.ShowSurface();
        Hide();
        RenderAssistantSurface();
    }

    private void ReturnToAssistantButton_Click(object sender, RoutedEventArgs e) => ShowAssistantSurface();

    private void RenderAssistantSurface()
    {
        RenderIslandSurface();
        if (_assistantWindow is null || _forceClose)
        {
            RefreshVisualVoiceConsent();
            return;
        }
        var voice = _voiceSession.Status;
        var presentation = SessionUiPresenter.Present(_currentSession);
        var turn = _currentSession?.ForegroundTurn ?? _currentSession?.Turns.LastOrDefault();
        var needsAttention = turn?.Phase is "WaitingForProject" or "WaitingForFile" or "WaitingForWindow"
            or "WaitingForWindowConsent" or "WaitingForConfirmation" or "WaitingForUser"
            or "WaitingForMemoryOutboundConsent" or "WaitingForPointerAnswerConsent";
        var currentReply = turn is not null && turn.UpdatedAtUtc >= _clientOpenedAt;
        var speaking = _speechCancellation is { IsCancellationRequested: false };
        var status = voice.Message;
        var detail = _voiceSession.IsListening
            ? "直接说出你想聊或想做的事。点击小球可暂停聆听。"
            : "点击小球开始语音，或打开管理窗口打字。";
        var micState = _voiceSession.IsListening ? "麦克风开启" : "麦克风未开启";
        var localReply = GetLocalVoiceReply(needsAttention || presentation.ShowStop || _voiceCommandBusy
            || turn?.Phase is "Failed" or "Interrupted"
            || _voiceReception.Mode == VoiceReceptionMode.Standby
            || voice.State is VoiceSessionState.Faulted or VoiceSessionState.CleanupFailed
            || ErrorBanner.Visibility == Visibility.Visible);
        if (needsAttention)
        {
            status = $"需要你确认 · {micState}";
            detail = (string.IsNullOrWhiteSpace(presentation.DetailText)
                ? presentation.StatusText : presentation.DetailText) + "。点击“查看确认”继续。";
        }
        else if (presentation.ShowStop)
        {
            status = $"正在处理 · {micState}";
            detail = presentation.StatusText;
        }
        else if (currentReply && !string.IsNullOrWhiteSpace(presentation.DetailText))
        {
            detail = presentation.DetailText;
        }
        if (localReply is not null)
        {
            status = $"正在聆听 · {micState}";
            detail = localReply;
        }
        if (speaking && (!_localCallSpeech || !needsAttention && !presentation.ShowStop && !_voiceCommandBusy))
        {
            status = $"正在回答 · {micState}";
            detail = _activeSpeechText ?? detail;
        }
        if (voice.State is VoiceSessionState.Faulted or VoiceSessionState.CleanupFailed)
        {
            status = "语音需要检查";
            detail = voice.Message;
        }
        if (ErrorBanner.Visibility == Visibility.Visible)
        {
            status = $"需要检查 · {micState}";
            detail = ErrorMessageText.Text;
        }
        if (_voiceSession.IsListening && _voiceReception.Mode == VoiceReceptionMode.Standby)
        {
            status = "本地待命 · 麦克风开启";
            detail = "只在本机听取唤醒词。说“元枢”或“你好元枢”继续当前话题；点击小球或按 Esc 关闭麦克风。";
        }
        else if (_voiceSession.IsListening && _voiceReception.Mode == VoiceReceptionMode.Transitioning && !speaking)
        {
            status = _receptionTransitionText;
            detail = "正在切换本机语音接收，暂不接收新的语音指令。";
        }
        // This permission must remain visible while the recognizer drains old audio.
        // Speech/transcription status cannot replace the purpose-specific prompt.
        if (!_islandRequested)
        {
            _visualConsentRenderedBinding = GetDisplayableVisualConsent();
            if (_visualConsentRenderedBinding is { } consent)
            {
                status = $"需要你确认 · {micState}";
                if (_visualConsentUnavailable == consent)
                {
                    detail = VisualSearchVoiceConsentGate.ManualConfirmationPrompt;
                    _visualConsentRenderedBinding = null;
                }
                else detail = (string.IsNullOrWhiteSpace(presentation.DetailText)
                    ? presentation.StatusText : presentation.DetailText) + "\n" + VisualConsentPrompt(consent);
            }
        }
        _assistantWindow.Render(status, detail, voice.CanStart, voice.CanStop, speaking, needsAttention,
            presentation.ShowStop || needsAttention || speaking);
        RefreshVisualVoiceConsent();
    }
}
