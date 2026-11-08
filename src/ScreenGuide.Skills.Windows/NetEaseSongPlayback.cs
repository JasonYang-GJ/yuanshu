using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ScreenGuide.Skills.Windows;

public sealed record MusicPlaybackResult(bool Verified, bool IsPreview);

public interface INetEaseSongPlayback
{
    Task<MusicPlaybackResult> PlayAsync(KnownDesktopApplication application, ForegroundWindowSnapshot window,
        HotMusicResolution song, Action validateTarget, CancellationToken cancellationToken);
}

/// <summary>One official song command to the already-bound executable, followed by local player readback.</summary>
public sealed class NetEaseSongPlayback : INetEaseSongPlayback
{
    private readonly INetEasePlaybackObserver _observer;
    private readonly Action<KnownDesktopApplication, long> _launch;
    private readonly Func<KnownDesktopApplication, bool> _supportsProtocol;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _clock;

    public NetEaseSongPlayback() : this(new NetEasePlaybackObserver(), Launch, HasBoundProtocol,
        TimeSpan.FromSeconds(12)) { }

    internal NetEaseSongPlayback(INetEasePlaybackObserver observer, Action<KnownDesktopApplication, long> launch,
        Func<KnownDesktopApplication, bool> supportsProtocol, TimeSpan timeout, TimeProvider? clock = null)
    {
        _observer = observer;
        _launch = launch;
        _supportsProtocol = supportsProtocol;
        _timeout = timeout;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<MusicPlaybackResult> PlayAsync(KnownDesktopApplication application,
        ForegroundWindowSnapshot window, HotMusicResolution song, Action validateTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validateTarget);
        ValidateSong(song);
        RequireTarget();
        if (!IsNetEaseExecutable(application) || !NetEaseSearchPageReader.Applies(window)
            || !_supportsProtocol(application))
            throw Failure("protocol_unavailable", "当前网易云未提供可核实的播放入口，尚未发起播放。");

        // Prove that this exact window exposes a readable player before sending any command.
        var before = Read();
        if (!before.Readable || before.WindowHandle != window.WindowHandle
            || string.IsNullOrWhiteSpace(before.FooterIdentity))
            throw Failure("player_unreadable", "无法可靠读取网易云播放器状态，尚未发起播放。");
        RequireTarget();
        if (!_supportsProtocol(application))
            throw Failure("protocol_changed", "网易云播放入口已变化，尚未发起播放。");
        RequireTarget();
        _launch(application, song.SongId);
        // Never repeat a command after a late readback, cancellation or failed verification.
        var started = _clock.GetTimestamp();
        NetEasePlaybackObservation? priorMatch = null;
        do
        {
            var current = Read();
            if (Expired()) break;
            // A matching old view is not acknowledgement of this command. The selected
            // song ID is the requested resource; this local receipt establishes only a
            // new matching title/artist/playing state, not a separately read-back song ID.
            var changed = !Same(before.Title, current.Title)
                || !before.Artists.Select(Normalize).Order(StringComparer.Ordinal)
                    .SequenceEqual(current.Artists.Select(Normalize).Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || before.Playing != current.Playing;
            if (changed && MatchesSong(current, song) && current.WindowHandle == window.WindowHandle)
            {
                if (priorMatch is not null && priorMatch.FooterIdentity == current.FooterIdentity
                    && priorMatch.IsPreview == current.IsPreview)
                {
                    RequireTarget();
                    if (Expired()) break;
                    return new(true, current.IsPreview);
                }
                priorMatch = current;
            }
            else priorMatch = null;
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        } while (!Expired());
        throw Failure("not_verified", "已请求播放，但尚未核对到同一首歌正在播放；没有重复执行。");

        void RequireTarget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            validateTarget();
            cancellationToken.ThrowIfCancellationRequested();
        }
        bool Expired() => _clock.GetElapsedTime(started) >= _timeout;
        NetEasePlaybackObservation Read()
        {
            RequireTarget();
            var observation = _observer.Read(window.WindowHandle, cancellationToken);
            RequireTarget();
            return observation;
        }
    }

    internal static bool MatchesSong(NetEasePlaybackObservation actual, HotMusicResolution expected) =>
        actual.Readable && actual.Playing && !string.IsNullOrWhiteSpace(actual.FooterIdentity)
        && Same(actual.Title, expected.Title)
        && actual.Artists.Count == expected.Artists.Count
        && actual.Artists.Select(Normalize).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Artists.Select(artist => Normalize(artist.Name)).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    internal static string BuildArgument(long songId)
    {
        if (songId <= 0 || songId > 9_007_199_254_740_991)
            throw new ArgumentOutOfRangeException(nameof(songId));
        // This is the official web client's fixed ASCII command format. Neither a model,
        // URI, title, registry argument nor remote command is inserted into this object.
        var body = JsonSerializer.Serialize(new { type = "song", id = songId, cmd = "play" });
        return "--webcmd=orpheus://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
    }

    internal static bool ProtocolMatches(string executable, string? registeredCommand)
    {
        if (!Path.IsPathFullyQualified(executable) || string.IsNullOrWhiteSpace(registeredCommand)) return false;
        // Windows' observed registration permits no space between the quoted path and switch.
        // Verify it, but never execute the registry's command string or its parameters.
        var command = registeredCommand.Trim();
        var quoted = "\"" + Path.GetFullPath(executable) + "\"";
        return command.StartsWith(quoted, StringComparison.OrdinalIgnoreCase)
            && command[quoted.Length..].Trim() == "--webcmd=\"%1\"";
    }

    private static bool HasBoundProtocol(KnownDesktopApplication application)
    {
        if (!IsNetEaseExecutable(application)) return false;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"orpheus\shell\open\command", writable: false);
            return ProtocolMatches(application.LaunchTarget, key?.GetValue(null) as string);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { return false; }
    }

    private static void Launch(KnownDesktopApplication application, long songId)
    {
        // Launch a single validated executable directly, with one fixed argument. No shell,
        // default URL handler, remote URI, credential or arbitrary extra argument is used.
        var start = new ProcessStartInfo(application.LaunchTarget) { UseShellExecute = false };
        start.ArgumentList.Add(BuildArgument(songId));
        using var process = Process.Start(start)
            ?? throw Failure("launch_failed", "网易云未接受本次播放请求。");
    }

    private static bool IsNetEaseExecutable(KnownDesktopApplication application) =>
        Path.IsPathFullyQualified(application.LaunchTarget)
        && string.Equals(Path.GetFileName(application.LaunchTarget), "cloudmusic.exe", StringComparison.OrdinalIgnoreCase);

    private static void ValidateSong(HotMusicResolution song)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (!ApplicationSearchWorkflowEvidence.IsValidHotMusic(song))
            throw Failure("song_invalid", "本次热歌曲目身份不完整，尚未发起播放。");
    }

    private static string Normalize(string value) => value.Trim().Normalize(NormalizationForm.FormKC);
    private static bool Same(string left, string right) => Normalize(left) == Normalize(right);
    private static ApplicationSearchWorkflowException Failure(string code, string message) =>
        new("music_playback_" + code, message);
}
