using System.Collections.Frozen;

namespace AcDream.Runtime.Chat;

public static class RetailClientCommandCatalog
{
    private sealed record Definition(
        ClientCommandId Command,
        string Usage,
        string HelpText,
        Func<string, bool> ValidateArguments,
        string? InvalidArgumentsText = null);

    public readonly record struct Match(
        ClientCommandId Command,
        string Arguments,
        string Usage,
        bool HasValidArguments,
        string? InvalidArgumentsText);

    private static readonly Definition Lifestone = new(
        ClientCommandId.LifestoneRecall,
        Usage: "/lifestone",
        HelpText: "/lifestone (/lif, /ls) - Returns you to the last lifestone you used without killing you.",
        ValidateArguments: static arguments => arguments.Length == 0,
        InvalidArgumentsText: "Please see @help lifestone for more information on how to use this command.");

    private static readonly Definition Marketplace = new(
        ClientCommandId.MarketplaceRecall,
        Usage: "/marketplace",
        HelpText: "/marketplace (/mar, /mp) - Teleports you to the Marketplace of Dereth.",
        ValidateArguments: static arguments => arguments.Length == 0,
        InvalidArgumentsText: "Please see @help marketplace for more information on how to use this command.");

    private static readonly Definition PkArena = NoArguments(
        ClientCommandId.PkArenaRecall,
        "/pkarena",
        "/pkarena (/pka) - Teleports a Player Killer to the PK Arena.");

    private static readonly Definition PkLiteArena = NoArguments(
        ClientCommandId.PkLiteArenaRecall,
        "/pklarena",
        "/pklarena (/pla) - Teleports a PKLite player to the PKLite Arena.");

    private static readonly Definition PkLite = NoArguments(
        ClientCommandId.EnterPkLite,
        "/pklite",
        "@pklite (@pkl) - Sets your status to Player Killer Lite. Type @help pklite for more details.");

    private static readonly Definition HouseRecall = NoArguments(
        ClientCommandId.HouseRecall,
        "/house recall",
        "/house recall (/hor, /hr) - Teleports you to your house.");

    private static readonly Definition MansionRecall = NoArguments(
        ClientCommandId.MansionRecall,
        "/house mansion_recall",
        "/house mansion_recall (/hom, /hoa) - Teleports you to your allegiance mansion.");

    private static readonly Definition HouseAbandon = NoArguments(
        ClientCommandId.HouseAbandon,
        "/house abandon",
        "@house abandon - Abandons your house.");

    private static readonly Definition QueryAge = NoArguments(
        ClientCommandId.QueryAge,
        "/age",
        "/age - Displays how long your character has been played.");

    private static readonly Definition QueryBirth = NoArguments(
        ClientCommandId.QueryBirth,
        "/birth",
        "/birth - Displays when your character was created.");

    private static readonly Definition FrameRate = NoArguments(
        ClientCommandId.ToggleFrameRate,
        "/framerate",
        "/framerate - Toggles the framerate display.");

    private static readonly Definition Day = AnyArguments(
        ClientCommandId.TogglePersistentDaylight,
        "/day",
        RetailCommandHelpTable.Day);

    private static readonly Definition Render = AnyArguments(
        ClientCommandId.RenderOption,
        "/render <option> <value>",
        RetailCommandHelpTable.Render);

    private static readonly Definition LockUi = NoArguments(
        ClientCommandId.ToggleUiLock,
        "/lockui",
        "/lockui - Toggles whether the interface can be moved or resized.");

    private static readonly Definition Version = NoArguments(
        ClientCommandId.ShowVersion,
        "/version",
        "/version - Displays the client version.");

    private static readonly Definition Location = NoArguments(
        ClientCommandId.ShowLocation,
        "/loc",
        "/loc - Displays your current position.");

    private static readonly Definition Corpse = new(
        ClientCommandId.ShowLastCorpseLocation,
        Usage: "/corpse",
        HelpText: "/corpse (/cor) - Displays the location of your last outdoor death.",
        ValidateArguments: static _ => true);

    private static readonly Definition Die = new(
        ClientCommandId.Die,
        Usage: "/die",
        HelpText: "/die - Kills your character after confirmation.",
        ValidateArguments: static arguments => arguments.Length == 0,
        InvalidArgumentsText: "Please see @help die for more information on how to use this command.");

    private static readonly Definition Clear = AnyArguments(
        ClientCommandId.ClearChat,
        "/clear [all]",
        "/clear [all] - Clears the current chat window, or every chat window.");

