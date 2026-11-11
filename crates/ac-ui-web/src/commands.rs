//! What the module sends the session: every command stamped with the
//! session's login generation and encoded as one command frame, but only
//! while the browser has control; the held movement keys sent again while
//! held, so the relay knows input is still coming; and the keys let go
//! when control or the keyboard goes.

use ac_remote_wire::EncodeContext;
use ac_runtime::movement::{MovementCommand, MovementKeys};
use ac_runtime::session::{Generation, SessionCommand};

use crate::host::Session;

/// How often held movement keys are sent again, milliseconds: the relay
/// takes control away after a second without input.
pub const HELD_KEYS_REPEAT_MS: f64 = 250.0;

/// The command frames waiting for the next frame, and what is needed to
/// make them.
#[derive(Debug, Default)]
pub struct Outbox {
    session: Session,
    frames: Vec<Vec<u8>>,
    /// The movement keys last sent, and when.
    held: MovementKeys,
    held_sent_ms: f64,
    errors: Vec<String>,
}

impl Outbox {
    /// Nothing sent; no control.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The session as the relay last described it.
    #[must_use]
    pub fn session(&self) -> Session {
        self.session
    }

    /// What the browser may do changed. Returns true when it had control
    /// and has it no longer (its keys are then to be let go).
    pub fn set_session(&mut self, session: Session) -> bool {
        let lost = self.session.controlling && !session.controlling;
        self.session = session;
        lost
    }

    /// Send `command` at page time `now_ms`; dropped unless the browser
    /// has control.
    pub fn send(&mut self, command: SessionCommand, now_ms: f64, cx: &EncodeContext) {
        if !self.session.controlling {
            return;
        }
        if let SessionCommand::Movement(MovementCommand::Keys(keys)) = &command {
            self.held = *keys;
            self.held_sent_ms = now_ms;
        }
        self.stamp(command, cx);
    }

    /// The held movement keys again, when they are held, the browser has
    /// control and [`HELD_KEYS_REPEAT_MS`] passed since they were last
    /// sent.
    pub fn repeat_held(&mut self, now_ms: f64, cx: &EncodeContext) {
        if self.session.controlling
            && self.held != MovementKeys::default()
            && now_ms - self.held_sent_ms >= HELD_KEYS_REPEAT_MS
        {
            self.send(
                SessionCommand::Movement(MovementCommand::Keys(self.held)),
                now_ms,
                cx,
            );
        }
    }

    /// Every key is let go: the session hears it (`tell`: the browser had
    /// control until now), and nothing held is sent again.
    pub fn release_keys(&mut self, tell: bool, cx: &EncodeContext) {
        self.held = MovementKeys::default();
        if tell {
            self.stamp(SessionCommand::Movement(MovementCommand::ReleaseKeys), cx);
        }
    }

    fn stamp(&mut self, command: SessionCommand, cx: &EncodeContext) {
        let stamped = SessionCommand::AtGeneration {
            generation: Generation(self.session.generation),
            command: Box::new(command),
        };
        match ac_remote_wire::encode_command(&stamped, cx) {
            Ok(frame) => self.frames.push(frame),
            Err(error) => self
                .errors
                .push(format!("ui: a command cannot be sent: {error}")),
        }
    }

    /// The frames to send, oldest first.
    pub fn take_frames(&mut self) -> Vec<Vec<u8>> {
        std::mem::take(&mut self.frames)
    }

    /// Commands that could not be encoded, for the log.
    pub fn take_errors(&mut self) -> Vec<String> {
        std::mem::take(&mut self.errors)
    }
}

#[cfg(test)]
mod tests {
    use ac_clock::Instant;
    use ac_remote_wire::DecodeContext;

    use super::*;

    fn cx() -> (EncodeContext, DecodeContext) {
        let epoch = Instant::now();
        (
            EncodeContext::new(epoch),
            DecodeContext::new(epoch, None, None),
        )
    }

