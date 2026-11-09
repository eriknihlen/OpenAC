//! The launcher's self-update across real processes: an apply killed at the
//! canonical boundary recovers on the next start, tampered or linked
//! rollback evidence never launches anything, a start cannot delete a
//! staging transaction still being written, the helper defers to a running
//! session, and spoofed internal arguments cross no plan state. Ported from
//! OpenAC `tests/AcDream.Launcher.Core.Tests/Updates/LauncherSelfUpdateProcessTests.cs`.
//!
//! OpenAC runs a fixture program (`InstallLeaseHolder`) as the launcher. Here
//! this test program is that fixture: it is copied into a launcher folder as
//! `openac-launcher`, and when started with the fixture environment it runs
//! the self-update bootstrap and the fixture modes instead of the tests
//! (`harness = false`, so its arguments are its own). The slow staging that
//! OpenAC runs in a second process runs on a thread here; the start that
//! races it is still another process.

mod common;

use std::panic::AssertUnwindSafe;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_host_core::PathSet;
use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::integrity::{compute_sha256_hex, sha256_hex};
use ac_launcher_core::net::{HttpError, HttpRequest, HttpResponse, HttpTransport};
use ac_launcher_core::updates::self_update_bootstrap::{
    self, CONFIRM_ARGUMENT, HELPER_ARGUMENT, UPDATE_LEASE_BUSY_EXIT_CODE,
};
use ac_launcher_core::updates::{
    INSTALL_RECORD_FILE_NAME, LauncherInstallationLayout, LauncherSelfUpdateManager,
    LauncherVersion, ReleaseArtifact, SelfUpdateApplyBoundary, SelfUpdatePlan, SelfUpdatePlanState,
    UpdateError, detect_rid,
};
use ac_launcher_core::url::WebUrl;
use common::TempDir;
use common::fixtures::{Compression, FakeServer, create_zip, launcher_zip};

const DATA_ENVIRONMENT: &str = "OPENAC_SELF_UPDATE_FIXTURE_DATA";
const TARGET_ENVIRONMENT: &str = "OPENAC_SELF_UPDATE_FIXTURE_TARGET";
const HELPER_PID_ENVIRONMENT: &str = "OPENAC_SELF_UPDATE_FIXTURE_HELPER_PID";
const MODE_ENVIRONMENT: &str = "OPENAC_SELF_UPDATE_FIXTURE_MODE";

// --- The fixture program ------------------------------------------------------------------

/// The fixture's view of a data directory (OpenAC's fixture `Paths`).
fn fixture_paths(data: &Path) -> PathSet {
    PathSet::from_members(
        data.join("fixture-config"),
        data.to_path_buf(),
        data.join("fixture-cache"),
    )
}

/// The bootstrap never downloads; a fixture manager has no network.
struct NoNetwork;

impl HttpTransport for NoNetwork {
    fn get(&self, _: &HttpRequest, _: &CancelToken) -> Result<HttpResponse, HttpError> {
        Err(HttpError::Failed("The fixture has no network.".into()))
    }
}

fn fixture_manager(data: &Path) -> LauncherSelfUpdateManager {
    LauncherSelfUpdateManager::new(&fixture_paths(data), Arc::new(NoNetwork))
}

fn is_bootstrap_invocation(arguments: &[String]) -> bool {
    matches!(
        arguments.first().map(String::as_str),
        Some(
            HELPER_ARGUMENT
                | CONFIRM_ARGUMENT
                | "--acdream-self-update-deferred-v1"
                | "canonical-probe"
        )
    )
}

/// `Some(exit code)` when this process was started as the fixture.
fn fixture_main() -> Option<i32> {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    let data = std::env::var(DATA_ENVIRONMENT)
        .ok()
        .filter(|v| !v.trim().is_empty());
    let target = std::env::var(TARGET_ENVIRONMENT)
        .ok()
        .filter(|v| !v.trim().is_empty());
    if let (Some(data), Some(_)) = (&data, &target)
        && is_bootstrap_invocation(&arguments)
    {
        if arguments[0] == HELPER_ARGUMENT
            && let Ok(helper_pid) = std::env::var(HELPER_PID_ENVIRONMENT)
            && !helper_pid.trim().is_empty()
        {
            std::fs::write(&helper_pid, std::process::id().to_string()).unwrap();
        }
        let manager = fixture_manager(Path::new(data));
        let executable = std::env::current_exe().unwrap();
        let base = executable.parent().unwrap().to_path_buf();
        let startup = match self_update_bootstrap::handle(&arguments, &manager, &base, &executable)
        {
            Ok(startup) => startup,
            Err(error) => {
                eprintln!("{error}");
                return Some(74);
            }
        };
        if startup.should_exit {
            return Some(startup.exit_code);
        }
        return Some(fixture_mode(&startup.remaining_arguments));
    }
    if std::env::var(MODE_ENVIRONMENT).is_ok() {
        return Some(fixture_mode(&arguments));
    }
    None
}

