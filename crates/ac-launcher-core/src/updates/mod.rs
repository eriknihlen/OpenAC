//! Installed client versions and updates: strict SemVer, the release
//! manifest, verified downloads, safe zip extraction, the versioned client
//! store with its activation pointer, the session/update barrier, the
//! client updater and the launcher's own update.

mod barrier;
mod client_store;
mod downloader;
mod manifest;
mod payload;
mod platform;
mod self_update;
pub mod self_update_bootstrap;
mod updater;
mod version;
pub mod zip;

pub use barrier::{ExclusiveLease, SessionLease, UPDATE_LOCK_FILE_NAME, UpdateSessionBarrier};
pub use client_store::{
    ClientActivationPointer, ClientVersionRecord, ClientVersionResolution, ClientVersionState,
    ClientVersionStore, FIRST_CLIENT_OF_THE_FOLDER_LAYOUT, InstalledFileRecord,
    is_normalized_relative, predates_the_folder_layout, predates_the_folder_layout_message,
    resolve_contained, validate_required_executables,
};
pub use downloader::{
    ArtifactDownloadProgress, VerifiedArtifactDownload, VerifiedArtifactDownloader,
};
pub use manifest::{
    CURRENT_FINGERPRINT_SCHEMA_VERSION, DEFAULT_FINGERPRINT_TIMEOUT,
    LAUNCHER_FINGERPRINT_FILE_NAME, LauncherFingerprintDocument, MAXIMUM_ARTIFACT_BYTES,
    MAXIMUM_MANIFEST_BYTES, PREVIOUS_MANIFEST_URI, PRODUCTION_MANIFEST_URI, ReleaseArtifact,
    ReleaseManifest, ReleaseManifestClient, ReleaseManifestSource, is_sha256,
    launcher_fingerprint_file_name,
};
pub use payload::{
    LAUNCHER, OPENAC, detect_rid, detect_rid_for, executables_for_payload, graphical_host_for_rid,
    headless_host_for_rid, is_valid_rid, is_windows_device_name, required_for_payload,
    suffix_for_current_os, suffix_for_rid,
};
pub use self_update::{
    ApplyObserver, INSTALL_RECORD_FILE_NAME, INSTALL_RECORD_SCHEMA_VERSION,
    LauncherBinaryInstallRecord, LauncherInstallationKind, LauncherInstallationLayout,
    LauncherSelfUpdateManager, MAC_BUNDLE_NAME, PLAN_SCHEMA_VERSION, SelfUpdateApplyBoundary,
    SelfUpdateApplyEntry, SelfUpdateApplyObservation, SelfUpdateApplyOperation, SelfUpdatePlan,
    SelfUpdatePlanState, SelfUpdateStageResult,
};
pub use updater::{
    LauncherUpdateCheckResult, LauncherUpdatePhase, LauncherUpdateProgress, LauncherUpdater,
    decide_launcher,
};
pub use version::LauncherVersion;
pub use zip::{ExtractedFileRecord, SafeZipExtractionLimits, SafeZipExtractor};

use std::io;

use crate::net::HttpError;

/// Why an install, update or plugin operation failed (OpenAC's
/// `LauncherUpdateException`, plus cancellation and transport failures,
/// which OpenAC raises as their own exception types).
#[derive(Debug, thiserror::Error)]
pub enum UpdateError {
    /// A rule was broken or a step failed; the text says which.
    #[error("{message}")]
    Failed {
        /// The player-facing text.
        message: String,
        /// The file-system failure underneath, if any.
        #[source]
        io: Option<io::Error>,
    },
    /// The HTTP transport failed before a response.
    #[error("{0}")]
    Transport(HttpError),
    /// The caller cancelled.
    #[error("The operation was cancelled.")]
    Cancelled,
}

impl UpdateError {
    /// A failure with this text.
    pub fn failed(message: impl Into<String>) -> Self {
        Self::Failed {
            message: message.into(),
            io: None,
        }
    }

    /// A failure with this text, caused by a file-system error.
    pub fn io(message: impl Into<String>, error: io::Error) -> Self {
        Self::Failed {
            message: message.into(),
            io: Some(error),
        }
    }

    /// Whether the failure came from a file-system error.
    pub fn is_io(&self) -> bool {
        matches!(self, Self::Failed { io: Some(_), .. })
    }

    /// Whether the caller cancelled.
    pub fn is_cancelled(&self) -> bool {
        matches!(
            self,
            Self::Cancelled | Self::Transport(HttpError::Cancelled)
        )
    }
}

impl From<crate::cancel::Cancelled> for UpdateError {
    fn from(_: crate::cancel::Cancelled) -> Self {
        Self::Cancelled
    }
}
