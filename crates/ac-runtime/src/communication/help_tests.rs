// Golden text: OpenAC tests/AcDream.UI.Abstractions.Tests/Panels/Chat/RetailCommandHelpTableTests.cs.

use std::collections::HashSet;

use super::*;
use crate::communication::catalog;

#[test]
fn the_death_group_is_retail_s_whole_listing() {
    assert_eq!(
        DEATH_GROUP_DETAIL,
        "@permit - Commands to give or revoke permission for others to loot your corpse.\n\
         @consent - Commands to help you manage the corpse-looting permissions that others give you.\n\
         @corpse - Displays the location of your last outdoor death.\n\
         @die - Kills your character and leaves a corpse, returning you to your lifestone.\n\
         @lifestone - Returns you to the last lifestone you used without killing you.\n\
         @marketplace - Teleports you to the Marketplace of Dereth.\n\
         @pkarena - Teleports you to the PK Arena. You must be PK to use this command.\n\
         @pklarena - Teleports you to the PKL Arena. You must be PKL to use this command.\n"
    );
    assert_eq!(help_text("death"), Some(DEATH_GROUP_DETAIL));
}

#[test]
fn the_status_group_keeps_retail_s_trailing_space() {
    assert_eq!(
        STATUS_GROUP_DETAIL,
        "@age - Displays your total gameplay time.\n\
         @birth - Displays when your character was created.\n\
         @day - A toggle that lightens the outdoor landscape. Note that this command may take several seconds to take effect. \n\
         @endurance - Explains how endurance affects your character.\n\
         @framerate - Toggles the framerate display.\n\
         @loc - Displays your current position.\n\
         @pklite - Sets your status to Player Killer Lite. Type @help pklite for more details.\n\
         @version - Tells you what version of the software you are using.\n"
    );
}

#[test]
fn the_text_and_allegiances_groups() {
    assert_eq!(
        TEXT_GROUP_DETAIL,
        "@clear - Clears the chat box of all text.\n\
         @filter - Commands to filter out incoming messages.\n\
         @unfilter - Commands to remove filters from incoming messages.\n\
         @loadfile - Reads in the given text file and executes each line in the chat entry field.\n\
         @log - Commands to echo chat text to a logfile.\n\
         @title <new title> - Sets the title of the popup chat window.\n"
    );
    assert_eq!(
        ALLEGIANCES_GROUP_DETAIL,
        "@allegiance - Commands to help manage your allegiance.\n\
         @allegiance motd - Displays or sets the message of the day for your allegiance, see @help motd for more information.\n"
    );
}

#[test]
fn the_channels_and_chatting_groups() {
    let channels = "@a - Sends a broadcast to your Allegiance.\n\
         @c - Sends a broadcast to your Co-vassals.\n\
         @m - Sends a broadcast to your Monarch.\n\
         @p - Sends a broadcast to your Patron.\n\
         @v - Sends a broadcast to your Vassals.\n\
         @f - Sends a broadcast to your Fellowship.\n";
    assert_eq!(CHANNELS_GROUP_DETAIL, channels);
    assert_eq!(help_text("channels"), Some(CHANNELS_GROUP_DETAIL));
    assert_eq!(
        CHATTING_GROUP_DETAIL,
        format!(
            "@chat - Sets whether or not you receive normal chat.\n\
             @notell - Sets whether or not you receive @tell's.\n\
             @reply - Sends some text to the last person who @tell'd you.\n\
             @pr - Sends some text to the last person who @p'd you.\n\
             @mr - Sends some text to the last person who @m'd you.\n\
             @retell - Sends some text to the last person you @tell'd.\n\
             @say - Says some text to everyone around you.\
             @tell - Sends a private message to another character.\n\
             {channels}\
             @afk - Set your away-from-keyboard status.\n"
        )
    );
    assert_eq!(help_text("chatting"), Some(CHATTING_GROUP_DETAIL));
}

