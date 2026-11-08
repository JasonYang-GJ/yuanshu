using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class HotMusicResolverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T12:00:00Z");

    [Fact]
    public async Task ResolvesOnlyOfficialCurrentFirstPlaceWithExactSongAndArtistIdentity()
    {
        using var fixture = new Fixture();
        var result = await fixture.Resolver.ResolveAsync();
        Assert.Equal(3778678, result.ChartId);
        Assert.Equal("热歌榜", result.ChartName);
        Assert.Equal(Now.AddHours(-1), result.ChartUpdatedAtUtc);
        Assert.Equal(Now, result.CheckedAtUtc);
        Assert.Equal(101, result.SongId);
        Assert.Equal("测试晨光", result.Title);
        Assert.Equal([new HotMusicArtist(201, "测试歌手")], result.Artists);
        Assert.Equal("测试歌手 测试晨光", result.SearchQuery);
        Assert.Equal(HotMusicResolver.ChartSourceUri, result.SourceUri);
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(HotMusicResolver.ChartApiUri, fixture.Uri);
        Assert.False(fixture.CredentialsSeen);
    }

    [Theory]
    [InlineData("wrong-chart")]
    [InlineData("wrong-chart-name")]
    [InlineData("missing-date")]
    [InlineData("invalid-date")]
    [InlineData("empty")]
    [InlineData("wrong-count")]
    [InlineData("missing-first-id")]
    [InlineData("missing-first-title")]
    [InlineData("unsafe-title")]
    [InlineData("missing-artist")]
    [InlineData("missing-artist-id")]
    [InlineData("same-artist-id")]
    [InlineData("query-too-long")]
    [InlineData("wrong-code")]
    public async Task IncompleteIdentityNeverFallsBackToSecondSong(string change)
    {
        using var fixture = new Fixture();
        var root = fixture.Document;
        var chart = root["result"]!;
        var first = chart["tracks"]![0]!;
        switch (change)
        {
            case "wrong-chart": chart["id"] = 123; break;
            case "wrong-chart-name": chart["name"] = "其它排行榜"; break;
            case "missing-date": chart.AsObject().Remove("updateTime"); break;
            case "invalid-date": chart["updateTime"] = long.MaxValue; break;
            case "empty": chart["tracks"] = new JsonArray(); break;
            case "wrong-count": chart["trackCount"] = 0; break;
            case "missing-first-id": first["id"] = 0; break;
            case "missing-first-title": first["name"] = ""; break;
            case "unsafe-title": first["name"] = "测试\u202e歌曲"; break;
            case "missing-artist": first["artists"] = new JsonArray(); break;
            case "missing-artist-id": first["artists"]![0]!["id"] = 0; break;
            case "same-artist-id": first["artists"]!.AsArray().Add(first["artists"]![0]!.DeepClone()); break;
            case "query-too-long": first["name"] = new string('歌', 200); break;
            case "wrong-code": root["code"] = 403; break;
        }
        var exception = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Resolver.ResolveAsync());
        Assert.Equal("hot_music_source_response_invalid", exception.Code);
        Assert.Equal(1, fixture.Calls);
    }

    [Theory]
    [InlineData(-8)]
    [InlineData(1)]
    public async Task StaleOrFutureChartIsNotCurrent(int days)
    {
        using var fixture = new Fixture();
        fixture.Document["result"]!["updateTime"] = Now.AddDays(days).ToUnixTimeMilliseconds();
        var exception = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Resolver.ResolveAsync());
        Assert.Equal("hot_music_chart_not_current", exception.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RedirectAndUnavailableSourceAreNeverRetried(HttpStatusCode status)
    {
        using var fixture = new Fixture();
        fixture.ResponseFactory = (request, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Headers = { Location = new Uri("https://untrusted.invalid/chart") }
        });
        var exception = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Resolver.ResolveAsync());
        Assert.Equal("hot_music_source_unavailable", exception.Code);
        Assert.Equal(1, fixture.Calls);
    }

    [Theory]
    [InlineData("content-type")]
    [InlineData("malformed")]
    [InlineData("duplicate-property")]
    [InlineData("changed-uri")]
    public async Task UnexpectedResponsesAreRejected(string change)
    {
        using var fixture = new Fixture();
        fixture.ResponseFactory = (request, _) =>
        {
            var json = fixture.Document.ToJsonString();
            if (change == "malformed") json = "{";
            if (change == "duplicate-property") json = json.Replace("\"code\":200", "\"code\":200,\"code\":200", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = change == "changed-uri" ? new(HttpMethod.Get, "https://untrusted.invalid/chart") : request,
                Content = new StringContent(json, Encoding.UTF8, change == "content-type" ? "text/html" : "application/json")
            });
        };
        var exception = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Resolver.ResolveAsync());
        Assert.Equal(change == "changed-uri" ? "hot_music_source_unavailable" : "hot_music_source_response_invalid", exception.Code);
        Assert.Equal(1, fixture.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponseLimitChecksBothHeadersAndActualStream(bool advertised)
    {
        using var fixture = new Fixture();
        fixture.ResponseFactory = (request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            response.Content = new StringContent(advertised ? "{}" : new string(' ', HotMusicResolver.MaximumResponseBytes + 1),
                Encoding.UTF8, "application/json");
            response.Content.Headers.ContentLength = advertised ? HotMusicResolver.MaximumResponseBytes + 1 : 1;
            return Task.FromResult(response);
        };
        var exception = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Resolver.ResolveAsync());
        Assert.Equal("hot_music_response_too_large", exception.Code);
    }

    [Fact]
    public async Task CancellationBeforeStartDoesNotRequestAndLateResponseCannotSucceed()
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.ResolveAsync(cancelled.Token));
        Assert.Equal(0, fixture.Calls);

        using var cancellation = new CancellationTokenSource();
        fixture.ResponseFactory = (request, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(fixture.Response(request));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.ResolveAsync(cancellation.Token));
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task CancellationReachesPendingTransport()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ResponseFactory = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        };
        using var cancellation = new CancellationTokenSource();
        var task = fixture.Resolver.ResolveAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task TimeoutCancelsTransportWithoutFallback()
    {
        using var fixture = new Fixture();
        fixture.ResponseFactory = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        };
        var resolver = new HotMusicResolver(fixture.Client, new FixedClock(), TimeSpan.FromMilliseconds(20));
        var exception = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => resolver.ResolveAsync());
        Assert.Equal("hot_music_lookup_timeout", exception.Code);
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task CredentialsAddedToClientAreRejectedBeforeRequest()
    {
        using var fixture = new Fixture();
        fixture.Client.DefaultRequestHeaders.Add("Cookie", "FAKE_COOKIE_SENTINEL");
        Assert.Throws<ArgumentException>(() => new HotMusicResolver(fixture.Client));
        var exception = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Resolver.ResolveAsync());
        Assert.Equal("hot_music_credentials_forbidden", exception.Code);
        Assert.Equal(0, fixture.Calls);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        public JsonNode Document { get; } = JsonNode.Parse("""
            {"code":200,"result":{"id":3778678,"name":"热歌榜","updateTime":0,"trackCount":2,"tracks":[
              {"id":101,"name":"测试晨光","artists":[{"id":201,"name":"测试歌手"}]},
              {"id":102,"name":"测试第二首","artists":[{"id":202,"name":"另一歌手"}]}]}}
            """)!;
        public HttpClient Client { get; }
        public HotMusicResolver Resolver { get; }
        public int Calls { get; private set; }
        public string? Uri { get; private set; }
        public bool CredentialsSeen { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? ResponseFactory { get; set; }

        public Fixture()
        {
            Document["result"]!["updateTime"] = Now.AddHours(-1).ToUnixTimeMilliseconds();
            Client = new HttpClient(new Handler(this));
            Resolver = new HotMusicResolver(Client, new FixedClock());
        }

        public HttpResponseMessage Response(HttpRequestMessage request) => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(Document.ToJsonString(), Encoding.UTF8, "application/json")
        };

        public void Dispose() => Client.Dispose();

        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                fixture.Calls++;
                fixture.Uri = request.RequestUri!.AbsoluteUri;
                fixture.CredentialsSeen = request.Headers.Authorization is not null || request.Headers.Contains("Cookie");
                return fixture.ResponseFactory?.Invoke(request, cancellationToken) ?? Task.FromResult(fixture.Response(request));
            }
        }
    }
}
