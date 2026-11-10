//! Installing, updating, removing and recovering launcher-managed plugins.
//! Install, update and a channel change run while clients play: they hold
//! the install's session lock shared, as a running client does (so a client
//! update or a move of the install cannot start underneath them), and the
//! plugin write lock (so two launchers never write plugins at once). The new
//! files are written into the plugin's own folder one at a time; a running
//! client read its module into memory, so nothing there is held open.
//! Remove and recovery still need every session closed and take the
//! exclusive lease. Installing never enables a plugin for any character and
//! never runs it.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::SystemTime;

use ac_host_core::PathSet;
use ac_host_core::paths::is_player_folder;

use super::catalog::PluginCatalog;
use super::inventory::{
    InstalledPluginSource, PluginInventory, evaluate_version_compatibility, find_block_reason,
};
use super::manifest::{
    InstallRules, PermissionDeclaration, PluginManifest, has_valid_install_id,
    is_newer_permission_vocabulary,
};
use super::record_store::{
    InstalledPluginRecord, InstalledPluginRecordStore, PendingPluginInstall, PluginInstallSource,
    PluginReleaseChannel,
};
use super::release::{MAXIMUM_DOCUMENT_BYTES, PluginReleaseClient, PluginReleaseFetchResult};
use super::rules::{
    ICON_FILE_NAME, PluginSha256File, github, validate_icon, validate_plugin_content,
};
use crate::cancel::CancelToken;
use crate::fsutil;
use crate::ids;
use crate::net::HttpTransport;
use crate::updates::{
    ClientVersionResolution, LauncherVersion, SafeZipExtractor, UpdateError, UpdateSessionBarrier,
    VerifiedArtifactDownloader,
};
use crate::url::WebUrl;

/// The refusal when removing a plugin while a session (or an update)
/// holds the barrier.
pub const SESSION_LEASE_REFUSAL: &str = "Close all OpenAC sessions to remove plugins.";

/// The refusal when an install, update or channel change finds a client
/// update, a move of the install folder or a plugin removal running.
pub const UPDATE_IN_PROGRESS_REFUSAL: &str =
    "A client update or another launcher task is running. Try again when it has finished.";

/// The refusal when another launcher is installing or updating a plugin.
pub const PLUGIN_WRITE_BUSY_REFUSAL: &str =
    "Another plugin install or update is running. Try again when it has finished.";

/// The lock file, beside the session lock, one plugin write at a time holds.
pub const PLUGIN_WRITE_LOCK_FILE_NAME: &str = ".plugin-write.lock";

/// The refusal for a permission vocabulary newer than this launcher's.
pub const PERMISSION_VOCABULARY_REFUSAL: &str = "This plugin needs a newer launcher than the one installed. Update the launcher, then try again.";

/// The refusal when the release's permissions differ from what the player
/// reviewed.
pub const PERMISSIONS_CHANGED_REFUSAL: &str =
    "This plugin changed since you reviewed it. Check it again before installing.";

/// The refusal of an install or update while no client is installed: which
/// plugins can run (the client versions and plugin API versions a plugin
/// needs) is the installed client's to say.
pub const CLIENT_NEEDED_REFUSAL: &str = "No client is installed, so nothing can say whether \
     this plugin would run. Install the client (Check for updates), then try again.";

/// Refuses a plugin the installed client cannot run, or any plugin when no
/// client is installed.
fn refuse_incompatible(
    manifest: &PluginManifest,
    client: Option<&ClientVersionResolution>,
) -> Result<(), UpdateError> {
    if client.and_then(|client| client.version.as_ref()).is_none() {
        return Err(UpdateError::failed(CLIENT_NEEDED_REFUSAL));
    }
    match evaluate_version_compatibility(manifest, client) {
        Some(reason) => Err(UpdateError::failed(format!("'{}' {reason}.", manifest.id))),
        None => Ok(()),
    }
}

/// The refusal when a package ships its own top-level `data` or `vtank`
/// folder.
pub const PACKAGED_FILES_REFUSAL: &str = "This plugin's package contains a top-level 'data' or \
     'vtank' folder. Those names are kept for the player's own saved files, so the launcher \
     will not install it.";

/// The folder inside a plugin's folder that holds the player's files for it.
const FILES_FOLDER_NAME: &str = ac_host_core::paths::PLUGIN_FILES_FOLDER_NAME;

/// The player's folders (see [`ac_host_core::paths::is_player_folder`]) directly
/// in `directory`, by name.
fn player_folders(directory: &Path) -> Vec<std::ffi::OsString> {
    std::fs::read_dir(directory)
        .map(|entries| {
            entries
                .flatten()
                .filter(|entry| entry.file_type().is_ok_and(|kind| kind.is_dir()))
                .map(|entry| entry.file_name())
                .filter(|name| is_player_folder(&name.to_string_lossy()))
                .collect()
        })
        .unwrap_or_default()
}

/// What runs right after an update set the old version's files aside and
/// before the new ones go in.
pub type AfterOldCodeSetAside = Box<dyn Fn() -> std::io::Result<()> + Send + Sync>;

/// The release contract's caps, set in one place.
pub mod contract_limits {
    use crate::updates::SafeZipExtractionLimits;

    /// Largest plugin archive.
    pub const MAXIMUM_ZIP_BYTES: u64 = 64 * 1024 * 1024;

    /// The extraction limits for a plugin archive (also applied to direct
    /// installs).
    pub fn extraction() -> SafeZipExtractionLimits {
        SafeZipExtractionLimits {
            maximum_entries: 2_000,
            maximum_entry_bytes: 64 * 1024 * 1024,
            maximum_total_bytes: 256 * 1024 * 1024,
            maximum_compression_ratio: 200.0,
            maximum_relative_path_length: 512,
        }
    }
}

/// Details read from a local plugin archive before the player confirms
/// installing it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LocalPluginZipPreview {
    /// Its manifest.
    pub manifest: PluginManifest,
    /// The SHA-256 of the file reviewed, uppercase hex.
    pub sha256: String,
}

fn conflicting(id: &str) -> UpdateError {
    UpdateError::failed(format!("'{id}' already has a conflicting installation."))
}

/// What an install did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginInstallResult {
    /// The plugin id.
    pub id: String,
    /// The version installed.
    pub version: String,
    /// Whether it replaced an older managed copy.
    pub was_update: bool,
}

