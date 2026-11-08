using System.Globalization;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

/// <summary>Checks a workflow receipt against its requested music scope; never grants action authority.</summary>
public static class ApplicationSearchWorkflowEvidence
{
    public static bool MatchesMusicIntent(ApplicationSearchWorkflow workflow, ApplicationSearchWorkflowResult evidence)
    {
        if (workflow.MusicChart is null)
            return evidence.HotMusic is null
                && (workflow.LatestMusicArtist is null
                    ? evidence.LatestMusic is null
                    : evidence.LatestMusic is not null);

        return workflow.MusicChart == ApplicationSearchWorkflow.NetEaseHotChart
            && workflow.LatestMusicArtist is null && evidence.LatestMusic is null
            && IsValidHotMusic(evidence.HotMusic);
    }

    public static bool IsValidHotMusic(HotMusicResolution? song)
    {
        if (song is null || song.ChartId != HotMusicResolver.NetEaseHotChartId
            || song.ChartName != "热歌榜" || song.SourceUri != HotMusicResolver.ChartSourceUri
            || !IsValidId(song.SongId) || !IsValidText(song.Title, 200)
            || song.Artists is null || song.Artists.Count is < 1 or > 16
            || !IsValidText(song.SearchQuery, 200)
            || song.CheckedAtUtc == default || song.ChartUpdatedAtUtc == default
            || song.ChartUpdatedAtUtc > song.CheckedAtUtc
            || song.CheckedAtUtc - song.ChartUpdatedAtUtc > TimeSpan.FromDays(7))
            return false;

        var ids = new HashSet<long>();
        foreach (var artist in song.Artists)
            if (artist is null || !IsValidId(artist.Id) || !ids.Add(artist.Id)
                || !IsValidText(artist.Name, 80))
                return false;

        // Match the resolver's canonical artist-first query exactly. An independently
        // plausible title or generic success flag cannot substitute for this identity.
        return song.SearchQuery == string.Join(" ", song.Artists.Select(artist => artist.Name)) + " " + song.Title;
    }

    private static bool IsValidId(long id) => id is > 0 and <= 9_007_199_254_740_991;

    private static bool IsValidText(string? text, int maximumLength) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= maximumLength && text == text.Trim()
        && !text.Any(character => char.IsControl(character)
            || char.GetUnicodeCategory(character) == UnicodeCategory.Format);
}
