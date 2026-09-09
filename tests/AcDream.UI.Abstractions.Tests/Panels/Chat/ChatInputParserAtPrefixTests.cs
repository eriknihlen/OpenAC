using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

public sealed class ChatInputParserAtPrefixTests
{
    [Theory]
    [InlineData("@a hi gang",         ChatChannelKind.Allegiance, "hi gang")]
    [InlineData("@guild hi gang",     ChatChannelKind.Allegiance, "hi gang")]
    [InlineData("@p heads up",        ChatChannelKind.Patron,     "heads up")]
    [InlineData("@patron heads up",   ChatChannelKind.Patron,     "heads up")]
    [InlineData("@f buff time",       ChatChannelKind.Fellowship, "buff time")]
    [InlineData("@g general msg",     ChatChannelKind.Fellowship, "general msg")]
    [InlineData("@general general msg", ChatChannelKind.General,  "general msg")]
    public void AtPrefix_KnownChannelVerb_RoutesSameAsSlash(string raw, ChatChannelKind expected, string text)
    {
        var parsed = ChatInputParser.Parse(raw, ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Value.Channel);
        Assert.Null(parsed.Value.TargetName);
        Assert.Equal(text, parsed.Value.Text);
    }

    [Fact]
    public void AtTell_KnownVerb_RoutesAsTell()
    {
        var parsed = ChatInputParser.Parse("@tell Bestie hi", ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal("Bestie", parsed.Value.TargetName);
        Assert.Equal("hi", parsed.Value.Text);
    }

    [Fact]
    public void AtReply_NeedsLastIncomingTell_LikeSlashReply()
    {
        var with = ChatInputParser.Parse("@reply back at you", ChatChannelKind.Say, lastTellSender: "Bestie");
        Assert.NotNull(with);
        Assert.Equal(ChatChannelKind.Tell, with!.Value.Channel);
        Assert.Equal("Bestie", with.Value.TargetName);
        Assert.Equal("back at you", with.Value.Text);

        var without = ChatInputParser.Parse("@reply hi", ChatChannelKind.Say, lastTellSender: null);
        Assert.Null(without);
    }

    [Theory]
    [InlineData("@acehelp")]
    [InlineData("@acecommands")]
    [InlineData("@tele 30 30 30")]
    [InlineData("@die")]
    [InlineData("@version")]
    [InlineData("@loc")]
    [InlineData("@nonsense filler")]
    [InlineData("@allegiance boot Bob")]
    [InlineData("@house open")]
    public void AtPrefix_UnknownVerb_PassesThroughIntactAsDefaultChannel(string raw)
    {
        var parsed = ChatInputParser.Parse(raw, ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Say, parsed!.Value.Channel);
        Assert.Equal(raw, parsed.Value.Text);
    }

    [Fact]
    public void Retell_WithLastOutgoingTarget_RoutesToTell()
    {
        var parsed = ChatInputParser.Parse(
            "/retell once more with feeling",
            ChatChannelKind.Say,
            lastTellSender: null,
            lastOutgoingTellTarget: "Caith");

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal("Caith", parsed.Value.TargetName);
        Assert.Equal("once more with feeling", parsed.Value.Text);
    }

    [Fact]
    public void Retell_AtPrefix_AlsoWorks()
    {
        var parsed = ChatInputParser.Parse(
            "@retell hi again",
            ChatChannelKind.Say,
            lastTellSender: null,
            lastOutgoingTellTarget: "Caith");

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal("Caith", parsed.Value.TargetName);
        Assert.Equal("hi again", parsed.Value.Text);
    }

    [Fact]
    public void Retell_NoPriorOutgoingTell_ReturnsNull()
    {
        var parsed = ChatInputParser.Parse(
            "/retell hello",
            ChatChannelKind.Say,
            lastTellSender: "Bestie",   // incoming is irrelevant for retell
            lastOutgoingTellTarget: null);

        Assert.Null(parsed);
    }

    [Fact]
    public void Retell_BareVerb_ReturnsNull()
    {
        var parsed = ChatInputParser.Parse(
            "/retell",
            ChatChannelKind.Say,
            lastTellSender: null,
            lastOutgoingTellTarget: "Caith");

        Assert.Null(parsed);
    }
}
