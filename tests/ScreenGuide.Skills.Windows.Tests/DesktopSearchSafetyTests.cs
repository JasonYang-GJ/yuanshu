namespace ScreenGuide.Skills.Windows.Tests;

public sealed class DesktopSearchSafetyTests
{
    [Fact]
    public void SearchContainerMetadataDoesNotDisableExistingUiaOnlySearchPath()
    {
        var input = Control("uia-search", name: "搜索框");
        input = input with { Snapshot = input.Snapshot with
        { NativeSearchIdentity = "ia2:9:-10", NativeSearchUsesContainer = true, NativeSearchContainerPending = true } };
        var driver = new FakeSearchDriver([input]);
        Assert.True(Executor(Window(), driver).Search(Window(), "示例音乐").Verified);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(1, driver.SubmitCount);
    }

    [Fact]
    public void WrittenSearchWaitsForContainerRebuildAndNativeAttestedRuntimePathWithoutRetyping()
    {
        var input = NativeInput();
        input = input with { Snapshot = input.Snapshot with { Name = "搜索框", NativeSearchUsesContainer = true } };
        var driver = new FakeSearchDriver([input]) { OnRead = (read, snapshot) => read < 3 ? snapshot : snapshot with
        {
            RuntimeId = "new-uia-path", HasNativeSearchSemantics = read >= 5,
            NativeSearchContainerPending = read < 5, Value = read < 5 ? "" : "示例音乐"
        } };
        Assert.True(Executor(Window(), driver).Search(Window(), "示例音乐").Verified);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(1, driver.SubmitCount);
    }

    [Theory]
    [InlineData("never-recovers")]
    [InlineData("password")]
    [InlineData("native-id-changed")]
    [InlineData("focus-lost")]
    [InlineData("context-type-changed")]
    [InlineData("cancel")]
    public void ContainerRebuildWaitNeverWritesAgainOrSubmitsUnsafeInput(string scenario)
    {
        using var cancellation = new CancellationTokenSource();
        var input = NativeInput();
        input = input with { Snapshot = input.Snapshot with { Name = "搜索框", NativeSearchUsesContainer = true } };
        var driver = new FakeSearchDriver([input]) { OnRead = (read, snapshot) =>
        {
            if (read < 3) return snapshot;
            var pending = snapshot with { HasNativeSearchSemantics = false, NativeSearchContainerPending = true };
            if (scenario == "cancel") cancellation.Cancel();
            return scenario switch
            {
                "password" => pending with { IsPassword = true },
                "native-id-changed" => pending with { NativeSearchIdentity = "different" },
                "focus-lost" => pending with { HasKeyboardFocus = false },
                "context-type-changed" => pending with { NativeSearchUsesContainer = false },
                _ => pending
            };
        } };
        if (scenario == "cancel")
            Assert.ThrowsAny<OperationCanceledException>(() => Executor(Window(), driver).Search(Window(), "示例音乐", cancellation.Token));
        else
            Assert.Throws<DesktopSearchException>(() => Executor(Window(), driver).Search(Window(), "示例音乐", cancellation.Token));
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Theory]
    [InlineData("tag:input;text-input-type:text;name-from:placeholder;", "搜索框", true)]
    [InlineData("tag:input;text-input-type:password;name-from:placeholder;", "搜索框", false)]
    [InlineData("tag:input;text-input-type:text;", "搜索框", false)]
    [InlineData("tag:input;text-input-type:text;name-from:placeholder;", "发送消息", false)]
    public void PendingContainerStillRequiresExplicitNativeTextSearchInput(string attributes, string name, bool expected) =>
        Assert.Equal(expected, LegacyBrowserSearchSemantics.IsLabeledTextSearchInput(attributes, name));

