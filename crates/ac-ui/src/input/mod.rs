//! Input: the game's actions, key bindings (the retail default key map, the
//! player's bindings file and retail `.keymap` files), the dispatcher that
//! turns key and button transitions into actions, and instant mouse look.
//!
//! Nothing here touches a window: the window feeds transitions in and
//! routes the actions that come out (movement and camera to the session
//! and the camera, panel toggles to the UI).

pub mod action;
pub mod action_map;
pub mod bindings;
pub mod chord;
pub mod dispatcher;
pub mod identity;
pub mod key_names;
pub mod keymap_file;
pub mod mouse_look;
pub mod scan_codes;
pub mod unmapped;

pub use action::InputAction;
pub use bindings::{KeyBindings, KeyBindingsError};
pub use chord::{ActivationType, Binding, InputKey, InputScope, KeyChord};
pub use dispatcher::{DispatchEvent, InputDevices, InputDispatcher};
pub use mouse_look::MouseLookState;