    private static readonly Definition ChatLogFile = AnyArguments(
        ClientCommandId.ChatLogFile,
        "/log [filename]",
        "/log [filename] - Echoes chat text to a logfile, or stops if already logging.");

    private static readonly Definition SaveUi = AnyArguments(
        ClientCommandId.SaveUi,
        "/saveui [filename]",
        "/saveui [filename] - Saves the current interface layout.");

    private static readonly Definition LoadUi = AnyArguments(
        ClientCommandId.LoadUi,
        "/loadui [filename]",
        "/loadui [filename] - Loads a saved interface layout.");

    private static readonly Definition SaveAutoUi = AnyArguments(
        ClientCommandId.SaveAutoUi,
        "/saveautoui",
        "/saveautoui - Saves the automatic character-and-resolution interface layout.");

    private static readonly Definition LoadAutoUi = AnyArguments(
        ClientCommandId.LoadAutoUi,
        "/loadautoui",
        "/loadautoui - Loads the automatic character-and-resolution interface layout.");

    private static readonly Definition Away = AnyArguments(
        ClientCommandId.Away,
        "/afk [on|off|msg <message>]",
        "/afk [on|off|msg <message>] - Sets your away-from-keyboard status.");

    private static readonly Definition Consent = AnyArguments(
        ClientCommandId.Consent,
        "/consent <on|off|who|clear|remove <name>>",
        "/consent - Manages corpse-looting consent.");

    private static readonly Definition Emote = AnyArguments(
        ClientCommandId.Emote,
        "/emote <text>",
        "/emote (/e, /em, /me) - Performs a text emote.");

    private static readonly Definition Emotes = NoArguments(
        ClientCommandId.ListEmotes,
        "/emotes",
        "/emotes - Lists all standard emotes.");

    private static readonly Definition Friends = AnyArguments(
        ClientCommandId.Friends,
        "/friends [add|remove|online|old]",
        "/friends - Helps you manage your friends list.");

    private static readonly Definition FriendsAdd = AnyArguments(
        ClientCommandId.FriendsAdd,
        "/friends_add <name>",
        "/friends_add <name> - Adds a character to your friends list.");

    private static readonly Definition FriendsRemove = AnyArguments(
        ClientCommandId.FriendsRemove,
        "/friends_remove <name|-all>",
        "/friends_remove <name|-all> - Removes friends from your list.");

    private static readonly Definition Squelch = AnyArguments(
        ClientCommandId.Squelch,
        "/squelch [options] <name>",
        "/squelch - Ignores messages from a player or account.");

    private static readonly Definition Unsquelch = AnyArguments(
        ClientCommandId.Unsquelch,
        "/unsquelch [options] <name>",
        "/unsquelch - Stops ignoring messages from a player or account.");

    private static readonly Definition Filter = AnyArguments(
        ClientCommandId.Filter,
        "/filter -<message type>",
        "/filter -<message type> - Globally hides a message category.");

    private static readonly Definition Unfilter = AnyArguments(
        ClientCommandId.Unfilter,
        "/unfilter -<message type>",
        "/unfilter -<message type> - Shows a globally hidden message category.");

    private static readonly Definition MessageTypes = NoArguments(
        ClientCommandId.ListMessageTypes,
        "/messagetypes",
        "/messagetypes (/message_types, /msgtypes, /msg_types) - Lists valid filter and squelch message types.");

    private static readonly Definition FillComponents = AnyArguments(
        ClientCommandId.FillComponents,
        "/fillcomps [component type] [pyreal value]",
        "/fillcomps - Helps you buy components in bulk.");


    private static readonly Definition Endurance = NoArguments(
        ClientCommandId.Endurance,
        "/endurance",
        "The endurance attribute has a number of abilities tied to it. Type @help endurance for the full description.");

    private static readonly Definition Speaker = NoArguments(
        ClientCommandId.Speaker,
        "/speaker",
        "This command is no longer in use, please see @allegiance officer.");

    private static readonly Definition SetTitle = AnyArguments(
        ClientCommandId.SetChatTitle,
        "/title <new title>",
        "@title <new title> - Sets the title of the popup chat window.");

