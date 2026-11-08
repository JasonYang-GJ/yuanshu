using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

internal static class DesktopTaskStepPolicy
{
    private static readonly Regex Sensitive = new(
        "密码|口令|验证码|私钥|密钥|令牌|password|passcode|private.?key|api.?key|access.?token|security.?code",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Consequential = new(
        "发送|发布|提交|付款|支付|购买|下单|订阅|转账|删除|移除|卸载|清空|格式化|保存|覆盖|替换|账号|帐户|账户|安全|权限|允许|授权|退出|关闭|确定|确认|send|post|publish|submit|pay|buy|purchase|subscribe|transfer|delete|remove|uninstall|erase|format|save|overwrite|replace|account|security|permission|allow|authorize|close|confirm|\\bok\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool RequiresStepConfirmation(DesktopTaskControl control, DesktopTaskProposal proposal, DesktopTaskWindow? window = null)
    {
        var identity = control.Name + " " + control.AutomationId + " " + control.ParentName;
        if (Sensitive.IsMatch(identity))
            throw new DesktopTaskException("desktop_task_sensitive_control", "密码、凭据和验证字段需要你亲自操作。");
        if (!control.Actions.Contains(proposal.Action) || proposal.TargetId != control.Id)
            throw new DesktopTaskException("desktop_task_action_scope", "这一步与当前可识别控件不一致，未执行。");
        if (proposal.Action == DesktopTaskActionKind.SetText)
        {
            if (control.IsReadOnly || string.IsNullOrWhiteSpace(control.Name) && string.IsNullOrWhiteSpace(control.AutomationId))
                throw new DesktopTaskException("desktop_task_unknown_field", "没有识别出可编辑字段，未输入文字。");
            if (proposal.Text is null || proposal.Text.Length > DesktopTaskLimits.MaximumValueLength
                || proposal.Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                throw new DesktopTaskException("desktop_task_invalid_text", "待输入文字不符合本次操作要求。");
            if (Regex.IsMatch(proposal.Text, @"^\s*(?:javascript:|vbscript:|powershell(?:\.exe)?\b|cmd(?:\.exe)?\s+/|mshta\b|file:|shell:)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new DesktopTaskException("desktop_task_command_input", "通用桌面任务不向字段输入系统命令或可执行网址。");
            // Replacing pre-existing user text is an explicit, separately shown step.
            return control.Value.Length > 0 && control.Value != proposal.Text || Consequential.IsMatch(identity);
        }
        // An arbitrary application's button label cannot prove its effects. Invoke
        // requires a reviewable step; ordinary selection/navigation stays in scope.
        return proposal.Action == DesktopTaskActionKind.Invoke && !SafeCalculatorKey(window, control)
            || Consequential.IsMatch(identity);
    }

    private static bool SafeCalculatorKey(DesktopTaskWindow? window, DesktopTaskControl control)
    {
        if (window is null || window.ProcessName != "CalculatorApp" || window.ClassName != "Windows.UI.Core.CoreWindow"
            || window.FrameIdentity is not { ClassName: "ApplicationFrameWindow" } frame
            || !string.Equals(frame.ExecutablePath, Path.Combine(Environment.SystemDirectory, "ApplicationFrameHost.exe"), StringComparison.OrdinalIgnoreCase)
            || control.ControlType != "Button") return false;
        var path = Path.GetFullPath(window.ExecutablePath);
        var package = Path.GetDirectoryName(path);
        if (package is null || !string.Equals(Path.GetFileName(path), "CalculatorApp.exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(package), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"), StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(Path.GetFileName(package), @"^Microsoft\.WindowsCalculator_\d+\.\d+\.\d+\.\d+_(?:x64|x86|arm64)__8wekyb3d8bbwe$")) return false;
        // Only the verified Windows calculator's arithmetic keys have known local
        // effects. Navigation, memory/history, clearing and all other buttons still
        // use a separately reviewable action confirmation.
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["num0Button"] = "零", ["num1Button"] = "一", ["num2Button"] = "二", ["num3Button"] = "三",
            ["num4Button"] = "四", ["num5Button"] = "五", ["num6Button"] = "六", ["num7Button"] = "七",
            ["num8Button"] = "八", ["num9Button"] = "九", ["plusButton"] = "加", ["minusButton"] = "减",
            ["multiplyButton"] = "乘以", ["divideButton"] = "除以", ["equalButton"] = "等于",
            ["decimalSeparatorButton"] = "十进制分隔符", ["negateButton"] = "正负", ["percentButton"] = "百分比",
            ["invertButton"] = "倒数", ["xpower2Button"] = "平方", ["squareRootButton"] = "平方根"
        };
        return names.TryGetValue(control.AutomationId, out var name) && name == control.Name;
    }

    public static string ActionBinding(Guid turnId, DesktopTaskWindow window, DesktopTaskControl control,
        DesktopTaskProposal proposal) => Hash(JsonSerializer.Serialize(new { turnId, window, control, proposal }));

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static void RequireEffectContext(string instruction, DesktopTaskSnapshot snapshot, DesktopTaskControl target,
        DesktopTaskProposal proposal)
    {
        if (proposal.Action != DesktopTaskActionKind.Invoke) return;
        var actionLabel = target.Name + " " + target.AutomationId;
        var categories = new (string Action, string Target, string? Content, bool AllowSelection)[]
        {
            ("发送|发布|提交|send|post|publish|submit", "收件人|联系人|接收方|发布到|目的地|recipient|contact|destination|channel", "正文|内容|消息|附件|message|body|content|attachment", false),
            ("付款|支付|购买|下单|订阅|转账|pay|buy|purchase|subscribe|transfer", "收款|商家|商品|订单|recipient|merchant|product|order", "金额|总价|价格|amount|total|price", false),
            ("删除|移除|卸载|清空|格式化|回收站|delete|remove|uninstall|erase|format", "文件|路径|名称|目标|file|path|name|target", null, true),
            ("保存|覆盖|替换|save|overwrite|replace", "文件名|文件路径|保存位置|filename|file name|file path|save location", null, false),
            ("账号|帐户|账户|安全|权限|授权|account|security|permission|authorize", "账号|帐户|账户|account", "权限|设置|permission|setting", false)
        };
        bool HasField(string pattern) => snapshot.Controls.Any(c => c.Value.Length > 0
            && Regex.IsMatch(c.Name + " " + c.ParentName, pattern, RegexOptions.IgnoreCase));
        var unquotedInstruction = Regex.Replace(instruction, """["“][^"”]*["”]|'[^']*'|‘[^’]*’""", string.Empty);
        foreach (var category in categories.Where(p => Regex.IsMatch(actionLabel, p.Action, RegexOptions.IgnoreCase)))
        {
            if (!Regex.IsMatch(unquotedInstruction, category.Action, RegexOptions.IgnoreCase)
                || Regex.IsMatch(unquotedInstruction, "(?:不要|不许|不得|别|勿|无需|不必|不能|禁止|do not|don't|never)[^，,。;；！？!?\\r\\n]{0,80}(?:" + category.Action + ")", RegexOptions.IgnoreCase))
                throw new DesktopTaskException("desktop_task_outside_goal", "这一步的实际后果不在你原来的任务范围内，已停止。");
            var hasTarget = HasField(category.Target) || category.AllowSelection && snapshot.Controls.Any(c =>
                c.State.Contains("selected=True", StringComparison.OrdinalIgnoreCase)
                    && c.ControlType is "ListItem" or "TreeItem" or "DataItem" && c.Name.Length > 0);
            if (snapshot.Truncated || !hasTarget || category.Content is { } content && !HasField(content))
                throw new DesktopTaskException("desktop_task_effect_unidentified", "无法完整确认这一步影响的对象和内容，请你在软件中亲自完成；元枢没有执行该步骤。");
        }
    }

    public static bool HasCompletionEvidence(string instruction, DesktopTaskSnapshot initial,
        DesktopTaskSnapshot latest, DesktopTaskProposal finish, int successfulActions)
    {
        if (successfulActions < 1 || latest.Id == initial.Id || finish.SnapshotId != latest.Id
            || finish.EvidenceText is not { Length: > 0 } evidence) return false;
        var control = latest.Controls.SingleOrDefault(c => c.Id == finish.EvidenceTargetId);
        if (control is null || !(control.Name.Contains(evidence, StringComparison.Ordinal)
            || control.Value.Contains(evidence, StringComparison.Ordinal) || control.State.Contains(evidence, StringComparison.Ordinal))) return false;
        // Static menus/labels do not demonstrate that the requested work happened.
        var original = initial.Controls.FirstOrDefault(c => c.RuntimeId.SequenceEqual(control.RuntimeId));
        if (original is not null
            && !(control.Name != original.Name && control.Name.Contains(evidence, StringComparison.Ordinal))
            && !(control.Value != original.Value && control.Value.Contains(evidence, StringComparison.Ordinal))
            && !(control.State != original.State && control.State.Contains(evidence, StringComparison.Ordinal))) return false;
        var errorPattern = @"失败|错误|未完成|正在|处理中|\b(?:failed|error|pending|loading)\b";
        if (!control.Actions.Contains(DesktopTaskActionKind.SetText)
            && (Regex.IsMatch(control.Value, errorPattern, RegexOptions.IgnoreCase)
                || Regex.IsMatch(control.Name, errorPattern, RegexOptions.IgnoreCase))) return false;
        if (Regex.IsMatch(evidence, @"^(?:enabled|visible|readonly|offscreen|pid|hwnd|metadata)(?:=|$)|失败|错误|未完成|正在|处理中|\b(?:failed|error|pending|loading)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) || evidence is "可用" or "可见") return false;
        // A query still present in its input is not proof of submitting/saving/sending.
        if (Regex.IsMatch(instruction, "提交|保存|发送|发布|搜索|付款|支付|submit|save|send|publish|search|pay", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && (control.Actions.Contains(DesktopTaskActionKind.SetText)
                || !control.Name.Contains(evidence, StringComparison.Ordinal) && !control.Value.Contains(evidence, StringComparison.Ordinal))) return false;
        return true;
    }
}
