using AcDream.UI.Abstractions.Panels.Chat;
using Xunit;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

public sealed class RetailCommandHelpTableTests
{
    [Fact]
    public void DeathGroup_IsCompleteVerbatimListing_NoLeadingSummaryLine()
    {
        Assert.Equal(
            "@permit - Commands to give or revoke permission for others to loot your corpse.\n"
            + "@consent - Commands to help you manage the corpse-looting permissions that others give you.\n"
            + "@corpse - Displays the location of your last outdoor death.\n"
            + "@die - Kills your character and leaves a corpse, returning you to your lifestone.\n"
            + "@lifestone - Returns you to the last lifestone you used without killing you.\n"
            + "@marketplace - Teleports you to the Marketplace of Dereth.\n"
            + "@pkarena - Teleports you to the PK Arena. You must be PK to use this command.\n"
            + "@pklarena - Teleports you to the PKL Arena. You must be PKL to use this command.\n",
            RetailCommandHelpTable.DeathGroupDetail);

        Assert.True(RetailCommandHelpTable.TryGetHelpText("death", out string viaLookup));
        Assert.Equal(RetailCommandHelpTable.DeathGroupDetail, viaLookup);
        Assert.DoesNotContain("has not yet extracted", viaLookup);
    }

    [Fact]
    public void StatusGroup_IsCompleteVerbatimListing_PreservesRetailTrailingSpace()
    {
        Assert.Equal(
            "@age - Displays your total gameplay time.\n"
            + "@birth - Displays when your character was created.\n"
            + "@day - A toggle that lightens the outdoor landscape. Note that this command may take several seconds to take effect. \n"
            + "@endurance - Explains how endurance affects your character.\n"
            + "@framerate - Toggles the framerate display.\n"
            + "@loc - Displays your current position.\n"
            + "@pklite - Sets your status to Player Killer Lite. Type @help pklite for more details.\n"
            + "@version - Tells you what version of the software you are using.\n",
            RetailCommandHelpTable.StatusGroupDetail);
    }

    [Fact]
    public void TextGroup_IsCompleteVerbatimListing()
    {
        Assert.Equal(
            "@clear - Clears the chat box of all text.\n"
            + "@filter - Commands to filter out incoming messages.\n"
            + "@unfilter - Commands to remove filters from incoming messages.\n"
            + "@loadfile - Reads in the given text file and executes each line in the chat entry field.\n"
            + "@log - Commands to echo chat text to a logfile.\n"
            + "@title <new title> - Sets the title of the popup chat window.\n",
            RetailCommandHelpTable.TextGroupDetail);
    }

    [Fact]
    public void AllegiancesGroup_IsCompleteVerbatimListing_TwoLines()
    {
        Assert.Equal(
            "@allegiance - Commands to help manage your allegiance.\n"
            + "@allegiance motd - Displays or sets the message of the day for your allegiance, see @help motd for more information.\n",
            RetailCommandHelpTable.AllegiancesGroupDetail);
    }


    [Fact]
    public void ChannelsGroup_IsCompleteVerbatimListing_SixBroadcastLines()
    {
        Assert.Equal(
            "@a - Sends a broadcast to your Allegiance.\n"
            + "@c - Sends a broadcast to your Co-vassals.\n"
            + "@m - Sends a broadcast to your Monarch.\n"
            + "@p - Sends a broadcast to your Patron.\n"
            + "@v - Sends a broadcast to your Vassals.\n"
            + "@f - Sends a broadcast to your Fellowship.\n",
            RetailCommandHelpTable.ChannelsGroupDetail);

        Assert.True(RetailCommandHelpTable.TryGetHelpText("channels", out string viaLookup));
        Assert.Equal(RetailCommandHelpTable.ChannelsGroupDetail, viaLookup);
        Assert.DoesNotContain("UNVERIFIED", viaLookup);
        Assert.DoesNotContain("not yet extracted", viaLookup);
    }

