//! What the module sends the session: every command stamped with the
//! session's login generation (the latest the relay named) and encoded as
//! one command frame, but only while the browser has control; while it has
//! control, the movement keys held (none held is a set too) at least every
//! quarter second, so the relay hears input is still coming; and the keys
//! let go when control or the keyboard goes.

use ac_remote_wire::EncodeContext;
use ac_runtime::movement::{MovementCommand, MovementKeys};
use ac_runtime::session::{Generation, SessionCommand};

use crate::host::Session;

/// How often a frame goes while the browser has control, milliseconds of
/// page time at most between two: the relay takes control away after a
/// second without input.
pub const HEARTBEAT_MS: f64 = 250.0;

/// The command frames waiting for the next frame, and what is needed to
/// make them.
#[derive(Debug, Default)]
pub struct Outbox {
    session: Session,
    frames: Vec<Vec<u8>>,
    /// The movement keys held, as last sent.
    held: MovementKeys,
    /// When a frame last went (page time); none since control came.
    last_frame_ms: Option<f64>,
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
        if !self.session.controlling && session.controlling {
            // Control came: the first beat goes with the next frame.
            self.last_frame_ms = None;
        }
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
        }
        self.stamp(command, now_ms, cx);
    }

    /// While the browser has control, the held movement keys (all up when
    /// none is held) when no frame went for [`HEARTBEAT_MS`]: one frame
    /// however long it was.
    pub fn heartbeat(&mut self, now_ms: f64, cx: &EncodeContext) {
        let due = self
            .last_frame_ms
            .is_none_or(|last| now_ms - last >= HEARTBEAT_MS);
        if self.session.controlling && due {
            self.send(
                SessionCommand::Movement(MovementCommand::Keys(self.held)),
                now_ms,
                cx,
            );
        }
    }

    /// Every key is let go: the session hears it (`tell`: the browser had
    /// control until now), and nothing is held from now on.
    pub fn release_keys(&mut self, tell: bool, now_ms: f64, cx: &EncodeContext) {
        self.held = MovementKeys::default();
        if tell {
            self.stamp(
                SessionCommand::Movement(MovementCommand::ReleaseKeys),
                now_ms,
                cx,
            );
        }
    }

    fn stamp(&mut self, command: SessionCommand, now_ms: f64, cx: &EncodeContext) {
        let stamped = SessionCommand::AtGeneration {
            generation: Generation(self.session.generation),
            command: Box::new(command),
        };
        match ac_remote_wire::encode_command(&stamped, cx) {
            Ok(frame) => {
                self.frames.push(frame);
                self.last_frame_ms = Some(now_ms);
            }
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

    fn keys(keys: MovementKeys) -> String {
        show(&SessionCommand::Movement(MovementCommand::Keys(keys)))
    }

    fn released() -> String {
        keys(MovementKeys::default())
    }

    #[test]
    fn nothing_is_sent_without_control() {
        let (e, _) = cx();
        let mut outbox = Outbox::new();
        outbox.send(forward(), 0.0, &e);
        outbox.heartbeat(0.0, &e);
        outbox.set_session(Session {
            generation: 3,
            may_control: true,
            ..Session::default()
        });
        outbox.send(forward(), 0.0, &e);
        outbox.heartbeat(1000.0, &e);
        assert!(outbox.take_frames().is_empty());
    }

    #[test]
    fn a_command_is_stamped_with_the_latest_generation_and_framed() {
        let (e, d) = cx();
        let mut outbox = Outbox::new();
        outbox.set_session(controlling(7));
        outbox.send(forward(), 0.0, &e);
        assert_eq!(sent(&mut outbox, &d, 7), [show(&forward())]);
        assert!(outbox.take_frames().is_empty(), "taken");
        outbox.set_session(controlling(3));
        outbox.set_session(controlling(4));
        outbox.send(forward(), 10.0, &e);
        assert_eq!(sent(&mut outbox, &d, 4), [show(&forward())]);
    }

    #[test]
    fn with_control_and_no_key_held_the_keys_go_every_quarter_second() {
        let (e, d) = cx();
        let mut outbox = Outbox::new();
        outbox.set_session(controlling(1));
        outbox.heartbeat(1000.0, &e);
        assert_eq!(sent(&mut outbox, &d, 1), [released()], "at once");
        outbox.heartbeat(1100.0, &e);
        outbox.heartbeat(1249.0, &e);
        assert!(outbox.take_frames().is_empty(), "not yet");
        outbox.heartbeat(1250.0, &e);
        assert_eq!(sent(&mut outbox, &d, 1), [released()]);
        outbox.heartbeat(1499.0, &e);
        assert!(outbox.take_frames().is_empty());
        outbox.heartbeat(1500.0, &e);
        assert_eq!(sent(&mut outbox, &d, 1), [released()]);
        // A jump of several seconds: one frame, not one per quarter.
        outbox.heartbeat(9000.0, &e);
        assert_eq!(sent(&mut outbox, &d, 1), [released()]);
        outbox.heartbeat(9100.0, &e);
        assert!(outbox.take_frames().is_empty());
    }

    #[test]
    fn the_held_keys_are_what_goes_again() {
        let (e, d) = cx();
        let mut outbox = Outbox::new();
        outbox.set_session(controlling(1));
        outbox.send(forward(), 1000.0, &e);
        sent(&mut outbox, &d, 1);
        outbox.heartbeat(1249.0, &e);
        assert!(outbox.take_frames().is_empty(), "a frame went at 1000");
        outbox.heartbeat(1250.0, &e);
        assert_eq!(sent(&mut outbox, &d, 1), [show(&forward())]);
        // Any frame counts: the next beat is a quarter second after it.
        outbox.send(SessionCommand::Chat("hi".into()), 1400.0, &e);
        sent(&mut outbox, &d, 1);
        outbox.heartbeat(1600.0, &e);
        assert!(outbox.take_frames().is_empty());
        outbox.heartbeat(1650.0, &e);
        assert_eq!(sent(&mut outbox, &d, 1), [show(&forward())]);
        // Let go: the empty set goes on.
        outbox.send(
            SessionCommand::Movement(MovementCommand::Keys(MovementKeys::default())),
            1700.0,
            &e,
        );
        sent(&mut outbox, &d, 1);
        outbox.heartbeat(1950.0, &e);
        assert_eq!(sent(&mut outbox, &d, 1), [released()]);
    }

    #[test]
    fn losing_control_lets_the_keys_go_once_and_stops_the_beat() {
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
        outbox.release_keys(lost, 10.0, &e);
        assert_eq!(
            sent(&mut outbox, &d, 2),
            [show(&SessionCommand::Movement(
                MovementCommand::ReleaseKeys
            ))]
        );
        outbox.heartbeat(10_000.0, &e);
        assert!(outbox.take_frames().is_empty(), "no control, no beat");
        outbox.set_session(controlling(2));
        outbox.heartbeat(10_000.0, &e);
        assert_eq!(sent(&mut outbox, &d, 2), [released()], "nothing held");
        assert!(!outbox.set_session(controlling(2)), "still controlling");
    }
}
