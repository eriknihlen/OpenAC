//! The launcher's own update. A newer launcher is downloaded, verified and
//! extracted into `<root>/app/launcher-update/transactions/<id>/payload` and a
//! plan (`<root>/app/launcher-update/pending.json`) records it as staged. On the
//! next start the staged launcher runs as a helper: it waits for the old
//! launcher to exit, applies the plan file by file (each change journaled so
//! a crash at any point rolls back to the exact prior files), starts the new
//! launcher, which confirms it runs, and finishes the transaction. Anything
//! that does not verify rolls back and starts the old launcher again.
//!
//! On macOS the launcher lives in `OpenAC.app` and the whole bundle is
//! swapped by two directory renames instead.

use std::collections::{BTreeMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::Arc;

use ac_host_core::PathSet;
use ac_host_core::json::{self as host_json, Node, ObjectReader};

use super::UpdateError;
use super::barrier::{ExclusiveLease, UpdateSessionBarrier};
use super::client_store::{
    InstalledFileRecord, is_normalized_relative, reject_link_tree, resolve_contained,
    validate_required_executables,
};
use super::downloader::{ArtifactDownloadProgress, VerifiedArtifactDownloader};
use super::manifest::{MAXIMUM_ARTIFACT_BYTES, ReleaseArtifact, ReleaseManifest, is_sha256};
use super::payload::{LAUNCHER, executables_for_payload, is_valid_rid, suffix_for_rid};
use super::platform;
use super::version::LauncherVersion;
use super::zip::{ExtractedFileRecord, SafeZipExtractor, try_delete_directory};
use crate::cancel::CancelToken;
use crate::fsutil;
use crate::ids;
use crate::integrity;
use crate::json::{self, ObjectBuilder, Out};
use crate::net::HttpTransport;

/// The ownership record a self-update writes into the launcher directory.
pub const INSTALL_RECORD_FILE_NAME: &str = "launcher.install.json";

/// The plan's schema version.
pub const PLAN_SCHEMA_VERSION: i32 = 4;

/// The ownership record's schema version.
pub const INSTALL_RECORD_SCHEMA_VERSION: i32 = 1;

/// The macOS application bundle's name.
pub const MAC_BUNDLE_NAME: &str = "OpenAC.app";

const TARGET_TRANSACTION_PREFIX: &str = ".acdream-self-update-";

/// How the launcher is installed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LauncherInstallationKind {
    /// A folder holding the launcher and its files.
    Flat,
    /// `OpenAC.app` on macOS.
    MacBundle,
}

impl LauncherInstallationKind {
    fn number(self) -> i64 {
        match self {
            Self::Flat => 0,
            Self::MacBundle => 1,
        }
    }
}

/// Where the running launcher is installed and where its executable sits in
/// a launcher payload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherInstallationLayout {
    /// Flat folder or app bundle.
    pub kind: LauncherInstallationKind,
    /// The folder, or the `OpenAC.app` directory.
    pub installed_root: PathBuf,
    /// The payload's top folder (`OpenAC.app`), empty for a flat payload.
    pub payload_root: String,
    /// The launcher executable relative to the installed root.
    pub launcher_relative_path: String,
}

fn normalize_directory(path: &Path) -> Result<PathBuf, UpdateError> {
    if path.as_os_str().is_empty() || !path.is_absolute() {
        return Err(UpdateError::failed(
            "The launcher installation directory must be absolute.",
        ));
    }
    Ok(fsutil::full_path(path))
}

fn launcher_file_name(rid: &str) -> String {
    format!("{LAUNCHER}{}", suffix_for_rid(rid))
}

impl LauncherInstallationLayout {
    /// A flat installation in `installed_root`.
    pub fn flat(installed_root: &Path, rid: &str) -> Result<Self, UpdateError> {
        Ok(Self {
            kind: LauncherInstallationKind::Flat,
            installed_root: normalize_directory(installed_root)?,
            payload_root: String::new(),
            launcher_relative_path: launcher_file_name(rid),
        })
    }

    /// The app bundle at `installed_root`, which must be named `OpenAC.app`.
    pub fn mac_bundle(installed_root: &Path, rid: &str) -> Result<Self, UpdateError> {
        let root = normalize_directory(installed_root)?;
        if root.file_name().and_then(|name| name.to_str()) != Some(MAC_BUNDLE_NAME) {
            return Err(UpdateError::failed(
                "The macOS launcher must run from OpenAC.app.",
            ));
        }
        Ok(Self {
            kind: LauncherInstallationKind::MacBundle,
            installed_root: root,
            payload_root: MAC_BUNDLE_NAME.to_owned(),
            launcher_relative_path: format!("Contents/MacOS/{}", launcher_file_name(rid)),
        })
    }

    /// The layout of a launcher running from `app_base_directory` (the
    /// executable's folder): flat, except on macOS, where it must be
    /// `OpenAC.app/Contents/MacOS`.
    pub fn detect(app_base_directory: &Path, rid: &str) -> Result<Self, UpdateError> {
        let base = normalize_directory(app_base_directory)?;
        if !cfg!(target_os = "macos") {
            return Self::flat(&base, rid);
        }
        let name = |path: Option<&Path>| {
            path.and_then(Path::file_name)
                .and_then(|name| name.to_str())
                .map(str::to_owned)
        };
        let contents = base.parent();
        let bundle = contents.and_then(Path::parent);
        if name(Some(&base)).as_deref() != Some("MacOS")
            || name(contents).as_deref() != Some("Contents")
            || name(bundle).as_deref() != Some(MAC_BUNDLE_NAME)
        {
            return Err(UpdateError::failed(
                "The macOS launcher must run from OpenAC.app/Contents/MacOS.",
            ));
        }
        let bundle = bundle
            .ok_or_else(|| UpdateError::failed("The macOS app bundle path is incomplete."))?;
        Self::mac_bundle(bundle, rid)
    }

    /// The folder holding the installation's transaction folders: the
    /// installed folder itself, or the folder holding the app bundle.
    pub fn container_directory(&self) -> Result<PathBuf, UpdateError> {
        match self.kind {
            LauncherInstallationKind::MacBundle => self
                .installed_root
                .parent()
                .map(Path::to_path_buf)
                .ok_or_else(|| UpdateError::failed("The app bundle has no containing directory.")),
            LauncherInstallationKind::Flat => Ok(self.installed_root.clone()),
        }
    }

    /// The installed launcher executable.
    pub fn launcher_path(&self) -> Result<PathBuf, UpdateError> {
        resolve_contained(&self.installed_root, &self.launcher_relative_path)
    }

    /// The launcher executable's path inside a payload.
    pub fn payload_launcher_path(&self) -> String {
        if self.payload_root.is_empty() {
            self.launcher_relative_path.clone()
        } else {
            format!("{}/{}", self.payload_root, self.launcher_relative_path)
        }
    }

    /// The transaction folder beside the installation.
    pub fn sibling_transaction_directory(
        &self,
        transaction_id: &str,
    ) -> Result<PathBuf, UpdateError> {
        Ok(self
            .container_directory()?
            .join(format!("{TARGET_TRANSACTION_PREFIX}{transaction_id}")))
    }
}

/// Where a self-update is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SelfUpdatePlanState {
    /// Downloaded and verified; applied on the next start.
    Staged,
    /// Being applied (a crash here rolls back on the next start).
    Applying,
    /// Applied; the new launcher has not confirmed yet.
    AwaitingConfirmation,
    /// Rolled back to the prior files.
    RolledBack,
}

const STATE_NAMES: [(&str, SelfUpdatePlanState); 4] = [
    ("staged", SelfUpdatePlanState::Staged),
    ("applying", SelfUpdatePlanState::Applying),
    (
        "awaitingConfirmation",
        SelfUpdatePlanState::AwaitingConfirmation,
    ),
    ("rolledBack", SelfUpdatePlanState::RolledBack),
];

impl SelfUpdatePlanState {
    /// The state as the plan names it.
    pub fn name(self) -> &'static str {
        STATE_NAMES
            .iter()
            .find(|(_, state)| *state == self)
            .map_or("staged", |(name, _)| name)
    }
}

impl std::fmt::Display for SelfUpdatePlanState {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // OpenAC prints the enum's own name in its messages.
        f.write_str(match self {
            Self::Staged => "Staged",
            Self::Applying => "Applying",
            Self::AwaitingConfirmation => "AwaitingConfirmation",
            Self::RolledBack => "RolledBack",
        })
    }
}

/// What applying does to one file.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SelfUpdateApplyOperation {
    /// Put the new file in place (replacing the prior one, if any).
    Install,
    /// Take an obsolete owned file away.
    Remove,
}

const OPERATION_NAMES: [(&str, SelfUpdateApplyOperation); 2] = [
    ("install", SelfUpdateApplyOperation::Install),
    ("remove", SelfUpdateApplyOperation::Remove),
];

/// One journaled file change: what was there before and what replaces it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelfUpdateApplyEntry {
    /// Relative to the launcher folder.
    pub path: String,
    /// Install or remove.
    pub operation: SelfUpdateApplyOperation,
    /// A file was there before.
    pub had_original: bool,
    /// The prior file's SHA-256.
    pub prior_sha256: Option<String>,
    /// The prior file's size.
    pub prior_size: Option<i64>,
    /// The prior file's permission bits.
    pub prior_unix_mode: Option<i64>,
    /// The new file's SHA-256.
    pub replacement_sha256: Option<String>,
    /// The new file's size.
    pub replacement_size: Option<i64>,
    /// The new file's permission bits.
    pub replacement_unix_mode: Option<i64>,
}

/// `pending.json`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelfUpdatePlan {
    /// [`PLAN_SCHEMA_VERSION`].
    pub schema_version: i32,
    /// 32 lowercase hex digits.
    pub transaction_id: String,
    /// Where it is.
    pub state: SelfUpdatePlanState,
    /// The new launcher's version.
    pub version: String,
    /// The platform.
    pub rid: String,
    /// The installed root it applies to.
    pub target_directory: PathBuf,
    /// Flat or bundle.
    pub installation_kind: LauncherInstallationKind,
    /// The release archive's SHA-256.
    pub archive_sha256: String,
    /// The release archive's size.
    pub archive_size: i64,
    /// Every payload file, sorted by path.
    pub files: Vec<InstalledFileRecord>,
    /// The apply journal (absent while staged).
    pub apply: Option<Vec<SelfUpdateApplyEntry>>,
}

/// `launcher.install.json`: the files a self-update installed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherBinaryInstallRecord {
    /// [`INSTALL_RECORD_SCHEMA_VERSION`].
    pub schema_version: i32,
    /// The installed launcher's version.
    pub version: String,
    /// The platform.
    pub rid: String,
    /// Every installed file, sorted by path.
    pub files: Vec<InstalledFileRecord>,
}

/// What staging produced.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelfUpdateStageResult {
    /// The staged version.
    pub version: LauncherVersion,
    /// The plan file.
    pub pending_plan_path: PathBuf,
    /// A line for the player.
    pub status: String,
}

/// Where in applying an observer is called.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SelfUpdateApplyBoundary {
    /// Right after one file (or the bundle) changed in the installation.
    AfterTargetMutation,
}

