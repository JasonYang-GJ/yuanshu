using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScreenGuide.DesktopClient;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.VoiceHarness;

internal static partial class Program
{
    private static async Task<DirectCallEvidence> RunDirectCallScenarioAsync(
        Application application, MainWindow window, DesktopApiClient api, ControlledVoiceListener voice,
        RecordingChatProvider provider, ControlledSpeechOutput speech, DelayedSubmissionApi delay)
    {
        // Real WPF callbacks and isolated Host, controlled ASR/TTS/provider only.
        // Never shows or captures another app and never uses the physical microphone.
        var initialization = await application.Dispatcher.InvokeAsync(window.InitializeClientAsync)
            .Task.ConfigureAwait(false);
        await initialization.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Require(await api.GetCurrentSessionAsync().ConfigureAwait(false) is null, "direct_call_profile_not_empty");
        speech.Override = (_, cancellation) => { cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await ClickAsync("StartVoiceButton").ConfigureAwait(false);
        await voice.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        var originalGeneration = voice.RecognitionGeneration;
        voice.Partial("你好袁");
        voice.Recognize("你好袁叔");
        await WaitAsync(() => Task.FromResult(delay.SubmittedTexts.Count > 0 || speech.Texts.Count > 0),
            "direct_call_produced_no_response").ConfigureAwait(false);
        Require(delay.SubmittedTexts.Count == 0, "direct_call_was_sent_to_host");
        Require(provider.InvocationCount == 0, "direct_call_invoked_chat_model");
        Require(speech.Texts.SequenceEqual(["我在。"]), "direct_call_acknowledgement_not_short_or_repeated");
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "direct_call_did_not_resume_listening")
            .ConfigureAwait(false);
        Require(await api.GetCurrentSessionAsync().ConfigureAwait(false) is null, "direct_call_created_session");
        voice.RecognizeAt("你好袁叔", originalGeneration);
        voice.Recognize("我在。");
        await DrainAsync().ConfigureAwait(false);
        Require(speech.Texts.Count == 1 && delay.SubmittedTexts.Count == 0, "direct_call_replayed_old_audio_or_echo");

        voice.Recognize(AfterCallInput);
        var actual = await WaitForSessionAsync(api, snapshot => snapshot.Turns.Count == 1
            && snapshot.Turns[0].InputText == AfterCallInput && snapshot.Turns[0].Phase == "Completed",
            TimeSpan.FromSeconds(10), "direct_call_swallowed_following_question").ConfigureAwait(false);
        await WaitAsync(() => Task.FromResult(speech.Texts.Count == 2), "direct_call_following_reply_not_spoken")
            .ConfigureAwait(false);
        Require(delay.SubmittedTexts.Count == 1 && provider.InvocationCount == 1,
            "direct_call_following_question_not_exactly_once");
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "direct_call_following_reply_blocked_reception")
            .ConfigureAwait(false);

