//! Installed client versions. Updates replace the installation at one
//! stable path, `<root>/app/client`, and keep one previous client in
//! `app/client-previous` for rollback (an older `app/<version>` folder
//! migrates on the next update). Each has an `install.json` listing every
//! file's hash, size and mode, and an activation pointer (`current.json`,
//! with `current.previous.json` as its recovery copy) names the active
//! version and the one before it. A durable replacement journal
//! (`.client-replacement.json`) lets startup finish an interrupted swap.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use ac_host::PathSet;
use ac_host::json::{self as host_json, Node, ObjectReader};

use super::UpdateError;
use super::barrier::UpdateSessionBarrier;
use super::manifest::{MAXIMUM_ARTIFACT_BYTES, ReleaseArtifact, is_sha256};
use super::payload::{is_valid_rid, is_windows_device_name, required_for_payload};
use super::version::LauncherVersion;
use super::zip::{ExtractedFileRecord, try_delete_directory};
use crate::cancel::CancelToken;
use crate::fsutil;
use crate::ids;
use crate::integrity;
use crate::json::{self, ObjectBuilder, Out};

/// The first client release that uses the install's current folder layout
/// (`ac_host::layout`): the lowest version of the release after 0.1.9, a
/// pre-release of it included. An older client reads and writes the folders
/// the layout moved, so the launcher never starts one and never rolls back
/// to one.
pub const FIRST_CLIENT_OF_THE_FOLDER_LAYOUT: &str = "0.1.10-0";

/// Whether a client version predates the install's folder layout (see
/// [`FIRST_CLIENT_OF_THE_FOLDER_LAYOUT`]).
pub fn predates_the_folder_layout(version: &LauncherVersion) -> bool {
    LauncherVersion::try_parse(FIRST_CLIENT_OF_THE_FOLDER_LAYOUT)
        .is_some_and(|first| *version < first)
}

/// What the player is told about a client older than the folder layout.
pub fn predates_the_folder_layout_message(version: &LauncherVersion) -> String {
    format!(
        "Client {version} was made for the old folder layout and cannot run on this \n         install. Update the client."
    )
}

/// One installed file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstalledFileRecord {
    /// Path relative to the version directory, `/`-separated.
    pub path: String,
    /// SHA-256, lowercase hex.
    pub sha256: String,
    /// Size in bytes.
    pub size: u64,
    /// Permission bits (0 to 0o777).
    pub unix_mode: u32,
}

/// A version directory's `install.json`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClientVersionRecord {
    /// 1.
    pub schema_version: i32,
    /// The version (equals the directory name).
    pub version: String,
    /// The runtime identifier it was installed for.
    pub rid: String,
    /// The release archive's SHA-256.
    pub archive_sha256: String,
    /// The release archive's size.
    pub archive_size: u64,
    /// Every file, sorted by path.
    pub files: Vec<InstalledFileRecord>,
}

/// `current.json`: the active version and the one before it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClientActivationPointer {
    /// 1.
    pub schema_version: i32,
    /// The active version.
    pub current_version: String,
    /// The version before it, for rollback.
    pub previous_version: Option<String>,
}

/// The installed client's state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ClientVersionState {
    /// Nothing installed.
    Missing,
    /// Every file matches its record.
    Verified,
    /// Something is wrong; the status says what.
    Invalid,
}

/// What the store found.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClientVersionResolution {
    /// Missing, verified or invalid.
    pub state: ClientVersionState,
    /// A line for the player.
    pub status: String,
    /// The active version, when verified.
    pub version: Option<LauncherVersion>,
    /// Its directory, when verified.
    pub directory: Option<PathBuf>,
    /// The rollback version, when there is one.
    pub previous_version: Option<String>,
    /// Its record, when verified.
    pub record: Option<ClientVersionRecord>,
}

impl ClientVersionResolution {
    /// Whether the client is verified.
    pub fn is_verified(&self) -> bool {
        self.state == ClientVersionState::Verified
    }

    fn missing() -> Self {
        Self {
            state: ClientVersionState::Missing,
            status: "No versioned client is installed. Check for updates to install one.".into(),
            version: None,
            directory: None,
            previous_version: None,
            record: None,
        }
    }

    fn invalid(status: impl Into<String>) -> Self {
        Self {
            state: ClientVersionState::Invalid,
            status: status.into(),
            version: None,
            directory: None,
            previous_version: None,
            record: None,
        }
    }
}

type HashFn = dyn Fn(&Path, &CancelToken) -> std::io::Result<String> + Send + Sync;

/// The versioned client store for one data directory.
pub struct ClientVersionStore {
    app_directory: PathBuf,
    current_pointer_path: PathBuf,
    previous_pointer_path: PathBuf,
    barrier: UpdateSessionBarrier,
    compute_sha256: Arc<HashFn>,
    cached: Mutex<ClientVersionResolution>,
}

impl std::fmt::Debug for ClientVersionStore {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClientVersionStore")
            .field("app_directory", &self.app_directory)
            .finish_non_exhaustive()
    }
}

struct PointerRead {
    pointer: Option<ClientActivationPointer>,
    error: Option<String>,
}

fn io_status(error: &std::io::Error) -> String {
    error.to_string()
}

/// A `/`-separated relative path every platform can hold: no empty, `.` or
/// `..` segment, no `\` or `:`, no trailing space or dot, no control or
/// wildcard character, no Windows device name, at most 512 characters.
pub fn is_normalized_relative(path: &str) -> bool {
    if path.is_empty()
        || path.chars().count() > 512
        || path.contains('\0')
        || path.contains('\\')
        || path.contains(':')
        || path.starts_with('/')
    {
        return false;
    }
    path.split('/').all(|part| {
        !part.is_empty()
            && part != "."
            && part != ".."
            && !part.ends_with(' ')
            && !part.ends_with('.')
            && !part
                .chars()
                .any(|c| c.is_control() || matches!(c, '<' | '>' | '"' | '|' | '?' | '*'))
            && !is_windows_device_name(part)
    })
}