fn fixture_mode(arguments: &[String]) -> i32 {
    match arguments.first().map(String::as_str) {
        Some("crash-self-update") => crash_self_update(&arguments[1..]),
        Some("bootstrap-probe") => bootstrap_probe(&arguments[1..]),
        Some("canonical-probe") => canonical_probe(&arguments[1..]),
        _ => 2,
    }
}

/// Applies the pending plan and freezes right after the canonical launcher
/// was replaced, for the test to kill.
fn crash_self_update(arguments: &[String]) -> i32 {
    let [data, target, ready, canonical_name] = arguments else {
        return 2;
    };
    let (target, ready, canonical_name) = (
        PathBuf::from(target),
        PathBuf::from(ready),
        canonical_name.clone(),
    );
    let watched = target.clone();
    let manager =
        fixture_manager(Path::new(data)).with_apply_observer(Box::new(move |observation| {
            if observation.boundary == SelfUpdateApplyBoundary::AfterTargetMutation
                && observation.path == canonical_name
            {
                if !watched.join(&canonical_name).exists() {
                    return Err(UpdateError::failed(
                        "The canonical launcher vanished at the apply boundary.",
                    ));
                }
                std::fs::write(&ready, std::process::id().to_string()).unwrap();
                loop {
                    std::thread::sleep(Duration::from_secs(3600));
                }
            }
            Ok(())
        }));
    let _lease = manager.barrier().acquire_exclusive().unwrap();
    match manager.apply_pending(&target, &CancelToken::new()) {
        Ok(_) => 0,
        Err(error) => {
            eprintln!("{error}");
            1
        }
    }
}

fn bootstrap_probe(arguments: &[String]) -> i32 {
    let [data, target, executable, result] = arguments else {
        return 2;
    };
    let manager = fixture_manager(Path::new(data));
    let startup = match self_update_bootstrap::handle(
        &["ordinary".to_owned()],
        &manager,
        Path::new(target),
        Path::new(executable),
    ) {
        Ok(startup) => startup,
        Err(error) => {
            eprintln!("{error}");
            return 74;
        }
    };
    let text = if startup.should_exit {
        "exit".to_owned()
    } else {
        startup.remaining_arguments.join("\n")
    };
    std::fs::write(result, text).unwrap();
    if startup.should_exit { 3 } else { 0 }
}

/// Records `<pid>|<this program>` and the remaining arguments, one a line.
fn canonical_probe(arguments: &[String]) -> i32 {
    let Some(marker) = arguments.first() else {
        return 2;
    };
    let mut text = format!(
        "{}|{}",
        std::process::id(),
        std::env::current_exe().unwrap().display()
    );
    for argument in &arguments[1..] {
        text.push('\n');
        text.push_str(argument);
    }
    std::fs::write(marker, text).unwrap();
    0
}

// --- The test runner ----------------------------------------------------------------------

