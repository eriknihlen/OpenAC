//! The client's own slash commands: which verbs exist, which command each
//! one is, and whether its arguments have the right shape. What a command
//! then does is [`super::dispatcher`]'s job.

/// A client command.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ClientCommandId {
    /// `/lifestone`: recall to the last lifestone.
    LifestoneRecall,
    /// `/marketplace`: recall to the Marketplace.
    MarketplaceRecall,
    /// `/pkarena`: recall a player killer to the PK arena.
    PkArenaRecall,
    /// `/pklarena`: recall a PK-lite player to the PK-lite arena.
    PkLiteArenaRecall,
    /// `/pklite`: become PK-lite.
    EnterPkLite,
    /// `/house recall`.
    HouseRecall,
    /// `/house mansion_recall`.
    MansionRecall,
    /// `/age`.
    QueryAge,
    /// `/birth`.
    QueryBirth,
    /// `/framerate`.
    ToggleFrameRate,
    /// `/day`.
    TogglePersistentDaylight,
    /// `/render`.
    RenderOption,
    /// `/lockui`.
    ToggleUiLock,
    /// `/version`.
    ShowVersion,
    /// `/loc`.
    ShowLocation,
    /// `/corpse`.
    ShowLastCorpseLocation,
    /// `/die`.
    Die,
    /// `/clear [all]`.
    ClearChat,
    /// `/log [file]`.
    ChatLogFile,
    /// `/saveui`.
    SaveUi,
    /// `/loadui`.
    LoadUi,
    /// `/saveautoui`.
    SaveAutoUi,
    /// `/loadautoui`.
    LoadAutoUi,
    /// `/afk`.
    Away,
    /// `/consent`.
    Consent,
    /// `/emote`.
    Emote,
    /// `/emotes`.
    ListEmotes,
    /// `/friends`.
    Friends,
    /// `/friends_add`.
    FriendsAdd,
    /// `/friends_remove`.
    FriendsRemove,
    /// `/squelch`.
    Squelch,
    /// `/unsquelch`.
    Unsquelch,
    /// `/filter`.
    Filter,
    /// `/unfilter`.
    Unfilter,
    /// `/messagetypes`.
    ListMessageTypes,
    /// `/fillcomps`.
    FillComponents,
    /// `/endurance`.
    Endurance,
    /// `/speaker`: a fixed deprecation notice.
    Speaker,
    /// `/title`.
    SetChatTitle,
    /// `/chat on|off`.
    ChatToggle,
    /// `/notell on|off`.
    NoTellToggle,
    /// `/join`.
    JoinChannel,
    /// `/leave`.
    LeaveChannel,
    /// `/permit`.
    Permit,
    /// `/hslist`.
    HouseAvailableList,
    /// `/index`.
    IndexChannels,
    /// `/clist`.
    ListChannel,
    /// `/on`.
    OnChannel,
    /// `/off`.
    OffChannel,
    /// `/alh`: recall to the allegiance bindstone.
    AllegianceHometown,
    /// `/allegiance info`.
    AllegianceInfo,
    /// `/allegiance boot`.
    AllegianceBoot,
    /// `/allegiance ban`.
    AllegianceBan,
    /// `/allegiance chat`.
    AllegianceChat,
    /// `/allegiance broadcast`.
    AllegianceBroadcast,
    /// `/allegiance officer`.
    AllegianceOfficer,
    /// `/allegiance title`.
    AllegianceOfficerTitle,
    /// `/allegiance name`.
    AllegianceName,
    /// `/allegiance lock`.
    AllegianceLock,
    /// `/allegiance house`.
    AllegianceHouse,
    /// `/allegiance motd`, `/motd`.
    AllegianceMotd,
    /// `/house abandon`.
    HouseAbandon,
    /// `/house open|close`.
    HouseOpenStatus,
    /// `/house storage`.
    HouseStorage,
    /// `/house boot|remove`.
    HouseBoot,
    /// `/house boot_all|remove_all`.
    HouseBootAll,
    /// `/house guest`.
    HouseGuests,
    /// `/house hooks`.
    HouseHooks,
    /// `/allegiance` with a subcommand that does not exist.
    AllegianceUnrecognizedSubcommand,
    /// `/house` with a subcommand that does not exist.
    HouseUnrecognizedSubcommand,
}

/// What a line matched.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CatalogMatch {
    /// The command.
    pub command: ClientCommandId,
    /// Its arguments, trimmed.
    pub arguments: String,
    /// Whether the arguments have the shape the command takes.
    pub has_valid_arguments: bool,
    /// The command's own refusal for bad arguments, when it has one.
    pub invalid_arguments_text: Option<&'static str>,
}