/// An observation at an apply boundary (tests use it to inject failures).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelfUpdateApplyObservation {
    /// Which boundary.
    pub boundary: SelfUpdateApplyBoundary,
    /// The file (or bundle) just changed.
    pub path: String,
    /// What was done to it.
    pub operation: SelfUpdateApplyOperation,
}

/// Called at each apply boundary; an error fails (and rolls back) the apply.
pub type ApplyObserver =
    Box<dyn Fn(&SelfUpdateApplyObservation) -> Result<(), UpdateError> + Send + Sync>;

#[derive(Debug, Clone, PartialEq, Eq)]
struct JournalFileMetadata {
    sha256: String,
    size: i64,
    unix_mode: i64,
}

struct RollbackAction {
    entry: SelfUpdateApplyEntry,
    target_path: PathBuf,
    backup_path: PathBuf,
    discard_path: PathBuf,
}

fn io_error(context: &str, error: std::io::Error) -> UpdateError {
    UpdateError::io(format!("{context}: {error}"), error)
}

/// The self-update manager for one data directory.
pub struct LauncherSelfUpdateManager {
    root_directory: PathBuf,
    transactions_directory: PathBuf,
    pending_plan_path: PathBuf,
    barrier: UpdateSessionBarrier,
    downloader: VerifiedArtifactDownloader,
    extractor: SafeZipExtractor,
    apply_observer: Option<ApplyObserver>,
}

impl std::fmt::Debug for LauncherSelfUpdateManager {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LauncherSelfUpdateManager")
            .field("root_directory", &self.root_directory)
            .finish_non_exhaustive()
    }
}

impl LauncherSelfUpdateManager {
    /// The manager under `<root>/app/launcher-update`, downloading through
    /// `transport`.
    pub fn new(paths: &PathSet, transport: Arc<dyn HttpTransport>) -> Self {
        let root_directory = fsutil::full_path(&paths.app_directory()).join("launcher-update");
        Self {
            transactions_directory: root_directory.join("transactions"),
            pending_plan_path: root_directory.join("pending.json"),
            root_directory,
            barrier: UpdateSessionBarrier::new(&paths.data_directory),
            downloader: VerifiedArtifactDownloader::new(transport),
            extractor: SafeZipExtractor::default(),
            apply_observer: None,
        }
    }

    /// Replaces the extractor (tests use tighter limits).
    #[must_use]
    pub fn with_extractor(mut self, extractor: SafeZipExtractor) -> Self {
        self.extractor = extractor;
        self
    }

    /// Calls `observer` at each apply boundary (failure injection).
    #[must_use]
    pub fn with_apply_observer(mut self, observer: ApplyObserver) -> Self {
        self.apply_observer = Some(observer);
        self
    }

    /// `<root>/app/launcher-update`.
    pub fn root_directory(&self) -> &Path {
        &self.root_directory
    }

    /// `<root>/app/launcher-update/transactions`.
    pub fn transactions_directory(&self) -> &Path {
        &self.transactions_directory
    }

    /// `<root>/app/launcher-update/pending.json`.
    pub fn pending_plan_path(&self) -> &Path {
        &self.pending_plan_path
    }

    /// The update/session barrier (shared with the client updater and the
    /// sessions).
    pub fn barrier(&self) -> &UpdateSessionBarrier {
        &self.barrier
    }

    /// Stages the release's launcher for `rid` into `layout`.
    pub fn stage(
        &self,
        manifest: &ReleaseManifest,
        rid: &str,
        layout: &LauncherInstallationLayout,
        progress: &mut dyn FnMut(ArtifactDownloadProgress),
        cancel: &CancelToken,
    ) -> Result<SelfUpdateStageResult, UpdateError> {
        let artifact = manifest.require_launcher(rid)?.clone();
        self.stage_artifact(&manifest.version, rid, &artifact, layout, progress, cancel)
    }

    /// Stages one launcher archive: download, verify, extract, check, and
    /// write the plan. The running installation is not touched.
    pub fn stage_artifact(
        &self,
        version: &LauncherVersion,
        rid: &str,
        artifact: &ReleaseArtifact,
        layout: &LauncherInstallationLayout,
        progress: &mut dyn FnMut(ArtifactDownloadProgress),
        cancel: &CancelToken,
    ) -> Result<SelfUpdateStageResult, UpdateError> {
        if !is_valid_rid(rid) {
            return Err(UpdateError::failed("RID is invalid."));
        }
        validate_layout(layout, rid)?;
        let lease = self.barrier.acquire_exclusive()?;
        let target = normalize_target_directory(&layout.installed_root)?;
        create_dir(&self.root_directory)?;
        create_dir(&self.transactions_directory)?;
        let existing = self.load_pending()?;
        self.cleanup_owned_residue_under_lease(existing.as_ref(), &target, &lease)?;
        if let Some(existing) = existing {
            return Err(UpdateError::failed(format!(
                "Launcher self-update {} is already {}. Restart the launcher to finish it \
                 before staging another.",
                existing.version, existing.state
            )));
        }

        let transaction_id = ids::unique_hex32();
        let transaction_directory = self.transaction_directory(&transaction_id)?;
        let payload_directory = self.payload_directory(&transaction_id)?;
        let archive_path = transaction_directory.join("launcher.zip");
        create_dir(&transaction_directory)?;
        let result = (|| {
            self.downloader
                .download_artifact(artifact, &archive_path, progress, cancel)?;
            let extracted = self.extractor.extract(
                &archive_path,
                &payload_directory,
                &executables_for_payload(rid, true),
                cancel,
            )?;
            if layout.kind == LauncherInstallationKind::MacBundle {
                validate_mac_bundle_executable(&extracted, layout)?;
            } else {
                validate_required_executables(&extracted, rid, true)?;
            }
            validate_payload_layout(&extracted, layout)?;
            if extracted
                .iter()
                .any(|file| file.path.eq_ignore_ascii_case(INSTALL_RECORD_FILE_NAME))
            {
                return Err(UpdateError::failed(format!(
                    "The launcher ZIP may not provide '{INSTALL_RECORD_FILE_NAME}'."
                )));
            }
            let mut files: Vec<InstalledFileRecord> = extracted
                .iter()
                .map(|file| InstalledFileRecord {
                    path: file.path.clone(),
                    sha256: file.sha256.clone(),
                    size: file.size,
                    unix_mode: file.unix_mode,
                })
                .collect();
            files.sort_by(|a, b| a.path.cmp(&b.path));
            let plan = SelfUpdatePlan {
                schema_version: PLAN_SCHEMA_VERSION,
                transaction_id: transaction_id.clone(),
                state: SelfUpdatePlanState::Staged,
                version: version.value().to_owned(),
                rid: rid.to_owned(),
                target_directory: target.clone(),
                installation_kind: layout.kind,
                archive_sha256: artifact.sha256.to_lowercase(),
                archive_size: i64::try_from(artifact.size).unwrap_or(i64::MAX),
                files,
                apply: None,
            };
            self.validate_plan(&plan, &target)?;
            self.write_plan(&plan)?;
            fsutil::try_delete_file(&archive_path);
            Ok(SelfUpdateStageResult {
                version: version.clone(),
                pending_plan_path: self.pending_plan_path.clone(),
                status: format!("Launcher {version} is staged and will be applied on next start."),
            })
        })();
        if result.is_err() && !self.pending_plan_path.exists() {
            try_delete_directory(&transaction_directory);
        }
        drop(lease);
        result
    }

    /// The pending plan, checked; `None` when nothing is pending.
    pub fn load_pending(&self) -> Result<Option<SelfUpdatePlan>, UpdateError> {
        if !self.pending_plan_path.exists() {
            return Ok(None);
        }
        let bytes = std::fs::read(&self.pending_plan_path).map_err(|error| {
            UpdateError::io(
                format!("The pending launcher self-update is invalid: {error}"),
                error,
            )
        })?;
        let plan = parse_plan(&bytes)
            .map_err(|message| {
                UpdateError::failed(format!(
                    "The pending launcher self-update is invalid: {message}"
                ))
            })?
            .ok_or_else(|| UpdateError::failed("The self-update plan is empty."))?;
        let plan = migrate_plan(plan);
        let target = plan.target_directory.clone();
        self.validate_plan(&plan, &target)?;
        Ok(Some(plan))
    }

    /// Applies the staged plan to `expected_target_directory` (the running
    /// launcher's installed root). Ends awaiting confirmation; any failure
    /// rolls back first.
    pub fn apply_pending(
        &self,
        expected_target_directory: &Path,
        cancel: &CancelToken,
    ) -> Result<SelfUpdatePlan, UpdateError> {
        let expected_target = normalize_target_directory(expected_target_directory)?;
        let mut plan = self
            .load_pending()?
            .ok_or_else(|| UpdateError::failed("There is no staged launcher self-update."))?;
        self.validate_plan(&plan, &expected_target)?;
        if plan.installation_kind == LauncherInstallationKind::MacBundle {
            return self.apply_bundle_pending(plan, cancel);
        }
        if plan.state == SelfUpdatePlanState::AwaitingConfirmation {
            return Ok(plan);
        }
        if plan.state == SelfUpdatePlanState::Applying {
            plan = self.rollback_applying(plan, cancel)?;
        }
        if plan.state == SelfUpdatePlanState::RolledBack {
            verify_restored_prior(&plan, &expected_target, cancel)?;
            plan.state = SelfUpdatePlanState::Staged;
            plan.apply = None;
            self.write_plan(&plan)?;
        }

        self.verify_payload(&plan, cancel)?;
        let previous = read_and_verify_install_record(&expected_target, &plan.rid, cancel)?;
        let apply = build_apply_journal(&plan, previous.as_ref(), &expected_target, cancel)?;
        let apply = self.prepare_target_transaction(&plan, apply, cancel)?;
        plan.state = SelfUpdatePlanState::Applying;
        plan.apply = Some(apply);
        self.write_plan(&plan)?;

        let applied = (|| {
            for entry in plan.apply.as_deref().unwrap_or_default() {
                cancel.check()?;
                self.apply_entry(&plan, entry, cancel)?;
                if let Some(observer) = &self.apply_observer {
                    observer(&SelfUpdateApplyObservation {
                        boundary: SelfUpdateApplyBoundary::AfterTargetMutation,
                        path: entry.path.clone(),
                        operation: entry.operation,
                    })?;
                }
            }
            let mut awaiting = plan.clone();
            awaiting.state = SelfUpdatePlanState::AwaitingConfirmation;
            self.write_plan(&awaiting)?;
            Ok(awaiting)
        })();
        match applied {
            Ok(plan) => Ok(plan),
            Err(error) => {
                self.rollback_applying(plan, &CancelToken::new())?;
                Err(error)
            }
        }
    }

    /// Rolls back a plan left applying by a crash; any other state is
    /// returned as it is.
    pub fn recover_applying(
        &self,
        expected_target_directory: &Path,
        cancel: &CancelToken,
    ) -> Result<SelfUpdatePlan, UpdateError> {
        let expected_target = normalize_target_path(expected_target_directory)?;
        let plan = self
            .load_pending()?
            .ok_or_else(|| UpdateError::failed("There is no pending self-update."))?;
        self.validate_plan(&plan, &expected_target)?;
        if plan.state != SelfUpdatePlanState::Applying {
            return Ok(plan);
        }
        if plan.installation_kind == LauncherInstallationKind::MacBundle {
            self.rollback_bundle_applying(plan, cancel)
        } else {
            self.rollback_applying(plan, cancel)
        }
    }

