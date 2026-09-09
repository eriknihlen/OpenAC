using System.Globalization;
using AcDream.App.UI;
using AcDream.Core.Chat;
using AcDream.Core.Physics;
using AcDream.Core.Social;
using AcDream.UI.Abstractions;

namespace AcDream.App.Tests.UI;

public sealed class ClientCommandControllerTests
{
    [Fact]
    public void RecallAndQueryCommands_ExecuteTheirExactBindings()
    {
        var calls = new List<string>();
        var controller = NewController(calls: calls);

        Execute(ClientCommandId.LifestoneRecall);
        Execute(ClientCommandId.MarketplaceRecall);
        Execute(ClientCommandId.PkArenaRecall);
        Execute(ClientCommandId.PkLiteArenaRecall);
        Execute(ClientCommandId.HouseRecall);
        Execute(ClientCommandId.MansionRecall);
        Execute(ClientCommandId.QueryAge);
        Execute(ClientCommandId.QueryBirth);

        Assert.Equal(
            ["ls", "mp", "pka", "pla", "house", "mansion", "age", "birth"],
            calls);

        void Execute(ClientCommandId id) => controller.Execute(
            new ExecuteClientCommandCmd(id, Arguments: string.Empty));
    }

    [Fact]
    public void PkArena_NonPk_ShowsRetailFailureWithoutSending()
    {
        var calls = new List<string>();
        var errors = new List<uint>();
        var controller = NewController(calls, errors, playerBitfield: 0x8u);

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.PkArenaRecall, string.Empty));

        Assert.Empty(calls);
        Assert.Equal([0x055Fu], errors);
    }

    [Fact]
    public void PkLiteArena_NonPkLite_ShowsRetailFailureWithoutSending()
    {
        var calls = new List<string>();
        var errors = new List<uint>();
        var controller = NewController(calls, errors, playerBitfield: 0x8u);

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.PkLiteArenaRecall, string.Empty));

        Assert.Empty(calls);
        Assert.Equal([0x0560u], errors);
    }

    [Fact]
    public void EnterPkLite_NonPk_SendsTheRetailGameAction()
    {
        var calls = new List<string>();
        var errors = new List<uint>();
        var controller = NewController(calls, errors, playerBitfield: 0x8u);

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.EnterPkLite, string.Empty));

        Assert.Equal(["pklite"], calls);
        Assert.Empty(errors);
    }

    [Fact]
    public void EnterPkLite_AlreadyPk_ShowsRetailFailureWithoutSending()
    {
        var calls = new List<string>();
        var errors = new List<uint>();
        var controller = NewController(calls, errors, playerBitfield: 0x20u);

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.EnterPkLite, string.Empty));

        Assert.Empty(calls);
        Assert.Equal([0x0507u], errors);
    }

    [Fact]
    public void EnterPkLite_AlreadyPkLite_ShowsRetailFailureWithoutSending()
    {
        var calls = new List<string>();
        var errors = new List<uint>();
        var controller = NewController(calls, errors, playerBitfield: 0x2000000u);

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.EnterPkLite, string.Empty));

        Assert.Empty(calls);
        Assert.Equal([0x0507u], errors);
    }

    [Fact]
    public void EnterPkLite_UnknownPlayerDescription_SendsRatherThanRejects()
    {
        var calls = new List<string>();
        var errors = new List<uint>();
        var controller = NewController(calls, errors, playerBitfield: null);

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.EnterPkLite, string.Empty));

        Assert.Equal(["pklite"], calls);
        Assert.Empty(errors);
    }

    [Fact]
    public void MissingPlayerDescription_DoesNotInventAClientRejection()
    {
        var calls = new List<string>();
        var controller = NewController(calls, playerBitfield: null);

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.PkArenaRecall, string.Empty));

        Assert.Equal(["pka"], calls);
    }

    [Fact]
    public void LocalPresentationCommands_UseApplicationServices()
    {
        var calls = new List<string>();
        var messages = new List<string>();
        var controller = NewController(calls, messages: messages);

        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.ToggleFrameRate, ""));
        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.ToggleUiLock, ""));
        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.ShowVersion, ""));

        Assert.Equal(["fps", "lock"], calls);
        Assert.Equal(["Client version 1.2.3"], messages);
    }

    [Fact]
    public void Die_RequiresRetailConfirmationBeforeSuicide()
    {
        var calls = new List<string>();
        var controller = NewController(calls);

        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.Die, ""));

        Assert.Equal(
            [
                "confirm:Do you really want to kill your character? You may drop items and accrue a vitae penalty.",
                "suicide",
            ],
            calls);
    }

    [Fact]
    public void ChatAndLayoutCommands_UseNamedAndAutomaticPersistenceBindings()
    {
        var calls = new List<string>();
        var messages = new List<string>();
        var controller = NewController(calls, messages: messages);

        Execute(ClientCommandId.ClearChat, "all");
        Execute(ClientCommandId.SaveUi, "hunt");
        Execute(ClientCommandId.LoadUi, "hunt");
        Execute(ClientCommandId.SaveAutoUi, string.Empty);
        Execute(ClientCommandId.LoadAutoUi, string.Empty);
        Execute(ClientCommandId.SaveUi, "this-name-is-over-16-characters");

        Assert.Equal(
            ["clear:True", "saveui:hunt", "loadui:hunt", "saveautoui", "loadautoui"],
            calls);
        Assert.Equal(["The file name must be 16 characters or less."], messages);

        void Execute(ClientCommandId id, string arguments) =>
            controller.Execute(new ExecuteClientCommandCmd(id, arguments));
    }

    [Fact]
    public void AwayCommands_RespectCurrentModeAndPackRetailMessageForm()
    {
        var calls = new List<string>();
        var messages = new List<string>();
        var controller = NewController(calls, messages: messages, isAway: false);

        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.Away, "on"));
        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.Away, "msg Stepped away"));

        Assert.Equal(["afk:True", "afkmsg:Stepped away\n"], calls);
        Assert.Equal(["New AFK message set: Stepped away\n"], messages);

        calls.Clear();
        controller = NewController(calls, isAway: true);
        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.Away, "off"));
        Assert.Equal(["afk:False"], calls);
    }

    [Fact]
    public void ConsentAndEmoteCommands_RouteTheirExactActions()
    {
        var calls = new List<string>();
        var controller = NewController(calls, acceptsLootPermits: false);

        Execute(ClientCommandId.Consent, "on");
        Execute(ClientCommandId.Consent, "who");
        Execute(ClientCommandId.Consent, "clear");
        Execute(ClientCommandId.Consent, "remove Alice Example");
        Execute(ClientCommandId.Emote, "waves happily");

        Assert.Equal(
            [
                "consentmode:True",
                "consentwho",
                "consentclear",
                "consentremove:Alice Example",
                "emote:waves happily",
            ],
            calls);

        void Execute(ClientCommandId id, string arguments) =>
            controller.Execute(new ExecuteClientCommandCmd(id, arguments));
    }

    [Fact]
    public void FriendsCommands_ReadAuthoritativeStateAndSendObjectId()
    {
        var friends = new FriendsState();
        friends.Apply(new FriendsUpdate(
            FriendsUpdateType.Full,
            [
                new FriendEntry(0x50000001u, "Alice", true, false, [], []),
                new FriendEntry(0x50000002u, "Bjørn", false, false, [], []),
            ]));
        var calls = new List<string>();
        var messages = new List<string>();
        var controller = NewController(calls, messages: messages, friends: friends);

        Execute(ClientCommandId.Friends, "online");
        Execute(ClientCommandId.FriendsRemove, "alice");
        Execute(ClientCommandId.FriendsAdd, "Cara");
        Execute(ClientCommandId.Friends, "old");

        Assert.Contains("Alice (Online)", messages.Single());
        Assert.DoesNotContain("Bjørn", messages.Single());
        Assert.Equal(
            ["friendremove:1342177281", "friendadd:Cara", "friendsold"],
            calls);

        void Execute(ClientCommandId id, string arguments) =>
            controller.Execute(new ExecuteClientCommandCmd(id, arguments));
    }

    [Fact]
    public void SquelchAndFilterCommands_ParseRetailOptions()
    {
        var calls = new List<string>();
        var controller = NewController(calls, lastTeller: "Recent Teller");

        Execute(ClientCommandId.Squelch, "-account Alice");
        Execute(ClientCommandId.Unsquelch, "-reply");
        Execute(ClientCommandId.Squelch, "-Magic Caster Name");
        Execute(ClientCommandId.Filter, "-Combat_Self");
        Execute(ClientCommandId.Unfilter, "-Tell");

        Assert.Equal(
            [
                "accountsquelch:True:Alice",
                "charsquelch:False:0:Recent Teller:1",
                "charsquelch:True:0:Caster Name:7",
                "globalsquelch:True:22",
                "globalsquelch:False:3",
            ],
            calls);

        void Execute(ClientCommandId id, string arguments) =>
            controller.Execute(new ExecuteClientCommandCmd(id, arguments));
    }

    [Fact]
    public void FillComponents_ClearWorksWithoutVendorAndBuyingRequiresOne()
    {
        var calls = new List<string>();
        var messages = new List<string>();
        var controller = NewController(calls, messages: messages, vendorOpen: false);

        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.FillComponents, "clear"));
        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.FillComponents, "scarabs 500"));

        Assert.Equal(["clearcomps"], calls);
        Assert.Equal(["Component list cleared.", "You need an open vendor."], messages);
    }


    [Fact]
    public void HouseAbandon_BothStagesAccepted_ShowsBothPromptsThenSendsExactlyOnce()
    {
        var calls = new List<string>();
        var controller = NewController(calls);

        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.HouseAbandon, ""));

        Assert.Equal(
            [
                "confirm:Do you really want to abandon your house? Any items in the house (on hooks or in storage) will stay with the house, and you will lose access to them.",
                "confirm:Are you absolutely certain you wish to abandon your house? Click yes only if you are sure!",
                "houseabandon",
            ],
            calls);
    }

    [Fact]
    public void HouseAbandon_DeclineFirstStage_ShowsOnlyOnePromptAndNeverSends()
    {
        var calls = new List<string>();
        var controller = NewController(calls, confirmationResponses: new Queue<bool>([false]));

        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.HouseAbandon, ""));

        Assert.Equal(
            [
                "confirm:Do you really want to abandon your house? Any items in the house (on hooks or in storage) will stay with the house, and you will lose access to them.",
            ],
            calls);
        Assert.DoesNotContain("houseabandon", calls);
    }

    [Fact]
    public void HouseAbandon_DeclineSecondStage_ShowsBothPromptsAndNeverSends()
    {
        var calls = new List<string>();
        var controller = NewController(calls, confirmationResponses: new Queue<bool>([true, false]));

        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.HouseAbandon, ""));

        Assert.Equal(
            [
                "confirm:Do you really want to abandon your house? Any items in the house (on hooks or in storage) will stay with the house, and you will lose access to them.",
                "confirm:Are you absolutely certain you wish to abandon your house? Click yes only if you are sure!",
            ],
            calls);
        Assert.DoesNotContain("houseabandon", calls);
    }


    [Fact]
    public void Permit_MultiWordName_JoinsTheRemainderIntoOneName()
    {
        var calls = new List<string>();
        var controller = NewController(calls);

        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.Permit, "add Aunt Agatha"));
        controller.Execute(new ExecuteClientCommandCmd(ClientCommandId.Permit, "remove Lord Gnarly Beard"));

        Assert.Equal(
            ["permitadd:Aunt Agatha", "permitremove:Lord Gnarly Beard"],
            calls);
    }


    [Fact]
    public void ChannelArgumentCommands_UnknownTag_ShowsWeenieError422WithoutSending()
    {
        var calls = new List<string>();
        var errors = new List<uint>();
        var controller = NewController(calls, errors);

        Execute(ClientCommandId.ListChannel, "nonsense");
        Execute(ClientCommandId.OnChannel, "nonsense");
        Execute(ClientCommandId.OffChannel, "nonsense");

        Assert.Empty(calls);
        Assert.Equal([0x0422u, 0x0422u, 0x0422u], errors);

        void Execute(ClientCommandId id, string arguments) =>
            controller.Execute(new ExecuteClientCommandCmd(id, arguments));
    }

    [Theory]
    [InlineData(ClientCommandId.ListChannel, "fellowship", "clist:2048")]
    [InlineData(ClientCommandId.OnChannel, "admin", "on:2")]
    [InlineData(ClientCommandId.OffChannel, "sentinel", "off:512")]
    [InlineData(ClientCommandId.ListChannel, "allegiance", "clist:33554432")]
    public void ChannelArgumentCommands_KnownTag_SendsWithoutError(
        ClientCommandId id, string tag, string expectedCall)
    {
        var calls = new List<string>();
        var errors = new List<uint>();
        var controller = NewController(calls, errors);

        controller.Execute(new ExecuteClientCommandCmd(id, tag));

        Assert.Empty(errors);
        Assert.Equal([expectedCall], calls);
    }

    [Fact]
    public void UnknownCommandId_FailsAtApplicationBoundary()
    {
        var controller = NewController();
        var command = new ExecuteClientCommandCmd((ClientCommandId)999, string.Empty);

        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Execute(command));
    }


    [Fact]
    public void Log_WithAName_ReportsWhereChatIsGoingAndHowToStop()
    {
        var messages = new List<string>();
        var calls = new List<string>();
        ClientCommandController ctrl = NewController(
            calls, messages: messages,
            chatLog: name => new ChatLogResult(true, false, name, null));

        ctrl.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.ChatLogFile, "aclog.txt"));

        Assert.Contains("log:aclog.txt", calls);
        Assert.Equal(
            "Copying chat to aclog.txt.  Run command again with no arguments "
            + "to turn off logging.",
            Assert.Single(messages));
    }

    [Fact]
    public void Log_WhenTheFileCannotBeOpened_SaysSoRatherThanClaimingSuccess()
    {
        var messages = new List<string>();
        ClientCommandController ctrl = NewController(
            messages: messages,
            chatLog: name => new ChatLogResult(false, false, name, null));

        ctrl.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.ChatLogFile, "C:/nope/x.txt"));

        Assert.Equal(
            "Failed to redirect to file C:/nope/x.txt!",
            Assert.Single(messages));
    }

    [Fact]
    public void Log_WithNoArgument_ClosesTheOpenLogAndSaysBothLines()
    {
        var messages = new List<string>();
        ClientCommandController ctrl = NewController(
            messages: messages,
            chatLog: _ => new ChatLogResult(false, true, string.Empty, "aclog.txt"));

        ctrl.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.ChatLogFile, ""));

        Assert.Equal(
            ["Chat log aclog.txt closed.", "Chat output now directed only to the screen."],
            messages);
    }

    [Fact]
    public void Log_WithNoArgumentAndNothingOpen_AsksForAFileName()
    {
        var messages = new List<string>();
        ClientCommandController ctrl = NewController(
            messages: messages,
            chatLog: _ => new ChatLogResult(false, false, string.Empty, null));

        ctrl.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.ChatLogFile, ""));

        Assert.Equal(
            "Please specify a file to append chat messages to.",
            Assert.Single(messages));
    }

    [Fact]
    public void Log_StartingASecondLogAnnouncesThatTheFirstEnded()
    {
        var messages = new List<string>();
        ClientCommandController ctrl = NewController(
            messages: messages,
            chatLog: name => new ChatLogResult(true, true, name, "old.txt"));

        ctrl.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.ChatLogFile, "new.txt"));

        Assert.Equal("Chat log old.txt closed.", messages[0]);
        Assert.StartsWith("Copying chat to new.txt.", messages[1]);
    }

    [Fact]
    public void Log_TakesTheWholeRemainderSoASpacedNameSurvives()
    {
        var calls = new List<string>();
        ClientCommandController ctrl = NewController(
            calls,
            chatLog: name => new ChatLogResult(true, false, name, null));

        ctrl.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.ChatLogFile, "my chat log.txt"));

        Assert.Contains("log:my chat log.txt", calls);
    }

    [Fact]
    public void Log_ResolvesFromTheCatalogWithItsWholeRemainderAsTheArgument()
    {
        Assert.True(RetailClientCommandCatalog.TryMatch(
            "/log my chat log.txt", out RetailClientCommandCatalog.Match match));

        Assert.Equal(ClientCommandId.ChatLogFile, match.Command);
        Assert.Equal("my chat log.txt", match.Arguments);
    }


    [Fact]
    public void Day_TogglesThePersistentOptionAndPrintsRetailsExactLines()
    {
        bool persistentDaylight = false;
        var values = new List<bool>();
        var messages = new List<string>();
        ClientCommandController controller = NewController(
            messages: messages,
            isPersistentDaylight: () => persistentDaylight,
            setPersistentDaylight: value =>
            {
                persistentDaylight = value;
                values.Add(value);
            });

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.TogglePersistentDaylight,
            "ignored exactly like retail"));
        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.TogglePersistentDaylight,
            string.Empty));

        Assert.Equal([true, false], values);
        Assert.Equal(
            ["Let there be light!", "Normality has been restored."],
            messages);
    }

    [Theory]
    [InlineData("radius 5", 5)]
    [InlineData("RADIUS 25 extra ignored", 25)]
    [InlineData("radius 12suffix", 12)]
    public void RenderRadius_AcceptsRetailRangeAndAtoiPrefix(
        string arguments,
        int expected)
    {
        var radii = new List<int>();
        var messages = new List<string>();
        ClientCommandController controller = NewController(
            messages: messages,
            setLandscapeRadius: radii.Add);

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.RenderOption,
            arguments));

        Assert.Equal([expected], radii);
        Assert.Equal(["Landscape radius set"], messages);
    }

    [Theory]
    [InlineData("fov 10", 10f)]
    [InlineData("FOV 160 extra", 160f)]
    [InlineData("fov 91degrees", 91f)]
    public void RenderFov_AcceptsRetailRangeAndAtoiPrefix(
        string arguments,
        float expected)
    {
        var values = new List<float>();
        var messages = new List<string>();
        ClientCommandController controller = NewController(
            messages: messages,
            setFieldOfView: values.Add);

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.RenderOption,
            arguments));

        Assert.Equal([expected], values);
        Assert.Equal(["Field of view set"], messages);
    }

    [Theory]
    [InlineData("radius", "Must specify a radius")]
    [InlineData("radius 4", "Radius must be between 5 and 25")]
    [InlineData("radius nope", "Radius must be between 5 and 25")]
    [InlineData("fov", "Must specify a field of view")]
    [InlineData("fov 161", "Field of view must be between 10 and 160")]
    public void Render_InvalidValuesPrintRetailsExactReply(
        string arguments,
        string expected)
    {
        var calls = new List<string>();
        var messages = new List<string>();
        ClientCommandController controller = NewController(
            calls,
            messages: messages);

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.RenderOption,
            arguments));

        Assert.DoesNotContain(calls, call =>
            call.StartsWith("radius:", StringComparison.Ordinal)
            || call.StartsWith("fov:", StringComparison.Ordinal));
        Assert.Equal([expected], messages);
    }

    [Fact]
    public void Render_UsageAndUnknownOptionMatchRetail()
    {
        var messages = new List<string>();
        ClientCommandController controller = NewController(messages: messages);

        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.RenderOption,
            string.Empty));
        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.RenderOption,
            "usage"));
        controller.Execute(new ExecuteClientCommandCmd(
            ClientCommandId.RenderOption,
            "unknown 1"));

        string usage = RetailCommandHelpTable.Render.TrimEnd('\n');
        Assert.Equal([usage, usage], messages);
    }

    [Fact]
    public void AllegianceAdministration_ExecutesEveryRetailDispatcherBranch()
    {
        var calls = new List<string>();
        var system = new List<string>();
        var clientLocal = new List<string>();
        ClientCommandController controller = NewController(
            calls: calls,
            messages: system,
            clientLocalMessages: clientLocal);

        Execute(ClientCommandId.AllegianceInfo, "Lord Bob");
        Execute(ClientCommandId.AllegianceBoot, "Lord Bob");
        Execute(ClientCommandId.AllegianceBoot, "-account Account Bob");
        Execute(ClientCommandId.AllegianceBoot, "-account");
        Execute(ClientCommandId.AllegianceBan, "list ignored");
        Execute(ClientCommandId.AllegianceBan, "add Lord Bob");
        Execute(ClientCommandId.AllegianceBan, "remove Lord Bob");
        Execute(ClientCommandId.AllegianceChat, "on");
        Execute(ClientCommandId.AllegianceChat, "off");
        Execute(ClientCommandId.AllegianceChat, "kick Bob");
        Execute(ClientCommandId.AllegianceChat, "kick Bob, Bad manners");
        Execute(ClientCommandId.AllegianceChat, "kick ");
        Execute(ClientCommandId.AllegianceChat, "gag Lord Bob");
        Execute(ClientCommandId.AllegianceChat, "ungag Lord Bob");
        Execute(ClientCommandId.AllegianceBroadcast, "Hear ye");
        Execute(ClientCommandId.AllegianceOfficer, "");
        Execute(ClientCommandId.AllegianceOfficer, "clear ignored");
        Execute(ClientCommandId.AllegianceOfficer, "remove Lord Bob");
        Execute(ClientCommandId.AllegianceOfficer, "add 0x2 Lord Bob");
        Execute(ClientCommandId.AllegianceOfficer, "set 03 Aunt Alice");
        Execute(ClientCommandId.AllegianceOfficerTitle, "");
        Execute(ClientCommandId.AllegianceOfficerTitle, "clear ignored");
        Execute(ClientCommandId.AllegianceOfficerTitle, "set 0x2 High Regent");
        Execute(ClientCommandId.AllegianceOfficerTitle, "set 1");
        Execute(ClientCommandId.AllegianceName, "");
        Execute(ClientCommandId.AllegianceName, "set The Best Allegiance");
        Execute(ClientCommandId.AllegianceName, "set");
        Execute(ClientCommandId.AllegianceName, "clear ignored");
        Execute(ClientCommandId.AllegianceLock, "");
        Execute(ClientCommandId.AllegianceLock, "off");
        Execute(ClientCommandId.AllegianceLock, "on");
        Execute(ClientCommandId.AllegianceLock, "toggle");
        Execute(ClientCommandId.AllegianceLock, "check");
        Execute(ClientCommandId.AllegianceLock, "bypass");
        Execute(ClientCommandId.AllegianceLock, "bypass clear");
        Execute(ClientCommandId.AllegianceLock, "bypass Lord Bob");
        Execute(ClientCommandId.AllegianceHouse, "");
        Execute(ClientCommandId.AllegianceHouse, "guest open");
        Execute(ClientCommandId.AllegianceHouse, "guest close");
        Execute(ClientCommandId.AllegianceHouse, "storage open");
        Execute(ClientCommandId.AllegianceHouse, "storage close");
        Execute(ClientCommandId.AllegianceMotd, "");
        Execute(ClientCommandId.AllegianceMotd, "set Welcome everyone");
        Execute(ClientCommandId.AllegianceMotd, "set");
        Execute(ClientCommandId.AllegianceMotd, "clear ignored");

        Assert.Equal(
            [
                "alleginfo:Lord Bob",
                "allegboot:Lord Bob:False",
                "allegboot:Account Bob:True",
                "allegboot::True",
                "allegban:list",
                "allegban:add:Lord Bob",
                "allegban:remove:Lord Bob",
                "charoption:27:True",
                "charoption:27:False",
                "allegchatboot:Bob:No reason given.",
                "allegchatboot:Bob:Bad manners",
                "allegchatboot::No reason given.",
                "allegchatgag:Lord Bob:True",
                "allegchatgag:Lord Bob:False",
                "allegbroadcast:Hear ye",
                "allegofficer:list",
                "allegofficer:clear",
                "allegofficer:remove:Lord Bob",
                "allegofficer:set:2:Lord Bob",
                "allegofficer:set:3:Aunt Alice",
                "allegtitle:list",
                "allegtitle:clear",
                "allegtitle:set:2:High Regent",
                "allegtitle:set:1:",
                "allegname:query",
                "allegname:set:The Best Allegiance",
                "allegname:set:",
                "allegname:clear",
                "alleglock:4",
                "alleglock:1",
                "alleglock:2",
                "alleglock:3",
                "alleglock:4",
                "alleglock:5",
                "alleglock:6",
                "alleglock:bypass:Lord Bob",
                "alleghouse:1",
                "alleghouse:2",
                "alleghouse:3",
                "alleghouse:4",
                "alleghouse:5",
                "motd:query",
                "motd:set:Welcome everyone",
                "motd:set:",
                "motd:clear",
            ],
            calls);
        Assert.Equal(
            [
                "Attempting to boot Lord Bob...",
                "Attempting to boot Account Bob (Account)...",
                "Attempting to boot  (Account)...",
            ],
            system);
        Assert.Empty(clientLocal);

        void Execute(ClientCommandId command, string arguments) =>
            controller.Execute(new ExecuteClientCommandCmd(command, arguments));
    }

    [Fact]
    public void HouseAdministration_ExecutesEveryRetailDispatcherBranch()
    {
        var calls = new List<string>();
        ClientCommandController controller = NewController(calls: calls);

        Execute(ClientCommandId.HouseOpenStatus, "open");
        Execute(ClientCommandId.HouseOpenStatus, "close");
        Execute(ClientCommandId.HouseGuests, "add Lord Bob");
        Execute(ClientCommandId.HouseGuests, "remove Lord Bob");
        Execute(ClientCommandId.HouseGuests, "remove_all ignored");
        Execute(ClientCommandId.HouseGuests, "list ignored");
        Execute(ClientCommandId.HouseGuests, "show ignored");
        Execute(ClientCommandId.HouseGuests, "add_allegiance ignored");
        Execute(ClientCommandId.HouseGuests, "remove_allegiance ignored");
        Execute(ClientCommandId.HouseStorage, "add Lord Bob");
        Execute(ClientCommandId.HouseStorage, "remove Lord Bob");
        Execute(ClientCommandId.HouseStorage, "add -all");
        Execute(ClientCommandId.HouseStorage, "remove -all");
        Execute(ClientCommandId.HouseStorage, "remove_all ignored");
        Execute(ClientCommandId.HouseStorage, "list ignored");
        Execute(ClientCommandId.HouseStorage, "show ignored");
        Execute(ClientCommandId.HouseStorage, "add_allegiance ignored");
        Execute(ClientCommandId.HouseStorage, "remove_allegiance ignored");
        Execute(ClientCommandId.HouseBoot, "Lord Bob");
        Execute(ClientCommandId.HouseBoot, "-all");
        Execute(ClientCommandId.HouseBootAll, "ignored");
        Execute(ClientCommandId.HouseHooks, "on ignored");
        Execute(ClientCommandId.HouseHooks, "off ignored");

        Assert.Equal(
            [
                "houseopen:True",
                "houseopen:False",
                "houseguest:add:Lord Bob",
                "houseguest:remove:Lord Bob",
                "houseguest:remove_all",
                "houseguest:list",
                "houseguest:list",
                "houseguest:allegiance:True",
                "houseguest:allegiance:False",
                "housestorage:True:Lord Bob",
                "housestorage:False:Lord Bob",
                "housestorage:add_all",
                "housestorage:remove_all",
                "housestorage:remove_all",
                "houseguest:list",
                "houseguest:list",
                "housestorage:allegiance:True",
                "housestorage:allegiance:False",
                "houseboot:Lord Bob",
                "houseboot:all",
                "houseboot:all",
                "househooks:True",
                "househooks:False",
            ],
            calls);

        void Execute(ClientCommandId command, string arguments) =>
            controller.Execute(new ExecuteClientCommandCmd(command, arguments));
    }

    [Theory]
    [InlineData(ClientCommandId.AllegianceInfo, "", "Please specify an actual name.")]
    [InlineData(ClientCommandId.AllegianceBoot, "", "Please specify an actual name.")]
    [InlineData(ClientCommandId.AllegianceBan, "add", "Please specify an actual name.")]
    [InlineData(ClientCommandId.AllegianceChat, "gag", "Please specify an actual name.")]
    [InlineData(ClientCommandId.AllegianceBroadcast, "", "Please see @help Allegiance for more information on how to use this command.")]
    [InlineData(ClientCommandId.AllegianceOfficer, "remove", "Please specify the name of an allegiance member.")]
    [InlineData(ClientCommandId.AllegianceOfficer, "add nope Bob", "Please specify a valid officer level as a number between 1 and 3. Check the game help files for more information on officer levels.")]
    [InlineData(ClientCommandId.AllegianceOfficer, "add 2", "Please specify the name of an allegiance member.")]
    [InlineData(ClientCommandId.AllegianceOfficerTitle, "set 4 Regent", "Please specify a valid officer level as a number between 1 and 3.")]
    [InlineData(ClientCommandId.AllegianceName, "nope", "Please see @help Allegiance for more information on how to use this command.")]
    [InlineData(ClientCommandId.AllegianceLock, "nope", "Please see @help Allegiance for more information on how to use this command.")]
    [InlineData(ClientCommandId.AllegianceHouse, "guest nope", "Please see @help Allegiance for more information on how to use this command.")]
    [InlineData(ClientCommandId.AllegianceMotd, "nope", "Please see @help Allegiance for more information on how to use this command.")]
    [InlineData(ClientCommandId.AllegianceUnrecognizedSubcommand, "nope", "Please see @help Allegiance for more information on how to use this command.")]
    [InlineData(ClientCommandId.HouseGuests, "add", "Please specify the guest's name.")]
    [InlineData(ClientCommandId.HouseStorage, "add", "Please specify an actual name.")]
    [InlineData(ClientCommandId.HouseBoot, "", "Please see @help House for more information on how to use this command.")]
    [InlineData(ClientCommandId.HouseHooks, "maybe", "Please see @help House for more information on how to use this command.")]
    [InlineData(ClientCommandId.HouseUnrecognizedSubcommand, "nope", "Please see @help House for more information on how to use this command.")]
    public void AdministrationInvalidForms_EmitExactRetailClientLocalText(
        ClientCommandId command,
        string arguments,
        string expected)
    {
        var calls = new List<string>();
        var system = new List<string>();
        var clientLocal = new List<string>();
        ClientCommandController controller = NewController(
            calls: calls,
            messages: system,
            clientLocalMessages: clientLocal);

        controller.Execute(new ExecuteClientCommandCmd(command, arguments));

        Assert.Empty(calls);
        Assert.Empty(system);
        Assert.Equal([expected], clientLocal);
    }

    private static ClientCommandController NewController(
        List<string>? calls = null,
        List<uint>? errors = null,
        uint? playerBitfield = 0x02000028u,
        List<string>? messages = null,
        List<string>? clientLocalMessages = null,
        bool isAway = false,
        bool acceptsLootPermits = true,
        FriendsState? friends = null,
        SquelchState? squelch = null,
        string? lastTeller = null,
        bool vendorOpen = false,
        Queue<bool>? confirmationResponses = null,
        Func<string, AcDream.Core.Chat.ChatLogResult>? chatLog = null,
        Func<bool>? isPersistentDaylight = null,
        Action<bool>? setPersistentDaylight = null,
        Action<int>? setLandscapeRadius = null,
        Action<float>? setFieldOfView = null)
    {
        calls ??= [];
        errors ??= [];
        messages ??= [];
        clientLocalMessages ??= messages;
        return new ClientCommandController(new ClientCommandController.Bindings(
            () => calls.Add("ls"),
            () => calls.Add("mp"),
            () => calls.Add("pka"),
            () => calls.Add("pla"),
            () => calls.Add("house"),
            () => calls.Add("mansion"),
            () => calls.Add("age"),
            () => calls.Add("birth"),
            () => calls.Add("fps"),
            () => calls.Add("lock"),
            messages.Add,
            clientLocalMessages.Add,
            errors.Add,
            () => playerBitfield,
            () => "1.2.3",
            () => new Position(
                0xA9B40001u,
                new CellFrame(new System.Numerics.Vector3(1f, 2f, 3f),
                    System.Numerics.Quaternion.Identity)),
            () => null,
            (message, completed) =>
            {
                calls.Add($"confirm:{message}");
                bool accepted = confirmationResponses is { Count: > 0 }
                    ? confirmationResponses.Dequeue()
                    : true;
                completed(accepted);
            },
            () => calls.Add("suicide"),
            all => calls.Add("clear:" + all),
            name =>
            {
                calls.Add("log:" + name);
                return chatLog?.Invoke(name)
                    ?? new AcDream.Core.Chat.ChatLogResult(
                        Opened: name.Length > 0,
                        Closed: name.Length == 0,
                        Name: name,
                        ClosedName: "old.txt");
            },
            name => calls.Add("saveui:" + name),
            name => calls.Add("loadui:" + name),
            () => calls.Add("saveautoui"),
            () => calls.Add("loadautoui"),
            () => isAway,
            away => calls.Add("afk:" + away),
            message => calls.Add("afkmsg:" + message),
            () => acceptsLootPermits,
            enabled => calls.Add("consentmode:" + enabled),
            () => calls.Add("consentwho"),
            () => calls.Add("consentclear"),
            name => calls.Add("consentremove:" + name),
            message => calls.Add("emote:" + message),
            friends ?? new FriendsState(),
            name => calls.Add("friendadd:" + name),
            id => calls.Add("friendremove:" + id),
            () => calls.Add("friendsclear"),
            () => calls.Add("friendsold"),
            squelch ?? new SquelchState(),
            (add, id, name, type) => calls.Add($"charsquelch:{add}:{id}:{name}:{type}"),
            (add, name) => calls.Add($"accountsquelch:{add}:{name}"),
            (add, type) => calls.Add($"globalsquelch:{add}:{type}"),
            () => lastTeller,
            () => calls.Add("clearcomps"),
            () => vendorOpen,
            (category, price) => calls.Add($"fillcomps:{category}:{price}"),
            () => calls.Add("pklite"),
            () => false,
            title => calls.Add("title:" + title),
            (optionId, value) => calls.Add($"charoption:{optionId}:{value}"),
            name => calls.Add("permitadd:" + name),
            name => calls.Add("permitremove:" + name),
            houseType => calls.Add("hslist:" + houseType),
            () => calls.Add("index"),
            channelId => calls.Add("clist:" + channelId),
            channelId => calls.Add("on:" + channelId),
            channelId => calls.Add("off:" + channelId),
            () => calls.Add("alh"),
            name => calls.Add("alleginfo:" + name),
            () => calls.Add("houseabandon"),
            NewAdministrationBindings(calls),
            isPersistentDaylight ?? (() => false),
            setPersistentDaylight ?? (value => calls.Add("day:" + value)),
            setLandscapeRadius ?? (value => calls.Add("radius:" + value)),
            setFieldOfView ?? (value => calls.Add(
                "fov:" + value.ToString(CultureInfo.InvariantCulture)))));
    }

    private static ClientCommandController.AdministrationBindings
        NewAdministrationBindings(List<string> calls) => new(
            BreakAllegianceBoot: (name, account) =>
                calls.Add($"allegboot:{name}:{account}"),
            AllegianceChatBoot: (name, reason) =>
                calls.Add($"allegchatboot:{name}:{reason}"),
            AllegianceChatGag: (name, enabled) =>
                calls.Add($"allegchatgag:{name}:{enabled}"),
            AllegianceBroadcast: text => calls.Add("allegbroadcast:" + text),
            ListAllegianceBans: () => calls.Add("allegban:list"),
            AddAllegianceBan: name => calls.Add("allegban:add:" + name),
            RemoveAllegianceBan: name => calls.Add("allegban:remove:" + name),
            ListAllegianceOfficers: () => calls.Add("allegofficer:list"),
            ClearAllegianceOfficers: () => calls.Add("allegofficer:clear"),
            SetAllegianceOfficer: (name, level) =>
                calls.Add($"allegofficer:set:{level}:{name}"),
            RemoveAllegianceOfficer: name =>
                calls.Add("allegofficer:remove:" + name),
            ListAllegianceOfficerTitles: () => calls.Add("allegtitle:list"),
            ClearAllegianceOfficerTitles: () => calls.Add("allegtitle:clear"),
            SetAllegianceOfficerTitle: (level, title) =>
                calls.Add($"allegtitle:set:{level}:{title}"),
            QueryAllegianceName: () => calls.Add("allegname:query"),
            SetAllegianceName: name => calls.Add("allegname:set:" + name),
            ClearAllegianceName: () => calls.Add("allegname:clear"),
            AllegianceLockAction: action => calls.Add("alleglock:" + action),
            SetAllegianceApprovedVassal: name =>
                calls.Add("alleglock:bypass:" + name),
            AllegianceHouseAction: action => calls.Add("alleghouse:" + action),
            QueryMotd: () => calls.Add("motd:query"),
            SetMotd: text => calls.Add("motd:set:" + text),
            ClearMotd: () => calls.Add("motd:clear"),
            SetOpenHouseStatus: open => calls.Add("houseopen:" + open),
            AddPermanentGuest: name => calls.Add("houseguest:add:" + name),
            RemovePermanentGuest: name => calls.Add("houseguest:remove:" + name),
            RemoveAllPermanentGuests: () => calls.Add("houseguest:remove_all"),
            ChangeStoragePermission: (name, enabled) =>
                calls.Add($"housestorage:{enabled}:{name}"),
            AddAllStoragePermission: () => calls.Add("housestorage:add_all"),
            RemoveAllStoragePermission: () => calls.Add("housestorage:remove_all"),
            RequestFullGuestList: () => calls.Add("houseguest:list"),
            BootSpecificHouseGuest: name => calls.Add("houseboot:" + name),
            BootEveryone: () => calls.Add("houseboot:all"),
            SetHooksVisibility: visible => calls.Add("househooks:" + visible),
            ModifyAllegianceGuestPermission: enabled =>
                calls.Add("houseguest:allegiance:" + enabled),
            ModifyAllegianceStoragePermission: enabled =>
                calls.Add("housestorage:allegiance:" + enabled));
}
