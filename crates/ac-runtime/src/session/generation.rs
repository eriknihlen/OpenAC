//! Session generations: every connection, every return to character
//! selection and every stop starts a new one. A command that names a
//! generation is carried out only in that generation, so a request made
//! before a reconnect can never act on the session after it.

use std::fmt;

/// One life of the session (a connection, or the time between two returns
/// to character selection). Zero is "never started".
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub struct Generation(pub u64);

impl Generation {
    /// The generation after this one.
    #[must_use]
    pub fn next(self) -> Self {
        Self(self.0.wrapping_add(1))
    }
}

impl fmt::Display for Generation {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.0)
    }
}

/// How the session answered a command.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CommandStatus {
    /// Carried out.
    Accepted,
    /// The session is not in a state where the command applies.
    Inactive,
    /// The command named a generation that has ended.
    StaleGeneration,
    /// The command applies, but its owner refused it.
    Rejected,
}

/// Where the session is in its life, as a host follows it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LifecycleState {
    /// Not connected (before the first connection, between a stop and a
    /// reconnect, and at the end).
    Stopped,
    /// Connecting, at character selection, or entering the world.
    Starting,
    /// A character is in the world.
    InWorld,
    /// The character is leaving the world.
    Stopping,
    /// The session failed.
    Faulted,
}

/// The session's lifecycle as one value.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Lifecycle {
    /// The current generation.
    pub generation: Generation,
    /// The state.
    pub state: LifecycleState,
    /// The character in the world (0 outside it).
    pub player_guid: u32,
    /// Whether a connection is open.
    pub has_transport: bool,
}

impl Lifecycle {
    /// Before anything started.
    #[must_use]
    pub fn stopped(generation: Generation) -> Self {
        Self {
            generation,
            state: LifecycleState::Stopped,
            player_guid: 0,
            has_transport: false,
        }
    }
}
