using System.Collections.Frozen;
using AcDream.Core.Chat;

namespace AcDream.Runtime.Chat;

public static class RetailCommandHelpTable
{
    public const string Tell =
        "@tell <name>, <text> - Sends a long-distance, private message to the specified character. Note that you must put a comma after the character's name.";

    public const string Reply =
        "@reply <text> - Sends the text to the last person who @tell'd you. You may also use @r or @rp.";

    public const string Retell =
        "@retell <text> - Sends the text to the last person you @tell'd. You may also use @rt.";

    public const string HelpPrefixNote =
        "\nNote: You may substitute a forward slash (/) for the at symbol (@).\n\n";

    public const string ForMoreInformationPrefix =
        "For more information, type @help <command>.\n";

    public const string UnknownCommand = "Unknown command";

    public const string MonarchReply =
        "@mr <text> - Sends the text to the last person who used @m to send  you a message. This only works for monarchs.";

    public const string PatronReply =
        "@pr <text> - Sends the text to the last vassal who used @p to send  you a message.";

    public const string DayLine =
        "@day - A toggle that lightens the outdoor landscape. Note that this command may take several seconds to take effect. \n";

    public const string Day = DayLine;

    public const string Log =
        "@log <name> - Echoes chat text to a logfile. All the information that appears in your chat window after you type this command will be copied into a text file. Choose the file you are copying to by naming it in the command. If this file already exists, it will add the additional text to the end of it. To turn off logging, simply retype @log.\n"
        + "@log AClog.txt - Echoes chat text to a log file named Aclog.txt in your Asheron's Call directory. After you use this command, all the information that appears in your chat window will be written to a file in your Asheron's Call directory named Aclog.txt.\n"
        + "@log - If you are currently copying the text in your chat window to a logfile, this command will stop the process.\n";

    public const string Render =
        "Usage:\n"
        + "@render <option> <value>\n"
        + "  radius #        : set landscape radius (between 5 and 25)\n"
        + "  fov #           : set field of view (between 10 and 160)\n";

    public const string Motd =
        "@allegiance motd - Displays the message of the day for your allegiance.\n"
        + "@allegiance motd set <text> - Sets the MOTD. Can only be used by monarchs.\n"
        + "@allegiance motd clear- Clears the MOTD. Can only be used by monarchs.\n";

    public const string AllegianceWarningLine =
        " WARNING! Officers banning or booting a character by account could wind up in a situation where they are no longer in the allegiance if they boot a character that is above them in the hierarchy.\n";

    public const string AllegianceOverview =
        "@allegiance - Commands to help manage your allegiance.\n"
        + "@allegiance boot [-account] <name> - Removes a character from your allegiance.\n"
        + "@allegiance ban <add/remove> <name> - Bans all characters on the given character's account from your allegiance (and boots them too!)\n"
        + "@allegiance ban list - List the characters whose accounts are banned from your allegiance.\n"
        + AllegianceWarningLine
        + "@allegiance info <name> - Requests information on a member of your allegiance.\n"
        + "@allegiance chat <on/off> - Turn allegiance chat on and off.\n"
        + "@allegiance chat kick <name>[, <reason>] - Kick a player temporarily from the allegiance chat room.\n"
        + "@allegiance chat gag <name> - Gags a player so that they cannot see or speak in the allegiance chat room for 5 minutes.\n"
        + "@allegiance chat ungag <name> - Ungags a gagged allegiance member so that they may once again see and speak in the allegiance chat room.\n"
        + "@allegiance broadcast <message> - Broadcast a message to the entire allegiance. Limited to 10/day. Also: @ab\n"
        + "@allegiance officer <add/set> <level #> <name> - Assigns the position of officer, with the given level of permissions, to the named character.\n"
        + "@allegiance officer <remove> <name> - Removed the named character as an allegiance officer.\n"
        + "@allegiance officer clear - Clears all officer positions.\n"
        + "@allegiance officer [list] - list your allegiance officer. Can be used by anyone in an allegiance.\n"
        + "@allegiance title set <level #> <title> - Sets the title of the given officer level.\n"
        + "@allegiance title clear - Clears all officer titles.\n"
        + "@allegiance title [list] - Lists all the officer titles for your allegiance.\n"
        + "@allegiance name <set/clear> - Displays, sets, or clears the name of your allegiance.\n"
        + "@allegiance lock <on/off/toggle/check> - Locks, unlocks, or displays the locked state of your allegiance.\n"
        + "@allegiance lock bypass <clear/name> - Sets, clears, or displays a single character as an approved vassal. That character may then swear into a locked allegiance.\n"
        + "@allegiance hometown - Recalls you to your allegiance bindstone, if your allegiance has tied to one.\n"
        + "@allegiance motd - Displays or sets the message of the day for your allegiance.\n";

