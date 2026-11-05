//! The chat transcript: every line the player sees in the chat window, in
//! arrival order, capped at 500 lines.
//!
//! Each inbound kind of text has its own entry point that decides the
//! line's kind, speaker and text class. Every line then passes the same
//! gate: suppression filters may drop it, the language filter may censor
//! it, and it gets a sequence number that is never reused.
//!
//! Time comes in from the caller as UTC milliseconds since the Unix epoch;
//! the log never reads a clock.

use std::collections::VecDeque;
use std::fmt;
use std::num::NonZeroUsize;

use super::combat_text::{CombatLineKind, CombatNotice};
use super::{LogTextType, language_filter, legacy_channel_chat_type};

/// How many lines the transcript keeps by default.
pub const DEFAULT_MAX_ENTRIES: usize = 500;

/// What kind of line an entry is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ChatKind {
    /// Speech heard nearby.
    LocalSpeech,
    /// Speech heard from further away (a shout).
    RangedSpeech,
    /// A channel message.
    Channel,
    /// A tell, received or sent.
    Tell,
    /// A system message.
    System,
    /// A message that also opened a popup.
    Popup,
    /// An emote.
    Emote,
    /// A soul emote.
    SoulEmote,
    /// A combat line.
    Combat,
}

/// One line of the transcript.
#[derive(Debug, Clone, PartialEq)]
pub struct ChatEntry {
    /// What kind of line it is.
    pub kind: ChatKind,
    /// Who spoke; `"You"` for the player's own speech, the addressee for a
    /// tell the player sent, empty for system text.
    pub sender: String,
    /// The text, without any timestamp.
    pub text: String,
    /// The speaker's object id, 0 when none. A player-killed line keeps the
    /// victim here.
    pub sender_guid: u32,
    /// The channel number; a system line keeps its chat type here and a
    /// player-killed line the killer's id.
    pub channel_id: u32,
    /// When the line arrived, UTC milliseconds since the Unix epoch.
    pub received_unix_ms: i64,
    /// The tint of a combat line.
    pub combat_kind: Option<CombatLineKind>,
    /// The channel's display name, when the line names one.
    pub channel_name: String,
    /// The wire text class (see [`LogTextType`]); kept raw so an
    /// unknown class survives.
    pub log_text_type: u32,
    /// Who a received tell was addressed to; 0 on every other line, and
    /// when the listener is not known.
    pub target_guid: u32,
    /// Append order, unique for the life of the log and never reused; 0 on
    /// a line that was never appended. Lets a holder of one line (a text
    /// selection, say) find it again after older lines are dropped.
    pub sequence: u64,
    /// Whether `text` may carry chat markup (a link such as
    /// `<COORD:World:42.1N, 33.5E>…`). Only a line a plugin wrote does;
    /// server and player text is never read for it. A coordinate pair
    /// written in plain text is a link in any line but combat text, whatever
    /// this says.
    pub allows_markup: bool,
}

impl ChatEntry {
    /// A tell the character sent itself: its speaker is its listener.
    #[must_use]
    pub fn is_tell_to_self(&self) -> bool {
        self.kind == ChatKind::Tell && self.sender_guid != 0 && self.sender_guid == self.target_guid
    }

    fn new(
        kind: ChatKind,
        sender: &str,
        text: &str,
        sender_guid: u32,
        channel_id: u32,
        log_text_type: u32,
    ) -> Self {
        Self {
            kind,
            sender: sender.to_owned(),
            text: text.to_owned(),
            sender_guid,
            channel_id,
            received_unix_ms: 0,
            combat_kind: None,
            channel_name: String::new(),
            log_text_type,
            target_guid: 0,
            sequence: 0,
            allows_markup: false,
        }
    }
}

/// A suppression filter: returns true to drop the line.
pub type ChatFilter = Box<dyn Fn(&ChatEntry) -> bool + Send>;

/// Identifies a registered suppression filter so it can be removed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ChatFilterId(u64);

/// The ordered set of predicates consulted before a line is appended. A
/// line a filter rejects is never appended, so it reaches neither the
/// transcript, the windows bound to it, nor the log file.
#[derive(Default)]
pub struct ChatSuppressionFilters {
    filters: Vec<(ChatFilterId, ChatFilter)>,
    next_id: u64,
}

impl fmt::Debug for ChatSuppressionFilters {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ChatSuppressionFilters")
            .field("count", &self.filters.len())
            .finish()
    }
}

impl ChatSuppressionFilters {
    /// Adds a filter at the end of the order.
    pub fn register(&mut self, filter: ChatFilter) -> ChatFilterId {
        self.next_id += 1;
        let id = ChatFilterId(self.next_id);
        self.filters.push((id, filter));
        id
    }

    /// Removes a filter; false when it was not registered.
    pub fn remove(&mut self, id: ChatFilterId) -> bool {
        let before = self.filters.len();
        self.filters.retain(|(candidate, _)| *candidate != id);
        self.filters.len() != before
    }