/// The plugin installer.
pub struct PluginInstaller {
    paths: PathSet,
    release_client: PluginReleaseClient,
    downloader: VerifiedArtifactDownloader,
    extractor: SafeZipExtractor,
    records: Arc<Mutex<InstalledPluginRecordStore>>,
    inventory: PluginInventory,
    barrier: UpdateSessionBarrier,
    after_old_code_set_aside: Option<AfterOldCodeSetAside>,
}

/// What a plugin write holds: the install's session lock shared and the
/// plugin write lock alone; both go when it is dropped.
#[derive(Debug)]
struct PluginWriteLease {
    _write_lock: std::fs::File,
    _session: crate::updates::SessionLease,
}

impl std::fmt::Debug for PluginInstaller {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PluginInstaller")
            .field("paths", &self.paths)
            .finish_non_exhaustive()
    }
}

fn require_success(
    result: PluginReleaseFetchResult,
    unavailable: &str,
) -> Result<super::release::PluginReleaseDocument, UpdateError> {
    match result {
        PluginReleaseFetchResult::Success(document) => Ok(document),
        PluginReleaseFetchResult::RateLimited => {
            Err(UpdateError::failed("GitHub is rate limiting; try later."))
        }
        PluginReleaseFetchResult::Unavailable => Err(UpdateError::failed(unavailable)),
    }
}

fn tagged(repo: &str, tag: &str, asset: &str) -> Result<WebUrl, UpdateError> {
    github::tagged_asset(repo, tag, asset)
        .ok_or_else(|| UpdateError::failed(format!("'{repo}' is not an 'owner/name' repository.")))
}

fn io(error: std::io::Error) -> UpdateError {
    UpdateError::io(error.to_string(), error)
}

/// Same declarations (permission, note and network hosts) in any order.
pub fn permissions_match(
    displayed: &[PermissionDeclaration],
    actual: &[PermissionDeclaration],
) -> bool {
    if displayed.len() != actual.len() {
        return false;
    }
    let sorted = |declarations: &[PermissionDeclaration]| {
        let mut entries: Vec<(ac_plugin_version::Permission, String, Vec<String>)> = declarations
            .iter()
            .map(|declaration| {
                let mut hosts = declaration.hosts.clone();
                hosts.sort();
                (declaration.permission, declaration.note.clone(), hosts)
            })
            .collect();
        entries.sort();
        entries
    };
    sorted(displayed) == sorted(actual)
}

impl PluginInstaller {
    /// An installer for `paths`, fetching through `transport`, sharing the
    /// record store with the rest of the launcher.
    pub fn new(
        paths: &PathSet,
        transport: Arc<dyn HttpTransport>,
        records: Arc<Mutex<InstalledPluginRecordStore>>,
    ) -> Self {
        Self {
            paths: paths.clone(),
            release_client: PluginReleaseClient::new(Arc::clone(&transport)),
            downloader: VerifiedArtifactDownloader::new(transport),
            extractor: SafeZipExtractor::new(contract_limits::extraction(), true),
            records,
            inventory: PluginInventory::new(paths),
            barrier: UpdateSessionBarrier::new(&paths.data_directory),
            after_old_code_set_aside: None,
        }
    }

    /// Runs `hook` right after an update set the old version's files aside
    /// and before the new ones go in; an error from it fails the update
    /// there. A test stands in for a write failing at that moment with it.
    pub fn set_after_old_code_set_aside(&mut self, hook: AfterOldCodeSetAside) {
        self.after_old_code_set_aside = Some(hook);
    }

    fn records(&self) -> MutexGuard<'_, InstalledPluginRecordStore> {
        self.records
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    fn plugins_directory(&self) -> PathBuf {
        self.paths.plugins_directory()
    }

    fn exclusive(&self) -> Result<crate::updates::ExclusiveLease, UpdateError> {
        self.barrier
            .try_acquire_exclusive()?
            .ok_or_else(|| UpdateError::failed(SESSION_LEASE_REFUSAL))
    }

    /// The locks a plugin write holds: the install's session lock shared,
    /// as a running client holds it, so nothing that rewrites the whole
    /// install can start meanwhile; and the plugin write lock alone, so no
    /// other launcher writes plugins at the same time.
    fn acquire_plugin_write(&self) -> Result<PluginWriteLease, UpdateError> {
        let session = self
            .barrier
            .try_acquire_session()?
            .ok_or_else(|| UpdateError::failed(UPDATE_IN_PROGRESS_REFUSAL))?;
        let busy = |error: std::io::Error| UpdateError::io(PLUGIN_WRITE_BUSY_REFUSAL, error);
        let lock_path = self
            .barrier
            .lock_path()
            .parent()
            .ok_or_else(|| UpdateError::failed("The session lock path has no parent folder."))?
            .join(PLUGIN_WRITE_LOCK_FILE_NAME);
        let file = std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(&lock_path)
            .map_err(busy)?;
        match file.try_lock() {
            Ok(()) => Ok(PluginWriteLease {
                _write_lock: file,
                _session: session,
            }),
            Err(std::fs::TryLockError::WouldBlock) => {
                Err(busy(std::io::Error::from(std::io::ErrorKind::WouldBlock)))
            }
            Err(std::fs::TryLockError::Error(error)) => Err(busy(error)),
        }
    }