    public const string HouseOneLiner =
        "@house - Commands that help you manage your house, including guest and storage management.\n";

    public const string HouseOverview =
        HouseOneLiner
        + "@house abandon - Abandons your house.\n"
        + "@house boot <name> - Removes a player from your house.\n"
        + "@house boot -all - Removes everyone from your house.\n"
        + "@house guest add <name> - Adds players to your house guest list.\n"
        + "@house guest remove <name> - Removes players from your house guest list.\n"
        + "@house guest add_allegiance - Adds your allegiance to the guest list.\n"
        + "@house guest remove_allegiance - Removes your allegiance from the guest list.\n"
        + "@house guest remove_all - Removes all guests from your house guest list.\n"
        + "@house guest list - Shows the current guest list.\n"
        + "@house recall - Teleports you to your house.\n"
        + "@house storage add <name> - Gives a player permission to use your house storage.\n"
        + "@house storage remove <name> - Removes permission to use your house storage from a player.\n"
        + "@house storage add_allegiance - Grants storage permission to your allegiance.\n"
        + "@house storage remove_allegiance - Removes storage permission from your allegiance.\n"
        + "@house storage remove_all - Removes all storage permissions from guests.\n"
        + "@house open - Creates an open house.\n"
        + "@house close - Closes your house.\n"
        + "@house hooks on|off - Makes the hooks in your house visible or invisible.\n"
        + "@house mansion_recall - Teleports you to your allegiance mansion or villa.\n"
        + "@house alleg_recall - Teleports you to your allegiance mansion or villa.\n"
        + "@house available - See @hslist\n";

    private const string ChannelsGroupSummary =
        "@help channels - How to communicate with people in your allegiance or fellowship.";

    private const string ChattingGroupSummaryVerbatim =
        "@help chatting - How to chat publically and privately.";

    private const string CommandsGroupSummary =
        "@help commands - Lists all commands.";

    public const string DeathGroupDetail =
        "@permit - Commands to give or revoke permission for others to loot your corpse.\n"
        + "@consent - Commands to help you manage the corpse-looting permissions that others give you.\n"
        + "@corpse - Displays the location of your last outdoor death.\n"
        + "@die - Kills your character and leaves a corpse, returning you to your lifestone.\n"
        + "@lifestone - Returns you to the last lifestone you used without killing you.\n"
        + "@marketplace - Teleports you to the Marketplace of Dereth.\n"
        + "@pkarena - Teleports you to the PK Arena. You must be PK to use this command.\n"
        + "@pklarena - Teleports you to the PKL Arena. You must be PKL to use this command.\n";

    public const string StatusGroupDetail =
        "@age - Displays your total gameplay time.\n"
        + "@birth - Displays when your character was created.\n"
        + DayLine
        + "@endurance - Explains how endurance affects your character.\n"
        + "@framerate - Toggles the framerate display.\n"
        + "@loc - Displays your current position.\n"
        + "@pklite - Sets your status to Player Killer Lite. Type @help pklite for more details.\n"
        + "@version - Tells you what version of the software you are using.\n";

    public const string TextGroupDetail =
        "@clear - Clears the chat box of all text.\n"
        + "@filter - Commands to filter out incoming messages.\n"
        + "@unfilter - Commands to remove filters from incoming messages.\n"
        + "@loadfile - Reads in the given text file and executes each line in the chat entry field.\n"
        + "@log - Commands to echo chat text to a logfile.\n"
        + "@title <new title> - Sets the title of the popup chat window.\n";

