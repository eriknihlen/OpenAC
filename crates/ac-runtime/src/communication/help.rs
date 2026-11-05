//! The retail help text: what `/help` (and `/?`, `@help`, `@?`) prints, as
//! the game client's own help tables word it, typos and spacing included.
//!
//! `/help` alone prints the note and the list of help topics; `/help <verb>`
//! prints the note and the verb's text after "For more information, ...",
//! or "Unknown command" for a verb with no help.

/// Expands to a literal, so the group texts below can be `concat!`ed from
/// the same lines the single-verb texts use.
macro_rules! day_line {
    () => {
        "@day - A toggle that lightens the outdoor landscape. Note that this command may take several seconds to take effect. \n"
    };
}
macro_rules! house_one_liner {
    () => {
        "@house - Commands that help you manage your house, including guest and storage management.\n"
    };
}
macro_rules! allegiance_warning_line {
    () => {
        " WARNING! Officers banning or booting a character by account could wind up in a situation where they are no longer in the allegiance if they boot a character that is above them in the hierarchy.\n"
    };
}
macro_rules! death_group_detail {
    () => {
        concat!(
            "@permit - Commands to give or revoke permission for others to loot your corpse.\n",
            "@consent - Commands to help you manage the corpse-looting permissions that others give you.\n",
            "@corpse - Displays the location of your last outdoor death.\n",
            "@die - Kills your character and leaves a corpse, returning you to your lifestone.\n",
            "@lifestone - Returns you to the last lifestone you used without killing you.\n",
            "@marketplace - Teleports you to the Marketplace of Dereth.\n",
            "@pkarena - Teleports you to the PK Arena. You must be PK to use this command.\n",
            "@pklarena - Teleports you to the PKL Arena. You must be PKL to use this command.\n",
        )
    };
}
macro_rules! status_group_detail {
    () => {
        concat!(
            "@age - Displays your total gameplay time.\n",
            "@birth - Displays when your character was created.\n",
            day_line!(),
            "@endurance - Explains how endurance affects your character.\n",
            "@framerate - Toggles the framerate display.\n",
            "@loc - Displays your current position.\n",
            "@pklite - Sets your status to Player Killer Lite. Type @help pklite for more details.\n",
            "@version - Tells you what version of the software you are using.\n",
        )
    };
}
macro_rules! text_group_detail {
    () => {
        concat!(
            "@clear - Clears the chat box of all text.\n",
            "@filter - Commands to filter out incoming messages.\n",
            "@unfilter - Commands to remove filters from incoming messages.\n",
            "@loadfile - Reads in the given text file and executes each line in the chat entry field.\n",
            "@log - Commands to echo chat text to a logfile.\n",
            "@title <new title> - Sets the title of the popup chat window.\n",
        )
    };
}
macro_rules! allegiances_group_detail {
    () => {
        concat!(
            "@allegiance - Commands to help manage your allegiance.\n",
            "@allegiance motd - Displays or sets the message of the day for your allegiance, see @help motd for more information.\n",
        )
    };
}
macro_rules! channels_group_detail {
    () => {
        concat!(
            "@a - Sends a broadcast to your Allegiance.\n",
            "@c - Sends a broadcast to your Co-vassals.\n",
            "@m - Sends a broadcast to your Monarch.\n",
            "@p - Sends a broadcast to your Patron.\n",
            "@v - Sends a broadcast to your Vassals.\n",
            "@f - Sends a broadcast to your Fellowship.\n",
        )
    };
}
macro_rules! chatting_group_detail {
    () => {
        concat!(
            "@chat - Sets whether or not you receive normal chat.\n",
            "@notell - Sets whether or not you receive @tell's.\n",
            "@reply - Sends some text to the last person who @tell'd you.\n",
            "@pr - Sends some text to the last person who @p'd you.\n",
            "@mr - Sends some text to the last person who @m'd you.\n",
            "@retell - Sends some text to the last person you @tell'd.\n",
            // Retail's line has no line break here.
            "@say - Says some text to everyone around you.",
            "@tell - Sends a private message to another character.\n",
            channels_group_detail!(),
            "@afk - Set your away-from-keyboard status.\n",
        )
    };
}
macro_rules! lock_ui_detail {
    () => {
        "@lockui - Toggles the locked state of the UI layout.\n"
    };
}

/// The note printed before every help text.
pub const HELP_PREFIX_NOTE: &str =
    "\nNote: You may substitute a forward slash (/) for the at symbol (@).\n\n";
/// The line before a verb's help text (retail leaves `<command>` as is).
pub const FOR_MORE_INFORMATION_PREFIX: &str = "For more information, type @help <command>.\n";
/// The answer for a verb with no help.
pub const UNKNOWN_COMMAND: &str = "Unknown command";