    [Fact]
    public void ChattingGroup_IsCompleteVerbatimListing_ThirteenLines()
    {
        Assert.Equal(
            "@chat - Sets whether or not you receive normal chat.\n"
            + "@notell - Sets whether or not you receive @tell's.\n"
            + "@reply - Sends some text to the last person who @tell'd you.\n"
            + "@pr - Sends some text to the last person who @p'd you.\n"
            + "@mr - Sends some text to the last person who @m'd you.\n"
            + "@retell - Sends some text to the last person you @tell'd.\n"
            + "@say - Says some text to everyone around you."
            + "@tell - Sends a private message to another character.\n"
            + "@a - Sends a broadcast to your Allegiance.\n"
            + "@c - Sends a broadcast to your Co-vassals.\n"
            + "@m - Sends a broadcast to your Monarch.\n"
            + "@p - Sends a broadcast to your Patron.\n"
            + "@v - Sends a broadcast to your Vassals.\n"
            + "@f - Sends a broadcast to your Fellowship.\n"
            + "@afk - Set your away-from-keyboard status.\n",
            RetailCommandHelpTable.ChattingGroupDetail);

        Assert.True(RetailCommandHelpTable.TryGetHelpText("chatting", out string viaLookup));
        Assert.Equal(RetailCommandHelpTable.ChattingGroupDetail, viaLookup);
        Assert.DoesNotContain("UNVERIFIED", viaLookup);
        Assert.DoesNotContain("not yet extracted verbatim", viaLookup);
    }

    [Fact]
    public void CommandsGroup_IsCompleteVerbatimListing_ConcatenatesEveryOtherGroup()
    {
        string expected =
            RetailCommandHelpTable.AllegiancesGroupDetail
            + RetailCommandHelpTable.ChannelsGroupDetail
            + RetailCommandHelpTable.ChattingGroupDetail
            + RetailCommandHelpTable.DeathGroupDetail
            + "@emote - Performs a text emote.\n@emotes - Lists all standard emotes.\n"
            + "@fillcomps - Helps you buy components in bulk.\n"
            + "@saveui <filename> - Saves the current user interface.\n"
            + "@loadui <filename> - Loads a previously saved user interface.\n"
            + "@saveui <filename> - Saves the current user interface.\n"
            + "@loadui <filename> - Loads a previously saved user interface.\n"
            + "@lockui - Toggles the locked state of the UI layout.\n"
            + "@friends - Helps you manage your friends list.\n"
            + "@house - Commands that help you manage your house, including guest and storage management.\n"
            + "@squelch - Squelches a character or account.\n"
            + "@unsquelch - Unsquelches a squelched character or account.\n"
            + "@messagetypes - Lists all types of messages that can be squelched or filtered.\n"
            + RetailCommandHelpTable.StatusGroupDetail
            + RetailCommandHelpTable.TextGroupDetail;

        Assert.Equal(RetailCommandHelpTable.CommandsGroupDetail, expected);

        Assert.True(RetailCommandHelpTable.TryGetHelpText("commands", out string viaLookup));
        Assert.Equal(RetailCommandHelpTable.CommandsGroupDetail, viaLookup);
        Assert.DoesNotContain("UNVERIFIED", viaLookup);
        Assert.DoesNotContain("not yet extracted", viaLookup);
    }

    [Theory]
    [InlineData("a", "@a - Sends a message to your Allegiance. Also: @guild, @gu")]
    [InlineData("guild", "@a - Sends a message to your Allegiance. Also: @guild, @gu")]
    [InlineData("general", "@general - Sends a message to the global General chat channel. Also: @cg")]
    [InlineData("trade", "@trade - Sends a message to the global Trade chat channel. Also: @ct")]
    [InlineData("lfg", "@lfg - Sends a message to the global Looking For Group (LFG) chat channel. Also: @clfg")]
    [InlineData("roleplay", "@roleplay - Sends a message to the global Roleplay chat channel. Also: @crp")]
    [InlineData("society", "@society - Sends a message to the your Society chat channel. Also: @soc")]
    [InlineData("olthoi", "@olthoi - If you are an Olthoi, sends a message to the global Olthoi chat channel. Also: @o")]
    public void ChannelVerb_VerbatimOneLiner_MatchesExactly(string verb, string expected)
    {
        Assert.True(RetailCommandHelpTable.TryGetHelpText(verb, out string text));
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("f", "Sends text to your Fellowship channel.")]
    [InlineData("monarch", "Sends text to your Monarch.")]
    [InlineData("patron", "Sends text to your Patron.")]
    [InlineData("vassal", "Sends text to your Vassals.")]
    [InlineData("covassal", "Sends text to your Co-vassals.")]
    public void ChannelVerb_StillAcdreamSummary_UnresolvedChannelHackFamily(
        string verb, string expected)
    {
        Assert.True(RetailCommandHelpTable.TryGetHelpText(verb, out string text));
        Assert.Equal(expected, text);
    }


    [Fact]
    public void HelpPrefixNote_HasRetailsLeadingAndTrailingBlankLines()
    {
        Assert.Equal(
            "\nNote: You may substitute a forward slash (/) for the at symbol (@).\n\n",
            RetailCommandHelpTable.HelpPrefixNote);
    }