    public const string AllegiancesGroupDetail =
        "@allegiance - Commands to help manage your allegiance.\n"
        + "@allegiance motd - Displays or sets the message of the day for your allegiance, see @help motd for more information.\n";

    public const string AllegianceBroadcastLine = "@a - Sends a broadcast to your Allegiance.\n";
    public const string CovassalBroadcastLine = "@c - Sends a broadcast to your Co-vassals.\n";
    public const string MonarchBroadcastLine = "@m - Sends a broadcast to your Monarch.\n";
    public const string PatronBroadcastLine = "@p - Sends a broadcast to your Patron.\n";
    public const string VassalBroadcastLine = "@v - Sends a broadcast to your Vassals.\n";
    public const string FellowshipBroadcastLine = "@f - Sends a broadcast to your Fellowship.\n";

    public const string ChannelsGroupDetail =
        AllegianceBroadcastLine
        + CovassalBroadcastLine
        + MonarchBroadcastLine
        + PatronBroadcastLine
        + VassalBroadcastLine
        + FellowshipBroadcastLine;

    public const string ReplySummaryLine = "@reply - Sends some text to the last person who @tell'd you.\n";
    public const string PatronReplySummaryLine = "@pr - Sends some text to the last person who @p'd you.\n";
    public const string MonarchReplySummaryLine = "@mr - Sends some text to the last person who @m'd you.\n";

    public const string ChattingGroupDetail =
        "@chat - Sets whether or not you receive normal chat.\n"
        + "@notell - Sets whether or not you receive @tell's.\n"
        + ReplySummaryLine
        + PatronReplySummaryLine
        + MonarchReplySummaryLine
        + "@retell - Sends some text to the last person you @tell'd.\n"
        + "@say - Says some text to everyone around you."
        + "@tell - Sends a private message to another character.\n"
        + ChannelsGroupDetail
        + "@afk - Set your away-from-keyboard status.\n";

    public const string EmoteAndEmotesShortSummary =
        "@emote - Performs a text emote.\n@emotes - Lists all standard emotes.\n";
    public const string FillCompsShortSummary = "@fillcomps - Helps you buy components in bulk.\n";
    public const string SaveUiShortSummary = "@saveui <filename> - Saves the current user interface.\n";
    public const string LoadUiShortSummary = "@loadui <filename> - Loads a previously saved user interface.\n";
    public const string FriendsShortSummary = "@friends - Helps you manage your friends list.\n";
    public const string SquelchGroupShortSummary =
        "@squelch - Squelches a character or account.\n"
        + "@unsquelch - Unsquelches a squelched character or account.\n"
        + "@messagetypes - Lists all types of messages that can be squelched or filtered.\n";

    public const string CommandsGroupDetail =
        AllegiancesGroupDetail
        + ChannelsGroupDetail
        + ChattingGroupDetail
        + DeathGroupDetail
        + EmoteAndEmotesShortSummary
        + FillCompsShortSummary
        + SaveUiShortSummary
        + LoadUiShortSummary
        + SaveUiShortSummary
        + LoadUiShortSummary
        + LockUiDetail
        + FriendsShortSummary
        + HouseOneLiner
        + SquelchGroupShortSummary
        + StatusGroupDetail
        + TextGroupDetail;

    public const string AvailableHelpListing =
        "Available help:\n"
        + "@help allegiances - Commands to help you deal with your Allegiance.\n"
        + ChannelsGroupSummary + "\n"
        + ChattingGroupSummaryVerbatim + "\n"
        + "@help death - Commands for making, finding, and looting corpses.\n"
        + "@help emote - How to perform text and action emotes.\n"
        + "@help fillcomps - A command to help you buy components in bulk.\n"
        + "@help friends - Commands to help you manage your friends list.\n"
        + "@help house - Commands that help you manage your house, including guest and storage management.\n"
        + "@help squelch - Commands that let you block out messages from other players.\n"
        + "@help status - Commands that display useful information.\n"
        + "@help text - Commands that help you manage your text window.\n"
        + CommandsGroupSummary + "\n";

    public const string LifestoneDetail =
        "@lifestone - Returns you to the last lifestone you used without killing you.\n";

    public const string MarketplaceDetail =
        "@marketplace - Teleports you to the Marketplace of Dereth.\n";

