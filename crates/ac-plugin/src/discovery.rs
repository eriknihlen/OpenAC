//! Finding plugins: every folder directly under a plugin root that holds a
//! `plugin.json` is one plugin.

use std::path::{Path, PathBuf};

use crate::manifest::PluginManifest;

/// The manifest file a plugin folder holds.
pub const MANIFEST_FILE: &str = "plugin.json";

/// One plugin folder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Discovered {
    /// The folder.
    pub directory: PathBuf,
    /// Its manifest, or why it could not be read.
    pub manifest: Result<PluginManifest, String>,
}

impl Discovered {
    /// The folder's own name (what an allow-list names when the manifest is
    /// unreadable).
    #[must_use]
    pub fn directory_name(&self) -> String {
        self.directory
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_default()
    }
}

/// The plugin folders under `root`, in ordinal order of their paths. A root
/// that does not exist holds none; a folder without a manifest is skipped;
/// an unreadable or invalid manifest is reported and the scan goes on.
///
/// # Errors
/// The root exists but cannot be listed.
pub fn scan(root: &Path) -> std::io::Result<Vec<Discovered>> {
    if !root.is_dir() {
        return Ok(Vec::new());
    }
    let mut folders: Vec<PathBuf> = std::fs::read_dir(root)?
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.is_dir())
        .collect();
    folders.sort();
    Ok(folders
        .into_iter()
        .filter_map(|directory| {
            let manifest_path = directory.join(MANIFEST_FILE);
            if !manifest_path.is_file() {
                return None;
            }
            let manifest = std::fs::read_to_string(&manifest_path)
                .map_err(|error| format!("{MANIFEST_FILE} could not be read: {error}"))
                .and_then(|text| PluginManifest::parse(&text).map_err(|e| e.0));
            Some(Discovered {
                directory,
                manifest,
            })
        })
        .collect())
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Core.Tests/Plugins/PluginDiscoveryTests.cs`.

    use super::*;

    const VALID: &str = r#"{
      "id": "acdream.example",
      "displayName": "Example",
      "version": "0.1.0",
      "entry": "example.wasm",
      "apiVersion": "1.0"
    }"#;

    struct TempDir(PathBuf);

    impl TempDir {
        fn new(tag: &str) -> Self {
            let path = std::env::temp_dir()
                .join(format!("ac-plugin-discovery-{tag}-{}", std::process::id()));
            let _ = std::fs::remove_dir_all(&path);
            std::fs::create_dir_all(&path).unwrap();
            Self(path)
        }

        fn manifest(&self, folder: &str, text: &str) -> PathBuf {
            let dir = self.0.join(folder);
            std::fs::create_dir_all(&dir).unwrap();
            std::fs::write(dir.join(MANIFEST_FILE), text).unwrap();
            dir
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn an_empty_root_holds_nothing() {
        let temp = TempDir::new("empty");
        assert!(scan(&temp.0).unwrap().is_empty());
    }

    #[test]
    fn a_missing_root_holds_nothing() {
        let temp = TempDir::new("missing");
        assert!(scan(&temp.0.join("does-not-exist")).unwrap().is_empty());
    }

    #[test]
    fn one_valid_plugin_is_found() {
        let temp = TempDir::new("one");
        let dir = temp.manifest("example", VALID);
        let found = scan(&temp.0).unwrap();
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].directory, dir);
        assert_eq!(found[0].manifest.as_ref().unwrap().id, "acdream.example");
    }

    #[test]
    fn a_folder_without_a_manifest_is_skipped() {
        let temp = TempDir::new("skip");
        std::fs::create_dir_all(temp.0.join("noplugin")).unwrap();
        temp.manifest("real", VALID);
        assert_eq!(scan(&temp.0).unwrap().len(), 1);
    }

    #[test]
    fn an_invalid_manifest_is_reported_and_the_scan_goes_on() {
        let temp = TempDir::new("invalid");
        temp.manifest("broken", "{ not json");
        temp.manifest("ok", VALID);
        let found = scan(&temp.0).unwrap();
        assert_eq!(found.len(), 2);
        assert!(found[0].manifest.is_err());
        assert_eq!(found[0].directory_name(), "broken");
        assert!(found[1].manifest.is_ok());
    }
}
