using ScreenGuide.Skills.Windows;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class NetEasePlaybackObserverTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReadsOnlyStructuredFooterAndDistinguishesPlayingAndPreview(bool playing, bool preview)
    {
        var fixture = new Fixture(playing);
        fixture.AddTag("VIP");
        if (preview) fixture.AddTag("试听");

        var result = fixture.Observe();

        Assert.True(result.Readable);
        Assert.Equal("示例星光", result.Title);
        Assert.Equal(["示例歌手"], result.Artists);
        Assert.Equal(playing, result.Playing);
        Assert.Equal(preview, result.IsPreview);
        Assert.Equal(42, result.WindowHandle);
        Assert.Equal(fixture.Footer.Metadata.Identity, result.FooterIdentity);
        Assert.DoesNotContain(fixture.UnrelatedPage, fixture.ChildrenRead);
        Assert.DoesNotContain(fixture.UnrelatedPage, fixture.NamesRead);
        Assert.DoesNotContain(fixture.OtherFooterLabel, fixture.NamesRead);
        Assert.All(fixture.NamesRead, node => Assert.True(node == fixture.Title || node == fixture.Artist
            || node == fixture.PlayButton || node.Metadata.Attributes.GetValueOrDefault("class") == "cmd-tag"
            || node.Name is "VIP" or "试听"));
    }

    [Fact]
    public void SongAndArtistNamesAreNotMistakenForVipOrPreviewTags()
    {
        var fixture = new Fixture(true);
        fixture.Title.Name = "试听";
        fixture.Artist.Name = "VIP";
        var result = fixture.Observe();
        Assert.True(result.Readable);
        Assert.Equal("试听", result.Title);
        Assert.Equal(["VIP"], result.Artists);
        Assert.False(result.IsPreview);
    }

    [Fact]
    public void SeparateArtistSpansPreserveAllNamesWithoutSplittingNamesOnPunctuation()
    {
        var fixture = new Fixture();
        fixture.Artist.Name = "测试A/B";
        fixture.Side.Children.Add(fixture.ArtistSpan("second-artist", "另一歌手"));
        Assert.Equal(["测试A/B", "另一歌手"], fixture.Observe().Artists);
    }

    [Theory]
    [InlineData("missing-footer")]
    [InlineData("duplicate-footer")]
    [InlineData("wrong-footer-role")]
    [InlineData("wrong-footer-semantic")]
    [InlineData("wrong-window")]
    [InlineData("duplicate-identity")]
    [InlineData("missing-bar")]
    [InlineData("duplicate-title")]
    [InlineData("missing-artist")]
    [InlineData("hidden-title")]
    [InlineData("busy-footer")]
    [InlineData("missing-play")]
    [InlineData("duplicate-play")]
    [InlineData("hidden-play")]
    [InlineData("disabled-play")]
    [InlineData("unknown-play-state")]
    [InlineData("inconsistent-icon")]
    [InlineData("missing-icon")]
    [InlineData("unsafe-title")]
    [InlineData("empty-title")]
    [InlineData("duplicate-artist")]
    [InlineData("unknown-tag")]
    [InlineData("inconsistent-tag")]
    [InlineData("incomplete-tree")]
    [InlineData("cycle")]
    [InlineData("large-tree")]
    public void AmbiguousChangedOrIncompleteStructureIsUnreadable(string change)
    {
        var fixture = new Fixture();
        switch (change)
        {
            case "missing-footer": fixture.Root.Children.Remove(fixture.Footer); break;
            case "duplicate-footer": fixture.Root.Children.Add(Fixture.Node("other-footer", 20, ("tag", "footer"))); break;
            case "wrong-footer-role": fixture.Footer.Metadata = fixture.Footer.Metadata with { Role = 41 }; break;
            case "wrong-footer-semantic": fixture.Footer.Set("xml-roles", "main"); break;
            case "wrong-window": fixture.Title.Metadata = fixture.Title.Metadata with { WindowHandle = 99 }; break;
            case "duplicate-identity": fixture.Artist.Metadata = fixture.Artist.Metadata with { Identity = fixture.Title.Metadata.Identity }; break;
            case "missing-bar": fixture.Bar.Set("id", "other-bar"); break;
            case "duplicate-title": fixture.Side.Children.Add(fixture.TitleSpan("another-title", "另一首")); break;
            case "missing-artist": fixture.ArtistParent.Children.Clear(); break;
            case "hidden-title": fixture.Title.Metadata = fixture.Title.Metadata with { State = 0x8000 }; break;
            case "busy-footer": fixture.Footer.Set("busy", "true"); break;
            case "missing-play": fixture.PlayButton.Set("id", "other-button"); break;
            case "duplicate-play": fixture.Wrapper.Children.Add(Fixture.Node("other-play", 57, ("id", "btn_pc_minibar_play"))); break;
            case "hidden-play": fixture.PlayButton.Metadata = fixture.PlayButton.Metadata with { State = 0x10000 }; break;
            case "disabled-play": fixture.PlayButton.Metadata = fixture.PlayButton.Metadata with { State = 1 }; break;
            case "unknown-play-state": fixture.PlayButton.Name = "正在播放广告"; break;
            case "inconsistent-icon": fixture.PlayButton.Name = "pause"; break;
            case "missing-icon": fixture.PlayButton.Children.Clear(); break;
            case "unsafe-title": fixture.Title.Name = "示例\u202e歌曲"; break;
            case "empty-title": fixture.Title.Name = ""; break;
            case "duplicate-artist": fixture.Side.Children.Add(fixture.ArtistSpan("another-artist", "示例歌手")); break;
            case "unknown-tag": fixture.AddTag("未知权限"); break;
            case "inconsistent-tag": fixture.AddTag("试听").Name = "Tag: VIP"; break;
            case "incomplete-tree": fixture.ArtistParent.Complete = false; break;
            case "cycle": fixture.Artist.Children.Add(fixture.Footer); break;
            case "large-tree": for (var i = 0; i < 129; i++) fixture.Artist.Children.Add(Fixture.Node("excess-" + i, 20)); break;
        }
        var result = fixture.Observe();
        Assert.False(result.Readable);
        Assert.False(result.Playing);
        Assert.Empty(result.Title);
        Assert.Empty(result.Artists);
        if (change == "duplicate-footer") Assert.Empty(fixture.NamesRead);
    }

    [Theory]
    [InlineData("footer")]
    [InlineData("title")]
    [InlineData("artist")]
    [InlineData("play-state")]
    public void TwoObservationsMustAgreeBeforeReportingAState(string change)
    {
        var fixture = new Fixture();
        fixture.BeforeSecondRead = () =>
        {
            switch (change)
            {
                case "footer": fixture.Footer.Metadata = fixture.Footer.Metadata with { Identity = "replacement-footer" }; break;
                case "title": fixture.Title.Name = "已经换歌"; break;
                case "artist": fixture.Artist.Name = "另一位歌手"; break;
                case "play-state": fixture.PlayButton.Name = "pause"; fixture.PlayIcon.Set("class", "cmd-icon cmd-icon-pause"); break;
            }
        };
        Assert.False(fixture.Observe().Readable);
    }

    [Fact]
    public void CancellationBeforeAndDuringMetadataReadPropagates()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Observe(cancellation.Token));
        Assert.Empty(fixture.NamesRead);

        using var during = new CancellationTokenSource();
        fixture.BeforeSecondRead = during.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Observe(during.Token));
    }

    private sealed class FakeNode(NetEasePlaybackNode metadata, string? name = null)
    {
        public NetEasePlaybackNode Metadata { get; set; } = metadata;
        public string? Name { get; set; } = name;
        public List<FakeNode> Children { get; } = [];
        public bool Complete { get; set; } = true;
        public void Set(string key, string value)
        {
            var attributes = Metadata.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value);
            attributes[key] = value;
            Metadata = Metadata with { Attributes = attributes };
        }
    }

    private sealed class Fixture
    {
        public FakeNode Root { get; } = Node("root", 20);
        public FakeNode UnrelatedPage { get; } = Node("unrelated", 20, ("id", "page_pc_home"));
        public FakeNode Footer { get; } = Node("footer", 20, ("tag", "footer"), ("xml-roles", "contentinfo"));
        public FakeNode Bar { get; } = Node("bar", 20, ("id", "page_pc_mini_bar"), ("tag", "div"));
        public FakeNode Wrapper { get; } = Node("wrapper", 20, ("class", "default-bar-wrapper unrelated-hash"), ("tag", "div"));
        public FakeNode Side { get; } = Node("side", 20, ("class", "side"), ("tag", "div"));
        public FakeNode PlayButton { get; } = Node("play", 57, ("id", "btn_pc_minibar_play"), ("tag", "button"));
        public FakeNode PlayIcon { get; } = Node("play-icon", 40, ("class", "cmd-icon cmd-icon-play"));
        public FakeNode Title { get; }
        public FakeNode Artist { get; }
        public FakeNode ArtistParent { get; }
        public FakeNode OtherFooterLabel { get; } = Node("irrelevant-quality-label", 41);
        public List<FakeNode> NamesRead { get; } = [];
        public List<FakeNode> ChildrenRead { get; } = [];
        public Action? BeforeSecondRead { get; set; }

        public Fixture(bool playing = false)
        {
            Root.Children.AddRange([UnrelatedPage, Footer]);
            UnrelatedPage.Children.Add(Node("private-text-never-read", 41));
            Footer.Children.AddRange([Bar, OtherFooterLabel]);
            Bar.Children.Add(Wrapper);
            Wrapper.Children.AddRange([Side, PlayButton]);
            var title = TitleSpan("title-parent", "示例星光");
            Title = title.Children[0];
            ArtistParent = ArtistSpan("artist-parent", "示例歌手");
            Artist = ArtistParent.Children[0];
            Side.Children.AddRange([title, ArtistParent]);
            PlayButton.Name = playing ? "pause" : "play";
            PlayIcon.Set("class", playing ? "cmd-icon cmd-icon-pause" : "cmd-icon cmd-icon-play");
            PlayButton.Children.Add(PlayIcon);
            OtherFooterLabel.Name = "VIP 试听 不应匹配的其它页脚文字";
        }

        public FakeNode AddTag(string text)
        {
            var tag = Node("tag-" + text, 20, ("tag", "div"), ("class", "cmd-tag"), ("explicit-name", "true"));
            tag.Name = "Tag: " + text;
            var content = Node("tag-content-" + text, 20, ("class", "cmd-tag-content"));
            var leaf = Node("tag-leaf-" + text, 41);
            leaf.Name = text;
            content.Children.Add(leaf);
            tag.Children.Add(content);
            Side.Children.Add(tag);
            return tag;
        }

        public FakeNode TitleSpan(string id, string text) => Span(id, text, "block");
        public FakeNode ArtistSpan(string id, string text) => Span(id, text, "inline");
        private static FakeNode Span(string id, string text, string display)
        {
            var parent = Node(id, 20, ("tag", "span"), ("display", display));
            var leaf = Node(id + "-text", 41, ("display", display));
            leaf.Name = text;
            parent.Children.Add(leaf);
            return parent;
        }

        public NetEasePlaybackObservation Observe(CancellationToken cancellationToken = default)
        {
            var reads = 0;
            return NetEasePlaybackObserver.ObserveTree(42,
                () => { if (++reads == 2) BeforeSecondRead?.Invoke(); return new[] { Root }; },
                node => node.Metadata,
                node => { ChildrenRead.Add(node); return node.Complete ? node.Children : null; },
                node => { NamesRead.Add(node); return node.Name; }, cancellationToken);
        }

        public static FakeNode Node(string id, int role, params (string Key, string Value)[] attributes) =>
            new(new(id, 42, role, 0, attributes.ToDictionary(pair => pair.Key, pair => pair.Value)));
    }
}