    fn forward() -> SessionCommand {
        SessionCommand::Movement(MovementCommand::Keys(MovementKeys {
            forward: true,
            ..MovementKeys::default()
        }))
    }

    fn controlling(generation: u64) -> Session {
        Session {
            generation,
            controlling: true,
            may_control: true,
            ..Session::default()
        }
    }

    /// A command as it compares (commands have no equality of their own).
    fn show(command: &SessionCommand) -> String {
        format!("{command:?}")
    }

    /// The commands of the frames waiting, unstamped, each checked for
    /// its generation.
    fn sent(outbox: &mut Outbox, d: &DecodeContext, generation: u64) -> Vec<String> {
        outbox
            .take_frames()
            .iter()
            .map(
                |frame| match ac_remote_wire::decode_command(frame, d).unwrap() {
                    SessionCommand::AtGeneration {
                        generation: g,
                        command,
                    } => {
                        assert_eq!(g, Generation(generation));
                        show(&command)
                    }
                    other => panic!("not stamped: {other:?}"),
                },
            )
            .collect()
    }

    #[test]
    fn nothing_is_sent_without_control() {
        let (e, _) = cx();
        let mut outbox = Outbox::new();
        outbox.send(forward(), 0.0, &e);
        outbox.set_session(Session {
            generation: 3,
            may_control: true,
            ..Session::default()
        });
        outbox.send(forward(), 0.0, &e);
        outbox.repeat_held(1000.0, &e);
        assert!(outbox.take_frames().is_empty());
    }

    #[test]
    fn a_command_is_stamped_with_the_generation_and_framed() {
        let (e, d) = cx();
        let mut outbox = Outbox::new();
        outbox.set_session(controlling(7));
        outbox.send(forward(), 0.0, &e);
        assert_eq!(sent(&mut outbox, &d, 7), [show(&forward())]);
        assert!(outbox.take_frames().is_empty(), "taken");
    }

    #[test]
    fn held_keys_go_again_every_quarter_second_while_held() {
        let (e, d) = cx();
        let mut outbox = Outbox::new();
        outbox.set_session(controlling(1));
        outbox.send(forward(), 1000.0, &e);
        sent(&mut outbox, &d, 1);
        outbox.repeat_held(1249.0, &e);
        assert!(outbox.take_frames().is_empty(), "not yet");
        outbox.repeat_held(1250.0, &e);
        assert_eq!(sent(&mut outbox, &d, 1), [show(&forward())]);
        outbox.repeat_held(1400.0, &e);
        assert!(outbox.take_frames().is_empty());
        outbox.repeat_held(1500.0, &e);
        assert_eq!(sent(&mut outbox, &d, 1), [show(&forward())]);
        // Let go: nothing is held, nothing repeats.
        outbox.send(
            SessionCommand::Movement(MovementCommand::Keys(MovementKeys::default())),
            1600.0,
            &e,
        );
        sent(&mut outbox, &d, 1);
        outbox.repeat_held(5000.0, &e);
        assert!(outbox.take_frames().is_empty());
    }

    #[test]
    fn losing_control_lets_the_keys_go_once_and_stops_the_repeat() {
        let (e, d) = cx();
        let mut outbox = Outbox::new();
        outbox.set_session(controlling(2));
        outbox.send(forward(), 0.0, &e);
        sent(&mut outbox, &d, 2);
        let lost = outbox.set_session(Session {
            generation: 2,
            may_control: true,
            ..Session::default()
        });
        assert!(lost);
        outbox.release_keys(lost, &e);
        assert_eq!(
            sent(&mut outbox, &d, 2),
            [show(&SessionCommand::Movement(
                MovementCommand::ReleaseKeys
            ))]
        );
        outbox.set_session(controlling(2));
        outbox.repeat_held(10_000.0, &e);
        assert!(outbox.take_frames().is_empty(), "nothing held any more");
        assert!(!outbox.set_session(controlling(2)), "still controlling");
    }
}