type Case = (&'static str, fn());

const CASES: &[Case] = &[
    (
        "killed_applying_plan_recovers_prior_and_continues_canonical_without_retry_loop",
        killed_applying_plan_recovers_prior_and_continues_canonical_without_retry_loop,
    ),
    (
        "corrupt_backup_after_canonical_crash_never_launches_and_preserves_evidence",
        corrupt_backup_after_canonical_crash_never_launches_and_preserves_evidence,
    ),
    (
        "backup_junction_or_symlink_after_canonical_crash_cannot_mutate_outside_or_launch",
        backup_junction_or_symlink_after_canonical_crash_cannot_mutate_outside_or_launch,
    ),
    (
        "concurrent_startup_cannot_delete_a_visible_slow_stage_transaction",
        concurrent_startup_cannot_delete_a_visible_slow_stage_transaction,
    ),
    (
        "helper_defers_and_opens_the_launcher_as_it_was_when_a_session_lease_appears",
        helper_defers_and_opens_the_launcher_as_it_was_when_a_session_lease_appears,
    ),
    (
        "spoofed_internal_prefixes_cannot_bypass_any_durable_plan_state",
        spoofed_internal_prefixes_cannot_bypass_any_durable_plan_state,
    ),
    (
        "a_staged_update_applies_through_the_helper_and_the_new_launcher_confirms_it",
        a_staged_update_applies_through_the_helper_and_the_new_launcher_confirms_it,
    ),
    (
        "an_update_that_fails_before_changing_anything_opens_the_launcher_with_why",
        an_update_that_fails_before_changing_anything_opens_the_launcher_with_why,
    ),
];

/// The test filters on the command line (the harness's own flags and their
/// values are skipped).
fn filters() -> Vec<String> {
    let mut filters = Vec::new();
    let mut arguments = std::env::args().skip(1);
    while let Some(argument) = arguments.next() {
        if matches!(
            argument.as_str(),
            "--test-threads" | "--format" | "--color" | "--logfile" | "--skip" | "-Z"
        ) {
            arguments.next();
        } else if !argument.starts_with('-') {
            filters.push(argument);
        }
    }
    filters
}

fn main() {
    if let Some(code) = fixture_main() {
        std::process::exit(code);
    }
    let filters = filters();
    let selected: Vec<&Case> = CASES
        .iter()
        .filter(|(name, _)| filters.is_empty() || filters.iter().any(|f| name.contains(f.as_str())))
        .collect();
    println!("\nrunning {} tests", selected.len());
    let mut failed = Vec::new();
    for (name, case) in &selected {
        let outcome = std::panic::catch_unwind(AssertUnwindSafe(case));
        println!(
            "test {name} ... {}",
            if outcome.is_ok() { "ok" } else { "FAILED" }
        );
        if outcome.is_err() {
            failed.push(*name);
        }
    }
    println!(
        "\ntest result: {}. {} passed; {} failed\n",
        if failed.is_empty() { "ok" } else { "FAILED" },
        selected.len() - failed.len(),
        failed.len()
    );
    if !failed.is_empty() {
        std::process::exit(101);
    }
}

// --- Helpers ------------------------------------------------------------------------------

fn none() -> CancelToken {
    CancelToken::none()
}

fn launcher_name(rid: &str) -> String {
    format!(
        "openac-launcher{}",
        if rid.starts_with("win-") { ".exe" } else { "" }
    )
}

fn hash(path: &Path) -> String {
    compute_sha256_hex(path).unwrap()
}

struct PreparedLauncher {
    canonical_name: String,
    canonical_path: PathBuf,
    new_archive: Vec<u8>,
    new_canonical_hash: String,
}

/// This program, installed as the launcher in `target` (marked old) and
/// packed as the update (marked new). Bytes after the end of a program do
/// not change how it runs.
fn prepare_launcher_closure(target: &Path, rid: &str) -> PreparedLauncher {
    std::fs::create_dir_all(target).unwrap();
    let canonical_name = launcher_name(rid);
    let canonical_path = target.join(&canonical_name);
    let program = std::fs::read(std::env::current_exe().unwrap()).unwrap();
    let old = [program.as_slice(), b"-old"].concat();
    let new = [program.as_slice(), b"-new"].concat();
    std::fs::write(&canonical_path, &old).unwrap();
    let attributes = if cfg!(unix) {
        ac_launcher_core::fsutil::set_unix_mode(&canonical_path, 0o755).unwrap();
        0x8000 | 0o755
    } else {
        0x81ED
    };
    let new_archive = create_zip(
        &[(canonical_name.as_str(), new.as_slice(), Some(attributes))],
        Compression::Stored,
    );
    PreparedLauncher {
        canonical_name,
        canonical_path,
        new_archive,
        new_canonical_hash: sha256_hex(&new),
    }
}

fn stage(
    manager: &LauncherSelfUpdateManager,
    server: &FakeServer,
    archive: &[u8],
    target: &Path,
    rid: &str,
) -> SelfUpdatePlan {
    let url = FakeServer::url("launcher.zip");
    server.add(&url, archive);
    manager
        .stage_artifact(
            &LauncherVersion::try_parse("2.0.0").unwrap(),
            rid,
            &ReleaseArtifact {
                url: WebUrl::parse(&url).unwrap(),
                sha256: sha256_hex(archive),
                size: archive.len() as u64,
            },
            &LauncherInstallationLayout::flat(target, rid).unwrap(),
            &mut |_| {},
            &none(),
        )
        .unwrap();
    manager.load_pending().unwrap().unwrap()
}

fn start(executable: &Path, arguments: &[&str], environment: &[(&str, &Path)]) -> Child {
    let mut command = Command::new(executable);
    command
        .args(arguments)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::piped());
    for (name, value) in environment {
        command.env(name, value);
    }
    command.spawn().unwrap()
}

/// This program in a fixture mode.
fn start_fixture(arguments: &[&str]) -> Child {
    let mut command = Command::new(std::env::current_exe().unwrap());
    command
        .args(arguments)
        .env(MODE_ENVIRONMENT, "1")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::piped());
    command.spawn().unwrap()
}

fn stderr_of(child: &mut Child) -> String {
    let mut text = String::new();
    if let Some(mut stderr) = child.stderr.take() {
        let _ = std::io::Read::read_to_string(&mut stderr, &mut text);
    }
    text
}

fn wait_exit(child: &mut Child, timeout: Duration) -> i32 {
    let deadline = Instant::now() + timeout;
    loop {
        if let Some(status) = child.try_wait().unwrap() {
            return status.code().unwrap_or(-1);
        }
        assert!(
            Instant::now() < deadline,
            "the process did not exit in time"
        );
        std::thread::sleep(Duration::from_millis(20));
    }
}

fn wait_until(
    condition: impl Fn() -> bool,
    timeout: Duration,
    failure: &str,
    child: Option<&mut Child>,
) {
    let deadline = Instant::now() + timeout;
    let mut child = child;
    while !condition() {
        if let Some(child) = child.as_deref_mut()
            && let Ok(Some(status)) = child.try_wait()
        {
            panic!(
                "{failure} The process exited {status}: {}",
                stderr_of(child)
            );
        }
        assert!(Instant::now() < deadline, "{failure}");
        std::thread::sleep(Duration::from_millis(20));
    }
}

