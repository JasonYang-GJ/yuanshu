using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed record VisualSearchVoiceConsentBinding(string CoordinatorId, DateTimeOffset CoordinatorStartedAt,
    Guid SessionId, Guid TurnId, Guid ConsentId, string Purpose, long WindowHandle, string WindowTitle)
{
    public static VisualSearchVoiceConsentBinding? From(SessionSnapshotDto? snapshot)
    {
        if (snapshot?.ForegroundTurn is not { Phase: "WaitingForWindowConsent", CancellationRequested: false,
                WindowConsent: { } consent } turn
            || !((turn.IntentKind is "ApplicationSearch" or "SearchForeground" && consent.Purpose is "Search" or "ResultRead")
                || turn.IntentKind == "DesktopTask" && consent.Purpose is "TaskObservation" or "TaskAction")
            || consent.ConsentId == Guid.Empty) return null;
        return new(snapshot.CoordinatorInstanceId, snapshot.CoordinatorStartedAtUtc, snapshot.SessionId, turn.Id,
            consent.ConsentId, consent.Purpose, consent.WindowHandle, consent.WindowTitle);
    }
}

public sealed record VisualSearchVoiceConsentResponse(VisualSearchVoiceConsentBinding Binding, bool Granted);

/// <summary>Ephemeral eligibility for one visibly presented consent, never Session or task state.</summary>
public sealed class VisualSearchVoiceConsentGate
{
    public const string ManualConfirmationPrompt = "当前许可内容未完整展示，语音确认已停用。请打开操作面板并点击“查看确认”，在管理窗口核对完整内容后手动确认。也可停止任务。";

    private VisualSearchVoiceConsentBinding? _binding;
    private long? _microphoneGeneration;
    private long? _recognitionGeneration;
    private bool _consumed;
    private bool _recognitionSuspended;

    public VisualSearchVoiceConsentBinding? Begin(SessionSnapshotDto? snapshot, long? microphoneGeneration)
    {
        var next = VisualSearchVoiceConsentBinding.From(snapshot);
        if (next is null || microphoneGeneration is null) { Clear(); return null; }
        if (_binding == next && _microphoneGeneration == microphoneGeneration)
        {
            // Speech may require a fresh visible presentation and audio drain, but
            // may never erase the consumed/revoked tombstone for this nonce.
            if (_consumed || !_recognitionSuspended) return null;
            _recognitionSuspended = false;
            return next;
        }
        _binding = next;
        _microphoneGeneration = microphoneGeneration;
        _recognitionGeneration = null;
        _consumed = false;
        _recognitionSuspended = false;
        return next;
    }

    public bool IsCurrent(VisualSearchVoiceConsentBinding binding, SessionSnapshotDto? snapshot, long? microphoneGeneration) =>
        _binding == binding && binding == VisualSearchVoiceConsentBinding.From(snapshot)
        && microphoneGeneration is not null && _microphoneGeneration == microphoneGeneration;

    public bool Arm(VisualSearchVoiceConsentBinding binding, SessionSnapshotDto? snapshot,
        long? microphoneGeneration, long recognitionGeneration, bool visiblyPresented)
    {
        if (!visiblyPresented || _consumed || _recognitionSuspended
            || !IsCurrent(binding, snapshot, microphoneGeneration)) return false;
        _recognitionGeneration = recognitionGeneration;
        return true;
    }

    public VisualSearchVoiceConsentResponse? TryConsume(string text, SessionSnapshotDto? snapshot,
        long? microphoneGeneration, long recognitionGeneration, bool visible)
    {
        if (!visible || _consumed || _recognitionSuspended || _binding is not { } binding
            || _recognitionGeneration != recognitionGeneration
            || !IsCurrent(binding, snapshot, microphoneGeneration)
            || !TryParse(text, out var purpose, out var granted) || purpose != binding.Purpose) return null;
        _consumed = true;
        return new(binding, granted);
    }

    public void Clear()
    {
        _binding = null;
        _microphoneGeneration = null;
        _recognitionGeneration = null;
        _consumed = false;
        _recognitionSuspended = false;
    }

    public void SuspendRecognition()
    {
        _recognitionGeneration = null;
        _recognitionSuspended = true;
    }

    public void Revoke(SessionSnapshotDto? snapshot, long? microphoneGeneration)
    {
        // Keep a tombstone for the visible nonce in this microphone run. A stale
        // waiting snapshot rendered after Stop/replacement must not re-arm it.
        if (VisualSearchVoiceConsentBinding.From(snapshot) is { } binding && microphoneGeneration is not null)
        {
            _binding = binding;
            _microphoneGeneration = microphoneGeneration;
        }
        _recognitionGeneration = null;
        _consumed = true;
        _recognitionSuspended = false;
    }

