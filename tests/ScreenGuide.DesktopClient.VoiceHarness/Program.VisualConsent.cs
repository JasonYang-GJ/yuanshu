using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScreenGuide.DesktopClient;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Voice.Windows;

namespace ScreenGuide.DesktopClient.VoiceHarness;

internal static partial class Program
{
    private static async Task<VisualConsentRoutingEvidence> RunVisualConsentRoutingScenarioAsync(
        Application application, MainWindow window, ControlledVoiceListener voice, DelayedSubmissionApi api)
    {
        // Actual WPF recognition callbacks and replacement queue; synthetic state only.
        // No visible surface, microphone, capture, native action, or permission API call.
        var now = DateTimeOffset.UtcNow;
        var turn = new UnifiedSessionTurnDto(Guid.NewGuid(), 1, "在测试应用里搜索示例", "Voice", "AssistantCommand",
            "WaitingForWindowConsent", "None", "ApplicationSearch", null, null, null, null, 18, "测试应用",
            false, false, "这次只读取明确窗口核对搜索结果。", null, now, now, null,
            WindowConsent: new(Guid.NewGuid(), "ResultRead", 42, "测试应用的临时窗口"));
        var snapshot = new SessionSnapshotDto(3, "test-host", now, Guid.NewGuid(), Guid.NewGuid(), "测试话题", turn.Phase,
            null, null, turn, [turn], [turn], []);
        var session = await OnUi(() =>
        {
            Set("_voiceCommandBusy", true);
            Set("_currentSession", snapshot);
            return (VoiceSessionController)Get("_voiceSession")!;
        }).ConfigureAwait(false);
        await session.StartAsync().ConfigureAwait(false);
        await DoOnUi(() => Invoke("SynchronizeVoiceReception")).ConfigureAwait(false);
        using var pending = new CancellationTokenSource();
        await DoOnUi(() => Set("_activeVoiceCommandCancellation", pending)).ConfigureAwait(false);
        var initialVersion = await OnUi(() => (long)Get("_replacementInputVersion")!).ConfigureAwait(false);

        var searchTurn = turn with { WindowConsent = new(Guid.NewGuid(), "Search", 18, "测试应用") };
        var searchSnapshot = snapshot with { ForegroundTurn = searchTurn, Turns = [searchTurn], ActiveTurns = [searchTurn] };
        await DoOnUi(() => Set("_currentSession", searchSnapshot)).ConfigureAwait(false);
        voice.Partial("允许这次识别");
        await DrainAsync().ConfigureAwait(false);
        Require(!pending.IsCancellationRequested, "search_permission_partial_cancelled_original_request");
        voice.Recognize("允许这次识别搜索");
        await DrainAsync().ConfigureAwait(false);
        Require(!pending.IsCancellationRequested, "search_permission_noun_misrecognition_cancelled_original_request");
        Require(await QueuedTextAsync().ConfigureAwait(false) is null, "search_permission_noun_misrecognition_queued_as_chat");
        Require(await OnUi(() => ((TextBlock)window.FindName("StatusBarText")).Text.Contains("没听清", StringComparison.Ordinal))
            .ConfigureAwait(false), "search_permission_noun_misrecognition_did_not_request_repeat");
        voice.Recognize("允许这次搜索");
        await DrainAsync().ConfigureAwait(false);
        Require(!pending.IsCancellationRequested && await QueuedTextAsync().ConfigureAwait(false) is null,
            "unarmed_short_search_permission_escaped_to_chat");
        await DoOnUi(() => Set("_currentSession", snapshot)).ConfigureAwait(false);

        await DoOnUi(() => ((TextBlock)window.FindName("StatusBarText")).Text = "before stale audio").ConfigureAwait(false);
        voice.RecognizeAt("原是读取这个临时窗口", voice.RecognitionGeneration - 1);
        await DrainAsync().ConfigureAwait(false);
        Require(await OnUi(() => ((TextBlock)window.FindName("StatusBarText")).Text == "before stale audio")
            .ConfigureAwait(false), "stale_consent_audio_changed_prompt");
        Require(!pending.IsCancellationRequested && await QueuedTextAsync().ConfigureAwait(false) is null,
            "stale_consent_audio_changed_original_request");

        voice.Partial("原是读");
        await DrainAsync().ConfigureAwait(false);
        Require(!pending.IsCancellationRequested, "consent_partial_cancelled_original_request");
        voice.Recognize("原是读取这个临时窗口");
        await DrainAsync().ConfigureAwait(false);
        Require(!pending.IsCancellationRequested, "unclear_consent_cancelled_original_request");
        Require(await QueuedTextAsync().ConfigureAwait(false) is null, "unclear_consent_queued_as_chat");
        Require(await OnUi(() => (long)Get("_replacementInputVersion")!).ConfigureAwait(false) == initialVersion,
            "unclear_consent_replaced_input_operation");
        Require(await OnUi(() => ((SessionSnapshotDto)Get("_currentSession")!).ForegroundTurn?.Id == turn.Id)
            .ConfigureAwait(false), "unclear_consent_changed_original_turn");
        Require(await OnUi(() => ((TextBlock)window.FindName("StatusBarText")).Text.Contains("没听清", StringComparison.Ordinal))
            .ConfigureAwait(false), "unclear_consent_did_not_request_repeat");

        // An exact short phrase is reserved control text even before a visible grant is armed.
        voice.Recognize("允许这次读取");
        await DrainAsync().ConfigureAwait(false);
        Require(await QueuedTextAsync().ConfigureAwait(false) is null, "short_consent_queued_as_chat");
        Require(!pending.IsCancellationRequested, "unarmed_short_consent_cancelled_original_request");

        // A clearly different final request keeps the existing replacement/cancellation semantics.
        voice.Partial("打开记事本");
        await DrainAsync().ConfigureAwait(false);
        Require(!pending.IsCancellationRequested, "pending_consent_cancelled_before_final_disambiguation");
        voice.Recognize("打开记事本");
        await DrainAsync().ConfigureAwait(false);
        Require(pending.IsCancellationRequested, "new_command_did_not_cancel_original_request");
        Require(await QueuedTextAsync().ConfigureAwait(false) == "打开记事本", "new_command_was_swallowed_as_consent");

        foreach (var replacement in new[] { "搜索这次读取", "允许这次搜索然后发送消息", "允许这次搜索删除" })
        {
            using var replacementPending = new CancellationTokenSource();
            await DoOnUi(() =>
            {
                Invoke("CancelPendingVoiceInput", true, false, false);
                Set("_activeVoiceCommandCancellation", replacementPending);
            }).ConfigureAwait(false);
            voice.Recognize(replacement);
            await DrainAsync().ConfigureAwait(false);
            Require(replacementPending.IsCancellationRequested, "explicit_new_action_did_not_replace_permission");
            Require(await QueuedTextAsync().ConfigureAwait(false) == replacement, "explicit_new_action_was_swallowed_as_permission");
            await DoOnUi(() => Set("_activeVoiceCommandCancellation", null)).ConfigureAwait(false);
        }

        await DoOnUi(() => Invoke("CancelPendingVoiceInput", true, false, false)).ConfigureAwait(false);
        using var stopPending = new CancellationTokenSource();
        await DoOnUi(() => Set("_activeVoiceCommandCancellation", stopPending)).ConfigureAwait(false);
        voice.Partial("请停止");
        await DrainAsync().ConfigureAwait(false);
        Require(stopPending.IsCancellationRequested, "stop_partial_was_delayed_by_consent");

        // Outside a consent wait, the same text remains ordinary user input.
        await DoOnUi(() => Set("_currentSession", null)).ConfigureAwait(false);
        voice.Recognize("原是读取这个临时窗口");
        await DrainAsync().ConfigureAwait(false);
        Require(await QueuedTextAsync().ConfigureAwait(false) == "原是读取这个临时窗口", "non_consent_chat_was_swallowed");
        Require(api.SubmittedTexts.IsEmpty && api.CancelledTurns.IsEmpty && api.ConfirmedTurns.IsEmpty && api.WindowConsentTurns.IsEmpty,
            "isolated_routing_fixture_escaped_to_host");
        await DoOnUi(() =>
        {
            Invoke("CancelPendingVoiceInput", true, false, false);
            Set("_activeVoiceCommandCancellation", null);
        }).ConfigureAwait(false);
        await session.StopAsync().ConfigureAwait(false);
        return new(true, true, true, true, true, true, true, true, true, false, false);

        object? Get(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        void Invoke(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
        async Task<T> OnUi<T>(Func<T> action) => await application.Dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
        async Task DoOnUi(Action action) => await application.Dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
        async Task DrainAsync() => await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle)
            .Task.ConfigureAwait(false);
        async Task<string?> QueuedTextAsync() => await OnUi(() => ((VoiceUtterance?)Get("_queuedVoiceUtterance"))?.Text)
            .ConfigureAwait(false);
    }

    private sealed record VisualConsentRoutingEvidence(bool PartialPreservedOriginal, bool UnclearFinalStayedLocal,
        bool UnarmedShortPhraseStayedLocal, bool NewCommandReplaced, bool StopPartialCancelled,
        bool OrdinaryChatUnchanged, bool StaleAudioIgnored, bool SearchNounMisrecognitionStayedLocal,
        bool ExplicitActionsNotSwallowed, bool RealMicrophoneUsed, bool DesktopActionsExecuted);
}
