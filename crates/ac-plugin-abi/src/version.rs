//! The plugin API version: `MAJOR.MINOR`. A minor version only adds (new
//! imports, new event kinds); a major version may break.
//!
//! The version type lives in `ac-plugin-version`, which the launcher links
//! without the rest of the calling convention; it is re-exported here. Which
//! versions this client loads is defined here, on the client's side only, so
//! a new API version changes no crate the launcher links: the launcher reads
//! the installed client's range from the `plugin-api.json` it ships with
//! (`openac plugin-api` prints [`API_RANGE`] as that file).

pub use ac_plugin_version::version::{
    API_SECTION, ApiRange, ApiVersion, PLUGIN_API_FILE, PLUGIN_API_SCHEMA_VERSION,
};

/// The API version this build of the ABI provides.
pub const API_CURRENT: ApiVersion = ApiVersion::new(1, 12);

/// The oldest API version a client built on this ABI still loads.
pub const API_MINIMUM: ApiVersion = ApiVersion::new(1, 0);

/// [`API_MINIMUM`] to [`API_CURRENT`]: what this client loads.
pub const API_RANGE: ApiRange = ApiRange::new(API_MINIMUM, API_CURRENT);

/// Whether a plugin built for `version` can load here: from [`API_MINIMUM`]
/// to [`API_CURRENT`], both included.
#[must_use]
pub fn is_supported(version: ApiVersion) -> bool {
    API_RANGE.contains(version)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn support_is_the_closed_range_minimum_to_current() {
        assert!(is_supported(API_CURRENT));
        assert!(is_supported(API_MINIMUM));
        assert!(!is_supported(ApiVersion::new(
            API_CURRENT.major,
            API_CURRENT.minor + 1
        )));
        assert!(!is_supported(ApiVersion::new(0, 9)));
        assert_eq!(API_CURRENT.to_string(), "1.12");
        assert_eq!(API_MINIMUM.to_string(), "1.0");
        assert_eq!(
            API_RANGE.to_file_text(),
            "{\"schemaVersion\":1,\"minimum\":\"1.0\",\"current\":\"1.12\"}\n"
        );
    }
}
