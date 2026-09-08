using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class ChatCommandTargetStateTests
{
    [Fact]
    public void TracksReplyAndRetellTargetsFromCommittedTranscript()
    {
        var chat = new ChatLog();
        using var targets = new ChatCommandTargetState(chat);

        chat.OnTellReceived("Bestie", "incoming", 0x50000001u, logTextType: 0x03u);
        chat.OnSelfSent(ChatKind.Tell, "outgoing", logTextType: 0x04u, targetOrChannel: "Caith");

        Assert.Equal("Bestie", targets.LastIncomingTellSender);
        Assert.Equal("Caith", targets.LastOutgoingTellTarget);
    }

    [Fact]
    public void TracksIndependentMonarchAndPatronReplyTargetsFromLegacyBroadcasts()
    {
        var chat = new ChatLog();
        using var targets = new ChatCommandTargetState(chat);

        chat.OnChannelBroadcast(0x4000u, "Monarch", "orders");
        chat.OnChannelBroadcast(0x2000u, "Patron", "hello");
        chat.OnChannelBroadcast(0x4000u, "New Monarch", "new orders");

        Assert.Equal("New Monarch", targets.LastMonarchSender);
        Assert.Equal("Patron", targets.LastPatronSender);
    }

    [Fact]
    public void ResetSessionForgetsTargetsButPreservesTranscript()
    {
        var chat = new ChatLog();
        using var targets = new ChatCommandTargetState(chat);
        chat.OnTellReceived("Bestie", "incoming", 0x50000001u, logTextType: 0x03u);
        chat.OnSelfSent(ChatKind.Tell, "outgoing", logTextType: 0x04u, targetOrChannel: "Caith");

        targets.ResetSession();

        Assert.Null(targets.LastIncomingTellSender);
        Assert.Null(targets.LastOutgoingTellTarget);
        Assert.Null(targets.LastMonarchSender);
        Assert.Null(targets.LastPatronSender);
        Assert.Equal(2, chat.Count);
    }

    [Fact]
    public void DisposeDetachesAndIsIdempotent()
    {
        var chat = new ChatLog();
        var targets = new ChatCommandTargetState(chat);

        targets.Dispose();
        targets.Dispose();
        chat.OnTellReceived("After", "ignored", 0x50000002u, logTextType: 0x03u);

        Assert.Null(targets.LastIncomingTellSender);
        Assert.Null(targets.LastOutgoingTellTarget);
    }
}
