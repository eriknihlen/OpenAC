//! The plugin API version: `MAJOR.MINOR`. A minor version only adds (new
//! imports, new event kinds); a major version may break.
//!
//! The rules live in `ac-plugin-version`, which the launcher links without
//! the rest of the calling convention; they are re-exported here.

pub use ac_plugin_version::version::{
    API_CURRENT, API_MINIMUM, API_SECTION, ApiVersion, is_supported,
};