/// `root` joined with a normalized relative path; refused when unsafe or
/// when it would leave `root`.
pub fn resolve_contained(root: &Path, relative: &str) -> Result<PathBuf, UpdateError> {
    if !is_normalized_relative(relative) {
        return Err(UpdateError::failed(format!(
            "Unsafe relative path '{relative}'."
        )));
    }
    let full_root = fsutil::full_path(root);
    let mut path = full_root.clone();
    for part in relative.split('/') {
        path.push(part);
    }
    let path = fsutil::full_path(&path);
    let root_text = full_root.to_string_lossy().into_owned();
    let prefix = if root_text.ends_with(std::path::MAIN_SEPARATOR) {
        root_text
    } else {
        format!("{root_text}{}", std::path::MAIN_SEPARATOR)
    };
    let text = path.to_string_lossy().into_owned();
    let contained = if cfg!(windows) {
        text.to_lowercase().starts_with(&prefix.to_lowercase())
    } else {
        text.starts_with(&prefix)
    };
    if contained {
        Ok(path)
    } else {
        Err(UpdateError::failed(format!(
            "Path '{relative}' escaped its root."
        )))
    }
}

fn normalize_relative(root: &Path, path: &Path) -> String {
    path.strip_prefix(root)
        .unwrap_or(path)
        .components()
        .map(|component| component.as_os_str().to_string_lossy().into_owned())
        .collect::<Vec<_>>()
        .join("/")
}

/// Refuses a directory tree holding any link or reparse point.
pub(crate) fn reject_link_tree(root: &Path) -> Result<(), UpdateError> {
    let io = |error: std::io::Error| UpdateError::io(error.to_string(), error);
    if fsutil::is_link(root).map_err(io)? {
        return Err(UpdateError::failed(
            "The client version directory is a reparse point.",
        ));
    }
    let mut pending = vec![root.to_path_buf()];
    while let Some(directory) = pending.pop() {
        for entry in std::fs::read_dir(&directory).map_err(io)? {
            let path = entry.map_err(io)?.path();
            let metadata = std::fs::symlink_metadata(&path).map_err(io)?;
            if fsutil::metadata_is_link(&metadata) {
                return Err(UpdateError::failed(format!(
                    "Client install path '{}' is a reparse point.",
                    normalize_relative(root, &path)
                )));
            }
            if metadata.is_dir() {
                pending.push(path);
            }
        }
    }
    Ok(())
}

fn list_files(root: &Path) -> std::io::Result<Vec<String>> {
    let mut files = Vec::new();
    let mut pending = vec![root.to_path_buf()];
    while let Some(directory) = pending.pop() {
        for entry in std::fs::read_dir(&directory)? {
            let path = entry?.path();
            let metadata = std::fs::symlink_metadata(&path)?;
            if metadata.is_dir() {
                pending.push(path);
            } else {
                files.push(normalize_relative(root, &path));
            }
        }
    }
    Ok(files)
}

fn has_canonical_hex_name(file_name: &str, prefix: &str, suffix: &str) -> bool {
    file_name
        .strip_prefix(prefix)
        .and_then(|rest| rest.strip_suffix(suffix))
        .is_some_and(ids::is_hex32)
}

fn is_unix_rid(rid: &str) -> bool {
    rid.starts_with("linux-") || rid.starts_with("osx-")
}

/// Refuses a payload missing a required executable, or (for a Unix
/// platform) one without owner execute permission.
pub fn validate_required_executables(
    files: &[ExtractedFileRecord],
    rid: &str,
    launcher_payload: bool,
) -> Result<(), UpdateError> {
    for path in required_for_payload(rid, launcher_payload) {
        let file = files
            .iter()
            .find(|candidate| candidate.path == path)
            .ok_or_else(|| {
                UpdateError::failed(format!(
                    "The release ZIP is missing required root executable '{path}'."
                ))
            })?;
        if is_unix_rid(rid) && file.unix_mode & 0o100 == 0 {
            return Err(UpdateError::failed(format!(
                "The Unix release executable '{path}' lacks owner execute permission."
            )));
        }
    }
    Ok(())
}

// --- JSON -------------------------------------------------------------------

fn record_json(record: &ClientVersionRecord) -> String {
    let files = record
        .files
        .iter()
        .map(|file| {
            ObjectBuilder::new()
                .member("path", Out::str(&file.path))
                .member("sha256", Out::str(&file.sha256))
                .member(
                    "size",
                    Out::Int(i64::try_from(file.size).unwrap_or(i64::MAX)),
                )
                .member("unixMode", Out::Int(i64::from(file.unix_mode)))
                .build()
        })
        .collect();
    json::to_pretty(
        &ObjectBuilder::new()
            .member("schemaVersion", Out::Int(i64::from(record.schema_version)))
            .member("version", Out::str(&record.version))
            .member("rid", Out::str(&record.rid))
            .member("archiveSha256", Out::str(&record.archive_sha256))
            .member(
                "archiveSize",
                Out::Int(i64::try_from(record.archive_size).unwrap_or(i64::MAX)),
            )
            .member("files", Out::Array(files))
            .build(),
    )
}

fn pointer_out(pointer: &ClientActivationPointer) -> Out {
    ObjectBuilder::new()
        .member("schemaVersion", Out::Int(i64::from(pointer.schema_version)))
        .member("currentVersion", Out::str(&pointer.current_version))
        .optional(
            "previousVersion",
            pointer.previous_version.as_deref().map(Out::str),
        )
        .build()
}

fn pointer_json(pointer: &ClientActivationPointer) -> String {
    json::to_pretty(&pointer_out(pointer))
}

fn parse_strict(bytes: &[u8]) -> Result<Node, String> {
    let root = host_json::parse(bytes).map_err(|error| match error.kind {
        host_json::JsonErrorKind::DuplicateMember => format!(
            "Duplicate JSON property at line {}, column {} is not allowed.",
            error.at.line, error.at.column
        ),
        _ => error.to_string(),
    })?;
    if json::depth(&root) > 32 {
        return Err("nested too deeply".into());
    }
    Ok(root)
}

