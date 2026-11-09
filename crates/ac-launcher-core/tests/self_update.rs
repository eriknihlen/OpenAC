//! The launcher's self-update in one process: staging, the apply journal,
//! confirmation, rollback at every boundary, the macOS bundle swap, and the
//! startup bootstrap's internal modes. Ported from OpenAC
//! `tests/AcDream.Launcher.Core.Tests/Updates/LauncherSelfUpdateManagerTests.cs`
//! (the loopback HTTP fixture is an in-memory transport here).
//! The cases that start real processes are in `self_update_process.rs`.

mod common;

use std::path::{Path, PathBuf};
use std::sync::Arc;

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::integrity::sha256_hex;
use ac_launcher_core::updates::self_update_bootstrap::{
    self, CONFIRM_ARGUMENT, HELPER_ARGUMENT, SelfUpdateStartupResult,
};
use ac_launcher_core::updates::{
    INSTALL_RECORD_FILE_NAME, LauncherInstallationKind, LauncherInstallationLayout,
    LauncherSelfUpdateManager, LauncherVersion, PLAN_SCHEMA_VERSION, ReleaseArtifact,
    SelfUpdateApplyBoundary, SelfUpdateApplyOperation, SelfUpdatePlan, SelfUpdatePlanState,
    SelfUpdateStageResult, UpdateError, detect_rid,
};
use ac_launcher_core::url::WebUrl;
use common::TempDir;
use common::fixtures::{Compression, FakeServer, create_zip, launcher_zip};

fn none() -> CancelToken {
    CancelToken::none()
}

fn version(text: &str) -> LauncherVersion {
    LauncherVersion::try_parse(text).unwrap()
}

fn read(path: &Path) -> String {
    std::fs::read_to_string(path).unwrap()
}

fn artifact_for(server: &FakeServer, name: &str, archive: &[u8]) -> ReleaseArtifact {
    let url = FakeServer::url(name);
    server.add(&url, archive);
    ReleaseArtifact {
        url: WebUrl::parse(&url).unwrap(),
        sha256: sha256_hex(archive),
        size: archive.len() as u64,
    }
}

// --- Plan edits (the tests' JsonNode edits, on the plan as written) ---------------------

fn edit_plan(manager: &LauncherSelfUpdateManager, edit: impl FnOnce(String) -> String) {
    let path = manager.pending_plan_path();
    let text = read(path);
    std::fs::write(path, edit(text)).unwrap();
}

fn set_member(text: &str, name: &str, value: &str) -> String {
    let key = format!("\"{name}\": ");
    let start = text.find(&key).expect("member") + key.len();
    let end = start + text[start..].find([',', '\n']).expect("member value end");
    format!("{}{value}{}", &text[..start], &text[end..])
}

fn set_plan_state(manager: &LauncherSelfUpdateManager, state: &str) {
    edit_plan(manager, |text| {
        set_member(&text, "state", &format!("\"{state}\""))
    });
}

fn set_bundle_applying(manager: &LauncherSelfUpdateManager) {
    edit_plan(manager, |text| {
        let text = set_member(&text, "state", "\"applying\"");
        set_member(&text, "apply", "[]")
    });
}

// --- Harness (flat layout) --------------------------------------------------------------

struct Harness {
    root: TempDir,
    server: FakeServer,
    artifact: ReleaseArtifact,
    target: PathBuf,
    rid: String,
    launcher_name: String,
    launcher_path: PathBuf,
    support_path: PathBuf,
    manager: LauncherSelfUpdateManager,
}

impl Harness {
    fn new() -> Self {
        // Spaces, '&' and '$(...)' in the path: nothing is parsed by a shell.
        let root = TempDir::new("self-update target & $(literal)");
        let target = root.join("published launcher");
        std::fs::create_dir_all(&target).unwrap();
        let rid = detect_rid().unwrap();
        let launcher_name = format!(
            "openac-launcher{}",
            if rid.starts_with("win-") { ".exe" } else { "" }
        );
        let launcher_path = target.join(&launcher_name);
        let support_path = target.join("support.dat");
        std::fs::write(&launcher_path, "old-launcher").unwrap();
        std::fs::write(&support_path, "old-support").unwrap();
        let server = FakeServer::new();
        let artifact = artifact_for(&server, "launcher.zip", &launcher_zip(&rid, "new-launcher"));
        let manager = LauncherSelfUpdateManager::new(&root.path_set(), server.transport());
        Self {
            root,
            server,
            artifact,
            target,
            rid,
            launcher_name,
            launcher_path,
            support_path,
            manager,
        }
    }

    fn layout(&self) -> LauncherInstallationLayout {
        LauncherInstallationLayout::flat(&self.target, &self.rid).unwrap()
    }

    fn stage(&self) -> Result<SelfUpdateStageResult, UpdateError> {
        self.manager.stage_artifact(
            &version("2.0.0"),
            &self.rid,
            &self.artifact,
            &self.layout(),
            &mut |_| {},
            &none(),
        )
    }

    fn stage_archive(
        &self,
        at: &str,
        archive: &[u8],
    ) -> Result<SelfUpdateStageResult, UpdateError> {
        let artifact = artifact_for(&self.server, "launcher.zip", archive);
        self.manager.stage_artifact(
            &version(at),
            &self.rid,
            &artifact,
            &self.layout(),
            &mut |_| {},
            &none(),
        )
    }

    fn manager_with_observer(
        &self,
        observer: impl Fn(
            &ac_launcher_core::updates::SelfUpdateApplyObservation,
        ) -> Result<(), UpdateError>
        + Send
        + Sync
        + 'static,
    ) -> LauncherSelfUpdateManager {
        LauncherSelfUpdateManager::new(&self.root.path_set(), self.server.transport())
            .with_apply_observer(Box::new(observer))
    }

    fn pending(&self) -> SelfUpdatePlan {
        self.manager
            .load_pending()
            .unwrap()
            .expect("a pending plan")
    }

    fn handle(&self, arguments: &[&str]) -> Result<SelfUpdateStartupResult, UpdateError> {
        self.handle_as(arguments, &self.launcher_path)
    }

