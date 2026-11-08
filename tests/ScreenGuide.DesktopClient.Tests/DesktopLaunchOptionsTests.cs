using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class DesktopLaunchOptionsTests
{
    [Fact]
    public void OrdinaryManualLaunchStartsTheVisibleVoiceAssistant()
    {
        var options = DesktopLaunchOptions.Parse([]);
        Assert.True(options.ShowAssistant);
        Assert.True(options.StartListening);
        Assert.False(options.Background);
    }

    [Theory]
    [InlineData("", true, true, false)]
    [InlineData("--paused", true, false, false)]
    [InlineData("--assistant", true, true, false)]
    [InlineData("--assistant --paused", true, false, false)]
    [InlineData("--show", false, false, false)]
    [InlineData("--show --assistant", false, false, false)]
    [InlineData("--settings --assistant", false, false, false)]
    [InlineData("--assistant-settings", false, false, false)]
    [InlineData("--background", false, false, true)]
    [InlineData("--autostart --assistant", false, false, true)]
    [InlineData("--background --assistant", false, false, true)]
    [InlineData("--background --show", false, false, true)]
    [InlineData("--ASSISTANT --PAUSED", true, false, false)]
    [InlineData("--unknown", true, false, false)]
    public void StartupHasOneVisibleEntryAndBackgroundNeverListens(
        string args, bool assistant, bool listening, bool background)
    {
        var options = DesktopLaunchOptions.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(assistant, options.ShowAssistant);
        Assert.Equal(listening, options.StartListening);
        Assert.Equal(background, options.Background);
    }

    [Theory]
    [InlineData("--island", true, true)]
    [InlineData("--island --paused", true, false)]
    [InlineData("--island --background", true, false)]
    [InlineData("--show --island", true, false)]
    [InlineData("", true, true)]
    [InlineData("--assistant", true, true)]
    public void IslandIsBuiltInAndOnlyManualVoiceEntryListens(string args, bool island, bool listening)
    {
        var options = DesktopLaunchOptions.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(island, options.UseIsland);
        Assert.Equal(listening, options.StartListening);
    }
}
