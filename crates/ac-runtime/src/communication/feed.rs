//! Chat lines worded the way the chat box reads them, and the short class
//! tag a colourless front end (a console) prints in front of each.
//!
//! Every front end that shows chat, and the `@log` file, takes its words
//! from here, so they all show the same text.

use ac_core::chat::tag_markup;
use ac_core::chat::{
    ChatEntry, ChatKind, ChatSpanRole, ChatTextSpan, ChatTextTag, CombatLineKind, LogTextType,
    format_timestamp_prefix, is_player_object_id, legacy_channel_heard, legacy_channel_sent,
};

/// One chat-box line, worded, as owned data for hosts.
#[derive(Debug, Clone, PartialEq)]
pub struct ChatLine {
    /// The transcript entry's sequence number (stable while older lines
    /// are dropped).
    pub sequence: u64,
    /// What kind of line it is.
    pub kind: ChatKind,
    /// The text class (what a chat window's filter and colour key on).
    pub log_text_type: u32,
    /// The tint of a combat line.
    pub combat_kind: Option<CombatLineKind>,
    /// When it arrived, UTC milliseconds since the Unix epoch.
    pub received_unix_ms: i64,
    /// The line as read on screen, timestamp included when that is on.
    pub text: String,
    /// The channel's display name, when the line names one.
    pub channel_name: String,
    /// The speaker's name ("You" for the player), empty for system text.
    pub sender: String,
    /// The speaker's object id, 0 when none.
    pub sender_guid: u32,
    /// The words as the entry carries them: no speaker, verb, quotes or
    /// timestamp (what a plugin reads as the line's text).
    pub words: String,
    /// The whole line as composed, before its tags are read, without the
    /// timestamp: another player's name wrapped in the tell link
    /// (`<Tell:IIDString:id:Name>Name<\Tell>`), any tag in the words kept.
    pub display_text: String,
    /// The channel number of a channel line, 0 on every other kind (the
    /// entry's number means other things on other kinds).
    pub channel_id: u32,
    /// The line split at its tags (a speaker's tell tag and any other tag
    /// the line holds, the coordinate pairs it shows unless it is combat
    /// text, and on a plugin's own line its coordinate tags) and its
    /// timestamp, or `None` when it has none. The tags themselves are not in the text. The spans always
    /// rejoin to `text`.
    pub spans: Option<Vec<ChatTextSpan>>,
}

impl ChatLine {
    /// Word an entry. `utc_offset_seconds` places the timestamp in the
    /// player's time zone when timestamps are on.
    #[must_use]
    pub fn from_entry(entry: &ChatEntry, timestamps: bool, utc_offset_seconds: i32) -> Self {
        let (mut text, mut spans) = shown(entry);
        if timestamps {
            let prefix = format_timestamp_prefix(entry.received_unix_ms, utc_offset_seconds);
            let mut stamped = vec![ChatTextSpan {
                text: prefix.clone(),
                tag: None,
                role: ChatSpanRole::Timestamp,
            }];
            stamped.extend(spans.unwrap_or_else(|| {
                vec![ChatTextSpan {
                    text: text.clone(),
                    tag: None,
                    role: ChatSpanRole::Body,
                }]
            }));
            spans = Some(stamped);
            text.insert_str(0, &prefix);
        }
        Self {
            sequence: entry.sequence,
            kind: entry.kind,
            log_text_type: entry.log_text_type,
            combat_kind: entry.combat_kind,
            received_unix_ms: entry.received_unix_ms,
            text,
            channel_name: entry.channel_name.clone(),
            sender: entry.sender.clone(),
            sender_guid: entry.sender_guid,
            words: entry.text.clone(),
            display_text: format_tagged(entry),
            channel_id: if entry.kind == ChatKind::Channel {
                entry.channel_id
            } else {
                0
            },
            spans,
        }
    }

    /// The tag to print in front of this line: empty when its words already
    /// open with a bracketed channel name.
    #[must_use]
    pub fn tag(&self) -> &'static str {
        if self.kind == ChatKind::Channel {
            ""
        } else {
            tag_for(self.log_text_type)
        }
    }
}

/// The line as plain text, as it is shown: the tags in it hidden, the
/// speaker's name left as plain text; a plugin's line with its link tags
/// and escapes removed.
#[must_use]
pub fn format(entry: &ChatEntry) -> String {
    if entry.allows_markup {
        tag_markup::to_plain_text(&format_with(entry, |sender| sender.to_owned()))
    } else {
        tag_markup::to_shown_text(&format_tagged(entry))
    }
}

/// The line as composed, before its tags are read: another player's name
/// wrapped in its tell tag so a front end can make it clickable, any tag in
/// the words kept as written, and a plugin's line as the plugin wrote it,
/// its COORD tags and escapes kept.
#[must_use]
pub fn format_tagged(entry: &ChatEntry) -> String {
    if should_tag_sender(entry) {
        let guid = entry.sender_guid;
        format_with(entry, |sender| {
            format!("<Tell:IIDString:{guid}:{sender}>{sender}<\\Tell>")
        })
    } else {
        format_with(entry, |sender| sender.to_owned())
    }
}

/// The line as shown, without its timestamp, and its spans when it has
/// tags. A plugin's own line has its link tags and escapes read, and a
/// coordinate pair it writes in plain text is a link too. Every other line,
/// the server's, the character's own and a combat line among them, is read
/// for tags the same way: a tag is hidden, and the speaker's name in its
/// tell tag is a link that starts a tell. A coordinate tag in the words is
/// shown as plain text and its hidden value goes nowhere. A coordinate pair
/// the line shows is a link to exactly that pair, except in combat text, so
/// a click reports what the reader sees.
fn shown(entry: &ChatEntry) -> (String, Option<Vec<ChatTextSpan>>) {
    let markup = format_tagged(entry);
    let spans = if entry.allows_markup {
        let read = tag_markup::parse_links(&markup);
        link_coordinates(&read).unwrap_or(read)
    } else {
        let read = tag_markup::parse_line(&markup);
        if entry.kind == ChatKind::Combat {
            read
        } else {
            link_shown_coordinates(&read).unwrap_or(read)
        }
    };
    if !spans.iter().any(|s| s.tag.is_some()) {
        return (spans.into_iter().map(|s| s.text).collect(), None);
    }
    (spans.iter().map(|s| s.text.as_str()).collect(), Some(spans))
}