    public const string PkArenaDetail =
        "@pkarena - Teleports you to the PK Arena. You must be PK to use this command.\n";

    public const string PkLiteArenaDetail =
        "@pklarena - Teleports you to the PKL Arena. You must be PKL to use this command.\n";

    public const string PkLiteDetail =
        "@pklite - Sets your status to Player Killer Lite (PK Lite). PK Lite characters can attack other PK Lite characters. They cannot, however, attack Player Killer (PK) characters. PK Lite characters operate under the same combat rules as PK characters, except that if you are killed  in a PK Lite battle, you will not accrue vitae and you will not drop any coins or items. Only Non-Player Killers may use this command to enter PK Lite. Dying in a PK Lite battle and logging off will restore your status to Non-Player Killer.\n";

    public const string AgeDetail =
        "@age - Displays your total gameplay time.\n";

    public const string BirthDetail =
        "@birth - Displays when your character was created.\n";

    public const string FrameRateDetail =
        "@framerate - Toggles the framerate display.\n";

    public const string LockUiDetail =
        "@lockui - Toggles the locked state of the UI layout.\n";

    public const string VersionDetail =
        "@version - Tells you what version of the software you are using.\n";

    public const string LocDetail =
        "@loc - Displays your current position in your chat window. Use this information when you wish to submit a bug report.\n";

    public const string CorpseDetail =
        "@corpse - Displays the location of your last outdoor death. Even if your corpse has disappeared or if you have subsequently died indoors, typing this command will display your last outdoor corpse location.\n";

    public const string DieDetail =
        "@die - If you wish to kill your character and leave a corpse, you may use the @die command.  This will result in your character's death, you will leave behind a corpse with some of your items, and you will appear at your lifestone.  If you wish to travel to your lifestone without leaving behind a corpse, you may use the @lifestone command.\n";

    public const string ClearDetail =
        "@clear - Clears the chat box of all text.\n";

    public const string SaveUiDetail =
        "@saveui <filename> - Saves the current user interface layout to disk using the provided file name. If no file name is provided the layout is saved with a name that is unique for your server, character and resolution.\n";

    public const string LoadUiDetail =
        "@loadui <filename> - Loads a previously saved user interface layout from disk using the provided file name";

    public const string SaveAutoUiDetail =
        "@saveautoui - Stores the current layout to a character and resolution specific file. This layout will automatically be used when the resolution changes for this character to the current size.\n";

    public const string LoadAutoUiDetail =
        "@loadautoui - Forces a previously saved layout to load for this user and resolution.";

    public const string AfkDetail =
        "@afk - Turns on AFK (away-from-keyboard) mode. When set to AFK, other players that send you directed chatyou will receive a customizable message that your are not currently at the keyboard.\n@afk on - Turns on AFK mode. When set to AFK, other players that send you directed chatyou will receive a customizable message that your are not currently at the keyboard.\n@afk off - Turn off AFK mode.\n@afk msg <message> - Set the message that will be sent to players that send you directed chat while you are in AFK mode. Issuing \"@afk msg\" with no message will set your AFK message back to the default. Your custom AFK message is limited to 192 characters.\n";

    public const string ConsentDetail =
        "The @consent commands allow you to display and manage your corpse-looting consent list. This list lets you control whether others may permit you to loot their corpse and also allows you to monitor who has given you permission. You may have a maximum of 20 separate permissions at any given time. You will not be able to loot a corpse that was the victim of a player killer, even if its owner has given you permission. Also, players who have squelched you are not able to permit you to loot their corpse. Note that you can toggle your consent on/off via the Character Options panel as well as through these commands.\n@consent on - Turns on your ability to accept permissions from other players.\n@consent off - Turns off your ability to accept permissions from other players.\n@consent who - Lists those who have given you permission to loot their corpses.\n@consent remove <name> - Removes the permission a player granted to you.\n@consent clear - Clears your entire consent list.\n\n";

    public const string EmoteDetail =
        "The @emote command causes your character to emote some text, by performing an action in the third person. For example, if you typed the following while logged in as a character named Arville:\n    @emote looks around the town curiously.\nthen the chat windows of everyone around you would display:\n    Arville looks around the town curiously.\nYou can use any of these shorter forms of the command as well:\n         @e <text>\n         @em <text>\n         ; <text>\n         : <text>\n\nYou can also use a variety of standard emotes. These emotes come with special animations as well as text. Type @emotes to see a list.\n\n";