    private static readonly Definition ChatToggle = new(
        ClientCommandId.ChatToggle,
        Usage: "/chat <on|off>",
        HelpText: "@chat <on/off> - Sets whether or not you receive normal chat. When set to \"off\", you will no longer receive any spoken speech (normal chat). However, you will still receive tells.",
        ValidateArguments: static arguments =>
            arguments.Equals("on", StringComparison.OrdinalIgnoreCase)
            || arguments.Equals("off", StringComparison.OrdinalIgnoreCase));

    private static readonly Definition NoTellToggle = new(
        ClientCommandId.NoTellToggle,
        Usage: "/notell <on|off>",
        HelpText: "@notell <on/off> - Sets whether or not you receive @tells. When set to \"on\", you will not receive any tells.",
        ValidateArguments: static arguments =>
            arguments.Equals("on", StringComparison.OrdinalIgnoreCase)
            || arguments.Equals("off", StringComparison.OrdinalIgnoreCase));

    public static bool TryResolveJoinLeaveOption(string tag, out uint optionId) =>
        JoinLeaveTags.TryGetValue(tag.Trim(), out optionId);

    private static readonly FrozenDictionary<string, uint> JoinLeaveTags =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["allegiance"] = 0x1Bu,
            ["general"] = 0x23u,
            ["trade"] = 0x24u,
            ["lfg"] = 0x25u,
            ["roleplay"] = 0x26u,
            ["society"] = 0x2Eu,
            ["soc"] = 0x2Eu,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly Definition JoinChannel = new(
        ClientCommandId.JoinChannel,
        Usage: "/join <channel tag>",
        HelpText: "@join <channel tag> - Allows you to hear and speak on the given channel.",
        ValidateArguments: static arguments => JoinLeaveTags.ContainsKey(arguments.Trim()));

    private static readonly Definition LeaveChannel = new(
        ClientCommandId.LeaveChannel,
        Usage: "/leave <channel tag>",
        HelpText: "@leave <channel tag> - Prevents you from hearing or speaking on the given channel.",
        ValidateArguments: static arguments => JoinLeaveTags.ContainsKey(arguments.Trim()));

    private static readonly Definition Permit = new(
        ClientCommandId.Permit,
        Usage: "/permit <add|remove> <name>",
        HelpText: "@permit add <name> - Allows another player to loot your corpse. @permit remove <name> - Removes permission to access your corpse from the named character.",
        ValidateArguments: static arguments =>
        {
            string[] parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                && (parts[0].Equals("add", StringComparison.OrdinalIgnoreCase)
                    || parts[0].Equals("remove", StringComparison.OrdinalIgnoreCase));
        });

    public static bool TryResolveHouseType(string type, out uint houseType) =>
        HouseTypes.TryGetValue(type.Trim(), out houseType);

    private static readonly FrozenDictionary<string, uint> HouseTypes =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["cottage"] = 1u,
            ["villa"] = 2u,
            ["mansion"] = 3u,
            ["apartment"] = 4u,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly Definition HouseAvailableList = new(
        ClientCommandId.HouseAvailableList,
        Usage: "/hslist <house type>",
        HelpText: "@hslist <house type> - Lists the number and, if appropriate, positions of houses currently available for purchase. Types include: Apartment, Cottage, Villa, Mansion",
        ValidateArguments: static arguments => HouseTypes.ContainsKey(arguments.Trim()),
        InvalidArgumentsText: "Please see @help hslist for more information on how to use this command");

    private static readonly Definition IndexChannels = AnyArguments(
        ClientCommandId.IndexChannels,
        "/index",
        "@index - Requests the channel index (restricted).");

    private static readonly Definition ListChannel = new(
        ClientCommandId.ListChannel,
        Usage: "/clist <channel>",
        HelpText: "@clist <channel> - Requests the member list of a channel (restricted).",
        ValidateArguments: static arguments => IsSingleToken(arguments),
        InvalidArgumentsText: "Please specify the channel name.");

    private static readonly Definition OnChannel = new(
        ClientCommandId.OnChannel,
        Usage: "/on <channel>",
        HelpText: "@on <channel> - Joins a channel (restricted).",
        ValidateArguments: static arguments => IsSingleToken(arguments),
        InvalidArgumentsText: "Please specify the channel name.");

    private static readonly Definition OffChannel = new(
        ClientCommandId.OffChannel,
        Usage: "/off <channel>",
        HelpText: "@off <channel> - Leaves a channel (restricted).",
        ValidateArguments: static arguments => IsSingleToken(arguments),
        InvalidArgumentsText: "Please specify the channel name.");

