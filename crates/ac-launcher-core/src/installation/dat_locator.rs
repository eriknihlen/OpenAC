//! Finding the retail DAT directory and checking it holds the four files.

use std::path::{Path, PathBuf};

use crate::fsutil;

/// The four retail DAT files the client reads.
pub const REQUIRED_DAT_FILE_NAMES: [&str; 4] = [
    "client_portal.dat",
    "client_cell_1.dat",
    "client_highres.dat",
    "client_local_English.dat",
];

/// What checking a directory found.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DatDirectoryValidation {
    /// The directory, made absolute, no trailing separator.
    pub directory: PathBuf,
    /// Whether all four files are there.
    pub is_valid: bool,
    /// A line for the player.
    pub message: String,
    /// The files that are missing.
    pub missing_file_names: Vec<String>,
}

impl DatDirectoryValidation {
    fn valid(directory: PathBuf) -> Self {
        Self {
            directory,
            is_valid: true,
            message: "All four required retail DAT files were found.".into(),
            missing_file_names: Vec::new(),
        }
    }

    fn invalid(directory: PathBuf, message: &str, missing: Vec<String>) -> Self {
        Self {
            directory,
            is_valid: false,
            message: message.into(),
            missing_file_names: missing,
        }
    }
}

type Probe = Box<dyn Fn(&Path) -> bool + Send + Sync>;

/// Finds and checks DAT directories.
pub struct DatDirectoryLocator {
    is_windows: bool,
    windows_candidates: Vec<PathBuf>,
    directory_exists: Probe,
    file_exists: Probe,
}

impl std::fmt::Debug for DatDirectoryLocator {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DatDirectoryLocator")
            .field("is_windows", &self.is_windows)
            .field("windows_candidates", &self.windows_candidates)
            .finish_non_exhaustive()
    }
}

impl Default for DatDirectoryLocator {
    fn default() -> Self {
        Self::new(cfg!(windows), None)
    }
}

fn default_windows_candidates() -> Vec<PathBuf> {
    let mut candidates = Vec::new();
    if let Some(profile) = std::env::home_dir().filter(|path| !path.as_os_str().is_empty()) {
        candidates.push(profile.join("Documents").join("Asheron's Call"));
    }
    candidates.push(PathBuf::from(r"C:\Turbine\Asheron's Call"));
    candidates
}

impl DatDirectoryLocator {
    /// A locator. On Windows it looks in `Documents\Asheron's Call` and
    /// `C:\Turbine\Asheron's Call` (or `windows_candidates`, in order);
    /// elsewhere it detects nothing, and a chosen directory is still checked.
    pub fn new(is_windows: bool, windows_candidates: Option<Vec<PathBuf>>) -> Self {
        let mut seen = std::collections::HashSet::new();
        let windows_candidates = windows_candidates
            .unwrap_or_else(default_windows_candidates)
            .into_iter()
            .filter(|path| !path.as_os_str().is_empty())
            .map(|path| fsutil::full_path(&path))
            .filter(|path| seen.insert(path.to_string_lossy().to_lowercase()))
            .collect();
        Self {
            is_windows,
            windows_candidates,
            directory_exists: Box::new(Path::is_dir),
            file_exists: Box::new(Path::is_file),
        }
    }

    /// The candidates that exist, each checked, in order (Windows only).
    pub fn detect(&self) -> Vec<DatDirectoryValidation> {
        if !self.is_windows {
            return Vec::new();
        }
        self.windows_candidates
            .iter()
            .filter(|path| (self.directory_exists)(path))
            .map(|path| self.validate(Some(path)))
            .collect()
    }

    /// Checks a directory the player chose.
    pub fn validate(&self, directory: Option<&Path>) -> DatDirectoryValidation {
        let Some(directory) = directory.filter(|path| !path.to_string_lossy().trim().is_empty())
        else {
            return DatDirectoryValidation::invalid(
                directory.map(Path::to_path_buf).unwrap_or_default(),
                "Choose the folder containing the retail DAT files.",
                REQUIRED_DAT_FILE_NAMES
                    .iter()
                    .map(|&name| name.to_owned())
                    .collect(),
            );
        };
        let full = fsutil::full_path(directory);
        if !(self.directory_exists)(&full) {
            return DatDirectoryValidation::invalid(
                full,
                "The DAT directory does not exist.",
                REQUIRED_DAT_FILE_NAMES
                    .iter()
                    .map(|&name| name.to_owned())
                    .collect(),
            );
        }
        let missing: Vec<String> = REQUIRED_DAT_FILE_NAMES
            .iter()
            .filter(|name| !(self.file_exists)(&full.join(name)))
            .map(|&name| name.to_owned())
            .collect();
        if missing.is_empty() {
            DatDirectoryValidation::valid(full)
        } else {
            DatDirectoryValidation::invalid(
                full,
                "The selected directory is missing required retail DAT files.",
                missing,
            )
        }
    }
}
