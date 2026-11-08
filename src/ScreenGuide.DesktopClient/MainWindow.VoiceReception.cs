using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient;

public partial class MainWindow
{
    private readonly VoiceReceptionGate _voiceReception = new();
    private readonly VoiceCommandSegments _voiceSegments = new();
    private readonly LocalVoiceReplyPresentation _localVoiceReply = new();
    private bool _localCallSpeech;
    private CancellationTokenSource? _receptionCancellation;
    private string _receptionTransitionText = "正在切换语音接收…";

    private void SynchronizeVoiceReception() => _voiceReception.SynchronizeMicrophone(
        _voiceSession.ListeningGeneration, _voice.RecognitionGeneration);

    private string? GetLocalVoiceReply(bool blocked) => _localVoiceReply.Get(_currentSession,
        _voiceSession.ListeningGeneration, _replacementInputVersion, blocked);

    private async Task AcknowledgeCallAsync(string text)
    {
        if (!_voiceSession.IsListening) return;
        // Calling the assistant is local feedback, not a replacement instruction.
        // Preserve the current Turn, pending operation and exact window consent.
        _voiceSegments.Reset();
        CancelVoiceFeedback();
        _islandPhaseOverride = null;
        _islandVoiceText = text;
        VoiceTranscriptText.Text = text;
        _localVoiceReply.Show("我在。", _currentSession, _voiceSession.ListeningGeneration, _replacementInputVersion);
        await SpeakAssistantCoreAsync("我在。", automaticRequest: null, localReceptionReply: false, localCallReply: true);
    }

    private void CancelReceptionTransition()
    {
        _receptionCancellation?.Cancel();
        if (_voiceReception.Mode != VoiceReceptionMode.Transitioning || _voiceSession.ListeningGeneration is null) return;
        // A newer UI input owns this recovery. The old goodbye/playback cannot restore
        // its mode later; Stop revokes microphone consent before this reset can reopen it.
        _ = RestoreReceptionAsync(VoiceReceptionMode.Direct);
    }

    private async Task RestoreReceptionAsync(VoiceReceptionMode mode)
    {
        var generation = _voiceSession.ListeningGeneration;
        var transition = _voiceReception.BeginTransition();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _receptionCancellation = cancellation;
        _receptionTransitionText = "正在恢复对话…";
        try
        {
            var recognition = await _voice.ResetRecognitionAsync(cancellation.Token);
            _voiceReception.CompleteTransition(transition, generation, recognition, mode);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _voiceSession.ListeningGeneration != generation) { }
        catch (Exception exception)
        {
            if (_voiceReception.OwnsTransition(transition, _voiceSession.ListeningGeneration))
            {
                await _voiceSession.StopAsync();
                ShowError("语音接收未能恢复，麦克风已停止，请重新开始语音。", exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_receptionCancellation, cancellation)) _receptionCancellation = null;
            SynchronizeVoiceReception();
            RenderVoiceStatus();
        }
    }

    private async Task ChangeLocalReceptionAsync(bool standby)
    {
        CancelPendingVoiceInput();
        CancelPendingPointerGesture();
        var operation = CaptureInputOperation();
        var generation = _voiceSession.ListeningGeneration;
        if (generation is null) return;
        var transition = _voiceReception.BeginTransition();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, operation.Cancellation);
        _receptionCancellation = cancellation;
        _receptionTransitionText = standby ? "正在停止当前请求并准备待命…" : "正在唤醒…";
        RenderVoiceStatus();
        try
        {
            var cancellationConfirmed = true;
            if (standby)
            {
                cancellationConfirmed = await AwaitPendingVoiceSubmissionAsync(operation, allowRegistrationHandoff: true);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_currentSession is { ForegroundTurn: { } turn } session && cancellationConfirmed)
                {
                    try
                    {
                        var stopped = await _api.CancelSessionTurnAsync(session.SessionId, turn.Id, cancellation.Token)
                            .WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (IsInputOperationCurrent(operation)) RenderSession(stopped);
                        cancellationConfirmed = stopped.Turns.SingleOrDefault(item => item.Id == turn.Id)?.Phase
                            is "Cancelled" or "Completed" or "Failed" or "Interrupted";
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch { cancellationConfirmed = false; }
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_voiceReception.OwnsTransition(transition, _voiceSession.ListeningGeneration)) return;
            await _voice.ResetRecognitionAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var reply = !standby ? "我在，请说。" : cancellationConfirmed
                ? "好的，我先休息。"
                : "我先待命。上一条请求尚未确认停止，请查看当前任务。";
            await SpeakAssistantAsync(reply, localReceptionReply: true);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_voiceReception.OwnsTransition(transition, _voiceSession.ListeningGeneration)) return;
            var recognition = await _voice.ResetRecognitionAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_voiceReception.CompleteTransition(transition, generation, recognition,
                standby ? VoiceReceptionMode.Standby : VoiceReceptionMode.Direct))
            {
                VoiceTranscriptText.Text = standby ? "只在本机等待唤醒词…" : "等待你说话…";
                StatusBarText.Text = standby ? "已进入本地待命；麦克风仍开启，只识别唤醒词。" : "已唤醒，继续当前话题。";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _voiceSession.ListeningGeneration != generation) { }
        catch (Exception exception)
        {
            if (_voiceReception.OwnsTransition(transition, _voiceSession.ListeningGeneration))
            {
                await _voiceSession.StopAsync();
                ShowError("语音模式切换失败，麦克风已停止，请重新开始语音。", exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_receptionCancellation, cancellation)) _receptionCancellation = null;
            SynchronizeVoiceReception();
            RenderVoiceStatus();
        }
    }

    private async Task ResetSpeechReceptionAsync(CancellationTokenSource owner, VoiceReceptionMode restoreMode)
    {
        if (!ReferenceEquals(_speechCancellation, owner) || owner.IsCancellationRequested
            || _voiceSession.ListeningGeneration is null) return;
        var transition = _voiceReception.BeginTransition();
        var generation = _voiceSession.ListeningGeneration;
        _receptionTransitionText = "正在切换语音接收…";
        try
        {
            var recognition = await _voice.ResetRecognitionAsync(owner.Token);
            owner.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_speechCancellation, owner))
                _voiceReception.CompleteTransition(transition, generation, recognition, restoreMode);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            // A snapshot may revoke only automatic playback, without a replacement
            // input. Drain with fresh ownership before returning to the prior mode;
            // a newer input/standby transition or stopped microphone wins instead.
            if (ReferenceEquals(_speechCancellation, owner)
                && generation == _voiceSession.ListeningGeneration
                && _voiceReception.OwnsTransition(transition, generation))
                await RestoreReceptionAsync(restoreMode);
            throw;
        }
        catch
        {
            if (_voiceReception.OwnsTransition(transition, _voiceSession.ListeningGeneration))
                await _voiceSession.StopAsync();
            throw;
        }
    }
}
