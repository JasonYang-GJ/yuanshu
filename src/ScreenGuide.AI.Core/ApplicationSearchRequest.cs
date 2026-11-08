using System.Text;
using System.Text.RegularExpressions;

namespace ScreenGuide.AI.Core;

/// <summary>Language-only request. ApplicationName is not a trusted application identity.</summary>
public sealed partial record ApplicationSearchRequest(
    string ApplicationName,
    string Query,
    string FollowUp = "None",
    string? LatestMusicArtist = null,
    string? MusicChart = null)
{
    private const string HotMusicQueryPattern = @"(?:(?:最近|近期|当前|现在|目前)(?:的)?\s*)?(?:(?:一|1)首\s*)?(?:(?:最热门|最热|最火|热门|热度最高)(?:的)?\s*(?:(?:一|1)首\s*)?(?:歌曲|单曲|歌)|热歌)";
    internal const string SearchFieldPattern = @"(?:搜索|文字|地址|输入)(?:框|栏)";
    // Commit to a complete verb; the query itself may legitimately start with 框 or 栏.
    internal const string SearchVerbPattern = @"(?>搜索|查找|搜一下|搜)(?:一下)?";

    internal static bool TryParse(string text, out ApplicationSearchRequest? request, out string? error)
    {
        request = null;
        error = null;
        text = NormalizeSpokenActionLeadIn(text);
        var match = OpenAndSearch().Match(text);
        var opensApplication = match.Success;
        if (!match.Success)
        {
            match = LocalApplicationThenSearch().Match(text);
            opensApplication = match.Success;
        }
        if (!match.Success) match = MatchNamedSearch(text);
        if (!match.Success) return false;
        var application = match.Groups["app"].Value.Trim().Trim('“', '”', '"');
        // Contextual phrases continue to use the captured foreground window, not the installed catalog.
        if (!opensApplication && (application is "" or "当前窗口" or "这个窗口" or "当前软件" or "这个软件"
            or "当前应用" or "这个应用" or "当前页面" or "这个页面" or "浏览器" or "当前浏览器")) return false;

        if (application.Length is < 1 or > 80 || text.Any(char.IsControl)) return Invalid(out error);
        var remainder = match.Groups["query"].Value.Trim().TrimEnd('。', '！', '!');
        var hotMusic = HotMusicQuery().Match(remainder);
        if (hotMusic.Success)
        {
            request = new(application, hotMusic.Groups["query"].Value.Trim(),
                hotMusic.Groups["play"].Success ? "PlayFirst" : "None", MusicChart: "NetEaseHotChart");
            return true;
        }
        // 找 is admitted only for the complete bounded chart request, not arbitrary new actions.
        if (match.Groups["musicFind"].Success || HotMusicPlaybackTail().IsMatch(remainder)) return Invalid(out error);
        if (!TryParseQuery(remainder, allowFollowUp: true,
                out var query, out var followUp, out var quotedQuery)) return Invalid(out error);
        if (!quotedQuery && HotMusicQuery().IsMatch(query)) return Invalid(out error);
        if (!quotedQuery && LatestMusicWithoutArtist().IsMatch(query))
        {
            error = "请补充歌手名字，例如“在网易云里搜索周杰伦最新的歌”；我会按发行时间查找，不把“最新的歌”直接作为关键词。";
            return true;
        }
        var latestArtist = quotedQuery ? null : ParseLatestMusicArtist(query, out error);
        if (error is not null) return true;
        request = new(application, query, followUp, latestArtist);
        return true;
    }

    internal static Match MatchNamedSearch(string text) => NamedSearch().Match(text);

    internal static bool TryParseQuery(string remainder, bool allowFollowUp,
        out string query, out string followUp, out bool quotedQuery)
    {
        query = string.Empty;
        followUp = "None";
        remainder = remainder.Trim();
        quotedQuery = remainder.StartsWith('“') || remainder.StartsWith('"') || remainder.StartsWith('「');
        if (remainder.Any(char.IsControl)) return false;
        if (quotedQuery)
        {
            var closing = remainder[0] switch { '“' => '”', '「' => '」', _ => '"' };
            var end = remainder.IndexOf(closing, 1);
            if (end < 0) return false;
            query = remainder[1..end];
            var tail = remainder[(end + 1)..].Trim().TrimEnd('。', '！', '!');
            if (tail.Length > 0 && (!allowFollowUp || !TryFollowUp(tail, out followUp))) return false;
        }
        else
        {
            remainder = remainder.TrimEnd('。', '！', '!');
            var suffix = allowFollowUp ? FollowUpSuffix().Match(remainder) : Match.Empty;
            if (suffix.Success)
            {
                query = remainder[..suffix.Index].Trim();
                followUp = suffix.Groups["verb"].Value == "播放" ? "PlayFirst" : "OpenFirst";
            }
            else query = remainder;
            // Unquoted additional commands are not silently turned into authorized steps or search text.
            if (AdditionalAction().IsMatch(query)) return false;
        }
        query = query.Trim().Normalize(NormalizationForm.FormKC);
        return query.Length is > 0 and <= 200;
    }

    private static string? ParseLatestMusicArtist(string query, out string? error)
    {
        // An explicit, complete request may ask for release ordering. Quoted text,
        // negation and lyric/title queries remain literal. An otherwise explicit
        // latest request cannot silently degrade into a keyword search.
        error = null;
        if (LiteralLatestArtist().IsMatch(query)) return null;
        // In speech the release qualifier may precede the artist, as in
        // “最近周杰伦的新歌”. Check that shape first so 最近 cannot become
        // part of the artist identity sent to a public release catalogue.
        var match = LeadingLatestMusicQuery().Match(query);
        if (!match.Success) match = LatestMusicQuery().Match(query);
        if (!match.Success) return null;
        var artist = match.Groups["artist"].Value.Trim();
        if (LiteralLatestArtist().IsMatch(artist)) return null;
        if (artist.Length is < 1 or > 80
            || artist is "我" or "你" or "他" or "她" or "它" or "我们" or "你们" or "他们" or "她们" or "它们"
                or "这个歌手" or "这位歌手" or "那个歌手" or "那位歌手" or "这首" or "歌手" or "谁"
            || MultipleLatestArtists().IsMatch(artist))
        {
            error = "请明确一位歌手的名字，例如“在网易云里搜索周杰伦最新的歌”；目前需要具体、单一的歌手才能核对发行时间。";
            return null;
        }
        return artist;
    }

    internal static string NormalizeSpokenActionLeadIn(string text)
    {
        // Hesitations and corrections carry no authority. Only remove bounded
        // leading words when the remaining text itself names an explicit action.
        var correction = SpokenActionCorrection().Match(text);
        if (correction.Success) text = text[correction.Length..];
        var match = SpokenActionLeadIn().Match(text);
        return match.Success ? text[match.Length..] : text;
    }

    internal static bool TryGetIncompleteSearchClarification(string text, out string? clarification)
    {
        clarification = null;
        if (ObservedApplicationThenSearch().IsMatch(text))
        {
            clarification = "没有听清是否要打开软件执行搜索。请说完整指令，例如“打开网易云，搜索周杰伦的新歌”。";
            return true;
        }

        var query = text.Trim().TrimEnd('。', '！', '!').Normalize(NormalizationForm.FormKC);
        if (query.Length is < 1 or > 200 || text.Any(char.IsControl)) return false;
        if (LatestMusicWithoutArtist().IsMatch(query))
        {
            clarification = "请说出歌手和搜索软件，例如“在网易云里搜索周杰伦的新歌”。";
            return true;
        }
        var artist = ParseLatestMusicArtist(query, out var error);
        if (artist is null && error is null) return false;
        clarification = artist is not null
            ? $"要在哪个软件查找“{artist}”的新歌？请说完整指令，例如“在网易云里搜索{artist}的新歌”。"
            : "请明确一位歌手和搜索软件，例如“在网易云里搜索周杰伦的新歌”。";
        return true;
    }

    // The desktop-file parser and planner must agree on where a compound command
    // ends its application target. A quoted/file-looking name remains a file.
    internal static bool HasExplicitApplicationSequence(string text)
    {
        text = NormalizeSpokenActionLeadIn(text);
        var match = OpenAndSearch().Match(text);
        if (!match.Success || !match.Groups["sequence"].Success) return false;
        var sequence = match.Groups["sequence"].Value;
        // Without a punctuation boundary the whole phrase can be a real filename
        // (e.g. 网易云然后搜索清单.txt). Keep that established file interpretation.
        if (sequence.IndexOfAny(['，', ',']) < 0
            && Regex.IsMatch(text.TrimEnd('。', '！', '!'), @"\.[\p{L}\p{N}]{1,12}$", RegexOptions.CultureInvariant)) return false;
        var target = match.Groups["app"].Value.Trim();
        return target.IndexOfAny(['“', '”', '"', '「', '」']) < 0
            && !target.Contains("文件", StringComparison.Ordinal)
            && !Regex.IsMatch(target, @"\.[\p{L}\p{N}]{1,12}$", RegexOptions.CultureInvariant);
    }

    private static bool TryFollowUp(string tail, out string followUp)
    {
        var match = FollowUpSuffix().Match(tail);
        followUp = match.Success && match.Groups["verb"].Value == "播放" ? "PlayFirst" : "OpenFirst";
        return match.Success && match.Index == 0;
    }

    private static bool Invalid(out string? error)
    {
        error = "请明确指定软件和搜索内容；后续操作目前支持“打开第一条”或“播放第一首”。搜索内容须为 1 到 200 个字符。";
        return true;
    }

    [GeneratedRegex("^(?:(?:请帮我|帮我|给我|麻烦你|麻烦|请)\\s*){0,3}(?:打开|启动)(?:一下)?\\s*(?<app>.+?)(?:(?<sequence>[，,]\\s*(?:然后|并且|并|再|接着)?|\\s*(?:然后|并且|并|再|接着))|\\s*)\\s*(?:在(?:里面|里|其中))?(?:帮我|给我)?(?:(?:搜索|查找|搜一下|搜)(?:一下)?|(?<musicFind>找(?:一下)?))\\s*(?<query>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAndSearch();

    // Spoken commands often omit 打开 but retain a local target and an explicit
    // sequencing word. Never infer this from a mention in an arbitrary sentence.
    [GeneratedRegex("^(?:(?:请帮我|帮我|给我|麻烦你|麻烦|请)\\s*){0,3}(?<app>(?:(?:我(?:的)?)?桌面(?:上)?(?:的)?|(?:我(?:的)?)?电脑(?:上|里)(?:的)?|我的)[^，,。！？?!“”\\\"「」]+?)(?:[，,]\\s*)?(?:然后|并且|并|再|接着)\\s*(?:在(?:里面|里|其中))?(?:帮我|给我)?(?:(?:搜索|查找|搜一下|搜)(?:一下)?|(?<musicFind>找(?:一下)?))\\s*(?<query>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalApplicationThenSearch();

    // Keep the established “在<完整应用名>里搜索” boundary, including app names
    // ending in 搜索框/输入框. A field followed by 里/中/内 needs an explicit 的;
    // without a location word, “在<应用>搜索框搜索” remains unambiguous field syntax.
    [GeneratedRegex(@"^(?:请帮我|帮我|给我|麻烦你|麻烦|请)?\s*在\s*(?<app>[^\r\n]{0,80}?)(?:的\s*"
        + SearchFieldPattern + @"\s*(?:里面|里|中|内)?|" + SearchFieldPattern
        + @"|(?:里面|里|中|内))\s*(?:请帮我|帮我|给我)?"
        + @"(?:" + SearchVerbPattern + @"|(?<musicFind>找(?:一下)?))\s*(?<query>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSearch();

    [GeneratedRegex(@"^(?<query>" + HotMusicQueryPattern + @")(?:\s*(?:[，,]\s*)?(?:(?:然后|并且|并|再|接着)\s*)?(?:帮我|给我)?(?<play>播放)(?:第(?:一|1)(?:首|个)(?:歌曲|歌|结果)?)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex HotMusicQuery();

    [GeneratedRegex(@"^" + HotMusicQueryPattern + @"\s*播放", RegexOptions.CultureInvariant)]
    private static partial Regex HotMusicPlaybackTail();

    [GeneratedRegex("(?:[，,]\\s*(?:然后|并且|并|再|接着)?|\\s*(?:然后|并且|并|再|接着))\\s*(?:帮我|给我)?(?<verb>打开|播放)(?:搜索结果中的?|搜索结果的?|结果中的?|结果的?)?第(?:一|1)(?:条|首|个)(?:结果|歌曲|歌|视频)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FollowUpSuffix();

    [GeneratedRegex("(?:[，,]\\s*|然后|并且|并|再|接着)\\s*(?:帮我|给我)?(?:打开|启动|搜索|查找|播放|发送|发布|付款|支付|购买|删除|下载|安装|登录|退出|关闭|修改|点赞|关注)", RegexOptions.CultureInvariant)]
    private static partial Regex AdditionalAction();

    [GeneratedRegex("^(?<artist>[\\p{L}\\p{M}\\p{N}][\\p{L}\\p{M}\\p{N} .·&+'’/(),，、-]{0,79}?)\\s*(?:的\\s*)?(?:(?:最新|最近|近期|刚刚|刚)(?:发布|发行|推出|出)?(?:的)?(?:歌曲|单曲|新歌|歌)|新(?:发布|发行|推出|出)的(?:歌曲|单曲|歌)|新歌)$", RegexOptions.CultureInvariant)]
    private static partial Regex LatestMusicQuery();

    [GeneratedRegex("^(?:最新|最近|近期)(?:发布|发行|推出|出)?(?:的)?\\s*(?<artist>[\\p{L}\\p{M}\\p{N}][\\p{L}\\p{M}\\p{N} .·&+'’/(),，、-]{0,79}?)\\s*(?:的\\s*)?(?:(?:最新|最近|近期)(?:发布|发行|推出|出)?(?:的)?)?(?:歌曲|单曲|新歌|歌)$", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingLatestMusicQuery();

    [GeneratedRegex("^(?:(?:最新|最近|近期|刚刚|刚)(?:发布|发行|推出|出)?(?:的)?(?:歌曲|单曲|新歌|歌)|新(?:发布|发行|推出|出)的(?:歌曲|单曲|歌)|新歌)$", RegexOptions.CultureInvariant)]
    private static partial Regex LatestMusicWithoutArtist();

    [GeneratedRegex("不要|不想|不是|不需要|别给|不包括|除了|然后|并且|接着|歌词|歌名|最热|最火|热门|好听|^(?:我(?:想|喜欢|很喜欢|觉得|听了|听过)|你(?:喜欢|觉得|听过)|关于|说说|聊聊|讨论|介绍|评价|推荐|有没有|是否|听说|怎么|如何|为什么|比如|例如|就是|那个|帮我|给我|请)", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralLatestArtist();

    [GeneratedRegex("或者|以及|.+(?:和|与).+|[，,、]", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleLatestArtists();

    [GeneratedRegex("^(?:(?:就是|那个|嗯|呃)[，, ]*){1,2}(?=(?:(?:请帮我|帮我|给我|麻烦你|麻烦|请)\\s*){0,3}(?:(?:打开|启动)(?:一下)?|在.{1,80}?(?:里面|里|中|内)(?:请帮我|帮我|给我)?(?:搜索|查找|搜一下|搜)))", RegexOptions.CultureInvariant)]
    private static partial Regex SpokenActionLeadIn();

    [GeneratedRegex(@"^(?:不对[，, ]*)?(?:我说的是|我的意思是|我是说)[，,：: ]*(?=(?:(?:请帮我|帮我|给我|麻烦你|麻烦|请)\s*){0,3}(?:打开|启动|运行)(?:一下)?\s*[^\s])", RegexOptions.CultureInvariant)]
    private static partial Regex SpokenActionCorrection();

    [GeneratedRegex("^(?:(?:就是|那个|嗯|呃)[，, ]*){0,2}我(?:刚才)?(?:看见|看到|见到)(?:(?:我(?:的)?)?桌面(?:上)?(?:的)?|(?:我(?:的)?)?电脑(?:上|里)(?:的)?).{1,80}?(?:[，,]\\s*)?(?:然后|并且|并|再|接着)\\s*(?:搜索|查找|搜一下|搜)(?:一下)?[^\\p{Cc}。！？?!]{0,200}[。！! ]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ObservedApplicationThenSearch();
}
