using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Windows;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class VisualSearchInputStateTests
{
    [Fact]
    public void PreCancelledActivationDoesNotCallActivationOrForegroundCheck()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var activations = 0;
        var foregroundChecks = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => WindowsVisualSearchInputDriver.ActivateWindow(
            42, cancellation.Token, (_, _, _) => { activations++; return true; }, _ => foregroundChecks++));

        Assert.Equal(0, activations);
        Assert.Equal(0, foregroundChecks);
    }

    [Fact]
    public void ActivationReceivesTheRealCancellationTokenAndCannotContinueAfterStop()
    {
        using var cancellation = new CancellationTokenSource();
        var foregroundChecks = 0;
        var activations = 0;
        bool Activate(IntPtr window, TimeSpan timeout, CancellationToken token)
        {
            activations++;
            Assert.Equal(new IntPtr(42), window);
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return true;
        }

        Assert.ThrowsAny<OperationCanceledException>(() => WindowsVisualSearchInputDriver.ActivateWindow(
            42, cancellation.Token, Activate, _ => foregroundChecks++));

        Assert.Equal(1, activations);
        Assert.Equal(0, foregroundChecks);
    }

    [Theory]
    [InlineData(0x01)] // Left
    [InlineData(0x02)] // Right
    [InlineData(0x04)] // Middle
    [InlineData(0x05)] // X1
    [InlineData(0x06)] // X2
    public void HeldMouseButtonPreventsBothPointerMovementAndMouseDown(int heldButton)
    {
        var moves = 0;
        var presses = 0;
        short State(int key) => key == heldButton ? unchecked((short)0x8000) : (short)0;

        var moveError = Assert.Throws<DesktopSearchException>(() =>
            WindowsVisualSearchInputDriver.WithReleasedMouseButtons(() => moves++, State));
        var pressError = Assert.Throws<DesktopSearchException>(() =>
            WindowsVisualSearchInputDriver.WithReleasedMouseButtons(() => presses++, State));

        Assert.Equal(DesktopSearchErrorCodes.FocusChanged, moveError.Code);
        Assert.Equal(DesktopSearchErrorCodes.FocusChanged, pressError.Code);
        Assert.Equal(0, moves);
        Assert.Equal(0, presses);
    }

    [Fact]
    public void UserPressAfterPointerMovementPreventsTheAssistantsMouseDown()
    {
        var moves = 0;
        var presses = 0;
        var held = false;
        short State(int key) => held && key == 0x01 ? unchecked((short)0x8000) : (short)0;

        WindowsVisualSearchInputDriver.WithReleasedMouseButtons(() => moves++, State);
        held = true;
        Assert.Throws<DesktopSearchException>(() =>
            WindowsVisualSearchInputDriver.WithReleasedMouseButtons(() => presses++, State));

        Assert.Equal(1, moves);
        Assert.Equal(0, presses);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)] // The historical click bit does not mean a button is held now.
    public void ReleasedButtonsPermitEachActionExactlyOnce(int state)
    {
        var moves = 0;
        var presses = 0;
        short State(int _) => (short)state;

        WindowsVisualSearchInputDriver.WithReleasedMouseButtons(() => moves++, State);
        WindowsVisualSearchInputDriver.WithReleasedMouseButtons(() => presses++, State);

        Assert.Equal(1, moves);
        Assert.Equal(1, presses);
    }
}
