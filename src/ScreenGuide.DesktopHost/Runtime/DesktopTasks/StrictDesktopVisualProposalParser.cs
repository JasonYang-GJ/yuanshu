using System.Text.Json;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

internal static class StrictDesktopVisualProposalParser
{
    public static DesktopVisualProposal Parse(string json, DesktopVisualSnapshot snapshot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 8000) throw Invalid();
            using var doc = JsonDocument.Parse(json, new() { MaxDepth = 5 });
            var root = doc.RootElement;
            RequireFields(root, "snapshotId", "coordinateSpace", "action", "bounds", "text", "target", "reason", "evidenceText");
            if (Text(root, "coordinateSpace", 32, false) != "normalized1000"
                || snapshot.ImageWidth is < 1 or > 2048 || snapshot.ImageHeight is < 1 or > 2048) throw Invalid();
            if (!Guid.TryParseExact(Text(root, "snapshotId", 36, false), "D", out var id) || id != snapshot.Id
                || !Enum.TryParse<DesktopVisualActionKind>(Text(root, "action", 32, false), false, out var action)
                || !Enum.IsDefined(action) || root.GetProperty("action").GetString() != action.ToString()) throw Invalid();
            var target = Text(root, "target", 200, false);
            var reason = Text(root, "reason", 500, false);
            var text = Text(root, "text", 500, true);
            var evidence = Text(root, "evidenceText", 500, true);
            var boundsElement = root.GetProperty("bounds");
            DesktopVisualBounds? bounds = null;
            if (boundsElement.ValueKind != JsonValueKind.Null)
            {
                RequireFields(boundsElement, "x", "y", "width", "height");
                bounds = new(Integer("x"), Integer("y"), Integer("width"), Integer("height"));
                if (bounds.X < 0 || bounds.Y < 0 || bounds.Width < 1 || bounds.Height < 1
                    || (long)bounds.X + bounds.Width > 1000 || (long)bounds.Y + bounds.Height > 1000) throw Invalid();
                // v2 has one explicit coordinate system. Never guess from the
                // numbers whether a model meant pixels or relative positions.
                var left = (int)((long)bounds.X * snapshot.ImageWidth / 1000);
                var top = (int)((long)bounds.Y * snapshot.ImageHeight / 1000);
                var right = (int)(((long)(bounds.X + bounds.Width) * snapshot.ImageWidth + 999) / 1000);
                var bottom = (int)(((long)(bounds.Y + bounds.Height) * snapshot.ImageHeight + 999) / 1000);
                bounds = new(left, top, right - left, bottom - top);
                if (bounds.Width < 3 || bounds.Height < 3) throw Invalid();
                int Integer(string name) => boundsElement.GetProperty(name).TryGetInt32(out var value) ? value : throw Invalid();
            }
            if (action is DesktopVisualActionKind.Wait or DesktopVisualActionKind.Ask or DesktopVisualActionKind.Finish)
            {
                if (bounds is not null || text is not null) throw Invalid();
            }
            else if (bounds is null || string.IsNullOrWhiteSpace(target)) throw Invalid();
            if (action == DesktopVisualActionKind.TypeText)
            {
                if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) throw Invalid();
            }
            else if (text is not null) throw Invalid();
            if (string.IsNullOrWhiteSpace(reason)) throw Invalid();
            if (action == DesktopVisualActionKind.Finish ? string.IsNullOrWhiteSpace(evidence) : evidence is not null) throw Invalid();
            return new(id, action, bounds, text, target!, reason!, evidence);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw Invalid();
        }
    }

    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var fields = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Length != names.Length || fields.Distinct(StringComparer.Ordinal).Count() != names.Length
            || fields.Except(names, StringComparer.Ordinal).Any()) throw Invalid();
    }
    private static string? Text(JsonElement value, string key, int maximum, bool nullable)
    {
        var field = value.GetProperty(key);
        if (nullable && field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String) throw Invalid();
        var text = field.GetString()!;
        if (text.Length > maximum || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw Invalid();
        return text;
    }
    private static DesktopTaskException Invalid() => new("desktop_visual_proposal_invalid", "识图建议缺少可靠的目标或格式不正确，因此没有执行。");
}
