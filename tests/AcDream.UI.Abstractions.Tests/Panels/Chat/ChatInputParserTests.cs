using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

public sealed class ChatInputParserTests
{

    [Fact]
    public void NoPrefix_UsesDefaultChannelLiteral()
    {
        var parsed = ChatInputParser.Parse("hello world", ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Say, parsed!.Value.Channel);
        Assert.Null(parsed.Value.TargetName);
        Assert.Equal("hello world", parsed.Value.Text);
    }

    [Fact]
    public void SayPrefix_StripsTheSlashSay()
    {
        var parsed = ChatInputParser.Parse("/say hi there", ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Say, parsed!.Value.Channel);
        Assert.Null(parsed.Value.TargetName);
        Assert.Equal("hi there", parsed.Value.Text);
    }

    // -- Tell aliases ----------------------------------------------------

    [Fact]
    public void TellPrefix_FullForm_ParsesTargetAndMessage()
    {
        var parsed = ChatInputParser.Parse("/tell Bestie hi there", ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal("Bestie", parsed.Value.TargetName);
        Assert.Equal("hi there", parsed.Value.Text);
    }

    [Fact]
    public void TellPrefix_ShortForm_ParsesTargetAndMessage()
    {
        var parsed = ChatInputParser.Parse("/t Bestie hi", ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal("Bestie", parsed.Value.TargetName);
        Assert.Equal("hi", parsed.Value.Text);
    }

    [Fact]
    public void TellPrefix_MultiWordTarget_ChompsFirstTokenAsTarget()
    {
        var parsed = ChatInputParser.Parse("/t Sir Lancelot hello", ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal("Sir", parsed.Value.TargetName);
        Assert.Equal("Lancelot hello", parsed.Value.Text);
    }

    [Theory]
    [InlineData("/t Caith, hi",  "Caith")]
    [InlineData("/t Caith: hi",  "Caith")]
    [InlineData("/t Caith; hi",  "Caith")]
    [InlineData("/t Caith. hi",  "Caith")]
    [InlineData("/t Caith! hi",  "Caith")]
    [InlineData("/t Caith? hi",  "Caith")]
    [InlineData("/tell Caith, hi", "Caith")]
    public void TellPrefix_StripsTrailingPunctuationFromTarget(string raw, string expectedTarget)
    {
        var parsed = ChatInputParser.Parse(raw, ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal(expectedTarget, parsed.Value.TargetName);
        Assert.Equal("hi", parsed.Value.Text);
    }

    // -- Reply aliases ---------------------------------------------------

    [Fact]
    public void ReplyPrefix_UsesLastIncomingTellSenderAsTarget()
    {
        var parsed = ChatInputParser.Parse("/r back at you", ChatChannelKind.Say, lastTellSender: "Bestie");

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal("Bestie", parsed.Value.TargetName);
        Assert.Equal("back at you", parsed.Value.Text);
    }

    [Fact]
    public void ReplyPrefix_NoLastSender_ReturnsNull()
    {
        var parsed = ChatInputParser.Parse("/r hi", ChatChannelKind.Say, lastTellSender: null);

        Assert.Null(parsed);
    }

    [Fact]
    public void RpAlias_IsReply_NotRoleplay()
    {
        var parsed = ChatInputParser.Parse("/rp back at you", ChatChannelKind.Say, lastTellSender: "Bestie");

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal("Bestie", parsed.Value.TargetName);
        Assert.Equal("back at you", parsed.Value.Text);
    }

    [Fact]
    public void TellAliases_SendWhisperW_AllRouteAsTell()
    {
        foreach (string verb in new[] { "/send", "/whisper", "/w" })
        {
            var parsed = ChatInputParser.Parse($"{verb} Bestie hi", ChatChannelKind.Say, lastTellSender: null);
            Assert.NotNull(parsed);
            Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
            Assert.Equal("Bestie", parsed.Value.TargetName);
            Assert.Equal("hi", parsed.Value.Text);
        }
    }

    [Fact]
    public void RetellAlias_Rt_RoutesLikeRetell()
    {
        var parsed = ChatInputParser.Parse(
            "/rt once more",
            ChatChannelKind.Say,
            lastTellSender: null,
            lastOutgoingTellTarget: "Caith");

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal("Caith", parsed.Value.TargetName);
        Assert.Equal("once more", parsed.Value.Text);
    }

    [Theory]
    [InlineData("/tell Aunt Agatha, hello", "Aunt Agatha", "hello")]
    [InlineData("/t Aunt Agatha, hello", "Aunt Agatha", "hello")]
    public void TellTarget_SplitsOnFirstComma_NotFirstWhitespace(string raw, string expectedTarget, string expectedText)
    {
        var parsed = ChatInputParser.Parse(raw, ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal(expectedTarget, parsed.Value.TargetName);
        Assert.Equal(expectedText, parsed.Value.Text);
    }


    [Theory]
    [InlineData("/g raid time",       ChatChannelKind.Fellowship, "raid time")]
    [InlineData("/f buff up",         ChatChannelKind.Fellowship, "buff up")]
    [InlineData("/a swearing in",     ChatChannelKind.Allegiance, "swearing in")]
    [InlineData("/m monarch broadcast", ChatChannelKind.Monarch, "monarch broadcast")]
    [InlineData("/p patron only",     ChatChannelKind.Patron,     "patron only")]
    [InlineData("/v vassals only",    ChatChannelKind.Vassals,    "vassals only")]
    [InlineData("/c covassals only",  ChatChannelKind.CoVassals,  "covassals only")]
    [InlineData("/lfg need 3 more",   ChatChannelKind.Lfg,        "need 3 more")]
    [InlineData("/trade wts gem",     ChatChannelKind.Trade,      "wts gem")]
    [InlineData("/roleplay *waves*",  ChatChannelKind.Roleplay,   "*waves*")]
    [InlineData("/society olthoi raid", ChatChannelKind.Society,  "olthoi raid")]
    [InlineData("/olthoi for the queen", ChatChannelKind.Olthoi,  "for the queen")]
    public void ChannelPrefixes_RouteToTheirChannel(string raw, ChatChannelKind expectedChannel, string expectedText)
    {
        var parsed = ChatInputParser.Parse(raw, ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(expectedChannel, parsed!.Value.Channel);
        Assert.Null(parsed.Value.TargetName);
        Assert.Equal(expectedText, parsed.Value.Text);
    }


    [Fact]
    public void Empty_ReturnsNull()
    {
        Assert.Null(ChatInputParser.Parse("", ChatChannelKind.Say, lastTellSender: null));
    }

    [Fact]
    public void WhitespaceOnly_ReturnsNull()
    {
        Assert.Null(ChatInputParser.Parse("   \t  ", ChatChannelKind.Say, lastTellSender: null));
    }

    [Fact]
    public void SayWithNoMessage_ReturnsNull()
    {
        Assert.Null(ChatInputParser.Parse("/say", ChatChannelKind.Say, lastTellSender: null));
        Assert.Null(ChatInputParser.Parse("/say   ", ChatChannelKind.Say, lastTellSender: null));
    }

    [Fact]
    public void TellWithNoTargetNoMessage_ReturnsNull()
    {
        Assert.Null(ChatInputParser.Parse("/t", ChatChannelKind.Say, lastTellSender: null));
        Assert.Null(ChatInputParser.Parse("/tell", ChatChannelKind.Say, lastTellSender: null));
    }

    [Fact]
    public void TellWithTargetButNoMessage_ReturnsNull()
    {
        Assert.Null(ChatInputParser.Parse("/t Bestie", ChatChannelKind.Say, lastTellSender: null));
        Assert.Null(ChatInputParser.Parse("/tell Bestie   ", ChatChannelKind.Say, lastTellSender: null));
    }

    [Fact]
    public void ReplyWithNoMessage_ReturnsNull_EvenWithLastSender()
    {
        Assert.Null(ChatInputParser.Parse("/r", ChatChannelKind.Say, lastTellSender: "Bestie"));
        Assert.Null(ChatInputParser.Parse("/r   ", ChatChannelKind.Say, lastTellSender: "Bestie"));
    }

    [Fact]
    public void ChannelPrefixWithNoMessage_ReturnsNull()
    {
        Assert.Null(ChatInputParser.Parse("/g", ChatChannelKind.Say, lastTellSender: null));
        Assert.Null(ChatInputParser.Parse("/f   ", ChatChannelKind.Say, lastTellSender: null));
        Assert.Null(ChatInputParser.Parse("/a", ChatChannelKind.Say, lastTellSender: null));
    }

    [Fact]
    public void UnknownSlashCommand_IsRewrittenToAtFormForServer()
    {
        var parsed = ChatInputParser.Parse("/xyz hello", ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Say, parsed!.Value.Channel);
        Assert.Null(parsed.Value.TargetName);
        Assert.Equal("@xyz hello", parsed.Value.Text);
    }

    [Theory]
    [InlineData("/ci 629",       "@ci 629")]
    [InlineData("/tele holtburg", "@tele holtburg")]
    [InlineData("@ci 629",       "@ci 629")]        // @ form already server-ready — untouched
    [InlineData("@acehelp",      "@acehelp")]
    public void ServerCommandVerbs_ReachTheWireInAtForm(string input, string expectedText)
    {
        var parsed = ChatInputParser.Parse(input, ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Say, parsed!.Value.Channel);
        Assert.Equal(expectedText, parsed.Value.Text);
    }

    [Fact]
    public void SlashWithoutLetterVerb_StaysLiteralAtParseLevel()
    {
        Assert.Equal("/ hello", ChatInputParser.Parse("/ hello", ChatChannelKind.Say, lastTellSender: null)!.Value.Text);
        Assert.Equal("//shrug", ChatInputParser.Parse("//shrug", ChatChannelKind.Say, lastTellSender: null)!.Value.Text);
    }


    [Fact]
    public void NoPrefix_HonoursAlternateDefaultChannel()
    {
        var parsed = ChatInputParser.Parse("hi gang", ChatChannelKind.Fellowship, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Fellowship, parsed!.Value.Channel);
        Assert.Null(parsed.Value.TargetName);
        Assert.Equal("hi gang", parsed.Value.Text);
    }


    [Fact]
    public void PrefixSubstring_IsNotAVerbMatch()
    {
        var parsed = ChatInputParser.Parse("/genio public", ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Say, parsed!.Value.Channel);
        Assert.Equal("@genio public", parsed.Value.Text);
    }

    [Theory]
    [InlineData("/g",          true)]
    [InlineData("/say",        true)]
    [InlineData("/tell",       true)]
    [InlineData("/retell",     true)]
    [InlineData("/rt",         true)]
    [InlineData("/rp",         true)]
    [InlineData("/guild",      true)]
    [InlineData("/allegiance", false)]
    [InlineData("/lookingforgroup", false)]
    [InlineData("/role",       false)] // acdream invention, deleted
    [InlineData("/cv",         false)] // acdream invention, deleted
    [InlineData("/genio",      false)]
    [InlineData("/ls",         false)]
    [InlineData("/foo",        false)]
    [InlineData("/",           false)]
    public void IsKnownVerb_ChecksAgainstAliasTables(string verb, bool expected)
    {
        Assert.Equal(expected, ChatInputParser.IsKnownVerb(verb));
    }

    [Fact]
    public void IsKnownVerb_TrimsTrailingComma()
    {
        Assert.True(ChatInputParser.IsKnownVerb("/f,"));
        Assert.True(ChatInputParser.IsKnownVerb("/f"));
    }

    [Fact]
    public void CommaTrimmedVerb_ParsesTheSameAsWithoutComma()
    {
        // "/f, hi" == "/f hi" end to end through Parse.
        var withComma = ChatInputParser.Parse("/f, hi", ChatChannelKind.Say, lastTellSender: null);
        var withoutComma = ChatInputParser.Parse("/f hi", ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(withComma);
        Assert.NotNull(withoutComma);
        Assert.Equal(withoutComma!.Value.Channel, withComma!.Value.Channel);
        Assert.Equal(withoutComma.Value.Text, withComma.Value.Text);
        Assert.Equal(ChatChannelKind.Fellowship, withComma.Value.Channel);
        Assert.Equal("hi", withComma.Value.Text);
    }

    [Theory]
    [InlineData("/g hello", "/g")]
    [InlineData("/tell Bob hi", "/tell")]
    [InlineData("/foo", "/foo")]
    [InlineData("/", "/")]
    public void GetVerbToken_PullsFirstWhitespaceToken(string command, string expected)
    {
        Assert.Equal(expected, ChatInputParser.GetVerbToken(command));
    }

    [Theory]
    [InlineData("/general what's the deal",     ChatChannelKind.General,    "what's the deal")]
    [InlineData("/guild recall",                 ChatChannelKind.Allegiance, "recall")]
    [InlineData("/patron need help",             ChatChannelKind.Patron,     "need help")]
    [InlineData("/vassals listen up",            ChatChannelKind.Vassals,    "listen up")]
    [InlineData("/monarch heads up",             ChatChannelKind.Monarch,    "heads up")]
    [InlineData("/covassals tax season",         ChatChannelKind.CoVassals,  "tax season")]
    [InlineData("/fellowship buff time",         ChatChannelKind.Fellowship, "buff time")]
    [InlineData("/fellow buff time",             ChatChannelKind.Fellowship, "buff time")]
    [InlineData("/fellows buff time",            ChatChannelKind.Fellowship, "buff time")]
    [InlineData("/group buff time",              ChatChannelKind.Fellowship, "buff time")]
    [InlineData("/party buff time",              ChatChannelKind.Fellowship, "buff time")]
    [InlineData("/clfg hunt invite",             ChatChannelKind.Lfg,        "hunt invite")]
    [InlineData("/roleplay walk-up",             ChatChannelKind.Roleplay,   "walk-up")]
    [InlineData("/crp walk-up",                  ChatChannelKind.Roleplay,   "walk-up")]
    public void LongFormAliases_RouteToTheirChannel(string raw, ChatChannelKind expected, string text)
    {
        var parsed = ChatInputParser.Parse(raw, ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Value.Channel);
        Assert.Null(parsed.Value.TargetName);
        Assert.Equal(text, parsed.Value.Text);
    }


    [Theory]
    [InlineData("/g")]
    [InlineData("/f")]
    [InlineData("/fellow")]
    [InlineData("/fellows")]
    [InlineData("/fellowship")]
    [InlineData("/group")]
    [InlineData("/party")]
    [InlineData("/a")]
    [InlineData("/guild")]
    [InlineData("/gu")]
    [InlineData("/ab")]
    [InlineData("/m")]
    [InlineData("/monarch")]
    [InlineData("/p")]
    [InlineData("/patron")]
    [InlineData("/v")]
    [InlineData("/vassal")]
    [InlineData("/vassals")]
    [InlineData("/c")]
    [InlineData("/covassal")]
    [InlineData("/covassals")]
    [InlineData("/co-vassals")]
    public void IsBareRegisteredChannelVerb_TrueForEveryLegacyChannelAlias(string verb)
    {
        Assert.True(ChatInputParser.IsBareRegisteredChannelVerb(verb));
    }

    [Theory]
    [InlineData("/general")]
    [InlineData("/cg")]
    [InlineData("/lfg")]
    [InlineData("/clfg")]
    [InlineData("/trade")]
    [InlineData("/ct")]
    [InlineData("/roleplay")]
    [InlineData("/crp")]
    [InlineData("/society")]
    [InlineData("/soc")]
    [InlineData("/olthoi")]
    [InlineData("/o")]
    public void IsBareRegisteredChannelVerb_FalseForTurbineOnlyChannels(string verb)
    {
        Assert.False(ChatInputParser.IsBareRegisteredChannelVerb(verb));
    }

    [Theory]
    [InlineData("/g hi gang")]
    [InlineData("/a hey")]
    [InlineData("hello")]
    [InlineData("/say hi")]
    [InlineData("/r hi")]
    public void IsBareRegisteredChannelVerb_FalseWithMessageOrNotAChannelVerb(string input)
    {
        Assert.False(ChatInputParser.IsBareRegisteredChannelVerb(input));
    }

    [Theory]
    [InlineData("/r hi there")]
    [InlineData("/reply hi there")]
    [InlineData("/rp hi there")]
    public void IsReplyMissingLastTeller_TrueWithMessageAndNoLastTeller(string input)
    {
        Assert.True(ChatInputParser.IsReplyMissingLastTeller(input, lastTellSender: null));
        Assert.True(ChatInputParser.IsReplyMissingLastTeller(input, lastTellSender: ""));
    }

    [Fact]
    public void IsReplyMissingLastTeller_FalseWhenLastTellerPresent()
    {
        Assert.False(ChatInputParser.IsReplyMissingLastTeller("/r hi there", lastTellSender: "Bestie"));
    }

    [Theory]
    [InlineData("/r")]
    [InlineData("/r   ")]
    [InlineData("/g hi gang")]
    [InlineData("hello")]
    public void IsReplyMissingLastTeller_FalseWithoutAMessageOrNotAReplyVerb(string input)
    {
        Assert.False(ChatInputParser.IsReplyMissingLastTeller(input, lastTellSender: null));
    }
}
