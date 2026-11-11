//! The game window's UI without a GPU or a window.
//!
//! A window follows a session through its presentation stream: [`scene`]
//! keeps the window's own copy of what it shows, [`ui`] mounts the retail
//! panels and feeds them from it, routes input to them first and turns
//! each frame into triangles and the textures they need. Nothing here
//! draws, opens a window, reads the system clipboard or the keyboard of
//! the platform: the host does (the native window in `ac-render`, or a web
//! page), and the few things the panels need from it are handed in:
//!
//! - the world as the host last drew it ([`world::WorldFrame`]): where a
//!   cell's point lands in the drawing frame, the camera, a held object's
//!   pose, the land's height, which icon anchors the world hides;
//! - the clipboard ([`ui::clipboard::Clipboard`]);
//! - where window layouts and plugin control state are kept
//!   ([`ui::WindowUi::set_stores`]) and the player's key files
//!   (`ac_ui::storage::UiStorage`);
//! - the operating system's key names, where it has them
//!   ([`ui::WindowUi::set_os_key_names`]);
//! - the time, through `ac_clock`.
//!
//! What the host draws for the world on the panels' behalf (plugin lines,
//! triangles, ground shapes, icon probes, the 3-D viewports) is described
//! by the plain types of [`world`], [`viewport`] and [`appearance`].

pub mod appearance;
pub mod scene;
pub mod setup_pose;
pub mod ui;
pub mod viewport;
pub mod world;
pub mod world_click;
