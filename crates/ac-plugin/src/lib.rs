//! The plugin host: WebAssembly plugins living inside a game session.
//!
//! A session owns one [`PluginSession`]. It finds plugins in the plugin
//! roots (one folder each, `plugin.json` at its root, [`manifest`]), loads
//! the session's allowed ones in order (`plugins` in the session document),
//! links each one only the client functions its permissions allow, starts
//! it, then gives it events and a fixed 15 ms tick ([`tick`]) on the
//! session's own thread. A call may take as long as it needs; a slow one
//! is reported ([`watchdog`]). A plugin that traps (a panic, a call stuck
//! past its deadline, a bad memory access, its memory cap) is unloaded, its
//! registrations are released, and the failure is reported;
//! `/plugin reload <id>` loads it again. At the end every plugin is
//! stopped, the last loaded first.
//!
//! Every client function a plugin can call is implemented once, by the
//! runtime, through [`SessionApi`]: a plugin behaves the same with or
//! without a window. The calling convention is `ac-plugin-abi`; the plugin
//! side is the `openac-plugin` SDK.

pub mod api;
pub mod classifiers;
pub mod commands;
pub mod discovery;
pub mod engine;
pub mod file_image;
#[cfg(feature = "test-fixtures")]
pub mod fixtures;
mod host_calls;
pub mod icon;
pub mod image_codec;
mod instance;
pub mod interceptors;
pub mod manifest;
pub mod reload;
pub mod services;
pub mod session;
pub mod settings;
pub mod status;
pub mod storage;
pub mod tick;
pub mod version;
pub mod watchdog;

pub use api::{Caller, PluginReport, SessionApi};
pub use engine::{ModuleCache, PluginEngine, PluginModule};
pub use manifest::PluginManifest;
pub use session::{PluginSession, PluginSessionOptions};
pub use settings::PluginSessionSettings;
pub use version::HostVersion;
pub use watchdog::{CALL_DEADLINE, SLOW_CALL, START_DEADLINE, TimeLimits};

pub use ac_plugin_abi as abi;
