//! The retail UI in the game window: the panels of `ac-ui` mounted and fed
//! from the session ([`window`]), input routed UI first ([`input`]), their
//! draw lists turned into triangles ([`batch`]) and drawn over the world
//! ([`gpu`]), the system clipboard behind the text fields ([`clipboard`]).
//!
//! Everything here belongs to one attached window: built on attach,
//! dropped on detach.

pub mod batch;
pub mod book_window;
pub mod character_pages;
pub mod chat_windows;
pub mod clipboard;
mod combat_window;
mod components_page;
pub mod container_window;
mod cooldowns;
pub mod cursor;
pub mod facts;
pub mod gpu;
pub mod icons;
pub mod indicator_panels;
pub mod input;
mod journal_window;
pub mod keyboard_window;
pub mod keys;
pub mod map_window;
pub mod mount;
pub mod options;
pub mod overlays;
mod panel_keys;
mod panels;
pub mod plugin_ui;
pub mod pregame;
pub mod salvage_window;
mod social_panels;
pub mod status;
pub mod viewports;
pub mod window;

pub use batch::UiBatch;
pub use gpu::{UiPixels, UiRenderer, UiTextureSource};
pub use window::WindowUi;
