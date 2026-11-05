//! Everything the session sends, queued in order, and the one game-action
//! counter every numbered action takes its number from.
//!
//! Login, chat, social and every later gameplay request go through the same
//! [`Outbox`], so two owners can never hand out the same action number.

use std::collections::VecDeque;

use ac_proto::GameActionSequence;

/// Which message queue a body travels on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Queue {
    /// Ordinary requests: character selection, game actions, log off.
    Ui,
    /// The game-data check.
    Database,
    /// Control messages (the legacy friends-list request, character
    /// restore).
    Control,
    /// Login messages (character create and delete).
    Login,
}

/// A message body the session wants sent.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Outgoing {
    /// The message body, opcode first.
    pub body: Vec<u8>,
    /// The queue to send it on.
    pub queue: Queue,
}

/// The session's outgoing queue and its game-action counter.
#[derive(Debug, Default)]
pub struct Outbox {
    sequence: GameActionSequence,
    pending: VecDeque<Outgoing>,
}

impl Outbox {
    /// Empty, with the counter at zero (the first action carries 1).
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Queue a body that does not take an action number.
    pub fn send(&mut self, body: Vec<u8>, queue: Queue) {
        self.pending.push_back(Outgoing { body, queue });
    }

    /// Queue a game action on the UI queue; `build` gets the next action
    /// number. Returns the number used.
    pub fn send_action(&mut self, build: impl FnOnce(u32) -> Vec<u8>) -> u32 {
        let sequence = self.sequence.advance();
        self.send(build(sequence), Queue::Ui);
        sequence
    }

    /// Queue a game action whose body can fail to build (a string too long
    /// for the wire). The number is taken only when the body was built, so a
    /// refused action leaves no gap in the sequence.
    pub fn try_send_action<E>(
        &mut self,
        build: impl FnOnce(u32) -> Result<Vec<u8>, E>,
    ) -> Result<u32, E> {
        let sequence = self.sequence.last().wrapping_add(1);
        let body = build(sequence)?;
        let taken = self.sequence.advance();
        debug_assert_eq!(taken, sequence);
        self.send(body, Queue::Ui);
        Ok(sequence)
    }

    /// The number the last action took (0 before any).
    #[must_use]
    pub fn last_sequence(&self) -> u32 {
        self.sequence.last()
    }

    /// Start numbering again, as on return to character selection.
    pub fn reset_sequence(&mut self) {
        self.sequence.reset();
    }

    /// How many bodies wait to be sent.
    #[must_use]
    pub fn len(&self) -> usize {
        self.pending.len()
    }

    /// True when nothing waits to be sent.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.pending.is_empty()
    }

    /// Take the bodies waiting to be sent, oldest first.
    pub fn drain(&mut self) -> std::collections::vec_deque::Drain<'_, Outgoing> {
        self.pending.drain(..)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_proto::chat::{build_talk, build_tell};
    use ac_proto::{ProtoError, build_house_query};

    #[test]
    fn every_action_takes_the_next_number_from_one_counter() {
        // OpenAC `WorldSessionChatTests.SendChannel_IncrementsSequence_*`:
        // consecutive actions carry 1, 2, ... whoever sends them.
        let mut outbox = Outbox::new();
        outbox.send_action(build_house_query);
        outbox
            .try_send_action(|seq| build_talk(seq, "hello"))
            .unwrap();
        outbox
            .try_send_action(|seq| build_tell(seq, "Alice", "hey"))
            .unwrap();
        let bodies: Vec<_> = outbox.drain().map(|o| o.body).collect();
        assert_eq!(
            bodies,
            [
                build_house_query(1),
                build_talk(2, "hello").unwrap(),
                build_tell(3, "Alice", "hey").unwrap(),
            ]
        );
        assert_eq!(outbox.last_sequence(), 3);
    }

    #[test]
    fn a_refused_action_takes_no_number() {
        let mut outbox = Outbox::new();
        let refused: Result<u32, ProtoError> = outbox.try_send_action(|_| {
            Err(ProtoError::InvalidArgument {
                what: "test refusal",
            })
        });
        assert!(refused.is_err());
        assert!(outbox.is_empty());
        assert_eq!(outbox.send_action(build_house_query), 1);
    }

    #[test]
    fn reset_starts_numbering_again() {
        let mut outbox = Outbox::new();
        outbox.send_action(build_house_query);
        outbox.reset_sequence();
        assert_eq!(outbox.send_action(build_house_query), 1);
        assert_eq!(outbox.len(), 2);
    }
}