    /// How many filters are registered.
    #[must_use]
    pub fn len(&self) -> usize {
        self.filters.len()
    }

    /// True when no filter is registered.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.filters.is_empty()
    }

    /// True when any filter rejects the candidate. Filters run in
    /// registration order and stop at the first rejection.
    #[must_use]
    pub fn should_suppress(&self, candidate: &ChatEntry) -> bool {
        self.filters.iter().any(|(_, filter)| filter(candidate))
    }
}

/// The transcript.
#[derive(Debug)]
pub struct ChatLog {
    buffer: VecDeque<ChatEntry>,
    max_entries: usize,
    local_player_guid: u32,
    revision: u64,
    sequence: u64,
    filter_language: bool,
    filter_language_patterns: Option<Vec<String>>,
    filters: ChatSuppressionFilters,
    /// Lines wait for filters the log cannot call (a session's plugins)
    /// instead of being kept.
    holding: bool,
    held: Vec<ChatEntry>,
}

impl Default for ChatLog {
    fn default() -> Self {
        Self::new()
    }
}

impl ChatLog {
    /// An empty transcript keeping [`DEFAULT_MAX_ENTRIES`] lines.
    #[must_use]
    pub fn new() -> Self {
        Self::with_max_entries(NonZeroUsize::new(DEFAULT_MAX_ENTRIES).unwrap_or(NonZeroUsize::MIN))
    }

    /// An empty transcript keeping `max_entries` lines.
    #[must_use]
    pub fn with_max_entries(max_entries: NonZeroUsize) -> Self {
        Self {
            buffer: VecDeque::new(),
            max_entries: max_entries.get(),
            local_player_guid: 0,
            revision: 0,
            sequence: 0,
            filter_language: false,
            filter_language_patterns: Some(Vec::new()),
            filters: ChatSuppressionFilters::default(),
            holding: false,
            held: Vec::new(),
        }
    }

    /// The lines, oldest first.
    pub fn entries(&self) -> impl ExactSizeIterator<Item = &ChatEntry> {
        self.buffer.iter()
    }

    /// A copy of the lines, oldest first.
    #[must_use]
    pub fn snapshot(&self) -> Vec<ChatEntry> {
        self.buffer.iter().cloned().collect()
    }

    /// How many lines are kept.
    #[must_use]
    pub fn len(&self) -> usize {
        self.buffer.len()
    }

