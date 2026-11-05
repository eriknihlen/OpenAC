//! The install record (`<data>/install.json`), its verification, the
//! verification cache and the installer's transaction lock.

use std::fs::{File, OpenOptions, TryLockError};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use ac_host_core::PathSet;
use ac_host_core::json::{self as host_json, ObjectReader, Value};

use super::dat_locator::DatDirectoryLocator;
use super::migration::{ContentMigrationPlan, resolve_content_migration};
use crate::cancel::{CancelToken, Cancelled};
use crate::fsutil;
use crate::integrity;
use crate::json::{self, ObjectBuilder, Out};
use crate::updates::is_sha256;

/// The record format version.
pub const CURRENT_INSTALL_RECORD_VERSION: i32 = 1;

/// The prepared-content recipe this launcher expects (OpenAC's bake tool
/// version).
pub const CURRENT_BAKE_TOOL_VERSION: u32 = 10;

/// What the launcher installed: the DATs, the prepared package and its
/// integrity facts.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherInstallRecord {
    /// The retail DAT directory.
    pub dat_directory: PathBuf,
    /// The prepared package (`<data>/pak/acdream.pak`).
    pub prepared_asset_path: PathBuf,
    /// Its SHA-256 (64 hex digits).
    pub prepared_asset_sha256: String,
    /// Its size.
    pub prepared_asset_size: i64,
    /// The recipe it was prepared with.
    pub bake_tool_version: u32,
    /// The record format version.
    pub version: i32,
    /// A layered update package, when one applies (not stored).
    pub prepared_asset_overlay_path: Option<PathBuf>,
    /// The recipe the overlay brings the content to (not stored; 0 = none).
    pub effective_bake_tool_version: u32,
    /// The content was prepared for a newer client than the installed one
    /// (not stored); launching is refused until confirmed.
    pub requires_client_compatibility_confirmation: bool,
}

impl LauncherInstallRecord {
    /// A record of the current format with no overlay.
    pub fn new(
        dat_directory: impl Into<PathBuf>,
        prepared_asset_path: impl Into<PathBuf>,
        prepared_asset_sha256: impl Into<String>,
        prepared_asset_size: i64,
        bake_tool_version: u32,
    ) -> Self {
        Self {
            dat_directory: dat_directory.into(),
            prepared_asset_path: prepared_asset_path.into(),
            prepared_asset_sha256: prepared_asset_sha256.into(),
            prepared_asset_size,
            bake_tool_version,
            version: CURRENT_INSTALL_RECORD_VERSION,
            prepared_asset_overlay_path: None,
            effective_bake_tool_version: 0,
            requires_client_compatibility_confirmation: false,
        }
    }

    /// SHA-256, size and recipe are all present.
    pub fn has_integrity_metadata(&self) -> bool {
        self.prepared_asset_sha256.len() == 64
            && self.prepared_asset_size > 0
            && self.bake_tool_version > 0
    }

    /// The recipe the content is at (the overlay's when there is one).
    pub fn resolved_bake_tool_version(&self) -> u32 {
        if self.effective_bake_tool_version == 0 {
            self.bake_tool_version
        } else {
            self.effective_bake_tool_version
        }
    }

    /// The record as stored.
    pub fn to_json(&self) -> String {
        json::to_pretty(
            &ObjectBuilder::new()
                .member(
                    "datDirectory",
                    Out::str(self.dat_directory.to_string_lossy()),
                )
                .member(
                    "preparedAssetPath",
                    Out::str(self.prepared_asset_path.to_string_lossy()),
                )
                .member("preparedAssetSha256", Out::str(&self.prepared_asset_sha256))
                .member("preparedAssetSize", Out::Int(self.prepared_asset_size))
                .member(
                    "bakeToolVersion",
                    Out::Int(i64::from(self.bake_tool_version)),
                )
                .member("version", Out::Int(i64::from(self.version)))
                .member(
                    "hasIntegrityMetadata",
                    Out::Bool(self.has_integrity_metadata()),
                )
                .build(),
        )
    }
}