    fn handle_as(
        &self,
        arguments: &[&str],
        executable: &Path,
    ) -> Result<SelfUpdateStartupResult, UpdateError> {
        let arguments: Vec<String> = arguments.iter().map(|a| (*a).to_owned()).collect();
        self_update_bootstrap::handle(&arguments, &self.manager, &self.target, executable)
    }
}

// --- macOS bundle -----------------------------------------------------------------------

const MAC_LAUNCHER: &str = "OpenAC.app/Contents/MacOS/openac-launcher";

fn bundle_archive(entries: &[(&str, &[u8], u32)]) -> Vec<u8> {
    let entries: Vec<(&str, &[u8], Option<u32>)> = entries
        .iter()
        .map(|(name, content, mode)| (*name, *content, Some(*mode)))
        .collect();
    create_zip(&entries, Compression::Stored)
}

struct MacBundleScenario {
    _root: TempDir,
    bundle: PathBuf,
    launcher: PathBuf,
    support: PathBuf,
    manager: LauncherSelfUpdateManager,
}

fn mac_bundle_scenario() -> MacBundleScenario {
    let root = TempDir::new("self-update-bundle");
    let bundle = root.join("Applications").join("OpenAC.app");
    let launcher = bundle
        .join("Contents")
        .join("MacOS")
        .join("openac-launcher");
    let support = bundle
        .join("Contents")
        .join("Resources")
        .join("support.dat");
    std::fs::create_dir_all(launcher.parent().unwrap()).unwrap();
    std::fs::create_dir_all(support.parent().unwrap()).unwrap();
    std::fs::write(&launcher, "old-launcher").unwrap();
    std::fs::write(&support, "old-support").unwrap();
    let archive = bundle_archive(&[
        (MAC_LAUNCHER, b"new-launcher", 0x81ED),
        ("OpenAC.app/Contents/Info.plist", b"<plist/>", 0x81A4),
        (
            "OpenAC.app/Contents/Resources/support.dat",
            b"new-support",
            0x81A4,
        ),
    ]);
    let server = FakeServer::new();
    let artifact = artifact_for(&server, "launcher.zip", &archive);
    let manager = LauncherSelfUpdateManager::new(&root.path_set(), server.transport());
    manager
        .stage_artifact(
            &version("2.0.0"),
            "osx-arm64",
            &artifact,
            &LauncherInstallationLayout::mac_bundle(&bundle, "osx-arm64").unwrap(),
            &mut |_| {},
            &none(),
        )
        .unwrap();
    MacBundleScenario {
        _root: root,
        bundle,
        launcher,
        support,
        manager,
    }
}

fn copy_directory(source: &Path, destination: &Path) {
    std::fs::create_dir_all(destination).unwrap();
    for entry in std::fs::read_dir(source).unwrap() {
        let entry = entry.unwrap();
        let to = destination.join(entry.file_name());
        if entry.file_type().unwrap().is_dir() {
            copy_directory(&entry.path(), &to);
        } else {
            std::fs::copy(entry.path(), to).unwrap();
        }
    }
}

// OpenAC LauncherSelfUpdateManagerTests.MacBundleApplyAndRollbackRenameTheWholeAppWithoutWritingInsideIt.
#[test]
fn mac_bundle_apply_and_rollback_rename_the_whole_app_without_writing_inside_it() {
    let scenario = mac_bundle_scenario();
    let manager = &scenario.manager;

    let applied = manager.apply_pending(&scenario.bundle, &none()).unwrap();

    assert_eq!(applied.state, SelfUpdatePlanState::AwaitingConfirmation);
    assert_eq!(read(&scenario.launcher), "new-launcher");
    assert_eq!(read(&scenario.support), "new-support");
    assert!(!scenario.bundle.join(INSTALL_RECORD_FILE_NAME).exists());
    assert!(
        manager
            .target_transaction_directory(&applied)
            .unwrap()
            .join("backup")
            .join("OpenAC.app")
            .is_dir()
    );

    let rolled_back = manager
        .rollback_awaiting_confirmation(&scenario.bundle, &none())
        .unwrap();
    assert_eq!(rolled_back.state, SelfUpdatePlanState::RolledBack);
    assert_eq!(read(&scenario.launcher), "old-launcher");
    assert_eq!(read(&scenario.support), "old-support");
    assert!(
        !manager
            .target_transaction_directory(&rolled_back)
            .unwrap()
            .exists()
    );
}

fn stage_bundle_archive(
    archive: &[u8],
) -> (
    TempDir,
    PathBuf,
    Result<SelfUpdateStageResult, UpdateError>,
    LauncherSelfUpdateManager,
) {
    stage_bundle_archive_for(archive, "osx-arm64")
}

fn stage_bundle_archive_for(
    archive: &[u8],
    rid: &str,
) -> (
    TempDir,
    PathBuf,
    Result<SelfUpdateStageResult, UpdateError>,
    LauncherSelfUpdateManager,
) {
    let root = TempDir::new("self-update-bundle-stage");
    let bundle = root.join("Applications").join("OpenAC.app");
    let launcher = bundle
        .join("Contents")
        .join("MacOS")
        .join("openac-launcher");
    std::fs::create_dir_all(launcher.parent().unwrap()).unwrap();
    std::fs::write(&launcher, "old-launcher").unwrap();
    let server = FakeServer::new();
    let artifact = artifact_for(&server, "launcher.zip", archive);
    let manager = LauncherSelfUpdateManager::new(&root.path_set(), server.transport());
    let result = manager.stage_artifact(
        &version("2.0.0"),
        rid,
        &artifact,
        &LauncherInstallationLayout::mac_bundle(&bundle, rid).unwrap(),
        &mut |_| {},
        &none(),
    );
    (root, launcher, result, manager)
}

