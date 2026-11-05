//! A game session: login, character selection, the world, log off.

mod chargen_table;
pub mod creation;
pub mod diagnostics;
mod flow;
mod generation;
pub mod interface;
mod link;
mod outbox;
pub mod policy;
pub mod pregame;
mod relogin;
#[cfg(test)]
mod routing_tests;
mod runner;
pub mod selection;
mod state;

pub use chargen_table::project_chargen;
pub use diagnostics::NetDiagnostics;
pub use flow::{
    CONNECT_TIMEOUT, CharacterSelector, ENTER_WORLD_TIMEOUT, LOGOFF_CONFIRMATION_TIMEOUT, Phase,
    SessionError, SessionEvent, SessionFlow,
};
pub use generation::{CommandStatus, Generation, Lifecycle, LifecycleState};
pub use link::{LinkStatus, PingClock};
pub use outbox::{Outbox, Outgoing, Queue};
pub use relogin::{LOST_CONNECTION_RETRY_INTERVAL, Relogin};
pub use runner::{
    HostEvent, LifecycleCommand, LoginAutomation, ObjectSummary, PresentationEvent,
    RECONNECT_QUIESCENCE, Recall, ServerRefusal, SessionCommand, SessionConfig, SessionEnd,
    SessionHandle, SessionSummary, spawn,
};
pub use state::{Clocks, SessionState};
