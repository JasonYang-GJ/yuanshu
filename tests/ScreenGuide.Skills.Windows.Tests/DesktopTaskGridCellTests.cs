using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class DesktopTaskGridCellTests
{
    [Fact]
    public void VerifiedWritableGridCellHasStableLocalBinding()
    {
        Assert.True(DesktopTaskGridCell.TryCreateBinding(Identity(), out var binding));
        Assert.Matches("^[0-9A-F]{64}$", binding);
        Assert.True(DesktopTaskGridCell.TryCreateBinding(Identity(), out var repeated));
        Assert.Equal(binding, repeated);
        DesktopTaskGridCell.RequireBinding("enabled=True;gridBinding=" + binding + ";selected=False", binding);
    }

    [Theory]
    [InlineData("row")]
    [InlineData("column")]
    [InlineData("rowSpan")]
    [InlineData("columnSpan")]
    [InlineData("grid")]
    public void EachGridCoordinateAndContainingGridIsBound(string change)
    {
        var original = Identity();
        var changed = change switch
        {
            "row" => original with { Row = 1 },
            "column" => original with { Column = 1 },
            "rowSpan" => original with { RowSpan = 2 },
            "columnSpan" => original with { ColumnSpan = 2 },
            _ => original with { GridRuntimeId = [4, 99] }
        };
        Assert.True(DesktopTaskGridCell.TryCreateBinding(original, out var before));
        Assert.True(DesktopTaskGridCell.TryCreateBinding(changed, out var after));
        Assert.NotEqual(before, after);
        var failure = Assert.Throws<DesktopTaskException>(() => DesktopTaskGridCell.RequireBinding("gridBinding=" + before, after));
        Assert.Equal("desktop_task_grid_changed", failure.Code);
    }

    [Theory]
    [InlineData("radio")]
    [InlineData("plainDataItem")]
    [InlineData("readOnlyOrMissingValue")]
    [InlineData("cellOutsideRoot")]
    [InlineData("gridOutsideRoot")]
    [InlineData("differentGrid")]
    [InlineData("row")]
    [InlineData("column")]
    [InlineData("rowSpan")]
    [InlineData("columnSpan")]
    [InlineData("rowOverflow")]
    [InlineData("columnOverflow")]
    [InlineData("emptyId")]
    [InlineData("longId")]
    [InlineData("nullId")]
    public void UnsupportedOrUnboundCellsNeverReceiveBinding(string change)
    {
        var original = Identity();
        var invalid = change switch
        {
            "radio" => original with { ControlType = "RadioButton" },
            "plainDataItem" => original with { HasGridItemPattern = false },
            "readOnlyOrMissingValue" => original with { HasWritableValuePattern = false },
            "cellOutsideRoot" => original with { CellBelongsToRoot = false },
            "gridOutsideRoot" => original with { GridBelongsToRoot = false },
            "differentGrid" => original with { CellBelongsToGrid = false },
            "row" => original with { Row = -1 },
            "column" => original with { Column = -1 },
            "rowSpan" => original with { RowSpan = 0 },
            "columnSpan" => original with { ColumnSpan = -1 },
            "rowOverflow" => original with { Row = int.MaxValue },
            "columnOverflow" => original with { Column = int.MaxValue },
            "emptyId" => original with { GridRuntimeId = [] },
            "longId" => original with { GridRuntimeId = new int[65] },
            _ => original with { GridRuntimeId = null! }
        };
        Assert.False(DesktopTaskGridCell.TryCreateBinding(invalid, out var binding));
        Assert.Empty(binding);
    }

    [Theory]
    [InlineData("")]
    [InlineData("gridBinding=")]
    [InlineData("gridBinding=short")]
    [InlineData("GRIDBINDING=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gridBinding=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("gridBinding=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA;gridBinding=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void MissingMalformedOrDuplicateBindingCannotAuthorizeGridInput(string state)
    {
        Assert.False(DesktopTaskGridCell.HasValidBinding(state));
        Assert.Throws<DesktopTaskException>(() => DesktopTaskGridCell.RequireBinding(state, Binding()));
        var details = Details() with { State = state };
        var snapshot = DesktopTaskSafety.SnapshotControl(Guid.NewGuid(), 0, Metadata(), details, out _);
        Assert.DoesNotContain(DesktopTaskActionKind.SetText, snapshot.Actions);
    }

    [Theory]
    [InlineData("DataItem", true, false, true)]
    [InlineData("DataItem", false, false, false)]
    [InlineData("DataItem", true, true, false)]
    [InlineData("RadioButton", true, false, false)]
    [InlineData("RadioButton", false, false, false)]
    [InlineData("ListItem", true, false, false)]
    [InlineData("Edit", false, false, true)]
    [InlineData("ComboBox", false, false, true)]
    [InlineData("Document", false, false, true)]
    public void StructuredCellFlagDoesNotBroadenOtherControlTypes(string type, bool structured, bool readOnly, bool allowed)
    {
        var control = DesktopTaskSafety.SnapshotControl(Guid.NewGuid(), 0, Metadata() with { ControlType = type },
            Details() with { IsStructuredCell = structured, IsReadOnly = readOnly }, out _);
        Assert.Equal(allowed, control.Actions.Contains(DesktopTaskActionKind.SetText));
        Assert.False(DesktopTaskSafety.IsTextInputType("DataItem"));
    }

    [Theory]
    [InlineData("普通文字")]
    [InlineData("hello@example.test")]
    [InlineData("A+B=C")]
    [InlineData("123")]
    [InlineData("-123")]
    [InlineData("-0.25")]
    [InlineData("-.5")]
    [InlineData("  -12.50  ")]
    [InlineData("多行\n说明")]
    [InlineData("'=1+1")]
    [InlineData("")]
    public void LiteralTextAndPlainNumbersAreAllowed(string text) => DesktopTaskGridCell.RequirePlainText(text);

    [Theory]
    [InlineData("=1+1")]
    [InlineData(" =SUM(A1:A2)")]
    [InlineData("\t\r\n=HYPERLINK(\"https://example.test\")")]
    [InlineData("\uFEFF=1+1")]
    [InlineData("\u200B +1")]
    [InlineData("+1")]
    [InlineData("+SUM(A1:A2)")]
    [InlineData("@SUM(A1:A2)")]
    [InlineData("-SUM(A1:A2)")]
    [InlineData("-1+2")]
    [InlineData("-1e2")]
    [InlineData("--1")]
    [InlineData("-1,000")]
    [InlineData("-cmd|' /C example'!A0")]
    [InlineData("cmd /c example")]
    [InlineData("powershell.exe -Command example")]
    [InlineData("pwsh -Command example")]
    [InlineData("javascript:example()")]
    [InlineData("vbscript:example")]
    [InlineData("mshta example")]
    [InlineData("file:C:/example")]
    [InlineData("shell:example")]
    public void FormulaAndCommandPrefixesAreRejected(string text)
    {
        var failure = Assert.Throws<DesktopTaskException>(() => DesktopTaskGridCell.RequirePlainText(text));
        Assert.Equal("desktop_task_grid_formula_not_allowed", failure.Code);
    }

    [Fact]
    public void GridTextRetainsLengthAndControlCharacterLimits()
    {
        Assert.Throws<DesktopTaskException>(() => DesktopTaskGridCell.RequirePlainText(null));
        Assert.Throws<DesktopTaskException>(() => DesktopTaskGridCell.RequirePlainText(new string('a', DesktopTaskLimits.MaximumValueLength + 1)));
        Assert.Throws<DesktopTaskException>(() => DesktopTaskGridCell.RequirePlainText("text\0"));
    }

    [Theory]
    [InlineData("测试文本")]
    [InlineData("-12.5")]
    public void WorkerWritesAndReadsBackExactlyBoundStructuredCell(string text)
    {
        var runtime = new GridRuntime();
        var (snapshot, control, context) = Observe(runtime);
        var result = Execute(runtime, snapshot, control, context, text);
        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Receipt!.Verified);
        Assert.Equal(1, runtime.Cell.Writes);
        Assert.Equal(text, runtime.Cell.DetailsValue.Value);
    }

    [Theory]
    [InlineData("formula")]
    [InlineData("gridChanged")]
    [InlineData("lostStructuredPattern")]
    [InlineData("readOnly")]
    [InlineData("oldValueChanged")]
    [InlineData("changedAtFinalBoundary")]
    public void WorkerRejectsUnsafeOrChangedCellBeforeAnyWrite(string change)
    {
        var runtime = new GridRuntime();
        var (snapshot, control, context) = Observe(runtime);
        switch (change)
        {
            case "gridChanged": runtime.Cell.DetailsValue = Details() with { State = "gridBinding=" + new string('A', 64) }; break;
            case "lostStructuredPattern": runtime.Cell.DetailsValue = Details() with { IsStructuredCell = false }; break;
            case "readOnly": runtime.Cell.DetailsValue = Details() with { IsReadOnly = true }; break;
            case "oldValueChanged": runtime.Cell.DetailsValue = Details() with { Value = "新内容" }; break;
            case "changedAtFinalBoundary": runtime.Cell.BeforeContextCheck = () => runtime.Cell.DetailsValue = Details() with { State = "gridBinding=" + new string('A', 64) }; break;
        }
        var result = Execute(runtime, snapshot, control, context, change == "formula" ? "=1+1" : "文字");
        Assert.False(result.Succeeded);
        Assert.Equal(0, runtime.Cell.Writes);
    }

    private static DesktopTaskGridCellIdentity Identity() => new("DataItem", true, true, true, true, true, 0, 0, 1, 1, [4, 17]);
    private static string Binding()
    {
        Assert.True(DesktopTaskGridCell.TryCreateBinding(Identity(), out var binding));
        return binding;
    }
    private static DesktopTaskElementMetadata Metadata() => new([2], "A1", "A1", "DataItem", "合成网格",
        new(1, 1, 80, 20), true, false, false, 200, 0);
    private static DesktopTaskElementDetails Details() => new("", false, "gridBinding=" + Binding(),
        [DesktopTaskActionKind.SetText], IsStructuredCell: true);

    private static (DesktopTaskSnapshot Snapshot, DesktopTaskControl Control, string Context) Observe(GridRuntime runtime)
    {
        var response = new WindowsDesktopTaskWorker(runtime).Execute(new("Observe", runtime.Window.Handle, runtime.Window));
        Assert.True(response.Succeeded, response.Message);
        var snapshot = response.Snapshot!;
        return (snapshot, Assert.Single(snapshot.Controls, item => item.AutomationId == "A1"), DesktopTaskContextFingerprint.Compute(snapshot));
    }
    private static DesktopTaskWorkerResponse Execute(GridRuntime runtime, DesktopTaskSnapshot snapshot,
        DesktopTaskControl control, string context, string text) => new WindowsDesktopTaskWorker(runtime).Execute(
            new("Execute", runtime.Window.Handle, runtime.Window, control,
                new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, text, "合成测试"), context));

    private sealed class GridRuntime : IDesktopTaskNativeRuntime
    {
        public DesktopTaskWindow Window { get; } = new(100, 200, "fixture", DateTimeOffset.Parse("2026-11-04T00:00:00Z"),
            @"C:\Fixture\fixture.exe", "FixtureWindow", "合成测试");
        public GridElement Cell { get; } = new();
        public long ForegroundWindowHandle => Window.Handle;
        public DesktopTaskWindow ReadWindow(long handle) => Window;
        public IDesktopTaskNativeElement Root(long handle) => Cell;
        public bool BelongsToWindow(IDesktopTaskNativeElement element, IDesktopTaskNativeElement root, long handle) => true;
    }

    private sealed class GridElement : IDesktopTaskNativeElement
    {
        public DesktopTaskElementDetails DetailsValue = DesktopTaskGridCellTests.Details();
        public int Writes;
        public Action? BeforeContextCheck;
        public DesktopTaskElementMetadata Metadata() => DesktopTaskGridCellTests.Metadata();
        public DesktopTaskElementDetails Details(int maximumTextLength) => DetailsValue;
        public IEnumerable<IDesktopTaskNativeElement> Children() => [];
        public void Perform(DesktopTaskActionKind action, string? text, DesktopTaskControl expected, Action verifyContext)
        {
            BeforeContextCheck?.Invoke();
            verifyContext();
            Writes++;
            DetailsValue = DetailsValue with { Value = text! };
        }
    }
}
