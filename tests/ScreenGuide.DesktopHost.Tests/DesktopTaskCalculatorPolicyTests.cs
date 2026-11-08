using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopTaskCalculatorPolicyTests
{
    private static DesktopTaskWindow Calculator() => new(11, 22, "CalculatorApp", DateTimeOffset.UtcNow,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps",
            "Microsoft.WindowsCalculator_1.0.0.0_x64__8wekyb3d8bbwe", "CalculatorApp.exe"),
        "Windows.UI.Core.CoreWindow", "计算器", new(33, 44, DateTimeOffset.UtcNow,
            Path.Combine(Environment.SystemDirectory, "ApplicationFrameHost.exe"), "ApplicationFrameWindow"));
    private static DesktopTaskControl Key(string id = "num2Button", string name = "二") =>
        new("control-1", [4, 1], name, id, "Button", "计算器", "", "enabled=True", true,
            new(10, 10, 40, 40), [DesktopTaskActionKind.Invoke]);
    private static DesktopTaskProposal Press(DesktopTaskControl control) =>
        new(Guid.NewGuid(), DesktopTaskActionKind.Invoke, control.Id, null, "继续用户要求的计算");

    [Theory]
    [InlineData("num2Button", "二")]
    [InlineData("num9Button", "九")]
    [InlineData("plusButton", "加")]
    [InlineData("equalButton", "等于")]
    public void KnownCalculatorArithmeticContinuesWithinTask(string id, string name)
    {
        var key = Key(id, name);
        Assert.False(DesktopTaskStepPolicy.RequiresStepConfirmation(key, Press(key), Calculator()));
    }

    [Theory]
    [InlineData("clearButton", "清除")]
    [InlineData("memButton", "记忆存储")]
    [InlineData("TogglePaneButton", "打开导航")]
    [InlineData("num2Button", "发送")]
    [InlineData("otherButton", "二")]
    public void OtherButtonsOrChangedNamesStillNeedReview(string id, string name)
    {
        var key = Key(id, name);
        Assert.True(DesktopTaskStepPolicy.RequiresStepConfirmation(key, Press(key), Calculator()));
    }

    [Fact]
    public void MatchingLabelsInAnotherApplicationCannotBypassReview()
    {
        var key = Key();
        var trusted = Calculator();
        foreach (var untrusted in new DesktopTaskWindow?[]
        {
            null,
            trusted with { ProcessName = "other" },
            trusted with { ClassName = "other" },
            trusted with { ExecutablePath = @"C:\synthetic\CalculatorApp.exe" },
            trusted with { ExecutablePath = trusted.ExecutablePath.Replace("8wekyb3d8bbwe", "otherpublisher") },
            trusted with { FrameIdentity = null },
            trusted with { FrameIdentity = trusted.FrameIdentity! with { ExecutablePath = @"C:\synthetic\ApplicationFrameHost.exe" } }
        }) Assert.True(DesktopTaskStepPolicy.RequiresStepConfirmation(key, Press(key), untrusted));
        var label = key with { ControlType = "Text" };
        Assert.True(DesktopTaskStepPolicy.RequiresStepConfirmation(label, Press(label), trusted));
    }

    [Fact]
    public void ArithmeticExceptionDoesNotAuthorizeAnUnboundTarget()
    {
        var key = Key();
        Assert.Throws<DesktopTaskException>(() => DesktopTaskStepPolicy.RequiresStepConfirmation(key,
            Press(key) with { TargetId = "another-snapshot-control" }, Calculator()));
    }
}
