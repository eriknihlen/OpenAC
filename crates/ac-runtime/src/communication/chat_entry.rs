//! The chat entry: the line being typed, where a plain line goes when it is
//! sent (the talk focus: a channel or one listener), and the lines sent
//! before it. One owner serves every front end, so a line typed at a
//! console does exactly what the same line typed in a chat box does.

use super::{ChatChannelKind, Defaults, SubmitOutcome};

/// How many sent lines are remembered for recall.
pub const HISTORY_LIMIT: usize = 100;

/// A request to the chat entry.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ChatEntryCommand {
    /// Send this line (`None`: the draft as it stands), routed with the
    /// entry's talk focus, remembered, and the draft cleared.
    Submit(Option<String>),
    /// The draft as the player typed it in a front end (which shows it
    /// already, so the shown revision does not move).
    SetDraft(String),
    /// Stage a line without sending it (refused while one is being typed).
    Compose(String),
    /// Aim plain text at a channel (any tell target is dropped).
    SetChannel(ChatChannelKind),
    /// Aim plain text at one listener by name and object id.
    SetTellTarget {
        /// The listener's name.
        name: String,
        /// Their object id (0: by name).
        guid: u32,
    },
    /// Put the line before the one recalled in the draft.
    RecallPrevious,
    /// Put the line after the one recalled in the draft.
    RecallNext,
    /// Stop walking back through the lines sent before (the player typed).
    ResetRecall,
    /// Empty the draft and stop recalling.
    Clear,
}

/// The entry's state.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatEntryState {
    history: Vec<String>,
    recall_index: Option<usize>,
    draft: String,
    composed_draft_pending: bool,
    channel: ChatChannelKind,
    tell_target: Option<String>,
    tell_target_guid: u32,
    /// Moves each time the draft changes.
    draft_revision: u64,
    /// Moves each time the draft changes other than by a front end's
    /// typing (a staged line, a recall, a send): front ends show it then.
    shown_revision: u64,
}

impl Default for ChatEntryState {
    fn default() -> Self {
        Self {
            history: Vec::new(),
            recall_index: None,
            draft: String::new(),
            composed_draft_pending: false,
            channel: ChatChannelKind::Say,
            tell_target: None,
            tell_target_guid: 0,
            draft_revision: 0,
            shown_revision: 0,
        }
    }
}

impl ChatEntryState {
    /// The line as it stands, unsent.
    #[must_use]
    pub fn draft(&self) -> &str {
        &self.draft
    }

    /// Moves each time the draft changes (a front end echoes it).
    #[must_use]
    pub fn draft_revision(&self) -> u64 {
        self.draft_revision
    }

    /// Moves each time the draft changes other than by a front end's
    /// typing ([`Self::type_draft`]): a front end shows the draft then.
    #[must_use]
    pub fn shown_revision(&self) -> u64 {
        self.shown_revision
    }

    /// Where a plain line goes.
    #[must_use]
    pub fn active_channel(&self) -> ChatChannelKind {
        self.channel
    }

    /// Who a tell goes to while the entry is aimed at one.
    #[must_use]
    pub fn tell_target(&self) -> Option<&str> {
        self.tell_target.as_deref()
    }

    /// The object id behind the tell target.
    #[must_use]
    pub fn tell_target_guid(&self) -> u32 {
        self.tell_target_guid
    }

    /// A composed line waits to be sent (the entry counts as being typed
    /// into until then).
    #[must_use]
    pub fn is_input_active(&self) -> bool {
        self.composed_draft_pending
    }

    /// How many lines are remembered.
    #[must_use]
    pub fn count(&self) -> usize {
        self.history.len()
    }

    /// A recalled line is in the entry.
    #[must_use]
    pub fn is_recalling(&self) -> bool {
        self.recall_index.is_some()
    }

    /// The draft as the player left it; setting what it already is changes
    /// nothing.
    pub fn set_draft(&mut self, text: &str) {
        if self.draft != text {
            text.clone_into(&mut self.draft);
            self.draft_revision += 1;
            self.shown_revision += 1;
        }
    }

    /// The draft as a front end's player typed it: the change
    /// [`Self::set_draft`] makes, but that front end shows it already, so
    /// the shown revision stays.
    pub fn type_draft(&mut self, text: &str) {
        if self.draft != text {
            text.clone_into(&mut self.draft);
            self.draft_revision += 1;
        }
    }

