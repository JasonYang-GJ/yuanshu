using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class DesktopTaskWindowBindingTests
{
    [Fact]
    public void OrdinaryTopLevelBindingKeepsTheExistingContract()
    {
        var window = Window(100, 200, "fixture", @"C:\Fixture\fixture.exe", "FixtureWindow");
        Assert.Same(window, DesktopTaskWindowBinding.Bind(new(window, 0, 100, false), null));
        Assert.Equal(100, DesktopTaskSafety.ForegroundHandle(window));
    }

    [Fact]
    public void OnlyTheContentWindowBecomesTheRootAndFrameIdentityRemainsSeparate()
    {
        var (content, frame) = Uwp();
        var bound = DesktopTaskWindowBinding.Bind(content, frame);
        Assert.Equal(content.Window.Handle, bound.Handle);
        Assert.Equal(content.Window.ProcessId, bound.ProcessId);
        Assert.Equal(content.Window.ExecutablePath, bound.ExecutablePath);
        Assert.Equal(frame.Window.Handle, bound.FrameIdentity!.Handle);
        Assert.Equal(frame.Window.ProcessId, bound.FrameIdentity.ProcessId);
        Assert.Equal(frame.Window.ProcessStartedAtUtc, bound.FrameIdentity.ProcessStartedAtUtc);
        Assert.Equal(frame.Window.ExecutablePath, bound.FrameIdentity.ExecutablePath);
        Assert.Equal(frame.Window.Handle, DesktopTaskSafety.ForegroundHandle(bound));
    }

    [Theory]
    [InlineData("not-child")]
    [InlineData("not-direct")]
    [InlineData("wrong-root")]
    [InlineData("wrong-content-class")]
    [InlineData("frame-is-child")]
    [InlineData("frame-is-not-root")]
    [InlineData("wrong-frame-class")]
    [InlineData("wrong-frame-process")]
    [InlineData("untrusted-frame-path")]
    [InlineData("same-process")]
    [InlineData("missing-frame")]
    public void ArbitraryChildrenOrUntrustedFrameRelationshipsAreRejected(string change)
    {
        var (content, frame) = Uwp();
        content = change switch
        {
            "not-child" => content with { IsChild = false },
            "not-direct" => content with { ParentHandle = 777 },
            "wrong-root" => content with { RootHandle = 778 },
            "wrong-content-class" => content with { Window = content.Window with { ClassName = "Edit" } },
            _ => content
        };
        frame = change switch
        {
            "frame-is-child" => frame with { IsChild = true },
            "frame-is-not-root" => frame with { RootHandle = 999 },
            "wrong-frame-class" => frame with { Window = frame.Window with { ClassName = "FixtureWindow" } },
            "wrong-frame-process" => frame with { Window = frame.Window with { ProcessName = "fixture" } },
            "untrusted-frame-path" => frame with { Window = frame.Window with { ExecutablePath = @"C:\Fixture\ApplicationFrameHost.exe" } },
            "same-process" => frame with { Window = frame.Window with { ProcessId = content.Window.ProcessId } },
            _ => frame
        };
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.Bind(content, change == "missing-frame" ? null : frame));
    }

    [Fact]
    public void BindingTheFrameItselfDoesNotGrantItsWholeSubtree()
    {
        var (_, frame) = Uwp();
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.Bind(frame, null));
    }

    [Fact]
    public void ACurrentFrameResolvesOnlyItsSingleDirectCoreContentWindow()
    {
        var (content, frame) = Uwp();
        var nonContent = content with { Window = content.Window with { Handle = 112, ClassName = "FrameDecoration" } };
        var bound = DesktopTaskWindowBinding.ResolveSingleContent(frame, [nonContent, content], false);
        Assert.Equal(content.Window.Handle, bound.Handle);
        Assert.Equal(frame.Window.Handle, bound.FrameIdentity!.Handle);
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.ResolveSingleContent(frame, [content], true));
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.ResolveSingleContent(frame, [nonContent], false));
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.ResolveSingleContent(frame,
            [content with { ParentHandle = 777 }], false));
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.ResolveSingleContent(
            frame with { Window = frame.Window with { ExecutablePath = @"C:\Fixture\ApplicationFrameHost.exe" } }, [content], false));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public void CurrentFrameWithMultipleCoreWindowsIsNeverGuessedEvenForTheSameProcess(int secondProcessId)
    {
        var (content, frame) = Uwp();
        var second = content with { Window = content.Window with { Handle = 113, ProcessId = secondProcessId } };
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.ResolveSingleContent(frame, [content, second], false));
    }

    [Fact]
    public void MetadataResolutionRequiresOneCompleteExactPidCoreWindowMatch()
    {
        var (content, frame) = Uwp();
        var unrelated = content with { Window = content.Window with { Handle = 111, ProcessId = 222 } };
        var result = DesktopTaskWindowBinding.ResolveChild(frame, [unrelated, content], content.Window.ProcessId, false);
        Assert.Equal(content.Window.Handle, result.Handle);
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.ResolveChild(frame, [content, content], content.Window.ProcessId, false));
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.ResolveChild(frame, [unrelated], content.Window.ProcessId, false));
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.ResolveChild(frame, [content], content.Window.ProcessId, true));
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.ResolveChild(frame, [content], 0, false));
        Assert.Throws<DesktopTaskException>(() => DesktopTaskWindowBinding.ResolveChild(frame,
            [content with { Window = content.Window with { ClassName = "Button" } }], content.Window.ProcessId, false));
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("path")]
    [InlineData("class")]
    [InlineData("removed")]
    public void EveryFrameIdentityFieldParticipatesInRevalidation(string change)
    {
        var (content, frame) = Uwp();
        var original = DesktopTaskWindowBinding.Bind(content, frame);
        var oldFrame = original.FrameIdentity!;
        var changed = original with { FrameIdentity = change switch
        {
            "handle" => oldFrame with { Handle = 901 },
            "pid" => oldFrame with { ProcessId = 301 },
            "start" => oldFrame with { ProcessStartedAtUtc = oldFrame.ProcessStartedAtUtc.AddSeconds(1) },
            "path" => oldFrame with { ExecutablePath = @"C:\Fixture\ApplicationFrameHost.exe" },
            "class" => oldFrame with { ClassName = "ChangedFrame" },
            _ => null
        } };
        Assert.False(DesktopTaskSafety.SameWindow(original, changed));
        Assert.True(DesktopTaskSafety.SameWindow(original, original with { Title = "任务内标题变化" }));
    }

    [Fact]
    public void DirectParentChangesAreRejectedEvenWhenProcessAndWindowMetadataStayTheSame()
    {
        var (content, _) = Uwp();
        Assert.False(DesktopTaskWindowBinding.SameMetadata(content, content with { ParentHandle = 901 }));
        Assert.False(DesktopTaskWindowBinding.SameMetadata(content, content with { RootHandle = 901 }));
        Assert.False(DesktopTaskWindowBinding.SameMetadata(content, content with { IsChild = false }));
    }

    private static (DesktopTaskNativeWindowMetadata Content, DesktopTaskNativeWindowMetadata Frame) Uwp() =>
        (new(Window(100, 200, "CalculatorApp", @"C:\Fixture\CalculatorApp.exe", "Windows.UI.Core.CoreWindow"), 900, 900, true),
         new(Window(900, 300, "ApplicationFrameHost", DesktopTaskWindowBinding.TrustedFramePath, "ApplicationFrameWindow"), 0, 900, false));

    private static DesktopTaskWindow Window(long handle, int pid, string process, string path, string className) =>
        new(handle, pid, process, DateTimeOffset.Parse("2026-11-04T00:00:00Z"), path, className, "合成应用");
}