fn kill(child: &mut Child) {
    let _ = child.kill();
    wait_exit(child, Duration::from_secs(10));
}

fn bootstrap_environment<'a>(
    data: &'a Path,
    target: &'a Path,
    helper_pid: &'a Path,
) -> Vec<(&'static str, &'a Path)> {
    vec![
        (DATA_ENVIRONMENT, data),
        (TARGET_ENVIRONMENT, target),
        (HELPER_PID_ENVIRONMENT, helper_pid),
    ]
}

struct CrashedUpdate {
    root: TempDir,
    data: PathBuf,
    target: PathBuf,
    manager: LauncherSelfUpdateManager,
    plan: SelfUpdatePlan,
    prepared: PreparedLauncher,
}

/// Stages an update and kills an apply right after the canonical launcher
/// was replaced.
fn prepare_killed_after_canonical_replace() -> CrashedUpdate {
    let root = TempDir::new("self-update-process");
    let data = root.join("data");
    let target = root.join("launcher");
    let ready = root.join("crash.ready");
    let rid = detect_rid().unwrap();
    let prepared = prepare_launcher_closure(&target, &rid);
    let server = FakeServer::new();
    let manager = LauncherSelfUpdateManager::new(&root.path_set(), server.transport());
    let plan = stage(&manager, &server, &prepared.new_archive, &target, &rid);
    let mut crash = start_fixture(&[
        "crash-self-update",
        &data.display().to_string(),
        &target.display().to_string(),
        &ready.display().to_string(),
        &prepared.canonical_name,
    ]);
    let ready_path = ready.clone();
    wait_until(
        || ready_path.exists(),
        Duration::from_secs(20),
        "The apply did not reach the canonical boundary.",
        Some(&mut crash),
    );
    kill(&mut crash);
    assert_eq!(
        manager.load_pending().unwrap().unwrap().state,
        SelfUpdatePlanState::Applying
    );
    CrashedUpdate {
        root,
        data,
        target,
        manager,
        plan,
        prepared,
    }
}

// --- Cases --------------------------------------------------------------------------------

// OpenAC LauncherSelfUpdateProcessTests.KilledApplyingPlanRecoversPriorAndContinuesCanonicalWithoutRetryLoop.
fn killed_applying_plan_recovers_prior_and_continues_canonical_without_retry_loop() {
    let root = TempDir::new("self-update-process");
    let data = root.join("data");
    let target = root.join("launcher");
    let ready = root.join("crash.ready");
    let launched = root.join("replacement.ready");
    let helper_pid = root.join("helper.pid");
    let suffix: Vec<String> = vec![
        "--config-dir".into(),
        root.join("isolated config").display().to_string(),
        "--data-dir".into(),
        root.join("isolated data").display().to_string(),
        "--cache-dir".into(),
        root.join("isolated cache").display().to_string(),
        "--update-manifest-uri".into(),
        "http://127.0.0.1:43119/manifest.json".into(),
    ];
    let rid = detect_rid().unwrap();
    let prepared = prepare_launcher_closure(&target, &rid);
    let old_hash = hash(&prepared.canonical_path);
    let server = FakeServer::new();
    let manager = LauncherSelfUpdateManager::new(&root.path_set(), server.transport());
    let plan = stage(&manager, &server, &prepared.new_archive, &target, &rid);

    let mut crash = start_fixture(&[
        "crash-self-update",
        &data.display().to_string(),
        &target.display().to_string(),
        &ready.display().to_string(),
        &prepared.canonical_name,
    ]);
    let ready_path = ready.clone();
    wait_until(
        || ready_path.exists(),
        Duration::from_secs(20),
        "The apply did not reach the canonical boundary.",
        Some(&mut crash),
    );
    assert!(prepared.canonical_path.exists());
    kill(&mut crash);

    assert!(prepared.canonical_path.exists());
    let boundary_hash = hash(&prepared.canonical_path);
    assert!(boundary_hash == old_hash || boundary_hash == prepared.new_canonical_hash);

    let mut arguments = vec!["canonical-probe".to_owned(), launched.display().to_string()];
    arguments.extend(suffix.iter().cloned());
    let arguments: Vec<&str> = arguments.iter().map(String::as_str).collect();
    let mut canonical = start(
        &prepared.canonical_path,
        &arguments,
        &bootstrap_environment(&data, &target, &helper_pid),
    );
    let code = wait_exit(&mut canonical, Duration::from_secs(20));
    assert_eq!(code, 0, "{}", stderr_of(&mut canonical));
    let launched_path = launched.clone();
    wait_until(
        || launched_path.exists(),
        Duration::from_secs(30),
        "The canonical launcher did not continue.",
        None,
    );
    let pending = manager.pending_plan_path().to_path_buf();
    wait_until(
        || !pending.exists(),
        Duration::from_secs(30),
        "The self-update journal did not converge.",
        None,
    );

    assert_eq!(hash(&prepared.canonical_path), old_hash);
    assert!(!target.join(INSTALL_RECORD_FILE_NAME).exists());
    assert!(
        !manager
            .transaction_directory(&plan.transaction_id)
            .unwrap()
            .exists()
    );
    {
        let lease = manager.barrier().acquire_exclusive().unwrap();
        assert!(
            manager
                .cleanup_owned_residue_under_lease(None, &target, &lease)
                .unwrap()
        );
    }
    let leftovers = std::fs::read_dir(&target)
        .unwrap()
        .flatten()
        .filter(|entry| {
            entry
                .file_name()
                .to_string_lossy()
                .starts_with(".acdream-self-update-")
        })
        .count();
    assert_eq!(leftovers, 0);
    assert_eq!(manager.load_pending().unwrap(), None);

    let marker = std::fs::read_to_string(&launched).unwrap();
    assert!(
        marker.ends_with(&format!("\n{}", suffix.join("\n"))),
        "{marker}"
    );
    let pid: u32 = marker.split('|').next().unwrap().parse().unwrap();
    assert_eq!(
        pid,
        canonical.id(),
        "the canonical launcher itself continued"
    );
    assert!(!helper_pid.exists());
    if cfg!(unix) {
        let mode = ac_launcher_core::fsutil::unix_mode(&prepared.canonical_path)
            .unwrap()
            .unwrap();
        assert_ne!(mode & 0o111, 0);
    }
}

