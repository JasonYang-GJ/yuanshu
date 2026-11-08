using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class DesktopTaskWorkerTests
{
    [Fact]
    public void BindingReadsOnlyMetadataAndDoesNotNeedForeground()
    {
        var runtime = new FakeRuntime { Foreground = 999 };
        var result = new WindowsDesktopTaskWorker(runtime).Execute(new("Bind", runtime.Window.Handle));
        Assert.True(result.Succeeded);
        Assert.Equal(runtime.Window, result.Window);
        Assert.Equal(0, runtime.RootReads);
        Assert.Equal(0, runtime.RootElement.DetailReads);
    }

    [Theory]
    [InlineData("cmd", "ConsoleWindowClass", "Command Prompt")]
    [InlineData("pwsh", "ConsoleWindowClass", "PowerShell")]
    [InlineData("CredentialUIBroker", "Dialog", "Credentials")]
    [InlineData("explorer", "#32770", "运行")]
    [InlineData("explorer", "#32770", "Run")]
    [InlineData("fixture", "Window", "请输入密码")]
    public void SensitiveWindowsAreRejectedBeforeAnyControlRead(string process, string className, string title)
    {
        var runtime = new FakeRuntime();
        runtime.Window = runtime.Window with { ProcessName = process, ClassName = className, Title = title };
        var result = new WindowsDesktopTaskWorker(runtime).Execute(new("Bind", runtime.Window.Handle));
        Assert.False(result.Succeeded);
        Assert.Equal("desktop_task_sensitive_window", result.ErrorCode);
        Assert.Equal(0, runtime.RootReads);
    }

    [Fact]
    public void ObserveRequiresForegroundBeforeReadingControls()
    {
        var runtime = new FakeRuntime { Foreground = 999 };
        var result = Observe(runtime);
        Assert.False(result.Succeeded);
        Assert.Equal("desktop_task_foreground_changed", result.ErrorCode);
        Assert.Equal(0, runtime.RootReads);
    }

    [Theory]
    [InlineData(900, true)]
    [InlineData(100, false)]
    [InlineData(999, false)]
    [InlineData(0, false)]
    public void UwpObservationRequiresTheExactBoundForegroundFrame(long foreground, bool allowed)
    {
        var runtime = new FakeRuntime();
        AddUwpFrame(runtime);
        runtime.Foreground = foreground;
        var result = Observe(runtime);
        Assert.Equal(allowed, result.Succeeded);
        if (!allowed)
        {
            Assert.Equal("desktop_task_foreground_changed", result.ErrorCode);
            Assert.Equal(0, runtime.RootReads);
        }
    }

    [Fact]
    public void UwpBindingStillReadsOnlyMetadataWithoutRequiringForeground()
    {
        var runtime = new FakeRuntime();
        AddUwpFrame(runtime);
        runtime.Foreground = 999;
        var result = new WindowsDesktopTaskWorker(runtime).Execute(new("Bind", runtime.Window.Handle));
        Assert.True(result.Succeeded);
        Assert.Equal(runtime.Window.FrameIdentity, result.Window!.FrameIdentity);
        Assert.Equal(0, runtime.RootReads);
    }

    [Fact]
    public void BindMayResolveAnExplicitFrameHandleToItsVerifiedContentIdentityWithoutReadingControls()
    {
        var runtime = new FakeRuntime();
        AddUwpFrame(runtime);
        var result = new WindowsDesktopTaskWorker(runtime).Execute(new("Bind", runtime.Window.FrameIdentity!.Handle));
        Assert.True(result.Succeeded);
        Assert.Equal(runtime.Window.Handle, result.Window!.Handle);
        Assert.Equal(runtime.Window.FrameIdentity, result.Window.FrameIdentity);
        Assert.Equal(0, runtime.RootReads);
        Assert.Equal(0, runtime.RootElement.DetailReads);
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("path")]
    [InlineData("class")]
    [InlineData("removed")]
    public void AFrameChangeAfterPatternAcquisitionPreventsTheContentAction(string change)
    {
        var (runtime, target, _, _) = Editable();
        AddUwpFrame(runtime);
        var snapshot = Observe(runtime).Snapshot!;
        var control = snapshot.Controls.Single(item => item.RuntimeId.SequenceEqual([2]));
        target.AfterPatternAcquired = () =>
        {
            var frame = runtime.Window.FrameIdentity!;
            runtime.Window = runtime.Window with { FrameIdentity = change switch
            {
                "handle" => frame with { Handle = 901 },
                "pid" => frame with { ProcessId = 301 },
                "start" => frame with { ProcessStartedAtUtc = frame.ProcessStartedAtUtc.AddSeconds(1) },
                "path" => frame with { ExecutablePath = @"C:\Fixture\ApplicationFrameHost.exe" },
                "class" => frame with { ClassName = "ChangedFrame" },
                _ => null
            } };
        };
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test"));
        Assert.False(result.Succeeded);
        Assert.Equal(0, target.Performs);
    }

    [Fact]
    public void AnUnchangedUwpFrameAllowsTheExactlyBoundContentAction()
    {
        var (runtime, target, _, _) = Editable();
        AddUwpFrame(runtime);
        var snapshot = Observe(runtime).Snapshot!;
        var control = snapshot.Controls.Single(item => item.RuntimeId.SequenceEqual([2]));
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test"));
        Assert.True(result.Succeeded);
        Assert.True(result.Receipt!.Verified);
        Assert.Equal(1, target.Performs);
    }

    [Theory]
    [InlineData(true, "ordinary", "field")]
    [InlineData(false, "API key", "field")]
    [InlineData(false, "ordinary", "PasswordBox")]
    [InlineData(false, "执行命令", "field")]
    public void SensitiveControlsAndTheirSubtreesAreNeverRead(bool password, string name, string automationId)
    {
        var runtime = new FakeRuntime();
        var sensitive = new FakeElement(2, name) { MetadataValue = new FakeElement(2, name).MetadataValue with { Password = password, AutomationId = automationId } };
        var child = new FakeElement(3, "secret child");
        sensitive.ChildElements.Add(child);
        runtime.RootElement.ChildElements.Add(sensitive);
        var result = Observe(runtime);
        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Snapshot!.Controls, control => control.RuntimeId.SequenceEqual([2]));
        Assert.Equal(0, sensitive.DetailReads);
        Assert.Equal(0, sensitive.ChildReads);
        Assert.Equal(0, child.MetadataReads);
    }

    [Fact]
    public void ReadOnlyTextIsEvidenceWithoutInventedActions()
    {
        var runtime = new FakeRuntime();
        var label = new FakeElement(2, "结果") { DetailsValue = new("已选择蓝色", true, "", []) };
        runtime.RootElement.ChildElements.Add(label);
        var result = Observe(runtime);
        var actual = Assert.Single(result.Snapshot!.Controls, control => control.Name == "结果");
        Assert.Equal("已选择蓝色", actual.Value);
        Assert.Empty(actual.Actions);
        Assert.True(actual.IsReadOnly);
        Assert.StartsWith(result.Snapshot.Id.ToString("N") + ":", actual.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScrollbarAuxiliaryButtonsMayAppearOrDisappearWithoutChangingTheReviewedContext(bool initiallyPresent)
    {
        var (runtime, target, _, _) = Editable();
        var scrollbar = Scrollbar(3);
        var arrows = new[] { new FakeElement(4, "arrow a"), new FakeElement(5, "page area"), new FakeElement(6, "arrow b") };
        foreach (var arrow in arrows)
        {
            arrow.MetadataValue = arrow.MetadataValue with { ControlType = "Button" };
            arrow.DetailsValue = new("", true, "", [DesktopTaskActionKind.Invoke]);
        }
        void SetAuxiliaryButtons(bool present)
        {
            scrollbar.ChildElements.Clear();
            if (present) scrollbar.ChildElements.AddRange(arrows);
        }
        SetAuxiliaryButtons(initiallyPresent);
        runtime.RootElement.ChildElements.Add(scrollbar);
        var snapshot = Observe(runtime).Snapshot!;
        var control = snapshot.Controls.Single(item => item.RuntimeId.SequenceEqual([2]));
        var observedScrollbar = Assert.Single(snapshot.Controls, item => item.RuntimeId.SequenceEqual([3]));
        Assert.Contains(DesktopTaskActionKind.ScrollDown, observedScrollbar.Actions);
        Assert.Contains("vertical=50", observedScrollbar.State);

        SetAuxiliaryButtons(!initiallyPresent);
        var changedChildren = Observe(runtime).Snapshot!;
        Assert.Equal(DesktopTaskContextFingerprint.Compute(snapshot), DesktopTaskContextFingerprint.Compute(changedChildren));
        SetAuxiliaryButtons(initiallyPresent);
        target.AfterPatternAcquired = () => SetAuxiliaryButtons(!initiallyPresent);
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test"));

        Assert.True(result.Succeeded);
        Assert.True(result.Receipt!.Verified);
        Assert.Equal(1, target.Performs);
        Assert.Equal(0, scrollbar.ChildReads);
        Assert.All(arrows, arrow =>
        {
            Assert.Equal(0, arrow.MetadataReads);
            Assert.Equal(0, arrow.DetailReads);
            Assert.Equal(0, arrow.Performs);
        });
    }

    [Fact]
    public void AScrollbarChildCannotBecomeAnActionTargetThroughAForgedControl()
    {
        var runtime = new FakeRuntime();
        var scrollbar = Scrollbar(2);
        var arrow = new FakeElement(3, "arrow")
        {
            DetailsValue = new("", true, "", [DesktopTaskActionKind.Invoke])
        };
        arrow.MetadataValue = arrow.MetadataValue with { ControlType = "Button" };
        scrollbar.ChildElements.Add(arrow);
        runtime.RootElement.ChildElements.Add(scrollbar);
        var snapshot = Observe(runtime).Snapshot!;
        var forged = DesktopTaskSafety.SnapshotControl(snapshot.Id, 2, arrow.MetadataValue, arrow.DetailsValue, out _);
        var result = Execute(runtime, forged, new(snapshot.Id, DesktopTaskActionKind.Invoke, forged.Id, null, "test"));

        Assert.False(result.Succeeded);
        Assert.Equal("desktop_task_control_changed", result.ErrorCode);
        Assert.Equal(0, scrollbar.ChildReads);
        Assert.Equal(0, arrow.MetadataReads);
        Assert.Equal(0, arrow.PatternAcquisitions);
        Assert.Equal(0, arrow.Performs);
    }

    [Theory]
    [InlineData("button")]
    [InlineData("field")]
    [InlineData("scroll-position")]
    public void ScrollbarPruningDoesNotHideBusinessControlsOrTheActualScrollState(string change)
    {
        var (runtime, target, _, _) = Editable();
        var scrollbar = Scrollbar(3);
        var panel = new FakeElement(4, "business panel");
        panel.MetadataValue = panel.MetadataValue with { ControlType = "Pane" };
        var button = new FakeElement(5, "ordinary action") { DetailsValue = new("", true, "", [DesktopTaskActionKind.Invoke]) };
        button.MetadataValue = button.MetadataValue with { ControlType = "Button" };
        var field = new FakeElement(6, "business input") { DetailsValue = new("original", false, "", [DesktopTaskActionKind.SetText]) };
        panel.ChildElements.AddRange([button, field]);
        runtime.RootElement.ChildElements.AddRange([scrollbar, panel]);
        var snapshot = Observe(runtime).Snapshot!;
        var control = snapshot.Controls.Single(item => item.RuntimeId.SequenceEqual([2]));
        Assert.Contains(snapshot.Controls, item => item.RuntimeId.SequenceEqual([5]));
        Assert.Contains(snapshot.Controls, item => item.RuntimeId.SequenceEqual([6]));
        target.AfterPatternAcquired = () =>
        {
            if (change == "button") button.MetadataValue = button.MetadataValue with { Name = "different action" };
            else if (change == "field") field.DetailsValue = field.DetailsValue with { Value = "changed recipient" };
            else scrollbar.DetailsValue = scrollbar.DetailsValue with { State = "vertical=75;horizontal=0" };
        };
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test"));

        Assert.False(result.Succeeded);
        Assert.Equal("desktop_task_context_changed", result.ErrorCode);
        Assert.Equal(0, target.Performs);
        Assert.True(panel.ChildReads > 0);
        Assert.True(button.DetailReads > 0);
        Assert.True(field.DetailReads > 0);
    }

    [Fact]
    public void SnapshotBoundsNodeCountDepthAndTotalText()
    {
        var runtime = new FakeRuntime();
        for (var i = 2; i < 400; i++) runtime.RootElement.ChildElements.Add(new FakeElement(i, "node"));
        var nodes = Observe(runtime).Snapshot!;
        Assert.True(nodes.Truncated);
        Assert.InRange(nodes.Controls.Count, 1, 256);
        Assert.Equal(0, runtime.RootElement.ChildElements[300].MetadataReads);

        runtime = new FakeRuntime();
        var cursor = runtime.RootElement;
        for (var i = 2; i < 30; i++) { var next = new FakeElement(i, "level"); cursor.ChildElements.Add(next); cursor = next; }
        Assert.True(Observe(runtime).Snapshot!.Truncated);
        Assert.Equal(0, cursor.MetadataReads);

        runtime = new FakeRuntime();
        for (var i = 2; i < 30; i++) runtime.RootElement.ChildElements.Add(new FakeElement(i, "text") { DetailsValue = new(new string('a', 1500), true, "", []) });
        var text = Observe(runtime).Snapshot!;
        Assert.True(text.Truncated);
        Assert.True(text.Controls.Sum(DesktopTaskSafety.TextLength) <= 20_000);
    }

    [Fact]
    public void AChangedProcessIdentityIsRejectedButTaskLocalTitleChangesAreAllowed()
    {
        var runtime = new FakeRuntime();
        var original = runtime.Window;
        runtime.Window = original with { Title = "fixture changed" };
        var worker = new WindowsDesktopTaskWorker(runtime);
        Assert.True(worker.Execute(new("Observe", original.Handle, original)).Succeeded);
        runtime.Window = original with { ProcessStartedAtUtc = original.ProcessStartedAtUtc.AddSeconds(1) };
        var reads = runtime.RootReads;
        var result = worker.Execute(new("Observe", original.Handle, original));
        Assert.Equal("desktop_task_window_changed", result.ErrorCode);
        Assert.Equal(reads, runtime.RootReads);
    }

    [Fact]
    public void AControlCannotBeBorrowedFromAnotherSnapshot()
    {
        var (runtime, target, snapshot, control) = Editable();
        var action = new DesktopTaskProposal(Guid.NewGuid(), DesktopTaskActionKind.SetText, control.Id, "new", "test");
        var result = Execute(runtime, control, action);
        Assert.Equal("desktop_task_invalid_target", result.ErrorCode);
        Assert.Equal(0, target.Performs);
        Assert.NotEqual(snapshot.Id, action.SnapshotId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("lowercase")]
    [InlineData("nonhex")]
    public void ExecuteRequiresAnExplicitUppercaseReviewedContextFingerprint(string invalid)
    {
        var (runtime, target, snapshot, control) = Editable();
        var fingerprint = invalid switch
        {
            "missing" => null,
            "empty" => "",
            "short" => "ABCD",
            "lowercase" => new string('a', 64),
            _ => new string('G', 64)
        };
        var reads = runtime.RootReads;
        var result = new WindowsDesktopTaskWorker(runtime).Execute(new("Execute", runtime.Window.Handle,
            runtime.Window, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test"), fingerprint));
        Assert.Equal("desktop_task_invalid_context", result.ErrorCode);
        Assert.Equal(reads, runtime.RootReads);
        Assert.Equal(0, target.PatternAcquisitions);
        Assert.Equal(0, target.Performs);
    }

    [Fact]
    public void AWellFormedButWrongContextFingerprintCannotAuthorizeAnAction()
    {
        var (runtime, target, snapshot, control) = Editable();
        var fingerprint = DesktopTaskContextFingerprint.Compute(snapshot);
        runtime.ReviewedContextFingerprint = (fingerprint[0] == '0' ? "1" : "0") + fingerprint[1..];
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test"));
        Assert.Equal("desktop_task_context_changed", result.ErrorCode);
        Assert.Equal(1, target.ContextChecks);
        Assert.Equal(0, target.Performs);
    }

    [Fact]
    public void TheReviewedTruncationStateMustMatchTheFinalObservation()
    {
        var (runtime, target, snapshot, control) = Editable();
        runtime.ReviewedContextFingerprint = DesktopTaskContextFingerprint.Compute(snapshot with { Truncated = true });
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test"));
        Assert.Equal("desktop_task_context_changed", result.ErrorCode);
        Assert.Equal(0, target.Performs);
    }

    [Theory]
    [InlineData("field", false)]
    [InlineData("field", true)]
    [InlineData("selection", false)]
    [InlineData("selection", true)]
    [InlineData("title", true)]
    [InlineData("identity", true)]
    [InlineData("bounds", true)]
    [InlineData("actions", true)]
    [InlineData("added-control", true)]
    public void AnUnchangedButtonCannotActOnContextChangedAfterReviewOrPatternAcquisition(string change, bool afterPattern)
    {
        var runtime = new FakeRuntime();
        var target = new FakeElement(2, "生成预览") { DetailsValue = new("", true, "", [DesktopTaskActionKind.Invoke]) };
        var other = new FakeElement(3, "其他字段") { DetailsValue = new("林舟", false, "selected=False", [DesktopTaskActionKind.SetText]) };
        runtime.RootElement.ChildElements.AddRange([target, other]);
        var snapshot = Observe(runtime).Snapshot!;
        var control = snapshot.Controls.Single(item => item.RuntimeId.SequenceEqual([2]));
        void ChangeContext()
        {
            switch (change)
            {
                case "field": other.DetailsValue = other.DetailsValue with { Value = "另一个对象" }; break;
                case "selection": other.DetailsValue = other.DetailsValue with { State = "selected=True" }; break;
                case "title": runtime.Window = runtime.Window with { Title = "另一个文档" }; break;
                case "identity": other.MetadataValue = other.MetadataValue with { AutomationId = "changed-field" }; break;
                case "bounds": other.MetadataValue = other.MetadataValue with { Bounds = new(8, 9, 90, 20) }; break;
                case "actions": other.DetailsValue = other.DetailsValue with { Actions = [] }; break;
                case "added-control": runtime.RootElement.ChildElements.Add(new FakeElement(4, "新增对象")); break;
            }
        }
        if (afterPattern) target.AfterPatternAcquired = ChangeContext; else ChangeContext();
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.Invoke, control.Id, null, "test"));
        Assert.Equal("desktop_task_context_changed", result.ErrorCode);
        Assert.Equal(1, target.PatternAcquisitions);
        Assert.Equal(1, target.ContextChecks);
        Assert.Equal(0, target.Performs);
        Assert.Equal("生成预览", target.MetadataValue.Name);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("automation-id")]
    [InlineData("type")]
    [InlineData("parent")]
    [InlineData("bounds")]
    [InlineData("state")]
    [InlineData("owner")]
    [InlineData("password")]
    [InlineData("readonly")]
    [InlineData("value")]
    [InlineData("pattern")]
    public void StaleTargetsNeverPerformTheAction(string change)
    {
        var (runtime, target, snapshot, control) = Editable();
        target.MetadataValue = change switch
        {
            "name" => target.MetadataValue with { Name = "changed" },
            "automation-id" => target.MetadataValue with { AutomationId = "different" },
            "type" => target.MetadataValue with { ControlType = "Button" },
            "parent" => target.MetadataValue with { ParentName = "other" },
            "bounds" => target.MetadataValue with { Bounds = new(5, 5, 80, 20) },
            "password" => target.MetadataValue with { Password = true },
            _ => target.MetadataValue
        };
        target.DetailsValue = change switch
        {
            "state" => target.DetailsValue with { State = "changed" },
            "readonly" => target.DetailsValue with { IsReadOnly = true },
            "value" => target.DetailsValue with { Value = "user new content" },
            "pattern" => target.DetailsValue with { Actions = [] },
            _ => target.DetailsValue
        };
        if (change == "owner") runtime.Owns = false;
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test"));
        Assert.False(result.Succeeded);
        Assert.Equal(0, target.Performs);
    }

    [Fact]
    public void FocusLossDuringRevalidationPreventsNativeAction()
    {
        var (runtime, target, snapshot, control) = Editable();
        target.BeforeDetails = () => runtime.Foreground = 999;
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test"));
        Assert.Equal("desktop_task_foreground_changed", result.ErrorCode);
        Assert.Equal(0, target.Performs);
    }

    [Fact]
    public void SetTextRequiresExactReadbackAndCallsOnlyOnce()
    {
        var (runtime, target, snapshot, control) = Editable();
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "测试文字", "test"));
        Assert.True(result.Succeeded);
        Assert.True(result.Receipt!.Verified);
        Assert.Equal(1, target.Performs);
        Assert.Equal(1, target.ContextChecks);
        Assert.Same(control, target.LastExpected);
        Assert.Equal("测试文字", target.DetailsValue.Value);
    }

    [Fact]
    public void IncorrectReadbackFailsWithoutRetryingTheWrite()
    {
        var (runtime, target, snapshot, control) = Editable();
        target.IgnoreWrites = true;
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test"));
        Assert.False(result.Succeeded);
        Assert.Equal("desktop_task_value_not_verified", result.ErrorCode);
        Assert.Equal(1, target.Performs);
    }

    [Theory]
    [InlineData(DesktopTaskActionKind.Invoke)]
    [InlineData(DesktopTaskActionKind.Select)]
    [InlineData(DesktopTaskActionKind.Expand)]
    [InlineData(DesktopTaskActionKind.Collapse)]
    [InlineData(DesktopTaskActionKind.ScrollUp)]
    [InlineData(DesktopTaskActionKind.ScrollDown)]
    public void PatternCallsRequireASeparateObservationBeforeClaimingAResult(DesktopTaskActionKind kind)
    {
        var runtime = new FakeRuntime();
        var target = new FakeElement(2, "safe target") { DetailsValue = new("", true, "", [kind]) };
        runtime.RootElement.ChildElements.Add(target);
        var snapshot = Observe(runtime).Snapshot!;
        var control = snapshot.Controls.Single(item => item.RuntimeId.SequenceEqual([2]));
        var result = Execute(runtime, control, new(snapshot.Id, kind, control.Id, null, "test"));
        Assert.True(result.Succeeded);
        Assert.False(result.Receipt!.Verified);
        Assert.Contains("重新观察", result.Receipt.Summary);
        Assert.Equal(1, target.Performs);
        Assert.Equal(1, target.ContextChecks);
    }

    [Fact]
    public void TruncatedTreeDoesNotInvalidateAnExactlyBoundTargetButDuplicateIdsDo()
    {
        var (runtime, target, snapshot, control) = Editable();
        for (var i = 3; i < 400; i++) runtime.RootElement.ChildElements.Add(new FakeElement(i, "node"));
        snapshot = Observe(runtime).Snapshot!;
        control = snapshot.Controls.Single(item => item.RuntimeId.SequenceEqual([2]));
        Assert.True(snapshot.Truncated);
        var action = new DesktopTaskProposal(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test");
        Assert.True(Execute(runtime, control, action).Succeeded);
        Assert.Equal(1, target.Performs);

        (runtime, target, snapshot, control) = Editable();
        runtime.RootElement.ChildElements.Add(new FakeElement(2, "duplicate"));
        Assert.False(Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test")).Succeeded);
        Assert.Equal(0, target.Performs);
    }

    [Fact]
    public void TruncatedDocumentTextCanBeScrolledButNeverReplaced()
    {
        var runtime = new FakeRuntime();
        var document = new FakeElement(2, "document") { DetailsValue = new(new string('a', 1501), false, "", [DesktopTaskActionKind.SetText, DesktopTaskActionKind.ScrollDown]) };
        runtime.RootElement.ChildElements.Add(document);
        var snapshot = Observe(runtime).Snapshot!;
        var control = snapshot.Controls.Single(item => item.RuntimeId.SequenceEqual([2]));
        Assert.DoesNotContain(DesktopTaskActionKind.SetText, control.Actions);
        Assert.Contains(DesktopTaskActionKind.ScrollDown, control.Actions);
        Assert.Equal(1500, control.Value.Length);
        Assert.True(Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.ScrollDown, control.Id, null, "test")).Succeeded);
    }

    [Theory]
    [InlineData("Edit", false, true)]
    [InlineData("Document", false, true)]
    [InlineData("ComboBox", false, true)]
    [InlineData("ComboBox", true, false)]
    [InlineData("Document", true, false)]
    [InlineData("RadioButton", false, false)]
    [InlineData("Button", false, false)]
    [InlineData("Text", false, false)]
    public void WritableValuePatternMustAlsoBelongToAnIdentifiedTextControl(string type, bool readOnly, bool allowed)
    {
        var runtime = new FakeRuntime();
        var target = new FakeElement(2, "合成控件");
        target.MetadataValue = target.MetadataValue with { ControlType = type };
        target.DetailsValue = new("old", readOnly, "", [DesktopTaskActionKind.SetText]);
        runtime.RootElement.ChildElements.Add(target);
        var snapshot = Observe(runtime).Snapshot!;
        var control = snapshot.Controls.Single(item => item.RuntimeId.SequenceEqual([2]));
        Assert.Equal(allowed, control.Actions.Contains(DesktopTaskActionKind.SetText));
        // Execution rejects the unsupported text action too, even if a caller
        // explicitly requests it for this otherwise valid snapshot target.
        var result = Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "new", "test"));
        Assert.Equal(allowed, result.Succeeded);
        Assert.Equal(allowed ? 1 : 0, target.Performs);
    }

    [Fact]
    public void ChromeRadioWithWritableValueExposesSelectionButCannotBeCoercedIntoSetText()
    {
        var runtime = new FakeRuntime();
        var radio = new FakeElement(2, "标准");
        radio.MetadataValue = radio.MetadataValue with { ControlType = "RadioButton", AutomationId = "plan-standard", ParentName = "选择方案" };
        radio.DetailsValue = new("", false, "selected=False",
            [DesktopTaskActionKind.Invoke, DesktopTaskActionKind.SetText, DesktopTaskActionKind.Select]);
        runtime.RootElement.ChildElements.Add(radio);
        var snapshot = Observe(runtime).Snapshot!;
        var control = snapshot.Controls.Single(item => item.RuntimeId.SequenceEqual([2]));
        Assert.Equal([DesktopTaskActionKind.Invoke, DesktopTaskActionKind.Select], control.Actions);
        var forged = control with { Actions = [DesktopTaskActionKind.Invoke, DesktopTaskActionKind.SetText, DesktopTaskActionKind.Select] };
        var result = Execute(runtime, forged, new(snapshot.Id, DesktopTaskActionKind.SetText, control.Id, "标准", "test"));
        Assert.False(result.Succeeded);
        Assert.Equal(0, radio.Performs);
        Assert.True(Execute(runtime, control, new(snapshot.Id, DesktopTaskActionKind.Select, control.Id, null, "test")).Succeeded);
        Assert.Equal(1, radio.Performs);
    }

    private static DesktopTaskWorkerResponse Observe(FakeRuntime runtime)
    {
        var response = new WindowsDesktopTaskWorker(runtime).Execute(new("Observe", runtime.Window.Handle, runtime.Window));
        if (response.Snapshot is not null) runtime.ReviewedContextFingerprint = DesktopTaskContextFingerprint.Compute(response.Snapshot);
        return response;
    }

    private static FakeElement Scrollbar(int id)
    {
        var scrollbar = new FakeElement(id, "scrollbar")
        {
            DetailsValue = new("", true, "vertical=50;horizontal=0", [DesktopTaskActionKind.ScrollUp, DesktopTaskActionKind.ScrollDown])
        };
        scrollbar.MetadataValue = scrollbar.MetadataValue with { ControlType = "ScrollBar" };
        return scrollbar;
    }

    private static void AddUwpFrame(FakeRuntime runtime)
    {
        runtime.Window = runtime.Window with { ClassName = "Windows.UI.Core.CoreWindow",
            FrameIdentity = new(900, 300, runtime.Window.ProcessStartedAtUtc,
                DesktopTaskWindowBinding.TrustedFramePath, "ApplicationFrameWindow") };
        runtime.Foreground = 900;
    }

    private static DesktopTaskWorkerResponse Execute(FakeRuntime runtime, DesktopTaskControl control, DesktopTaskProposal action) =>
        new WindowsDesktopTaskWorker(runtime).Execute(new("Execute", runtime.Window.Handle, runtime.Window, control, action, runtime.ReviewedContextFingerprint));

    private static (FakeRuntime Runtime, FakeElement Target, DesktopTaskSnapshot Snapshot, DesktopTaskControl Control) Editable()
    {
        var runtime = new FakeRuntime();
        var target = new FakeElement(2, "测试文本框") { DetailsValue = new("old", false, "", [DesktopTaskActionKind.SetText]) };
        runtime.RootElement.ChildElements.Add(target);
        var snapshot = Observe(runtime).Snapshot!;
        return (runtime, target, snapshot, snapshot.Controls.Single(control => control.RuntimeId.SequenceEqual([2])));
    }

    private sealed class FakeRuntime : IDesktopTaskNativeRuntime
    {
        public DesktopTaskWindow Window = new(100, 200, "fixture", DateTimeOffset.Parse("2026-11-04T00:00:00Z"), @"C:\Fixture\fixture.exe", "FixtureWindow", "Fixture");
        public FakeElement RootElement { get; } = new(1, "root");
        public long Foreground = 100;
        public bool Owns = true;
        public int RootReads;
        public string? ReviewedContextFingerprint;
        public long ForegroundWindowHandle => Foreground;
        public DesktopTaskWindow ReadWindow(long handle) => Window;
        public IDesktopTaskNativeElement Root(long handle) { RootReads++; return RootElement; }
        public bool BelongsToWindow(IDesktopTaskNativeElement element, IDesktopTaskNativeElement root, long handle) => Owns;
    }

    private sealed class FakeElement(int id, string name) : IDesktopTaskNativeElement
    {
        public DesktopTaskElementMetadata MetadataValue = new([id], name, "field-" + id, "Edit", "root", new(1, 1, 80, 20), true, false, false, 200, 0);
        public DesktopTaskElementDetails DetailsValue = new("", true, "", []);
        public List<FakeElement> ChildElements { get; } = [];
        public int MetadataReads;
        public int DetailReads;
        public int ChildReads;
        public int Performs;
        public int PatternAcquisitions;
        public int ContextChecks;
        public DesktopTaskControl? LastExpected;
        public bool IgnoreWrites;
        public Action? BeforeDetails;
        public Action? AfterPatternAcquired;
        public DesktopTaskElementMetadata Metadata() { MetadataReads++; return MetadataValue; }
        public DesktopTaskElementDetails Details(int maximumTextLength) { BeforeDetails?.Invoke(); DetailReads++; return DetailsValue; }
        public IEnumerable<IDesktopTaskNativeElement> Children() { ChildReads++; return ChildElements; }
        public void Perform(DesktopTaskActionKind action, string? text, DesktopTaskControl expected, Action verifyContext)
        {
            PatternAcquisitions++;
            AfterPatternAcquired?.Invoke();
            ContextChecks++;
            verifyContext();
            LastExpected = expected;
            Performs++;
            if (action == DesktopTaskActionKind.SetText && !IgnoreWrites) DetailsValue = DetailsValue with { Value = text! };
        }
    }
}
