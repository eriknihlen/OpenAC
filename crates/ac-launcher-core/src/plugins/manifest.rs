//! The launcher's rules over a plugin's `plugin.json`.
//!
//! The file is read by the client's own parser (`ac_plugin_manifest`): one
//! reader, so the launcher accepts exactly what a session loads. A manifest
//! naming `entryDll` was built for OpenAC's C# host and that parser refuses
//! it ("built for OpenAC (C#)"). This module adds what a published plugin
//! must also meet (OpenAC's install rules) and the release tag that names a
//! WebAssembly build (`wasm-v<version>`, or `wasm-<plugin id>-v<version>`
//! in a repository that publishes several plugins).

pub use ac_plugin_manifest::HostVersion;
pub use ac_plugin_manifest::manifest::{ManifestError, PermissionDeclaration, PluginManifest};
pub use ac_plugin_version::{ApiVersion, PERMISSIONS_VERSION, Permission};

use crate::updates::{LauncherVersion, is_windows_device_name};

/// How a WebAssembly release tag of a repository's one plugin begins:
/// `wasm-v<version>` (one publishing several uses [`plugin_tag_prefix`]).
/// OpenAC's C# launcher reads only `v<version>` tags, so it never picks one
/// of these.
pub const WASM_TAG_PREFIX: &str = "wasm-v";

/// The release tag of a WebAssembly build of `version`.
pub fn release_tag(version: &str) -> String {
    format!("{WASM_TAG_PREFIX}{version}")
}

/// How a release tag of one plugin in a repository that publishes several
/// begins: `wasm-<plugin id>-v`.
pub fn plugin_tag_prefix(id: &str) -> String {
    format!("wasm-{id}-v")
}

/// The release tag of a WebAssembly build of plugin `id` at `version`, for
/// a repository that publishes several plugins.
pub fn plugin_release_tag(id: &str, version: &str) -> String {
    format!("{}{version}", plugin_tag_prefix(id))
}

/// Whether the parser refused a manifest because its permission vocabulary
/// is newer than the one this launcher was built with (so a newer launcher
/// would read it).
pub fn is_newer_permission_vocabulary(error: &ManifestError) -> bool {
    error.0.starts_with("permissionsVersion ")
        && error.0.ends_with(&format!(
            "is newer than this client's vocabulary ({PERMISSIONS_VERSION})"
        ))
}

/// Whether the parser refused a manifest written for OpenAC's C# host.
pub fn is_built_for_csharp(error: &ManifestError) -> bool {
    error.0.starts_with("built for OpenAC (C#)")
}

/// `[a-z0-9][a-z0-9-]*` segments joined by dots, at least two of them.
fn is_namespaced_id(id: &str) -> bool {
    let segments: Vec<&str> = id.split('.').collect();
    segments.len() >= 2
        && segments.iter().all(|segment| {
            let mut bytes = segment.bytes();
            bytes
                .next()
                .is_some_and(|first| first.is_ascii_lowercase() || first.is_ascii_digit())
                && bytes
                    .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
        })
}

/// Whether `id` may name a storage path (the install id rules).
pub fn has_valid_install_id(id: &str) -> bool {
    is_namespaced_id(id) && !is_windows_device_name(id)
}

/// Whether `id` has the namespaced shape.
pub fn is_well_formed_id(id: &str) -> bool {
    is_namespaced_id(id)
}

fn invalid(message: impl Into<String>) -> ManifestError {
    ManifestError(message.into())
}

/// The rules a manifest the launcher installs must meet beyond parsing.
pub trait InstallRules {
    /// A namespaced id that is not a Windows device name, a strict SemVer
    /// version, a declared minimum client version and a permission
    /// vocabulary this build knows. (The parser already enforced the
    /// permission vocabulary's entries and the note rules.) Whether the
    /// `apiVersion` loads is the installed client's to say, so it is judged
    /// with the client versions (`version_only_reason`), not here.
    fn validate_for_install(&self) -> Result<(), ManifestError>;

    /// Whether a release tag names this manifest's version, as
    /// `wasm-v<version>` or `wasm-<id>-v<version>`.
    fn matches_tag(&self, tag: &str) -> bool;
}

impl InstallRules for PluginManifest {
    fn validate_for_install(&self) -> Result<(), ManifestError> {
        if !is_namespaced_id(&self.id) {
            return Err(invalid(format!(
                "id '{}' does not match the required namespaced pattern",
                self.id
            )));
        }
        if is_windows_device_name(&self.id) {
            return Err(invalid(format!(
                "id '{}' starts with a name Windows reserves for a device",
                self.id
            )));
        }
        if LauncherVersion::try_parse(&self.version).is_none() {
            return Err(invalid(format!(
                "version '{}' is not a valid SemVer 2.0 version",
                self.version
            )));
        }
        if self.min_host_version.is_none() {
            return Err(invalid("minHostVersion is required for launcher installs"));
        }
        // The parser refuses a newer vocabulary only when entries use it; an install
        // refuses the declared version itself, as OpenAC refuses capabilitiesVersion.
        if self.permissions_version > PERMISSIONS_VERSION {
            return Err(invalid(format!(
                "permissionsVersion {} is newer than this client's vocabulary \
                 ({PERMISSIONS_VERSION})",
                self.permissions_version
            )));
        }
        Ok(())
    }

    fn matches_tag(&self, tag: &str) -> bool {
        tag == release_tag(&self.version) || tag == plugin_release_tag(&self.id, &self.version)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_vocabulary_refusal_is_recognized_from_the_parser_itself() {
        let text = r#"{ "id": "a.b", "displayName": "X", "version": "1.0.0", "entry": "x.wasm",
            "apiVersion": "1.0", "permissionsVersion": 2,
            "permissions": [{ "name": "chat", "note": "n" }] }"#;
        let error = PluginManifest::parse(text).unwrap_err();
        assert!(is_newer_permission_vocabulary(&error), "{error}");
        assert!(!is_built_for_csharp(&error));
    }

    #[test]
    fn the_csharp_refusal_is_recognized_from_the_parser_itself() {
        let text = r#"{ "id": "a.b", "displayName": "X", "version": "1.0.0",
            "entryDll": "x.dll", "apiVersion": 1 }"#;
        let error = PluginManifest::parse(text).unwrap_err();
        assert!(is_built_for_csharp(&error), "{error}");
        assert!(!is_newer_permission_vocabulary(&error));
    }
}
