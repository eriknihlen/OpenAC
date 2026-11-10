//! The plugin API's version type and permission vocabulary: the parts of
//! the calling convention a `plugin.json` names, and the file a client
//! carries to say which API versions it loads.
//!
//! `ac-plugin-abi` re-exports everything here under its old names; the
//! launcher links this crate alone to check a manifest, so a change to the
//! rest of the calling convention leaves the launcher as it is. Which API
//! versions a client loads (`API_MINIMUM` to `API_CURRENT`) is not here but
//! in `ac-plugin-abi`, the client's side: a new API version changes the
//! client alone, and the launcher reads the installed client's range from
//! its [`PLUGIN_API_FILE`]. Nothing here changes when the API gains a
//! version.

pub mod permission;
pub mod version;

pub use permission::{PERMISSIONS_VERSION, Permission};
pub use version::{API_SECTION, ApiRange, ApiVersion, PLUGIN_API_FILE, PLUGIN_API_SCHEMA_VERSION};
