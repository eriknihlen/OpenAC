//! What the launcher does about a self-update before its window opens: a
//! normal start finishes or recovers whatever a previous update left, or
//! hands a staged update to the staged launcher (the helper) and exits; the
//! helper applies it and starts the new launcher, which confirms it runs.
//! Each internal mode is an exact first argument; anything else with the
//! internal prefix is refused (exit 64).

use std::path::Path;
use std::process::Command;
use std::time::{Duration, Instant};

use super::UpdateError;
use super::platform;
use super::self_update::{
    LauncherInstallationLayout, LauncherSelfUpdateManager, SelfUpdatePlan, SelfUpdatePlanState,
};
use crate::cancel::CancelToken;
use crate::fsutil;

/// First argument of the helper mode: `<parent pid> <target> <transaction>`
/// and the launcher's own arguments follow.
pub const HELPER_ARGUMENT: &str = "--acdream-self-update-helper-v1";

/// First argument of the confirmation mode: `<transaction>` and the
/// launcher's own arguments follow.
pub const CONFIRM_ARGUMENT: &str = "--acdream-self-update-confirm-v1";

/// The helper's exit code when a session or another update holds the
/// barrier: the update waits for the next start.
pub const UPDATE_LEASE_BUSY_EXIT_CODE: i32 = 73;

/// Exit code for a malformed internal invocation (`EX_USAGE`).
pub const USAGE_EXIT_CODE: i32 = 64;

/// The helper rolled back and started the prior launcher.
pub const ROLLED_BACK_EXIT_CODE: i32 = 74;

/// The helper could not establish a safe state and started nothing.
pub const AMBIGUOUS_EXIT_CODE: i32 = 75;

const INTERNAL_ARGUMENT_PREFIX: &str = "--acdream-self-update-";
const CONFIRMATION_TIMEOUT: Duration = Duration::from_secs(30);

/// What the start should do next.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelfUpdateStartupResult {
    /// Exit now with `exit_code` (an internal mode ran, or a helper took
    /// over).
    pub should_exit: bool,
    /// The exit code when exiting.
    pub exit_code: i32,
    /// The launcher's own arguments, to start the window with.
    pub remaining_arguments: Vec<String>,
}

impl SelfUpdateStartupResult {
    fn exit(exit_code: i32) -> Self {
        Self {
            should_exit: true,
            exit_code,
            remaining_arguments: Vec::new(),
        }
    }

    fn continue_with(arguments: &[String]) -> Self {
        Self {
            should_exit: false,
            exit_code: 0,
            remaining_arguments: arguments.to_vec(),
        }
    }
}

/// [`handle`] for the launcher installed per `layout`.
pub fn handle_for_layout(
    arguments: &[String],
    manager: &LauncherSelfUpdateManager,
    layout: &LauncherInstallationLayout,
    current_executable_path: &Path,
) -> Result<SelfUpdateStartupResult, UpdateError> {
    handle(
        arguments,
        manager,
        &layout.installed_root,
        current_executable_path,
    )
}