// OpenAC LauncherSelfUpdateProcessTests.CorruptBackupAfterCanonicalCrashNeverLaunchesAndPreservesEvidence.
fn corrupt_backup_after_canonical_crash_never_launches_and_preserves_evidence() {
    let crashed = prepare_killed_after_canonical_replace();
    let backup_path = crashed
        .manager
        .target_transaction_directory(&crashed.plan)
        .unwrap()
        .join("backup")
        .join(&crashed.prepared.canonical_name);
    assert!(backup_path.exists());
    std::fs::write(&backup_path, "tampered rollback backup").unwrap();
    let tampered_hash = hash(&backup_path);
    let launched = crashed.root.join("corrupt-backup-launched");
    let helper_pid = crashed.root.join("corrupt-backup-helper.pid");

    let mut canonical = start(
        &crashed.prepared.canonical_path,
        &["canonical-probe", &launched.display().to_string()],
        &bootstrap_environment(&crashed.data, &crashed.target, &helper_pid),
    );
    assert_ne!(wait_exit(&mut canonical, Duration::from_secs(20)), 0);
    assert!(!helper_pid.exists());

    assert!(!launched.exists());
    let preserved = crashed.manager.load_pending().unwrap().unwrap();
    assert_eq!(preserved.state, SelfUpdatePlanState::Applying);
    assert_eq!(preserved.transaction_id, crashed.plan.transaction_id);
    assert!(
        crashed
            .manager
            .target_transaction_directory(&crashed.plan)
            .unwrap()
            .is_dir()
    );
    assert_eq!(hash(&backup_path), tampered_hash);
    assert_eq!(
        hash(&crashed.prepared.canonical_path),
        crashed.prepared.new_canonical_hash
    );
    assert!(
        crashed
            .manager
            .verify_restored_prior(&crashed.target, &none())
            .is_err()
    );
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

fn create_directory_link(link: &Path, target: &Path) {
    #[cfg(unix)]
    std::os::unix::fs::symlink(target, link).unwrap();
    #[cfg(windows)]
    {
        let output = Command::new("cmd.exe")
            .args(["/d", "/c", "mklink", "/J"])
            .arg(link)
            .arg(target)
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "Could not create the test junction: {}{}",
            String::from_utf8_lossy(&output.stderr),
            String::from_utf8_lossy(&output.stdout)
        );
    }
}

// OpenAC LauncherSelfUpdateProcessTests.BackupJunctionOrSymlinkAfterCanonicalCrashCannotMutateOutsideOrLaunch.
fn backup_junction_or_symlink_after_canonical_crash_cannot_mutate_outside_or_launch() {
    let crashed = prepare_killed_after_canonical_replace();
    let swap = crashed
        .manager
        .target_transaction_directory(&crashed.plan)
        .unwrap();
    let backup = swap.join("backup");
    let preserved_backup = crashed.root.join("preserved-backup");
    let outside = crashed.root.join("outside-backup");
    std::fs::rename(&backup, &preserved_backup).unwrap();
    copy_directory(&preserved_backup, &outside);
    let outside_canonical = outside.join(&crashed.prepared.canonical_name);
    let outside_hash = hash(&outside_canonical);
    create_directory_link(&backup, &outside);
    let launched = crashed.root.join("reparse-backup-launched");
    let helper_pid = crashed.root.join("reparse-backup-helper.pid");

    let result = std::panic::catch_unwind(AssertUnwindSafe(|| {
        let mut canonical = start(
            &crashed.prepared.canonical_path,
            &["canonical-probe", &launched.display().to_string()],
            &bootstrap_environment(&crashed.data, &crashed.target, &helper_pid),
        );
        assert_ne!(wait_exit(&mut canonical, Duration::from_secs(20)), 0);
        assert!(!helper_pid.exists());

        assert!(!launched.exists());
        assert!(outside_canonical.exists());
        assert_eq!(hash(&outside_canonical), outside_hash);
        assert!(ac_launcher_core::fsutil::is_link(&backup).unwrap());
        let preserved = crashed.manager.load_pending().unwrap().unwrap();
        assert_eq!(preserved.state, SelfUpdatePlanState::Applying);
        assert_eq!(
            hash(&crashed.prepared.canonical_path),
            crashed.prepared.new_canonical_hash
        );
    }));
    // Take the link away before the folder is deleted (never follow it).
    if ac_launcher_core::fsutil::is_link(&backup).unwrap_or(false) {
        let _ = std::fs::remove_dir(&backup).or_else(|_| std::fs::remove_file(&backup));
    }
    if let Err(panic) = result {
        std::panic::resume_unwind(panic);
    }
}