/// `@tell`.
pub const TELL: &str = "@tell <name>, <text> - Sends a long-distance, private message to the specified character. Note that you must put a comma after the character's name.";
/// `@reply`.
pub const REPLY: &str = "@reply <text> - Sends the text to the last person who @tell'd you. You may also use @r or @rp.";
/// `@retell`.
pub const RETELL: &str =
    "@retell <text> - Sends the text to the last person you @tell'd. You may also use @rt.";
/// `@mr` (retail's double space kept).
pub const MONARCH_REPLY: &str = "@mr <text> - Sends the text to the last person who used @m to send  you a message. This only works for monarchs.";
/// `@pr` (retail's double space kept).
pub const PATRON_REPLY: &str =
    "@pr <text> - Sends the text to the last vassal who used @p to send  you a message.";
/// `@day`.
pub const DAY: &str = day_line!();
/// `@log`.
pub const LOG: &str = concat!(
    "@log <name> - Echoes chat text to a logfile. All the information that appears in your chat window after you type this command will be copied into a text file. Choose the file you are copying to by naming it in the command. If this file already exists, it will add the additional text to the end of it. To turn off logging, simply retype @log.\n",
    "@log AClog.txt - Echoes chat text to a log file named Aclog.txt in your Asheron's Call directory. After you use this command, all the information that appears in your chat window will be written to a file in your Asheron's Call directory named Aclog.txt.\n",
    "@log - If you are currently copying the text in your chat window to a logfile, this command will stop the process.\n",
);
/// `@render`.
pub const RENDER: &str = concat!(
    "Usage:\n",
    "@render <option> <value>\n",
    "  radius #        : set landscape radius (between 5 and 25)\n",
    "  fov #           : set field of view (between 10 and 160)\n",
);
/// `@motd` (retail's "clear-" kept).
pub const MOTD: &str = concat!(
    "@allegiance motd - Displays the message of the day for your allegiance.\n",
    "@allegiance motd set <text> - Sets the MOTD. Can only be used by monarchs.\n",
    "@allegiance motd clear- Clears the MOTD. Can only be used by monarchs.\n",
);
/// `@allegiance`.
pub const ALLEGIANCE_OVERVIEW: &str = concat!(
    "@allegiance - Commands to help manage your allegiance.\n",
    "@allegiance boot [-account] <name> - Removes a character from your allegiance.\n",
    "@allegiance ban <add/remove> <name> - Bans all characters on the given character's account from your allegiance (and boots them too!)\n",
    "@allegiance ban list - List the characters whose accounts are banned from your allegiance.\n",
    allegiance_warning_line!(),
    "@allegiance info <name> - Requests information on a member of your allegiance.\n",
    "@allegiance chat <on/off> - Turn allegiance chat on and off.\n",
    "@allegiance chat kick <name>[, <reason>] - Kick a player temporarily from the allegiance chat room.\n",
    "@allegiance chat gag <name> - Gags a player so that they cannot see or speak in the allegiance chat room for 5 minutes.\n",
    "@allegiance chat ungag <name> - Ungags a gagged allegiance member so that they may once again see and speak in the allegiance chat room.\n",
    "@allegiance broadcast <message> - Broadcast a message to the entire allegiance. Limited to 10/day. Also: @ab\n",
    "@allegiance officer <add/set> <level #> <name> - Assigns the position of officer, with the given level of permissions, to the named character.\n",
    "@allegiance officer <remove> <name> - Removed the named character as an allegiance officer.\n",
    "@allegiance officer clear - Clears all officer positions.\n",
    "@allegiance officer [list] - list your allegiance officer. Can be used by anyone in an allegiance.\n",
    "@allegiance title set <level #> <title> - Sets the title of the given officer level.\n",
    "@allegiance title clear - Clears all officer titles.\n",
    "@allegiance title [list] - Lists all the officer titles for your allegiance.\n",
    "@allegiance name <set/clear> - Displays, sets, or clears the name of your allegiance.\n",
    "@allegiance lock <on/off/toggle/check> - Locks, unlocks, or displays the locked state of your allegiance.\n",
    "@allegiance lock bypass <clear/name> - Sets, clears, or displays a single character as an approved vassal. That character may then swear into a locked allegiance.\n",
    "@allegiance hometown - Recalls you to your allegiance bindstone, if your allegiance has tied to one.\n",
    "@allegiance motd - Displays or sets the message of the day for your allegiance.\n",
);
/// `@house`.
pub const HOUSE_OVERVIEW: &str = concat!(
    house_one_liner!(),
    "@house abandon - Abandons your house.\n",
    "@house boot <name> - Removes a player from your house.\n",
    "@house boot -all - Removes everyone from your house.\n",
    "@house guest add <name> - Adds players to your house guest list.\n",
    "@house guest remove <name> - Removes players from your house guest list.\n",
    "@house guest add_allegiance - Adds your allegiance to the guest list.\n",
    "@house guest remove_allegiance - Removes your allegiance from the guest list.\n",
    "@house guest remove_all - Removes all guests from your house guest list.\n",
    "@house guest list - Shows the current guest list.\n",
    "@house recall - Teleports you to your house.\n",
    "@house storage add <name> - Gives a player permission to use your house storage.\n",
    "@house storage remove <name> - Removes permission to use your house storage from a player.\n",
    "@house storage add_allegiance - Grants storage permission to your allegiance.\n",
    "@house storage remove_allegiance - Removes storage permission from your allegiance.\n",
    "@house storage remove_all - Removes all storage permissions from guests.\n",
    "@house open - Creates an open house.\n",
    "@house close - Closes your house.\n",
    "@house hooks on|off - Makes the hooks in your house visible or invisible.\n",
    "@house mansion_recall - Teleports you to your allegiance mansion or villa.\n",
    "@house alleg_recall - Teleports you to your allegiance mansion or villa.\n",
    "@house available - See @hslist\n",
);
/// `@help death`.
pub const DEATH_GROUP_DETAIL: &str = death_group_detail!();
/// `@help status`.
pub const STATUS_GROUP_DETAIL: &str = status_group_detail!();
/// `@help text`.
pub const TEXT_GROUP_DETAIL: &str = text_group_detail!();
/// `@help allegiances`.
pub const ALLEGIANCES_GROUP_DETAIL: &str = allegiances_group_detail!();
/// `@help channels`.
pub const CHANNELS_GROUP_DETAIL: &str = channels_group_detail!();
/// `@help chatting`.
pub const CHATTING_GROUP_DETAIL: &str = chatting_group_detail!();
/// `@help commands`: every group, the user-interface lines twice as retail
/// prints them.
pub const COMMANDS_GROUP_DETAIL: &str = concat!(
    allegiances_group_detail!(),
    channels_group_detail!(),
    chatting_group_detail!(),
    death_group_detail!(),
    "@emote - Performs a text emote.\n@emotes - Lists all standard emotes.\n",
    "@fillcomps - Helps you buy components in bulk.\n",
    "@saveui <filename> - Saves the current user interface.\n",
    "@loadui <filename> - Loads a previously saved user interface.\n",
    "@saveui <filename> - Saves the current user interface.\n",
    "@loadui <filename> - Loads a previously saved user interface.\n",
    lock_ui_detail!(),
    "@friends - Helps you manage your friends list.\n",
    house_one_liner!(),
    "@squelch - Squelches a character or account.\n",
    "@unsquelch - Unsquelches a squelched character or account.\n",
    "@messagetypes - Lists all types of messages that can be squelched or filtered.\n",
    status_group_detail!(),
    text_group_detail!(),
);
/// `@help` alone: the help topics.
pub const AVAILABLE_HELP_LISTING: &str = concat!(
    "Available help:\n",
    "@help allegiances - Commands to help you deal with your Allegiance.\n",
    "@help channels - How to communicate with people in your allegiance or fellowship.\n",
    "@help chatting - How to chat publically and privately.\n",
    "@help death - Commands for making, finding, and looting corpses.\n",
    "@help emote - How to perform text and action emotes.\n",
    "@help fillcomps - A command to help you buy components in bulk.\n",
    "@help friends - Commands to help you manage your friends list.\n",
    "@help house - Commands that help you manage your house, including guest and storage management.\n",
    "@help squelch - Commands that let you block out messages from other players.\n",
    "@help status - Commands that display useful information.\n",
    "@help text - Commands that help you manage your text window.\n",
    "@help commands - Lists all commands.\n",
);

