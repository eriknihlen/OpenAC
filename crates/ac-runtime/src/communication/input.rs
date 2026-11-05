//! Reading a typed chat line: which channel it goes to, who a tell is for,
//! and the text. Also the channel tags a line may start with, the numbered
//! channels they stand for, and the reply-abbreviation expansion.
//!
//! Verb matching is exact on the alias tables (`/f` is fellowship, `/fe` is
//! nothing); [`is_known_verb`] ignores case.

/// Where a chat line goes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum ChatChannelKind {
    /// Spoken aloud nearby (where the chat entry starts aimed).
    #[default]
    Say,
    /// A tell to one character.
    Tell,
    /// The fellowship.
    Fellowship,
    /// The allegiance room (or the allegiance broadcast without rooms).
    Allegiance,
    /// The player's vassals.
    Vassals,
    /// The player's patron.
    Patron,
    /// The player's monarch.
    Monarch,
    /// The player's co-vassals.
    CoVassals,
    /// The General room.
    General,
    /// The Trade room.
    Trade,
    /// The looking-for-group room.
    Lfg,
    /// The Roleplay room.
    Roleplay,
    /// The player's society room.
    Society,
    /// The Olthoi room.
    Olthoi,
    /// The numbered allegiance broadcast channel.
    AllegianceBroadcast,
    /// Nowhere known.
    Unknown,
}

/// A typed line read into where it goes and what it says.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParsedInput {
    /// Where it goes.
    pub channel: ChatChannelKind,
    /// The addressee of a tell, by name.
    pub target_name: Option<String>,
    /// The text.
    pub text: String,
    /// The addressee picked in the world, 0 when typed by name.
    pub target_guid: u32,
}

const SAY_ALIASES: &[&str] = &["/say", "/s"];
const TELL_ALIASES: &[&str] = &["/tell", "/t", "/send", "/whisper", "/w"];
const REPLY_ALIASES: &[&str] = &["/reply", "/r", "/rp"];
const RETELL_ALIASES: &[&str] = &["/retell", "/rt"];

/// The channel verbs, in matching order.
const CHANNEL_VERBS: &[(&str, ChatChannelKind)] = &[
    ("/general", ChatChannelKind::General),
    ("/cg", ChatChannelKind::General),
    ("/f", ChatChannelKind::Fellowship),
    ("/fellow", ChatChannelKind::Fellowship),
    ("/fellows", ChatChannelKind::Fellowship),
    ("/fellowship", ChatChannelKind::Fellowship),
    ("/g", ChatChannelKind::Fellowship),
    ("/group", ChatChannelKind::Fellowship),
    ("/party", ChatChannelKind::Fellowship),
    ("/a", ChatChannelKind::Allegiance),
    ("/guild", ChatChannelKind::Allegiance),
    ("/gu", ChatChannelKind::Allegiance),
    ("/ab", ChatChannelKind::AllegianceBroadcast),
    ("/m", ChatChannelKind::Monarch),
    ("/monarch", ChatChannelKind::Monarch),
    ("/p", ChatChannelKind::Patron),
    ("/patron", ChatChannelKind::Patron),
    ("/v", ChatChannelKind::Vassals),
    ("/vassal", ChatChannelKind::Vassals),
    ("/vassals", ChatChannelKind::Vassals),
    ("/c", ChatChannelKind::CoVassals),
    ("/covassal", ChatChannelKind::CoVassals),
    ("/covassals", ChatChannelKind::CoVassals),
    ("/co-vassals", ChatChannelKind::CoVassals),
    ("/lfg", ChatChannelKind::Lfg),
    ("/clfg", ChatChannelKind::Lfg),
    ("/trade", ChatChannelKind::Trade),
    ("/ct", ChatChannelKind::Trade),
    ("/crp", ChatChannelKind::Roleplay),
    ("/roleplay", ChatChannelKind::Roleplay),
    ("/society", ChatChannelKind::Society),
    ("/soc", ChatChannelKind::Society),
    ("/olthoi", ChatChannelKind::Olthoi),
    ("/o", ChatChannelKind::Olthoi),
];