    /// True when no line is kept.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.buffer.is_empty()
    }

    /// Advances on every append and every clear.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// The player's object id, so their own speech reads "You".
    pub fn set_local_player_guid(&mut self, guid: u32) {
        self.local_player_guid = guid;
    }

    /// Forgets the player's id at a session boundary. The transcript itself
    /// stays.
    pub fn reset_session_identity(&mut self) {
        self.local_player_guid = 0;
    }

    /// Turns the language filter on or off (the player's option). Checked
    /// once per line appended.
    pub fn set_filter_language(&mut self, on: bool) {
        self.filter_language = on;
    }

    /// The banned-word patterns; `None` when the pattern table failed to
    /// load (the filter then censors every word), empty when it loaded
    /// with none.
    pub fn set_filter_language_patterns(&mut self, patterns: Option<Vec<String>>) {
        self.filter_language_patterns = patterns;
    }

    /// The suppression filters.
    pub fn filters_mut(&mut self) -> &mut ChatSuppressionFilters {
        &mut self.filters
    }

    /// Local or ranged speech. The player's own echo (their id, or an empty
    /// sender) reads "You". `log_text_type` is the wire's chat type,
    /// passed through as is.
    pub fn on_local_speech(
        &mut self,
        now_unix_ms: i64,
        sender: &str,
        text: &str,
        sender_guid: u32,
        is_ranged: bool,
        log_text_type: u32,
    ) -> Option<&ChatEntry> {
        let is_own_echo = self.local_player_guid != 0 && sender_guid == self.local_player_guid;
        let effective_sender = if is_own_echo || sender.is_empty() {
            "You"
        } else {
            sender
        };
        let kind = if is_ranged {
            ChatKind::RangedSpeech
        } else {
            ChatKind::LocalSpeech
        };
        let entry = ChatEntry::new(kind, effective_sender, text, sender_guid, 0, log_text_type);
        self.append(now_unix_ms, entry)
    }

    /// A server-worded third-person emote.
    pub fn on_emote(
        &mut self,
        now_unix_ms: i64,
        sender_name: &str,
        text: &str,
        sender_guid: u32,
    ) -> Option<&ChatEntry> {
        let entry = ChatEntry::new(
            ChatKind::Emote,
            sender_name,
            text,
            sender_guid,
            0,
            LogTextType::Emote.as_u32(),
        );
        self.append(now_unix_ms, entry)
    }

    /// A soul emote.
    pub fn on_soul_emote(
        &mut self,
        now_unix_ms: i64,
        sender_name: &str,
        text: &str,
        sender_guid: u32,
    ) -> Option<&ChatEntry> {
        let entry = ChatEntry::new(
            ChatKind::SoulEmote,
            sender_name,
            text,
            sender_guid,
            0,
            LogTextType::Emote.as_u32(),
        );
        self.append(now_unix_ms, entry)
    }

    /// A death announcement. Not shown when the player is the victim or the
    /// killer (`local_player_guid` 0 means unknown, and then it shows): they
    /// get their own wording elsewhere.
    pub fn on_player_killed(
        &mut self,
        now_unix_ms: i64,
        death_message: &str,
        victim_guid: u32,
        killer_guid: u32,
        local_player_guid: u32,
    ) -> Option<&ChatEntry> {
        if local_player_guid != 0
            && (local_player_guid == victim_guid || local_player_guid == killer_guid)
        {
            return None;
        }
        let entry = ChatEntry::new(
            ChatKind::System,
            "",
            death_message,
            victim_guid,
            killer_guid,
            LogTextType::Default.as_u32(),
        );
        self.append(now_unix_ms, entry)
    }

    /// A message on a numbered channel or a chat room. Without an explicit
    /// text class, a numbered channel's own class is used: as sent when the
    /// line has no speaker (the server hands the player's own line back
    /// without a name), as heard otherwise.
    pub fn on_channel_broadcast(
        &mut self,
        now_unix_ms: i64,
        channel_id: u32,
        sender: &str,
        text: &str,
        log_text_type: Option<u32>,
        channel_name: &str,
    ) -> Option<&ChatEntry> {
        let own_line = sender.is_empty();
        let log_text_type = log_text_type
            .unwrap_or_else(|| legacy_channel_chat_type(channel_id, own_line).as_u32());
        let mut entry = ChatEntry::new(
            ChatKind::Channel,
            sender,
            text,
            0,
            channel_id,
            log_text_type,
        );
        entry.channel_name = channel_name.to_owned();
        self.append(now_unix_ms, entry)
    }

    /// A tell received. `log_text_type` is the wire's, passed through.
    /// `target_guid` is who it was addressed to (0 when not known): a tell
    /// whose speaker is its listener is one the character sent itself, and
    /// reads as a thought.
    pub fn on_tell_received(
        &mut self,
        now_unix_ms: i64,
        sender: &str,
        text: &str,
        sender_guid: u32,
        log_text_type: u32,
        target_guid: u32,
    ) -> Option<&ChatEntry> {
        let mut entry = ChatEntry::new(ChatKind::Tell, sender, text, sender_guid, 0, log_text_type);
        entry.target_guid = target_guid;
        self.append(now_unix_ms, entry)
    }

    /// A system message; the chat type is kept as both the channel number
    /// and the text class. Identical text can describe distinct events (two
    /// "Additional damage" notices), so every occurrence is kept.
    pub fn on_system_message(
        &mut self,
        now_unix_ms: i64,
        text: &str,
        chat_type: u32,
    ) -> Option<&ChatEntry> {
        let entry = ChatEntry::new(ChatKind::System, "", text, 0, chat_type, chat_type);
        self.append(now_unix_ms, entry)
    }

    /// A system line the client writes on a plugin's behalf: only its
    /// `<COORD:World:pair>` tags are read, as links; any other angle-bracket
    /// text is shown as written ([`ChatEntry::allows_markup`]).
    pub fn on_plugin_message(
        &mut self,
        now_unix_ms: i64,
        text: &str,
        chat_type: u32,
    ) -> Option<&ChatEntry> {
        let mut entry = ChatEntry::new(ChatKind::System, "", text, 0, chat_type, chat_type);
        entry.allows_markup = true;
        self.append(now_unix_ms, entry)
    }

    /// A message that also opened a popup.
    pub fn on_popup(&mut self, now_unix_ms: i64, text: &str) -> Option<&ChatEntry> {
        let entry = ChatEntry::new(
            ChatKind::Popup,
            "",
            text,
            0,
            0,
            LogTextType::Default.as_u32(),
        );
        self.append(now_unix_ms, entry)
    }

    /// A combat line.
    pub fn on_combat_line(
        &mut self,
        now_unix_ms: i64,
        text: &str,
        log_text_type: u32,
        kind: CombatLineKind,
    ) -> Option<&ChatEntry> {
        let mut entry = ChatEntry::new(ChatKind::Combat, "", text, 0, 0, log_text_type);
        entry.combat_kind = Some(kind);
        self.append(now_unix_ms, entry)
    }

    /// A combat notification turned into its chat line.
    pub fn on_combat_notice(
        &mut self,
        now_unix_ms: i64,
        notice: &CombatNotice,
    ) -> Option<&ChatEntry> {
        let (text, log_text_type, kind) = notice.chat_line();
        self.on_combat_line(now_unix_ms, &text, log_text_type.as_u32(), kind)
    }

    /// The player's own outgoing line. For a tell, `target_or_channel` is
    /// the addressee and becomes the sender; for a channel it is the
    /// channel's name. `channel_id` is the numbered channel the text went
    /// to, when it went to one: such a line is worded by its channel.
    pub fn on_self_sent(
        &mut self,
        now_unix_ms: i64,
        kind: ChatKind,
        text: &str,
        log_text_type: u32,
        target_or_channel: &str,
        channel_id: u32,
    ) -> Option<&ChatEntry> {
        let sender = if kind == ChatKind::Tell {
            target_or_channel
        } else {
            ""
        };
        let mut entry = ChatEntry::new(kind, sender, text, 0, channel_id, log_text_type);
        if kind == ChatKind::Channel {
            target_or_channel.clone_into(&mut entry.channel_name);
        }
        self.append(now_unix_ms, entry)
    }

    /// Drops every line. The sequence keeps counting.
    pub fn clear(&mut self) {
        self.buffer.clear();
        self.revision += 1;
    }

    fn append(&mut self, now_unix_ms: i64, mut entry: ChatEntry) -> Option<&ChatEntry> {
        entry.received_unix_ms = now_unix_ms;
        if self.filters.should_suppress(&entry) {
            return None;
        }
        if self.holding {
            self.held.push(entry);
            return None;
        }
        Some(self.admit(entry))
    }

    /// Lines wait for filters the log cannot call itself (`true`): a line
    /// that passes the log's own gates is held ([`Self::take_held`]) until
    /// its owner asks them and hands it back with [`Self::admit`]. Turning
    /// it off holds nothing new; lines already held stay for their owner.
    pub fn set_holding(&mut self, on: bool) {
        self.holding = on;
    }

    /// The lines held since the last call, in arrival order: not appended,
    /// with no sequence yet.
    pub fn take_held(&mut self) -> Vec<ChatEntry> {
        std::mem::take(&mut self.held)
    }

    /// Appends a line that passed every filter (a held one, or one just
    /// made): the language filter censors it, it gets its sequence, and the
    /// oldest line drops past the cap.
    pub fn admit(&mut self, mut entry: ChatEntry) -> &ChatEntry {
        if self.filter_language {
            entry.text =
                language_filter::censor(&entry.text, self.filter_language_patterns.as_deref());
        }
        self.sequence += 1;
        entry.sequence = self.sequence;
        self.buffer.push_back(entry);
        while self.buffer.len() > self.max_entries {
            self.buffer.pop_front();
        }
        self.revision += 1;
        let last = self.buffer.len() - 1;
        &self.buffer[last]
    }
}