    /// Installs a new plugin, or updates the managed one from the same
    /// repository, at exactly `tag` (never re-resolving latest).
    /// `displayed_permissions`, when given, must match the release's own
    /// (the consent the player saw: what the client will let the plugin
    /// do); `channel`, when given, wins over the channel inferred from the
    /// version.
    #[allow(clippy::too_many_arguments)]
    pub fn install_or_update(
        &self,
        repo: &str,
        tag: &str,
        catalog: Option<&PluginCatalog>,
        client_resolution: Option<&ClientVersionResolution>,
        displayed_permissions: Option<&[PermissionDeclaration]>,
        channel: Option<PluginReleaseChannel>,
        cancel: &CancelToken,
    ) -> Result<PluginInstallResult, UpdateError> {
        if repo.trim().is_empty() || tag.trim().is_empty() {
            return Err(UpdateError::failed("A repository and a tag are required."));
        }
        let manifest_document = require_success(
            self.release_client.fetch_document(
                &tagged(repo, tag, "plugin.json")?,
                MAXIMUM_DOCUMENT_BYTES,
                cancel,
            )?,
            "The plugin release is unavailable.",
        )?;
        let manifest_bytes = manifest_document
            .content
            .strip_prefix(b"\xEF\xBB\xBF")
            .unwrap_or(&manifest_document.content);
        let manifest = PluginManifest::parse(&String::from_utf8_lossy(manifest_bytes))
            .and_then(|manifest| manifest.validate_for_install().map(|()| manifest))
            .map_err(|error| {
                if is_newer_permission_vocabulary(&error) {
                    UpdateError::failed(PERMISSION_VOCABULARY_REFUSAL)
                } else {
                    UpdateError::failed(format!("The plugin manifest is invalid: {error}"))
                }
            })?;
        if let Some(displayed) = displayed_permissions
            && !permissions_match(displayed, &manifest.permissions)
        {
            return Err(UpdateError::failed(PERMISSIONS_CHANGED_REFUSAL));
        }
        if !manifest.matches_tag(tag) {
            return Err(UpdateError::failed(format!(
                "The release tag does not match plugin version {}.",
                manifest.version
            )));
        }
        let plugin_version = LauncherVersion::try_parse(&manifest.version).ok_or_else(|| {
            UpdateError::failed(format!(
                "version '{}' is not a valid SemVer 2.0 version",
                manifest.version
            ))
        })?;
        if let Some(reason) = find_block_reason(catalog, &manifest.id, &plugin_version) {
            return Err(UpdateError::failed(format!(
                "'{}' is blocked: {reason}",
                manifest.id
            )));
        }
        refuse_incompatible(&manifest, client_resolution)?;

        let existing = self.records().find(&manifest.id).cloned();
        let is_update = existing
            .as_ref()
            .is_some_and(|record| record.repo.eq_ignore_ascii_case(repo));
        if let Some(existing) = &existing
            && !is_update
        {
            return Err(UpdateError::failed(format!(
                "'{}' is already installed from '{}'.",
                manifest.id, existing.repo
            )));
        }
        if !is_update {
            let other = {
                let records = self.records();
                self.inventory
                    .find(&manifest.id, &records, client_resolution, catalog)
            };
            if let Some(other) = other {
                return Err(UpdateError::failed(format!(
                    "'{}' is already present as a {} plugin.",
                    manifest.id,
                    other.source.describe()
                )));
            }
        }
        let target_directory = self.plugins_directory().join(&manifest.id);
        refuse_unmanaged_folder(existing.as_ref(), &manifest.id, &target_directory)?;
        if is_update {
            let current = existing
                .as_ref()
                .and_then(|record| record.version.as_deref())
                .and_then(LauncherVersion::try_parse)
                .ok_or_else(|| {
                    UpdateError::failed(format!(
                        "'{}' has no confirmed installed version to update. Restart the \
                         launcher to recover it first.",
                        manifest.id
                    ))
                })?;
            if plugin_version <= current {
                return Err(UpdateError::failed(format!(
                    "'{}' {plugin_version} is not newer than the installed {current}.",
                    manifest.id
                )));
            }
        }

        let zip_name = format!("{}-{}.zip", manifest.id, manifest.version);
        let sha_document = require_success(
            self.release_client.fetch_document(
                &tagged(repo, tag, &format!("{zip_name}.sha256"))?,
                MAXIMUM_DOCUMENT_BYTES,
                cancel,
            )?,
            "The plugin release is unavailable.",
        )?;
        let sha_file = PluginSha256File::parse(&String::from_utf8_lossy(&sha_document.content))?;
        sha_file.require_matches(&zip_name)?;

        let zip_path = self
            .paths
            .cache_directory
            .join("plugin-downloads")
            .join(format!("{}-{}.zip", manifest.id, ids::unique_hex32()));
        let staging_root = self.plugins_directory().join(".staging");
        let staging_directory =
            staging_root.join(format!("{}-{}", manifest.id, ids::unique_hex32()));
        let result = (|| {
            self.downloader.download_capped(
                &tagged(repo, tag, &zip_name)?,
                &sha_file.sha256,
                contract_limits::MAXIMUM_ZIP_BYTES,
                &zip_path,
                &mut |_| {},
                cancel,
            )?;
            let extracted = self
                .extractor
                .extract(&zip_path, &staging_directory, &[], cancel)?;
            validate_plugin_content(&extracted, &manifest.entry)?;
            refuse_packaged_files_folder(&staging_directory)?;
            let zip_manifest = std::fs::read(staging_directory.join("plugin.json")).map_err(io)?;
            if zip_manifest != manifest_document.content {
                return Err(UpdateError::failed(
                    "The plugin archive's plugin.json does not match the published release.",
                ));
            }
            let zip_icon_path = staging_directory.join(ICON_FILE_NAME);
            if zip_icon_path.is_file() {
                let zip_icon = std::fs::read(&zip_icon_path).map_err(io)?;
                validate_icon(&zip_icon)?;
                let icon_document = require_success(
                    self.release_client.fetch_document(
                        &tagged(repo, tag, ICON_FILE_NAME)?,
                        MAXIMUM_DOCUMENT_BYTES,
                        cancel,
                    )?,
                    "The release is missing its icon.png asset.",
                )?;
                if zip_icon != icon_document.content {
                    return Err(UpdateError::failed(
                        "The plugin archive's icon.png does not match the published release.",
                    ));
                }
            }
            let _write = self.acquire_plugin_write()?;
            // Asked again now that no other launcher can change the folder: a
            // copy placed by hand while the download ran must not be replaced
            // as if it were the old version.
            refuse_unmanaged_folder(existing.as_ref(), &manifest.id, &target_directory)?;
            self.replace_in_place(
                &manifest.id,
                repo,
                catalog,
                &manifest.version,
                tag,
                &sha_file.sha256,
                plugin_version.is_pre_release(),
                existing.as_ref(),
                &staging_directory,
                &target_directory,
                channel,
                &manifest.entry,
            )?;
            Ok(PluginInstallResult {
                id: manifest.id.clone(),
                version: manifest.version.clone(),
                was_update: is_update,
            })
        })();
        fsutil::try_delete_file(&zip_path);
        // A staging folder holds the player's files only if a swap failed
        // part-way; they go back, or stay for recovery.
        reclaim_staging(&staging_directory, &target_directory);
        fsutil::try_delete_if_empty(&staging_root);
        result
    }

    /// Checks a local ZIP with the same extraction rules as a release
    /// package, before the player is asked to confirm. The hash pins the
    /// file the player reviewed to the file installed after confirmation.
    pub fn inspect_local_zip(
        &self,
        source: &Path,
        cancel: &CancelToken,
    ) -> Result<LocalPluginZipPreview, UpdateError> {
        let (archive, sha256) = self.snapshot_local_zip(source)?;
        let previews = self.paths.cache_directory.join("plugin-previews");
        let staging = previews.join(ids::unique_hex32());
        let result = self.extract_local_manifest(&archive, &staging, cancel);
        fsutil::try_delete_file(&archive);
        fsutil::try_delete_tree(&staging);
        fsutil::try_delete_if_empty(&previews);
        Ok(LocalPluginZipPreview {
            manifest: result?,
            sha256,
        })
    }

