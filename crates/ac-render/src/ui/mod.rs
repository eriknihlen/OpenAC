//! The retail UI in the game window. The panels, their feeding from the
//! session, input routing and the triangles are `ac_ui_window`'s (re-exported
//! here under their old paths); this window draws the triangles over the
//! world ([`gpu`]), names the platform's keys ([`keys`]) and cursors
//! ([`cursor`]), and hands the UI the system clipboard ([`clipboard`]), the
//! settings file ([`stores`]) and the operating system's key names.
//!
//! Everything here belongs to one attached window: built on attach,
//! dropped on detach.

pub use ac_ui_window::ui::{
    barber_window, batch, book_window, character_pages, chat_windows, container_window, facts,
    icons, indicator_panels, input, keyboard_window, map_window, mount, options, overlays,
    plugin_ui, pregame, salvage_window, status, textures, viewports, window,
};

pub mod clipboard;
pub mod cursor;
pub mod gpu;
pub mod keys;
pub mod stores;

pub use batch::UiBatch;
pub use gpu::{UiPixels, UiRenderer, UiTextureSource};
pub use stores::SettingsStores;
pub use window::WindowUi;
