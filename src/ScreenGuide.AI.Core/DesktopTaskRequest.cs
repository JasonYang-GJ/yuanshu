using System.Text.RegularExpressions;

namespace ScreenGuide.AI.Core;

public sealed record DesktopTaskRequest(string? ApplicationName, string Goal)
{
    public bool UsesCurrentWindow => ApplicationName is null;
    public bool PrefersVisual => Regex.IsMatch(Goal, @"^(?:(?:请帮我|帮我|请你|请)\s*)?(?:看图|识图|根据画面|通过画面)", RegexOptions.CultureInvariant);

    // 选择的/选中的 describe an existing target, not a request to select it.
    // Do not apply that suffix rule to input text, which may start with 的.
    private const string ActionStart = @"(?:(?:看图|识图|根据画面|通过画面)\s*)?(?:点击|填写|输入|写入|写|选择(?!的)|选中(?!的)|滚动|切换|展开|收起|勾选|取消勾选|计算|新建|创建|保存|整理|调整|编辑|设置|把|将|给|完成|自动完成|执行)";
    // A location is part of the task, never an extension of the installed app's
    // name. Keep this grammar explicit instead of accepting an arbitrary suffix.
    private const string LocationStart = @"(?:在|的)\s*(?:[A-Za-z]{1,3}[1-9][0-9]{0,6}(?::[A-Za-z]{1,3}[1-9][0-9]{0,6})?(?:单元格|格)?|正文|文档正文|空白单元格|当前单元格|选中的单元格|(?:标题|内容|姓名|备注|地址)(?:栏|框|字段)|第[一二三四五六七八九十0-9]+(?:行|列|段))(?:里面|里|中|内)?\s*";
    private const string GoalStart = @"(?:(?:请帮我|帮我|请你|请)\s*)?(?:" + LocationStart + @")?" + ActionStart;

    // An explicit software/action envelope supplies authority, never a semantic
    // suggestion or words read from a window. Keep ordinary questions as chat.
    public static bool TryParse(string text, out DesktopTaskRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000
            || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) return false;
        var value = ApplicationSearchRequest.NormalizeSpokenActionLeadIn(text.Trim());
        value = Regex.Replace(value, @"^(?:请你|请帮我|麻烦你|麻烦|帮我|请|给我)", string.Empty).TrimStart();
        if (Regex.IsMatch(value, """^(?:不要|别|不必|如果|假如|怎么|如何|能不能|可以|为什么|[“"”‘'])""")) return false;
        var match = Regex.Match(value,
            @"^(?:在|操作)?(?<app>当前窗口|当前软件|这个窗口|这个软件)(?:里面|里|中|，|,)?(?<goal>.+)$",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success)
            // Find the explicit action before looking for a generic 里/中
            // separator: those characters can belong to the requested text.
            match = Regex.Match(value,
                @"^(?:用|使用|在)(?<app>[^，,。！？!?\r\n]{1,60}?)(?:里面|里|中|内)?\s*(?<goal>" + GoalStart + @".+)$",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success)
            match = Regex.Match(value,
                @"^(?:在|用|使用)(?<app>[^，,。！？!?\r\n]{1,60}?)(?:里面|里|中)(?<goal>.+)$",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success)
            // Try the verb-bound form first so a comma between later steps cannot
            // turn "使用记事本输入你好，然后选择全部" into a different app name.
            match = Regex.Match(value,
                @"^(?:用|使用)(?<app>[^，,。！？!?\r\n]{1,60}?)(?:，|,)(?<goal>.+)$",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success)
            match = Regex.Match(value,
                @"^(?:打开|启动|操作)(?<app>[^，,。！？!?\r\n]{1,60}?)(?:，然后|,然后|然后|并且|并|，|,)(?<goal>.+)$",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        var app = match.Groups["app"].Value.Trim();
        var goal = match.Groups["goal"].Value.Trim().TrimStart('，', ',');
        // A search query may itself begin with 输入/写/创建. The earlier search
        // envelope owns that query; do not consume its verb into an app name.
        var namedSearch = ApplicationSearchRequest.MatchNamedSearch(value);
        if (namedSearch.Success)
        {
            var taskApplicationEnd = match.Groups["app"].Index + match.Groups["app"].Length;
            var searchApplicationEnd = namedSearch.Groups["app"].Index + namedSearch.Groups["app"].Length;
            // In a complete search envelope, 输入框/输入栏 can be the end of
            // the app name (Nova输入框), not an 输入 action. Keep this specific
            // to search syntax so literal text such as 输入框架设计 stays valid.
            if (searchApplicationEnd <= taskApplicationEnd
                || searchApplicationEnd > taskApplicationEnd
                    && (value[taskApplicationEnd..searchApplicationEnd] is "输入框" or "输入栏")) return false;
        }
        var unquoted = Regex.Replace(goal, """["“][^"”]*["”]|'[^']*'|‘[^’]*’""", string.Empty).Trim();
        if (unquoted.EndsWith('?') || unquoted.EndsWith('？')
            || Regex.IsMatch(unquoted, "会发生什么|会怎样|会怎么样|是什么意思|如何操作|怎么操作|是否可以|能否|(?:但|不过|先|请)?不要执行|不要操作", RegexOptions.CultureInvariant)) return false;
        if (goal.Length == 0 || Regex.IsMatch(goal, """^(?:不要|别|如果|假如|怎么|如何|为什么|能不能|是否|[“"”‘'])""")
            || Regex.IsMatch(app, @"不要|不许|不得|不必|禁止|别|如果|假如|怎么|如何|为什么|能不能|是否", RegexOptions.CultureInvariant)
            || app.IndexOfAny(['\\', '/', ':', ';', '"', '“', '”']) >= 0) return false;
        if (!Regex.IsMatch(goal, "^" + GoalStart, RegexOptions.CultureInvariant)) return false;
        request = new(app is "当前窗口" or "当前软件" or "这个窗口" or "这个软件" ? null : app, goal);
        return true;
    }
}
