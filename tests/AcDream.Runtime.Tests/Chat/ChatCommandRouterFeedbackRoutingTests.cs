using AcDream.Core.Chat;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Chat;

public sealed class ChatCommandRouterFeedbackRoutingTests
{
    [Fact]
    public void DegeneratePrefix_UnknownCommandRefusal_RoutesToChatLog_NeverSpewBox()
    {
        using var communication = new RuntimeCommunicationState();
        var feedback = new RuntimeChatCommandFeedback(communication);

        SubmitOutcome outcome = ChatCommandRouter.Submit(
            "/", feedback, NullCommandBus.Instance, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.UnknownCommand, outcome);
        ChatEntry entry = Assert.Single(communication.Chat.Snapshot());
        Assert.Contains("Unknown command:", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);

        communication.SpewBox.Tick(0d);
        Assert.Equal(0, communication.SpewBox.Count);
    }

    [Fact]
    public void HelpUnresolvedVerb_UnknownCommandText_RoutesToChatLog_NeverSpewBox()
    {
        using var communication = new RuntimeCommunicationState();
        var feedback = new RuntimeChatCommandFeedback(communication);

        SubmitOutcome outcome = ChatCommandRouter.Submit(
            "/help nonsenseverb", feedback, NullCommandBus.Instance, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        ChatEntry entry = Assert.Single(communication.Chat.Snapshot());
        Assert.Equal(RetailCommandHelpTable.UnknownCommand, entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);

        communication.SpewBox.Tick(0d);
        Assert.Equal(0, communication.SpewBox.Count);
    }

    [Fact]
    public void HelpConfirmedNullVerb_UnknownCommandText_RoutesToChatLog_NeverSpewBox()
    {
        using var communication = new RuntimeCommunicationState();
        var feedback = new RuntimeChatCommandFeedback(communication);

        SubmitOutcome outcome = ChatCommandRouter.Submit(
            "/help index", feedback, NullCommandBus.Instance, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        ChatEntry entry = Assert.Single(communication.Chat.Snapshot());
        Assert.Equal(RetailCommandHelpTable.UnknownCommand, entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);

        communication.SpewBox.Tick(0d);
        Assert.Equal(0, communication.SpewBox.Count);
    }

    [Fact]
    public void RealCommandBadArguments_StillRoutesToSpewBox_NeverChatLog()
    {
        using var communication = new RuntimeCommunicationState();
        var feedback = new RuntimeChatCommandFeedback(communication);

        SubmitOutcome outcome = ChatCommandRouter.Submit(
            "/ls now", feedback, NullCommandBus.Instance, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(communication.Chat.Snapshot());

        communication.SpewBox.Tick(0d);
        Assert.Equal(1, communication.SpewBox.Count);
        Assert.Equal(
            "Please see @help lifestone for more information on how to use this command.",
            communication.SpewBox.Snapshot()[0].Text);
    }
}
