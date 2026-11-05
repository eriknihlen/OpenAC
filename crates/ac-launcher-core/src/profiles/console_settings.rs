//! Whether session consoles show every line the client writes or only what
//! a player would see, remembered across restarts (`launcher-console.json`
//! in the settings folder).

use std::path::{Path, PathBuf};

use ac_host_core::PathSet;
use ac_host_core::json::{self as host_json, ObjectReader};

use crate::fsutil;
use crate::json::{self, ObjectBuilder, Out};

/// The file's name in the settings folder.
pub const CONSOLE_SETTINGS_FILE_NAME: &str = "launcher-console.json";

/// The remembered console settings.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct LauncherConsoleSettings {
    /// Consoles show the client's log too, not only chat, the status box,
    /// typed lines and their answers.
    pub verbose: bool,
}

/// Why the settings could not be read or written.
#[derive(Debug, thiserror::Error)]
#[error("{0}")]
pub struct ConsoleSettingsError(pub String);

/// Reads and writes the console settings file.
#[derive(Debug, Clone)]
pub struct LauncherConsoleSettingsStore {
    file_path: PathBuf,
}

impl LauncherConsoleSettingsStore {
    /// A store for `file_path`.
    pub fn new(file_path: &Path) -> Self {
        Self {
            file_path: fsutil::full_path(file_path),
        }
    }

    /// The store in the settings folder.
    pub fn for_paths(paths: &PathSet) -> Self {
        Self::new(&paths.config_directory.join(CONSOLE_SETTINGS_FILE_NAME))
    }

    /// The file.
    pub fn file_path(&self) -> &Path {
        &self.file_path
    }

    /// Reads the file; a missing file is the default (not verbose). A file
    /// that is not an object with `verbose` is an error and is left as it is.
    pub fn load(&self) -> Result<LauncherConsoleSettings, ConsoleSettingsError> {
        let bytes = match std::fs::read(&self.file_path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(LauncherConsoleSettings::default());
            }
            Err(error) => return Err(ConsoleSettingsError(error.to_string())),
        };
        let invalid = |detail: String| {
            ConsoleSettingsError(format!("The console settings file is invalid: {detail}"))
        };
        let root = host_json::parse(&bytes).map_err(|error| invalid(error.to_string()))?;
        let mut object = ObjectReader::new(&root).map_err(|error| invalid(error.to_string()))?;
        let verbose = object
            .take("verbose")
            .map(host_json::boolean)
            .transpose()
            .map_err(|error| invalid(error.to_string()))?;
        object
            .finish()
            .map_err(|error| invalid(error.to_string()))?;
        let Some(verbose) = verbose else {
            return Err(invalid("it needs verbose.".into()));
        };
        Ok(LauncherConsoleSettings { verbose })
    }

    /// Writes `settings`.
    pub fn save(&self, settings: LauncherConsoleSettings) -> Result<(), ConsoleSettingsError> {
        let document = ObjectBuilder::new()
            .member("verbose", Out::Bool(settings.verbose))
            .build();
        fsutil::write_via_tmp(
            &self.file_path,
            json::to_pretty(&document).as_bytes(),
            false,
        )
        .map_err(|error| ConsoleSettingsError(error.to_string()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn store(name: &str) -> (PathBuf, LauncherConsoleSettingsStore) {
        let folder = std::env::temp_dir().join(format!(
            "openac-console-settings-{name}-{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&folder);
        std::fs::create_dir_all(&folder).unwrap();
        let store = LauncherConsoleSettingsStore::new(&folder.join(CONSOLE_SETTINGS_FILE_NAME));
        (folder, store)
    }

    #[test]
    fn a_missing_file_is_not_verbose() {
        let (folder, store) = store("missing");
        assert_eq!(store.load().unwrap(), LauncherConsoleSettings::default());
        assert!(!LauncherConsoleSettings::default().verbose);
        let _ = std::fs::remove_dir_all(folder);
    }

    #[test]
    fn verbose_is_remembered() {
        let (folder, store) = store("round-trip");
        for verbose in [true, false] {
            store.save(LauncherConsoleSettings { verbose }).unwrap();
            assert_eq!(store.load().unwrap(), LauncherConsoleSettings { verbose });
        }
        let _ = std::fs::remove_dir_all(folder);
    }

    #[test]
    fn a_file_that_cannot_be_read_says_so_and_is_left_alone() {
        let (folder, store) = store("invalid");
        std::fs::write(store.file_path(), b"{\"loud\": true}").unwrap();
        let error = store.load().unwrap_err();
        assert!(
            error.0.contains("console settings file is invalid"),
            "{error}"
        );
        assert_eq!(
            std::fs::read(store.file_path()).unwrap(),
            b"{\"loud\": true}"
        );
        let _ = std::fs::remove_dir_all(folder);
    }
}