/// How verification went.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InstallRecordVerificationState {
    /// No record.
    Missing,
    /// The package matches its record.
    Verified,
    /// The package is from an older recipe; the plan says what to do.
    ContentUpdateRequired,
    /// Something is wrong; the status says what.
    Invalid,
}

/// What verification found.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallRecordVerification {
    /// The outcome.
    pub state: InstallRecordVerificationState,
    /// The record, when it is usable.
    pub record: Option<LauncherInstallRecord>,
    /// A line for the player.
    pub status: String,
    /// The migration an older recipe needs.
    pub required_content_work: Option<ContentMigrationPlan>,
}

impl InstallRecordVerification {
    /// Whether the package verified.
    pub fn is_verified(&self) -> bool {
        self.state == InstallRecordVerificationState::Verified
    }

    /// Whether a content update is needed.
    pub fn requires_content_update(&self) -> bool {
        self.state == InstallRecordVerificationState::ContentUpdateRequired
    }

    fn invalid(status: impl Into<String>) -> Self {
        Self {
            state: InstallRecordVerificationState::Invalid,
            record: None,
            status: status.into(),
            required_content_work: None,
        }
    }

    fn verified(record: LauncherInstallRecord, status: &str) -> Self {
        Self {
            state: InstallRecordVerificationState::Verified,
            record: Some(record),
            status: status.into(),
            required_content_work: None,
        }
    }
}

const VERIFIED_STATUS: &str = "Client content SHA-256, size, and bake-tool version verified.";

/// `<package>.previous-install`, the copy kept while a package is replaced.
pub fn backup_path(prepared_asset_path: &Path) -> PathBuf {
    let mut path = prepared_asset_path.as_os_str().to_owned();
    path.push(".previous-install");
    PathBuf::from(path)
}

// --- The installer transaction lock -------------------------------------------

/// The installer's exclusive lock (`<data>/.install.lock`), held for the
/// length of one verification or record write.
#[derive(Debug)]
pub struct InstallerTransactionLease {
    _file: File,
}

impl InstallerTransactionLease {
    /// The lock file for a data directory.
    pub fn lock_path(data_directory: &Path) -> PathBuf {
        fsutil::full_path(data_directory).join(".install.lock")
    }

    /// Takes the lock, retrying every 50 ms until it is free or cancelled.
    pub fn acquire(
        data_directory: &Path,
        cancel: &CancelToken,
    ) -> std::io::Result<Result<Self, Cancelled>> {
        let path = Self::lock_path(data_directory);
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        loop {
            if cancel.is_cancelled() {
                return Ok(Err(Cancelled));
            }
            let file = OpenOptions::new()
                .read(true)
                .write(true)
                .create(true)
                .truncate(false)
                .open(&path)?;
            match file.try_lock() {
                Ok(()) => return Ok(Ok(Self { _file: file })),
                Err(TryLockError::WouldBlock) => std::thread::sleep(Duration::from_millis(50)),
                Err(TryLockError::Error(error)) => return Err(error),
            }
        }
    }
}

// --- The verification cache ------------------------------------------------------

/// A remembered full hash of the package at one size and write time.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PreparedAssetVerificationEntry {
    /// The format version (1).
    pub version: i32,
    /// The package path.
    pub path: PathBuf,
    /// Its size then.
    pub size: i64,
    /// Its write time then, in .NET ticks (100 ns since 0001-01-01 UTC).
    pub last_write_utc_ticks: i64,
    /// The hash then.
    pub sha256: String,
}

/// `<data>/install.verification.json`: spares a full hash of an unchanged
/// package at every start. An unreadable cache means "hash again".
#[derive(Debug, Clone)]
pub struct PreparedAssetVerificationCache {
    path: PathBuf,
}

const TICKS_AT_UNIX_EPOCH: i64 = 621_355_968_000_000_000;

