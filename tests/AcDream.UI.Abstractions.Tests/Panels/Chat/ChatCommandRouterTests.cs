using AcDream.Core.Chat;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Chat;
using Xunit;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

public class ChatCommandRouterTests
{
    [Fact]
    public void PluginCommand_IsHandledBeforeUnknownServerFallback()
    {
        var (vm, _, inner) = Fixture();
        var bus = new PluginCommandBus(inner);

        SubmitOutcome outcome = ChatCommandRouter.Submit(
            "/vt start",
            vm,
            bus,
            ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Equal("/vt start", bus.Handled);
        Assert.Empty(inner.Published);
    }

    private sealed class CaptureBus : ICommandBus
    {
        public List<object> Published { get; } = new();

        public void Publish<T>(T command) where T : notnull
            => Published.Add(command);
    }

    private sealed class PluginCommandBus(CaptureBus inner)
        : IPluginCommandBus
    {
        public string? Handled { get; private set; }

        public bool TryHandlePluginCommand(string commandLine)
        {
            Handled = commandLine;
            return true;
        }

        public void Publish<T>(T command) where T : notnull =>
            inner.Publish(command);
    }

    private static (ChatVM vm, ChatLog log, CaptureBus bus) Fixture()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log, displayLimit: 50);
        return (vm, log, new CaptureBus());
    }

    private static (ChatVM vm, ChatLog log, CaptureBus bus, List<string> interfaceTexts) FixtureWithInterfaceSink()
    {
        var log = new ChatLog();
        var interfaceTexts = new List<string>();
        var vm = new ChatVM(log, displayLimit: 50)
        {
            OnInterfaceText = interfaceTexts.Add,
        };
        return (vm, log, new CaptureBus(), interfaceTexts);
    }

    [Fact]
    public void PlainText_PublishesOnDefaultChannel()
    {
        var (vm, _, bus) = Fixture();
        var outcome = ChatCommandRouter.Submit("hello there", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        var command = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal(ChatChannelKind.Say, command.Channel);
        Assert.Equal("hello there", command.Text);
    }

    [Fact]
    public void DefaultChannel_IsHonored()
    {
        var (vm, _, bus) = Fixture();
        ChatCommandRouter.Submit("hi", vm, bus, ChatChannelKind.Fellowship);

        var command = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal(ChatChannelKind.Fellowship, command.Channel);
    }

    [Fact]
    public void ClearCommand_PublishesTypedRetailCommand()
    {
        var (vm, log, bus) = Fixture();
        log.OnSystemMessage("x", chatType: 0);

        var outcome = ChatCommandRouter.Submit("/clear", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var command = Assert.IsType<ExecuteClientCommandCmd>(Assert.Single(bus.Published));
        Assert.Equal(ClientCommandId.ClearChat, command.Command);
        Assert.Equal("", command.Arguments);
        Assert.Single(log.Snapshot());
    }

    [Theory]
    [InlineData("/lifestone")]
    [InlineData("/lif")]
    [InlineData("/ls")]
    [InlineData("@LS")]
    public void LifestoneAliases_PublishTypedClientCommand(string input)
    {
        var (vm, _, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit(input, vm, bus, ChatChannelKind.Fellowship);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var command = Assert.IsType<ExecuteClientCommandCmd>(Assert.Single(bus.Published));
        Assert.Equal(ClientCommandId.LifestoneRecall, command.Command);
    }

    [Fact]
    public void LifestoneWithArguments_ShowsRetailsBespokeRefusal_ViaInterfaceTextSeam()
    {
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit("/ls now", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published);
        Assert.Equal(
            "Please see @help lifestone for more information on how to use this command.",
            Assert.Single(interfaceTexts));
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void LifestoneWithArguments_NoInterfaceSinkWired_FallsBackToChatLog_TaggedClientLocal()
    {
        var (vm, log, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit("/ls now", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var entry = Assert.Single(log.Snapshot());
        Assert.Equal(
            "Please see @help lifestone for more information on how to use this command.",
            entry.Text);
        Assert.Equal((uint)RetailLogTextType.ClientLocal, entry.LogTextType);
    }

    [Fact]
    public void PkArenaWithArguments_ShowsRetailsGenericBadArgsFallback_ViaInterfaceTextSeam()
    {
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit("/pka now", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published);
        Assert.Equal("That is not a valid command.", Assert.Single(interfaceTexts));
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void MarketplaceWithArguments_ShowsRetailsBespokeRefusal_ViaInterfaceTextSeam()
    {
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit("/mar now", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published);
        Assert.Equal(
            "Please see @help marketplace for more information on how to use this command.",
            Assert.Single(interfaceTexts));
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void PkLiteAlias_ResolvesAsClientHandled_NotTheServerTextPath()
    {
        var (vm, _, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit("@pklite", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var command = Assert.IsType<ExecuteClientCommandCmd>(Assert.Single(bus.Published));
        Assert.Equal(ClientCommandId.EnterPkLite, command.Command);
    }

    [Fact]
    public void UnknownSlashVerb_RoutesThroughExplicitServerCommand()
    {
        var (vm, log, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit(
            "/notacommand", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        var command = Assert.IsType<SendServerCommandCmd>(Assert.Single(bus.Published));
        Assert.Equal("@notacommand", command.Text);
        Assert.DoesNotContain(log.Snapshot(), entry => entry.Text.Contains("Unknown command"));
    }

    [Theory]
    [InlineData("/ci 629 5")]
    [InlineData("@ci 629 5")]
    public void ServerCommandWithArgs_PublishesCanonicalAtForm_EvenOnChannelDefault(
        string input)
    {
        var (vm, _, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit(
            input, vm, bus, ChatChannelKind.Fellowship);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        var command = Assert.IsType<SendServerCommandCmd>(Assert.Single(bus.Published));
        Assert.Equal("@ci 629 5", command.Text);
    }

    [Fact]
    public void EmptyInput_DoesNothing()
    {
        var (vm, _, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit("   ", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.Empty, outcome);
        Assert.Empty(bus.Published);
    }


    [Theory]
    [InlineData(":waves")]
    [InlineData(";waves")]
    public void EmotePrefix_RewritesToAtEmote(string raw)
    {
        var (vm, _, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit(raw, vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var command = Assert.IsType<ExecuteClientCommandCmd>(Assert.Single(bus.Published));
        Assert.Equal(ClientCommandId.Emote, command.Command);
        Assert.Equal("waves", command.Arguments);
    }

    [Fact]
    public void UnregisteredChannelTag_PublishesRawChannelBroadcast()
    {
        var (vm, _, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit("/admin server is misbehaving", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        var command = Assert.IsType<SendRawChannelCmd>(Assert.Single(bus.Published));
        Assert.Equal(0x00000002u, command.ChannelId);
        Assert.Equal("server is misbehaving", command.Text);
    }

    [Fact]
    public void UnregisteredChannelTag_WithNoText_PassesThroughToServer()
    {
        var (vm, log, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit("/sentinel", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        var command = Assert.IsType<SendServerCommandCmd>(Assert.Single(bus.Published));
        Assert.Equal("@sentinel", command.Text);
        Assert.DoesNotContain(log.Snapshot(), entry => entry.Text.Contains("You must specify the text"));
    }


    [Theory]
    [InlineData("/allegiance nope Bob")]
    [InlineData("/all nope Bob")]
    public void AllegianceUnrecognizedSubcommand_ShowsRetailRefusal_NeverBroadcastsOrSends(string input)
    {
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit(input, vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published); // no SendRawChannelCmd, no SendServerCommandCmd
        Assert.Equal(
            "Please see @help Allegiance for more information on how to use this command.",
            Assert.Single(interfaceTexts));
        Assert.Empty(log.Snapshot());
    }

    [Theory]
    [InlineData("/allegiance boot Lord Bob", ClientCommandId.AllegianceBoot, "Lord Bob")]
    [InlineData("/house storage add Lord Bob", ClientCommandId.HouseStorage, "add Lord Bob")]
    [InlineData("/motd set Welcome home", ClientCommandId.AllegianceMotd, "set Welcome home")]
    public void AdministrationCommand_PublishesTypedClientCommand(
        string input,
        ClientCommandId expected,
        string expectedArguments)
    {
        var (vm, _, bus) = Fixture();

        SubmitOutcome outcome = ChatCommandRouter.Submit(
            input,
            vm,
            bus,
            ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var command = Assert.IsType<ExecuteClientCommandCmd>(Assert.Single(bus.Published));
        Assert.Equal(expected, command.Command);
        Assert.Equal(expectedArguments, command.Arguments);
    }

    [Fact]
    public void RegisteredChannelVerb_NeverReachesTheRawFallback()
    {
        var (vm, _, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit("/f hi gang", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        var command = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal(ChatChannelKind.Fellowship, command.Channel);
    }

    [Fact]
    public void HelpVerb_ShowsCatalogHelpText()
    {
        var (vm, log, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit("/help lifestone", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published);
        Assert.Contains(log.Snapshot(), entry => entry.Text.Contains("Returns you to the last lifestone"));
    }

    [Theory]
    [InlineData("/help mr", "@mr <text> - Sends the text to the last person who used @m to send  you a message. This only works for monarchs.")]
    [InlineData("/help pr", "@pr <text> - Sends the text to the last vassal who used @p to send  you a message.")]
    public void HelpVerb_MrPr_ShowsVerbatimRetailText(string input, string expected)
    {
        var (vm, log, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit(input, vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var entries = log.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.Equal(RetailCommandHelpTable.HelpPrefixNote, entries[0].Text);
        Assert.Equal(RetailCommandHelpTable.ForMoreInformationPrefix + expected, entries[1].Text);
    }

    [Fact]
    public void HelpVerb_UnknownVerb_ShowsRetailUnknownCommandText_InChatLog_TaggedDefault()
    {
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit("/help nonsenseverb", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published);
        Assert.Empty(interfaceTexts);
        var entry = Assert.Single(log.Snapshot());
        Assert.Equal(RetailCommandHelpTable.UnknownCommand, entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);
    }

    [Fact]
    public void HelpVerb_UnknownVerb_NoInterfaceSinkWired_StillRoutesToChatLog_TaggedDefault()
    {
        var (vm, log, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit("/help nonsenseverb", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var entry = Assert.Single(log.Snapshot());
        Assert.Equal(RetailCommandHelpTable.UnknownCommand, entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);
    }

    [Fact]
    public void HelpBare_ShowsRetailTwoEntryShape()
    {
        var (vm, log, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit("/help", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var entries = log.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.Equal(RetailCommandHelpTable.HelpPrefixNote, entries[0].Text);
        Assert.Equal(RetailCommandHelpTable.AvailableHelpListing, entries[1].Text);
    }


    [Theory]
    [InlineData("/clist")]
    [InlineData("/clist a b")]
    [InlineData("/on")]
    [InlineData("/off nonsense extra")]
    public void ChannelListOnOff_BadArgumentShape_ShowsRetailRefusal_ViaInterfaceTextSeam(string input)
    {
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit(input, vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published);
        Assert.Equal("Please specify the channel name.", Assert.Single(interfaceTexts));
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void HouseAvailableList_BadHouseType_ShowsRetailRefusal_ViaInterfaceTextSeam()
    {
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit("/hslist nonsense", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published);
        Assert.Equal(
            "Please see @help hslist for more information on how to use this command",
            Assert.Single(interfaceTexts));
        Assert.Empty(log.Snapshot());
    }

    [Theory]
    [InlineData("/g")]
    [InlineData("/f")]
    [InlineData("/fellowship")]
    [InlineData("/a")]
    [InlineData("/ab")]
    [InlineData("/m")]
    [InlineData("/p")]
    [InlineData("/v")]
    [InlineData("/c")]
    public void BareRegisteredChannelVerb_ShowsDoStupidChannelHackRefusal_ViaInterfaceTextSeam(string input)
    {
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit(input, vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published);
        Assert.Equal("You must specify the text you wish to say!", Assert.Single(interfaceTexts));
        Assert.Empty(log.Snapshot());
    }

    [Theory]
    [InlineData("/lfg")]
    [InlineData("/trade")]
    [InlineData("/general")]
    [InlineData("/roleplay")]
    [InlineData("/society")]
    [InlineData("/olthoi")]
    public void BareTurbineOnlyChannelVerb_NeverShowsDoStupidChannelHackRefusal(string input)
    {
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit(input, vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.Dropped, outcome);
        Assert.Empty(bus.Published);
        Assert.Empty(interfaceTexts);
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void Reply_WithMessage_NoLastTeller_ShowsDoReplyRefusal_ViaInterfaceTextSeam()
    {
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit("/r hi there", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published);
        Assert.Equal("Someone must @tell you first!", Assert.Single(interfaceTexts));
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void Reply_WithMessage_NoLastTeller_NoInterfaceSinkWired_FallsBackToChatLog_TaggedClientLocal()
    {
        var (vm, log, bus) = Fixture();

        var outcome = ChatCommandRouter.Submit("/reply hi there", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var entry = Assert.Single(log.Snapshot());
        Assert.Equal("Someone must @tell you first!", entry.Text);
        Assert.Equal((uint)RetailLogTextType.ClientLocal, entry.LogTextType);
    }

    [Fact]
    public void Reply_WithMessage_WithLastTeller_StillSendsNormally()
    {
        // Sanity: the new missing-last-teller predicate must not shadow
        // the ordinary reply path once a Tell has arrived.
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();
        log.OnTellReceived("Bestie", "psst", senderGuid: 0x5000_0042, logTextType: 0x03u);

        var outcome = ChatCommandRouter.Submit("/r hi there", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        var command = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal("Bestie", command.TargetName);
        Assert.Empty(interfaceTexts);
    }

    [Fact]
    public void DegeneratePrefix_UnknownCommand_ShowsRefusal_InChatLog_TaggedDefault()
    {
        var (vm, log, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit("/", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.UnknownCommand, outcome);
        Assert.Empty(bus.Published);
        Assert.Empty(interfaceTexts);
        var entry = Assert.Single(log.Snapshot());
        Assert.Contains("Unknown command:", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);
    }

    [Fact]
    public void EnduranceCommand_ValidArguments_NeverTouchesTheInterfaceTextSeam()
    {
        var (vm, _, bus, interfaceTexts) = FixtureWithInterfaceSink();

        var outcome = ChatCommandRouter.Submit("/endurance", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var command = Assert.IsType<ExecuteClientCommandCmd>(Assert.Single(bus.Published));
        Assert.Equal(ClientCommandId.Endurance, command.Command);
        Assert.Empty(interfaceTexts);
    }
}
