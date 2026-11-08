using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

/// <summary>Transient display feedback only; never a Session, Turn, history, or authorization.</summary>
public sealed class LocalVoiceReplyPresentation
{
    private Feedback? _feedback;

    public void Show(string text, SessionSnapshotDto? snapshot, long? microphoneGeneration, long inputVersion)
    {
        _feedback = microphoneGeneration is null ? null
            : new(text, Binding.From(snapshot, microphoneGeneration.Value, inputVersion));
    }

    public string? Get(SessionSnapshotDto? snapshot, long? microphoneGeneration, long inputVersion,
        bool blocked = false)
    {
        if (_feedback is not { } feedback) return null;
        if (blocked || microphoneGeneration is null
            || feedback.Binding != Binding.From(snapshot, microphoneGeneration.Value, inputVersion))
        {
            Clear();
            return null;
        }
        return feedback.Text;
    }

    public void Clear() => _feedback = null;

    private sealed record Feedback(string Text, Binding Binding);

    private sealed record Binding(long MicrophoneGeneration, long InputVersion, string? CoordinatorId,
        DateTimeOffset? CoordinatorStartedAt, Guid? SessionId, long? ChangeVersion, Guid? TurnId,
        int? TurnSequence, string? TurnPhase, DateTimeOffset? TurnUpdatedAt)
    {
        public static Binding From(SessionSnapshotDto? snapshot, long microphoneGeneration, long inputVersion)
        {
            var turn = snapshot?.ForegroundTurn ?? snapshot?.Turns.MaxBy(item => item.SequenceNumber);
            return new(microphoneGeneration, inputVersion, snapshot?.CoordinatorInstanceId,
                snapshot?.CoordinatorStartedAtUtc, snapshot?.SessionId, snapshot?.ChangeVersion,
                turn?.Id, turn?.SequenceNumber, turn?.Phase, turn?.UpdatedAtUtc);
        }
    }
}