    /// Checks that a rolled-back plan's prior files are all back in place.
    pub fn verify_restored_prior(
        &self,
        expected_target_directory: &Path,
        cancel: &CancelToken,
    ) -> Result<(), UpdateError> {
        let expected_target = normalize_target_directory(expected_target_directory)?;
        let plan = self
            .load_pending()?
            .ok_or_else(|| UpdateError::failed("There is no rolled-back self-update."))?;
        self.validate_plan(&plan, &expected_target)?;
        if plan.state != SelfUpdatePlanState::RolledBack {
            return Err(UpdateError::failed(
                "The pending self-update has no verified rollback receipt.",
            ));
        }
        verify_restored_prior(&plan, &expected_target, cancel)
    }

    /// The new launcher confirms it started: only the installed launcher
    /// executable of the plan awaiting confirmation may do so.
    pub fn confirm(
        &self,
        transaction_id: &str,
        expected_target_directory: &Path,
        current_executable_path: &Path,
        cancel: &CancelToken,
    ) -> Result<(), UpdateError> {
        let plan = self
            .load_pending()?
            .ok_or_else(|| UpdateError::failed("There is no self-update to confirm."))?;
        let expected_target = normalize_target_directory(expected_target_directory)?;
        self.validate_plan(&plan, &expected_target)?;
        if plan.transaction_id != transaction_id
            || plan.state != SelfUpdatePlanState::AwaitingConfirmation
        {
            return Err(UpdateError::failed(
                "The running launcher does not match the pending confirmation plan.",
            ));
        }
        let expected_executable = layout_for(&plan)?.launcher_path()?;
        if !fsutil::paths_equal(&expected_executable, current_executable_path) {
            return Err(UpdateError::failed(
                "Only the newly installed launcher executable may confirm self-update.",
            ));
        }
        verify_applied_targets(&plan, &expected_target, cancel)?;
        if plan.installation_kind == LauncherInstallationKind::Flat {
            verify_installed_ownership_matches_plan(&plan, &expected_target, cancel)?;
        }
        let confirmation = self.confirmation_path(transaction_id)?;
        fsutil::write_atomic_unique(&confirmation, b"confirmed")
            .map_err(|error| io_error("The self-update confirmation could not be written", error))
    }

    /// Whether the new launcher confirmed `transaction_id`.
    pub fn is_confirmed(&self, transaction_id: &str) -> bool {
        self.confirmation_path(transaction_id)
            .is_ok_and(|path| path.is_file())
    }

    /// Ends a confirmed update: the plan and both transaction folders go.
    pub fn complete_confirmed(
        &self,
        transaction_id: &str,
        expected_target_directory: &Path,
    ) -> Result<(), UpdateError> {
        let plan = self
            .load_pending()?
            .ok_or_else(|| UpdateError::failed("There is no self-update to complete."))?;
        let expected_target = normalize_target_directory(expected_target_directory)?;
        self.validate_plan(&plan, &expected_target)?;
        if plan.transaction_id != transaction_id
            || plan.state != SelfUpdatePlanState::AwaitingConfirmation
            || !self.is_confirmed(transaction_id)
        {
            return Err(UpdateError::failed("The self-update is not confirmed."));
        }
        std::fs::remove_file(&self.pending_plan_path)
            .map_err(|error| io_error("The self-update plan could not be removed", error))?;
        try_delete_directory(&self.target_transaction_directory(&plan)?);
        try_delete_directory(&self.transaction_directory(transaction_id)?);
        Ok(())
    }

    /// Ends a rolled-back update once the prior files verify.
    pub fn complete_rolled_back(
        &self,
        transaction_id: &str,
        expected_target_directory: &Path,
        lease: &ExclusiveLease,
        cancel: &CancelToken,
    ) -> Result<(), UpdateError> {
        self.barrier.require_owned(lease)?;
        let expected_target = normalize_target_directory(expected_target_directory)?;
        let plan = self
            .load_pending()?
            .ok_or_else(|| UpdateError::failed("There is no rolled-back self-update."))?;
        self.validate_plan(&plan, &expected_target)?;
        if plan.transaction_id != transaction_id || plan.state != SelfUpdatePlanState::RolledBack {
            return Err(UpdateError::failed(
                "The self-update does not have the expected rollback receipt.",
            ));
        }
        verify_restored_prior(&plan, &expected_target, cancel)?;
        std::fs::remove_file(&self.pending_plan_path)
            .map_err(|error| io_error("The self-update plan could not be removed", error))?;
        try_delete_directory(&self.target_transaction_directory(&plan)?);
        try_delete_directory(&self.transaction_directory(transaction_id)?);
        Ok(())
    }

    /// Rolls back an applied update the new launcher did not confirm.
    pub fn rollback_awaiting_confirmation(
        &self,
        expected_target_directory: &Path,
        cancel: &CancelToken,
    ) -> Result<SelfUpdatePlan, UpdateError> {
        let expected_target = normalize_target_directory(expected_target_directory)?;
        let mut plan = self
            .load_pending()?
            .ok_or_else(|| UpdateError::failed("There is no self-update to roll back."))?;
        self.validate_plan(&plan, &expected_target)?;
        if plan.state != SelfUpdatePlanState::AwaitingConfirmation {
            return Err(UpdateError::failed(
                "The pending self-update is not awaiting confirmation.",
            ));
        }
        plan.state = SelfUpdatePlanState::Applying;
        self.write_plan(&plan)?;
        if plan.installation_kind == LauncherInstallationKind::MacBundle {
            self.rollback_bundle_applying(plan, cancel)
        } else {
            self.rollback_applying(plan, cancel)
        }
    }

    /// `<root>/app/launcher-update/transactions/<id>`.
    pub fn transaction_directory(&self, transaction_id: &str) -> Result<PathBuf, UpdateError> {
        require_transaction_id(transaction_id)?;
        Ok(self.transactions_directory.join(transaction_id))
    }

    /// The staged payload of a transaction.
    pub fn payload_directory(&self, transaction_id: &str) -> Result<PathBuf, UpdateError> {
        Ok(self.transaction_directory(transaction_id)?.join("payload"))
    }

    /// The confirmation marker of a transaction.
    pub fn confirmation_path(&self, transaction_id: &str) -> Result<PathBuf, UpdateError> {
        Ok(self
            .transaction_directory(transaction_id)?
            .join("confirmed"))
    }

    /// The transaction folder beside the installation.
    pub fn target_transaction_directory(
        &self,
        plan: &SelfUpdatePlan,
    ) -> Result<PathBuf, UpdateError> {
        layout_for(plan)?.sibling_transaction_directory(&plan.transaction_id)
    }

    /// The staged launcher executable (the helper).
    pub fn staged_launcher_path(&self, plan: &SelfUpdatePlan) -> Result<PathBuf, UpdateError> {
        resolve_contained(
            &self.payload_directory(&plan.transaction_id)?,
            &layout_for(plan)?.payload_launcher_path(),
        )
    }

    /// Removes transaction leftovers this manager owns, keeping what the
    /// pending plan still needs. True when nothing reclaimable is left.
    pub fn cleanup_owned_residue_under_lease(
        &self,
        pending: Option<&SelfUpdatePlan>,
        target_directory: &Path,
        lease: &ExclusiveLease,
    ) -> Result<bool, UpdateError> {
        self.barrier.require_owned(lease)?;
        let target = normalize_target_directory(target_directory)?;
        self.cleanup_data_residue(pending.map(|plan| plan.transaction_id.as_str()));
        let keep_target = pending
            .filter(|plan| {
                matches!(
                    plan.state,
                    SelfUpdatePlanState::Applying | SelfUpdatePlanState::AwaitingConfirmation
                )
            })
            .map(|plan| plan.transaction_id.as_str());
        let transaction_container = if pending
            .is_some_and(|plan| plan.installation_kind == LauncherInstallationKind::MacBundle)
            || target.file_name().and_then(|name| name.to_str()) == Some(MAC_BUNDLE_NAME)
        {
            target
                .parent()
                .map(Path::to_path_buf)
                .ok_or_else(|| UpdateError::failed("The app bundle has no containing directory."))?
        } else {
            target
        };
        cleanup_target_residue(&transaction_container, keep_target);
        Ok(!self.has_reclaimable_residue(
            pending.map(|plan| plan.transaction_id.as_str()),
            &transaction_container,
            keep_target,
        ))
    }

    // --- Applying ---------------------------------------------------------------------------

    fn prepare_target_transaction(
        &self,
        plan: &SelfUpdatePlan,
        apply: Vec<SelfUpdateApplyEntry>,
        cancel: &CancelToken,
    ) -> Result<Vec<SelfUpdateApplyEntry>, UpdateError> {
        let swap = self.target_transaction_directory(plan)?;
        if swap.is_dir() {
            reject_link_tree(&swap)?;
            try_delete_directory(&swap);
        }
        if std::fs::symlink_metadata(&swap).is_ok() {
            return Err(UpdateError::failed(
                "The target-local self-update transaction could not be reclaimed.",
            ));
        }
        let incoming = swap.join("incoming");
        create_dir(&incoming)?;
        let payload = self.payload_directory(&plan.transaction_id)?;
        for file in &plan.files {
            cancel.check()?;
            let source = resolve_contained(&payload, &file.path)?;
            let destination = resolve_contained(&incoming, &file.path)?;
            if let Some(parent) = destination.parent() {
                create_dir(parent)?;
            }
            copy_file_durably(&source, &destination)?;
            if cfg!(unix) && file.unix_mode != 0 {
                fsutil::set_unix_mode(&destination, file.unix_mode).map_err(|error| {
                    io_error("The incoming launcher mode could not be set", error)
                })?;
            }
            verify_file(&incoming, file, "Target-local incoming launcher", cancel)?;
        }

        let ownership = LauncherBinaryInstallRecord {
            schema_version: INSTALL_RECORD_SCHEMA_VERSION,
            version: plan.version.clone(),
            rid: plan.rid.clone(),
            files: plan.files.clone(),
        };
        fsutil::write_atomic_unique(
            &incoming.join(INSTALL_RECORD_FILE_NAME),
            install_record_json(&ownership).as_bytes(),
        )
        .map_err(|error| io_error("The launcher ownership record could not be written", error))?;
        reject_link_tree(&swap)?;

        let mut actual = list_files(&incoming)?;
        actual.sort();
        let mut expected: Vec<String> = apply
            .iter()
            .filter(|entry| entry.operation == SelfUpdateApplyOperation::Install)
            .map(|entry| entry.path.clone())
            .collect();
        expected.sort();
        if actual != expected {
            return Err(UpdateError::failed(
                "The target-local self-update incoming tree is incomplete.",
            ));
        }

        let mut completed = Vec::with_capacity(apply.len());
        for mut entry in apply {
            if entry.operation == SelfUpdateApplyOperation::Remove {
                completed.push(entry);
                continue;
            }
            let replacement = capture_required_file_metadata(
                &resolve_contained(&incoming, &entry.path)?,
                &format!("Target-local incoming launcher file '{}'", entry.path),
                cancel,
            )?;
            entry.replacement_sha256 = Some(replacement.sha256);
            entry.replacement_size = Some(replacement.size);
            entry.replacement_unix_mode = Some(replacement.unix_mode);
            completed.push(entry);
        }
        Ok(completed)
    }