// OpenAC LauncherSelfUpdateManagerTests.MacBundleStageRejectsFilesOutsideOpenAcApp.
#[test]
fn mac_bundle_stage_rejects_files_outside_openac_app() {
    let archive = bundle_archive(&[
        (MAC_LAUNCHER, b"new-launcher", 0x81ED),
        ("outside-bundle", b"unexpected", 0x81A4),
    ]);
    let (_root, launcher, result, manager) = stage_bundle_archive(&archive);
    assert!(result.is_err());
    assert_eq!(read(&launcher), "old-launcher");
    assert!(!manager.pending_plan_path().exists());
}

// The Intel Mac launcher (osx-x64) updates its bundle as the Apple-silicon one does.
#[test]
fn mac_bundle_stages_for_an_intel_mac() {
    let archive = bundle_archive(&[
        (MAC_LAUNCHER, b"new-launcher", 0x81ED),
        ("OpenAC.app/Contents/Info.plist", b"<plist/>", 0x81A4),
    ]);
    let (_root, launcher, result, manager) = stage_bundle_archive_for(&archive, "osx-x64");
    result.unwrap();
    assert_eq!(read(&launcher), "old-launcher");
    let plan = manager.load_pending().unwrap().unwrap();
    assert_eq!(plan.rid, "osx-x64");
    assert_eq!(plan.installation_kind, LauncherInstallationKind::MacBundle);
}

// OpenAC LauncherSelfUpdateManagerTests.MacBundleStageRequiresInfoPlist.
#[test]
fn mac_bundle_stage_requires_info_plist() {
    let archive = bundle_archive(&[(MAC_LAUNCHER, b"new-launcher", 0x81ED)]);
    let (_root, _launcher, result, _manager) = stage_bundle_archive(&archive);
    assert!(result.is_err());
}

// OpenAC LauncherSelfUpdateManagerTests.MacBundleHelperUsesTheStagedMacOsDirectoryForItsTrustCheck.
#[test]
fn mac_bundle_helper_uses_the_staged_macos_directory_for_its_trust_check() {
    let scenario = mac_bundle_scenario();
    let plan = scenario.manager.load_pending().unwrap().unwrap();
    let layout = LauncherInstallationLayout::mac_bundle(&scenario.bundle, "osx-arm64").unwrap();
    let arguments = vec![
        HELPER_ARGUMENT.to_owned(),
        std::process::id().to_string(),
        scenario.bundle.display().to_string(),
        plan.transaction_id.clone(),
    ];
    let error = self_update_bootstrap::handle_for_layout(
        &arguments,
        &scenario.manager,
        &layout,
        &scenario.manager.staged_launcher_path(&plan).unwrap(),
    )
    .unwrap_err();
    assert!(
        error.to_string().contains("cannot wait on itself"),
        "{error}"
    );
}

// OpenAC LauncherSelfUpdateManagerTests.MacBundleRecoveryRestoresThePriorBundleAtEveryRenameBoundary.
#[test]
fn mac_bundle_recovery_restores_the_prior_bundle_at_every_rename_boundary() {
    for boundary in 0..=2 {
        let scenario = mac_bundle_scenario();
        let manager = &scenario.manager;
        let staged = manager.load_pending().unwrap().unwrap();
        let swap = manager.target_transaction_directory(&staged).unwrap();
        let incoming = swap.join("incoming").join("OpenAC.app");
        copy_directory(
            &manager
                .payload_directory(&staged.transaction_id)
                .unwrap()
                .join("OpenAC.app"),
            &incoming,
        );
        set_bundle_applying(manager);
        let backup = swap.join("backup").join("OpenAC.app");
        if boundary >= 1 {
            std::fs::create_dir_all(backup.parent().unwrap()).unwrap();
            std::fs::rename(&scenario.bundle, &backup).unwrap();
        }
        if boundary >= 2 {
            std::fs::rename(&incoming, &scenario.bundle).unwrap();
        }

        let recovered = manager.recover_applying(&scenario.bundle, &none()).unwrap();

        assert_eq!(
            recovered.state,
            SelfUpdatePlanState::RolledBack,
            "{boundary}"
        );
        assert_eq!(read(&scenario.launcher), "old-launcher", "{boundary}");
        assert_eq!(read(&scenario.support), "old-support", "{boundary}");
        assert!(!swap.exists(), "{boundary}");
    }
}

// OpenAC LauncherSelfUpdateManagerTests.MacBundleConfirmationCleansBothTransactionRoots.
#[test]
fn mac_bundle_confirmation_cleans_both_transaction_roots() {
    let scenario = mac_bundle_scenario();
    let manager = &scenario.manager;
    let applied = manager.apply_pending(&scenario.bundle, &none()).unwrap();
    manager
        .confirm(
            &applied.transaction_id,
            &scenario.bundle,
            &scenario.launcher,
            &none(),
        )
        .unwrap();
    manager
        .complete_confirmed(&applied.transaction_id, &scenario.bundle)
        .unwrap();

    assert!(!manager.pending_plan_path().exists());
    assert!(
        !manager
            .target_transaction_directory(&applied)
            .unwrap()
            .exists()
    );
    assert!(
        !manager
            .transaction_directory(&applied.transaction_id)
            .unwrap()
            .exists()
    );
    assert_eq!(read(&scenario.launcher), "new-launcher");
}

// OpenAC LauncherSelfUpdateManagerTests.SchemaThreeFlatPlanMigratesToSchemaFourFlatPlan.
#[test]
fn schema_three_flat_plan_migrates_to_schema_four_flat_plan() {
    let harness = Harness::new();
    harness.stage().unwrap();
    edit_plan(&harness.manager, |text| {
        let text = set_member(&text, "schemaVersion", "3");
        text.replace("  \"installationKind\": 0,\n", "")
    });

    let migrated = harness.pending();

    assert_eq!(migrated.schema_version, PLAN_SCHEMA_VERSION);
    assert_eq!(migrated.installation_kind, LauncherInstallationKind::Flat);
}

// OpenAC LauncherSelfUpdateManagerTests.TamperedBundlePlanCannotBeReinterpretedAsFlat.
#[test]
fn tampered_bundle_plan_cannot_be_reinterpreted_as_flat() {
    let scenario = mac_bundle_scenario();
    edit_plan(&scenario.manager, |text| {
        set_member(&text, "installationKind", "0")
    });
    assert!(scenario.manager.load_pending().is_err());
}