/// The numbered channels whose bare verb (no text) gets the "say what?"
/// refusal; the room-only channels drop silently.
fn is_legacy_channel_kind(kind: ChatChannelKind) -> bool {
    matches!(
        kind,
        ChatChannelKind::Fellowship
            | ChatChannelKind::Allegiance
            | ChatChannelKind::AllegianceBroadcast
            | ChatChannelKind::Vassals
            | ChatChannelKind::Patron
            | ChatChannelKind::Monarch
            | ChatChannelKind::CoVassals
    )
}

/// True when the line is exactly a numbered-channel verb with no text.
#[must_use]
pub fn is_bare_registered_channel_verb(trimmed: &str) -> bool {
    CHANNEL_VERBS
        .iter()
        .any(|(verb, kind)| is_legacy_channel_kind(*kind) && is_bare_verb(trimmed, &[verb]))
}

/// True for a reply with text when nobody has sent a tell yet.
#[must_use]
pub fn is_reply_missing_last_teller(trimmed: &str, last_tell_sender: Option<&str>) -> bool {
    last_tell_sender.is_none_or(str::is_empty)
        && parse_message_only(trimmed, REPLY_ALIASES).is_some()
}

/// True for a retell with text when the player has sent no tell yet.
#[must_use]
pub fn is_retell_missing_last_tellee(
    trimmed: &str,
    last_outgoing_tell_target: Option<&str>,
) -> bool {
    last_outgoing_tell_target.is_none_or(str::is_empty)
        && parse_message_only(trimmed, RETELL_ALIASES).is_some()
}

/// Read a typed line. `None` when it is empty, a verb with nothing to say,
/// a reply or retell with nobody to address, or a line on the tell channel
/// with no addressee.
///
/// An `@` line whose verb is a chat verb reads as the `/` form; any other
/// `@` line is server text, sent as said. A `/` line with an unknown verb is
/// sent to the server as its `@` form.
#[must_use]
pub fn parse(
    raw: &str,
    default_channel: ChatChannelKind,
    last_tell_sender: Option<&str>,
    last_outgoing_tell_target: Option<&str>,
    default_tell_target: Option<&str>,
    default_tell_target_guid: u32,
) -> Option<ParsedInput> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return None;
    }
    let plain = |channel, target_name: Option<&str>, text: &str| {
        Some(ParsedInput {
            channel,
            target_name: target_name.map(str::to_owned),
            text: text.to_owned(),
            target_guid: 0,
        })
    };

    if let Some(rest) = trimmed.strip_prefix('@') {
        let substituted = format!("/{rest}");
        if is_known_verb(verb_token(&substituted)) {
            return parse(
                &substituted,
                default_channel,
                last_tell_sender,
                last_outgoing_tell_target,
                default_tell_target,
                default_tell_target_guid,
            );
        }
        return plain(ChatChannelKind::Say, None, trimmed);
    }

    if let Some(message) = parse_message_only(trimmed, SAY_ALIASES) {
        return plain(ChatChannelKind::Say, None, message);
    }
    if is_bare_verb(trimmed, SAY_ALIASES) {
        return None;
    }

    if let Some((target, message)) = parse_targeted(trimmed, TELL_ALIASES) {
        return plain(ChatChannelKind::Tell, Some(target), message);
    }
    if is_bare_verb(trimmed, TELL_ALIASES) || is_verb_with_single_token(trimmed, TELL_ALIASES) {
        return None;
    }

    if let Some(message) = parse_message_only(trimmed, REPLY_ALIASES) {
        let target = last_tell_sender.filter(|s| !s.is_empty())?;
        return plain(ChatChannelKind::Tell, Some(target), message);
    }
    if is_bare_verb(trimmed, REPLY_ALIASES) {
        return None;
    }

    if let Some(message) = parse_message_only(trimmed, RETELL_ALIASES) {
        let target = last_outgoing_tell_target.filter(|s| !s.is_empty())?;
        return plain(ChatChannelKind::Tell, Some(target), message);
    }
    if is_bare_verb(trimmed, RETELL_ALIASES) {
        return None;
    }

    for (verb, channel) in CHANNEL_VERBS {
        if let Some(message) = parse_message_only(trimmed, &[verb]) {
            return plain(*channel, None, message);
        }
        if is_bare_verb(trimmed, &[verb]) {
            return None;
        }
    }

    let mut chars = trimmed.chars();
    if chars.next() == Some('/') && chars.next().is_some_and(char::is_alphabetic) {
        return plain(ChatChannelKind::Say, None, &format!("@{}", &trimmed[1..]));
    }

    if default_channel == ChatChannelKind::Tell {
        let target = default_tell_target.filter(|s| !s.is_empty())?;
        return Some(ParsedInput {
            channel: ChatChannelKind::Tell,
            target_name: Some(target.to_owned()),
            text: trimmed.to_owned(),
            target_guid: default_tell_target_guid,
        });
    }

    plain(default_channel, None, trimmed)
}

