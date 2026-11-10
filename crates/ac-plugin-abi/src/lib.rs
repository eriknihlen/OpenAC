//! The calling convention between the openAC client and its WebAssembly
//! plugins, shared by the client (`ac-plugin`) and the plugin SDK
//! (`openac-plugin`).
//!
//! A plugin is a plain wasm module (no component model, no WASI). It
//! imports client functions from module [`MODULE`], one import per function
//! ([`Import`], typed by the marker types in [`calls`]), and exports the
//! entry points in [`exports`]. Every argument and reply is a record encoded
//! with [`wire`] and passed as `(pointer, length)` in the plugin's memory.
//! This crate is the contract, one module per API group.
//!
//! A released API version never changes: a new function, event or record
//! field arrives in the next minor version, and a field or event added
//! later is marked with the version it arrived in, so a plugin built for an
//! older version keeps its layout. Every version's shape is pinned by the
//! [`layout`] fingerprint, and the client compares a plugin with the layout
//! of the version it was built for.

pub mod callback;
pub mod character;
pub mod chat_coordinates;
pub mod combat;
pub mod host;
pub mod imports;
pub mod items;
pub mod keys;
pub mod layout;
pub mod magic;
pub mod navigation;
pub mod net;
pub mod peers;
pub mod projectile;
pub mod services;
pub mod social;
pub mod status;
pub mod storage;
mod table;
pub mod trade;
pub mod types;
pub mod ui;
#[cfg(feature = "ui-host")]
pub mod ui_host;
pub mod verbs;
pub mod version;
pub mod wire;
pub mod world;

pub use character::CharacterInfo;
pub use imports::{Call, Import, MODULE, PERMISSIONS_VERSION, Permission, calls, exports};
pub use layout::{LAYOUT_FINGERPRINT, LAYOUT_SECTION};
pub use types::{
    ChatLine, ChatLinkClicked, ChatPost, CommandCall, CommandCompletion, CommandDef, Event,
    LogLevel, LogRequest, StartInfo,
};
pub use version::{
    API_CURRENT, API_MINIMUM, API_RANGE, API_SECTION, ApiRange, ApiVersion, PLUGIN_API_FILE,
    is_supported,
};
pub use wire::{DecodeError, Reader, Wire, Writer};

/// The fixed tick a plugin is driven at, in seconds: every tick carries
/// exactly this much time, with or without a window.
pub const TICK_SECONDS: f64 = 0.015;

/// Carries out one call on the serving side: decodes the request as
/// `C::Request`, hands it to `serve`, encodes the reply.
///
/// # Errors
/// The request does not decode.
pub fn serve<C: Call>(
    request: &[u8],
    serve: impl FnOnce(C::Request) -> C::Reply,
) -> Result<Vec<u8>, DecodeError> {
    let request = C::Request::from_bytes(request)?;
    Ok(serve(request).to_bytes())
}

/// [`serve`] for a plugin built for API `version`: the request is read and
/// the reply written as that version knows them ([`Wire::from_bytes_at`],
/// [`Wire::to_bytes_at`]), so a record field a later version added is left
/// out of what an older plugin is sent.
///
/// # Errors
/// The request does not decode.
pub fn serve_at<C: Call>(
    request: &[u8],
    version: ApiVersion,
    serve: impl FnOnce(C::Request) -> C::Reply,
) -> Result<Vec<u8>, DecodeError> {
    let request = C::Request::from_bytes_at(request, version)?;
    Ok(serve(request).to_bytes_at(version))
}
