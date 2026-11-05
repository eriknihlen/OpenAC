//! The plugin API's version rules and permission vocabulary: the parts of
//! the calling convention a `plugin.json` names.
//!
//! `ac-plugin-abi` re-exports everything here under its old names; the
//! launcher links this crate alone to check a manifest, so a change to the
//! rest of the calling convention leaves the launcher as it is.

pub mod permission;
pub mod version;

pub use permission::{PERMISSIONS_VERSION, Permission};
pub use version::{API_CURRENT, API_MINIMUM, API_SECTION, ApiVersion, is_supported};