/// Splits every plain body span at the coordinate pairs it holds, each pair
/// becoming a span of its own tagged as a `COORD:World` link, as the line's
/// own markup makes one. Tagged spans and the timestamp are kept as they
/// are. A pair is found exactly when the plugin SDK's parser reads it (the
/// matcher is shared). `None` when the spans hold no pair; the new spans
/// rejoin to the same text.
#[must_use]
pub fn link_coordinates(spans: &[ChatTextSpan]) -> Option<Vec<ChatTextSpan>> {
    let mut result: Option<Vec<ChatTextSpan>> = None;
    for (i, span) in spans.iter().enumerate() {
        if span.tag.is_some() || span.role != ChatSpanRole::Body {
            if let Some(result) = &mut result {
                result.push(span.clone());
            }
            continue;
        }
        let pairs = ac_plugin_abi::chat_coordinates::find(&span.text);
        if pairs.is_empty() {
            if let Some(result) = &mut result {
                result.push(span.clone());
            }
            continue;
        }
        let out = result.get_or_insert_with(|| spans[..i].to_vec());
        let piece = |text: &str, tag: Option<ChatTextTag>| ChatTextSpan {
            text: text.to_owned(),
            tag,
            role: span.role,
        };
        let mut run_start = 0;
        for pair in pairs {
            if pair.start > run_start {
                out.push(piece(&span.text[run_start..pair.start], span.tag.clone()));
            }
            out.push(piece(
                &span.text[pair.start..pair.start + pair.len],
                Some(coordinate_tag(pair.east_west, pair.north_south)),
            ));
            run_start = pair.start + pair.len;
        }
        if run_start < span.text.len() {
            out.push(piece(&span.text[run_start..], span.tag.clone()));
        }
    }
    result
}

/// Links the coordinate pairs in the text a line shows. Plain body spans
/// that stand side by side (a hidden tag between them) are read as one text
/// first, so a pair is found exactly where the reader sees one, and each
/// link's target is built from the shown pair alone. Tagged spans and the
/// timestamp are kept as they are. `None` when the shown text holds no
/// pair; the new spans rejoin to the same text.
#[must_use]
pub fn link_shown_coordinates(spans: &[ChatTextSpan]) -> Option<Vec<ChatTextSpan>> {
    let is_plain_body = |span: &ChatTextSpan| span.tag.is_none() && span.role == ChatSpanRole::Body;
    let mut joined: Vec<ChatTextSpan> = Vec::with_capacity(spans.len());
    for span in spans {
        match joined.last_mut() {
            Some(last) if is_plain_body(last) && is_plain_body(span) => {
                last.text.push_str(&span.text);
            }
            _ => joined.push(span.clone()),
        }
    }
    link_coordinates(&joined)
}

/// The link a coordinate is carried by, in the chat markup's own form:
/// north-south first, then east-west, each its shortest exact decimal and a
/// compass letter.
#[must_use]
pub fn coordinate_tag(east_west: f64, north_south: f64) -> ChatTextTag {
    let axis = |value: f64, positive: char, negative: char| {
        format!(
            "{}{}",
            value.abs(),
            if value < 0.0 { negative } else { positive }
        )
    };
    ChatTextTag {
        tag_type: "COORD".to_owned(),
        format: "World".to_owned(),
        data: format!(
            "{}, {}",
            axis(north_south, 'N', 'S'),
            axis(east_west, 'E', 'W')
        ),
    }
}

/// Whether the line names another player a click could send a tell to. A
/// channel line carries only the speaker's name, which still links; other
/// kinds link only a speaker whose id is a player's.
#[must_use]
pub fn should_tag_sender(entry: &ChatEntry) -> bool {
    (entry.kind == ChatKind::Channel || is_player_object_id(entry.sender_guid))
        && !entry.sender.is_empty()
        && !entry.sender.contains(['<', '>'])
        && !is_own_speaker(&entry.sender)
        && !entry.is_tell_to_self()
        && matches!(
            entry.kind,
            ChatKind::LocalSpeech | ChatKind::RangedSpeech | ChatKind::Channel | ChatKind::Tell
        )
}

fn is_own_speaker(sender: &str) -> bool {
    sender.is_empty() || sender == "You"
}

fn format_with(entry: &ChatEntry, decorate: impl Fn(&str) -> String) -> String {
    let text = &entry.text;
    match entry.kind {
        ChatKind::LocalSpeech if is_own_speaker(&entry.sender) => format!("You say, \"{text}\""),
        ChatKind::LocalSpeech => format!("{} says, \"{text}\"", decorate(&entry.sender)),
        // A ranged line has no sentence of its own, not even for the
        // speaker: it is printed under the name it arrived with.
        ChatKind::RangedSpeech => format!("{} says, \"{text}\"", decorate(&entry.sender)),
        ChatKind::Channel => channel_line(entry, &decorate),
        ChatKind::Tell if entry.is_tell_to_self() => format!("You think, \"{text}\""),
        ChatKind::Tell if entry.sender_guid != 0 => {
            format!("{} tells you, \"{text}\"", decorate(&entry.sender))
        }
        ChatKind::Tell => format!("You tell {}, \"{text}\"", entry.sender),
        ChatKind::Popup => format!("[Popup] {text}"),
        ChatKind::Emote | ChatKind::SoulEmote => emote_line(entry),
        ChatKind::System | ChatKind::Combat => text.clone(),
    }
}

/// An emote is the name followed by the action, with no marker in front;
/// the space between them is left out when the action starts with an
/// apostrophe, so "Bob" and "'s eyes narrow." read "Bob's eyes narrow."
fn emote_line(entry: &ChatEntry) -> String {
    if entry.text.starts_with('\'') {
        format!("{}{}", entry.sender, entry.text)
    } else {
        format!("{} {}", entry.sender, entry.text)
    }
}

