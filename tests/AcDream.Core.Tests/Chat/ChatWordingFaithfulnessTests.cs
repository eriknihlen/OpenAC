using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

/// <summary>
/// Three lines the chat window used to word or class differently from the
/// original client: a tell the character sends itself, a ranged line, and a
/// numbered channel's line that comes back without a speaker. Each expected
/// value is the original client's own format string or text class; the
/// research note on chat wording quotes the branches.
///
/// Mutation checks (2026-09-25), run: wording a ranged line "shouts",
/// classing a speakerless channel line as heard, and letting a tell to self
/// set the reply target each turned the matching tests here red and left
/// the rest green.
///
/// Emotes and the reply target joined later: emotes print the name, a
/// space unless the action starts with an apostrophe, then the action; only
/// a sender in the player id range becomes the reply target. Mutation check
/// (2026-09-25), run: the old "* Name text" wording and the old "any sender"
/// reply rule turned the five new tests red.
/// </summary>
public sealed class ChatWordingFaithfulnessTests
{
    private const uint Self = 0x5000_000Au;
    private const uint Other = 0x5000_0077u;

    [Fact]
    public void ATellWhoseSpeakerIsItsListenerReadsAsAThought()
    {
        var log = new ChatLog();
        log.OnTellReceived("Acdream", "remember the key", Self, 0x03u, targetGuid: Self);

        ChatEntry entry = log.Snapshot()[0];
        Assert.True(entry.IsTellToSelf);
        Assert.Equal("You think, \"remember the key\"", ChatLineWording.FormatTagged(entry));
        Assert.False(ChatLineWording.ShouldTagSender(entry));
    }

    [Fact]
    public void ATellFromSomeoneElseStillTellsYou()
    {
        var log = new ChatLog();
        log.OnTellReceived("Bob", "hi", Other, 0x03u, targetGuid: Self);

        ChatEntry entry = log.Snapshot()[0];
        Assert.False(entry.IsTellToSelf);
        Assert.Equal(
            $"<Tell:IIDString:{Other}:Bob>Bob<\\Tell> tells you, \"hi\"",
            ChatLineWording.FormatTagged(entry));
    }

    /// <summary>A tell the character sent itself names nobody to reply to.</summary>
    [Fact]
    public void ATellToSelfIsNotSomeoneToReplyTo()
    {
        var chat = new ChatLog();
        using var targets = new ChatCommandTargetState(chat);

        chat.OnTellReceived("Bob", "hi", Other, 0x03u, targetGuid: Self);
        chat.OnTellReceived("Acdream", "note to self", Self, 0x03u, targetGuid: Self);

        Assert.Equal("Bob", targets.LastIncomingTellSender);
    }

    [Fact]
    public void ARangedLineIsSpeechUnderTheNameItArrivedWith()
    {
        var log = new ChatLog();
        log.OnLocalSpeech("Guard", "Halt!", 0x8000_1234u, isRanged: true, logTextType: 0x02u);

        Assert.Equal("Guard says, \"Halt!\"", ChatLineWording.FormatTagged(log.Snapshot()[0]));
    }

    /// <summary>
    /// The fellowship broadcast with no speaker is the sender's own line
    /// handed back: the sender's text class, and the words as they were
    /// written.
    /// </summary>
    [Fact]
    public void ASpeakerlessFellowshipBroadcastTakesTheFellowshipTextClass()
    {
        var log = new ChatLog();
        log.OnChannelBroadcast(0x0400_0000u, "", "Bob has joined the fellowship.");

        ChatEntry entry = log.Snapshot()[0];
        Assert.Equal((uint)RetailLogTextType.Fellowship, entry.LogTextType);
        Assert.Equal("Bob has joined the fellowship.", ChatLineWording.FormatTagged(entry));
    }

    [Fact]
    public void AFellowshipBroadcastWithASpeakerKeepsTheChannelTextClass()
    {
        var log = new ChatLog();
        log.OnChannelBroadcast(0x0400_0000u, "Bob", "hi");

        Assert.Equal((uint)RetailLogTextType.Channel, log.Snapshot()[0].LogTextType);
    }

    /// <summary>
    /// The branch is picked by the missing speaker on every numbered channel,
    /// not only the fellowship broadcast: a patron line with no speaker is the
    /// sender's own and takes the sender's text class.
    /// </summary>
    [Fact]
    public void ASpeakerlessPatronLineTakesTheSendersTextClass()
    {
        var log = new ChatLog();
        log.OnChannelBroadcast(0x0000_1000u, "", "thanks");

        ChatEntry entry = log.Snapshot()[0];
        Assert.Equal((uint)RetailLogTextType.SocialSend, entry.LogTextType);
        Assert.Equal("You say to your Vassals, \"thanks\"", ChatLineWording.FormatTagged(entry));
    }
    /// <summary>
    /// An emote reads as the name followed by the action, with no marker in
    /// front of it.
    /// </summary>
    [Fact]
    public void AnEmoteIsTheNameThenTheAction()
    {
        var log = new ChatLog();
        log.OnEmote("Bob", "waves.", Other);
        log.OnSoulEmote("Bob", "bows deeply.", Other);

        Assert.Equal("Bob waves.", ChatLineWording.FormatTagged(log.Snapshot()[0]));
        Assert.Equal("Bob bows deeply.", ChatLineWording.FormatTagged(log.Snapshot()[1]));
    }

    /// <summary>
    /// An action that starts with an apostrophe is joined straight onto the
    /// name, so a possessive reads as one word.
    /// </summary>
    [Fact]
    public void AnEmoteStartingWithAnApostropheJoinsTheName()
    {
        var log = new ChatLog();
        log.OnEmote("Bob", "'s eyes narrow.", Other);

        Assert.Equal("Bob's eyes narrow.", ChatLineWording.Format(log.Snapshot()[0]));
    }

    /// <summary>
    /// Only a player becomes the one to answer. A tell from a creature is
    /// shown, but the reply target stays on the last player who told you
    /// something.
    /// </summary>
    [Fact]
    public void ATellFromACreatureIsNotSomeoneToReplyTo()
    {
        var chat = new ChatLog();
        using var targets = new ChatCommandTargetState(chat);

        chat.OnTellReceived("Bob", "hi", Other, 0x03u, targetGuid: Self);
        chat.OnTellReceived("Town Crier", "news!", 0x8000_1234u, 0x03u, targetGuid: Self);

        Assert.Equal(2, chat.Count);
        Assert.Equal("Bob", targets.LastIncomingTellSender);
    }

    /// <summary>
    /// Both ends of the player id range count as players.
    /// </summary>
    [Theory]
    [InlineData(0x5000_0001u)]
    [InlineData(0x6FFF_FFFFu)]
    public void TheWholePlayerRangeIsSomeoneToReplyTo(uint sender)
    {
        var chat = new ChatLog();
        using var targets = new ChatCommandTargetState(chat);

        chat.OnTellReceived("Edge", "hi", sender, 0x03u, targetGuid: Self);

        Assert.Equal("Edge", targets.LastIncomingTellSender);
    }

    /// <summary>Just outside the player id range is not a player.</summary>
    [Theory]
    [InlineData(0x5000_0000u)]
    [InlineData(0x7000_0000u)]
    public void JustOutsideThePlayerRangeIsNotSomeoneToReplyTo(uint sender)
    {
        var chat = new ChatLog();
        using var targets = new ChatCommandTargetState(chat);

        chat.OnTellReceived("Edge", "hi", sender, 0x03u, targetGuid: Self);

        Assert.Null(targets.LastIncomingTellSender);
    }
}