fn optional_text(object: &mut ObjectReader<'_>, name: &str) -> Result<Option<String>, String> {
    match object.take(name) {
        Some(node) => host_json::string_or_null(node).map_err(|error| error.to_string()),
        None => Ok(None),
    }
}

fn optional_int(object: &mut ObjectReader<'_>, name: &str) -> Result<i64, String> {
    match object.take(name) {
        Some(node) => json::int64(node).map_err(|error| error.to_string()),
        None => Ok(0),
    }
}

fn parse_pointer_bytes(bytes: &[u8]) -> Result<Option<ClientActivationPointer>, String> {
    let root = parse_strict(bytes)?;
    if host_json::is_null(&root) {
        return Ok(None);
    }
    let mut object = ObjectReader::new(&root).map_err(|error| error.to_string())?;
    let schema_version = optional_int(&mut object, "schemaVersion")?;
    let current_version = optional_text(&mut object, "currentVersion")?;
    let previous_version = optional_text(&mut object, "previousVersion")?;
    object.finish().map_err(|error| error.to_string())?;
    Ok(Some(ClientActivationPointer {
        schema_version: i32::try_from(schema_version).map_err(|_| "out of range".to_owned())?,
        current_version: current_version.unwrap_or_default(),
        previous_version,
    }))
}

fn parse_record_bytes(bytes: &[u8]) -> Result<Option<ClientVersionRecord>, String> {
    let root = parse_strict(bytes)?;
    if host_json::is_null(&root) {
        return Ok(None);
    }
    let mut object = ObjectReader::new(&root).map_err(|error| error.to_string())?;
    let schema_version = optional_int(&mut object, "schemaVersion")?;
    let version = optional_text(&mut object, "version")?.unwrap_or_default();
    let rid = optional_text(&mut object, "rid")?.unwrap_or_default();
    let archive_sha256 = optional_text(&mut object, "archiveSha256")?.unwrap_or_default();
    let archive_size = optional_int(&mut object, "archiveSize")?;
    let files = match object.take("files") {
        Some(node) if !host_json::is_null(node) => host_json::array_items(node)
            .map_err(|error| error.to_string())?
            .iter()
            .map(|item| {
                let mut file = ObjectReader::new(item).map_err(|error| error.to_string())?;
                let path = optional_text(&mut file, "path")?.unwrap_or_default();
                let sha256 = optional_text(&mut file, "sha256")?.unwrap_or_default();
                let size = optional_int(&mut file, "size")?;
                let unix_mode = optional_int(&mut file, "unixMode")?;
                file.finish().map_err(|error| error.to_string())?;
                Ok(InstalledFileRecord {
                    path,
                    sha256,
                    size: u64::try_from(size).map_err(|_| "negative size".to_owned())?,
                    unix_mode: u32::try_from(unix_mode)
                        .ok()
                        .filter(|&mode| mode <= 0o777)
                        .ok_or_else(|| "invalid mode".to_owned())?,
                })
            })
            .collect::<Result<Vec<_>, String>>()?,
        _ => Vec::new(),
    };
    object.finish().map_err(|error| error.to_string())?;
    Ok(Some(ClientVersionRecord {
        schema_version: i32::try_from(schema_version).map_err(|_| "out of range".to_owned())?,
        version,
        rid,
        archive_sha256,
        archive_size: u64::try_from(archive_size).map_err(|_| "negative size".to_owned())?,
        files,
    }))
}

fn validate_pointer(pointer: &ClientActivationPointer) -> Option<String> {
    if pointer.schema_version != 1 {
        return Some(format!(
            "Client pointer schema version {} is not supported.",
            pointer.schema_version
        ));
    }
    if LauncherVersion::try_parse(&pointer.current_version).is_none() {
        return Some("Client pointer currentVersion is invalid.".into());
    }
    if let Some(previous) = &pointer.previous_version
        && (LauncherVersion::try_parse(previous).is_none() || *previous == pointer.current_version)
    {
        return Some("Client pointer previousVersion is invalid.".into());
    }
    None
}

fn validate_record(
    record: &ClientVersionRecord,
    version: &LauncherVersion,
    rid: &str,
) -> Option<String> {
    if record.schema_version != 1 {
        return Some(format!(
            "Client install schema version {} is not supported.",
            record.schema_version
        ));
    }
    if record.version != version.value() || LauncherVersion::try_parse(&record.version).is_none() {
        return Some("Client install version does not match its directory.".into());
    }
    if record.rid != rid || !is_valid_rid(&record.rid) {
        return Some(format!("Client install RID does not match '{rid}'."));
    }
    if !is_sha256(&record.archive_sha256)
        || record.archive_size == 0
        || record.archive_size > MAXIMUM_ARTIFACT_BYTES
    {
        return Some("Client install archive metadata is invalid.".into());
    }
    if record.files.is_empty() {
        return Some("Client install file list is empty.".into());
    }
    let mut paths = std::collections::HashSet::new();
    let mut prior: Option<&str> = None;
    for file in &record.files {
        if !is_normalized_relative(&file.path)
            || !paths.insert(file.path.to_lowercase())
            || !is_sha256(&file.sha256)
            || file.unix_mode > 0o777
            || prior.is_some_and(|prior| prior >= file.path.as_str())
        {
            return Some(
                "Client install file metadata is invalid, duplicated, or unsorted.".into(),
            );
        }
        prior = Some(&file.path);
    }
    for required in required_for_payload(rid, false) {
        if !paths.contains(&required.to_lowercase()) {
            return Some(format!("Client install is missing '{required}'."));
        }
    }
    None
}

impl ClientVersionStore {
    /// The store under `<data>/app`.
    pub fn new(paths: &PathSet) -> Self {
        Self::with_hasher(paths, |path, cancel| {
            integrity::compute_sha256_hex_cancellable(path, cancel)
        })
    }

