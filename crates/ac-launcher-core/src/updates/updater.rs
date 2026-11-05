//! The updater: checks the feed, installs a newer client (download, verify,
//! extract, activate) and rolls back, and stages a newer launcher, refusing
//! while sessions run.

use std::path::Path;
use std::sync::{Arc, Mutex};

use super::UpdateError;
use super::client_store::{ClientVersionResolution, ClientVersionStore};
use super::downloader::VerifiedArtifactDownloader;
use super::manifest::{LauncherFingerprintDocument, ReleaseManifest, ReleaseManifestSource};
use super::payload::{executables_for_payload, is_valid_rid};
use super::self_update::{
    LauncherInstallationLayout, LauncherSelfUpdateManager, SelfUpdateStageResult,
};
use super::version::LauncherVersion;
use super::zip::{SafeZipExtractor, try_delete_directory};
use crate::cancel::CancelToken;
use crate::fsutil;
use crate::ids;
use crate::net::HttpTransport;

/// Where an update operation is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LauncherUpdatePhase {
    /// Nothing running.
    Idle,
    /// Reading the feed.
    Checking,
    /// Downloading the client archive.
    DownloadingClient,
    /// Checking and extracting it.
    ExtractingClient,
    /// Switching the activation pointer.
    ActivatingClient,
    /// Downloading the launcher.
    DownloadingLauncher,
    /// Staging the launcher.
    StagingLauncher,
    /// Activating the previous client.
    RollingBack,
    /// Done.
    Completed,
    /// Cancelled; nothing changed.
    Cancelled,
    /// Failed.
    Failed,
}

/// One progress report.
#[derive(Debug, Clone, PartialEq)]
pub struct LauncherUpdateProgress {
    /// The phase.
    pub phase: LauncherUpdatePhase,
    /// A line for the player.
    pub status: String,
    /// Work done.
    pub completed: u64,
    /// Work in all.
    pub total: u64,
}

impl LauncherUpdateProgress {
    /// Percent done, 0 to 100.
    pub fn percent(&self) -> f64 {
        if self.total == 0 {
            0.0
        } else {
            (self.completed as f64 * 100.0 / self.total as f64).clamp(0.0, 100.0)
        }
    }
}

/// What a check found.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherUpdateCheckResult {
    /// The release read.
    pub manifest: ReleaseManifest,
    /// This platform.
    pub rid: String,
    /// The running launcher's version.
    pub launcher_version: LauncherVersion,
    /// The verified installed client, if any.
    pub installed_client_version: Option<LauncherVersion>,
    /// A newer client (or any client, when none is installed) is offered.
    pub is_client_update_available: bool,
    /// A newer launcher is offered.
    pub is_launcher_update_available: bool,
    /// This launcher may install the release's client.
    pub is_launcher_minimum_satisfied: bool,
    /// A line for the player.
    pub status: String,
}

/// The client updater.
pub struct LauncherUpdater {
    manifest_source: Arc<dyn ReleaseManifestSource>,
    versions: Arc<ClientVersionStore>,
    downloader: VerifiedArtifactDownloader,
    extractor: SafeZipExtractor,
    launcher_version: LauncherVersion,
    rid: String,
    has_running_sessions: Box<dyn Fn() -> bool + Send + Sync>,
    operation_gate: Mutex<()>,
    self_updates: Option<(Arc<LauncherSelfUpdateManager>, LauncherInstallationLayout)>,
    launcher_fingerprint: Option<String>,
}

/// Whether the release has a new launcher for this one, and whether this
/// launcher may install the release's client. With both fingerprints known
/// and the published one belonging to this release, the launcher updates
/// only when its fingerprint differs, and one that matches is this
/// release's launcher whatever its version says. Otherwise the versions
/// decide.
pub fn decide_launcher(
    manifest: &ReleaseManifest,
    running: &LauncherVersion,
    running_fingerprint: Option<&str>,
    published: Option<&LauncherFingerprintDocument>,
) -> (bool, bool) {
    if let (Some(running_fingerprint), Some(published)) = (running_fingerprint, published)
        && published.version == manifest.version
    {
        let same = published
            .fingerprint
            .eq_ignore_ascii_case(running_fingerprint);
        return (
            !same && manifest.version > *running,
            same || *running >= manifest.minimum_launcher_version,
        );
    }
    (
        manifest.version > *running,
        *running >= manifest.minimum_launcher_version,
    )
}

