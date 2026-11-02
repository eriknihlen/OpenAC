//! The orchestrator: owns the profile store, the install record and every
//! session or character refresh; answers what may be started; starts,
//! watches and stops sessions; and folds what they report back into the
//! profiles.
//!
//! One lock guards all of it. Nothing slow (composing, starting or stopping
//! a process, reading a status file) runs while it is held, and listeners
//! are told of changes after it is released.

use std::io::Read;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError, Weak};
use std::time::{Duration, SystemTime};

use ac_host::PathSet;

use super::capabilities::{LauncherCapability, LauncherPlatformCapabilities};
use super::executables::LauncherExecutableSet;
use super::exit_reason::{ClientExit, describe_client_exit};
use super::snapshot::{
    LauncherAccountSnapshot, LauncherActivityKind, LauncherActivityState,
    LauncherCharacterSnapshot, LauncherServerSnapshot, LauncherSessionSnapshot,
    LauncherSessionStop, LauncherSessionWindow, LauncherStateSnapshot,
};
use crate::cancel::{CancelToken, Cancelled, LinkedToken};
use crate::installation::LauncherInstallRecord;
use crate::launching::{
    LauncherProcessSupervisorFactory, LauncherSessionConfigService, LauncherSessionState,
    ProcessSupervisor, ProcessSupervisorFactory, SessionConfigService, StopOutcome,
};
use crate::plugins::PluginCatalog;
use crate::profiles::{
    AccountProfile, CharacterProfile, CharacterRosterEntry, LaunchMode, LauncherProfileStore,
    LauncherTextEditorKind, ProfileError, ServerProfile, apply_text, describe_account_removals,
    read_text, same_account,
};
use crate::status::{
    StatusEvent, StatusEventKind, StatusEventSource, StatusEventSourceFactory,
    StatusFileTailerFactory,
};
use crate::updates::{SessionLease, UpdateSessionBarrier};

/// How long the server may keep holding an account after a session ended
/// without logging out cleanly. A login during it is usually refused, but
/// the attempt itself makes the server drop the stale login, so the next
/// one gets in: the launcher waits this long, and a refusal starts the
/// wait again.
pub const SERVER_SESSION_HOLD: Duration = Duration::from_secs(30);

/// Why nothing may launch before first-run setup.
pub const FIRST_RUN_REQUIRED: &str =
    "Client content is not configured. Complete the first-run setup before launching.";

const INSTALL_VERIFIED: &str = "Client content SHA-256, size, and bake-tool version verified.";
const INSTALL_CONFIGURED: &str = "Client content paths are configured.";
const ALREADY_RUNNING: &str = "A session or character refresh is already running for this account.";
/// How long a client in the world may go without reporting progress (it
/// writes a heartbeat every 10 s) before it shows as not responding.
pub const NOT_RESPONDING_AFTER: Duration = Duration::from_secs(30);
const NOT_RESPONDING: &str = "Not responding.";
/// A Stop that had to kill: a stop, not a crash.
const FORCED_STOP: &str = "Stopped (did not respond, closed by force).";
const STOP_TIMEOUT_ON_DISPOSE: Duration = Duration::from_secs(5);

/// How long a running session's client has to report a switched window
/// before the switch is given up (opening one loads the GPU and the world
/// around the player).
pub const WINDOW_SWITCH_TIMEOUT: Duration = Duration::from_secs(30);
const WINDOW_SWITCH_NOT_RUNNING: &str =
    "Only a running play session can switch between graphical and headless.";
const WINDOW_SWITCH_BUSY: &str = "This session is already switching; wait for it to finish.";
const WINDOW_SWITCH_NOT_SENT: &str =
    "Could not switch the session: its client takes no console input.";
const WINDOW_SWITCH_UNANSWERED: &str =
    "The client did not report the window switch in time; the session keeps its current mode.";

/// Makes session ids.
pub type SessionIdFactory = Arc<dyn Fn() -> String + Send + Sync>;
/// The current time.
pub type Clock = Arc<dyn Fn() -> SystemTime + Send + Sync>;
/// Told after launcher state changed.
pub type StateChangedListener = Arc<dyn Fn() + Send + Sync>;
/// Launch availability by (server, account).
pub type AccountCapabilities = std::collections::HashMap<(String, String), LauncherCapability>;

/// Why an orchestrator operation failed.
#[derive(Debug, thiserror::Error)]
pub enum OrchestratorError {
    /// The operation is not allowed now; the text says why.
    #[error("{0}")]
    Operation(String),
    /// A profile edit or lookup failed.
    #[error(transparent)]
    Profile(#[from] ProfileError),
    /// The caller cancelled.
    #[error("The operation was cancelled.")]
    Cancelled,
    /// The orchestrator was disposed.
    #[error("The launcher orchestrator was disposed.")]
    Disposed,
    /// A required argument was blank.
    #[error("The value of '{0}' cannot be empty or white space.")]
    BlankArgument(&'static str),
}

impl From<Cancelled> for OrchestratorError {
    fn from(_: Cancelled) -> Self {
        Self::Cancelled
    }
}

/// The replaceable parts; anything left `None` gets the real one.
#[derive(Default)]
pub struct OrchestratorParts {
    /// What this platform may launch (default: detected).
    pub platform: Option<LauncherPlatformCapabilities>,
    /// Composes and writes session documents.
    pub config_service: Option<Arc<dyn SessionConfigService>>,
    /// Creates process supervisors.
    pub supervisor_factory: Option<Arc<dyn ProcessSupervisorFactory>>,
    /// Opens status files.
    pub status_source_factory: Option<Arc<dyn StatusEventSourceFactory>>,
    /// Makes session ids (default: [`create_session_id`]).
    pub session_id_factory: Option<SessionIdFactory>,
    /// The first installation status line.
    pub installation_status: Option<String>,
    /// The update barrier (default: the data directory's).
    pub update_session_barrier: Option<UpdateSessionBarrier>,
    /// The clock (default: the system clock).
    pub clock: Option<Clock>,
}

impl std::fmt::Debug for OrchestratorParts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OrchestratorParts")
            .field("platform", &self.platform)
            .field("installation_status", &self.installation_status)
            .field("update_session_barrier", &self.update_session_barrier)
            .finish_non_exhaustive()
    }
}

/// The launcher's state owner. Every method may be called from any thread;
/// `launch`, `probe` and `stop_session` block until their process work is
/// done, so a window calls them from a worker.
pub struct LauncherOrchestrator {
    inner: Arc<Inner>,
}

impl std::fmt::Debug for LauncherOrchestrator {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LauncherOrchestrator")
            .field("paths", &self.inner.paths)
            .field("platform", &self.inner.platform)
            .finish_non_exhaustive()
    }
}

struct Inner {
    gate: Mutex<State>,
    paths: PathSet,
    executables: LauncherExecutableSet,
    platform: LauncherPlatformCapabilities,
    config_service: Arc<dyn SessionConfigService>,
    supervisor_factory: Arc<dyn ProcessSupervisorFactory>,
    status_source_factory: Arc<dyn StatusEventSourceFactory>,
    session_id_factory: SessionIdFactory,
    barrier: UpdateSessionBarrier,
    clock: Clock,
    listeners: Mutex<Vec<StateChangedListener>>,
    next_serial: AtomicU64,
}

struct State {
    store: LauncherProfileStore,
    activities: Vec<Activity>,
    install_record: Option<LauncherInstallRecord>,
    installation_status: String,
    plugin_catalog: Option<Arc<PluginCatalog>>,
    disposed: bool,
}

type SharedStatusSource = Arc<Mutex<Box<dyn StatusEventSource>>>;

/// Set once, waited on by whoever disposes the activity while its start
/// is still running.
#[derive(Default)]
struct Completion {
    done: Mutex<bool>,
    signal: Condvar,
}

impl Completion {
    fn set(&self) {
        *lock(&self.done) = true;
        self.signal.notify_all();
    }

    fn wait(&self) {
        let mut done = lock(&self.done);
        while !*done {
            done = self
                .signal
                .wait(done)
                .unwrap_or_else(PoisonError::into_inner);
        }
    }
}