/// `@lifestone`.
pub const LIFESTONE_DETAIL: &str =
    "@lifestone - Returns you to the last lifestone you used without killing you.\n";
/// `@marketplace`.
pub const MARKETPLACE_DETAIL: &str = "@marketplace - Teleports you to the Marketplace of Dereth.\n";
/// `@pkarena`.
pub const PK_ARENA_DETAIL: &str =
    "@pkarena - Teleports you to the PK Arena. You must be PK to use this command.\n";
/// `@pklarena`.
pub const PK_LITE_ARENA_DETAIL: &str =
    "@pklarena - Teleports you to the PKL Arena. You must be PKL to use this command.\n";
/// `@pklite`.
pub const PK_LITE_DETAIL: &str = "@pklite - Sets your status to Player Killer Lite (PK Lite). PK Lite characters can attack other PK Lite characters. They cannot, however, attack Player Killer (PK) characters. PK Lite characters operate under the same combat rules as PK characters, except that if you are killed  in a PK Lite battle, you will not accrue vitae and you will not drop any coins or items. Only Non-Player Killers may use this command to enter PK Lite. Dying in a PK Lite battle and logging off will restore your status to Non-Player Killer.\n";
/// `@age`.
pub const AGE_DETAIL: &str = "@age - Displays your total gameplay time.\n";
/// `@birth`.
pub const BIRTH_DETAIL: &str = "@birth - Displays when your character was created.\n";
/// `@framerate`.
pub const FRAME_RATE_DETAIL: &str = "@framerate - Toggles the framerate display.\n";
/// `@lockui`.
pub const LOCK_UI_DETAIL: &str = lock_ui_detail!();
/// `@version`.
pub const VERSION_DETAIL: &str =
    "@version - Tells you what version of the software you are using.\n";