    public const string EmoteListDetail =
        "Standard Emotes:\nNote: These commands should be bound on either side by asterisks. (Example: *wave*)\nShakeFist; Beckon; BeSeeingYou; BlowKiss; BowDeep; ClapHands; Cry; Laugh; Nod; Point; Shrug; Wave; Akimbo; HeartyLaugh; Salute; TapFoot; WaveHigh; WaveLow; Yawn; Stretch; Cringe; Kneel; Plead; Shiver; Shoo; Slouch; Spit; Surrender; Woah; Winded; YMCA; Eat; Drink; Teapot; Pray; Mock; Cheer; Helper; Warm Hands; Scratch Head; Shake Head\n\n";

    public const string FriendsDetail =
        "Every time someone on your friends list logs in or out, you will receive notification. In addition, you can query the online status of your friends list at any time. Your friends list can contain up to 50 characters.\n@friends - Shows all your current friends and indicates if any of them are online.\n@friends online - Shows your current online friends.\n@friends add <name> - Adds a character to your friends list.\n@friends remove <name> - Removes a character from your friends list.\n@friends remove -all - Clears your friends list.\n@friends old - Shows the characters who were on your old-style friends list prior to the January 2006 update, so you can move them to your new-style friends list if necessary.\n";

    public const string SquelchDetail =
        "The @squelch commands let you block out messages from specific characters or players. The @unsquelch commands lets squelched messages reach you again. Use the options on these commands to squelch all message types or just some types of messages; one character or an entire account. You may have up to 32 players squelched at once. Note that NPCs cannot be permanently squelched.\n\n@squelch - Shows the current list of squelched characters.\n@squelch [-account] <name> - Squelches all messages from a character. With the account flag, this command also stops everything except normal chat coming from the target's other characters.\n@squelch [-message_type] <character> - This will filter out all text messages of a certain type  from a specific character.  For example, the following will filter out all tell messages from Oswald:\n     Example: @squelch -tell Oswald.\n@squelch -reply [-account] [-message_type] - This filters out all text messages from whoever last tell'd you.  You may also use the -account flag and/or limit the squelch by indicating specific message types. For example, this will filter out all tell messages from the account of Oswald, assuming that Oswald was the last person who sent you an @tell:\n     Example: @squelch -reply -account -tell\n\n@unsquelch - Shows the current list of squelched characters.\n@unsquelch <name> - Removes all squelches from a character, including account squelch.\n@unsquelch [-message_type] <character> : This allows text messages of type message_type to come from a squelched character. For example the following allows assessment messages from a character name Oswald:\n     Example: @unsquelch -assessment Oswald\n@unsquelch -reply [-account] [-message_type] : This allows text messages of type message_type from whoever last sent you an @tell. For example, the following will allow any character on Oswald's account to once again send you @tells, assuming that Oswald was the last person who sent you an @tell:\n     Example: @squelch -reply -account -tell\n\nType @messagetypes for a complete list of message types.\n";

    public const string FilterDetail =
        "The @filter commands filter out all incoming messages of a certain type. Type @messagetypes to see a list of the message types that you can filter.\n@filter - List all the filters currently in place.\n@filter <-message_type> - Filters out all incoming messages of a specific type. For example, the following will filter out all spellcasting text: \n     Example: @filter -spellcasting\n@filter -all - Filters out all incoming messages of all types.\n\n";

    public const string UnfilterDetail =
        "The @unfilter commands remove specific filters from your incoming messages. For a complete list of message types that you can filter, type @help messagetypes.\n@unfilter <-message_type> - Removes filters on incoming messages of a specific type.  For example, the following allows spellcasting text to resume:\n     Example: @unfilter -spellcasting\n@unfilter -all - Removes all filters on incoming messages of all types.\n";