// OpenAC LauncherSelfUpdateManagerTests.StartupWithoutPlanReclaimsOnlyExactOwnedResidue.
#[test]
fn startup_without_plan_reclaims_only_exact_owned_residue() {
    let harness = Harness::new();
    let manager = &harness.manager;
    let orphan = manager
        .transaction_directory(&ac_launcher_core::ids::unique_hex32())
        .unwrap();
    std::fs::create_dir_all(&orphan).unwrap();
    std::fs::write(orphan.join("partial"), "partial").unwrap();
    let temporary = manager.root_directory().join(format!(
        ".pending.json.{}.tmp",
        ac_launcher_core::ids::unique_hex32()
    ));
    let unrelated = manager.root_directory().join("pending.user.tmp");
    std::fs::write(&temporary, "partial").unwrap();
    std::fs::write(&unrelated, "preserve").unwrap();

    assert_eq!(manager.load_pending().unwrap(), None);
    let lease = manager.barrier().acquire_exclusive().unwrap();
    assert!(
        manager
            .cleanup_owned_residue_under_lease(None, &harness.target, &lease)
            .unwrap()
    );

    assert!(!orphan.exists());
    assert!(!temporary.exists());
    assert!(unrelated.exists());
}

// OpenAC LauncherSelfUpdateManagerTests.VerifiedStageIsDurableAndDoesNotTouchRunningTarget.
#[test]
fn verified_stage_is_durable_and_does_not_touch_running_target() {
    let harness = Harness::new();

    let result = harness.stage().unwrap();
    let plan = harness.pending();

    assert_eq!(result.version.value(), "2.0.0");
    assert_eq!(plan.state, SelfUpdatePlanState::Staged);
    assert_eq!(plan.apply, None);
    assert_eq!(read(&harness.launcher_path), "old-launcher");
    assert_eq!(
        read(
            &harness
                .manager
                .payload_directory(&plan.transaction_id)
                .unwrap()
                .join(&harness.launcher_name)
        ),
        "new-launcher"
    );
    let pending = read(&result.pending_plan_path);
    assert!(pending.contains("\"state\": \"staged\""), "{pending}");
    assert!(!pending.contains("\"state\": \"Staged\""));
    assert!(!pending.to_lowercase().contains("cmd"));
}

// A staged update that was never handed to its helper (the helper could not
// be started, or a client held the install when the launcher started) is
// staged again by Update, not refused with "Restart the launcher": the
// same release keeps its verified transaction, so Update can hand it over.
// An addition over OpenAC, which refused any second stage.
#[test]
fn staging_the_same_release_again_reuses_the_staged_transaction() {
    let harness = Harness::new();
    harness.stage().unwrap();
    let first = harness.pending();

    let again = harness.stage().unwrap();
    let plan = harness.pending();

    assert_eq!(again.version.value(), "2.0.0");
    assert_eq!(plan, first);
    assert_eq!(read(&harness.launcher_path), "old-launcher");
    assert_eq!(
        read(&harness.manager.staged_launcher_path(&plan).unwrap()),
        "new-launcher"
    );
}

// A staged release that is not the one being staged (an older one, or one
// whose payload no longer verifies) never touched the installation, so it
// is dropped with its transaction and the new release is staged.
#[test]
fn staging_another_release_replaces_a_staged_one() {
    let harness = Harness::new();
    harness.stage().unwrap();
    let older = harness.pending();

    let newer = harness
        .stage_archive("3.0.0", &launcher_zip(&harness.rid, "newer-launcher"))
        .unwrap();
    let plan = harness.pending();

    assert_eq!(newer.version.value(), "3.0.0");
    assert_eq!(plan.version, "3.0.0");
    assert_eq!(plan.state, SelfUpdatePlanState::Staged);
    assert_ne!(plan.transaction_id, older.transaction_id);
    assert!(
        !harness
            .manager
            .transaction_directory(&older.transaction_id)
            .unwrap()
            .exists()
    );
    assert_eq!(read(&harness.launcher_path), "old-launcher");

    // A damaged payload of the same release is staged afresh too.
    let damaged = harness.manager.staged_launcher_path(&plan).unwrap();
    std::fs::write(&damaged, "tampered").unwrap();
    harness
        .stage_archive("3.0.0", &launcher_zip(&harness.rid, "newer-launcher"))
        .unwrap();
    let restaged = harness.pending();
    assert_ne!(restaged.transaction_id, plan.transaction_id);
    assert_eq!(
        read(&harness.manager.staged_launcher_path(&restaged).unwrap()),
        "newer-launcher"
    );
}

// What an install holds after a 0.2.2 launcher installed itself: no plan, and
// the helper's own transaction (its payload kept by the helper's running
// executable). Staging the next release reclaims it.
#[test]
fn staging_reclaims_the_previous_updates_helper_transaction() {
    let harness = Harness::new();
    let previous = harness
        .manager
        .payload_directory(&ac_launcher_core::ids::unique_hex32())
        .unwrap();
    std::fs::create_dir_all(&previous).unwrap();
    std::fs::write(previous.join(&harness.launcher_name), "previous-helper").unwrap();
    std::fs::write(previous.join("THIRD-PARTY-LICENSES.html"), "licenses").unwrap();

    harness.stage().unwrap();

    assert!(!previous.parent().unwrap().exists());
    assert_eq!(harness.pending().state, SelfUpdatePlanState::Staged);
}