    fn apply_entry(
        &self,
        plan: &SelfUpdatePlan,
        entry: &SelfUpdateApplyEntry,
        cancel: &CancelToken,
    ) -> Result<(), UpdateError> {
        let swap = self.target_transaction_directory(plan)?;
        let incoming = swap.join("incoming");
        let backup = swap.join("backup");
        let target_path = resolve_contained(&plan.target_directory, &entry.path)?;
        let backup_path = resolve_contained(&backup, &entry.path)?;
        let incoming_path = resolve_contained(&incoming, &entry.path)?;
        reject_link_tree(&swap)?;
        ensure_existing_parents_safe(&plan.target_directory, &target_path)?;
        ensure_existing_parents_safe(&swap, &incoming_path)?;
        ensure_existing_parents_safe(&swap, &backup_path)?;
        ensure_path_missing(
            &backup_path,
            &format!("Self-update backup '{}'", entry.path),
        )?;

        if entry.had_original {
            verify_prior_file(entry, &target_path, cancel)?;
        } else {
            ensure_path_missing(
                &target_path,
                &format!("Self-update target '{}'", entry.path),
            )?;
        }

        if entry.operation == SelfUpdateApplyOperation::Remove {
            ensure_safe_parent(&swap, &backup_path)?;
            return std::fs::rename(&target_path, &backup_path)
                .map_err(|error| io_error("An obsolete launcher file could not be moved", error));
        }

        verify_replacement_file(entry, &incoming_path, cancel)?;
        ensure_safe_parent(&swap, &backup_path)?;
        if entry.had_original {
            platform::replace_file(&incoming_path, &target_path, &backup_path)
                .map_err(|error| io_error("A launcher file could not be replaced", error))
        } else {
            std::fs::rename(&incoming_path, &target_path)
                .map_err(|error| io_error("A launcher file could not be installed", error))
        }
    }

    fn apply_bundle_pending(
        &self,
        mut plan: SelfUpdatePlan,
        cancel: &CancelToken,
    ) -> Result<SelfUpdatePlan, UpdateError> {
        if plan.state == SelfUpdatePlanState::AwaitingConfirmation {
            return Ok(plan);
        }
        if plan.state == SelfUpdatePlanState::Applying {
            plan = self.rollback_bundle_applying(plan, cancel)?;
        }
        if plan.state == SelfUpdatePlanState::RolledBack {
            verify_bundle_restored(&plan, cancel)?;
            plan.state = SelfUpdatePlanState::Staged;
            plan.apply = None;
            self.write_plan(&plan)?;
        }

        self.verify_payload(&plan, cancel)?;
        let layout = layout_for(&plan)?;
        let swap = self.target_transaction_directory(&plan)?;
        if swap.is_dir() {
            reject_link_tree(&swap)?;
            try_delete_directory(&swap);
        }
        if std::fs::symlink_metadata(&swap).is_ok() {
            return Err(UpdateError::failed(
                "The macOS bundle transaction could not be reclaimed.",
            ));
        }
        let incoming_root = swap.join("incoming");
        create_dir(&incoming_root)?;
        self.copy_bundle_incoming(&plan, &incoming_root, cancel)?;
        let incoming_bundle = resolve_contained(&incoming_root, &layout.payload_root)?;
        let backup_root = swap.join("backup");
        let backup_bundle = backup_root.join(&layout.payload_root);
        reject_link_tree(&swap)?;
        if !layout.installed_root.is_dir()
            || fsutil::is_link(&layout.installed_root).unwrap_or(true)
        {
            return Err(UpdateError::failed(
                "The installed macOS app bundle is missing or linked.",
            ));
        }

        plan.state = SelfUpdatePlanState::Applying;
        plan.apply = Some(Vec::new());
        self.write_plan(&plan)?;
        let applied = (|| {
            create_dir(&backup_root)?;
            std::fs::rename(&layout.installed_root, &backup_bundle)
                .map_err(|error| io_error("The installed app bundle could not be moved", error))?;
            if let Some(observer) = &self.apply_observer {
                observer(&SelfUpdateApplyObservation {
                    boundary: SelfUpdateApplyBoundary::AfterTargetMutation,
                    path: layout.payload_root.clone(),
                    operation: SelfUpdateApplyOperation::Install,
                })?;
            }
            std::fs::rename(&incoming_bundle, &layout.installed_root)
                .map_err(|error| io_error("The new app bundle could not be moved", error))?;
            verify_applied_targets(&plan, &layout.installed_root, cancel)?;
            let mut awaiting = plan.clone();
            awaiting.state = SelfUpdatePlanState::AwaitingConfirmation;
            self.write_plan(&awaiting)?;
            Ok(awaiting)
        })();
        match applied {
            Ok(plan) => Ok(plan),
            Err(error) => {
                self.rollback_bundle_applying(plan, &CancelToken::new())?;
                Err(error)
            }
        }
    }

    fn copy_bundle_incoming(
        &self,
        plan: &SelfUpdatePlan,
        incoming_root: &Path,
        cancel: &CancelToken,
    ) -> Result<(), UpdateError> {
        let payload = self.payload_directory(&plan.transaction_id)?;
        for file in &plan.files {
            cancel.check()?;
            let source = resolve_contained(&payload, &file.path)?;
            let destination = resolve_contained(incoming_root, &file.path)?;
            if let Some(parent) = destination.parent() {
                create_dir(parent)?;
            }
            copy_file_durably(&source, &destination)?;
            if cfg!(unix) && file.unix_mode != 0 {
                fsutil::set_unix_mode(&destination, file.unix_mode).map_err(|error| {
                    io_error("The incoming bundle mode could not be set", error)
                })?;
            }
            verify_file(incoming_root, file, "Incoming macOS app bundle", cancel)?;
        }
        Ok(())
    }

    fn rollback_bundle_applying(
        &self,
        mut plan: SelfUpdatePlan,
        cancel: &CancelToken,
    ) -> Result<SelfUpdatePlan, UpdateError> {
        if plan.state != SelfUpdatePlanState::Applying || plan.apply.is_none() {
            return Err(UpdateError::failed(
                "The macOS bundle rollback journal is missing.",
            ));
        }
        let layout = layout_for(&plan)?;
        let swap = self.target_transaction_directory(&plan)?;
        let backup = swap.join("backup").join(&layout.payload_root);
        let discard = swap.join("rollback-discard").join(&layout.payload_root);
        if !swap.is_dir() {
            return Err(UpdateError::failed(
                "The macOS bundle rollback transaction is missing.",
            ));
        }
        reject_link_tree(&swap)?;
        if backup.is_dir() {
            if layout.installed_root.is_dir() {
                if let Some(parent) = discard.parent() {
                    create_dir(parent)?;
                }
                std::fs::rename(&layout.installed_root, &discard).map_err(|error| {
                    io_error("The applied app bundle could not be set aside", error)
                })?;
            }
            std::fs::rename(&backup, &layout.installed_root)
                .map_err(|error| io_error("The prior app bundle could not be restored", error))?;
        } else if !layout.installed_root.is_dir() {
            return Err(UpdateError::failed(
                "The macOS bundle rollback state is ambiguous.",
            ));
        }
        verify_bundle_restored(&plan, cancel)?;
        plan.state = SelfUpdatePlanState::RolledBack;
        self.write_plan(&plan)?;
        try_delete_directory(&swap);
        Ok(plan)
    }

    fn rollback_applying(
        &self,
        mut plan: SelfUpdatePlan,
        cancel: &CancelToken,
    ) -> Result<SelfUpdatePlan, UpdateError> {
        if plan.state != SelfUpdatePlanState::Applying || plan.apply.is_none() {
            return Err(UpdateError::failed(
                "The self-update rollback journal is missing.",
            ));
        }
        let swap = self.target_transaction_directory(&plan)?;
        let actions = self.preflight_rollback(&plan, cancel)?;
        for action in actions {
            cancel.check()?;
            reject_link_tree(&swap)?;
            ensure_existing_parents_safe(&plan.target_directory, &action.target_path)?;
            ensure_existing_parents_safe(&swap, &action.backup_path)?;
            ensure_existing_parents_safe(&swap, &action.discard_path)?;
            if action.entry.operation == SelfUpdateApplyOperation::Remove {
                ensure_safe_parent(&plan.target_directory, &action.target_path)?;
                std::fs::rename(&action.backup_path, &action.target_path).map_err(|error| {
                    io_error("An obsolete launcher file could not be restored", error)
                })?;
                continue;
            }
            ensure_safe_parent(&swap, &action.discard_path)?;
            if action.entry.had_original {
                platform::replace_file(
                    &action.backup_path,
                    &action.target_path,
                    &action.discard_path,
                )
                .map_err(|error| io_error("A prior launcher file could not be restored", error))?;
            } else {
                std::fs::rename(&action.target_path, &action.discard_path).map_err(|error| {
                    io_error("A new launcher file could not be set aside", error)
                })?;
            }
        }
        let target = plan.target_directory.clone();
        verify_restored_prior(&plan, &target, cancel)?;
        plan.state = SelfUpdatePlanState::RolledBack;
        self.write_plan(&plan)?;
        try_delete_directory(&swap);
        Ok(plan)
    }

