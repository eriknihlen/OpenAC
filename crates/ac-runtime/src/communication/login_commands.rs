//! The session document's login commands: typed into the chat box one at a
//! time after the character enters the world, a fixed delay apart.
//!
//! The server takes nothing from a character until the client has said its
//! login is complete, which comes some time after the world is entered, so
//! the list waits until the server is listening. Each generation runs the
//! list once; a new generation (a reconnect, a return through character
//! selection) runs it again from the top, and a generation that ends stops
//! it.

use std::time::Duration;

use super::SubmitOutcome;
use crate::session::Generation;

/// A login command the chat box could not route.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LoginCommandFailure {
    /// Its place in the list, from 0.
    pub command_index: usize,
    /// The line as configured.
    pub command: String,
    /// Why.
    pub error: String,
}

/// The configured commands and how far through them this generation is.
#[derive(Debug, Clone, Default)]
pub struct LoginCommandSequence {
    commands: Vec<String>,
    delay: Duration,
    generation: Generation,
    last_started: Option<Generation>,
    next_deadline: Duration,
    next_index: usize,
    active: bool,
}

impl LoginCommandSequence {
    /// The commands, a `delay` apart.
    #[must_use]
    pub fn new(commands: Vec<String>, delay: Duration) -> Self {
        Self {
            commands,
            delay,
            ..Self::default()
        }
    }

    /// How many commands are configured.
    #[must_use]
    pub fn command_count(&self) -> usize {
        self.commands.len()
    }

    /// A command waits to run in this generation.
    #[must_use]
    pub fn is_active(&self) -> bool {
        self.active
    }

    /// The next command's place in the list.
    #[must_use]
    pub fn next_command_index(&self) -> usize {
        self.next_index
    }

    /// The character entered the world in `generation` at `now`: the list
    /// starts (once a generation), its first command at once when the
    /// server is listening.
    pub fn entered_world(
        &mut self,
        generation: Generation,
        listening: bool,
        now: &mut dyn FnMut() -> Duration,
        submit: &mut dyn FnMut(&str) -> SubmitOutcome,
        failures: &mut Vec<LoginCommandFailure>,
    ) {
        if self.last_started == Some(generation) {
            return;
        }
        self.last_started = Some(generation);
        self.generation = generation;
        self.next_index = 0;
        self.active = !self.commands.is_empty();
        self.next_deadline = now();
        self.drain_due(generation, listening, now, submit, failures);
    }

    /// Once a pass: run every command whose time has come.
    pub fn tick(
        &mut self,
        generation: Generation,
        in_world: bool,
        now: &mut dyn FnMut() -> Duration,
        submit: &mut dyn FnMut(&str) -> SubmitOutcome,
        failures: &mut Vec<LoginCommandFailure>,
    ) {
        self.drain_due(generation, in_world, now, submit, failures);
    }

    /// The generation ended: the rest of its list is dropped.
    pub fn cancel(&mut self, generation: Generation) {
        if self.active && self.generation == generation {
            self.active = false;
        }
    }