    public const string FillCompsDetail =
        "The @fillcomps command assists in the bulk purchase of spell components. It is the sole interface for filling the buy list, which is the column of red zeros to the right in your components panel. To designate which components you would like to buy, change the zeros to the number of each component you would like to buy. The types of components you can buy are scarabs, herbs, powders, potions, and talismans.\n\nThis is the proper syntax: @fillcomps <component type> <pyreal value>\n\n@fillcomps - Fills the buy list with all of the components that are desired.\n@fillcomps <component type> - Fills the buy list with all of the components of the given type.\n@fillcomps <pyreal value> - Fills the buy list with all of the components until the total price of the components exceeds the given value.\n@fillcomps <component type> <pyreal value> - Fills the buy list with all of the components of the given type until the total price of components exceeds the given value.\n@fillcomps clear - Sets the requested amount for all components to zero.\n";

    public const string EnduranceDetail =
        "The endurance attribute has a number of abilities tied to it.\nFirst, some combination of strength and endurance (with endurance being more important) now allows one to regenerate hit points at a faster rate the higher one's endurance is.  This bonus is in addition to any regeneration spells one may have placed upon themselves.  This endurance regeneration bonus caps at around 110%.\nSecond, the higher a player's Endurance, the less stamina one uses while attacking.  This benefit is tied to Endurance only, and it caps out at around 50% less stamina used per attack.  The minimum stamina used per attack remains one.\nThird, the higher a player's Endurance, the more likely they are not to use a point of stamina to successfully evade a missile or melee attack.  A player is required to have Melee Defense for melee attacks or Missile Defense for missile attacks trained or specialized in order for this specific ability to work.  This benefit is tied to Endurance only, and it caps out at around a 75% chance to avoid losing a point of stamina per successful evasion.\nFourth, some combination of strength and endurance (the two are roughly of equivalent importance) now allows one to partially resist drain and harm attacks, up to a maximum of roughly 50%.\nFifth, some combination of strength and endurance (the two are roughly of equivalent importance) now allows one to have a level of \"natural resistances\" to the 7 damage types, the same as a certain level of life protections.  This caps out at a 50% resistance (the equivalent to level 5 life prots) to these damage types.  This resistance is not additive to life protections: higher level life protections will overwrite these natural resistances, although life vulns will take these natural resistances into account, if the player does not have a higher level life protection cast upon him.\nThe natural resistances, drain resistances, and regeneration rate info are now visible on the Character Information Panel, in what was once the Burden panel.  This panel now displays the above three Endurance benefits, the burden info, as well as information about your age, birth date, and number of deaths.\nThe 5 categories for the endurance benefits are, in order from lowest benefit to highest: Poor, Mediocre, Hardy, Resilient, and Indomitable, with each range of benefits divided up equally amongst the 5 (e.g. Poor describes having anywhere from 1-10% resistance against drain health attacks, etc.).\n";

    public const string SpeakerDetail =
        "@speaker - No longer used, see @allegiance officer for a similar command.\n";

    public const string TitleDetail =
        "@title <new title> - Sets the title of the popup chat window.\n";

    public const string ChatToggleDetail =
        "@chat <on/off> - Sets whether or not you receive normal chat. When set to \"off\", you will no longer receive any spoken speech (normal chat).  However, you will still receive tells.\n";

    public const string NoTellDetail =
        "@notell <on/off> - Sets whether or not you receive @tells. When set to \"on\", you will not receive any tells.\n";

    public const string JoinChatDetail =
        "@join <channel tag> - Allows you to hear and speak on the given channel.\n";

    public const string LeaveChatDetail =
        "@leave <channel tag> - Prevents you from hearing or speaking on the given channel.\n";

    public const string PermitDetail =
        "The @permit command gives or revokes corpse-looting permissions to other players. You can permit other players to loot any one of your corpses. You may not @permit a player again until he or she has looted your corpse. Permissions expire either after one hour or when the permitted player logs off. If you were killed by a player killer, no one can loot your corpse except you or your killer, even if you give someone else permission.\n@permit add <name> - Allows  another player to loot your corpse.\n@permit remove <name> - Removes permission to access your corpse from the named character.\nType @help consent for more details on corpse looting.\n";

    public const string HslistDetail =
        "@hslist <house type> - Lists the number and, if appropriate, positions of houses currently available for purchase. Types include: Apartment, Cottage, Villa, Mansion\n";