impl std::fmt::Debug for LauncherUpdater {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LauncherUpdater")
            .field("rid", &self.rid)
            .field("launcher_version", &self.launcher_version)
            .finish_non_exhaustive()
    }
}

fn report(
    progress: &mut dyn FnMut(LauncherUpdateProgress),
    phase: LauncherUpdatePhase,
    status: impl Into<String>,
    completed: u64,
    total: u64,
) {
    progress(LauncherUpdateProgress {
        phase,
        status: status.into(),
        completed,
        total,
    });
}

fn thousands(value: u64) -> String {
    let digits = value.to_string();
    let mut out = String::new();
    for (index, digit) in digits.chars().enumerate() {
        if index > 0 && (digits.len() - index).is_multiple_of(3) {
            out.push(',');
        }
        out.push(digit);
    }
    out
}

impl LauncherUpdater {
    /// An updater. `has_running_sessions` is asked before every change.
    pub fn new(
        manifest_source: Arc<dyn ReleaseManifestSource>,
        transport: Arc<dyn HttpTransport>,
        versions: Arc<ClientVersionStore>,
        launcher_version: LauncherVersion,
        rid: &str,
        has_running_sessions: impl Fn() -> bool + Send + Sync + 'static,
    ) -> Result<Self, UpdateError> {
        if !is_valid_rid(rid) {
            return Err(UpdateError::failed("RID is invalid."));
        }
        Ok(Self {
            manifest_source,
            versions,
            downloader: VerifiedArtifactDownloader::new(transport),
            extractor: SafeZipExtractor::default(),
            launcher_version,
            rid: rid.to_owned(),
            has_running_sessions: Box::new(has_running_sessions),
            operation_gate: Mutex::new(()),
            self_updates: None,
            launcher_fingerprint: None,
        })
    }

    /// Lets the updater stage the launcher's own update through `manager`
    /// into the running installation `layout`.
    #[must_use]
    pub fn with_self_updates(
        mut self,
        manager: Arc<LauncherSelfUpdateManager>,
        layout: LauncherInstallationLayout,
    ) -> Self {
        self.self_updates = Some((manager, layout));
        self
    }

    /// The fingerprint this launcher was built with (a release build's);
    /// `None` or blank (a developer build) compares versions only.
    #[must_use]
    pub fn with_launcher_fingerprint(mut self, fingerprint: Option<&str>) -> Self {
        self.launcher_fingerprint = fingerprint
            .filter(|fingerprint| !fingerprint.trim().is_empty())
            .map(str::to_owned);
        self
    }

    /// Replaces the extractor (tests use tighter limits).
    #[must_use]
    pub fn with_extractor(mut self, extractor: SafeZipExtractor) -> Self {
        self.extractor = extractor;
        self
    }

    /// The installed client as last resolved.
    pub fn current_client(&self) -> ClientVersionResolution {
        self.versions.cached_resolution()
    }

    /// Resolves (and if needed recovers) the installed client.
    pub fn initialize(&self, cancel: &CancelToken) -> Result<ClientVersionResolution, UpdateError> {
        self.versions.load_and_recover(&self.rid, cancel)
    }

    fn gate(&self) -> std::sync::MutexGuard<'_, ()> {
        self.operation_gate
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    /// Reads the feed and says what is available.
    pub fn check(&self, cancel: &CancelToken) -> Result<LauncherUpdateCheckResult, UpdateError> {
        let _gate = self.gate();
        let manifest = self.manifest_source.fetch(cancel)?;
        manifest.require_client(&self.rid)?;
        manifest.require_launcher(&self.rid)?;
        let installed = self.versions.cached_resolution();
        let installed_version = if installed.is_verified() {
            installed.version
        } else {
            None
        };
        let client_available = installed_version
            .as_ref()
            .is_none_or(|installed| manifest.version > *installed);
        let published = match &self.launcher_fingerprint {
            Some(_) => self.manifest_source.fetch_launcher_fingerprint(cancel)?,
            None => None,
        };
        let (launcher_available, minimum_satisfied) = decide_launcher(
            &manifest,
            &self.launcher_version,
            self.launcher_fingerprint.as_deref(),
            published.as_ref(),
        );
        let status = build_check_status(
            &manifest,
            installed_version.as_ref(),
            client_available,
            launcher_available,
            minimum_satisfied,
        );
        Ok(LauncherUpdateCheckResult {
            manifest,
            rid: self.rid.clone(),
            launcher_version: self.launcher_version.clone(),
            installed_client_version: installed_version,
            is_client_update_available: client_available,
            is_launcher_update_available: launcher_available,
            is_launcher_minimum_satisfied: minimum_satisfied,
            status,
        })
    }

