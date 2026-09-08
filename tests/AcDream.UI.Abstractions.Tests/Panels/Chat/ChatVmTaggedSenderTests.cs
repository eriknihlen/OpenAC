using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

public sealed class ChatVmTaggedSenderTests
{
    private const uint PlayerGuid = 0x5000000Au;

    private static ChatEntry Speech(
        string sender, string text, uint senderGuid, ChatKind kind = ChatKind.LocalSpeech)
        => new(kind, sender, text, senderGuid, 0u);

    [Fact]
    public void APlayersNameBecomesItsOwnSpan()
    {
        string markup = ChatVM.FormatEntryTagged(Speech("Dww", "hello", PlayerGuid));
        IReadOnlyList<ChatTextSpan> spans = ChatTagMarkup.Parse(markup);

        Assert.Equal("Dww", spans[0].Text);
        Assert.NotNull(spans[0].Tag);
        Assert.True(spans[0].Tag!.Value.TryGetIidString(out uint id, out string name));
        Assert.Equal(PlayerGuid, id);
        Assert.Equal("Dww", name);
    }

    [Fact]
    public void TheTaggedLineShowsExactlyTheSameCharactersAsThePlainOne()
    {
        ChatEntry entry = Speech("Dww", "hello", PlayerGuid);

        string plain = ChatVM.FormatEntry(entry);
        string visible = string.Concat(
            ChatTagMarkup.Parse(ChatVM.FormatEntryTagged(entry)).Select(s => s.Text));

        Assert.Equal(plain, visible);
        Assert.Equal("Dww says, \"hello\"", visible);
    }

    [Theory]
    [InlineData(ChatKind.LocalSpeech)]
    [InlineData(ChatKind.RangedSpeech)]
    [InlineData(ChatKind.Channel)]
    [InlineData(ChatKind.Tell)]
    public void EveryKindThatNamesASpeakerTagsIt(ChatKind kind)
        => Assert.True(ChatVM.ShouldTagSender(Speech("Dww", "hi", PlayerGuid, kind)));

    [Fact]
    public void NonPlayerSendersAreNeverTagged()
    {
        Assert.False(ChatVM.ShouldTagSender(Speech("A Drudge", "hi", 0x80000001u)));
        Assert.False(ChatVM.ShouldTagSender(Speech("A Drudge", "hi", 0x4FFFFFFFu)));

        // Boundaries of the range itself.
        Assert.True(ChatVM.ShouldTagSender(Speech("P", "hi", 0x50000001u)));
        Assert.True(ChatVM.ShouldTagSender(Speech("P", "hi", 0x6FFFFFFFu)));
        Assert.False(ChatVM.ShouldTagSender(Speech("P", "hi", 0x70000000u)));
    }

    [Fact]
    public void OurOwnLinesAndUnnamedSendersAreNotTagged()
    {
        // No guid at all (our own echo), and the substituted "You".
        Assert.False(ChatVM.ShouldTagSender(Speech("Dww", "hi", 0u)));
        Assert.False(ChatVM.ShouldTagSender(Speech("You", "hi", PlayerGuid)));
        Assert.False(ChatVM.ShouldTagSender(Speech(string.Empty, "hi", PlayerGuid)));
    }

    [Fact]
    public void AnUntaggedLineIsFormattedExactlyAsBefore()
    {
        // The overwhelming majority of lines take this path; it must be
        // byte-identical to the pre-CT formatting.
        ChatEntry system = new(ChatKind.System, string.Empty, "Welcome.", 0u, 0u);

        Assert.Equal(ChatVM.FormatEntry(system), ChatVM.FormatEntryTagged(system));
        Assert.Equal("Welcome.", ChatVM.FormatEntryTagged(system));
    }

    [Fact]
    public void ANameContainingMarkupCharactersStillRoundTrips()
    {
        ChatEntry entry = Speech("Od<d", "hi", PlayerGuid);

        string visible = string.Concat(
            ChatTagMarkup.Parse(ChatVM.FormatEntryTagged(entry)).Select(s => s.Text));

        Assert.Equal(ChatVM.FormatEntry(entry), visible);
    }
}
