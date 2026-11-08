using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

// This describes a local UIA binding, not authority supplied by a model. No cell
// value is read here; callers still apply sensitivity and original-value checks.
internal sealed record DesktopTaskGridCellIdentity(string ControlType, bool HasGridItemPattern,
    bool HasWritableValuePattern, bool CellBelongsToRoot, bool GridBelongsToRoot, bool CellBelongsToGrid,
    int Row, int Column, int RowSpan, int ColumnSpan, int[] GridRuntimeId);

internal static class DesktopTaskGridCell
{
    private const string BindingKey = "gridBinding=";
    private static readonly Regex NegativeNumber = new(@"\A-(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex CommandPrefix = new(
        @"\A(?:javascript:|vbscript:|powershell(?:\.exe)?\b|pwsh(?:\.exe)?\b|cmd(?:\.exe)?(?:\s|$)|mshta\b|file:|shell:)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool TryReadBinding(AutomationElement element, AutomationElement root,
        DesktopTaskWindow window, out string binding)
    {
        binding = string.Empty;
        var current = element.Current;
        if (current.ControlType != ControlType.DataItem || current.IsPassword
            || current.ProcessId != window.ProcessId
            || !element.TryGetCurrentPattern(GridItemPattern.Pattern, out var rawGrid)
            || rawGrid is not GridItemPattern item
            || !element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawValue)
            || rawValue is not ValuePattern value || value.Current.IsReadOnly) return false;
        var before = item.Current;
        var grid = before.ContainingGrid;
        if (grid is null || grid.Current.ProcessId != window.ProcessId) return false;
        var gridId = grid.GetRuntimeId();
        var identity = new DesktopTaskGridCellIdentity("DataItem", true, true,
            WindowsDesktopTaskNativeRuntime.Native.Belongs(element, root, window),
            WindowsDesktopTaskNativeRuntime.Native.Belongs(grid, root, window),
            IsDescendantOf(element, grid), before.Row, before.Column, before.RowSpan, before.ColumnSpan, gridId);
        if (!TryCreateBinding(identity, out var candidate)) return false;
        // A provider may replace a row or grid while properties are being read.
        // Re-read the relationship without reading Name, Value or Text.
        var after = item.Current;
        if (value.Current.IsReadOnly || element.Current.ControlType != ControlType.DataItem
            || element.Current.IsPassword || element.Current.ProcessId != window.ProcessId
            || after.Row != before.Row || after.Column != before.Column
            || after.RowSpan != before.RowSpan || after.ColumnSpan != before.ColumnSpan
            || after.ContainingGrid is not { } currentGrid
            || !currentGrid.GetRuntimeId().SequenceEqual(gridId)
            || !WindowsDesktopTaskNativeRuntime.Native.Belongs(element, root, window)
            || !WindowsDesktopTaskNativeRuntime.Native.Belongs(currentGrid, root, window)
            || !IsDescendantOf(element, currentGrid)) return false;
        binding = candidate;
        return true;
    }

    internal static bool TryCreateBinding(DesktopTaskGridCellIdentity identity, out string binding)
    {
        binding = string.Empty;
        if (identity.ControlType != "DataItem" || !identity.HasGridItemPattern || !identity.HasWritableValuePattern
            || !identity.CellBelongsToRoot || !identity.GridBelongsToRoot || !identity.CellBelongsToGrid
            || identity.GridRuntimeId is not { Length: > 0 and <= 64 }
            || identity.Row < 0 || identity.Column < 0 || identity.RowSpan <= 0 || identity.ColumnSpan <= 0
            || (long)identity.Row + identity.RowSpan > int.MaxValue
            || (long)identity.Column + identity.ColumnSpan > int.MaxValue) return false;
        var grid = string.Join(',', identity.GridRuntimeId.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        var encoded = FormattableString.Invariant($"grid-cell-v1|{identity.GridRuntimeId.Length}|{grid}|{identity.Row}|{identity.Column}|{identity.RowSpan}|{identity.ColumnSpan}");
        binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encoded)));
        return true;
    }

    internal static bool HasValidBinding(string state) => TryGetBinding(state, out _);

    internal static void RequireBinding(string expectedState, string actualBinding)
    {
        if (!TryGetBinding(expectedState, out var expected) || !IsHash(actualBinding)
            || !string.Equals(expected, actualBinding, StringComparison.Ordinal))
            throw DesktopTaskSafety.Error("grid_changed", "单元格所属表格、行列或合并范围已变化，未输入内容。");
    }

    internal static void RequirePlainText(string? text)
    {
        DesktopTaskSafety.RequireText(text);
        var trimmed = text!.Trim();
        // Leading formatting marks must not disguise a formula prefix.
        var start = 0;
        while (start < trimmed.Length && (char.IsWhiteSpace(trimmed[start])
            || char.GetUnicodeCategory(trimmed[start]) == UnicodeCategory.Format)) start++;
        trimmed = trimmed[start..];
        if (trimmed.Length > 0 && (trimmed[0] is '=' or '+' or '@'
            || trimmed[0] == '-' && !NegativeNumber.IsMatch(trimmed)
            || CommandPrefix.IsMatch(trimmed)))
            throw DesktopTaskSafety.Error("grid_formula_not_allowed", "网格单元格只允许填写普通文字或数字，不执行公式或命令表达式。");
    }

    private static bool TryGetBinding(string state, out string binding)
    {
        binding = string.Empty;
        foreach (var item in state.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!item.StartsWith(BindingKey, StringComparison.Ordinal)) continue;
            if (binding.Length > 0 || !IsHash(item[BindingKey.Length..])) return false;
            binding = item[BindingKey.Length..];
        }
        return binding.Length > 0;
    }

    private static bool IsHash(string value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsDescendantOf(AutomationElement element, AutomationElement grid)
    {
        var gridId = grid.GetRuntimeId();
        if (gridId is not { Length: > 0 and <= 64 }) return false;
        var cursor = TreeWalker.RawViewWalker.GetParent(element);
        for (var depth = 0; cursor is not null && depth < 64; depth++)
        {
            if (cursor.GetRuntimeId().SequenceEqual(gridId)) return true;
            cursor = TreeWalker.RawViewWalker.GetParent(cursor);
        }
        return false;
    }
}