/// `@loc`.
pub const LOC_DETAIL: &str = "@loc - Displays your current position in your chat window. Use this information when you wish to submit a bug report.\n";
/// `@corpse`.
pub const CORPSE_DETAIL: &str = "@corpse - Displays the location of your last outdoor death. Even if your corpse has disappeared or if you have subsequently died indoors, typing this command will display your last outdoor corpse location.\n";
/// `@die`.
pub const DIE_DETAIL: &str = "@die - If you wish to kill your character and leave a corpse, you may use the @die command.  This will result in your character's death, you will leave behind a corpse with some of your items, and you will appear at your lifestone.  If you wish to travel to your lifestone without leaving behind a corpse, you may use the @lifestone command.\n";
/// `@clear`.
pub const CLEAR_DETAIL: &str = "@clear - Clears the chat box of all text.\n";
/// `@saveui`.
pub const SAVE_UI_DETAIL: &str = "@saveui <filename> - Saves the current user interface layout to disk using the provided file name. If no file name is provided the layout is saved with a name that is unique for your server, character and resolution.\n";
/// `@loadui` (retail's text has no line break).
pub const LOAD_UI_DETAIL: &str = "@loadui <filename> - Loads a previously saved user interface layout from disk using the provided file name";
/// `@saveautoui`.
pub const SAVE_AUTO_UI_DETAIL: &str = "@saveautoui - Stores the current layout to a character and resolution specific file. This layout will automatically be used when the resolution changes for this character to the current size.\n";
/// `@loadautoui`.
pub const LOAD_AUTO_UI_DETAIL: &str =
    "@loadautoui - Forces a previously saved layout to load for this user and resolution.";
