using System.Globalization;
using System.Text.Json;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>Parses untrusted model output; matching a control never grants authority to execute it.</summary>
public static class StrictDesktopTaskProposalParser
{
    private const int MaximumJsonCharacters = 32768;
    private const int MaximumIdCharacters = 128;
    private const int MaximumTextCharacters = 4000;
    private const int MaximumReasonCharacters = 500;
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "snapshotId", "action", "targetId", "text", "reason", "evidenceTargetId", "evidenceText"
    };

    public static DesktopTaskProposal Parse(string json, DesktopTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonCharacters
            || snapshot.Id == Guid.Empty || snapshot.Controls is null)
            throw Invalid();

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Fields.Contains(property.Name) || !seen.Add(property.Name)) throw Invalid();
            if (!seen.SetEquals(Fields)) throw Invalid();

            var snapshotText = String(root, "snapshotId", 36, nullable: false)!;
            if (!Guid.TryParseExact(snapshotText, "D", out var snapshotId) || snapshotId != snapshot.Id)
                throw Invalid();
            var actionText = String(root, "action", 32, nullable: false)!;
            if (!Enum.TryParse<DesktopTaskActionKind>(actionText, ignoreCase: false, out var action)
                || !Enum.IsDefined(action) || action.ToString() != actionText)
                throw Invalid();

            var targetId = String(root, "targetId", MaximumIdCharacters, nullable: true);
            var text = String(root, "text", MaximumTextCharacters, nullable: true, multiline: true);
            var reason = String(root, "reason", MaximumReasonCharacters, nullable: false)!;
            var evidenceId = String(root, "evidenceTargetId", MaximumIdCharacters, nullable: true);
            var evidenceText = String(root, "evidenceText", MaximumTextCharacters, nullable: true, multiline: true);
            if (string.IsNullOrWhiteSpace(reason) || reason != reason.Trim()
                || targetId is not null && !ValidId(targetId)
                || evidenceId is not null && !ValidId(evidenceId))
                throw Invalid();

            var controls = new Dictionary<string, DesktopTaskControl>(StringComparer.Ordinal);
            foreach (var control in snapshot.Controls)
                if (control is null || !ValidId(control.Id) || !controls.TryAdd(control.Id, control))
                    throw Invalid();

            if (action == DesktopTaskActionKind.Finish)
            {
                if (targetId is not null || text is not null || evidenceId is null
                    || string.IsNullOrWhiteSpace(evidenceText)
                    || !controls.TryGetValue(evidenceId, out var evidence)
                    || !HasEvidence(evidence, evidenceText))
                    throw Invalid();
            }
            else
            {
                if (evidenceId is not null || evidenceText is not null) throw Invalid();
                if (action is DesktopTaskActionKind.Wait or DesktopTaskActionKind.Ask)
                {
                    if (targetId is not null || action == DesktopTaskActionKind.Wait && text is not null
                        || action == DesktopTaskActionKind.Ask && string.IsNullOrWhiteSpace(text))
                        throw Invalid();
                }
                else
                {
                    if (targetId is null || !controls.TryGetValue(targetId, out var target)
                        || target.Actions is null || !target.Actions.Contains(action)
                        || action == DesktopTaskActionKind.SetText && (text is null || text.Length > DesktopTaskLimits.MaximumValueLength || target.IsReadOnly)
                        || action != DesktopTaskActionKind.SetText && text is not null)
                        throw Invalid();
                }
            }

            return new DesktopTaskProposal(snapshotId, action, targetId, text, reason, evidenceId, evidenceText);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static string? String(JsonElement root, string name, int maximum, bool nullable, bool multiline = false)
    {
        var value = root.GetProperty(name);
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString()!;
        if (text.Length > maximum || text.Any(character =>
                char.IsControl(character) && !(multiline && character is '\r' or '\n' or '\t')))
            throw Invalid();
        return text;
    }

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumIdCharacters && value == value.Trim() && !value.Any(char.IsControl);

    private static bool HasEvidence(DesktopTaskControl control, string text) =>
        !DesktopTaskSemanticState.IsTechnicalEvidence(text)
        && ((control.Name?.Contains(text, StringComparison.Ordinal) ?? false)
            || (control.Value?.Contains(text, StringComparison.Ordinal) ?? false)
            || DesktopTaskSemanticState.HasCompletionEvidence(control.State, text));

    private static DesktopTaskException Invalid() => new("desktop_task_proposal_invalid",
        "模型没有给出与本次控件快照一致的有效步骤，因此没有执行。请重新说明目标。");
}

// Native State also carries local process/handle/binding metadata. Never send it wholesale.
internal static class DesktopTaskSemanticState
{
    private static readonly HashSet<string> LocalOnlyKeys = new(StringComparer.Ordinal)
        { "pid", "hwnd", "metadata", "officeDocument", "gridBinding" };
    private static readonly HashSet<string> BooleanKeys = new(StringComparer.Ordinal)
        { "enabled", "offscreen", "selected" };
    private static readonly HashSet<string> ExpansionValues = new(StringComparer.Ordinal)
        { "Collapsed", "Expanded", "PartiallyExpanded", "LeafNode" };

    internal static bool TryProject(string? state, out string projected)
    {
        projected = string.Empty;
        if (state is null) return false;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in state.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) return false;
            var key = part[..separator];
            if (!seen.Add(key)) return false;
            if (LocalOnlyKeys.Contains(key)) continue;
            var value = part[(separator + 1)..];
            var valid = BooleanKeys.Contains(key) ? value is "True" or "False" or "true" or "false"
                : key == "expansion" ? ExpansionValues.Contains(value)
                : key is "vertical" or "horizontal"
                    && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                    && double.IsFinite(percent) && percent is >= -1 and <= 100;
            if (!valid) return false;
            result.Add(part);
        }
        projected = string.Join(';', result);
        return true;
    }

    internal static bool IsTechnicalEvidence(string text) =>
        new[] { "enabled", "offscreen", "pid", "hwnd", "metadata", "readonly", "visible" }
            .Any(key => text.Equals(key, StringComparison.OrdinalIgnoreCase)
                || text.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));

    internal static bool HasCompletionEvidence(string? state, string evidence) =>
        TryProject(state, out var projected)
        && projected.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(part =>
            part == evidence && (part.StartsWith("selected=", StringComparison.Ordinal)
                || part.StartsWith("expansion=", StringComparison.Ordinal)
                || part.StartsWith("vertical=", StringComparison.Ordinal)
                || part.StartsWith("horizontal=", StringComparison.Ordinal)));
}