// A notice an earlier attempt left is stale once an update is confirmed:
// the confirming launcher drops it before its window reads it, and so does
// an ordinary start that confirms the update itself, and a new staging.
#[test]
fn a_confirmed_or_new_update_forgets_an_earlier_attempts_notice() {
    let stale = "Launcher 1.9.0 could not be installed.";
    let harness = Harness::new();
    harness.stage().unwrap();
    let applied = harness
        .manager
        .apply_pending(&harness.target, &none())
        .unwrap();
    harness.manager.keep_update_notice(stale);
    {
        let _helper_lease = harness.manager.barrier().acquire_exclusive().unwrap();
        let confirmation = harness
            .handle(&[CONFIRM_ARGUMENT, &applied.transaction_id])
            .unwrap();
        assert!(!confirmation.should_exit);
    }
    assert_eq!(harness.manager.take_update_notice(), None);

    // An ordinary start that confirms the update itself.
    let harness = Harness::new();
    harness.stage().unwrap();
    harness
        .manager
        .apply_pending(&harness.target, &none())
        .unwrap();
    harness.manager.keep_update_notice(stale);
    assert!(!harness.handle(&["ordinary"]).unwrap().should_exit);
    assert!(!harness.manager.pending_plan_path().exists());
    assert_eq!(harness.manager.take_update_notice(), None);

    // A new staging.
    let harness = Harness::new();
    harness.manager.keep_update_notice(stale);
    harness.stage().unwrap();
    assert_eq!(harness.manager.take_update_notice(), None);
}

// The helper could not be started from the staged payload (security
// software removed it, say): nothing was changed and the next start could
// not start it either. The plan goes, and the start opens the launcher as
// it is with the reason, instead of failing on every start.
#[test]
fn a_start_whose_helper_cannot_start_drops_the_plan_and_opens_with_why() {
    let harness = Harness::new();
    harness.stage().unwrap();
    let plan = harness.pending();
    std::fs::remove_file(harness.manager.staged_launcher_path(&plan).unwrap()).unwrap();

    let startup = harness.handle(&["ordinary", "argument"]).unwrap();

    assert!(!startup.should_exit);
    assert_eq!(startup.remaining_arguments, ["ordinary", "argument"]);
    assert!(!harness.manager.pending_plan_path().exists());
    assert!(
        !harness
            .manager
            .transaction_directory(&plan.transaction_id)
            .unwrap()
            .exists()
    );
    assert_eq!(read(&harness.launcher_path), "old-launcher");
    let notice = harness.manager.take_update_notice().expect("why");
    assert!(
        notice.contains("Launcher 2.0.0 could not be installed"),
        "{notice}"
    );
    assert!(notice.contains("Nothing was changed"), "{notice}");
    assert!(!harness.handle(&["ordinary"]).unwrap().should_exit);
}

// A start that finds the install in use leaves the staged update waiting,
// as before. The window says so on a status line, not in a dialog: clients
// may run all day, and a dialog would greet every start.
#[test]
fn a_start_with_the_install_in_use_says_the_update_waits_without_a_dialog() {
    let harness = Harness::new();
    assert_eq!(
        self_update_bootstrap::waiting_update_text(&harness.manager),
        None
    );
    harness.stage().unwrap();
    let plan = harness.pending();
    {
        let _client = harness.manager.barrier().acquire_session().unwrap();
        assert!(!harness.handle(&["ordinary"]).unwrap().should_exit);
    }
    assert_eq!(harness.pending(), plan);
    assert_eq!(harness.manager.take_update_notice(), None, "no dialog");
    let line = self_update_bootstrap::waiting_update_text(&harness.manager).expect("a line");
    assert!(line.contains("Launcher 2.0.0 is downloaded"), "{line}");
    assert!(line.contains("no client running"), "{line}");
    assert!(line.contains("plugin install"), "{line}");
}
// The plan goes before the notice is written: a notice that cannot be
// written must not leave the plan for every start to retry.
#[test]
fn abandoning_drops_the_plan_even_when_the_notice_cannot_be_written() {
    let harness = Harness::new();
    harness.stage().unwrap();
    let plan = harness.pending();
    std::fs::create_dir_all(harness.manager.update_notice_path().join("blocked")).unwrap();
    let lease = harness.manager.barrier().acquire_exclusive().unwrap();

    harness
        .manager
        .abandon_unapplied_under_lease(&plan.transaction_id, "why", &lease)
        .unwrap();

    assert!(!harness.manager.pending_plan_path().exists());
}

// A plan past staging changes the installation; only a start finishes or
// undoes it, so staging over it is refused, and so is staging over a
// release staged for another copy of the launcher.
#[test]
fn staging_refuses_an_applied_plan_and_another_launchers_staged_plan() {
    let harness = Harness::new();
    harness.stage().unwrap();
    harness
        .manager
        .apply_pending(&harness.target, &none())
        .unwrap();
    let refused = harness.stage().unwrap_err().to_string();
    assert!(refused.contains("AwaitingConfirmation"), "{refused}");
    assert!(refused.contains("Restart the launcher"), "{refused}");

    let other = Harness::new();
    let elsewhere = other.root.join("another copy");
    std::fs::create_dir_all(&elsewhere).unwrap();
    other
        .manager
        .stage_artifact(
            &version("2.0.0"),
            &other.rid,
            &other.artifact,
            &LauncherInstallationLayout::flat(&elsewhere, &other.rid).unwrap(),
            &mut |_| {},
            &none(),
        )
        .unwrap();
    let refused = other.stage().unwrap_err().to_string();
    assert!(refused.contains("another copy"), "{refused}");
    assert_eq!(
        other.pending().target_directory.file_name().unwrap(),
        "another copy"
    );
}

// OpenAC LauncherSelfUpdateManagerTests.ApplyConfirmAndCompletionUseMoveJournalAndRemoveTransaction.
#[test]
fn apply_confirm_and_completion_use_move_journal_and_remove_transaction() {
    let harness = Harness::new();
    harness.stage().unwrap();

    let applied = harness
        .manager
        .apply_pending(&harness.target, &none())
        .unwrap();

    assert_eq!(applied.state, SelfUpdatePlanState::AwaitingConfirmation);
    assert!(applied.apply.is_some());
    assert_eq!(read(&harness.launcher_path), "new-launcher");
    assert_eq!(read(&harness.support_path), "support-new-launcher");
    harness
        .manager
        .confirm(
            &applied.transaction_id,
            &harness.target,
            &harness.launcher_path,
            &none(),
        )
        .unwrap();
    assert!(harness.manager.is_confirmed(&applied.transaction_id));

    harness
        .manager
        .complete_confirmed(&applied.transaction_id, &harness.target)
        .unwrap();

    assert!(!harness.manager.pending_plan_path().exists());
    assert!(
        !harness
            .manager
            .transaction_directory(&applied.transaction_id)
            .unwrap()
            .exists()
    );
    assert_eq!(read(&harness.launcher_path), "new-launcher");
}