/// One session or refresh. Identified internally by a serial number, so a
/// late callback for a removed activity never reaches a newer one that
/// reuses its session id.
struct Activity {
    serial: u64,
    session_id: String,
    kind: LauncherActivityKind,
    server_name: String,
    account_name: String,
    character_name: Option<String>,
    launch_mode: Option<LaunchMode>,
    created_at: SystemTime,
    state: LauncherActivityState,
    status: String,
    exit_code: Option<i32>,
    error: Option<String>,
    plugin_notice: Option<String>,
    stderr_log_path: Option<PathBuf>,
    host_terminal_status: Option<String>,
    /// The client's own exit reason; set only when it reported its exit.
    exit_reason: Option<String>,
    /// The server's own words, when a refusal from it ended the session.
    server_message: Option<String>,
    /// That refusal only said the account was still logged on.
    account_in_use: bool,
    /// The player stopped it, and how that went.
    stop: Option<LauncherSessionStop>,
    /// When its last status line was read (launcher clock).
    last_progress_at: Option<SystemTime>,
    /// Its client writes heartbeats; only such a client is judged
    /// unresponsive by its silence.
    heartbeats_seen: bool,
    /// In the world and silent for [`NOT_RESPONDING_AFTER`].
    not_responding: bool,
    /// `error` is the exit's reason, worded from what was known then; a
    /// later exit report words it again.
    error_is_exit_reason: bool,
    /// The client reported its own exit, rather than the launcher only
    /// seeing the process end.
    host_reported_exit: bool,
    /// The process was seen to end. Status files are read until both this
    /// and the client's own exit report arrived; either can come first.
    process_exited: bool,
    terminal_at: Option<SystemTime>,
    supervisor: Option<Arc<dyn ProcessSupervisor>>,
    status_source: Option<SharedStatusSource>,
    start_cancellation: Option<LinkedToken>,
    update_session_lease: Option<SessionLease>,
    start_completed: Arc<Completion>,
    /// The start (configuration, process creation) still runs.
    startup_in_flight: bool,
    /// The game window as the client reports it, and a pending switch.
    window: LauncherSessionWindow,
    /// When the pending switch was sent.
    window_switch_sent_at: Option<SystemTime>,
}

impl Activity {
    /// Owns the account: not ended, or its start still runs, or its process
    /// still lives (an early exit report does not free the account).
    fn is_active(&self) -> bool {
        !self.is_terminal() || self.startup_in_flight || self.has_live_process()
    }

    /// Ended, failed or cancelled.
    fn is_terminal(&self) -> bool {
        !self.state.is_active()
    }

    fn has_live_process(&self) -> bool {
        self.supervisor
            .as_ref()
            .is_some_and(|supervisor| supervisor.has_live_process())
    }

    fn exited_gracefully(&self) -> bool {
        self.host_reported_exit && self.exit_code == Some(0)
    }

    fn set_state(&mut self, state: LauncherActivityState, now: SystemTime) {
        self.state = state;
        if self.is_terminal() && self.terminal_at.is_none() {
            self.terminal_at = Some(now);
        }
    }

    fn snapshot(&self) -> LauncherSessionSnapshot {
        LauncherSessionSnapshot {
            session_id: self.session_id.clone(),
            kind: self.kind,
            server_name: self.server_name.clone(),
            account_name: self.account_name.clone(),
            character_name: self.character_name.clone(),
            launch_mode: self.launch_mode,
            state: self.state,
            status: self.status.clone(),
            exit_code: self.exit_code,
            error: self.error.clone(),
            created_at: self.created_at,
            exit_reason: self.exit_reason.clone(),
            exited_gracefully: self.exited_gracefully(),
            server_message: self.server_message.clone(),
            account_in_use: self.account_in_use,
            stop: self.stop,
            not_responding: self.not_responding && self.state == LauncherActivityState::InWorld,
            plugin_notice: self.plugin_notice.clone(),
            has_live_process: Some(self.has_live_process()),
            startup_in_flight: self.startup_in_flight,
            window: self.window.clone(),
        }
    }
}

/// What a start needs, copied out under the lock. The account carries no
/// password and no characters; the password travels separately and is
/// dropped as soon as the process has it.
struct StartRequest {
    serial: u64,
    session_id: String,
    launch_mode: Option<LaunchMode>,
    server: ServerProfile,
    account: AccountProfile,
    character: Option<CharacterProfile>,
    install: LauncherInstallRecord,
    password: Option<String>,
    cancellation: LinkedToken,
    plugin_catalog: Option<Arc<PluginCatalog>>,
    completed: Arc<Completion>,
}

impl StartRequest {
    fn is_probe(&self) -> bool {
        self.character.is_none()
    }
}

/// How a start ended early.
enum StartFailure {
    Cancelled,
    Error(String),
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

fn require_text(value: &str, name: &'static str) -> Result<(), OrchestratorError> {
    if value.trim().is_empty() {
        Err(OrchestratorError::BlankArgument(name))
    } else {
        Ok(())
    }
}

/// A new session id: the UTC time to the millisecond, a dash, and 32
/// random hex digits.
pub fn create_session_id() -> String {
    let stamp: String = crate::time::format_utc(SystemTime::now())
        .chars()
        .filter(char::is_ascii_digit)
        .take(17)
        .collect();
    format!("{stamp}-{}", crate::ids::unique_hex32())
}

/// `prefix: detail` with `secret` replaced by `[redacted]`, or `prefix.`
/// when there is no detail.
fn safe_error(prefix: &str, detail: &str, secret: Option<&str>) -> String {
    let detail = match secret {
        Some(secret) if !secret.is_empty() => detail.replace(secret, "[redacted]"),
        _ => detail.to_owned(),
    };
    if detail.trim().is_empty() {
        format!("{prefix}.")
    } else {
        format!("{prefix}: {detail}")
    }
}

/// The error a failed client shows: a specific one when the client said its
/// prepared content is from another version, otherwise why it ended in
/// plain words (see [`describe_client_exit`]).
fn read_exit_failure(activity: &Activity) -> String {
    if let Some(path) = &activity.stderr_log_path
        && let Ok(file) = std::fs::File::open(path)
    {
        let mut buffer = Vec::with_capacity(4096);
        if file.take(4096).read_to_end(&mut buffer).is_ok() {
            let text = String::from_utf8_lossy(&buffer);
            if text.contains("bake tool") && text.contains("does not match") {
                return "The installed client and prepared game files are different versions. \
                        Update the client to match your game files before trying again."
                    .to_owned();
            }
        }
    }
    describe_client_exit(ClientExit {
        code: activity.exit_code,
        reported_reason: activity
            .exit_reason
            .as_deref()
            .filter(|_| activity.host_reported_exit),
        server_message: activity.server_message.as_deref(),
    })
}

/// Words the activity's exit failure again from what is known now: a
/// failed exit gets its reason unless an error of another kind is already
/// shown; a graceful one keeps its error as it is.
fn word_exit_failure(activity: &mut Activity) {
    // A process the player's Stop ended without its own report (killed
    // after the timeout) did not fail.
    let stopped = activity.stop.is_some() && !activity.host_reported_exit;
    let failed = activity.exit_code.is_some_and(|code| code != 0) && !stopped;
    if failed && (activity.error.is_none() || activity.error_is_exit_reason) {
        activity.error = Some(read_exit_failure(activity));
        activity.error_is_exit_reason = true;
    }
}

fn find_server<'a>(
    store: &'a LauncherProfileStore,
    server_name: &str,
) -> Result<&'a ServerProfile, OrchestratorError> {
    require_text(server_name, "serverName")?;
    store
        .document()
        .servers
        .iter()
        .find(|server| server.name == server_name)
        .ok_or_else(|| ProfileError::Invalid(format!("No server named '{server_name}'.")).into())
}

fn find_account<'a>(
    store: &'a LauncherProfileStore,
    server_name: &str,
    account_name: &str,
) -> Result<&'a AccountProfile, OrchestratorError> {
    require_text(account_name, "accountName")?;
    let accounts = &find_server(store, server_name)?.accounts;
    crate::profiles::find_account(accounts, |account| &account.account, account_name)
        .map(|index| &accounts[index])
        .ok_or_else(|| {
            ProfileError::Invalid(format!(
                "No account '{account_name}' on server '{server_name}'."
            ))
            .into()
        })
}

fn find_character<'a>(
    store: &'a LauncherProfileStore,
    server_name: &str,
    account_name: &str,
    character_name: &str,
) -> Result<&'a CharacterProfile, OrchestratorError> {
    require_text(character_name, "characterName")?;
    find_account(store, server_name, account_name)?
        .characters
        .iter()
        .find(|character| character.name == character_name)
        .ok_or_else(|| {
            ProfileError::Invalid(format!(
                "No character '{character_name}' on account '{account_name}'."
            ))
            .into()
        })
}

fn find_active<'a>(
    activities: &'a [Activity],
    server_name: &str,
    account_name: &str,
) -> Option<&'a Activity> {
    activities.iter().rev().find(|activity| {
        activity.is_active()
            && activity.server_name == server_name
            && same_account(&activity.account_name, account_name)
    })
}

