using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ScreenGuide.AI.Core;
using ScreenGuide.DesktopClient;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Voice.Windows;

namespace ScreenGuide.DesktopClient.VoiceHarness;

internal static partial class Program
{
    private const string ResultPrefix = "SCREEN_GUIDE_VOICE_HARNESS_RESULT ";
    private const string OldInput = "VOICE_HARNESS_BLOCK_THEN_LATE_RESULT";
    private const string NewInput = "请回答独立语音替换问题。";
    private const string ThirdInput = "请回答播放期间的新问题。";
    private const string DelayedInput = "请回答延迟回执问题。";
    private const string QueuedInput = "旧队列不得进入新话题";
    private const string StoppedReceiptInput = "VOICE_HARNESS_STOP_BEFORE_RECEIPT";
    private const string BeforeTextRegistrationInput = "VOICE_HARNESS_DELAY_BEFORE_TEXT_REGISTRATION";
    private const string BeforeTopicRegistrationInput = "VOICE_HARNESS_DELAY_BEFORE_TOPIC_REGISTRATION";
    private const string BeforeStoppedTopicRegistrationInput = "VOICE_HARNESS_DELAY_BEFORE_STOPPED_TOPIC";
    private const string ReplacementTextInput = "请回答文字替换问题。";
    private const string SupersededTextInput = "被新话题替代的文字不得提交";
    private const string SupersededProgrammingInput = "旧编程任务不得越过新话题";
    private const string SelectedActionTarget = "voice-harness-selected-app";
    private const string HandoffTextInput = "VOICE_HARNESS_TOPIC_HANDOFF_TEXT";
    private const string HandoffVoiceInput = "VOICE_HARNESS_TOPIC_HANDOFF_VOICE";
    private const string ManualRetryInput = "VOICE_HARNESS_MANUAL_CANCEL_RETRY";
    private const string AfterWakeInput = "请继续解释海浪的形成。";
    private const string SpeechResetInput = "请介绍月球环形山。";
    private const string SnapshotReplacementInput = "请介绍北极星的位置。";
    private const string AfterSpeechResetInput = "请解释闪电形成的原因。";
    private const string AfterCallInput = "我在学习，请解释海浪形成的原因。";
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] is "--exit-owned-island" or "--release-owned-island")
            return CloseIslandForUpdateAsync(int.Parse(args[1]), args[2], args[0] == "--release-owned-island").GetAwaiter().GetResult();
        var stopwatch = Stopwatch.StartNew();
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"screen-guide-voice-harness-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(testRoot, "user-data");
        Application? application = null;
        IHost? host = null;
        TrayIconService? tray = null;
        ControlledVoiceListener? voice = null;
        Task<object>? scenario = null;
        object? evidence = null;
        Exception? failure = null;
        var errorCode = "voice_harness_unexpected";
        var entryAssemblyConfirmed = false;
        var resourceAssemblyConfirmed = false;
        var naturalShutdown = false;
        var cleanupCompleted = false;

        try
        {
            Require(args.Length is 1 or 2 or 3 && File.Exists(args[0]), "fake_codex_missing");
            if (args.Length >= 2 && args[1] is not ("--voice-segments" or "--visual-consent-routing" or "--first-voice-reply" or "--direct-call")
                && !IsStartupVoiceScenario(args[1])) Require(File.Exists(args[1]), "island_executable_missing");
            if (args.Length == 3) Require(args[2] is "--drag-placement" or "--execution-progress", "unknown_scenario");
            Directory.CreateDirectory(dataRoot);
            application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            var harnessAssembly = typeof(Program).Assembly;
            entryAssemblyConfirmed = ReferenceEquals(Assembly.GetEntryAssembly(), harnessAssembly);
            resourceAssemblyConfirmed = ReferenceEquals(Application.ResourceAssembly, harnessAssembly);
            Require(entryAssemblyConfirmed, "entry_assembly_invalid");
            Require(resourceAssemblyConfirmed, "resource_assembly_invalid");
            // Load the real resource definitions without invoking App's production startup.
            using (var resource = harnessAssembly.GetManifestResourceStream("VoiceHarness.ActualApp.xaml")!)
            {
                var document = System.Xml.Linq.XDocument.Load(resource);
                System.Xml.Linq.XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var dictionary = new System.Xml.Linq.XElement(wpf + "ResourceDictionary",
                    new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                    document.Root!.Element(wpf + "Application.Resources")!.Elements());
                application.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
            }

            var settingsStore = new DesktopClientSettingsStore(dataRoot);
            var clientSettings = new DesktopClientSettings
            {
                RunInBackground = false,
                CloseToTray = false,
                NotificationsEnabled = false,
                OnboardingCompleted = true,
                NotificationStateInitialized = true
            };
            settingsStore.SaveAsync(clientSettings).GetAwaiter().GetResult();

            var provider = RecordingChatProvider.Create();
            var pipeName = $"ScreenGuide.VoiceHarness.{Guid.NewGuid():N}";
            Environment.SetEnvironmentVariable("SCREEN_GUIDE_PIPE_NAME", pipeName);
            // The test uses its controlled provider, independent of the product's current default route.
            var route = new AiSettings(new ChatModelRoute("codex", "codex-default"));
            using (var routeStore = new FileAiSettingsStore(Path.Combine(dataRoot, "settings", "ai-settings.json"), route))
                routeStore.SaveAsync(route).GetAwaiter().GetResult();
            host = DesktopHostFactory.Build(
                [],
                new DesktopHostOptions(dataRoot, args[0], pipeName),
                services =>
                {
                    services.RemoveAll<IChatModelProvider>();
                    services.AddSingleton<IChatModelProvider>(provider);
                });
            host.StartAsync().GetAwaiter().GetResult();

            var api = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(5));
            WaitForHostAsync(api, TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            voice = new ControlledVoiceListener();
            var speech = new ControlledSpeechOutput();
            var delayedApi = DispatchProxy.Create<IDesktopApiClient, DelayedSubmissionApi>();
            var submissionDelay = (DelayedSubmissionApi)delayedApi;
            submissionDelay.Inner = api;
            tray = new TrayIconService();
            var window = new MainWindow(
                delayedApi,
                new DesktopHostProcessManager(api),
                settingsStore,
                new WindowsStartupService(),
                tray,
                clientSettings,
                voice,
                speech.SpeakAsync);
            application.MainWindow = window;
            var shutdownOutcome = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            scenario = Task.Run<object>(async () =>
            {
                try
                {
                    if (args.Length == 2 && IsStartupVoiceScenario(args[1]))
                        return await RunStartupVoiceScenarioAsync(application, window, voice,
                            submissionDelay, args[1]).ConfigureAwait(false);
                    if (args.Length == 2 && args[1] == "--first-voice-reply")
                        return await RunFirstVoiceReplyScenarioAsync(application, window, api, voice,
                            speech, submissionDelay).ConfigureAwait(false);
                    if (args.Length == 2 && args[1] == "--direct-call")
                        return await RunDirectCallScenarioAsync(application, window, api, voice,
                            provider, speech, submissionDelay).ConfigureAwait(false);
                    if (args.Length == 2 && args[1] == "--voice-segments")
                        return await RunVoiceSegmentsScenarioAsync(application, window, voice).ConfigureAwait(false);
                    if (args.Length == 2 && args[1] == "--visual-consent-routing")
                        return await RunVisualConsentRoutingScenarioAsync(application, window, voice,
                            submissionDelay).ConfigureAwait(false);
                    if (args.Length == 3 && args[2] == "--execution-progress")
                        return await RunExecutionProgressScenarioAsync(application, window, api, voice,
                            provider, speech, submissionDelay, args[1]).ConfigureAwait(false);
                    if (args.Length == 3)
                        return await RunIslandDragScenarioAsync(application, window, api, voice, provider,
                            speech, submissionDelay, args[1]).ConfigureAwait(false);
                    if (args.Length == 2)
                        return await RunIslandScenarioAsync(application, window, api, voice, provider,
                            speech, submissionDelay, args[1]).ConfigureAwait(false);
                    return await RunScenarioAsync(
                        application,
                        window,
                        api,
                        voice,
                        provider,
                        speech,
                        submissionDelay).ConfigureAwait(false);
                }
                finally
                {
                    shutdownOutcome.TrySetResult(args.Length == 2 && args[1] == "--startup-exit"
                        && (application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
                        || await RequestNaturalShutdownAsync(application, window).ConfigureAwait(false));
                }
            });

            Dispatcher.Run();
            evidence = scenario.WaitAsync(TimeSpan.FromSeconds(90)).GetAwaiter().GetResult();
            naturalShutdown = shutdownOutcome.Task
                .WaitAsync(TimeSpan.FromSeconds(10))
                .GetAwaiter()
                .GetResult();
            Require(!voice.IsListening, "orb_exit_kept_microphone_open");
        }
        catch (Exception exception)
        {
            failure = Unwrap(exception);
            if (args.Length >= 2) Console.Error.WriteLine(failure);
            errorCode = failure is HarnessFailureException harnessFailure
                ? harnessFailure.Code
                : "voice_harness_unexpected";
            if (application is not null
                && !application.Dispatcher.HasShutdownStarted
                && !application.Dispatcher.HasShutdownFinished)
            {
                application.Shutdown(-1);
            }
        }
        finally
        {
            try
            {
                host?.StopAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                host?.Dispose();
                tray?.Dispose();
                if (voice is not null)
                {
                    voice.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }

                cleanupCompleted = true;
            }
            catch (Exception cleanupException)
            {
                failure ??= Unwrap(cleanupException);
                errorCode = "voice_harness_cleanup_failed";
            }
        }

        stopwatch.Stop();
        if (failure is null && evidence is not null && naturalShutdown && cleanupCompleted)
        {
            if (evidence is DirectCallEvidence callEvidence)
            {
                Console.Out.WriteLine("SCREEN_GUIDE_DIRECT_CALL_RESULT " + JsonSerializer.Serialize(
                    new { stage = "completed", callEvidence, naturalShutdown, cleanupCompleted,
                        elapsedMilliseconds = stopwatch.ElapsedMilliseconds }, ResultJson));
                return 0;
            }
            if (evidence is StartupVoiceEvidence startupEvidence)
            {
                Console.Out.WriteLine("SCREEN_GUIDE_STARTUP_VOICE_RESULT " + JsonSerializer.Serialize(
                    new { stage = "completed", startupEvidence, naturalShutdown, cleanupCompleted,
                        elapsedMilliseconds = stopwatch.ElapsedMilliseconds }, ResultJson));
                return 0;
            }
            if (evidence is FirstVoiceReplyEvidence firstVoiceEvidence)
            {
                Console.Out.WriteLine("SCREEN_GUIDE_FIRST_VOICE_REPLY_RESULT " + JsonSerializer.Serialize(
                    new { stage = "completed", firstVoiceEvidence, naturalShutdown, cleanupCompleted,
                        elapsedMilliseconds = stopwatch.ElapsedMilliseconds }, ResultJson));
                return 0;
            }
            if (evidence is ExecutionProgressEvidence progressEvidence)
            {
                Console.Out.WriteLine("SCREEN_GUIDE_EXECUTION_PROGRESS_RESULT " + JsonSerializer.Serialize(
                    new { stage = "completed", progressEvidence, naturalShutdown, cleanupCompleted,
                        elapsedMilliseconds = stopwatch.ElapsedMilliseconds }, ResultJson));
                return 0;
            }
            if (evidence is VoiceSegmentsEvidence segmentsEvidence)
            {
                Console.Out.WriteLine("SCREEN_GUIDE_VOICE_SEGMENTS_RESULT " + JsonSerializer.Serialize(
                    new { stage = "completed", segmentsEvidence, naturalShutdown, cleanupCompleted,
                        elapsedMilliseconds = stopwatch.ElapsedMilliseconds }, ResultJson));
                return 0;
            }
            if (evidence is VisualConsentRoutingEvidence consentEvidence)
            {
                Console.Out.WriteLine("SCREEN_GUIDE_VISUAL_CONSENT_ROUTING_RESULT " + JsonSerializer.Serialize(
                    new { stage = "completed", consentEvidence, naturalShutdown, cleanupCompleted,
                        elapsedMilliseconds = stopwatch.ElapsedMilliseconds }, ResultJson));
                return 0;
            }
            if (evidence is IslandDragEvidence dragEvidence)
            {
                Console.Out.WriteLine("SCREEN_GUIDE_ISLAND_DRAG_RESULT " + JsonSerializer.Serialize(
                    new { stage = "completed", dragEvidence, naturalShutdown, cleanupCompleted,
                        elapsedMilliseconds = stopwatch.ElapsedMilliseconds }, ResultJson));
                return 0;
            }
            if (evidence is IslandHarnessEvidence islandEvidence)
            {
                Console.Out.WriteLine("SCREEN_GUIDE_ISLAND_HARNESS_RESULT " + JsonSerializer.Serialize(
                    new { stage = "completed", islandEvidence, naturalShutdown, cleanupCompleted,
                        elapsedMilliseconds = stopwatch.ElapsedMilliseconds }, ResultJson));
                return 0;
            }
            var result = HarnessResult.Completed(
                (HarnessEvidence)evidence,
                entryAssemblyConfirmed,
                resourceAssemblyConfirmed,
                stopwatch.ElapsedMilliseconds);
            Console.Out.WriteLine(ResultPrefix + JsonSerializer.Serialize(result, ResultJson));
            return 0;
        }

        var failed = HarnessResult.Failed(
            entryAssemblyConfirmed,
            resourceAssemblyConfirmed,
            naturalShutdown && cleanupCompleted,
            stopwatch.ElapsedMilliseconds,
            errorCode,
            failure?.GetType().Name ?? "HarnessFailureException");
        Console.Out.WriteLine(ResultPrefix + JsonSerializer.Serialize(failed, ResultJson));
        return 1;
    }

    private static async Task<HarnessEvidence> RunScenarioAsync(
        Application application,
        MainWindow window,
        DesktopApiClient api,
        ControlledVoiceListener voice,
        RecordingChatProvider provider,
        ControlledSpeechOutput speech,
        DelayedSubmissionApi submissionDelay)
    {
        var showOrb = await application.Dispatcher.InvokeAsync(() => window.ShowAssistantAsync(startListening: false))
            .Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await showOrb.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var orbHandle = await application.Dispatcher.InvokeAsync(() =>
        {
            var orb = application.Windows.Cast<Window>().Single(item => item.GetType().Name == "AssistantOrbWindow");
            Require(orb.IsVisible && !window.IsVisible, "paused_orb_not_primary_surface");
            return new WindowInteropHelper(orb).Handle;
        }).Task.ConfigureAwait(false);
        var orbRoot = AutomationElement.FromHandle(orbHandle);
        Require(orbRoot.Current.AutomationId == "AssistantOrb", "orb_automation_identity_missing");
        Require(!voice.IsListening && voice.StartCalls == 0, "paused_orb_started_without_consent");
        await VerifyEmptyProfileOperationAsync(application, window, api, submissionDelay).ConfigureAwait(false);
        var orbToggle = await WaitForElementByAutomationIdAsync(orbRoot, "OrbVoiceToggle",
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        ((InvokePattern)orbToggle.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await voice.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Require(voice.StartCalls == 1, "orb_toggle_started_multiple_listeners");
        var dashboard = await WaitForElementByAutomationIdAsync(orbRoot, "OrbDashboard",
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        ((InvokePattern)dashboard.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        var windowHandle = IntPtr.Zero;
        var showDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (windowHandle == IntPtr.Zero && DateTimeOffset.UtcNow < showDeadline)
        {
            windowHandle = await application.Dispatcher
                .InvokeAsync(() => window.IsVisible ? new WindowInteropHelper(window).Handle : IntPtr.Zero)
                .Task.ConfigureAwait(false);
            if (windowHandle == IntPtr.Zero) await Task.Delay(30).ConfigureAwait(false);
        }
        Require(windowHandle != IntPtr.Zero, "window_handle_missing");
        var automationRoot = AutomationElement.FromHandle(windowHandle);
        Require(automationRoot is not null, "automation_root_missing");
        Require(voice.IsListening && voice.StartCalls == 1, "dashboard_open_restarted_voice");
        var startVoice = await WaitForElementByAutomationIdAsync(automationRoot!, "StartVoice",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        voice.Recognize(OldInput);
        var oldTurnId = await provider.BlockingTurnStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        var respondingSnapshot = await WaitForSessionAsync(
            api,
            snapshot => snapshot.Turns.Any(turn =>
                turn.Id == oldTurnId && turn.Phase == "Responding"),
            TimeSpan.FromSeconds(15),
            "old_turn_not_responding").ConfigureAwait(false);
        var oldTurn = respondingSnapshot.Turns.Single(turn => turn.Id == oldTurnId);
        var uiOldInput = await WaitForElementByNameAsync(
            automationRoot!,
            OldInput,
            ControlType.Text,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await WaitForAutomationTextAsync(
            automationRoot!,
            "SessionStatus",
            "正在回答，你可以随时插话",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var stop = await WaitForElementByAutomationIdAsync(
            automationRoot!,
            "StopSession",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var uiShowedStop = stop.Current.IsEnabled && !stop.Current.IsOffscreen;
        Require(uiShowedStop, "stop_state_not_visible");

        voice.Recognize(NewInput);
        var cancellationTurnId = await provider.CancellationObserved.Task
            .WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        provider.AllowLateReply.TrySetResult();
        var lateReplyTurnId = await provider.LateReplyReturned.Task
            .WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        var finalSnapshot = await WaitForSessionAsync(
            api,
            snapshot =>
            {
                var prior = snapshot.Turns.SingleOrDefault(turn => turn.Id == oldTurnId);
                var replacement = snapshot.Turns.SingleOrDefault(turn =>
                    turn.Id != oldTurnId && turn.InputText == NewInput);
                return prior?.Phase == "Cancelled" && replacement?.Phase == "Completed";
            },
            TimeSpan.FromSeconds(20),
            "replacement_turn_not_completed").ConfigureAwait(false);
        var finalOldTurn = finalSnapshot.Turns.Single(turn => turn.Id == oldTurnId);
        var newTurn = finalSnapshot.Turns.Single(turn =>
            turn.Id != oldTurnId && turn.InputText == NewInput);
        var newReply = finalSnapshot.Messages.Single(message =>
            message.Role == "Assistant" && message.Content == RecordingChatProvider.NewReply);
        var newInputPersisted = finalSnapshot.Messages.Any(message =>
            message.Role == "User" && message.Content == NewInput);
        var lateReplyRejected = finalSnapshot.Messages.All(message =>
            !message.Content.Contains(RecordingChatProvider.LateReply, StringComparison.Ordinal));
        var uiNewInput = await WaitForElementByNameAsync(
            automationRoot!,
            NewInput,
            ControlType.Text,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var uiNewReply = await WaitForElementByNameAsync(
            automationRoot!,
            newReply.Content,
            ControlType.Text,
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var uiRejectedLateReply = !ContainsAutomationName(
            automationRoot!,
            RecordingChatProvider.LateReply);

        Require(oldTurn.InputModality == "Voice", "old_input_not_voice");
        Require(newTurn.InputModality == "Voice", "new_input_not_voice");
        Require(newTurn.Id != oldTurnId, "session_turn_not_replaced");
        Require(cancellationTurnId == oldTurnId, "cancellation_turn_mismatch");
        Require(lateReplyTurnId == oldTurnId, "late_reply_turn_mismatch");
        Require(newInputPersisted, "new_input_not_persisted");
        Require(lateReplyRejected, "late_reply_persisted");
        Require(uiRejectedLateReply, "late_reply_visible");
        await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Require(speech.Texts.SequenceEqual([RecordingChatProvider.NewReply]), "automatic_voice_reply_missing_or_wrong");
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("RenderSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [finalSnapshot])).Task.ConfigureAwait(false);
        Require(speech.Texts.Count == 1, "refresh_repeated_voice_reply");
        voice.Recognize(ThirdInput);
        await speech.FirstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await speech.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        speech.AllowFirstFinish.TrySetResult();
        await speech.FirstReturned.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() => { }).Task.ConfigureAwait(false);
        Require(!speech.SecondToken.IsCancellationRequested, "old_speech_completion_cancelled_new_speech");

        var stopVoice = await WaitForElementByAutomationIdAsync(automationRoot!, "StopVoice",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        ((InvokePattern)stopVoice.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await WaitForAutomationTextAsync(automationRoot!, "VoiceState", "语音已停止",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        ((InvokePattern)startVoice.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await WaitForAutomationTextAsync(automationRoot!, "VoiceState", "正在聆听",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        submissionDelay.ResetReceiptHold();
        submissionDelay.DelayNextVoice = true;
        voice.Recognize(StoppedReceiptInput);
        await submissionDelay.ReceiptHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var stoppedReceiptTurnId = await provider.StoppedReceiptTurnStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        ((InvokePattern)stopVoice.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await WaitForAutomationTextAsync(automationRoot!, "VoiceState", "语音已停止",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await WaitForElementByNameAsync(automationRoot!, "正在取消上一条请求；收到回执后才能确认停止。",
            ControlType.Text, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        submissionDelay.ReleaseReceipt.TrySetResult();
        await WaitForSessionAsync(api,
            snapshot => snapshot.Turns.Any(turn => turn.Id == stoppedReceiptTurnId && turn.Phase == "Cancelled"),
            TimeSpan.FromSeconds(10), "stopped_late_receipt_turn_kept_running").ConfigureAwait(false);
        Require(submissionDelay.CancelledTurns.Contains(stoppedReceiptTurnId), "late_receipt_exact_turn_not_cancelled");
        Require(submissionDelay.SubmittedTexts.Count(text => text == StoppedReceiptInput) == 1,
            "stopped_voice_submission_was_replayed");
        Require(speech.Texts.Count == 2, "stopped_late_receipt_rearmed_speech");
        await WaitForElementByNameAsync(automationRoot!, "上一条请求已取消。",
            ControlType.Text, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() =>
        {
            var status = (System.Windows.Controls.TextBlock)window.FindName("StatusBarText");
            status.Text = "新操作状态不得被旧取消回执覆盖";
            typeof(MainWindow).GetMethod("RefreshVoiceCancellationStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            Require(status.Text == "新操作状态不得被旧取消回执覆盖", "old_cancellation_overwrote_new_status");
        }).Task.ConfigureAwait(false);
        ((InvokePattern)startVoice.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await WaitForAutomationTextAsync(automationRoot!, "VoiceState", "正在聆听",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        submissionDelay.ResetReceiptHold();
        submissionDelay.DelayNextVoice = true;
        voice.Recognize(DelayedInput);
        await submissionDelay.ReceiptHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        voice.Recognize(QueuedInput);
        var topic = await WaitForElementByAutomationIdAsync(automationRoot!, "NewTopic",
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var topicCallsBeforeQueuedCancellation = submissionDelay.NewTopicCalls;
        ((InvokePattern)topic.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await Task.Delay(150).ConfigureAwait(false);
        Require(submissionDelay.NewTopicCalls == topicCallsBeforeQueuedCancellation,
            "new_topic_sent_before_previous_voice_receipt");
        submissionDelay.ReleaseReceipt.TrySetResult();
        await WaitForSessionAsync(api, item => item.SessionId != finalSnapshot.SessionId,
            TimeSpan.FromSeconds(10), "new_topic_not_created").ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() => { }).Task.ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false);
        Require(!submissionDelay.SubmittedTexts.Contains(QueuedInput), "old_queued_voice_crossed_new_topic");
        Require(speech.Texts.Count == 2, "old_receipt_rearmed_automatic_speech");
        ((InvokePattern)stopVoice.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await WaitForAutomationTextAsync(automationRoot!, "VoiceState", "语音已停止",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Require(!voice.IsListening, "voice_kept_listening_after_stop");
        await speech.SecondCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Require(speech.Texts.SequenceEqual([RecordingChatProvider.NewReply, RecordingChatProvider.ThirdReply]),
            "voice_reply_replayed_or_old_result_spoken");
        // Exercise the real dashboard refresh that previously replaced stopped/faulted state.
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("RenderVoiceStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)).Task.ConfigureAwait(false);
        await WaitForAutomationTextAsync(automationRoot!, "VoiceState", "语音已停止",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        ((InvokePattern)startVoice.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await WaitForAutomationTextAsync(automationRoot!, "VoiceState", "正在聆听",
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await VerifyCrossEntryRegistrationBarriersAsync(application, window, api, voice, submissionDelay)
            .ConfigureAwait(false);
        await VerifyNonVoiceRegistrationBarriersAsync(application, window, api, submissionDelay).ConfigureAwait(false);
        await VerifyMultiRpcOperationBoundariesAsync(application, window, api, submissionDelay).ConfigureAwait(false);
        await VerifyPendingCreationHandoffAsync(application, window, api, voice, submissionDelay).ConfigureAwait(false);
        await VerifyManualCancellationRecoveryAsync(application, window, api, submissionDelay).ConfigureAwait(false);
        await VerifyLocalStandbyAsync(application, window, api, voice, provider, speech, submissionDelay).ConfigureAwait(false);
        await VerifyLateSpeechResetFailureAsync(application, window, voice, speech).ConfigureAwait(false);
        await VerifyMemoryNavigationAsync(application, window, api, provider, submissionDelay).ConfigureAwait(false);
        var startsBeforeSurfaceSwitch = voice.StartCalls;
        await application.Dispatcher.InvokeAsync(window.Close).Task.ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() =>
        {
            var orb = application.Windows.Cast<Window>().Single(item => item.GetType().Name == "AssistantOrbWindow");
            Require(!window.IsVisible && orb.IsVisible && voice.IsListening, "management_close_stopped_or_hid_assistant");
            window.ShowAssistantSurface();
            Require(!window.IsVisible && orb.IsVisible, "assistant_surface_reopened_management");
        }).Task.ConfigureAwait(false);
        var showAgain = await application.Dispatcher.InvokeAsync(() => window.ShowAssistantAsync(startListening: true)).Task.ConfigureAwait(false);
        await showAgain.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() =>
        {
            window.OpenDashboardFromTray();
            Require(window.IsVisible, "dashboard_did_not_reopen");
            window.Close();
            Require(!window.IsVisible, "management_close_did_not_return_to_orb");
        }).Task.ConfigureAwait(false);
        Require(voice.IsListening && voice.StartCalls == startsBeforeSurfaceSwitch, "surface_switch_restarted_listener");

        return new HarnessEvidence(
            finalOldTurn.Phase,
            newTurn.Phase,
            FirstInputVoice: true,
            SecondInputVoice: true,
            DistinctSessionTurns: true,
            OldTurnResponding: true,
            UiShowedOldInput: uiOldInput is not null,
            UiShowedStop: uiShowedStop,
            CancellationObserved: true,
            CancellationTurnMatched: true,
            LateReplyReturned: true,
            NewInputPersisted: newInputPersisted,
            NewReplyPersisted: newReply is not null,
            LateReplyRejected: lateReplyRejected,
            UiShowedNewInput: uiNewInput is not null,
            UiShowedNewReply: uiNewReply is not null,
            UiRejectedLateReply: uiRejectedLateReply,
            Orb: new OrbHarnessEvidence(true, true, true, true, true));
    }

    private static async Task VerifyCrossEntryRegistrationBarriersAsync(
        Application application, MainWindow window, DesktopApiClient api,
        ControlledVoiceListener voice, DelayedSubmissionApi delay)
    {
        var originalSession = await api.GetCurrentSessionAsync().ConfigureAwait(false);
        delay.ResetReceiptHold();
        delay.DelayBeforeRegistration = true;
        delay.DelayNextVoice = true;
        voice.Recognize(BeforeTextRegistrationInput);
        await delay.ReceiptHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() =>
        {
            ((System.Windows.Controls.TextBox)window.FindName("ConversationInputTextBox")).Text = ReplacementTextInput;
            typeof(MainWindow).GetMethod("SendConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
        }).Task.ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false);
        Require(!delay.SubmittedTexts.Contains(ReplacementTextInput), "text_rpc_overtook_unregistered_voice");
        delay.ReleaseReceipt.TrySetResult();
        var afterText = await WaitForSessionAsync(api, snapshot =>
            snapshot.SessionId == originalSession!.SessionId
            && snapshot.Turns.Any(turn => turn.InputText == BeforeTextRegistrationInput && turn.Phase == "Cancelled")
            && snapshot.Turns.Any(turn => turn.InputText == ReplacementTextInput && turn.Phase == "Completed"),
            TimeSpan.FromSeconds(15), "text_replacement_did_not_survive_late_voice_registration").ConfigureAwait(false);
        Require(delay.SubmittedTexts.Count(text => text == ReplacementTextInput) == 1, "text_replacement_replayed");

        delay.ResetReceiptHold();
        delay.DelayBeforeRegistration = true;
        delay.DelayNextVoice = true;
        voice.Recognize(BeforeTopicRegistrationInput);
        await delay.ReceiptHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var topicCalls = delay.NewTopicCalls;
        await application.Dispatcher.InvokeAsync(() =>
        {
            ((System.Windows.Controls.TextBox)window.FindName("ConversationInputTextBox")).Text = SupersededTextInput;
            typeof(MainWindow).GetMethod("SendConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
        }).Task.ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("NewConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()])).Task.ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false);
        Require(delay.NewTopicCalls == topicCalls, "new_topic_rpc_overtook_unregistered_voice");
        delay.ReleaseReceipt.TrySetResult();
        var newTopic = await WaitForSessionAsync(api, snapshot => snapshot.SessionId != afterText.SessionId,
            TimeSpan.FromSeconds(15), "new_topic_waiting_for_voice_never_started").ConfigureAwait(false);
        Require(delay.HeldReceipt is { } held && delay.CancelledTurns.Contains(held.TurnId),
            "old_voice_not_cancelled_before_new_topic");
        await Task.Delay(150).ConfigureAwait(false);
        Require((await api.GetCurrentSessionAsync().ConfigureAwait(false))?.SessionId == newTopic.SessionId,
            "late_voice_switched_back_to_old_topic");
        Require(delay.SubmittedTexts.Count(text => text == BeforeTopicRegistrationInput) == 1,
            "old_topic_voice_was_replayed");
        Require(!delay.SubmittedTexts.Contains(SupersededTextInput), "superseded_text_ran_after_new_topic");

        delay.ResetReceiptHold();
        delay.DelayBeforeRegistration = true;
        delay.DelayNextVoice = true;
        voice.Recognize(BeforeStoppedTopicRegistrationInput);
        await delay.ReceiptHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        topicCalls = delay.NewTopicCalls;
        await application.Dispatcher.InvokeAsync(() =>
        {
            typeof(MainWindow).GetMethod("NewConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            typeof(MainWindow).GetMethod("StopVoiceButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
        }).Task.ConfigureAwait(false);
        delay.ReleaseReceipt.TrySetResult();
        await WaitForSessionAsync(api, snapshot => snapshot.SessionId == newTopic.SessionId
            && snapshot.Turns.Any(turn => turn.InputText == BeforeStoppedTopicRegistrationInput && turn.Phase == "Cancelled"),
            TimeSpan.FromSeconds(15), "stopped_queued_topic_voice_not_cancelled").ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() => { }).Task.ConfigureAwait(false);
        Require(delay.NewTopicCalls == topicCalls, "stopped_waiting_new_topic_was_sent_later");
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("StartVoiceButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()])).Task.ConfigureAwait(false);
        var listeningDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!voice.IsListening && DateTimeOffset.UtcNow < listeningDeadline) await Task.Delay(20).ConfigureAwait(false);
        Require(voice.IsListening, "voice_not_restarted_after_cross_entry_stop");
    }

    private static async Task VerifyNonVoiceRegistrationBarriersAsync(
        Application application, MainWindow window, DesktopApiClient api, DelayedSubmissionApi delay)
    {
        var original = await api.GetCurrentSessionAsync().ConfigureAwait(false);
        delay.ResetReceiptHold();
        delay.DelayNextNonVoiceBeforeRegistration = true;
        await application.Dispatcher.InvokeAsync(() =>
        {
            ((System.Windows.Controls.TextBox)window.FindName("ConversationInputTextBox")).Text = ReplacementTextInput;
            typeof(MainWindow).GetMethod("SendConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
        }).Task.ConfigureAwait(false);
        await delay.ReceiptHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var topicCalls = delay.NewTopicCalls;
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("NewConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()])).Task.ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false);
        Require(delay.NewTopicCalls == topicCalls, "new_topic_overtook_unregistered_text");
        delay.ReleaseReceipt.TrySetResult();
        var afterText = await WaitForSessionAsync(api, snapshot => snapshot.SessionId != original!.SessionId,
            TimeSpan.FromSeconds(15), "new_topic_after_text_registration_not_started").ConfigureAwait(false);
        Require(delay.HeldReceipt is { } textReceipt && delay.CancelledTurns.Contains(textReceipt.TurnId),
            "late_text_receipt_not_cancelled_before_new_topic");

        delay.ResetReceiptHold();
        delay.DelayNextNonVoiceBeforeRegistration = true;
        var selected = await application.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow)
            .GetMethod("ExecuteDesktopActionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, ["启动受控未安装应用", "OpenApplication", "voice-harness-uninstalled-app"])!).Task.ConfigureAwait(false);
        await delay.ReceiptHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        topicCalls = delay.NewTopicCalls;
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("NewConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()])).Task.ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false);
        Require(delay.NewTopicCalls == topicCalls, "new_topic_overtook_unregistered_selected_action");
        delay.ReleaseReceipt.TrySetResult();
        var final = await WaitForSessionAsync(api, snapshot => snapshot.SessionId != afterText.SessionId,
            TimeSpan.FromSeconds(15), "new_topic_after_selected_registration_not_started").ConfigureAwait(false);
        await selected.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Require(delay.HeldReceipt is { } selectedReceipt && delay.CancelledTurns.Contains(selectedReceipt.TurnId)
            && !delay.ConfirmedTurns.Contains(selectedReceipt.TurnId), "superseded_selected_registration_not_cancelled");
        Require((await api.GetCurrentSessionAsync().ConfigureAwait(false))?.SessionId == final.SessionId,
            "nonvoice_late_registration_switched_back_to_old_topic");

        delay.ResetNewSessionHold();
        delay.DelayNextNewSessionReply = true;
        delay.DelayNewSessionBeforeRegistration = true;
        const string unregisteredProgrammingInput = "VOICE_HARNESS_UNREGISTERED_NEW_SESSION_PROGRAMMING";
        var fakeProject = new ProjectDto(Guid.NewGuid(), "Controlled project", "X:\\synthetic-project",
            "Authorized", true, false, "Clean", 0, false, DateTimeOffset.UtcNow);
        var creating = await application.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow)
            .GetMethod("CreateTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [fakeProject, unregisteredProgrammingInput, "尚未登记的旧话题"])!).Task.ConfigureAwait(false);
        await delay.NewSessionReplyHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        topicCalls = delay.NewTopicCalls;
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("NewConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()])).Task.ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false);
        Require(delay.NewTopicCalls == topicCalls, "new_topic_overtook_unregistered_session_creation");
        delay.ReleaseNewSessionReply.TrySetResult();
        await WaitForSessionAsync(api, snapshot => snapshot.SessionId != final.SessionId && snapshot.Title == "新话题",
            TimeSpan.FromSeconds(15), "new_topic_after_session_registration_not_started").ConfigureAwait(false);
        await creating.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Require(!delay.SubmittedTexts.Contains(unregisteredProgrammingInput), "superseded_session_creation_submitted_input");
        delay.ResetNewSessionHold();
    }

    private static async Task VerifyPendingCreationHandoffAsync(
        Application application, MainWindow window, DesktopApiClient api, ControlledVoiceListener voice, DelayedSubmissionApi delay)
    {
        foreach (var input in new[] { HandoffTextInput, HandoffVoiceInput })
        {
            delay.ResetNewSessionHold();
            delay.DelayNextNewSessionReply = true;
            delay.DelayNewSessionBeforeRegistration = true;
            delay.HoldNewSessionAfterRegistration = true;
            var fakeProject = new ProjectDto(Guid.NewGuid(), "Controlled project", "X:\\synthetic-project",
                "Authorized", true, false, "Clean", 0, false, DateTimeOffset.UtcNow);
            var oldInput = "NEVER_SUBMIT_" + input;
            var creating = await application.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow)
                .GetMethod("CreateTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [fakeProject, oldInput, "待交接的话题"])!).Task.ConfigureAwait(false);
            await delay.NewSessionReplyHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (input == HandoffVoiceInput) voice.Recognize(input);
            else await application.Dispatcher.InvokeAsync(() =>
            {
                ((System.Windows.Controls.TextBox)window.FindName("ConversationInputTextBox")).Text = input;
                typeof(MainWindow).GetMethod("SendConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window, new RoutedEventArgs()]);
            }).Task.ConfigureAwait(false);
            await application.Dispatcher.InvokeAsync(() => { }).Task.ConfigureAwait(false);
            delay.ReleaseNewSessionReply.TrySetResult();
            await delay.NewSessionRegistered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
                .GetMethod("RenderSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [delay.HeldNewSession])).Task.ConfigureAwait(false);
            delay.ReleaseRegisteredSession.TrySetResult();
            await WaitForSessionAsync(api, snapshot => snapshot.SessionId == delay.HeldNewSession!.SessionId
                && snapshot.Turns.Any(turn => turn.InputText == input && turn.Phase == "Completed"),
                TimeSpan.FromSeconds(15), "new_input_lost_during_topic_registration_handoff").ConfigureAwait(false);
            await creating.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Require(delay.SubmittedTexts.Count(text => text == input) == 1, "topic_handoff_replayed_new_input");
            Require(!delay.SubmittedTexts.Contains(oldInput), "topic_handoff_continued_superseded_programming");
        }
        delay.ResetNewSessionHold();
    }

    private static async Task VerifyManualCancellationRecoveryAsync(
        Application application, MainWindow window, DesktopApiClient api, DelayedSubmissionApi delay)
    {
        delay.ResetReceiptHold();
        delay.DelayNextNonVoiceReceipt = true;
        await application.Dispatcher.InvokeAsync(() =>
        {
            ((System.Windows.Controls.TextBox)window.FindName("ConversationInputTextBox")).Text = ManualRetryInput;
            typeof(MainWindow).GetMethod("SendConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
        }).Task.ConfigureAwait(false);
        await delay.ReceiptHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var held = delay.HeldReceipt!;
        var original = await WaitForSessionAsync(api, snapshot => snapshot.Turns.Any(turn => turn.Id == held.TurnId
            && turn.Phase == "Responding"), TimeSpan.FromSeconds(10), "manual_retry_turn_not_responding").ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("RenderSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [original])).Task.ConfigureAwait(false);
        delay.FailNextCancellation = true;
        var topicCalls = delay.NewTopicCalls;
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("NewConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()])).Task.ConfigureAwait(false);
        delay.ReleaseReceipt.TrySetResult();
        await delay.FailedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false);
        Require(delay.NewTopicCalls == topicCalls, "failed_cancellation_did_not_block_new_topic");
        await application.Dispatcher.InvokeAsync(() =>
        {
            var registration = (IInputRegistrationBoundary)typeof(MainWindow)
                .GetField("_latestInputRegistration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var cancelled = original with { Turns = original.Turns.Select(turn => turn.Id == held.TurnId
                ? turn with { Phase = "Cancelled" } : turn).ToArray() };
            typeof(MainWindow).GetMethod("ReconcileInputCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [cancelled with { CoordinatorInstanceId = "unrelated-host" }]);
            Require(!registration.HasConfirmedCancellation, "other_host_unblocked_failed_cancellation");
            typeof(MainWindow).GetMethod("ReconcileInputCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [cancelled with { SessionId = Guid.NewGuid() }]);
            Require(!registration.HasConfirmedCancellation, "other_session_unblocked_failed_cancellation");
        }).Task.ConfigureAwait(false);
        var stopped = await application.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow)
            .GetMethod("CancelCurrentSessionTurnAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!).Task.ConfigureAwait(false);
        await stopped.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("NewConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()])).Task.ConfigureAwait(false);
        await WaitForSessionAsync(api, snapshot => snapshot.SessionId != original.SessionId,
            TimeSpan.FromSeconds(10), "confirmed_stop_did_not_release_registration_gate").ConfigureAwait(false);
    }

    private static async Task VerifyLocalStandbyAsync(
        Application application, MainWindow window, DesktopApiClient api, ControlledVoiceListener voice,
        RecordingChatProvider provider, ControlledSpeechOutput speech, DelayedSubmissionApi delay)
    {
        var original = await api.GetCurrentSessionAsync().ConfigureAwait(false);
        var submittedBeforeRest = delay.SubmittedTexts.Count;
        var invocationsBeforeRest = provider.InvocationCount;
        var beforeRestGeneration = voice.RecognitionGeneration;
        speech.Override = async (text, cancellation) =>
        {
            if (text != "好的，我先休息。") return;
            // Decode during the farewell and again just before its completion. The
            // latter events remain queued on Dispatcher until the next raw generation.
            voice.Recognize("元枢");
            voice.Recognize("元书");
            voice.Recognize("打开我的微信");
            await Task.Delay(50, cancellation).ConfigureAwait(false);
            voice.Recognize("你好元枢");
        };
        voice.Recognize("先休息");
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Standby,
            "explicit_rest_did_not_enter_local_standby").ConfigureAwait(false);
        Require(voice.IsListening, "standby_closed_instead_of_waiting_for_local_wake");
        Require(delay.SubmittedTexts.Count == submittedBeforeRest, "rest_phrase_was_sent_to_host");
        Require(speech.Texts.Contains("好的，我先休息。"), "standby_goodbye_not_spoken");
        voice.Recognize("打开我的微信");
        voice.Recognize("这首歌里提到了元枢");
        voice.Recognize("元枢打开微信");
        voice.RecognizeAt("你好元枢", beforeRestGeneration);
        voice.RecognizeAt("你好袁叔", beforeRestGeneration);
        voice.Recognize("元素");
        voice.Recognize("你好元素");
        voice.Recognize("元书打开微信");
        voice.Recognize("元枢好");
        await Task.Delay(150).ConfigureAwait(false);
        Require(delay.SubmittedTexts.Count == submittedBeforeRest, "standby_submitted_ordinary_speech");
        Require(provider.InvocationCount == invocationsBeforeRest, "standby_called_model_provider");
        Require((await api.GetCurrentSessionAsync().ConfigureAwait(false))?.SessionId == original!.SessionId,
            "standby_created_another_session");
        await application.Dispatcher.InvokeAsync(() =>
        {
            Require(((System.Windows.Controls.TextBlock)window.FindName("VoiceTranscriptText")).Text == "只在本机等待唤醒词…",
                "standby_displayed_ignored_partial_speech");
            Require(((System.Windows.Controls.TextBlock)window.FindName("VoiceStateText")).Text == "本地待命 · 说“元枢”唤醒",
                "standby_status_not_visible");
        }).Task.ConfigureAwait(false);

        speech.Override = (_, _) => Task.CompletedTask;
        var standbyGeneration = voice.RecognitionGeneration;
        voice.RecognizeAt("你好袁叔", standbyGeneration);
        voice.RecognizeAt("你好袁叔", standbyGeneration); // The same wake segment must never become a Host input.
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "exact_wake_did_not_resume").ConfigureAwait(false);
        Require(speech.Texts.Contains("我在，请说。"), "wake_acknowledgement_not_spoken");
        Require(delay.SubmittedTexts.Count == submittedBeforeRest, "wake_phrase_was_sent_to_host");
        voice.RecognizeAt("退下", standbyGeneration);
        voice.RecognizeAt("不应提交的旧音频", beforeRestGeneration);
        await Task.Delay(100).ConfigureAwait(false);
        Require(delay.SubmittedTexts.Count == submittedBeforeRest, "old_recognition_survived_mode_switch");
        voice.Recognize(AfterWakeInput);
        var afterWake = await WaitForSessionAsync(api, session => session.Turns.Any(turn =>
            turn.InputText == AfterWakeInput && turn.Phase == "Completed"), TimeSpan.FromSeconds(10),
            "awake_command_did_not_reach_session_coordinator").ConfigureAwait(false);
        Require(afterWake.SessionId == original!.SessionId && delay.SubmittedTexts.Count(text => text == AfterWakeInput) == 1,
            "wake_recreated_topic_or_replayed_command");
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "awake_reply_left_mode_transitioning").ConfigureAwait(false);

        // A stopped local reply deliberately returns late. A new explicit start
        // must stay in direct mode; the old completion cannot rearm standby.
        var farewellStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFarewell = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        speech.Override = async (text, _) =>
        {
            if (text != "好的，我先休息。") return;
            farewellStarted.TrySetResult();
            await releaseFarewell.Task.ConfigureAwait(false);
        };
        voice.Recognize("退下");
        await farewellStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await InvokeVoiceButtonAsync(application, window, "StopVoiceButton_Click").ConfigureAwait(false);
        await WaitForVoiceStoppedAsync(application, window, voice).ConfigureAwait(false);
        await InvokeVoiceButtonAsync(application, window, "StartVoiceButton_Click").ConfigureAwait(false);
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "restart_did_not_restore_direct_mode").ConfigureAwait(false);
        releaseFarewell.TrySetResult();
        await Task.Delay(100).ConfigureAwait(false);
        Require(voice.IsListening, "old_goodbye_stopped_new_microphone_session");
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "old_goodbye_rearmed_standby").ConfigureAwait(false);

        speech.Override = (_, _) => Task.CompletedTask;
        voice.HoldNextReset = true;
        voice.Recognize(SpeechResetInput);
        await voice.ResetHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var external = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
            SnapshotReplacementInput, SessionId: original.SessionId)).ConfigureAwait(false);
        var replacement = await WaitForSessionAsync(api, session => session.Turns.Any(turn =>
            turn.Id == external.TurnId && turn.Phase == "Completed"), TimeSpan.FromSeconds(10),
            "snapshot_replacement_not_completed").ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("RenderSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [replacement])).Task.ConfigureAwait(false);
        voice.ReleaseReset.TrySetResult();
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "revoked_tts_reset_stranded_reception").ConfigureAwait(false);
        voice.Recognize(AfterSpeechResetInput);
        await WaitForSessionAsync(api, session => session.Turns.Any(turn => turn.InputText == AfterSpeechResetInput
            && turn.Phase == "Completed"), TimeSpan.FromSeconds(10), "voice_not_usable_after_revoked_tts_reset").ConfigureAwait(false);
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "final_reply_left_mode_transitioning").ConfigureAwait(false);

        speech.Override = async (_, cancellation) =>
        {
            voice.Recognize("先休息");
            voice.Recognize("退下");
            await Task.Delay(50, cancellation).ConfigureAwait(false);
        };
        await InvokeSpeechAsync(application, window, "先休息、退下，这是语音指令说明。").ConfigureAwait(false);
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "tts_echo_triggered_local_rest").ConfigureAwait(false);

        // Enter standby directly through a fresh user command after an unrelated
        // spoken sentence, then test manual-playback wake echoes in standby.
        speech.Override = (_, _) => Task.CompletedTask;
        await InvokeSpeechAsync(application, window, "下面验证本地接收。").ConfigureAwait(false);
        voice.Recognize("退下");
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Standby, "second_rest_failed").ConfigureAwait(false);
        var beforeEcho = delay.SubmittedTexts.Count;
        speech.Override = async (_, cancellation) =>
        {
            voice.Recognize("元枢");
            voice.Recognize("元书");
            await Task.Delay(50, cancellation).ConfigureAwait(false);
        };
        await InvokeSpeechAsync(application, window, "元枢").ConfigureAwait(false);
        voice.Recognize("元书"); // Also reject a homophone echo after playback returns to standby.
        await Task.Delay(100).ConfigureAwait(false);
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Standby, "tts_echo_triggered_local_wake").ConfigureAwait(false);
        Require(delay.SubmittedTexts.Count == beforeEcho, "local_controls_reached_host");
        speech.Override = (_, _) => Task.CompletedTask;
        await InvokeSpeechAsync(application, window, "接下来继续测试。").ConfigureAwait(false);
        voice.Recognize("元书");
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "short_homophone_wake_did_not_resume").ConfigureAwait(false);
        Require(delay.SubmittedTexts.Count == beforeEcho, "short_wake_created_host_turn");
        await application.Dispatcher.InvokeAsync(() =>
        {
            var orb = application.Windows.Cast<Window>().Single(item => item.GetType().Name == "AssistantOrbWindow");
            var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(orb)!, 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            orb.RaiseEvent(key);
        }).Task.ConfigureAwait(false);
        await WaitForVoiceStoppedAsync(application, window, voice).ConfigureAwait(false);
        voice.RecognizeAt("元枢", voice.RecognitionGeneration);
        voice.RecognizeAt("元书", voice.RecognitionGeneration);
        await Task.Delay(100).ConfigureAwait(false);
        Require(!voice.IsListening, "late_wake_reopened_stopped_microphone");
        speech.Override = (_, _) => Task.CompletedTask;
        await InvokeVoiceButtonAsync(application, window, "StartVoiceButton_Click").ConfigureAwait(false);
        await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct, "explicit_start_not_direct").ConfigureAwait(false);
    }

    private static async Task VerifyLateSpeechResetFailureAsync(
        Application application, MainWindow window, ControlledVoiceListener voice, ControlledSpeechOutput speech)
    {
        const string oldReply = "这段播报用于验证旧收尾异常。";
        var resetHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ioFailureRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        speech.Override = (text, _) =>
        {
            if (text == oldReply)
            {
                // Arm only after the initial reset has finished and playback has begun.
                // The following reset therefore belongs to this TTS's finally block.
                voice.SetNextResetHook(async () =>
                {
                    resetHeld.TrySetResult();
                    await releaseReset.Task.ConfigureAwait(false);
                    // Native cleanup can fail independently of the cancelled playback.
                    // Deliberately do not turn this delayed failure into cancellation.
                    ioFailureRaised.TrySetResult();
                    throw new IOException("Controlled stale TTS cleanup failure.");
                });
            }
            return Task.CompletedTask;
        };

        var oldPlayback = InvokeSpeechAsync(application, window, oldReply);
        try
        {
            await resetHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await InvokeVoiceButtonAsync(application, window, "StopVoiceButton_Click").ConfigureAwait(false);
            await WaitForVoiceStoppedAsync(application, window, voice).ConfigureAwait(false);
            await InvokeVoiceButtonAsync(application, window, "StartVoiceButton_Click").ConfigureAwait(false);
            await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct,
                "late_reset_fixture_did_not_restart_voice").ConfigureAwait(false);
            var currentStartCalls = voice.StartCalls;
            var currentStopCalls = voice.StopCalls;
            var currentGeneration = voice.RecognitionGeneration;
            var currentUi = await application.Dispatcher.InvokeAsync(() => (
                ErrorVisibility: ((FrameworkElement)window.FindName("ErrorBanner")).Visibility,
                ErrorMessage: ((System.Windows.Controls.TextBlock)window.FindName("ErrorMessageText")).Text,
                ErrorDetail: ((System.Windows.Controls.TextBlock)window.FindName("ErrorTechnicalText")).Text,
                VoiceState: ((System.Windows.Controls.TextBlock)window.FindName("VoiceStateText")).Text,
                ListeningGeneration: ((VoiceSessionController)typeof(MainWindow)
                    .GetField("_voiceSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!).ListeningGeneration)).Task.ConfigureAwait(false);

            releaseReset.TrySetResult();
            await ioFailureRaised.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await oldPlayback.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Require(voice.IsListening, "stale_tts_reset_failure_stopped_new_microphone");
            Require(voice.StopCalls == currentStopCalls, "stale_tts_reset_failure_called_stop_on_new_session");
            Require(voice.StartCalls == currentStartCalls && voice.RecognitionGeneration == currentGeneration,
                "stale_tts_reset_failure_recreated_new_microphone");
            await application.Dispatcher.InvokeAsync(() =>
            {
                var controller = (VoiceSessionController)typeof(MainWindow)
                    .GetField("_voiceSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Require(controller.ListeningGeneration == currentUi.ListeningGeneration,
                    "stale_tts_reset_failure_changed_new_listening_generation");
                Require(((FrameworkElement)window.FindName("ErrorBanner")).Visibility == currentUi.ErrorVisibility
                    && ((System.Windows.Controls.TextBlock)window.FindName("ErrorMessageText")).Text == currentUi.ErrorMessage
                    && ((System.Windows.Controls.TextBlock)window.FindName("ErrorTechnicalText")).Text == currentUi.ErrorDetail,
                    "stale_tts_reset_failure_overwrote_new_error_state");
                Require(((System.Windows.Controls.TextBlock)window.FindName("VoiceStateText")).Text == currentUi.VoiceState,
                    "stale_tts_reset_failure_overwrote_new_voice_status");
            }).Task.ConfigureAwait(false);
            await WaitForReceptionAsync(application, window, VoiceReceptionMode.Direct,
                "stale_tts_reset_failure_changed_new_reception_mode").ConfigureAwait(false);
        }
        finally
        {
            releaseReset.TrySetResult();
            speech.Override = (_, _) => Task.CompletedTask;
            // Observe completion even when a regression assertion fails; no old playback
            // or held reset may leak into a later harness scenario or teardown.
            try { await oldPlayback.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch { }
        }
    }

    private static async Task WaitForReceptionAsync(Application application, MainWindow window,
        VoiceReceptionMode expected, string error)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var matched = await application.Dispatcher.InvokeAsync(() =>
                ((VoiceReceptionGate)typeof(MainWindow).GetField("_voiceReception", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!).Mode == expected
                && ((VoiceSessionController)typeof(MainWindow).GetField("_voiceSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!).ListeningGeneration is not null).Task.ConfigureAwait(false);
            if (matched) return;
            await Task.Delay(30).ConfigureAwait(false);
        }
        throw new HarnessFailureException(error);
    }

    private static async Task InvokeVoiceButtonAsync(Application application, MainWindow window, string method) =>
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()])).Task.ConfigureAwait(false);

    private static async Task WaitForVoiceStoppedAsync(
        Application application, MainWindow window, ControlledVoiceListener voice)
    {
        // The real Stop button has already been invoked. Join the controller's same
        // stop task: listener.IsListening becomes false before controller cleanup is
        // complete, and Start during that interval would only join the old stop.
        var stopping = await application.Dispatcher.InvokeAsync(() =>
            ((VoiceSessionController)typeof(MainWindow)
                .GetField("_voiceSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!).StopAsync()).Task.ConfigureAwait(false);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Require(!voice.IsListening, "explicit_stop_kept_microphone_open");
    }

    private static async Task InvokeSpeechAsync(Application application, MainWindow window, string text)
    {
        var task = await application.Dispatcher.InvokeAsync(() =>
        {
            // Match the actual Read result button: consume/revoke the prior automatic
            // reply before playing this manual text, including a late Host projection.
            typeof(MainWindow).GetMethod("CancelVoiceFeedback", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            return (Task)typeof(MainWindow)
                .GetMethod("SpeakAssistantAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [text, null, false])!;
        }).Task.ConfigureAwait(false);
        await task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    private static async Task VerifyMemoryNavigationAsync(Application application, MainWindow window,
        DesktopApiClient api, RecordingChatProvider provider, DelayedSubmissionApi delay)
    {
        var prior = await api.GetCurrentSessionAsync().ConfigureAwait(false);
        var invocations = provider.InvocationCount;
        var submissions = delay.SubmittedTexts.Count;
        var handle = await application.Dispatcher.InvokeAsync(() =>
        {
            window.ShowAssistantSurface();
            return new WindowInteropHelper(application.Windows.Cast<Window>()
                .Single(item => item.GetType().Name == "AssistantOrbWindow")).Handle;
        }).Task.ConfigureAwait(false);
        var button = await WaitForElementByAutomationIdAsync(AutomationElement.FromHandle(handle), "OrbMemories",
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Require(!button.Current.IsOffscreen && button.Current.IsEnabled, "orb_memory_button_not_visible");
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task.ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() =>
        {
            var settings = (System.Windows.Controls.ScrollViewer)window.FindName("SettingsPage");
            var memory = (FrameworkElement)window.FindName("LocalMemorySection");
            Require(window.IsVisible && settings.IsVisible && memory.IsVisible, "memory_navigation_not_visible");
            var position = memory.TransformToAncestor(settings).Transform(new Point(0, 0));
            Require(position.Y < settings.ActualHeight && position.Y + memory.ActualHeight > 0,
                "memory_navigation_did_not_scroll_to_section");
        }).Task.ConfigureAwait(false);
        var current = await api.GetCurrentSessionAsync().ConfigureAwait(false);
        Require(current?.SessionId == prior!.SessionId && current.Turns.Select(turn => turn.Id).SequenceEqual(prior.Turns.Select(turn => turn.Id))
            && provider.InvocationCount == invocations && delay.SubmittedTexts.Count == submissions,
            "memory_navigation_submitted_or_changed_topic");
    }

    private static async Task VerifyEmptyProfileOperationAsync(
        Application application, MainWindow window, DesktopApiClient api, DelayedSubmissionApi delay)
    {
        Require(await api.GetCurrentSessionAsync().ConfigureAwait(false) is null, "empty_profile_was_not_empty");
        delay.DelayNextNewSessionReply = true;
        const string firstInput = "VOICE_HARNESS_FIRST_SESSION_PROGRAMMING";
        var fakeProject = new ProjectDto(Guid.NewGuid(), "Controlled project", "X:\\synthetic-project",
            "Authorized", true, false, "Clean", 0, false, DateTimeOffset.UtcNow);
        var firstOperation = await application.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow)
            .GetMethod("CreateTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [fakeProject, firstInput, "首个话题"])!).Task.ConfigureAwait(false);
        await delay.NewSessionReplyHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var firstSession = delay.HeldNewSession!;
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("RenderSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [firstSession])).Task.ConfigureAwait(false);
        delay.ReleaseNewSessionReply.TrySetResult();
        await firstOperation.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Require(delay.SubmittedTexts.Count(text => text == firstInput) == 1, "first_created_session_was_rejected");
        var firstSnapshot = await api.GetCurrentSessionAsync().ConfigureAwait(false);
        var firstTurn = firstSnapshot!.Turns.Single(turn => turn.InputText == firstInput);
        Require(delay.ProvidedProjectTurns.Contains(firstTurn.Id), "first_session_project_not_reached");
        Require(!delay.ConfirmedTurns.Contains(firstTurn.Id), "first_synthetic_project_was_confirmed");
        await api.CancelSessionTurnAsync(firstSession.SessionId, firstTurn.Id).ConfigureAwait(false);
        delay.ResetNewSessionHold();
    }

    private static async Task VerifyMultiRpcOperationBoundariesAsync(
        Application application, MainWindow window, DesktopApiClient api, DelayedSubmissionApi delay)
    {
        delay.DelayNextNewSessionReply = true;
        var fakeProject = new ProjectDto(Guid.NewGuid(), "Controlled project", "X:\\synthetic-project",
            "Authorized", true, false, "Clean", 0, false, DateTimeOffset.UtcNow);
        var oldOperation = await application.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow)
            .GetMethod("CreateTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [fakeProject, SupersededProgrammingInput, "旧编程请求"])!).Task.ConfigureAwait(false);
        await delay.NewSessionReplyHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var oldSessionId = delay.HeldNewSession!.SessionId;
        var topicCalls = delay.NewTopicCalls;
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("NewConversationButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()])).Task.ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false);
        Require(delay.NewTopicCalls == topicCalls, "new_topic_overtook_unacknowledged_session_creation");
        delay.ReleaseNewSessionReply.TrySetResult();
        var replacement = await WaitForSessionAsync(api, snapshot => snapshot.SessionId != oldSessionId,
            TimeSpan.FromSeconds(10), "replacement_topic_not_started_after_old_session_reply").ConfigureAwait(false);
        await oldOperation.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Require(!delay.SubmittedTexts.Contains(SupersededProgrammingInput), "superseded_programming_submitted_after_new_topic");
        Require((await api.GetCurrentSessionAsync().ConfigureAwait(false))?.SessionId == replacement.SessionId,
            "old_programming_reply_switched_back_to_old_session");

        // The operation's own Session can be projected before its receipt. This must
        // remain usable without accepting a Stop/newer input as a fresh generation.
        delay.ResetNewSessionHold();
        delay.DelayNextNewSessionReply = true;
        const string ownSessionInput = "VOICE_HARNESS_OWN_SESSION_PROGRAMMING";
        var ownOperation = await application.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow)
            .GetMethod("CreateTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [fakeProject, ownSessionInput, "自己的编程话题"])!).Task.ConfigureAwait(false);
        await delay.NewSessionReplyHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var ownSession = delay.HeldNewSession!;
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("RenderSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [ownSession])).Task.ConfigureAwait(false);
        delay.ReleaseNewSessionReply.TrySetResult();
        await ownOperation.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Require(delay.SubmittedTexts.Count(text => text == ownSessionInput) == 1,
            "own_created_session_was_mistaken_for_replacement");
        var ownSnapshot = await api.GetCurrentSessionAsync().ConfigureAwait(false);
        var ownTurn = ownSnapshot!.Turns.Single(turn => turn.InputText == ownSessionInput);
        Require(delay.ProvidedProjectTurns.Contains(ownTurn.Id), "own_programming_project_not_reached");
        Require(!delay.ConfirmedTurns.Contains(ownTurn.Id), "synthetic_project_was_confirmed");
        await api.CancelSessionTurnAsync(ownSession.SessionId, ownTurn.Id).ConfigureAwait(false);

        delay.ResetNewSessionHold();
        delay.DelayNextNewSessionReply = true;
        const string changedHostInput = "VOICE_HARNESS_CHANGED_COORDINATOR_PROGRAMMING";
        var changedHostOperation = await application.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow)
            .GetMethod("CreateTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [fakeProject, changedHostInput, "旧中枢回执"])!).Task.ConfigureAwait(false);
        await delay.NewSessionReplyHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        delay.NewSessionReplyOverride = delay.HeldNewSession! with { CoordinatorInstanceId = Guid.NewGuid().ToString("N") };
        delay.ReleaseNewSessionReply.TrySetResult();
        await changedHostOperation.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Require(!delay.SubmittedTexts.Contains(changedHostInput), "changed_coordinator_reply_was_adopted");
        delay.ResetNewSessionHold();

        var actionBase = await api.GetCurrentSessionAsync().ConfigureAwait(false);
        var fakeTurnId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var fakeTurn = new UnifiedSessionTurnDto(fakeTurnId, (actionBase!.Turns.LastOrDefault()?.SequenceNumber ?? 0) + 1,
            "启动受控测试应用", "Text", "DesktopAction", "WaitingForConfirmation", "None", "OpenApplication",
            null, null, null, null, null, null, true, false, null, null, now, now, null,
            "OpenApplication", SelectedActionTarget, SelectedActionTarget);
        delay.SelectedActionPlan = actionBase with { ForegroundTurn = fakeTurn, Turns = [.. actionBase.Turns, fakeTurn] };
        // Background polling before submission is not evidence that the action
        // reached its planning wait. Keep this race deterministic in the harness.
        var earlyPoll = ((IDesktopApiClient)delay).GetCurrentSessionAsync();
        Require(!delay.SelectedActionPlanHeld.Task.IsCompleted, "selected_action_plan_gate_fired_before_submission");
        await earlyPoll.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var selectedAction = await application.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow)
            .GetMethod("ExecuteDesktopActionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, ["启动受控测试应用", "OpenApplication", SelectedActionTarget])!).Task.ConfigureAwait(false);
        await delay.SelectedActionSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await delay.SelectedActionPlanHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("StopVoiceButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()])).Task.ConfigureAwait(false);
        delay.ReleaseSelectedActionPlan.TrySetResult();
        await selectedAction.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Require(!delay.ConfirmedTurns.Contains(fakeTurnId), "selected_action_auto_confirmed_after_stop");
        Require(delay.CancelledTurns.Contains(fakeTurnId), "stopped_selected_action_turn_not_cancelled");
        delay.SelectedActionPlan = null;
        await application.Dispatcher.InvokeAsync(() => typeof(MainWindow)
            .GetMethod("StartVoiceButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()])).Task.ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
    }

    private static async Task<bool> RequestNaturalShutdownAsync(
        Application application,
        MainWindow window)
    {
        try
        {
            if (application.Dispatcher.HasShutdownStarted
                || application.Dispatcher.HasShutdownFinished)
            {
                return false;
            }

            var exitTask = await application.Dispatcher
                .InvokeAsync(window.ExitApplicationAsync)
                .Task
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            // Product shutdown allows up to five seconds for capture plus five for Host.
            await exitTask.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (!application.Dispatcher.HasShutdownStarted
                && !application.Dispatcher.HasShutdownFinished)
            {
                await application.Dispatcher
                    .InvokeAsync(() => application.Dispatcher.BeginInvokeShutdown(
                        DispatcherPriority.Normal))
                    .Task
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch
        {
            if (!application.Dispatcher.HasShutdownStarted
                && !application.Dispatcher.HasShutdownFinished)
            {
                try
                {
                    await application.Dispatcher
                        .InvokeAsync(() =>
                        {
                            application.Shutdown(-1);
                            if (!application.Dispatcher.HasShutdownStarted)
                            {
                                application.Dispatcher.BeginInvokeShutdown(
                                    DispatcherPriority.Normal);
                            }
                        })
                        .Task
                        .WaitAsync(TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }

            return false;
        }
    }

    private static async Task WaitForHostAsync(DesktopApiClient api, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (await api.PingAsync().ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or DesktopApiException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new HarnessFailureException("host_not_ready");
    }

    private static async Task<SessionSnapshotDto> WaitForSessionAsync(
        DesktopApiClient api,
        Func<SessionSnapshotDto, bool> predicate,
        TimeSpan timeout,
        string failureCode)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var snapshot = await api.GetCurrentSessionAsync().ConfigureAwait(false);
            if (snapshot is not null && predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new HarnessFailureException(failureCode);
    }

    private static async Task<AutomationElement> WaitForElementByAutomationIdAsync(
        AutomationElement root,
        string automationId,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                var element = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        automationId));
                if (element is not null)
                {
                    return element;
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new HarnessFailureException("automation_element_missing");
    }

    private static async Task<AutomationElement> WaitForElementByNameAsync(
        AutomationElement root,
        string name,
        ControlType controlType,
        TimeSpan timeout)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                var element = root.FindFirst(TreeScope.Descendants, condition);
                if (element is not null)
                {
                    return element;
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new HarnessFailureException("automation_text_missing");
    }

    private static async Task WaitForAutomationTextAsync(
        AutomationElement root,
        string automationId,
        string expected,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                var element = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        automationId));
                if (element is not null
                    && string.Equals(element.Current.Name, expected, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new HarnessFailureException("automation_state_missing");
    }

    private static bool ContainsAutomationName(AutomationElement root, string expected)
    {
        try
        {
            if (root.Current.Name.Contains(expected, StringComparison.Ordinal))
            {
                return true;
            }

            return root.FindAll(
                    TreeScope.Descendants,
                    System.Windows.Automation.Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Any(element => element.Current.Name.Contains(expected, StringComparison.Ordinal));
        }
        catch (ElementNotAvailableException)
        {
            throw new HarnessFailureException("automation_tree_unavailable");
        }
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new HarnessFailureException(code);
        }
    }

    private static Exception Unwrap(Exception exception) =>
        exception is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? Unwrap(aggregate.InnerExceptions[0])
            : exception;

    private sealed class HarnessFailureException(string code) : Exception
    {
        public string Code { get; } = code;
    }

    private sealed class ControlledSpeechOutput
    {
        public Func<string, CancellationToken, Task>? Override { get; set; }
        public ConcurrentQueue<string> Texts { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFirstFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken SecondToken { get; private set; }
        public async Task SpeakAsync(string text, CancellationToken cancellationToken)
        {
            Texts.Enqueue(text);
            if (Override is { } replacement)
            {
                await replacement(text, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (text is "好的，我先休息。" or "我在，请说。")
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            Started.TrySetResult();
            var first = Texts.Count == 1;
            if (!first)
            {
                SecondToken = cancellationToken;
                SecondStarted.TrySetResult();
            }
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (first)
                {
                    FirstCancelled.TrySetResult();
                    await AllowFirstFinish.Task.ConfigureAwait(false);
                    FirstReturned.TrySetResult();
                }
                else SecondCancelled.TrySetResult();
                throw;
            }
        }
    }

    private class DelayedSubmissionApi : DispatchProxy
    {
        public TaskCompletionSource? StartupDashboardGate { get; set; }
        public TaskCompletionSource StartupDashboardRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartupDashboardRequests;
        public Action<SessionInputRequestDto>? BeforeSubmission { get; set; }
        public IDesktopApiClient Inner { get; set; } = null!;
        public bool DelayNextVoice { get; set; }
        public bool DelayBeforeRegistration { get; set; }
        public bool DelayNextNonVoiceBeforeRegistration { get; set; }
        public bool DelayNextNonVoiceReceipt { get; set; }
        public bool FailNextCancellation { get; set; }
        public TaskCompletionSource FailedCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int NewTopicCalls;
        public SessionTurnCommandResultDto? HeldReceipt { get; private set; }
        public bool DelayNextNewSessionReply { get; set; }
        public bool DelayNewSessionBeforeRegistration { get; set; }
        public bool HoldNewSessionAfterRegistration { get; set; }
        public TaskCompletionSource NewSessionRegistered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRegisteredSession { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SessionSnapshotDto? HeldNewSession { get; private set; }
        public SessionSnapshotDto? NewSessionReplyOverride { get; set; }
        public TaskCompletionSource NewSessionReplyHeld { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseNewSessionReply { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SessionSnapshotDto? SelectedActionPlan { get; set; }
        public TaskCompletionSource SelectedActionSubmitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SelectedActionPlanHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSelectedActionPlan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> SubmittedTexts { get; } = new();
        public ConcurrentQueue<Guid> CancelledTurns { get; } = new();
        public ConcurrentQueue<Guid> ConfirmedTurns { get; } = new();
        public ConcurrentQueue<Guid> WindowConsentTurns { get; } = new();
        public ConcurrentQueue<Guid> ProvidedProjectTurns { get; } = new();
        public TaskCompletionSource ReceiptHeld { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReceipt { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ResetReceiptHold()
        {
            ReceiptHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseReceipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
            HeldReceipt = null;
            DelayBeforeRegistration = false;
        }
        public void ResetNewSessionHold()
        {
            HeldNewSession = null;
            NewSessionReplyOverride = null;
            DelayNewSessionBeforeRegistration = false;
            HoldNewSessionAfterRegistration = false;
            NewSessionRegistered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseRegisteredSession = new(TaskCreationOptions.RunContinuationsAsynchronously);
            NewSessionReplyHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseNewSessionReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IDesktopApiClient.RespondSessionWindowConsentAsync) && args?[1] is Guid consentTurn)
                WindowConsentTurns.Enqueue(consentTurn);
            if (method!.Name == nameof(IDesktopApiClient.GetDashboardAsync) && StartupDashboardGate is not null)
                return HoldStartupDashboardAsync(args is [CancellationToken startupToken] ? startupToken : default);
            if (method!.Name == nameof(IDesktopApiClient.ProvideSessionProjectAsync) && args?[1] is Guid projectTurn)
                ProvidedProjectTurns.Enqueue(projectTurn);
            if (method!.Name == nameof(IDesktopApiClient.CancelSessionTurnAsync) && args?[1] is Guid turnId)
            {
                CancelledTurns.Enqueue(turnId);
                if (FailNextCancellation)
                {
                    FailNextCancellation = false;
                    FailedCancellation.TrySetResult();
                    return Task.FromException<SessionSnapshotDto>(new IOException("controlled cancellation reply failure"));
                }
                if (SelectedActionPlan?.ForegroundTurn?.Id == turnId) return Task.FromResult(SelectedActionPlan);
            }
            if (method.Name == nameof(IDesktopApiClient.ConfirmSessionTurnAsync) && args?[1] is Guid confirmedTurn)
            {
                ConfirmedTurns.Enqueue(confirmedTurn);
                if (SelectedActionPlan?.ForegroundTurn?.Id == confirmedTurn) return Task.FromResult(SelectedActionPlan);
            }
            if (method.Name == nameof(IDesktopApiClient.GetCurrentSessionAsync)
                && SelectedActionSubmitted.Task.IsCompleted && SelectedActionPlan is { } selectedPlan)
                return HoldSelectedActionPlanAsync(selectedPlan);
            if (method.Name == nameof(IDesktopApiClient.StartNewSessionAsync))
            {
                Interlocked.Increment(ref NewTopicCalls);
                if (DelayNextNewSessionReply)
                {
                    DelayNextNewSessionReply = false;
                    if (DelayNewSessionBeforeRegistration)
                        return HoldNewSessionBeforeRegistrationAsync((string?)args![0], (CancellationToken)args[1]!);
                    return HoldNewSessionAsync((Task<SessionSnapshotDto>)method.Invoke(Inner, args)!);
                }
            }
            if (method.Name == nameof(IDesktopApiClient.SubmitSessionInputAsync)
                && args?[0] is SessionInputRequestDto request)
            {
                BeforeSubmission?.Invoke(request);
                SubmittedTexts.Enqueue(request.Text);
                if (DelayNextNonVoiceReceipt && request.InputModality != "Voice")
                {
                    DelayNextNonVoiceReceipt = false;
                    return HoldAsync((Task<SessionTurnCommandResultDto>)method.Invoke(Inner, args)!);
                }
                if (DelayNextNonVoiceBeforeRegistration && request.InputModality != "Voice")
                {
                    DelayNextNonVoiceBeforeRegistration = false;
                    return HoldBeforeRegistrationAsync(request, args[1] is CancellationToken inputToken ? inputToken : default);
                }
                if (request.ExpectedTarget == SelectedActionTarget && SelectedActionPlan is { } plan)
                {
                    SelectedActionSubmitted.TrySetResult();
                    return Task.FromResult(new SessionTurnCommandResultDto(plan.SessionId, plan.ForegroundTurn!.Id, false));
                }
                if (DelayNextVoice && request.InputModality == "Voice")
                {
                    DelayNextVoice = false;
                    if (DelayBeforeRegistration)
                        return HoldBeforeRegistrationAsync(request, args[1] is CancellationToken token ? token : default);
                    return HoldAsync((Task<SessionTurnCommandResultDto>)method.Invoke(Inner, args)!);
                }
            }
            return method.Invoke(Inner, args);
        }

        private async Task<DashboardDto> HoldStartupDashboardAsync(CancellationToken token)
        {
            Interlocked.Increment(ref StartupDashboardRequests);
            var dashboard = await Inner.GetDashboardAsync(token).ConfigureAwait(false);
            StartupDashboardRequested.TrySetResult();
            await StartupDashboardGate!.Task.WaitAsync(token).ConfigureAwait(false);
            return dashboard;
        }
        private async Task<SessionSnapshotDto> HoldNewSessionBeforeRegistrationAsync(string? title, CancellationToken cancellationToken)
        {
            NewSessionReplyHeld.TrySetResult();
            await ReleaseNewSessionReply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            HeldNewSession = await Inner.StartNewSessionAsync(title, cancellationToken).ConfigureAwait(false);
            NewSessionRegistered.TrySetResult();
            if (HoldNewSessionAfterRegistration) await ReleaseRegisteredSession.Task.ConfigureAwait(false);
            return HeldNewSession;
        }
        private async Task<SessionSnapshotDto> HoldNewSessionAsync(Task<SessionSnapshotDto> creating)
        {
            HeldNewSession = await creating.ConfigureAwait(false);
            NewSessionReplyHeld.TrySetResult();
            await ReleaseNewSessionReply.Task.ConfigureAwait(false);
            return NewSessionReplyOverride ?? HeldNewSession;
        }
        private async Task<SessionSnapshotDto?> HoldSelectedActionPlanAsync(SessionSnapshotDto plan)
        {
            SelectedActionPlanHeld.TrySetResult();
            await ReleaseSelectedActionPlan.Task.ConfigureAwait(false);
            return plan;
        }
        private async Task<SessionTurnCommandResultDto> HoldBeforeRegistrationAsync(
            SessionInputRequestDto request, CancellationToken token)
        {
            ReceiptHeld.TrySetResult();
            await ReleaseReceipt.Task.WaitAsync(token).ConfigureAwait(false);
            return HeldReceipt = await Inner.SubmitSessionInputAsync(request, token).ConfigureAwait(false);
        }
        private async Task<SessionTurnCommandResultDto> HoldAsync(Task<SessionTurnCommandResultDto> request)
        {
            var result = HeldReceipt = await request.ConfigureAwait(false);
            ReceiptHeld.TrySetResult();
            await ReleaseReceipt.Task.ConfigureAwait(false);
            return result;
        }
    }

    private sealed class ControlledVoiceListener : IContinuousVoiceListener
    {
        private CancellationToken _captureCancellation;
        private Func<Task>? _nextResetHook;
        private int _stopCalls;
        public bool IsListening { get; private set; }
        public long RecognitionGeneration { get; private set; }
        public int StopCalls => Volatile.Read(ref _stopCalls);
        public void SetNextResetHook(Func<Task> hook) => Interlocked.Exchange(ref _nextResetHook, hook);
        public bool HoldNextReset { get; set; }
        public TaskCompletionSource ResetHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReset { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<long> ResetRecognitionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsListening) throw new OperationCanceledException();
            var capture = _captureCancellation;
            if (Interlocked.Exchange(ref _nextResetHook, null) is { } hook)
                await hook().ConfigureAwait(false);
            if (HoldNextReset)
            {
                HoldNextReset = false;
                ResetHeld.TrySetResult();
                await ReleaseReset.Task.ConfigureAwait(false);
            }
            // Match native replacement: playback cancellation cannot strand capture;
            // revoked microphone consent still wins even after a later explicit start.
            capture.ThrowIfCancellationRequested();
            return ++RecognitionGeneration;
        }
        public int StartCalls { get; private set; }
        public bool HoldStart { get; set; }
        public TaskCompletionSource StartHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<VoicePartialText>? PartialTextChanged;

        public event EventHandler<VoiceUtterance>? UtteranceRecognized;

        public event EventHandler<ContinuousVoiceStatus>? StateChanged;

        public VoiceCapabilityReport GetCapabilityReport() => new(
            RecognitionModelAvailable: true,
            RecognitionProvider: "Controlled voice harness",
            MicrophoneCount: 1,
            ChineseSpeechOutputAvailable: false,
            Message: "Controlled voice harness ready.");

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HoldStart)
            {
                StartHeld.TrySetResult();
                await ReleaseStart.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            _captureCancellation = cancellationToken;
            RecognitionGeneration++;
            StartCalls++;
            IsListening = true;
            StateChanged?.Invoke(
                this,
                new ContinuousVoiceStatus(ContinuousVoiceState.Listening, "Listening"));
            Started.TrySetResult();
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stopCalls);
            IsListening = false;
            StateChanged?.Invoke(
                this,
                new ContinuousVoiceStatus(ContinuousVoiceState.Stopped, "Stopped"));
            return Task.CompletedTask;
        }

        public void Recognize(string text)
        {
            Require(IsListening, "voice_listener_not_started");
            RecognizeAt(text, RecognitionGeneration);
        }

        public void RecognizeAt(string text, long generation)
        {
            PartialTextChanged?.Invoke(this, new VoicePartialText(text, generation));
            UtteranceRecognized?.Invoke(
                this,
                new VoiceUtterance(text, TimeSpan.FromSeconds(1), generation));
        }

        public void Partial(string text) => PartialTextChanged?.Invoke(this,
            new VoicePartialText(text, RecognitionGeneration));

        public async ValueTask DisposeAsync() =>
            await StopAsync().ConfigureAwait(false);
    }

    private sealed class RecordingChatProvider : IChatModelProvider
    {
        public const string LateReply = "CONTROLLED_LATE_REPLY_MUST_NOT_APPEAR";
        public const string NewReply = "CONTROLLED_REPLACEMENT_REPLY";
        public const string ThirdReply = "CONTROLLED_THIRD_REPLY";
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
        private int _invocationCount;
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        private RecordingChatProvider()
        {
        }

        public ChatProviderDescriptor Descriptor { get; } = new(
            "codex",
            "Controlled Codex",
            "Local controlled voice harness",
            SendsDataOffDevice: false,
            [new ChatModelDescriptor(
                "codex-default",
                "Controlled Chat",
                ChatModelCapabilities.Streaming | ChatModelCapabilities.JsonObjectOutput)],
            ChatProviderCredentialKind.None,
            ChatProviderWorkloads.OrdinaryChat | ChatProviderWorkloads.ProgrammingAgent);

        public TaskCompletionSource<Guid> BlockingTurnStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Guid> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowLateReply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Guid> LateReplyReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Guid> StoppedReceiptTurnStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static RecordingChatProvider Create() => new();

        public Task<ChatProviderHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatProviderHealth(
                Descriptor.ProviderId,
                ChatProviderHealthState.Healthy,
                IsConfigured: true,
                "Controlled provider ready.",
                DateTimeOffset.UtcNow));

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            ChatModelStreamCallback? streamCallback = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            var completion = CompleteCoreAsync(request, cancellationToken);
            if (request.Prompt?.PromptId != "intent.semantic"
                && request.Messages.Last().Content == OldInput)
            {
                _ = completion.ContinueWith(
                    completed =>
                    {
                        if (completed.Status == TaskStatus.RanToCompletion)
                        {
                            LateReplyReturned.TrySetResult(request.TurnId);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return completion;
        }

        private async Task<ChatModelResponse> CompleteCoreAsync(
            ChatModelRequest request,
            CancellationToken cancellationToken)
        {
            using var local = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                local.Token);
            Require(_active.TryAdd(request.TurnId, local), "provider_turn_already_active");
            try
            {
                var latest = request.Messages.Last().Content;
                if (request.Prompt?.PromptId == "intent.semantic")
                {
                    const string structured =
                        "{\"kind\":\"Conversation\",\"target\":null,\"confidence\":0.99," +
                        "\"isAmbiguous\":false,\"missingContext\":\"None\"}";
                    return Response(request, structured, structured);
                }

                if (latest == OldInput)
                {
                    BlockingTurnStarted.TrySetResult(request.TurnId);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        CancellationObserved.TrySetResult(request.TurnId);
                        await AllowLateReply.Task.ConfigureAwait(false);
                        return Response(request, LateReply);
                    }
                }

                if (latest is StoppedReceiptInput or BeforeTextRegistrationInput or BeforeTopicRegistrationInput
                    or BeforeStoppedTopicRegistrationInput or ManualRetryInput)
                {
                    StoppedReceiptTurnStarted.TrySetResult(request.TurnId);
                    await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false);
                }

                Require(latest is NewInput or ThirdInput or DelayedInput or ReplacementTextInput
                    or HandoffTextInput or HandoffVoiceInput or AfterWakeInput or SpeechResetInput
                    or SnapshotReplacementInput or AfterSpeechResetInput or AfterCallInput, "provider_received_unexpected_input");
                return Response(request, latest == ThirdInput ? ThirdReply : NewReply);
            }
            finally
            {
                _active.TryRemove(request.TurnId, out _);
            }
        }

        public Task CancelAsync(Guid turnId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_active.TryGetValue(turnId, out var active))
            {
                active.Cancel();
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            AllowLateReply.TrySetResult();
            foreach (var active in _active.Values)
            {
                active.Cancel();
            }

            _active.Clear();
            return ValueTask.CompletedTask;
        }

        private ChatModelResponse Response(
            ChatModelRequest request,
            string text,
            string? structuredJson = null) =>
            new(
                text,
                ChatFinishReason.Stop,
                new ChatModelUsage(10, 5, 15),
                new ChatProviderMetadata(
                    Descriptor.ProviderId,
                    request.ModelId,
                    $"controlled-{Guid.NewGuid():N}",
                    Descriptor.DataDestination),
                StructuredJson: structuredJson);
    }

    private sealed record HarnessEvidence(
        string OldTurnState,
        string NewTurnState,
        bool FirstInputVoice,
        bool SecondInputVoice,
        bool DistinctSessionTurns,
        bool OldTurnResponding,
        bool UiShowedOldInput,
        bool UiShowedStop,
        bool CancellationObserved,
        bool CancellationTurnMatched,
        bool LateReplyReturned,
        bool NewInputPersisted,
        bool NewReplyPersisted,
        bool LateReplyRejected,
        bool UiShowedNewInput,
        bool UiShowedNewReply,
        bool UiRejectedLateReply,
        OrbHarnessEvidence Orb);

    private sealed record OrbHarnessEvidence(
        bool PausedOrbWithoutListening,
        bool ToggleStartsOnce,
        bool ManagementCloseKeepsListening,
        bool RepeatedSurfaceDoesNotRestart,
        bool ExitStopsMicrophone);

    private sealed record HarnessResult(
        string Stage,
        string OldTurnState,
        string NewTurnState,
        bool EntryAssemblyConfirmed,
        bool ResourceAssemblyConfirmed,
        bool FirstInputVoice,
        bool SecondInputVoice,
        bool DistinctSessionTurns,
        bool OldTurnResponding,
        bool UiShowedOldInput,
        bool UiShowedStop,
        bool CancellationObserved,
        bool CancellationTurnMatched,
        bool LateReplyReturned,
        bool NewInputPersisted,
        bool NewReplyPersisted,
        bool LateReplyRejected,
        bool UiShowedNewInput,
        bool UiShowedNewReply,
        bool UiRejectedLateReply,
        bool NaturalShutdown,
        long ElapsedMilliseconds,
        string? ErrorCode,
        string? ExceptionType,
        OrbHarnessEvidence? Orb = null)
    {
        public static HarnessResult Completed(
            HarnessEvidence evidence,
            bool entryAssemblyConfirmed,
            bool resourceAssemblyConfirmed,
            long elapsedMilliseconds) =>
            new(
                "completed",
                evidence.OldTurnState,
                evidence.NewTurnState,
                entryAssemblyConfirmed,
                resourceAssemblyConfirmed,
                evidence.FirstInputVoice,
                evidence.SecondInputVoice,
                evidence.DistinctSessionTurns,
                evidence.OldTurnResponding,
                evidence.UiShowedOldInput,
                evidence.UiShowedStop,
                evidence.CancellationObserved,
                evidence.CancellationTurnMatched,
                evidence.LateReplyReturned,
                evidence.NewInputPersisted,
                evidence.NewReplyPersisted,
                evidence.LateReplyRejected,
                evidence.UiShowedNewInput,
                evidence.UiShowedNewReply,
                evidence.UiRejectedLateReply,
                NaturalShutdown: true,
                elapsedMilliseconds,
                ErrorCode: null,
                ExceptionType: null,
                Orb: evidence.Orb);

        public static HarnessResult Failed(
            bool entryAssemblyConfirmed,
            bool resourceAssemblyConfirmed,
            bool naturalShutdown,
            long elapsedMilliseconds,
            string errorCode,
            string exceptionType) =>
            new(
                "failed",
                "Unknown",
                "Unknown",
                entryAssemblyConfirmed,
                resourceAssemblyConfirmed,
                FirstInputVoice: false,
                SecondInputVoice: false,
                DistinctSessionTurns: false,
                OldTurnResponding: false,
                UiShowedOldInput: false,
                UiShowedStop: false,
                CancellationObserved: false,
                CancellationTurnMatched: false,
                LateReplyReturned: false,
                NewInputPersisted: false,
                NewReplyPersisted: false,
                LateReplyRejected: false,
                UiShowedNewInput: false,
                UiShowedNewReply: false,
                UiRejectedLateReply: false,
                naturalShutdown,
                elapsedMilliseconds,
                errorCode,
                exceptionType);
    }
}
