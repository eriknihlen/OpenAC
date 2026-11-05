//! The retained retail UI, driven by the dat layouts (LayoutDesc).
//!
//! No GPU and no window: input arrives as plain data ([`UiRoot::on_mouse_down`],
//! [`UiRoot::on_key_down`], ...), and each frame produces a [`DrawList`] of
//! fills, textured quads, polygons and glyph runs that a renderer draws.
//!
//! - [`import`] reads a layout into element descriptions (base-element
//!   inheritance resolved) and builds widgets from them.
//! - [`UiRoot`] holds the element tree and routes input: hit testing,
//!   bubbling, keyboard focus, pointer capture, hover and tooltip timing,
//!   drag and drop, window move and resize, popups, z-order.
//! - [`widgets`] are the element behaviours of this slice; [`UiHost`] routes
//!   the root's outputs to panel controllers and tooltips.
//!
//! The UI holds only presentation state it can rebuild; game state lives in
//! the runtime, and panel controllers rebuild what they show from it.

pub mod automation;
pub mod bundled_font;
pub mod content;
pub mod controls_ini;
pub mod cursor;
pub mod draw;
pub mod element_info;
pub mod event;
pub mod font;
pub mod geometry;
pub mod host;
pub mod icons;
pub mod import;
pub mod input;
pub mod layout_persistence;
pub mod layout_policy;
pub mod media;
pub mod overlay;
pub mod panels;
pub mod plugin;
pub mod pregame;
pub mod radar;
pub mod root;
pub mod shapes;
pub mod states;
pub mod strings;
pub mod text;
pub mod tooltip;
pub mod tree;
pub mod widgets;
pub mod window_chrome;
pub mod windows;
pub mod world_drag;

pub use content::DatContent;
pub use draw::{Color, DrawCmd, DrawList, RenderContext, SpriteSizes, TextureRef};
pub use event::{DragPayload, ItemDragPayload, Key, Modifiers, MouseButton, UiEvent};
pub use font::UiFont;
pub use host::{PanelController, UiHost};
pub use root::{ElementMessage, UiOutput, UiRoot};
pub use tree::{Element, ElementId, Widget};
pub use windows::WindowEvent;