// OpenAC LauncherSelfUpdateManagerTests.AwaitingConfirmationRollbackRestoresEveryOldFileAndCanRetry.
#[test]
fn awaiting_confirmation_rollback_restores_every_old_file_and_can_retry() {
    let harness = Harness::new();
    harness.stage().unwrap();
    harness
        .manager
        .apply_pending(&harness.target, &none())
        .unwrap();

    let rolled_back = harness
        .manager
        .rollback_awaiting_confirmation(&harness.target, &none())
        .unwrap();

    assert_eq!(rolled_back.state, SelfUpdatePlanState::RolledBack);
    for entry in rolled_back.apply.as_ref().unwrap() {
        if entry.had_original {
            let sha = entry.prior_sha256.as_deref().unwrap();
            assert!(
                sha.len() == 64
                    && sha
                        .bytes()
                        .all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
            );
            assert!(entry.prior_size.is_some());
            assert!(entry.prior_unix_mode.is_some());
        }
    }
    assert_eq!(read(&harness.launcher_path), "old-launcher");
    assert_eq!(read(&harness.support_path), "old-support");
    let retried = harness
        .manager
        .apply_pending(&harness.target, &none())
        .unwrap();
    assert_eq!(retried.state, SelfUpdatePlanState::AwaitingConfirmation);
    assert_eq!(read(&harness.launcher_path), "new-launcher");
}

// OpenAC LauncherSelfUpdateManagerTests.PriorOwnershipRemovesObsoleteFilesAndRollbackRestoresThem.
#[test]
fn prior_ownership_removes_obsolete_files_and_rollback_restores_them() {
    let harness = Harness::new();
    let manager = &harness.manager;
    let first = create_zip(
        &[
            (&harness.launcher_name, b"launcher-v2", Some(0x81ED)),
            ("support.dat", b"support-v2", Some(0x81A4)),
            ("obsolete.dll", b"obsolete-v2", Some(0x81A4)),
        ],
        Compression::Stored,
    );
    harness.stage_archive("2.0.0", &first).unwrap();
    let applied = manager.apply_pending(&harness.target, &none()).unwrap();
    manager
        .confirm(
            &applied.transaction_id,
            &harness.target,
            &harness.launcher_path,
            &none(),
        )
        .unwrap();
    manager
        .complete_confirmed(&applied.transaction_id, &harness.target)
        .unwrap();
    let obsolete = harness.target.join("obsolete.dll");
    assert_eq!(read(&obsolete), "obsolete-v2");

    let second = create_zip(
        &[
            (&harness.launcher_name, b"launcher-v3", Some(0x81ED)),
            ("support.dat", b"support-v3", Some(0x81A4)),
        ],
        Compression::Stored,
    );
    harness.stage_archive("3.0.0", &second).unwrap();
    let applied = manager.apply_pending(&harness.target, &none()).unwrap();

    assert!(!obsolete.exists());
    assert!(applied.apply.as_ref().unwrap().iter().any(|entry| {
        entry.path == "obsolete.dll"
            && entry.operation == SelfUpdateApplyOperation::Remove
            && entry.had_original
    }));

    let rolled_back = manager
        .rollback_awaiting_confirmation(&harness.target, &none())
        .unwrap();
    assert_eq!(rolled_back.state, SelfUpdatePlanState::RolledBack);
    assert_eq!(read(&harness.launcher_path), "launcher-v2");
    assert_eq!(read(&harness.support_path), "support-v2");
    assert_eq!(read(&obsolete), "obsolete-v2");

    let retried = manager.apply_pending(&harness.target, &none()).unwrap();
    manager
        .confirm(
            &retried.transaction_id,
            &harness.target,
            &harness.launcher_path,
            &none(),
        )
        .unwrap();
    manager
        .complete_confirmed(&retried.transaction_id, &harness.target)
        .unwrap();
    assert!(!obsolete.exists());
    let ownership = read(&harness.target.join(INSTALL_RECORD_FILE_NAME));
    assert!(!ownership.contains("obsolete.dll"));
}

// OpenAC LauncherSelfUpdateManagerTests.ApplyFailpointAfterCanonicalAtomicReplaceLeavesVerifiedRollbackReceipt.
#[test]
fn apply_failpoint_after_canonical_atomic_replace_leaves_verified_rollback_receipt() {
    let harness = Harness::new();
    harness.stage().unwrap();
    let launcher_name = harness.launcher_name.clone();
    let launcher_path = harness.launcher_path.clone();
    let faulting = harness.manager_with_observer(move |observation| {
        if observation.boundary == SelfUpdateApplyBoundary::AfterTargetMutation
            && observation.path == launcher_name
        {
            assert!(launcher_path.exists());
            return Err(UpdateError::failed("failpoint"));
        }
        Ok(())
    });

    let failure = faulting
        .apply_pending(&harness.target, &none())
        .unwrap_err();
    let recovered = harness.pending();

    assert_eq!(failure.to_string(), "failpoint");
    assert_eq!(recovered.state, SelfUpdatePlanState::RolledBack);
    harness
        .manager
        .verify_restored_prior(&harness.target, &none())
        .unwrap();
    assert_eq!(read(&harness.launcher_path), "old-launcher");
    assert_eq!(read(&harness.support_path), "old-support");
    assert_eq!(
        read(
            &harness
                .manager
                .payload_directory(&recovered.transaction_id)
                .unwrap()
                .join(&harness.launcher_name)
        ),
        "new-launcher"
    );
}

