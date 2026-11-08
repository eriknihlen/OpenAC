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

/// The API version this build of the ABI provides.
pub const API_CURRENT: ApiVersion = ApiVersion::new(1, 10);

/// The oldest API version a client built on this ABI still loads.
pub const API_MINIMUM: ApiVersion = ApiVersion::new(1, 0);

/// Whether a plugin built for `version` can load here: from [`API_MINIMUM`]
/// to [`API_CURRENT`], both included.
#[must_use]
pub fn is_supported(version: ApiVersion) -> bool {
    version >= API_MINIMUM && version <= API_CURRENT
}

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
    fn support_is_the_closed_range_minimum_to_current() {
        assert!(is_supported(API_CURRENT));
        assert!(is_supported(API_MINIMUM));
        assert!(!is_supported(ApiVersion::new(
            API_CURRENT.major,
            API_CURRENT.minor + 1
        )));
        assert!(!is_supported(ApiVersion::new(0, 9)));
        assert_eq!(API_CURRENT.to_string(), "1.10");
        assert_eq!(API_MINIMUM.to_string(), "1.0");
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
