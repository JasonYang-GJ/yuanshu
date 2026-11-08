using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using ScreenGuide.DesktopClient;
using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient.VoiceHarness;

internal static partial class Program
{
    private static bool IsStartupVoiceScenario(string value) => value is
        "--startup-voice" or "--startup-stop" or "--startup-hidden" or "--startup-exit" or "--startup-recovery";

    private static async Task<StartupVoiceEvidence> RunStartupVoiceScenarioAsync(
        Application application, MainWindow window, ControlledVoiceListener voice,
        DelayedSubmissionApi delay, string mode)
    {
        delay.StartupDashboardGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var launch = DesktopLaunchOptions.Parse([]);
        var opening = await application.Dispatcher.InvokeAsync(() =>
        {
            if (mode == "--startup-recovery")
                window.ConfigureIslandSurface(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    $"missing-island-{Guid.NewGuid():N}.exe"));
            return window.ShowAssistantAsync(launch.StartListening);
        })
            .Task.ConfigureAwait(false);
        await delay.StartupDashboardRequested.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Require(!voice.IsListening && voice.StartCalls == 0, "startup_capture_preceded_client_readiness");
        Require(await application.Dispatcher.InvokeAsync(() => Orb().IsVisible).Task.ConfigureAwait(false),
            "startup_assistant_not_visible");
        var initialOpenings = new List<Task> { opening };
        if (mode == "--startup-voice")
        {
            for (var i = 0; i < 4; i++)
                initialOpenings.Add(await application.Dispatcher.InvokeAsync(() => window.ShowAssistantAsync(startListening: true))
                    .Task.ConfigureAwait(false));
            Require(delay.StartupDashboardRequests == 1 && voice.StartCalls == 0,
                "concurrent_openings_duplicated_initialization_or_started_early");
        }

