//! Plugin icons: a PNG that passed the install rules (`validate_icon`),
//! decoded to 8-bit RGBA for the window. The rules and the decoder are the
//! client's (`ac_plugin_icon`), which the client's plugin dock
//! uses for the same file.

pub use ac_plugin_icon::{PluginIcon, decode_icon, decode_png};