#[test]
fn the_commands_group_concatenates_every_other_group() {
    let expected = [
        ALLEGIANCES_GROUP_DETAIL,
        CHANNELS_GROUP_DETAIL,
        CHATTING_GROUP_DETAIL,
        DEATH_GROUP_DETAIL,
        "@emote - Performs a text emote.\n@emotes - Lists all standard emotes.\n",
        "@fillcomps - Helps you buy components in bulk.\n",
        "@saveui <filename> - Saves the current user interface.\n",
        "@loadui <filename> - Loads a previously saved user interface.\n",
        "@saveui <filename> - Saves the current user interface.\n",
        "@loadui <filename> - Loads a previously saved user interface.\n",
        "@lockui - Toggles the locked state of the UI layout.\n",
        "@friends - Helps you manage your friends list.\n",
        "@house - Commands that help you manage your house, including guest and storage management.\n",
        "@squelch - Squelches a character or account.\n",
        "@unsquelch - Unsquelches a squelched character or account.\n",
        "@messagetypes - Lists all types of messages that can be squelched or filtered.\n",
        STATUS_GROUP_DETAIL,
        TEXT_GROUP_DETAIL,
    ]
    .concat();
    assert_eq!(COMMANDS_GROUP_DETAIL, expected);
    assert_eq!(help_text("commands"), Some(COMMANDS_GROUP_DETAIL));
}

#[test]
fn channel_verbs_have_their_one_liners() {
    for (verb, expected) in [
        (
            "a",
            "@a - Sends a message to your Allegiance. Also: @guild, @gu",
        ),
        (
            "guild",
            "@a - Sends a message to your Allegiance. Also: @guild, @gu",
        ),
        (
            "general",
            "@general - Sends a message to the global General chat channel. Also: @cg",
        ),
        (
            "trade",
            "@trade - Sends a message to the global Trade chat channel. Also: @ct",
        ),
        (
            "lfg",
            "@lfg - Sends a message to the global Looking For Group (LFG) chat channel. Also: @clfg",
        ),
        (
            "roleplay",
            "@roleplay - Sends a message to the global Roleplay chat channel. Also: @crp",
        ),
        (
            "society",
            "@society - Sends a message to the your Society chat channel. Also: @soc",
        ),
        (
            "olthoi",
            "@olthoi - If you are an Olthoi, sends a message to the global Olthoi chat channel. Also: @o",
        ),
        ("f", "Sends text to your Fellowship channel."),
        ("monarch", "Sends text to your Monarch."),
        ("patron", "Sends text to your Patron."),
        ("vassal", "Sends text to your Vassals."),
        ("covassal", "Sends text to your Co-vassals."),
    ] {
        assert_eq!(help_text(verb), Some(expected), "{verb}");
    }
}

#[test]
fn the_note_the_prefix_and_the_unknown_answer() {
    assert_eq!(
        HELP_PREFIX_NOTE,
        "\nNote: You may substitute a forward slash (/) for the at symbol (@).\n\n"
    );
    assert_eq!(
        FOR_MORE_INFORMATION_PREFIX,
        "For more information, type @help <command>.\n"
    );
    assert_eq!(UNKNOWN_COMMAND, "Unknown command");
}

#[test]
fn the_topic_list_is_retail_s_thirteen_lines() {
    assert_eq!(
        AVAILABLE_HELP_LISTING,
        "Available help:\n\
         @help allegiances - Commands to help you deal with your Allegiance.\n\
         @help channels - How to communicate with people in your allegiance or fellowship.\n\
         @help chatting - How to chat publically and privately.\n\
         @help death - Commands for making, finding, and looting corpses.\n\
         @help emote - How to perform text and action emotes.\n\
         @help fillcomps - A command to help you buy components in bulk.\n\
         @help friends - Commands to help you manage your friends list.\n\
         @help house - Commands that help you manage your house, including guest and storage management.\n\
         @help squelch - Commands that let you block out messages from other players.\n\
         @help status - Commands that display useful information.\n\
         @help text - Commands that help you manage your text window.\n\
         @help commands - Lists all commands.\n"
    );
}