    private static readonly (RetailLogTextType Type, string Name)[] SquelchLegalChannels =
    {
        (RetailLogTextType.Speech, "Speech"),
        (RetailLogTextType.Tell, "Tell"),
        (RetailLogTextType.Combat, "Combat"),
        (RetailLogTextType.Magic, "Magic"),
        (RetailLogTextType.Emote, "Emote"),
        (RetailLogTextType.Appraisal, "Appraisal"),
        (RetailLogTextType.Spellcasting, "Spellcasting"),
        (RetailLogTextType.Allegiance, "Allegiance"),
        (RetailLogTextType.Fellowship, "Fellowship"),
        (RetailLogTextType.CombatEnemy, "Combat_Enemy"),
        (RetailLogTextType.CombatSelf, "Combat_Self"),
        (RetailLogTextType.Recall, "Recall"),
        (RetailLogTextType.Craft, "Craft"),
        (RetailLogTextType.Salvaging, "Salvaging"),
    };

    private static string BuildMessageTypesDetail() =>
        "Squelch channels are as follows:\n  "
        + string.Join(", ", Array.ConvertAll(SquelchLegalChannels, c => c.Name))
        + "\n";

    public static readonly string MessageTypesDetail = BuildMessageTypesDetail();

    private static readonly FrozenDictionary<string, string> CatalogVerbDetailByVerb =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["log"] = Log,
            ["day"] = Day,
            ["render"] = Render,
            ["motd"] = Motd,
            ["lifestone"] = LifestoneDetail,
            ["lif"] = LifestoneDetail,
            ["ls"] = LifestoneDetail,
            ["marketplace"] = MarketplaceDetail,
            ["mar"] = MarketplaceDetail,
            ["mp"] = MarketplaceDetail,
            ["pkarena"] = PkArenaDetail,
            ["pka"] = PkArenaDetail,
            ["pklarena"] = PkLiteArenaDetail,
            ["pla"] = PkLiteArenaDetail,
            ["pklite"] = PkLiteDetail,
            ["pkl"] = PkLiteDetail,
            ["hor"] = HouseOverview,
            ["hr"] = HouseOverview,
            ["hom"] = HouseOverview,
            ["hoa"] = HouseOverview,
            ["age"] = AgeDetail,
            ["birth"] = BirthDetail,
            ["framerate"] = FrameRateDetail,
            ["lockui"] = LockUiDetail,
            ["version"] = VersionDetail,
            ["loc"] = LocDetail,
            ["corpse"] = CorpseDetail,
            ["cor"] = CorpseDetail,
            ["die"] = DieDetail,
            ["clear"] = ClearDetail,
            ["saveui"] = SaveUiDetail,
            ["loadui"] = LoadUiDetail,
            ["saveautoui"] = SaveAutoUiDetail,
            ["loadautoui"] = LoadAutoUiDetail,
            ["afk"] = AfkDetail,
            ["consent"] = ConsentDetail,
            ["e"] = EmoteDetail,
            ["em"] = EmoteDetail,
            ["emote"] = EmoteDetail,
            ["me"] = EmoteDetail,
            ["emotes"] = EmoteListDetail,
            ["friends"] = FriendsDetail,
            ["friends_add"] = FriendsDetail,
            ["friends_remove"] = FriendsDetail,
            ["squelch"] = SquelchDetail,
            ["unsquelch"] = SquelchDetail,
            ["filter"] = FilterDetail,
            ["unfilter"] = UnfilterDetail,
            ["messagetypes"] = MessageTypesDetail,
            ["message_types"] = MessageTypesDetail,
            ["msgtypes"] = MessageTypesDetail,
            ["msg_types"] = MessageTypesDetail,
            ["fillcomps"] = FillCompsDetail,
            ["endurance"] = EnduranceDetail,
            ["speaker"] = SpeakerDetail,
            ["title"] = TitleDetail,
            ["chat"] = ChatToggleDetail,
            ["notell"] = NoTellDetail,
            ["join"] = JoinChatDetail,
            ["leave"] = LeaveChatDetail,
            ["permit"] = PermitDetail,
            ["hslist"] = HslistDetail,
            ["alh"] = AllegianceOverview,
            ["ah"] = AllegianceOverview,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> CatalogVerbsWithNoRetailHelp =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index", "clist", "on", "off" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetCatalogVerbDetailText(string verb, out string detailText) =>
        CatalogVerbDetailByVerb.TryGetValue(verb.TrimEnd(','), out detailText!);