    fn preflight_rollback(
        &self,
        plan: &SelfUpdatePlan,
        cancel: &CancelToken,
    ) -> Result<Vec<RollbackAction>, UpdateError> {
        let swap = self.target_transaction_directory(plan)?;
        if !swap.is_dir() {
            return Err(UpdateError::failed(
                "The target-local self-update rollback transaction is missing.",
            ));
        }
        reject_link_tree(&swap)?;
        validate_rollback_tree(plan, &swap)?;
        let incoming = swap.join("incoming");
        let backup = swap.join("backup");
        let discard = swap.join("rollback-discard");
        let mut actions = Vec::new();
        for entry in plan.apply.as_deref().unwrap_or_default().iter().rev() {
            cancel.check()?;
            let target_path = resolve_contained(&plan.target_directory, &entry.path)?;
            let incoming_path = resolve_contained(&incoming, &entry.path)?;
            let backup_path = resolve_contained(&backup, &entry.path)?;
            let discard_path = resolve_contained(&discard, &entry.path)?;
            ensure_existing_parents_safe(&plan.target_directory, &target_path)?;
            ensure_existing_parents_safe(&swap, &incoming_path)?;
            ensure_existing_parents_safe(&swap, &backup_path)?;
            ensure_existing_parents_safe(&swap, &discard_path)?;

            let target = capture_optional_file_metadata(
                &target_path,
                &format!("Rollback target '{}'", entry.path),
                cancel,
            )?;
            let incoming_file = capture_optional_file_metadata(
                &incoming_path,
                &format!("Rollback incoming file '{}'", entry.path),
                cancel,
            )?;
            let backup_file = capture_optional_file_metadata(
                &backup_path,
                &format!("Rollback backup file '{}'", entry.path),
                cancel,
            )?;
            let discarded_file = capture_optional_file_metadata(
                &discard_path,
                &format!("Rollback discard file '{}'", entry.path),
                cancel,
            )?;
            let action = || RollbackAction {
                entry: entry.clone(),
                target_path: target_path.clone(),
                backup_path: backup_path.clone(),
                discard_path: discard_path.clone(),
            };

            if entry.operation == SelfUpdateApplyOperation::Remove {
                require_missing(incoming_file.as_ref(), &entry.path, "incoming")?;
                require_missing(discarded_file.as_ref(), &entry.path, "discard")?;
                match (&backup_file, &target) {
                    (Some(backup_file), None) => {
                        require_prior_metadata(entry, backup_file, "rollback backup")?;
                        actions.push(action());
                        continue;
                    }
                    (None, Some(target)) => {
                        require_prior_metadata(entry, target, "restored rollback target")?;
                        continue;
                    }
                    _ => return Err(ambiguous_rollback(&entry.path)),
                }
            }

            if entry.had_original {
                if let (Some(backup_file), Some(target), None, None) =
                    (&backup_file, &target, &incoming_file, &discarded_file)
                {
                    require_prior_metadata(entry, backup_file, "rollback backup")?;
                    require_replacement_metadata(entry, target, "applied rollback target")?;
                    actions.push(action());
                    continue;
                }
                if let (None, Some(target)) = (&backup_file, &target) {
                    require_prior_metadata(entry, target, "restored rollback target")?;
                    match (&incoming_file, &discarded_file) {
                        (Some(incoming_file), None) => {
                            require_replacement_metadata(
                                entry,
                                incoming_file,
                                "unapplied rollback incoming file",
                            )?;
                            continue;
                        }
                        (None, Some(discarded_file)) => {
                            require_replacement_metadata(
                                entry,
                                discarded_file,
                                "completed rollback discard",
                            )?;
                            continue;
                        }
                        _ => {}
                    }
                }
                return Err(ambiguous_rollback(&entry.path));
            }

            require_missing(backup_file.as_ref(), &entry.path, "backup")?;
            match (&target, &incoming_file, &discarded_file) {
                (Some(target), None, None) => {
                    require_replacement_metadata(entry, target, "applied rollback target")?;
                    actions.push(action());
                }
                (None, Some(incoming_file), None) => {
                    require_replacement_metadata(
                        entry,
                        incoming_file,
                        "unapplied rollback incoming file",
                    )?;
                }
                (None, None, Some(discarded_file)) => {
                    require_replacement_metadata(
                        entry,
                        discarded_file,
                        "completed rollback discard",
                    )?;
                }
                _ => return Err(ambiguous_rollback(&entry.path)),
            }
        }
        Ok(actions)
    }

    fn verify_payload(
        &self,
        plan: &SelfUpdatePlan,
        cancel: &CancelToken,
    ) -> Result<(), UpdateError> {
        let payload = self.payload_directory(&plan.transaction_id)?;
        if !payload.is_dir() {
            return Err(UpdateError::failed(
                "The staged launcher payload is missing.",
            ));
        }
        reject_link_tree(&payload)?;
        let mut actual = list_files(&payload)?;
        actual.sort();
        let mut expected: Vec<String> = plan.files.iter().map(|file| file.path.clone()).collect();
        expected.sort();
        if actual != expected {
            return Err(UpdateError::failed(
                "The staged launcher contains missing or unrecorded files.",
            ));
        }
        for file in &plan.files {
            verify_file(&payload, file, "Staged launcher", cancel)?;
        }
        Ok(())
    }

    fn write_plan(&self, plan: &SelfUpdatePlan) -> Result<(), UpdateError> {
        self.validate_plan(plan, &plan.target_directory)?;
        fsutil::write_atomic_unique(&self.pending_plan_path, plan_json(plan).as_bytes())
            .map_err(|error| io_error("The self-update plan could not be written", error))
    }

    fn validate_plan(
        &self,
        plan: &SelfUpdatePlan,
        expected_target_directory: &Path,
    ) -> Result<(), UpdateError> {
        if plan.schema_version != PLAN_SCHEMA_VERSION {
            return Err(UpdateError::failed(format!(
                "Self-update schema version {} is not supported.",
                plan.schema_version
            )));
        }
        require_transaction_id(&plan.transaction_id)?;
        if LauncherVersion::try_parse(&plan.version).is_none()
            || !is_valid_rid(&plan.rid)
            || !is_sha256(&plan.archive_sha256)
            || plan.archive_size <= 0
            || u64::try_from(plan.archive_size).is_ok_and(|size| size > MAXIMUM_ARTIFACT_BYTES)
        {
            return Err(UpdateError::failed(
                "The self-update plan metadata is invalid.",
            ));
        }
        let bundle_recovery = plan.installation_kind == LauncherInstallationKind::MacBundle
            && plan.state == SelfUpdatePlanState::Applying;
        let target = if bundle_recovery {
            normalize_target_path(&plan.target_directory)?
        } else {
            normalize_target_directory(&plan.target_directory)?
        };
        if !fsutil::paths_equal(&target, expected_target_directory) {
            return Err(UpdateError::failed(
                "The self-update target does not match the running launcher directory.",
            ));
        }
        validate_file_records(&plan.files, "self-update file list")?;
        let layout = layout_for(plan)?;
        if plan.installation_kind == LauncherInstallationKind::MacBundle {
            let prefix = format!("{}/", layout.payload_root);
            let info_plist = format!("{}/Contents/Info.plist", layout.payload_root);
            let launcher = layout.payload_launcher_path();
            if !plan.rid.starts_with("osx-")
                || plan
                    .files
                    .iter()
                    .any(|file| !file.path.starts_with(&prefix))
                || !plan.files.iter().any(|file| file.path == launcher)
                || !plan.files.iter().any(|file| file.path == info_plist)
                || (plan.state == SelfUpdatePlanState::Staged) != plan.apply.is_none()
                || plan.apply.as_ref().is_some_and(|apply| !apply.is_empty())
            {
                return Err(UpdateError::failed(
                    "The macOS bundle self-update plan is invalid.",
                ));
            }
            let bundle_transaction = self.target_transaction_directory(plan)?;
            if !is_contained(&layout.container_directory()?, &bundle_transaction) {
                return Err(UpdateError::failed(
                    "A macOS bundle transaction path escaped.",
                ));
            }
            return Ok(());
        }

        let new_paths: HashSet<String> = plan
            .files
            .iter()
            .map(|file| file.path.to_lowercase())
            .collect();
        if new_paths.contains(&INSTALL_RECORD_FILE_NAME.to_lowercase())
            || !new_paths.contains(&launcher_file_name(&plan.rid).to_lowercase())
        {
            return Err(UpdateError::failed(
                "The self-update file list has a reserved path or lacks the launcher executable.",
            ));
        }
        if (plan.state == SelfUpdatePlanState::Staged) != plan.apply.is_none() {
            return Err(UpdateError::failed(
                "The self-update apply journal does not match its state.",
            ));
        }
        if let Some(apply) = &plan.apply {
            let mut apply_paths = HashSet::new();
            let mut prior: Option<&str> = None;
            for entry in apply {
                let prior_present = entry.prior_sha256.is_some()
                    || entry.prior_size.is_some()
                    || entry.prior_unix_mode.is_some();
                let prior_valid = entry.prior_sha256.as_deref().is_some_and(is_sha256)
                    && entry.prior_size.is_some_and(|size| size >= 0)
                    && entry.prior_unix_mode.is_some_and(valid_mode);
                let replacement_present = entry.replacement_sha256.is_some()
                    || entry.replacement_size.is_some()
                    || entry.replacement_unix_mode.is_some();
                let replacement_valid = entry.replacement_sha256.as_deref().is_some_and(is_sha256)
                    && entry.replacement_size.is_some_and(|size| size >= 0)
                    && entry.replacement_unix_mode.is_some_and(valid_mode);
                let install = entry.operation == SelfUpdateApplyOperation::Install;
                if !is_normalized_relative(&entry.path)
                    || !apply_paths.insert(entry.path.to_lowercase())
                    || (entry.operation == SelfUpdateApplyOperation::Remove && !entry.had_original)
                    || entry.had_original != prior_valid
                    || entry.had_original != prior_present
                    || install != replacement_valid
                    || install != replacement_present
                    || prior.is_some_and(|prior| prior >= entry.path.as_str())
                {
                    return Err(UpdateError::failed(
                        "The self-update apply journal is invalid, duplicated, or unsorted.",
                    ));
                }
                prior = Some(&entry.path);
            }
            let required = new_paths
                .iter()
                .cloned()
                .chain(std::iter::once(INSTALL_RECORD_FILE_NAME.to_lowercase()));
            for required in required {
                let entry = apply
                    .iter()
                    .find(|candidate| candidate.path.to_lowercase() == required);
                if entry.map(|entry| entry.operation) != Some(SelfUpdateApplyOperation::Install) {
                    return Err(UpdateError::failed(
                        "The self-update apply journal does not install every new owned file.",
                    ));
                }
            }
            if apply.iter().any(|entry| {
                entry.operation == SelfUpdateApplyOperation::Remove
                    && (new_paths.contains(&entry.path.to_lowercase())
                        || entry.path.eq_ignore_ascii_case(INSTALL_RECORD_FILE_NAME))
            }) {
                return Err(UpdateError::failed(
                    "The self-update journal removes a new or reserved file.",
                ));
            }
        }
        let transaction_directory = self.transaction_directory(&plan.transaction_id)?;
        if !is_contained(&self.transactions_directory, &transaction_directory)
            || !is_contained(
                &layout.container_directory()?,
                &self.target_transaction_directory(plan)?,
            )
        {
            return Err(UpdateError::failed(
                "A self-update transaction path escaped.",
            ));
        }
        Ok(())
    }

    // --- Residue ----------------------------------------------------------------------------

    fn cleanup_data_residue(&self, keep_transaction_id: Option<&str>) {
        if let Ok(entries) = std::fs::read_dir(&self.transactions_directory) {
            for entry in entries.flatten() {
                let name = entry.file_name().to_string_lossy().into_owned();
                if entry.path().is_dir()
                    && ids::is_hex32(&name)
                    && Some(name.as_str()) != keep_transaction_id
                {
                    try_delete_directory(&entry.path());
                }
            }
        }
        let Ok(entries) = std::fs::read_dir(&self.root_directory) else {
            return;
        };
        const PREFIX: &str = ".pending.json.";
        const SUFFIX: &str = ".tmp";
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().into_owned();
            let is_owned_temp = name.len() == PREFIX.len() + 32 + SUFFIX.len()
                && name.starts_with(PREFIX)
                && name.ends_with(SUFFIX)
                && ids::is_hex32(&name[PREFIX.len()..PREFIX.len() + 32]);
            if is_owned_temp && entry.path().is_file() {
                fsutil::try_delete_file(&entry.path());
            }
        }
    }

    fn has_reclaimable_residue(
        &self,
        keep_data_transaction_id: Option<&str>,
        target_directory: &Path,
        keep_target_transaction_id: Option<&str>,
    ) -> bool {
        let data = std::fs::read_dir(&self.transactions_directory).is_ok_and(|entries| {
            entries.flatten().any(|entry| {
                let name = entry.file_name().to_string_lossy().into_owned();
                entry.path().is_dir()
                    && ids::is_hex32(&name)
                    && Some(name.as_str()) != keep_data_transaction_id
            })
        });
        let target = target_transaction_ids(target_directory)
            .into_iter()
            .any(|id| Some(id.as_str()) != keep_target_transaction_id);
        data || target
    }
}