    [Fact]
    public void SearchContainerNeverClaimsUniquenessFromPartialOrSimpleChildEnumeration()
    {
        Assert.True(LegacyBrowserSearchSemantics.IsCompleteSearchEnumeration(2, 2, 2, 0, [true, true]));
        Assert.False(LegacyBrowserSearchSemantics.IsCompleteSearchEnumeration(3, 2, 3, 1, [true, true, false]));
        Assert.False(LegacyBrowserSearchSemantics.IsCompleteSearchEnumeration(3, 2, 3, 0, [true, true, false]));
        Assert.False(LegacyBrowserSearchSemantics.IsCompleteSearchEnumeration(3, 3, 3, 0, [true, true, false]));
        Assert.False(LegacyBrowserSearchSemantics.IsCompleteSearchEnumeration(2, 2, 3, 0, [true, true]));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("two-inputs", false)]
    [InlineData("two-buttons", false)]
    [InlineData("unlabeled-input", false)]
    [InlineData("wrong-button", false)]
    [InlineData("cross-window", false)]
    [InlineData("disabled", false)]
    [InlineData("password", false)]
    [InlineData("wrong-identity", false)]
    public void NativeTextSearchRequiresOneNamedInputAndOneSearchButtonInSameSmallContainer(string scenario, bool expected)
    {
        var input = new LegacyBrowserSearchSemantics.SearchContextPart("input", 42, 42, "input", "text", "搜索你感兴趣的内容", true);
        var button = new LegacyBrowserSearchSemantics.SearchContextPart("button", 42, 43, "button", "", "搜索", true);
        var parts = new List<LegacyBrowserSearchSemantics.SearchContextPart> { input, button };
        switch (scenario)
        {
            case "two-inputs": parts.Add(input with { Identity = "second" }); break;
            case "two-buttons": parts.Add(button with { Identity = "second" }); break;
            case "unlabeled-input": parts[0] = input with { Name = "消息" }; break;
            case "wrong-button": parts[1] = button with { Name = "发送" }; break;
            case "cross-window": parts[1] = button with { OwnerWindow = 77 }; break;
            case "disabled": parts[1] = button with { Available = false }; break;
            case "password": parts[0] = input with { InputType = "password" }; break;
            case "wrong-identity": parts[0] = input with { Identity = "other" }; break;
        }
        Assert.Equal(expected, LegacyBrowserSearchSemantics.IsBoundSearchContainer(parts, 42, "input"));
    }

