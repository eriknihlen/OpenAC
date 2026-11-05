//! Whether checked clients are kept running, and the delay before one is
//! relaunched, remembered across restarts (`launcher-relaunch.json` in the
//! settings folder).

use std::path::{Path, PathBuf};

use ac_host::PathSet;
use ac_host::json::{self as host_json, ObjectReader};

use crate::fsutil;
use crate::json::{self, ObjectBuilder, Out};
use crate::orchestration::{
    DEFAULT_RELAUNCH_DELAY_SECONDS, MAXIMUM_RELAUNCH_DELAY_SECONDS, MINIMUM_RELAUNCH_DELAY_SECONDS,
};

/// The file's name in the settings folder.
pub const RELAUNCH_SETTINGS_FILE_NAME: &str = "launcher-relaunch.json";

/// The remembered relaunch settings.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LauncherRelaunchSettings {
    /// Checked clients are kept running.
    pub enabled: bool,
    /// The delay before a relaunch, in seconds (5 to 3600).
    pub delay_seconds: u32,
}

impl Default for LauncherRelaunchSettings {
    fn default() -> Self {
        Self {
            enabled: false,
            delay_seconds: DEFAULT_RELAUNCH_DELAY_SECONDS,
        }
    }
}

/// Why the settings could not be read or written.
#[derive(Debug, thiserror::Error)]
#[error("{0}")]
pub struct RelaunchSettingsError(pub String);

/// Reads and writes the relaunch settings file.
#[derive(Debug, Clone)]
pub struct LauncherRelaunchSettingsStore {
    file_path: PathBuf,
}

impl LauncherRelaunchSettingsStore {
    /// A store for `file_path`.
    pub fn new(file_path: &Path) -> Self {
        Self {
            file_path: fsutil::full_path(file_path),
        }
    }

    /// The store in the settings folder.
    pub fn for_paths(paths: &PathSet) -> Self {
        Self::new(&paths.config_directory.join(RELAUNCH_SETTINGS_FILE_NAME))
    }

    /// The file.
    pub fn file_path(&self) -> &Path {
        &self.file_path
    }

    /// Reads the file; a missing file is the defaults (off, 30 seconds). A
    /// file that is not an object with `enabled` and an in-range
    /// `delaySeconds` is an error and is left as it is.
    pub fn load(&self) -> Result<LauncherRelaunchSettings, RelaunchSettingsError> {
        let bytes = match std::fs::read(&self.file_path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(LauncherRelaunchSettings::default());
            }
            Err(error) => return Err(RelaunchSettingsError(error.to_string())),
        };
        let invalid = |detail: String| {
            RelaunchSettingsError(format!("The relaunch settings file is invalid: {detail}"))
        };
        let root = host_json::parse(&bytes).map_err(|error| invalid(error.to_string()))?;
        let mut object = ObjectReader::new(&root).map_err(|error| invalid(error.to_string()))?;
        let enabled = object
            .take("enabled")
            .map(host_json::boolean)
            .transpose()
            .map_err(|error| invalid(error.to_string()))?;
        let delay = object
            .take("delaySeconds")
            .map(host_json::int32)
            .transpose()
            .map_err(|error| invalid(error.to_string()))?;
        object
            .finish()
            .map_err(|error| invalid(error.to_string()))?;
        let (Some(enabled), Some(delay)) = (enabled, delay) else {
            return Err(invalid("it needs enabled and delaySeconds.".into()));
        };
        let delay_seconds = u32::try_from(delay)
            .ok()
            .filter(|seconds| {
                (MINIMUM_RELAUNCH_DELAY_SECONDS..=MAXIMUM_RELAUNCH_DELAY_SECONDS).contains(seconds)
            })
            .ok_or_else(|| invalid("delaySeconds must be from 5 to 3600.".into()))?;
        Ok(LauncherRelaunchSettings {
            enabled,
            delay_seconds,
        })
    }

    /// Writes `settings`.
    pub fn save(&self, settings: LauncherRelaunchSettings) -> Result<(), RelaunchSettingsError> {
        let document = ObjectBuilder::new()
            .member("enabled", Out::Bool(settings.enabled))
            .member("delaySeconds", Out::Int(i64::from(settings.delay_seconds)))
            .build();
        fsutil::write_via_tmp(
            &self.file_path,
            json::to_pretty(&document).as_bytes(),
            false,
        )
        .map_err(|error| RelaunchSettingsError(error.to_string()))
    }
}