    fn validate_check(&self, check: &LauncherUpdateCheckResult) -> Result<(), UpdateError> {
        if check.rid != self.rid || check.launcher_version != self.launcher_version {
            return Err(UpdateError::failed(
                "The update check belongs to a different launcher runtime.",
            ));
        }
        check.manifest.require_client(&self.rid)?;
        check.manifest.require_launcher(&self.rid)?;
        Ok(())
    }

    fn refuse_running_sessions(&self) -> Result<(), UpdateError> {
        if (self.has_running_sessions)() {
            Err(UpdateError::failed(
                "Stop every launcher session before installing or rolling back an update.",
            ))
        } else {
            Ok(())
        }
    }

    /// Installs the checked release's client and activates it.
    pub fn install_client(
        &self,
        check: &LauncherUpdateCheckResult,
        progress: &mut dyn FnMut(LauncherUpdateProgress),
        cancel: &CancelToken,
    ) -> Result<ClientVersionResolution, UpdateError> {
        self.validate_check(check)?;
        let _gate = self.gate();
        self.refuse_running_sessions()?;
        let _lease = self.versions.barrier().acquire_exclusive()?;
        self.refuse_running_sessions()?;
        let current = self
            .versions
            .load_and_recover_under_lease(&self.rid, cancel)?;
        if !check.is_launcher_minimum_satisfied {
            return Err(UpdateError::failed(format!(
                "Client {} requires launcher {} or newer. Stage the launcher update first.",
                check.manifest.version, check.manifest.minimum_launcher_version
            )));
        }
        if current.is_verified()
            && current
                .version
                .as_ref()
                .is_some_and(|version| *version >= check.manifest.version)
        {
            report(
                progress,
                LauncherUpdatePhase::Completed,
                format!(
                    "Client {} is already current.",
                    current
                        .version
                        .as_ref()
                        .map(LauncherVersion::value)
                        .unwrap_or_default()
                ),
                1,
                1,
            );
            return Ok(current);
        }
        let artifact = check.manifest.require_client(&self.rid)?.clone();
        let transaction = ids::unique_hex32();
        let staging = self
            .versions
            .app_directory()
            .join(format!(".client-staging-{transaction}"));
        let archive = self
            .versions
            .app_directory()
            .join(format!(".client-download-{transaction}.zip"));
        let version = check.manifest.version.clone();
        let result: Result<ClientVersionResolution, UpdateError> = (|| {
            report(
                progress,
                LauncherUpdatePhase::DownloadingClient,
                format!("Downloading client {version}..."),
                0,
                artifact.size,
            );
            self.downloader.download_artifact(
                &artifact,
                &archive,
                &mut |value| {
                    report(
                        progress,
                        LauncherUpdatePhase::DownloadingClient,
                        format!(
                            "Downloading client {version}: {}/{} bytes",
                            thousands(value.bytes_received),
                            thousands(value.total_bytes)
                        ),
                        value.bytes_received,
                        value.total_bytes,
                    );
                },
                cancel,
            )?;
            report(
                progress,
                LauncherUpdatePhase::ExtractingClient,
                "Verifying paths and extracting the client archive...",
                0,
                0,
            );
            let files = self.extractor.extract(
                &archive,
                &staging,
                &executables_for_payload(&self.rid, false),
                cancel,
            )?;
            report(
                progress,
                LauncherUpdatePhase::ActivatingClient,
                format!("Atomically activating client {version}..."),
                0,
                0,
            );
            let result = self.versions.promote_and_activate_under_lease(
                &staging, &version, &self.rid, &artifact, &files, cancel,
            )?;
            report(
                progress,
                LauncherUpdatePhase::Completed,
                format!("Client {version} installed and activated."),
                1,
                1,
            );
            Ok(result)
        })();
        if let Err(error) = &result {
            if error.is_cancelled() {
                report(
                    progress,
                    LauncherUpdatePhase::Cancelled,
                    "Client update cancelled; the active version was not changed.",
                    0,
                    0,
                );
            } else {
                report(
                    progress,
                    LauncherUpdatePhase::Failed,
                    format!("Client update failed: {error}"),
                    0,
                    0,
                );
            }
        }
        fsutil::try_delete_file(&archive);
        // An interrupted replacement still needs its incoming folder.
        if !self.versions.has_pending_replacement() {
            try_delete_directory(&staging);
        }
        result
    }