/// True when `verb` (with its `/`) is a chat verb, ignoring case and a
/// trailing comma.
#[must_use]
pub fn is_known_verb(verb: &str) -> bool {
    let verb = trim_verb_comma(verb);
    SAY_ALIASES
        .iter()
        .chain(TELL_ALIASES)
        .chain(REPLY_ALIASES)
        .chain(RETELL_ALIASES)
        .chain(CHANNEL_VERBS.iter().map(|(v, _)| v))
        .any(|known| known.eq_ignore_ascii_case(verb))
}

/// The line's first whitespace-separated token.
#[must_use]
pub fn verb_token(command: &str) -> &str {
    split_at_whitespace(command).map_or(command, |(verb, _)| verb)
}

/// `(before, after)` around the first whitespace character, which is
/// dropped.
fn split_at_whitespace(text: &str) -> Option<(&str, &str)> {
    text.char_indices()
        .find(|(_, c)| c.is_whitespace())
        .map(|(at, c)| (&text[..at], &text[at + c.len_utf8()..]))
}

fn trim_verb_comma(verb: &str) -> &str {
    verb.trim_end_matches(',')
}

/// `verb target message` or `verb target, message`: the target is the first
/// token (trailing punctuation dropped), or everything before the first
/// comma.
fn parse_targeted<'a>(command: &'a str, aliases: &[&str]) -> Option<(&'a str, &'a str)> {
    let (verb, rest) = split_at_whitespace(command)?;
    if !aliases.contains(&trim_verb_comma(verb)) {
        return None;
    }
    let rest = rest.trim_start();
    if rest.is_empty() {
        return None;
    }
    let (target, message) = match rest.find(',') {
        None => {
            let (target, message) = split_at_whitespace(rest)?;
            (
                target.trim_end_matches([',', ';', ':', '.', '!', '?']),
                message.trim_start(),
            )
        }
        Some(comma) => (rest[..comma].trim_end(), rest[comma + 1..].trim_start()),
    };
    (!target.is_empty() && !message.is_empty()).then_some((target, message))
}

/// `verb message`: the message, when there is one.
fn parse_message_only<'a>(command: &'a str, aliases: &[&str]) -> Option<&'a str> {
    let (verb, rest) = split_at_whitespace(command)?;
    if !aliases.contains(&trim_verb_comma(verb)) {
        return None;
    }
    let message = rest.trim_start();
    (!message.is_empty()).then_some(message)
}

fn is_bare_verb(command: &str, aliases: &[&str]) -> bool {
    aliases.contains(&trim_verb_comma(command))
}

fn is_verb_with_single_token(command: &str, aliases: &[&str]) -> bool {
    let Some((verb, rest)) = split_at_whitespace(command) else {
        return false;
    };
    if !aliases.contains(&trim_verb_comma(verb)) {
        return false;
    }
    let rest = rest.trim_start();
    rest.is_empty() || split_at_whitespace(rest).is_none()
}