    /// Installs a reviewed local ZIP as a direct install, or updates the
    /// direct install of the same id. Never turns a launcher-managed or
    /// bundled plugin into a direct install, and never touches the player's folders.
    #[allow(clippy::too_many_arguments)]
    pub fn install_local_zip(
        &self,
        source: &Path,
        expected_sha256: &str,
        catalog: Option<&PluginCatalog>,
        client_resolution: Option<&ClientVersionResolution>,
        displayed_permissions: &[PermissionDeclaration],
        cancel: &CancelToken,
    ) -> Result<PluginInstallResult, UpdateError> {
        if expected_sha256.trim().is_empty() {
            return Err(UpdateError::failed("The reviewed ZIP's hash is required."));
        }
        let (archive, sha256) = self.snapshot_local_zip(source)?;
        let staging_root = self.plugins_directory().join(".staging");
        let staging = staging_root.join(ids::unique_hex32());
        let mut target_directory: Option<PathBuf> = None;
        let result = (|| {
            if !sha256.eq_ignore_ascii_case(expected_sha256) {
                return Err(UpdateError::failed(
                    "The selected ZIP changed since you reviewed it. Select it again.",
                ));
            }
            let manifest = self.extract_local_manifest(&archive, &staging, cancel)?;
            if !permissions_match(displayed_permissions, &manifest.permissions) {
                return Err(UpdateError::failed(PERMISSIONS_CHANGED_REFUSAL));
            }
            let version = LauncherVersion::try_parse(&manifest.version).ok_or_else(|| {
                UpdateError::failed(format!(
                    "version '{}' is not a valid SemVer 2.0 version",
                    manifest.version
                ))
            })?;
            if let Some(reason) = find_block_reason(catalog, &manifest.id, &version) {
                return Err(UpdateError::failed(format!(
                    "'{}' is blocked: {reason}",
                    manifest.id
                )));
            }
            refuse_incompatible(&manifest, client_resolution)?;
            let target = self.plugins_directory().join(&manifest.id);
            target_directory = Some(target.clone());
            let _write = self.acquire_plugin_write()?;
            let is_update = self.validate_local_target(
                &manifest,
                &version,
                &target,
                client_resolution,
                catalog,
            )?;
            let journal =
                self.move_staged_code(&manifest.id, &staging, &target, &manifest.entry)?;
            self.finish_in_place(&journal);
            Ok(PluginInstallResult {
                id: manifest.id.clone(),
                version: manifest.version.clone(),
                was_update: is_update,
            })
        })();
        fsutil::try_delete_file(&archive);
        reclaim_staging(
            &staging,
            target_directory.as_deref().unwrap_or(Path::new("")),
        );
        fsutil::try_delete_if_empty(&staging_root);
        result
    }

    /// Whether a local ZIP may go to `target_directory`: a new direct
    /// install (false), or a newer version of the one direct install there
    /// (true).
    fn validate_local_target(
        &self,
        manifest: &PluginManifest,
        version: &LauncherVersion,
        target_directory: &Path,
        client_resolution: Option<&ClientVersionResolution>,
        catalog: Option<&PluginCatalog>,
    ) -> Result<bool, UpdateError> {
        let records = self.records();
        if records.find(&manifest.id).is_some() {
            return Err(UpdateError::failed(format!(
                "'{}' is managed by the launcher. Use its Update button.",
                manifest.id
            )));
        }
        let matches: Vec<_> = self
            .inventory
            .build(&records, client_resolution, catalog)
            .into_iter()
            .filter(|info| info.id.eq_ignore_ascii_case(&manifest.id))
            .collect();
        drop(records);
        let [installed] = matches.as_slice() else {
            if matches.is_empty() {
                if fsutil::is_link(target_directory).unwrap_or(false) {
                    return Err(UpdateError::failed(format!(
                        "'{}' has a linked plugin folder.",
                        manifest.id
                    )));
                }
                refuse_unmanaged_folder(None, &manifest.id, target_directory)?;
                return Ok(false);
            }
            return Err(conflicting(&manifest.id));
        };
        if installed.source != InstalledPluginSource::Direct
            || !fsutil::paths_equal(&installed.directory, target_directory)
            || installed.has_duplicate
            || installed.refusal.is_some()
        {
            return Err(conflicting(&manifest.id));
        }
        match LauncherVersion::try_parse(&installed.version) {
            Some(current) if *version > current => Ok(true),
            _ => Err(UpdateError::failed(format!(
                "'{}' {version} is not newer than the installed {}.",
                manifest.id, installed.version
            ))),
        }
    }

    /// Unpacks a local archive into `staging` and checks it as a release
    /// package: its `plugin.json`, its content, no packaged `data` or `vtank` folder,
    /// its icon.
    fn extract_local_manifest(
        &self,
        archive: &Path,
        staging: &Path,
        cancel: &CancelToken,
    ) -> Result<PluginManifest, UpdateError> {
        let extracted = self.extractor.extract(archive, staging, &[], cancel)?;
        let manifest_path = staging.join("plugin.json");
        if !manifest_path.is_file() {
            return Err(UpdateError::failed(
                "The plugin archive has no 'plugin.json' at its root.",
            ));
        }
        let bytes = std::fs::read(&manifest_path).map_err(io)?;
        let text = String::from_utf8_lossy(&bytes);
        let manifest = PluginManifest::parse(text.trim_start_matches('\u{feff}'))
            .and_then(|manifest| manifest.validate_for_install().map(|()| manifest))
            .map_err(|error| {
                if is_newer_permission_vocabulary(&error) {
                    UpdateError::failed(PERMISSION_VOCABULARY_REFUSAL)
                } else {
                    UpdateError::failed(format!("The plugin manifest is invalid: {error}"))
                }
            })?;
        validate_plugin_content(&extracted, &manifest.entry)?;
        refuse_packaged_files_folder(staging)?;
        let icon = staging.join(ICON_FILE_NAME);
        if icon.is_file() {
            validate_icon(&std::fs::read(&icon).map_err(io)?)?;
        }
        Ok(manifest)
    }