/// A time as .NET ticks.
pub fn to_ticks(time: SystemTime) -> i64 {
    match time.duration_since(UNIX_EPOCH) {
        Ok(since) => TICKS_AT_UNIX_EPOCH + (since.as_nanos() / 100) as i64,
        Err(before) => TICKS_AT_UNIX_EPOCH - (before.duration().as_nanos() / 100) as i64,
    }
}

impl PreparedAssetVerificationCache {
    /// The cache for a data directory.
    pub fn new(data_directory: &Path) -> Self {
        Self {
            path: fsutil::full_path(data_directory).join("install.verification.json"),
        }
    }

    /// The cache file.
    pub fn cache_path(&self) -> &Path {
        &self.path
    }

    /// The entry, or `None` when missing, unreadable or of another version.
    pub fn try_read(&self) -> Option<PreparedAssetVerificationEntry> {
        let bytes = std::fs::read(&self.path).ok()?;
        let root = host_json::parse(&bytes).ok()?;
        let Value::Object(members) = &root.value else {
            return None;
        };
        let find = |name: &str| {
            members
                .iter()
                .find(|member| member.name.eq_ignore_ascii_case(name))
                .map(|member| &member.value)
        };
        let version = host_json::int32(find("version")?).ok()?;
        let path = json::string(find("path")?).ok()?;
        let size = json::int64(find("size")?).ok()?;
        let ticks = json::int64(find("lastWriteUtcTicks")?).ok()?;
        let sha256 = json::string(find("sha256")?).ok()?;
        if version != 1 || path.trim().is_empty() || sha256.trim().is_empty() || size <= 0 {
            return None;
        }
        Some(PreparedAssetVerificationEntry {
            version,
            path: PathBuf::from(path),
            size,
            last_write_utc_ticks: ticks,
            sha256,
        })
    }

    /// Remembers a full hash (failures are ignored).
    pub fn write(&self, path: &Path, size: i64, last_write: SystemTime, sha256: &str) {
        let entry = ObjectBuilder::new()
            .member("version", Out::Int(1))
            .member("path", Out::str(fsutil::full_path(path).to_string_lossy()))
            .member("size", Out::Int(size))
            .member("lastWriteUtcTicks", Out::Int(to_ticks(last_write)))
            .member("sha256", Out::str(sha256))
            .build();
        if fsutil::write_via_tmp(&self.path, json::to_pretty(&entry).as_bytes(), false).is_err() {
            let mut temp = self.path.as_os_str().to_owned();
            temp.push(".tmp");
            fsutil::try_delete_file(Path::new(&temp));
        }
    }

    /// Forgets the entry.
    pub fn invalidate(&self) {
        fsutil::try_delete_file(&self.path);
    }

    /// Whether the entry vouches for this package as recorded.
    pub fn satisfies(
        entry: Option<&PreparedAssetVerificationEntry>,
        path: &Path,
        size: i64,
        last_write: SystemTime,
        recorded_sha256: &str,
    ) -> bool {
        entry.is_some_and(|entry| {
            fsutil::text_equal(
                &entry.path.to_string_lossy(),
                &fsutil::full_path(path).to_string_lossy(),
            ) && entry.size == size
                && entry.last_write_utc_ticks == to_ticks(last_write)
                && integrity::matches(&entry.sha256, recorded_sha256)
        })
    }
}

// --- The record store ---------------------------------------------------------------

type HashFn = dyn Fn(&Path, &CancelToken) -> std::io::Result<String> + Send + Sync;

/// Loads, verifies and saves the install record.
pub struct LauncherInstallRecordStore {
    data_directory: PathBuf,
    dat_directories: DatDirectoryLocator,
    compute_sha256: Arc<HashFn>,
    verification_cache: PreparedAssetVerificationCache,
}

impl std::fmt::Debug for LauncherInstallRecordStore {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LauncherInstallRecordStore")
            .field("data_directory", &self.data_directory)
            .finish_non_exhaustive()
    }
}

struct FileVerification {
    is_valid: bool,
    status: String,
}