/// Runs the self-update part of a launcher start. `launcher_base_directory`
/// is the installed root, `current_executable_path` this program.
pub fn handle(
    arguments: &[String],
    manager: &LauncherSelfUpdateManager,
    launcher_base_directory: &Path,
    current_executable_path: &Path,
) -> Result<SelfUpdateStartupResult, UpdateError> {
    let base_directory = fsutil::full_path(launcher_base_directory);
    let executable = fsutil::full_path(current_executable_path);
    let cancel = CancelToken::new();

    if arguments.first().map(String::as_str) == Some(HELPER_ARGUMENT) {
        let helper_base = executable
            .parent()
            .map(Path::to_path_buf)
            .ok_or_else(|| UpdateError::failed("The staged launcher has no directory."))?;
        let parent_pid = arguments.get(1).and_then(|text| parse_pid(text));
        let (Some(parent_pid), true) = (parent_pid, arguments.len() >= 4) else {
            return Ok(SelfUpdateStartupResult::exit(USAGE_EXIT_CODE));
        };
        let exit_code = run_helper(
            manager,
            &helper_base,
            &executable,
            parent_pid,
            Path::new(&arguments[2]),
            &arguments[3],
            &arguments[4..],
            &cancel,
        )?;
        return Ok(SelfUpdateStartupResult::exit(exit_code));
    }

    if arguments.first().map(String::as_str) == Some(CONFIRM_ARGUMENT) {
        if arguments.len() < 2 {
            return Ok(SelfUpdateStartupResult::exit(USAGE_EXIT_CODE));
        }
        if manager.barrier().try_acquire_session()?.is_some() {
            return Err(UpdateError::failed(
                "Self-update confirmation is trusted only while its helper owns the exclusive \
                 update lease.",
            ));
        }
        manager.confirm(&arguments[1], &base_directory, &executable, &cancel)?;
        return Ok(SelfUpdateStartupResult::continue_with(&arguments[2..]));
    }

    if arguments
        .first()
        .is_some_and(|first| first.starts_with(INTERNAL_ARGUMENT_PREFIX))
    {
        // Internal modes are an exact vocabulary: an unknown one (an old
        // deferred-restart marker among them) never skips a pending
        // recovery.
        return Ok(SelfUpdateStartupResult::exit(USAGE_EXIT_CODE));
    }

    manager.load_pending()?;

    let Some(lease) = manager.barrier().try_acquire_exclusive()? else {
        let Some(_shared) = manager.barrier().try_acquire_session()? else {
            return Err(UpdateError::failed(
                "Launcher startup is blocked by an active update or recovery transaction.",
            ));
        };
        let Some(blocked) = manager.load_pending()? else {
            return Ok(SelfUpdateStartupResult::continue_with(arguments));
        };
        validate_canonical_startup(&blocked, &base_directory, &executable)?;
        if blocked.state != SelfUpdatePlanState::Staged {
            return Err(UpdateError::failed(format!(
                "Self-update state '{}' requires exclusive recovery.",
                blocked.state
            )));
        }
        return Ok(SelfUpdateStartupResult::continue_with(arguments));
    };

    let plan = manager.load_pending()?;
    manager.cleanup_owned_residue_under_lease(plan.as_ref(), &base_directory, &lease)?;
    let Some(mut plan) = plan else {
        return Ok(SelfUpdateStartupResult::continue_with(arguments));
    };
    validate_canonical_startup(&plan, &base_directory, &executable)?;

    if plan.state == SelfUpdatePlanState::AwaitingConfirmation {
        if !manager.is_confirmed(&plan.transaction_id) {
            manager.confirm(&plan.transaction_id, &base_directory, &executable, &cancel)?;
        }
        manager.complete_confirmed(&plan.transaction_id, &base_directory)?;
        manager.cleanup_owned_residue_under_lease(None, &base_directory, &lease)?;
        return Ok(SelfUpdateStartupResult::continue_with(arguments));
    }

    if matches!(
        plan.state,
        SelfUpdatePlanState::Applying | SelfUpdatePlanState::RolledBack
    ) {
        if plan.state == SelfUpdatePlanState::Applying {
            plan = manager.recover_applying(&base_directory, &cancel)?;
        }
        if plan.state != SelfUpdatePlanState::RolledBack {
            return Err(UpdateError::failed(
                "The interrupted self-update did not produce a rollback receipt.",
            ));
        }
        manager.complete_rolled_back(&plan.transaction_id, &base_directory, &lease, &cancel)?;
        manager.cleanup_owned_residue_under_lease(None, &base_directory, &lease)?;
        return Ok(SelfUpdateStartupResult::continue_with(arguments));
    }

    start_staged_helper(manager, &plan, &base_directory, arguments)?;
    Ok(SelfUpdateStartupResult::exit(0))
}

/// The running launcher hands a just-staged update to its helper. True when
/// the helper was started (the caller then exits); false when the barrier
/// is busy or nothing staged is pending.
pub fn try_apply_staged_update_now(
    manager: &LauncherSelfUpdateManager,
    launcher_base_directory: &Path,
    current_executable_path: &Path,
    public_arguments: &[String],
) -> Result<bool, UpdateError> {
    let base_directory = fsutil::full_path(launcher_base_directory);
    let executable = fsutil::full_path(current_executable_path);
    let Some(_lease) = manager.barrier().try_acquire_exclusive()? else {
        return Ok(false);
    };
    let Some(plan) = manager
        .load_pending()?
        .filter(|plan| plan.state == SelfUpdatePlanState::Staged)
    else {
        return Ok(false);
    };
    validate_canonical_startup(&plan, &base_directory, &executable)?;
    start_staged_helper(manager, &plan, &base_directory, public_arguments)?;
    Ok(true)
}