    fn drain_due(
        &mut self,
        generation: Generation,
        in_world: bool,
        now: &mut dyn FnMut() -> Duration,
        submit: &mut dyn FnMut(&str) -> SubmitOutcome,
        failures: &mut Vec<LoginCommandFailure>,
    ) {
        if !self.active || !in_world || generation != self.generation {
            return;
        }
        let mut at = now();
        while self.active && self.next_index < self.commands.len() && at >= self.next_deadline {
            let index = self.next_index;
            let command = self.commands[index].clone();
            let outcome = submit(&command);
            if matches!(
                outcome,
                SubmitOutcome::UnknownCommand | SubmitOutcome::Dropped
            ) {
                failures.push(LoginCommandFailure {
                    command_index: index,
                    command,
                    error: format!("Chat command routing returned {outcome:?}."),
                });
            }
            self.next_index += 1;
            if self.next_index >= self.commands.len() {
                self.active = false;
                return;
            }
            // The delay counts from when the command finished, not from
            // when it was due.
            at = now();
            self.next_deadline = at.saturating_add(self.delay);
        }
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Runtime.Tests/Chat/LoginCommandSequenceTests.cs`.

    use std::cell::Cell;

    use super::*;

    const MS: Duration = Duration::from_millis(1);

    struct Harness {
        time: Cell<Duration>,
        sent: Vec<String>,
        failures: Vec<LoginCommandFailure>,
    }

    impl Harness {
        fn new() -> Self {
            Self {
                time: Cell::new(Duration::ZERO),
                sent: Vec::new(),
                failures: Vec::new(),
            }
        }

        fn advance(&self, by: Duration) {
            self.time.set(self.time.get() + by);
        }

        /// Talk goes out; a bare `/` is an unknown command.
        fn route(sent: &mut Vec<String>, line: &str) -> SubmitOutcome {
            if line.trim() == "/" {
                return SubmitOutcome::UnknownCommand;
            }
            if line.trim().is_empty() {
                return SubmitOutcome::Empty;
            }
            sent.push(line.to_owned());
            SubmitOutcome::Sent
        }

        fn entered(&mut self, s: &mut LoginCommandSequence, generation: u64, listening: bool) {
            let time = &self.time;
            let sent = &mut self.sent;
            s.entered_world(
                Generation(generation),
                listening,
                &mut || time.get(),
                &mut |line| Self::route(sent, line),
                &mut self.failures,
            );
        }

        fn tick(&mut self, s: &mut LoginCommandSequence, generation: u64, in_world: bool) {
            let time = &self.time;
            let sent = &mut self.sent;
            s.tick(
                Generation(generation),
                in_world,
                &mut || time.get(),
                &mut |line| Self::route(sent, line),
                &mut self.failures,
            );
        }
    }

    fn list(lines: &[&str]) -> Vec<String> {
        lines.iter().map(|&l| l.to_owned()).collect()
    }

    #[test]
    fn entering_runs_the_first_at_once_then_keeps_the_delay() {
        // EnteredWorldExecutesImmediatelyThenHonorsMonotonicDelay.
        let mut h = Harness::new();
        let mut s = LoginCommandSequence::new(list(&["one", "two", "three"]), 500 * MS);
        h.entered(&mut s, 7, true);
        assert_eq!(h.sent, ["one"]);
        h.advance(499 * MS);
        h.tick(&mut s, 7, true);
        assert_eq!(h.sent, ["one"]);
        h.advance(MS);
        h.tick(&mut s, 7, true);
        assert_eq!(h.sent, ["one", "two"]);
        h.advance(500 * MS);
        h.tick(&mut s, 7, true);
        assert_eq!(h.sent, ["one", "two", "three"]);
        assert!(!s.is_active());
    }

    #[test]
    fn nothing_goes_out_until_the_server_is_listening() {
        // NothingIsSentUntilTheServerIsListening.
        let mut h = Harness::new();
        let mut s = LoginCommandSequence::new(list(&["one", "two"]), 500 * MS);
        h.entered(&mut s, 7, false);
        h.advance(Duration::from_secs(5));
        h.tick(&mut s, 7, false);
        assert!(h.sent.is_empty());
        h.tick(&mut s, 7, true);
        assert_eq!(h.sent, ["one"]);
        h.advance(500 * MS);
        h.tick(&mut s, 7, true);
        assert_eq!(h.sent, ["one", "two"]);
    }

    #[test]
    fn the_time_a_command_takes_does_not_shorten_the_delay() {
        // HandlerRuntimeDoesNotConsumeTheInterCommandDelay: the command
        // itself takes a second.
        let time = Cell::new(Duration::ZERO);
        let mut sent = Vec::new();
        let mut failures = Vec::new();
        let mut s = LoginCommandSequence::new(list(&["one", "two"]), 500 * MS);
        let mut run = |s: &mut LoginCommandSequence, enter: bool| {
            let mut submit = |line: &str| {
                sent.push(line.to_owned());
                time.set(time.get() + Duration::from_secs(1));
                SubmitOutcome::Sent
            };
            let mut now = || time.get();
            if enter {
                s.entered_world(Generation(7), true, &mut now, &mut submit, &mut failures);
            } else {
                s.tick(Generation(7), true, &mut now, &mut submit, &mut failures);
            }
        };
        run(&mut s, true);
        time.set(time.get() + 499 * MS);
        run(&mut s, false);
        time.set(time.get() + MS);
        run(&mut s, false);
        assert_eq!(sent, ["one", "two"]);
    }

    #[test]
    fn a_routing_failure_is_reported_and_the_list_goes_on() {
        // ParseAndHandlerFailuresAndStatusFailureAllContinue (the routing
        // half; handlers here cannot throw).
        let mut h = Harness::new();
        let mut s = LoginCommandSequence::new(list(&["/", "after"]), Duration::ZERO);
        h.entered(&mut s, 1, true);
        assert_eq!(h.sent, ["after"]);
        assert_eq!(
            h.failures,
            [LoginCommandFailure {
                command_index: 0,
                command: "/".into(),
                error: "Chat command routing returned UnknownCommand.".into(),
            }]
        );
        assert!(!s.is_active());
    }

    #[test]
    fn cancel_and_a_new_generation_stop_the_old_list_and_restart() {
        // CancelAndGenerationReplacementPreventLeaksAndReconnectRestarts.
        let mut h = Harness::new();
        let mut s = LoginCommandSequence::new(list(&["one", "two"]), 500 * MS);
        h.entered(&mut s, 1, true);
        s.cancel(Generation(1));
        h.advance(Duration::from_secs(1));
        h.tick(&mut s, 1, true);
        h.entered(&mut s, 1, true); // once per generation
        assert_eq!(h.sent, ["one"]);
        h.entered(&mut s, 2, true);
        h.tick(&mut s, 1, true); // a stale generation does nothing
        h.advance(500 * MS);
        h.tick(&mut s, 2, true);
        assert_eq!(h.sent, ["one", "one", "two"]);
    }

    #[test]
    fn an_empty_list_never_starts() {
        // NullAndEmptyCollectionsAreNoOpsAndNullEntriesTypeAsEmpty.
        let mut h = Harness::new();
        let mut s = LoginCommandSequence::new(Vec::new(), Duration::ZERO);
        h.entered(&mut s, 1, true);
        assert!(!s.is_active());
        let mut blank = LoginCommandSequence::new(list(&[""]), Duration::ZERO);
        h.entered(&mut blank, 1, true);
        assert!(!blank.is_active());
        assert!(h.sent.is_empty() && h.failures.is_empty());
    }
}