    [Fact]
    public void ForMoreInformationPrefix_KeepsRetailsUnsubstitutedPlaceholderVerbatim()
    {
        Assert.Equal(
            "For more information, type @help <command>.\n",
            RetailCommandHelpTable.ForMoreInformationPrefix);
    }

    [Fact]
    public void UnknownCommand_MatchesRetailsExactFallbackText()
    {
        Assert.Equal("Unknown command", RetailCommandHelpTable.UnknownCommand);
    }

    [Fact]
    public void AvailableHelpListing_MatchesDoHelpsCompleteThirteenItemSequence()
    {
        Assert.Equal(
            "Available help:\n"
            + "@help allegiances - Commands to help you deal with your Allegiance.\n"
            + "@help channels - How to communicate with people in your allegiance or fellowship.\n"
            + "@help chatting - How to chat publically and privately.\n"
            + "@help death - Commands for making, finding, and looting corpses.\n"
            + "@help emote - How to perform text and action emotes.\n"
            + "@help fillcomps - A command to help you buy components in bulk.\n"
            + "@help friends - Commands to help you manage your friends list.\n"
            + "@help house - Commands that help you manage your house, including guest and storage management.\n"
            + "@help squelch - Commands that let you block out messages from other players.\n"
            + "@help status - Commands that display useful information.\n"
            + "@help text - Commands that help you manage your text window.\n"
            + "@help commands - Lists all commands.\n",
            RetailCommandHelpTable.AvailableHelpListing);
    }