fn canonical_spelling_equals(serialized: &Path, canonical: &Path, trim_ending: bool) -> bool {
    let mut text = serialized.to_string_lossy().into_owned();
    if trim_ending {
        while text.len() > 1 && (text.ends_with('/') || text.ends_with('\\')) {
            text.pop();
        }
    }
    fsutil::text_equal(&text, &canonical.to_string_lossy())
}

fn format_missing(missing: &[String]) -> String {
    if missing.is_empty() {
        String::new()
    } else {
        format!(" Missing: {}.", missing.join(", "))
    }
}

impl LauncherInstallRecordStore {
    /// The store for these paths, hashing files with SHA-256.
    pub fn new(paths: &PathSet) -> Self {
        Self::with(paths, DatDirectoryLocator::default(), |path, cancel| {
            integrity::compute_sha256_hex_cancellable(path, cancel)
        })
    }

    /// The store with its locator and hashing replaced (tests).
    pub fn with(
        paths: &PathSet,
        dat_directories: DatDirectoryLocator,
        compute_sha256: impl Fn(&Path, &CancelToken) -> std::io::Result<String> + Send + Sync + 'static,
    ) -> Self {
        let data_directory = fsutil::full_path(&paths.game_data_directory());
        Self {
            verification_cache: PreparedAssetVerificationCache::new(&data_directory),
            data_directory,
            dat_directories,
            compute_sha256: Arc::new(compute_sha256),
        }
    }

    /// The data directory.
    pub fn data_directory(&self) -> &Path {
        &self.data_directory
    }

    /// `<data>/install.json`.
    pub fn record_path(&self) -> PathBuf {
        self.data_directory.join("install.json")
    }

    /// `<data>/pak/acdream.pak`.
    pub fn prepared_asset_path(&self) -> PathBuf {
        self.data_directory.join("pak").join("acdream.pak")
    }

    /// The verification cache.
    pub fn verification_cache(&self) -> &PreparedAssetVerificationCache {
        &self.verification_cache
    }

    /// Loads and verifies the record under the installer lock.
    /// `force_full_verification` hashes the package even when the cache
    /// vouches for it (install, update and "verify my files" pass true).
    /// `progress` hears about a long read before it starts.
    pub fn load_and_verify(
        &self,
        cancel: &CancelToken,
        force_full_verification: bool,
        progress: &mut dyn FnMut(&str),
    ) -> Result<InstallRecordVerification, Cancelled> {
        let lease = InstallerTransactionLease::acquire(&self.data_directory, cancel);
        let _lease = match lease {
            Ok(Ok(lease)) => lease,
            Ok(Err(cancelled)) => return Err(cancelled),
            Err(error) => {
                return Ok(InstallRecordVerification::invalid(format!(
                    "The install record could not be read: {error}"
                )));
            }
        };
        self.load_and_verify_under_lease(cancel, force_full_verification, progress)
    }

