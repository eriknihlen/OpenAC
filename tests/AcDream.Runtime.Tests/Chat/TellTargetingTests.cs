using AcDream.Core.Chat;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Chat;

/// <summary>
/// Covers the two tell-addressing paths: a typed name, and an addressee picked
/// in the world. Both must leave a target behind for the retell verb.
/// </summary>
public sealed class TellTargetingTests
{
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Communication = new RuntimeCommunicationState();
            Character = new RuntimeCharacterState();
            Route = new LiveChatCommandRoute(new LiveChatCommandBindings(
                _ => { },
                Communication,
                Communication.Chat,
                Communication.TurbineChat,
                Character,
                () => 0x50000001u,
                text => Sent.Add($"talk:{text}"),
                (target, text) => Sent.Add($"tell:{target}:{text}"),
                (guid, text) => Sent.Add($"talkdirect:{guid:X8}:{text}"),
                (channel, text) => Sent.Add($"channel:{channel:X8}:{text}"),
                (_, _, _, _, text, _) => Sent.Add($"turbine:{text}")));
            Route.Activate();
            Feedback = new RuntimeChatCommandFeedback(Communication);
        }

        public RuntimeCommunicationState Communication { get; }
        public RuntimeCharacterState Character { get; }
        public LiveChatCommandRoute Route { get; }
        public RuntimeChatCommandFeedback Feedback { get; }
        public List<string> Sent { get; } = [];

        public void Dispose()
        {
            Route.Dispose();
            Character.Dispose();
            Communication.Dispose();
        }
    }

    [Fact]
    public void TypedTell_RecordsTheAddressee_SoRetellReachesTheSamePerson_Issue39()
    {
        using var f = new Fixture();

        Assert.Equal(
            SubmitOutcome.Sent,
            ChatCommandRouter.Submit(
                "/tell Bob, hello", f.Feedback, f.Route, ChatChannelKind.Say));

        Assert.Equal("Bob", f.Feedback.LastOutgoingTellTarget);

        Assert.Equal(
            SubmitOutcome.Sent,
            ChatCommandRouter.Submit(
                "/rt hello again", f.Feedback, f.Route, ChatChannelKind.Say));

        Assert.Equal(
            ["tell:Bob:hello", "tell:Bob:hello again"],
            f.Sent);
    }

    [Fact]
    public void TypedTell_LeavesNoTranscriptLine_TheServerEchoesOurOwnTell_Issue39()
    {
        using var f = new Fixture();

        ChatCommandRouter.Submit(
            "/tell Bob, hello", f.Feedback, f.Route, ChatChannelKind.Say);

        Assert.Empty(f.Communication.Chat.Snapshot());
    }

    [Fact]
    public void Retell_WithNoEarlierTell_RefusesAndSendsNothing_Issue39()
    {
        using var f = new Fixture();

        Assert.Equal(
            SubmitOutcome.ClientHandled,
            ChatCommandRouter.Submit(
                "/rt hello", f.Feedback, f.Route, ChatChannelKind.Say));

        Assert.Empty(f.Sent);
        Assert.Empty(f.Communication.Chat.Snapshot());
        f.Communication.SpewBox.Tick(0d);
        Assert.Equal(1, f.Communication.SpewBox.Count);
        Assert.Equal(
            "You must first provide a name using @tell",
            f.Communication.SpewBox.Snapshot()[0].Text);
    }

    [Fact]
    public void TellToPickedTarget_AimsAtTheObject_NotAtItsName_Issue50()
    {
        using var f = new Fixture();

        Assert.Equal(
            SubmitOutcome.Sent,
            ChatCommandRouter.Submit(
                "I would like to trade",
                f.Feedback,
                f.Route,
                ChatChannelKind.Tell,
                defaultTellTarget: "Aun Tanua",
                defaultTellTargetGuid: 0x8000ABCDu));

        Assert.Equal(["talkdirect:8000ABCD:I would like to trade"], f.Sent);
        Assert.Equal("Aun Tanua", f.Feedback.LastOutgoingTellTarget);
    }

    [Fact]
    public void TellToPickedTarget_WithoutAnId_FallsBackToTheNameLookup_Issue50()
    {
        using var f = new Fixture();

        Assert.Equal(
            SubmitOutcome.Sent,
            ChatCommandRouter.Submit(
                "hi",
                f.Feedback,
                f.Route,
                ChatChannelKind.Tell,
                defaultTellTarget: "Bob"));

        Assert.Equal(["tell:Bob:hi"], f.Sent);
    }

    [Fact]
    public void TypedTell_IgnoresThePickedTarget_AndStillGoesByName_Issue50()
    {
        using var f = new Fixture();

        ChatCommandRouter.Submit(
            "/tell Bob, hello",
            f.Feedback,
            f.Route,
            ChatChannelKind.Tell,
            defaultTellTarget: "Aun Tanua",
            defaultTellTargetGuid: 0x8000ABCDu);

        Assert.Equal(["tell:Bob:hello"], f.Sent);
    }

    [Fact]
    public void RetellAfterAPickedTarget_KeepsAimingAtTheName_Issue50()
    {
        using var f = new Fixture();

        ChatCommandRouter.Submit(
            "hello",
            f.Feedback,
            f.Route,
            ChatChannelKind.Tell,
            defaultTellTarget: "Aun Tanua",
            defaultTellTargetGuid: 0x8000ABCDu);
        ChatCommandRouter.Submit(
            "/rt again", f.Feedback, f.Route, ChatChannelKind.Say);

        Assert.Equal(
            ["talkdirect:8000ABCD:hello", "tell:Aun Tanua:again"],
            f.Sent);
    }

    [Fact]
    public void AnInactiveRoute_RecordsNoAddressee_Issue39()
    {
        using var f = new Fixture();
        f.Route.Dispose();

        ChatCommandRouter.Submit(
            "/tell Bob, hello", f.Feedback, f.Route, ChatChannelKind.Say);

        Assert.Empty(f.Sent);
        Assert.Null(f.Feedback.LastOutgoingTellTarget);
    }
}