    [Fact]
    public void DeathGroup_ThroughRouter_PrintsRetailsCompleteTwoEntryShape()
    {
        var log = new AcDream.Core.Chat.ChatLog();
        var vm = new ChatVM(log, displayLimit: 50);
        var bus = new RecordingCommandBus();

        var outcome = ChatCommandRouter.Submit(
            "/help death", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var entries = log.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.Equal(RetailCommandHelpTable.HelpPrefixNote, entries[0].Text);
        Assert.Equal(
            RetailCommandHelpTable.ForMoreInformationPrefix
                + RetailCommandHelpTable.DeathGroupDetail,
            entries[1].Text);
    }


    [Fact]
    public void HelpDie_ThroughRouter_PrintsRetailsExactDetailText()
    {
        var log = new AcDream.Core.Chat.ChatLog();
        var vm = new ChatVM(log, displayLimit: 50);
        var bus = new RecordingCommandBus();

        var outcome = ChatCommandRouter.Submit(
            "/help die", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var entries = log.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.Equal(RetailCommandHelpTable.HelpPrefixNote, entries[0].Text);
        Assert.Equal(
            RetailCommandHelpTable.ForMoreInformationPrefix
                + RetailCommandHelpTable.DieDetail,
            entries[1].Text);
        Assert.Equal(
            "@die - If you wish to kill your character and leave a corpse, "
            + "you may use the @die command.  This will result in your "
            + "character's death, you will leave behind a corpse with some "
            + "of your items, and you will appear at your lifestone.  If "
            + "you wish to travel to your lifestone without leaving behind "
            + "a corpse, you may use the @lifestone command.\n",
            RetailCommandHelpTable.DieDetail);
    }

    [Fact]
    public void HelpLifestone_ThroughRouter_PrintsRetailsExactDetailText()
    {
        var log = new AcDream.Core.Chat.ChatLog();
        var vm = new ChatVM(log, displayLimit: 50);
        var bus = new RecordingCommandBus();

        var outcome = ChatCommandRouter.Submit(
            "/help lifestone", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var entries = log.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.Equal(RetailCommandHelpTable.HelpPrefixNote, entries[0].Text);
        Assert.Equal(
            RetailCommandHelpTable.ForMoreInformationPrefix
                + RetailCommandHelpTable.LifestoneDetail,
            entries[1].Text);
        Assert.Equal(
            "@lifestone - Returns you to the last lifestone you used without killing you.\n",
            RetailCommandHelpTable.LifestoneDetail);
    }

    [Fact]
    public void HelpAlh_RoutesToTheSameHelpAllegianceTextAsHelpAllegiance()
    {
        Assert.True(RetailCommandHelpTable.TryGetCatalogVerbDetailText(
            "alh", out string alhText));
        Assert.Equal(RetailCommandHelpTable.AllegianceOverview, alhText);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("clist")]
    [InlineData("on")]
    [InlineData("off")]
    public void ConfirmedNullHelpVerb_ThroughRouter_ShowsUnknownCommandNotCatalogSummary(
        string verb)
    {
        var log = new AcDream.Core.Chat.ChatLog();
        var vm = new ChatVM(log, displayLimit: 50);
        var bus = new RecordingCommandBus();

        var outcome = ChatCommandRouter.Submit(
            $"/help {verb}", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var entries = log.Snapshot();
        Assert.Single(entries);
        Assert.Equal(RetailCommandHelpTable.UnknownCommand, entries[0].Text);
        Assert.Equal(
            (uint)AcDream.Core.Chat.RetailLogTextType.Default,
            entries[0].LogTextType);
    }

    [Fact]
    public void HelpForLog_StillReturnsRetailsOwnTextNowThatItIsACatalogVerb()
    {
        Assert.True(
            RetailCommandHelpTable.TryGetCatalogVerbDetailText("log", out string detail));
        Assert.Equal(RetailCommandHelpTable.Log, detail);
        Assert.StartsWith("@log <name> - Echoes chat text to a logfile.", detail);
    }

    [Fact]
    public void CatalogLeafVerbCoverage_ExtractedVsConfirmedNullVsUnverified_MatchesConsolidatedReviewCount()
    {
        var seenSummaries = new HashSet<string>(StringComparer.Ordinal);
        int extractedCount = 0;
        int confirmedNullCount = 0;
        int unverifiedCount = 0;

        foreach (string verb in RetailClientCommandCatalog.KnownVerbs)
        {
            if (verb is "house" or "hou" or "allegiance" or "all")
                continue;

            Assert.True(
                RetailClientCommandCatalog.TryGetHelpText(verb, out string summary),
                $"catalog verb '{verb}' has no summary text");
            if (!seenSummaries.Add(summary))
                continue;

            bool confirmedNull = RetailCommandHelpTable.CatalogVerbsWithNoRetailHelp
                .Contains(verb);
            bool extracted = RetailCommandHelpTable.TryGetCatalogVerbDetailText(
                verb, out _);

            Assert.False(
                confirmedNull && extracted,
                $"'{verb}' is both confirmed-null and has an extracted Detail override");

            if (confirmedNull)
                confirmedNullCount++;
            else if (extracted)
                extractedCount++;
            else
                unverifiedCount++;
        }

        Assert.Equal(47, extractedCount);
        Assert.Equal(4, confirmedNullCount);
        Assert.Equal(0, unverifiedCount);
    }


    public static IEnumerable<object[]> AllUserVisibleStrings()
    {
        yield return new object[] { nameof(RetailCommandHelpTable.Day), RetailCommandHelpTable.Day };
        yield return new object[] { nameof(RetailCommandHelpTable.Log), RetailCommandHelpTable.Log };
        yield return new object[] { nameof(RetailCommandHelpTable.Render), RetailCommandHelpTable.Render };
        yield return new object[] { nameof(RetailCommandHelpTable.Motd), RetailCommandHelpTable.Motd };
        yield return new object[]
        {
            nameof(RetailCommandHelpTable.AllegianceOverview), RetailCommandHelpTable.AllegianceOverview,
        };
        yield return new object[]
        {
            nameof(RetailCommandHelpTable.HouseOverview), RetailCommandHelpTable.HouseOverview,
        };
        yield return new object[]
        {
            nameof(RetailCommandHelpTable.ChannelsGroupDetail), RetailCommandHelpTable.ChannelsGroupDetail,
        };
        yield return new object[]
        {
            nameof(RetailCommandHelpTable.ChattingGroupDetail), RetailCommandHelpTable.ChattingGroupDetail,
        };
        yield return new object[]
        {
            nameof(RetailCommandHelpTable.CommandsGroupDetail), RetailCommandHelpTable.CommandsGroupDetail,
        };
        yield return new object[]
        {
            nameof(RetailCommandHelpTable.MessageTypesDetail), RetailCommandHelpTable.MessageTypesDetail,
        };
        yield return new object[]
        {
            nameof(RetailCommandHelpTable.AvailableHelpListing), RetailCommandHelpTable.AvailableHelpListing,
        };
    }

    [Theory]
    [MemberData(nameof(AllUserVisibleStrings))]
    public void UserVisibleString_CarriesNoHonestyMarker(string _, string text)
    {
        Assert.DoesNotContain("IMPLEMENTED", text);
        Assert.DoesNotContain("UNVERIFIED", text);
        Assert.DoesNotContain("has not yet extracted", text);
        Assert.DoesNotContain("acdream has not", text);
        Assert.DoesNotContain("not yet extracted", text);
        Assert.DoesNotContain("NOT YET", text);
    }

    [Fact]
    public void AllegianceOverview_NoBracketAnnotations_MatchesPristineRetailData()
    {
        Assert.Contains("Also: @ab\n", RetailCommandHelpTable.AllegianceOverview);
        Assert.DoesNotContain("[IMPLEMENTED", RetailCommandHelpTable.AllegianceOverview);
        Assert.Contains(
            "@allegiance hometown - Recalls you to your allegiance bindstone, if your allegiance has tied to one.\n",
            RetailCommandHelpTable.AllegianceOverview);
        Assert.DoesNotContain("Subcommands NOT marked", RetailCommandHelpTable.AllegianceOverview);
    }

    [Fact]
    public void HouseOverview_NoBracketAnnotations_MatchesPristineRetailData()
    {
        Assert.Contains(
            "@house available - See @hslist\n",
            RetailCommandHelpTable.HouseOverview);
        Assert.Contains(
            "@house recall - Teleports you to your house.\n",
            RetailCommandHelpTable.HouseOverview);
        Assert.DoesNotContain("[IMPLEMENTED", RetailCommandHelpTable.HouseOverview);
        Assert.DoesNotContain("Subcommands NOT marked", RetailCommandHelpTable.HouseOverview);
    }

    [Fact]
    public void Log_IsCompleteThreeParagraphRetailText()
    {
        Assert.Equal(
            "@log <name> - Echoes chat text to a logfile. All the information that appears in your chat window after you type this command will be copied into a text file. Choose the file you are copying to by naming it in the command. If this file already exists, it will add the additional text to the end of it. To turn off logging, simply retype @log.\n"
            + "@log AClog.txt - Echoes chat text to a log file named Aclog.txt in your Asheron's Call directory. After you use this command, all the information that appears in your chat window will be written to a file in your Asheron's Call directory named Aclog.txt.\n"
            + "@log - If you are currently copying the text in your chat window to a logfile, this command will stop the process.\n",
            RetailCommandHelpTable.Log);
    }

    [Fact]
    public void Render_IsRealRetailUsageText_NotAcdreamDescription()
    {
        Assert.Equal(
            "Usage:\n"
            + "@render <option> <value>\n"
            + "  radius #        : set landscape radius (between 5 and 25)\n"
            + "  fov #           : set field of view (between 10 and 160)\n",
            RetailCommandHelpTable.Render);
    }

    [Fact]
    public void Motd_IsCompleteThreeLineRetailText_PreservesRetailTypo()
    {
        Assert.Equal(
            "@allegiance motd - Displays the message of the day for your allegiance.\n"
            + "@allegiance motd set <text> - Sets the MOTD. Can only be used by monarchs.\n"
            + "@allegiance motd clear- Clears the MOTD. Can only be used by monarchs.\n",
            RetailCommandHelpTable.Motd);
    }

    [Fact]
    public void MessageTypesDetail_IsRealConstructionFromLegalChannelWhitelist()
    {
        Assert.Equal(
            "Squelch channels are as follows:\n"
            + "  Speech, Tell, Combat, Magic, Emote, Appraisal, Spellcasting, Allegiance, Fellowship, Combat_Enemy, Combat_Self, Recall, Craft, Salvaging\n",
            RetailCommandHelpTable.MessageTypesDetail);

        Assert.True(RetailCommandHelpTable.TryGetCatalogVerbDetailText(
            "messagetypes", out string messagetypes));
        Assert.Equal(RetailCommandHelpTable.MessageTypesDetail, messagetypes);

        foreach (string alias in new[] { "message_types", "msgtypes", "msg_types" })
        {
            Assert.True(RetailCommandHelpTable.TryGetCatalogVerbDetailText(alias, out string aliasText));
            Assert.Equal(RetailCommandHelpTable.MessageTypesDetail, aliasText);
        }
    }

    [Fact]
    public void HelpMessageTypes_ThroughRouter_PrintsRetailsExactDetailText()
    {
        var log = new AcDream.Core.Chat.ChatLog();
        var vm = new ChatVM(log, displayLimit: 50);
        var bus = new RecordingCommandBus();

        var outcome = ChatCommandRouter.Submit(
            "/help messagetypes", vm, bus, ChatChannelKind.Say);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        var entries = log.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.Equal(RetailCommandHelpTable.HelpPrefixNote, entries[0].Text);
        Assert.Equal(
            RetailCommandHelpTable.ForMoreInformationPrefix
                + RetailCommandHelpTable.MessageTypesDetail,
            entries[1].Text);
    }

    private sealed class RecordingCommandBus : ICommandBus
    {
        public List<object> Published { get; } = new();
        public void Publish<T>(T command) where T : notnull => Published.Add(command);
    }
}