#[derive(Clone, Copy)]
enum Arguments {
    None,
    Any,
    OnOff,
    JoinLeaveTag,
    PermitShape,
    HouseType,
    SingleToken,
    Never,
}

#[derive(Clone, Copy)]
struct Definition {
    command: ClientCommandId,
    arguments: Arguments,
    invalid: Option<&'static str>,
}

const fn def(command: ClientCommandId, arguments: Arguments) -> Definition {
    Definition {
        command,
        arguments,
        invalid: None,
    }
}

const fn refusing(
    command: ClientCommandId,
    arguments: Arguments,
    invalid: &'static str,
) -> Definition {
    Definition {
        command,
        arguments,
        invalid: Some(invalid),
    }
}

use ClientCommandId as C;

const LIFESTONE: Definition = refusing(
    C::LifestoneRecall,
    Arguments::None,
    "Please see @help lifestone for more information on how to use this command.",
);
const MARKETPLACE: Definition = refusing(
    C::MarketplaceRecall,
    Arguments::None,
    "Please see @help marketplace for more information on how to use this command.",
);
const PK_ARENA: Definition = def(C::PkArenaRecall, Arguments::None);
const PK_LITE_ARENA: Definition = def(C::PkLiteArenaRecall, Arguments::None);
const PK_LITE: Definition = def(C::EnterPkLite, Arguments::None);
const HOUSE_RECALL: Definition = def(C::HouseRecall, Arguments::None);
const MANSION_RECALL: Definition = def(C::MansionRecall, Arguments::None);
const HOUSE_ABANDON: Definition = def(C::HouseAbandon, Arguments::None);
const DIE: Definition = refusing(
    C::Die,
    Arguments::None,
    "Please see @help die for more information on how to use this command.",
);
const HOUSE_AVAILABLE: Definition = refusing(
    C::HouseAvailableList,
    Arguments::HouseType,
    "Please see @help hslist for more information on how to use this command",
);
const CHANNEL_NAME: &str = "Please specify the channel name.";
const ALLEGIANCE_HOMETOWN: Definition = def(C::AllegianceHometown, Arguments::None);
const ALLEGIANCE_MOTD: Definition = def(C::AllegianceMotd, Arguments::Any);
const SEE_HELP_ALLEGIANCE: &str =
    "Please see @help Allegiance for more information on how to use this command.";
const SEE_HELP_HOUSE: &str =
    "Please see @help House for more information on how to use this command.";

