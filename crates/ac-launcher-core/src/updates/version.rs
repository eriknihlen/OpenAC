//! Strict SemVer 2.0 versions, compared without numeric overflow.

use std::cmp::Ordering;
use std::fmt;
use std::hash::{Hash, Hasher};

/// A strict SemVer 2.0 version: `MAJOR.MINOR.PATCH`, an optional
/// pre-release and optional build metadata, at most 128 characters. Build
/// metadata does not take part in comparison.
#[derive(Debug, Clone)]
pub struct LauncherVersion {
    value: String,
    core: [String; 3],
    pre_release: Vec<String>,
}

fn is_digits(value: &str) -> bool {
    !value.is_empty() && value.bytes().all(|byte| byte.is_ascii_digit())
}

fn valid_core_number(value: &str) -> bool {
    is_digits(value) && (value.len() == 1 || !value.starts_with('0'))
}

fn valid_identifiers(value: &str, numeric_leading_zero_rule: bool) -> bool {
    value.split('.').all(|identifier| {
        !identifier.is_empty()
            && identifier
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
            && (!numeric_leading_zero_rule
                || !is_digits(identifier)
                || identifier.len() == 1
                || !identifier.starts_with('0'))
    })
}

fn compare_numeric(left: &str, right: &str) -> Ordering {
    left.len().cmp(&right.len()).then_with(|| left.cmp(right))
}

impl LauncherVersion {
    /// Reads a version; `None` when it is not strict SemVer 2.0.
    pub fn try_parse(value: &str) -> Option<Self> {
        if value.is_empty() || value.len() > 128 || value != value.trim() {
            return None;
        }
        let mut precedence = value;
        if let Some(plus) = value.find('+') {
            let build = &value[plus + 1..];
            if build.is_empty() || build.contains('+') || !valid_identifiers(build, false) {
                return None;
            }
            precedence = &value[..plus];
        }
        let mut core_text = precedence;
        let mut pre_release = Vec::new();
        if let Some(dash) = precedence.find('-') {
            let pre = &precedence[dash + 1..];
            if pre.is_empty() || !valid_identifiers(pre, true) {
                return None;
            }
            core_text = &precedence[..dash];
            pre_release = pre.split('.').map(str::to_owned).collect();
        }
        let parts: Vec<&str> = core_text.split('.').collect();
        if parts.len() != 3 || !parts.iter().all(|part| valid_core_number(part)) {
            return None;
        }
        Some(Self {
            value: value.to_owned(),
            core: [
                parts[0].to_owned(),
                parts[1].to_owned(),
                parts[2].to_owned(),
            ],
            pre_release,
        })
    }

    /// The text as written.
    pub fn value(&self) -> &str {
        &self.value
    }

    /// Whether it carries a pre-release part.
    pub fn is_pre_release(&self) -> bool {
        !self.pre_release.is_empty()
    }
}

impl Ord for LauncherVersion {
    fn cmp(&self, other: &Self) -> Ordering {
        for index in 0..3 {
            let ordering = compare_numeric(&self.core[index], &other.core[index]);
            if ordering != Ordering::Equal {
                return ordering;
            }
        }
        match (self.pre_release.is_empty(), other.pre_release.is_empty()) {
            (true, true) => return Ordering::Equal,
            (true, false) => return Ordering::Greater,
            (false, true) => return Ordering::Less,
            (false, false) => {}
        }
        for (left, right) in self.pre_release.iter().zip(&other.pre_release) {
            let ordering = match (is_digits(left), is_digits(right)) {
                (true, true) => compare_numeric(left, right),
                (true, false) => Ordering::Less,
                (false, true) => Ordering::Greater,
                (false, false) => left.as_str().cmp(right.as_str()),
            };
            if ordering != Ordering::Equal {
                return ordering;
            }
        }
        self.pre_release.len().cmp(&other.pre_release.len())
    }
}

impl PartialOrd for LauncherVersion {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl PartialEq for LauncherVersion {
    fn eq(&self, other: &Self) -> bool {
        self.cmp(other) == Ordering::Equal
    }
}

impl Eq for LauncherVersion {}

impl Hash for LauncherVersion {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.core.hash(state);
        self.pre_release.hash(state);
    }
}

impl fmt::Display for LauncherVersion {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.value)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Ported from OpenAC tests/.../Updates/ReleaseTransportTests.cs (LauncherVersionTests).

    fn parse(value: &str) -> LauncherVersion {
        LauncherVersion::try_parse(value).unwrap()
    }

    #[test]
    fn strict_sem_ver_orders_without_numeric_overflow() {
        for (lower, higher) in [
            ("1.0.0-alpha", "1.0.0-alpha.1"),
            ("1.0.0-alpha.1", "1.0.0-alpha.beta"),
            ("1.0.0-beta.11", "1.0.0-rc.1"),
            ("1.0.0-rc.1", "1.0.0"),
            ("1.9.999999999999999999999", "1.10.0"),
            ("999999999999999999999.0.0", "1000000000000000000000.0.0"),
        ] {
            let left = parse(lower);
            let right = parse(higher);
            assert!(left < right, "{lower} < {higher}");
            assert!(right > left);
            assert_eq!(
                parse(&format!("{higher}+build.7")).cmp(&right),
                Ordering::Equal
            );
        }
    }

    #[test]
    fn strict_sem_ver_rejects_ambiguous_versions() {
        for value in [
            "", " 1.0.0", "1.0", "01.0.0", "1.0.0-01", "1.0.0-", "1.0.0+", "v1.0.0",
        ] {
            assert!(LauncherVersion::try_parse(value).is_none(), "{value:?}");
        }
    }

    #[test]
    fn strict_sem_ver_is_bounded_against_manifest_path_amplification() {
        assert!(LauncherVersion::try_parse(&format!("1.0.0+{}", "a".repeat(129))).is_none());
    }

    #[test]
    fn pre_release_sorts_between_releases() {
        // As OpenAC documents it: 0.1.13-dev.1 sorts above 0.1.12 and below 0.1.13.
        assert!(parse("0.1.13-dev.1") > parse("0.1.12"));
        assert!(parse("0.1.13-dev.1") < parse("0.1.13"));
    }
}