// --- Free functions ------------------------------------------------------------------------

fn create_dir(path: &Path) -> Result<(), UpdateError> {
    std::fs::create_dir_all(path)
        .map_err(|error| io_error(&format!("'{}' could not be created", path.display()), error))
}

fn valid_mode(mode: i64) -> bool {
    (0..=0o777).contains(&mode)
}

fn layout_for(plan: &SelfUpdatePlan) -> Result<LauncherInstallationLayout, UpdateError> {
    match plan.installation_kind {
        LauncherInstallationKind::MacBundle => {
            LauncherInstallationLayout::mac_bundle(&plan.target_directory, &plan.rid)
        }
        LauncherInstallationKind::Flat => {
            LauncherInstallationLayout::flat(&plan.target_directory, &plan.rid)
        }
    }
}

fn validate_layout(layout: &LauncherInstallationLayout, rid: &str) -> Result<(), UpdateError> {
    if layout.kind == LauncherInstallationKind::MacBundle
        && (!rid.starts_with("osx-")
            || LauncherInstallationLayout::mac_bundle(&layout.installed_root, rid)
                .ok()
                .as_ref()
                != Some(layout))
    {
        return Err(UpdateError::failed(
            "The macOS launcher installation layout is invalid.",
        ));
    }
    Ok(())
}

fn validate_payload_layout(
    files: &[ExtractedFileRecord],
    layout: &LauncherInstallationLayout,
) -> Result<(), UpdateError> {
    if layout.kind != LauncherInstallationKind::MacBundle {
        return Ok(());
    }
    let prefix = format!("{}/", layout.payload_root);
    let info_plist = format!("{}/Contents/Info.plist", layout.payload_root);
    let launcher = layout.payload_launcher_path();
    if files.iter().any(|file| !file.path.starts_with(&prefix))
        || !files.iter().any(|file| file.path == launcher)
        || !files.iter().any(|file| file.path == info_plist)
    {
        return Err(UpdateError::failed(
            "The macOS launcher ZIP must contain exactly the OpenAC.app bundle root and launcher \
             executable.",
        ));
    }
    Ok(())
}

fn validate_mac_bundle_executable(
    files: &[ExtractedFileRecord],
    layout: &LauncherInstallationLayout,
) -> Result<(), UpdateError> {
    let launcher = layout.payload_launcher_path();
    let matching: Vec<&ExtractedFileRecord> =
        files.iter().filter(|file| file.path == launcher).collect();
    match matching.as_slice() {
        [file] if file.unix_mode & 0o100 != 0 => Ok(()),
        _ => Err(UpdateError::failed(
            "The macOS launcher ZIP lacks an executable app-bundle launcher.",
        )),
    }
}

fn build_apply_journal(
    plan: &SelfUpdatePlan,
    previous: Option<&LauncherBinaryInstallRecord>,
    target_directory: &Path,
    cancel: &CancelToken,
) -> Result<Vec<SelfUpdateApplyEntry>, UpdateError> {
    // Keyed ignoring case (one entry per file on every file system), applied
    // in ordinal path order.
    let mut seen = HashSet::new();
    let mut operations: BTreeMap<String, SelfUpdateApplyOperation> = BTreeMap::new();
    let mut add = |path: &str, operation| {
        if seen.insert(path.to_lowercase()) {
            operations.insert(path.to_owned(), operation);
            true
        } else {
            false
        }
    };
    for file in &plan.files {
        if !add(&file.path, SelfUpdateApplyOperation::Install) {
            return Err(UpdateError::failed(
                "The self-update file list is invalid, duplicated, or unsorted.",
            ));
        }
    }
    if !add(INSTALL_RECORD_FILE_NAME, SelfUpdateApplyOperation::Install) {
        return Err(UpdateError::failed(
            "The self-update file list has a reserved path or lacks the launcher executable.",
        ));
    }
    if let Some(previous) = previous {
        for file in &previous.files {
            add(&file.path, SelfUpdateApplyOperation::Remove);
        }
    }

    let launcher = launcher_file_name(&plan.rid);
    let mut result = Vec::with_capacity(operations.len());
    for (path, operation) in operations {
        let target_path = resolve_contained(target_directory, &path)?;
        ensure_safe_parent(target_directory, &target_path)?;
        let prior = capture_optional_file_metadata(
            &target_path,
            &format!("Self-update target '{path}'"),
            cancel,
        )?;
        let had_original = prior.is_some();
        if operation == SelfUpdateApplyOperation::Remove && !had_original {
            return Err(UpdateError::failed(format!(
                "Owned obsolete launcher file '{path}' is missing."
            )));
        }
        if path.eq_ignore_ascii_case(&launcher) && !had_original {
            return Err(UpdateError::failed(
                "The canonical launcher executable is missing before self-update.",
            ));
        }
        result.push(SelfUpdateApplyEntry {
            path,
            operation,
            had_original,
            prior_sha256: prior.as_ref().map(|prior| prior.sha256.clone()),
            prior_size: prior.as_ref().map(|prior| prior.size),
            prior_unix_mode: prior.as_ref().map(|prior| prior.unix_mode),
            replacement_sha256: None,
            replacement_size: None,
            replacement_unix_mode: None,
        });
    }
    Ok(result)
}

fn validate_rollback_tree(plan: &SelfUpdatePlan, swap: &Path) -> Result<(), UpdateError> {
    require_transaction_container(&swap.join("incoming"), true)?;
    require_transaction_container(&swap.join("backup"), false)?;
    require_transaction_container(&swap.join("rollback-discard"), false)?;
    let mut allowed: HashSet<String> = HashSet::from(["incoming".to_owned()]);
    let mut add = |container: &str, relative: &str| {
        allowed.insert(container.to_owned());
        let mut current = container.to_owned();
        for segment in relative.split('/') {
            current.push('/');
            current.push_str(segment);
            allowed.insert(current.clone());
        }
    };
    for entry in plan.apply.as_deref().unwrap_or_default() {
        if entry.operation == SelfUpdateApplyOperation::Install {
            add("incoming", &entry.path);
            add("rollback-discard", &entry.path);
        }
        if entry.had_original {
            add("backup", &entry.path);
        }
    }
    for relative in list_entries(swap)? {
        if !allowed.contains(&relative) {
            return Err(UpdateError::failed(format!(
                "The rollback transaction contains unrecorded path '{relative}'."
            )));
        }
    }
    Ok(())
}

fn require_transaction_container(path: &Path, required: bool) -> Result<(), UpdateError> {
    let name = path
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default();
    match std::fs::symlink_metadata(path) {
        Ok(metadata) if metadata.is_dir() && !fsutil::metadata_is_link(&metadata) => Ok(()),
        Ok(_) => Err(UpdateError::failed(format!(
            "Rollback container '{name}' is not a safe directory."
        ))),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            if required {
                Err(UpdateError::failed(format!(
                    "Required rollback container '{name}' is missing."
                )))
            } else {
                Ok(())
            }
        }
        Err(error) => Err(io_error("A rollback container could not be read", error)),
    }
}

fn ambiguous_rollback(path: &str) -> UpdateError {
    UpdateError::failed(format!(
        "Rollback state for '{path}' is corrupt or ambiguous; transaction evidence was preserved."
    ))
}

fn require_missing(
    metadata: Option<&JournalFileMetadata>,
    path: &str,
    location: &str,
) -> Result<(), UpdateError> {
    if metadata.is_some() {
        return Err(UpdateError::failed(format!(
            "Rollback {location} for '{path}' is unexpected; transaction evidence was preserved."
        )));
    }
    Ok(())
}

fn verify_restored_prior(
    plan: &SelfUpdatePlan,
    target_directory: &Path,
    cancel: &CancelToken,
) -> Result<(), UpdateError> {
    if plan.installation_kind == LauncherInstallationKind::MacBundle {
        return verify_bundle_restored(plan, cancel);
    }
    let apply = plan
        .apply
        .as_deref()
        .ok_or_else(|| UpdateError::failed("The rollback receipt is missing its apply journal."))?;
    for entry in apply {
        cancel.check()?;
        let target_path = resolve_contained(target_directory, &entry.path)?;
        ensure_existing_parents_safe(target_directory, &target_path)?;
        if entry.had_original {
            verify_prior_file(entry, &target_path, cancel)?;
        } else {
            ensure_path_missing(
                &target_path,
                &format!("Restored rollback target '{}'", entry.path),
            )?;
        }
    }
    Ok(())
}

fn verify_bundle_restored(plan: &SelfUpdatePlan, cancel: &CancelToken) -> Result<(), UpdateError> {
    cancel.check()?;
    let layout = layout_for(plan)?;
    if !layout.installed_root.is_dir()
        || !layout.launcher_path()?.is_file()
        || fsutil::is_link(&layout.installed_root).unwrap_or(true)
    {
        return Err(UpdateError::failed(
            "The prior macOS app bundle was not restored.",
        ));
    }
    Ok(())
}

fn verify_prior_file(
    entry: &SelfUpdateApplyEntry,
    path: &Path,
    cancel: &CancelToken,
) -> Result<(), UpdateError> {
    let actual = capture_required_file_metadata(
        path,
        &format!("Prior launcher file '{}'", entry.path),
        cancel,
    )?;
    require_prior_metadata(entry, &actual, "prior launcher file")
}

fn verify_replacement_file(
    entry: &SelfUpdateApplyEntry,
    path: &Path,
    cancel: &CancelToken,
) -> Result<(), UpdateError> {
    let actual = capture_required_file_metadata(
        path,
        &format!("Replacement launcher file '{}'", entry.path),
        cancel,
    )?;
    require_replacement_metadata(entry, &actual, "replacement launcher file")
}

fn require_prior_metadata(
    entry: &SelfUpdateApplyEntry,
    actual: &JournalFileMetadata,
    description: &str,
) -> Result<(), UpdateError> {
    require_metadata(
        &entry.path,
        description,
        actual,
        entry.prior_sha256.as_deref(),
        entry.prior_size,
        entry.prior_unix_mode,
    )
}

fn require_replacement_metadata(
    entry: &SelfUpdateApplyEntry,
    actual: &JournalFileMetadata,
    description: &str,
) -> Result<(), UpdateError> {
    require_metadata(
        &entry.path,
        description,
        actual,
        entry.replacement_sha256.as_deref(),
        entry.replacement_size,
        entry.replacement_unix_mode,
    )
}

fn require_metadata(
    path: &str,
    description: &str,
    actual: &JournalFileMetadata,
    expected_sha256: Option<&str>,
    expected_size: Option<i64>,
    expected_unix_mode: Option<i64>,
) -> Result<(), UpdateError> {
    if !expected_sha256.is_some_and(|expected| actual.sha256.eq_ignore_ascii_case(expected))
        || Some(actual.size) != expected_size
        || Some(actual.unix_mode) != expected_unix_mode
    {
        return Err(UpdateError::failed(format!(
            "The {description} '{path}' failed its rollback integrity check; transaction \
             evidence was preserved."
        )));
    }
    Ok(())
}

