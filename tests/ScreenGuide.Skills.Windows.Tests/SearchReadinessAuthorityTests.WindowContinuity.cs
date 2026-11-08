namespace ScreenGuide.Skills.Windows.Tests;

public sealed partial class SearchReadinessAuthorityTests
{
    [Theory]
    [InlineData("discovery")]
    [InlineData("focus")]
    [InlineData("write")]
    [InlineData("submit")]
    [InlineData("result-readback")]
    public void BoundApplicationTitleChangesDoNotInterruptOneSearch(string boundary)
    {
        var setup = new Harness();
        setup.Prepare();
        Action changeTitle = () => setup.Windows.Current = setup.Windows.Current with
        {
            WindowTitle = "合成播放器 - 已完成加载"
        };
        switch (boundary)
        {
            case "discovery": setup.Input.OnDiscover = changeTitle; break;
            case "focus": setup.Input.AfterFocus = changeTitle; break;
            case "write": setup.Input.AfterWrite = changeTitle; break;
            case "submit": setup.Input.BeforeSubmit = changeTitle; break;
        }

        setup.Search();
        if (boundary == "result-readback") setup.Input.OnRead = changeTitle;

        Assert.Equal(SearchResultPageState.Results, setup.ReadResults().State);
        Assert.True(setup.VerifyReceipt());
        Assert.Equal(1, setup.Input.Discoveries);
        Assert.Equal(1, setup.Input.Focuses);
        Assert.Equal(1, setup.Input.Writes);
        Assert.Equal(1, setup.Input.Submissions);
        Assert.Equal(Query, setup.Input.LastWrittenValue);
    }

    [Fact]
    public void UnboundForegroundSearchKeepsItsStrictTitleBoundary()
    {
        var windows = new Windows();
        var input = new InputDriver(windows);
        var service = new WindowsUiAutomationService(windows, input);
        input.OnDiscover = () => windows.Current = Window with { WindowTitle = "其他页面" };

        var failure = Assert.Throws<WindowIdentityException>(() => service.Search(Window, Query));

        Assert.Equal(WindowIdentityErrorCodes.Changed, failure.Code);
        AssertNoInput(input);
    }

    public static IEnumerable<object[]> TargetChangesAtInputBoundaries =>
        from change in new[] { "handle", "pid", "process-start", "process-name", "executable", "foreground" }
        from afterWrite in new[] { false, true }
        select new object[] { change, afterWrite };

    [Theory]
    [MemberData(nameof(TargetChangesAtInputBoundaries))]
    public void TitleContinuityNeverTransfersInputToChangedTarget(string change, bool afterWrite)
    {
        var setup = new Harness();
        setup.Prepare();
        Action changeTarget = () =>
        {
            setup.Windows.Current = setup.Windows.Current with { WindowTitle = "加载完成" };
            switch (change)
            {
                case "handle": setup.Windows.Current = setup.Windows.Current with { WindowHandle = 43 }; break;
                case "pid": setup.Windows.Current = setup.Windows.Current with { ProcessId = 456 }; break;
                case "process-start": setup.Windows.Current = setup.Windows.Current with { ProcessStartTimeUtc = Window.ProcessStartTimeUtc.AddSeconds(1) }; break;
                case "process-name": setup.Windows.Current = setup.Windows.Current with { ProcessName = "other" }; break;
                case "executable": setup.Windows.Executable = @"C:\fixtures\other.exe"; break;
                case "foreground": setup.Windows.Foreground = 43; break;
            }
        };
        if (afterWrite) setup.Input.AfterWrite = changeTarget;
        else setup.Input.OnDiscover = changeTarget;

        var failure = Assert.Throws<ApplicationSearchWorkflowException>(() => setup.Search());

        Assert.Equal("application_search_window_changed", failure.Code);
        Assert.Equal(1, setup.Input.Discoveries);
        Assert.Equal(afterWrite ? 1 : 0, setup.Input.Writes);
        Assert.Equal(0, setup.Input.Submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalWorkflowTargetValidatorRunsAgainAfterDiscoveryAndWrite(bool afterWrite)
    {
        var setup = new Harness();
        setup.Prepare();
        var registrationChanged = false;
        setup.ValidateTarget = () =>
        {
            if (registrationChanged)
                throw new ApplicationSearchWorkflowException("application_search_target_changed", "合成应用注册已变化");
        };
        Action replaceRegistration = () => registrationChanged = true;
        if (afterWrite) setup.Input.AfterWrite = replaceRegistration;
        else setup.Input.OnDiscover = replaceRegistration;

        var failure = Assert.Throws<ApplicationSearchWorkflowException>(() => setup.Search());

        Assert.Equal("application_search_target_changed", failure.Code);
        Assert.Equal(1, setup.Input.Discoveries);
        Assert.Equal(afterWrite ? 1 : 0, setup.Input.Writes);
        Assert.Equal(0, setup.Input.Submissions);
    }

    [Fact]
    public void ApplicationIdentityChangedInsideTheFinalControlReadPreventsWriting()
    {
        var setup = new Harness();
        setup.Prepare();
        setup.Input.OnRead = () =>
        {
            if (setup.Input.Focuses == 1 && setup.Input.Writes == 0)
                setup.Windows.Executable = @"C:\fixtures\replacement.exe";
        };

        Assert.Throws<ApplicationSearchWorkflowException>(() => setup.Search());

        Assert.Equal(1, setup.Input.Focuses);
        Assert.Equal(0, setup.Input.Writes);
        Assert.Equal(0, setup.Input.Submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationWhileTheBoundTitleChangesNeverResumesInput(bool afterWrite)
    {
        using var cancellation = new CancellationTokenSource();
        var setup = new Harness();
        setup.Prepare(cancellation.Token);
        Action cancelAndChangeTitle = () =>
        {
            setup.Windows.Current = Window with { WindowTitle = "加载完成" };
            cancellation.Cancel();
        };
        if (afterWrite) setup.Input.AfterWrite = cancelAndChangeTitle;
        else setup.Input.OnDiscover = cancelAndChangeTitle;

        Assert.ThrowsAny<OperationCanceledException>(() => setup.Search(cancellation.Token));

        Assert.Equal(1, setup.Input.Discoveries);
        Assert.Equal(afterWrite ? 1 : 0, setup.Input.Writes);
        Assert.Equal(0, setup.Input.Submissions);
    }

    [Fact]
    public void SubmittedReceiptAlsoRevalidatesTheOriginalApplicationTarget()
    {
        var setup = new Harness();
        var targetChanged = false;
        setup.ValidateTarget = () =>
        {
            if (targetChanged)
                throw new ApplicationSearchWorkflowException("application_search_target_changed", "合成应用注册已变化");
        };
        setup.Prepare();
        setup.Search();
        targetChanged = true;

        Assert.False(setup.VerifyReceipt());
        Assert.Equal(SearchResultPageState.Unverified, setup.ReadResults().State);
        Assert.Equal(1, setup.Input.Discoveries);
        Assert.Equal(1, setup.Input.Writes);
        Assert.Equal(1, setup.Input.Submissions);
    }
}