    /// Copies the player's ZIP into the cache (at most 64 MiB) and hashes
    /// the copy, so what is checked and installed is exactly what was read.
    fn snapshot_local_zip(&self, source: &Path) -> Result<(PathBuf, String), UpdateError> {
        let is_zip = source
            .extension()
            .is_some_and(|extension| extension.eq_ignore_ascii_case("zip"));
        if source.as_os_str().is_empty() || !is_zip {
            return Err(UpdateError::failed("Choose a plugin ZIP file."));
        }
        let archive = self
            .paths
            .cache_directory
            .join("plugin-downloads")
            .join(format!("local-{}.zip", ids::unique_hex32()));
        let copied = (|| -> Result<String, UpdateError> {
            use std::io::{Read as _, Write as _};
            std::fs::create_dir_all(archive.parent().unwrap_or(Path::new(""))).map_err(io)?;
            let mut input = std::fs::File::open(source).map_err(io)?;
            let mut output = std::fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&archive)
                .map_err(io)?;
            let mut buffer = vec![0u8; 128 * 1024];
            let mut total: u64 = 0;
            loop {
                let read = input.read(&mut buffer).map_err(io)?;
                if read == 0 {
                    break;
                }
                total += read as u64;
                if total > contract_limits::MAXIMUM_ZIP_BYTES {
                    return Err(UpdateError::failed("The plugin ZIP is larger than 64 MiB."));
                }
                output.write_all(&buffer[..read]).map_err(io)?;
            }
            output.flush().map_err(io)?;
            drop(output);
            crate::integrity::compute_sha256_hex(&archive).map_err(io)
        })();
        match copied {
            Ok(sha256) => Ok((archive, sha256.to_ascii_uppercase())),
            Err(error) => {
                fsutil::try_delete_file(&archive);
                Err(error)
            }
        }
    }

    /// Sets a managed plugin's channel; returns the updated record.
    pub fn set_channel(
        &self,
        id: &str,
        channel: PluginReleaseChannel,
    ) -> Result<InstalledPluginRecord, UpdateError> {
        let record = self.records().find(id).cloned().ok_or_else(|| {
            UpdateError::failed(format!("'{id}' is not a launcher-managed plugin."))
        })?;
        let _write = self.acquire_plugin_write()?;
        let updated = InstalledPluginRecord { channel, ..record };
        let mut records = self.records();
        records.upsert(updated.clone());
        records.save()?;
        Ok(updated)
    }

    /// Removes a managed plugin's code. Its private files in
    /// `plugins/<id>/data` stay unless `delete_storage` asks for them too;
    /// never the shared root and never the VTank profile folder.
    pub fn remove(&self, id: &str, delete_storage: bool) -> Result<(), UpdateError> {
        if self.records().find(id).is_none() {
            return Err(UpdateError::failed(format!(
                "'{id}' is not a launcher-managed plugin."
            )));
        }
        let _lease = self.exclusive()?;
        let target = self.plugins_directory().join(id);
        if target.is_dir() {
            self.remove_code_keeping_files(id, &target, delete_storage)?;
        }
        fsutil::try_delete_if_empty(&self.plugins_directory().join(".trash"));
        let mut records = self.records();
        records
            .records
            .retain(|record| !record.id.eq_ignore_ascii_case(id));
        records.save()?;
        Ok(())
    }

    /// Moves a plugin folder to the trash in one rename, hands the player's
    /// folders back to a fresh folder of the same name (its private files
    /// only unless asked to delete them; the shared VTank folder always),
    /// then deletes the trash. A crash between the renames leaves them in
    /// the trash, where recovery finds and returns them.
    fn remove_code_keeping_files(
        &self,
        trash_name: &str,
        plugin_directory: &Path,
        delete_files: bool,
    ) -> Result<(), UpdateError> {
        let trash = self.create_trash_path(trash_name)?;
        std::fs::rename(plugin_directory, &trash).map_err(io)?;
        for name in player_folders(&trash) {
            if delete_files
                && name
                    .to_string_lossy()
                    .eq_ignore_ascii_case(FILES_FOLDER_NAME)
            {
                continue;
            }
            std::fs::create_dir_all(plugin_directory).map_err(io)?;
            std::fs::rename(trash.join(&name), plugin_directory.join(&name)).map_err(io)?;
        }
        fsutil::try_delete_tree(&trash);
        Ok(())
    }

    /// Removes a direct install by its folder (never a managed or bundled
    /// plugin, never outside the plugins folder).
    pub fn remove_direct(&self, directory: &Path, delete_storage: bool) -> Result<(), UpdateError> {
        let _lease = self.exclusive()?;
        let row = {
            let records = self.records();
            self.inventory
                .build(&records, None, None)
                .into_iter()
                .find(|info| fsutil::paths_equal(&info.directory, directory))
        }
        .ok_or_else(|| UpdateError::failed("That plugin is no longer installed."))?;
        if row.source != InstalledPluginSource::Direct {
            return Err(UpdateError::failed(
                "Only a directly installed plugin can be removed this way.",
            ));
        }
        match row.directory.parent() {
            Some(parent) if fsutil::paths_equal(parent, &self.plugins_directory()) => {}
            _ => {
                return Err(UpdateError::failed(
                    "That plugin is not under the plugins folder.",
                ));
            }
        }
        let folder_name = row
            .directory
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default();
        if folder_name.starts_with('.') {
            return Err(UpdateError::failed("That folder is not a plugin install."));
        }
        self.remove_code_keeping_files(&folder_name, &row.directory, delete_storage)?;
        fsutil::try_delete_if_empty(&self.plugins_directory().join(".trash"));
        // A plugin keeps its files under its manifest id, which for a
        // hand-unzipped folder need not match the folder's own name.
        if delete_storage
            && has_valid_install_id(&row.id)
            && !row.id.eq_ignore_ascii_case(&folder_name)
        {
            fsutil::try_delete_tree(&self.paths.plugin_files_directory(&row.id));
            fsutil::try_delete_if_empty(&self.plugins_directory().join(&row.id));
        }
        Ok(())
    }

    /// Startup recovery: reclaims staging and downloads, restores or
    /// discards trash, and reconciles every pending record. Does nothing
    /// while the lease is held elsewhere.
    pub fn recover(&self) -> Result<(), UpdateError> {
        let Some(_lease) = self.barrier.try_acquire_exclusive()? else {
            return Ok(());
        };
        let plugins = self.plugins_directory();
        // The trash first: an interrupted update may have moved the old
        // folder there, and it has to be back in place before a staged copy
        // of the player's files can be returned into it.
        self.recover_trash()?;
        let staging_root = plugins.join(".staging");
        if staging_root.is_dir() {
            // Before any staging folder goes: an unfinished in-place update
            // keeps the old version's files beside its staging folder until
            // it is settled.
            self.recover_in_place_updates(&staging_root)?;
        }
        if let Ok(entries) = std::fs::read_dir(&staging_root) {
            let entries: Vec<_> = entries.flatten().map(|entry| entry.path()).collect();
            for staging in entries.iter().filter(|path| path.is_dir()) {
                let name = staging
                    .file_name()
                    .map(|name| name.to_string_lossy().into_owned())
                    .unwrap_or_default();
                reclaim_staging(staging, &plugins.join(id_from_trash_name(&name)));
            }
            // A marker whose staging folder is already gone says nothing.
            for marker in &entries {
                let name = marker.to_string_lossy();
                if let Some(staging) = name.strip_suffix(PLAYER_FILES_SUFFIX)
                    && marker.is_file()
                    && !Path::new(staging).is_dir()
                {
                    fsutil::try_delete_file(marker);
                }
            }
            fsutil::try_delete_if_empty(&staging_root);
        }
        if let Ok(entries) = std::fs::read_dir(self.paths.cache_directory.join("plugin-downloads"))
        {
            for entry in entries.flatten() {
                if entry.path().is_file() {
                    fsutil::try_delete_file(&entry.path());
                }
            }
        }
        let mut records = self.records();
        let mut changed = false;
        for record in records.records.clone() {
            let Some(pending) = &record.pending else {
                continue;
            };
            let manifest_path = plugins.join(&record.id).join("plugin.json");
            let actual = std::fs::read_to_string(&manifest_path)
                .ok()
                .and_then(|text| PluginManifest::parse(&text).ok())
                .map(|manifest| manifest.version);
            if actual.as_deref() == Some(pending.version.as_str()) {
                records.upsert(InstalledPluginRecord {
                    version: Some(pending.version.clone()),
                    tag: Some(pending.tag.clone()),
                    zip_sha256: Some(pending.zip_sha256.clone()),
                    pending: None,
                    ..record.clone()
                });
            } else if record.version.is_some() {
                records.upsert(InstalledPluginRecord {
                    pending: None,
                    ..record.clone()
                });
            } else {
                records
                    .records
                    .retain(|candidate| !candidate.id.eq_ignore_ascii_case(&record.id));
            }
            changed = true;
        }
        let before = records.records.len();
        records.records.retain(|record| {
            record.pending.is_some() || has_plugin_code(&plugins.join(&record.id))
        });
        changed |= records.records.len() != before;
        if changed {
            records.save()?;
        }
        Ok(())
    }

    /// Restores a managed plugin whose update or removal was interrupted
    /// with its folder in the trash, and otherwise returns any of the
    /// player's files the trash still holds before deleting it.
    fn recover_trash(&self) -> Result<(), UpdateError> {
        let plugins = self.plugins_directory();
        let trash_root = plugins.join(".trash");
        let Ok(entries) = std::fs::read_dir(&trash_root) else {
            return Ok(());
        };
        for entry in entries.flatten() {
            let trash = entry.path();
            if !trash.is_dir() {
                continue;
            }
            let name = entry.file_name().to_string_lossy().into_owned();
            let id = id_from_trash_name(&name);
            let original = plugins.join(id);
            // Record-less trash is a direct removal, never a managed one;
            // resurrecting it would undo a removal that already succeeded.
            if self.records().find(id).is_some() && !has_plugin_code(&original) {
                let kept = player_folders(&original);
                if kept.iter().any(|name| trash.join(name).exists()) {
                    // Two copies cannot arise from one swap; leave both
                    // for the player rather than pick one.
                    continue;
                }
                for name in kept {
                    std::fs::rename(original.join(&name), trash.join(&name)).map_err(io)?;
                }
                if original.is_dir() {
                    std::fs::remove_dir(&original).map_err(io)?;
                }
                std::fs::rename(&trash, &original).map_err(io)?;
            } else if return_staged_files(&trash, &original) {
                fsutil::try_delete_tree(&trash);
            }
        }
        fsutil::try_delete_if_empty(&trash_root);
        Ok(())
    }

    /// Writes a new version into the plugin's own folder while clients may
    /// be running it. The player's folders are never moved. The old
    /// version's files are set aside first and the new ones moved in, with
    /// `plugin.json` last, so a folder holding the new `plugin.json` holds
    /// all of the new version. A journal beside the staging folder names the
    /// old files until the swap is done: a failure puts them back, and so
    /// does [`Self::recover`] after a crash.
    #[allow(clippy::too_many_arguments)]
    fn replace_in_place(
        &self,
        id: &str,
        repo: &str,
        catalog: Option<&PluginCatalog>,
        new_version: &str,
        new_tag: &str,
        new_zip_sha256: &str,
        new_version_is_pre_release: bool,
        existing: Option<&InstalledPluginRecord>,
        staging_directory: &Path,
        target_directory: &Path,
        explicit_channel: Option<PluginReleaseChannel>,
        entry: &str,
    ) -> Result<(), UpdateError> {
        let pending = PendingPluginInstall {
            version: new_version.to_owned(),
            tag: new_tag.to_owned(),
            zip_sha256: new_zip_sha256.to_owned(),
        };
        // A pre-release lands on beta; a stable release keeps the existing
        // channel; the player's explicit choice wins over both.
        let channel = explicit_channel.unwrap_or(if new_version_is_pre_release {
            PluginReleaseChannel::Beta
        } else {
            existing.map_or(PluginReleaseChannel::Stable, |record| record.channel)
        });
        let pending_record = match existing {
            None => InstalledPluginRecord {
                id: id.to_owned(),
                repo: repo.to_owned(),
                source: determine_source(catalog, id, repo),
                version: None,
                tag: None,
                zip_sha256: None,
                installed_at: SystemTime::now(),
                pending: Some(pending),
                channel,
            },
            Some(record) => InstalledPluginRecord {
                pending: Some(pending),
                channel,
                ..record.clone()
            },
        };
        {
            let mut records = self.records();
            records.upsert(pending_record.clone());
            records.save()?;
        }
        let journal = self.move_staged_code(id, staging_directory, target_directory, entry)?;
        {
            let mut records = self.records();
            records.upsert(InstalledPluginRecord {
                version: Some(new_version.to_owned()),
                tag: Some(new_tag.to_owned()),
                zip_sha256: Some(new_zip_sha256.to_owned()),
                pending: None,
                ..pending_record
            });
            records.save()?;
        }
        self.finish_in_place(&journal);
        Ok(())
    }

    /// Sets the old version's code aside and moves the new code in, the
    /// entry module next to last and `plugin.json` last; a failure puts the
    /// old version back. Returns the journal, which the caller finishes.
    fn move_staged_code(
        &self,
        id: &str,
        staging_directory: &Path,
        target_directory: &Path,
        entry: &str,
    ) -> Result<PathBuf, UpdateError> {
        std::fs::create_dir_all(target_directory).map_err(io)?;
        let old_code = code_files(target_directory);
        let mut new_code = code_files(staging_directory);
        new_code.sort_by(|left, right| {
            install_order(left, entry)
                .cmp(&install_order(right, entry))
                .then_with(|| left.cmp(right))
        });
        let journal = in_place_journal(staging_directory);
        let set_aside = set_aside_directory(staging_directory);
        let mut lines = vec![id.to_owned()];
        lines.extend(old_code.iter().cloned());
        std::fs::write(&journal, lines.join("\n") + "\n").map_err(io)?;
        let moved = (|| -> std::io::Result<()> {
            for file in &old_code {
                move_file(&target_directory.join(file), &set_aside.join(file))?;
            }
            if let Some(hook) = &self.after_old_code_set_aside {
                hook()?;
            }
            for file in &new_code {
                move_file(&staging_directory.join(file), &target_directory.join(file))?;
            }
            Ok(())
        })();
        if let Err(error) = moved {
            self.put_back(&journal).map_err(io)?;
            return Err(UpdateError::io(
                format!(
                    "'{id}' could not be updated: {error} The previous version is back in place."
                ),
                error,
            ));
        }
        Ok(journal)
    }

    /// Undoes an unfinished in-place update: every old file set aside goes
    /// back, and every file the old version did not have leaves the
    /// plugin's folder. It can run again after being interrupted itself,
    /// because it only ever moves what is still set aside. The player's
    /// folders are never touched.
    fn put_back(&self, journal: &Path) -> std::io::Result<()> {
        let text = std::fs::read_to_string(journal)?;
        let mut lines = text.lines();
        let id = lines.next().unwrap_or_default().to_owned();
        let old_code: std::collections::HashSet<&str> = lines.collect();
        let target_directory = self.plugins_directory().join(&id);
        let set_aside = set_aside_directory(&staging_of(journal));
        for file in code_files(&set_aside) {
            move_file(&set_aside.join(&file), &target_directory.join(&file))?;
        }
        for file in code_files(&target_directory) {
            if !old_code.contains(file.as_str()) {
                std::fs::remove_file(target_directory.join(&file))?;
            }
        }
        remove_empty_code_folders(&target_directory)?;
        fsutil::try_delete_tree(&set_aside);
        fsutil::try_delete_file(journal);
        Ok(())
    }

    /// Ends a finished in-place update: the old version's files and the
    /// journal go. Anything left set aside a later recovery deletes.
    fn finish_in_place(&self, journal: &Path) {
        let id = std::fs::read_to_string(journal)
            .ok()
            .and_then(|text| text.lines().next().map(str::to_owned))
            .unwrap_or_default();
        if !id.is_empty() {
            let _ = remove_empty_code_folders(&self.plugins_directory().join(&id));
        }
        fsutil::try_delete_tree(&set_aside_directory(&staging_of(journal)));
        fsutil::try_delete_file(journal);
    }

    /// Settles every in-place update a crash left unfinished. One whose new
    /// `plugin.json` had already gone in is finished; any other is put back
    /// to the old version.
    fn recover_in_place_updates(&self, staging_root: &Path) -> Result<(), UpdateError> {
        let Ok(entries) = std::fs::read_dir(staging_root) else {
            return Ok(());
        };
        let journals: Vec<PathBuf> = entries
            .flatten()
            .map(|entry| entry.path())
            .filter(|path| {
                path.is_file()
                    && path
                        .file_name()
                        .is_some_and(|name| name.to_string_lossy().ends_with(IN_PLACE_SUFFIX))
            })
            .collect();
        for journal in journals {
            let staging = staging_of(&journal);
            let id = std::fs::read_to_string(&journal)
                .ok()
                .and_then(|text| text.lines().next().map(str::to_owned))
                .unwrap_or_default();
            if id.trim().is_empty() {
                fsutil::try_delete_file(&journal);
                continue;
            }
            let new_manifest_is_in = !staging.join("plugin.json").exists()
                && self
                    .plugins_directory()
                    .join(&id)
                    .join("plugin.json")
                    .exists()
                && staging.is_dir();
            if new_manifest_is_in {
                self.finish_in_place(&journal);
            } else {
                self.put_back(&journal).map_err(io)?;
            }
        }
        Ok(())
    }

    fn create_trash_path(&self, id: &str) -> Result<PathBuf, UpdateError> {
        let trash_root = self.plugins_directory().join(".trash");
        std::fs::create_dir_all(&trash_root).map_err(io)?;
        Ok(trash_root.join(format!("{id}-{}", ids::unique_hex32())))
    }
}