// OpenAC LauncherSelfUpdateManagerTests.ConditionalPriorIntegrityFieldsAreStrictAndFailClosed.
#[test]
fn conditional_prior_integrity_fields_are_strict_and_fail_closed() {
    let harness = Harness::new();
    harness.stage().unwrap();
    let applied = harness
        .manager
        .apply_pending(&harness.target, &none())
        .unwrap();
    let canonical: Vec<_> = applied
        .apply
        .as_ref()
        .unwrap()
        .iter()
        .filter(|entry| entry.path == harness.launcher_name)
        .collect();
    assert_eq!(canonical.len(), 1);
    let canonical = canonical[0];
    assert!(canonical.had_original);
    assert_eq!(canonical.prior_sha256.as_ref().map(String::len), Some(64));
    assert!(canonical.prior_size.is_some());
    assert!(canonical.prior_unix_mode.is_some());
    assert_eq!(
        canonical.replacement_sha256.as_ref().map(String::len),
        Some(64)
    );

    let launcher = harness.launcher_name.clone();
    edit_plan(&harness.manager, |text| {
        // The canonical launcher's entry in the apply journal (after the
        // file list).
        let apply = text.find("\"apply\": [").unwrap();
        let entry = apply
            + text[apply..]
                .find(&format!("\"path\": \"{launcher}\""))
                .unwrap();
        let (head, tail) = text.split_at(entry);
        format!("{head}{}", set_member(tail, "priorSha256", "null"))
    });

    assert!(harness.manager.load_pending().is_err());
    assert_eq!(read(&harness.launcher_path), "new-launcher");
    assert!(
        harness
            .manager
            .target_transaction_directory(&applied)
            .unwrap()
            .is_dir()
    );
}

// OpenAC LauncherSelfUpdateManagerTests.CorruptPayloadWrongTargetAndUnknownPlanFieldFailClosed.
#[test]
fn corrupt_payload_wrong_target_and_unknown_plan_field_fail_closed() {
    let harness = Harness::new();
    harness.stage().unwrap();
    let staged = harness.pending();
    std::fs::write(
        harness
            .manager
            .payload_directory(&staged.transaction_id)
            .unwrap()
            .join(&harness.launcher_name),
        "bad-payload!",
    )
    .unwrap();

    assert!(
        harness
            .manager
            .apply_pending(&harness.target, &none())
            .is_err()
    );
    assert_eq!(read(&harness.launcher_path), "old-launcher");
    let wrong_target = harness.root.join("other-target");
    std::fs::create_dir_all(&wrong_target).unwrap();
    assert!(
        harness
            .manager
            .apply_pending(&wrong_target, &none())
            .is_err()
    );

    edit_plan(&harness.manager, |text| {
        format!(
            "{},\"unknown\":true}}",
            text.trim_end().trim_end_matches('}')
        )
    });
    assert!(harness.manager.load_pending().is_err());
}

fn public_arguments(root: &TempDir) -> Vec<String> {
    vec![
        "--config-dir".into(),
        root.join("config with spaces").display().to_string(),
        "--data-dir".into(),
        root.join("data & literal").display().to_string(),
        "--cache-dir".into(),
        root.join("cache").display().to_string(),
        "--update-manifest-uri".into(),
        "http://127.0.0.1:43119/manifest.json".into(),
    ]
}

fn with_prefix(prefix: &[&str], rest: &[String]) -> Vec<String> {
    prefix
        .iter()
        .map(|item| (*item).to_owned())
        .chain(rest.iter().cloned())
        .collect()
}

// OpenAC LauncherSelfUpdateManagerTests.BootstrapConfirmationAndOrdinaryStartupDoNotUseShellParsing.
#[test]
fn bootstrap_confirmation_and_ordinary_startup_do_not_use_shell_parsing() {
    let harness = Harness::new();
    let public = public_arguments(&harness.root);
    let handle = |arguments: &[String]| {
        self_update_bootstrap::handle(
            arguments,
            &harness.manager,
            &harness.target,
            &harness.launcher_path,
        )
    };

    let ordinary = handle(&public).unwrap();
    assert!(!ordinary.should_exit);
    assert_eq!(ordinary.remaining_arguments, public);

    let deferred = handle(&with_prefix(
        &["--acdream-self-update-deferred-v1"],
        &public,
    ))
    .unwrap();
    assert!(deferred.should_exit);
    assert_eq!(deferred.exit_code, 64);
    assert!(deferred.remaining_arguments.is_empty());

    harness.stage().unwrap();
    let applied = harness
        .manager
        .apply_pending(&harness.target, &none())
        .unwrap();
    let confirmation = {
        let _helper_lease = harness.manager.barrier().acquire_exclusive().unwrap();
        handle(&with_prefix(
            &[CONFIRM_ARGUMENT, &applied.transaction_id],
            &public,
        ))
        .unwrap()
    };

    assert!(!confirmation.should_exit);
    assert_eq!(confirmation.remaining_arguments, public);
    assert!(harness.manager.pending_plan_path().exists());
    assert!(harness.manager.is_confirmed(&applied.transaction_id));
    harness
        .manager
        .complete_confirmed(&applied.transaction_id, &harness.target)
        .unwrap();
    assert!(!harness.manager.pending_plan_path().exists());
}

// OpenAC LauncherSelfUpdateManagerTests.ContendedOrdinaryStartupAllowsOnlyNoPlanOrValidatedStagedPlan.
#[test]
fn contended_ordinary_startup_allows_only_no_plan_or_validated_staged_plan() {
    let harness = Harness::new();
    let barrier = harness.manager.barrier().clone();
    {
        let _session = barrier.acquire_session().unwrap();
        assert!(!harness.handle(&["ordinary"]).unwrap().should_exit);
    }

    harness.stage().unwrap();
    {
        let _session = barrier.acquire_session().unwrap();
        assert!(!harness.handle(&["ordinary"]).unwrap().should_exit);
    }

    let awaiting = harness
        .manager
        .apply_pending(&harness.target, &none())
        .unwrap();
    {
        let _session = barrier.acquire_session().unwrap();
        assert!(harness.handle(&["ordinary"]).is_err());
    }

    let rolled_back = harness
        .manager
        .rollback_awaiting_confirmation(&harness.target, &none())
        .unwrap();
    {
        let _session = barrier.acquire_session().unwrap();
        assert!(harness.handle(&["ordinary"]).is_err());
    }

    set_plan_state(&harness.manager, "applying");
    {
        let _session = barrier.acquire_session().unwrap();
        assert!(harness.handle(&["ordinary"]).is_err());
    }

    assert_eq!(awaiting.state, SelfUpdatePlanState::AwaitingConfirmation);
    assert_eq!(rolled_back.state, SelfUpdatePlanState::RolledBack);
}