/// `@afk`.
pub const AFK_DETAIL: &str = "@afk - Turns on AFK (away-from-keyboard) mode. When set to AFK, other players that send you directed chatyou will receive a customizable message that your are not currently at the keyboard.\n@afk on - Turns on AFK mode. When set to AFK, other players that send you directed chatyou will receive a customizable message that your are not currently at the keyboard.\n@afk off - Turn off AFK mode.\n@afk msg <message> - Set the message that will be sent to players that send you directed chat while you are in AFK mode. Issuing \"@afk msg\" with no message will set your AFK message back to the default. Your custom AFK message is limited to 192 characters.\n";
/// `@consent`.
pub const CONSENT_DETAIL: &str = "The @consent commands allow you to display and manage your corpse-looting consent list. This list lets you control whether others may permit you to loot their corpse and also allows you to monitor who has given you permission. You may have a maximum of 20 separate permissions at any given time. You will not be able to loot a corpse that was the victim of a player killer, even if its owner has given you permission. Also, players who have squelched you are not able to permit you to loot their corpse. Note that you can toggle your consent on/off via the Character Options panel as well as through these commands.\n@consent on - Turns on your ability to accept permissions from other players.\n@consent off - Turns off your ability to accept permissions from other players.\n@consent who - Lists those who have given you permission to loot their corpses.\n@consent remove <name> - Removes the permission a player granted to you.\n@consent clear - Clears your entire consent list.\n\n";
/// `@emote`.
pub const EMOTE_DETAIL: &str = "The @emote command causes your character to emote some text, by performing an action in the third person. For example, if you typed the following while logged in as a character named Arville:\n    @emote looks around the town curiously.\nthen the chat windows of everyone around you would display:\n    Arville looks around the town curiously.\nYou can use any of these shorter forms of the command as well:\n         @e <text>\n         @em <text>\n         ; <text>\n         : <text>\n\nYou can also use a variety of standard emotes. These emotes come with special animations as well as text. Type @emotes to see a list.\n\n";
/// `@emotes`.
pub const EMOTE_LIST_DETAIL: &str = "Standard Emotes:\nNote: These commands should be bound on either side by asterisks. (Example: *wave*)\nShakeFist; Beckon; BeSeeingYou; BlowKiss; BowDeep; ClapHands; Cry; Laugh; Nod; Point; Shrug; Wave; Akimbo; HeartyLaugh; Salute; TapFoot; WaveHigh; WaveLow; Yawn; Stretch; Cringe; Kneel; Plead; Shiver; Shoo; Slouch; Spit; Surrender; Woah; Winded; YMCA; Eat; Drink; Teapot; Pray; Mock; Cheer; Helper; Warm Hands; Scratch Head; Shake Head\n\n";
/// `@friends`.
pub const FRIENDS_DETAIL: &str = "Every time someone on your friends list logs in or out, you will receive notification. In addition, you can query the online status of your friends list at any time. Your friends list can contain up to 50 characters.\n@friends - Shows all your current friends and indicates if any of them are online.\n@friends online - Shows your current online friends.\n@friends add <name> - Adds a character to your friends list.\n@friends remove <name> - Removes a character from your friends list.\n@friends remove -all - Clears your friends list.\n@friends old - Shows the characters who were on your old-style friends list prior to the January 2006 update, so you can move them to your new-style friends list if necessary.\n";
/// `@squelch`, `@unsquelch`.
pub const SQUELCH_DETAIL: &str = "The @squelch commands let you block out messages from specific characters or players. The @unsquelch commands lets squelched messages reach you again. Use the options on these commands to squelch all message types or just some types of messages; one character or an entire account. You may have up to 32 players squelched at once. Note that NPCs cannot be permanently squelched.\n\n@squelch - Shows the current list of squelched characters.\n@squelch [-account] <name> - Squelches all messages from a character. With the account flag, this command also stops everything except normal chat coming from the target's other characters.\n@squelch [-message_type] <character> - This will filter out all text messages of a certain type  from a specific character.  For example, the following will filter out all tell messages from Oswald:\n     Example: @squelch -tell Oswald.\n@squelch -reply [-account] [-message_type] - This filters out all text messages from whoever last tell'd you.  You may also use the -account flag and/or limit the squelch by indicating specific message types. For example, this will filter out all tell messages from the account of Oswald, assuming that Oswald was the last person who sent you an @tell:\n     Example: @squelch -reply -account -tell\n\n@unsquelch - Shows the current list of squelched characters.\n@unsquelch <name> - Removes all squelches from a character, including account squelch.\n@unsquelch [-message_type] <character> : This allows text messages of type message_type to come from a squelched character. For example the following allows assessment messages from a character name Oswald:\n     Example: @unsquelch -assessment Oswald\n@unsquelch -reply [-account] [-message_type] : This allows text messages of type message_type from whoever last sent you an @tell. For example, the following will allow any character on Oswald's account to once again send you @tells, assuming that Oswald was the last person who sent you an @tell:\n     Example: @squelch -reply -account -tell\n\nType @messagetypes for a complete list of message types.\n";
/// `@filter`.
pub const FILTER_DETAIL: &str = "The @filter commands filter out all incoming messages of a certain type. Type @messagetypes to see a list of the message types that you can filter.\n@filter - List all the filters currently in place.\n@filter <-message_type> - Filters out all incoming messages of a specific type. For example, the following will filter out all spellcasting text: \n     Example: @filter -spellcasting\n@filter -all - Filters out all incoming messages of all types.\n\n";
/// `@unfilter`.
pub const UNFILTER_DETAIL: &str = "The @unfilter commands remove specific filters from your incoming messages. For a complete list of message types that you can filter, type @help messagetypes.\n@unfilter <-message_type> - Removes filters on incoming messages of a specific type.  For example, the following allows spellcasting text to resume:\n     Example: @unfilter -spellcasting\n@unfilter -all - Removes all filters on incoming messages of all types.\n";
/// `@messagetypes`: the message types that can be squelched or filtered.
pub const MESSAGE_TYPES_DETAIL: &str = "Squelch channels are as follows:\n  Speech, Tell, Combat, Magic, Emote, Appraisal, Spellcasting, Allegiance, Fellowship, Combat_Enemy, Combat_Self, Recall, Craft, Salvaging\n";
/// `@fillcomps`.
pub const FILL_COMPS_DETAIL: &str = "The @fillcomps command assists in the bulk purchase of spell components. It is the sole interface for filling the buy list, which is the column of red zeros to the right in your components panel. To designate which components you would like to buy, change the zeros to the number of each component you would like to buy. The types of components you can buy are scarabs, herbs, powders, potions, and talismans.\n\nThis is the proper syntax: @fillcomps <component type> <pyreal value>\n\n@fillcomps - Fills the buy list with all of the components that are desired.\n@fillcomps <component type> - Fills the buy list with all of the components of the given type.\n@fillcomps <pyreal value> - Fills the buy list with all of the components until the total price of the components exceeds the given value.\n@fillcomps <component type> <pyreal value> - Fills the buy list with all of the components of the given type until the total price of components exceeds the given value.\n@fillcomps clear - Sets the requested amount for all components to zero.\n";
/// `@endurance`.
pub const ENDURANCE_DETAIL: &str = "The endurance attribute has a number of abilities tied to it.\nFirst, some combination of strength and endurance (with endurance being more important) now allows one to regenerate hit points at a faster rate the higher one's endurance is.  This bonus is in addition to any regeneration spells one may have placed upon themselves.  This endurance regeneration bonus caps at around 110%.\nSecond, the higher a player's Endurance, the less stamina one uses while attacking.  This benefit is tied to Endurance only, and it caps out at around 50% less stamina used per attack.  The minimum stamina used per attack remains one.\nThird, the higher a player's Endurance, the more likely they are not to use a point of stamina to successfully evade a missile or melee attack.  A player is required to have Melee Defense for melee attacks or Missile Defense for missile attacks trained or specialized in order for this specific ability to work.  This benefit is tied to Endurance only, and it caps out at around a 75% chance to avoid losing a point of stamina per successful evasion.\nFourth, some combination of strength and endurance (the two are roughly of equivalent importance) now allows one to partially resist drain and harm attacks, up to a maximum of roughly 50%.\nFifth, some combination of strength and endurance (the two are roughly of equivalent importance) now allows one to have a level of \"natural resistances\" to the 7 damage types, the same as a certain level of life protections.  This caps out at a 50% resistance (the equivalent to level 5 life prots) to these damage types.  This resistance is not additive to life protections: higher level life protections will overwrite these natural resistances, although life vulns will take these natural resistances into account, if the player does not have a higher level life protection cast upon him.\nThe natural resistances, drain resistances, and regeneration rate info are now visible on the Character Information Panel, in what was once the Burden panel.  This panel now displays the above three Endurance benefits, the burden info, as well as information about your age, birth date, and number of deaths.\nThe 5 categories for the endurance benefits are, in order from lowest benefit to highest: Poor, Mediocre, Hardy, Resilient, and Indomitable, with each range of benefits divided up equally amongst the 5 (e.g. Poor describes having anywhere from 1-10% resistance against drain health attacks, etc.).\n";
/// `@speaker`.
pub const SPEAKER_DETAIL: &str =
    "@speaker - No longer used, see @allegiance officer for a similar command.\n";