fn refuse_unmanaged_folder(
    existing: Option<&InstalledPluginRecord>,
    id: &str,
    target_directory: &Path,
) -> Result<(), UpdateError> {
    // A folder holding nothing but the player's folders is what removing a plugin's code
    // leaves behind; installing the plugin again picks its files up.
    if existing.is_some() || !has_plugin_code(target_directory) {
        return Ok(());
    }
    // The full path stays out of the player-facing message; the cause keeps it.
    Err(UpdateError::io(
        format!(
            "A folder named {id} is already in your plugins folder, and the launcher didn't \
             install it. Move or delete that folder, then try again."
        ),
        std::io::Error::new(
            std::io::ErrorKind::AlreadyExists,
            format!(
                "'{}' already exists and is not a launcher-managed plugin.",
                target_directory.display()
            ),
        ),
    ))
}

/// The end of the marker file's name beside a staging folder.
const PLAYER_FILES_SUFFIX: &str = ".player-files";

/// The end of an in-place update's journal's name beside its staging folder.
const IN_PLACE_SUFFIX: &str = ".in-place";

/// The end of the folder's name, beside the staging folder, that keeps the
/// old version's files until an in-place update is done.
const SET_ASIDE_SUFFIX: &str = ".old";

/// The journal of an in-place update: the plugin id, then the old version's
/// files, one per line.
fn in_place_journal(staging_directory: &Path) -> PathBuf {
    let mut journal = staging_directory.as_os_str().to_owned();
    journal.push(IN_PLACE_SUFFIX);
    PathBuf::from(journal)
}

