//! The UI's 3-D viewports: small private scenes the renderer draws into
//! textures the UI shows (`ac_ui::widgets::Viewport::texture`).
//!
//! Five exist, one texture each:
//!
//! - the **inventory figure** (paperdoll): the player as the world shows
//!   him (his look and what he wears), holding the heritage's pose, seen
//!   from an eye fixed per heritage;
//! - the **examine portrait**: a copy of an examined creature or character
//!   in its current pose, with what it holds, the camera fitted to its box;
//! - the **character-creation previews** of the appearance and summary
//!   pages: the look being built, idling (or resting while zoomed in), in
//!   front of the heritage's scenery, turned by the page's buttons;
//! - the **barber preview**: the look the barber builds, at rest, from the
//!   barber's own eye, turned by its two buttons.
//!
//! The scenes reuse the world renderer's models, textures and appearance
//! composition (`crate::renderer`): a figure is the parts the world would
//! draw, posed here and drawn by the world's mesh pipelines into a texture
//! with its own camera and light. This module holds the pure rules (the
//! cameras, the poses, the creation preview's controls, the copies'
//! placement) and the GPU targets. What the UI asks for (the slots, their
//! subjects and outcomes) is the UI's, re-exported here.

pub mod camera;
pub mod chargen;
pub mod examine;
pub(crate) mod gpu;
pub mod pose;

pub use ac_ui_window::viewport::*;
