using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

// Excel's UIA ValuePattern can acknowledge text without changing Range.Value2.
// A recognized Excel cell therefore uses only this bound native single-cell path.
internal static class ExcelDesktopTaskCell
{
    private const string IdentityKey = "officeDocument=";
    private const int MaximumIndexEntries = 2048;
    [ThreadStatic] private static ObservationScope? observationScope;

    // Only Observe enters this synchronous scope. Execute's last action boundary
    // is outside it and always performs a new exact-name uniqueness query.
    internal static IDisposable BeginObservation()
    {
        var scope = new ObservationScope(observationScope);
        observationScope = scope;
        return scope;
    }
    internal static bool HasObservationScope => observationScope is not null;

    internal static bool IsExcelDataItem(DesktopTaskWindow window, DesktopTaskElementMetadata metadata) =>
        window.FrameIdentity is null && window.ClassName == "XLMAIN"
        && window.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase)
        && IsTrustedExecutable(window.ExecutablePath) && metadata.ProcessId == window.ProcessId
        && metadata.ControlType == "DataItem";

    internal static bool IsCandidate(DesktopTaskWindow window, DesktopTaskElementMetadata metadata) =>
        IsExcelDataItem(window, metadata) && DesktopTaskSafety.Usable(metadata)
        && metadata.Name == metadata.AutomationId && TryAddress(metadata.Name, out _, out _);

    internal static bool IsTrustedExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        var full = Path.GetFullPath(path);
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var relative in new[] { @"Microsoft Office\Root\Office16\EXCEL.EXE", @"Microsoft Office\Office16\EXCEL.EXE" })
                if (string.Equals(full, Path.Combine(root, relative), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static bool TryAddress(string address, out int row, out int column)
    {
        row = column = 0;
        var match = Regex.Match(address, @"\A(?<column>[A-Z]{1,3})(?<row>[1-9][0-9]{0,6})\z", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["row"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out row)
            || row > 1_048_576) return false;
        foreach (var c in match.Groups["column"].Value) column = column * 26 + c - 'A' + 1;
        return column <= 16_384;
    }

    internal static DesktopTaskElementDetails Read(AutomationElement cell, AutomationElement root,
        DesktopTaskWindow window, int maximumTextLength)
    {
        try
        {
            using var session = NativeSession.Open(cell, root, window);
            var snapshot = session.Read(maximumTextLength);
            bool? selected = cell.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var rawSelection)
                ? ((SelectionItemPattern)rawSelection).Current.IsSelected : null;
            return ToDetails(snapshot, selected);
        }
        catch (Exception e) when (NativeFailure(e)) { throw Unavailable(); }
    }

    internal static DesktopTaskElementDetails ToDetails(ExcelDesktopTaskCellSnapshot value, bool? selected = null)
    {
        if (!IsHash(value.Identity) || !IsHash(value.GridBinding) || value.Text.Length > DesktopTaskSafety.MaximumValue) throw Unavailable();
        var state = IdentityKey + value.Identity + ";gridBinding=" + value.GridBinding;
        if (selected is not null) state += ";selected=" + selected.Value;
        return new(value.Text, !value.Writable, state,
            value.Writable && !value.Truncated ? [DesktopTaskActionKind.SetText] : [], value.Truncated, true);
    }

    internal static void SetText(AutomationElement cell, AutomationElement root, DesktopTaskWindow window,
        DesktopTaskControl expected, string text, Action verifyContext, Action requireActionBoundary)
    {
        try
        {
            using var session = NativeSession.Open(cell, root, window);
            SetText(session, expected, text, verifyContext, requireActionBoundary);
        }
        catch (Exception e) when (NativeFailure(e)) { throw Unavailable(); }
    }

    internal static void SetText(IExcelDesktopTaskCellSession session, DesktopTaskControl expected,
        string text, Action verifyContext, Action requireActionBoundary)
    {
        RequirePlainText(text);
        var identities = expected.State.Split(';').Where(x => x.StartsWith(IdentityKey, StringComparison.Ordinal)).ToArray();
        if (expected.ControlType != "DataItem" || expected.Name != expected.AutomationId
            || !TryAddress(expected.Name, out _, out _) || expected.IsReadOnly
            || !expected.Actions.Contains(DesktopTaskActionKind.SetText) || identities.Length != 1
            || !IsHash(identities[0][IdentityKey.Length..])) throw Changed();
        var identity = identities[0][IdentityKey.Length..];
        void RequireExpected(ExcelDesktopTaskCellSnapshot value)
        {
            if (!value.Writable || value.Truncated || value.Identity != identity || value.Text != expected.Value) throw Changed();
            DesktopTaskGridCell.RequireBinding(expected.State, value.GridBinding);
        }
        RequireExpected(session.Read(DesktopTaskSafety.MaximumValue));
        verifyContext();
        RequireExpected(session.Read(DesktopTaskSafety.MaximumValue));
        session.Write(text, requireActionBoundary);
        var after = session.Read(DesktopTaskSafety.MaximumValue);
        if (after.Identity != identity || after.Truncated || after.Text != text)
            throw DesktopTaskSafety.Error("excel_cell_not_verified", "单元格已提交，但 Excel 原生值未准确读回，已停止后续操作。");
        DesktopTaskGridCell.RequireBinding(expected.State, after.GridBinding);
    }

    internal static void RequirePlainText(string text)
    {
        DesktopTaskGridCell.RequirePlainText(text);
        var trimmed = text.Trim();
        // Do not silently turn a literal into a date, a rounded number or a
        // different numeric spelling in a General cell. Never change its format.
        if (Regex.IsMatch(trimmed, @"\A[0-9]+(?:[/:-][0-9]+)+\z", RegexOptions.CultureInvariant)
            || Regex.IsMatch(trimmed, @"\A[-.]?[0-9][0-9eE+.,%\- ]*\z", RegexOptions.CultureInvariant)
                && (trimmed != text || trimmed.Count(char.IsDigit) > 15
                    || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || !double.IsFinite(number) || number.ToString("R", CultureInfo.InvariantCulture) != text))
            throw DesktopTaskSafety.Error("excel_cell_text_conversion", "这段数字或日期文字可能被 Excel 自动改写，请使用明确的普通文字或标准数字；未写入。");
    }

    internal static string ScalarText(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        _ => throw DesktopTaskSafety.Error("excel_cell_value_unsupported", "该单元格不是当前支持的普通文字或数字，未自动改写。")
    };

    internal static bool CanWrite(ExcelDesktopTaskCellProtection p, bool truncated) =>
        !truncated && !p.ReadOnly && !p.HasVbaProject && !p.ProtectStructure
        && !p.ProtectContents && !p.ProtectDrawingObjects && !p.ProtectScenarios
        && !p.Merged && !p.HasFormula && !p.HasArray && !p.InTable
        && p.NumberFormat is "General" or "G/通用格式" or "@";

    internal static string Identity(DesktopTaskWindow window, long childHandle, string fullName, string workbookName,
        DateTime created, string sheetName, int sheetIndex, string sheetCodeName, string address, string gridBinding)
    {
        if (childHandle == 0 || string.IsNullOrEmpty(fullName) || fullName.Length > 4096
            || string.IsNullOrEmpty(workbookName) || workbookName.Length > 255 || created.Year < 1900
            || string.IsNullOrEmpty(sheetName) || sheetName.Length > 31 || sheetIndex < 1 || sheetCodeName.Length > 255
            || !TryAddress(address, out _, out _) || !IsHash(gridBinding)) throw Unavailable();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            window.Handle, window.ProcessId, window.ProcessStartedAtUtc, window.ExecutablePath,
            childHandle, fullName, workbookName, CreatedTicks = created.Ticks, sheetName, sheetIndex, sheetCodeName, address, gridBinding
        })));
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool NativeFailure(Exception e) => e is COMException or TargetInvocationException or InvalidComObjectException
        or InvalidCastException or MissingMethodException or ArgumentException or FormatException or OverflowException;
    private static DesktopTaskException Unavailable() => DesktopTaskSafety.Error("excel_cell_unavailable", "无法可靠绑定当前 Excel 单元格的原生值，未使用界面假值或键盘作为替代。");
    private static DesktopTaskException Changed() => DesktopTaskSafety.Error("excel_cell_changed", "Excel 工作簿、工作表、地址、原值或保护状态已变化，未改写单元格。");

    private sealed class NativeSession : IExcelDesktopTaskCellSession
    {
        private readonly AutomationElement cell, root;
        private readonly DesktopTaskWindow window;
        private readonly long childHandle;
        private readonly string address, gridBinding;
        private readonly int row, column;
        private readonly object nativeWindow, sheet, workbook, range;
        private bool ready;

        private NativeSession(AutomationElement cell, AutomationElement root, DesktopTaskWindow window,
            long childHandle, string address, string gridBinding, object nativeWindow, object sheet, object workbook, object range)
        {
            this.cell = cell; this.root = root; this.window = window; this.childHandle = childHandle;
            this.address = address; this.gridBinding = gridBinding; this.nativeWindow = nativeWindow;
            this.sheet = sheet; this.workbook = workbook; this.range = range;
            if (!TryAddress(address, out row, out column)) throw Unavailable();
        }

        internal static NativeSession Open(AutomationElement cell, AutomationElement root, DesktopTaskWindow window)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw Unavailable();
            var address = cell.Current.Name;
            RequireCellBinding(cell, root, window, address, out var gridBinding, requireUnique: true);
            long childHandle = 0;
            var cursor = cell;
            for (var depth = 0; cursor is not null && depth < 64; depth++)
            {
                var handle = cursor.Current.NativeWindowHandle;
                if (handle != 0 && ClassName(new IntPtr(handle)) == "EXCEL7") { childHandle = handle; break; }
                if (cursor.GetRuntimeId().SequenceEqual(root.GetRuntimeId())) break;
                cursor = TreeWalker.RawViewWalker.GetParent(cursor);
            }
            RequireNativeChild(window, childHandle);
            object? native = null, sheet = null, book = null, range = null;
            try
            {
                var iid = new Guid("00020400-0000-0000-C000-000000000046");
                var result = AccessibleObjectFromWindow(new IntPtr(childHandle), 0xFFFFFFF0, ref iid, out native);
                if (result != 0 || native is null || WindowHandle(native) != window.Handle) throw Unavailable();
                sheet = Get(native, "ActiveSheet"); // Scoped to this exact Window; never Application.ActiveSheet.
                if (Convert.ToInt32(Get(sheet, "Type"), CultureInfo.InvariantCulture) != -4167) throw Unavailable();
                book = Get(sheet, "Parent");
                range = Get(sheet, "Range", address);
                // Read performs the complete held-object check before any Value2
                // access. Avoid an identical check while merely constructing it.
                return new NativeSession(cell, root, window, childHandle, address, gridBinding, native, sheet, book, range);
            }
            catch { Release(range); Release(book); Release(sheet); Release(native); throw; }
        }

        public ExcelDesktopTaskCellSnapshot Read(int maximumTextLength)
        {
            ready = false;
            if (maximumTextLength is < 1 or > DesktopTaskSafety.MaximumValue) throw Unavailable();
            RequireBinding();
            var identity = Identity(window, childHandle, Convert.ToString(Get(workbook, "FullName"), CultureInfo.InvariantCulture)!,
                Convert.ToString(Get(workbook, "Name"), CultureInfo.InvariantCulture)!, CreationTime(workbook),
                Convert.ToString(Get(sheet, "Name"), CultureInfo.InvariantCulture)!, Convert.ToInt32(Get(sheet, "Index"), CultureInfo.InvariantCulture),
                Convert.ToString(Get(sheet, "CodeName"), CultureInfo.InvariantCulture)!, address, gridBinding);
            var protection = ReadProtection(workbook, sheet, range);
            var text = ScalarText(GetOptional(range, "Value2"));
            var truncated = text.Length > maximumTextLength;
            if (truncated) text = text[..maximumTextLength];
            RequireBinding();
            ready = CanWrite(protection, truncated);
            return new(identity, gridBinding, text, ready, truncated);
        }

        public void Write(string text, Action requireActionBoundary)
        {
            if (!ready) throw Changed();
            ready = false;
            RequireBinding();
            requireActionBoundary();
            Set(range, "Value2", text);
        }

        private void RequireBinding()
        {
            RequireCellBinding(cell, root, window, address, out var currentBinding);
            if (currentBinding != gridBinding) throw Changed();
            RequireNativeChild(window, childHandle);
            if (WindowHandle(nativeWindow) != window.Handle) throw Changed();
            object? active = null, book = null, parent = null;
            try
            {
                active = Get(nativeWindow, "ActiveSheet"); RequireSameCom(active, sheet);
                book = Get(active, "Parent"); RequireSameCom(book, workbook);
                parent = Get(range, "Parent"); RequireSameCom(parent, sheet);
                var absolute = "$" + Regex.Match(address, "^[A-Z]+").Value + "$" + row.ToString(CultureInfo.InvariantCulture);
                if (Convert.ToString(Get(range, "Address"), CultureInfo.InvariantCulture) != absolute
                    || Convert.ToInt32(Get(range, "Row"), CultureInfo.InvariantCulture) != row
                    || Convert.ToInt32(Get(range, "Column"), CultureInfo.InvariantCulture) != column
                    || Convert.ToInt64(Get(range, "CountLarge"), CultureInfo.InvariantCulture) != 1) throw Changed();
            }
            finally { Release(parent); Release(book); Release(active); }
        }

        public void Dispose() { ready = false; Release(range); Release(workbook); Release(sheet); Release(nativeWindow); }
    }

    private static void RequireCellBinding(AutomationElement cell, AutomationElement root, DesktopTaskWindow expected,
        string address, out string binding, bool requireUnique = false)
    {
        binding = string.Empty;
        var runtime = new WindowsDesktopTaskNativeRuntime();
        var current = runtime.ReadWindow(expected.Handle);
        var metadata = new WindowsDesktopTaskNativeElement(cell, root, current, runtime).Metadata();
        if (!DesktopTaskSafety.SameWindow(expected, current) || !IsCandidate(current, metadata)
            || metadata.Name != address || !TryAddress(address, out var row, out var column)
            || !DesktopTaskGridCell.TryReadBinding(cell, root, current, out binding)) throw Changed();
        // Every newly opened session checks uniqueness, including the fresh
        // session opened by the final native action boundary. While that session
        // holds its exact cell/range, recheck its live bindings without repeatedly
        // scanning the entire window. No result survives a session/worker.
        if (requireUnique)
            RequireUniqueCell(root, current, metadata);
        var grid = ((GridItemPattern)cell.GetCurrentPattern(GridItemPattern.Pattern)).Current;
        // This supported Excel provider includes row/column headers. The address
        // comes from the exact A1 label, and these native coordinates must agree.
        if (grid.Row != row || grid.Column != column || grid.RowSpan != 1 || grid.ColumnSpan != 1) throw Changed();
    }

    private static void RequireUniqueCell(AutomationElement root, DesktopTaskWindow window, DesktopTaskElementMetadata metadata)
    {
        if (observationScope is { } scope)
        {
            var key = FormattableString.Invariant($"{window.Handle}|{window.ProcessId}|{window.ProcessStartedAtUtc:O}|")
                + window.ExecutablePath + "|" + string.Join(',', root.GetRuntimeId());
            if (!scope.Indexes.TryGetValue(key, out var index))
            {
                index = ReadObservationIndex(root, window.ProcessId);
                scope.Indexes.Add(key, index);
            }
            RequireIndexedCell(index, metadata);
            return;
        }
        var matches = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
            new PropertyCondition(AutomationElement.NameProperty, metadata.Name),
            new PropertyCondition(AutomationElement.AutomationIdProperty, metadata.AutomationId)));
        if (matches.Count != 1 || !matches[0].GetRuntimeId().SequenceEqual(metadata.RuntimeId)) throw Changed();
    }

    private static IReadOnlyDictionary<string, int[]> ReadObservationIndex(AutomationElement root, int processId)
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.AutomationIdProperty,
                     AutomationElement.ProcessIdProperty, AutomationElement.RuntimeIdProperty }) cache.Add(property);
        AutomationElementCollection cells;
        using (cache.Activate())
            cells = root.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
        if (cells.Count > MaximumIndexEntries) throw Unavailable();
        var entries = new List<ExcelDesktopTaskCellIndexEntry>(cells.Count);
        foreach (AutomationElement cell in cells)
        {
            var data = cell.Cached;
            if (cell.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) is not int[] runtimeId) throw Changed();
            entries.Add(new(data.Name, data.AutomationId, data.ProcessId, runtimeId));
        }
        return BuildObservationIndex(entries, processId);
    }

    internal static IReadOnlyDictionary<string, int[]> BuildObservationIndex(IReadOnlyList<ExcelDesktopTaskCellIndexEntry> entries, int processId)
    {
        if (entries.Count > MaximumIndexEntries) throw Unavailable();
        var result = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.ProcessId != processId || entry.RuntimeId is not { Length: > 0 and <= 64 }
                || !seen.Add(string.Join(',', entry.RuntimeId))) throw Changed();
            if (entry.Name != entry.AutomationId || !TryAddress(entry.Name, out _, out _)) continue;
            if (!result.TryAdd(entry.Name, entry.RuntimeId.ToArray())) throw Changed();
        }
        return result;
    }

    internal static void RequireIndexedCell(IReadOnlyDictionary<string, int[]> index, DesktopTaskElementMetadata metadata)
    {
        if (metadata.Name != metadata.AutomationId || !index.TryGetValue(metadata.Name, out var runtimeId)
            || !runtimeId.SequenceEqual(metadata.RuntimeId)) throw Changed();
    }

    private sealed class ObservationScope(ObservationScope? previous) : IDisposable
    {
        internal Dictionary<string, IReadOnlyDictionary<string, int[]>> Indexes { get; } = new(StringComparer.Ordinal);
        public void Dispose() { Indexes.Clear(); observationScope = previous; }
    }

    private static void RequireNativeChild(DesktopTaskWindow window, long handle)
    {
        var child = new IntPtr(handle);
        _ = WindowsDesktopTaskNativeRuntime.Native.GetWindowThreadProcessId(child, out var pid);
        if (handle == 0 || pid != window.ProcessId || !WindowsDesktopTaskNativeRuntime.Native.IsWindow(child)
            || ClassName(child) != "EXCEL7" || !WindowsDesktopTaskNativeRuntime.Native.IsChild(new IntPtr(window.Handle), child)
            || WindowsDesktopTaskNativeRuntime.Native.GetAncestor(child, 2).ToInt64() != window.Handle) throw Changed();
    }

    private static ExcelDesktopTaskCellProtection ReadProtection(object workbook, object sheet, object range)
    {
        object? table = null;
        try
        {
            table = GetOptional(range, "ListObject");
            return new(Bool(workbook, "ReadOnly"), Bool(workbook, "HasVBProject"), Bool(workbook, "ProtectStructure"),
                Bool(sheet, "ProtectContents"), Bool(sheet, "ProtectDrawingObjects"), Bool(sheet, "ProtectScenarios"),
                Bool(range, "MergeCells"), Bool(range, "HasFormula"), Bool(range, "HasArray"), table is not null,
                Convert.ToString(Get(range, "NumberFormat"), CultureInfo.InvariantCulture)!);
        }
        finally { Release(table); }
    }

    private static DateTime CreationTime(object workbook)
    {
        object? properties = null, property = null;
        try
        {
            properties = Get(workbook, "BuiltinDocumentProperties"); property = Get(properties, "Item", 11);
            return Convert.ToDateTime(Get(property, "Value"), CultureInfo.InvariantCulture);
        }
        finally { Release(property); Release(properties); }
    }
    private static bool Bool(object target, string property) => Convert.ToBoolean(Get(target, property), CultureInfo.InvariantCulture);
    private static long WindowHandle(object native) { var value = Convert.ToInt64(Get(native, "Hwnd"), CultureInfo.InvariantCulture); return value < 0 ? unchecked((uint)value) : value; }
    private static void RequireSameCom(object expected, object actual)
    {
        var first = Marshal.GetIUnknownForObject(expected); var second = Marshal.GetIUnknownForObject(actual);
        try { if (first != second) throw Changed(); }
        finally { Marshal.Release(second); Marshal.Release(first); }
    }
    private static string ClassName(IntPtr handle)
    {
        var name = new StringBuilder(256); _ = WindowsDesktopTaskNativeRuntime.Native.GetClassName(handle, name, name.Capacity); return name.ToString();
    }
    private static object Get(object target, string name, params object[] args) => GetOptional(target, name, args) ?? throw Unavailable();
    // Excel rejects the invariant LCID (127) for some dispatch properties. Use
    // the installed user locale for COM only; value/identity formatting stays invariant.
    private static object? GetOptional(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args, CultureInfo.CurrentCulture);
    private static void Set(object target, string name, object value) =>
        _ = target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value], CultureInfo.CurrentCulture);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value); }
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? native);
}

internal sealed record ExcelDesktopTaskCellSnapshot(string Identity, string GridBinding, string Text, bool Writable, bool Truncated = false);
internal sealed record ExcelDesktopTaskCellIndexEntry(string Name, string AutomationId, int ProcessId, int[] RuntimeId);
internal sealed record ExcelDesktopTaskCellProtection(bool ReadOnly = false, bool HasVbaProject = false,
    bool ProtectStructure = false, bool ProtectContents = false, bool ProtectDrawingObjects = false, bool ProtectScenarios = false,
    bool Merged = false, bool HasFormula = false, bool HasArray = false, bool InTable = false, string NumberFormat = "General");
internal interface IExcelDesktopTaskCellSession : IDisposable
{
    ExcelDesktopTaskCellSnapshot Read(int maximumTextLength);
    void Write(string text, Action requireActionBoundary);
}