/// Where an in-place update keeps the old version's files until it is done.
fn set_aside_directory(staging_directory: &Path) -> PathBuf {
    let mut folder = staging_directory.as_os_str().to_owned();
    folder.push(SET_ASIDE_SUFFIX);
    PathBuf::from(folder)
}

/// The staging folder a journal belongs to.
fn staging_of(journal: &Path) -> PathBuf {
    let text = journal.as_os_str().to_string_lossy();
    PathBuf::from(text.strip_suffix(IN_PLACE_SUFFIX).unwrap_or(&text))
}

/// Every file of a plugin's code, relative to its folder with `/`
/// separators and in ordinal order: everything but the player's top-level
/// `data` and `vtank` folders.
fn code_files(directory: &Path) -> Vec<String> {
    let mut files = Vec::new();
    let mut pending = vec![directory.to_path_buf()];
    while let Some(folder) = pending.pop() {
        let Ok(entries) = std::fs::read_dir(&folder) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let Ok(kind) = entry.file_type() else {
                continue;
            };
            if kind.is_dir() {
                pending.push(path);
            } else if let Ok(relative) = path.strip_prefix(directory) {
                let relative = relative
                    .components()
                    .map(|component| component.as_os_str().to_string_lossy().into_owned())
                    .collect::<Vec<_>>()
                    .join("/");
                if !is_in_player_folder(&relative) {
                    files.push(relative);
                }
            }
        }
    }
    files.sort();
    files
}