/// `@title`.
pub const TITLE_DETAIL: &str = "@title <new title> - Sets the title of the popup chat window.\n";
/// `@chat`.
pub const CHAT_TOGGLE_DETAIL: &str = "@chat <on/off> - Sets whether or not you receive normal chat. When set to \"off\", you will no longer receive any spoken speech (normal chat).  However, you will still receive tells.\n";
/// `@notell`.
pub const NO_TELL_DETAIL: &str = "@notell <on/off> - Sets whether or not you receive @tells. When set to \"on\", you will not receive any tells.\n";
/// `@join`.
pub const JOIN_CHAT_DETAIL: &str =
    "@join <channel tag> - Allows you to hear and speak on the given channel.\n";
/// `@leave`.
pub const LEAVE_CHAT_DETAIL: &str =
    "@leave <channel tag> - Prevents you from hearing or speaking on the given channel.\n";
/// `@permit`.
pub const PERMIT_DETAIL: &str = "The @permit command gives or revokes corpse-looting permissions to other players. You can permit other players to loot any one of your corpses. You may not @permit a player again until he or she has looted your corpse. Permissions expire either after one hour or when the permitted player logs off. If you were killed by a player killer, no one can loot your corpse except you or your killer, even if you give someone else permission.\n@permit add <name> - Allows  another player to loot your corpse.\n@permit remove <name> - Removes permission to access your corpse from the named character.\nType @help consent for more details on corpse looting.\n";
/// `@hslist`.
pub const HSLIST_DETAIL: &str = "@hslist <house type> - Lists the number and, if appropriate, positions of houses currently available for purchase. Types include: Apartment, Cottage, Villa, Mansion\n";

/// The client command verbs retail gives their own help text, matched
/// ignoring case.
const CATALOG_VERB_DETAIL: &[(&str, &str)] = &[
    ("log", LOG),
    ("day", DAY),
    ("render", RENDER),
    ("motd", MOTD),
    ("lifestone", LIFESTONE_DETAIL),
    ("lif", LIFESTONE_DETAIL),
    ("ls", LIFESTONE_DETAIL),
    ("marketplace", MARKETPLACE_DETAIL),
    ("mar", MARKETPLACE_DETAIL),
    ("mp", MARKETPLACE_DETAIL),
    ("pkarena", PK_ARENA_DETAIL),
    ("pka", PK_ARENA_DETAIL),
    ("pklarena", PK_LITE_ARENA_DETAIL),
    ("pla", PK_LITE_ARENA_DETAIL),
    ("pklite", PK_LITE_DETAIL),
    ("pkl", PK_LITE_DETAIL),
    ("hor", HOUSE_OVERVIEW),
    ("hr", HOUSE_OVERVIEW),
    ("hom", HOUSE_OVERVIEW),
    ("hoa", HOUSE_OVERVIEW),
    ("age", AGE_DETAIL),
    ("birth", BIRTH_DETAIL),
    ("framerate", FRAME_RATE_DETAIL),
    ("lockui", LOCK_UI_DETAIL),
    ("version", VERSION_DETAIL),
    ("loc", LOC_DETAIL),
    ("corpse", CORPSE_DETAIL),
    ("cor", CORPSE_DETAIL),
    ("die", DIE_DETAIL),
    ("clear", CLEAR_DETAIL),
    ("saveui", SAVE_UI_DETAIL),
    ("loadui", LOAD_UI_DETAIL),
    ("saveautoui", SAVE_AUTO_UI_DETAIL),
    ("loadautoui", LOAD_AUTO_UI_DETAIL),
    ("afk", AFK_DETAIL),
    ("consent", CONSENT_DETAIL),
    ("e", EMOTE_DETAIL),
    ("em", EMOTE_DETAIL),
    ("emote", EMOTE_DETAIL),
    ("me", EMOTE_DETAIL),
    ("emotes", EMOTE_LIST_DETAIL),
    ("friends", FRIENDS_DETAIL),
    ("friends_add", FRIENDS_DETAIL),
    ("friends_remove", FRIENDS_DETAIL),
    ("squelch", SQUELCH_DETAIL),
    ("unsquelch", SQUELCH_DETAIL),
    ("filter", FILTER_DETAIL),
    ("unfilter", UNFILTER_DETAIL),
    ("messagetypes", MESSAGE_TYPES_DETAIL),
    ("message_types", MESSAGE_TYPES_DETAIL),
    ("msgtypes", MESSAGE_TYPES_DETAIL),
    ("msg_types", MESSAGE_TYPES_DETAIL),
    ("fillcomps", FILL_COMPS_DETAIL),
    ("endurance", ENDURANCE_DETAIL),
    ("speaker", SPEAKER_DETAIL),
    ("title", TITLE_DETAIL),
    ("chat", CHAT_TOGGLE_DETAIL),
    ("notell", NO_TELL_DETAIL),
    ("join", JOIN_CHAT_DETAIL),
    ("leave", LEAVE_CHAT_DETAIL),
    ("permit", PERMIT_DETAIL),
    ("hslist", HSLIST_DETAIL),
    ("alh", ALLEGIANCE_OVERVIEW),
    ("ah", ALLEGIANCE_OVERVIEW),
];