        Task? exiting = null;
        if (mode == "--startup-stop")
        {
            // Invoke the product's actual stop event while its initial status is still loading.
            // No native microphone or audio device is created by this controlled fixture.
            await application.Dispatcher.InvokeAsync(() => ((Button)window.FindName("StopVoiceButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent))).Task.ConfigureAwait(false);
        }
        else if (mode == "--startup-hidden")
            await application.Dispatcher.InvokeAsync(() => Orb().Hide()).Task.ConfigureAwait(false);
        else if (mode == "--startup-exit")
            exiting = await application.Dispatcher.InvokeAsync(window.ExitApplicationAsync).Task.ConfigureAwait(false);

        delay.StartupDashboardGate.TrySetResult();
        await Task.WhenAll(initialOpenings).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (exiting is not null) await exiting.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (mode == "--startup-recovery")
        {
            Require(!voice.IsListening && voice.StartCalls == 0, "failed_surface_opened_microphone");
            await (await application.Dispatcher.InvokeAsync(() => window.ShowAssistantAsync(startListening: true))
                .Task.ConfigureAwait(false)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Require(voice.IsListening && voice.StartCalls == 1, "failed_surface_poisoned_later_manual_opening");
            await PauseAsync().ConfigureAwait(false);
            return new(mode, true, true, 1, true, false, false, false, false);
        }
        if (mode != "--startup-voice")
        {
            Require(!voice.IsListening && voice.StartCalls == 0, "revoked_startup_opened_microphone_late");
            return new(mode, true, true, 0, false, false, false, false, false);
        }

        Require(voice.IsListening && voice.StartCalls == 1, "manual_startup_did_not_listen_once");
        await PauseAsync().ConfigureAwait(false);
        using var single = await application.Dispatcher.InvokeAsync(DesktopClientSingleInstance.TryAcquire)
            .Task.ConfigureAwait(false);
        Require(single is not null, "isolated_startup_instance_unavailable");
        var activations = new ConcurrentQueue<Task>();
        single!.SetActivationRequestHandler(request => application.Dispatcher.BeginInvoke(new Action(() =>
            activations.Enqueue(window.ActivateFromLaunchAsync(request)))));

        Require(await DesktopClientSingleInstance.TrySignalExistingAsync().ConfigureAwait(false), "paused_activation_not_sent");
        await WaitUntilAsync(() => activations.Count == 1, "paused_activation_missing").ConfigureAwait(false);
        await Task.WhenAll(activations.ToArray()).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Require(!voice.IsListening && voice.StartCalls == 1, "nonvoice_activation_opened_microphone");

        voice.HoldStart = true;
        Require(await DesktopClientSingleInstance.TrySignalExistingAsync(startListening: true).ConfigureAwait(false),
            "voice_reactivation_not_sent");
        await voice.StartHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        for (var i = 0; i < 4; i++)
            Require(await DesktopClientSingleInstance.TrySignalExistingAsync(startListening: true).ConfigureAwait(false),
                "repeated_voice_activation_not_sent");
        await WaitUntilAsync(() => activations.Count == 6, "repeated_voice_activation_missing").ConfigureAwait(false);
        voice.ReleaseStart.TrySetResult();
        await Task.WhenAll(activations.ToArray()).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Require(voice.IsListening && voice.StartCalls == 2, "voice_reactivation_not_deduplicated");
        Require(await application.Dispatcher.InvokeAsync(() => application.Windows.Cast<Window>()
            .Count(item => item is AssistantOrbWindow) == 1).Task.ConfigureAwait(false), "reopening_created_another_surface");

        var generation = voice.RecognitionGeneration;
        Require(await DesktopClientSingleInstance.TrySignalExistingAsync(startListening: true).ConfigureAwait(false),
            "listening_reactivation_not_sent");
        await WaitUntilAsync(() => activations.Count == 7, "listening_reactivation_missing").ConfigureAwait(false);
        await Task.WhenAll(activations.ToArray()).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Require(voice.IsListening && voice.StartCalls == 2 && voice.RecognitionGeneration == generation,
            "reopening_interrupted_existing_listening");
        await PauseAsync().ConfigureAwait(false);
        return new(mode, true, true, voice.StartCalls, true, true, false, false, false);

        AssistantOrbWindow Orb() => application.Windows.Cast<Window>().OfType<AssistantOrbWindow>().Single();

        async Task PauseAsync()
        {
            await WaitUntilAsync(async () => await application.Dispatcher.InvokeAsync(() =>
                ((Button)Orb().FindName("OrbVoiceToggle")).IsEnabled
                && ((TextBlock)Orb().FindName("VoiceToggleLabel")).Text == "暂停语音")
                .Task.ConfigureAwait(false), "voice_pause_control_not_ready").ConfigureAwait(false);
            await application.Dispatcher.InvokeAsync(() => ((Button)Orb().FindName("OrbVoiceToggle"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent))).Task.ConfigureAwait(false);
            await WaitUntilAsync(() => !voice.IsListening, "voice_pause_did_not_stop").ConfigureAwait(false);
            await WaitUntilAsync(async () => await application.Dispatcher.InvokeAsync(() =>
                ((TextBlock)Orb().FindName("VoiceToggleLabel")).Text == "开始语音")
                .Task.ConfigureAwait(false), "voice_pause_not_completed").ConfigureAwait(false);
        }
    }

    private static Task WaitUntilAsync(Func<bool> check, string failure) =>
        WaitUntilAsync(() => Task.FromResult(check()), failure);

    private static async Task WaitUntilAsync(Func<Task<bool>> check, string failure)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await check().ConfigureAwait(false)) return;
            await Task.Delay(25).ConfigureAwait(false);
        }
        Require(false, failure);
    }

    private sealed record StartupVoiceEvidence(string Scenario, bool VisibleBeforeVoice, bool AwaitedClientReadiness,
        int StartCalls, bool PausedInstanceReopened, bool RepeatedActivationDeduplicated,
        bool RealMicrophoneUsed, bool RealSpeechOutputUsed, bool RealModelUsed);
}
