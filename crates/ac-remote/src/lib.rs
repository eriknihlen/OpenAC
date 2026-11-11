//! A session's link to a remote-window relay.
//!
//! The client dials out to a relay over a secure WebSocket; a browser connects
//! to the same relay. This crate is the transport: text frames carry JSON
//! control messages ([`protocol`]), binary frames carry opaque payload bytes.
//!
//! - [`Link::start`] runs one worker thread per relay link. It connects,
//!   sends the hello, and reports what happens as [`Inbound`] events; it
//!   reconnects by itself, with backoff, after any loss. The game never
//!   waits on it.
//! - [`Link::send_control`] and [`Link::send_binary`] queue messages while
//!   connected; [`Link::update_hello`] keeps the hello (and, while
//!   connected, the relay) up to date.
//! - [`ws`] is the WebSocket client underneath, [`url`] the relay address.
//!
//! Messages queued for a connection never go out on the next one: a loss
//! clears the queue before [`Inbound::Disconnected`] is sent. A message
//! queued after the next `Connected`, by a caller that had not yet read the
//! `Disconnected`, does go out; the relay drops payload frames until the
//! next `attached`, so such a straggler is harmless.

mod base64;
pub mod endpoint;
pub mod link;
mod net;
pub mod protocol;
mod secret;
mod sha1;
pub mod url;
pub mod ws;

pub use link::{Inbound, Link, LinkConfig, LinkLoss, SendError, Timing};
pub use secret::Secret;
pub use url::{RelayUrl, UrlError};
