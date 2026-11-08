using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows;

internal static class DesktopTaskSafety
{
    internal const int MaximumControls = DesktopTaskLimits.MaximumControls;
    internal const int MaximumDepth = 20;
    internal const int MaximumText = DesktopTaskLimits.MaximumControlText;
    internal const int MaximumValue = DesktopTaskLimits.MaximumValueLength;

    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "WindowsTerminal", "wt", "conhost", "OpenConsole",
        "CredentialUIBroker", "LogonUI", "LockApp", "consent", "SecurityHealthHost", "SecHealthUI",
        "regedit", "mmc", "runas"
    };

    private static readonly string[] SensitiveWindowWords =
    [
        "Windows 安全中心", "Windows Security", "用户账户控制", "User Account Control",
        "输入密码", "请输入密码", "更改密码", "安全验证", "输入凭据", "Windows PowerShell",
        "命令提示符", "Windows Terminal", "Developer Tools", "DevTools", "开发者工具"
    ];

    private static readonly string[] SensitiveControlWords =
    [
        "password", "passcode", "credential", "secret", "token", "apikey", "api key",
        "密码", "口令", "密钥", "凭据", "验证码", "安全码", "银行卡号", "信用卡号",
        "命令提示符", "powershell", "终端", "控制台", "开发者工具", "devtools",
        "commandline", "command line", "console", "执行命令", "immediate window"
    ];

    internal static void RequireWindow(DesktopTaskWindow window)
    {
        if (window.Handle == 0 || window.ProcessId <= 0 || window.ProcessStartedAtUtc == default
            || string.IsNullOrWhiteSpace(window.ProcessName) || string.IsNullOrWhiteSpace(window.ClassName)
            || window.Title is null || !Path.IsPathFullyQualified(window.ExecutablePath))
            throw Error("window_unavailable", "无法可靠识别目标窗口，已停止本次操作。");
        if (window.FrameIdentity is { } frame && (window.ClassName != "Windows.UI.Core.CoreWindow"
            || frame.Handle == 0 || frame.Handle == window.Handle || frame.ProcessId <= 0 || frame.ProcessId == window.ProcessId
            || frame.ProcessStartedAtUtc == default || frame.ClassName != "ApplicationFrameWindow"
            || !string.Equals(frame.ExecutablePath, DesktopTaskWindowBinding.TrustedFramePath, StringComparison.OrdinalIgnoreCase)))
            throw Error("window_unavailable", "应用内容窗口的系统宿主身份不可靠，已停止本次操作。");
        var process = Path.GetFileNameWithoutExtension(window.ProcessName);
        if (BlockedProcesses.Contains(process) || BlockedProcesses.Contains(Path.GetFileNameWithoutExtension(window.ExecutablePath))
            || window.ClassName.Equals("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase)
            || window.ClassName.Equals("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase)
            || window.ClassName.Equals("Credential Dialog Xaml Host", StringComparison.OrdinalIgnoreCase)
            || window.ClassName.Equals("#32770", StringComparison.Ordinal)
                && (window.Title.Trim().Equals("运行", StringComparison.Ordinal)
                    || window.Title.Trim().Equals("Run", StringComparison.OrdinalIgnoreCase))
            || SensitiveWindowWords.Any(word => window.Title.Contains(word, StringComparison.OrdinalIgnoreCase)))
            throw Error("sensitive_window", "终端、系统命令、安全验证或凭据窗口不支持自动操作。");
    }

    internal static bool SameWindow(DesktopTaskWindow expected, DesktopTaskWindow actual) =>
        expected.Handle == actual.Handle && expected.ProcessId == actual.ProcessId
        && expected.ProcessStartedAtUtc == actual.ProcessStartedAtUtc
        && string.Equals(expected.ProcessName, actual.ProcessName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.ExecutablePath, actual.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.ClassName, actual.ClassName, StringComparison.Ordinal)
        && SameFrame(expected.FrameIdentity, actual.FrameIdentity);

    private static bool SameFrame(DesktopTaskFrameIdentity? expected, DesktopTaskFrameIdentity? actual) =>
        expected is null ? actual is null : actual is not null
        && expected.Handle == actual.Handle && expected.ProcessId == actual.ProcessId
        && expected.ProcessStartedAtUtc == actual.ProcessStartedAtUtc
        && string.Equals(expected.ExecutablePath, actual.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.ClassName, actual.ClassName, StringComparison.Ordinal);

    internal static long ForegroundHandle(DesktopTaskWindow window) => window.FrameIdentity?.Handle ?? window.Handle;

    internal static bool Sensitive(DesktopTaskElementMetadata metadata) => metadata.Password
        || SensitiveControlWords.Any(word => metadata.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
            || metadata.AutomationId.Contains(word, StringComparison.OrdinalIgnoreCase)
            || metadata.ParentName.Contains(word, StringComparison.OrdinalIgnoreCase));

    internal static bool Usable(DesktopTaskElementMetadata metadata) => !Sensitive(metadata)
        && !metadata.Offscreen && metadata.RuntimeId.Length is > 0 and <= 64
        && metadata.Bounds is { Width: > 0, Height: > 0 }
        && double.IsFinite(metadata.Bounds.X) && double.IsFinite(metadata.Bounds.Y)
        && double.IsFinite(metadata.Bounds.Width) && double.IsFinite(metadata.Bounds.Height);

    // Some providers expose writable ValuePattern on radio buttons and other
    // controls. Pattern availability alone does not identify a text input.
    // A ComboBox additionally needs its actual ValuePattern to report writable.
    internal static bool IsTextInputType(string controlType, bool isStructuredCell = false) =>
        controlType is "Edit" or "Document" or "ComboBox" || controlType == "DataItem" && isStructuredCell;

    internal static DesktopTaskControl SnapshotControl(Guid snapshot, int index, DesktopTaskElementMetadata metadata,
        DesktopTaskElementDetails details, out bool truncated)
    {
        var name = Clip(metadata.Name, 400);
        var automationId = Clip(metadata.AutomationId, 200);
        var parent = Clip(metadata.ParentName, 400);
        var value = Clip(details.Value, MaximumValue);
        var labelsClipped = name.Length != metadata.Name.Length || automationId.Length != metadata.AutomationId.Length
            || parent.Length != metadata.ParentName.Length;
        var valueClipped = value.Length != details.Value.Length || details.TextTruncated;
        truncated = labelsClipped || valueClipped;
        // A clipped label cannot identify an action, and a clipped old value cannot
        // authorize replacing text. Long read-only documents can still be scrolled.
        var actions = metadata.Enabled && !labelsClipped ? details.Actions
            .Where(action => action != DesktopTaskActionKind.SetText
                || !valueClipped && !details.IsReadOnly && IsTextInputType(metadata.ControlType,
                    details.IsStructuredCell && DesktopTaskGridCell.HasValidBinding(details.State)))
            .Distinct().Order().ToArray() : [];
        var state = FormattableString.Invariant($"enabled={metadata.Enabled};offscreen={metadata.Offscreen};pid={metadata.ProcessId};hwnd={metadata.NativeHandle};")
            + details.State + ";metadata=" + MetadataHash(metadata);
        return new($"{snapshot:N}:{index}", metadata.RuntimeId.ToArray(), name, automationId,
            metadata.ControlType, parent, value, state, details.IsReadOnly, metadata.Bounds, actions);
    }

    internal static int TextLength(DesktopTaskControl control) => control.Name.Length + control.AutomationId.Length
        + control.ControlType.Length + control.ParentName.Length + control.Value.Length + control.State.Length;

    internal static bool SameControl(DesktopTaskControl expected, DesktopTaskControl actual) =>
        expected.RuntimeId.SequenceEqual(actual.RuntimeId) && expected.Name == actual.Name
        && expected.AutomationId == actual.AutomationId && expected.ControlType == actual.ControlType
        && expected.ParentName == actual.ParentName && expected.Bounds == actual.Bounds
        && expected.State == actual.State && expected.IsReadOnly == actual.IsReadOnly
        && expected.Actions.SequenceEqual(actual.Actions);

    internal static bool IsSnapshotControlId(string id, Guid snapshot) => !string.IsNullOrEmpty(id) && snapshot != Guid.Empty
        && id.StartsWith(snapshot.ToString("N") + ":", StringComparison.Ordinal)
        && int.TryParse(id.AsSpan(33), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
        && index is >= 0 and < MaximumControls;

    internal static void RequireText(string? text)
    {
        if (text is null || text.Length > MaximumValue || text.Any(character =>
                char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw Error("invalid_text", "输入文字超出长度限制或包含不支持的控制字符。");
    }

    internal static DesktopTaskException Error(string suffix, string message) => new("desktop_task_" + suffix, message);
    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];
    private static string MetadataHash(DesktopTaskElementMetadata metadata) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\0', metadata.Name, metadata.AutomationId, metadata.ControlType, metadata.ParentName))));
}