    /// Stages the checked release's launcher; it is applied when the
    /// launcher next starts (or at once through
    /// [`super::self_update_bootstrap::try_apply_staged_update_now`]).
    pub fn stage_launcher(
        &self,
        check: &LauncherUpdateCheckResult,
        progress: &mut dyn FnMut(LauncherUpdateProgress),
        cancel: &CancelToken,
    ) -> Result<SelfUpdateStageResult, UpdateError> {
        self.validate_check(check)?;
        let _gate = self.gate();
        self.refuse_running_sessions()?;
        if check.manifest.version <= self.launcher_version {
            return Err(UpdateError::failed(format!(
                "Launcher {} is already current.",
                self.launcher_version
            )));
        }
        let (manager, layout) = self.self_updates.as_ref().ok_or_else(|| {
            UpdateError::failed("This launcher has no installation to stage its update into.")
        })?;
        let version = check.manifest.version.clone();
        report(
            progress,
            LauncherUpdatePhase::DownloadingLauncher,
            format!("Downloading launcher {version}..."),
            0,
            0,
        );
        let result = manager.stage(
            &check.manifest,
            &self.rid,
            layout,
            &mut |value| {
                report(
                    progress,
                    LauncherUpdatePhase::DownloadingLauncher,
                    format!(
                        "Downloading launcher {version}: {}/{} bytes",
                        thousands(value.bytes_received),
                        thousands(value.total_bytes)
                    ),
                    value.bytes_received,
                    value.total_bytes,
                );
            },
            cancel,
        );
        match &result {
            Ok(staged) => report(
                progress,
                LauncherUpdatePhase::StagingLauncher,
                staged.status.clone(),
                1,
                1,
            ),
            Err(error) if error.is_cancelled() => report(
                progress,
                LauncherUpdatePhase::Cancelled,
                "Launcher update staging cancelled.",
                0,
                0,
            ),
            Err(error) => report(
                progress,
                LauncherUpdatePhase::Failed,
                format!("Launcher update staging failed: {error}"),
                0,
                0,
            ),
        }
        result
    }

    /// Activates the previous client version.
    pub fn rollback_client(
        &self,
        progress: &mut dyn FnMut(LauncherUpdateProgress),
        cancel: &CancelToken,
    ) -> Result<ClientVersionResolution, UpdateError> {
        let _gate = self.gate();
        self.refuse_running_sessions()?;
        report(
            progress,
            LauncherUpdatePhase::RollingBack,
            "Verifying and activating the previous client version...",
            0,
            0,
        );
        let result = self.versions.rollback(&self.rid, cancel)?;
        report(
            progress,
            LauncherUpdatePhase::Completed,
            format!(
                "Rolled back to client {}.",
                result
                    .version
                    .as_ref()
                    .map(LauncherVersion::value)
                    .unwrap_or_default()
            ),
            1,
            1,
        );
        Ok(result)
    }

    /// The staging folder a fresh install would use (for tests).
    pub fn app_directory(&self) -> &Path {
        self.versions.app_directory()
    }
}

fn build_check_status(
    manifest: &ReleaseManifest,
    installed: Option<&LauncherVersion>,
    client_available: bool,
    launcher_available: bool,
    minimum_satisfied: bool,
) -> String {
    if !minimum_satisfied {
        return format!(
            "Release {} requires launcher {} or newer.",
            manifest.version, manifest.minimum_launcher_version
        );
    }
    match (client_available, launcher_available) {
        (true, true) => format!("Client and launcher {} are available.", manifest.version),
        (true, false) => match installed {
            None => format!("Client {} is available for installation.", manifest.version),
            Some(installed) => format!(
                "Client update {installed} -> {} is available.",
                manifest.version
            ),
        },
        (false, true) => format!("Launcher {} is available.", manifest.version),
        (false, false) => "Client and launcher are up to date.".to_owned(),
    }
}