// OpenAC LauncherSelfUpdateManagerTests.OrdinaryStartupRecoversApplyingAndFinalizesVerifiedRollback.
#[test]
fn ordinary_startup_recovers_applying_and_finalizes_verified_rollback() {
    let harness = Harness::new();
    harness.stage().unwrap();
    harness
        .manager
        .apply_pending(&harness.target, &none())
        .unwrap();
    set_plan_state(&harness.manager, "applying");

    let result = harness.handle(&["ordinary"]).unwrap();

    assert!(!result.should_exit);
    assert_eq!(result.remaining_arguments, ["ordinary"]);
    assert_eq!(harness.manager.load_pending().unwrap(), None);
    assert_eq!(read(&harness.launcher_path), "old-launcher");
    assert_eq!(read(&harness.support_path), "old-support");
}

// OpenAC LauncherSelfUpdateManagerTests.InternalPrefixSpoofsCannotCrossPlanStateOrExecutableTrust.
#[test]
fn internal_prefix_spoofs_cannot_cross_plan_state_or_executable_trust() {
    let harness = Harness::new();
    harness.stage().unwrap();
    let staged = harness.pending();
    let target = harness.target.display().to_string();
    let max = i32::MAX.to_string();

    assert!(
        harness
            .handle(&[HELPER_ARGUMENT, &max, &target, &staged.transaction_id])
            .is_err()
    );
    assert!(
        harness
            .handle(&[CONFIRM_ARGUMENT, &staged.transaction_id])
            .is_err()
    );

    let awaiting = harness
        .manager
        .apply_pending(&harness.target, &none())
        .unwrap();
    assert!(
        harness
            .handle_as(
                &[CONFIRM_ARGUMENT, &awaiting.transaction_id],
                &harness.target.join("spoof-launcher"),
            )
            .is_err()
    );
    assert!(
        harness
            .handle(&[HELPER_ARGUMENT, &max, &target, &awaiting.transaction_id])
            .is_err()
    );

    let rolled_back = harness
        .manager
        .rollback_awaiting_confirmation(&harness.target, &none())
        .unwrap();
    assert!(
        harness
            .handle(&[HELPER_ARGUMENT, &max, &target, &rolled_back.transaction_id])
            .is_err()
    );
    assert!(
        harness
            .handle(&[CONFIRM_ARGUMENT, &rolled_back.transaction_id])
            .is_err()
    );

    let deferred = harness
        .handle(&["--acdream-self-update-deferred-v1"])
        .unwrap();
    assert!(deferred.should_exit);
    assert_eq!(deferred.exit_code, 64);

    std::fs::write(harness.manager.pending_plan_path(), "{ambiguous").unwrap();
    assert!(
        harness
            .handle(&[CONFIRM_ARGUMENT, &rolled_back.transaction_id])
            .is_err()
    );
    assert!(
        harness
            .handle(&[HELPER_ARGUMENT, &max, &target, &rolled_back.transaction_id])
            .is_err()
    );
}

// The updater's half: `LauncherUpdater::stage_launcher` (OpenAC
// LauncherUpdater.StageLauncherAsync), refused for a launcher that is
// already current.
#[test]
fn the_updater_stages_a_newer_launcher_and_refuses_the_current_one() {
    use ac_launcher_core::updates::{
        ClientVersionStore, LauncherUpdatePhase, LauncherUpdater, ReleaseManifestClient,
    };
    let harness = Harness::new();
    let client = common::fixtures::client_zip(&harness.rid, "client-v2");
    let launcher = launcher_zip(&harness.rid, "new-launcher");
    let manifest = common::fixtures::manifest(
        "2.0.0",
        "1.0.0",
        &harness.rid,
        &FakeServer::url("client.zip"),
        &client,
        &FakeServer::url("launcher.zip"),
        &launcher,
    );
    harness
        .server
        .add(&FakeServer::url("manifest.json"), &manifest);
    harness.server.add(&FakeServer::url("client.zip"), &client);
    harness
        .server
        .add(&FakeServer::url("launcher.zip"), &launcher);
    let updater = |at: &str| {
        let source = ReleaseManifestClient::local_update_feed_override(
            harness.server.transport(),
            &FakeServer::url("manifest.json"),
        )
        .unwrap();
        LauncherUpdater::new(
            Arc::new(source),
            harness.server.transport(),
            Arc::new(ClientVersionStore::new(&harness.root.path_set())),
            version(at),
            &harness.rid,
            || false,
        )
        .unwrap()
        .with_self_updates(
            Arc::new(LauncherSelfUpdateManager::new(
                &harness.root.path_set(),
                harness.server.transport(),
            )),
            harness.layout(),
        )
    };

    let current = updater("2.0.0");
    let check = current.check(&none()).unwrap();
    assert!(!check.is_launcher_update_available);
    let refused = current
        .stage_launcher(&check, &mut |_| {}, &none())
        .unwrap_err();
    assert!(refused.to_string().contains("already current"), "{refused}");

    let older = updater("1.0.0");
    let check = older.check(&none()).unwrap();
    assert!(check.is_launcher_update_available);
    let mut phases = Vec::new();
    let staged = older
        .stage_launcher(&check, &mut |progress| phases.push(progress.phase), &none())
        .unwrap();
    assert_eq!(staged.version.value(), "2.0.0");
    assert_eq!(
        phases.first(),
        Some(&LauncherUpdatePhase::DownloadingLauncher)
    );
    assert_eq!(phases.last(), Some(&LauncherUpdatePhase::StagingLauncher));
    assert_eq!(harness.pending().state, SelfUpdatePlanState::Staged);
    assert_eq!(read(&harness.launcher_path), "old-launcher");
}