    /// [`Self::load_and_verify`] for a caller holding the lock.
    pub fn load_and_verify_under_lease(
        &self,
        cancel: &CancelToken,
        force_full_verification: bool,
        progress: &mut dyn FnMut(&str),
    ) -> Result<InstallRecordVerification, Cancelled> {
        let record_path = self.record_path();
        if !record_path.exists() {
            return Ok(InstallRecordVerification {
                state: InstallRecordVerificationState::Missing,
                record: None,
                status: "Client content is not installed. Complete the first-run setup.".into(),
                required_content_work: None,
            });
        }
        let record = match self.read_record(&record_path) {
            Ok(record) => record,
            Err(status) => return Ok(InstallRecordVerification::invalid(status)),
        };
        if let Some(error) = self.validate_record_contract(&record, true, false) {
            return Ok(InstallRecordVerification::invalid(error));
        }
        if record.bake_tool_version > CURRENT_BAKE_TOOL_VERSION {
            return Ok(InstallRecordVerification::invalid(format!(
                "Prepared content recipe {} is newer than this launcher's recipe \
                 {CURRENT_BAKE_TOOL_VERSION}. Update the launcher.",
                record.bake_tool_version
            )));
        }
        if record.bake_tool_version < CURRENT_BAKE_TOOL_VERSION {
            if !record.prepared_asset_path.exists() {
                return Ok(InstallRecordVerification::invalid(
                    "The prepared package is missing.",
                ));
            }
            let size = std::fs::metadata(&record.prepared_asset_path)
                .map(|metadata| metadata.len() as i64)
                .unwrap_or(-1);
            if size != record.prepared_asset_size {
                return Ok(InstallRecordVerification::invalid(
                    "The prepared package size changed.",
                ));
            }
            return Ok(
                match resolve_content_migration(record.bake_tool_version, CURRENT_BAKE_TOOL_VERSION)
                {
                    Ok(plan) => InstallRecordVerification {
                        state: InstallRecordVerificationState::ContentUpdateRequired,
                        status: format!("World data update required: {}.", plan.reason),
                        record: Some(record),
                        required_content_work: Some(plan),
                    },
                    Err(error) => InstallRecordVerification::invalid(error),
                },
            );
        }

        let backup = backup_path(&record.prepared_asset_path);
        let current = self.verify_file(
            &record.prepared_asset_path,
            &record,
            cancel,
            !force_full_verification,
            progress,
        )?;
        if current.is_valid {
            fsutil::try_delete_file(&backup);
            return Ok(InstallRecordVerification::verified(record, VERIFIED_STATUS));
        }
        let backup_verification = self.verify_file(&backup, &record, cancel, false, progress)?;
        if backup_verification.is_valid {
            let restored = record
                .prepared_asset_path
                .parent()
                .map_or(Ok(()), std::fs::create_dir_all)
                .and_then(|()| std::fs::rename(&backup, &record.prepared_asset_path));
            return Ok(match restored {
                Ok(()) => InstallRecordVerification::verified(
                    record,
                    "Recovered and verified the previous client content.",
                ),
                Err(error) => InstallRecordVerification::invalid(format!(
                    "The previous verified package could not be restored: {error}"
                )),
            });
        }
        Ok(InstallRecordVerification::invalid(current.status))
    }