    /// A store whose file hashing is replaced (tests count or fake it).
    pub fn with_hasher(
        paths: &PathSet,
        compute_sha256: impl Fn(&Path, &CancelToken) -> std::io::Result<String> + Send + Sync + 'static,
    ) -> Self {
        let app_directory = fsutil::full_path(&paths.data_directory).join("app");
        Self {
            current_pointer_path: app_directory.join("current.json"),
            previous_pointer_path: app_directory.join("current.previous.json"),
            app_directory,
            barrier: UpdateSessionBarrier::new(&paths.data_directory),
            compute_sha256: Arc::new(compute_sha256),
            cached: Mutex::new(ClientVersionResolution::missing()),
        }
    }

    /// `<data>/app`.
    pub fn app_directory(&self) -> &Path {
        &self.app_directory
    }

    /// `<data>/app/current.json`.
    pub fn current_pointer_path(&self) -> &Path {
        &self.current_pointer_path
    }

    /// `<data>/app/current.previous.json`.
    pub fn previous_pointer_path(&self) -> &Path {
        &self.previous_pointer_path
    }

    /// The barrier sessions and updates share.
    pub fn barrier(&self) -> &UpdateSessionBarrier {
        &self.barrier
    }

    /// The last resolution.
    pub fn cached_resolution(&self) -> ClientVersionResolution {
        self.cached
            .lock()
            .map(|cached| cached.clone())
            .unwrap_or_else(|poisoned| poisoned.into_inner().clone())
    }

    fn set_cached(&self, resolution: &ClientVersionResolution) {
        match self.cached.lock() {
            Ok(mut cached) => *cached = resolution.clone(),
            Err(poisoned) => *poisoned.into_inner() = resolution.clone(),
        }
    }

    /// Where `version` is installed: the stable client folder or the
    /// rollback copy when either holds it (or the client folder when the
    /// pointer names it), else an older `<root>/app/<version>` folder, which
    /// migrates on the next update.
    pub fn version_directory(&self, version: &LauncherVersion) -> PathBuf {
        for directory in [self.client_directory(), self.backup_directory()] {
            if installed_version(&directory).as_deref() == Some(version.value()) {
                return directory;
            }
        }
        if self.client_directory().is_dir()
            && Self::read_pointer(&self.current_pointer_path)
                .pointer
                .is_some_and(|pointer| pointer.current_version == version.value())
        {
            return self.client_directory();
        }
        // Existing installations migrate on their next update.
        self.app_directory.join(version.value())
    }

    /// `<root>/app/client`: the installed client, whatever its version.
    pub fn client_directory(&self) -> PathBuf {
        self.app_directory.join("client")
    }

    /// `<root>/app/client-previous`: the one client kept for rollback.
    pub fn backup_directory(&self) -> PathBuf {
        self.app_directory.join("client-previous")
    }

    /// A replacement was interrupted and waits for startup to finish it.
    pub fn has_pending_replacement(&self) -> bool {
        self.replacement_path().exists()
    }

    fn replacement_path(&self) -> PathBuf {
        self.app_directory.join(".client-replacement.json")
    }

    fn replaced_directory(&self) -> PathBuf {
        self.app_directory.join(".client-replaced")
    }

    /// `<directory>/install.json`.
    pub fn metadata_path(version_directory: &Path) -> PathBuf {
        fsutil::full_path(version_directory).join("install.json")
    }

    /// Resolves the active version under the exclusive lease, cleaning up
    /// leftovers and recovering the pointer from its copy when needed. When
    /// a session holds the barrier, resolves read-only instead.
    pub fn load_and_recover(
        &self,
        rid: &str,
        cancel: &CancelToken,
    ) -> Result<ClientVersionResolution, UpdateError> {
        match self.barrier.acquire_exclusive() {
            Ok(_lease) => self.load_and_recover_under_lease(rid, cancel),
            Err(error) if error.is_io() => self.load_current_read_only(rid, cancel),
            Err(error) => Err(error),
        }
    }

    /// Resolves the active version without changing anything.
    pub fn load_current_read_only(
        &self,
        rid: &str,
        cancel: &CancelToken,
    ) -> Result<ClientVersionResolution, UpdateError> {
        require_rid(rid)?;
        if self.has_pending_replacement() {
            let pending = ClientVersionResolution::invalid(
                "Client replacement is incomplete; close running sessions and restart the \
                 launcher to recover.",
            );
            self.set_cached(&pending);
            return Ok(pending);
        }
        let current = Self::read_pointer(&self.current_pointer_path);
        let resolution = match current.pointer {
            Some(pointer) => self.resolve_pointer(&pointer, rid, cancel)?,
            None if !self.current_pointer_path.exists() => ClientVersionResolution::missing(),
            None => ClientVersionResolution::invalid(
                current
                    .error
                    .unwrap_or_else(|| "The client activation pointer is invalid.".into()),
            ),
        };
        self.set_cached(&resolution);
        Ok(resolution)
    }

    /// [`Self::load_and_recover`] for a caller already holding the lease.
    pub fn load_and_recover_under_lease(
        &self,
        rid: &str,
        cancel: &CancelToken,
    ) -> Result<ClientVersionResolution, UpdateError> {
        require_rid(rid)?;
        std::fs::create_dir_all(&self.app_directory)
            .map_err(|error| UpdateError::io(io_status(&error), error))?;
        self.recover_replacement(rid, cancel)?;
        self.cleanup_owned_residue();
        let current = Self::read_pointer(&self.current_pointer_path);
        if let Some(pointer) = &current.pointer {
            let resolution = self.resolve_pointer(pointer, rid, cancel)?;
            self.set_cached(&resolution);
            return Ok(resolution);
        }
        let previous = Self::read_pointer(&self.previous_pointer_path);
        if let Some(pointer) = &previous.pointer {
            let mut recovered = self.resolve_pointer(pointer, rid, cancel)?;
            if let Some(version) = recovered
                .version
                .as_ref()
                .filter(|version| predates_the_folder_layout(version))
            {
                // Only the previous client is left, and it would run on the
                // old folders: nothing is restored.
                let refused =
                    ClientVersionResolution::invalid(predates_the_folder_layout_message(version));
                self.set_cached(&refused);
                return Ok(refused);
            }
            if recovered.is_verified() {
                let installed = installed_version(&self.client_directory())
                    .and_then(|text| LauncherVersion::try_parse(&text));
                if recovered.directory.as_deref() == Some(self.backup_directory().as_path())
                    && let Some(installed) = installed
                {
                    // The pointer names the rollback copy: bring it back to
                    // the client folder.
                    let old = ClientActivationPointer {
                        schema_version: 1,
                        current_version: installed.value().to_owned(),
                        previous_version: None,
                    };
                    let restored = ClientActivationPointer {
                        previous_version: Some(installed.value().to_owned()),
                        ..pointer.clone()
                    };
                    self.replace_installation(
                        &self.backup_directory(),
                        Some(&old),
                        &restored,
                        rid,
                        cancel,
                    )?;
                    let mut recovered = self.resolve_pointer(&restored, rid, cancel)?;
                    recovered.status = "Recovered the last valid client activation pointer.".into();
                    self.set_cached(&recovered);
                    return Ok(recovered);
                }
                Self::write_pointer_file(&self.current_pointer_path, pointer)?;
                recovered.status = "Recovered the last valid client activation pointer.".into();
                self.set_cached(&recovered);
                return Ok(recovered);
            }
        }
        let resolution =
            if !self.current_pointer_path.exists() && !self.previous_pointer_path.exists() {
                ClientVersionResolution::missing()
            } else {
                ClientVersionResolution::invalid(current.error.or(previous.error).unwrap_or_else(
                    || "No valid client activation pointer could be recovered.".into(),
                ))
            };
        self.set_cached(&resolution);
        Ok(resolution)
    }