/// Client command verbs retail has no help for: they answer
/// [`UNKNOWN_COMMAND`].
const CATALOG_VERBS_WITH_NO_HELP: &[&str] = &["index", "clist", "on", "off"];

const FELLOWSHIP_ONE_LINER: &str = "Sends text to your Fellowship channel.";
const ALLEGIANCE_ONE_LINER: &str = "@a - Sends a message to your Allegiance. Also: @guild, @gu";
const GENERAL_ONE_LINER: &str =
    "@general - Sends a message to the global General chat channel. Also: @cg";
const TRADE_ONE_LINER: &str =
    "@trade - Sends a message to the global Trade chat channel. Also: @ct";
const LFG_ONE_LINER: &str =
    "@lfg - Sends a message to the global Looking For Group (LFG) chat channel. Also: @clfg";
const ROLEPLAY_ONE_LINER: &str =
    "@roleplay - Sends a message to the global Roleplay chat channel. Also: @crp";
const SOCIETY_ONE_LINER: &str =
    "@society - Sends a message to the your Society chat channel. Also: @soc";
const OLTHOI_ONE_LINER: &str =
    "@olthoi - If you are an Olthoi, sends a message to the global Olthoi chat channel. Also: @o";
const SAY_ONE_LINER: &str = "@say <text> - Speaks the text aloud to nearby players.";
const MONARCH_ONE_LINER: &str = "Sends text to your Monarch.";
const PATRON_ONE_LINER: &str = "Sends text to your Patron.";
const VASSALS_ONE_LINER: &str = "Sends text to your Vassals.";
const COVASSALS_ONE_LINER: &str = "Sends text to your Co-vassals.";

/// Chat verbs and help topics (not client commands), matched ignoring case.
const BY_VERB: &[(&str, &str)] = &[
    ("say", SAY_ONE_LINER),
    ("s", SAY_ONE_LINER),
    ("tell", TELL),
    ("t", TELL),
    ("send", TELL),
    ("whisper", TELL),
    ("w", TELL),
    ("reply", REPLY),
    ("r", REPLY),
    ("rp", REPLY),
    ("retell", RETELL),
    ("rt", RETELL),
    ("mr", MONARCH_REPLY),
    ("pr", PATRON_REPLY),
    ("day", DAY),
    ("log", LOG),
    ("render", RENDER),
    ("motd", MOTD),
    ("commands", COMMANDS_GROUP_DETAIL),
    ("allegiances", ALLEGIANCES_GROUP_DETAIL),
    ("channels", CHANNELS_GROUP_DETAIL),
    ("chatting", CHATTING_GROUP_DETAIL),
    ("death", DEATH_GROUP_DETAIL),
    ("status", STATUS_GROUP_DETAIL),
    ("text", TEXT_GROUP_DETAIL),
    ("f", FELLOWSHIP_ONE_LINER),
    ("fellow", FELLOWSHIP_ONE_LINER),
    ("fellows", FELLOWSHIP_ONE_LINER),
    ("fellowship", FELLOWSHIP_ONE_LINER),
    ("g", FELLOWSHIP_ONE_LINER),
    ("group", FELLOWSHIP_ONE_LINER),
    ("party", FELLOWSHIP_ONE_LINER),
    ("a", ALLEGIANCE_ONE_LINER),
    ("guild", ALLEGIANCE_ONE_LINER),
    ("gu", ALLEGIANCE_ONE_LINER),
    (
        "ab",
        "Broadcasts text to your entire allegiance (monarch/speaker permission). Also @allegiance broadcast.",
    ),
    ("general", GENERAL_ONE_LINER),
    ("cg", GENERAL_ONE_LINER),
    ("trade", TRADE_ONE_LINER),
    ("ct", TRADE_ONE_LINER),
    ("lfg", LFG_ONE_LINER),
    ("clfg", LFG_ONE_LINER),
    ("roleplay", ROLEPLAY_ONE_LINER),
    ("crp", ROLEPLAY_ONE_LINER),
    ("society", SOCIETY_ONE_LINER),
    ("soc", SOCIETY_ONE_LINER),
    ("olthoi", OLTHOI_ONE_LINER),
    ("o", OLTHOI_ONE_LINER),
    ("m", MONARCH_ONE_LINER),
    ("monarch", MONARCH_ONE_LINER),
    ("p", PATRON_ONE_LINER),
    ("patron", PATRON_ONE_LINER),
    ("v", VASSALS_ONE_LINER),
    ("vassal", VASSALS_ONE_LINER),
    ("vassals", VASSALS_ONE_LINER),
    ("c", COVASSALS_ONE_LINER),
    ("covassal", COVASSALS_ONE_LINER),
    ("covassals", COVASSALS_ONE_LINER),
    ("co-vassals", COVASSALS_ONE_LINER),
];

