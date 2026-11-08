using System.Text.RegularExpressions;

namespace ScreenGuide.AI.Core;

public enum DesktopFileRequestMode { Open, Find }

public sealed record ParsedDesktopFileRequest(string Name, DesktopFileRequestMode Mode);

/// <summary>Recognizes a user-named desktop file without resolving a path or granting authority.</summary>
public static partial class DesktopFileRequest
{
    public static bool TryGetName(string? text, out string name)
    {
        name = string.Empty;
        if (!TryParse(text, out var request) || request!.Mode != DesktopFileRequestMode.Open) return false;
        name = request.Name;
        return true;
    }

    public static bool TryParse(string? text, out ParsedDesktopFileRequest? request)
    {
        request = null;
        text = ApplicationSearchRequest.NormalizeSpokenActionLeadIn(text?.Trim() ?? string.Empty);
        // “打开桌面上的某应用，然后搜索…” names an application workflow, not
        // one long desktop filename. Keep literal/quoted filenames authoritative.
        if (ApplicationSearchRequest.HasExplicitApplicationSequence(text?.Trim() ?? string.Empty)) return false;
        var match = ParsedRequestRegex().Match(text?.Trim() ?? string.Empty);
        if (!match.Success) match = LocationFirstRequestRegex().Match(text?.Trim() ?? string.Empty);
        if (!match.Success) return false;
        var name = match.Groups["name"].Value.Trim().Trim('“', '”', '"', '「', '」').Trim();
        var mode = match.Groups["action"].Value is "打开" or "查看"
            ? DesktopFileRequestMode.Open : DesktopFileRequestMode.Find;
        request = new(name, mode);
        return true;
    }

    public static bool IsValidName(string name) => name.Length is > 0 and <= 200
        && name is not ("." or "..") && !name.Any(char.IsControl)
        && name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0;

    [GeneratedRegex("^(?:请帮我|帮我|请|给我|麻烦你|麻烦)?\\s*(?<action>打开|查看|查找|寻找|找找|找)(?:一下)?\\s*(?:我(?:的)?)?桌面(?:上)?(?:的)?(?:文件)?\\s*(?<name>[\\s\\S]*?)\\s*[。！!]?$", RegexOptions.CultureInvariant)]
    private static partial Regex ParsedRequestRegex();

    [GeneratedRegex("^(?:请帮我|帮我|请|给我|麻烦你|麻烦)?\\s*在(?:我(?:的)?)?桌面(?:上)?\\s*(?<action>查找|寻找|找找|找)(?:一下)?(?:文件)?\\s*(?<name>[\\s\\S]*?)\\s*[。！!]?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocationFirstRequestRegex();

}