    /// A fresh staging directory name under `<data>/app`.
    pub fn create_client_staging_directory(&self) -> Result<PathBuf, UpdateError> {
        std::fs::create_dir_all(&self.app_directory)
            .map_err(|error| UpdateError::io(io_status(&error), error))?;
        Ok(self
            .app_directory
            .join(format!(".client-staging-{}", ids::unique_hex32())))
    }

    /// Writes the record into a verified staging directory, moves it into
    /// place (quarantining a corrupt copy of the same version) and switches
    /// the pointer, keeping the previous version for rollback. The caller
    /// holds the exclusive lease.
    pub fn promote_and_activate_under_lease(
        &self,
        staging_directory: &Path,
        version: &LauncherVersion,
        rid: &str,
        artifact: &ReleaseArtifact,
        extracted_files: &[ExtractedFileRecord],
        cancel: &CancelToken,
    ) -> Result<ClientVersionResolution, UpdateError> {
        require_rid(rid)?;
        let staging = fsutil::full_path(staging_directory);
        self.require_owned_staging_path(&staging)?;
        validate_required_executables(extracted_files, rid, false)?;
        if extracted_files
            .iter()
            .any(|file| file.path.eq_ignore_ascii_case("install.json"))
        {
            return Err(UpdateError::failed(
                "The client ZIP may not provide the launcher's install.json record.",
            ));
        }
        let mut files: Vec<InstalledFileRecord> = extracted_files
            .iter()
            .map(|file| InstalledFileRecord {
                path: file.path.clone(),
                sha256: file.sha256.clone(),
                size: file.size,
                unix_mode: file.unix_mode,
            })
            .collect();
        files.sort_by(|left, right| left.path.cmp(&right.path));
        let record = ClientVersionRecord {
            schema_version: 1,
            version: version.value().to_owned(),
            rid: rid.to_owned(),
            archive_sha256: artifact.sha256.to_ascii_lowercase(),
            archive_size: artifact.size,
            files,
        };
        if let Some(error) = validate_record(&record, version, rid) {
            return Err(UpdateError::failed(error));
        }
        let io = |error: std::io::Error| UpdateError::io(io_status(&error), error);
        fsutil::write_atomic_unique(
            &Self::metadata_path(&staging),
            record_json(&record).as_bytes(),
        )
        .map_err(io)?;
        let staged = self.verify_version_directory(&staging, version, rid, cancel)?;
        if !staged.is_verified() {
            return Err(UpdateError::failed(staged.status));
        }

        let old_pointer = Self::read_pointer(&self.current_pointer_path).pointer;
        if let Some(old) = old_pointer
            .as_ref()
            .filter(|pointer| pointer.current_version == version.value())
        {
            let existing = self.resolve_pointer(old, rid, cancel)?;
            let same = existing.is_verified()
                && existing.record.as_ref().is_some_and(|existing| {
                    existing.archive_sha256 == record.archive_sha256
                        && existing.archive_size == record.archive_size
                });
            if !same {
                return Err(UpdateError::failed(
                    "The active client version cannot be replaced with different or corrupt \
                     content.",
                ));
            }
            if existing.directory.as_deref() == Some(self.client_directory().as_path()) {
                try_delete_directory(&staging);
                self.set_cached(&existing);
                return Ok(existing);
            }
        }
        let previous_version = match &old_pointer {
            None => None,
            Some(pointer) if pointer.current_version == version.value() => {
                pointer.previous_version.clone()
            }
            Some(pointer) => Some(pointer.current_version.clone()),
        };
        let pointer = ClientActivationPointer {
            schema_version: 1,
            current_version: version.value().to_owned(),
            previous_version,
        };
        self.replace_installation(&staging, old_pointer.as_ref(), &pointer, rid, cancel)?;
        let resolution = self.resolve_pointer(&pointer, rid, cancel)?;
        if !resolution.is_verified() {
            return Err(UpdateError::failed(resolution.status));
        }
        self.set_cached(&resolution);
        Ok(resolution)
    }