/// The numbered channel a channel kind sends on, with its display name;
/// `None` for say, tells and the rooms.
#[must_use]
pub fn legacy_channel(kind: ChatChannelKind) -> Option<(u32, &'static str)> {
    Some(match kind {
        ChatChannelKind::Fellowship => (0x0000_0800, "Fellowship"),
        ChatChannelKind::AllegianceBroadcast => (0x0200_0000, "Allegiance"),
        ChatChannelKind::Vassals => (0x0000_1000, "Vassals"),
        ChatChannelKind::Patron => (0x0000_2000, "Patron"),
        ChatChannelKind::Monarch => (0x0000_4000, "Monarch"),
        ChatChannelKind::CoVassals => (0x0100_0000, "CoVassals"),
        _ => return None,
    })
}

/// Every channel tag a `/tag text` line may use, and its channel number.
const CHANNEL_TAGS: &[(&str, u32)] = &[
    ("abuse", 0x0000_0001),
    ("ad", 0x0000_0002),
    ("admin", 0x0000_0002),
    ("au", 0x0000_0004),
    ("audit", 0x0000_0004),
    ("av", 0x0000_0008),
    ("av1", 0x0000_0008),
    ("advocate", 0x0000_0008),
    ("advocate1", 0x0000_0008),
    ("av2", 0x0000_0010),
    ("advocate2", 0x0000_0010),
    ("av3", 0x0000_0020),
    ("advocate3", 0x0000_0020),
    ("sent", 0x0000_0200),
    ("sentinel", 0x0000_0200),
    ("celestialhand", 0x0800_0000),
    ("celhan", 0x0800_0000),
    ("eldrytchweb", 0x1000_0000),
    ("eldweb", 0x1000_0000),
    ("radiantblood", 0x2000_0000),
    ("radblo", 0x2000_0000),
    ("ol", 0x4000_0000),
    ("olthoi", 0x4000_0000),
    ("fellowship", 0x0000_0800),
    ("fellow", 0x0000_0800),
    ("fellows", 0x0000_0800),
    ("f", 0x0000_0800),
    ("group", 0x0000_0800),
    ("g", 0x0000_0800),
    ("party", 0x0000_0800),
    ("vassals", 0x0000_1000),
    ("vassal", 0x0000_1000),
    ("v", 0x0000_1000),
    ("patron", 0x0000_2000),
    ("p", 0x0000_2000),
    ("monarch", 0x0000_4000),
    ("m", 0x0000_4000),
    ("covassals", 0x0100_0000),
    ("covassal", 0x0100_0000),
    ("co-vassals", 0x0100_0000),
    ("c", 0x0100_0000),
    ("a", 0x0200_0000),
    ("ab", 0x0200_0000),
    ("allegiance", 0x0200_0000),
];

/// The channel number a tag stands for, ignoring case.
#[must_use]
pub fn resolve_channel_tag(tag: &str) -> Option<u32> {
    CHANNEL_TAGS
        .iter()
        .find(|(known, _)| known.eq_ignore_ascii_case(tag))
        .map(|(_, id)| *id)
}