fn capture_required_file_metadata(
    path: &Path,
    description: &str,
    cancel: &CancelToken,
) -> Result<JournalFileMetadata, UpdateError> {
    capture_optional_file_metadata(path, description, cancel)?
        .ok_or_else(|| UpdateError::failed(format!("{description} is missing.")))
}

fn file_unix_mode(path: &Path) -> Result<i64, UpdateError> {
    Ok(fsutil::unix_mode(path)
        .map_err(|error| io_error("A file's mode could not be read", error))?
        .map_or(0, |mode| i64::from(mode & 0o777)))
}

fn capture_optional_file_metadata(
    path: &Path,
    description: &str,
    cancel: &CancelToken,
) -> Result<Option<JournalFileMetadata>, UpdateError> {
    let before = match std::fs::symlink_metadata(path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(io_error(description, error)),
    };
    if before.is_dir() || fsutil::metadata_is_link(&before) {
        return Err(UpdateError::failed(format!(
            "{description} is a directory or reparse point."
        )));
    }
    let size = before.len();
    let unix_mode = file_unix_mode(path)?;
    let sha256 = integrity::compute_sha256_hex_cancellable(path, cancel)
        .map_err(|error| io_error(description, error))?;
    cancel.check()?;
    let changed = match std::fs::symlink_metadata(path) {
        Ok(after) => {
            after.is_dir()
                || fsutil::metadata_is_link(&after)
                || after.len() != size
                || file_unix_mode(path)? != unix_mode
        }
        Err(_) => true,
    };
    if changed {
        return Err(UpdateError::failed(format!(
            "{description} changed while it was measured."
        )));
    }
    Ok(Some(JournalFileMetadata {
        sha256,
        size: i64::try_from(size).unwrap_or(i64::MAX),
        unix_mode,
    }))
}

fn ensure_path_missing(path: &Path, description: &str) -> Result<(), UpdateError> {
    match std::fs::symlink_metadata(path) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        _ => Err(UpdateError::failed(format!(
            "{description} already exists."
        ))),
    }
}

fn verify_applied_targets(
    plan: &SelfUpdatePlan,
    target_directory: &Path,
    cancel: &CancelToken,
) -> Result<(), UpdateError> {
    let layout = layout_for(plan)?;
    for file in &plan.files {
        if plan.installation_kind == LauncherInstallationKind::MacBundle {
            let prefix = format!("{}/", layout.payload_root);
            let installed = InstalledFileRecord {
                path: file.path[prefix.len().min(file.path.len())..].to_owned(),
                ..file.clone()
            };
            verify_file(target_directory, &installed, "Applied launcher", cancel)?;
        } else {
            verify_file(target_directory, file, "Applied launcher", cancel)?;
        }
    }
    for obsolete in plan
        .apply
        .as_deref()
        .unwrap_or_default()
        .iter()
        .filter(|entry| entry.operation == SelfUpdateApplyOperation::Remove)
    {
        let path = resolve_contained(target_directory, &obsolete.path)?;
        if std::fs::symlink_metadata(&path).is_ok() {
            return Err(UpdateError::failed(format!(
                "Obsolete launcher file '{}' remains after apply.",
                obsolete.path
            )));
        }
    }
    Ok(())
}

fn verify_file(
    root: &Path,
    file: &InstalledFileRecord,
    description: &str,
    cancel: &CancelToken,
) -> Result<(), UpdateError> {
    cancel.check()?;
    let path = resolve_contained(root, &file.path)?;
    ensure_safe_parent(root, &path)?;
    let intact = std::fs::symlink_metadata(&path).is_ok_and(|metadata| {
        metadata.is_file() && !fsutil::metadata_is_link(&metadata) && metadata.len() == file.size
    });
    if !intact {
        return Err(UpdateError::failed(format!(
            "{description} file '{}' is missing, linked, or corrupt.",
            file.path
        )));
    }
    let sha256 = integrity::compute_sha256_hex_cancellable(&path, cancel)
        .map_err(|error| io_error(description, error))?;
    if !sha256.eq_ignore_ascii_case(&file.sha256) {
        return Err(UpdateError::failed(format!(
            "{description} file '{}' SHA-256 is corrupt.",
            file.path
        )));
    }
    if cfg!(unix) && file_unix_mode(&path)? != i64::from(file.unix_mode) {
        return Err(UpdateError::failed(format!(
            "{description} file '{}' mode is corrupt.",
            file.path
        )));
    }
    Ok(())
}

fn read_and_verify_install_record(
    target_directory: &Path,
    rid: &str,
    cancel: &CancelToken,
) -> Result<Option<LauncherBinaryInstallRecord>, UpdateError> {
    let path = target_directory.join(INSTALL_RECORD_FILE_NAME);
    if !path.is_file() {
        return Ok(None);
    }
    if fsutil::is_link(&path).unwrap_or(true) {
        return Err(UpdateError::failed(
            "The launcher ownership record is linked.",
        ));
    }
    let bytes = std::fs::read(&path)
        .map_err(|error| io_error("The launcher ownership record could not be read", error))?;
    let record = parse_install_record(&bytes).map_err(|message| {
        UpdateError::failed(format!(
            "The launcher ownership record is invalid: {message}"
        ))
    })?;
    validate_install_record(record.as_ref(), rid)?;
    let record =
        record.ok_or_else(|| UpdateError::failed("The launcher ownership record is invalid."))?;
    for file in &record.files {
        verify_file(target_directory, file, "Owned launcher", cancel)?;
    }
    Ok(Some(record))
}

fn verify_installed_ownership_matches_plan(
    plan: &SelfUpdatePlan,
    target_directory: &Path,
    cancel: &CancelToken,
) -> Result<(), UpdateError> {
    let record = read_and_verify_install_record(target_directory, &plan.rid, cancel)?;
    if !record.is_some_and(|record| record.version == plan.version && record.files == plan.files) {
        return Err(UpdateError::failed(
            "The installed launcher ownership record does not match the pending plan.",
        ));
    }
    Ok(())
}

fn migrate_plan(mut plan: SelfUpdatePlan) -> SelfUpdatePlan {
    if plan.schema_version == 3 {
        plan.schema_version = PLAN_SCHEMA_VERSION;
        plan.installation_kind = LauncherInstallationKind::Flat;
    }
    plan
}

fn validate_install_record(
    record: Option<&LauncherBinaryInstallRecord>,
    rid: &str,
) -> Result<(), UpdateError> {
    let Some(record) = record.filter(|record| {
        record.schema_version == INSTALL_RECORD_SCHEMA_VERSION
            && LauncherVersion::try_parse(&record.version).is_some()
            && record.rid == rid
    }) else {
        return Err(UpdateError::failed(
            "The launcher ownership record is invalid.",
        ));
    };
    validate_file_records(&record.files, "launcher ownership file list")?;
    let launcher = launcher_file_name(rid);
    if record
        .files
        .iter()
        .any(|file| file.path.eq_ignore_ascii_case(INSTALL_RECORD_FILE_NAME))
        || !record
            .files
            .iter()
            .any(|file| file.path.eq_ignore_ascii_case(&launcher))
    {
        return Err(UpdateError::failed(
            "The launcher ownership record contains its reserved metadata path or lacks the \
             canonical launcher executable.",
        ));
    }
    Ok(())
}

fn validate_file_records(
    files: &[InstalledFileRecord],
    description: &str,
) -> Result<(), UpdateError> {
    if files.is_empty() {
        return Err(UpdateError::failed(format!("The {description} is empty.")));
    }
    let mut paths = HashSet::new();
    let mut prior: Option<&str> = None;
    for file in files {
        if !is_normalized_relative(&file.path)
            || !paths.insert(file.path.to_lowercase())
            || !is_sha256(&file.sha256)
            || file.unix_mode > 0o777
            || prior.is_some_and(|prior| prior >= file.path.as_str())
        {
            return Err(UpdateError::failed(format!(
                "The {description} is invalid, duplicated, or unsorted."
            )));
        }
        prior = Some(&file.path);
    }
    Ok(())
}

fn copy_file_durably(source: &Path, destination: &Path) -> Result<(), UpdateError> {
    use std::io::Write;
    let copy = || -> std::io::Result<()> {
        let mut input = std::fs::File::open(source)?;
        let mut output = fsutil::create_new(destination, false)?;
        std::io::copy(&mut input, &mut output)?;
        output.flush()?;
        output.sync_all()
    };
    copy().map_err(|error| io_error("A launcher file could not be copied", error))
}

fn normalize_target_directory(target_directory: &Path) -> Result<PathBuf, UpdateError> {
    let target = normalize_target_path(target_directory)?;
    let safe = std::fs::symlink_metadata(&target)
        .is_ok_and(|metadata| metadata.is_dir() && !fsutil::metadata_is_link(&metadata));
    if !safe {
        return Err(UpdateError::failed(
            "The self-update target directory is missing or is a reparse point.",
        ));
    }
    Ok(target)
}

fn normalize_target_path(target_directory: &Path) -> Result<PathBuf, UpdateError> {
    if target_directory.as_os_str().is_empty() || !target_directory.is_absolute() {
        return Err(UpdateError::failed(
            "The self-update target directory must be absolute.",
        ));
    }
    Ok(fsutil::full_path(target_directory))
}

fn ensure_safe_parent(root: &Path, file_path: &Path) -> Result<(), UpdateError> {
    let parent = file_path
        .parent()
        .ok_or_else(|| UpdateError::failed("A self-update target has no parent."))?;
    ensure_existing_parents_safe(root, file_path)?;
    create_dir(parent)?;
    ensure_existing_parents_safe(root, file_path)
}