#[test]
fn single_verb_texts_are_retail_s() {
    assert_eq!(
        DIE_DETAIL,
        "@die - If you wish to kill your character and leave a corpse, you may use the @die command.  \
         This will result in your character's death, you will leave behind a corpse with some of your \
         items, and you will appear at your lifestone.  If you wish to travel to your lifestone without \
         leaving behind a corpse, you may use the @lifestone command.\n"
    );
    assert_eq!(
        LIFESTONE_DETAIL,
        "@lifestone - Returns you to the last lifestone you used without killing you.\n"
    );
    assert_eq!(catalog_verb_detail("alh"), Some(ALLEGIANCE_OVERVIEW));
    assert_eq!(catalog_verb_detail("log"), Some(LOG));
    assert!(LOG.starts_with("@log <name> - Echoes chat text to a logfile."));
    assert_eq!(
        LOG,
        "@log <name> - Echoes chat text to a logfile. All the information that appears in your chat window after you type this command will be copied into a text file. Choose the file you are copying to by naming it in the command. If this file already exists, it will add the additional text to the end of it. To turn off logging, simply retype @log.\n\
         @log AClog.txt - Echoes chat text to a log file named Aclog.txt in your Asheron's Call directory. After you use this command, all the information that appears in your chat window will be written to a file in your Asheron's Call directory named Aclog.txt.\n\
         @log - If you are currently copying the text in your chat window to a logfile, this command will stop the process.\n"
    );
    assert_eq!(
        RENDER,
        "Usage:\n@render <option> <value>\n  radius #        : set landscape radius (between 5 and 25)\n  fov #           : set field of view (between 10 and 160)\n"
    );
    assert_eq!(
        MOTD,
        "@allegiance motd - Displays the message of the day for your allegiance.\n\
         @allegiance motd set <text> - Sets the MOTD. Can only be used by monarchs.\n\
         @allegiance motd clear- Clears the MOTD. Can only be used by monarchs.\n"
    );
}

#[test]
fn the_message_types_text_and_its_aliases() {
    assert_eq!(
        MESSAGE_TYPES_DETAIL,
        "Squelch channels are as follows:\n  Speech, Tell, Combat, Magic, Emote, Appraisal, Spellcasting, Allegiance, Fellowship, Combat_Enemy, Combat_Self, Recall, Craft, Salvaging\n"
    );
    for alias in ["messagetypes", "message_types", "msgtypes", "msg_types"] {
        assert_eq!(catalog_verb_detail(alias), Some(MESSAGE_TYPES_DETAIL));
    }
}

#[test]
fn the_overviews_carry_retail_s_lines() {
    assert!(ALLEGIANCE_OVERVIEW.contains("Also: @ab\n"));
    assert!(ALLEGIANCE_OVERVIEW.contains(
        "@allegiance hometown - Recalls you to your allegiance bindstone, if your allegiance has tied to one.\n"
    ));
    assert!(HOUSE_OVERVIEW.contains("@house available - See @hslist\n"));
    assert!(HOUSE_OVERVIEW.contains("@house recall - Teleports you to your house.\n"));
    for text in [
        DAY,
        LOG,
        RENDER,
        MOTD,
        ALLEGIANCE_OVERVIEW,
        HOUSE_OVERVIEW,
        CHANNELS_GROUP_DETAIL,
        CHATTING_GROUP_DETAIL,
        COMMANDS_GROUP_DETAIL,
        MESSAGE_TYPES_DETAIL,
        AVAILABLE_HELP_LISTING,
    ] {
        for marker in ["IMPLEMENTED", "UNVERIFIED", "not yet extracted", "NOT YET"] {
            assert!(!text.contains(marker));
        }
    }
}

/// Every client command has retail detail text or none at all: 47 commands
/// with text, 4 with none (OpenAC's
/// `CatalogLeafVerbCoverage_ExtractedVsConfirmedNullVsUnverified_MatchesConsolidatedReviewCount`).
#[test]
fn every_client_command_verb_has_retail_help_or_none() {
    let mut seen = HashSet::new();
    let (mut extracted, mut confirmed_null) = (0, 0);
    for (verb, command) in catalog::known_verbs() {
        if !seen.insert(command) {
            continue;
        }
        let null = has_no_retail_help(verb);
        let detail = catalog_verb_detail(verb).is_some();
        assert!(!(null && detail), "{verb}");
        assert!(null || detail, "{verb} has no retail help");
        if null {
            confirmed_null += 1;
        } else {
            extracted += 1;
        }
    }
    assert_eq!((extracted, confirmed_null), (47, 4));
    for verb in ["house", "hou", "allegiance", "all"] {
        assert!(catalog_help_text(verb).is_some());
    }
}

#[test]
fn a_help_request_is_recognised_in_every_spelling() {
    assert_eq!(help_request("/help"), Some(""));
    assert_eq!(help_request("@?"), Some(""));
    assert_eq!(help_request("/HELP die"), Some("die"));
    assert_eq!(help_request("@? ls"), Some("ls"));
    assert_eq!(help_request("/helpme"), None);
    assert_eq!(help_request("/tell x"), None);
    assert_eq!(lines("die,"), lines("die"));
}