fn ensure_server_idle(activities: &[Activity], server_name: &str) -> Result<(), OrchestratorError> {
    if activities
        .iter()
        .any(|activity| activity.is_active() && activity.server_name == server_name)
    {
        return Err(OrchestratorError::Operation(
            "Stop this server's running launcher sessions before editing or removing it.".into(),
        ));
    }
    Ok(())
}

fn ensure_account_idle(
    activities: &[Activity],
    server_name: &str,
    account_name: &str,
) -> Result<(), OrchestratorError> {
    if find_active(activities, server_name, account_name).is_some() {
        return Err(OrchestratorError::Operation(
            "Stop this account's running launcher session before editing or removing it.".into(),
        ));
    }
    Ok(())
}

fn ensure_character_idle(
    activities: &[Activity],
    server_name: &str,
    account_name: &str,
    character_name: &str,
) -> Result<(), OrchestratorError> {
    if activities.iter().any(|activity| {
        activity.is_active()
            && activity.kind == LauncherActivityKind::Play
            && activity.server_name == server_name
            && same_account(&activity.account_name, account_name)
            && activity.character_name.as_deref() == Some(character_name)
    }) {
        return Err(OrchestratorError::Operation(
            "Stop this character's running session before editing or removing it.".into(),
        ));
    }
    Ok(())
}

fn clone_server(source: &ServerProfile) -> ServerProfile {
    ServerProfile::new(source.name.clone(), source.host.clone(), source.port)
}

fn clone_account_without_characters(source: &AccountProfile) -> AccountProfile {
    let mut account = AccountProfile::new(source.account.clone(), String::new());
    account.plugins.clone_from(&source.plugins);
    account.login_commands.clone_from(&source.login_commands);
    account
}

fn clone_character(source: &CharacterProfile, mode: LaunchMode) -> CharacterProfile {
    CharacterProfile {
        name: source.name.clone(),
        id: source.id.clone(),
        launch_mode: mode,
        plugins: source.plugins.clone(),
    }
}

impl LauncherOrchestrator {
    /// An orchestrator over `store` (not yet loaded; call
    /// [`load_profiles`](Self::load_profiles)) with the real parts.
    pub fn new(
        store: LauncherProfileStore,
        paths: PathSet,
        executables: LauncherExecutableSet,
        install_record: Option<LauncherInstallRecord>,
    ) -> Self {
        Self::with_parts(
            store,
            paths,
            executables,
            install_record,
            OrchestratorParts::default(),
        )
    }

    /// An orchestrator with some parts replaced.
    pub fn with_parts(
        store: LauncherProfileStore,
        paths: PathSet,
        executables: LauncherExecutableSet,
        install_record: Option<LauncherInstallRecord>,
        parts: OrchestratorParts,
    ) -> Self {
        let installation_status = parts.installation_status.unwrap_or_else(|| {
            if install_record.is_some() {
                INSTALL_CONFIGURED.to_owned()
            } else {
                FIRST_RUN_REQUIRED.to_owned()
            }
        });
        let barrier = parts
            .update_session_barrier
            .unwrap_or_else(|| UpdateSessionBarrier::new(&paths.data_directory));
        let inner = Inner {
            gate: Mutex::new(State {
                store,
                activities: Vec::new(),
                install_record,
                installation_status,
                plugin_catalog: None,
                disposed: false,
            }),
            paths,
            executables,
            platform: parts
                .platform
                .unwrap_or_else(LauncherPlatformCapabilities::detect),
            config_service: parts
                .config_service
                .unwrap_or_else(|| Arc::new(LauncherSessionConfigService)),
            supervisor_factory: parts
                .supervisor_factory
                .unwrap_or_else(|| Arc::new(LauncherProcessSupervisorFactory::default())),
            status_source_factory: parts
                .status_source_factory
                .unwrap_or_else(|| Arc::new(StatusFileTailerFactory)),
            session_id_factory: parts
                .session_id_factory
                .unwrap_or_else(|| Arc::new(create_session_id)),
            barrier,
            clock: parts.clock.unwrap_or_else(|| Arc::new(SystemTime::now)),
            listeners: Mutex::new(Vec::new()),
            next_serial: AtomicU64::new(1),
        };
        Self {
            inner: Arc::new(inner),
        }
    }

    /// Adds a listener told after every state change.
    pub fn subscribe(&self, listener: StateChangedListener) {
        lock(&self.inner.listeners).push(listener);
    }

    /// Loads the profile file (migrating an older launcher's once).
    pub fn load_profiles(&self) -> Result<(), OrchestratorError> {
        {
            let mut state = self.inner.state()?;
            state.store.load()?;
        }
        self.inner.raise_state_changed();
        Ok(())
    }

    /// Everything the window shows.
    pub fn snapshot(&self) -> Result<LauncherStateSnapshot, OrchestratorError> {
        let state = self.inner.state()?;
        let servers = state
            .store
            .document()
            .servers
            .iter()
            .map(|server| server_snapshot(&state.activities, server))
            .collect();
        let mut sessions: Vec<&Activity> = state.activities.iter().collect();
        sessions.sort_by_key(|activity| std::cmp::Reverse(activity.created_at));
        Ok(LauncherStateSnapshot {
            servers,
            sessions: sessions.into_iter().map(Activity::snapshot).collect(),
            platform: self.inner.platform.clone(),
            is_installation_ready: state.install_record.is_some(),
            installation_status: state.installation_status.clone(),
            show_beta_plugins: state.store.document().show_beta_plugins,
        })
    }

    /// Whether `mode` may launch at all: platform, executable, first-run
    /// setup.
    pub fn launch_capability(
        &self,
        mode: LaunchMode,
    ) -> Result<LauncherCapability, OrchestratorError> {
        let platform = self.inner.platform.for_launch_mode(mode);
        if !platform.is_available {
            return Ok(platform);
        }
        let executable = self.inner.executables.availability(mode);
        if !executable.is_available {
            return Ok(executable);
        }
        let state = self.inner.state()?;
        Ok(if state.install_record.is_none() {
            LauncherCapability::unavailable(FIRST_RUN_REQUIRED)
        } else {
            LauncherCapability::AVAILABLE
        })
    }

    /// Whether `mode` may launch for this account now: as
    /// [`launch_capability`](Self::launch_capability), with no activity
    /// running for it and no unclean exit the server may still hold.
    pub fn account_launch_capability(
        &self,
        server_name: &str,
        account_name: &str,
        mode: LaunchMode,
    ) -> Result<LauncherCapability, OrchestratorError> {
        require_text(server_name, "serverName")?;
        require_text(account_name, "accountName")?;
        let capability = self.launch_capability(mode)?;
        if !capability.is_available {
            return Ok(capability);
        }
        let state = self.inner.state()?;
        find_account(&state.store, server_name, account_name)?;
        if let Some(active) = find_active(&state.activities, server_name, account_name) {
            return Ok(LauncherCapability::unavailable(format!(
                "Stop the running {} for this account before starting another activity.",
                active.kind.lower_name()
            )));
        }
        Ok(self
            .inner
            .reconnect_hold(&state.activities, server_name, account_name))
    }

    /// [`account_launch_capability`](Self::account_launch_capability) for
    /// every configured account at once, for one window refresh: the
    /// executable is checked once, the activities and reconnect holds per
    /// account. Launches check again.
    pub fn account_launch_capabilities(
        &self,
        mode: LaunchMode,
    ) -> Result<AccountCapabilities, OrchestratorError> {
        let host = self.launch_capability(mode)?;
        let state = self.inner.state()?;
        let mut result = AccountCapabilities::new();
        for server in &state.store.document().servers {
            for account in &server.accounts {
                let capability = if !host.is_available {
                    host.clone()
                } else if let Some(active) =
                    find_active(&state.activities, &server.name, &account.account)
                {
                    LauncherCapability::unavailable(format!(
                        "Stop the running {} for this account before starting another activity.",
                        active.kind.lower_name()
                    ))
                } else {
                    self.inner
                        .reconnect_hold(&state.activities, &server.name, &account.account)
                };
                result.insert((server.name.clone(), account.account.clone()), capability);
            }
        }
        Ok(result)
    }

