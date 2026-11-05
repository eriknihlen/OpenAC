//! Retail content for this client: the DAT directory the player chose in
//! first-run setup, remembered in `<data>/retail-content.json`.
//!
//! OpenAC's first run bakes a prepared package and records it in
//! `install.json`; this client reads the retail DATs directly and has no
//! bake tool, so its setup is the DAT directory alone. The record handed to the orchestrator names the
//! canonical package path, which the client does not read.

use std::path::{Path, PathBuf};

use ac_host_core::PathSet;
use ac_host_core::json::{self as host_json, ObjectReader};

use super::dat_locator::DatDirectoryLocator;
use super::record_store::{
    InstallRecordVerification, InstallRecordVerificationState, LauncherInstallRecord,
};
use crate::fsutil;
use crate::json::{self, ObjectBuilder, Out};

/// The record format version.
pub const RETAIL_CONTENT_VERSION: i32 = 1;

/// The file name under the data directory.
pub const RETAIL_CONTENT_FILE_NAME: &str = "retail-content.json";

/// Loads and saves the chosen DAT directory.
#[derive(Debug)]
pub struct RetailContentStore {
    data_directory: PathBuf,
    locator: DatDirectoryLocator,
}

impl RetailContentStore {
    /// The store for these paths, checking with the default locator.
    pub fn new(paths: &PathSet) -> Self {
        Self::with_locator(paths, DatDirectoryLocator::default())
    }

    /// The store with its locator replaced (tests).
    pub fn with_locator(paths: &PathSet, locator: DatDirectoryLocator) -> Self {
        Self {
            data_directory: fsutil::full_path(&paths.game_data_directory()),
            locator,
        }
    }

    /// The DAT checker.
    pub fn locator(&self) -> &DatDirectoryLocator {
        &self.locator
    }

    /// `<data>/retail-content.json`.
    pub fn record_path(&self) -> PathBuf {
        self.data_directory.join(RETAIL_CONTENT_FILE_NAME)
    }

    /// The record the orchestrator launches with for `dat_directory`.
    pub fn install_record(&self, dat_directory: &Path) -> LauncherInstallRecord {
        LauncherInstallRecord::new(
            dat_directory,
            self.data_directory.join("pak").join("acdream.pak"),
            "",
            0,
            0,
        )
    }

    /// Reads the record and checks the directory still holds the four DAT
    /// files.
    pub fn load_and_verify(&self) -> InstallRecordVerification {
        let path = self.record_path();
        if !path.exists() {
            return InstallRecordVerification {
                state: InstallRecordVerificationState::Missing,
                record: None,
                status: "Client content is not configured. Complete the first-run setup.".into(),
                required_content_work: None,
            };
        }
        let invalid = |status: String| InstallRecordVerification {
            state: InstallRecordVerificationState::Invalid,
            record: None,
            status,
            required_content_work: None,
        };
        let directory = match read_record(&path) {
            Ok(directory) => directory,
            Err(error) => return invalid(format!("The content record could not be read: {error}")),
        };
        let validation = self.locator.validate(Some(&directory));
        if !validation.is_valid {
            let missing = if validation.missing_file_names.is_empty() {
                String::new()
            } else {
                format!(" Missing: {}.", validation.missing_file_names.join(", "))
            };
            return invalid(format!(
                "The retail DAT directory is no longer usable: {}{missing}",
                validation.message
            ));
        }
        InstallRecordVerification {
            state: InstallRecordVerificationState::Verified,
            record: Some(self.install_record(&validation.directory)),
            status: format!(
                "Retail DAT files found in {}.",
                validation.directory.display()
            ),
            required_content_work: None,
        }
    }

    /// Checks `dat_directory` and saves it; the record to launch with.
    pub fn save(&self, dat_directory: &Path) -> Result<LauncherInstallRecord, String> {
        let validation = self.locator.validate(Some(dat_directory));
        if !validation.is_valid {
            return Err(validation.message);
        }
        let text = json::to_pretty(
            &ObjectBuilder::new()
                .member("version", Out::Int(i64::from(RETAIL_CONTENT_VERSION)))
                .member(
                    "datDirectory",
                    Out::str(validation.directory.to_string_lossy()),
                )
                .build(),
        );
        std::fs::create_dir_all(&self.data_directory)
            .and_then(|()| fsutil::write_via_tmp(&self.record_path(), text.as_bytes(), false))
            .map_err(|error| format!("The content record could not be saved: {error}"))?;
        Ok(self.install_record(&validation.directory))
    }
}

fn read_record(path: &Path) -> Result<PathBuf, String> {
    let bytes = std::fs::read(path).map_err(|error| error.to_string())?;
    let root = host_json::parse(&bytes).map_err(|error| error.to_string())?;
    let mut object = ObjectReader::new(&root).map_err(|error| error.to_string())?;
    let version = host_json::int32(object.required("version").map_err(|e| e.to_string())?)
        .map_err(|error| error.to_string())?;
    if version != RETAIL_CONTENT_VERSION {
        return Err(format!("version {version} is not supported"));
    }
    let directory = host_json::string_or_null(
        object
            .required("datDirectory")
            .map_err(|error| error.to_string())?,
    )
    .map_err(|error| error.to_string())?
    .unwrap_or_default();
    object.finish().map_err(|error| error.to_string())?;
    if directory.trim().is_empty() || !Path::new(&directory).is_absolute() {
        return Err("the DAT directory must be an absolute path".into());
    }
    Ok(PathBuf::from(directory))
}
