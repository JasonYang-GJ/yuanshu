namespace ScreenGuide.DesktopClient.Services;

public sealed record DesktopLaunchOptions(bool ShowAssistant, bool StartListening, bool Background, bool UseIsland = true)
{
    public static DesktopLaunchOptions Parse(IEnumerable<string> arguments)
    {
        var args = new HashSet<string>(arguments, StringComparer.OrdinalIgnoreCase);
        // Background/autostart must never grant a microphone session, even with conflicting flags.
        if (args.Contains("--background") || args.Contains("--autostart")) return new(false, false, true);
        if (args.Contains("--show") || args.Contains("--settings") || args.Contains("--assistant-settings"))
            return new(false, false, false);
        // A manual launch is this run's request to open the visible voice assistant.
        // Installation uses --paused; unknown automation arguments do not grant capture.
        var manualVoiceEntry = args.All(argument =>
            string.Equals(argument, "--assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "--island", StringComparison.OrdinalIgnoreCase));
        return new(true, manualVoiceEntry, false);
    }
}
