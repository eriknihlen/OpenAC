//! The plugin API version: `MAJOR.MINOR`. A minor version only adds (new
//! imports, new event kinds); a major version may break.

use std::fmt;

/// An API version, `MAJOR.MINOR`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct ApiVersion {
    /// Breaking changes.
    pub major: u16,
    /// Additions.
    pub minor: u16,
}

impl ApiVersion {
    /// A version.
    #[must_use]
    pub const fn new(major: u16, minor: u16) -> Self {
        Self { major, minor }
    }

    /// Reads `MAJOR.MINOR`: two decimal numbers, no sign, no leading zeros
    /// (as the manifest's host versions), nothing else.
    #[must_use]
    pub fn parse(text: &str) -> Option<Self> {
        let (major, minor) = text.split_once('.')?;
        Some(Self::new(component(major)?, component(minor)?))
    }

    /// Whether this version is `other` or older (`<=`, usable in a
    /// constant).
    #[must_use]
    pub const fn is_at_most(self, other: Self) -> bool {
        self.major < other.major || (self.major == other.major && self.minor <= other.minor)
    }
}

fn component(text: &str) -> Option<u16> {
    if text.is_empty()
        || !text.bytes().all(|b| b.is_ascii_digit())
        || (text.len() > 1 && text.starts_with('0'))
    {
        return None;
    }
    text.parse().ok()
}

impl fmt::Display for ApiVersion {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}.{}", self.major, self.minor)
    }
}

/// The plugin API versions a client loads: from `minimum` to `current`,
/// both included. Which range a client has belongs to that client (its
/// `ac-plugin-abi`); the launcher learns it from the installed client's
/// [`PLUGIN_API_FILE`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ApiRange {
    /// The oldest version it still loads.
    pub minimum: ApiVersion,
    /// The newest version it provides.
    pub current: ApiVersion,
}

impl ApiRange {
    /// A range from `minimum` to `current`.
    #[must_use]
    pub const fn new(minimum: ApiVersion, current: ApiVersion) -> Self {
        Self { minimum, current }
    }

    /// Whether a plugin built for `version` loads in this range.
    #[must_use]
    pub const fn contains(self, version: ApiVersion) -> bool {
        self.minimum.is_at_most(version) && version.is_at_most(self.current)
    }

    /// The text of [`PLUGIN_API_FILE`] for this range, one line of JSON:
    /// `{"schemaVersion":1,"minimum":"1.0","current":"1.11"}`. Schema
    /// version 1 may gain members; a reader ignores members it does not
    /// know.
    #[must_use]
    pub fn to_file_text(self) -> String {
        format!(
            "{{\"schemaVersion\":{PLUGIN_API_SCHEMA_VERSION},\"minimum\":\"{}\",\"current\":\"{}\"}}\n",
            self.minimum, self.current
        )
    }
}

impl fmt::Display for ApiRange {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{} to {}", self.minimum, self.current)
    }
}

/// The file at the root of a released client's folder, beside the `openac`
/// program, that says which plugin API versions that client loads (as
/// [`ApiRange::to_file_text`] writes it). `openac plugin-api` prints it and
/// the release packages that output, so it is the program's own answer.
pub const PLUGIN_API_FILE: &str = "plugin-api.json";

/// The schema version of [`PLUGIN_API_FILE`].
pub const PLUGIN_API_SCHEMA_VERSION: u32 = 1;

/// The custom section the SDK writes into every plugin's `.wasm`: the API
/// version it was built for, as UTF-8 `MAJOR.MINOR`.
pub const API_SECTION: &str = "openac.api";

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_reads_major_dot_minor_only() {
        assert_eq!(ApiVersion::parse("1.0"), Some(ApiVersion::new(1, 0)));
        assert_eq!(ApiVersion::parse("2.13"), Some(ApiVersion::new(2, 13)));
        for bad in ["1", "1.0.0", "01.0", "1.00", "v1.0", "-1.0", "1.", ".1", ""] {
            assert_eq!(ApiVersion::parse(bad), None, "{bad}");
        }
    }

    #[test]
    fn a_range_holds_its_ends_and_what_lies_between() {
        let v = ApiVersion::new;
        let range = ApiRange::new(v(1, 0), v(1, 11));
        for inside in [v(1, 0), v(1, 9), v(1, 10), v(1, 11)] {
            assert!(range.contains(inside), "{inside}");
        }
        for outside in [v(0, 9), v(1, 12), v(2, 0)] {
            assert!(!range.contains(outside), "{outside}");
        }
        assert_eq!(range.to_string(), "1.0 to 1.11");
    }

    #[test]
    fn the_file_text_is_one_line_of_json() {
        let range = ApiRange::new(ApiVersion::new(1, 0), ApiVersion::new(1, 11));
        assert_eq!(
            range.to_file_text(),
            "{\"schemaVersion\":1,\"minimum\":\"1.0\",\"current\":\"1.11\"}\n"
        );
    }

    #[test]
    fn is_at_most_orders_major_then_minor() {
        let v = ApiVersion::new;
        assert!(v(1, 0).is_at_most(v(1, 0)));
        assert!(v(1, 0).is_at_most(v(1, 1)));
        assert!(!v(1, 1).is_at_most(v(1, 0)));
        assert!(v(1, 9).is_at_most(v(2, 0)));
        // By number, not by text: 1.10 comes after 1.9.
        assert!(v(1, 9).is_at_most(v(1, 10)));
        assert!(!v(1, 10).is_at_most(v(1, 9)));
        assert_eq!(ApiVersion::parse("1.10"), Some(v(1, 10)));
        assert!(!v(2, 0).is_at_most(v(1, 9)));
        for (a, b) in [(v(1, 0), v(1, 1)), (v(1, 1), v(1, 0)), (v(2, 3), v(2, 3))] {
            assert_eq!(a.is_at_most(b), a <= b);
        }
    }
}