    /// Stage text without sending it; refused while the entry is being
    /// typed into.
    pub fn compose(&mut self, text: &str) -> bool {
        if self.is_input_active() {
            return false;
        }
        self.reset_recall();
        self.set_draft(text);
        self.composed_draft_pending = true;
        true
    }

    /// Send a line (`None`: the draft) the way the chat box sends it:
    /// `route` gets it with the entry's talk focus. The line is remembered
    /// and the draft cleared, as Enter in the entry does.
    pub fn submit(
        &mut self,
        text: Option<&str>,
        route: impl FnOnce(&str, Defaults<'_>) -> SubmitOutcome,
    ) -> SubmitOutcome {
        let line = text.map_or_else(|| self.draft.clone(), str::to_owned);
        self.composed_draft_pending = false;
        if line.trim().is_empty() {
            self.clear();
            return SubmitOutcome::Empty;
        }
        let defaults = Defaults {
            channel: self.channel,
            tell_target: self.tell_target.as_deref(),
            tell_target_guid: self.tell_target_guid,
        };
        let outcome = route(&line, defaults);
        self.remember(line);
        self.clear();
        outcome
    }

    /// Empty the draft and stop recalling.
    pub fn clear(&mut self) {
        self.reset_recall();
        self.set_draft("");
    }

    /// Remember a line that was sent.
    pub fn remember(&mut self, line: String) {
        self.history.push(line);
        if self.history.len() > HISTORY_LIMIT {
            self.history.remove(0);
        }
        self.recall_index = None;
    }

    /// The line before the one being recalled (the newest at first); `None`
    /// with nothing remembered.
    pub fn recall_previous(&mut self) -> Option<String> {
        if self.history.is_empty() {
            return None;
        }
        let index = match self.recall_index {
            None => self.history.len() - 1,
            Some(index) => index.saturating_sub(1),
        };
        self.recall_index = Some(index);
        let line = self.history[index].clone();
        self.set_draft(&line);
        Some(line)
    }

    /// The line after the one being recalled; `None` (and an empty entry)
    /// past the newest, `None` (entry untouched) when not recalling.
    pub fn recall_next(&mut self) -> Option<String> {
        let index = self.recall_index? + 1;
        if index >= self.history.len() {
            self.recall_index = None;
            self.set_draft("");
            return None;
        }
        self.recall_index = Some(index);
        let line = self.history[index].clone();
        self.set_draft(&line);
        Some(line)
    }

    /// Stop recalling.
    pub fn reset_recall(&mut self) {
        self.recall_index = None;
    }

    /// Aim plain text at a channel; a channel and a listener are
    /// alternatives, so the tell target goes.
    pub fn set_channel(&mut self, channel: ChatChannelKind) {
        self.channel = channel;
        self.tell_target = None;
        self.tell_target_guid = 0;
    }

    /// Aim plain text at one listener.
    pub fn set_tell_target(&mut self, name: &str, guid: u32) {
        if name.is_empty() {
            return;
        }
        self.channel = ChatChannelKind::Tell;
        self.tell_target = Some(name.to_owned());
        self.tell_target_guid = guid;
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Runtime.Tests/Chat/RuntimeChatEntryOwnerTests.cs`
    //! (the router stands behind `route`; what it was given is recorded).

    use super::*;

    #[derive(Debug, PartialEq, Eq)]
    struct Routed {
        line: String,
        channel: ChatChannelKind,
        tell_target: Option<String>,
        tell_target_guid: u32,
    }

    fn send(
        entry: &mut ChatEntryState,
        text: Option<&str>,
        sent: &mut Vec<Routed>,
    ) -> SubmitOutcome {
        entry.submit(text, |line, defaults| {
            sent.push(Routed {
                line: line.to_owned(),
                channel: defaults.channel,
                tell_target: defaults.tell_target.map(str::to_owned),
                tell_target_guid: defaults.tell_target_guid,
            });
            SubmitOutcome::Sent
        })
    }

    #[test]
    fn setting_the_draft_to_what_it_is_changes_nothing() {
        let mut entry = ChatEntryState::default();
        entry.set_draft("hello");
        let revision = entry.draft_revision();
        entry.set_draft("hello");
        assert_eq!(entry.draft_revision(), revision);
        assert_eq!(entry.draft(), "hello");
    }

    #[test]
    fn composing_stages_the_line_and_counts_as_typing_until_sent() {
        let mut entry = ChatEntryState::default();
        assert!(!entry.is_input_active());
        assert!(entry.compose("/vt nav"));
        assert_eq!(entry.draft(), "/vt nav");
        assert!(entry.is_input_active());
        // A second compose while one waits is refused.
        assert!(!entry.compose("something else"));
        send(&mut entry, None, &mut Vec::new());
        assert!(!entry.is_input_active());
        assert_eq!(entry.draft(), "");
    }

    #[test]
    fn sending_routes_through_the_active_channel_or_the_tell_target() {
        let mut entry = ChatEntryState::default();
        let mut sent = Vec::new();
        entry.set_channel(ChatChannelKind::Fellowship);
        assert_eq!(
            send(&mut entry, Some("group up"), &mut sent),
            SubmitOutcome::Sent
        );
        entry.set_tell_target("Bob", 0x5000_000A);
        send(&mut entry, Some("hi"), &mut sent);
        assert_eq!(
            sent,
            [
                Routed {
                    line: "group up".into(),
                    channel: ChatChannelKind::Fellowship,
                    tell_target: None,
                    tell_target_guid: 0
                },
                Routed {
                    line: "hi".into(),
                    channel: ChatChannelKind::Tell,
                    tell_target: Some("Bob".into()),
                    tell_target_guid: 0x5000_000A
                }
            ]
        );
    }

    #[test]
    fn choosing_a_channel_drops_the_tell_target() {
        let mut entry = ChatEntryState::default();
        entry.set_tell_target("Bob", 0x5000_000A);
        entry.set_channel(ChatChannelKind::Trade);
        assert_eq!(entry.tell_target(), None);
        assert_eq!(entry.tell_target_guid(), 0);
        assert_eq!(entry.active_channel(), ChatChannelKind::Trade);
    }

    #[test]
    fn an_empty_line_clears_and_sends_nothing() {
        let mut entry = ChatEntryState::default();
        let mut sent = Vec::new();
        entry.set_draft("   ");
        assert_eq!(send(&mut entry, None, &mut sent), SubmitOutcome::Empty);
        assert!(sent.is_empty());
        assert_eq!(entry.draft(), "");
        assert_eq!(entry.count(), 0);
    }

    #[test]
    fn sending_over_a_pending_draft_sends_what_was_asked_and_clears_it() {
        let mut entry = ChatEntryState::default();
        let mut sent = Vec::new();
        assert!(entry.compose("half a sentence"));
        send(&mut entry, Some("hello"), &mut sent);
        assert_eq!(sent[0].line, "hello");
        assert_eq!(entry.draft(), "");
        assert!(!entry.is_input_active());
    }

    #[test]
    fn walking_back_reaches_the_line_before_last_and_sends_it_again() {
        let mut entry = ChatEntryState::default();
        let mut sent = Vec::new();
        for line in ["first", "second", "third"] {
            send(&mut entry, Some(line), &mut sent);
        }
        assert_eq!(entry.recall_previous().as_deref(), Some("third"));
        assert_eq!(entry.recall_previous().as_deref(), Some("second"));
        assert_eq!(entry.draft(), "second");
        send(&mut entry, None, &mut sent);
        assert_eq!(
            sent.iter().map(|r| r.line.as_str()).collect::<Vec<_>>(),
            ["first", "second", "third", "second"]
        );
    }

    #[test]
    fn walking_forward_past_the_newest_empties_the_entry() {
        let mut entry = ChatEntryState::default();
        entry.remember("first".into());
        assert_eq!(entry.recall_previous().as_deref(), Some("first"));
        assert_eq!(entry.recall_next(), None);
        assert_eq!(entry.draft(), "");
        assert!(!entry.is_recalling());
    }

    #[test]
    fn walking_forward_without_walking_back_does_nothing() {
        let mut entry = ChatEntryState::default();
        entry.remember("first".into());
        entry.set_draft("half a sen");
        assert_eq!(entry.recall_next(), None);
        assert_eq!(entry.draft(), "half a sen");
    }

    #[test]
    fn only_the_last_hundred_lines_are_kept() {
        let mut entry = ChatEntryState::default();
        for index in 0..HISTORY_LIMIT + 5 {
            entry.remember(format!("line {index}"));
        }
        assert_eq!(entry.count(), HISTORY_LIMIT);
        assert_eq!(
            entry.recall_previous(),
            Some(format!("line {}", HISTORY_LIMIT + 4))
        );
    }
}