/// A channel that arrives with a name is shown under it; a bare number is
/// one of the fixed channels, each with its own sentence.
fn channel_line(entry: &ChatEntry, decorate: &impl Fn(&str) -> String) -> String {
    let own = is_own_speaker(&entry.sender);
    let text = &entry.text;
    if entry.channel_name.is_empty() {
        if own {
            legacy_channel_sent(entry.channel_id, text)
        } else {
            legacy_channel_heard(entry.channel_id, &decorate(&entry.sender), text)
        }
    } else if own {
        format!("[{}] You say, \"{text}\"", entry.channel_name)
    } else {
        format!(
            "[{}] {} says, \"{text}\"",
            entry.channel_name,
            decorate(&entry.sender)
        )
    }
}

/// The tag for a text class, brackets and trailing space included.
#[must_use]
pub fn tag_for(log_text_type: u32) -> &'static str {
    use LogTextType as T;
    match LogTextType::from_u32(log_text_type) {
        Some(T::All) => "[all] ",
        Some(T::Speech | T::SpeechDirectSend) => "[say] ",
        Some(T::Tell) => "[tell] ",
        Some(T::AdminTell) => "[admin] ",
        Some(T::System) => "[system] ",
        Some(T::Combat | T::CombatEnemy | T::CombatSelf) => "[combat] ",
        Some(T::Magic) => "[magic] ",
        Some(T::Spellcasting) => "[spell] ",
        Some(T::Channel | T::ChannelSend) => "[channel] ",
        Some(T::Social | T::SocialSend) => "[social] ",
        Some(T::Emote) => "[emote] ",
        Some(T::Advancement) => "[advance] ",
        Some(T::Abuse) => "[abuse] ",
        Some(T::Help) => "[help] ",
        Some(T::Appraisal) => "[appraise] ",
        Some(T::Allegiance) => "[allegiance] ",
        Some(T::Fellowship) => "[fellowship] ",
        Some(T::WorldBroadcast) => "[broadcast] ",
        Some(T::Recall) => "[recall] ",
        Some(T::Craft) => "[craft] ",
        Some(T::Salvaging) => "[salvage] ",
        Some(T::ClientLocal) => CLIENT_LOCAL_TAG,
        Some(T::TurbineGeneral) => "[general] ",
        Some(T::TurbineTrade) => "[trade] ",
        Some(T::TurbineLfg) => "[lfg] ",
        Some(T::TurbineRoleplay) => "[roleplay] ",
        Some(T::TurbineSociety) => "[society] ",
        Some(T::Default | T::Reserved21) | None => "[msg] ",
    }
}

/// The tag for client-local text (the status overlay's notices).
pub const CLIENT_LOCAL_TAG: &str = "[client] ";

#[cfg(test)]
mod tests {
    use super::*;
    use ac_core::chat::ChatLog;

    const OTHER_PLAYER: u32 = 0x5000_000A;
    const T0: i64 = 1_786_000_000_000;

