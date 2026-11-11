//! A game session: login, character selection, the world, log off.
//!
//! The commands a window sends, the stream it is fed and the views on it
//! build on their own; the session state and the runner that drives it
//! (thread, network, plugins) come with the `host` feature.

mod account_end;
mod chargen_table;
mod clocks;
mod commands;
pub mod creation;
#[cfg(feature = "host")]
pub mod diagnostics;
mod flow;
mod generation;
pub mod interface;
mod link;
mod outbox;
#[cfg(feature = "host")]
pub mod policy;
pub mod pregame;
// Its recovery steps are the runner's; without it only the public facts stay
// in use.
#[cfg_attr(not(feature = "host"), allow(dead_code))]
mod relogin;
#[cfg(test)]
mod routing_tests;
#[cfg(feature = "host")]
mod runner;
pub mod selection;
#[cfg(feature = "host")]
mod state;
pub mod world_tap;

pub use chargen_table::project_chargen;
pub use clocks::Clocks;
pub use commands::{LifecycleCommand, PresentationEvent, Recall, SessionCommand};
#[cfg(feature = "host")]
pub use diagnostics::NetDiagnostics;
pub use flow::{
    CONNECT_TIMEOUT, CharacterSelector, ENTER_WORLD_TIMEOUT, LOGOFF_CONFIRMATION_TIMEOUT, Phase,
    SessionError, SessionEvent, SessionFlow,
};
pub use generation::{CommandStatus, Generation, Lifecycle, LifecycleState};
pub use link::{LinkStatus, PingClock};
pub use outbox::{Outbox, Outgoing, Queue};
pub use relogin::{LOST_CONNECTION_RETRY_INTERVAL, Relogin};
#[cfg(feature = "host")]
pub use runner::{
    HostEvent, LoginAutomation, ObjectSummary, RECONNECT_QUIESCENCE, ServerRefusal, SessionConfig,
    SessionEnd, SessionHandle, SessionSummary, spawn,
};
#[cfg(feature = "host")]
pub use state::SessionState;