/// Digits only, a positive 32-bit process id.
fn parse_pid(text: &str) -> Option<u32> {
    if text.is_empty() || !text.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    text.parse::<i32>()
        .ok()
        .filter(|&pid| pid > 0)
        .and_then(|pid| u32::try_from(pid).ok())
}

fn start_staged_helper(
    manager: &LauncherSelfUpdateManager,
    plan: &SelfUpdatePlan,
    base_directory: &Path,
    public_arguments: &[String],
) -> Result<(), UpdateError> {
    let helper_path = manager.staged_launcher_path(plan)?;
    let working_directory = helper_path
        .parent()
        .ok_or_else(|| UpdateError::failed("The staged launcher has no directory."))?;
    Command::new(&helper_path)
        .arg(HELPER_ARGUMENT)
        .arg(std::process::id().to_string())
        .arg(base_directory)
        .arg(&plan.transaction_id)
        .args(public_arguments)
        .current_dir(working_directory)
        .spawn()
        .map(drop)
        .map_err(|error| {
            UpdateError::io(
                format!("The launcher self-update helper could not be started: {error}"),
                error,
            )
        })
}

fn layout_of(plan: &SelfUpdatePlan) -> Result<LauncherInstallationLayout, UpdateError> {
    match plan.installation_kind {
        super::self_update::LauncherInstallationKind::MacBundle => {
            LauncherInstallationLayout::mac_bundle(&plan.target_directory, &plan.rid)
        }
        super::self_update::LauncherInstallationKind::Flat => {
            LauncherInstallationLayout::flat(&plan.target_directory, &plan.rid)
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn run_helper(
    manager: &LauncherSelfUpdateManager,
    helper_base_directory: &Path,
    current_executable_path: &Path,
    parent_pid: u32,
    target_directory: &Path,
    transaction_id: &str,
    public_arguments: &[String],
    cancel: &CancelToken,
) -> Result<i32, UpdateError> {
    let plan = manager
        .load_pending()?
        .ok_or_else(|| UpdateError::failed("The helper found no pending self-update."))?;
    if plan.state != SelfUpdatePlanState::Staged || plan.transaction_id != transaction_id {
        return Err(UpdateError::failed(
            "The helper mode does not match a staged self-update transaction.",
        ));
    }
    if !fsutil::paths_equal(&plan.target_directory, target_directory) {
        return Err(UpdateError::failed(
            "The helper target does not match the pending self-update.",
        ));
    }
    let expected_helper_path = manager.staged_launcher_path(&plan)?;
    let expected_helper_directory = expected_helper_path
        .parent()
        .ok_or_else(|| UpdateError::failed("The staged launcher has no directory."))?;
    if !fsutil::paths_equal(helper_base_directory, expected_helper_directory)
        || !fsutil::paths_equal(current_executable_path, &expected_helper_path)
    {
        return Err(UpdateError::failed(
            "Self-update helper mode is trusted only from the staged launcher payload.",
        ));
    }

    let launcher_path = layout_of(&plan)?.launcher_path()?;
    let launcher_directory = launcher_path
        .parent()
        .map(Path::to_path_buf)
        .ok_or_else(|| UpdateError::failed("The installed launcher has no directory."))?;

    wait_for_parent_exit(parent_pid)?;
    let Some(lease) = manager.barrier().try_acquire_exclusive()? else {
        return Ok(UPDATE_LEASE_BUSY_EXIT_CODE);
    };

    let restored = {
        let plan = manager.load_pending()?.ok_or_else(|| {
            UpdateError::failed(
                "The helper found no pending self-update after acquiring the lease.",
            )
        })?;
        if plan.state != SelfUpdatePlanState::Staged
            || plan.transaction_id != transaction_id
            || !fsutil::paths_equal(&plan.target_directory, target_directory)
        {
            return Err(UpdateError::failed(
                "The pending self-update changed before the helper acquired its lease.",
            ));
        }
        manager.cleanup_owned_residue_under_lease(Some(&plan), target_directory, &lease)?;

        let mut replacement: Option<std::process::Child> = None;
        let applied = (|| -> Result<(), UpdateError> {
            manager.apply_pending(target_directory, cancel)?;
            let child = Command::new(&launcher_path)
                .arg(super::self_update_bootstrap::CONFIRM_ARGUMENT)
                .arg(transaction_id)
                .args(public_arguments)
                .current_dir(&launcher_directory)
                .spawn()
                .map_err(|error| {
                    UpdateError::io(
                        format!("The updated launcher could not be started: {error}"),
                        error,
                    )
                })?;
            let replacement = replacement.insert(child);
            let deadline = Instant::now() + CONFIRMATION_TIMEOUT;
            while !manager.is_confirmed(transaction_id) {
                cancel.check()?;
                let exited = replacement.try_wait().ok().flatten();
                if exited.is_some() || Instant::now() >= deadline {
                    return Err(UpdateError::failed(match exited {
                        Some(status) => format!(
                            "The updated launcher exited with code {} before confirming startup.",
                            status.code().unwrap_or(-1)
                        ),
                        None => "The updated launcher did not confirm startup in time.".to_owned(),
                    }));
                }
                std::thread::sleep(Duration::from_millis(100));
            }
            manager.complete_confirmed(transaction_id, target_directory)
        })();
        if applied.is_ok() {
            return Ok(0);
        }

        if let Some(child) = replacement.as_mut()
            && child.try_wait().ok().flatten().is_none()
        {
            let _ = child.kill();
            let _ = child.wait();
        }
        let none = CancelToken::new();
        let receipt = (|| -> Result<Option<SelfUpdatePlan>, UpdateError> {
            let pending = manager.load_pending()?;
            Ok(match pending.as_ref().map(|plan| plan.state) {
                Some(SelfUpdatePlanState::Applying) => {
                    Some(manager.recover_applying(target_directory, &none)?)
                }
                Some(SelfUpdatePlanState::AwaitingConfirmation) => {
                    Some(manager.rollback_awaiting_confirmation(target_directory, &none)?)
                }
                Some(SelfUpdatePlanState::RolledBack) => pending,
                _ => None,
            })
        })();
        match receipt {
            Ok(Some(receipt)) if receipt.state == SelfUpdatePlanState::RolledBack => {}
            // An ambiguous state must not start either executable.
            _ => return Ok(AMBIGUOUS_EXIT_CODE),
        }
        if manager
            .verify_restored_prior(target_directory, &none)
            .is_err()
        {
            return Ok(AMBIGUOUS_EXIT_CODE);
        }
        let mut restored = Command::new(&launcher_path);
        restored
            .args(public_arguments)
            .current_dir(&launcher_directory);
        restored
    };
    drop(lease);
    let mut restored = restored;
    if restored.spawn().is_err() {
        return Ok(AMBIGUOUS_EXIT_CODE);
    }
    Ok(ROLLED_BACK_EXIT_CODE)
}

fn validate_canonical_startup(
    plan: &SelfUpdatePlan,
    base_directory: &Path,
    executable: &Path,
) -> Result<(), UpdateError> {
    if !fsutil::paths_equal(&plan.target_directory, base_directory) {
        return Err(UpdateError::failed(
            "The pending self-update targets a different launcher directory.",
        ));
    }
    let expected_executable = layout_of(plan)?.launcher_path()?;
    if !fsutil::paths_equal(executable, &expected_executable) {
        return Err(UpdateError::failed(
            "Self-update can run only from the published OpenAC launcher executable.",
        ));
    }
    Ok(())
}

fn wait_for_parent_exit(parent_pid: u32) -> Result<(), UpdateError> {
    if parent_pid == std::process::id() {
        return Err(UpdateError::failed(
            "The self-update helper cannot wait on itself.",
        ));
    }
    platform::wait_for_process_exit(parent_pid);
    Ok(())
}
