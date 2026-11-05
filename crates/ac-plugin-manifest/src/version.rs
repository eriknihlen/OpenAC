//! The client's own version as manifests name it, and the check of a
//! manifest's version fields against it.

use std::fmt;

use crate::manifest::PluginManifest;

/// The version core (`MAJOR.MINOR.PATCH`) a client build or a manifest
/// field names.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct HostVersion {
    /// Major.
    pub major: u32,
    /// Minor.
    pub minor: u32,
    /// Patch.
    pub patch: u32,
}

impl HostVersion {
    /// A version.
    #[must_use]
    pub const fn new(major: u32, minor: u32, patch: u32) -> Self {
        Self {
            major,
            minor,
            patch,
        }
    }

    /// Reads `MAJOR.MINOR.PATCH`: decimal digits only, no leading zeros,
    /// nothing else.
    #[must_use]
    pub fn parse(text: &str) -> Option<Self> {
        let mut parts = text.split('.');
        let version = Self::new(
            component(parts.next()?)?,
            component(parts.next()?)?,
            component(parts.next()?)?,
        );
        parts.next().is_none().then_some(version)
    }

    /// The version core of a full version (`0.1.7+3a71d75`,
    /// `0.2.0-beta.1`): pre-release and build metadata dropped.
    #[must_use]
    pub fn from_informational(version: &str) -> Option<Self> {
        let core = version
            .find(['-', '+'])
            .map_or(version, |cut| &version[..cut]);
        Self::parse(core)
    }

    /// This build of the client (the workspace version).
    #[must_use]
    pub fn current() -> Self {
        Self::from_informational(env!("CARGO_PKG_VERSION")).unwrap_or(Self::new(0, 0, 0))
    }
}

fn component(text: &str) -> Option<u32> {
    if text.is_empty()
        || !text.bytes().all(|b| b.is_ascii_digit())
        || (text.len() > 1 && text.starts_with('0'))
    {
        return None;
    }
    text.parse().ok()
}

impl fmt::Display for HostVersion {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}.{}.{}", self.major, self.minor, self.patch)
    }
}

/// Why `manifest` cannot run on client `version`, or `None` when it can.
#[must_use]
pub fn incompatibility(manifest: &PluginManifest, version: HostVersion) -> Option<String> {
    if let Some(min) = manifest.min_host_version
        && version < min
    {
        return Some(format!(
            "requires openAC {min} or newer (this is {version})"
        ));
    }
    if let Some(max) = manifest.max_host_version
        && version > max
    {
        return Some(format!("supports openAC up to {max} (this is {version})"));
    }
    if manifest.skip_host_versions.contains(&version) {
        return Some(format!("is marked broken on openAC {version}"));
    }
    None
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Core.Tests/Plugins/PluginHostVersionTests.cs`.

    use super::*;

    #[test]
    fn from_informational_strips_build_and_prerelease() {
        for (text, expected) in [
            ("0.1.7+3a71d75", HostVersion::new(0, 1, 7)),
            ("0.2.0-beta.1+x", HostVersion::new(0, 2, 0)),
            ("1.0.0", HostVersion::new(1, 0, 0)),
        ] {
            assert_eq!(HostVersion::from_informational(text), Some(expected));
        }
    }

    #[test]
    fn from_informational_malformed_core_is_none() {
        for text in ["v1.0.0", "1.0", "01.0.0", "1.0.0.0", ""] {
            assert_eq!(HostVersion::from_informational(text), None, "{text}");
        }
    }

    #[test]
    fn the_current_version_is_the_workspace_version() {
        assert_eq!(
            HostVersion::current().to_string(),
            env!("CARGO_PKG_VERSION")
        );
    }
}