    private static bool IsSingleToken(string arguments) =>
        arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length == 1;

    private static readonly Definition AllegianceHometown = NoArguments(
        ClientCommandId.AllegianceHometown,
        "/alh",
        "@allegiance hometown (@alh, @ah) - Recalls you to your allegiance bindstone, if your allegiance has tied to one.");

    private static readonly Definition AllegianceInfo = AnyArguments(
        ClientCommandId.AllegianceInfo,
        "/allegiance info <name>",
        "@allegiance info <name> - Requests information on a member of your allegiance.");

    private static readonly Definition AllegianceBoot = AnyArguments(
        ClientCommandId.AllegianceBoot,
        "/allegiance boot [-account] <name>",
        RetailCommandHelpTable.AllegianceOverview);

    private static readonly Definition AllegianceBan = AnyArguments(
        ClientCommandId.AllegianceBan,
        "/allegiance ban <add|remove|list> [name]",
        RetailCommandHelpTable.AllegianceOverview);

    private static readonly Definition AllegianceChat = AnyArguments(
        ClientCommandId.AllegianceChat,
        "/allegiance chat <on|off|kick|gag|ungag> ...",
        RetailCommandHelpTable.AllegianceOverview);

    private static readonly Definition AllegianceBroadcast = AnyArguments(
        ClientCommandId.AllegianceBroadcast,
        "/allegiance broadcast <message>",
        RetailCommandHelpTable.AllegianceOverview);

    private static readonly Definition AllegianceOfficer = AnyArguments(
        ClientCommandId.AllegianceOfficer,
        "/allegiance officer [add|set|remove|clear|list] ...",
        RetailCommandHelpTable.AllegianceOverview);

    private static readonly Definition AllegianceOfficerTitle = AnyArguments(
        ClientCommandId.AllegianceOfficerTitle,
        "/allegiance title [set|clear|list] ...",
        RetailCommandHelpTable.AllegianceOverview);

    private static readonly Definition AllegianceName = AnyArguments(
        ClientCommandId.AllegianceName,
        "/allegiance name [set|clear] ...",
        RetailCommandHelpTable.AllegianceOverview);

    private static readonly Definition AllegianceLock = AnyArguments(
        ClientCommandId.AllegianceLock,
        "/allegiance lock [on|off|toggle|check|bypass] ...",
        RetailCommandHelpTable.AllegianceOverview);

    private static readonly Definition AllegianceHouse = AnyArguments(
        ClientCommandId.AllegianceHouse,
        "/allegiance house [guest|storage] [open|close]",
        RetailCommandHelpTable.AllegianceOverview);

    private static readonly Definition AllegianceMotd = AnyArguments(
        ClientCommandId.AllegianceMotd,
        "/motd [set <text>|clear]",
        RetailCommandHelpTable.Motd);

    private static readonly Definition HouseOpenStatus = AnyArguments(
        ClientCommandId.HouseOpenStatus,
        "/house <open|close>",
        RetailCommandHelpTable.HouseOverview);

    private static readonly Definition HouseStorage = AnyArguments(
        ClientCommandId.HouseStorage,
        "/house storage <subcommand>",
        RetailCommandHelpTable.HouseOverview);

    private static readonly Definition HouseBoot = AnyArguments(
        ClientCommandId.HouseBoot,
        "/house boot <name|-all>",
        RetailCommandHelpTable.HouseOverview);

    private static readonly Definition HouseBootAll = AnyArguments(
        ClientCommandId.HouseBootAll,
        "/house boot_all",
        RetailCommandHelpTable.HouseOverview);

    private static readonly Definition HouseGuests = AnyArguments(
        ClientCommandId.HouseGuests,
        "/house guest <subcommand>",
        RetailCommandHelpTable.HouseOverview);

    private static readonly Definition HouseHooks = AnyArguments(
        ClientCommandId.HouseHooks,
        "/house hooks <on|off>",
        RetailCommandHelpTable.HouseOverview);

    private static readonly Definition AllegianceUnrecognizedSubcommand = new(
        ClientCommandId.AllegianceUnrecognizedSubcommand,
        Usage: "/allegiance <sub>",
        HelpText: "Please see @help Allegiance for more information on how to use this command.",
        ValidateArguments: static _ => false,
        InvalidArgumentsText: "Please see @help Allegiance for more information on how to use this command.");