    /// Whether this account's characters may be refreshed now.
    pub fn probe_capability(
        &self,
        server_name: &str,
        account_name: &str,
    ) -> Result<LauncherCapability, OrchestratorError> {
        require_text(server_name, "serverName")?;
        require_text(account_name, "accountName")?;
        let platform = self.inner.platform.for_launch_mode(LaunchMode::Headless);
        if !platform.is_available {
            return Ok(platform);
        }
        let executable = self.inner.executables.availability(LaunchMode::Headless);
        if !executable.is_available {
            return Ok(executable);
        }
        let state = self.inner.state()?;
        find_account(&state.store, server_name, account_name)?;
        if state.install_record.is_none() {
            return Ok(LauncherCapability::unavailable(FIRST_RUN_REQUIRED));
        }
        Ok(
            match find_active(&state.activities, server_name, account_name) {
                None => LauncherCapability::AVAILABLE,
                Some(active) => LauncherCapability::unavailable(format!(
                    "Stop the running {} for this account before refreshing its characters.",
                    active.kind.lower_name()
                )),
            },
        )
    }

    /// Sets the install record, with the matching status line.
    pub fn set_install_record(
        &self,
        install_record: Option<LauncherInstallRecord>,
    ) -> Result<(), OrchestratorError> {
        let status = if install_record.is_some() {
            INSTALL_VERIFIED
        } else {
            FIRST_RUN_REQUIRED
        };
        self.set_installation_state(install_record, status)
    }

    /// Sets the install record and the status line shown with it.
    pub fn set_installation_state(
        &self,
        install_record: Option<LauncherInstallRecord>,
        installation_status: &str,
    ) -> Result<(), OrchestratorError> {
        require_text(installation_status, "installationStatus")?;
        {
            let mut state = self.inner.state()?;
            state.install_record = install_record;
            state.installation_status = installation_status.to_owned();
        }
        self.inner.raise_state_changed();
        Ok(())
    }

    /// The plugin list the next launched session filters blocked ids
    /// against.
    pub fn set_plugin_catalog(
        &self,
        catalog: Option<PluginCatalog>,
    ) -> Result<(), OrchestratorError> {
        self.inner.state()?.plugin_catalog = catalog.map(Arc::new);
        Ok(())
    }