    public static string Prompt(string purpose) => purpose switch
    {
        "TaskObservation" => "允许请说“允许这次任务”；拒绝请说“拒绝这次任务”。",
        "TaskAction" => "仅确认所显示步骤请说“确认这一步”；拒绝请说“拒绝这一步”。",
        "ResultRead" => "请勿切换或点击其他窗口。允许请说“允许这次读取”；拒绝请说“拒绝这次读取”。",
        _ => "允许请说“允许这次搜索”；拒绝请说“拒绝这次搜索”。"
    };

    public static bool ShouldDeferPartial(string text, SessionSnapshotDto? snapshot) =>
        VisualSearchVoiceConsentBinding.From(snapshot) is not null && !IsImmediateInterruption(text);

    public static bool IsImmediateInterruption(string text)
    {
        var value = Normalize(text);
        for (var count = 0; count < 3; count++)
        {
            var prefix = PolitePrefixes.FirstOrDefault(item => value.StartsWith(item, StringComparison.Ordinal));
            if (prefix is null) break;
            value = value[prefix.Length..];
        }
        return value is "停" or "停一下" or "等一下" or "不对" or "不是" or "你说错了" or "退下" or "先休息"
            || value.StartsWith("停止", StringComparison.Ordinal)
            || value.StartsWith("取消", StringComparison.Ordinal)
            || value.StartsWith("暂停", StringComparison.Ordinal);
    }

    // This is a routing hint, never an authorization parser. Uncertain permission
    // replies stay local and ask for an exact phrase instead of replacing the Turn.
    public static bool IsUnclearConsentReply(string text)
    {
        var value = Normalize(text);
        if (value.Length == 0 || value.Length > 18 || IsImmediateInterruption(value)) return false;
        if (IsConsentPrefix(value)) return true;
        return Phrases.Any(item =>
        {
            if (!EditDistanceAtMostTwo(value, item.Phrase)) return false;
            // Only a close Search permission may use the final noun “搜索”. ASR can
            // misrecognize its modifier; that must ask for a repeat, never grant or chat.
            // An earlier “搜索” or any additional action still belongs to new input.
            var actionText = item.Purpose == "Search" && value.EndsWith("搜索", StringComparison.Ordinal)
                ? value[..^2] : value;
            return !NewActionWords.Any(word => actionText.Contains(word, StringComparison.Ordinal));
        });
    }

    public static bool IsConsentPrefix(string text)
    {
        var value = Normalize(text);
        return value.Length > 0 && Phrases.Any(item => item.Phrase.StartsWith(value, StringComparison.Ordinal));
    }

    public static bool TryParse(string text, out string? purpose, out bool granted)
    {
        var value = Normalize(text);
        foreach (var item in Phrases)
            if (item.Phrase == value) { purpose = item.Purpose; granted = item.Granted; return true; }
        purpose = null;
        granted = false;
        return false;
    }

    private static readonly (string Phrase, string Purpose, bool Granted)[] Phrases =
    [
        ("允许这次任务", "TaskObservation", true), ("拒绝这次任务", "TaskObservation", false),
        ("确认这一步", "TaskAction", true), ("拒绝这一步", "TaskAction", false),
        ("允许这次搜索", "Search", true), ("拒绝这次搜索", "Search", false),
        ("允许这次视觉搜索", "Search", true), ("拒绝这次视觉搜索", "Search", false),
        ("允许这次读取", "ResultRead", true), ("拒绝这次读取", "ResultRead", false),
        ("允许读取这个临时窗口", "ResultRead", true), ("拒绝读取这个临时窗口", "ResultRead", false)
    ];

    private static readonly string[] NewActionWords =
        ["打开", "关闭", "查找", "发送", "删除", "输入", "点击", "播放", "复制", "粘贴", "切换", "搜索"];
    private static readonly string[] PolitePrefixes = ["请你", "请", "帮我", "麻烦"];

    private static bool EditDistanceAtMostTwo(string value, string phrase)
    {
        if (Math.Abs(value.Length - phrase.Length) > 2) return false;
        Span<int> previous = stackalloc int[phrase.Length + 1];
        Span<int> current = stackalloc int[phrase.Length + 1];
        for (var column = 0; column <= phrase.Length; column++) previous[column] = column;
        for (var row = 1; row <= value.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= phrase.Length; column++)
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + (value[row - 1] == phrase[column - 1] ? 0 : 1));
            current.CopyTo(previous);
        }
        return previous[phrase.Length] <= 2;
    }

    private static string Normalize(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));
}