/// The reply abbreviation alone (`/r `, `@rp `, `/reply `, any case),
/// expanded to a tell at the last teller so the player can keep typing.
/// `None` when the text is anything else or nobody has sent a tell.
#[must_use]
pub fn expand_reply_abbreviation(text: &str, last_teller: Option<&str>) -> Option<String> {
    let last_teller = last_teller.filter(|t| !t.is_empty())?;
    let rest = text.strip_prefix(['/', '@'])?;
    let verb = rest.strip_suffix(' ')?;
    ["r", "rp", "reply"]
        .iter()
        .any(|v| v.eq_ignore_ascii_case(verb))
        .then(|| format!("@tell {last_teller}, "))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn p(raw: &str) -> Option<ParsedInput> {
        parse(raw, ChatChannelKind::Say, None, None, None, 0)
    }

    fn channel_text(raw: &str) -> (ChatChannelKind, Option<String>, String) {
        let parsed = p(raw).unwrap_or_else(|| panic!("{raw} parsed to nothing"));
        (parsed.channel, parsed.target_name, parsed.text)
    }

    // Golden cases: OpenAC tests/AcDream.UI.Abstractions.Tests/Panels/Chat/ChatInputParserTests.cs

    #[test]
    fn plain_text_and_say_use_the_say_channel() {
        assert_eq!(
            channel_text("hello world"),
            (ChatChannelKind::Say, None, "hello world".into())
        );
        assert_eq!(
            channel_text("/say hi there"),
            (ChatChannelKind::Say, None, "hi there".into())
        );
    }

    #[test]
    fn tell_aliases_read_target_and_message() {
        for (raw, target, text) in [
            ("/tell Bestie hi there", "Bestie", "hi there"),
            ("/t Bestie hi", "Bestie", "hi"),
            ("/t Sir Lancelot hello", "Sir", "Lancelot hello"),
            ("/send Bestie hi", "Bestie", "hi"),
            ("/whisper Bestie hi", "Bestie", "hi"),
            ("/w Bestie hi", "Bestie", "hi"),
            ("/tell Aunt Agatha, hello", "Aunt Agatha", "hello"),
            ("/t Aunt Agatha, hello", "Aunt Agatha", "hello"),
        ] {
            assert_eq!(
                channel_text(raw),
                (ChatChannelKind::Tell, Some(target.into()), text.into()),
                "{raw}"
            );
        }
    }

    #[test]
    fn tell_target_loses_trailing_punctuation() {
        for raw in [
            "/t Caith, hi",
            "/t Caith: hi",
            "/t Caith; hi",
            "/t Caith. hi",
            "/t Caith! hi",
            "/t Caith? hi",
            "/tell Caith, hi",
        ] {
            assert_eq!(
                channel_text(raw),
                (ChatChannelKind::Tell, Some("Caith".into()), "hi".into()),
                "{raw}"
            );
        }
    }

    #[test]
    fn reply_and_retell_need_a_known_target() {
        let parsed = parse(
            "/r back at you",
            ChatChannelKind::Say,
            Some("Bestie"),
            None,
            None,
            0,
        )
        .unwrap();
        assert_eq!(parsed.target_name.as_deref(), Some("Bestie"));
        assert_eq!(parsed.text, "back at you");
        let rp = parse(
            "/rp back at you",
            ChatChannelKind::Say,
            Some("Bestie"),
            None,
            None,
            0,
        )
        .unwrap();
        assert_eq!(rp.channel, ChatChannelKind::Tell);
        assert_eq!(p("/r hi"), None);
        let rt = parse(
            "/rt once more",
            ChatChannelKind::Say,
            None,
            Some("Caith"),
            None,
            0,
        )
        .unwrap();
        assert_eq!(
            (rt.channel, rt.target_name.as_deref(), rt.text.as_str()),
            (ChatChannelKind::Tell, Some("Caith"), "once more")
        );
    }

    #[test]
    fn channel_verbs_route_to_their_channel() {
        for (raw, channel, text) in [
            ("/g raid time", ChatChannelKind::Fellowship, "raid time"),
            ("/f buff up", ChatChannelKind::Fellowship, "buff up"),
            ("/a swearing in", ChatChannelKind::Allegiance, "swearing in"),
            (
                "/m monarch broadcast",
                ChatChannelKind::Monarch,
                "monarch broadcast",
            ),
            ("/p patron only", ChatChannelKind::Patron, "patron only"),
            ("/v vassals only", ChatChannelKind::Vassals, "vassals only"),
            (
                "/c covassals only",
                ChatChannelKind::CoVassals,
                "covassals only",
            ),
            ("/lfg need 3 more", ChatChannelKind::Lfg, "need 3 more"),
            ("/trade wts gem", ChatChannelKind::Trade, "wts gem"),
            ("/roleplay *waves*", ChatChannelKind::Roleplay, "*waves*"),
            (
                "/society olthoi raid",
                ChatChannelKind::Society,
                "olthoi raid",
            ),
            (
                "/olthoi for the queen",
                ChatChannelKind::Olthoi,
                "for the queen",
            ),
            (
                "/general what's the deal",
                ChatChannelKind::General,
                "what's the deal",
            ),
            ("/guild recall", ChatChannelKind::Allegiance, "recall"),
            ("/patron need help", ChatChannelKind::Patron, "need help"),
            ("/vassals listen up", ChatChannelKind::Vassals, "listen up"),
            ("/monarch heads up", ChatChannelKind::Monarch, "heads up"),
            (
                "/covassals tax season",
                ChatChannelKind::CoVassals,
                "tax season",
            ),
            (
                "/fellowship buff time",
                ChatChannelKind::Fellowship,
                "buff time",
            ),
            (
                "/fellow buff time",
                ChatChannelKind::Fellowship,
                "buff time",
            ),
            (
                "/fellows buff time",
                ChatChannelKind::Fellowship,
                "buff time",
            ),
            ("/group buff time", ChatChannelKind::Fellowship, "buff time"),
            ("/party buff time", ChatChannelKind::Fellowship, "buff time"),
            ("/clfg hunt invite", ChatChannelKind::Lfg, "hunt invite"),
            ("/crp walk-up", ChatChannelKind::Roleplay, "walk-up"),
        ] {
            assert_eq!(channel_text(raw), (channel, None, text.into()), "{raw}");
        }
    }

    #[test]
    fn empty_and_wordless_lines_parse_to_nothing() {
        for raw in [
            "",
            "   \t  ",
            "/say",
            "/say   ",
            "/t",
            "/tell",
            "/t Bestie",
            "/tell Bestie   ",
            "/g",
            "/f   ",
            "/a",
        ] {
            assert_eq!(p(raw), None, "{raw:?}");
        }
        for raw in ["/r", "/r   "] {
            assert_eq!(
                parse(raw, ChatChannelKind::Say, Some("Bestie"), None, None, 0),
                None
            );
        }
    }

    #[test]
    fn unknown_verbs_reach_the_server_in_at_form() {
        for (raw, text) in [
            ("/xyz hello", "@xyz hello"),
            ("/ci 629", "@ci 629"),
            ("/tele holtburg", "@tele holtburg"),
            ("@ci 629", "@ci 629"),
            ("@acehelp", "@acehelp"),
            ("/genio public", "@genio public"),
        ] {
            assert_eq!(
                channel_text(raw),
                (ChatChannelKind::Say, None, text.into()),
                "{raw}"
            );
        }
        assert_eq!(p("/ hello").unwrap().text, "/ hello");
        assert_eq!(p("//shrug").unwrap().text, "//shrug");
    }

    #[test]
    fn plain_text_follows_the_default_channel() {
        let parsed = parse("hi gang", ChatChannelKind::Fellowship, None, None, None, 0).unwrap();
        assert_eq!(parsed.channel, ChatChannelKind::Fellowship);
        assert_eq!(parsed.text, "hi gang");
    }

    #[test]
    fn known_verbs_ignore_case_and_a_trailing_comma() {
        for (verb, known) in [
            ("/g", true),
            ("/say", true),
            ("/tell", true),
            ("/retell", true),
            ("/rt", true),
            ("/rp", true),
            ("/guild", true),
            ("/allegiance", false),
            ("/lookingforgroup", false),
            ("/role", false),
            ("/cv", false),
            ("/genio", false),
            ("/ls", false),
            ("/foo", false),
            ("/", false),
            ("/f,", true),
            ("/F", true),
        ] {
            assert_eq!(is_known_verb(verb), known, "{verb}");
        }
        let with = p("/f, hi").unwrap();
        assert_eq!(
            (with.channel, with.text.as_str()),
            (ChatChannelKind::Fellowship, "hi")
        );
    }

    #[test]
    fn verb_token_is_the_first_token() {
        for (command, verb) in [
            ("/g hello", "/g"),
            ("/tell Bob hi", "/tell"),
            ("/foo", "/foo"),
            ("/", "/"),
        ] {
            assert_eq!(verb_token(command), verb);
        }
    }

    #[test]
    fn bare_numbered_channel_verbs_are_recognised() {
        for verb in [
            "/g",
            "/f",
            "/fellow",
            "/fellows",
            "/fellowship",
            "/group",
            "/party",
            "/a",
            "/guild",
            "/gu",
            "/ab",
            "/m",
            "/monarch",
            "/p",
            "/patron",
            "/v",
            "/vassal",
            "/vassals",
            "/c",
            "/covassal",
            "/covassals",
            "/co-vassals",
        ] {
            assert!(is_bare_registered_channel_verb(verb), "{verb}");
        }
        for verb in [
            "/general",
            "/cg",
            "/lfg",
            "/clfg",
            "/trade",
            "/ct",
            "/roleplay",
            "/crp",
            "/society",
            "/soc",
            "/olthoi",
            "/o",
            "/g hi gang",
            "/a hey",
            "hello",
            "/say hi",
            "/r hi",
        ] {
            assert!(!is_bare_registered_channel_verb(verb), "{verb}");
        }
    }

    #[test]
    fn reply_without_a_teller_is_detected() {
        for raw in ["/r hi there", "/reply hi there", "/rp hi there"] {
            assert!(is_reply_missing_last_teller(raw, None));
            assert!(is_reply_missing_last_teller(raw, Some("")));
        }
        assert!(!is_reply_missing_last_teller("/r hi there", Some("Bestie")));
        for raw in ["/r", "/r   ", "/g hi gang", "hello"] {
            assert!(!is_reply_missing_last_teller(raw, None), "{raw}");
        }
    }

    #[test]
    fn a_tell_line_with_a_picked_target_carries_its_id() {
        // OpenAC tests/AcDream.Runtime.Tests/Chat/TellTargetingTests.cs
        let parsed = parse(
            "I would like to trade",
            ChatChannelKind::Tell,
            None,
            None,
            Some("Aun Tanua"),
            0x8000_ABCD,
        )
        .unwrap();
        assert_eq!(parsed.target_guid, 0x8000_ABCD);
        assert_eq!(parsed.target_name.as_deref(), Some("Aun Tanua"));
        assert_eq!(
            parse("hi", ChatChannelKind::Tell, None, None, None, 0),
            None
        );
    }

    #[test]
    fn channel_tags_resolve_ignoring_case() {
        assert_eq!(resolve_channel_tag("admin"), Some(0x2));
        assert_eq!(resolve_channel_tag("SENTINEL"), Some(0x200));
        assert_eq!(resolve_channel_tag("allegiance"), Some(0x0200_0000));
        assert_eq!(resolve_channel_tag("nonsense"), None);
    }

    // Golden cases: OpenAC tests/AcDream.Runtime.Tests/Chat/ChatTextReplacementsTests.cs

    #[test]
    fn a_reply_abbreviation_expands_to_a_tell_at_the_last_teller() {
        for typed in ["/r ", "/rp ", "/reply ", "@r ", "@rp ", "@reply ", "/R "] {
            assert_eq!(
                expand_reply_abbreviation(typed, Some("Dww")).as_deref(),
                Some("@tell Dww, "),
                "{typed}"
            );
        }
        assert_eq!(
            expand_reply_abbreviation("/r ", Some("Aunt Agatha")).as_deref(),
            Some("@tell Aunt Agatha, ")
        );
    }

    #[test]
    fn anything_else_is_left_alone() {
        for typed in [
            "/r",
            "/rep",
            "/roleplay ",
            "/rt ",
            "/r hello ",
            "r ",
            "hello ",
            "",
        ] {
            assert_eq!(
                expand_reply_abbreviation(typed, Some("Dww")),
                None,
                "{typed}"
            );
        }
        assert_eq!(expand_reply_abbreviation("/r ", None), None);
        assert_eq!(expand_reply_abbreviation("/r ", Some("")), None);
    }
}