    /// Whether beta-only plugins are offered (saved).
    pub fn set_show_beta_plugins(&self, value: bool) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, _| {
            store.set_show_beta_plugins(value);
            Ok(())
        })
    }

    /// The text editor's view of `kind`.
    pub fn read_profile_text(
        &self,
        kind: LauncherTextEditorKind,
    ) -> Result<String, OrchestratorError> {
        let state = self.inner.state()?;
        Ok(read_text(state.store.document(), kind))
    }

    /// Saves the text editor's `text` for `kind`, refused when the profiles
    /// changed since the editor read `original_text` or any server has a
    /// running session.
    pub fn save_profile_text(
        &self,
        kind: LauncherTextEditorKind,
        text: &str,
        original_text: &str,
    ) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, activities| {
            if read_text(store.document(), kind) != original_text {
                return Err(ProfileError::Invalid(
                    "Profiles changed while this editor was open. Close and reopen it before \
                     saving."
                        .into(),
                )
                .into());
            }
            // Logon commands take effect at the next login, so they may
            // change while sessions run; so may accounts, as long as no
            // running account is removed.
            if kind == LauncherTextEditorKind::Servers {
                for server in &store.document().servers {
                    ensure_server_idle(activities, &server.name)?;
                }
            }
            apply_text(store.document_mut(), kind, text)?;
            for activity in activities.iter().filter(|activity| activity.is_active()) {
                let still_there = store.document().servers.iter().any(|server| {
                    server.name == activity.server_name
                        && server
                            .accounts
                            .iter()
                            .any(|account| same_account(&account.account, &activity.account_name))
                });
                if !still_there {
                    return Err(OrchestratorError::Operation(format!(
                        "Stop {}'s session on {} before removing that account.",
                        activity.account_name, activity.server_name
                    )));
                }
            }
            Ok(())
        })
    }

    /// Adds a server.
    pub fn add_server(&self, name: &str, host: &str, port: i32) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, _| Ok(store.add_server(name, host, port)?))
    }

    /// Edits an idle server.
    pub fn edit_server(
        &self,
        name: &str,
        new_name: &str,
        new_host: &str,
        new_port: i32,
    ) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, activities| {
            ensure_server_idle(activities, name)?;
            Ok(store.edit_server(name, Some(new_name), Some(new_host), Some(new_port))?)
        })
    }

    /// Removes an idle server.
    pub fn remove_server(&self, name: &str) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, activities| {
            ensure_server_idle(activities, name)?;
            Ok(store.remove_server(name)?)
        })
    }

    /// Adds an account.
    pub fn add_account(
        &self,
        server_name: &str,
        account_name: &str,
        password: &str,
    ) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, _| {
            Ok(store.add_account(server_name, account_name, password)?)
        })
    }

    /// Renames an idle account or changes its password (`None` keeps it).
    pub fn edit_account(
        &self,
        server_name: &str,
        account_name: &str,
        new_account_name: &str,
        new_password: Option<&str>,
    ) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, activities| {
            ensure_account_idle(activities, server_name, account_name)?;
            Ok(store.edit_account(
                server_name,
                account_name,
                Some(new_account_name),
                new_password,
            )?)
        })
    }

    /// Removes an idle account.
    pub fn remove_account(
        &self,
        server_name: &str,
        account_name: &str,
    ) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, activities| {
            ensure_account_idle(activities, server_name, account_name)?;
            Ok(store.remove_account(server_name, account_name)?)
        })
    }

    /// Adds a character by hand (to the character screen, no plugins).
    pub fn add_character(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: &str,
        character_id: Option<&str>,
    ) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, _| {
            Ok(store.add_character(
                server_name,
                account_name,
                character_name,
                character_id,
                LaunchMode::GuiSelect,
                None,
            )?)
        })
    }

    /// Renames an idle character or changes its id.
    pub fn edit_character_identity(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: &str,
        new_character_name: &str,
        new_character_id: Option<&str>,
    ) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, activities| {
            ensure_character_idle(activities, server_name, account_name, character_name)?;
            Ok(store.edit_character(
                server_name,
                account_name,
                character_name,
                None,
                Some(new_character_name),
                new_character_id,
            )?)
        })
    }

    /// Changes a character's launch mode and its own plugin list (`None`:
    /// it uses its account's).
    pub fn update_character_settings(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: &str,
        launch_mode: LaunchMode,
        plugins: Option<&[String]>,
    ) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, _| {
            store.edit_character(
                server_name,
                account_name,
                character_name,
                Some(launch_mode),
                None,
                None,
            )?;
            Ok(store.set_character_plugins(server_name, account_name, character_name, plugins)?)
        })
    }

    /// The plugins every character on an account launches with unless it
    /// has its own list.
    pub fn update_account_plugins(
        &self,
        server_name: &str,
        account_name: &str,
        plugins: &[String],
    ) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, _| {
            Ok(store.set_account_plugins(server_name, account_name, plugins)?)
        })
    }

    /// The accounts saving `text` would remove that have something saved on
    /// them; only the accounts text removes any.
    pub fn describe_profile_text_removals(
        &self,
        kind: LauncherTextEditorKind,
        text: &str,
    ) -> Result<Vec<String>, OrchestratorError> {
        let state = self.inner.state()?;
        Ok(if kind == LauncherTextEditorKind::Accounts {
            describe_account_removals(state.store.document(), text)
        } else {
            Vec::new()
        })
    }

    /// What the last profile load could not carry over from an older file;
    /// taken once.
    pub fn take_profile_migration_notice(&self) -> Result<Option<String>, OrchestratorError> {
        Ok(self.inner.state()?.store.take_migration_notice())
    }

    /// Remembers the character and launch mode an account's row is set to.
    pub fn update_account_selection(
        &self,
        server_name: &str,
        account_name: &str,
        selected_character: Option<&str>,
        selected_launch_mode: LaunchMode,
    ) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, _| {
            Ok(store.edit_account_selection(
                server_name,
                account_name,
                selected_character,
                selected_launch_mode,
            )?)
        })
    }

    /// Removes an idle character.
    pub fn remove_character(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: &str,
    ) -> Result<(), OrchestratorError> {
        self.mutate_profiles(|store, activities| {
            ensure_character_idle(activities, server_name, account_name, character_name)?;
            Ok(store.remove_character(server_name, account_name, character_name)?)
        })
    }

    fn mutate_profiles(
        &self,
        mutation: impl FnOnce(&mut LauncherProfileStore, &[Activity]) -> Result<(), OrchestratorError>,
    ) -> Result<(), OrchestratorError> {
        {
            let mut guard = self.inner.state()?;
            let State {
                store, activities, ..
            } = &mut *guard;
            store.execute_transaction(|store| mutation(store, activities))?;
        }
        self.inner.raise_state_changed();
        Ok(())
    }

    /// Starts a play session and returns once its process is started.
    /// Without a character only [`LaunchMode::GuiSelect`] is allowed; the
    /// session then loads only the plugins every character on the account
    /// enabled.
    pub fn launch(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: Option<&str>,
        mode: LaunchMode,
        cancel: &CancelToken,
    ) -> Result<LauncherSessionSnapshot, OrchestratorError> {
        let capability = self.launch_capability(mode)?;
        if !capability.is_available {
            return Err(OrchestratorError::Operation(
                capability
                    .reason
                    .unwrap_or_else(|| "Launch is unavailable.".into()),
            ));
        }
        let character_name = character_name.filter(|name| !name.trim().is_empty());
        let request = {
            let mut guard = self.inner.state()?;
            let state = &mut *guard;
            if find_active(&state.activities, server_name, account_name).is_some() {
                return Err(OrchestratorError::Operation(ALREADY_RUNNING.into()));
            }
            // Every start obeys the hold: the window's, automatic relaunch's
            // and the control endpoint's.
            let hold = self
                .inner
                .reconnect_hold(&state.activities, server_name, account_name);
            if !hold.is_available {
                return Err(OrchestratorError::Operation(
                    hold.reason
                        .unwrap_or_else(|| "Launch is unavailable.".into()),
                ));
            }
            let server = find_server(&state.store, server_name)?;
            let account = find_account(&state.store, server_name, account_name)?;
            let character = match character_name {
                None => {
                    if mode != LaunchMode::GuiSelect {
                        return Err(OrchestratorError::Operation(
                            "Select a cached character for GUI or headless launch.".into(),
                        ));
                    }
                    // The character is picked inside the client, so the
                    // launch carries the account's plugins and commands,
                    // which every character on it shares.
                    CharacterProfile::new(String::new())
                }
                Some(name) => {
                    find_character(&state.store, server_name, account_name, name)?.clone()
                }
            };
            let install = state
                .install_record
                .clone()
                .ok_or_else(|| OrchestratorError::Operation(FIRST_RUN_REQUIRED.into()))?;
            let session_id = self.inner.reserve_session_id(&state.activities)?;
            let server_copy = clone_server(server);
            let account_copy = clone_account_without_characters(account);
            let password = account.password.clone();
            let mut activity = self.inner.new_activity(
                session_id,
                LauncherActivityKind::Play,
                server.name.clone(),
                account.account.clone(),
                character_name.map(|_| character.name.clone()),
                Some(mode),
                "Preparing session configuration…",
            );
            let cancellation = cancel.child();
            activity.start_cancellation = Some(cancellation.clone());
            let request = StartRequest {
                serial: activity.serial,
                session_id: activity.session_id.clone(),
                launch_mode: Some(mode),
                server: server_copy,
                account: account_copy,
                character: Some(clone_character(&character, mode)),
                install,
                password: Some(password),
                cancellation,
                plugin_catalog: state.plugin_catalog.clone(),
                completed: Arc::clone(&activity.start_completed),
            };
            state.activities.push(activity);
            request
        };
        self.inner.raise_state_changed();
        Inner::start_activity(&self.inner, request)
    }

    /// Starts a character refresh (log in, read the roster, log out) and
    /// returns once its process is started.
    pub fn probe(
        &self,
        server_name: &str,
        account_name: &str,
        cancel: &CancelToken,
    ) -> Result<LauncherSessionSnapshot, OrchestratorError> {
        let capability = self.probe_capability(server_name, account_name)?;
        if !capability.is_available {
            return Err(OrchestratorError::Operation(
                capability
                    .reason
                    .unwrap_or_else(|| "Character refresh is unavailable.".into()),
            ));
        }
        let request = {
            let mut guard = self.inner.state()?;
            let state = &mut *guard;
            if find_active(&state.activities, server_name, account_name).is_some() {
                return Err(OrchestratorError::Operation(ALREADY_RUNNING.into()));
            }
            let server = find_server(&state.store, server_name)?;
            let account = find_account(&state.store, server_name, account_name)?;
            let install = state
                .install_record
                .clone()
                .ok_or_else(|| OrchestratorError::Operation(FIRST_RUN_REQUIRED.into()))?;
            let session_id = self.inner.reserve_session_id(&state.activities)?;
            let mut activity = self.inner.new_activity(
                session_id,
                LauncherActivityKind::Probe,
                server.name.clone(),
                account.account.clone(),
                None,
                None,
                "Preparing character refresh…",
            );
            let cancellation = cancel.child();
            activity.start_cancellation = Some(cancellation.clone());
            let request = StartRequest {
                serial: activity.serial,
                session_id: activity.session_id.clone(),
                launch_mode: None,
                server: clone_server(server),
                account: clone_account_without_characters(account),
                character: None,
                install,
                password: Some(account.password.clone()),
                cancellation,
                plugin_catalog: None,
                completed: Arc::clone(&activity.start_completed),
            };
            state.activities.push(activity);
            request
        };
        self.inner.raise_state_changed();
        Inner::start_activity(&self.inner, request)
    }

    /// Sends one console line to a running windowless session; false when
    /// it is not running or takes no console input.
    pub fn try_send_console_line(&self, session_id: &str, line: &str) -> bool {
        let supervisor = {
            let state = lock(&self.inner.gate);
            if state.disposed {
                return false;
            }
            match state
                .activities
                .iter()
                .find(|activity| activity.session_id == session_id)
            {
                Some(activity) if activity.is_active() => activity.supervisor.clone(),
                _ => None,
            }
        };
        supervisor.is_some_and(|supervisor| supervisor.try_send_line(line))
    }

    /// Switches a running play session between graphical and headless
    /// without restarting it (an addition over OpenAC): `open` sends the
    /// client's console `/window <session id>`, otherwise `/close`. The
    /// session's window state follows what the client then reports in its
    /// status file; a switch it does not answer within
    /// [`WINDOW_SWITCH_TIMEOUT`] is given up. Nothing is sent when the window
    /// is already as asked.
    pub fn set_session_window(
        &self,
        session_id: &str,
        open: bool,
    ) -> Result<(), OrchestratorError> {
        require_text(session_id, "sessionId")?;
        let supervisor = {
            let mut state = self.inner.state()?;
            let activity = state
                .activities
                .iter_mut()
                .find(|activity| activity.session_id == session_id)
                .ok_or_else(|| {
                    OrchestratorError::Operation(format!("No launcher session '{session_id}'."))
                })?;
            let switchable = activity.kind == LauncherActivityKind::Play
                && activity.is_active()
                && !activity.is_terminal()
                && activity.state != LauncherActivityState::Stopping
                && activity.has_live_process();
            if !switchable {
                return Err(OrchestratorError::Operation(
                    WINDOW_SWITCH_NOT_RUNNING.into(),
                ));
            }
            if activity.window.switching_to.is_some() {
                return Err(OrchestratorError::Operation(WINDOW_SWITCH_BUSY.into()));
            }
            if activity.window.is_open == open {
                return Ok(());
            }
            activity.supervisor.clone()
        };
        let line = if open {
            format!("/window {session_id}")
        } else {
            "/close".to_owned()
        };
        let sent = supervisor.is_some_and(|supervisor| supervisor.try_send_line(&line));
        let now = (self.inner.clock)();
        {
            let mut state = self.inner.state()?;
            let Some(activity) = state
                .activities
                .iter_mut()
                .find(|activity| activity.session_id == session_id)
            else {
                return Err(OrchestratorError::Operation(
                    WINDOW_SWITCH_NOT_RUNNING.into(),
                ));
            };
            if sent {
                activity.window.switching_to = Some(open);
                activity.window.error = None;
                activity.window_switch_sent_at = Some(now);
            } else {
                activity.window.error = Some(WINDOW_SWITCH_NOT_SENT.into());
            }
        }
        self.inner.raise_state_changed();
        if sent {
            Ok(())
        } else {
            Err(OrchestratorError::Operation(WINDOW_SWITCH_NOT_SENT.into()))
        }
    }

    /// The file a session's error output is captured in.
    pub fn session_log_path(&self, session_id: &str) -> Option<PathBuf> {
        lock(&self.inner.gate)
            .activities
            .iter()
            .find(|activity| activity.session_id == session_id)
            .and_then(|activity| activity.stderr_log_path.clone())
    }

    /// Stops a session: cancels a start still running and stops the
    /// process, waiting up to `timeout` before killing it. Blocks until the
    /// supervisor is done.
    pub fn stop_session(
        &self,
        session_id: &str,
        timeout: Duration,
        cancel: &CancelToken,
    ) -> Result<(), OrchestratorError> {
        require_text(session_id, "sessionId")?;
        let (supervisor, start_cancellation) = {
            let mut state = self.inner.state()?;
            let now = (self.inner.clock)();
            let activity = state
                .activities
                .iter_mut()
                .find(|activity| activity.session_id == session_id)
                .ok_or_else(|| {
                    OrchestratorError::Operation(format!("No launcher session '{session_id}'."))
                })?;
            if !activity.is_active() {
                return Ok(());
            }
            activity.set_state(LauncherActivityState::Stopping, now);
            activity.status = "Stopping session…".into();
            activity.stop.get_or_insert(LauncherSessionStop::Requested);
            (
                activity.supervisor.clone(),
                activity.start_cancellation.clone(),
            )
        };
        self.inner.raise_state_changed();
        cancel.check()?;
        if let Some(token) = start_cancellation {
            token.cancel();
        }
        let Some(supervisor) = supervisor else {
            return Ok(());
        };
        let outcome = supervisor.stop(timeout);
        if let Ok(StopOutcome::Killed) = outcome {
            {
                let mut state = lock(&self.inner.gate);
                if let Some(activity) = state
                    .activities
                    .iter_mut()
                    .find(|activity| activity.session_id == session_id)
                {
                    activity.stop = Some(LauncherSessionStop::Forced);
                    if activity.error_is_exit_reason {
                        activity.error = None;
                        activity.error_is_exit_reason = false;
                    }
                    if activity.is_terminal() {
                        activity.status = FORCED_STOP.into();
                    }
                }
            }
            self.inner.raise_state_changed();
        }
        if let Err(error) = outcome {
            let message = safe_error("Could not stop the session", &error.to_string(), None);
            {
                let mut state = lock(&self.inner.gate);
                if let Some(activity) = state
                    .activities
                    .iter_mut()
                    .find(|activity| activity.session_id == session_id)
                {
                    activity.error = Some(message.clone());
                    activity.status = message.clone();
                }
            }
            self.inner.raise_state_changed();
            return Err(OrchestratorError::Operation(message));
        }
        Ok(())
    }

    /// Reads every session's new status lines and applies them.
    pub fn poll_status(&self) -> Result<(), OrchestratorError> {
        let sources: Vec<(u64, SharedStatusSource)> = self
            .inner
            .state()?
            .activities
            .iter()
            // Nothing more comes once the process ended and the client
            // reported its exit.
            .filter(|activity| !(activity.process_exited && activity.host_reported_exit))
            .filter_map(|activity| {
                activity
                    .status_source
                    .clone()
                    .map(|source| (activity.serial, source))
            })
            .collect();
        let mut changed = false;
        for (serial, source) in sources {
            let events = lock(&source).read_new_events();
            for event in events {
                self.inner.apply_status_event(serial, event);
                changed = true;
            }
        }
        changed |= self.inner.expire_window_switches();
        changed |= self.inner.judge_responsiveness();
        if changed {
            self.inner.raise_state_changed();
        }
        Ok(())
    }

    /// Forgets every ended session.
    pub fn clear_finished_sessions(&self) -> Result<(), OrchestratorError> {
        let removed: Vec<Activity> = {
            let mut state = self.inner.state()?;
            let (finished, kept): (Vec<Activity>, Vec<Activity>) =
                std::mem::take(&mut state.activities)
                    .into_iter()
                    .partition(|activity| !activity.is_active());
            state.activities = kept;
            finished
        };
        let any = !removed.is_empty();
        for activity in removed {
            dispose_activity(activity);
        }
        if any {
            self.inner.raise_state_changed();
        }
        Ok(())
    }

    /// Stops every session (each up to five seconds before a kill) and
    /// refuses further use. Blocks until every process is seen to end.
    /// Dropping the orchestrator does this too.
    pub fn dispose(&self) {
        let activities = {
            let mut state = lock(&self.inner.gate);
            if state.disposed {
                return;
            }
            state.disposed = true;
            std::mem::take(&mut state.activities)
        };
        for activity in activities {
            dispose_activity(activity);
        }
    }
}

