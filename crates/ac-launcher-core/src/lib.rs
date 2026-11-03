//! The launcher's logic, without a window: what OpenAC keeps in
//! `AcDream.Launcher.Core`.
//!
//! - [`profiles`]: servers, accounts and characters, one validated document.
//! - [`status`]: reading a session's status file; server reachability.
//! - [`launching`]: composing the session-config document, supervising the
//!   client process, reading its console text.
//! - [`orchestration`]: the launcher's state: profiles, running sessions,
//!   capabilities and the reconnect hold.
//! - [`installation`] and [`integrity`]: the DAT directory, the install
//!   record, file hashes.
//! - [`updates`]: versions, the release manifest, safe zip extraction, the
//!   versioned client store and the client updater.
//! - [`plugins`]: the curated list, manifests, compatibility, installed
//!   inventory and the plugin installer.
//! - [`control`]: the optional same-user local endpoint test runs drive the
//!   launcher through (`--control-pipe`).
//! - [`classic_migration`]: copying a classic VirindiTank and UtilityBelt
//!   setup to where MossTank reads it.
//! - [`layout_gate`]: moving the install into the current folder layout at
//!   start, and clearing out old logs; [`launcher_log`]: the launcher's own
//!   log.
//!
//! Nothing here opens a network connection. Every HTTP request goes through
//! [`net::HttpTransport`], which the host implements (the TLS choice is the
//! host's).

pub mod cancel;
pub mod classic_migration;
pub mod control;
pub mod fsutil;
pub mod ids;
pub mod installation;
pub mod integrity;
pub mod json;
pub mod launcher_log;
pub mod launching;
pub mod layout_gate;
pub mod net;
pub mod orchestration;
pub mod plugins;
pub mod profiles;
pub mod status;
pub mod time;
pub mod updates;
pub mod url;
pub mod window;
