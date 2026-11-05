//! Installation: finding and checking the retail DAT directory, and the
//! install record (`<data>/install.json`) naming the DATs and OpenAC's
//! prepared content package with its size, SHA-256 and recipe version.
//!
//! OpenAC's first-run installer also bakes that package with its bake tool;
//! this client has no bake tool and no package yet (a deliberate
//! divergence), so the bake pipeline is not ported. The record
//! format, its verification (with the size-and-time cache that spares a
//! full hash at every start) and the recipe migration catalog are.

mod dat_locator;
mod migration;
mod record_store;
mod retail;
mod root_mover;

pub use dat_locator::{DatDirectoryLocator, DatDirectoryValidation, REQUIRED_DAT_FILE_NAMES};
pub use migration::{ContentMigrationPlan, ContentWorkKind, resolve_content_migration};
pub use record_store::{
    CURRENT_BAKE_TOOL_VERSION, CURRENT_INSTALL_RECORD_VERSION, InstallRecordVerification,
    InstallRecordVerificationState, InstallerTransactionLease, LauncherInstallRecord,
    LauncherInstallRecordStore, PreparedAssetVerificationCache, PreparedAssetVerificationEntry,
    backup_path,
};
pub use retail::{RETAIL_CONTENT_FILE_NAME, RETAIL_CONTENT_VERSION, RetailContentStore};
pub use root_mover::{
    InstallRootMoveResult, InstallRootMover, MOVE_SESSION_REFUSAL, SameVolume,
    repair_content_records,
};
