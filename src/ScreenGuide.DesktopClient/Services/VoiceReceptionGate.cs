namespace ScreenGuide.DesktopClient.Services;

public enum VoiceReceptionMode { Direct, Transitioning, Standby }
public enum VoiceReceptionDecision { Ignore, Submit, Rest, Wake, Call, Stop }

/// <summary>Local microphone reception only; it neither creates nor owns a Session or Turn.</summary>
public sealed class VoiceReceptionGate
{
    private long? _microphoneGeneration;
    private long _recognitionGeneration;
    private long _transition;

    public VoiceReceptionMode Mode { get; private set; } = VoiceReceptionMode.Direct;
    public long Transition => _transition;

    public void SynchronizeMicrophone(long? generation, long recognitionGeneration)
    {
        if (_microphoneGeneration == generation) return;
        _microphoneGeneration = generation;
        _recognitionGeneration = recognitionGeneration;
        _transition++;
        Mode = VoiceReceptionMode.Direct;
    }

    public bool AcceptsPartial(long? microphoneGeneration, long recognitionGeneration) =>
        Mode == VoiceReceptionMode.Direct && Matches(microphoneGeneration, recognitionGeneration);

    public VoiceReceptionDecision Classify(string text, long? microphoneGeneration, long recognitionGeneration)
    {
        if (!Matches(microphoneGeneration, recognitionGeneration) || Mode == VoiceReceptionMode.Transitioning)
            return VoiceReceptionDecision.Ignore;
        var phrase = string.Concat(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));
        if (Mode == VoiceReceptionMode.Standby)
            return CanonicalWakePhrase(phrase) is not null ? VoiceReceptionDecision.Wake : VoiceReceptionDecision.Ignore;
        if (IsStopPhrase(phrase)) return VoiceReceptionDecision.Stop;
        if (CanonicalWakePhrase(phrase) is not null) return VoiceReceptionDecision.Call;
        return phrase is "退下" or "先休息" ? VoiceReceptionDecision.Rest : VoiceReceptionDecision.Submit;
    }

    public static bool IsStopPhrase(string text)
    {
        var phrase = string.Concat(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));
        return phrase is "停" or "停一下" or "停停" or "停停停" or "停止" or "停止说话";
    }

    public static bool IsCallPrefix(string text)
    {
        var phrase = string.Concat(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));
        return phrase.Length > 0 && CallPhrases.Any(candidate => candidate.StartsWith(phrase, StringComparison.Ordinal));
    }

    private static readonly string[] CallPhrases =
    [
        "元枢", "元书", "原书", "袁叔", "袁书",
        "你好元枢", "你好元书", "你好原书", "你好袁叔", "你好袁书"
    ];

    public static string? CanonicalWakePhrase(string text)
    {
        var phrase = string.Concat(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));
        // The local ASR vocabulary lacks 枢. Only these complete, observed
        // homophone spellings may wake standby or acknowledge a complete call;
        // never rewrite or split an ordinary question or action.
        return phrase switch
        {
            "元枢" or "元书" or "原书" or "袁叔" or "袁书" => "元枢",
            "你好元枢" or "你好元书" or "你好原书" or "你好袁叔" or "你好袁书" => "你好元枢",
            _ => null
        };
    }

    public long BeginTransition()
    {
        Mode = VoiceReceptionMode.Transitioning;
        return ++_transition;
    }

    public bool OwnsTransition(long transition, long? microphoneGeneration) =>
        _microphoneGeneration is not null && _microphoneGeneration == microphoneGeneration
        && transition == _transition && Mode == VoiceReceptionMode.Transitioning;

    public bool CompleteTransition(long transition, long? microphoneGeneration, long recognitionGeneration, VoiceReceptionMode mode)
    {
        if (mode == VoiceReceptionMode.Transitioning) throw new ArgumentException("需要稳定的接收模式。", nameof(mode));
        if (!OwnsTransition(transition, microphoneGeneration)) return false;
        _recognitionGeneration = recognitionGeneration;
        Mode = mode;
        return true;
    }

    private bool Matches(long? microphoneGeneration, long recognitionGeneration) =>
        _microphoneGeneration is not null && microphoneGeneration == _microphoneGeneration
        && recognitionGeneration == _recognitionGeneration;
}