fn ensure_existing_parents_safe(root: &Path, file_path: &Path) -> Result<(), UpdateError> {
    let mut directory = file_path
        .parent()
        .ok_or_else(|| UpdateError::failed("A self-update target has no parent."))?
        .to_path_buf();
    loop {
        if !is_contained(root, &directory) {
            return Ok(());
        }
        match std::fs::symlink_metadata(&directory) {
            Ok(metadata) => {
                if !metadata.is_dir() || fsutil::metadata_is_link(&metadata) {
                    return Err(UpdateError::failed(format!(
                        "Self-update target parent '{}' is not a safe directory.",
                        directory.display()
                    )));
                }
                if fsutil::paths_equal(&directory, root) {
                    return Ok(());
                }
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => {
                return Err(io_error(
                    "A self-update target parent could not be read",
                    error,
                ));
            }
        }
        match directory.parent() {
            Some(parent) => directory = parent.to_path_buf(),
            None => return Ok(()),
        }
    }
}

fn is_contained(root: &Path, path: &Path) -> bool {
    let full_root = fsutil::full_path(root);
    let full_path = fsutil::full_path(path);
    if fsutil::paths_equal(&full_root, &full_path) {
        return true;
    }
    let root_text = full_root.to_string_lossy().into_owned();
    let prefix = if root_text.ends_with(std::path::MAIN_SEPARATOR) {
        root_text
    } else {
        format!("{root_text}{}", std::path::MAIN_SEPARATOR)
    };
    let text = full_path.to_string_lossy().into_owned();
    if cfg!(windows) {
        text.to_lowercase().starts_with(&prefix.to_lowercase())
    } else {
        text.starts_with(&prefix)
    }
}

fn require_transaction_id(transaction_id: &str) -> Result<(), UpdateError> {
    if ids::is_hex32(transaction_id) {
        Ok(())
    } else {
        Err(UpdateError::failed(
            "The self-update transaction id is invalid.",
        ))
    }
}

fn cleanup_target_residue(target_directory: &Path, keep_transaction_id: Option<&str>) {
    for id in target_transaction_ids(target_directory) {
        if Some(id.as_str()) != keep_transaction_id {
            try_delete_directory(
                &target_directory.join(format!("{TARGET_TRANSACTION_PREFIX}{id}")),
            );
        }
    }
}

/// The transaction ids of the `.acdream-self-update-<id>` folders in
/// `directory`.
fn target_transaction_ids(directory: &Path) -> Vec<String> {
    let Ok(entries) = std::fs::read_dir(directory) else {
        return Vec::new();
    };
    entries
        .flatten()
        .filter(|entry| entry.path().is_dir())
        .filter_map(|entry| {
            let name = entry.file_name().to_string_lossy().into_owned();
            name.strip_prefix(TARGET_TRANSACTION_PREFIX)
                .filter(|id| ids::is_hex32(id))
                .map(str::to_owned)
        })
        .collect()
}

fn relative_of(root: &Path, path: &Path) -> String {
    path.strip_prefix(root)
        .unwrap_or(path)
        .components()
        .map(|component| component.as_os_str().to_string_lossy().into_owned())
        .collect::<Vec<_>>()
        .join("/")
}

/// Every file under `root`, relative and `/`-separated.
fn list_files(root: &Path) -> Result<Vec<String>, UpdateError> {
    Ok(walk(root)?
        .into_iter()
        .filter(|(_, is_directory)| !is_directory)
        .map(|(path, _)| path)
        .collect())
}

/// Every file and folder under `root`, relative and `/`-separated.
fn list_entries(root: &Path) -> Result<Vec<String>, UpdateError> {
    Ok(walk(root)?.into_iter().map(|(path, _)| path).collect())
}

fn walk(root: &Path) -> Result<Vec<(String, bool)>, UpdateError> {
    let io = |error: std::io::Error| io_error("A transaction folder could not be listed", error);
    let mut out = Vec::new();
    let mut pending = vec![root.to_path_buf()];
    while let Some(directory) = pending.pop() {
        for entry in std::fs::read_dir(&directory).map_err(io)? {
            let path = entry.map_err(io)?.path();
            let metadata = std::fs::symlink_metadata(&path).map_err(io)?;
            let is_directory = metadata.is_dir() && !fsutil::metadata_is_link(&metadata);
            out.push((relative_of(root, &path), is_directory));
            if is_directory {
                pending.push(path);
            }
        }
    }
    Ok(out)
}

// --- JSON -----------------------------------------------------------------------------------

fn file_records_out(files: &[InstalledFileRecord]) -> Out {
    Out::Array(
        files
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
            .collect(),
    )
}

fn optional_str(value: Option<&str>) -> Out {
    value.map_or(Out::Null, Out::str)
}

fn optional_int(value: Option<i64>) -> Out {
    value.map_or(Out::Null, Out::Int)
}

fn plan_json(plan: &SelfUpdatePlan) -> String {
    let apply = plan.apply.as_ref().map_or(Out::Null, |apply| {
        Out::Array(
            apply
                .iter()
                .map(|entry| {
                    ObjectBuilder::new()
                        .member("path", Out::str(&entry.path))
                        .member(
                            "operation",
                            Out::str(
                                OPERATION_NAMES
                                    .iter()
                                    .find(|(_, operation)| *operation == entry.operation)
                                    .map_or("install", |(name, _)| name),
                            ),
                        )
                        .member("hadOriginal", Out::Bool(entry.had_original))
                        .member("priorSha256", optional_str(entry.prior_sha256.as_deref()))
                        .member("priorSize", optional_int(entry.prior_size))
                        .member("priorUnixMode", optional_int(entry.prior_unix_mode))
                        .member(
                            "replacementSha256",
                            optional_str(entry.replacement_sha256.as_deref()),
                        )
                        .member("replacementSize", optional_int(entry.replacement_size))
                        .member(
                            "replacementUnixMode",
                            optional_int(entry.replacement_unix_mode),
                        )
                        .build()
                })
                .collect(),
        )
    });
    json::to_pretty(
        &ObjectBuilder::new()
            .member("schemaVersion", Out::Int(i64::from(plan.schema_version)))
            .member("transactionId", Out::str(&plan.transaction_id))
            .member("state", Out::str(plan.state.name()))
            .member("version", Out::str(&plan.version))
            .member("rid", Out::str(&plan.rid))
            .member(
                "targetDirectory",
                Out::str(plan.target_directory.to_string_lossy()),
            )
            // An enum without a name converter: OpenAC writes its number.
            .member(
                "installationKind",
                Out::Int(plan.installation_kind.number()),
            )
            .member("archiveSha256", Out::str(&plan.archive_sha256))
            .member("archiveSize", Out::Int(plan.archive_size))
            .member("files", file_records_out(&plan.files))
            .member("apply", apply)
            .build(),
    )
}

fn install_record_json(record: &LauncherBinaryInstallRecord) -> String {
    json::to_pretty(
        &ObjectBuilder::new()
            .member("schemaVersion", Out::Int(i64::from(record.schema_version)))
            .member("version", Out::str(&record.version))
            .member("rid", Out::str(&record.rid))
            .member("files", file_records_out(&record.files))
            .build(),
    )
}

fn parse_strict(bytes: &[u8]) -> Result<Node, String> {
    let root = host_json::parse(bytes).map_err(|error| error.to_string())?;
    if json::depth(&root) > 32 {
        return Err("nested too deeply".into());
    }
    Ok(root)
}

fn text(object: &mut ObjectReader<'_>, name: &str) -> Result<Option<String>, String> {
    match object.take(name) {
        Some(node) => host_json::string_or_null(node).map_err(|error| error.to_string()),
        None => Ok(None),
    }
}

fn integer(object: &mut ObjectReader<'_>, name: &str) -> Result<Option<i64>, String> {
    match object.take(name) {
        Some(node) if host_json::is_null(node) => Ok(None),
        Some(node) => json::int64(node)
            .map(Some)
            .map_err(|error| error.to_string()),
        None => Ok(None),
    }
}

fn int32(object: &mut ObjectReader<'_>, name: &str) -> Result<i32, String> {
    let value = integer(object, name)?.unwrap_or(0);
    i32::try_from(value).map_err(|_| format!("'{name}' is out of range"))
}

fn file_records(node: Option<&Node>) -> Result<Vec<InstalledFileRecord>, String> {
    let Some(node) = node.filter(|node| !host_json::is_null(node)) else {
        return Ok(Vec::new());
    };
    host_json::array_items(node)
        .map_err(|error| error.to_string())?
        .iter()
        .map(|item| {
            let mut file = ObjectReader::new(item).map_err(|error| error.to_string())?;
            let path = text(&mut file, "path")?.unwrap_or_default();
            let sha256 = text(&mut file, "sha256")?.unwrap_or_default();
            let size = integer(&mut file, "size")?.unwrap_or(0);
            let unix_mode = integer(&mut file, "unixMode")?.unwrap_or(0);
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
        .collect()
}

fn parse_plan(bytes: &[u8]) -> Result<Option<SelfUpdatePlan>, String> {
    let root = parse_strict(bytes)?;
    if host_json::is_null(&root) {
        return Ok(None);
    }
    let mut object = ObjectReader::new(&root).map_err(|error| error.to_string())?;
    let schema_version = int32(&mut object, "schemaVersion")?;
    let transaction_id = text(&mut object, "transactionId")?.unwrap_or_default();
    let state = match object.take("state") {
        Some(node) => {
            host_json::enumeration(node, &STATE_NAMES).map_err(|error| error.to_string())?
        }
        None => SelfUpdatePlanState::Staged,
    };
    let version = text(&mut object, "version")?.unwrap_or_default();
    let rid = text(&mut object, "rid")?.unwrap_or_default();
    let target_directory = PathBuf::from(text(&mut object, "targetDirectory")?.unwrap_or_default());
    let installation_kind = match integer(&mut object, "installationKind")?.unwrap_or(0) {
        0 => LauncherInstallationKind::Flat,
        1 => LauncherInstallationKind::MacBundle,
        _ => return Err("The self-update installation layout is invalid.".into()),
    };
    let archive_sha256 = text(&mut object, "archiveSha256")?.unwrap_or_default();
    let archive_size = integer(&mut object, "archiveSize")?.unwrap_or(0);
    let files = file_records(object.take("files"))?;
    let apply = match object.take("apply") {
        Some(node) if !host_json::is_null(node) => Some(
            host_json::array_items(node)
                .map_err(|error| error.to_string())?
                .iter()
                .map(parse_apply_entry)
                .collect::<Result<Vec<_>, String>>()?,
        ),
        _ => None,
    };
    object.finish().map_err(|error| error.to_string())?;
    Ok(Some(SelfUpdatePlan {
        schema_version,
        transaction_id,
        state,
        version,
        rid,
        target_directory,
        installation_kind,
        archive_sha256,
        archive_size,
        files,
        apply,
    }))
}

fn parse_apply_entry(node: &Node) -> Result<SelfUpdateApplyEntry, String> {
    let mut entry = ObjectReader::new(node).map_err(|error| error.to_string())?;
    let path = text(&mut entry, "path")?.unwrap_or_default();
    let operation = match entry.take("operation") {
        Some(node) => {
            host_json::enumeration(node, &OPERATION_NAMES).map_err(|error| error.to_string())?
        }
        None => SelfUpdateApplyOperation::Install,
    };
    let had_original = match entry.take("hadOriginal") {
        Some(node) => host_json::boolean(node).map_err(|error| error.to_string())?,
        None => false,
    };
    let result = SelfUpdateApplyEntry {
        path,
        operation,
        had_original,
        prior_sha256: text(&mut entry, "priorSha256")?,
        prior_size: integer(&mut entry, "priorSize")?,
        prior_unix_mode: integer(&mut entry, "priorUnixMode")?,
        replacement_sha256: text(&mut entry, "replacementSha256")?,
        replacement_size: integer(&mut entry, "replacementSize")?,
        replacement_unix_mode: integer(&mut entry, "replacementUnixMode")?,
    };
    entry.finish().map_err(|error| error.to_string())?;
    Ok(result)
}

fn parse_install_record(bytes: &[u8]) -> Result<Option<LauncherBinaryInstallRecord>, String> {
    let root = parse_strict(bytes)?;
    if host_json::is_null(&root) {
        return Ok(None);
    }
    let mut object = ObjectReader::new(&root).map_err(|error| error.to_string())?;
    let schema_version = int32(&mut object, "schemaVersion")?;
    let version = text(&mut object, "version")?.unwrap_or_default();
    let rid = text(&mut object, "rid")?.unwrap_or_default();
    let files = file_records(object.take("files"))?;
    object.finish().map_err(|error| error.to_string())?;
    Ok(Some(LauncherBinaryInstallRecord {
        schema_version,
        version,
        rid,
        files,
    }))
}
