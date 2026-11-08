using System.Text;
using System.Text.Json;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class NetEaseSongPlaybackTests
{
    private static readonly KnownDesktopApplication App = new("fixture-player", "合成播放器", @"C:\fixtures\cloudmusic.exe");
    private static readonly ForegroundWindowSnapshot Window = new(42, "合成播放器", "cloudmusic", 123,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
    private static readonly HotMusicResolution Song = new(3778678, "热歌榜", DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, 123456, "合成歌曲", [new(987, "合成歌手")], "合成歌手 合成歌曲", HotMusicResolver.ChartSourceUri);
    private static NetEasePlaybackObservation Playing => new(true, Song.Title, ["合成歌手"], true, false)
        { WindowHandle = 42, FooterIdentity = "fixture-footer" };

    [Fact]
    public void OfficialArgumentContainsOnlyFixedCommandAndNumericSongIdentity()
    {
        var argument = NetEaseSongPlayback.BuildArgument(123456);
        Assert.StartsWith("--webcmd=orpheus://", argument);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(argument["--webcmd=orpheus://".Length..])));
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        Assert.Equal("song", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(123456, json.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("play", json.RootElement.GetProperty("cmd").GetString());
        Assert.Throws<ArgumentOutOfRangeException>(() => NetEaseSongPlayback.BuildArgument(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetEaseSongPlayback.BuildArgument(long.MaxValue));
    }

    [Theory]
    [InlineData("\"C:\\fixtures\\cloudmusic.exe\"--webcmd=\"%1\"", true)]
    [InlineData("\"C:\\fixtures\\cloudmusic.exe\" --webcmd=\"%1\"", true)]
    [InlineData("\"C:\\other\\cloudmusic.exe\" --webcmd=\"%1\"", false)]
    [InlineData("\"C:\\fixtures\\cloudmusic.exe\" --webcmd=\"%1\" --other", false)]
    [InlineData("C:\\fixtures\\cloudmusic.exe --webcmd=\"%1\"", false)]
    public void RegistrationMustMatchOnlyBoundExecutableAndExpectedSwitch(string command, bool expected) =>
        Assert.Equal(expected, NetEaseSongPlayback.ProtocolMatches(App.LaunchTarget, command));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneCommandRequiresTwoMatchingPlayingSamplesAndReportsPreview(bool preview)
    {
        var count = 0;
        var read = 0;
        var observer = new Observer(() => ++read == 1 ? Playing with { Playing = false }
            : Playing with { IsPreview = preview });
        var player = new NetEaseSongPlayback(observer, (app, id) =>
        {
            Assert.Equal(App, app); Assert.Equal(Song.SongId, id); count++;
        }, _ => true, TimeSpan.FromSeconds(2));
        var result = await player.PlayAsync(App, Window, Song, () => { }, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(preview, result.IsPreview);
        Assert.Equal(1, count);
        Assert.Equal(3, read);
    }

    [Theory]
    [InlineData("artist")]
    [InlineData("title")]
    [InlineData("paused")]
    [InlineData("window")]
    public async Task MismatchedOrPausedReadbackNeverReportsSuccessOrRetries(string mismatch)
    {
        var count = 0; var read = 0;
        var wrong = mismatch switch
        {
            "artist" => Playing with { Artists = ["另一位歌手"] },
            "title" => Playing with { Title = "另一首歌" },
            "paused" => Playing with { Playing = false },
            _ => Playing with { WindowHandle = 43 }
        };
        var player = new NetEaseSongPlayback(new Observer(() => ++read == 1 ? Playing : wrong),
            (_, _) => count++, _ => true, TimeSpan.Zero);
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() =>
            player.PlayAsync(App, Window, Song, () => { }, CancellationToken.None));
        Assert.Equal("music_playback_not_verified", error.Code);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("protocol")]
    [InlineData("protocol-changed")]
    [InlineData("target")]
    public async Task MissingPreconditionsCannotLaunch(string problem)
    {
        var launches = 0; var protocolChecks = 0;
        var player = new NetEaseSongPlayback(new Observer(() => Playing with { Readable = problem != "unreadable" }),
            (_, _) => launches++, _ => problem != "protocol" && (problem != "protocol-changed" || ++protocolChecks == 1),
            TimeSpan.Zero);
        await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => player.PlayAsync(App, Window, Song,
            () => { if (problem == "target") throw new ApplicationSearchWorkflowException("changed", "changed"); }, CancellationToken.None));
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task CancellationDuringReadbackRejectsLateSuccessWithoutRepeatingCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0; var launches = 0;
        var player = new NetEaseSongPlayback(new Observer(() =>
        {
            if (++reads == 2) cancellation.Cancel();
            return Playing;
        }), (_, _) => launches++, _ => true, TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            player.PlayAsync(App, Window, Song, () => { }, cancellation.Token));
        Assert.Equal(1, launches);
        Assert.Equal(2, reads);
    }

    private sealed class Observer(Func<NetEasePlaybackObservation> read) : INetEasePlaybackObserver
    {
        public NetEasePlaybackObservation Read(long hwnd, CancellationToken cancellationToken)
        { Assert.Equal(42, hwnd); cancellationToken.ThrowIfCancellationRequested(); return read(); }
    }

    [Fact]
    public async Task IgnoredCommandAndUnchangedMatchingOldViewCannotProveNewPlayback()
    {
        var launches = 0; var reads = 0;
        var clock = new Clock();
        var player = new NetEaseSongPlayback(new Observer(() =>
        {
            if (++reads == 4) clock.Milliseconds = 1000;
            return Playing;
        }), (_, _) => launches++, _ => true, TimeSpan.FromMilliseconds(500), clock);
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() =>
            player.PlayAsync(App, Window, Song, () => { }, CancellationToken.None));
        Assert.Equal("music_playback_not_verified", error.Code);
        Assert.Equal(1, launches);
        Assert.Equal(4, reads);
    }

    [Fact]
    public async Task MatchingSecondSampleReturnedAfterDeadlineCannotBecomeSuccess()
    {
        var clock = new Clock();
        var reads = 0; var launches = 0;
        var player = new NetEaseSongPlayback(new Observer(() =>
        {
            reads++;
            if (reads == 1) return Playing with { Playing = false };
            if (reads == 3) clock.Milliseconds = 1000;
            return Playing;
        }), (_, _) => launches++, _ => true, TimeSpan.FromMilliseconds(500), clock);
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() =>
            player.PlayAsync(App, Window, Song, () => { }, CancellationToken.None));
        Assert.Equal("music_playback_not_verified", error.Code);
        Assert.Equal(3, reads);
        Assert.Equal(1, launches);
    }

    private sealed class Clock : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}