impl Drop for LauncherOrchestrator {
    fn drop(&mut self) {
        self.dispose();
    }
}

/// Cancels a running start and waits for it, stops the process, then
/// releases the update lease: an update may start only once the process is
/// seen to end.
fn dispose_activity(mut activity: Activity) {
    if let Some(token) = activity.start_cancellation.take() {
        token.cancel();
    }
    activity.start_completed.wait();
    if let Some(supervisor) = activity.supervisor.take() {
        // A stop that fails here still ends in a kill attempt inside the
        // supervisor; there is no one left to report it to.
        let _ = supervisor.stop(STOP_TIMEOUT_ON_DISPOSE);
        supervisor.dispose();
    }
    drop(activity.update_session_lease.take());
}

fn server_snapshot(activities: &[Activity], server: &ServerProfile) -> LauncherServerSnapshot {
    LauncherServerSnapshot {
        name: server.name.clone(),
        host: server.host.clone(),
        port: server.port,
        accounts: server
            .accounts
            .iter()
            .map(|account| account_snapshot(activities, server, account))
            .collect(),
    }
}

fn account_snapshot(
    activities: &[Activity],
    server: &ServerProfile,
    account: &AccountProfile,
) -> LauncherAccountSnapshot {
    let active = find_active(activities, &server.name, &account.account);
    let characters = account
        .characters
        .iter()
        .map(|character| {
            let running = activities.iter().rev().find(|candidate| {
                candidate.is_active()
                    && candidate.kind == LauncherActivityKind::Play
                    && candidate.server_name == server.name
                    && same_account(&candidate.account_name, &account.account)
                    && candidate.character_name.as_deref() == Some(character.name.as_str())
            });
            LauncherCharacterSnapshot {
                server_name: server.name.clone(),
                account_name: account.account.clone(),
                name: character.name.clone(),
                id: character.id.clone(),
                launch_mode: character.launch_mode,
                plugins: account.plugins_for(Some(character)).to_vec(),
                login_commands: account.login_commands.clone(),
                has_running_session: running.is_some(),
                session_status: running.map_or_else(
                    || "Not running".to_owned(),
                    |activity| activity.status.clone(),
                ),
                has_own_plugins: character.plugins.is_some(),
            }
        })
        .collect();
    LauncherAccountSnapshot {
        server_name: server.name.clone(),
        account_name: account.account.clone(),
        characters,
        has_running_activity: active.is_some(),
        activity_status: active
            .map_or_else(|| "Idle".to_owned(), |activity| activity.status.clone()),
        selected_character: account.selected_character.clone(),
        selected_launch_mode: account.selected_launch_mode,
        plugins: account.plugins.clone(),
        profiles: account.profiles.clone(),
        login_commands: account.login_commands.clone(),
    }
}

