using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class LocalVoiceReplyPresentationTests
{
    [Fact]
    public void LocalAcknowledgementSurvivesUnchangedRefreshWithoutAddingHistory()
    {
        var snapshot = Snapshot();
        var feedback = new LocalVoiceReplyPresentation();
        feedback.Show("我在。", snapshot, 3, 8);
        Assert.Equal("我在。", feedback.Get(snapshot with { }, 3, 8));
        Assert.Equal("旧回答", snapshot.Turns.Single().ResultSummary);
        Assert.Empty(snapshot.Messages);
        feedback.Clear();
        Assert.Null(feedback.Get(snapshot, 3, 8));
    }

    [Theory]
    [InlineData("microphone")]
    [InlineData("stopped")]
    [InlineData("input")]
    [InlineData("host")]
    [InlineData("host-start")]
    [InlineData("session")]
    [InlineData("version")]
    [InlineData("turn")]
    [InlineData("turn-phase")]
    [InlineData("turn-time")]
    [InlineData("blocked")]
    public void ContextChangePermanentlyRemovesTheOldLocalReply(string change)
    {
        var original = Snapshot();
        var feedback = new LocalVoiceReplyPresentation();
        feedback.Show("我在。", original, 3, 8);
        var changed = change switch
        {
            "host" => original with { CoordinatorInstanceId = "restarted" },
            "host-start" => original with { CoordinatorStartedAtUtc = original.CoordinatorStartedAtUtc.AddSeconds(1) },
            "session" => original with { SessionId = Guid.NewGuid() },
            "version" => original with { ChangeVersion = original.ChangeVersion + 1 },
            "turn" => original with { Turns = [original.Turns[0] with { Id = Guid.NewGuid() }] },
            "turn-phase" => original with { Turns = [original.Turns[0] with { Phase = "WaitingForConfirmation" }] },
            "turn-time" => original with { Turns = [original.Turns[0] with { UpdatedAtUtc = original.Turns[0].UpdatedAtUtc.AddSeconds(1) }] },
            _ => original
        };
        long? microphone = change == "stopped" ? null : change == "microphone" ? 4 : 3;
        Assert.Null(feedback.Get(changed, microphone, change == "input" ? 9 : 8, change == "blocked"));
        Assert.Null(feedback.Get(original, 3, 8));
    }

    [Fact]
    public void AFirstCallDoesNotRequireOrCreateASessionAndCannotSurviveOneAppearing()
    {
        var feedback = new LocalVoiceReplyPresentation();
        feedback.Show("我在。", null, 1, 0);
        Assert.Equal("我在。", feedback.Get(null, 1, 0));
        Assert.Null(feedback.Get(Snapshot(), 1, 0));
        Assert.Null(feedback.Get(null, 1, 0));
        feedback.Show("我在。", null, null, 0);
        Assert.Null(feedback.Get(null, 1, 0));
    }

    private static SessionSnapshotDto Snapshot()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var turn = new UnifiedSessionTurnDto(Guid.NewGuid(), 1, "测试问题", "Voice", "Conversation",
            "Completed", "None", "Conversation", null, null, null, null, null, null, false, false,
            "旧回答", null, now, now, now);
        return new(3, "host", now, Guid.NewGuid(), Guid.NewGuid(), "测试话题", "Completed",
            null, null, null, [], [turn], []);
    }
}