/// Every verb (without its `/` or `@`) and its command; matched ignoring
/// case. `house`/`hou` and `allegiance`/`all` take a subcommand instead.
const BY_VERB: &[(&str, Definition)] = &[
    ("lifestone", LIFESTONE),
    ("lif", LIFESTONE),
    ("ls", LIFESTONE),
    ("marketplace", MARKETPLACE),
    ("mar", MARKETPLACE),
    ("mp", MARKETPLACE),
    ("pkarena", PK_ARENA),
    ("pka", PK_ARENA),
    ("pklarena", PK_LITE_ARENA),
    ("pla", PK_LITE_ARENA),
    ("pklite", PK_LITE),
    ("pkl", PK_LITE),
    ("hor", HOUSE_RECALL),
    ("hr", HOUSE_RECALL),
    ("hom", MANSION_RECALL),
    ("hoa", MANSION_RECALL),
    ("age", def(C::QueryAge, Arguments::None)),
    ("birth", def(C::QueryBirth, Arguments::None)),
    ("framerate", def(C::ToggleFrameRate, Arguments::None)),
    ("day", def(C::TogglePersistentDaylight, Arguments::Any)),
    ("render", def(C::RenderOption, Arguments::Any)),
    ("lockui", def(C::ToggleUiLock, Arguments::None)),
    ("version", def(C::ShowVersion, Arguments::None)),
    ("loc", def(C::ShowLocation, Arguments::None)),
    ("corpse", def(C::ShowLastCorpseLocation, Arguments::Any)),
    ("cor", def(C::ShowLastCorpseLocation, Arguments::Any)),
    ("die", DIE),
    ("clear", def(C::ClearChat, Arguments::Any)),
    ("log", def(C::ChatLogFile, Arguments::Any)),
    ("saveui", def(C::SaveUi, Arguments::Any)),
    ("loadui", def(C::LoadUi, Arguments::Any)),
    ("saveautoui", def(C::SaveAutoUi, Arguments::Any)),
    ("loadautoui", def(C::LoadAutoUi, Arguments::Any)),
    ("afk", def(C::Away, Arguments::Any)),
    ("consent", def(C::Consent, Arguments::Any)),
    ("e", def(C::Emote, Arguments::Any)),
    ("em", def(C::Emote, Arguments::Any)),
    ("emote", def(C::Emote, Arguments::Any)),
    ("me", def(C::Emote, Arguments::Any)),
    ("emotes", def(C::ListEmotes, Arguments::None)),
    ("friends", def(C::Friends, Arguments::Any)),
    ("friends_add", def(C::FriendsAdd, Arguments::Any)),
    ("friends_remove", def(C::FriendsRemove, Arguments::Any)),
    ("squelch", def(C::Squelch, Arguments::Any)),
    ("unsquelch", def(C::Unsquelch, Arguments::Any)),
    ("filter", def(C::Filter, Arguments::Any)),
    ("unfilter", def(C::Unfilter, Arguments::Any)),
    ("messagetypes", def(C::ListMessageTypes, Arguments::None)),
    ("message_types", def(C::ListMessageTypes, Arguments::None)),
    ("msgtypes", def(C::ListMessageTypes, Arguments::None)),
    ("msg_types", def(C::ListMessageTypes, Arguments::None)),
    ("fillcomps", def(C::FillComponents, Arguments::Any)),
    ("endurance", def(C::Endurance, Arguments::None)),
    ("speaker", def(C::Speaker, Arguments::None)),
    ("title", def(C::SetChatTitle, Arguments::Any)),
    ("chat", def(C::ChatToggle, Arguments::OnOff)),
    ("notell", def(C::NoTellToggle, Arguments::OnOff)),
    ("join", def(C::JoinChannel, Arguments::JoinLeaveTag)),
    ("leave", def(C::LeaveChannel, Arguments::JoinLeaveTag)),
    ("permit", def(C::Permit, Arguments::PermitShape)),
    ("hslist", HOUSE_AVAILABLE),
    ("index", def(C::IndexChannels, Arguments::Any)),
    (
        "clist",
        refusing(C::ListChannel, Arguments::SingleToken, CHANNEL_NAME),
    ),
    (
        "on",
        refusing(C::OnChannel, Arguments::SingleToken, CHANNEL_NAME),
    ),
    (
        "off",
        refusing(C::OffChannel, Arguments::SingleToken, CHANNEL_NAME),
    ),
    ("alh", ALLEGIANCE_HOMETOWN),
    ("ah", ALLEGIANCE_HOMETOWN),
    ("motd", ALLEGIANCE_MOTD),
];

/// The `/join` and `/leave` tags and the character option each one sets.
const JOIN_LEAVE_TAGS: &[(&str, u32)] = &[
    ("allegiance", 0x1B),
    ("general", 0x23),
    ("trade", 0x24),
    ("lfg", 0x25),
    ("roleplay", 0x26),
    ("society", 0x2E),
    ("soc", 0x2E),
];

/// The `/hslist` house types and their numbers.
const HOUSE_TYPES: &[(&str, u32)] = &[
    ("cottage", 1),
    ("villa", 2),
    ("mansion", 3),
    ("apartment", 4),
];

fn lookup(table: &[(&str, u32)], key: &str) -> Option<u32> {
    table
        .iter()
        .find(|(k, _)| k.eq_ignore_ascii_case(key))
        .map(|(_, v)| *v)
}

/// Every single-word client command verb with its command (the `house` and
/// `allegiance` subcommand verbs are not listed).
#[cfg(test)]
pub fn known_verbs() -> impl Iterator<Item = (&'static str, ClientCommandId)> {
    BY_VERB.iter().map(|(verb, d)| (*verb, d.command))
}

/// The character option a `/join`/`/leave` tag sets.
#[must_use]
pub fn join_leave_option(tag: &str) -> Option<u32> {
    lookup(JOIN_LEAVE_TAGS, tag.trim())
}

/// The house type a `/hslist` argument names.
#[must_use]
pub fn house_type(name: &str) -> Option<u32> {
    lookup(HOUSE_TYPES, name.trim())
}

fn words(text: &str) -> Vec<&str> {
    text.split_whitespace().collect()
}

fn valid(arguments: Arguments, text: &str) -> bool {
    match arguments {
        Arguments::None => text.is_empty(),
        Arguments::Any => true,
        Arguments::OnOff => text.eq_ignore_ascii_case("on") || text.eq_ignore_ascii_case("off"),
        Arguments::JoinLeaveTag => join_leave_option(text).is_some(),
        Arguments::PermitShape => {
            let parts = words(text);
            parts.len() >= 2
                && (parts[0].eq_ignore_ascii_case("add") || parts[0].eq_ignore_ascii_case("remove"))
        }
        Arguments::HouseType => house_type(text).is_some(),
        Arguments::SingleToken => words(text).len() == 1,
        Arguments::Never => false,
    }
}

