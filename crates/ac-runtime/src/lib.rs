//! The one owner of game and session state. Headless and graphical hosts
//! drive the same runtime; a host never keeps its own copy of this state.
//!
//! Without the `host` feature only the view and command types and the pure
//! logic a window draws from are built (no session state, runner, network
//! or plugins), so a window can be built for the browser.

pub mod character;
pub mod communication;
pub mod frame_clock;
pub mod gameplay;
// Without the session state that drives them, the owners' crate-internal
// driving halves below have no caller; the `host` build still lints them.
#[cfg_attr(not(feature = "host"), allow(dead_code))]
pub mod inventory;
/// The player's local time zone (in `ac-host`, which the launcher shares).
pub use ac_host::local_time;
#[cfg_attr(not(feature = "host"), allow(dead_code))]
pub mod movement;
#[cfg_attr(not(feature = "host"), allow(dead_code, unused_imports))]
pub mod navigation;
// Plugins run through the plugin engine, which comes with the runner.
#[cfg(feature = "host")]
pub mod plugins;
pub mod session;
pub mod world;