    private static readonly Definition HouseUnrecognizedSubcommand = new(
        ClientCommandId.HouseUnrecognizedSubcommand,
        Usage: "/house <sub>",
        HelpText: "Please see @help House for more information on how to use this command.",
        ValidateArguments: static _ => false,
        InvalidArgumentsText: "Please see @help House for more information on how to use this command.");

    private static readonly FrozenDictionary<string, Definition> ByVerb =
        new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase)
        {
            ["lifestone"] = Lifestone,
            ["lif"] = Lifestone,
            ["ls"] = Lifestone,
            ["marketplace"] = Marketplace,
            ["mar"] = Marketplace,
            ["mp"] = Marketplace,
            ["pkarena"] = PkArena,
            ["pka"] = PkArena,
            ["pklarena"] = PkLiteArena,
            ["pla"] = PkLiteArena,
            ["pklite"] = PkLite,
            ["pkl"] = PkLite,
            ["hor"] = HouseRecall,
            ["hr"] = HouseRecall,
            ["hom"] = MansionRecall,
            ["hoa"] = MansionRecall,
            ["age"] = QueryAge,
            ["birth"] = QueryBirth,
            ["framerate"] = FrameRate,
            ["day"] = Day,
            ["render"] = Render,
            ["lockui"] = LockUi,
            ["version"] = Version,
            ["loc"] = Location,
            ["corpse"] = Corpse,
            ["cor"] = Corpse,
            ["die"] = Die,
            ["clear"] = Clear,
            ["log"] = ChatLogFile,
            ["saveui"] = SaveUi,
            ["loadui"] = LoadUi,
            ["saveautoui"] = SaveAutoUi,
            ["loadautoui"] = LoadAutoUi,
            ["afk"] = Away,
            ["consent"] = Consent,
            ["e"] = Emote,
            ["em"] = Emote,
            ["emote"] = Emote,
            ["me"] = Emote,
            ["emotes"] = Emotes,
            ["friends"] = Friends,
            ["friends_add"] = FriendsAdd,
            ["friends_remove"] = FriendsRemove,
            ["squelch"] = Squelch,
            ["unsquelch"] = Unsquelch,
            ["filter"] = Filter,
            ["unfilter"] = Unfilter,
            ["messagetypes"] = MessageTypes,
            ["message_types"] = MessageTypes,
            ["msgtypes"] = MessageTypes,
            ["msg_types"] = MessageTypes,
            ["fillcomps"] = FillComponents,
            ["endurance"] = Endurance,
            ["speaker"] = Speaker,
            ["title"] = SetTitle,
            ["chat"] = ChatToggle,
            ["notell"] = NoTellToggle,
            ["join"] = JoinChannel,
            ["leave"] = LeaveChannel,
            ["permit"] = Permit,
            ["hslist"] = HouseAvailableList,
            ["index"] = IndexChannels,
            ["clist"] = ListChannel,
            ["on"] = OnChannel,
            ["off"] = OffChannel,
            ["alh"] = AllegianceHometown,
            ["ah"] = AllegianceHometown,
            ["motd"] = AllegianceMotd,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool TryMatch(string input, out Match match)
    {
        match = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string trimmed = input.Trim();
        if (trimmed.Length < 2 || trimmed[0] is not ('/' or '@'))
            return false;

        int separator = IndexOfWhitespace(trimmed);
        string verb = separator < 0
            ? trimmed[1..]
            : trimmed.Substring(1, separator - 1);
        verb = verb.TrimEnd(',');
        string arguments = separator < 0
            ? string.Empty
            : trimmed[(separator + 1)..].Trim();

        Definition? definition;
        if (verb.Equals("house", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("hou", StringComparison.OrdinalIgnoreCase))
        {
            return TryMatchHouse(arguments, out match);
        }

        if (verb.Equals("allegiance", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return TryMatchAllegiance(arguments, out match);
        }

        if (!ByVerb.TryGetValue(verb, out definition))
        {
            return false;
        }

        match = new Match(
            definition.Command,
            arguments,
            definition.Usage,
            definition.ValidateArguments(arguments),
            definition.InvalidArgumentsText);
        return true;
    }

    private static bool TryMatchHouse(string arguments, out Match match)
    {
        int separator = IndexOfWhitespace(arguments);
        string subcommand = separator < 0 ? arguments : arguments[..separator];
        string rest = separator < 0 ? string.Empty : arguments[(separator + 1)..].Trim();
        Definition? definition = subcommand.ToLowerInvariant() switch
        {
            "recall" or "re" => HouseRecall,
            "mansion_recall" or "alleg_recall" or "ma" => MansionRecall,
            "abandon" => HouseAbandon,
            "open" or "close" => HouseOpenStatus,
            "storage" => HouseStorage,
            "remove" or "boot" => HouseBoot,
            "boot_all" or "remove_all" => HouseBootAll,
            "guest" => HouseGuests,
            "available" => HouseAvailableList,
            "hooks" => HouseHooks,
            _ => null,
        };
        if (definition is null)
        {
            match = new Match(
                HouseUnrecognizedSubcommand.Command,
                arguments,
                HouseUnrecognizedSubcommand.Usage,
                HasValidArguments: false,
                HouseUnrecognizedSubcommand.InvalidArgumentsText);
            return true;
        }

        if (definition == HouseRecall || definition == MansionRecall)
        {
            match = new Match(
                definition.Command,
                rest,
                definition.Usage,
                HasValidArguments: rest.Length == 0,
                InvalidArgumentsText: "Please see @help House for more information on how to use this command.");
            return true;
        }

        if (definition == HouseAvailableList)
        {
            match = new Match(
                definition.Command,
                rest,
                definition.Usage,
                definition.ValidateArguments(rest),
                definition.InvalidArgumentsText);
            return true;
        }

        string nestedArguments = definition == HouseOpenStatus
            ? subcommand
            : rest;

        match = new Match(
            definition.Command,
            Arguments: nestedArguments,
            definition.Usage,
            HasValidArguments: true,
            InvalidArgumentsText: null);
        return true;
    }

    private static bool TryMatchAllegiance(string arguments, out Match match)
    {
        int separator = IndexOfWhitespace(arguments);
        string subcommand = separator < 0 ? arguments : arguments[..separator];
        string rest = separator < 0 ? string.Empty : arguments[(separator + 1)..].Trim();

        Definition? definition = subcommand.ToLowerInvariant() switch
        {
            "boot" => AllegianceBoot,
            "info" => AllegianceInfo,
            "chat" or "ch" => AllegianceChat,
            "broadcast" or "br" => AllegianceBroadcast,
            "ban" => AllegianceBan,
            "officer" => AllegianceOfficer,
            "title" => AllegianceOfficerTitle,
            "hometown" or "ho" => AllegianceHometown,
            "motd" => AllegianceMotd,
            "name" => AllegianceName,
            "lock" => AllegianceLock,
            "house" => AllegianceHouse,
            _ => null,
        };

        if (definition is not null)
        {
            match = new Match(
                definition.Command,
                definition == AllegianceHometown ? string.Empty : rest,
                definition.Usage,
                HasValidArguments: true,
                InvalidArgumentsText: null);
            return true;
        }

        match = new Match(
            AllegianceUnrecognizedSubcommand.Command,
            arguments,
            AllegianceUnrecognizedSubcommand.Usage,
            HasValidArguments: false,
            AllegianceUnrecognizedSubcommand.InvalidArgumentsText);
        return true;
    }

    public static IReadOnlyCollection<string> KnownVerbs { get; } =
        ByVerb.Keys.Concat(["house", "hou", "allegiance", "all"])
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetHelpText(string verb, out string helpText)
    {
        string trimmedVerb = verb.TrimEnd(',');
        if (trimmedVerb.Equals("house", StringComparison.OrdinalIgnoreCase)
            || trimmedVerb.Equals("hou", StringComparison.OrdinalIgnoreCase))
        {
            helpText = RetailCommandHelpTable.HouseOverview;
            return true;
        }

        if (trimmedVerb.Equals("allegiance", StringComparison.OrdinalIgnoreCase)
            || trimmedVerb.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            helpText = RetailCommandHelpTable.AllegianceOverview;
            return true;
        }

        if (ByVerb.TryGetValue(trimmedVerb, out Definition? definition))
        {
            helpText = definition.HelpText;
            return true;
        }

        helpText = string.Empty;
        return false;
    }

    private static Definition NoArguments(
        ClientCommandId command, string usage, string helpText) =>
        new(command, usage, helpText, static arguments => arguments.Length == 0);

    private static Definition AnyArguments(
        ClientCommandId command, string usage, string helpText) =>
        new(command, usage, helpText, static _ => true);

    private static int IndexOfWhitespace(string value)
    {
        for (int i = 1; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
                return i;
        }

        return -1;
    }
}
