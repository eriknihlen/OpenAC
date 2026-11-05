//! Who the reply verbs address: the last person who sent the player a tell
//! (reply), the last person the player sent one to (retell), and the last
//! monarch and patron who spoke on their allegiance channels.

use super::{CHANNEL_MONARCH, CHANNEL_PATRON, ChatEntry, ChatKind, is_player_object_id};

/// The reply and retell targets, learned from the committed transcript.
///
/// The runtime feeds it every line the [`super::ChatLog`] appends (the
/// `on_*` methods return the appended line) and every tell the player sends.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ChatCommandTargetState {
    last_incoming_tell_sender: Option<String>,
    last_outgoing_tell_target: Option<String>,
    last_monarch_sender: Option<String>,
    last_patron_sender: Option<String>,
}

impl ChatCommandTargetState {
    /// No targets yet.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Who last sent the player a tell (the reply target).
    #[must_use]
    pub fn last_incoming_tell_sender(&self) -> Option<&str> {
        self.last_incoming_tell_sender.as_deref()
    }

    /// Who the player last sent a tell to (the retell target).
    #[must_use]
    pub fn last_outgoing_tell_target(&self) -> Option<&str> {
        self.last_outgoing_tell_target.as_deref()
    }

    /// Who last spoke on the monarch channel.
    #[must_use]
    pub fn last_monarch_sender(&self) -> Option<&str> {
        self.last_monarch_sender.as_deref()
    }

    /// Who last spoke on the patron channel.
    #[must_use]
    pub fn last_patron_sender(&self) -> Option<&str> {
        self.last_patron_sender.as_deref()
    }

    /// Records who a tell was just addressed to, so the retell verb has a
    /// target. The send records it directly because the transcript line an
    /// outgoing tell produces comes back from the server and carries no
    /// addressee to key on.
    pub fn note_outgoing_tell(&mut self, target_name: &str) {
        if !target_name.is_empty() {
            self.last_outgoing_tell_target = Some(target_name.to_owned());
        }
    }

    /// Learns from a line just appended to the transcript. A tell with a
    /// sender id was received; one without was the player's own. Only a
    /// tell from another player names who to reply to: one the character
    /// sent itself, or one from a creature or an item, is shown but names
    /// nobody. A channel line from the monarch or patron channel names who
    /// to answer there; the player's own channel lines (no sender) name
    /// nobody.
    pub fn observe(&mut self, entry: &ChatEntry) {
        if entry.sender.is_empty() {
            return;
        }
        match entry.kind {
            ChatKind::Tell => {
                if entry.is_tell_to_self() {
                    return;
                }
                if entry.sender_guid == 0 {
                    self.last_outgoing_tell_target = Some(entry.sender.clone());
                } else if is_player_object_id(entry.sender_guid) {
                    self.last_incoming_tell_sender = Some(entry.sender.clone());
                }
            }
            ChatKind::Channel => match entry.channel_id {
                CHANNEL_MONARCH => self.last_monarch_sender = Some(entry.sender.clone()),
                CHANNEL_PATRON => self.last_patron_sender = Some(entry.sender.clone()),
                _ => {}
            },
            _ => {}
        }
    }

    /// Forgets every target at a session boundary.
    pub fn reset_session(&mut self) {
        *self = Self::default();
    }
}

#[cfg(test)]
mod tests {
    use super::super::ChatLog;
    use super::*;

    const T0: i64 = 1_786_000_000_000;

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/ChatCommandTargetStateTests.cs
    #[test]
    fn tracks_reply_and_retell_targets_from_committed_transcript() {
        let mut chat = ChatLog::new();
        let mut targets = ChatCommandTargetState::new();

        if let Some(e) = chat.on_tell_received(T0, "Bestie", "incoming", 0x5000_0001, 0x03, 0) {
            targets.observe(e);
        }
        if let Some(e) = chat.on_self_sent(T0, ChatKind::Tell, "outgoing", 0x04, "Caith", 0) {
            targets.observe(e);
        }

        assert_eq!(targets.last_incoming_tell_sender(), Some("Bestie"));
        assert_eq!(targets.last_outgoing_tell_target(), Some("Caith"));
    }

    #[test]
    fn tracks_independent_monarch_and_patron_reply_targets() {
        let mut chat = ChatLog::new();
        let mut targets = ChatCommandTargetState::new();
        for (channel, sender, text) in [
            (0x4000, "Monarch", "orders"),
            (0x2000, "Patron", "hello"),
            (0x4000, "New Monarch", "new orders"),
        ] {
            if let Some(e) = chat.on_channel_broadcast(T0, channel, sender, text, None, "") {
                targets.observe(e);
            }
        }
        assert_eq!(targets.last_monarch_sender(), Some("New Monarch"));
        assert_eq!(targets.last_patron_sender(), Some("Patron"));
    }

    #[test]
    fn reset_session_forgets_targets_but_preserves_transcript() {
        let mut chat = ChatLog::new();
        let mut targets = ChatCommandTargetState::new();
        if let Some(e) = chat.on_tell_received(T0, "Bestie", "incoming", 0x5000_0001, 0x03, 0) {
            targets.observe(e);
        }
        if let Some(e) = chat.on_self_sent(T0, ChatKind::Tell, "outgoing", 0x04, "Caith", 0) {
            targets.observe(e);
        }

        targets.reset_session();

        assert_eq!(targets.last_incoming_tell_sender(), None);
        assert_eq!(targets.last_outgoing_tell_target(), None);
        assert_eq!(targets.last_monarch_sender(), None);
        assert_eq!(targets.last_patron_sender(), None);
        assert_eq!(chat.len(), 2);
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/ChatWordingFaithfulnessTests.cs
    const SELF: u32 = 0x5000_000A;
    const OTHER: u32 = 0x5000_0077;

    fn observed(tells: &[(&str, u32)]) -> ChatCommandTargetState {
        let mut chat = ChatLog::new();
        let mut targets = ChatCommandTargetState::new();
        for (sender, guid) in tells {
            if let Some(e) = chat.on_tell_received(T0, sender, "hi", *guid, 0x03, SELF) {
                targets.observe(e);
            }
        }
        targets
    }

    #[test]
    fn a_tell_to_self_is_not_someone_to_reply_to() {
        let targets = observed(&[("Bob", OTHER), ("Acdream", SELF)]);
        assert_eq!(targets.last_incoming_tell_sender(), Some("Bob"));
    }

    #[test]
    fn a_tell_from_a_creature_is_not_someone_to_reply_to() {
        let targets = observed(&[("Bob", OTHER), ("Town Crier", 0x8000_1234)]);
        assert_eq!(targets.last_incoming_tell_sender(), Some("Bob"));
    }

    #[test]
    fn the_whole_player_range_is_someone_to_reply_to() {
        for sender in [0x5000_0001, 0x6FFF_FFFF] {
            let targets = observed(&[("Edge", sender)]);
            assert_eq!(targets.last_incoming_tell_sender(), Some("Edge"));
        }
    }

    #[test]
    fn just_outside_the_player_range_is_not_someone_to_reply_to() {
        for sender in [0x5000_0000, 0x7000_0000] {
            let targets = observed(&[("Edge", sender)]);
            assert_eq!(targets.last_incoming_tell_sender(), None);
        }
    }

    #[test]
    fn note_outgoing_tell_ignores_an_empty_name() {
        let mut targets = ChatCommandTargetState::new();
        targets.note_outgoing_tell("Caith");
        targets.note_outgoing_tell("");
        assert_eq!(targets.last_outgoing_tell_target(), Some("Caith"));
    }
}