    [Theory]
    [InlineData("geometry")]
    [InlineData("name")]
    [InlineData("semantics")]
    public void ContainerBasedSearchRejectsLayoutNameOrContainerEvidenceChanges(string change)
    {
        var input = NativeInput();
        input = input with { Snapshot = input.Snapshot with { Name = "搜索框", NativeSearchUsesContainer = true } };
        var driver = new FakeSearchDriver([input]) { OnRead = (_, snapshot) => change switch
        {
            "geometry" => snapshot with { Bounds = new(11, 10, 360, 30), NativeSearchIdentity = "", HasNativeSearchSemantics = false },
            "name" => snapshot with { Name = "其他搜索框" },
            _ => snapshot with { NativeSearchUsesContainer = false }
        } };
        Assert.Throws<DesktopSearchException>(() => Executor(Window(), driver).Search(Window(), "示例音乐"));
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ContainerSearchRetainsUniqueInputThroughSuggestionExpansionAndAttestedFocusBorderChange()
    {
        var parts = new List<LegacyBrowserSearchSemantics.SearchContextPart>
        {
            new("input", 42, 42, "input", "text", "搜索框", true),
            new("button", 42, 43, "button", "", "搜索", true)
        };
        parts.AddRange(Enumerable.Range(0, 72).Select(index =>
            new LegacyBrowserSearchSemantics.SearchContextPart("suggestion:" + index, 42, 41, "", "", "", true)));
        Assert.True(LegacyBrowserSearchSemantics.IsBoundSearchContainer(parts, 42, "input"));
        parts.Add(new("nested-other-input", 42, 42, "input", "text", "搜索框", true));
        Assert.False(LegacyBrowserSearchSemantics.IsBoundSearchContainer(parts, 42, "input"));

        var input = NativeInput();
        input = input with { Snapshot = input.Snapshot with { Name = "搜索框", NativeSearchUsesContainer = true } };
        var driver = new FakeSearchDriver([input])
        { OnRead = (read, snapshot) => read > 1 ? snapshot with { Bounds = new(10, 11, 361, 28) } : snapshot };
        Assert.True(Executor(Window(), driver).Search(Window(), "示例音乐").Verified);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(1, driver.SubmitCount);
    }

    [Fact]
    public void NativeSearchInputCanUseVerifiedNativeFocusWhenUiaFocusIsUnsupported()
    {
        var attempts = 0;
        WindowsDesktopSearchAutomationDriver.FocusWithNativeFallback(true,
            () => throw new InvalidOperationException("target cannot receive focus"), () => { attempts++; return true; });
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void NativeFocusFallbackCannotAuthorizeGenericInputsOrIgnoreFailedFocusReadback()
    {
        var attempts = 0;
        Assert.Throws<InvalidOperationException>(() => WindowsDesktopSearchAutomationDriver.FocusWithNativeFallback(false,
            () => throw new InvalidOperationException(), () => { attempts++; return true; }));
        Assert.Equal(0, attempts);
        Assert.Throws<System.Windows.Automation.ElementNotAvailableException>(() => WindowsDesktopSearchAutomationDriver.FocusWithNativeFallback(true,
            () => throw new InvalidOperationException(), () => false));
    }

    [Fact]
    public void BoundSearchInvokeOnlyFollowsUnsupportedUiaAndFailedNativeFocus()
    {
        var invokes = 0;
        WindowsDesktopSearchAutomationDriver.FocusWithNativeFallback(true,
            () => throw new InvalidOperationException(), () => false, () => { invokes++; return true; });
        Assert.Equal(1, invokes);
        WindowsDesktopSearchAutomationDriver.FocusWithNativeFallback(true,
            () => throw new InvalidOperationException(), () => true, () => { invokes++; return true; });
        WindowsDesktopSearchAutomationDriver.FocusWithNativeFallback(true,
            () => { }, () => false, () => { invokes++; return true; });
        Assert.Equal(1, invokes);
        Assert.Throws<InvalidOperationException>(() => WindowsDesktopSearchAutomationDriver.FocusWithNativeFallback(false,
            () => throw new InvalidOperationException(), () => false, () => { invokes++; return true; }));
        Assert.Equal(1, invokes);
    }

    [Fact]
    public void AsyncNativeValueUpdateAndClearButtonResizeAreReadWithoutRewriting()
    {
        var input = NativeInput();
        var driver = new FakeSearchDriver([input])
        {
            OnRead = (read, snapshot) => read >= 3 ? snapshot with
            {
                Value = read < 5 ? "" : "示例音乐",
                Bounds = new DesktopSearchBounds(10, 10, 360, 30)
            } : snapshot
        };
        Assert.True(Executor(Window(), driver).Search(Window(), "示例音乐").Verified);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(1, driver.SubmitCount);
    }

    [Theory]
    [InlineData("wrong-value")]
    [InlineData("focus-changed")]
    [InlineData("native-id-changed")]
    [InlineData("native-geometry-not-attested")]
    [InlineData("offscreen")]
    public void AsyncNativeReadbackNeverSubmitsThroughInvalidEvidence(string scenario)
    {
        var driver = new FakeSearchDriver([NativeInput()])
        {
            OnRead = (read, snapshot) => read < 3 ? snapshot : scenario switch
            {
                "wrong-value" => snapshot with { Value = "其他" },
                "focus-changed" => snapshot with { Value = "", HasKeyboardFocus = false },
                "native-id-changed" => snapshot with { Value = "", NativeSearchIdentity = "ia2:9:-11" },
                "native-geometry-not-attested" => snapshot with { NativeSearchIdentity = "", HasNativeSearchSemantics = false, Bounds = new(10, 10, 360, 30) },
                _ => snapshot with { IsOffscreen = true }
            }
        };
        Assert.Throws<DesktopSearchException>(() => Executor(Window(), driver).Search(Window(), "示例音乐"));
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void CancellationWhileNativeValueIsUpdatingPreventsEnter()
    {
        using var cancellation = new CancellationTokenSource();
        var driver = new FakeSearchDriver([NativeInput()])
        {
            OnRead = (read, snapshot) =>
            {
                if (read < 3) return snapshot;
                cancellation.Cancel();
                return snapshot with { Value = "" };
            }
        };
        Assert.ThrowsAny<OperationCanceledException>(() => Executor(Window(), driver).Search(Window(), "示例音乐", cancellation.Token));
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    private static DesktopSearchControl NativeInput()
    {
        var input = Control("native", name: "公开占位内容");
        return input with { Snapshot = input.Snapshot with { NativeSearchIdentity = "ia2:9:-10", HasNativeSearchSemantics = true } };
    }

    [Fact]
    public void RotatingPlaceholderOnSameNativeSearchInputDoesNotInvalidateTarget()
    {
        var input = Control("native", name: "公开推荐甲");
        input = input with { Snapshot = input.Snapshot with { NativeSearchIdentity = "ia2:9:-10", HasNativeSearchSemantics = true } };
        var driver = new FakeSearchDriver([input]) { OnRead = (read, snapshot) => snapshot with { Name = "公开推荐" + read } };
        Assert.True(Executor(Window(), driver).Search(Window(), "示例音乐").Verified);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(1, driver.SubmitCount);
    }

    [Fact]
    public void ChangingNameWithoutIndependentNativeSearchIdentityStillFailsClosed()
    {
        var driver = new FakeSearchDriver([Control("generic", name: "搜索框")])
        { OnRead = (_, snapshot) => snapshot with { Name = "消息输入框" } };
        Assert.Throws<DesktopSearchException>(() => Executor(Window(), driver).Search(Window(), "示例音乐"));
        Assert.Equal(0, driver.SetValueCount);
    }

    [Fact]
    public void NativeSearchTypeRecognizesPlaceholderAndDeduplicatesOnlyTheSameNativeObject()
    {
        var first = Control("one", name: "推荐歌曲") with { Snapshot = Control("one", name: "推荐歌曲").Snapshot with {
            NativeSearchIdentity = "ia2:9:-10", HasNativeSearchSemantics = true } };
        var alias = first with { NativeReference = "two", Snapshot = first.Snapshot with { RuntimeId = "two" } };
        var driver = new FakeSearchDriver([first, alias]);
        Assert.True(Executor(Window(), driver).Search(Window(), "最近最热的歌").Verified);
        Assert.Equal(1, driver.SetValueCount); Assert.Equal(1, driver.SubmitCount);
    }

    [Fact]
    public void DistinctNativeSearchInputsAreStillAmbiguous()
    {
        var first = Control("one", name: "推荐歌曲");
        first = first with { Snapshot = first.Snapshot with { NativeSearchIdentity = "ia2:9:-10", HasNativeSearchSemantics = true } };
        var second = first with { NativeReference = "two", Snapshot = first.Snapshot with { RuntimeId = "two", NativeSearchIdentity = "ia2:9:-11" } };
        var driver = new FakeSearchDriver([first, second]);
        Assert.Throws<DesktopSearchException>(() => Executor(Window(), driver).Search(Window(), "音乐"));
        Assert.Equal(0, driver.SetValueCount);
    }

    [Fact]
    public void ChangingNativeIdentityOrCancellationPreventsSubmission()
    {
        var first = Control("one", name: "Search");
        first = first with { Snapshot = first.Snapshot with { NativeSearchIdentity = "ia2:9:-10", HasNativeSearchSemantics = true } };
        var driver = new FakeSearchDriver([first]) { OnRead = (_, s) => s with { NativeSearchIdentity = "ia2:9:-11" } };
        Assert.Throws<DesktopSearchException>(() => Executor(Window(), driver).Search(Window(), "音乐"));
        Assert.Equal(0, driver.SetValueCount);
        using var cancelled = new CancellationTokenSource();
        driver = new FakeSearchDriver([Control("one", name: "Search")]) { BeforeSubmitSend = (_, _) => cancelled.Cancel() };
        Assert.Throws<OperationCanceledException>(() => Executor(Window(), driver).Search(Window(), "音乐", cancelled.Token));
        Assert.Equal(0, driver.SubmitCount);
    }

    [Theory]
    [InlineData("tag:input;text-input-type:search;", true)]
    [InlineData("class:anything;tag:input;text-input-type:search;", true)]
    [InlineData("tag:input;text-input-type:password;", false)]
    [InlineData("tag:textarea;text-input-type:search;", false)]
    [InlineData("tag:input;class:fake\\;text-input-type:search;", false)]
    [InlineData("tag:input;text-input-type:text;text-input-type:search;", false)]
    [InlineData("tag:input;text-input-type:search", false)]
    public void NativeAttributesMustDescribeAnUnambiguousSearchInput(string attributes, bool expected)
        => Assert.Equal(expected, LegacyBrowserSearchSemantics.IsSearchInput(attributes));

    [Fact]
    public void UniqueStrongCandidateWritesExactQueryAndSubmitsOnce()
    {
        var expected = Window();
        var driver = new FakeSearchDriver(
        [
            Control("primary", name: "地址和搜索栏")
        ]);
        var executor = Executor(expected, driver);

        var result = executor.Search(expected, "元枢");

        Assert.True(result.Verified);
        Assert.Equal("元枢", driver.Value("primary"));
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(1, driver.SubmitCount);
        Assert.Equal(1, driver.ActivationCount);
        Assert.DoesNotContain("地址和搜索栏", result.TechnicalDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("元枢", result.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void StrongNamedCandidateWinsWhileMetadataOnlyFieldIsExcluded()
    {
        var expected = Window();
        var driver = new FakeSearchDriver(
        [
            Control("primary", name: "地址和搜索栏"),
            Control("secondary", name: "推荐内容", automationId: "search", helpText: "搜索")
        ]);

        Executor(expected, driver).Search(expected, "周杰伦");

        Assert.Equal("周杰伦", driver.Value("primary"));
        Assert.Equal(string.Empty, driver.Value("secondary"));
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(1, driver.SubmitCount);
        Assert.Equal(1, driver.FocusCount);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("推荐内容", "")]
    [InlineData("推荐内容", "ia2:9:-10")]
    [InlineData("find", "ia2:9:-10")]
    public void InternalIdAndHelpCannotTurnAnUnprovenFieldIntoSearch(string name, string nativeIdentity)
    {
        var expected = Window();
        var field = Control("embedded", name: name, automationId: "search", helpText: "搜索");
        field = field with { Snapshot = field.Snapshot with
            { NativeSearchIdentity = nativeIdentity, HasNativeSearchSemantics = false } };
        var driver = new FakeSearchDriver([field]);

        Assert.False(SafeForegroundSearchExecutor.IsReliableCandidate(field.Snapshot, expected.WindowHandle));
        var error = Assert.Throws<DesktopSearchException>(() => Executor(expected, driver).Search(expected, "示例"));

        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, error.Code);
        Assert.Equal(0, driver.FocusCount);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoStrongCandidatesAreAmbiguousEvenWhenScoresDiffer(bool nativeSecond)
    {
        var expected = Window();
        var second = Control("second", name: nativeSecond ? "公开推荐" : "搜索框", automationId: "search", helpText: "搜索");
        if (nativeSecond) second = second with { Snapshot = second.Snapshot with
            { NativeSearchIdentity = "ia2:9:-10", HasNativeSearchSemantics = true } };
        var driver = new FakeSearchDriver([Control("first", name: "搜索框"), second]);

        var error = Assert.Throws<DesktopSearchException>(() => Executor(expected, driver).Search(expected, "示例"));

        Assert.Equal(DesktopSearchErrorCodes.CandidateAmbiguous, error.Code);
        Assert.Equal(0, driver.FocusCount);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void TiedStrongCandidatesFailClosedBeforeWriting()
    {
        var expected = Window();
        var driver = new FakeSearchDriver(
        [
            Control("one", name: "搜索框"),
            Control("two", labeledBy: "搜索框")
        ]);

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.CandidateAmbiguous, exception.Code);
        Assert.Equal(0, driver.FocusCount);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void WeakGenericEditFailsClosedBeforeWriting()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("generic", name: "编辑")]);

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, exception.Code);
        Assert.Equal(0, driver.FocusCount);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("offscreen")]
    [InlineData("password")]
    [InlineData("readonly")]
    [InlineData("unsupported")]
    [InlineData("out-of-window")]
    [InlineData("empty-bounds")]
    [InlineData("missing-runtime")]
    public void UnsafeCandidatesAreRejectedBeforeWriting(string scenario)
    {
        var expected = Window();
        var snapshot = Control("unsafe", name: "搜索框").Snapshot;
        snapshot = scenario switch
        {
            "disabled" => snapshot with { IsEnabled = false },
            "offscreen" => snapshot with { IsOffscreen = true },
            "password" => snapshot with { IsPassword = true },
            "readonly" => snapshot with { IsReadOnly = true },
            "unsupported" => snapshot with { SupportsValuePattern = false },
            "out-of-window" => snapshot with { OwnerWindowHandle = 43 },
            "empty-bounds" => snapshot with { Bounds = new DesktopSearchBounds(0, 0, 0, 0) },
            "missing-runtime" => snapshot with { RuntimeId = string.Empty },
            _ => throw new InvalidOperationException()
        };
        var driver = new FakeSearchDriver([new DesktopSearchControl("unsafe", snapshot)]);

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.CandidateNotFound, exception.Code);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void LabeledByProvidesStrongSearchEvidence()
    {
        var expected = Window();
        var driver = new FakeSearchDriver(
        [
            Control("labeled", name: "编辑", labeledBy: "站内搜索框")
        ]);

        Executor(expected, driver).Search(expected, "今日头条");

        Assert.Equal("今日头条", driver.Value("labeled"));
        Assert.Equal(1, driver.SubmitCount);
    }

    [Fact]
    public void WindowIdentityChangeImmediatelyBeforeWriteFailsClosed()
    {
        var expected = Window();
        var changed = expected with { WindowTitle = "另一个窗口" };
        var foreground = new SequencedForegroundProvider(expected, expected, expected, changed);
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")]);
        var executor = new SafeForegroundSearchExecutor(foreground, driver);

        var exception = Assert.Throws<WindowIdentityException>(
            () => executor.Search(expected, "天气"));

        Assert.Equal(WindowIdentityErrorCodes.Changed, exception.Code);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ForegroundWindowChangeImmediatelyBeforeWriteFailsClosed()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")]);
        driver.OnRead = (read, snapshot) =>
        {
            if (read == 1)
            {
                driver.ForegroundWindowHandle = 43;
            }

            return snapshot;
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, exception.Code);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ElementBoundsChangeImmediatelyBeforeWriteFailsClosed()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            OnRead = (read, snapshot) => read == 2
                ? snapshot with { Bounds = new DesktopSearchBounds(11, 10, 400, 30) }
                : snapshot
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, exception.Code);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ElementRuntimeIdChangeAfterWriteFailsClosedBeforeSubmit()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            OnRead = (read, snapshot) => read == 3
                ? snapshot with { RuntimeId = "replacement" }
                : snapshot
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void FocusLossAfterWriteFailsClosedBeforeSubmit()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            OnRead = (read, snapshot) => read == 3
                ? snapshot with { HasKeyboardFocus = false }
                : snapshot
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.FocusChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void FocusLossImmediatelyBeforeEnterFailsClosed()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            OnRead = (read, snapshot) => read == 4
                ? snapshot with { HasKeyboardFocus = false }
                : snapshot
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.FocusChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ForegroundChangeInsideSubmitBoundaryPreventsEnter()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            BeforeSubmitSend = (current, _) => current.ForegroundWindowHandle = 43
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void FocusChangeInsideSubmitBoundaryPreventsEnter()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            BeforeSubmitSend = (current, control) => current.Mutate(
                control,
                snapshot => snapshot with { HasKeyboardFocus = false })
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.FocusChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ExactValueChangeInsideSubmitBoundaryPreventsEnter()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            BeforeSubmitSend = (current, control) => current.Mutate(
                control,
                snapshot => snapshot with { Value = "changed" })
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.WriteVerificationFailed, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void OwnershipChangeInsideSubmitBoundaryPreventsEnter()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            BeforeSubmitSend = (current, control) => current.Mutate(
                control,
                snapshot => snapshot with { OwnerWindowHandle = 43 })
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.TargetChanged, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ExactValueMismatchFailsClosedBeforeSubmit()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")])
        {
            OnRead = (read, snapshot) => read == 3
                ? snapshot with { Value = "different" }
                : snapshot
        };

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, "天气"));

        Assert.Equal(DesktopSearchErrorCodes.WriteVerificationFailed, exception.Code);
        Assert.Equal(1, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Theory]
    [InlineData("天气\r\n明天")]
    [InlineData("天气\t明天")]
    [InlineData("天气\u0001明天")]
    public void ExecutionBoundaryRejectsControlCharactersBeforeActivation(string query)
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")]);

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, query));

