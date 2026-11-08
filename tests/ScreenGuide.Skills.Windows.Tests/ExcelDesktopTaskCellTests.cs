using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class ExcelDesktopTaskCellTests
{
    private static readonly string Document = new('A', 64);
    private static readonly string Grid = new('B', 64);
    private static readonly DesktopTaskWindow Window = new(10, 20, "EXCEL", DateTimeOffset.Parse("2026-11-04T10:00:00Z"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft Office\Root\Office16\EXCEL.EXE"), "XLMAIN", "合成工作簿");

    [Theory]
    [InlineData("A1", 1, 1)]
    [InlineData("B2", 2, 2)]
    [InlineData("AA20", 20, 27)]
    [InlineData("XFD1048576", 1048576, 16384)]
    public void A1AddressIsParsedWithoutUsingHeaderInclusiveGridOffsets(string address, int row, int column)
    {
        Assert.True(ExcelDesktopTaskCell.TryAddress(address, out var actualRow, out var actualColumn));
        Assert.Equal(row, actualRow);
        Assert.Equal(column, actualColumn);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("A0")]
    [InlineData("A01")]
    [InlineData("a1")]
    [InlineData("$A$1")]
    [InlineData("A1:B2")]
    [InlineData("Sheet1!B2")]
    [InlineData("[other.xlsx]Sheet1!B2")]
    [InlineData("XFE1")]
    [InlineData("A1048577")]
    [InlineData("B1\n")]
    public void AnAddressCannotSelectAnotherSheetRangeOrOutsideCell(string address) =>
        Assert.False(ExcelDesktopTaskCell.TryAddress(address, out _, out _));

    [Fact]
    public void CandidateRequiresTrustedOfficeAndMatchingCellLabels()
    {
        var metadata = Metadata();
        Assert.True(ExcelDesktopTaskCell.IsCandidate(Window, metadata));
        Assert.False(ExcelDesktopTaskCell.IsCandidate(Window with { ExecutablePath = @"C:\Fixture\EXCEL.EXE" }, metadata));
        Assert.False(ExcelDesktopTaskCell.IsCandidate(Window with { ClassName = "OtherWindow" }, metadata));
        Assert.False(ExcelDesktopTaskCell.IsCandidate(Window, metadata with { ProcessId = 21 }));
        Assert.False(ExcelDesktopTaskCell.IsCandidate(Window, metadata with { AutomationId = "C2" }));
        Assert.False(ExcelDesktopTaskCell.IsCandidate(Window, metadata with { Name = "B", AutomationId = "B" }));
        Assert.False(ExcelDesktopTaskCell.IsCandidate(Window, metadata with { Offscreen = true }));
        Assert.False(ExcelDesktopTaskCell.IsCandidate(Window, metadata with { Password = true }));
        Assert.False(ExcelDesktopTaskCell.IsCandidate(Window, metadata with { ControlType = "Edit" }));
        // A recognized Excel header remains in the adapter's no-UIA-fallback scope.
        Assert.True(ExcelDesktopTaskCell.IsExcelDataItem(Window, metadata with { Name = "B", AutomationId = "B" }));
    }

    [Fact]
    public void ObservationIndexNeverSurvivesTheObservationOrAnException()
    {
        Assert.False(ExcelDesktopTaskCell.HasObservationScope);
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var outer = ExcelDesktopTaskCell.BeginObservation();
            Assert.True(ExcelDesktopTaskCell.HasObservationScope);
            using (ExcelDesktopTaskCell.BeginObservation()) Assert.True(ExcelDesktopTaskCell.HasObservationScope);
            Assert.True(ExcelDesktopTaskCell.HasObservationScope);
            throw new InvalidOperationException();
        }));
        Assert.False(ExcelDesktopTaskCell.HasObservationScope);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateAddressOrRepeatedRuntimeIdCannotEnterAnObservationIndex(bool repeatRuntimeId)
    {
        var entries = new[] { new ExcelDesktopTaskCellIndexEntry("B2", "B2", 20, [1, 2]),
            new ExcelDesktopTaskCellIndexEntry(repeatRuntimeId ? "C2" : "B2", repeatRuntimeId ? "C2" : "B2", 20, repeatRuntimeId ? [1, 2] : [3, 4]) };
        Assert.Throws<DesktopTaskException>(() => ExcelDesktopTaskCell.BuildObservationIndex(entries, 20));
    }

    [Fact]
    public void AnIndexedNameAloneCannotAuthorizeADifferentLiveRuntimeId()
    {
        var original = new[] { 1, 2 };
        var index = ExcelDesktopTaskCell.BuildObservationIndex([new("B2", "B2", 20, original)], 20);
        original[0] = 99;
        ExcelDesktopTaskCell.RequireIndexedCell(index, Metadata());
        Assert.Throws<DesktopTaskException>(() => ExcelDesktopTaskCell.RequireIndexedCell(index, Metadata() with { RuntimeId = [99, 2] }));
        Assert.Throws<DesktopTaskException>(() => ExcelDesktopTaskCell.RequireIndexedCell(index, Metadata() with { AutomationId = "C2" }));
        Assert.Throws<DesktopTaskException>(() => ExcelDesktopTaskCell.BuildObservationIndex([new("B2", "B2", 21, [1, 2])], 20));
    }

    [Theory]
    [InlineData("General")]
    [InlineData("G/通用格式")]
    [InlineData("@")]
    public void OnlyOrdinarySupportedCellFormatsAllowWrites(string format) =>
        Assert.True(ExcelDesktopTaskCell.CanWrite(new(NumberFormat: format), false));

    public static IEnumerable<object[]> UnsafeProtection()
    {
        yield return [new ExcelDesktopTaskCellProtection(ReadOnly: true)];
        yield return [new ExcelDesktopTaskCellProtection(HasVbaProject: true)];
        yield return [new ExcelDesktopTaskCellProtection(ProtectStructure: true)];
        yield return [new ExcelDesktopTaskCellProtection(ProtectContents: true)];
        yield return [new ExcelDesktopTaskCellProtection(ProtectDrawingObjects: true)];
        yield return [new ExcelDesktopTaskCellProtection(ProtectScenarios: true)];
        yield return [new ExcelDesktopTaskCellProtection(Merged: true)];
        yield return [new ExcelDesktopTaskCellProtection(HasFormula: true)];
        yield return [new ExcelDesktopTaskCellProtection(HasArray: true)];
        yield return [new ExcelDesktopTaskCellProtection(InTable: true)];
        yield return [new ExcelDesktopTaskCellProtection(NumberFormat: "yyyy-mm-dd")];
        yield return [new ExcelDesktopTaskCellProtection(NumberFormat: "0.00")];
    }
    [Theory]
    [MemberData(nameof(UnsafeProtection))]
    public void ExistingRichOrProtectedCellCannotBeOverwritten(object value) =>
        Assert.False(ExcelDesktopTaskCell.CanWrite(Assert.IsType<ExcelDesktopTaskCellProtection>(value), false));

    [Fact]
    public void LongOldValueCannotAuthorizeAReplacement()
    {
        Assert.False(ExcelDesktopTaskCell.CanWrite(new(), true));
        var details = ExcelDesktopTaskCell.ToDetails(new(Document, Grid, "clipped", false, true));
        Assert.True(details.TextTruncated);
        Assert.True(details.IsReadOnly);
        Assert.Empty(details.Actions);
    }

    [Fact]
    public void NativeValueProjectionKeepsSelectionChangesInTheCompleteContext()
    {
        var before = ExcelDesktopTaskCell.ToDetails(Snapshot(""), false);
        var after = ExcelDesktopTaskCell.ToDetails(Snapshot(""), true);
        Assert.Contains("selected=False", before.State);
        Assert.Contains("selected=True", after.State);
        var first = DesktopTaskSafety.SnapshotControl(Guid.NewGuid(), 0, Metadata(), before, out _);
        var second = DesktopTaskSafety.SnapshotControl(Guid.NewGuid(), 0, Metadata(), after, out _);
        Assert.False(DesktopTaskSafety.SameControl(first, second));
        Assert.NotEqual(DesktopTaskContextFingerprint.Compute(new(Guid.NewGuid(), Window, [first], false)),
            DesktopTaskContextFingerprint.Compute(new(Guid.NewGuid(), Window, [second], false)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("安装版验收")]
    [InlineData("测试0012")]
    [InlineData("42")]
    [InlineData("-2")]
    [InlineData("0.5")]
    public void OrdinaryTextAndCanonicalNumbersAreAccepted(string text) => ExcelDesktopTaskCell.RequirePlainText(text);

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("@SUM(A1)")]
    [InlineData("powershell.exe whoami")]
    [InlineData("0012")]
    [InlineData("1e3")]
    [InlineData("1.00")]
    [InlineData("1,000")]
    [InlineData("2026-11-05")]
    [InlineData("1/2")]
    [InlineData("1234567890123456")]
    [InlineData(" 42 ")]
    public void FormulaOrAmbiguousConversionIsRejectedBeforeOpeningAWrite(string text) =>
        Assert.Throws<DesktopTaskException>(() => ExcelDesktopTaskCell.RequirePlainText(text));

    [Fact]
    public void NativeScalarReadingDoesNotTreatErrorArrayBoolOrDateAsPlainText()
    {
        Assert.Equal("", ExcelDesktopTaskCell.ScalarText(null));
        Assert.Equal("0012", ExcelDesktopTaskCell.ScalarText("0012"));
        Assert.Equal("42", ExcelDesktopTaskCell.ScalarText(42d));
        foreach (var value in new object[] { true, 2042, new object[1, 1], DateTime.UtcNow, double.NaN, double.PositiveInfinity })
            Assert.Throws<DesktopTaskException>(() => ExcelDesktopTaskCell.ScalarText(value));
    }

    [Fact]
    public void StableIdentityBindsWorkbookCreationSheetAddressAndGridWithoutInventingCodeName()
    {
        var created = new DateTime(2026, 11, 4, 18, 0, 0);
        string Identity(string fullName = "工作簿1", string sheet = "Sheet1", int index = 1,
            string address = "B2", string? grid = null, DateTime? creation = null) =>
            ExcelDesktopTaskCell.Identity(Window, 30, fullName, "工作簿1", creation ?? created, sheet, index, "", address, grid ?? Grid);
        var baseline = Identity();
        Assert.Equal(64, baseline.Length);
        Assert.Equal(baseline, Identity());
        Assert.NotEqual(baseline, Identity(fullName: @"C:\Fixture\other.xlsx"));
        Assert.NotEqual(baseline, Identity(sheet: "Sheet2"));
        Assert.NotEqual(baseline, Identity(index: 2));
        Assert.NotEqual(baseline, Identity(address: "C2"));
        Assert.NotEqual(baseline, Identity(grid: new string('C', 64)));
        Assert.NotEqual(baseline, Identity(creation: created.AddSeconds(1)));
        Assert.Throws<DesktopTaskException>(() => Identity(creation: DateTime.MinValue));
    }

    [Fact]
    public void WriteRequiresFreshSameCellAfterContextAndUsesNativeReadback()
    {
        var trace = new List<string>();
        var session = new FakeSession(trace, Snapshot("old"), Snapshot("old"), Snapshot("new"));
        ExcelDesktopTaskCell.SetText(session, Control("old"), "new", () => trace.Add("context"), () => trace.Add("boundary"));
        Assert.Equal(new[] { "read", "context", "read", "boundary", "write:new", "read" }, trace);
        Assert.Equal(1, session.Writes);
    }

    [Theory]
    [InlineData("sheet")]
    [InlineData("grid")]
    [InlineData("old-value")]
    [InlineData("protection")]
    [InlineData("truncated")]
    public void AChangeDuringContextVerificationPreventsWriting(string change)
    {
        var changed = change switch
        {
            "sheet" => Snapshot("old") with { Identity = new string('C', 64) },
            "grid" => Snapshot("old") with { GridBinding = new string('C', 64) },
            "old-value" => Snapshot("changed by user"),
            "protection" => Snapshot("old") with { Writable = false },
            _ => Snapshot("old") with { Truncated = true }
        };
        var session = new FakeSession([], Snapshot("old"), changed);
        Assert.Throws<DesktopTaskException>(() => ExcelDesktopTaskCell.SetText(session, Control("old"), "new", () => { }, () => { }));
        Assert.Equal(0, session.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopAtContextOrLastBoundaryPreventsNativeWrite(bool atBoundary)
    {
        var session = new FakeSession([], Snapshot("old"), Snapshot("old"));
        Assert.Throws<OperationCanceledException>(() => ExcelDesktopTaskCell.SetText(session, Control("old"), "new",
            () => { if (!atBoundary) throw new OperationCanceledException(); },
            () => { if (atBoundary) throw new OperationCanceledException(); }));
        Assert.Equal(0, session.Writes);
    }

    [Fact]
    public void AProviderAcknowledgementCannotReplaceNativeValueVerification()
    {
        var session = new FakeSession([], Snapshot("old"), Snapshot("old"), Snapshot("old"));
        var error = Assert.Throws<DesktopTaskException>(() => ExcelDesktopTaskCell.SetText(session, Control("old"), "new", () => { }, () => { }));
        Assert.Equal("desktop_task_excel_cell_not_verified", error.Code);
        Assert.Equal(1, session.Writes);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("identity")]
    [InlineData("readonly")]
    public void InvalidExpectedCellNeverReachesSessionWrite(string change)
    {
        var expected = change switch
        {
            "address" => Control("old") with { AutomationId = "C2" },
            "identity" => Control("old") with { State = "gridBinding=" + Grid },
            _ => Control("old") with { IsReadOnly = true }
        };
        var session = new FakeSession([], Snapshot("old"));
        Assert.Throws<DesktopTaskException>(() => ExcelDesktopTaskCell.SetText(session, expected, "new", () => { }, () => { }));
        Assert.Equal(0, session.Writes);
        Assert.Equal(0, session.Reads);
    }

    private static ExcelDesktopTaskCellSnapshot Snapshot(string text) => new(Document, Grid, text, true);
    private static DesktopTaskElementMetadata Metadata() => new([1, 2], "B2", "B2", "DataItem", "grid",
        new(1, 1, 50, 20), true, false, false, Window.ProcessId, 0);
    private static DesktopTaskControl Control(string value) => new("cell", [1, 2], "B2", "B2", "DataItem", "grid", value,
        "officeDocument=" + Document + ";gridBinding=" + Grid, false, new(1, 1, 50, 20), [DesktopTaskActionKind.SetText]);
    private sealed class FakeSession(List<string> trace, params ExcelDesktopTaskCellSnapshot[] snapshots) : IExcelDesktopTaskCellSession
    {
        private readonly Queue<ExcelDesktopTaskCellSnapshot> pending = new(snapshots);
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public ExcelDesktopTaskCellSnapshot Read(int maximumTextLength) { Reads++; trace.Add("read"); return pending.Dequeue(); }
        public void Write(string text, Action boundary) { boundary(); Writes++; trace.Add("write:" + text); }
        public void Dispose() { }
    }
}
