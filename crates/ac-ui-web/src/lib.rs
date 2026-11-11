//! The client's panels as a module for a web page.
//!
//! The page (the host) pushes everything in by calling the module's
//! exports: the session's stream frames, input, the time, the canvas size,
//! and the dat files and stored settings the panels ask for. It pulls
//! everything out by taking the outputs of each frame: triangles and the
//! textures they use, the command frames to send to the session, requests
//! for dat files, settings to store. The module imports nothing: it never
//! touches a network, a clock, storage or a GPU.
//!
//! [`output`] is what a frame hands back and how it is laid out in bytes;
//! [`host`] is the module behind the exports, with the panels behind
//! [`host::Panels`]. Both are plain Rust and tested natively; the exports
//! themselves are a thin layer built only for the web.

#[cfg(target_arch = "wasm32")]
mod export;
pub mod host;
pub mod output;

/// The number of the interface this module speaks.
pub const INTERFACE_VERSION: u32 = 1;