        Assert.Equal(DesktopSearchErrorCodes.InvalidQuery, exception.Code);
        Assert.Equal(0, driver.ActivationCount);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    [Fact]
    public void ExecutionBoundaryRejectsOverTwoHundredCharactersBeforeActivation()
    {
        var expected = Window();
        var driver = new FakeSearchDriver([Control("primary", name: "搜索框")]);

        var exception = Assert.Throws<DesktopSearchException>(
            () => Executor(expected, driver).Search(expected, new string('长', 201)));

        Assert.Equal(DesktopSearchErrorCodes.InvalidQuery, exception.Code);
        Assert.Equal(0, driver.ActivationCount);
        Assert.Equal(0, driver.SetValueCount);
        Assert.Equal(0, driver.SubmitCount);
    }

    private static SafeForegroundSearchExecutor Executor(
        ForegroundWindowSnapshot expected,
        FakeSearchDriver driver) =>
        new(new StableForegroundProvider(expected), driver);

    private static ForegroundWindowSnapshot Window() =>
        new(
            42,
            "合成浏览器",
            "fake-browser",
            101,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 1, TimeSpan.Zero));

    private static DesktopSearchControl Control(
        string id,
        string name = "",
        string automationId = "",
        string helpText = "",
        string labeledBy = "") =>
        new(
            id,
            new DesktopSearchControlSnapshot(
                RuntimeId: id,
                OwnerWindowHandle: 42,
                Name: name,
                AutomationId: automationId,
                HelpText: helpText,
                LabeledBy: labeledBy,
                IsEnabled: true,
                IsOffscreen: false,
                IsPassword: false,
                IsReadOnly: false,
                SupportsValuePattern: true,
                HasKeyboardFocus: false,
                Bounds: new DesktopSearchBounds(10, 10, 400, 30),
                Value: string.Empty));

    private sealed class StableForegroundProvider(ForegroundWindowSnapshot snapshot)
        : IForegroundWindowContextProvider
    {
        public ForegroundWindowSnapshot? GetLastExternalWindow() => snapshot;

        public ForegroundWindowSnapshot? ResolveWindow(long windowHandle) =>
            windowHandle == snapshot.WindowHandle ? snapshot : null;
    }

    private sealed class SequencedForegroundProvider(params ForegroundWindowSnapshot[] snapshots)
        : IForegroundWindowContextProvider
    {
        private int _index;

        public ForegroundWindowSnapshot? GetLastExternalWindow() => snapshots[0];

        public ForegroundWindowSnapshot? ResolveWindow(long windowHandle)
        {
            var index = Math.Min(_index++, snapshots.Length - 1);
            return snapshots[index];
        }
    }

    private sealed class FakeSearchDriver(IReadOnlyList<DesktopSearchControl> controls)
        : IDesktopSearchAutomationDriver
    {
        private readonly Dictionary<string, DesktopSearchControlSnapshot> _states =
            controls.ToDictionary(
                control => (string)control.NativeReference,
                control => control.Snapshot,
                StringComparer.Ordinal);
        private int _readCount;

        public Func<int, DesktopSearchControlSnapshot, DesktopSearchControlSnapshot>? OnRead { get; set; }

        public Action<FakeSearchDriver, DesktopSearchControl>? BeforeSubmitSend { get; init; }

        public long ForegroundWindowHandle { get; set; } = 42;

        public int ActivationCount { get; private set; }

        public int SetValueCount { get; private set; }

        public int FocusCount { get; private set; }

        public int SubmitCount { get; private set; }

        public string Value(string id) => _states[id].Value;

        public void Mutate(
            DesktopSearchControl control,
            Func<DesktopSearchControlSnapshot, DesktopSearchControlSnapshot> mutation)
        {
            var id = (string)control.NativeReference;
            _states[id] = mutation(_states[id]);
        }

        public bool IsWindow(long windowHandle) => windowHandle == 42;

        public bool TryActivate(long windowHandle)
        {
            ActivationCount++;
            return windowHandle == 42;
        }

        public long GetForegroundWindowHandle() => ForegroundWindowHandle;

        public IReadOnlyList<DesktopSearchControl> Discover(long windowHandle) => controls;

        public DesktopSearchControlSnapshot Read(DesktopSearchControl control)
        {
            var id = (string)control.NativeReference;
            var current = _states[id];
            _readCount++;
            current = OnRead?.Invoke(_readCount, current) ?? current;
            _states[id] = current;
            return current;
        }

        public void Focus(DesktopSearchControl control)
        {
            FocusCount++;
            var id = (string)control.NativeReference;
            _states[id] = _states[id] with { HasKeyboardFocus = true };
        }

        public void SetValue(DesktopSearchControl control, string value)
        {
            SetValueCount++;
            var id = (string)control.NativeReference;
            _states[id] = _states[id] with { Value = value };
        }

        public void SubmitEnter(
            DesktopSearchControl control,
            Action validateAtSendBoundary)
        {
            BeforeSubmitSend?.Invoke(this, control);
            validateAtSendBoundary();
            SubmitCount++;
        }
    }
}