        // A call is attention, not permission or a replacement instruction.
        // Show only this fixture's own Orb so the real visibility and speech-refresh
        // path runs; all window identities below are fake, and no consent API is called.
        var show = await OnUiAsync(() => window.ShowAssistantAsync(startListening: false)).ConfigureAwait(false);
        await show.ConfigureAwait(false);
        var consent = new SessionWindowConsentDto(Guid.NewGuid(), "ResultRead", 42, "测试应用的临时窗口");
        var pending = actual.Turns[0] with { Phase = "WaitingForWindowConsent", WindowConsent = consent,
            IntentKind = "ApplicationSearch" };
        var snapshot = actual with { ForegroundTurn = pending, Turns = [pending], ActiveTurns = [pending] };
        Require(VisualSearchVoiceConsentBinding.From(snapshot) is not null, "pending_fixture_did_not_bind_visual_permission");
        using var operation = new CancellationTokenSource();
        await DoOnUiAsync(() =>
        {
            Set("_currentSession", snapshot);
            Set("_voiceCommandBusy", true);
            Set("_activeVoiceCommandCancellation", operation);
            typeof(MainWindow).GetMethod("RenderAssistantSurface", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
        }).ConfigureAwait(false);
        var binding = VisualSearchVoiceConsentBinding.From(snapshot)!;
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "pending_permission_not_prepared")
            .ConfigureAwait(false);
        await WaitForPermissionReadyAsync().ConfigureAwait(false);
        var oldConsentRecognition = voice.RecognitionGeneration;
        var oldInputVersion = await OnUiAsync(() => (long)Get("_replacementInputVersion")!).ConfigureAwait(false);
        voice.Partial("你好袁");
        voice.Recognize("你好袁叔");
        await WaitAsync(() => Task.FromResult(speech.Texts.Count == 3), "pending_permission_call_not_acknowledged")
            .ConfigureAwait(false);
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "pending_permission_call_stranded_reception")
            .ConfigureAwait(false);
        await WaitForPermissionReadyAsync().ConfigureAwait(false);
        Require(!operation.IsCancellationRequested, "direct_call_cancelled_pending_permission");
        Require(await OnUiAsync(() => (long)Get("_replacementInputVersion")! == oldInputVersion
            && ((SessionSnapshotDto)Get("_currentSession")!).ForegroundTurn?.WindowConsent == consent)
            .ConfigureAwait(false), "direct_call_replaced_permission_identity");
        Require(delay.SubmittedTexts.Count == 1 && provider.InvocationCount == 1,
            "pending_permission_call_dispatched_new_request");
        Require(await OnUiAsync(() =>
        {
            var gate = (VisualSearchVoiceConsentGate)Get("_visualVoiceConsent")!;
            return gate.TryConsume("允许这次读取", snapshot,
                ((VoiceSessionController)Get("_voiceSession")!).ListeningGeneration, oldConsentRecognition, true) is null;
        }).ConfigureAwait(false), "call_reused_pre_speech_permission_audio");
        // Consume only the synthetic local gate, never send a Host/window response.
        Require(await OnUiAsync(() => ((VisualSearchVoiceConsentGate)Get("_visualVoiceConsent")!)
            .TryConsume("允许这次读取", snapshot, ((VoiceSessionController)Get("_voiceSession")!).ListeningGeneration,
                voice.RecognitionGeneration, true) is not null).ConfigureAwait(false), "call_did_not_rearm_visible_unconsumed_permission");
        voice.Recognize("元枢");
        await WaitAsync(() => Task.FromResult(speech.Texts.Count == 4), "consumed_permission_call_not_acknowledged")
            .ConfigureAwait(false);
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "consumed_permission_call_stranded_reception")
            .ConfigureAwait(false);
        Require(await OnUiAsync(() =>
        {
            var gate = (VisualSearchVoiceConsentGate)Get("_visualVoiceConsent")!;
            var generation = ((VoiceSessionController)Get("_voiceSession")!).ListeningGeneration;
            return gate.IsCurrent(binding, snapshot, generation)
                && !gate.Arm(binding, snapshot, generation, voice.RecognitionGeneration, true);
        }).ConfigureAwait(false), "call_rearmed_consumed_permission");
        await DoOnUiAsync(() =>
        {
            Set("_activeVoiceCommandCancellation", null);
            Set("_voiceCommandBusy", false);
            Set("_currentSession", actual);
        }).ConfigureAwait(false);

        voice.Recognize(OldInput);
        var blocked = await provider.BlockingTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await WaitForSessionAsync(api, current => current.ForegroundTurn?.Id == blocked
            && current.ForegroundTurn.Phase == "Responding", TimeSpan.FromSeconds(10),
            "repeated_stop_target_not_responding").ConfigureAwait(false);
        voice.Partial("停停");
        voice.Recognize("停停");
        Require(await provider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false) == blocked,
            "repeated_stop_cancelled_wrong_provider_request");
        provider.AllowLateReply.TrySetResult();
        await provider.LateReplyReturned.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var stopped = await WaitForSessionAsync(api, current => current.Turns.Single(item => item.Id == blocked).Phase == "Cancelled",
            TimeSpan.FromSeconds(10), "repeated_stop_did_not_cancel_host_turn").ConfigureAwait(false);
        Require(stopped.Turns.Count == 2 && delay.SubmittedTexts.Count == 2
            && !delay.SubmittedTexts.Contains("停停"), "repeated_stop_created_chat_request");
        Require(stopped.Messages.All(item => !item.Content.Contains(RecordingChatProvider.LateReply, StringComparison.Ordinal))
            && !speech.Texts.Contains(RecordingChatProvider.LateReply), "repeated_stop_accepted_late_reply");
        await ClickAsync("StopVoiceButton").ConfigureAwait(false);
        return new(true, true, true, true, true, true, true, true, false, false, false);

        object? Get(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        async Task ClickAsync(string name) => await DoOnUiAsync(() =>
            ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))).ConfigureAwait(false);
        async Task DoOnUiAsync(Action action) => await application.Dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
        async Task<T> OnUiAsync<T>(Func<T> action) => await application.Dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
        async Task DrainAsync() => await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.ConfigureAwait(false);
        async Task WaitForPermissionReadyAsync() => await WaitAsync(() => OnUiAsync(() =>
        {
            var gate = (VisualSearchVoiceConsentGate)Get("_visualVoiceConsent")!;
            return (long?)typeof(VisualSearchVoiceConsentGate)
                .GetField("_recognitionGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate)
                == voice.RecognitionGeneration;
        }), "visible_permission_not_rearmed_after_call").ConfigureAwait(false);
        static async Task WaitAsync(Func<Task<bool>> predicate, string failure)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await predicate().ConfigureAwait(false)) return;
                await Task.Delay(25).ConfigureAwait(false);
            }
            Require(false, failure);
        }
    }

    private sealed record DirectCallEvidence(bool LocalAcknowledgementOnly, bool NoSessionForCall,
        bool StaleAndEchoRejected, bool FollowingQuestionExactlyOnce, bool PendingPermissionPreserved,
        bool VisiblePermissionRearmedWithoutNonceReplay,
        bool RepeatedStopCancelledHostWithoutChat, bool StoppedLateReplyRejected,
        bool RealMicrophoneUsed, bool RealSpeechOutputUsed, bool RealModelUsed);
}
