using System.IO;
using System.Runtime.InteropServices;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class WordDesktopTaskBodyTests
{
    private static readonly string DocumentIdentity = new('A', 64);

    [Fact]
    public void OnlyTheExplicitBodyOfTrustedWordCanUseNativeObjectModel()
    {
        Assert.True(WordDesktopTaskBody.IsCandidate(Window(), Metadata()));
        Assert.False(WordDesktopTaskBody.IsCandidate(Window() with { ExecutablePath = @"C:\Synthetic\WINWORD.EXE" }, Metadata()));
        Assert.False(WordDesktopTaskBody.IsCandidate(Window() with { ProcessName = "other" }, Metadata()));
        Assert.False(WordDesktopTaskBody.IsCandidate(Window() with { ClassName = "OtherWindow" }, Metadata()));
        Assert.False(WordDesktopTaskBody.IsCandidate(Window(), Metadata() with { ProcessId = 301 }));
        Assert.False(WordDesktopTaskBody.IsCandidate(Window(), Metadata() with { AutomationId = "TellMeTextBoxAutomationId" }));
        Assert.False(WordDesktopTaskBody.IsCandidate(Window(), Metadata() with { ControlType = "Document" }));
        Assert.False(WordDesktopTaskBody.IsCandidate(Window(), Metadata() with { Password = true }));
        Assert.False(WordDesktopTaskBody.IsCandidate(Window(), Metadata() with { Offscreen = true }));
    }

    [Fact]
    public void OfficeLikePathCannotAcquireWordBodyCapability()
    {
        var actual = Window().ExecutablePath;
        Assert.False(WordDesktopTaskBody.IsTrustedExecutable(actual + ".fake"));
        Assert.False(WordDesktopTaskBody.IsTrustedExecutable(actual.Replace("Office16", "Office16-copy", StringComparison.Ordinal)));
        Assert.False(WordDesktopTaskBody.IsTrustedExecutable("WINWORD.EXE"));
        Assert.False(WordDesktopTaskBody.IsTrustedExecutable(@"C:\Synthetic\Microsoft Office\Root\Office16\WINWORD.EXE"));
    }

    [Fact]
    public void OnlyAnExactTrustedPageWithOneDirectBodyOmitsItsDuplicateText()
    {
        var page = PageMetadata();
        Assert.True(WordDesktopTaskBody.IsPageTextMirror(Window(), page, [Metadata()]));
        Assert.True(WordDesktopTaskBody.IsPageTextMirror(Window(), page with
        {
            AutomationId = "UIA_AutomationId_Word_Page_12"
        }, [Metadata(), Metadata() with { RuntimeId = [3], AutomationId = "OtherControl" }]));
        Assert.False(WordDesktopTaskBody.IsPageTextMirror(Window(), page, []));
        Assert.False(WordDesktopTaskBody.IsPageTextMirror(Window(), page,
            [Metadata(), Metadata() with { RuntimeId = [3] }]));
        Assert.False(WordDesktopTaskBody.IsPageTextMirror(Window(), page,
            [Metadata() with { AutomationId = "OtherControl" }]));
    }

    [Theory]
    [InlineData("UIA_AutomationId_Word_Page_")]
    [InlineData("UIA_AutomationId_Word_Page_0")]
    [InlineData("UIA_AutomationId_Word_Page_01")]
    [InlineData("UIA_AutomationId_Word_Page_-1")]
    [InlineData("UIA_AutomationId_Word_Page_1x")]
    [InlineData("UIA_AutomationId_Word_Page_1\n")]
    [InlineData("UIA_AutomationId_Word_Page_１")]
    [InlineData("uia_AutomationId_Word_Page_1")]
    [InlineData("Other_UIA_AutomationId_Word_Page_1")]
    public void SimilarPageIdentifiersKeepTheirNormalTextObservation(string automationId)
    {
        Assert.False(WordDesktopTaskBody.IsPageTextMirror(Window(),
            PageMetadata() with { AutomationId = automationId }, [Metadata()]));
    }

    [Fact]
    public void UntrustedWindowsAndNonPageControlsCannotHideTextBehindWordNames()
    {
        var window = Window();
        var page = PageMetadata();
        foreach (var untrusted in new[]
        {
            window with { ExecutablePath = @"C:\Synthetic\WINWORD.EXE" },
            window with { ProcessName = "other" },
            window with { ClassName = "OtherWindow" },
            window with { ProcessId = 301 },
            window with { FrameIdentity = new(999, 998, window.ProcessStartedAtUtc, @"C:\Synthetic\frame.exe", "ApplicationFrameWindow") }
        }) Assert.False(WordDesktopTaskBody.IsPageTextMirror(untrusted, page, [Metadata()]));
        foreach (var invalidPage in new[]
        {
            page with { ProcessId = 301 },
            page with { ControlType = "Document" },
            page with { ControlType = "Edit" },
            page with { Password = true },
            page with { Offscreen = true }
        }) Assert.False(WordDesktopTaskBody.IsPageTextMirror(window, invalidPage, [Metadata()]));
    }

    [Fact]
    public void AForeignOrUnusableBodyCannotSuppressThePageText()
    {
        var page = PageMetadata();
        foreach (var invalidBody in new[]
        {
            Metadata() with { ProcessId = 301 },
            Metadata() with { ControlType = "Document" },
            Metadata() with { Password = true },
            Metadata() with { Offscreen = true },
            Metadata() with { RuntimeId = page.RuntimeId }
        }) Assert.False(WordDesktopTaskBody.IsPageTextMirror(Window(), page, [invalidBody]));
    }

    [Theory]
    [InlineData("readOnly")]
    [InlineData("protected")]
    [InlineData("tracking")]
    [InlineData("macro")]
    [InlineData("template")]
    [InlineData("revision")]
    [InlineData("table")]
    [InlineData("inlineObject")]
    [InlineData("shape")]
    [InlineData("field")]
    [InlineData("form")]
    [InlineData("contentControl")]
    [InlineData("hyperlink")]
    [InlineData("subdocument")]
    [InlineData("footnote")]
    [InlineData("endnote")]
    [InlineData("section")]
    public void NonPlainOrProtectedDocumentNeverOffersWholeBodyReplacement(string condition)
    {
        var plain = new WordDesktopTaskBodyProtection();
        var restricted = condition switch
        {
            "readOnly" => plain with { ReadOnly = true },
            "protected" => plain with { ProtectionType = 3 },
            "tracking" => plain with { TrackRevisions = true },
            "macro" => plain with { HasVbaProject = true },
            "template" => plain with { DocumentType = 1 },
            "revision" => plain with { Revisions = 1 },
            "table" => plain with { Tables = 1 },
            "inlineObject" => plain with { InlineShapes = 1 },
            "shape" => plain with { Shapes = 1 },
            "field" => plain with { Fields = 1 },
            "form" => plain with { FormFields = 1 },
            "contentControl" => plain with { ContentControls = 1 },
            "hyperlink" => plain with { Hyperlinks = 1 },
            "subdocument" => plain with { Subdocuments = 1 },
            "footnote" => plain with { Footnotes = 1 },
            "endnote" => plain with { Endnotes = 1 },
            "section" => plain with { Sections = 2 },
            _ => throw new ArgumentOutOfRangeException(nameof(condition))
        };

        Assert.True(WordDesktopTaskBody.CanWrite(plain, "ordinary text", false));
        Assert.False(WordDesktopTaskBody.CanWrite(restricted, "ordinary text", false));
    }

    [Fact]
    public void PlainWhitespaceIsPreservedButTruncationAndObjectCharactersDisableWriting()
    {
        var plain = new WordDesktopTaskBodyProtection();
        Assert.True(WordDesktopTaskBody.CanWrite(plain, " \r\n\t ", false));
        Assert.False(WordDesktopTaskBody.CanWrite(plain, "old", true));
        Assert.False(WordDesktopTaskBody.CanWrite(plain, "cell\a", false));
        Assert.False(WordDesktopTaskBody.CanWrite(plain, "object\uFFFC", false));
    }

    [Theory]
    [InlineData(1, 1500, 0, false)]
    [InlineData(1501, 1500, 1500, false)]
    [InlineData(1502, 1500, 1500, true)]
    [InlineData(1000000, 500, 500, true)]
    public void NativeTextRangeIsBoundedBeforeReadingAndExcludesOnlyFinalWordMarker(
        int nativeEnd, int limit, int expectedEnd, bool truncated)
    {
        var span = WordDesktopTaskBody.ReadRange(0, nativeEnd, limit);
        Assert.Equal(0, span.Start);
        Assert.Equal(expectedEnd, span.End);
        Assert.Equal(truncated, span.Truncated);
    }

    [Theory]
    [InlineData(1, 10, 1500)]
    [InlineData(0, 0, 1500)]
    [InlineData(0, 10, 1501)]
    [InlineData(0, 10, 0)]
    public void UnexpectedStoryRangeOrReadBudgetIsRejected(int start, int end, int limit)
    {
        Assert.Throws<DesktopTaskException>(() => WordDesktopTaskBody.ReadRange(start, end, limit));
    }

    [Fact]
    public void EmptyBodyDoesNotCallTheNativeGetterThatReturnsNullForCollapsedRanges()
    {
        var span = WordDesktopTaskBody.ReadRange(0, 1, 1500);
        var calls = 0;
        var text = WordDesktopTaskBody.ReadTextRange(span.Start, span.End, () => { calls++; return null; });

        Assert.Equal(string.Empty, text);
        Assert.Equal(0, calls);
        Assert.True(WordDesktopTaskBody.CanWrite(new(), text, span.Truncated));
    }

    [Fact]
    public void NonemptyBodyReadsNativeTextOnceAndPreservesWhitespace()
    {
        var span = WordDesktopTaskBody.ReadRange(0, 4, 1500);
        var calls = 0;
        var text = WordDesktopTaskBody.ReadTextRange(span.Start, span.End, () => { calls++; return " a "; });

        Assert.Equal(" a ", text);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NonemptyRangeWithMissingNativeTextRemainsAnError()
    {
        var span = WordDesktopTaskBody.ReadRange(0, 2, 1500);
        var calls = 0;
        var exception = Assert.Throws<DesktopTaskException>(() =>
            WordDesktopTaskBody.ReadTextRange(span.Start, span.End, () => { calls++; return null; }));

        Assert.Equal("desktop_task_word_body_unavailable", exception.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DocumentIdentityBindsWindowProcessDocumentAndCreationWithoutExposingPath()
    {
        var window = Window();
        var created = new DateTime(2026, 1, 1, 0, 0, 0);
        const string fullName = @"C:\Synthetic\review.docx";
        var identity = WordDesktopTaskBody.Identity(window, 101, window.Handle, fullName, "review.docx", created);
        var variants = new[]
        {
            WordDesktopTaskBody.Identity(window with { ProcessStartedAtUtc = window.ProcessStartedAtUtc.AddSeconds(1) }, 101, window.Handle, fullName, "review.docx", created),
            WordDesktopTaskBody.Identity(window, 102, window.Handle, fullName, "review.docx", created),
            WordDesktopTaskBody.Identity(window, 101, window.Handle, @"C:\Synthetic\other\review.docx", "review.docx", created),
            WordDesktopTaskBody.Identity(window, 101, window.Handle, fullName, "review.docx", created.AddSeconds(1))
        };
        Assert.All(variants, other => Assert.NotEqual(identity, other));
        var details = WordDesktopTaskBody.ToDetails(new(identity, "text", true));
        Assert.Equal("officeDocument=" + identity, details.State);
        Assert.DoesNotContain("review.docx", details.State);
        Assert.DoesNotContain("Synthetic", details.State);
    }

    [Fact]
    public void ReadOnlyAndTruncatedObservationsNeverOfferSetText()
    {
        var readOnly = WordDesktopTaskBody.ToDetails(new(DocumentIdentity, "existing", false));
        Assert.True(readOnly.IsReadOnly);
        Assert.Empty(readOnly.Actions);
        var truncated = WordDesktopTaskBody.ToDetails(new(DocumentIdentity, new string('x', 1500), false, true));
        Assert.True(truncated.TextTruncated);
        Assert.Empty(truncated.Actions);
        Assert.Equal([DesktopTaskActionKind.SetText], WordDesktopTaskBody.ToDetails(new(DocumentIdentity, "", true)).Actions);
    }

    [Fact]
    public void SuccessfulWriteRechecksAfterContextAndBeforeSingleWriteThenReadsBackExactly()
    {
        var session = new FakeSession { Snapshot = new(DocumentIdentity, "old", true) };
        const string replacement = " 第一行\n\n第二行 \t";

        WordDesktopTaskBody.SetText(session, Expected("old"), replacement,
            () => session.Events.Add("context"), () => session.Events.Add("boundary"));

        Assert.Equal(new[] { "read", "context", "read", "boundary", "write", "read" }, session.Events);
        Assert.Equal(1, session.Writes);
        Assert.Equal(replacement, session.Snapshot.Text);
        Assert.All(session.ReadLimits, limit => Assert.Equal(1500, limit));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("text")]
    [InlineData("protection")]
    [InlineData("truncation")]
    public void ChangesDuringContextObservationPreventWriting(string change)
    {
        var session = new FakeSession { Snapshot = new(DocumentIdentity, "old", true) };

        var exception = Assert.Throws<DesktopTaskException>(() => WordDesktopTaskBody.SetText(session, Expected("old"), "new", () =>
        {
            session.Snapshot = change switch
            {
                "document" => session.Snapshot with { Identity = new string('B', 64) },
                "text" => session.Snapshot with { Text = "user typed this" },
                "protection" => session.Snapshot with { Writable = false },
                "truncation" => session.Snapshot with { Truncated = true },
                _ => throw new ArgumentOutOfRangeException(nameof(change))
            };
        }, () => throw new InvalidOperationException("Must not reach the final boundary.")));

        Assert.Equal("desktop_task_word_body_changed", exception.Code);
        Assert.Equal(0, session.Writes);
    }

    [Fact]
    public void LateForegroundOrWindowBoundaryFailurePreventsNativeWrite()
    {
        var session = new FakeSession();
        var exception = Assert.Throws<DesktopTaskException>(() => WordDesktopTaskBody.SetText(session, Expected(""), "new", () => { },
            () => throw new DesktopTaskException("desktop_task_foreground_changed", "changed")));

        Assert.Equal("desktop_task_foreground_changed", exception.Code);
        Assert.Equal(0, session.Writes);
    }

    [Theory]
    [InlineData("first\rsecond")]
    [InlineData("first\r\nsecond")]
    [InlineData("embedded\uFFFCobject")]
    public void NonRoundtrippableInputIsRejectedBeforeAnyBodyRead(string text)
    {
        var session = new FakeSession();
        Assert.Throws<DesktopTaskException>(() => WordDesktopTaskBody.SetText(session, Expected(""), text, () => { }, () => { }));
        Assert.Empty(session.ReadLimits);
        Assert.Equal(0, session.Writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("otherControl")]
    public void ProposalCannotInventOrAmbiguouslyBindTheWordBody(string invalid)
    {
        var session = new FakeSession();
        var expected = Expected("");
        expected = invalid switch
        {
            "missing" => expected with { State = "enabled=True" },
            "duplicate" => expected with { State = expected.State + ";officeDocument=" + DocumentIdentity },
            "malformed" => expected with { State = "officeDocument=not-a-hash" },
            "otherControl" => expected with { AutomationId = "Font" },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        Assert.Throws<DesktopTaskException>(() => WordDesktopTaskBody.SetText(session, expected, "new", () => { }, () => { }));
        Assert.Empty(session.ReadLimits);
        Assert.Equal(0, session.Writes);
    }

    [Fact]
    public void AFailedReadbackDoesNotRetryTheAlreadyIssuedWrite()
    {
        var session = new FakeSession { AfterWrite = value => value with { Text = "different result" } };
        var exception = Assert.Throws<DesktopTaskException>(() => WordDesktopTaskBody.SetText(session, Expected(""), "new", () => { }, () => { }));
        Assert.Equal("desktop_task_word_body_not_verified", exception.Code);
        Assert.Equal(1, session.Writes);
    }

    [Fact]
    public void NativeFailureDoesNotRetryOrClaimAReadback()
    {
        var session = new FakeSession { WriteFailure = new COMException("synthetic failure") };
        Assert.Throws<COMException>(() => WordDesktopTaskBody.SetText(session, Expected(""), "new", () => { }, () => { }));
        Assert.Equal(1, session.Writes);
        Assert.Equal(2, session.ReadLimits.Count);
    }

    private static DesktopTaskWindow Window() => new(100, 300, "WINWORD", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft Office\Root\Office16\WINWORD.EXE"), "OpusApp", "Synthetic Word");

    private static DesktopTaskElementMetadata Metadata() => new([2], "正文", "Body", "Edit", "测试文档", new(10, 10, 200, 200),
        true, false, false, 300, 0);

    private static DesktopTaskElementMetadata PageMetadata() => new([1], "页面 1", "UIA_AutomationId_Word_Page_1",
        "Custom", "测试文档", new(10, 10, 200, 200), true, false, false, 300, 0);

    private static DesktopTaskControl Expected(string value) => new("body", [2], "正文", "Body", "Edit", "测试文档", value,
        "enabled=True;officeDocument=" + DocumentIdentity, false, new(10, 10, 200, 200), [DesktopTaskActionKind.SetText]);

    private sealed class FakeSession : IWordDesktopTaskBodySession
    {
        public WordDesktopTaskBodySnapshot Snapshot { get; set; } = new(DocumentIdentity, "", true);
        public List<string> Events { get; } = [];
        public List<int> ReadLimits { get; } = [];
        public Func<WordDesktopTaskBodySnapshot, WordDesktopTaskBodySnapshot>? AfterWrite { get; init; }
        public Exception? WriteFailure { get; init; }
        public int Writes { get; private set; }
        public WordDesktopTaskBodySnapshot Read(int maximumTextLength)
        {
            Events.Add("read");
            ReadLimits.Add(maximumTextLength);
            return Snapshot;
        }
        public void Write(string text, Action requireActionBoundary)
        {
            requireActionBoundary();
            Events.Add("write");
            Writes++;
            if (WriteFailure is not null) throw WriteFailure;
            Snapshot = Snapshot with { Text = text };
            if (AfterWrite is not null) Snapshot = AfterWrite(Snapshot);
        }
        public void Dispose() { }
    }
}