// OpenAC LauncherSelfUpdateProcessTests.ConcurrentStartupCannotDeleteAVisibleSlowStageTransaction.
fn concurrent_startup_cannot_delete_a_visible_slow_stage_transaction() {
    let root = TempDir::new("self-update-process");
    let data = root.join("data");
    let target = root.join("launcher");
    let result_path = root.join("startup.result");
    std::fs::create_dir_all(&target).unwrap();
    let rid = detect_rid().unwrap();
    let canonical = target.join(launcher_name(&rid));
    std::fs::write(&canonical, "running launcher").unwrap();
    let archive = launcher_zip(&rid, &"s".repeat(2 * 1024 * 1024));
    let server = FakeServer::new();
    let url = FakeServer::url("slow-launcher.zip");
    server.add_slow(&url, &archive, 4096, Duration::from_millis(2));
    let observer = LauncherSelfUpdateManager::new(&root.path_set(), server.transport());

    std::thread::scope(|scope| {
        let staging = scope.spawn(|| {
            let manager = LauncherSelfUpdateManager::new(&root.path_set(), server.transport());
            manager.stage_artifact(
                &LauncherVersion::try_parse("2.0.0").unwrap(),
                &rid,
                &ReleaseArtifact {
                    url: WebUrl::parse(&url).unwrap(),
                    sha256: sha256_hex(&archive),
                    size: archive.len() as u64,
                },
                &LauncherInstallationLayout::flat(&target, &rid).unwrap(),
                &mut |_| {},
                &none(),
            )
        });
        let transactions = observer.transactions_directory().to_path_buf();
        wait_until(
            || std::fs::read_dir(&transactions).is_ok_and(|mut entries| entries.next().is_some()),
            Duration::from_secs(10),
            "The slow staging transaction did not become visible.",
            None,
        );
        assert!(!observer.pending_plan_path().exists());
        let transaction: Vec<PathBuf> = std::fs::read_dir(&transactions)
            .unwrap()
            .flatten()
            .map(|entry| entry.path())
            .collect();
        assert_eq!(transaction.len(), 1);
        let transaction = &transaction[0];

        let mut startup = start_fixture(&[
            "bootstrap-probe",
            &data.display().to_string(),
            &target.display().to_string(),
            &canonical.display().to_string(),
            &result_path.display().to_string(),
        ]);
        assert_ne!(wait_exit(&mut startup, Duration::from_secs(10)), 0);
        assert!(!result_path.exists());
        assert!(transaction.is_dir());
        assert!(!observer.pending_plan_path().exists());

        staging.join().unwrap().unwrap();
        let plan = observer.load_pending().unwrap().unwrap();
        assert_eq!(plan.state, SelfUpdatePlanState::Staged);
        assert!(transaction.is_dir());
    });
}