/// The `H:mm:ss ` prefix (hour not padded, a trailing space) shown before a
/// line when timestamps are on. `utc_offset_seconds` is the player's local
/// offset from UTC; the caller knows the time zone, this crate does not.
#[must_use]
pub fn format_timestamp_prefix(received_unix_ms: i64, utc_offset_seconds: i32) -> String {
    let local_seconds = received_unix_ms
        .div_euclid(1000)
        .saturating_add(i64::from(utc_offset_seconds));
    let second_of_day = local_seconds.rem_euclid(86_400);
    let hour = second_of_day / 3600;
    let minute = second_of_day % 3600 / 60;
    let second = second_of_day % 60;
    format!("{hour}:{minute:02}:{second:02} ")
}

#[cfg(test)]
mod tests {
    use super::*;

    const T0: i64 = 1_786_000_000_000;

    fn texts(log: &ChatLog) -> Vec<String> {
        log.entries().map(|e| e.text.clone()).collect()
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/ChatLogTests.cs
    #[test]
    fn local_speech_appends_and_returns_the_entry() {
        let mut log = ChatLog::new();
        let seen = log
            .on_local_speech(T0, "Alice", "hi", 0xAA, false, 0x02)
            .cloned();
        assert_eq!(log.len(), 1);
        let seen = seen.expect("appended");
        assert_eq!(seen.kind, ChatKind::LocalSpeech);
        assert_eq!(seen.sender, "Alice");
        assert_eq!(seen.text, "hi");
        assert_eq!(seen.sequence, 1);
        assert_eq!(seen.received_unix_ms, T0);
    }

    #[test]
    fn ranged_speech_sets_ranged_kind() {
        let mut log = ChatLog::new();
        log.on_local_speech(T0, "Bob", "SHOUT", 0xBB, true, 0x02);
        assert_eq!(log.snapshot()[0].kind, ChatKind::RangedSpeech);
    }

    #[test]
    fn channel_broadcast_sets_channel_id() {
        let mut log = ChatLog::new();
        log.on_channel_broadcast(T0, 42, "Alice", "allegiance motd", None, "");
        let e = &log.snapshot()[0];
        assert_eq!(e.channel_id, 42);
        assert_eq!(e.kind, ChatKind::Channel);
    }

    #[test]
    fn tell_received_sets_tell_kind() {
        let mut log = ChatLog::new();
        log.on_tell_received(T0, "Alice", "psst", 0xAA, 0x03, 0);
        assert_eq!(log.snapshot()[0].kind, ChatKind::Tell);
    }

    #[test]
    fn system_message_encodes_chat_type_as_channel_id_and_class() {
        let mut log = ChatLog::new();
        log.on_system_message(T0, "Your spell fizzled!", 5);
        let e = &log.snapshot()[0];
        assert_eq!(e.kind, ChatKind::System);
        assert_eq!(e.channel_id, 5);
        assert_eq!(e.log_text_type, 5);
    }

    #[test]
    fn self_sent_tell_echoes_outbound_with_addressee_as_sender() {
        let mut log = ChatLog::new();
        log.on_self_sent(T0, ChatKind::Tell, "hey", 0x04, "Alice", 0);
        let e = &log.snapshot()[0];
        assert_eq!(e.sender, "Alice");
        assert_eq!(e.text, "hey");
        assert_eq!(e.log_text_type, 0x04);
    }

    #[test]
    fn self_sent_channel_keeps_explicit_class_and_name() {
        let mut log = ChatLog::new();
        log.on_self_sent(T0, ChatKind::Channel, "hi all", 0x0B, "Fellowship", 0);
        log.on_self_sent(T0, ChatKind::Channel, "hi all", 0x13, "Fellowship", 0);
        let snapshot = log.snapshot();
        assert_eq!(snapshot[0].log_text_type, 0x0B);
        assert_eq!(snapshot[1].log_text_type, 0x13);
        assert_eq!(snapshot[0].channel_name, "Fellowship");
        assert_eq!(snapshot[0].sender, "");
    }

    #[test]
    fn ring_buffer_drops_oldest_beyond_capacity() {
        let mut log = ChatLog::with_max_entries(NonZeroUsize::new(3).expect("nonzero"));
        for (sender, text) in [("A", "1"), ("B", "2"), ("C", "3"), ("D", "4")] {
            log.on_local_speech(T0, sender, text, 0, false, 0x02);
        }
        assert_eq!(texts(&log), ["2", "3", "4"]);
        // Sequence numbers are never reused after a drop.
        assert_eq!(log.snapshot()[0].sequence, 2);
    }

    #[test]
    fn clear_empties_buffer() {
        let mut log = ChatLog::new();
        log.on_local_speech(T0, "A", "1", 0, false, 0x02);
        let before = log.revision();
        log.clear();
        assert_eq!(log.len(), 0);
        assert!(log.revision() > before);
    }

    #[test]
    fn emote_and_soul_emote_append_with_emote_class() {
        let mut log = ChatLog::new();
        log.on_emote(T0, "Caith", "waves at you", 0xCAFE);
        log.on_soul_emote(T0, "Bob", "dances", 0xBEEF);
        let snapshot = log.snapshot();
        assert_eq!(snapshot[0].kind, ChatKind::Emote);
        assert_eq!(snapshot[0].sender, "Caith");
        assert_eq!(snapshot[0].text, "waves at you");
        assert_eq!(snapshot[0].sender_guid, 0xCAFE);
        assert_eq!(snapshot[0].log_text_type, 0x0C);
        assert_eq!(snapshot[1].kind, ChatKind::SoulEmote);
        assert_eq!(snapshot[1].sender_guid, 0xBEEF);
        assert_eq!(snapshot[1].log_text_type, 0x0C);
    }

    #[test]
    fn player_killed_appends_system_entry_and_stores_guids() {
        let mut log = ChatLog::new();
        log.on_player_killed(
            T0,
            "Caith was killed by a Drudge.",
            0x1234_5678,
            0x90AB_CDEF,
            0x1111_1111,
        );
        let e = &log.snapshot()[0];
        assert_eq!(e.kind, ChatKind::System);
        assert_eq!(e.text, "Caith was killed by a Drudge.");
        assert_eq!(e.sender_guid, 0x1234_5678);
        assert_eq!(e.channel_id, 0x90AB_CDEF);
        assert_eq!(e.log_text_type, 0x00);
    }

    #[test]
    fn player_killed_is_suppressed_for_victim_and_killer() {
        for local in [0x1234_5678, 0x90AB_CDEF] {
            let mut log = ChatLog::new();
            let appended = log
                .on_player_killed(
                    T0,
                    "Caith was killed by a Drudge.",
                    0x1234_5678,
                    0x90AB_CDEF,
                    local,
                )
                .is_some();
            assert!(!appended);
            assert!(log.is_empty());
        }
    }

    #[test]
    fn local_speech_empty_sender_substitutes_you() {
        let mut log = ChatLog::new();
        log.on_local_speech(T0, "", "hello", 0, false, 0x02);
        assert_eq!(log.snapshot()[0].sender, "You");
        assert_eq!(log.snapshot()[0].text, "hello");
    }

    #[test]
    fn local_speech_passes_wire_chat_type_verbatim() {
        let mut log = ChatLog::new();
        log.on_local_speech(T0, "Alice", "hi", 0xAA, false, 0x02);
        log.on_local_speech(T0, "Mosswart", "grumble", 0x5000_1234, false, 0x0C);
        assert_eq!(log.snapshot()[0].log_text_type, 0x02);
        assert_eq!(log.snapshot()[1].log_text_type, 0x0C);
    }

    #[test]
    fn combat_line_tags_entry_as_combat_with_its_tint() {
        let mut log = ChatLog::new();
        log.on_combat_line(
            T0,
            "You hit Mosswart for 5 slashing damage (50.0%).",
            0x06,
            CombatLineKind::Info,
        );
        log.on_combat_line(T0, "Mosswart hit you.", 0x06, CombatLineKind::Warning);
        log.on_combat_line(T0, "Attack failed.", 0x06, CombatLineKind::Error);
        let snapshot = log.snapshot();
        assert_eq!(snapshot[0].kind, ChatKind::Combat);
        assert_eq!(snapshot[0].combat_kind, Some(CombatLineKind::Info));
        assert_eq!(snapshot[0].log_text_type, 0x06);
        assert_eq!(snapshot[1].combat_kind, Some(CombatLineKind::Warning));
        assert_eq!(snapshot[2].combat_kind, Some(CombatLineKind::Error));
    }

    #[test]
    fn popup_has_default_class() {
        let mut log = ChatLog::new();
        log.on_popup(T0, "A modal message.");
        let e = &log.snapshot()[0];
        assert_eq!(e.kind, ChatKind::Popup);
        assert_eq!(e.log_text_type, 0x00);
    }

    #[test]
    fn tell_received_passes_wire_chat_type_verbatim() {
        let mut log = ChatLog::new();
        log.on_tell_received(T0, "Alice", "psst", 0xAA, 0x03, 0);
        log.on_tell_received(T0, "Alice", "psst", 0xAA, 0x1F, 0);
        assert_eq!(log.snapshot()[0].log_text_type, 0x03);
        assert_eq!(log.snapshot()[1].log_text_type, 0x1F);
    }

    #[test]
    fn channel_broadcast_derives_class_from_the_legacy_channel_bit() {
        let cases: [(u32, &str, u32); 9] = [
            (0x0800, "Fellowship", 0x13),
            (0x1000, "Patron", 0x0A),
            (0x2000, "Vassal", 0x0A),
            (0x4000, "Follower", 0x0A),
            (0x0100_0000, "Co-Vassals", 0x0A),
            (0x0200_0000, "Allegiance Broadcast", 0x0A),
            (0x0400_0000, "?", 0x08),
            (0x0400, "Help", 0x0F),
            (0x0900, "Audit", 0x08),
        ];
        for (bit, name, expected) in cases {
            let mut log = ChatLog::new();
            log.on_channel_broadcast(T0, bit, "Someone", "hi", None, name);
            assert_eq!(log.snapshot()[0].log_text_type, expected, "{bit:#X}");
        }
    }

    /// A speakerless line is the player's own handed back and takes the
    /// sent class (OpenAC `ChatLog.OnChannelBroadcast`): the audit
    /// echo of an admin command is 0x09, a patron line 0x0B, a fellowship
    /// broadcast 0x13.
    #[test]
    fn a_speakerless_channel_line_takes_the_sent_class() {
        let cases: [(u32, u32); 4] = [
            (0x0004, 0x09),
            (0x1000, 0x0B),
            (0x0400_0000, 0x13),
            (0x0400, 0x0F),
        ];
        for (channel, expected) in cases {
            let mut log = ChatLog::new();
            log.on_channel_broadcast(T0, channel, "", "@create 12702", None, "");
            assert_eq!(log.snapshot()[0].log_text_type, expected, "{channel:#X}");
        }
    }

    #[test]
    fn channel_broadcast_explicit_class_overrides_derivation() {
        let mut log = ChatLog::new();
        log.on_channel_broadcast(T0, 0x7000_0001, "Someone", "hi", Some(0x1B), "General");
        assert_eq!(log.snapshot()[0].log_text_type, 0x1B);
        assert_eq!(log.snapshot()[0].channel_name, "General");
    }

    fn filtered_log(on: bool, patterns: Option<Vec<String>>) -> ChatLog {
        let mut log = ChatLog::new();
        log.set_filter_language(on);
        log.set_filter_language_patterns(patterns);
        log
    }

    #[test]
    fn language_filter_on_censors_speech_tells_and_channels() {
        let zork = || Some(vec!["zork".to_owned()]);
        let mut log = filtered_log(true, zork());
        log.on_local_speech(T0, "Alice", "a zork b", 0xAA, false, 0x02);
        log.on_tell_received(T0, "Alice", "a zork b", 0xAA, 0x03, 0);
        log.on_channel_broadcast(T0, 42, "Alice", "a zork b", None, "");
        for e in log.entries() {
            assert_eq!(e.text, "a **** b");
            assert_eq!(e.sender, "Alice");
        }
    }

    #[test]
    fn language_filter_off_leaves_text_untouched() {
        let mut log = filtered_log(false, Some(vec!["zork".to_owned()]));
        log.on_local_speech(T0, "Alice", "a zork b", 0xAA, false, 0x02);
        assert_eq!(log.snapshot()[0].text, "a zork b");
    }

    #[test]
    fn language_filter_on_with_missing_table_censors_every_word() {
        let mut log = filtered_log(true, None);
        log.on_local_speech(T0, "Alice", "a zork b", 0xAA, false, 0x02);
        assert_eq!(log.snapshot()[0].text, "**** **** ****");
    }

    #[test]
    fn timestamp_prefix_uses_literal_colons_and_an_unpadded_hour() {
        // 2026-08-11 13:05:09 UTC.
        let at = 1_786_453_509_000;
        assert_eq!(format_timestamp_prefix(at, 0), "13:05:09 ");
        assert_eq!(format_timestamp_prefix(at, -13 * 3600), "0:05:09 ");
        assert_eq!(format_timestamp_prefix(at, 2 * 3600), "15:05:09 ");
    }

    #[test]
    fn suppression_filters_drop_lines_before_they_are_appended() {
        let mut log = ChatLog::new();
        let id = log
            .filters_mut()
            .register(Box::new(|entry: &ChatEntry| entry.text.contains("spam")));
        assert!(
            log.on_local_speech(T0, "A", "buy spam", 0, false, 0x02)
                .is_none()
        );
        assert!(
            log.on_local_speech(T0, "A", "hello", 0, false, 0x02)
                .is_some()
        );
        assert_eq!(texts(&log), ["hello"]);
        assert_eq!(log.snapshot()[0].sequence, 1);
        assert!(log.filters_mut().remove(id));
        assert!(!log.filters_mut().remove(id));
        assert!(
            log.on_local_speech(T0, "A", "buy spam", 0, false, 0x02)
                .is_some()
        );
    }

    #[test]
    fn filters_see_the_uncensored_text() {
        let mut log = filtered_log(true, Some(vec!["zork".to_owned()]));
        log.filters_mut()
            .register(Box::new(|entry: &ChatEntry| entry.text == "zork"));
        assert!(
            log.on_local_speech(T0, "A", "zork", 0, false, 0x02)
                .is_none()
        );
    }

    // Held lines (filters the log cannot call, OpenAC ChatLog.Append's
    // suppression gate run by the line's owner): the held line has no
    // sequence, the language filter and the sequence come when it is
    // admitted.
    #[test]
    fn held_lines_wait_unappended_and_are_censored_when_admitted() {
        let mut log = filtered_log(true, Some(vec!["zork".to_owned()]));
        log.set_holding(true);
        assert!(log.on_system_message(T0, "zork here", 0).is_none());
        assert!(log.on_system_message(T0 + 10, "zork here", 0).is_none());
        assert!(log.is_empty());
        let held = log.take_held();
        assert_eq!(held.len(), 2, "a repeated line is held too");
        assert_eq!((held[0].text.as_str(), held[0].sequence), ("zork here", 0));
        assert_eq!(held[0].received_unix_ms, T0);
        let admitted = log.admit(held[0].clone()).clone();
        assert_eq!(
            (admitted.text.as_str(), admitted.sequence),
            ("**** here", 1)
        );
        log.set_holding(false);
        assert!(log.on_system_message(T0 + 5000, "plain", 0).is_some());
        assert!(log.take_held().is_empty());
    }

    // OpenAC tests/AcDream.Core.Tests/Chat/ChatLogSystemMessageTests.cs
    // (v0.1.25, #198: repeated server messages are kept)
    #[test]
    fn system_identical_messages_keep_both_entries() {
        let mut log = ChatLog::new();
        log.on_system_message(T0, "Unknown command: help", 0);
        log.on_system_message(T0, "Unknown command: help", 0);
        assert_eq!(log.len(), 2);
    }

    #[test]
    fn system_different_text_both_show() {
        let mut log = ChatLog::new();
        log.on_system_message(T0, "Welcome to Asheron's Call", 0);
        log.on_system_message(
            T0,
            "Use @acecommands to get a complete list of commands.",
            0,
        );
        assert_eq!(log.len(), 2);
    }

    #[test]
    fn system_identical_burst_keeps_every_entry() {
        let mut log = ChatLog::new();
        for _ in 0..3 {
            log.on_system_message(T0, "Unknown command: help", 0);
        }
        assert_eq!(log.len(), 3);
    }

    #[test]
    fn system_repeated_text_around_another_line_keeps_the_order() {
        let mut log = ChatLog::new();
        log.on_system_message(T0, "Unknown command: help", 0);
        log.on_system_message(T0, "Welcome to Asheron's Call", 0);
        log.on_system_message(T0, "Unknown command: help", 0);
        let texts: Vec<String> = log.entries().map(|e| e.text.clone()).collect();
        assert_eq!(
            texts,
            [
                "Unknown command: help",
                "Welcome to Asheron's Call",
                "Unknown command: help"
            ]
        );
    }

    #[test]
    fn system_identical_text_with_different_types_keeps_both_types() {
        let mut log = ChatLog::new();
        let (own, enemy) = (
            LogTextType::CombatSelf.as_u32(),
            LogTextType::CombatEnemy.as_u32(),
        );
        log.on_system_message(T0, "Additional damage: 5", own);
        log.on_system_message(T0, "Additional damage: 5", enemy);
        let types: Vec<u32> = log.entries().map(|e| e.log_text_type).collect();
        assert_eq!(types, [own, enemy]);
    }

    #[test]
    fn repeated_server_damage_between_ordinary_hits_keeps_all_four_lines() {
        let mut log = ChatLog::new();
        let hit = CombatNotice::DamageDealt {
            defender_name: "Target".to_owned(),
            damage_type: 0x01,
            damage: 10,
            damage_percent: 0.1,
            critical: false,
            attack_conditions: 0,
        };
        let own = LogTextType::CombatSelf.as_u32();
        log.on_combat_notice(T0, &hit);
        log.on_system_message(T0, "Additional damage: 5", own);
        log.on_combat_notice(T0, &hit);
        log.on_system_message(T0, "Additional damage: 5", own);
        let entries = log.snapshot();
        assert_eq!(entries.len(), 4);
        assert_eq!(entries[0].text, entries[2].text);
        assert_eq!(entries[1].text, entries[3].text);
        let kinds: Vec<ChatKind> = entries.iter().map(|e| e.kind).collect();
        assert_eq!(
            kinds,
            [
                ChatKind::Combat,
                ChatKind::System,
                ChatKind::Combat,
                ChatKind::System
            ]
        );
    }

    // OpenAC tests/AcDream.Core.Tests/Chat/ChatLogLocalGuidTests.cs
    #[test]
    fn own_guid_match_substitutes_you() {
        let mut log = ChatLog::new();
        log.set_local_player_guid(0x5000_000A);
        log.on_local_speech(T0, "+Acdream", "hello world", 0x5000_000A, false, 0x02);
        let e = &log.snapshot()[0];
        assert_eq!(e.kind, ChatKind::LocalSpeech);
        assert_eq!(e.sender, "You");
        assert_eq!(e.text, "hello world");
    }

    #[test]
    fn different_guid_keeps_sender_name() {
        let mut log = ChatLog::new();
        log.set_local_player_guid(0x5000_000A);
        log.on_local_speech(T0, "Caith", "hi", 0x5000_0042, false, 0x02);
        assert_eq!(log.snapshot()[0].sender, "Caith");
    }

    #[test]
    fn no_local_guid_falls_back_to_empty_sender_substitution() {
        let mut log = ChatLog::new();
        log.on_local_speech(T0, "", "anyone home?", 0, true, 0x02);
        assert_eq!(log.snapshot()[0].sender, "You");
        assert_eq!(log.snapshot()[0].kind, ChatKind::RangedSpeech);
    }

    #[test]
    fn reset_session_identity_keeps_transcript_but_clears_guid() {
        let mut log = ChatLog::new();
        let (old_guid, new_guid) = (0x5000_0001, 0x5000_0002);
        log.set_local_player_guid(old_guid);
        log.on_system_message(T0, "session boundary", 1);
        log.on_local_speech(T0, "Old", "before", old_guid, false, 0x02);

        log.reset_session_identity();
        log.on_system_message(T0, "session boundary", 1);
        log.on_local_speech(T0, "Old", "after", old_guid, false, 0x02);
        log.set_local_player_guid(new_guid);
        log.on_local_speech(T0, "New", "new", new_guid, false, 0x02);

        let entries = log.snapshot();
        assert_eq!(entries.len(), 5);
        assert_eq!(entries[1].sender, "You");
        assert_eq!(entries[3].sender, "Old");
        assert_eq!(entries[4].sender, "You");
    }

    // OpenAC tests/AcDream.Core.Tests/Chat/CombatChatTranslatorTests.cs
    #[test]
    fn combat_notice_lands_as_a_combat_line() {
        let mut log = ChatLog::new();
        log.on_combat_notice(
            T0,
            &CombatNotice::DamageDealt {
                defender_name: "Mosswart Defiler".to_owned(),
                damage_type: 0x01,
                damage: 12,
                damage_percent: 0.54,
                critical: false,
                attack_conditions: 0,
            },
        );
        let e = &log.snapshot()[0];
        assert_eq!(e.kind, ChatKind::Combat);
        assert_eq!(e.combat_kind, Some(CombatLineKind::Info));
        assert_eq!(
            e.text,
            "You mangle Mosswart Defiler for 12 points of slashing damage!"
        );
        assert_eq!(e.log_text_type, 0x16);
    }
}