/// Whether a `/`-separated path relative to a plugin's folder lies inside
/// one of the player's folders.
fn is_in_player_folder(relative: &str) -> bool {
    relative
        .split_once('/')
        .is_some_and(|(first, _)| is_player_folder(first))
}

/// The new version's files go in with its entry module next to last and
/// `plugin.json` last: a client reloads when either changes, and by then
/// everything it needs is there.
fn install_order(file: &str, entry: &str) -> u8 {
    if file.eq_ignore_ascii_case("plugin.json") {
        2
    } else if file.eq_ignore_ascii_case(&entry.replace('\\', "/")) {
        1
    } else {
        0
    }
}

/// Moves a file, making its folder and replacing what is there.
fn move_file(source: &Path, destination: &Path) -> std::io::Result<()> {
    if let Some(parent) = destination.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::rename(source, destination)
}

/// Removes folders the old version had and the new one left empty; the
/// player's folders and what is inside them stay.
fn remove_empty_code_folders(plugin_directory: &Path) -> std::io::Result<()> {
    if !plugin_directory.is_dir() {
        return Ok(());
    }
    let mut folders = Vec::new();
    let mut pending = vec![plugin_directory.to_path_buf()];
    while let Some(folder) = pending.pop() {
        let Ok(entries) = std::fs::read_dir(&folder) else {
            continue;
        };
        for entry in entries.flatten() {
            if entry.file_type().is_ok_and(|kind| kind.is_dir()) {
                folders.push(entry.path());
                pending.push(entry.path());
            }
        }
    }
    folders.sort_by_key(|folder| std::cmp::Reverse(folder.as_os_str().len()));
    for folder in folders {
        let relative = folder
            .strip_prefix(plugin_directory)
            .map(|relative| {
                relative
                    .components()
                    .map(|component| component.as_os_str().to_string_lossy().into_owned())
                    .collect::<Vec<_>>()
                    .join("/")
            })
            .unwrap_or_default();
        if is_player_folder(&relative) || is_in_player_folder(&relative) {
            continue;
        }
        if std::fs::read_dir(&folder)?.next().is_none() {
            std::fs::remove_dir(&folder)?;
        }
    }
    Ok(())
}

/// The plugin id a trash or staging folder name `<id>-<hex>` was made for.
fn id_from_trash_name(name: &str) -> &str {
    match name.rfind('-') {
        Some(dash) if dash > 0 => &name[..dash],
        _ => name,
    }
}

/// Refuses a package that ships its own top-level `data` or `vtank`
/// folder: those names inside a plugin's folder belong to the player's
/// files, and an update carries the old ones into the new version.
fn refuse_packaged_files_folder(staging_directory: &Path) -> Result<(), UpdateError> {
    let entries = std::fs::read_dir(staging_directory).map_err(io)?;
    for entry in entries {
        let entry = entry.map_err(io)?;
        if is_player_folder(&entry.file_name().to_string_lossy()) {
            return Err(UpdateError::failed(PACKAGED_FILES_REFUSAL));
        }
    }
    Ok(())
}

/// Whether a plugin folder holds anything besides the player's folders.
fn has_plugin_code(plugin_directory: &Path) -> bool {
    std::fs::read_dir(plugin_directory).is_ok_and(|entries| {
        entries
            .flatten()
            .any(|entry| !is_player_folder(&entry.file_name().to_string_lossy()))
    })
}

/// The file beside a staging folder that says its player's folders hold
/// the player's files rather than anything the package brought. It sits
/// outside the staging folder, so no package can ship it.
fn player_files_marker(staging_directory: &Path) -> PathBuf {
    let mut marker = staging_directory.as_os_str().to_owned();
    marker.push(PLAYER_FILES_SUFFIX);
    PathBuf::from(marker)
}

/// Returns the player's folders a swap staged beside new code to their
/// plugin folder. True when nothing of the player's is left in
/// `staging_directory`.
fn return_staged_files(staging_directory: &Path, plugin_directory: &Path) -> bool {
    let mut all_returned = true;
    for name in player_folders(staging_directory) {
        let target = plugin_directory.join(&name);
        // Left where it is on failure; the next recovery tries again.
        let returned = !target.exists()
            && std::fs::create_dir_all(plugin_directory).is_ok()
            && std::fs::rename(staging_directory.join(&name), &target).is_ok();
        all_returned &= returned;
    }
    all_returned
}

/// Hands a staging folder's player folders back only when the marker says they are
/// the player's, then deletes the staging folder and its marker. Leaves
/// both when the files could not be handed back.
fn reclaim_staging(staging_directory: &Path, plugin_directory: &Path) {
    let marker = player_files_marker(staging_directory);
    if marker.is_file() && !return_staged_files(staging_directory, plugin_directory) {
        return;
    }
    fsutil::try_delete_tree(staging_directory);
    fsutil::try_delete_file(&marker);
}

fn determine_source(catalog: Option<&PluginCatalog>, id: &str, repo: &str) -> PluginInstallSource {
    let listed = catalog.is_some_and(|catalog| {
        catalog
            .entries_for_this_client()
            .any(|entry| entry.id.eq_ignore_ascii_case(id) && entry.repo.eq_ignore_ascii_case(repo))
    });
    if listed {
        PluginInstallSource::Listed
    } else {
        PluginInstallSource::Unlisted
    }
}