    private static readonly FrozenDictionary<string, string> ByVerb =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["say"] = "@say <text> - Speaks the text aloud to nearby players.",
            ["s"] = "@say <text> - Speaks the text aloud to nearby players.",
            ["tell"] = Tell,
            ["t"] = Tell,
            ["send"] = Tell,
            ["whisper"] = Tell,
            ["w"] = Tell,
            ["reply"] = Reply,
            ["r"] = Reply,
            ["rp"] = Reply,
            ["retell"] = Retell,
            ["rt"] = Retell,
            ["mr"] = MonarchReply,
            ["pr"] = PatronReply,
            ["day"] = Day,
            ["log"] = Log,
            ["render"] = Render,
            ["motd"] = Motd,
            ["commands"] = CommandsGroupDetail,
            ["allegiances"] = AllegiancesGroupDetail,
            ["channels"] = ChannelsGroupDetail,
            ["chatting"] = ChattingGroupDetail,
            ["death"] = DeathGroupDetail,
            ["status"] = StatusGroupDetail,
            ["text"] = TextGroupDetail,
            ["f"] = "Sends text to your Fellowship channel.",
            ["fellow"] = "Sends text to your Fellowship channel.",
            ["fellows"] = "Sends text to your Fellowship channel.",
            ["fellowship"] = "Sends text to your Fellowship channel.",
            ["g"] = "Sends text to your Fellowship channel.",
            ["group"] = "Sends text to your Fellowship channel.",
            ["party"] = "Sends text to your Fellowship channel.",
            ["a"] = "@a - Sends a message to your Allegiance. Also: @guild, @gu",
            ["guild"] = "@a - Sends a message to your Allegiance. Also: @guild, @gu",
            ["gu"] = "@a - Sends a message to your Allegiance. Also: @guild, @gu",
            ["ab"] = "Broadcasts text to your entire allegiance (monarch/speaker permission). Also @allegiance broadcast.",
            ["general"] = "@general - Sends a message to the global General chat channel. Also: @cg",
            ["cg"] = "@general - Sends a message to the global General chat channel. Also: @cg",
            ["trade"] = "@trade - Sends a message to the global Trade chat channel. Also: @ct",
            ["ct"] = "@trade - Sends a message to the global Trade chat channel. Also: @ct",
            ["lfg"] = "@lfg - Sends a message to the global Looking For Group (LFG) chat channel. Also: @clfg",
            ["clfg"] = "@lfg - Sends a message to the global Looking For Group (LFG) chat channel. Also: @clfg",
            ["roleplay"] = "@roleplay - Sends a message to the global Roleplay chat channel. Also: @crp",
            ["crp"] = "@roleplay - Sends a message to the global Roleplay chat channel. Also: @crp",
            ["society"] = "@society - Sends a message to the your Society chat channel. Also: @soc",
            ["soc"] = "@society - Sends a message to the your Society chat channel. Also: @soc",
            ["olthoi"] = "@olthoi - If you are an Olthoi, sends a message to the global Olthoi chat channel. Also: @o",
            ["o"] = "@olthoi - If you are an Olthoi, sends a message to the global Olthoi chat channel. Also: @o",
            ["m"] = "Sends text to your Monarch.",
            ["monarch"] = "Sends text to your Monarch.",
            ["p"] = "Sends text to your Patron.",
            ["patron"] = "Sends text to your Patron.",
            ["v"] = "Sends text to your Vassals.",
            ["vassal"] = "Sends text to your Vassals.",
            ["vassals"] = "Sends text to your Vassals.",
            ["c"] = "Sends text to your Co-vassals.",
            ["covassal"] = "Sends text to your Co-vassals.",
            ["covassals"] = "Sends text to your Co-vassals.",
            ["co-vassals"] = "Sends text to your Co-vassals.",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetHelpText(string verb, out string helpText) =>
        ByVerb.TryGetValue(verb.TrimEnd(','), out helpText!);
}