    /// Makes the previous version active again (and the current one the
    /// previous), after verifying it.
    pub fn rollback(
        &self,
        rid: &str,
        cancel: &CancelToken,
    ) -> Result<ClientVersionResolution, UpdateError> {
        let _lease = self.barrier.acquire_exclusive()?;
        let read = Self::read_pointer(&self.current_pointer_path);
        let pointer = read.pointer.ok_or_else(|| {
            UpdateError::failed(
                read.error
                    .unwrap_or_else(|| "There is no active client version to roll back.".into()),
            )
        })?;
        let previous = pointer
            .previous_version
            .clone()
            .filter(|v| !v.is_empty())
            .ok_or_else(|| {
                UpdateError::failed("There is no previous client version available for rollback.")
            })?;
        let previous = LauncherVersion::try_parse(&previous)
            .ok_or_else(|| UpdateError::failed("Client pointer previousVersion is invalid."))?;
        if predates_the_folder_layout(&previous) {
            return Err(UpdateError::failed(predates_the_folder_layout_message(
                &previous,
            )));
        }
        let verified = self.verify_version_directory(
            &self.version_directory(&previous),
            &previous,
            rid,
            cancel,
        )?;
        if !verified.is_verified() {
            return Err(UpdateError::failed(format!(
                "The previous client version cannot be activated: {}",
                verified.status
            )));
        }
        let swapped = ClientActivationPointer {
            schema_version: 1,
            current_version: previous.value().to_owned(),
            previous_version: Some(pointer.current_version.clone()),
        };
        let incoming = verified.directory.clone().unwrap_or_default();
        self.replace_installation(&incoming, Some(&pointer), &swapped, rid, cancel)?;
        let resolution = self.resolve_pointer(&swapped, rid, cancel)?;
        self.set_cached(&resolution);
        Ok(resolution)
    }

    fn resolve_pointer(
        &self,
        pointer: &ClientActivationPointer,
        rid: &str,
        cancel: &CancelToken,
    ) -> Result<ClientVersionResolution, UpdateError> {
        if let Some(error) = validate_pointer(pointer) {
            return Ok(ClientVersionResolution::invalid(error));
        }
        let Some(version) = LauncherVersion::try_parse(&pointer.current_version) else {
            return Ok(ClientVersionResolution::invalid(
                "Client pointer currentVersion is invalid.",
            ));
        };
        let mut resolution = self.verify_version_directory(
            &self.version_directory(&version),
            &version,
            rid,
            cancel,
        )?;
        if resolution.is_verified() {
            resolution
                .previous_version
                .clone_from(&pointer.previous_version);
        }
        Ok(resolution)
    }

    fn verify_version_directory(
        &self,
        directory: &Path,
        version: &LauncherVersion,
        rid: &str,
        cancel: &CancelToken,
    ) -> Result<ClientVersionResolution, UpdateError> {
        if !directory.is_dir() {
            return Ok(ClientVersionResolution::invalid(format!(
                "Client version {version} directory is missing."
            )));
        }
        let unverifiable = |detail: String| {
            ClientVersionResolution::invalid(format!(
                "Client version {version} could not be verified: {detail}"
            ))
        };
        if let Err(error) = reject_link_tree(directory) {
            return Ok(unverifiable(error.to_string()));
        }
        let metadata = Self::metadata_path(directory);
        if !metadata.exists() {
            return Ok(ClientVersionResolution::invalid(format!(
                "Client version {version} install.json is missing."
            )));
        }
        let bytes = match std::fs::read(&metadata) {
            Ok(bytes) => bytes,
            Err(error) => return Ok(unverifiable(error.to_string())),
        };
        let record = match parse_record_bytes(&bytes) {
            Ok(Some(record)) => record,
            Ok(None) => {
                return Ok(ClientVersionResolution::invalid(format!(
                    "Client version {version} install.json is missing."
                )));
            }
            Err(error) => return Ok(unverifiable(error)),
        };
        if let Some(error) = validate_record(&record, version, rid) {
            return Ok(ClientVersionResolution::invalid(error));
        }
        let mut actual = match list_files(directory) {
            Ok(files) => files,
            Err(error) => return Ok(unverifiable(error.to_string())),
        };
        actual.retain(|path| !fsutil::text_equal(path, "install.json"));
        actual.sort();
        let mut recorded: Vec<&str> = record.files.iter().map(|file| file.path.as_str()).collect();
        recorded.sort_unstable();
        if actual != recorded {
            return Ok(ClientVersionResolution::invalid(format!(
                "Client version {version} contains missing or unrecorded files."
            )));
        }
        for file in &record.files {
            cancel.check()?;
            let path = match resolve_contained(directory, &file.path) {
                Ok(path) => path,
                Err(error) => return Ok(unverifiable(error.to_string())),
            };
            let size = std::fs::metadata(&path).map(|metadata| metadata.len());
            if size.as_ref().map_or(true, |&size| size != file.size) {
                return Ok(ClientVersionResolution::invalid(format!(
                    "Client version {version} file '{}' size is corrupt.",
                    file.path
                )));
            }
            let sha256 = match (self.compute_sha256)(&path, cancel) {
                Ok(sha256) => sha256,
                Err(_) if cancel.is_cancelled() => return Err(UpdateError::Cancelled),
                Err(error) => return Ok(unverifiable(error.to_string())),
            };
            if !sha256.eq_ignore_ascii_case(&file.sha256) {
                return Ok(ClientVersionResolution::invalid(format!(
                    "Client version {version} file '{}' SHA-256 is corrupt.",
                    file.path
                )));
            }
            if let Ok(Some(mode)) = fsutil::unix_mode(&path)
                && mode & 0o777 != file.unix_mode
            {
                return Ok(ClientVersionResolution::invalid(format!(
                    "Client version {version} file '{}' mode is corrupt.",
                    file.path
                )));
            }
        }
        Ok(ClientVersionResolution {
            state: ClientVersionState::Verified,
            status: format!("Client version {version} verified."),
            version: Some(version.clone()),
            directory: Some(directory.to_path_buf()),
            previous_version: None,
            record: Some(record),
        })
    }

    fn save_pointer(&self, pointer: &ClientActivationPointer) -> Result<(), UpdateError> {
        if let Some(error) = validate_pointer(pointer) {
            return Err(UpdateError::failed(error));
        }
        let io = |error: std::io::Error| UpdateError::io(io_status(&error), error);
        if self.current_pointer_path.exists() {
            let previous = std::fs::read(&self.current_pointer_path).map_err(io)?;
            if Self::parse_pointer(&previous).pointer.is_some() {
                fsutil::write_atomic_unique(&self.previous_pointer_path, &previous).map_err(io)?;
            }
        }
        Self::write_pointer_file(&self.current_pointer_path, pointer)
    }