/// Split at the first whitespace after the first character.
fn split_verb(text: &str) -> (&str, &str) {
    match text.char_indices().skip(1).find(|(_, c)| c.is_whitespace()) {
        Some((at, c)) => (&text[..at], &text[at + c.len_utf8()..]),
        None => (text, ""),
    }
}

fn found(definition: Definition, arguments: &str) -> CatalogMatch {
    CatalogMatch {
        command: definition.command,
        arguments: arguments.to_owned(),
        has_valid_arguments: valid(definition.arguments, arguments),
        invalid_arguments_text: definition.invalid,
    }
}

/// The client command a `/` or `@` line is, if any.
#[must_use]
pub fn try_match(input: &str) -> Option<CatalogMatch> {
    let trimmed = input.trim();
    let rest = trimmed.strip_prefix(['/', '@'])?;
    if rest.is_empty() {
        return None;
    }
    let (verb, arguments) = split_verb(trimmed);
    let verb = verb[1..].trim_end_matches(',');
    let arguments = arguments.trim();
    if verb.eq_ignore_ascii_case("house") || verb.eq_ignore_ascii_case("hou") {
        return Some(match_house(arguments));
    }
    if verb.eq_ignore_ascii_case("allegiance") || verb.eq_ignore_ascii_case("all") {
        return Some(match_allegiance(arguments));
    }
    BY_VERB
        .iter()
        .find(|(known, _)| known.eq_ignore_ascii_case(verb))
        .map(|(_, definition)| found(*definition, arguments))
}

fn subcommand(arguments: &str) -> (String, &str) {
    let (sub, rest) = match arguments.char_indices().find(|(_, c)| c.is_whitespace()) {
        Some((at, c)) => (&arguments[..at], arguments[at + c.len_utf8()..].trim()),
        None => (arguments, ""),
    };
    (sub.to_ascii_lowercase(), rest)
}

fn match_house(arguments: &str) -> CatalogMatch {
    let (sub, rest) = subcommand(arguments);
    let definition = match sub.as_str() {
        "recall" | "re" => HOUSE_RECALL,
        "mansion_recall" | "alleg_recall" | "ma" => MANSION_RECALL,
        "abandon" => HOUSE_ABANDON,
        "open" | "close" => def(C::HouseOpenStatus, Arguments::Any),
        "storage" => def(C::HouseStorage, Arguments::Any),
        "remove" | "boot" => def(C::HouseBoot, Arguments::Any),
        "boot_all" | "remove_all" => def(C::HouseBootAll, Arguments::Any),
        "guest" => def(C::HouseGuests, Arguments::Any),
        "available" => HOUSE_AVAILABLE,
        "hooks" => def(C::HouseHooks, Arguments::Any),
        _ => {
            return found(
                refusing(
                    C::HouseUnrecognizedSubcommand,
                    Arguments::Never,
                    SEE_HELP_HOUSE,
                ),
                arguments,
            );
        }
    };
    match definition.command {
        C::HouseRecall | C::MansionRecall => CatalogMatch {
            command: definition.command,
            arguments: rest.to_owned(),
            has_valid_arguments: rest.is_empty(),
            invalid_arguments_text: Some(SEE_HELP_HOUSE),
        },
        C::HouseAvailableList => found(definition, rest),
        // Open and close are told apart by the subcommand itself.
        C::HouseOpenStatus => CatalogMatch {
            command: definition.command,
            arguments: sub,
            has_valid_arguments: true,
            invalid_arguments_text: None,
        },
        _ => CatalogMatch {
            command: definition.command,
            arguments: rest.to_owned(),
            has_valid_arguments: true,
            invalid_arguments_text: None,
        },
    }
}

fn match_allegiance(arguments: &str) -> CatalogMatch {
    let (sub, rest) = subcommand(arguments);
    let command = match sub.as_str() {
        "boot" => C::AllegianceBoot,
        "info" => C::AllegianceInfo,
        "chat" | "ch" => C::AllegianceChat,
        "broadcast" | "br" => C::AllegianceBroadcast,
        "ban" => C::AllegianceBan,
        "officer" => C::AllegianceOfficer,
        "title" => C::AllegianceOfficerTitle,
        "hometown" | "ho" => C::AllegianceHometown,
        "motd" => C::AllegianceMotd,
        "name" => C::AllegianceName,
        "lock" => C::AllegianceLock,
        "house" => C::AllegianceHouse,
        _ => {
            return found(
                refusing(
                    C::AllegianceUnrecognizedSubcommand,
                    Arguments::Never,
                    SEE_HELP_ALLEGIANCE,
                ),
                arguments,
            );
        }
    };
    CatalogMatch {
        command,
        arguments: if command == C::AllegianceHometown {
            String::new()
        } else {
            rest.to_owned()
        },
        has_valid_arguments: true,
        invalid_arguments_text: None,
    }
}

