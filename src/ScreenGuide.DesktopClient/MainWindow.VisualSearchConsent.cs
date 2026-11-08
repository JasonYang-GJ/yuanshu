using System.Windows.Threading;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Voice.Windows;

namespace ScreenGuide.DesktopClient;

public partial class MainWindow
{
    private readonly VisualSearchVoiceConsentGate _visualVoiceConsent = new();
    private CancellationTokenSource? _visualConsentPresentationCancellation;
    private CancellationTokenSource? _visualConsentResponseCancellation;
    private VisualSearchVoiceConsentBinding? _visualConsentRenderedBinding;
    private VisualSearchVoiceConsentBinding? _visualConsentUnavailable;
    private VisualSearchVoiceConsentBinding? _visualConsentRepeat;

    private bool IsVisualConsentSurfaceVisible() => _islandRequested
        ? _islandBridge is { IsVisible: true } && _islandProcess is { HasExited: false }
        : _assistantWindow is { IsVisible: true };

    private VisualSearchVoiceConsentBinding? GetDisplayableVisualConsent() =>
        !_forceClose && _voiceSession.IsListening && _voiceReception.Mode != VoiceReceptionMode.Standby
        && _voiceSession.Status.State is not (VoiceSessionState.Faulted or VoiceSessionState.CleanupFailed)
            ? VisualSearchVoiceConsentBinding.From(_currentSession) : null;

    private bool IsVisualConsentPromptVisible(VisualSearchVoiceConsentBinding binding) =>
        _visualConsentUnavailable != binding && IsVisualConsentSurfaceVisible() && _visualConsentRenderedBinding == binding
        && GetDisplayableVisualConsent() == binding;

    private string VisualConsentPrompt(VisualSearchVoiceConsentBinding binding) =>
        _visualConsentUnavailable == binding
            ? VisualSearchVoiceConsentGate.ManualConfirmationPrompt
            : (_visualConsentRepeat == binding ? "刚才没听清许可，请重说。" : string.Empty)
                + VisualSearchVoiceConsentGate.Prompt(binding.Purpose);