    fn write_pointer_file(
        path: &Path,
        pointer: &ClientActivationPointer,
    ) -> Result<(), UpdateError> {
        fsutil::write_atomic_unique(path, pointer_json(pointer).as_bytes())
            .map_err(|error| UpdateError::io(io_status(&error), error))
    }

    fn read_pointer(path: &Path) -> PointerRead {
        if !path.exists() {
            return PointerRead {
                pointer: None,
                error: None,
            };
        }
        match std::fs::read(path) {
            Ok(bytes) => Self::parse_pointer(&bytes),
            Err(error) => PointerRead {
                pointer: None,
                error: Some(format!("Client pointer could not be read: {error}")),
            },
        }
    }

    fn parse_pointer(bytes: &[u8]) -> PointerRead {
        match parse_pointer_bytes(bytes) {
            Ok(Some(pointer)) => match validate_pointer(&pointer) {
                None => PointerRead {
                    pointer: Some(pointer),
                    error: None,
                },
                Some(error) => PointerRead {
                    pointer: None,
                    error: Some(error),
                },
            },
            Ok(None) => PointerRead {
                pointer: None,
                error: Some("Client pointer is empty.".into()),
            },
            Err(error) => PointerRead {
                pointer: None,
                error: Some(format!("Client pointer is invalid: {error}")),
            },
        }
    }

    fn cleanup_owned_residue(&self) {
        let Ok(entries) = std::fs::read_dir(&self.app_directory) else {
            return;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let name = entry.file_name().to_string_lossy().into_owned();
            let is_dir = path.is_dir();
            if is_dir
                && (has_canonical_hex_name(&name, ".client-staging-", "")
                    || has_canonical_hex_name(&name, ".client-corrupt-", ""))
            {
                try_delete_directory(&path);
            } else if !is_dir && has_canonical_hex_name(&name, ".client-download-", ".zip") {
                fsutil::try_delete_file(&path);
            } else if !is_dir && name.starts_with(".current") && name.ends_with(".tmp") {
                let parts: Vec<&str> = name.split('.').collect();
                if parts.len() >= 4 && ids::is_hex32(parts[parts.len() - 2]) {
                    fsutil::try_delete_file(&path);
                }
            }
        }
    }

    fn require_owned_staging_path(&self, path: &Path) -> Result<(), UpdateError> {
        let parent = path.parent().unwrap_or(Path::new(""));
        let name = path
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default();
        if !fsutil::paths_equal(parent, &self.app_directory)
            || !has_canonical_hex_name(&name, ".client-staging-", "")
        {
            return Err(UpdateError::failed(
                "The client extraction path is not an owned staging directory.",
            ));
        }
        Ok(())
    }
}

/// What a replacement does, written before it starts so startup can finish
/// it: which folder comes in, which goes out, the pointer to publish, and
/// whether the outgoing copy is thrown away (the same version replaced)
/// rather than kept for rollback.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Replacement {
    incoming_name: String,
    old_name: Option<String>,
    pointer: ClientActivationPointer,
    keep_previous: bool,
}

impl Replacement {
    fn to_json(&self) -> String {
        json::to_pretty(
            &ObjectBuilder::new()
                .member("incomingName", Out::str(&self.incoming_name))
                .member(
                    "oldName",
                    self.old_name.as_deref().map_or(Out::Null, Out::str),
                )
                .member("pointer", pointer_out(&self.pointer))
                .member("keepPrevious", Out::Bool(self.keep_previous))
                .build(),
        )
    }

    fn parse(bytes: &[u8]) -> Result<Self, String> {
        let root = parse_strict(bytes)?;
        if host_json::is_null(&root) {
            return Err("The client replacement journal is empty.".into());
        }
        let mut object = ObjectReader::new(&root).map_err(|error| error.to_string())?;
        let incoming_name = optional_text(&mut object, "incomingName")?.unwrap_or_default();
        let old_name = optional_text(&mut object, "oldName")?;
        let pointer = match object.take("pointer") {
            Some(node) => {
                let mut reader = ObjectReader::new(node).map_err(|error| error.to_string())?;
                let schema_version = optional_int(&mut reader, "schemaVersion")?;
                let current_version = optional_text(&mut reader, "currentVersion")?;
                let previous_version = optional_text(&mut reader, "previousVersion")?;
                reader.finish().map_err(|error| error.to_string())?;
                ClientActivationPointer {
                    schema_version: i32::try_from(schema_version)
                        .map_err(|_| "out of range".to_owned())?,
                    current_version: current_version.unwrap_or_default(),
                    previous_version,
                }
            }
            None => return Err("The client replacement journal has no pointer.".into()),
        };
        let keep_previous = match object.take("keepPrevious") {
            Some(node) => host_json::boolean(node).map_err(|error| error.to_string())?,
            None => false,
        };
        object.finish().map_err(|error| error.to_string())?;
        Ok(Self {
            incoming_name,
            old_name,
            pointer,
            keep_previous,
        })
    }

    /// Only the known folder names and a valid pointer.
    fn validate(&self) -> Result<(), UpdateError> {
        let is_slot = |name: &str| {
            name == "client"
                || name == "client-previous"
                || LauncherVersion::try_parse(name).is_some()
        };
        let incoming_ok = self.incoming_name == "client-previous"
            || LauncherVersion::try_parse(&self.incoming_name).is_some()
            || has_canonical_hex_name(&self.incoming_name, ".client-staging-", "");
        if validate_pointer(&self.pointer).is_some()
            || !incoming_ok
            || self.old_name.as_deref().is_some_and(|name| !is_slot(name))
            || self.old_name.as_deref() == Some(self.incoming_name.as_str())
        {
            return Err(UpdateError::failed(
                "The client replacement journal contains invalid paths or versions.",
            ));
        }
        Ok(())
    }
}

/// The version an installation folder's record names, if it can be read.
fn installed_version(directory: &Path) -> Option<String> {
    let bytes = std::fs::read(ClientVersionStore::metadata_path(directory)).ok()?;
    parse_record_bytes(&bytes)
        .ok()
        .flatten()
        .map(|record| record.version)
}