impl Inner {
    fn state(&self) -> Result<MutexGuard<'_, State>, OrchestratorError> {
        let state = lock(&self.gate);
        if state.disposed {
            return Err(OrchestratorError::Disposed);
        }
        Ok(state)
    }

    fn raise_state_changed(&self) {
        let listeners = lock(&self.listeners).clone();
        for listener in listeners {
            listener();
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn new_activity(
        &self,
        session_id: String,
        kind: LauncherActivityKind,
        server_name: String,
        account_name: String,
        character_name: Option<String>,
        launch_mode: Option<LaunchMode>,
        status: &str,
    ) -> Activity {
        Activity {
            serial: self.next_serial.fetch_add(1, Ordering::Relaxed),
            session_id,
            kind,
            server_name,
            account_name,
            character_name,
            launch_mode,
            created_at: (self.clock)(),
            state: LauncherActivityState::Starting,
            status: status.to_owned(),
            exit_code: None,
            error: None,
            plugin_notice: None,
            stderr_log_path: None,
            host_terminal_status: None,
            server_message: None,
            account_in_use: false,
            error_is_exit_reason: false,
            stop: None,
            last_progress_at: None,
            heartbeats_seen: false,
            not_responding: false,
            exit_reason: None,
            host_reported_exit: false,
            process_exited: false,
            terminal_at: None,
            supervisor: None,
            status_source: None,
            start_cancellation: None,
            update_session_lease: None,
            start_completed: Arc::new(Completion::default()),
            startup_in_flight: true,
            // A graphical launch opens its window at once; the client says
            // so, or that it could not.
            window: LauncherSessionWindow {
                is_open: launch_mode.is_some_and(|mode| mode != LaunchMode::Headless),
                ..LauncherSessionWindow::default()
            },
            window_switch_sent_at: None,
        }
    }

    fn reserve_session_id(&self, activities: &[Activity]) -> Result<String, OrchestratorError> {
        let session_id = (self.session_id_factory)();
        require_text(&session_id, "sessionId")?;
        if activities
            .iter()
            .any(|activity| activity.session_id == session_id)
        {
            return Err(OrchestratorError::Operation(format!(
                "The launcher generated duplicate session id '{session_id}'."
            )));
        }
        Ok(session_id)
    }

    /// Unavailable while the last session for the account ended without a
    /// clean logout less than [`SERVER_SESSION_HOLD`] ago.
    fn reconnect_hold(
        &self,
        activities: &[Activity],
        server_name: &str,
        account_name: &str,
    ) -> LauncherCapability {
        let now = (self.clock)();
        let Some(terminal_at) = activities
            .iter()
            .filter(|activity| {
                activity.is_terminal()
                    && !activity.exited_gracefully()
                    && activity.server_name == server_name
                    && same_account(&activity.account_name, account_name)
            })
            .filter_map(|activity| activity.terminal_at)
            .max()
        else {
            return LauncherCapability::AVAILABLE;
        };
        let elapsed = now.duration_since(terminal_at).unwrap_or(Duration::ZERO);
        let Some(remaining) = SERVER_SESSION_HOLD.checked_sub(elapsed) else {
            return LauncherCapability::AVAILABLE;
        };
        if remaining.is_zero() {
            return LauncherCapability::AVAILABLE;
        }
        LauncherCapability::held(remaining)
    }

    fn with_activity<T>(&self, serial: u64, action: impl FnOnce(&mut Activity) -> T) -> Option<T> {
        let mut state = lock(&self.gate);
        state
            .activities
            .iter_mut()
            .find(|activity| activity.serial == serial)
            .map(action)
    }

    fn start_activity(
        inner: &Arc<Self>,
        mut request: StartRequest,
    ) -> Result<LauncherSessionSnapshot, OrchestratorError> {
        let result = Self::start_core(inner, &mut request);
        request.password = None;
        {
            let mut state = lock(&inner.gate);
            if let Some(activity) = state
                .activities
                .iter_mut()
                .find(|activity| activity.serial == request.serial)
            {
                activity.start_cancellation = None;
                activity.startup_in_flight = false;
            }
            request.completed.set();
        }
        inner.raise_state_changed();
        result?;
        inner
            .with_activity(request.serial, |activity| activity.snapshot())
            .ok_or(OrchestratorError::Disposed)
    }

    fn start_core(inner: &Arc<Self>, request: &mut StartRequest) -> Result<(), OrchestratorError> {
        let mut supervisor: Option<Arc<dyn ProcessSupervisor>> = None;
        let mut host_started = false;
        let password = request.password.take();
        let outcome = Self::start_steps(
            inner,
            request,
            password.as_deref(),
            &mut supervisor,
            &mut host_started,
        );
        let result = match outcome {
            Ok(()) => Ok(()),
            Err(StartFailure::Cancelled) => {
                if let Some(supervisor) = &supervisor {
                    try_stop(supervisor.as_ref());
                }
                let now = (inner.clock)();
                inner.with_activity(request.serial, |activity| {
                    if !activity.is_terminal() {
                        activity.set_state(LauncherActivityState::Cancelled, now);
                        activity.status = "Operation cancelled.".into();
                        activity.error = None;
                    }
                });
                inner.raise_state_changed();
                Err(OrchestratorError::Cancelled)
            }
            Err(StartFailure::Error(detail)) => {
                let prefix = if request.is_probe() {
                    "Could not refresh characters"
                } else {
                    "Could not launch the client"
                };
                let message = safe_error(prefix, &detail, password.as_deref());
                let now = (inner.clock)();
                inner.with_activity(request.serial, |activity| {
                    if !activity.is_terminal() {
                        activity.set_state(LauncherActivityState::Failed, now);
                        activity.status = message.clone();
                        activity.error = Some(message.clone());
                    }
                });
                inner.raise_state_changed();
                Err(OrchestratorError::Operation(message))
            }
        };
        drop(password);
        // A child that outlived its failed start still holds the lease
        // until it is seen to end.
        if !host_started
            && !supervisor
                .as_ref()
                .is_some_and(|supervisor| supervisor.has_live_process())
        {
            let lease = inner
                .with_activity(request.serial, |activity| {
                    activity.update_session_lease.take()
                })
                .flatten();
            drop(lease);
        }
        result
    }

    fn start_steps(
        inner: &Arc<Self>,
        request: &StartRequest,
        password: Option<&str>,
        supervisor_slot: &mut Option<Arc<dyn ProcessSupervisor>>,
        host_started: &mut bool,
    ) -> Result<(), StartFailure> {
        let check = |token: &LinkedToken| {
            if token.is_cancelled() {
                Err(StartFailure::Cancelled)
            } else {
                Ok(())
            }
        };
        check(&request.cancellation)?;

        let lease = inner
            .barrier
            .acquire_session()
            .map_err(|error| StartFailure::Error(error.to_string()))?;
        let mut lease = Some(lease);
        inner
            .with_activity(request.serial, |activity| {
                activity.update_session_lease = lease.take();
            })
            .ok_or(StartFailure::Cancelled)?;

        let composed = match &request.character {
            None => inner.config_service.compose_probe_and_write(
                &request.server,
                &request.account,
                &request.install,
                &inner.paths,
                &request.session_id,
            ),
            Some(character) => inner.config_service.compose_and_write(
                &request.server,
                &request.account,
                character,
                &request.install,
                &inner.paths,
                &request.session_id,
                None,
                request.plugin_catalog.as_deref(),
            ),
        }
        .map_err(|error| StartFailure::Error(error.to_string()))?;
        check(&request.cancellation)?;

        let supervisor = inner.supervisor_factory.create();
        *supervisor_slot = Some(Arc::clone(&supervisor));
        let weak: Weak<Self> = Arc::downgrade(inner);
        let serial = request.serial;
        supervisor.subscribe(Arc::new(move |state| {
            if let Some(inner) = weak.upgrade() {
                inner.apply_supervisor_state(serial, state);
            }
        }));
        let status_source: SharedStatusSource = Arc::new(Mutex::new(
            inner
                .status_source_factory
                .create(&composed.status_file_path),
        ));
        let plugin_notice = (!composed.plugin_status_lines.is_empty())
            .then(|| composed.plugin_status_lines.join(" "));
        inner
            .with_activity(request.serial, |activity| {
                activity.supervisor = Some(Arc::clone(&supervisor));
                activity.status_source = Some(status_source);
                activity.stderr_log_path = Some(composed.stderr_log_path.clone());
                activity.plugin_notice = plugin_notice;
                activity.status = "Starting host process…".into();
            })
            .ok_or(StartFailure::Cancelled)?;
        inner.raise_state_changed();
        check(&request.cancellation)?;

        let mut spec = match request.launch_mode {
            None => inner
                .executables
                .create_probe_spec(&composed.config_file_path, Some(&composed.stderr_log_path)),
            Some(mode) => inner.executables.create_play_spec(
                mode,
                &composed.config_file_path,
                Some(&composed.stderr_log_path),
            ),
        }
        .map_err(|error| StartFailure::Error(error.to_string()))?;
        // The child resolves exactly the launcher's folders, not a pointer
        // file or whatever the shell exported.
        spec.environment
            .extend(crate::launching::child_environment(&inner.paths));
        supervisor
            .start(&spec, password)
            .map_err(|error| StartFailure::Error(error.to_string()))?;
        *host_started = true;
        check(&request.cancellation)
    }

    fn apply_supervisor_state(&self, serial: u64, process_state: LauncherSessionState) {
        let now = (self.clock)();
        let applied = self.with_activity(serial, |activity| match process_state {
            LauncherSessionState::Starting => {
                if activity.state == LauncherActivityState::Starting {
                    activity.status = "Starting host process…".into();
                }
                None
            }
            LauncherSessionState::Running => {
                if activity.state == LauncherActivityState::Starting {
                    activity.set_state(LauncherActivityState::Running, now);
                    activity.status = "Host process running; waiting for connection…".into();
                }
                None
            }
            LauncherSessionState::Exited => {
                activity.process_exited = true;
                if activity.exit_code.is_none() {
                    activity.exit_code = activity
                        .supervisor
                        .as_ref()
                        .and_then(|supervisor| supervisor.exit_code());
                }
                word_exit_failure(activity);
                if !activity.is_terminal() {
                    activity.set_state(LauncherActivityState::Exited, now);
                    activity.status = activity.host_terminal_status.clone().unwrap_or_else(|| {
                        match activity.exit_code {
                            _ if activity.stop == Some(LauncherSessionStop::Forced) => {
                                FORCED_STOP.to_owned()
                            }
                            _ if activity.stop.is_some() => "Stopped.".to_owned(),
                            Some(code) => format!("Host process exited with code {code}."),
                            None => "Host process exited.".to_owned(),
                        }
                    });
                } else if activity.state == LauncherActivityState::Exited
                    && let Some(status) = &activity.host_terminal_status
                {
                    activity.status = status.clone();
                }
                activity.update_session_lease.take()
            }
        });
        let Some(lease) = applied else {
            return;
        };
        drop(lease);
        self.raise_state_changed();
    }

    /// Marks the clients in the world that have gone silent for
    /// [`NOT_RESPONDING_AFTER`] as not responding (detection only: nothing
    /// is stopped); true when one changed.
    fn judge_responsiveness(&self) -> bool {
        let now = (self.clock)();
        let mut state = lock(&self.gate);
        let mut changed = false;
        for activity in &mut state.activities {
            let silent = activity.last_progress_at.is_some_and(|at| {
                now.duration_since(at).unwrap_or_default() >= NOT_RESPONDING_AFTER
            });
            if silent
                && activity.heartbeats_seen
                && !activity.not_responding
                && activity.state == LauncherActivityState::InWorld
            {
                activity.not_responding = true;
                activity.status = NOT_RESPONDING.into();
                changed = true;
            }
        }
        changed
    }

    /// Gives up the window switches their clients did not answer in time;
    /// true when one was given up.
    fn expire_window_switches(&self) -> bool {
        let now = (self.clock)();
        let mut state = lock(&self.gate);
        let mut expired = false;
        for activity in &mut state.activities {
            let Some(sent_at) = activity.window_switch_sent_at else {
                continue;
            };
            let waited = now.duration_since(sent_at).unwrap_or_default();
            if activity.is_terminal() || waited >= WINDOW_SWITCH_TIMEOUT {
                activity.window.switching_to = None;
                activity.window_switch_sent_at = None;
                if !activity.is_terminal() {
                    activity.window.error = Some(WINDOW_SWITCH_UNANSWERED.into());
                }
                expired = true;
            }
        }
        expired
    }

    fn apply_status_event(&self, serial: u64, event: StatusEvent) {
        let now = (self.clock)();
        let mut guard = lock(&self.gate);
        let State {
            store, activities, ..
        } = &mut *guard;
        let Some(activity) = activities
            .iter_mut()
            .find(|activity| activity.serial == serial)
        else {
            return;
        };
        if event.session_id() != activity.session_id {
            activity.error = Some("Ignored a status event for a different session id.".into());
            return;
        }
        // Any line is progress; a heartbeat also says the client keeps them
        // coming while it is well.
        activity.last_progress_at = Some(now);
        if event.kind == StatusEventKind::Heartbeat {
            activity.heartbeats_seen = true;
        }
        if std::mem::take(&mut activity.not_responding)
            && activity.state == LauncherActivityState::InWorld
        {
            activity.status = "In world.".into();
        }

        if activity.is_terminal() {
            match event.kind {
                StatusEventKind::Exited {
                    code,
                    reason,
                    server_message,
                    account_in_use,
                } => {
                    activity.exit_code.get_or_insert(code);
                    activity.host_reported_exit = true;
                    let status = format!("Exited: {reason} (code {code}).");
                    activity.exit_reason.get_or_insert(reason);
                    activity.server_message = server_message;
                    activity.account_in_use = account_in_use;
                    word_exit_failure(activity);
                    let status = activity.host_terminal_status.get_or_insert(status).clone();
                    if activity.state == LauncherActivityState::Exited {
                        activity.status = status;
                    }
                }
                StatusEventKind::CharacterList {
                    account_name,
                    characters,
                    ..
                } => apply_roster(store, activity, &account_name, &characters, false),
                _ => {}
            }
            return;
        }

        match event.kind {
            StatusEventKind::Started => activity.status = "Host started.".into(),
            StatusEventKind::Connected => {
                if activity.state != LauncherActivityState::Stopping {
                    activity.set_state(LauncherActivityState::Connected, now);
                }
                activity.status = "Connected; waiting for character roster…".into();
            }
            StatusEventKind::CharacterList {
                account_name,
                characters,
                ..
            } => apply_roster(store, activity, &account_name, &characters, true),
            StatusEventKind::EnteredWorld { character_name, .. } => {
                if activity.state != LauncherActivityState::Stopping {
                    activity.set_state(LauncherActivityState::InWorld, now);
                }
                if !character_name.trim().is_empty() {
                    activity.character_name = Some(character_name);
                }
                // The character has its own column; the status stays short.
                activity.status = "In world.".into();
            }
            StatusEventKind::PluginLoaded { plugin } => {
                activity.status = format!("Plugin loaded: {plugin}.");
            }
            StatusEventKind::PluginFailed { plugin, error } => {
                let message = format!("Plugin failed: {plugin}: {error}");
                activity.status = message.clone();
                activity.error = Some(message);
            }
            StatusEventKind::LoginCommandFailed {
                command_index,
                error,
                ..
            } => {
                let message = format!("Login command {command_index} failed: {error}");
                activity.status = message.clone();
                activity.error = Some(message);
            }
            StatusEventKind::Disconnected { reason } => {
                if activity.state != LauncherActivityState::Stopping {
                    activity.set_state(LauncherActivityState::Disconnected, now);
                }
                activity.status = format!("Disconnected: {reason}.");
            }
            StatusEventKind::Exited {
                code,
                reason,
                server_message,
                account_in_use,
            } => {
                activity.set_state(LauncherActivityState::Exited, now);
                activity.exit_code = Some(code);
                activity.host_reported_exit = true;
                let status = format!("Exited: {reason} (code {code}).");
                activity.exit_reason = Some(reason);
                activity.server_message = server_message;
                activity.account_in_use = account_in_use;
                word_exit_failure(activity);
                activity.host_terminal_status = Some(status.clone());
                activity.status = status;
            }
            // Progress only; the not-responding check reads it.
            StatusEventKind::Heartbeat => {}
            StatusEventKind::Malformed { error } => {
                activity.error = Some(format!("Malformed host status event: {error}"));
            }
            StatusEventKind::Unknown { .. } => {
                activity.status = if event.envelope.e.trim().is_empty() {
                    "Ignored an unreadable host status event.".into()
                } else {
                    format!("Ignored unknown host event '{}'.", event.envelope.e)
                };
            }
            // Character creation is reported to the creation screen, not
            // to the session row.
            StatusEventKind::CharacterCreated { .. } | StatusEventKind::CreationFailed { .. } => {}
            StatusEventKind::WindowOpened => {
                activity.window.is_open = true;
                if activity.window.switching_to == Some(true) {
                    activity.window.switching_to = None;
                    activity.window_switch_sent_at = None;
                }
            }
            StatusEventKind::WindowClosed { .. } => {
                activity.window.is_open = false;
                if activity.window.switching_to == Some(false) {
                    activity.window.switching_to = None;
                    activity.window_switch_sent_at = None;
                }
            }
            StatusEventKind::WindowFailed { error } => {
                activity.window.is_open = false;
                activity.window.switching_to = None;
                activity.window_switch_sent_at = None;
                activity.window.error = Some(format!("The game window could not open: {error}"));
            }
        }
    }
}

/// Folds a reported roster into the account's characters and saves it.
fn apply_roster(
    store: &mut LauncherProfileStore,
    activity: &mut Activity,
    account_name: &str,
    characters: &[crate::status::StatusCharacterEntry],
    update_status: bool,
) {
    if !same_account(account_name, &activity.account_name) {
        activity.error = Some(
            "Ignored a character roster whose account did not match the launched account.".into(),
        );
        return;
    }
    let roster: Vec<CharacterRosterEntry> = characters
        .iter()
        .map(|character| {
            CharacterRosterEntry::new(
                character.id,
                character.name.clone(),
                character.seconds_greyed_out,
            )
        })
        .collect();
    let server_name = activity.server_name.clone();
    let account = activity.account_name.clone();
    match store.execute_transaction(|store| store.merge_roster(&server_name, &account, &roster)) {
        Ok(()) => {
            if update_status {
                activity.status = if roster.len() == 1 {
                    "Character roster refreshed: 1 character.".into()
                } else {
                    format!("Character roster refreshed: {} characters.", roster.len())
                };
            }
        }
        Err(error) => {
            let message = safe_error(
                "Could not save the refreshed character roster",
                &error.to_string(),
                None,
            );
            if update_status {
                activity.status = message.clone();
            }
            activity.error = Some(message);
        }
    }
}

fn try_stop(supervisor: &dyn ProcessSupervisor) {
    // Best effort while unwinding a cancelled start; the supervisor kills
    // after the timeout on its own.
    let _ = supervisor.stop(STOP_TIMEOUT_ON_DISPOSE);
}
