using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace ScreenGuide.Skills.Windows;

public interface IHotMusicResolver
{
    Task<HotMusicResolution> ResolveAsync(CancellationToken cancellationToken = default);
}

public sealed record HotMusicArtist(long Id, string Name);

public sealed record HotMusicResolution(
    long ChartId,
    string ChartName,
    DateTimeOffset ChartUpdatedAtUtc,
    DateTimeOffset CheckedAtUtc,
    long SongId,
    string Title,
    IReadOnlyList<HotMusicArtist> Artists,
    string SearchQuery,
    string SourceUri);

/// <summary>Anonymous, current first-place metadata from one fixed official chart; never action authority.</summary>
public sealed class HotMusicResolver : IHotMusicResolver
{
    public const long NetEaseHotChartId = 3778678;
    public const string ChartSourceUri = "https://music.163.com/discover/toplist?id=3778678";
    internal const string ChartApiUri = "https://music.163.com/api/playlist/detail?id=3778678";
    internal const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly HttpClient PublicClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _totalTimeout;

    public HotMusicResolver() : this(PublicClient) { }

    public HotMusicResolver(HttpClient httpClient, TimeProvider? timeProvider = null, TimeSpan? totalTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.DefaultRequestHeaders.Authorization is not null
            || _httpClient.DefaultRequestHeaders.Contains("Cookie"))
            throw new ArgumentException("公开热歌榜查询不能携带账号凭据。", nameof(httpClient));
        _clock = timeProvider ?? TimeProvider.System;
        _totalTimeout = totalTimeout ?? TimeSpan.FromSeconds(12);
        if (_totalTimeout <= TimeSpan.Zero || _totalTimeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(totalTimeout));
    }

    public async Task<HotMusicResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(_totalTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            // A caller-supplied client must not gain credentials between construction and use.
            if (_httpClient.DefaultRequestHeaders.Authorization is not null
                || _httpClient.DefaultRequestHeaders.Contains("Cookie"))
                throw Failure("credentials_forbidden", "公开热歌榜查询不能携带账号凭据。");
            using var request = new HttpRequestMessage(HttpMethod.Get, ChartApiUri);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (response.StatusCode != HttpStatusCode.OK
                || response.RequestMessage?.RequestUri is { } actualUri && actualUri.AbsoluteUri != ChartApiUri)
                throw Failure("source_unavailable", "网易云热歌榜暂时不可用，没有改用其它来源或歌曲。");
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                throw InvalidResponse();
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                throw Failure("response_too_large", "热歌榜返回内容超出本次可核实的范围。");
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (body.Length + read > MaximumResponseBytes)
                    throw Failure("response_too_large", "热歌榜返回内容超出本次可核实的范围。");
                body.Write(buffer, 0, read);
            }
            linked.Token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var resolution = Parse(document.RootElement, _clock.GetUtcNow());
            linked.Token.ThrowIfCancellationRequested();
            return resolution;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("lookup_timeout", "热歌榜查询超时，尚未确认本次歌曲。");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw Failure("source_unavailable", "暂时无法连接网易云热歌榜，没有改用其它来源或歌曲。");
        }
        catch (Exception exception) when ((exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException)
            && exception is not ApplicationSearchWorkflowException)
        {
            throw InvalidResponse();
        }
    }

    private static HotMusicResolution Parse(JsonElement root, DateTimeOffset now)
    {
        RequireObject(root);
        if (Number(root, "code") != 200) throw InvalidResponse();
        var chart = root.GetProperty("result");
        RequireObject(chart);
        if (Number(chart, "id") != NetEaseHotChartId || Text(chart, "name", 80) != "热歌榜") throw InvalidResponse();
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(Number(chart, "updateTime"));
        if (updatedAt > now || now - updatedAt > TimeSpan.FromDays(7))
            throw Failure("chart_not_current", "热歌榜更新时间无法确认为近期，尚未选择或播放歌曲。");
        var tracks = chart.GetProperty("tracks");
        var count = Number(chart, "trackCount");
        if (tracks.ValueKind != JsonValueKind.Array || tracks.GetArrayLength() is < 1 or > 1000
            || count < tracks.GetArrayLength() || count > 1000) throw InvalidResponse();
        // An invalid first item never permits silently choosing a lower-ranked song.
        var first = tracks[0];
        RequireObject(first);
        var songId = Number(first, "id");
        var title = Text(first, "name", 200);
        var artistRows = first.GetProperty("artists");
        if (songId <= 0 || artistRows.ValueKind != JsonValueKind.Array || artistRows.GetArrayLength() is < 1 or > 16)
            throw InvalidResponse();
        var artists = new List<HotMusicArtist>();
        foreach (var artist in artistRows.EnumerateArray())
        {
            RequireObject(artist);
            var id = Number(artist, "id");
            if (id <= 0 || artists.Any(item => item.Id == id)) throw InvalidResponse();
            artists.Add(new(id, Text(artist, "name", 80)));
        }
        var query = string.Join(" ", artists.Select(item => item.Name)) + " " + title;
        if (query.Length > 200) throw InvalidResponse();
        return new(NetEaseHotChartId, "热歌榜", updatedAt, now, songId, title,
            artists.ToArray(), query, ChartSourceUri);
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != names.Distinct(StringComparer.Ordinal).Count()) throw InvalidResponse();
    }

    private static long Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.TryGetInt64(out var number) ? number : throw InvalidResponse();

    private static string Text(JsonElement value, string name, int maximum)
    {
        var text = value.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text != text.Trim()
            || text.Any(character => char.IsControl(character)
                || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))
            throw InvalidResponse();
        return text;
    }

    private static ApplicationSearchWorkflowException InvalidResponse() =>
        Failure("source_response_invalid", "网易云热歌榜缺少可核实的榜首歌曲身份，未继续播放。");

    private static ApplicationSearchWorkflowException Failure(string code, string message) => new("hot_music_" + code, message);
}