fn lookup(table: &[(&str, &'static str)], verb: &str) -> Option<&'static str> {
    let verb = verb.trim_end_matches(',');
    table
        .iter()
        .find(|(key, _)| key.eq_ignore_ascii_case(verb))
        .map(|(_, text)| *text)
}

/// Retail's own help text for a client command verb.
#[must_use]
pub fn catalog_verb_detail(verb: &str) -> Option<&'static str> {
    lookup(CATALOG_VERB_DETAIL, verb)
}

/// The help text of a chat verb or help topic.
#[must_use]
pub fn help_text(verb: &str) -> Option<&'static str> {
    lookup(BY_VERB, verb)
}

/// The command catalog's own help: the house and allegiance overviews for
/// the verbs that take a subcommand. (Every other client command verb has
/// retail detail text or none; `catalog::tests` pins that.)
#[must_use]
pub fn catalog_help_text(verb: &str) -> Option<&'static str> {
    let verb = verb.trim_end_matches(',');
    if verb.eq_ignore_ascii_case("house") || verb.eq_ignore_ascii_case("hou") {
        Some(HOUSE_OVERVIEW)
    } else if verb.eq_ignore_ascii_case("allegiance") || verb.eq_ignore_ascii_case("all") {
        Some(ALLEGIANCE_OVERVIEW)
    } else {
        None
    }
}

/// True for a client command verb retail has no help for.
#[must_use]
pub fn has_no_retail_help(verb: &str) -> bool {
    let verb = verb.trim_end_matches(',');
    CATALOG_VERBS_WITH_NO_HELP
        .iter()
        .any(|v| v.eq_ignore_ascii_case(verb))
}

/// The lines `/help` (`verb` empty) or `/help <verb>` prints, in order.
#[must_use]
pub fn lines(verb: &str) -> Vec<String> {
    let verb = verb.trim();
    if verb.is_empty() {
        return vec![
            HELP_PREFIX_NOTE.to_owned(),
            AVAILABLE_HELP_LISTING.to_owned(),
        ];
    }
    let normalized = verb.trim_start_matches(['/', '@']);
    if has_no_retail_help(normalized) {
        return vec![UNKNOWN_COMMAND.to_owned()];
    }
    let text = catalog_verb_detail(normalized)
        .or_else(|| catalog_help_text(normalized))
        .or_else(|| help_text(normalized));
    match text {
        Some(text) => vec![
            HELP_PREFIX_NOTE.to_owned(),
            format!("{FOR_MORE_INFORMATION_PREFIX}{text}"),
        ],
        None => vec![UNKNOWN_COMMAND.to_owned()],
    }
}

/// The help request in a typed line, if it is one: `Some("")` for a bare
/// `/help`, `/?`, `@help` or `@?`, `Some(verb)` for one followed by a verb.
#[must_use]
pub fn help_request(trimmed: &str) -> Option<&str> {
    for bare in ["/help", "/?", "@help", "@?"] {
        if trimmed.eq_ignore_ascii_case(bare) {
            return Some("");
        }
    }
    for prefix in ["/help ", "@help ", "/? ", "@? "] {
        if trimmed.len() >= prefix.len()
            && trimmed.is_char_boundary(prefix.len())
            && trimmed[..prefix.len()].eq_ignore_ascii_case(prefix)
        {
            let space = trimmed.find(' ').unwrap_or(trimmed.len());
            return Some(trimmed[space + 1..].trim());
        }
    }
    None
}

#[cfg(test)]
#[path = "help_tests.rs"]
mod tests;