// OpenAC LauncherSelfUpdateProcessTests.HelperDefersWithoutRestartWhenSharedSessionLeaseAppears.
// Changed from OpenAC, which started nothing: the user saw the launcher
// close and never come back. The launcher as it was opens instead and says
// why the update waits.
fn helper_defers_and_opens_the_launcher_as_it_was_when_a_session_lease_appears() {
    let root = TempDir::new("self-update-process");
    let data = root.join("data");
    let target = root.join("launcher");
    let prior_launch = root.join("prior-launch");
    let helper_pid = root.join("helper.pid");
    let rid = detect_rid().unwrap();
    let prepared = prepare_launcher_closure(&target, &rid);
    let server = FakeServer::new();
    let manager = LauncherSelfUpdateManager::new(&root.path_set(), server.transport());
    let plan = stage(&manager, &server, &prepared.new_archive, &target, &rid);
    let _session = manager.barrier().acquire_session().unwrap();

    let max = i32::MAX.to_string();
    let target_text = target.display().to_string();
    let launch_text = prior_launch.display().to_string();
    let mut helper = start(
        &manager.staged_launcher_path(&plan).unwrap(),
        &[
            HELPER_ARGUMENT,
            &max,
            &target_text,
            &plan.transaction_id,
            "canonical-probe",
            &launch_text,
        ],
        &bootstrap_environment(&data, &target, &helper_pid),
    );
    let code = wait_exit(&mut helper, Duration::from_secs(10));

    assert_eq!(
        code,
        UPDATE_LEASE_BUSY_EXIT_CODE,
        "stderr: {}",
        stderr_of(&mut helper)
    );
    assert!(helper_pid.exists());
    let marker = prior_launch.clone();
    wait_until(
        || marker.exists(),
        Duration::from_secs(20),
        "The helper did not open the launcher as it was.",
        None,
    );
    let text = std::fs::read_to_string(&prior_launch).unwrap();
    let launched_path = text.lines().next().unwrap().split('|').nth(1).unwrap();
    assert!(
        ac_launcher_core::fsutil::paths_equal(Path::new(launched_path), &prepared.canonical_path),
        "{text}"
    );
    assert_ne!(hash(&prepared.canonical_path), prepared.new_canonical_hash);
    let deferred = manager.load_pending().unwrap().unwrap();
    assert_eq!(deferred.state, SelfUpdatePlanState::Staged);
    assert_eq!(deferred.transaction_id, plan.transaction_id);
    let notice = manager.take_update_notice().expect("why the update waits");
    assert!(notice.contains("not installed yet"), "{notice}");
}

fn set_plan_state(manager: &LauncherSelfUpdateManager, state: &str) {
    let path = manager.pending_plan_path();
    let text = std::fs::read_to_string(path).unwrap();
    let key = "\"state\": \"";
    let start = text.find(key).unwrap() + key.len();
    let end = start + text[start..].find('"').unwrap();
    std::fs::write(path, format!("{}{state}{}", &text[..start], &text[end..])).unwrap();
}

fn assert_internal_spoofs_rejected(
    canonical: &Path,
    target: &Path,
    transaction_id: &str,
    data: &Path,
    state: &str,
) {
    let max = i32::MAX.to_string();
    let target_text = target.display().to_string();
    let attempts: [(&str, Vec<&str>, Option<i32>); 3] = [
        (
            "deferred",
            vec!["--acdream-self-update-deferred-v1"],
            Some(64),
        ),
        (
            "helper",
            vec![HELPER_ARGUMENT, &max, &target_text, transaction_id],
            None,
        ),
        ("confirm", vec![CONFIRM_ARGUMENT, transaction_id], None),
    ];
    for (name, arguments, exact_exit) in attempts {
        let mut process = start(
            canonical,
            &arguments,
            &[(DATA_ENVIRONMENT, data), (TARGET_ENVIRONMENT, target)],
        );
        let code = wait_exit(&mut process, Duration::from_secs(10));
        let stderr = stderr_of(&mut process);
        match exact_exit {
            Some(expected) => assert_eq!(code, expected, "{state}/{name}: {stderr}"),
            None => assert_ne!(code, 0, "{state}/{name} unexpectedly succeeded."),
        }
    }
}

// OpenAC LauncherSelfUpdateProcessTests.SpoofedInternalPrefixesCannotBypassAnyDurablePlanState.
fn spoofed_internal_prefixes_cannot_bypass_any_durable_plan_state() {
    let root = TempDir::new("self-update-process");
    let data = root.join("data");
    let target = root.join("launcher");
    let rid = detect_rid().unwrap();
    let prepared = prepare_launcher_closure(&target, &rid);
    let server = FakeServer::new();
    let manager = LauncherSelfUpdateManager::new(&root.path_set(), server.transport());
    let plan = stage(&manager, &server, &prepared.new_archive, &target, &rid);
    let canonical = &prepared.canonical_path;

    assert_internal_spoofs_rejected(canonical, &target, &plan.transaction_id, &data, "staged");

    let plan = manager.apply_pending(&target, &none()).unwrap();
    set_plan_state(&manager, "applying");
    assert_internal_spoofs_rejected(canonical, &target, &plan.transaction_id, &data, "applying");

    manager.recover_applying(&target, &none()).unwrap();
    let plan = manager.apply_pending(&target, &none()).unwrap();
    assert_eq!(plan.state, SelfUpdatePlanState::AwaitingConfirmation);
    assert_internal_spoofs_rejected(
        canonical,
        &target,
        &plan.transaction_id,
        &data,
        "awaitingConfirmation",
    );

    let plan = manager
        .rollback_awaiting_confirmation(&target, &none())
        .unwrap();
    assert_internal_spoofs_rejected(
        canonical,
        &target,
        &plan.transaction_id,
        &data,
        "rolledBack",
    );

    std::fs::write(manager.pending_plan_path(), "{ambiguous").unwrap();
    assert_internal_spoofs_rejected(canonical, &target, &plan.transaction_id, &data, "ambiguous");
}