    fn read_record(&self, path: &Path) -> Result<LauncherInstallRecord, String> {
        let unreadable = |detail: String| format!("The install record could not be read: {detail}");
        let bytes = std::fs::read(path).map_err(|error| unreadable(error.to_string()))?;
        let root = host_json::parse(&bytes).map_err(|error| unreadable(error.to_string()))?;
        let has_version = match &root.value {
            Value::Object(members) => members
                .iter()
                .find(|member| member.name == "version")
                .is_some_and(|member| host_json::int32(&member.value).is_ok()),
            _ => false,
        };
        if !has_version {
            return Err("The install record must contain an explicit integer version.".into());
        }
        let mut object = ObjectReader::new(&root).map_err(|error| unreadable(error.to_string()))?;
        let read = |object: &mut ObjectReader<'_>, name: &str| -> Result<Option<String>, String> {
            match object.take(name) {
                Some(node) => {
                    host_json::string_or_null(node).map_err(|error| unreadable(error.to_string()))
                }
                None => Ok(None),
            }
        };
        let dat_directory = read(&mut object, "datDirectory")?.unwrap_or_default();
        let prepared_asset_path = read(&mut object, "preparedAssetPath")?.unwrap_or_default();
        let prepared_asset_sha256 = read(&mut object, "preparedAssetSha256")?.unwrap_or_default();
        let size = match object.take("preparedAssetSize") {
            Some(node) => json::int64(node).map_err(|error| unreadable(error.to_string()))?,
            None => 0,
        };
        let bake = match object.take("bakeToolVersion") {
            Some(node) => host_json::uint32(node).map_err(|error| unreadable(error.to_string()))?,
            None => 0,
        };
        let version = host_json::int32(
            object
                .required("version")
                .map_err(|error| unreadable(error.to_string()))?,
        )
        .map_err(|error| unreadable(error.to_string()))?;
        // Written by OpenAC's serializer; read-only, ignored.
        let _ = object.take("hasIntegrityMetadata");
        object
            .finish()
            .map_err(|error| unreadable(error.to_string()))?;
        let mut record = LauncherInstallRecord::new(
            dat_directory,
            prepared_asset_path,
            prepared_asset_sha256,
            size,
            bake,
        );
        record.version = version;
        Ok(record)
    }

    /// Normalizes and writes the record atomically under the installer lock.
    pub fn save_atomically(
        &self,
        record: &LauncherInstallRecord,
        cancel: &CancelToken,
    ) -> Result<(), String> {
        let lease = InstallerTransactionLease::acquire(&self.data_directory, cancel)
            .map_err(|error| error.to_string())?
            .map_err(|cancelled| cancelled.to_string())?;
        let result = self.save_atomically_under_lease(record);
        drop(lease);
        result
    }

    /// [`Self::save_atomically`] for a caller holding the lock.
    pub fn save_atomically_under_lease(
        &self,
        record: &LauncherInstallRecord,
    ) -> Result<(), String> {
        let normalized = self.normalize_for_save(record)?;
        if let Some(error) = self.validate_record_contract(&normalized, true, true) {
            return Err(error);
        }
        fsutil::write_atomic_unique(&self.record_path(), normalized.to_json().as_bytes())
            .map_err(|error| error.to_string())
    }

    /// Remembers that the package's bytes were fully verified.
    pub fn remember_verified_package(&self, record: &LauncherInstallRecord) {
        if let Ok(metadata) = std::fs::metadata(&record.prepared_asset_path)
            && metadata.len() as i64 == record.prepared_asset_size
            && let Ok(modified) = metadata.modified()
        {
            self.verification_cache.write(
                &record.prepared_asset_path,
                metadata.len() as i64,
                modified,
                &record.prepared_asset_sha256,
            );
        }
    }

    fn validate_record_contract(
        &self,
        record: &LauncherInstallRecord,
        require_canonical_serialized_paths: bool,
        require_current_recipe: bool,
    ) -> Option<String> {
        if record.version != CURRENT_INSTALL_RECORD_VERSION {
            return Some(format!(
                "Install record version {} is not supported.",
                record.version
            ));
        }
        if !record.has_integrity_metadata() {
            return Some(
                "The install record is missing SHA-256, size, or bake-tool metadata.".into(),
            );
        }
        if require_current_recipe && record.bake_tool_version != CURRENT_BAKE_TOOL_VERSION {
            return Some(format!(
                "Bake tool version {} is not supported; version {CURRENT_BAKE_TOOL_VERSION} is \
                 required.",
                record.bake_tool_version
            ));
        }
        if !is_sha256(&record.prepared_asset_sha256) {
            return Some("The install record contains an invalid SHA-256 digest.".into());
        }
        if record
            .prepared_asset_path
            .to_string_lossy()
            .trim()
            .is_empty()
        {
            return Some("The prepared asset path is missing.".into());
        }
        if record.dat_directory.to_string_lossy().trim().is_empty() {
            return Some("The DAT directory path is missing.".into());
        }
        let canonical_prepared = fsutil::full_path(&self.prepared_asset_path());
        if require_canonical_serialized_paths && !record.prepared_asset_path.is_absolute() {
            return Some("The prepared asset path must be absolute.".into());
        }
        let recorded_prepared = fsutil::full_path(&record.prepared_asset_path);
        if !fsutil::paths_equal(&recorded_prepared, &canonical_prepared) {
            return Some(
                "The install record does not point to the launcher's canonical \
                 DataDirectory/pak/acdream.pak path."
                    .into(),
            );
        }
        if require_canonical_serialized_paths
            && !canonical_spelling_equals(&record.prepared_asset_path, &recorded_prepared, false)
        {
            return Some("The prepared asset path is not canonical.".into());
        }
        if require_canonical_serialized_paths && !record.dat_directory.is_absolute() {
            return Some("The DAT directory path must be absolute.".into());
        }
        let validation = self.dat_directories.validate(Some(&record.dat_directory));
        if !validation.is_valid {
            return Some(format!(
                "{}{}",
                validation.message,
                format_missing(&validation.missing_file_names)
            ));
        }
        (require_canonical_serialized_paths
            && !canonical_spelling_equals(&record.dat_directory, &validation.directory, true))
        .then(|| "The DAT directory path is not canonical.".into())
    }

    fn normalize_for_save(
        &self,
        record: &LauncherInstallRecord,
    ) -> Result<LauncherInstallRecord, String> {
        if record.version != CURRENT_INSTALL_RECORD_VERSION {
            return Err(format!(
                "Install record version {} is not supported.",
                record.version
            ));
        }
        if record
            .prepared_asset_path
            .to_string_lossy()
            .trim()
            .is_empty()
        {
            return Err("The prepared asset path is missing.".into());
        }
        let validation = self.dat_directories.validate(Some(&record.dat_directory));
        if !validation.is_valid {
            return Err(format!(
                "{}{}",
                validation.message,
                format_missing(&validation.missing_file_names)
            ));
        }
        if !fsutil::paths_equal(&record.prepared_asset_path, &self.prepared_asset_path()) {
            return Err(
                "The install record does not point to the launcher's canonical \
                        DataDirectory/pak/acdream.pak path."
                    .into(),
            );
        }
        let mut normalized = record.clone();
        normalized.version = CURRENT_INSTALL_RECORD_VERSION;
        normalized.dat_directory = validation.directory;
        normalized.prepared_asset_path = fsutil::full_path(&self.prepared_asset_path());
        Ok(normalized)
    }

    fn verify_file(
        &self,
        path: &Path,
        record: &LauncherInstallRecord,
        cancel: &CancelToken,
        allow_cached_result: bool,
        progress: &mut dyn FnMut(&str),
    ) -> Result<FileVerification, Cancelled> {
        let metadata = match std::fs::metadata(path) {
            Ok(metadata) if metadata.is_file() => metadata,
            _ => {
                return Ok(FileVerification {
                    is_valid: false,
                    status: "The prepared package is missing.".into(),
                });
            }
        };
        let length = metadata.len() as i64;
        if length != record.prepared_asset_size {
            return Ok(FileVerification {
                is_valid: false,
                status: format!(
                    "The prepared package size changed (expected {}, found {length}).",
                    record.prepared_asset_size
                ),
            });
        }
        let last_write = metadata.modified().unwrap_or(UNIX_EPOCH);
        if allow_cached_result
            && PreparedAssetVerificationCache::satisfies(
                self.verification_cache.try_read().as_ref(),
                path,
                length,
                last_write,
                &record.prepared_asset_sha256,
            )
        {
            return Ok(FileVerification {
                is_valid: true,
                status: "Client content verified.".into(),
            });
        }
        progress(if allow_cached_result {
            "The verification cache is missing or changed. Reading the whole world-data pak once; \
             this can take around 30 seconds."
        } else {
            "Reading the whole world-data pak for explicit verification; this can take around 30 \
             seconds."
        });
        let sha256 = match (self.compute_sha256)(path, cancel) {
            Ok(sha256) => sha256,
            Err(_) if cancel.is_cancelled() => return Err(Cancelled),
            Err(error) => {
                return Ok(FileVerification {
                    is_valid: false,
                    status: format!("The prepared package could not be verified: {error}"),
                });
            }
        };
        if !integrity::matches(&sha256, &record.prepared_asset_sha256) {
            self.verification_cache.invalidate();
            return Ok(FileVerification {
                is_valid: false,
                status: "The prepared package SHA-256 does not match the install record.".into(),
            });
        }
        let hashed_write = std::fs::metadata(path).and_then(|metadata| metadata.modified());
        if hashed_write.is_ok_and(|time| time == last_write) {
            self.verification_cache
                .write(path, length, last_write, &sha256);
        }
        Ok(FileVerification {
            is_valid: true,
            status: "Client content verified.".into(),
        })
    }
}
