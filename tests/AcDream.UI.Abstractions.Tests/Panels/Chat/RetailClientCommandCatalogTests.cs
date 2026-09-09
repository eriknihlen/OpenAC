using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

public sealed class RetailClientCommandCatalogTests
{
    [Theory]
    [InlineData("/lifestone")]
    [InlineData("@lifestone")]
    [InlineData("/lif")]
    [InlineData("@LIF")]
    [InlineData("/ls")]
    [InlineData("@LS")]
    public void RetailAliases_ResolveCaseInsensitively(string input)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(ClientCommandId.LifestoneRecall, match.Command);
        Assert.True(match.HasValidArguments);
        Assert.Empty(match.Arguments);
    }

    [Theory]
    [InlineData("/marketplace", ClientCommandId.MarketplaceRecall)]
    [InlineData("@MAR", ClientCommandId.MarketplaceRecall)]
    [InlineData("/mp", ClientCommandId.MarketplaceRecall)]
    [InlineData("/pkarena", ClientCommandId.PkArenaRecall)]
    [InlineData("/pka", ClientCommandId.PkArenaRecall)]
    [InlineData("/pklarena", ClientCommandId.PkLiteArenaRecall)]
    [InlineData("/pla", ClientCommandId.PkLiteArenaRecall)]
    [InlineData("/pklite", ClientCommandId.EnterPkLite)]
    [InlineData("@pklite", ClientCommandId.EnterPkLite)]
    [InlineData("/hor", ClientCommandId.HouseRecall)]
    [InlineData("/hr", ClientCommandId.HouseRecall)]
    [InlineData("/hom", ClientCommandId.MansionRecall)]
    [InlineData("/hoa", ClientCommandId.MansionRecall)]
    [InlineData("/age", ClientCommandId.QueryAge)]
    [InlineData("/birth", ClientCommandId.QueryBirth)]
    [InlineData("/framerate", ClientCommandId.ToggleFrameRate)]
    [InlineData("/lockui", ClientCommandId.ToggleUiLock)]
    [InlineData("/version", ClientCommandId.ShowVersion)]
    [InlineData("/loc", ClientCommandId.ShowLocation)]
    [InlineData("/corpse", ClientCommandId.ShowLastCorpseLocation)]
    [InlineData("/cor", ClientCommandId.ShowLastCorpseLocation)]
    public void AdditionalRetailAliases_Resolve(string input, ClientCommandId expected)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(expected, match.Command);
        Assert.True(match.HasValidArguments);
    }

    [Theory]
    [InlineData("/clear all", ClientCommandId.ClearChat)]
    [InlineData("/saveui hunt", ClientCommandId.SaveUi)]
    [InlineData("/loadui hunt", ClientCommandId.LoadUi)]
    [InlineData("/saveautoui", ClientCommandId.SaveAutoUi)]
    [InlineData("/loadautoui", ClientCommandId.LoadAutoUi)]
    [InlineData("/afk msg lunch", ClientCommandId.Away)]
    [InlineData("/consent who", ClientCommandId.Consent)]
    [InlineData("/e waves", ClientCommandId.Emote)]
    [InlineData("/em waves", ClientCommandId.Emote)]
    [InlineData("/emote waves", ClientCommandId.Emote)]
    [InlineData("/me waves", ClientCommandId.Emote)]
    [InlineData("/emotes", ClientCommandId.ListEmotes)]
    [InlineData("/friends online", ClientCommandId.Friends)]
    [InlineData("/friends_add Alice", ClientCommandId.FriendsAdd)]
    [InlineData("/friends_remove Alice", ClientCommandId.FriendsRemove)]
    [InlineData("/squelch -tell Alice", ClientCommandId.Squelch)]
    [InlineData("/unsquelch Alice", ClientCommandId.Unsquelch)]
    [InlineData("/filter -combat", ClientCommandId.Filter)]
    [InlineData("/unfilter -combat", ClientCommandId.Unfilter)]
    [InlineData("/messagetypes", ClientCommandId.ListMessageTypes)]
    [InlineData("/fillcomps scarabs 500", ClientCommandId.FillComponents)]
    [InlineData("/day", ClientCommandId.TogglePersistentDaylight)]
    [InlineData("@day ignored", ClientCommandId.TogglePersistentDaylight)]
    [InlineData("/render radius 12", ClientCommandId.RenderOption)]
    [InlineData("@render usage", ClientCommandId.RenderOption)]
    public void CommandFamilies_ResolveToTypedClientCommands(
        string input, ClientCommandId expected)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(expected, match.Command);
        Assert.True(match.HasValidArguments);
    }

    [Theory]
    [InlineData("/house recall", ClientCommandId.HouseRecall)]
    [InlineData("@house mansion_recall", ClientCommandId.MansionRecall)]
    [InlineData("/house alleg_recall", ClientCommandId.MansionRecall)]
    public void HouseRecallSubcommands_Resolve(string input, ClientCommandId expected)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(expected, match.Command);
        Assert.True(match.HasValidArguments);
    }

    [Theory]
    [InlineData("/house open", ClientCommandId.HouseOpenStatus, "open")]
    [InlineData("/house close ignored", ClientCommandId.HouseOpenStatus, "close")]
    [InlineData("/house guest add Bob", ClientCommandId.HouseGuests, "add Bob")]
    [InlineData("/house storage add Lord Bob", ClientCommandId.HouseStorage, "add Lord Bob")]
    [InlineData("/house remove Bob", ClientCommandId.HouseBoot, "Bob")]
    [InlineData("/house boot -all", ClientCommandId.HouseBoot, "-all")]
    [InlineData("/house boot_all ignored", ClientCommandId.HouseBootAll, "ignored")]
    [InlineData("/house remove_all", ClientCommandId.HouseBootAll, "")]
    [InlineData("/house available Cottage", ClientCommandId.HouseAvailableList, "Cottage")]
    [InlineData("/house hooks off", ClientCommandId.HouseHooks, "off")]
    public void HouseManagementSubcommands_ResolveLocally(
        string input,
        ClientCommandId expected,
        string expectedArguments)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.True(match.HasValidArguments);
        Assert.Equal(expected, match.Command);
        Assert.Equal(expectedArguments, match.Arguments);
    }

    [Theory]
    [InlineData("/house")]
    [InlineData("/house nope")]
    public void HouseUnknownOrMissingSubcommand_ShowsRetailRefusalLocally(string input)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(ClientCommandId.HouseUnrecognizedSubcommand, match.Command);
        Assert.False(match.HasValidArguments);
        Assert.Equal(
            "Please see @help House for more information on how to use this command.",
            match.InvalidArgumentsText);
    }

    [Fact]
    public void HouseAvailableWithoutType_UsesHslistArgumentValidation()
    {
        Assert.True(RetailClientCommandCatalog.TryMatch("/house available", out var match));
        Assert.Equal(ClientCommandId.HouseAvailableList, match.Command);
        Assert.False(match.HasValidArguments);
    }

    [Theory]
    [InlineData("/house abandon", ClientCommandId.HouseAbandon)]
    [InlineData("/house re", ClientCommandId.HouseRecall)]
    [InlineData("/house ma", ClientCommandId.MansionRecall)]
    [InlineData("/hou recall", ClientCommandId.HouseRecall)]
    public void HouseAliasesAndShortcuts_Resolve(string input, ClientCommandId expected)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(expected, match.Command);
        Assert.True(match.HasValidArguments);
    }

    [Theory]
    [InlineData("/allegiance boot Bob", ClientCommandId.AllegianceBoot, "Bob")]
    [InlineData("/allegiance ban add Bob", ClientCommandId.AllegianceBan, "add Bob")]
    [InlineData("/all chat gag Bob", ClientCommandId.AllegianceChat, "gag Bob")]
    [InlineData("/all ch off", ClientCommandId.AllegianceChat, "off")]
    [InlineData("/allegiance broadcast hello", ClientCommandId.AllegianceBroadcast, "hello")]
    [InlineData("/allegiance br hello", ClientCommandId.AllegianceBroadcast, "hello")]
    [InlineData("/all officer add 2 Bob", ClientCommandId.AllegianceOfficer, "add 2 Bob")]
    [InlineData("/all title set 2 Regent", ClientCommandId.AllegianceOfficerTitle, "set 2 Regent")]
    [InlineData("/allegiance motd", ClientCommandId.AllegianceMotd, "")]
    [InlineData("/allegiance name set A Name", ClientCommandId.AllegianceName, "set A Name")]
    [InlineData("/allegiance lock bypass Bob", ClientCommandId.AllegianceLock, "bypass Bob")]
    [InlineData("/allegiance house guest open", ClientCommandId.AllegianceHouse, "guest open")]
    [InlineData("/motd set Welcome", ClientCommandId.AllegianceMotd, "set Welcome")]
    public void AllegianceManagementSubcommands_ResolveLocally(
        string input,
        ClientCommandId expected,
        string expectedArguments)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.True(match.HasValidArguments);
        Assert.Equal(expected, match.Command);
        Assert.Equal(expectedArguments, match.Arguments);
    }

    [Theory]
    [InlineData("/allegiance")]
    [InlineData("/allegiance nope")]
    [InlineData("/all nonsense")]
    public void UnknownAllegianceSubcommand_ShowsRetailRefusal_ClientSide(string input)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(ClientCommandId.AllegianceUnrecognizedSubcommand, match.Command);
        Assert.False(match.HasValidArguments);
        Assert.Equal(
            "Please see @help Allegiance for more information on how to use this command.",
            match.InvalidArgumentsText);
    }

    [Theory]
    [InlineData("/allegiance hometown", ClientCommandId.AllegianceHometown, "")]
    [InlineData("/allegiance ho", ClientCommandId.AllegianceHometown, "")]
    [InlineData("/alh", ClientCommandId.AllegianceHometown, "")]
    [InlineData("/ah", ClientCommandId.AllegianceHometown, "")]
    [InlineData("/allegiance info", ClientCommandId.AllegianceInfo, "")]
    [InlineData("/allegiance info Bob", ClientCommandId.AllegianceInfo, "Bob")]
    [InlineData("/all info Bob", ClientCommandId.AllegianceInfo, "Bob")]
    public void AllegianceSimpleSubcommands_Resolve(
        string input, ClientCommandId expected, string expectedArguments)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(expected, match.Command);
        Assert.True(match.HasValidArguments);
        Assert.Equal(expectedArguments, match.Arguments);
    }

    [Theory]
    [InlineData("/pkl", ClientCommandId.EnterPkLite)]
    [InlineData("/message_types", ClientCommandId.ListMessageTypes)]
    [InlineData("/msgtypes", ClientCommandId.ListMessageTypes)]
    [InlineData("/msg_types", ClientCommandId.ListMessageTypes)]
    [InlineData("/endurance", ClientCommandId.Endurance)]
    [InlineData("/speaker", ClientCommandId.Speaker)]
    [InlineData("/index", ClientCommandId.IndexChannels)]
    [InlineData("/index foo", ClientCommandId.IndexChannels)]
    public void MissingAliasesSweep_Resolve(string input, ClientCommandId expected)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(expected, match.Command);
        Assert.True(match.HasValidArguments);
    }

    [Theory]
    [InlineData("/chat on")]
    [InlineData("/chat off")]
    [InlineData("/notell on")]
    [InlineData("/notell off")]
    public void ChatNoTellToggle_ValidArguments_Resolve(string input)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.True(match.HasValidArguments);
    }

    [Fact]
    public void ChatToggle_InvalidArgument_IsRejected()
    {
        Assert.True(RetailClientCommandCatalog.TryMatch("/chat maybe", out var match));
        Assert.False(match.HasValidArguments);
    }

    [Theory]
    [InlineData("/join allegiance")]
    [InlineData("/join general")]
    [InlineData("/leave society")]
    [InlineData("/leave soc")]
    public void JoinLeave_ValidTags_Resolve(string input)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.True(match.HasValidArguments);
    }

    [Fact]
    public void JoinLeave_InvalidTag_IsRejected()
    {
        Assert.True(RetailClientCommandCatalog.TryMatch("/join nonsense", out var match));
        Assert.False(match.HasValidArguments);
    }

    [Theory]
    [InlineData("/permit add Bob", true)]
    [InlineData("/permit remove Bob", true)]
    [InlineData("/permit add Aunt Agatha", true)]
    [InlineData("/permit remove Lord Gnarly Beard", true)]
    [InlineData("/permit add", false)]
    [InlineData("/permit maybe Bob", false)]
    public void Permit_ArgumentShape(string input, bool expectedValid)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(expectedValid, match.HasValidArguments);
    }

    [Theory]
    [InlineData("/hslist Cottage", true)]
    [InlineData("/hslist mansion", true)]
    [InlineData("/hslist nonsense", false)]
    public void HouseAvailableList_ArgumentShape(string input, bool expectedValid)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(expectedValid, match.HasValidArguments);
    }

    [Theory]
    [InlineData("/clist fellowship", true)]
    [InlineData("/on admin", true)]
    [InlineData("/off nonsense", true)]
    [InlineData("/clist", false)]
    [InlineData("/on fellowship extra", false)]
    public void ChannelArgumentCommands_RequireExactlyOneToken(string input, bool expectedValid)
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(input, out var match));
        Assert.Equal(expectedValid, match.HasValidArguments);
    }

    [Fact]
    public void MrPr_AreNeverExecutable()
    {
        Assert.False(RetailClientCommandCatalog.TryMatch("/mr", out _));
        Assert.False(RetailClientCommandCatalog.TryMatch("/pr", out _));
        Assert.False(RetailClientCommandCatalog.TryMatch("/mr hello", out _));
        Assert.False(RetailClientCommandCatalog.TryMatch("/pr hello", out _));
    }

    [Fact]
    public void LifestoneArgument_IsRecognizedButInvalid()
    {
        Assert.True(RetailClientCommandCatalog.TryMatch("/ls now", out var match));
        Assert.Equal("now", match.Arguments);
        Assert.False(match.HasValidArguments);
        Assert.Equal("/lifestone", match.Usage);
    }

    [Theory]
    [InlineData("/ci 629")]
    [InlineData("@acehelp")]
    [InlineData("ordinary speech")]
    public void NonClientCommands_DoNotMatch(string input)
        => Assert.False(RetailClientCommandCatalog.TryMatch(input, out _));
}
