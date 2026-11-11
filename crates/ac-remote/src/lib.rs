//! A session's link to a remote-window relay.
//!
//! The client dials out to a relay over a secure WebSocket; a browser connects
//! to the same relay. This crate is the transport: text frames carry JSON
//! control messages ([`protocol`]), binary frames carry opaque payload bytes.

mod base64;
pub mod protocol;
mod secret;
mod sha1;
pub mod url;
pub mod ws;

pub use secret::Secret;
pub use url::{RelayUrl, UrlError};