/// True when `verb` (without `/` or `@`) is a client command verb.
#[must_use]
pub fn is_catalog_verb(verb: &str) -> bool {
    ["house", "hou", "allegiance", "all"]
        .iter()
        .chain(BY_VERB.iter().map(|(v, _)| v))
        .any(|known| known.eq_ignore_ascii_case(verb))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn matched(line: &str) -> CatalogMatch {
        try_match(line).unwrap_or_else(|| panic!("{line} did not match"))
    }

    // Golden cases: OpenAC tests/AcDream.UI.Abstractions.Tests/Panels/Chat/ChatCommandRouterTests.cs
    // and tests/AcDream.Runtime.Tests/Chat/RuntimeClientCommandDispatcherTests.cs.

    #[test]
    fn lifestone_aliases_match_ignoring_case() {
        for line in ["/lifestone", "/lif", "/ls", "@LS"] {
            let m = matched(line);
            assert_eq!(m.command, C::LifestoneRecall);
            assert!(m.has_valid_arguments);
        }
        let m = matched("/ls now");
        assert!(!m.has_valid_arguments);
        assert_eq!(
            m.invalid_arguments_text,
            Some("Please see @help lifestone for more information on how to use this command.")
        );
        assert_eq!(matched("/pka now").invalid_arguments_text, None);
    }

    #[test]
    fn log_takes_the_whole_remainder() {
        let m = matched("/log my chat log.txt");
        assert_eq!(m.command, C::ChatLogFile);
        assert_eq!(m.arguments, "my chat log.txt");
    }

    #[test]
    fn subcommand_verbs_split_their_arguments() {
        for (line, command, arguments) in [
            ("/allegiance boot Lord Bob", C::AllegianceBoot, "Lord Bob"),
            (
                "/house storage add Lord Bob",
                C::HouseStorage,
                "add Lord Bob",
            ),
            (
                "/motd set Welcome home",
                C::AllegianceMotd,
                "set Welcome home",
            ),
            ("/house open", C::HouseOpenStatus, "open"),
            ("/all hometown", C::AllegianceHometown, ""),
        ] {
            let m = matched(line);
            assert_eq!((m.command, m.arguments.as_str()), (command, arguments));
            assert!(m.has_valid_arguments);
        }
        for line in ["/allegiance nope Bob", "/all nope Bob"] {
            let m = matched(line);
            assert_eq!(m.command, C::AllegianceUnrecognizedSubcommand);
            assert!(!m.has_valid_arguments);
            assert_eq!(m.invalid_arguments_text, Some(SEE_HELP_ALLEGIANCE));
        }
    }

    #[test]
    fn argument_shapes_are_checked() {
        for line in ["/clist", "/clist a b", "/on", "/off nonsense extra"] {
            let m = matched(line);
            assert!(!m.has_valid_arguments, "{line}");
            assert_eq!(m.invalid_arguments_text, Some(CHANNEL_NAME));
        }
        let m = matched("/hslist nonsense");
        assert!(!m.has_valid_arguments);
        assert_eq!(
            m.invalid_arguments_text,
            Some("Please see @help hslist for more information on how to use this command")
        );
        assert!(matched("/chat on").has_valid_arguments);
        assert!(!matched("/chat maybe").has_valid_arguments);
        assert!(matched("/join general").has_valid_arguments);
        assert!(!matched("/join nowhere").has_valid_arguments);
        assert!(matched("/permit add Aunt Agatha").has_valid_arguments);
        assert!(!matched("/permit Aunt").has_valid_arguments);
    }

    #[test]
    fn chat_verbs_and_unknown_verbs_do_not_match() {
        for line in [
            "/f hi",
            "/tell Bob hi",
            "/notacommand",
            "/",
            "hello",
            "@acehelp",
        ] {
            assert_eq!(try_match(line), None, "{line}");
        }
    }

    #[test]
    fn join_leave_and_house_tables() {
        assert_eq!(join_leave_option(" General "), Some(0x23));
        assert_eq!(join_leave_option("soc"), Some(0x2E));
        assert_eq!(house_type("Villa"), Some(2));
        assert!(is_catalog_verb("LS"));
        assert!(is_catalog_verb("all"));
        assert!(!is_catalog_verb("f"));
    }
}