// The whole happy path, which OpenAC's process tests leave to its live
// release checks: a start with a staged plan hands it to the helper and
// exits; the helper waits for it, applies the plan and starts the new
// launcher, which confirms under the helper's lease and continues with the
// launcher's own arguments; the helper then finishes the transaction.
fn a_staged_update_applies_through_the_helper_and_the_new_launcher_confirms_it() {
    let root = TempDir::new("self-update-process");
    let data = root.join("data");
    let target = root.join("launcher");
    let launched = root.join("updated.ready");
    let helper_pid = root.join("helper.pid");
    let rid = detect_rid().unwrap();
    let prepared = prepare_launcher_closure(&target, &rid);
    let server = FakeServer::new();
    let manager = LauncherSelfUpdateManager::new(&root.path_set(), server.transport());
    stage(&manager, &server, &prepared.new_archive, &target, &rid);

    let launched_text = launched.display().to_string();
    let mut first = start(
        &prepared.canonical_path,
        &["canonical-probe", &launched_text, "--public", "argument"],
        &bootstrap_environment(&data, &target, &helper_pid),
    );
    let code = wait_exit(&mut first, Duration::from_secs(20));
    assert_eq!(code, 0, "{}", stderr_of(&mut first));
    assert!(
        !launched.exists(),
        "the old launcher handed over instead of continuing"
    );

    let pending = manager.pending_plan_path().to_path_buf();
    wait_until(
        || launched.exists() && !pending.exists(),
        Duration::from_secs(60),
        "The helper did not apply and confirm the update.",
        None,
    );
    assert!(helper_pid.exists());
    assert_eq!(hash(&prepared.canonical_path), prepared.new_canonical_hash);
    assert!(target.join(INSTALL_RECORD_FILE_NAME).is_file());
    let marker = std::fs::read_to_string(&launched).unwrap();
    assert!(marker.ends_with("\n--public\nargument"), "{marker}");
    let pid: u32 = marker.split('|').next().unwrap().parse().unwrap();
    assert_ne!(
        pid,
        first.id(),
        "the new launcher continued, not the old one"
    );
}

// The helper stops before it changes anything when the launcher's files do
// not match its install record. It used to exit and start nothing, leaving
// the plan staged, so every start handed it over again and the launcher
// never opened. Now the plan goes, the launcher starts as it was, and the
// reason waits for its window; the next start is an ordinary one.
fn an_update_that_fails_before_changing_anything_opens_the_launcher_with_why() {
    let root = TempDir::new("self-update-process");
    let data = root.join("data");
    let target = root.join("launcher");
    let launched = root.join("prior.ready");
    let helper_pid = root.join("helper.pid");
    let rid = detect_rid().unwrap();
    let prepared = prepare_launcher_closure(&target, &rid);
    let server = FakeServer::new();
    let manager = LauncherSelfUpdateManager::new(&root.path_set(), server.transport());
    stage(&manager, &server, &prepared.new_archive, &target, &rid);
    std::fs::write(target.join(INSTALL_RECORD_FILE_NAME), "{}").unwrap();
    let prior_hash = hash(&prepared.canonical_path);

    let launched_text = launched.display().to_string();
    let environment = bootstrap_environment(&data, &target, &helper_pid);
    let mut first = start(
        &prepared.canonical_path,
        &["canonical-probe", &launched_text, "--public", "argument"],
        &environment,
    );
    let code = wait_exit(&mut first, Duration::from_secs(20));
    assert_eq!(code, 0, "{}", stderr_of(&mut first));
    let marker = launched.clone();
    wait_until(
        || marker.exists(),
        Duration::from_secs(30),
        "The helper did not start the launcher as it was.",
        None,
    );

    assert!(helper_pid.exists(), "the start handed over to the helper");
    assert!(!manager.pending_plan_path().exists());
    assert_eq!(hash(&prepared.canonical_path), prior_hash);
    let text = std::fs::read_to_string(&launched).unwrap();
    assert!(text.ends_with("\n--public\nargument"), "{text}");
    let pid: u32 = text.split('|').next().unwrap().parse().unwrap();
    assert_ne!(pid, first.id());
    let notice = manager.take_update_notice().expect("the reason is kept");
    assert!(
        notice.contains("Launcher 2.0.0 could not be installed"),
        "{notice}"
    );
    assert!(notice.contains("Nothing was changed"), "{notice}");
    assert!(notice.contains(INSTALL_RECORD_FILE_NAME), "{notice}");
    assert!(notice.contains("new, empty folder"), "{notice}");
    assert_eq!(manager.take_update_notice(), None, "shown once");

    // The next start is an ordinary one: no helper, no loop.
    std::fs::remove_file(&launched).unwrap();
    std::fs::remove_file(&helper_pid).unwrap();
    let mut second = start(
        &prepared.canonical_path,
        &["canonical-probe", &launched_text],
        &environment,
    );
    let code = wait_exit(&mut second, Duration::from_secs(20));
    assert_eq!(code, 0, "{}", stderr_of(&mut second));
    let text = std::fs::read_to_string(&launched).unwrap();
    let pid: u32 = text.split('|').next().unwrap().parse().unwrap();
    assert_eq!(pid, second.id(), "the start continued itself");
    assert!(!helper_pid.exists());
}