    fn entry(kind: ChatKind, sender: &str, text: &str, guid: u32, channel: u32) -> ChatEntry {
        ChatEntry {
            kind,
            sender: sender.into(),
            text: text.into(),
            sender_guid: guid,
            channel_id: channel,
            received_unix_ms: 0,
            combat_kind: None,
            channel_name: String::new(),
            log_text_type: 0,
            target_guid: 0,
            sequence: 0,
            allows_markup: false,
        }
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/ChatWordingFaithfulnessTests.cs
    const SELF: u32 = 0x5000_000A;
    const OTHER: u32 = 0x5000_0077;

    #[test]
    fn a_tell_whose_speaker_is_its_listener_reads_as_a_thought() {
        let mut log = ChatLog::new();
        let e = log
            .on_tell_received(T0, "Acdream", "remember the key", SELF, 0x03, SELF)
            .cloned()
            .expect("appended");
        assert!(e.is_tell_to_self());
        assert_eq!(format_tagged(&e), "You think, \"remember the key\"");
        assert!(!should_tag_sender(&e));
    }

    #[test]
    fn a_tell_from_someone_else_still_tells_you() {
        let mut log = ChatLog::new();
        let e = log
            .on_tell_received(T0, "Bob", "hi", OTHER, 0x03, SELF)
            .cloned()
            .expect("appended");
        assert!(!e.is_tell_to_self());
        assert_eq!(
            format_tagged(&e),
            format!("<Tell:IIDString:{OTHER}:Bob>Bob<\\Tell> tells you, \"hi\"")
        );
    }

    #[test]
    fn a_ranged_line_is_speech_under_the_name_it_arrived_with() {
        let mut log = ChatLog::new();
        let e = log
            .on_local_speech(T0, "Guard", "Halt!", 0x8000_1234, true, 0x02)
            .cloned()
            .expect("appended");
        assert_eq!(format_tagged(&e), "Guard says, \"Halt!\"");
    }

    #[test]
    fn a_speakerless_fellowship_broadcast_takes_the_fellowship_text_class() {
        let mut log = ChatLog::new();
        let e = log
            .on_channel_broadcast(
                T0,
                0x0400_0000,
                "",
                "Bob has joined the fellowship.",
                None,
                "",
            )
            .cloned()
            .expect("appended");
        assert_eq!(e.log_text_type, LogTextType::Fellowship.as_u32());
        assert_eq!(format_tagged(&e), "Bob has joined the fellowship.");
    }

    #[test]
    fn a_speakerless_patron_line_takes_the_senders_text_class() {
        let mut log = ChatLog::new();
        let e = log
            .on_channel_broadcast(T0, 0x0000_1000, "", "thanks", None, "")
            .cloned()
            .expect("appended");
        assert_eq!(e.log_text_type, LogTextType::SocialSend.as_u32());
        assert_eq!(format_tagged(&e), "You say to your Vassals, \"thanks\"");
    }

    #[test]
    fn an_emote_is_the_name_then_the_action() {
        let mut log = ChatLog::new();
        log.on_emote(T0, "Bob", "waves.", OTHER);
        log.on_soul_emote(T0, "Bob", "bows deeply.", OTHER);
        let lines: Vec<String> = log.entries().map(format_tagged).collect();
        assert_eq!(lines, ["Bob waves.", "Bob bows deeply."]);
    }

    #[test]
    fn an_emote_starting_with_an_apostrophe_joins_the_name() {
        let mut log = ChatLog::new();
        log.on_emote(T0, "Bob", "'s eyes narrow.", OTHER);
        assert_eq!(format(&log.snapshot()[0]), "Bob's eyes narrow.");
    }

    // Golden values: OpenAC tests/AcDream.Runtime.Tests/Chat/RuntimeChatFeedTests.cs

    #[test]
    fn each_kind_is_worded_the_way_the_chat_box_reads_it() {
        let mut fellowship = entry(ChatKind::Channel, "Bob", "group up", OTHER_PLAYER, 7);
        fellowship.channel_name = "Fellowship".into();
        for (e, expected) in [
            (
                entry(ChatKind::LocalSpeech, "Bob", "hi there", OTHER_PLAYER, 0),
                "Bob says, \"hi there\"",
            ),
            (
                entry(ChatKind::LocalSpeech, "You", "hi there", 0, 0),
                "You say, \"hi there\"",
            ),
            (
                entry(ChatKind::RangedSpeech, "Bob", "hi there", OTHER_PLAYER, 0),
                "Bob says, \"hi there\"",
            ),
            (fellowship, "[Fellowship] Bob says, \"group up\""),
            (
                entry(ChatKind::Channel, "Bob", "group up", OTHER_PLAYER, 7),
                "Bob says on the <unknown> channel, \"group up\"",
            ),
            (
                entry(ChatKind::Tell, "Bob", "meet me", OTHER_PLAYER, 0),
                "Bob tells you, \"meet me\"",
            ),
            (
                entry(ChatKind::Tell, "Bob", "meet me", 0, 0),
                "You tell Bob, \"meet me\"",
            ),
            (
                entry(ChatKind::Popup, "", "You have died.", 0, 0),
                "[Popup] You have died.",
            ),
            (
                entry(ChatKind::Emote, "Bob", "waves.", OTHER_PLAYER, 0),
                "Bob waves.",
            ),
            (
                entry(ChatKind::SoulEmote, "Bob", "waves.", OTHER_PLAYER, 0),
                "Bob waves.",
            ),
            (
                entry(ChatKind::System, "", "Welcome to Dereth.", 0, 0),
                "Welcome to Dereth.",
            ),
        ] {
            assert_eq!(format(&e), expected);
        }
    }

    #[test]
    fn another_players_name_is_marked_up_and_the_visible_text_is_unchanged() {
        let e = entry(ChatKind::LocalSpeech, "Bob", "hi there", OTHER_PLAYER, 0);
        let tagged = format_tagged(&e);
        assert_eq!(
            tagged,
            "<Tell:IIDString:1342177290:Bob>Bob<\\Tell> says, \"hi there\""
        );
        let visible: String = ac_core::chat::tag_markup::parse(&tagged)
            .into_iter()
            .map(|span| span.text)
            .collect();
        assert_eq!(visible, format(&e));
    }

    #[test]
    fn a_line_carries_its_classification_and_optional_timestamp() {
        let mut log = ChatLog::new();
        let e = log
            .on_combat_line(
                T0,
                "A Drudge Slinker slashes you for 9 points of damage!",
                LogTextType::CombatEnemy.as_u32(),
                CombatLineKind::Warning,
            )
            .unwrap()
            .clone();
        let line = ChatLine::from_entry(&e, false, 0);
        assert_eq!(
            line.text,
            "A Drudge Slinker slashes you for 9 points of damage!"
        );
        assert_eq!(line.kind, ChatKind::Combat);
        assert_eq!(line.log_text_type, LogTextType::CombatEnemy.as_u32());
        assert_eq!(line.combat_kind, Some(CombatLineKind::Warning));
        assert_eq!(line.sequence, 1);
        let stamped = ChatLine::from_entry(&e, true, 0);
        assert_eq!(stamped.text, format_timestamp_prefix(T0, 0) + &line.text);
    }

    #[test]
    fn a_channel_line_is_not_tagged_twice() {
        let mut log = ChatLog::new();
        let e = log
            .on_channel_broadcast(
                T0,
                7,
                "Bob",
                "group up",
                Some(LogTextType::Fellowship.as_u32()),
                "Fellowship",
            )
            .unwrap()
            .clone();
        let line = ChatLine::from_entry(&e, false, 0);
        assert_eq!(line.tag(), "");
        assert_eq!(line.text, "[Fellowship] Bob says, \"group up\"");
        let e = log
            .on_local_speech(
                T0,
                "Bob",
                "hi there",
                OTHER_PLAYER,
                false,
                LogTextType::Speech.as_u32(),
            )
            .unwrap()
            .clone();
        assert_eq!(ChatLine::from_entry(&e, false, 0).tag(), "[say] ");
        assert_eq!(tag_for(0x1A), "[client] ");
        assert_eq!(tag_for(0xFF), "[msg] ");
    }

    // ── Coordinate links: a pair a line shows is a link to that pair, in
    // every line but combat. OpenAC `RuntimeChatFeedTests` and
    // `ChatCoordinateLinksTests` (PR #224, feat/player-coordinate-links) ──

    const SPEECH: u32 = 0x02;
    const TELL: u32 = 0x03;

    fn lines(log: &ChatLog, timestamps: bool) -> Vec<ChatLine> {
        log.entries()
            .map(|e| ChatLine::from_entry(e, timestamps, 0))
            .collect()
    }

    fn is_coordinate(span: &ChatTextSpan) -> bool {
        span.tag
            .as_ref()
            .is_some_and(|t| t.tag_type.eq_ignore_ascii_case("COORD"))
    }

    fn has_coordinate_link(line: &ChatLine) -> bool {
        line.spans
            .as_ref()
            .is_some_and(|spans| spans.iter().any(is_coordinate))
    }

    /// The one coordinate span of a line.
    fn coordinate_span(line: &ChatLine) -> ChatTextSpan {
        let spans: Vec<&ChatTextSpan> = line
            .spans
            .as_ref()
            .expect("spans")
            .iter()
            .filter(|s| is_coordinate(s))
            .collect();
        assert_eq!(spans.len(), 1, "{:?}", line.spans);
        spans[0].clone()
    }

    fn rejoined(line: &ChatLine) -> String {
        line.spans
            .as_ref()
            .expect("spans")
            .iter()
            .map(|s| s.text.as_str())
            .collect()
    }

    fn the_named_speaker(line: &ChatLine) -> String {
        let tagged: Vec<&ChatTextSpan> = line
            .spans
            .as_ref()
            .expect("spans")
            .iter()
            .filter(|s| s.tag.is_some())
            .collect();
        assert_eq!(tagged.len(), 1);
        tagged[0].text.clone()
    }

    /// Where a coordinate span's link points: (east-west, north-south).
    fn target(span: &ChatTextSpan) -> (f64, f64) {
        span.tag
            .as_ref()
            .and_then(ChatTextTag::coordinate)
            .expect("a coordinate link")
    }

    /// The tell-tagged speaker of a line.
    fn the_tell_name(line: &ChatLine) -> String {
        let tells: Vec<&ChatTextSpan> = line
            .spans
            .as_ref()
            .expect("spans")
            .iter()
            .filter(|s| s.tag.as_ref().is_some_and(ChatTextTag::is_tell))
            .collect();
        assert_eq!(tells.len(), 1);
        tells[0].text.clone()
    }

    #[test]
    fn a_coordinate_a_player_writes_is_a_link_to_the_pair_it_shows() {
        // ACoordinateAPlayerWritesIsALinkToThePairItShows.
        let mut log = ChatLog::new();
        log.on_local_speech(
            T0,
            "Bob",
            "meet at 42.1N 33.5E",
            OTHER_PLAYER,
            false,
            SPEECH,
        );
        log.on_tell_received(T0, "Bob", "meet at 42.1N 33.5E", OTHER_PLAYER, TELL, 0);
        log.on_channel_broadcast(T0, 7, "Bob", "meet at 42.1N 33.5E", None, "Fellowship");
        let lines = lines(&log, false);
        assert_eq!(lines.len(), 3);
        assert_eq!(lines[0].text, "Bob says, \"meet at 42.1N 33.5E\"");
        for line in &lines {
            let coordinate = coordinate_span(line);
            assert_eq!(coordinate.text, "42.1N 33.5E");
            assert_eq!(target(&coordinate), (33.5, 42.1));
            // The speaker's name is still the tell link it always was.
            assert_eq!(the_tell_name(line), "Bob");
            assert_eq!(rejoined(line), line.text);
        }
    }

    #[test]
    fn a_coordinate_in_the_characters_own_line_or_a_server_line_is_a_link() {
        // ACoordinateInTheCharactersOwnLineOrAServerLineIsALink.
        let mut log = ChatLog::new();
        log.on_local_speech(T0, "You", "here 1N 2E", 0, false, SPEECH);
        log.on_system_message(T0, "Your corpse is located at (42.1N, 33.5E).", 0);
        let lines = lines(&log, false);
        assert_eq!(lines[0].text, "You say, \"here 1N 2E\"");
        assert_eq!(coordinate_span(&lines[0]).text, "1N 2E");
        assert_eq!(target(&coordinate_span(&lines[0])), (2.0, 1.0));
        assert_eq!(lines[1].text, "Your corpse is located at (42.1N, 33.5E).");
        let corpse = coordinate_span(&lines[1]);
        assert_eq!(corpse.text, "42.1N, 33.5E");
        assert_eq!(target(&corpse), (33.5, 42.1));
        assert_eq!(rejoined(&lines[1]), lines[1].text);
    }

    #[test]
    fn a_coordinate_tag_a_player_types_never_links_its_hidden_value() {
        // ACoordinateTagAPlayerTypesNeverLinksItsHiddenValue: the tag's
        // hidden value is never a link; a caption that is itself a pair is
        // linked to that shown pair, as if written plainly.
        const TYPED: &str = r"<COORD:World:0N, 0E>42.1N 33.5E<\COORD>";
        let mut log = ChatLog::new();
        log.on_local_speech(T0, "Bob", TYPED, OTHER_PLAYER, false, SPEECH);
        log.on_tell_received(T0, "Bob", TYPED, OTHER_PLAYER, TELL, 0);
        log.on_channel_broadcast(T0, 7, "Bob", TYPED, None, "Fellowship");
        log.on_system_message(T0, TYPED, 0);
        let lines = lines(&log, false);
        assert_eq!(lines.len(), 4);
        assert_eq!(lines[0].text, "Bob says, \"42.1N 33.5E\"");
        assert_eq!(lines[3].text, "42.1N 33.5E");
        for line in &lines {
            let coordinate = coordinate_span(line);
            assert_eq!(coordinate.text, "42.1N 33.5E");
            assert_eq!(target(&coordinate), (33.5, 42.1));
            assert_eq!(rejoined(line), line.text);
        }
    }

    #[test]
    fn a_coordinate_tag_a_player_types_over_words_is_no_link() {
        // ACoordinateTagAPlayerTypesOverWordsIsNoLink.
        const TYPED: &str = r"<COORD:World:0N, 0E>the bridge<\COORD>";
        let mut log = ChatLog::new();
        log.on_local_speech(T0, "Bob", TYPED, OTHER_PLAYER, false, SPEECH);
        log.on_system_message(T0, TYPED, 0);
        let lines = lines(&log, false);
        assert_eq!(lines[0].text, "Bob says, \"the bridge\"");
        assert!(!has_coordinate_link(&lines[0]));
        assert_eq!(the_named_speaker(&lines[0]), "Bob");
        assert_eq!(lines[1].text, "the bridge");
        assert_eq!(lines[1].spans, None);
    }

    #[test]
    fn a_pair_the_line_shows_across_a_hidden_tag_is_one_link() {
        // APairTheLineShowsAcrossAHiddenTagIsOneLink: the pair is found in
        // the text as shown; a hidden tag that splits it does not stop it
        // being read, and one that glues it to a word does.
        let mut log = ChatLog::new();
        log.on_local_speech(
            T0,
            "Bob",
            r"at <COORD:World:0N, 0E>42.1N<\COORD> 33.5E",
            OTHER_PLAYER,
            false,
            SPEECH,
        );
        log.on_system_message(T0, r"x<COORD:World:0N, 0E>42.1N 33.5E<\COORD>", 0);
        let lines = lines(&log, false);
        assert_eq!(lines[0].text, "Bob says, \"at 42.1N 33.5E\"");
        let coordinate = coordinate_span(&lines[0]);
        assert_eq!(coordinate.text, "42.1N 33.5E");
        assert_eq!(target(&coordinate), (33.5, 42.1));
        assert_eq!(rejoined(&lines[0]), lines[0].text);
        assert_eq!(lines[1].text, "x42.1N 33.5E");
        assert_eq!(lines[1].spans, None);
    }

    #[test]
    fn a_combat_line_is_not_searched_for_coordinates() {
        // ACombatLineIsNotSearchedForCoordinates.
        let mut log = ChatLog::new();
        log.on_combat_line(
            T0,
            "You hit 3N 4E for 9 points.",
            LogTextType::CombatSelf.as_u32(),
            CombatLineKind::Info,
        );
        log.on_combat_line(
            T0,
            r"You hit <DID:DID:0x06001234>the drudge<\DID> at 3N 4E.",
            LogTextType::CombatSelf.as_u32(),
            CombatLineKind::Info,
        );
        let lines = lines(&log, false);
        assert_eq!(lines[0].spans, None);
        assert!(!has_coordinate_link(&lines[1]));
    }

    #[test]
    fn a_coordinate_a_plugin_writes_is_a_link() {
        // ACoordinateAPluginWritesIsALink.
        let mut log = ChatLog::new();
        log.on_plugin_message(T0, "meet at 42.1N 33.5E", 0);
        let line = &lines(&log, false)[0];
        assert_eq!(line.text, "meet at 42.1N 33.5E");
        let coordinate = coordinate_span(line);
        assert_eq!(coordinate.text, "42.1N 33.5E");
        assert_eq!(coordinate.tag.unwrap().coordinate(), Some((33.5, 42.1)));
    }

    #[test]
    fn a_timestamped_plugin_line_keeps_its_stamp_and_links_the_coordinate() {
        // ATimestampedPluginLineKeepsItsStampAndLinksTheCoordinate.
        let mut log = ChatLog::new();
        log.on_plugin_message(T0, "at 5S 6W", 0);
        let line = &lines(&log, true)[0];
        assert_eq!(
            line.spans.as_ref().unwrap()[0].role,
            ChatSpanRole::Timestamp
        );
        assert_eq!(coordinate_span(line).text, "5S 6W");
        assert_eq!(rejoined(line), line.text);
    }

    #[test]
    fn a_plugin_lines_markup_is_read_as_a_link() {
        // APluginLinesMarkupIsReadAsALink.
        let mut log = ChatLog::new();
        log.on_plugin_message(
            T0,
            r"Waypoint: <COORD:World:12.5N, 3.25W>the bridge<\COORD>.",
            0,
        );
        let line = &lines(&log, false)[0];
        assert_eq!(line.text, "Waypoint: the bridge.");
        let coordinate = coordinate_span(line);
        assert_eq!(coordinate.text, "the bridge");
        assert_eq!(coordinate.tag.unwrap().coordinate(), Some((-3.25, 12.5)));
        assert_eq!(format(&log.snapshot()[0]), "Waypoint: the bridge.");
    }

    // ── Every other line is read for tags: OpenAC chat tag fix
    // `RuntimeChatFeedTests` ──

    fn tagged(line: &ChatLine) -> Vec<&ChatTextSpan> {
        line.spans
            .as_ref()
            .expect("spans")
            .iter()
            .filter(|s| s.tag.is_some())
            .collect()
    }

    #[test]
    fn a_server_lines_tag_is_hidden_and_its_tell_name_is_tagged() {
        // AServerLinesTagIsHiddenAndItsTellNameIsTagged.
        let mut log = ChatLog::new();
        log.on_system_message(T0, r"<Tell:IIDString:1:Bob>Bob<\Tell> has logged on.", 0);
        let line = &lines(&log, false)[0];
        assert_eq!(line.text, "Bob has logged on.");
        let name = tagged(line);
        assert_eq!(name.len(), 1);
        assert_eq!(name[0].text, "Bob");
        assert!(name[0].tag.as_ref().unwrap().is_tell());
        assert_eq!(format(&log.snapshot()[0]), "Bob has logged on.");
    }

    #[test]
    fn the_characters_own_and_a_combat_lines_tags_are_hidden_too() {
        // TheCharactersOwnAndACombatLinesTagsAreHiddenToo.
        let mut log = ChatLog::new();
        log.on_local_speech(
            T0,
            "You",
            r"ask <Tell:IIDString:7:Ann>Ann<\Tell> please",
            0,
            false,
            LogTextType::Speech.as_u32(),
        );
        log.on_combat_line(
            T0,
            r"You hit <DID:DID:0x06001234>the drudge<\DID>.",
            LogTextType::CombatSelf.as_u32(),
            CombatLineKind::Info,
        );
        let lines = lines(&log, false);
        assert_eq!(lines[0].text, "You say, \"ask Ann please\"");
        let ann = tagged(&lines[0]);
        assert_eq!(ann.len(), 1);
        assert_eq!(ann[0].text, "Ann");
        assert_eq!(lines[1].text, "You hit the drudge.");
        let other = tagged(&lines[1]);
        assert_eq!(other.len(), 1);
        assert_eq!(other[0].text, "the drudge");
        assert!(!other[0].tag.as_ref().unwrap().is_tell());
    }

    #[test]
    fn an_unmatched_bracket_with_no_tag_open_is_shown_as_written() {
        // AnUnmatchedBracketWithNoTagOpenIsShownAsWritten.
        let mut log = ChatLog::new();
        log.on_system_message(T0, "3 < 4, and <Tell:IIDString:1:Bob", 0);
        log.on_local_speech(T0, "You", "x <y", 0, false, LogTextType::Speech.as_u32());
        let lines = lines(&log, false);
        assert_eq!(lines[0].text, "3 < 4, and <Tell:IIDString:1:Bob");
        assert_eq!(lines[0].spans, None);
        assert_eq!(lines[1].text, "You say, \"x <y\"");
        assert_eq!(lines[1].spans, None);
    }

    #[test]
    fn a_coordinate_tag_in_a_server_line_is_closed_by_the_next_bracketed_text_whatever_it_is() {
        // ACoordinateTagInAServerLineIsClosedByTheNextBracketedTextWhateverItIs:
        // the coordinate tag is read as a tag that is never a link, so the
        // next bracketed text only closes it: "<gate>" is hidden, and the
        // tell tag after the caption opens nothing, which leaves its end tag
        // shown.
        let mut log = ChatLog::new();
        log.on_system_message(T0, "<COORD:World:1N, 2E> meet at <gate>", 0);
        log.on_system_message(
            T0,
            r"<COORD:World:1N, 2E>x <Tell:IIDString:5:Bob>Bob<\Tell>",
            0,
        );
        let lines = lines(&log, false);
        assert_eq!(lines[0].text, " meet at ");
        assert_eq!(lines[0].spans, None);
        assert_eq!(lines[1].text, r"x Bob<\Tell>");
        assert_eq!(lines[1].spans, None);
    }

    #[test]
    fn a_server_lines_bracketed_text_that_is_not_a_tag_is_shown_as_written() {
        // AServerLinesBracketedTextThatIsNotATagIsShownAsWritten.
        let mut log = ChatLog::new();
        log.on_system_message(T0, "Use <item> on <target>, or 3 < 4.", 0);
        let line = &lines(&log, false)[0];
        assert_eq!(line.text, "Use <item> on <target>, or 3 < 4.");
        assert_eq!(line.spans, None);
    }

    #[test]
    fn a_plugin_line_with_no_tag_keeps_its_angle_brackets() {
        // APluginLineWithNoTagKeepsItsAngleBrackets.
        let mut log = ChatLog::new();
        log.on_plugin_message(T0, "a < b > c <none>", 0);
        let line = &lines(&log, false)[0];
        assert_eq!(line.text, "a < b > c <none>");
        assert_eq!(line.spans, None);
    }

    /// The plugin SDK's escape (`openac_plugin::chat_markup::escape`).
    fn escape(text: &str) -> String {
        text.replace('<', "\\<")
    }

    #[test]
    fn a_players_tag_a_plugin_reposts_escaped_is_shown_as_written_and_is_not_a_link() {
        // APlayersTagAPluginRepostsEscapedIsShownAsWrittenAndIsNotALink.
        const TYPED: &str = r"<COORD:World:0N, 0E>the bridge<\COORD>";
        let mut log = ChatLog::new();
        log.on_plugin_message(T0, &format!("Bob told you: {}", escape(TYPED)), 0);
        log.on_plugin_message(T0, &escape("press <Ctrl:F>"), 0);
        let lines = lines(&log, false);
        assert_eq!(lines[0].text, format!("Bob told you: {TYPED}"));
        assert_eq!(format(&log.snapshot()[0]), format!("Bob told you: {TYPED}"));
        // The caption is not a link. The tag's own text, now shown, holds a
        // plain pair, and that pair links to exactly what it shows.
        assert!(
            !lines[0]
                .spans
                .as_ref()
                .unwrap()
                .iter()
                .any(|s| s.tag.is_some() && s.text.contains("bridge"))
        );
        let shown = coordinate_span(&lines[0]);
        assert_eq!(shown.text, "0N, 0E");
        assert_eq!(shown.tag.unwrap().coordinate(), Some((0.0, 0.0)));
        assert_eq!(lines[1].text, "press <Ctrl:F>");
        assert_eq!(lines[1].spans, None);
    }

    #[test]
    fn a_plugin_lines_other_angle_bracket_text_is_shown_as_written() {
        // APluginLinesOtherAngleBracketTextIsShownAsWritten.
        let mut log = ChatLog::new();
        log.on_plugin_message(T0, "Press <Ctrl:F> to follow", 0);
        log.on_plugin_message(
            T0,
            r"Press <Ctrl:F>, then <COORD:World:1N, 2E>go<\COORD> <a:b:c>",
            0,
        );
        let lines = lines(&log, false);
        assert_eq!(lines[0].text, "Press <Ctrl:F> to follow");
        assert_eq!(lines[0].spans, None);
        assert_eq!(lines[1].text, "Press <Ctrl:F>, then go <a:b:c>");
        assert_eq!(coordinate_span(&lines[1]).text, "go");
        assert_eq!(
            format(&log.snapshot()[1]),
            "Press <Ctrl:F>, then go <a:b:c>"
        );
        // The plugin is handed its line as it wrote it, its tags kept.
        assert_eq!(
            lines[1].display_text,
            r"Press <Ctrl:F>, then <COORD:World:1N, 2E>go<\COORD> <a:b:c>"
        );
    }

    fn plain(text: &str) -> Vec<ChatTextSpan> {
        vec![ChatTextSpan {
            text: text.to_owned(),
            tag: None,
            role: ChatSpanRole::Body,
        }]
    }

    fn texts(spans: &[ChatTextSpan]) -> Vec<&str> {
        spans.iter().map(|s| s.text.as_str()).collect()
    }

    #[test]
    fn a_pair_in_plain_text_becomes_a_coordinate_link() {
        // ChatCoordinateLinksTests.APairInPlainTextBecomesACoordinateLink.
        let spans = link_coordinates(&plain("meet at 42.1N 33.5E now")).unwrap();
        assert_eq!(texts(&spans), ["meet at ", "42.1N 33.5E", " now"]);
        assert_eq!(spans[0].tag, None);
        assert_eq!(spans[2].tag, None);
        assert_eq!(
            spans[1].tag.as_ref().unwrap().coordinate(),
            Some((33.5, 42.1))
        );
    }

    #[test]
    fn south_and_west_and_either_order_are_read() {
        // ChatCoordinateLinksTests.SouthAndWestAndEitherOrderAreRead.
        let spans = link_coordinates(&plain("go 12.5W, 3.25S")).unwrap();
        let tagged: Vec<&ChatTextSpan> = spans.iter().filter(|s| s.tag.is_some()).collect();
        assert_eq!(tagged.len(), 1);
        assert_eq!(
            tagged[0].tag.as_ref().unwrap().coordinate(),
            Some((-12.5, -3.25))
        );
    }

    #[test]
    fn every_pair_on_the_line_is_linked() {
        // ChatCoordinateLinksTests.EveryPairOnTheLineIsLinked.
        let spans = link_coordinates(&plain("from 1N 2E to 3S 4W")).unwrap();
        assert_eq!(spans.iter().filter(|s| s.tag.is_some()).count(), 2);
        assert_eq!(texts(&spans).concat(), "from 1N 2E to 3S 4W");
    }

    #[test]
    fn a_line_with_no_pair_or_a_pair_inside_a_word_is_left_alone() {
        // ChatCoordinateLinksTests.ALineWithNoPairIsLeftAlone,
        // APairInsideAWordIsNotLinked.
        for text in [
            "hello there",
            "42N 33N is not a pair",
            "10n 20east",
            "10north 20e",
            "v2.1N 3E",
            "x10n 20e",
        ] {
            assert_eq!(link_coordinates(&plain(text)), None, "{text}");
        }
    }

    #[test]
    fn an_already_tagged_span_and_the_timestamp_are_not_searched() {
        // ChatCoordinateLinksTests.AnAlreadyTaggedSpanAndTheTimestampAreNotSearched.
        let tell = ChatTextTag {
            tag_type: "Tell".into(),
            format: "IIDString".into(),
            data: "1:Bob 1N 2E".into(),
        };
        let spans = vec![
            ChatTextSpan {
                text: "[10:00] 1N 2E ".into(),
                tag: None,
                role: ChatSpanRole::Timestamp,
            },
            ChatTextSpan {
                text: "Bob 1N 2E".into(),
                tag: Some(tell),
                role: ChatSpanRole::Body,
            },
            ChatTextSpan {
                text: " says, \"hi\"".into(),
                tag: None,
                role: ChatSpanRole::Body,
            },
        ];
        assert_eq!(link_coordinates(&spans), None);
    }

    #[test]
    fn a_tagged_span_is_kept_and_the_plain_span_after_it_is_linked() {
        // ChatCoordinateLinksTests.ATaggedSpanIsKeptAndThePlainSpanAfterItIsLinked.
        let tell = ChatTextTag {
            tag_type: "Tell".into(),
            format: "IIDString".into(),
            data: "1:Bob".into(),
        };
        let spans = link_coordinates(&[
            ChatTextSpan {
                text: "Bob".into(),
                tag: Some(tell.clone()),
                role: ChatSpanRole::Body,
            },
            ChatTextSpan {
                text: " says, \"at 5N 6E\"".into(),
                tag: None,
                role: ChatSpanRole::Body,
            },
        ])
        .unwrap();
        assert_eq!(texts(&spans), ["Bob", " says, \"at ", "5N 6E", "\""]);
        assert_eq!(spans[0].tag, Some(tell));
        assert_eq!(spans[2].tag.as_ref().unwrap().tag_type, "COORD");
    }

    fn body(text: &str) -> ChatTextSpan {
        ChatTextSpan {
            text: text.into(),
            tag: None,
            role: ChatSpanRole::Body,
        }
    }

    #[test]
    fn link_shown_coordinates_reads_neighbouring_plain_spans_as_one_text() {
        // ChatCoordinateLinksTests.LinkShownReadsNeighbouringPlainSpansAsOneText:
        // a hidden tag between two plain spans leaves them side by side; the
        // reader sees one text, and the pair is found in that text.
        let tell = ChatTextTag {
            tag_type: "Tell".into(),
            format: "IIDString".into(),
            data: "1:Bob".into(),
        };
        let spans = link_shown_coordinates(&[
            ChatTextSpan {
                text: "Bob".into(),
                tag: Some(tell.clone()),
                role: ChatSpanRole::Body,
            },
            body(" says, \"at "),
            body("42.1N"),
            body(" 33.5E\""),
        ])
        .unwrap();
        assert_eq!(texts(&spans), ["Bob", " says, \"at ", "42.1N 33.5E", "\""]);
        assert_eq!(spans[0].tag, Some(tell));
        assert_eq!(
            spans[2].tag.as_ref().unwrap().coordinate(),
            Some((33.5, 42.1))
        );
    }

    #[test]
    fn link_shown_coordinates_finds_no_pair_the_shown_text_glues_to_a_word() {
        // ChatCoordinateLinksTests.LinkShownFindsNoPairThatTheShownTextGluesToAWord.
        assert_eq!(
            link_shown_coordinates(&[body("x"), body("42.1N 33.5E")]),
            None
        );
    }

    #[test]
    fn it_links_exactly_the_pairs_the_plugin_parser_reads() {
        // ChatCoordinateLinksTests.ItLinksExactlyThePairsThePluginParserReads:
        // the SDK's parser reads `ac_plugin_abi::chat_coordinates::find`.
        for text in [
            "42.1N 33.5E",
            "42.1n, 33.5e",
            "33.5E; 42.1N",
            "0.5S/101.9W",
            "a 1N 2E b 3N 4W c",
            "no pair 1N here",
            "1E 2W",
            "10n 20east and (42.1N, 33.5E).",
        ] {
            let parsed: Vec<(f64, f64)> = ac_plugin_abi::chat_coordinates::find(text)
                .into_iter()
                .map(|p| (p.east_west, p.north_south))
                .collect();
            let spans = link_coordinates(&plain(text)).unwrap_or_else(|| plain(text));
            let linked: Vec<(f64, f64)> = spans
                .iter()
                .filter_map(|s| s.tag.as_ref())
                .map(|t| t.coordinate().expect("a coordinate tag"))
                .collect();
            assert_eq!(parsed, linked, "{text}");
        }
    }
}