    private void RefreshVisualVoiceConsent()
    {
        var generation = _voiceSession.ListeningGeneration;
        if (_forceClose || _lifetime.IsCancellationRequested || generation is null || !IsVisualConsentSurfaceVisible())
        {
            RevokeVisualVoiceConsent();
            return;
        }
        if (VisualSearchVoiceConsentBinding.From(_currentSession) is not { } pending)
        {
            // Executing is the expected result of our response. Do not revoke its
            // in-flight receipt merely because the Host has already resumed the Turn.
            _visualVoiceConsent.Clear();
            _visualConsentPresentationCancellation?.Cancel();
            return;
        }
        if (_visualConsentUnavailable == pending)
        {
            // Manual-only is sticky for this exact nonce, even after microphone
            // pause/resume. A short fallback is not the original full permission.
            _visualVoiceConsent.Revoke(_currentSession, generation);
            return;
        }
        if (_speechCancellation is { IsCancellationRequested: false })
        {
            _visualVoiceConsent.SuspendRecognition();
            _visualConsentPresentationCancellation?.Cancel();
            return;
        }
        if (_voiceReception.Mode != VoiceReceptionMode.Direct && _visualConsentPresentationCancellation is null) return;
        if (_visualVoiceConsent.Begin(_currentSession, generation) is not { } binding) return;
        _visualConsentUnavailable = null;
        _visualConsentRepeat = null;
        _visualConsentPresentationCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _replacementInputCancellation.Token);
        _visualConsentPresentationCancellation = cancellation;
        _ = PrepareVisualVoiceConsentAsync(binding, generation.Value, cancellation);
    }

    private async Task PrepareVisualVoiceConsentAsync(VisualSearchVoiceConsentBinding binding, long generation,
        CancellationTokenSource cancellation)
    {
        long? transition = null;
        try
        {
            if (_islandRequested)
            {
                var bridge = _islandBridge ?? throw new InvalidOperationException("许可界面不可见。");
                var revision = RenderIslandSurface();
                await bridge.WaitForPresentedAsync(revision, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3), cancellation.Token);
                if (!ReferenceEquals(bridge, _islandBridge) || !bridge.IsVisible) return;
            }
            else
            {
                // Let the already visible, non-activating Orb render the permission.
                // No Show/Activate or focus restoration is performed here.
                await Dispatcher.InvokeAsync(() => _assistantWindow?.UpdateLayout(), DispatcherPriority.Render,
                    cancellation.Token);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsVisualConsentPromptVisible(binding) || !_visualVoiceConsent.IsCurrent(binding, _currentSession, generation)
                || _voiceReception.Mode != VoiceReceptionMode.Direct || _voiceSession.ListeningGeneration != generation) return;
            transition = _voiceReception.BeginTransition();
            _voiceSegments.Reset();
            // Discard audio buffered before this exact permission was displayed.
            var recognition = await _voice.ResetRecognitionAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_voiceReception.CompleteTransition(transition.Value, generation, recognition, VoiceReceptionMode.Direct))
                _visualVoiceConsent.Arm(binding, _currentSession, _voiceSession.ListeningGeneration, recognition,
                    IsVisualConsentPromptVisible(binding));
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!_forceClose && _visualVoiceConsent.IsCurrent(binding, _currentSession, generation))
            {
                _visualConsentUnavailable = binding;
                _visualVoiceConsent.Revoke(_currentSession, generation);
                StatusBarText.Text = VisualSearchVoiceConsentGate.ManualConfirmationPrompt;
                RenderAssistantSurface();
            }
        }
        finally
        {
            if (transition is { } owned && _voiceReception.OwnsTransition(owned, generation))
                _voiceReception.CompleteTransition(owned, generation, _voice.RecognitionGeneration, VoiceReceptionMode.Direct);
            if (ReferenceEquals(_visualConsentPresentationCancellation, cancellation))
                _visualConsentPresentationCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool TryHandleVisualConsentUtterance(VoiceUtterance utterance, long generation)
    {
        if (!VisualSearchVoiceConsentGate.TryParse(utterance.Text, out _, out _))
        {
            if (!_voiceReception.AcceptsPartial(generation, utterance.RecognitionGeneration)
                || VisualSearchVoiceConsentBinding.From(_currentSession) is not { } pending
                || !VisualSearchVoiceConsentGate.IsUnclearConsentReply(utterance.Text)) return false;
            _visualConsentRepeat = pending;
            VoiceTranscriptText.Text = utterance.Text;
            _islandVoiceText = utterance.Text;
            _islandPhaseOverride = null;
            StatusBarText.Text = "刚才没听清许可，没有批准；请按当前提示重说。";
            RenderAssistantSurface();
            return true;
        }
        // These complete control phrases are never merged into an earlier search,
        // sent to a model, or reinterpreted as a replacement Session input.
        var response = _voiceReception.Mode == VoiceReceptionMode.Direct
            ? _visualVoiceConsent.TryConsume(utterance.Text, _currentSession, generation,
                utterance.RecognitionGeneration, _visualConsentRenderedBinding is { } shown
                    && IsVisualConsentPromptVisible(shown) && _visualConsentUnavailable != shown) : null;
        if (response is null)
        {
            StatusBarText.Text = _visualConsentUnavailable == VisualSearchVoiceConsentBinding.From(_currentSession)
                && _visualConsentUnavailable is not null ? VisualSearchVoiceConsentGate.ManualConfirmationPrompt
                : "没有与这句话匹配的有效窗口许可；请查看当前提示后再说。";
            return true;
        }
        VoiceTranscriptText.Text = utterance.Text;
        _visualConsentRepeat = null;
        _islandVoiceText = utterance.Text;
        _islandPhaseOverride = null;
        RenderAssistantSurface();
        _ = RespondToVisualConsentByVoiceAsync(response);
        return true;
    }

    private async Task RespondToVisualConsentByVoiceAsync(VisualSearchVoiceConsentResponse response)
    {
        var binding = response.Binding;
        var operation = CaptureInputOperation();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, operation.Cancellation);
        _visualConsentResponseCancellation = cancellation;
        try
        {
            SessionSnapshotDto? snapshot = null;
            using var submission = new RevocableInputSubmission<SessionTurnCommandResultDto>(
                async token =>
                {
                    snapshot = await _api.RespondSessionWindowConsentAsync(binding.SessionId, binding.TurnId,
                        response.Granted, token, binding.ConsentId);
                    // Reuse the existing receipt/cancellation reconciliation for this
                    // exact Turn. No replacement input or new command is submitted.
                    return new(binding.SessionId, binding.TurnId, false);
                },
                (receipt, token) => _api.CancelSessionTurnAsync(receipt.SessionId, receipt.TurnId, token),
                cancellation.Token, _lifetime.Token);
            SetInputRegistration(submission, operation);
            await submission.WaitAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            if (snapshot is not null && IsInputOperationCurrent(operation) && snapshot.CoordinatorInstanceId == binding.CoordinatorId
                && snapshot.CoordinatorStartedAtUtc == binding.CoordinatorStartedAt && snapshot.SessionId == binding.SessionId)
                RenderSession(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!_forceClose && _currentSession?.SessionId == binding.SessionId)
                StatusBarText.Text = "这次窗口许可没有生效，请查看当前请求；不会复用旧确认。";
        }
        finally
        {
            if (ReferenceEquals(_visualConsentResponseCancellation, cancellation)) _visualConsentResponseCancellation = null;
        }
    }

    private void RevokeVisualVoiceConsent()
    {
        _visualVoiceConsent.Revoke(_currentSession, _voiceSession.ListeningGeneration);
        _visualConsentPresentationCancellation?.Cancel();
        _visualConsentResponseCancellation?.Cancel();
    }
}