fn file_name_of(path: &Path) -> String {
    path.file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default()
}

impl ClientVersionStore {
    /// Replaces the client folder with `incoming` (a verified staging
    /// folder, an older version folder, or the rollback copy) and
    /// publishes `pointer`. The journal is written first; from then on the
    /// swap is finished even if the caller is cancelled, and startup
    /// resumes the same steps after a crash.
    fn replace_installation(
        &self,
        incoming: &Path,
        old_pointer: Option<&ClientActivationPointer>,
        pointer: &ClientActivationPointer,
        rid: &str,
        cancel: &CancelToken,
    ) -> Result<(), UpdateError> {
        if self.replacement_path().exists() || self.replaced_directory().exists() {
            return Err(UpdateError::failed(
                "An interrupted client replacement needs startup recovery first.",
            ));
        }
        let old_directory = old_pointer
            .and_then(|pointer| LauncherVersion::try_parse(&pointer.current_version))
            .map(|version| self.version_directory(&version));
        if old_directory.as_deref() != Some(self.client_directory().as_path())
            && self.client_directory().is_dir()
        {
            return Err(UpdateError::failed(
                "The client installation folder is already occupied by another installation.",
            ));
        }
        let replacement = Replacement {
            incoming_name: file_name_of(incoming),
            old_name: old_directory.as_deref().map(file_name_of),
            pointer: pointer.clone(),
            keep_previous: old_pointer
                .is_some_and(|old| old.current_version == pointer.current_version),
        };
        replacement.validate()?;
        cancel.check()?;
        fsutil::write_atomic_unique(&self.replacement_path(), replacement.to_json().as_bytes())
            .map_err(|error| UpdateError::io(io_status(&error), error))?;
        self.set_cached(&ClientVersionResolution::invalid(
            "Client replacement is in progress.",
        ));
        // Once the durable journal exists, finish the swap even if the
        // download operation is cancelled.
        self.complete_replacement(&replacement, rid)
    }

    /// Finishes a replacement the journal records (startup recovery).
    fn recover_replacement(&self, rid: &str, _cancel: &CancelToken) -> Result<(), UpdateError> {
        let path = self.replacement_path();
        if !path.exists() {
            return Ok(());
        }
        let bytes =
            std::fs::read(&path).map_err(|error| UpdateError::io(io_status(&error), error))?;
        let replacement = Replacement::parse(&bytes).map_err(UpdateError::failed)?;
        replacement.validate()?;
        self.complete_replacement(&replacement, rid)
    }

    fn complete_replacement(
        &self,
        replacement: &Replacement,
        rid: &str,
    ) -> Result<(), UpdateError> {
        let io = |error: std::io::Error| UpdateError::io(io_status(&error), error);
        // The swap finishes whatever the caller asked: it is never cancelled.
        let never = CancelToken::none();
        let incoming = self.app_directory.join(&replacement.incoming_name);
        let old = replacement
            .old_name
            .as_ref()
            .map(|name| self.app_directory.join(name));
        let version = LauncherVersion::try_parse(&replacement.pointer.current_version)
            .ok_or_else(|| UpdateError::failed("Client pointer currentVersion is invalid."))?;
        let client = self.client_directory();
        let published = Self::read_pointer(&self.current_pointer_path).pointer;
        let already_published = published.as_ref() == Some(&replacement.pointer)
            && installed_version(&client).as_deref() == Some(version.value());
        if !already_published && incoming.is_dir() {
            let candidate = self.verify_version_directory(&incoming, &version, rid, &never)?;
            if !candidate.is_verified() {
                return Err(UpdateError::failed(candidate.status));
            }
            if let Some(old) = old.as_ref().filter(|old| old.is_dir()) {
                reject_link_tree(old)?;
                std::fs::rename(old, self.replaced_directory()).map_err(io)?;
            }
            std::fs::rename(&incoming, &client).map_err(io)?;
        }
        let installed = self.verify_version_directory(&client, &version, rid, &never)?;
        if !installed.is_verified() {
            return Err(UpdateError::failed(installed.status));
        }
        let current = Self::read_pointer(&self.current_pointer_path).pointer;
        if current.as_ref() != Some(&replacement.pointer) {
            self.save_pointer(&replacement.pointer)?;
        }
        let replaced = self.replaced_directory();
        if replaced.is_dir() {
            reject_link_tree(&replaced)?;
            if replacement.keep_previous {
                std::fs::remove_dir_all(&replaced).map_err(io)?;
            } else {
                let backup = self.backup_directory();
                if backup.is_dir() {
                    reject_link_tree(&backup)?;
                    std::fs::remove_dir_all(&backup).map_err(io)?;
                }
                std::fs::rename(&replaced, &backup).map_err(io)?;
            }
        }
        std::fs::remove_file(self.replacement_path()).map_err(io)
    }
}

fn require_rid(rid: &str) -> Result<(), UpdateError> {
    if is_valid_rid(rid) {
        Ok(())
    } else {
        Err(UpdateError::failed("RID is invalid."))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Ported from OpenAC tests/.../Updates/SafeZipExtractorTests.cs
    // (VersionMetadataUsesTheSameCompletePortableDeviceRules).
    #[test]
    fn version_metadata_uses_the_same_complete_portable_device_rules() {
        for path in [
            "CONIN$.txt",
            "CONOUT$/child",
            "COM\u{b9}.dll",
            "LPT\u{b3}/child",
            "CLOCK$",
        ] {
            assert!(!is_normalized_relative(path), "{path}");
        }
        assert!(is_normalized_relative("assets/readme.txt"));
    }

    #[test]
    fn pointer_json_omits_a_missing_previous_version() {
        let pointer = ClientActivationPointer {
            schema_version: 1,
            current_version: "1.0.0".into(),
            previous_version: None,
        };
        let text = pointer_json(&pointer);
        assert!(!text.contains("previousVersion"));
        let parsed = parse_pointer_bytes(text.as_bytes()).unwrap().unwrap();
        assert_eq!(parsed, pointer);
    }
}
