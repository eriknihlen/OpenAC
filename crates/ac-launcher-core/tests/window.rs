//! The launcher window's logic. Ported from OpenAC
//! `tests/AcDream.Launcher.Tests/`: `LauncherWindowViewModel.Accounts.Tests`,
//! `LauncherWindowViewModel.Plugins.Tests`, `LauncherSessionRowViewModelTests`,
//! `ProfileFieldsEditorTests` and `PluginInstallDialogViewModelTests`; each
//! test names its source.

mod common;

use std::collections::VecDeque;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use ac_host::PathSet;
use ac_launcher_core::cancel::{CancelToken, Cancelled};
use ac_launcher_core::installation::LauncherInstallRecord;
use ac_launcher_core::launcher_log::LauncherLog;
use ac_launcher_core::net::{HttpError, HttpRequest, HttpResponse, HttpTransport};
use ac_launcher_core::orchestration::{
    AccountCapabilities, LauncherAccountSnapshot, LauncherActivityKind, LauncherActivityState,
    LauncherCapability, LauncherCharacterSnapshot, LauncherExecutableSet, LauncherOrchestrator,
    LauncherPlatformCapabilities, LauncherServerSnapshot, LauncherSessionSnapshot,
    LauncherSessionWindow, LauncherStateSnapshot, OrchestratorError, StateChangedListener,
};
use ac_launcher_core::plugins::{Permission, PermissionDeclaration, PluginCatalog};
use ac_launcher_core::profiles::{
    LaunchMode, LauncherCheckedAccountStore, LauncherProfileStore, LauncherTextEditorKind,
};
use ac_launcher_core::status::ServerHealthSnapshot;
use ac_launcher_core::updates::{ClientVersionResolution, ClientVersionState, LauncherVersion};
use ac_launcher_core::url::WebUrl;
use ac_launcher_core::window::accounts::{CHARACTER_SELECT, RowLaunchMode};
use ac_launcher_core::window::classic_migration::ClassicMigrationDialog;
use ac_launcher_core::window::install_dialog::{
    InstallDialogRequest, PluginCharacterOption, PluginEnableChoice, PluginInstallDialog,
};
use ac_launcher_core::window::plugins::PluginComposition;
use ac_launcher_core::window::profile_editor::{HIDDEN_PASSWORD, ProfileTextEditor};
use ac_launcher_core::window::sessions::SessionRow;
use ac_launcher_core::window::{
    ClosePrompt, InlineSpawner, LauncherWindow, LauncherWindowParts, Orchestration, ServerHealth,
    Spawner,
};

// --- Fixtures ---------------------------------------------------------------------------

type LaunchHandler =
    Box<dyn Fn(&CancelToken) -> Result<LauncherSessionSnapshot, OrchestratorError> + Send + Sync>;
type StopHandler = Box<dyn Fn(&CancelToken) -> Result<(), OrchestratorError> + Send + Sync>;

/// server, account, character, mode.
type LaunchRequest = (String, String, Option<String>, LaunchMode);
/// server, account, character, mode, its own plugins (`None`: the
/// account's).
type SettingsUpdate = (String, String, String, LaunchMode, Option<Vec<String>>);
/// server, account, plugins.
type AccountPluginUpdate = (String, String, Vec<String>);

/// OpenAC's `FakeLauncherOrchestrator`.
#[derive(Default)]
struct Fake {
    servers: Mutex<Vec<LauncherServerSnapshot>>,
    session: Mutex<Option<LauncherSessionSnapshot>>,
    account_launch_capability: Mutex<Option<LauncherCapability>>,
    launch_requests: Mutex<Vec<LaunchRequest>>,
    launch_handler: Mutex<Option<LaunchHandler>>,
    saved_row_selection: Mutex<Option<(Option<String>, LaunchMode)>>,
    settings_updates: Mutex<Vec<SettingsUpdate>>,
    account_plugin_updates: Mutex<Vec<AccountPluginUpdate>>,
    removed_character: Mutex<Option<(String, String, String)>>,
    show_beta_plugins: Mutex<bool>,
    installed: Mutex<Option<LauncherInstallRecord>>,
    listeners: Mutex<Vec<StateChangedListener>>,
    /// Replaces `session` as the session list when set.
    sessions_override: Mutex<Option<Vec<LauncherSessionSnapshot>>>,
    stop_handler: Mutex<Option<StopHandler>>,
    stopped: Mutex<Vec<String>>,
    /// Each stop's graceful timeout, in order.
    stop_timeouts: Mutex<Vec<Duration>>,
    /// The launcher is closing.
    closed_to_new_sessions: Mutex<bool>,
    snapshot_reads: std::sync::atomic::AtomicUsize,
    capability_reads: std::sync::atomic::AtomicUsize,
    /// Window switches asked for: session, open.
    window_switches: Mutex<Vec<(String, bool)>>,
    /// Refuses the next window switch with this message.
    window_switch_refusal: Mutex<Option<String>>,
}

fn character(
    server: &str,
    account: &str,
    name: &str,
    mode: LaunchMode,
    plugins: &[&str],
) -> LauncherCharacterSnapshot {
    LauncherCharacterSnapshot {
        server_name: server.into(),
        account_name: account.into(),
        name: name.into(),
        id: Some("123".into()),
        launch_mode: mode,
        plugins: plugins.iter().map(|id| (*id).to_owned()).collect(),
        login_commands: Vec::new(),
        has_running_session: false,
        session_status: "Ready".into(),
        // A character listed with plugins of its own.
        has_own_plugins: !plugins.is_empty(),
    }
}

fn account(
    server: &str,
    name: &str,
    characters: Vec<LauncherCharacterSnapshot>,
) -> LauncherAccountSnapshot {
    LauncherAccountSnapshot {
        server_name: server.into(),
        account_name: name.into(),
        characters,
        has_running_activity: false,
        activity_status: "Ready".into(),
        selected_character: None,
        selected_launch_mode: None,
        plugins: Vec::new(),
        profiles: Vec::new(),
        login_commands: Vec::new(),
    }
}

fn server(name: &str, accounts: Vec<LauncherAccountSnapshot>) -> LauncherServerSnapshot {
    LauncherServerSnapshot {
        name: name.into(),
        host: "localhost".into(),
        port: 9000,
        accounts,
    }
}

/// OpenAC `BatchServer`: each account has "A character".
fn batch_server(name: &str, accounts: &[&str]) -> LauncherServerSnapshot {
    server(
        name,
        accounts
            .iter()
            .map(|account_name| {
                account(
                    name,
                    account_name,
                    vec![character(
                        name,
                        account_name,
                        "A character",
                        LaunchMode::Gui,
                        &[],
                    )],
                )
            })
            .collect(),
    )
}

/// OpenAC `CreateSession`.
fn session(state: LauncherActivityState) -> LauncherSessionSnapshot {
    LauncherSessionSnapshot {
        session_id: "session-1".into(),
        kind: LauncherActivityKind::Play,
        server_name: "Local ACE".into(),
        account_name: "testaccount".into(),
        character_name: Some("+Acdream".into()),
        launch_mode: Some(LaunchMode::Gui),
        state,
        status: "Connected.".into(),
        exit_code: (state == LauncherActivityState::Exited).then_some(0),
        error: None,
        created_at: UNIX_EPOCH,
        exit_reason: None,
        exited_gracefully: false,
        server_message: None,
        account_in_use: false,
        stop: None,
        not_responding: false,
        plugin_notice: None,
        has_live_process: None,
        startup_in_flight: false,
        window: LauncherSessionWindow {
            is_open: true,
            ..LauncherSessionWindow::default()
        },
    }
}

/// OpenAC `BatchOrchestrator`.
fn batch() -> Arc<Fake> {
    let fake = Fake::default();
    *fake.servers.lock().unwrap() = vec![
        batch_server("One", &["Alice", "Bob"]),
        batch_server("Two", &["Alice"]),
    ];
    *fake.session.lock().unwrap() = Some(session(LauncherActivityState::Exited));
    Arc::new(fake)
}

impl Fake {
    fn raise_state_changed(&self) {
        for listener in self.listeners.lock().unwrap().iter() {
            listener();
        }
    }

    fn set_session(&self, update: impl FnOnce(&mut LauncherSessionSnapshot)) {
        let mut guard = self.session.lock().unwrap();
        let session = guard.get_or_insert_with(|| session(LauncherActivityState::Exited));
        update(session);
    }

    fn platform() -> LauncherPlatformCapabilities {
        LauncherPlatformCapabilities::for_operating_system(true, false, false)
    }
}

impl Orchestration for Fake {
    fn subscribe(&self, listener: StateChangedListener) {
        self.listeners.lock().unwrap().push(listener);
    }
    fn load_profiles(&self) -> Result<(), OrchestratorError> {
        Ok(())
    }
    fn snapshot(&self) -> Result<LauncherStateSnapshot, OrchestratorError> {
        self.snapshot_reads
            .fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        let sessions = match self.sessions_override.lock().unwrap().clone() {
            Some(sessions) => sessions,
            None => self.session.lock().unwrap().clone().into_iter().collect(),
        };
        Ok(LauncherStateSnapshot {
            servers: self.servers.lock().unwrap().clone(),
            sessions,
            platform: Self::platform(),
            is_installation_ready: self.installed.lock().unwrap().is_some(),
            installation_status: "No installed client is configured.".into(),
            show_beta_plugins: *self.show_beta_plugins.lock().unwrap(),
        })
    }
    fn launch_capability(&self, mode: LaunchMode) -> Result<LauncherCapability, OrchestratorError> {
        Ok(Self::platform().for_launch_mode(mode))
    }
    fn account_launch_capability(
        &self,
        _server: &str,
        _account: &str,
        mode: LaunchMode,
    ) -> Result<LauncherCapability, OrchestratorError> {
        self.capability_reads
            .fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        match self.account_launch_capability.lock().unwrap().clone() {
            Some(capability) => Ok(capability),
            None => self.launch_capability(mode),
        }
    }
    // OpenAC's fake answers from its servers without a snapshot.
    fn account_launch_capabilities(
        &self,
        mode: LaunchMode,
    ) -> Result<AccountCapabilities, OrchestratorError> {
        let servers = self.servers.lock().unwrap().clone();
        let mut result = AccountCapabilities::new();
        for server in servers {
            for account in server.accounts {
                let capability =
                    self.account_launch_capability(&server.name, &account.account_name, mode)?;
                result.insert((server.name.clone(), account.account_name), capability);
            }
        }
        Ok(result)
    }
    fn set_install_record(
        &self,
        record: Option<LauncherInstallRecord>,
    ) -> Result<(), OrchestratorError> {
        *self.installed.lock().unwrap() = record;
        self.raise_state_changed();
        Ok(())
    }
    fn set_installation_state(
        &self,
        record: Option<LauncherInstallRecord>,
        _status: &str,
    ) -> Result<(), OrchestratorError> {
        *self.installed.lock().unwrap() = record;
        self.raise_state_changed();
        Ok(())
    }
    fn set_plugin_catalog(&self, _catalog: Option<PluginCatalog>) -> Result<(), OrchestratorError> {
        Ok(())
    }
    fn set_show_beta_plugins(&self, value: bool) -> Result<(), OrchestratorError> {
        *self.show_beta_plugins.lock().unwrap() = value;
        Ok(())
    }
    fn read_profile_text(
        &self,
        _kind: LauncherTextEditorKind,
    ) -> Result<String, OrchestratorError> {
        Ok(String::new())
    }
    fn save_profile_text(
        &self,
        _kind: LauncherTextEditorKind,
        _text: &str,
        _original: &str,
    ) -> Result<(), OrchestratorError> {
        Ok(())
    }
    fn describe_profile_text_removals(
        &self,
        _kind: LauncherTextEditorKind,
        _text: &str,
    ) -> Result<Vec<String>, OrchestratorError> {
        Ok(Vec::new())
    }
    fn update_character_settings(
        &self,
        server: &str,
        account: &str,
        character: &str,
        mode: LaunchMode,
        plugins: Option<&[String]>,
    ) -> Result<(), OrchestratorError> {
        self.settings_updates.lock().unwrap().push((
            server.into(),
            account.into(),
            character.into(),
            mode,
            plugins.map(<[String]>::to_vec),
        ));
        Ok(())
    }
    fn update_account_plugins(
        &self,
        server: &str,
        account: &str,
        plugins: &[String],
    ) -> Result<(), OrchestratorError> {
        self.account_plugin_updates.lock().unwrap().push((
            server.into(),
            account.into(),
            plugins.to_vec(),
        ));
        Ok(())
    }
    fn remove_character(
        &self,
        server: &str,
        account: &str,
        character: &str,
    ) -> Result<(), OrchestratorError> {
        *self.removed_character.lock().unwrap() =
            Some((server.into(), account.into(), character.into()));
        Ok(())
    }
    fn take_profile_migration_notice(&self) -> Result<Option<String>, OrchestratorError> {
        Ok(None)
    }
    fn add_server(&self, name: &str, host: &str, port: i32) -> Result<(), OrchestratorError> {
        let mut servers = self.servers.lock().unwrap();
        if servers
            .iter()
            .any(|server| server.name.eq_ignore_ascii_case(name))
        {
            return Err(OrchestratorError::Operation(format!(
                "A server named '{name}' already exists."
            )));
        }
        let mut added = server(name, Vec::new());
        added.host = host.into();
        added.port = port;
        servers.push(added);
        Ok(())
    }
    fn update_account_selection(
        &self,
        _server: &str,
        _account: &str,
        character: Option<&str>,
        mode: LaunchMode,
    ) -> Result<(), OrchestratorError> {
        *self.saved_row_selection.lock().unwrap() = Some((character.map(str::to_owned), mode));
        Ok(())
    }
    fn launch(
        &self,
        server: &str,
        account: &str,
        character: Option<&str>,
        mode: LaunchMode,
        cancel: &CancelToken,
    ) -> Result<LauncherSessionSnapshot, OrchestratorError> {
        self.launch_requests.lock().unwrap().push((
            server.into(),
            account.into(),
            character.map(str::to_owned),
            mode,
        ));
        match self.launch_handler.lock().unwrap().as_ref() {
            Some(handler) => handler(cancel),
            None => Ok(self.session.lock().unwrap().clone().unwrap()),
        }
    }
    fn stop_session(
        &self,
        id: &str,
        timeout: Duration,
        cancel: &CancelToken,
    ) -> Result<(), OrchestratorError> {
        self.stopped.lock().unwrap().push(id.to_owned());
        self.stop_timeouts.lock().unwrap().push(timeout);
        match self.stop_handler.lock().unwrap().as_ref() {
            Some(handler) => handler(cancel),
            None => Ok(()),
        }
    }
    fn poll_status(&self) -> Result<(), OrchestratorError> {
        Ok(())
    }
    fn try_send_console_line(&self, _id: &str, _line: &str) -> bool {
        false
    }
    fn set_session_window(&self, session_id: &str, open: bool) -> Result<(), OrchestratorError> {
        self.window_switches
            .lock()
            .unwrap()
            .push((session_id.to_owned(), open));
        if let Some(refusal) = self.window_switch_refusal.lock().unwrap().take() {
            return Err(OrchestratorError::Operation(refusal));
        }
        if let Some(session) = self.session.lock().unwrap().as_mut() {
            session.window.switching_to = Some(open);
        }
        Ok(())
    }
    fn session_log_path(&self, _id: &str) -> Option<PathBuf> {
        None
    }
    fn close_to_new_sessions(&self) -> Result<(), OrchestratorError> {
        *self.closed_to_new_sessions.lock().unwrap() = true;
        Ok(())
    }
    fn dispose(&self) {}
}

/// Holds jobs until the test runs them: work "still running".
#[derive(Default)]
struct QueueSpawner {
    jobs: Mutex<VecDeque<Box<dyn FnOnce() + Send>>>,
}

impl Spawner for QueueSpawner {
    fn spawn(&self, job: Box<dyn FnOnce() + Send>) {
        self.jobs.lock().unwrap().push_back(job);
    }
}

impl QueueSpawner {
    fn run_all(&self) {
        loop {
            let job = self.jobs.lock().unwrap().pop_front();
            match job {
                Some(job) => job(),
                None => break,
            }
        }
    }

    fn take(&self) -> Option<Box<dyn FnOnce() + Send>> {
        self.jobs.lock().unwrap().pop_front()
    }
}

fn parts(fake: Arc<dyn Orchestration>, spawner: Arc<dyn Spawner>) -> LauncherWindowParts {
    LauncherWindowParts {
        orchestrator: fake,
        spawner,
        wake: Arc::new(|| {}),
        content: None,
        updater: None,
        updater_unavailable_reason: "No update feed.".into(),
        plugins: None,
        server_health: None,
        launcher_version: None,
        client_version: Arc::new(|| None),
        apply_launcher_update: None,
        install_folder: None,
        checked_accounts: None,
        relaunch_clock: None,
        known_servers: None,
        classic_migration: None,
    }
}

/// OpenAC `CreateInitialized`.
fn create_initialized(fake: &Arc<Fake>) -> LauncherWindow {
    let mut window = LauncherWindow::new(parts(fake.clone(), Arc::new(InlineSpawner)));
    window.initialize();
    window
}

/// `CreateInitialized`, then `StartBackgroundInitializationAsync` and
/// `CloseActiveModal`.
fn create_started(fake: &Arc<Fake>) -> LauncherWindow {
    let mut window = create_initialized(fake);
    window.start_background_initialization();
    window.poll();
    window.close_active_modal();
    window.refresh_row_states();
    window
}

fn row_keys(window: &LauncherWindow) -> Vec<(String, String)> {
    window
        .accounts
        .iter()
        .flat_map(|group| group.servers.iter())
        .map(|row| (row.account_name.clone(), row.server_name.clone()))
        .collect()
}

// --- Accounts tab -----------------------------------------------------------------------

// OpenAC LauncherWindowViewModelTests.PollRefreshesPlayWhenReconnectDelayExpiresWithoutAnEvent.
#[test]
fn poll_refreshes_play_when_reconnect_delay_expires_without_an_event() {
    let fake = batch();
    let mut window = create_started(&fake);
    let (account, server) = row_keys(&window)[0].clone();
    *fake.account_launch_capability.lock().unwrap() =
        Some(LauncherCapability::unavailable("Try again in 1 s."));
    window.poll();
    assert!(!window.row(&account, &server).unwrap().can_play());
    *fake.account_launch_capability.lock().unwrap() = Some(LauncherCapability::AVAILABLE);
    window.poll();
    assert!(window.row(&account, &server).unwrap().can_play());
}

// Not an OpenAC test: the server's hold is shown on the row as a countdown,
// not only as the Play button's tooltip; another refusal is not.
#[test]
fn the_servers_hold_counts_down_on_the_row() {
    let fake = batch();
    let mut window = create_started(&fake);
    let (account, server) = row_keys(&window)[0].clone();
    *fake.account_launch_capability.lock().unwrap() =
        Some(LauncherCapability::held(Duration::from_secs(25)));
    window.poll();
    let row = window.row(&account, &server).unwrap();
    assert!(!row.can_play());
    assert_eq!(
        row.hold_text.as_deref(),
        Some("The server may still hold this account. Ready in 25 s.")
    );
    *fake.account_launch_capability.lock().unwrap() =
        Some(LauncherCapability::held(Duration::from_millis(1_500)));
    window.poll();
    assert!(
        window
            .row(&account, &server)
            .unwrap()
            .hold_text
            .as_deref()
            .is_some_and(|text| text.ends_with("Ready in 2 s."))
    );
    *fake.account_launch_capability.lock().unwrap() =
        Some(LauncherCapability::unavailable("Something else."));
    window.poll();
    assert_eq!(window.row(&account, &server).unwrap().hold_text, None);
    *fake.account_launch_capability.lock().unwrap() = Some(LauncherCapability::AVAILABLE);
    window.poll();
    assert_eq!(window.row(&account, &server).unwrap().hold_text, None);
}

// OpenAC LauncherWindowViewModelTests.FailedStartupIsShownInItsRowAndNotCountedAsStarted.
#[test]
fn failed_startup_is_shown_in_its_row_and_not_counted_as_started() {
    let fake = batch();
    let mut window = create_started(&fake);
    let (account, server) = row_keys(&window)[0].clone();
    let failing = fake.clone();
    let (row_account, row_server) = (account.clone(), server.clone());
    *fake.launch_handler.lock().unwrap() = Some(Box::new(move |_| {
        failing.set_session(|session| {
            session.server_name = row_server.clone();
            session.account_name = row_account.clone();
            session.state = LauncherActivityState::Failed;
            session.error = Some("Client files do not match".into());
            session.exit_code = Some(1);
        });
        Ok(failing.session.lock().unwrap().clone().unwrap())
    }));
    window.launch_row(&account, &server);
    window.poll();
    assert!(window.operation_status.contains("Started 0 of 1"));
    let row = window.row(&account, &server).unwrap();
    assert_eq!(
        row.launch_error.as_deref(),
        Some("Client files do not match")
    );
    assert!(row.has_launch_error());
    assert!(!row.is_active());
    assert!(row.can_play());
}

struct CountingHealth {
    checks: Mutex<usize>,
}

impl ServerHealth for CountingHealth {
    fn check(
        &self,
        _host: &str,
        _port: u16,
        _name: &str,
        _cancel: &CancelToken,
    ) -> Result<ServerHealthSnapshot, Cancelled> {
        *self.checks.lock().unwrap() += 1;
        Ok(ServerHealthSnapshot {
            is_reachable: true,
            latency_milliseconds: Some(1.0),
            player_count: Some(0),
            is_player_count_stale: false,
            checked_at: SystemTime::now(),
        })
    }
}

fn with_health(
    fake: &Arc<Fake>,
    spawner: Arc<dyn Spawner>,
) -> (LauncherWindow, Arc<CountingHealth>) {
    let health = Arc::new(CountingHealth {
        checks: Mutex::new(0),
    });
    let mut parts = parts(fake.clone(), spawner);
    parts.server_health = Some(health.clone());
    let mut window = LauncherWindow::new(parts);
    window.initialize();
    (window, health)
}

// OpenAC LauncherWindowViewModelTests.AnInactiveWindowNeverChecksServers.
#[test]
fn an_inactive_window_never_checks_servers() {
    let (mut window, health) = with_health(&batch(), Arc::new(InlineSpawner));
    let now = Instant::now();
    window.poll_server_health(false, now);
    window.poll_server_health(false, now + Duration::from_secs(300));
    assert_eq!(*health.checks.lock().unwrap(), 0);
}

// OpenAC LauncherWindowViewModelTests.AnActiveWindowChecksEveryTenSeconds.
#[test]
fn an_active_window_checks_every_ten_seconds() {
    let (mut window, health) = with_health(&batch(), Arc::new(InlineSpawner));
    let start = Instant::now();
    window.poll_server_health(true, start);
    window.poll();
    let after_first = *health.checks.lock().unwrap();
    window.poll_server_health(true, start + Duration::from_secs(5));
    let after_five = *health.checks.lock().unwrap();
    // The interval runs from when the check finished (now), so eleven seconds after it.
    window.poll_server_health(true, Instant::now() + Duration::from_secs(11));
    window.poll();
    assert!(after_first > 0);
    assert_eq!(after_first, after_five);
    assert_eq!(*health.checks.lock().unwrap(), after_first * 2);
}

// OpenAC LauncherWindowViewModelTests.ReactivatingPastTheIntervalChecksOnTheNextPoll.
#[test]
fn reactivating_past_the_interval_checks_on_the_next_poll() {
    let (mut window, health) = with_health(&batch(), Arc::new(InlineSpawner));
    window.poll_server_health(true, Instant::now());
    window.poll();
    let after_first = *health.checks.lock().unwrap();
    let later = Instant::now() + Duration::from_secs(30);
    window.poll_server_health(false, later);
    assert_eq!(*health.checks.lock().unwrap(), after_first);
    window.poll_server_health(true, later);
    window.poll();
    assert_eq!(*health.checks.lock().unwrap(), after_first * 2);
}

// OpenAC LauncherWindowViewModelTests.ABackgroundCheckDoesNotStartWhileOneIsStillRunning and
// ABackgroundCheckLeavesTheCheckServersButtonEnabled.
#[test]
fn a_background_check_does_not_start_while_one_is_still_running() {
    let spawner = Arc::new(QueueSpawner::default());
    let (mut window, health) = with_health(&batch(), spawner.clone());
    let start = Instant::now();
    window.poll_server_health(true, start);
    assert!(window.can_check_servers());
    window.poll_server_health(true, start + Duration::from_secs(60));
    spawner.run_all();
    window.poll();
    // One pass: one check per server.
    assert_eq!(*health.checks.lock().unwrap(), 2);
}

// OpenAC LauncherWindowViewModelTests.ConfiguringServerHealthEnablesTheCheckServersButton.
#[test]
fn configuring_server_health_enables_the_check_servers_button() {
    let window = create_initialized(&batch());
    assert!(!window.can_check_servers());
    let (window, _) = with_health(&batch(), Arc::new(InlineSpawner));
    assert!(window.can_check_servers());
}

// OpenAC LauncherWindowViewModelTests.TheVersionLinesShowLauncherAndClientWithoutBuildMetadata.
#[test]
fn the_version_lines_show_launcher_and_client_without_build_metadata() {
    for (launcher, client, launcher_text, client_text) in [
        ("0.1.10", Some("0.1.8"), "0.1.10", "0.1.8"),
        (
            "0.2.0-beta.1+3a71d75",
            Some("0.2.0+abc"),
            "0.2.0-beta.1",
            "0.2.0",
        ),
        ("0.1.10", None, "0.1.10", "not installed"),
    ] {
        let resolution = ClientVersionResolution {
            state: if client.is_some() {
                ClientVersionState::Verified
            } else {
                ClientVersionState::Missing
            },
            status: String::new(),
            version: client.and_then(LauncherVersion::try_parse),
            directory: None,
            previous_version: None,
            record: None,
        };
        let mut parts = parts(batch(), Arc::new(InlineSpawner));
        parts.launcher_version = Some(launcher.into());
        parts.client_version = Arc::new(move || Some(resolution.clone()));
        let window = LauncherWindow::new(parts);
        assert!(window.has_versions());
        assert_eq!(
            (window.launcher_version_text(), window.client_version_text()),
            (launcher_text.to_owned(), client_text.to_owned())
        );
        assert_eq!(window.plugins_update_text(), "none installed");
    }
}
// OpenAC LauncherWindowViewModelTests.ChangingEndpointClearsHealthFromTheOldEndpoint.
#[test]
fn changing_endpoint_clears_health_from_the_old_endpoint() {
    let fake = batch();
    let mut window = create_initialized(&fake);
    let (account, server) = row_keys(&window)[0].clone();
    {
        let group = window.accounts.iter_mut().next().unwrap();
        let row = &mut group.servers[0];
        row.is_server_online = Some(true);
        row.server_status_text = "Online · 12 players".into();
    }
    for snapshot in fake.servers.lock().unwrap().iter_mut() {
        snapshot.host = "different.example".into();
    }
    fake.raise_state_changed();
    window.poll();
    let row = window.row(&account, &server).unwrap();
    assert_eq!(row.is_server_online, None);
    assert_eq!(row.server_status_text, "Not checked");
    assert_eq!(row.ping_bars(), 0);
}

// OpenAC LauncherWindowViewModelTests.ThePingMeterFillsFewerBarsAsTheReplyGetsSlower and
// ThePingMeterIsEmptyForAServerThatDidNotAnswer.
#[test]
fn the_ping_meter_fills_fewer_bars_as_the_reply_gets_slower() {
    let mut window = create_initialized(&batch());
    let row = &mut window.accounts[0].servers[0];
    for (milliseconds, bars) in [(12.0, 3), (80.0, 3), (81.0, 2), (200.0, 2), (201.0, 1)] {
        row.is_server_online = Some(true);
        row.latency_milliseconds = Some(milliseconds);
        assert_eq!(row.ping_bars(), bars, "{milliseconds} ms");
        assert!(row.has_latency());
    }
    row.latency_milliseconds = Some(15.0);
    row.is_server_online = Some(false);
    assert_eq!(row.ping_bars(), 0);
}

// OpenAC LauncherWindowViewModelTests.PickingACharacterOrLaunchModeIsSavedAsItIsPicked.
#[test]
fn picking_a_character_or_launch_mode_is_saved_as_it_is_picked() {
    let fake = batch();
    let mut window = create_initialized(&fake);
    let (account, server) = row_keys(&window)[0].clone();
    window.set_row_character(&account, &server, "A character");
    assert_eq!(
        *fake.saved_row_selection.lock().unwrap(),
        Some((Some("A character".into()), LaunchMode::Gui))
    );
    window.set_row_launch_mode(&account, &server, RowLaunchMode::Headless);
    assert_eq!(
        *fake.saved_row_selection.lock().unwrap(),
        Some((Some("A character".into()), LaunchMode::Headless))
    );
}

// OpenAC LauncherWindowViewModelTests.ASavedSelectionComesBackWhenTheRowIsBuilt.
#[test]
fn a_saved_selection_comes_back_when_the_row_is_built() {
    let fake = batch();
    for snapshot in fake.servers.lock().unwrap().iter_mut() {
        for account in &mut snapshot.accounts {
            account.selected_character = Some("A character".into());
            account.selected_launch_mode = Some(LaunchMode::Headless);
        }
    }
    let window = create_initialized(&fake);
    let row = &window.accounts[0].servers[0];
    assert_eq!(row.selected_character, "A character");
    assert_eq!(row.selected_launch_mode, RowLaunchMode::Headless);
    assert_eq!(*fake.saved_row_selection.lock().unwrap(), None);
}

// OpenAC LauncherWindowViewModelTests.TheCharacterBoxShowsWhoIsInWorldAndReturnsToTheSavedChoiceAfterwards.
#[test]
fn the_character_box_shows_who_is_in_world_and_returns_to_the_saved_choice_afterwards() {
    let fake = batch();
    let mut window = create_initialized(&fake);
    let (account, server) = row_keys(&window)[0].clone();
    assert_eq!(
        window.row(&account, &server).unwrap().displayed_character(),
        CHARACTER_SELECT
    );
    fake.set_session(|session| {
        session.server_name = server.clone();
        session.account_name = account.clone();
        session.character_name = Some("A character".into());
        session.state = LauncherActivityState::InWorld;
        session.exit_code = None;
    });
    fake.raise_state_changed();
    window.poll();
    let row = window.row(&account, &server).unwrap();
    assert_eq!(row.displayed_character(), "A character");
    assert_eq!(row.selected_character, CHARACTER_SELECT);
    fake.set_session(|session| {
        session.state = LauncherActivityState::Exited;
        session.exit_code = Some(0);
    });
    fake.raise_state_changed();
    window.poll();
    assert_eq!(
        window.row(&account, &server).unwrap().displayed_character(),
        CHARACTER_SELECT
    );
}

// Not an OpenAC test: a client that stopped responding says so on its row
// and in the session log, before an older error.
#[test]
fn a_client_that_stopped_responding_says_so() {
    let fake = batch();
    let mut window = create_initialized(&fake);
    let (account, server) = row_keys(&window)[0].clone();
    fake.set_session(|session| {
        session.server_name = server.clone();
        session.account_name = account.clone();
        session.state = LauncherActivityState::InWorld;
        session.exit_code = None;
        session.error = Some("Plugin failed: x: y".into());
        session.not_responding = true;
    });
    fake.raise_state_changed();
    window.poll();
    assert_eq!(
        window.row(&account, &server).unwrap().status,
        "Not responding"
    );
    assert_eq!(window.sessions[0].state, "Not responding");
}

// OpenAC LauncherWindowViewModelTests.CheckedRowsLaunchAllSelectedAccountsWithRequestedModesAndContinueAfterFailure.
#[test]
fn checked_rows_launch_all_selected_accounts_with_requested_modes_and_continue_after_failure() {
    let fake = batch();
    let mut window = create_started(&fake);
    let keys = row_keys(&window);
    for (account, server) in &keys {
        window.set_row_checked(account, server, true);
    }
    window.set_row_character(&keys[1].0, &keys[1].1, "A character");
    window.set_row_launch_mode(&keys[1].0, &keys[1].1, RowLaunchMode::Headless);
    let calls = Arc::new(Mutex::new(0));
    let counter = calls.clone();
    let source = fake.clone();
    *fake.launch_handler.lock().unwrap() = Some(Box::new(move |_| {
        let mut calls = counter.lock().unwrap();
        *calls += 1;
        if *calls == 1 {
            Err(OrchestratorError::Operation("Failed to start".into()))
        } else {
            Ok(source.session.lock().unwrap().clone().unwrap())
        }
    }));
    window.launch_checked();
    window.poll();
    let requests = fake.launch_requests.lock().unwrap().clone();
    assert_eq!(requests.len(), 3);
    assert_eq!(requests[0].3, LaunchMode::GuiSelect);
    assert_eq!(requests[0].2, None);
    assert_eq!(requests[1].3, LaunchMode::Headless);
    assert_eq!(requests[1].2.as_deref(), Some("A character"));
    assert!(window.operation_status.contains("Started 2 of 3"));
    assert!(
        window
            .last_error
            .as_deref()
            .unwrap()
            .contains("Failed to start")
    );
}

// OpenAC LauncherWindowViewModelTests.PlayableCheckedRowsFollowsWhatIsCheckedAndReady.
#[test]
fn playable_checked_rows_follows_what_is_checked_and_ready() {
    let fake = batch();
    let mut window = create_started(&fake);
    let (account, server) = row_keys(&window)[0].clone();
    assert!(!window.has_playable_checked_rows());
    window.set_row_checked(&account, &server, true);
    fake.raise_state_changed();
    window.poll();
    assert_eq!(
        window.row(&account, &server).unwrap().can_play(),
        window.has_playable_checked_rows()
    );
    window.set_row_checked(&account, &server, false);
    fake.raise_state_changed();
    window.poll();
    assert!(!window.has_playable_checked_rows());
}

// OpenAC LauncherWindowViewModelTests.PollingPreservesChecksCharactersAndExpansionAndBatchCannotDoubleStart.
#[test]
fn polling_preserves_checks_characters_and_expansion_and_batch_cannot_double_start() {
    let fake = batch();
    let spawner = Arc::new(QueueSpawner::default());
    let mut window = LauncherWindow::new(parts(fake.clone(), spawner.clone()));
    window.initialize();
    window.start_background_initialization();
    spawner.run_all();
    window.poll();
    spawner.run_all();
    window.poll();
    window.close_active_modal();
    let (account, server) = row_keys(&window)[0].clone();
    window.set_group_expanded(&account, false);
    window.set_row_checked(&account, &server, true);
    window.set_row_character(&account, &server, "A character");
    fake.raise_state_changed();
    window.poll();
    assert!(!window.accounts[0].is_expanded);
    let row = window.row(&account, &server).unwrap();
    assert!(row.is_checked);
    assert_eq!(row.selected_character, "A character");

    // The first batch is still running (its job has not run): neither a second batch nor a row's
    // Play starts anything.
    window.launch_checked();
    fake.raise_state_changed();
    window.poll();
    window.launch_checked();
    window.launch_row(&account, &server);
    spawner.run_all();
    window.poll();
    assert_eq!(fake.launch_requests.lock().unwrap().len(), 1);
}

// OpenAC LauncherWindowViewModelTests.MixedCheckedRowsReportInvalidSelectionWithoutDroppingReadyLaunches.
#[test]
fn mixed_checked_rows_report_invalid_selection_without_dropping_ready_launches() {
    let fake = batch();
    let mut window = create_started(&fake);
    let keys = row_keys(&window);
    // Alice's two rows (One, Two).
    window.set_row_checked(&keys[0].0, &keys[0].1, true);
    window.set_row_checked(&keys[1].0, &keys[1].1, true);
    window.set_row_launch_mode(&keys[1].0, &keys[1].1, RowLaunchMode::Headless);
    window.launch_checked();
    window.poll();
    assert_eq!(fake.launch_requests.lock().unwrap().len(), 1);
    assert!(
        window
            .last_error
            .as_deref()
            .unwrap()
            .contains("Choose a character")
    );
    assert!(window.operation_status.contains("Started 1 of 2"));
}

// OpenAC LauncherWindowViewModelTests.RosterRefreshKeepsChosenCharacterWhenSelectorTemporarilyClearsBinding.
#[test]
fn roster_refresh_keeps_the_chosen_character() {
    let fake = batch();
    let mut window = create_initialized(&fake);
    let (account, server) = row_keys(&window)[0].clone();
    window.set_row_character(&account, &server, "A character");
    {
        let mut servers = fake.servers.lock().unwrap();
        let mut another = servers[0].accounts[0].characters[0].clone();
        another.name = "Another character".into();
        servers[0].accounts[0].characters.push(another);
    }
    fake.raise_state_changed();
    window.poll();
    let row = window.row(&account, &server).unwrap();
    assert_eq!(row.selected_character, "A character");
    assert!(
        row.character_choices
            .iter()
            .any(|choice| choice == "Another character")
    );
}

// OpenAC LauncherWindowViewModelTests.ClosingDuringBatchCancelsRemainingLaunchesWithoutReadingDisposedCancellationSource.
#[test]
fn closing_during_batch_cancels_remaining_launches() {
    let fake = batch();
    let spawner = Arc::new(QueueSpawner::default());
    let mut window = LauncherWindow::new(parts(fake.clone(), spawner.clone()));
    window.initialize();
    window.start_background_initialization();
    spawner.run_all();
    window.poll();
    spawner.run_all();
    window.poll();
    window.close_active_modal();
    let keys = row_keys(&window);
    for (account, server) in keys.iter().filter(|(account, _)| account == "Alice") {
        window.set_row_checked(account, server, true);
    }
    let (release, wait) = std::sync::mpsc::channel::<()>();
    let wait = Mutex::new(wait);
    let source = fake.clone();
    *fake.launch_handler.lock().unwrap() = Some(Box::new(move |_| {
        // The first launch is pending until the test lets it finish.
        let _ = wait.lock().unwrap().recv();
        Ok(source.session.lock().unwrap().clone().unwrap())
    }));
    window.launch_checked();
    let job = spawner.take().expect("the batch job");
    let worker = std::thread::spawn(job);
    while fake.launch_requests.lock().unwrap().is_empty() {
        std::thread::sleep(Duration::from_millis(1));
    }
    window.dispose();
    release.send(()).unwrap();
    worker.join().unwrap();
    assert_eq!(fake.launch_requests.lock().unwrap().len(), 1);
}

// OpenAC LauncherWindowViewModelTests.HeadlessRequiresCharacterAndFreshActiveStateBlocksStaleRowLaunch.
#[test]
fn headless_requires_character_and_fresh_active_state_blocks_stale_row_launch() {
    let fake = batch();
    let mut window = create_started(&fake);
    let (account, server) = row_keys(&window)[0].clone();
    window.set_row_checked(&account, &server, true);
    window.set_row_launch_mode(&account, &server, RowLaunchMode::Headless);
    let row = window.row(&account, &server).unwrap();
    assert!(!row.can_play());
    assert!(row.disabled_reason.contains("Choose a character"));
    window.set_row_character(&account, &server, "A character");
    assert!(window.row(&account, &server).unwrap().can_play());
    // The session becomes active without an event: until one arrives the row keeps the
    // displayed snapshot, and the launch checks the newest state.
    fake.set_session(|session| {
        session.server_name = server.clone();
        session.account_name = account.clone();
        session.state = LauncherActivityState::Running;
        session.exit_code = None;
    });
    window.refresh_row_states();
    assert!(window.row(&account, &server).unwrap().can_play());
    window.launch_checked();
    window.poll();
    assert!(fake.launch_requests.lock().unwrap().is_empty());
    fake.raise_state_changed();
    window.poll();
    assert!(!window.row(&account, &server).unwrap().can_play());
}

// --- Session log ------------------------------------------------------------------------

fn session_row(
    state: LauncherActivityState,
    kind: LauncherActivityKind,
    character: Option<&str>,
    mode: Option<LaunchMode>,
) -> SessionRow {
    let mut snapshot = session(state);
    snapshot.kind = kind;
    snapshot.character_name = character.map(str::to_owned);
    snapshot.launch_mode = mode;
    snapshot.account_name = "testaccount".into();
    SessionRow::new(&snapshot)
}

// OpenAC LauncherSessionRowViewModelTests.StateReadsAsPlainEnglish.
#[test]
fn state_reads_as_plain_english() {
    for (state, expected) in [
        (LauncherActivityState::Starting, "Starting"),
        (LauncherActivityState::Running, "Starting"),
        (LauncherActivityState::Connected, "Character select"),
        (LauncherActivityState::InWorld, "In game"),
        (LauncherActivityState::Disconnected, "Stopping"),
        (LauncherActivityState::Stopping, "Stopping"),
        (LauncherActivityState::Exited, "Stopped"),
        (LauncherActivityState::Cancelled, "Stopped"),
        (LauncherActivityState::Failed, "Failed"),
    ] {
        let row = session_row(
            state,
            LauncherActivityKind::Play,
            Some("+Acdream"),
            Some(LaunchMode::Gui),
        );
        assert_eq!(row.state, expected, "{state:?}");
    }
}

// OpenAC LauncherSessionRowViewModelTests.InGameDoesNotDependOnHowTheSessionWasLaunched,
// ACharacterSelectLaunchSaysSoUntilACharacterIsKnown, AnEnteredCharacterReplacesTheCharacterSelectPlaceholder,
// AProbeIsLabelledAsACharacterRefresh, StopIsOfferedOnlyWhileTheSessionIsActive.
#[test]
fn a_session_row_names_who_is_playing() {
    for mode in [LaunchMode::Gui, LaunchMode::GuiSelect, LaunchMode::Headless] {
        let row = session_row(
            LauncherActivityState::InWorld,
            LauncherActivityKind::Play,
            Some("+Acdream"),
            Some(mode),
        );
        assert_eq!(row.state, "In game");
    }
    let select = session_row(
        LauncherActivityState::Connected,
        LauncherActivityKind::Play,
        None,
        Some(LaunchMode::GuiSelect),
    );
    assert_eq!(select.character, "Character select");
    assert_eq!(select.account, "testaccount");
    let entered = session_row(
        LauncherActivityState::InWorld,
        LauncherActivityKind::Play,
        Some("+alex"),
        Some(LaunchMode::GuiSelect),
    );
    assert_eq!(entered.character, "+alex");
    let probe = session_row(
        LauncherActivityState::Connected,
        LauncherActivityKind::Probe,
        None,
        None,
    );
    assert_eq!(probe.character, "Character refresh");
    assert_eq!(probe.state, "Reading characters");
    assert!(
        session_row(
            LauncherActivityState::InWorld,
            LauncherActivityKind::Play,
            None,
            None
        )
        .is_active
    );
    assert!(
        !session_row(
            LauncherActivityState::Exited,
            LauncherActivityKind::Play,
            None,
            None
        )
        .is_active
    );
}

// --- Profile editors (real orchestrator) ------------------------------------------------

struct EditorFixture {
    root: PathBuf,
    orchestrator: LauncherOrchestrator,
    editor: ProfileTextEditor,
}

impl EditorFixture {
    fn new() -> Self {
        let root = std::env::temp_dir()
            .join("openac-launcher-window-tests")
            .join(ac_launcher_core::ids::unique_hex32());
        let paths =
            PathSet::from_members(root.join("config"), root.join("data"), root.join("cache"));
        let store = LauncherProfileStore::for_paths(&paths);
        let orchestrator = LauncherOrchestrator::new(
            store,
            paths,
            LauncherExecutableSet::new("unused-client", "unused-headless", None),
            None,
        );
        orchestrator.load_profiles().unwrap();
        Self {
            root,
            orchestrator,
            editor: ProfileTextEditor::default(),
        }
    }

    fn profile_path(&self) -> PathBuf {
        self.root
            .join("config")
            .join(ac_launcher_core::profiles::PROFILE_FILE_NAME)
    }

    fn reload(&self) -> LauncherProfileStore {
        let mut store = LauncherProfileStore::new(self.profile_path(), None).unwrap();
        store.load().unwrap();
        store
    }

    fn open(&mut self, kind: LauncherTextEditorKind) {
        self.editor.open(&self.orchestrator, kind).unwrap();
    }

    fn save(&mut self) {
        self.editor.save(&self.orchestrator);
    }
}

impl Drop for EditorFixture {
    fn drop(&mut self) {
        self.orchestrator.dispose();
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

// OpenAC ProfileFieldsEditorTests.TheAccountsEditorHidesPasswordsAndIsReadOnlyUntilShown.
#[test]
fn the_accounts_editor_hides_passwords_and_is_read_only_until_shown() {
    let mut fixture = EditorFixture::new();
    fixture
        .orchestrator
        .add_server("Coldeve", "play.coldeve.ac", 9000)
        .unwrap();
    fixture
        .orchestrator
        .add_account("Coldeve", "notan3", "p,a\"ss")
        .unwrap();
    fixture.open(LauncherTextEditorKind::Accounts);

    assert!(fixture.editor.is_password_hidden());
    assert_eq!(
        fixture.editor.displayed_text(),
        format!("#Coldeve\nName=notan3,Password={HIDDEN_PASSWORD}\n")
    );

    fixture.editor.show_passwords = true;
    assert_eq!(fixture.editor.displayed_text(), fixture.editor.text());
    fixture
        .editor
        .set_text("#Coldeve\nName=notan3,Password=changed,Profiles=Bots\n");
    fixture.save();

    assert!(!fixture.editor.is_open, "{:?}", fixture.editor.error);
    let store = fixture.reload();
    let account = &store.document().servers[0].accounts[0];
    assert_eq!(account.password, "changed");
    assert_eq!(account.profiles, ["Bots"]);
}

// OpenAC ProfileFieldsEditorTests.RemovingOrRenamingAnAccountWithSettingsAsksFirstAndSavesOnTheSecondSave.
#[test]
fn removing_or_renaming_an_account_with_settings_asks_first_and_saves_on_the_second_save() {
    let mut fixture = EditorFixture::new();
    let core = &fixture.orchestrator;
    core.add_server("Coldeve", "play.coldeve.ac", 9000).unwrap();
    core.add_account("Coldeve", "notan3", "secret").unwrap();
    core.add_account("Coldeve", "empty", "secret").unwrap();
    core.add_character("Coldeve", "notan3", "Festivus", Some("0x5005FBB5"))
        .unwrap();
    core.update_account_plugins("Coldeve", "notan3", &["a.one".into(), "b.two".into()])
        .unwrap();
    let before = std::fs::read_to_string(fixture.profile_path()).unwrap();
    fixture.open(LauncherTextEditorKind::Accounts);
    fixture.editor.show_passwords = true;

    fixture
        .editor
        .set_text("#Coldeve\nName=notan5,Password=secret\n");
    fixture.save();

    assert!(fixture.editor.is_open);
    assert_eq!(
        std::fs::read_to_string(fixture.profile_path()).unwrap(),
        before
    );
    let warning = fixture.editor.removal_warning().unwrap().to_owned();
    assert!(warning.contains("notan3 on Coldeve (1 character, 2 plugins)"));
    assert!(!warning.contains("empty"));
    assert_eq!(fixture.editor.save_text(), "Remove and save");

    fixture.save();

    assert!(!fixture.editor.is_open, "{:?}", fixture.editor.error);
    let store = fixture.reload();
    assert_eq!(store.document().servers[0].accounts.len(), 1);
    assert_eq!(store.document().servers[0].accounts[0].account, "notan5");
}

// OpenAC ProfileFieldsEditorTests.EditingTheTextAgainAsksAgain.
#[test]
fn editing_the_text_again_asks_again() {
    let mut fixture = EditorFixture::new();
    let core = &fixture.orchestrator;
    core.add_server("Coldeve", "play.coldeve.ac", 9000).unwrap();
    core.add_account("Coldeve", "notan3", "secret").unwrap();
    core.add_character("Coldeve", "notan3", "Festivus", Some("0x5005FBB5"))
        .unwrap();
    fixture.open(LauncherTextEditorKind::Accounts);
    fixture.editor.show_passwords = true;
    fixture.editor.set_text("#Coldeve\n");
    fixture.save();

    fixture.editor.set_text("#Coldeve\nName=other\n");
    assert_eq!(fixture.editor.removal_warning(), None);
    fixture.save();

    assert!(fixture.editor.is_open);
    let store = fixture.reload();
    assert_eq!(store.document().servers[0].accounts[0].account, "notan3");
}

// OpenAC ProfileFieldsEditorTests.RemovingAnAccountWithNothingSavedOnItSavesAtOnce.
#[test]
fn removing_an_account_with_nothing_saved_on_it_saves_at_once() {
    let mut fixture = EditorFixture::new();
    let core = &fixture.orchestrator;
    core.add_server("Coldeve", "play.coldeve.ac", 9000).unwrap();
    core.add_account("Coldeve", "empty", "secret").unwrap();
    fixture.open(LauncherTextEditorKind::Accounts);
    fixture.editor.show_passwords = true;
    fixture.editor.set_text("#Coldeve\n");
    fixture.save();

    assert!(!fixture.editor.is_open, "{:?}", fixture.editor.error);
    assert!(fixture.reload().document().servers[0].accounts.is_empty());
}

// OpenAC ProfileFieldsEditorTests.AWrongLineKeepsTheEditorOpenWithItsNumberAndSavesNothing.
#[test]
fn a_wrong_line_keeps_the_editor_open_with_its_number_and_saves_nothing() {
    let mut fixture = EditorFixture::new();
    fixture
        .orchestrator
        .add_server("Coldeve", "play.coldeve.ac", 9000)
        .unwrap();
    let before = std::fs::read_to_string(fixture.profile_path()).unwrap();
    fixture.open(LauncherTextEditorKind::LogonCommands);
    fixture.editor.set_text("#Coldeve\n##nobody\n/vt start");
    fixture.save();

    assert!(fixture.editor.is_open);
    assert_eq!(
        fixture.editor.error.as_deref(),
        Some("Line 2: server 'Coldeve' has no account 'nobody'. Add it in Edit accounts first.")
    );
    assert_eq!(
        std::fs::read_to_string(fixture.profile_path()).unwrap(),
        before
    );
}

// OpenAC ProfileFieldsEditorTests.LogonCommandsSaveToTheAccountAndTheSummaryCountsWhatIsTyped.
#[test]
fn logon_commands_save_to_the_account_and_the_summary_counts_what_is_typed() {
    let mut fixture = EditorFixture::new();
    let core = &fixture.orchestrator;
    core.add_server("Coldeve", "play.coldeve.ac", 9000).unwrap();
    core.add_account("Coldeve", "notan3", "secret").unwrap();
    core.add_account("Coldeve", "notan", "secret").unwrap();
    fixture.open(LauncherTextEditorKind::LogonCommands);
    assert_eq!(
        fixture.editor.summary(),
        "1 server, 2 accounts · an account with no lines runs no commands"
    );

    fixture
        .editor
        .set_text("#Coldeve\n##notan3\n/vt meta load bore\n/vt start\n##notan\n");
    fixture.save();

    assert!(!fixture.editor.is_open, "{:?}", fixture.editor.error);
    let store = fixture.reload();
    assert_eq!(
        store.document().servers[0].accounts[0].login_commands,
        ["/vt meta load bore", "/vt start"]
    );
    assert!(
        store.document().servers[0].accounts[1]
            .login_commands
            .is_empty()
    );
}

// OpenAC ProfileFieldsEditorTests.ServerAddressPersistsAndReopensWithPort.
#[test]
fn server_address_persists_and_reopens_with_port() {
    for (address, host, port) in [
        ("example.com:9000", "example.com", 9000),
        (" 127.0.0.1:1 ", "127.0.0.1", 1),
        ("[::1]:65535", "::1", 65535),
    ] {
        let mut fixture = EditorFixture::new();
        fixture.open(LauncherTextEditorKind::Servers);
        fixture.editor.rows[0].name = "Test server".into();
        fixture.editor.rows[0].value = address.into();
        fixture.save();
        assert!(
            !fixture.editor.is_open,
            "{address}: {:?}",
            fixture.editor.error
        );
        let store = fixture.reload();
        assert_eq!(store.document().servers[0].host, host);
        assert_eq!(store.document().servers[0].port, port);
        fixture.open(LauncherTextEditorKind::Servers);
        assert_eq!(fixture.editor.rows[0].value, address.trim());
    }
}

// OpenAC ProfileFieldsEditorTests.InvalidEndpointKeepsEditorOpenAndLeavesSavedProfileUnchanged.
#[test]
fn invalid_endpoint_keeps_editor_open_and_leaves_saved_profile_unchanged() {
    for address in [
        "localhost",
        "localhost:0",
        "localhost:65536",
        "localhost:abc",
        "https://example.com:9000",
        "example.com/path:9000",
        "::1:9000",
        "[example.com]:9000",
    ] {
        let mut fixture = EditorFixture::new();
        fixture
            .orchestrator
            .add_server("Existing", "localhost", 9000)
            .unwrap();
        let original = std::fs::read_to_string(fixture.profile_path()).unwrap();
        fixture.open(LauncherTextEditorKind::Servers);
        fixture.editor.rows[0].value = address.into();
        fixture.save();
        assert!(fixture.editor.is_open, "{address} was accepted");
        assert!(fixture.editor.error.is_some());
        assert_eq!(
            std::fs::read_to_string(fixture.profile_path()).unwrap(),
            original
        );
    }
}

// OpenAC ProfileFieldsEditorTests.ExistingCharactersSurviveEndpointEditsAndConcurrentChangesAreRejected.
#[test]
fn existing_characters_survive_endpoint_edits_and_concurrent_changes_are_rejected() {
    let mut fixture = EditorFixture::new();
    fixture
        .orchestrator
        .add_server("Existing", "localhost", 9000)
        .unwrap();
    fixture
        .orchestrator
        .add_account("Existing", "Player", "secret")
        .unwrap();
    fixture
        .orchestrator
        .add_character("Existing", "Player", "Character", Some("0x50000001"))
        .unwrap();
    fixture.open(LauncherTextEditorKind::Servers);
    fixture.editor.rows[0].value = "game.example.com:9001".into();
    fixture.save();
    assert!(!fixture.editor.is_open, "{:?}", fixture.editor.error);
    let store = fixture.reload();
    assert_eq!(
        store.document().servers[0].accounts[0].characters[0].name,
        "Character"
    );

    fixture.open(LauncherTextEditorKind::Servers);
    fixture
        .orchestrator
        .add_server("Added elsewhere", "localhost", 9002)
        .unwrap();
    fixture.editor.rows[0].value = "other.example.com:9003".into();
    fixture.save();
    assert!(fixture.editor.is_open);
    assert!(fixture.editor.error.as_deref().unwrap().contains("changed"));
    let store = fixture.reload();
    assert_eq!(store.document().servers.len(), 2);
    assert_eq!(store.document().servers[0].host, "game.example.com");
}

// OpenAC ProfileFieldsEditorTests.SavingUnchangedEditorsPreservesWhitespaceAndSavedCharacters.
#[test]
fn saving_unchanged_editors_preserves_whitespace_and_saved_characters() {
    let mut fixture = EditorFixture::new();
    fixture
        .orchestrator
        .add_server(" server ", "localhost", 9000)
        .unwrap();
    fixture
        .orchestrator
        .add_account(" server ", " account ", "secret")
        .unwrap();
    fixture
        .orchestrator
        .add_character(" server ", " account ", "Character", Some("0x50000001"))
        .unwrap();
    for kind in [
        LauncherTextEditorKind::Accounts,
        LauncherTextEditorKind::LogonCommands,
        LauncherTextEditorKind::Servers,
    ] {
        fixture.open(kind);
        fixture.save();
        assert!(
            !fixture.editor.is_open,
            "{kind:?}: {:?}",
            fixture.editor.error
        );
    }
    let store = fixture.reload();
    let server = &store.document().servers[0];
    assert_eq!(server.name, " server ");
    assert_eq!(server.accounts[0].account, " account ");
    assert_eq!(server.accounts[0].characters[0].name, "Character");
}

// --- Plugins dialogs: account and character lists ------------------------------------------

/// Refuses every request: the checklist tests never touch the network.
struct NoNetwork;

impl HttpTransport for NoNetwork {
    fn get(
        &self,
        _request: &HttpRequest,
        _cancel: &CancelToken,
    ) -> Result<HttpResponse, HttpError> {
        Err(HttpError::Failed("no network in tests".into()))
    }
}

/// OpenAC `PluginChecklistFixture`.
struct ChecklistFixture {
    root: PathBuf,
    paths: PathSet,
}

impl ChecklistFixture {
    fn new() -> Self {
        let root = std::env::temp_dir()
            .join("openac-plugin-checklist-tests")
            .join(ac_launcher_core::ids::unique_hex32());
        let paths =
            PathSet::from_members(root.join("config"), root.join("data"), root.join("cache"));
        Self { root, paths }
    }

    fn write_manifest(&self, id: &str) {
        let directory = self.paths.plugins_directory().join(id);
        std::fs::create_dir_all(&directory).unwrap();
        std::fs::write(
            directory.join("plugin.json"),
            format!(
                "{{\n  \"id\": \"{id}\",\n  \"displayName\": \"{id}\",\n  \"version\": \"0.1.0\",\n  \
                 \"entry\": \"{id}.wasm\",\n  \"apiVersion\": \"1.0\",\n  \"minHostVersion\": \"0.1.0\"\n}}\n"
            ),
        )
        .unwrap();
        std::fs::write(directory.join(format!("{id}.wasm")), []).unwrap();
    }

    fn window(&self, fake: &Arc<Fake>) -> LauncherWindow {
        let mut parts = parts(fake.clone(), Arc::new(InlineSpawner));
        parts.plugins = Some(Arc::new(PluginComposition::new(
            &self.paths,
            WebUrl::parse("https://example.test/plugins.json").unwrap(),
            Arc::new(NoNetwork),
            LauncherLog::open(&self.paths),
        )));
        let mut window = LauncherWindow::new(parts);
        window.initialize();
        window
    }
}

impl Drop for ChecklistFixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

/// One account with `characters` and `account_plugins`, tagged "Bots"
/// (OpenAC `AccountWithPlugins`).
fn account_with_plugins(
    account_plugins: &[&str],
    characters: Vec<LauncherCharacterSnapshot>,
) -> Arc<Fake> {
    let fake = Fake::default();
    let mut account = account("Local ACE", "testaccount", characters);
    account.plugins = account_plugins.iter().map(|id| (*id).to_owned()).collect();
    account.profiles = vec!["Bots".into()];
    *fake.servers.lock().unwrap() = vec![server("Local ACE", vec![account])];
    Arc::new(fake)
}

/// A character with its own plugin list.
fn character_with_own(name: &str, mode: LaunchMode, plugins: &[&str]) -> LauncherCharacterSnapshot {
    let mut character = character("Local ACE", "testaccount", name, mode, plugins);
    character.id = Some("0x5000000A".into());
    character.has_own_plugins = true;
    character
}

/// The row picks `name` and its Options › Plugins for this character opens.
fn open_character(window: &mut LauncherWindow, name: &str) {
    window.set_row_character("testaccount", "Local ACE", name);
    window.open_character_plugins("testaccount", "Local ACE");
}

// OpenAC LauncherWindowViewModelTests.ChecklistOffersOnlyHostCompatiblePluginsAndKeepsMissingIdsChecked:
// every plugin runs in every launch mode (manifests name no hosts), so every
// installed plugin is offered; missing ids stay checked.
#[test]
fn checklist_offers_every_installed_plugin_and_keeps_missing_ids_checked() {
    let fixture = ChecklistFixture::new();
    fixture.write_manifest("plugin.first");
    fixture.write_manifest("plugin.second");
    let fake = account_with_plugins(
        &[],
        vec![character_with_own(
            "+Acdream",
            LaunchMode::Headless,
            &["plugin.first", "ghost.missing"],
        )],
    );
    let mut window = fixture.window(&fake);
    open_character(&mut window, "+Acdream");
    assert!(window.is_row_options_open);
    assert_eq!(window.row_options_choices.len(), 3);
    let choice = |window: &LauncherWindow, id: &str| {
        window
            .row_options_choices
            .iter()
            .find(|choice| choice.id == id)
            .cloned()
            .unwrap()
    };
    let installed = choice(&window, "plugin.first");
    assert!(!installed.is_missing && installed.is_checked);
    let offered = choice(&window, "plugin.second");
    assert!(!offered.is_missing && !offered.is_checked);
    let missing = choice(&window, "ghost.missing");
    assert!(missing.is_missing && missing.is_checked);

    window
        .row_options_choices
        .iter_mut()
        .find(|choice| choice.id == "ghost.missing")
        .unwrap()
        .is_checked = false;
    window.character_uses_account_plugins = false;
    window.save_row_options();
    let updates = fake.settings_updates.lock().unwrap();
    assert_eq!(
        updates.last().unwrap().4,
        Some(vec!["plugin.first".to_owned()])
    );
}

// OpenAC LauncherWindowViewModelTests.DuplicateConfiguredPluginIdsProduceOneChecklistEntry.
#[test]
fn duplicate_configured_plugin_ids_produce_one_checklist_entry() {
    let fixture = ChecklistFixture::new();
    let fake = account_with_plugins(
        &[],
        vec![character_with_own(
            "+Acdream",
            LaunchMode::Headless,
            &["ghost.missing", "ghost.missing"],
        )],
    );
    let mut window = fixture.window(&fake);
    open_character(&mut window, "+Acdream");
    assert_eq!(window.row_options_choices.len(), 1);
    assert_eq!(window.row_options_choices[0].id, "ghost.missing");
    window.character_uses_account_plugins = false;
    window.save_row_options();
    assert_eq!(
        fake.settings_updates.lock().unwrap().last().unwrap().4,
        Some(vec!["ghost.missing".to_owned()])
    );
}

// OpenAC LauncherWindowViewModelTests.AnInstalledButWrongHostConfiguredIdIsLabeledNotMissing:
// with no hosts there is no wrong host; an installed id is a working choice
// in every mode.
#[test]
fn an_installed_configured_id_is_a_working_choice_in_every_mode() {
    for mode in [LaunchMode::Headless, LaunchMode::Gui, LaunchMode::GuiSelect] {
        let fixture = ChecklistFixture::new();
        fixture.write_manifest("plugin.any");
        let fake = account_with_plugins(
            &[],
            vec![character_with_own("+Acdream", mode, &["plugin.any"])],
        );
        let mut window = fixture.window(&fake);
        open_character(&mut window, "+Acdream");
        assert_eq!(window.row_options_choices.len(), 1, "{mode:?}");
        assert_eq!(window.row_options_choices[0].display_name, "plugin.any");
        assert!(!window.row_options_choices[0].is_missing);
    }
}

// OpenAC LauncherWindowViewModelTests.EditPluginsOnAnAccountOffersEveryPluginAndSavesTheListInItsOrder.
#[test]
fn edit_plugins_on_an_account_offers_every_plugin_and_saves_the_list_in_its_order() {
    let fixture = ChecklistFixture::new();
    fixture.write_manifest("plugin.both");
    fixture.write_manifest("plugin.graphical");
    let fake = account_with_plugins(&["plugin.both", "other.kept"], Vec::new());
    let mut window = fixture.window(&fake);

    let (account, server) = (
        window.accounts[0].account_name.clone(),
        window.accounts[0].server_name.clone(),
    );
    window.open_account_plugins(&account, &server);

    assert!(window.is_row_options_open);
    assert!(window.is_account_row_options());
    assert!(!window.show_character_plugin_choice());
    assert_eq!(window.row_options_title(), "Plugins for testaccount");
    let choices: Vec<(String, String, bool)> = window
        .row_options_choices
        .iter()
        .map(|choice| {
            (
                choice.id.clone(),
                choice.display_name.clone(),
                choice.is_checked,
            )
        })
        .collect();
    assert_eq!(
        choices,
        [
            ("plugin.both".into(), "plugin.both".into(), true),
            ("plugin.graphical".into(), "plugin.graphical".into(), false),
            ("other.kept".into(), "other.kept (missing)".into(), true),
        ]
    );

    window
        .row_options_choices
        .iter_mut()
        .find(|choice| choice.id == "plugin.graphical")
        .unwrap()
        .is_checked = true;
    window.save_row_options();

    let updates = fake.account_plugin_updates.lock().unwrap();
    assert_eq!(updates.len(), 1);
    assert_eq!(
        updates[0].2,
        ["plugin.both", "other.kept", "plugin.graphical"]
    );
    assert!(!window.is_row_options_open);
}

// OpenAC LauncherWindowViewModelTests.AccountPluginOptionsApplyToTheSelectedServerRow.
#[test]
fn account_plugin_options_apply_to_the_selected_server_row() {
    let fixture = ChecklistFixture::new();
    fixture.write_manifest("plugin.both");
    let fake = account_with_plugins(&[], Vec::new());
    {
        let mut servers = fake.servers.lock().unwrap();
        servers.push(server(
            "Second",
            vec![account("Second", "testaccount", Vec::new())],
        ));
    }
    let mut window = fixture.window(&fake);
    assert_eq!(window.accounts.len(), 1);
    let rows: Vec<&str> = window.accounts[0]
        .servers
        .iter()
        .map(|row| row.server_name.as_str())
        .collect();
    assert_eq!(rows, ["Local ACE", "Second"]);

    window.open_account_plugins("testaccount", "Second");
    assert!(window.is_account_row_options());
    window
        .row_options_choices
        .iter_mut()
        .find(|choice| choice.id == "plugin.both")
        .unwrap()
        .is_checked = true;
    window.save_row_options();

    let updates = fake.account_plugin_updates.lock().unwrap();
    assert_eq!(updates.len(), 1);
    assert_eq!(updates[0].0, "Second");
    assert_eq!(updates[0].1, "testaccount");
}

// OpenAC LauncherWindowViewModelTests.ACharacterFollowsItsAccountUntilGivenItsOwnList.
#[test]
fn a_character_follows_its_account_until_given_its_own_list() {
    let fixture = ChecklistFixture::new();
    fixture.write_manifest("plugin.both");
    let mut hero = character(
        "Local ACE",
        "testaccount",
        "+Hero",
        LaunchMode::Gui,
        &["plugin.both"],
    );
    hero.has_own_plugins = false;
    let fake = account_with_plugins(&["plugin.both"], vec![hero]);
    let mut window = fixture.window(&fake);
    assert_eq!(
        window
            .row("testaccount", "Local ACE")
            .unwrap()
            .character_name(),
        None
    );

    open_character(&mut window, "+Hero");
    assert!(window.character_uses_account_plugins);
    assert!(!window.can_edit_plugin_choices());
    window.save_row_options();
    assert_eq!(fake.settings_updates.lock().unwrap()[0].4, None);

    window.open_character_plugins("testaccount", "Local ACE");
    window.character_uses_account_plugins = false;
    assert!(window.can_edit_plugin_choices());
    window.row_options_choices[0].is_checked = false;
    window.save_row_options();
    assert_eq!(
        fake.settings_updates.lock().unwrap().last().unwrap().4,
        Some(Vec::new())
    );
}

// OpenAC LauncherWindowViewModelTests.RemoveCharacterAsksFirstAndTheOptionsButtonNamesItsRow.
#[test]
fn remove_character_asks_first_and_the_options_button_names_its_row() {
    let fake = account_with_plugins(
        &[],
        vec![character(
            "Local ACE",
            "testaccount",
            "+Hero",
            LaunchMode::Gui,
            &[],
        )],
    );
    let mut window = create_initialized(&fake);
    assert_eq!(
        window
            .row("testaccount", "Local ACE")
            .unwrap()
            .options_automation_name(),
        "Options for testaccount on Local ACE"
    );
    window.set_row_character("testaccount", "Local ACE", "+Hero");
    assert_eq!(
        window
            .row("testaccount", "Local ACE")
            .unwrap()
            .options_automation_name(),
        "Options for +Hero on Local ACE"
    );

    window.request_remove_character("testaccount", "Local ACE");

    assert!(fake.removed_character.lock().unwrap().is_none());
    assert_eq!(window.remove_character_title(), "Remove +Hero?");
    window.confirm_remove_character();
    assert_eq!(
        fake.removed_character.lock().unwrap().clone(),
        Some((
            "Local ACE".to_owned(),
            "testaccount".to_owned(),
            "+Hero".to_owned()
        ))
    );
}

// OpenAC LauncherWindowViewModelTests.AccountGroupsShowTheirProfilesPluginsAndCharacterCount.
#[test]
fn account_groups_show_their_profiles_plugins_and_character_count() {
    let fixture = ChecklistFixture::new();
    let fake = account_with_plugins(
        &["plugin.both", "other"],
        vec![character(
            "Local ACE",
            "testaccount",
            "+Hero",
            LaunchMode::Gui,
            &[],
        )],
    );
    let window = fixture.window(&fake);
    let group = &window.accounts[0];
    assert_eq!(group.profiles, ["Bots"]);
    assert_eq!(group.plugins_summary, "plugin.both · other");
    assert_eq!(group.subtitle, "Local ACE · 1 character");
}

/// OpenAC `ProfiledOrchestrator`.
fn profiled() -> Arc<Fake> {
    let tagged = |server_name: &str, name: &str, profiles: &[&str]| {
        let mut account = account(
            server_name,
            name,
            vec![character(
                server_name,
                name,
                "A character",
                LaunchMode::Gui,
                &[],
            )],
        );
        account.profiles = profiles.iter().map(|tag| (*tag).to_owned()).collect();
        account
    };
    let fake = Fake::default();
    *fake.servers.lock().unwrap() = vec![
        server(
            "One",
            vec![
                tagged("One", "Alice", &["Main"]),
                tagged("One", "Bob", &["bots"]),
            ],
        ),
        server("Two", vec![tagged("Two", "Alice", &["Bots", "Mules"])]),
    ];
    *fake.session.lock().unwrap() = Some(session(LauncherActivityState::Exited));
    Arc::new(fake)
}

// OpenAC LauncherWindowViewModelTests.EachAccountShowsItsConfiguredServersInProfileOrder.
#[test]
fn each_account_shows_its_configured_servers_in_profile_order() {
    let window = create_initialized(&profiled());
    let groups: Vec<(&str, &str)> = window
        .accounts
        .iter()
        .map(|group| (group.server_name.as_str(), group.account_name.as_str()))
        .collect();
    assert_eq!(groups, [("One", "Alice"), ("One", "Bob")]);
    let rows = |index: usize| -> Vec<String> {
        window.accounts[index]
            .servers
            .iter()
            .map(|row| row.server_name.clone())
            .collect()
    };
    assert_eq!(rows(0), ["One", "Two"]);
    assert_eq!(rows(1), ["One"]);
    let chips: Vec<&str> = window
        .profile_filters
        .iter()
        .map(|chip| chip.label.as_str())
        .collect();
    assert_eq!(chips, ["All", "Main", "bots", "Mules"]);
    assert!(window.profile_filters[0].is_selected);
}

// OpenAC LauncherWindowViewModelTests.AProfileChipShowsItsAccountsAndPlaySelectedStartsOnlyThoseTicked.
#[test]
fn a_profile_chip_shows_its_accounts_and_play_selected_starts_only_those_ticked() {
    let fake = profiled();
    let mut window = create_started(&fake);
    for (account, server) in [("Alice", "One"), ("Alice", "Two"), ("Bob", "One")] {
        window.set_row_checked(account, server, true);
    }
    assert_eq!(window.play_checked_text(), "Play selected (3)");

    window.set_profile_filter(Some("bots"));

    assert_eq!(window.profile_filter.as_deref(), Some("bots"));
    let visible: Vec<bool> = window
        .accounts
        .iter()
        .map(|group| group.is_visible)
        .collect();
    assert_eq!(visible, [true, true]);
    let alice: Vec<bool> = window.accounts[0]
        .servers
        .iter()
        .map(|row| row.is_visible)
        .collect();
    assert_eq!(alice, [false, true]);
    assert_eq!(window.play_checked_text(), "Play selected (2)");
    window.launch_checked();
    window.poll();
    let requests: Vec<(String, String)> = fake
        .launch_requests
        .lock()
        .unwrap()
        .iter()
        .map(|request| (request.0.clone(), request.1.clone()))
        .collect();
    assert_eq!(
        requests,
        [
            ("Two".to_owned(), "Alice".to_owned()),
            ("One".to_owned(), "Bob".to_owned())
        ]
    );

    window.set_profile_filter(None);
    assert!(window.accounts.iter().all(|group| group.is_visible));
}

// --- Checked accounts remembered -------------------------------------------------------------

struct CheckedFixture {
    root: PathBuf,
}

impl CheckedFixture {
    fn new() -> Self {
        let root = std::env::temp_dir()
            .join("openac-launcher-checks")
            .join(ac_launcher_core::ids::unique_hex32());
        std::fs::create_dir_all(&root).unwrap();
        Self { root }
    }

    fn file_path(&self) -> PathBuf {
        self.root
            .join(ac_launcher_core::profiles::CHECKED_ACCOUNTS_FILE_NAME)
    }

    fn window(&self, fake: &Arc<Fake>) -> LauncherWindow {
        let mut parts = parts(fake.clone(), Arc::new(InlineSpawner));
        parts.checked_accounts = Some(LauncherCheckedAccountStore::new(&self.file_path()));
        let mut window = LauncherWindow::new(parts);
        window.initialize();
        window
    }
}

impl Drop for CheckedFixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

// OpenAC LauncherWindowViewModelTests.CheckedAccountsReopenOnTheirOwnServerWithoutLaunching.
#[test]
fn checked_accounts_reopen_on_their_own_server_without_launching() {
    let selection = CheckedFixture::new();
    {
        let mut window = selection.window(&batch());
        assert!(
            window
                .accounts
                .iter()
                .all(|group| { group.servers.iter().all(|row| !row.is_checked) })
        );
        let (account, server) = (
            window.accounts[0].servers[1].account_name.clone(),
            window.accounts[0].servers[1].server_name.clone(),
        );
        window.set_row_checked(&account, &server, true);
        assert_eq!(window.last_error, None);
    }
    let fake = batch();
    let reopened = selection.window(&fake);
    let checked: Vec<(&str, &str)> = reopened
        .accounts
        .iter()
        .flat_map(|group| group.servers.iter())
        .filter(|row| row.is_checked)
        .map(|row| (row.account_name.as_str(), row.server_name.as_str()))
        .collect();
    assert_eq!(checked, [("Alice", "Two")]);
    assert!(fake.launch_requests.lock().unwrap().is_empty());
}

// OpenAC LauncherWindowViewModelTests.SavingChecksKeepsHiddenSelectionsAndPersistsUnchecking.
#[test]
fn saving_checks_keeps_hidden_selections_and_persists_unchecking() {
    let selection = CheckedFixture::new();
    {
        let mut window = selection.window(&profiled());
        window.set_row_checked("Alice", "One", true);
        window.set_profile_filter(Some("bots"));
        assert!(!window.row("Alice", "One").unwrap().is_visible);
        window.set_row_checked("Alice", "Two", true);
        let mut saved = LauncherCheckedAccountStore::new(&selection.file_path());
        saved.load().unwrap();
        assert!(saved.contains("One", "Alice"));
        assert!(saved.contains("Two", "Alice"));
        window.set_row_checked("Alice", "Two", false);
        assert_eq!(window.last_error, None);
    }
    let reopened = selection.window(&profiled());
    assert!(reopened.row("Alice", "One").unwrap().is_checked);
    assert!(!reopened.row("Alice", "Two").unwrap().is_checked);
}

// OpenAC LauncherWindowViewModelTests.RestoringAndRefreshingChecksDoesNotRewriteSelectionFile.
#[test]
fn restoring_and_refreshing_checks_does_not_rewrite_the_selection_file() {
    let selection = CheckedFixture::new();
    LauncherCheckedAccountStore::new(&selection.file_path())
        .save([ac_launcher_core::profiles::LauncherCheckedAccount::new(
            "Two", "Alice",
        )])
        .unwrap();
    let original = std::fs::read(selection.file_path()).unwrap();
    let fake = batch();
    let mut window = selection.window(&fake);
    window.poll();
    assert_eq!(std::fs::read(selection.file_path()).unwrap(), original);
    assert!(fake.saved_row_selection.lock().unwrap().is_none());
}

// OpenAC LauncherWindowViewModelTests.ACheckedAccountMatchesItsRowWhateverTheCaseOfItsName.
#[test]
fn a_checked_account_matches_its_row_whatever_the_case_of_its_name() {
    let selection = CheckedFixture::new();
    LauncherCheckedAccountStore::new(&selection.file_path())
        .save([ac_launcher_core::profiles::LauncherCheckedAccount::new(
            "Two", "ALICE",
        )])
        .unwrap();
    let window = selection.window(&batch());
    let checked: Vec<(&str, &str)> = window
        .accounts
        .iter()
        .flat_map(|group| group.servers.iter())
        .filter(|row| row.is_checked)
        .map(|row| (row.account_name.as_str(), row.server_name.as_str()))
        .collect();
    assert_eq!(checked, [("Alice", "Two")]);
    let mut store = LauncherCheckedAccountStore::new(&selection.file_path());
    store.load().unwrap();
    assert!(!store.contains("two", "Alice"));
}

// OpenAC LauncherWindowViewModelTests.OneAccountIsOneGroupWhateverTheCaseOfItsNameAndEachEntryKeepsItsRow.
#[test]
fn one_account_is_one_group_whatever_the_case_of_its_name_and_each_entry_keeps_its_row() {
    let fake = Fake::default();
    *fake.servers.lock().unwrap() = vec![
        batch_server("One", &["Alice", "ALICE", "Bob"]),
        batch_server("Two", &["alice"]),
    ];
    *fake.session.lock().unwrap() = Some(session(LauncherActivityState::Exited));
    let fake = Arc::new(fake);
    let mut window = create_initialized(&fake);
    let groups: Vec<&str> = window
        .accounts
        .iter()
        .map(|group| group.account_name.as_str())
        .collect();
    assert_eq!(groups, ["Alice", "Bob"]);
    let rows = |window: &LauncherWindow| -> Vec<(String, String)> {
        window.accounts[0]
            .servers
            .iter()
            .map(|row| (row.server_name.clone(), row.account_name.clone()))
            .collect()
    };
    let expected = [
        ("One".to_owned(), "Alice".to_owned()),
        ("One".to_owned(), "ALICE".to_owned()),
        ("Two".to_owned(), "alice".to_owned()),
    ];
    assert_eq!(rows(&window), expected);
    // Each spelling finds its own row; another finds the first.
    assert_eq!(window.row("ALICE", "One").unwrap().account_name, "ALICE");
    assert_eq!(window.row("alice", "One").unwrap().account_name, "Alice");
    assert_eq!(window.row("ALICE", "Two").unwrap().account_name, "alice");
    fake.raise_state_changed();
    window.poll();
    assert_eq!(rows(&window), expected);
}

// OpenAC LauncherWindowViewModelTests.InvalidSelectionFileIsReportedAndPreserved.
#[test]
fn an_invalid_selection_file_is_reported_and_preserved() {
    let selection = CheckedFixture::new();
    std::fs::write(
        selection.file_path(),
        "[{\"server\":null,\"account\":\"Alice\"}]",
    )
    .unwrap();
    let original = std::fs::read_to_string(selection.file_path()).unwrap();
    let mut window = selection.window(&batch());
    assert!(window.last_error.is_some());
    let (account, server) = (
        window.accounts[0].servers[0].account_name.clone(),
        window.accounts[0].servers[0].server_name.clone(),
    );
    window.set_row_checked(&account, &server, true);
    assert!(
        window
            .last_error
            .as_deref()
            .unwrap()
            .contains("selections cannot be saved")
    );
    assert_eq!(
        std::fs::read_to_string(selection.file_path()).unwrap(),
        original
    );
}

// --- Install dialog ---------------------------------------------------------------------

fn declaration(permission: Permission, note: &str) -> PermissionDeclaration {
    PermissionDeclaration {
        permission,
        note: note.into(),
        hosts: Vec::new(),
    }
}

fn request() -> InstallDialogRequest {
    InstallDialogRequest {
        local_zip: None,
        repo: "owner/plugin".into(),
        plugin_id: "owner.plugin".into(),
        display_name: "Plugin".into(),
        is_listed: true,
        is_update: false,
        offered_version: Some("1.0.0".into()),
        pinned_tag: Some("v1.0.0".into()),
        channel: None,
        characters: vec![
            PluginCharacterOption {
                server_name: "Local ACE".into(),
                account_name: "testaccount".into(),
                character_name: "+One".into(),
                display_name: "+One (testaccount@Local ACE)".into(),
            },
            PluginCharacterOption {
                server_name: "Local ACE".into(),
                account_name: "testaccount".into(),
                character_name: "+Two".into(),
                display_name: "+Two (testaccount@Local ACE)".into(),
            },
        ],
        permissions: Some(Vec::new()),
        installed_permissions: None,
        affected_characters: Vec::new(),
    }
}

// OpenAC PluginInstallDialogViewModelTests.DefaultChoiceIsNoneAndConfirmWritesNoCharacterList,
// ChoosingAllEnablesEveryOfferedCharacter, ChoosingSpecificCharactersEnablesOnlyThoseChecked,
// ChoosingWithNothingCheckedWritesNoCharacterList.
#[test]
fn the_enable_choice_decides_which_characters_are_written() {
    let mut dialog = PluginInstallDialog::default();
    dialog.open(request());
    assert_eq!(dialog.choice, PluginEnableChoice::None);
    assert!(dialog.chosen_characters().is_empty());
    dialog.choice = PluginEnableChoice::All;
    assert_eq!(dialog.chosen_characters().len(), 2);
    dialog.choice = PluginEnableChoice::Choose;
    assert!(dialog.chosen_characters().is_empty());
    dialog.characters[1].is_checked = true;
    let chosen = dialog.chosen_characters();
    assert_eq!(chosen.len(), 1);
    assert_eq!(chosen[0].character_name, "+Two");
}

// OpenAC PluginInstallDialogViewModelTests.ConfirmIsEnabledAssoonAsAListedInstallOpensWithNoTick,
// ConfirmIsEnabledAssoonAsAnUnlistedInstallOpensWithNoTick, APrereleaseOfferedVersionAddsAPreReleaseSentenceToTheNotice,
// AStableOfferedVersionAddsNoPreReleaseSentence, UpdateOpensWithNoEnableChoiceOffered.
#[test]
fn the_notice_names_unlisted_and_prerelease_offers() {
    let mut dialog = PluginInstallDialog::default();
    dialog.open(request());
    assert!(dialog.can_confirm());
    assert!(
        !dialog
            .warning_text()
            .contains("not on the OpenAC plugin list")
    );
    assert!(!dialog.warning_text().contains("pre-release"));
    let mut unlisted = request();
    unlisted.is_listed = false;
    unlisted.offered_version = Some("1.1.0-beta.1".into());
    dialog.open(unlisted);
    assert!(dialog.can_confirm());
    assert!(
        dialog
            .warning_text()
            .contains("This plugin is not on the OpenAC plugin list.")
    );
    assert!(
        dialog
            .warning_text()
            .ends_with("This version is a pre-release.")
    );
    let mut update = request();
    update.is_update = true;
    dialog.open(update);
    assert!(!dialog.show_enable_choice());
    assert_eq!(dialog.busy_label(), "Updating…");
}

// OpenAC PluginInstallDialogViewModelTests.CapabilityDisplayOrderFollowsTheVocabularyNotTheManifestArray,
// EveryDeclaredCapabilityReachesTheDialogWithItsNoteIntact, DeclaringNoCapabilitiesShowsNoChips:
// the chips are the enforced permissions (vocabulary order: chat, network,
// listen, profiles, clipboard).
#[test]
fn permission_display_order_follows_the_vocabulary_not_the_manifest_array() {
    let mut dialog = PluginInstallDialog::default();
    let mut with_permissions = request();
    with_permissions.permissions = Some(vec![
        declaration(Permission::Clipboard, "Copies item stats."),
        declaration(Permission::Network, "Talks to a server."),
        declaration(Permission::Chat, "Reads chat."),
    ]);
    dialog.open(with_permissions);
    assert_eq!(dialog.permissions_heading(), "This plugin may:");
    let labels: Vec<&str> = dialog
        .permissions
        .iter()
        .map(|chip| chip.label.as_str())
        .collect();
    assert_eq!(labels, ["use chat", "use the network", "set the clipboard"]);
    assert_eq!(dialog.permissions[1].note, "Talks to a server.");
    assert_eq!(dialog.no_permissions_text(), None);
    dialog.open(request());
    assert!(dialog.permissions.is_empty());
    assert!(dialog.no_permissions_text().is_some());
}

#[test]
fn a_network_permission_chip_names_its_hosts() {
    let mut dialog = PluginInstallDialog::default();
    let mut with_hosts = request();
    with_hosts.permissions = Some(vec![PermissionDeclaration {
        permission: Permission::Network,
        note: "Downloads profiles.".into(),
        hosts: vec!["github.com".into(), "example.org".into()],
    }]);
    dialog.open(with_hosts);
    assert_eq!(
        dialog.permissions[0].label,
        "use the network (github.com, example.org)"
    );
    assert_eq!(
        dialog.permissions[0].automation_name,
        "use the network (github.com, example.org): Downloads profiles."
    );
}

// OpenAC PluginInstallDialogViewModelTests.UnknownCapabilitiesShowLoadingAndDisableInstallUntilTheFetchCompletes,
// UnknownCapabilitiesLeaveInstallDisabledWithAReasonWhenTheFetchFails, KnownCapabilitiesNeverShowLoadingEvenWhenEmpty.
#[test]
fn unknown_permissions_show_loading_and_disable_install_until_the_fetch_completes() {
    let mut dialog = PluginInstallDialog::default();
    let mut unknown = request();
    unknown.permissions = None;
    let generation = dialog.open(unknown.clone());
    assert!(dialog.is_loading_permissions);
    assert!(!dialog.can_confirm());
    dialog.permissions_loaded(generation, Ok(vec![declaration(Permission::Network, "x")]));
    assert!(!dialog.is_loading_permissions);
    assert!(dialog.can_confirm());
    assert_eq!(dialog.displayed_permissions.len(), 1);

    let generation = dialog.open(unknown);
    dialog.permissions_loaded(
        generation,
        Err("GitHub is rate limiting; try later.".into()),
    );
    assert!(!dialog.can_confirm());
    assert_eq!(
        dialog.permissions_load_error.as_deref(),
        Some("GitHub is rate limiting; try later.")
    );

    dialog.open(request());
    assert!(!dialog.is_loading_permissions);
}

// OpenAC PluginInstallDialogViewModelTests.UpdateWithIdenticalReorderedCapabilitiesNeedsNoFreshConsent,
// UpdateAddingACapabilityMarksItNewAndUsesTheConsentLabel, UpdateWithARewordedNoteCountsAsChangedButNotAsNew,
// AffectedCharactersTextNamesEveryoneCurrentlyEnabled,
// ConfirmingAnUpdateWithChangedCapabilitiesStripsEveryAffectedCharacterByDefault,
// ConfirmingWithKeepEnabledLeavesEveryAffectedCharacterAlone,
// ChangedCapabilitiesWithNoAffectedCharactersNeverCallsDisable.
#[test]
fn an_update_that_changes_permissions_asks_for_fresh_consent() {
    let network = declaration(Permission::Network, "Talks to a server.");
    let chat = declaration(Permission::Chat, "Reads chat.");
    let mut update = request();
    update.is_update = true;
    update.installed_permissions = Some(vec![chat.clone(), network.clone()]);
    update.permissions = Some(vec![network.clone(), chat.clone()]);
    update.affected_characters = vec!["+One (testaccount@Local ACE)".into()];
    let mut dialog = PluginInstallDialog::default();
    dialog.open(update.clone());
    assert!(!dialog.permissions_changed);
    assert_eq!(dialog.confirm_label(), "Install");
    assert!(!dialog.disables_after_update());

    let listen = declaration(Permission::Listen, "Serves a phone remote.");
    update.permissions = Some(vec![network.clone(), chat.clone(), listen]);
    dialog.open(update.clone());
    assert!(dialog.permissions_changed);
    assert_eq!(dialog.confirm_label(), "Update and allow");
    assert!(
        dialog
            .permissions
            .iter()
            .any(|chip| chip.is_new && chip.label == "accept network connections (new)")
    );
    assert!(dialog.show_keep_enabled_choice());
    assert_eq!(
        dialog.affected_characters_text(),
        "Currently enabled for: +One (testaccount@Local ACE)."
    );
    assert!(dialog.disables_after_update());
    dialog.keep_enabled = true;
    assert!(!dialog.disables_after_update());

    update.permissions = Some(vec![
        declaration(Permission::Network, "Talks to two servers."),
        chat.clone(),
    ]);
    dialog.open(update.clone());
    assert!(dialog.permissions_changed);
    assert!(dialog.permissions.iter().all(|chip| !chip.is_new));

    update.affected_characters.clear();
    dialog.open(update);
    assert!(!dialog.show_keep_enabled_choice());
    assert!(!dialog.disables_after_update());
}

// --- Plugins tab: the Check pipeline and Discover ---------------------------------------

const LIST_URI: &str = "https://example.test/plugins.json";
const HELLO_REPO: &str = "shaneedwards/openac-plugin-hello";

/// OpenAC `PluginPanelFixture`.
struct PanelFixture {
    root: common::TempDir,
    server: common::fixtures::FakeServer,
}

impl PanelFixture {
    fn new() -> Self {
        Self {
            root: common::TempDir::new("window-plugins"),
            server: common::fixtures::FakeServer::new(),
        }
    }

    fn paths(&self) -> PathSet {
        self.root.path_set()
    }

    fn write_manifest(&self, id: &str, version: &str) {
        let directory = self.paths().plugins_directory().join(id);
        std::fs::create_dir_all(&directory).unwrap();
        std::fs::write(directory.join("plugin.json"), manifest_json(id, version)).unwrap();
        std::fs::write(directory.join(format!("{id}.wasm")), []).unwrap();
    }

    fn add_record(&self, id: &str, version: &str) {
        self.add_records(&[(id, version)]);
    }

    /// Records each `(id, version)` as installed from the test repository.
    fn add_records(&self, plugins: &[(&str, &str)]) {
        let path = self
            .paths()
            .data_directory
            .join("app")
            .join("plugins-installed.json");
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        let records = plugins
            .iter()
            .map(|(id, version)| {
                format!(
                    r#"{{ "id": "{id}", "repo": "{HELLO_REPO}",
                      "source": "listed", "version": "{version}", "tag": "wasm-v{version}",
                      "zipSha256": "{}", "installedAt": "2026-01-01T00:00:00Z", "pending": null }}"#,
                    "a".repeat(64)
                )
            })
            .collect::<Vec<_>>()
            .join(",");
        std::fs::write(
            &path,
            format!(r#"{{ "schemaVersion": 1, "plugins": [{records}] }}"#),
        )
        .unwrap();
    }

    fn list(&self, plugins: &[&str], blocked: &str) {
        let entries = plugins
            .iter()
            .map(|id| {
                format!(
                    r#"{{ "id": "{id}", "name": "{id}", "author": "Shane Edwards",
                      "description": "Test fixture.", "repo": "{HELLO_REPO}",
                      "client": "openac-rust" }}"#
                )
            })
            .collect::<Vec<_>>()
            .join(",");
        self.server.add(
            LIST_URI,
            format!(r#"{{ "schemaVersion": 1, "plugins": [{entries}], "blocked": [{blocked}] }}"#)
                .as_bytes(),
        );
    }

    /// Publishes `id` at `version` as the repository's newest WebAssembly
    /// release (`wasm-v<version>` on its releases feed).
    fn release(&self, id: &str, version: &str) {
        let tag = ac_launcher_core::plugins::release_tag(version);
        let tagged =
            ac_launcher_core::plugins::github::tagged_asset(HELLO_REPO, &tag, "plugin.json")
                .unwrap()
                .to_string();
        let feed = ac_launcher_core::plugins::github::releases_feed(HELLO_REPO)
            .unwrap()
            .to_string();
        self.server.add(
            &feed,
            format!(
                r#"<?xml version="1.0" encoding="UTF-8"?>
<feed xmlns="http://www.w3.org/2005/Atom"><entry><link rel="alternate" href="https://github.com/{HELLO_REPO}/releases/tag/{tag}"/></entry></feed>"#
            )
            .as_bytes(),
        );
        self.server
            .add(&tagged, manifest_json(id, version).as_bytes());
    }

    /// Publishes each `(tag, id, version)` on the repository's releases feed,
    /// the tag serving `id`'s manifest at `version`.
    fn releases(&self, releases: &[(&str, &str, &str)]) {
        let links: String = releases
            .iter()
            .map(|(tag, ..)| {
                format!(
                    r#"<entry><link rel="alternate" href="https://github.com/{HELLO_REPO}/releases/tag/{tag}"/></entry>"#
                )
            })
            .collect();
        let feed = ac_launcher_core::plugins::github::releases_feed(HELLO_REPO)
            .unwrap()
            .to_string();
        self.server.add(
            &feed,
            format!(
                r#"<?xml version="1.0" encoding="UTF-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">{links}</feed>"#
            )
            .as_bytes(),
        );
        for (tag, id, version) in releases {
            let tagged =
                ac_launcher_core::plugins::github::tagged_asset(HELLO_REPO, tag, "plugin.json")
                    .unwrap()
                    .to_string();
            self.server
                .add(&tagged, manifest_json(id, version).as_bytes());
        }
    }

    /// Publishes the zip and its checksum for `id` at `version` under `tag`,
    /// so an install of that release goes through.
    fn release_zip(&self, tag: &str, id: &str, version: &str) {
        let zip_name = format!("{id}-{version}.zip");
        let wasm = format!("{id}.wasm");
        let manifest = manifest_json(id, version);
        let zip_bytes = common::fixtures::create_zip(
            &[
                ("plugin.json", manifest.as_bytes(), None),
                (wasm.as_str(), b"binary".as_slice(), None),
            ],
            common::fixtures::Compression::Stored,
        );
        let asset = |name: &str| {
            ac_launcher_core::plugins::github::tagged_asset(HELLO_REPO, tag, name)
                .unwrap()
                .to_string()
        };
        self.server.add(
            &asset(&format!("{zip_name}.sha256")),
            format!(
                "{}  {zip_name}",
                ac_launcher_core::integrity::sha256_hex(&zip_bytes)
            )
            .as_bytes(),
        );
        self.server.add(&asset(&zip_name), &zip_bytes);
    }

    fn window(&self) -> LauncherWindow {
        self.window_with(Arc::new(Fake::default()))
    }

    fn window_with(&self, fake: Arc<Fake>) -> LauncherWindow {
        let mut parts = parts(fake, Arc::new(InlineSpawner));
        parts.plugins = Some(Arc::new(PluginComposition::new(
            &self.paths(),
            WebUrl::parse(LIST_URI).unwrap(),
            self.server.transport(),
            LauncherLog::open(&self.paths()),
        )));
        let mut window = LauncherWindow::new(parts);
        window.initialize();
        window
    }
}

fn manifest_json(id: &str, version: &str) -> String {
    format!(
        r#"{{
  "id": "{id}",
  "displayName": "{id}",
  "version": "{version}",
  "entry": "{id}.wasm",
  "apiVersion": "1.0",
  "minHostVersion": "0.1.0"
}}"#
    )
}

fn check(window: &mut LauncherWindow) {
    window.plugins_check_now();
    window.poll();
}

// OpenAC LauncherWindowViewModelTests.RateLimitedListSkipsPerPluginRequests.
#[test]
fn rate_limited_list_skips_per_plugin_requests() {
    let fixture = PanelFixture::new();
    fixture.write_manifest("edwards.hello", "0.1.0");
    fixture.add_record("edwards.hello", "0.1.0");
    fixture
        .server
        .add_route(LIST_URI, common::fixtures::Route::Status(429));
    let mut window = fixture.window();
    check(&mut window);
    assert!(window.plugins.is_rate_limited);
    assert_eq!(
        window.plugins.error.as_deref(),
        Some("GitHub is rate limiting; try later.")
    );
    assert_eq!(fixture.server.requests().len(), 1);
}

// OpenAC LauncherWindowViewModelTests.OfflineWithNoCacheShowsAnError.
#[test]
fn offline_with_no_cache_shows_an_error() {
    let fixture = PanelFixture::new();
    fixture
        .server
        .fallback(|_, _| common::fixtures::Route::Fail);
    let mut window = fixture.window();
    check(&mut window);
    assert!(!window.plugins.is_rate_limited);
    assert!(window.plugins.list_age.is_none());
    assert_eq!(
        window.plugins.error.as_deref(),
        Some("Could not reach the plugin list.")
    );
}

fn classic_migration_dialog(profiles: PathBuf) -> ClassicMigrationDialog {
    ClassicMigrationDialog::with_finders(profiles, Arc::new(|| None), Arc::new(|| None))
}

// OpenAC LauncherWindowViewModelTests.TheClassicImportDialogIsAModalOfTheWindow.
#[test]
fn the_classic_import_dialog_is_a_modal_of_the_window() {
    let mut window = LauncherWindow::new(LauncherWindowParts {
        classic_migration: Some(classic_migration_dialog(
            std::env::temp_dir().join("openac-window-import-unused"),
        )),
        ..parts(Arc::new(Fake::default()), Arc::new(InlineSpawner))
    });
    window.initialize();

    window.open_classic_migration();
    assert!(window.is_modal_open());

    window.close_active_modal();
    assert!(!window.classic_migration.as_ref().unwrap().is_open);
    assert!(!window.is_modal_open());
}

// OpenAC LauncherWindowViewModelTests.TheClassicImportIsOfferedOnlyWhenMossTankIsInstalled:
// the import is only offered once MossTank is installed, and follows the
// installed list as it changes.
#[test]
fn the_classic_import_is_offered_only_when_mosstank_is_installed() {
    let fixture = PanelFixture::new();
    fixture.write_manifest("someone.other", "1.0.0");
    fixture.list(&[], "");
    let mut window = fixture.window();
    assert!(!window.show_classic_migration(), "no dialog, no button");
    window.classic_migration = Some(classic_migration_dialog(
        fixture.paths().vtank_profiles_directory(),
    ));

    check(&mut window);
    assert!(!window.show_classic_migration());

    fixture.write_manifest("acdream.mosstank", "1.0.0");
    check(&mut window);
    assert!(window.show_classic_migration());

    std::fs::remove_dir_all(fixture.paths().plugins_directory().join("acdream.mosstank")).unwrap();
    check(&mut window);
    assert!(!window.show_classic_migration());
}

// OpenAC ClassicMigrationViewModelTests.PreviewListsTheFilesAndImportCopiesThem, through the
// window's worker.
#[test]
fn the_window_runs_the_classic_import_on_its_worker() {
    let fixture = PanelFixture::new();
    let vtank = fixture.paths().root_directory().join("classic-vtank");
    std::fs::create_dir_all(&vtank).unwrap();
    std::fs::write(vtank.join("Hunt.met"), "meta").unwrap();
    let profiles = fixture.paths().vtank_profiles_directory();
    let spawner = Arc::new(QueueSpawner::default());
    let mut window = LauncherWindow::new(LauncherWindowParts {
        classic_migration: Some(classic_migration_dialog(profiles.clone())),
        ..parts(Arc::new(Fake::default()), spawner.clone())
    });
    window.initialize();
    window.open_classic_migration();
    window.classic_migration.as_mut().unwrap().virindi_tank_path = vtank.display().to_string();

    window.run_classic_migration(true);
    assert!(window.classic_migration.as_ref().unwrap().is_busy());
    window.close_active_modal();
    assert!(
        window.classic_migration.as_ref().unwrap().is_open,
        "a running import keeps the dialog open"
    );
    spawner.run_all();
    window.poll();

    let dialog = window.classic_migration.as_ref().unwrap();
    assert!(!dialog.is_busy());
    assert!(
        dialog
            .status
            .as_deref()
            .unwrap()
            .starts_with("Copied 1 file(s)")
    );
    assert_eq!(
        std::fs::read_to_string(profiles.join("mosstank").join("metas").join("Hunt.met")).unwrap(),
        "meta"
    );
}

// OpenAC LauncherWindowViewModelTests.CacheFallbackShowsTheListAge.
#[test]
fn cache_fallback_shows_the_list_age() {
    let fixture = PanelFixture::new();
    let cache = fixture.paths().cache_directory.join("plugins.json");
    std::fs::create_dir_all(cache.parent().unwrap()).unwrap();
    std::fs::write(&cache, r#"{ "schemaVersion": 1, "plugins": [] }"#).unwrap();
    std::fs::File::options()
        .write(true)
        .open(&cache)
        .unwrap()
        .set_modified(SystemTime::now() - Duration::from_secs(3 * 3600))
        .unwrap();
    fixture
        .server
        .fallback(|_, _| common::fixtures::Route::Fail);
    let mut window = fixture.window();
    check(&mut window);
    let age = window.plugins.list_age_text(SystemTime::now()).unwrap();
    assert!(age.contains("3 hour(s)"), "{age}");
    assert_eq!(window.plugins.error, None);
}

// OpenAC LauncherWindowViewModelTests.UpdateIsOfferedOnlyForLauncherManagedPluginsButRemoveIsOfferedForDirectToo
// and UpdateWithheldReasonExplainsWhyNoUpdateIsOffered.
#[test]
fn update_is_offered_only_for_launcher_managed_plugins_but_remove_is_offered_for_direct_too() {
    let fixture = PanelFixture::new();
    fixture.write_manifest("edwards.managed", "0.1.0");
    fixture.add_record("edwards.managed", "0.1.0");
    fixture.write_manifest("someone.manual", "1.0.0");
    fixture.list(&[], "");
    fixture.release("edwards.managed", "0.2.0");
    let mut window = fixture.window();
    check(&mut window);
    let managed = window
        .plugins
        .installed
        .iter()
        .find(|row| row.info.id == "edwards.managed")
        .unwrap();
    assert!(managed.can_remove);
    assert_eq!(managed.source_badge, "Listed");
    // Without an installed client the newer release is withheld, not offered.
    assert!(managed.update.is_none());
    assert_eq!(
        managed.update_withheld_reason.as_deref(),
        Some("client not installed")
    );
    let direct = window
        .plugins
        .installed
        .iter()
        .find(|row| row.info.id == "someone.manual")
        .unwrap();
    assert!(direct.can_remove);
    assert!(direct.update.is_none());
    assert!(!direct.show_beta_toggle);
    assert_eq!(direct.source_badge, "Direct install");
}

// A repository publishing several plugins: each is checked against its own
// releases, never another plugin's newer one.
#[test]
fn the_update_check_offers_only_the_installed_plugins_own_release() {
    let withheld = |releases: &[(&str, &str, &str)]| {
        let fixture = PanelFixture::new();
        fixture.write_manifest("edwards.managed", "0.1.0");
        fixture.add_record("edwards.managed", "0.1.0");
        fixture.list(&[], "");
        fixture.releases(releases);
        let mut window = fixture.window();
        check(&mut window);
        window
            .plugins
            .installed
            .iter()
            .find(|row| row.info.id == "edwards.managed")
            .unwrap()
            .update_withheld_reason
            .clone()
    };
    // Another plugin's newer release, in either tag form, is no update.
    assert_eq!(withheld(&[("wasm-v0.2.0", "edwards.other", "0.2.0")]), None);
    assert_eq!(
        withheld(&[("wasm-edwards.other-v0.2.0", "edwards.other", "0.2.0")]),
        None
    );
    // Its own is found (and withheld only for want of a client).
    assert_eq!(
        withheld(&[
            ("wasm-edwards.other-v0.3.0", "edwards.other", "0.3.0"),
            ("wasm-edwards.managed-v0.2.0", "edwards.managed", "0.2.0"),
        ])
        .as_deref(),
        Some("client not installed")
    );
}

#[test]
fn discover_offers_each_plugin_of_one_repository_reading_its_feed_once() {
    let fixture = PanelFixture::new();
    fixture.list(&["edwards.vision", "edwards.navigation"], "");
    fixture.releases(&[
        ("wasm-edwards.vision-v1.2.0", "edwards.vision", "1.2.0"),
        (
            "wasm-edwards.navigation-v0.4.0",
            "edwards.navigation",
            "0.4.0",
        ),
    ]);
    let mut window = fixture.window();
    check(&mut window);
    window.select_plugins_tab();
    window.poll();
    let view = window.plugins.discover_view();
    let mut offered: Vec<(String, Option<String>)> = view
        .iter()
        .map(|&row| {
            let row = &window.plugins.discover[row];
            (
                row.entry.id.clone(),
                row.latest_version().map(str::to_owned),
            )
        })
        .collect();
    offered.sort();
    assert_eq!(
        offered,
        [
            ("edwards.navigation".to_owned(), Some("0.4.0".to_owned())),
            ("edwards.vision".to_owned(), Some("1.2.0".to_owned())),
        ]
    );
    let feed = ac_launcher_core::plugins::github::releases_feed(HELLO_REPO)
        .unwrap()
        .to_string();
    assert_eq!(fixture.server.count(&feed), 1);
}

// A broken index is a publisher's mistake: the player still gets the feed's
// releases, and the launcher log says why the index was ignored.
#[test]
fn a_malformed_plugin_index_is_logged_and_the_feed_still_offers() {
    let fixture = PanelFixture::new();
    fixture.list(&["edwards.vision"], "");
    fixture.releases(&[("wasm-edwards.vision-v1.2.0", "edwards.vision", "1.2.0")]);
    let index = ac_launcher_core::plugins::github::tagged_asset(
        HELLO_REPO,
        ac_launcher_core::plugins::PLUGIN_INDEX_TAG,
        ac_launcher_core::plugins::PLUGIN_INDEX_ASSET,
    )
    .unwrap()
    .to_string();
    fixture.server.add(&index, b"{ not json");
    let mut window = fixture.window();
    check(&mut window);
    window.select_plugins_tab();
    window.poll();
    assert_eq!(window.plugins.discover_view().len(), 1);
    let log = std::fs::read_to_string(fixture.paths().launcher_log_file()).unwrap();
    assert!(
        log.contains(&format!(
            "Plugin index of {HELLO_REPO} ignored: The plugin index is invalid"
        )),
        "{log}"
    );
}

// Two installed plugins of one repository share its index: a broken one is
// logged once a check, not once for each of them.
#[test]
fn a_malformed_plugin_index_is_logged_once_a_check_for_a_repository() {
    let fixture = PanelFixture::new();
    fixture.list(&[], "");
    fixture.write_manifest("edwards.vision", "1.0.0");
    fixture.write_manifest("edwards.navigation", "0.3.0");
    fixture.add_records(&[("edwards.vision", "1.0.0"), ("edwards.navigation", "0.3.0")]);
    fixture.releases(&[
        ("wasm-edwards.vision-v1.2.0", "edwards.vision", "1.2.0"),
        (
            "wasm-edwards.navigation-v0.4.0",
            "edwards.navigation",
            "0.4.0",
        ),
    ]);
    let index = ac_launcher_core::plugins::github::tagged_asset(
        HELLO_REPO,
        ac_launcher_core::plugins::PLUGIN_INDEX_TAG,
        ac_launcher_core::plugins::PLUGIN_INDEX_ASSET,
    )
    .unwrap()
    .to_string();
    fixture.server.add(&index, b"{ not json");
    let mut window = fixture.window();
    check(&mut window);
    let log = std::fs::read_to_string(fixture.paths().launcher_log_file()).unwrap();
    assert_eq!(
        log.matches(&format!("Plugin index of {HELLO_REPO} ignored"))
            .count(),
        1,
        "{log}"
    );
}

// The backend writes to the launcher's own log, never a second instance on
// the same file that would roll it over on its own count.
#[test]
fn the_plugin_backend_writes_to_the_log_it_is_given() {
    let fixture = PanelFixture::new();
    let given = fixture.paths().root_directory().join("given.log");
    let composition = PluginComposition::new(
        &fixture.paths(),
        WebUrl::parse(LIST_URI).unwrap(),
        fixture.server.transport(),
        LauncherLog::at(
            &given,
            1024 * 1024,
            ac_host::local_time::LocalZone::fixed(0),
        ),
    );
    composition.log_index_warning_text(HELLO_REPO, Some("a test warning"));
    let log = std::fs::read_to_string(&given).unwrap();
    assert!(
        log.contains(&format!(
            "Plugin index of {HELLO_REPO} ignored: a test warning"
        )),
        "{log}"
    );
    assert!(!fixture.paths().launcher_log_file().exists());
}

// OpenAC LauncherWindowViewModelTests.DiscoverHidesABlockedListedPluginAndMakesNoDetailRequestForIt.
#[test]
fn discover_hides_a_blocked_listed_plugin_and_makes_no_detail_request_for_it() {
    let fixture = PanelFixture::new();
    fixture.list(
        &["edwards.discoverable"],
        r#"{ "id": "edwards.discoverable", "versions": ["*"], "reason": "test" }"#,
    );
    let mut window = fixture.window();
    check(&mut window);
    window.select_plugins_tab();
    window.poll();
    assert!(window.plugins.discover_view().is_empty());
    assert_eq!(fixture.server.requests().len(), 1);
}

// OpenAC LauncherWindowViewModelTests.DiscoverKeepsAPluginBlockedOnAnOlderVersionUntilItsLatestClears.
#[test]
fn discover_keeps_a_plugin_blocked_on_an_older_version_until_its_latest_clears() {
    let fixture = PanelFixture::new();
    fixture.list(
        &["edwards.discoverable"],
        r#"{ "id": "edwards.discoverable", "versions": ["0.1.0"], "reason": "test" }"#,
    );
    fixture.release("edwards.discoverable", "0.2.0");
    let mut window = fixture.window();
    check(&mut window);
    // Hidden until its release resolves, like any listing.
    assert!(window.plugins.discover_view().is_empty());
    window.select_plugins_tab();
    window.poll();
    let view = window.plugins.discover_view();
    assert_eq!(view.len(), 1);
    assert_eq!(
        window.plugins.discover[view[0]].latest_version(),
        Some("0.2.0")
    );
}

// OpenAC LauncherWindowViewModelTests.DiscoverHidesAPluginOnceItsLatestVersionIsFoundBlocked.
#[test]
fn discover_hides_a_plugin_once_its_latest_version_is_found_blocked() {
    let fixture = PanelFixture::new();
    fixture.list(
        &["edwards.discoverable"],
        r#"{ "id": "edwards.discoverable", "versions": ["0.2.0"], "reason": "test" }"#,
    );
    fixture.release("edwards.discoverable", "0.2.0");
    let mut window = fixture.window();
    check(&mut window);
    window.select_plugins_tab();
    window.poll();
    assert!(window.plugins.discover_view().is_empty());
}

// The list's `client` field: an entry without it (OpenAC's C# build) is
// never offered or checked here; this client's entry of the same id is.
#[test]
fn discover_offers_only_this_clients_entries_of_the_shared_list() {
    let fixture = PanelFixture::new();
    let csharp = format!(
        r#"{{ "id": "edwards.csharp", "name": "C#", "author": "A", "description": "D",
              "repo": "{HELLO_REPO}" }}"#
    );
    fixture.server.add(
        LIST_URI,
        format!(r#"{{ "schemaVersion": 1, "plugins": [{csharp}] }}"#).as_bytes(),
    );
    let mut window = fixture.window();
    check(&mut window);
    window.select_plugins_tab();
    window.poll();
    assert!(window.plugins.discover.is_empty());
    // Only the list itself was fetched: no release check for a C# entry.
    assert_eq!(fixture.server.requests().len(), 1);

    let fixture = PanelFixture::new();
    fixture.list(&["edwards.discoverable"], "");
    fixture.release("edwards.discoverable", "0.2.0");
    let mut window = fixture.window();
    check(&mut window);
    window.select_plugins_tab();
    window.poll();
    assert_eq!(window.plugins.discover_view().len(), 1);
}

// OpenAC LauncherWindowViewModelTests.A404OnPluginJsonHidesTheDiscoverRow.
#[test]
fn a_404_on_plugin_json_hides_the_discover_row() {
    let fixture = PanelFixture::new();
    fixture.list(&["edwards.discoverable"], "");
    let mut window = fixture.window();
    check(&mut window);
    window.select_plugins_tab();
    window.poll();
    assert!(window.plugins.discover_view().is_empty());
    assert!(window.plugins.discover_status_line.is_none());
    assert!(!window.plugins.is_discover_checking);
}

// OpenAC LauncherWindowViewModelTests.SearchingDiscoverKeepsOnlyTheMatchingPluginsAndSaysSoWhenNoneMatch.
#[test]
fn searching_discover_keeps_only_the_matching_plugins() {
    let fixture = PanelFixture::new();
    fixture.list(&["edwards.discoverable"], "");
    fixture.release("edwards.discoverable", "0.2.0");
    let mut window = fixture.window();
    check(&mut window);
    window.select_plugins_tab();
    window.poll();
    assert_eq!(window.plugins.discover_view().len(), 1);
    window.plugins.discover_filter = "nothing like it".into();
    assert!(window.plugins.discover_view().is_empty());
    assert_eq!(
        window.plugins.discover_empty_text(),
        "No listed plugin matches that search."
    );
    window.plugins.discover_filter = "DISCOVERABLE".into();
    assert_eq!(window.plugins.discover_view().len(), 1);
}

// OpenAC LauncherWindowViewModelTests.AWidePluginsPanelPutsDiscoverBesideInstalledAndANarrowOneStacksThem.
#[test]
fn a_wide_plugins_panel_puts_discover_beside_installed() {
    let mut window = create_initialized(&batch());
    window.plugins.set_panel_width(819.0);
    assert!(!window.plugins.is_side_by_side);
    window.plugins.set_panel_width(820.0);
    assert!(window.plugins.is_side_by_side);
}

// OpenAC LauncherPluginsViewModel.cs PluginMonogram.From and FormatAge.
#[test]
fn monograms_and_ages_read_as_openac_writes_them() {
    use ac_launcher_core::window::plugins_panel::{format_age, monogram};
    assert_eq!(monogram("BuffBot"), "BB");
    assert_eq!(monogram("magic tools"), "MT");
    assert_eq!(monogram("hello"), "H");
    assert_eq!(monogram("  "), "?");
    assert_eq!(format_age(Duration::from_secs(10)), "1 minute(s)");
    assert_eq!(format_age(Duration::from_secs(3 * 3600)), "3 hour(s)");
    assert_eq!(format_age(Duration::from_secs(2 * 86_400)), "2 day(s)");
}

// --- Content setup and the client beside the launcher -----------------------------------

#[test]
fn retail_content_is_the_chosen_dat_directory_while_its_files_are_there() {
    use ac_launcher_core::installation::{
        DatDirectoryLocator, InstallRecordVerificationState, REQUIRED_DAT_FILE_NAMES,
        RetailContentStore,
    };
    let root = common::TempDir::new("retail-content");
    let dats = root.join("dats");
    std::fs::create_dir_all(&dats).unwrap();
    for name in REQUIRED_DAT_FILE_NAMES {
        std::fs::write(dats.join(name), b"dat").unwrap();
    }
    let store = RetailContentStore::with_locator(
        &root.path_set(),
        DatDirectoryLocator::new(true, Some(Vec::new())),
    );
    assert_eq!(
        store.load_and_verify().state,
        InstallRecordVerificationState::Missing
    );
    assert!(store.save(&root.join("nowhere")).is_err());
    let record = store.save(&dats).unwrap();
    assert_eq!(record.dat_directory, dats);
    let verified = store.load_and_verify();
    assert_eq!(verified.state, InstallRecordVerificationState::Verified);
    assert_eq!(verified.record.unwrap().dat_directory, dats);
    std::fs::remove_file(dats.join("client_cell_1.dat")).unwrap();
    let broken = store.load_and_verify();
    assert_eq!(broken.state, InstallRecordVerificationState::Invalid);
    assert!(
        broken.status.contains("client_cell_1.dat"),
        "{}",
        broken.status
    );
}

#[test]
fn without_an_installed_version_the_client_beside_the_launcher_is_used() {
    use ac_launcher_core::updates::{ClientVersionStore, suffix_for_current_os};
    let root = common::TempDir::new("side-by-side");
    let store = Arc::new(ClientVersionStore::new(&root.path_set()));
    let beside = root.join("bin");
    let executables =
        LauncherExecutableSet::from_current_version_store_or_directory(Arc::clone(&store), &beside);
    assert!(executables.paths().is_err(), "no client anywhere");
    std::fs::create_dir_all(&beside).unwrap();
    let name = format!("openac{}", suffix_for_current_os());
    std::fs::write(beside.join(&name), b"client").unwrap();
    let paths = executables.paths().unwrap();
    assert_eq!(paths.graphical_host_path, beside.join(&name));
    assert_eq!(paths.working_directory.as_deref(), Some(beside.as_path()));
}

// --- Add from URL, Remove, Show beta plugins ----------------------------------------------

// OpenAC LauncherWindowViewModelTests.AddFromUrlForAnAlreadyInstalledRepoShowsAMessageWithNoExtraRequest
// and AddFromUrlMatchesAnAlreadyInstalledRepoCaseInsensitively. A repository
// can publish several plugins, so it is read before "already installed" is
// said: one of its other plugins may not be.
#[test]
fn add_from_url_for_an_already_installed_plugin_shows_a_message() {
    let fixture = PanelFixture::new();
    fixture.write_manifest("edwards.hello", "0.1.0");
    fixture.add_record("edwards.hello", "0.1.0");
    // GitHub answers a repository in any case; the record matches ignoring it.
    let typed = "ShaneEdwards/OpenAC-Plugin-Hello";
    let feed = ac_launcher_core::plugins::github::releases_feed(typed)
        .unwrap()
        .to_string();
    fixture.server.add(
        &feed,
        format!(
            r#"<?xml version="1.0" encoding="UTF-8"?>
<feed xmlns="http://www.w3.org/2005/Atom"><entry><link rel="alternate" href="https://github.com/{HELLO_REPO}/releases/tag/wasm-v0.2.0"/></entry></feed>"#
        )
        .as_bytes(),
    );
    let tagged =
        ac_launcher_core::plugins::github::tagged_asset(typed, "wasm-v0.2.0", "plugin.json")
            .unwrap()
            .to_string();
    fixture
        .server
        .add(&tagged, manifest_json("edwards.hello", "0.2.0").as_bytes());
    let mut window = fixture.window();
    window.plugins.add_from_url_text = format!("https://github.com/{typed}");
    window.add_from_url();
    window.poll();
    assert_eq!(
        window.plugins.status_text.as_deref(),
        Some("edwards.hello is already installed.")
    );
    assert!(window.plugins.add_from_url_text.is_empty());
    assert!(!window.plugins.install_dialog.is_open);
}

#[test]
fn add_from_url_asks_which_plugin_when_a_repository_publishes_several() {
    let fixture = PanelFixture::new();
    fixture.list(&[], "");
    fixture.releases(&[
        ("wasm-edwards.vision-v1.2.0", "edwards.vision", "1.2.0"),
        (
            "wasm-edwards.navigation-v0.4.0",
            "edwards.navigation",
            "0.4.0",
        ),
    ]);
    let mut window = fixture.window();
    check(&mut window);
    window.plugins.add_from_url_text = format!("https://github.com/{HELLO_REPO}");
    window.add_from_url();
    window.poll();
    assert!(!window.plugins.install_dialog.is_open);
    let choices: Vec<(String, String)> = window
        .plugins
        .add_from_url_choices
        .iter()
        .map(|choice| (choice.display_name.clone(), choice.version.clone()))
        .collect();
    assert_eq!(
        choices,
        [
            ("edwards.navigation".to_owned(), "0.4.0".to_owned()),
            ("edwards.vision".to_owned(), "1.2.0".to_owned()),
        ]
    );
    window.choose_add_from_url(1);
    window.poll();
    assert!(window.plugins.add_from_url_choices.is_empty());
    let dialog = &window.plugins.install_dialog;
    assert!(dialog.is_open);
    assert_eq!(dialog.display_name(), "edwards.vision");
    assert_eq!(
        dialog.request.as_ref().unwrap().pinned_tag.as_deref(),
        Some("wasm-edwards.vision-v1.2.0")
    );
    // Cancelling the picker offers nothing.
    window.close_install_dialog();
    window.plugins.add_from_url_text = format!("https://github.com/{HELLO_REPO}");
    window.add_from_url();
    window.poll();
    assert_eq!(window.plugins.add_from_url_choices.len(), 2);
    window.cancel_add_from_url_choice();
    assert!(window.plugins.add_from_url_choices.is_empty());
    assert!(!window.plugins.install_dialog.is_open);
}

/// A window whose Add from URL found three plugins of the test repository,
/// none installed, each release installable.
fn add_from_url_with_three_plugins(fixture: &PanelFixture) -> LauncherWindow {
    fixture.list(&[], "");
    let releases = [
        ("wasm-edwards.vision-v1.2.0", "edwards.vision", "1.2.0"),
        (
            "wasm-edwards.navigation-v0.4.0",
            "edwards.navigation",
            "0.4.0",
        ),
        ("wasm-edwards.macro-v2.0.0", "edwards.macro", "2.0.0"),
    ];
    fixture.releases(&releases);
    for (tag, id, version) in releases {
        fixture.release_zip(tag, id, version);
    }
    let mut window = fixture.window();
    check(&mut window);
    window.plugins.add_from_url_text = format!("https://github.com/{HELLO_REPO}");
    window.add_from_url();
    window.poll();
    window
}

fn choice_names(window: &LauncherWindow) -> Vec<String> {
    window
        .plugins
        .add_from_url_choices
        .iter()
        .map(|choice| choice.display_name.clone())
        .collect()
}

// Adding one plugin of several brings the pick back with the rest, so the
// player never types the address again.
#[test]
fn add_from_url_asks_again_for_the_rest_after_an_install() {
    let fixture = PanelFixture::new();
    let mut window = add_from_url_with_three_plugins(&fixture);
    assert_eq!(
        choice_names(&window),
        ["edwards.macro", "edwards.navigation", "edwards.vision"]
    );
    window.choose_add_from_url(1);
    assert!(window.plugins.install_dialog.is_open);
    assert!(window.plugins.add_from_url_choices.is_empty());
    window.confirm_install();
    window.poll();
    assert!(
        !window.plugins.install_dialog.is_open,
        "{:?}",
        window.plugins.install_dialog.error
    );
    assert!(
        fixture
            .paths()
            .plugins_directory()
            .join("edwards.navigation")
            .join("plugin.json")
            .is_file()
    );
    assert_eq!(choice_names(&window), ["edwards.macro", "edwards.vision"]);
    // The last one left is still asked about, and Cancel ends it.
    window.choose_add_from_url(0);
    window.confirm_install();
    window.poll();
    assert_eq!(choice_names(&window), ["edwards.vision"]);
    window.cancel_add_from_url_choice();
    assert!(!window.is_modal_open());
}

// Cancelling the install of one goes back to the pick with every plugin
// still not installed, that one included.
#[test]
fn add_from_url_asks_again_after_an_install_is_cancelled() {
    let fixture = PanelFixture::new();
    let mut window = add_from_url_with_three_plugins(&fixture);
    window.choose_add_from_url(2);
    window.close_install_dialog();
    assert_eq!(
        choice_names(&window),
        ["edwards.macro", "edwards.navigation", "edwards.vision"]
    );
    window.close_active_modal();
    assert!(window.plugins.add_from_url_choices.is_empty());
    assert!(!window.is_modal_open());
}

// The pick is a dialog: it holds the window, and Escape cancels it.
#[test]
fn add_from_url_asks_in_a_dialog_that_escape_cancels() {
    let fixture = PanelFixture::new();
    fixture.list(&[], "");
    fixture.releases(&[
        ("wasm-edwards.vision-v1.2.0", "edwards.vision", "1.2.0"),
        (
            "wasm-edwards.navigation-v0.4.0",
            "edwards.navigation",
            "0.4.0",
        ),
    ]);
    let mut window = fixture.window();
    check(&mut window);
    window.plugins.add_from_url_text = format!("https://github.com/{HELLO_REPO}");
    window.add_from_url();
    window.poll();
    assert_eq!(window.plugins.add_from_url_choices.len(), 2);
    assert!(window.is_modal_open());
    window.close_active_modal();
    assert!(window.plugins.add_from_url_choices.is_empty());
    assert!(!window.is_modal_open());
    assert!(!window.plugins.install_dialog.is_open);
}

#[test]
fn add_from_url_offers_the_one_plugin_of_a_repository_not_yet_installed() {
    let fixture = PanelFixture::new();
    fixture.write_manifest("edwards.vision", "1.2.0");
    fixture.add_record("edwards.vision", "1.2.0");
    fixture.list(&[], "");
    fixture.releases(&[
        ("wasm-edwards.vision-v1.2.0", "edwards.vision", "1.2.0"),
        (
            "wasm-edwards.navigation-v0.4.0",
            "edwards.navigation",
            "0.4.0",
        ),
    ]);
    let mut window = fixture.window();
    check(&mut window);
    window.plugins.add_from_url_text = format!("https://github.com/{HELLO_REPO}");
    window.add_from_url();
    window.poll();
    assert!(window.plugins.add_from_url_choices.is_empty());
    let dialog = &window.plugins.install_dialog;
    assert!(dialog.is_open);
    assert_eq!(dialog.display_name(), "edwards.navigation");
}

// OpenAC LauncherPluginsViewModel.AddFromUrlAsync (a URL that is not a repository).
#[test]
fn add_from_url_refuses_something_that_is_not_a_repository_url() {
    let fixture = PanelFixture::new();
    let mut window = fixture.window();
    window.plugins.add_from_url_text = "https://example.com/owner/name".into();
    window.add_from_url();
    assert_eq!(
        window.plugins.error.as_deref(),
        Some("Enter a URL like https://github.com/owner/name.")
    );
    assert!(fixture.server.requests().is_empty());
}

// OpenAC LauncherWindowViewModelTests.AddFromUrlRefusesAPluginBlockedForAllVersions.
#[test]
fn add_from_url_refuses_a_plugin_blocked_for_all_versions() {
    let fixture = PanelFixture::new();
    fixture.list(
        &[],
        r#"{ "id": "edwards.blocked", "versions": ["*"], "reason": "Sends chat spam." }"#,
    );
    fixture.release("edwards.blocked", "0.2.0");
    let mut window = fixture.window();
    check(&mut window);
    window.plugins.add_from_url_text = format!("https://github.com/{HELLO_REPO}");
    window.add_from_url();
    window.poll();
    assert_eq!(
        window.plugins.error.as_deref(),
        Some("'edwards.blocked' is blocked: Sends chat spam.")
    );
    assert!(!window.plugins.install_dialog.is_open);
}

// A blocked plugin of a repository publishing several is never one of the
// choices, not at first and not when the pick comes back.
#[test]
fn add_from_url_never_offers_a_blocked_plugin_among_several() {
    let fixture = PanelFixture::new();
    let releases = [
        ("wasm-edwards.vision-v1.2.0", "edwards.vision", "1.2.0"),
        (
            "wasm-edwards.navigation-v0.4.0",
            "edwards.navigation",
            "0.4.0",
        ),
        ("wasm-edwards.macro-v2.0.0", "edwards.macro", "2.0.0"),
    ];
    fixture.releases(&releases);
    fixture.list(
        &[],
        r#"{ "id": "edwards.macro", "versions": ["*"], "reason": "Sends chat spam." }"#,
    );
    let mut window = fixture.window();
    check(&mut window);
    window.plugins.add_from_url_text = format!("https://github.com/{HELLO_REPO}");
    window.add_from_url();
    window.poll();
    assert_eq!(
        choice_names(&window),
        ["edwards.navigation", "edwards.vision"]
    );
    window.choose_add_from_url(0);
    window.close_install_dialog();
    assert_eq!(
        choice_names(&window),
        ["edwards.navigation", "edwards.vision"]
    );
    window.cancel_add_from_url_choice();
    // With one plugin left that is not blocked, it is offered at once.
    fixture.list(
        &[],
        r#"{ "id": "edwards.macro", "versions": ["*"], "reason": "Sends chat spam." },
           { "id": "edwards.vision", "versions": ["*"], "reason": "Sends chat spam." }"#,
    );
    check(&mut window);
    window.plugins.add_from_url_text = format!("https://github.com/{HELLO_REPO}");
    window.add_from_url();
    window.poll();
    assert!(window.plugins.add_from_url_choices.is_empty());
    assert_eq!(
        window.plugins.install_dialog.display_name(),
        "edwards.navigation"
    );
}

// A plugin blocked after the pick was shown is refused when chosen, and the
// pick comes back without it.
#[test]
fn add_from_url_drops_a_choice_refused_as_blocked() {
    let fixture = PanelFixture::new();
    let mut window = add_from_url_with_three_plugins(&fixture);
    fixture.list(
        &[],
        r#"{ "id": "edwards.macro", "versions": ["*"], "reason": "Sends chat spam." }"#,
    );
    check(&mut window);
    assert_eq!(
        choice_names(&window),
        ["edwards.macro", "edwards.navigation", "edwards.vision"]
    );
    window.choose_add_from_url(0);
    assert_eq!(
        window.plugins.error.as_deref(),
        Some("'edwards.macro' is blocked: Sends chat spam.")
    );
    assert!(!window.plugins.install_dialog.is_open);
    assert_eq!(
        choice_names(&window),
        ["edwards.navigation", "edwards.vision"]
    );
}

// The list names plugins, not repositories: another plugin of a listed
// plugin's repository is still unlisted.
#[test]
fn add_from_url_marks_an_unlisted_plugin_of_a_listed_repository_unlisted() {
    let fixture = PanelFixture::new();
    fixture.write_manifest("edwards.vision", "1.2.0");
    fixture.add_record("edwards.vision", "1.2.0");
    fixture.list(&["edwards.vision"], "");
    fixture.releases(&[
        ("wasm-edwards.vision-v1.2.0", "edwards.vision", "1.2.0"),
        (
            "wasm-edwards.navigation-v0.4.0",
            "edwards.navigation",
            "0.4.0",
        ),
    ]);
    let mut window = fixture.window();
    check(&mut window);
    window.plugins.add_from_url_text = format!("https://github.com/{HELLO_REPO}");
    window.add_from_url();
    window.poll();
    let dialog = &window.plugins.install_dialog;
    assert_eq!(dialog.display_name(), "edwards.navigation");
    assert!(
        dialog
            .warning_text()
            .contains("This plugin is not on the OpenAC plugin list."),
        "{}",
        dialog.warning_text()
    );
}

#[test]
fn add_from_url_logs_a_malformed_plugin_index() {
    let fixture = PanelFixture::new();
    fixture.list(&[], "");
    fixture.releases(&[("wasm-edwards.vision-v1.2.0", "edwards.vision", "1.2.0")]);
    let index = ac_launcher_core::plugins::github::tagged_asset(
        HELLO_REPO,
        ac_launcher_core::plugins::PLUGIN_INDEX_TAG,
        ac_launcher_core::plugins::PLUGIN_INDEX_ASSET,
    )
    .unwrap()
    .to_string();
    fixture.server.add(&index, b"{ not json");
    let mut window = fixture.window();
    window.plugins.add_from_url_text = format!("https://github.com/{HELLO_REPO}");
    window.add_from_url();
    window.poll();
    assert_eq!(
        window.plugins.install_dialog.display_name(),
        "edwards.vision"
    );
    let log = std::fs::read_to_string(fixture.paths().launcher_log_file()).unwrap();
    assert_eq!(
        log.matches(&format!("Plugin index of {HELLO_REPO} ignored:"))
            .count(),
        1,
        "{log}"
    );
}

// OpenAC LauncherWindowViewModelTests.AddFromUrlForAnUnlistedRepoOpensTheInstallDialog (the
// unlisted notice, UnlistedInstallNoticeAddsANotOnTheListLine).
#[test]
fn add_from_url_opens_the_install_dialog_with_the_unlisted_notice() {
    let fixture = PanelFixture::new();
    fixture.list(&[], "");
    fixture.release("edwards.fresh", "0.2.0");
    let mut window = fixture.window();
    check(&mut window);
    window.plugins.add_from_url_text = format!("https://github.com/{HELLO_REPO}");
    window.add_from_url();
    window.poll();
    let dialog = &window.plugins.install_dialog;
    assert!(dialog.is_open);
    assert_eq!(dialog.display_name(), "edwards.fresh");
    assert!(
        dialog
            .warning_text()
            .contains("This plugin is not on the OpenAC plugin list.")
    );
    assert_eq!(
        dialog.request.as_ref().unwrap().pinned_tag.as_deref(),
        Some("wasm-v0.2.0")
    );
}

// OpenAC LauncherWindowViewModelTests.RemovingStripsTheIdFromEveryCharacterThatHadItAndLeavesOtherIds.
#[test]
fn removing_strips_the_id_from_every_character_that_had_it_and_leaves_other_ids() {
    let fixture = PanelFixture::new();
    fixture.write_manifest("edwards.hello", "0.1.0");
    fixture.list(&[], "");
    let fake = Fake::default();
    *fake.servers.lock().unwrap() = vec![server(
        "Local ACE",
        vec![account(
            "Local ACE",
            "testaccount",
            vec![
                character(
                    "Local ACE",
                    "testaccount",
                    "+One",
                    LaunchMode::Gui,
                    &["edwards.hello", "other.kept"],
                ),
                character(
                    "Local ACE",
                    "testaccount",
                    "+Two",
                    LaunchMode::Gui,
                    &["other.kept"],
                ),
            ],
        )],
    )];
    let fake = Arc::new(fake);
    let mut window = fixture.window_with(fake.clone());
    check(&mut window);
    let row = window
        .plugins
        .installed
        .iter()
        .find(|row| row.info.id == "edwards.hello")
        .unwrap();
    assert_eq!(row.source_badge, "Direct install");
    window.open_remove_dialog("edwards.hello");
    assert!(window.plugins.is_remove_dialog_open);
    window.confirm_remove();
    window.poll();
    assert!(!window.plugins.is_remove_dialog_open);
    assert!(
        !fixture
            .paths()
            .plugins_directory()
            .join("edwards.hello")
            .exists()
    );
    let updates = fake.settings_updates.lock().unwrap();
    assert_eq!(updates.len(), 1);
    assert_eq!(updates[0].2, "+One");
    assert_eq!(updates[0].4, Some(vec!["other.kept".to_owned()]));
}

// OpenAC LauncherWindowViewModelTests.ShowBetaPluginsPersistsAcrossAViewModelReload and
// TheChannelPickerShowsForEveryRowWhenShowBetaPluginsIsOnEvenWithNoBetaCandidate.
#[test]
fn show_beta_plugins_persists_across_a_reload_and_shows_the_channel_picker() {
    let fixture = PanelFixture::new();
    fixture.list(&["edwards.discoverable"], "");
    fixture.release("edwards.discoverable", "0.2.0");
    let fake = Arc::new(Fake::default());
    let mut window = fixture.window_with(fake.clone());
    check(&mut window);
    window.select_plugins_tab();
    window.poll();
    window.set_show_beta_plugins(true);
    window.poll();
    assert!(*fake.show_beta_plugins.lock().unwrap());
    let view = window.plugins.discover_view();
    assert_eq!(view.len(), 1);
    assert!(window.plugins.discover[view[0]].show_channel_picker);
    let reloaded = fixture.window_with(fake);
    assert!(reloaded.plugins.show_beta_plugins);
}

// --- The update prompt (OpenAC LauncherUpdateViewModelTests) ----------------------------
//
// OpenAC drives the prompt with a fake updater; here it runs the real
// updater over an in-memory release feed, so a check reads a manifest, an
// install really downloads, verifies and activates a client, and a launcher
// update really stages.

struct UpdateFeed {
    root: common::TempDir,
    server: common::fixtures::FakeServer,
    rid: String,
    launcher_directory: PathBuf,
    versions: Arc<ac_launcher_core::updates::ClientVersionStore>,
}

impl UpdateFeed {
    /// A feed offering release 2.0.0 (client and launcher), or a feed that
    /// fails when `reachable` is false.
    fn new(reachable: bool) -> Self {
        use common::fixtures::{FakeServer, Route, client_zip, launcher_zip};
        let root = common::TempDir::new("window-updates");
        let rid = ac_launcher_core::updates::detect_rid().unwrap();
        let server = FakeServer::new();
        server.add(
            &FakeServer::url("client.zip"),
            &client_zip(&rid, "client-v2"),
        );
        server.add(
            &FakeServer::url("launcher.zip"),
            &launcher_zip(&rid, "launcher-v2"),
        );
        let launcher_directory = root.join("launcher");
        std::fs::create_dir_all(&launcher_directory).unwrap();
        std::fs::write(
            launcher_directory.join(format!(
                "openac-launcher{}",
                ac_launcher_core::updates::suffix_for_rid(&rid)
            )),
            "launcher-v1",
        )
        .unwrap();
        let versions = Arc::new(ac_launcher_core::updates::ClientVersionStore::new(
            &root.path_set(),
        ));
        let feed = Self {
            root,
            server,
            rid,
            launcher_directory,
            versions,
        };
        if reachable {
            feed.reachable();
        } else {
            feed.server
                .add_route(&FakeServer::url("manifest.json"), Route::Fail);
        }
        feed
    }

    /// The feed answers with release 2.0.0.
    fn reachable(&self) {
        use common::fixtures::{FakeServer, client_zip, launcher_zip, manifest};
        let client = client_zip(&self.rid, "client-v2");
        let launcher = launcher_zip(&self.rid, "launcher-v2");
        self.server.add(
            &FakeServer::url("manifest.json"),
            &manifest(
                "2.0.0",
                "1.0.0",
                &self.rid,
                &FakeServer::url("client.zip"),
                &client,
                &FakeServer::url("launcher.zip"),
                &launcher,
            ),
        );
    }

    /// The window over this feed, the launcher at `launcher_version`.
    fn window(
        &self,
        fake: &Arc<Fake>,
        launcher_version: &str,
        apply_launcher_update: Option<ac_launcher_core::window::ApplyLauncherUpdate>,
    ) -> LauncherWindow {
        use ac_launcher_core::updates::{
            LauncherInstallationLayout, LauncherSelfUpdateManager, LauncherUpdater,
            ReleaseManifestClient,
        };
        let source = ReleaseManifestClient::local_update_feed_override(
            self.server.transport(),
            &common::fixtures::FakeServer::url("manifest.json"),
        )
        .unwrap();
        let updater = LauncherUpdater::new(
            Arc::new(source),
            self.server.transport(),
            Arc::clone(&self.versions),
            LauncherVersion::try_parse(launcher_version).unwrap(),
            &self.rid,
            || false,
        )
        .unwrap()
        .with_self_updates(
            Arc::new(LauncherSelfUpdateManager::new(
                &self.root.path_set(),
                self.server.transport(),
            )),
            LauncherInstallationLayout::flat(&self.launcher_directory, &self.rid).unwrap(),
        );
        let mut parts = parts(fake.clone(), Arc::new(InlineSpawner));
        parts.updater = Some(Arc::new(updater));
        parts.apply_launcher_update = apply_launcher_update;
        let mut window = LauncherWindow::new(parts);
        window.initialize();
        window
    }

    fn installed_client(&self) -> Option<String> {
        self.versions
            .load_current_read_only(&self.rid, &CancelToken::none())
            .ok()
            .filter(ClientVersionResolution::is_verified)
            .and_then(|resolution| resolution.version)
            .map(|version| version.value().to_owned())
    }

    fn pending_launcher_update(&self) -> bool {
        self.root
            .join("data")
            .join("app")
            .join("launcher-update")
            .join("pending.json")
            .exists()
    }
}

/// OpenAC `StartupCheckAsync`: the startup content and update checks, with
/// first-run setup (there is no content here) set aside.
fn startup_check(window: &mut LauncherWindow) {
    window.start_background_initialization();
    window.poll();
    window.first_run.close();
}

fn auto_opening(window: &mut LauncherWindow) {
    window.update_prompt.auto_open_discovered_updates = true;
}

// OpenAC LauncherUpdateViewModelTests.BannerModeDefersModalUntilReviewAndClearsAvailabilityAfterInstall.
#[test]
fn banner_mode_defers_modal_until_review_and_clears_availability_after_install() {
    let feed = UpdateFeed::new(true);
    let mut window = feed.window(&batch(), "2.0.0", None);
    startup_check(&mut window);
    assert!(window.update_prompt.is_client_update_available());
    assert!(!window.update_prompt.is_open);
    window.review_update();
    assert!(window.update_prompt.is_open);
    window.install_update();
    window.poll();
    assert!(!window.update_prompt.is_client_update_available());
    assert!(!window.update_prompt.is_open);
    assert_eq!(feed.installed_client().as_deref(), Some("2.0.0"));
}

// OpenAC LauncherUpdateViewModelTests.SuccessfulManualRetryReplacesPreviousFailureStatus.
#[test]
fn successful_manual_retry_replaces_previous_failure_status() {
    let feed = UpdateFeed::new(false);
    let mut window = feed.window(&batch(), "2.0.0", None);
    startup_check(&mut window);
    assert!(
        window.update_prompt.status.contains("could not"),
        "{}",
        window.update_prompt.status
    );
    feed.reachable();
    window.check_for_updates();
    window.poll();
    assert!(window.update_prompt.startup_check_succeeded);
    assert!(!window.update_prompt.status.contains("could not"));
}

// OpenAC LauncherUpdateViewModelTests.NothingOutOfDateNeverShowsTheDialog.
#[test]
fn nothing_out_of_date_never_shows_the_dialog() {
    let feed = UpdateFeed::new(true);
    // Bring the client to the release first.
    let mut window = feed.window(&batch(), "2.0.0", None);
    startup_check(&mut window);
    window.review_update();
    window.install_update();
    window.poll();
    assert_eq!(feed.installed_client().as_deref(), Some("2.0.0"));

    let mut window = feed.window(&batch(), "2.0.0", None);
    auto_opening(&mut window);
    startup_check(&mut window);

    assert_eq!(window.update_prompt.status, "OpenAC is up to date.");
    assert!(!window.update_prompt.is_open);
    assert!(!feed.pending_launcher_update());
}

// OpenAC LauncherUpdateViewModelTests.AClientUpdateAsksOnceAndInstallsOnUpdate.
#[test]
fn a_client_update_asks_once_and_installs_on_update() {
    let feed = UpdateFeed::new(true);
    let mut window = feed.window(&batch(), "2.0.0", None);
    auto_opening(&mut window);
    startup_check(&mut window);

    assert!(window.update_prompt.is_open);
    let body = window.update_prompt.body();
    assert!(body.to_lowercase().contains("game"), "{body}");
    assert!(body.contains("2.0.0"), "{body}");

    window.install_update();
    window.poll();

    assert_eq!(feed.installed_client().as_deref(), Some("2.0.0"));
    assert!(!feed.pending_launcher_update());
    assert!(!window.update_prompt.is_open);
    assert_eq!(window.update_prompt.error, None);
}

// OpenAC LauncherUpdateViewModelTests.ALauncherUpdateStagesThenRestartsWithoutTouchingTheClient.
#[test]
fn a_launcher_update_stages_then_restarts_without_touching_the_client() {
    let feed = UpdateFeed::new(true);
    let applied = Arc::new(Mutex::new(0));
    let calls = Arc::clone(&applied);
    let mut window = feed.window(
        &batch(),
        "1.0.0",
        Some(Arc::new(move |_: &CancelToken| {
            *calls.lock().unwrap() += 1;
            Ok(true)
        })),
    );
    auto_opening(&mut window);
    startup_check(&mut window);
    assert!(window.update_prompt.is_open);
    assert!(window.update_prompt.is_launcher_update_available());

    window.install_update();
    window.poll();

    assert!(
        feed.pending_launcher_update(),
        "the launcher update was staged"
    );
    assert_eq!(*applied.lock().unwrap(), 1);
    assert!(window.is_shutdown_requested());
    assert_eq!(feed.installed_client(), None);
    // No client runs: the window closes at once (with clients it would
    // close them first, as a confirmed close does).
    assert!(window.should_exit());
}

// OpenAC LauncherUpdateViewModelTests.ALauncherUpdateThatCannotRestartTellsTheUserToReopen.
#[test]
fn a_launcher_update_that_cannot_restart_tells_the_user_to_reopen() {
    let feed = UpdateFeed::new(true);
    let mut window = feed.window(
        &batch(),
        "1.0.0",
        Some(Arc::new(|_: &CancelToken| Ok(false))),
    );
    auto_opening(&mut window);
    startup_check(&mut window);
    window.install_update();
    window.poll();

    assert!(feed.pending_launcher_update());
    assert!(!window.is_shutdown_requested());
    assert!(
        window
            .update_prompt
            .status
            .to_lowercase()
            .contains("reopen"),
        "{}",
        window.update_prompt.status
    );
}

// OpenAC LauncherUpdateViewModelTests.AnUnreachableFeedStaysSilent.
#[test]
fn an_unreachable_feed_stays_silent() {
    let feed = UpdateFeed::new(false);
    let mut window = feed.window(&batch(), "2.0.0", None);
    auto_opening(&mut window);
    startup_check(&mut window);
    assert!(!window.update_prompt.is_open);
    assert_eq!(window.update_prompt.error, None);
    assert_eq!(
        window.operation_status,
        "Update check unavailable; the launcher works offline."
    );
}

// OpenAC LauncherUpdateViewModelTests.NotNowClosesWithoutUpdatingAnything.
#[test]
fn not_now_closes_without_updating_anything() {
    let feed = UpdateFeed::new(true);
    let mut window = feed.window(&batch(), "1.0.0", None);
    auto_opening(&mut window);
    startup_check(&mut window);
    assert!(window.update_prompt.is_open);

    window.close_active_modal();

    assert!(!window.update_prompt.is_open);
    assert_eq!(feed.installed_client(), None);
    assert!(!feed.pending_launcher_update());
}

// OpenAC LauncherUpdateViewModelTests.UpdatingIsRefusedWhileASessionIsRunning.
#[test]
fn updating_is_refused_while_a_session_is_running() {
    let feed = UpdateFeed::new(true);
    let fake = batch();
    fake.set_session(|session| {
        session.state = LauncherActivityState::Running;
        session.exit_code = None;
    });
    let mut window = feed.window(&fake, "2.0.0", None);
    auto_opening(&mut window);
    startup_check(&mut window);
    assert!(window.has_active_sessions());
    assert!(window.update_prompt.is_open);

    window.install_update();
    window.poll();

    assert!(window.update_prompt.is_open);
    assert!(!window.update_prompt.is_busy);
    assert_eq!(feed.installed_client(), None);
}

// OpenAC LauncherUpdateViewModelTests.AFailedInstallReportsTheReasonAndLeavesTheDialogOpen.
#[test]
fn a_failed_install_reports_the_reason_and_leaves_the_dialog_open() {
    let feed = UpdateFeed::new(true);
    // The archive (same size) no longer matches the digest the manifest names.
    feed.server.add(
        &common::fixtures::FakeServer::url("client.zip"),
        &common::fixtures::client_zip(&feed.rid, "client-v3"),
    );
    let mut window = feed.window(&batch(), "2.0.0", None);
    auto_opening(&mut window);
    startup_check(&mut window);
    window.install_update();
    window.poll();

    assert!(window.update_prompt.is_open);
    let error = window.update_prompt.error.clone().unwrap_or_default();
    assert!(error.contains("SHA-256"), "{error}");
    assert_eq!(
        window.update_prompt.status,
        "The update could not be installed."
    );
    assert_eq!(feed.installed_client(), None);
}

// --- Client lifecycle: stops, starts and automatic relaunch -----------------------------
// OpenAC `LauncherWindowViewModel.Relaunch.Tests` and `.Accounts.Tests`.

fn reads(counter: &std::sync::atomic::AtomicUsize) -> usize {
    counter.load(std::sync::atomic::Ordering::SeqCst)
}

/// A session on `server` for `account` whose process lives.
fn live_session(id: &str, server: &str, account: &str) -> LauncherSessionSnapshot {
    let mut session = session(LauncherActivityState::Connected);
    session.session_id = id.into();
    session.server_name = server.into();
    session.account_name = account.into();
    session.exit_code = None;
    session.has_live_process = Some(true);
    session
}

/// `CreateInitialized` and the startup checks with jobs held until run.
fn create_started_queued(fake: &Arc<Fake>) -> (LauncherWindow, Arc<QueueSpawner>) {
    create_started_queued_with(fake, None)
}

fn create_started_queued_with(
    fake: &Arc<Fake>,
    clock: Option<ac_launcher_core::orchestration::MonotonicClock>,
) -> (LauncherWindow, Arc<QueueSpawner>) {
    let spawner = Arc::new(QueueSpawner::default());
    let mut parts = parts(fake.clone(), spawner.clone());
    parts.relaunch_clock = clock;
    let mut window = LauncherWindow::new(parts);
    window.initialize();
    window.start_background_initialization();
    spawner.run_all();
    window.poll();
    spawner.run_all();
    window.poll();
    window.close_active_modal();
    window.refresh_row_states();
    (window, spawner)
}

// OpenAC LauncherWindowViewModelTests.IdlePollDoesNotRebuildProfileOrSessionSnapshots.
#[test]
fn idle_poll_does_not_rebuild_profile_or_session_snapshots() {
    for automatic in [false, true] {
        let fake = batch();
        *fake.session.lock().unwrap() = Some(live_session("session-1", "One", "Alice"));
        let mut window = create_initialized(&fake);
        window.set_row_checked("Alice", "One", true);
        window.set_relaunch_checked_clients_automatically(automatic);
        let before = reads(&fake.snapshot_reads);
        for _ in 0..50 {
            window.poll();
        }
        assert_eq!(
            reads(&fake.snapshot_reads),
            before,
            "automatic: {automatic}"
        );
    }
}

// OpenAC LauncherWindowViewModelTests.CheckedFilterCombinesWithProfilesAndKeepsSelectionsWhileActiveCanBeUnchecked.
#[test]
fn checked_filter_combines_with_profiles_and_keeps_selections_while_active_can_be_unchecked() {
    let fake = profiled();
    *fake.session.lock().unwrap() = Some(live_session("session-1", "One", "Alice"));
    let mut window = create_initialized(&fake);
    assert!(!window.show_only_checked_accounts());
    assert!(!window.relaunch_checked_clients_automatically());
    assert_eq!(window.relaunch_delay_seconds_text(), "180");
    // The running row's selection cannot be edited, but its check can change.
    let row = window.row("Alice", "One").unwrap();
    assert!(row.is_active());
    window.set_row_checked("Alice", "One", true);
    window.set_row_checked("Alice", "Two", true);
    window.set_show_only_checked_accounts(true);
    let groups = |window: &LauncherWindow| -> Vec<bool> {
        window
            .accounts
            .iter()
            .map(|group| group.is_visible)
            .collect()
    };
    assert_eq!(groups(&window), [true, false]);
    window.set_profile_filter(Some("bots"));
    assert_eq!(groups(&window), [true, false]);
    assert_eq!(
        window.accounts[0]
            .servers
            .iter()
            .map(|row| row.is_visible)
            .collect::<Vec<_>>(),
        [false, true]
    );
    assert!(window.row("Alice", "One").unwrap().is_checked);
    window.set_row_checked("Alice", "One", false);
    window.set_profile_filter(None);
    assert_eq!(groups(&window), [true, false]);
    window.set_show_only_checked_accounts(false);
    assert!(window.accounts.iter().all(|group| group.is_visible));
    assert!(window.row("Alice", "Two").unwrap().is_checked);
}

// OpenAC LauncherWindowViewModelTests.OnlyRequestedStopButtonStaysDisabledUntilActualProcessExit.
#[test]
fn only_requested_stop_button_stays_disabled_until_actual_process_exit() {
    for result in ["return", "throw", "cancel"] {
        let fake = batch();
        let first = live_session("session-1", "One", "Alice");
        let second = live_session("second", "One", "Bob");
        *fake.sessions_override.lock().unwrap() = Some(vec![first.clone(), second.clone()]);
        *fake.stop_handler.lock().unwrap() = Some(Box::new(move |_| match result {
            "throw" => Err(OrchestratorError::Operation("stop failed".into())),
            "cancel" => Err(OrchestratorError::Cancelled),
            _ => Ok(()),
        }));
        let mut window = create_started(&fake);
        window.stop_row("Alice", "One");
        window.poll();
        assert!(!window.is_busy(), "{result}");
        assert!(!window.can_stop_row("Alice", "One"), "{result}");
        assert!(window.can_stop_row("Bob", "One"), "{result}");
        assert!(window.can_stop_session("second"), "{result}");
        let mut exited = first.clone();
        exited.state = LauncherActivityState::Exited;
        *fake.sessions_override.lock().unwrap() = Some(vec![exited, second.clone()]);
        fake.raise_state_changed();
        window.poll();
        assert!(window.can_stop_row("Bob", "One"), "{result}");
        assert!(window.row("Alice", "Two").unwrap().can_play(), "{result}");
        // A stopped session still finishing start-up holds the gate too.
        let mut starting = first.clone();
        starting.state = LauncherActivityState::Cancelled;
        starting.has_live_process = Some(false);
        starting.startup_in_flight = true;
        *fake.sessions_override.lock().unwrap() = Some(vec![starting, second.clone()]);
        fake.raise_state_changed();
        window.poll();
        assert!(window.can_stop_row("Bob", "One"), "{result}");
        let mut ended = first.clone();
        ended.state = LauncherActivityState::Exited;
        ended.has_live_process = Some(false);
        *fake.sessions_override.lock().unwrap() = Some(vec![ended, second.clone()]);
        fake.raise_state_changed();
        window.poll();
        assert!(window.can_stop_row("Bob", "One"), "{result}");
        assert!(window.can_stop_session("second"), "{result}");
    }
}

// OpenAC LauncherWindowViewModelTests.CanStopSecondClientWhileFirstStopIsStillAwaitingExit.
#[test]
fn can_stop_second_client_while_first_stop_is_still_awaiting_exit() {
    let fake = batch();
    *fake.sessions_override.lock().unwrap() = Some(vec![
        live_session("session-1", "One", "Alice"),
        live_session("second", "One", "Bob"),
    ]);
    let (mut window, spawner) = create_started_queued(&fake);
    // The first stop is still running (its job has not run).
    window.stop_row("Alice", "One");
    assert!(window.can_stop_row("Bob", "One"));
    window.stop_row("Bob", "One");
    // Both requests overlap; their results are applied one after another.
    spawner.run_all();
    window.poll();
    assert_eq!(*fake.stopped.lock().unwrap(), ["session-1", "second"]);
    assert_eq!(window.operation_status, "Stop requested.");
}

// OpenAC LauncherWindowViewModelTests.StopAndPlayOnDifferentAccountsDoNotBlockEachOtherOrSettings.
#[test]
fn stop_and_play_on_different_accounts_do_not_block_each_other_or_settings() {
    let fake = batch();
    *fake.sessions_override.lock().unwrap() = Some(vec![live_session("session-1", "One", "Alice")]);
    let (mut window, spawner) = create_started_queued(&fake);
    window.stop_row("Alice", "One");
    assert!(!window.row("Alice", "One").unwrap().can_play());
    assert!(window.row("Bob", "One").unwrap().can_play());
    assert!(window.can_interact_with_row("Bob", "One"));
    assert!(window.can_interact());
    window.launch_row("Bob", "One");
    assert!(window.is_row_starting("Bob", "One"));
    assert!(!window.can_interact_with_row("Bob", "One"));
    assert!(window.can_interact());
    window.refresh_row_states();
    assert!(window.row("Alice", "Two").unwrap().can_play());
    assert_eq!(
        window.row("Bob", "One").unwrap().disabled_reason,
        "This account is starting."
    );
    spawner.run_all();
    window.poll();
    assert_eq!(fake.launch_requests.lock().unwrap().len(), 1);
    assert!(!window.is_row_starting("Bob", "One"));
}

// OpenAC LauncherWindowViewModelTests.DifferentAccountsCanStartConcurrentlyAndCancelCoversBoth
// (and, from LaunchGuiCommand, a start is cancelable without making the window busy).
#[test]
fn different_accounts_can_start_concurrently_and_cancel_covers_both() {
    let fake = batch();
    let (mut window, spawner) = create_started_queued(&fake);
    window.launch_row("Alice", "One");
    window.refresh_row_states();
    assert!(!window.is_busy());
    assert!(window.has_cancelable_operation());
    assert!(!window.row("Alice", "One").unwrap().can_play());
    assert!(window.row("Bob", "One").unwrap().can_play());
    window.launch_row("Bob", "One");
    assert!(window.can_interact());
    assert!(window.can_cancel_operation());
    window.cancel_operation();
    spawner.run_all();
    window.poll();
    assert!(fake.launch_requests.lock().unwrap().is_empty());
    assert!(!window.has_cancelable_operation());
    assert!(!window.is_row_starting("Alice", "One"));
    assert!(!window.is_row_starting("Bob", "One"));
    assert!(window.operation_status.starts_with("Launch cancelled."));
}

// OpenAC LauncherWindowViewModelTests.BindingReadsReuseAvailabilityUntilTheNextPoll.
#[test]
fn binding_reads_reuse_availability_until_the_next_poll() {
    let fake = batch();
    let mut window = create_started(&fake);
    window.poll();
    let (snapshots, capabilities) = (reads(&fake.snapshot_reads), reads(&fake.capability_reads));
    for _ in 0..100 {
        let row = window.row("Alice", "One").unwrap();
        assert!(row.can_play());
        assert!(row.disabled_reason.is_empty());
    }
    assert_eq!(reads(&fake.snapshot_reads), snapshots);
    assert_eq!(reads(&fake.capability_reads), capabilities);
    *fake.account_launch_capability.lock().unwrap() =
        Some(LauncherCapability::unavailable("Try again in 2 s."));
    window.poll();
    assert_eq!(
        window.row("Alice", "One").unwrap().disabled_reason,
        "Try again in 2 s."
    );
    *fake.account_launch_capability.lock().unwrap() =
        Some(LauncherCapability::unavailable("Try again in 1 s."));
    window.poll();
    assert_eq!(
        window.row("Alice", "One").unwrap().disabled_reason,
        "Try again in 1 s."
    );
}

// OpenAC LauncherWindowViewModelTests.LaunchChecksChangesThatHaveNotReachedTheUiSnapshot.
#[test]
fn launch_checks_changes_that_have_not_reached_the_ui_snapshot() {
    let fake = batch();
    let mut window = create_started(&fake);
    assert!(window.row("Alice", "One").unwrap().can_play());
    fake.servers.lock().unwrap().clear();
    window.launch_row("Alice", "One");
    window.poll();
    assert!(fake.launch_requests.lock().unwrap().is_empty());
    assert!(
        window
            .last_error
            .as_deref()
            .unwrap()
            .contains("no longer configured")
    );
}

// OpenAC LauncherWindowViewModelTests.AutoRelaunchIncludesHiddenCheckedAccountsAndRespectsInvalidDelay.
#[test]
fn auto_relaunch_includes_hidden_checked_accounts_and_respects_invalid_delay() {
    let fake = profiled();
    *fake.session.lock().unwrap() = Some(live_session("session-1", "One", "Alice"));
    let seconds = Arc::new(std::sync::atomic::AtomicU64::new(0));
    let clock_seconds = Arc::clone(&seconds);
    let clock: ac_launcher_core::orchestration::MonotonicClock = Arc::new(move || {
        Duration::from_secs(clock_seconds.load(std::sync::atomic::Ordering::SeqCst))
    });
    let mut window = LauncherWindow::new(LauncherWindowParts {
        relaunch_clock: Some(clock),
        ..parts(fake.clone(), Arc::new(InlineSpawner))
    });
    window.initialize();
    window.start_background_initialization();
    window.poll();
    window.close_active_modal();
    window.set_row_checked("Alice", "One", true);
    window.set_relaunch_checked_clients_automatically(true);
    window.set_relaunch_delay_seconds_text("5");
    window.set_profile_filter(Some("bots"));
    assert!(!window.row("Alice", "One").unwrap().is_visible);
    fake.set_session(|session| {
        session.state = LauncherActivityState::Exited;
        session.has_live_process = Some(false);
    });
    fake.raise_state_changed();
    window.poll();
    seconds.fetch_add(5, std::sync::atomic::Ordering::SeqCst);
    window.set_relaunch_delay_seconds_text("4.5");
    assert!(!window.is_relaunch_delay_valid());
    window.poll();
    assert!(fake.launch_requests.lock().unwrap().is_empty());
    window.set_relaunch_delay_seconds_text("5");
    window.poll();
    let requests = fake.launch_requests.lock().unwrap().clone();
    assert_eq!(requests.len(), 1);
    assert_eq!(
        (requests[0].0.as_str(), requests[0].1.as_str()),
        ("One", "Alice")
    );
}

// --- Update checks while the launcher is open --------------------------------------------

// OpenAC LauncherWindowViewModelTests.UpdatesAreCheckedAtStartupThenEveryTwentyMinutesAndOnTheButton
// (only the startup check verifies the installed client).
#[test]
fn updates_are_checked_at_startup_then_every_twenty_minutes_and_on_the_button() {
    let feed = UpdateFeed::new(true);
    let manifest = common::fixtures::FakeServer::url("manifest.json");
    let mut window = feed.window(&batch(), "2.0.0", None);
    let start = Instant::now();
    let checks = |feed: &UpdateFeed| feed.server.count(&manifest);

    window.poll_update_check(start + Duration::from_secs(3600));
    assert_eq!(checks(&feed), 0);
    startup_check(&mut window);
    assert_eq!(checks(&feed), 1);
    // A verification would reclaim this leftover of an interrupted download.
    let residue = feed.root.join("data").join("app").join(format!(
        ".client-download-{}.zip",
        ac_launcher_core::ids::unique_hex32()
    ));
    std::fs::write(&residue, "left over").unwrap();

    let minutes = |count: u64| start + Duration::from_secs(count * 60);
    window.poll_update_check(start);
    window.poll_update_check(minutes(19));
    window.poll();
    assert_eq!(checks(&feed), 1);
    window.poll_update_check(minutes(20));
    window.poll();
    assert_eq!(checks(&feed), 2);
    window.poll_update_check(minutes(39));
    window.poll();
    assert_eq!(checks(&feed), 2);
    window.poll_update_check(minutes(40));
    window.poll();
    assert_eq!(checks(&feed), 3);
    // A background check raises nothing but the banner.
    assert!(!window.update_prompt.is_open);

    // The button checks at once, whatever the wait.
    window.check_for_updates();
    window.poll();
    assert_eq!(checks(&feed), 4);
    assert!(residue.exists());
}

// OpenAC LauncherWindowViewModelTests.TheUpdateCheckLooksOnlyAtInstalledPluginsAndLeavesThePanelAsItWas.
#[test]
fn the_update_check_looks_only_at_installed_plugins_and_leaves_the_panel_as_it_was() {
    let fixture = PanelFixture::new();
    fixture.list(&["edwards.discoverable"], "");
    fixture.release("edwards.discoverable", "0.2.0");
    let mut window = fixture.window();
    check(&mut window);
    window.select_plugins_tab();
    window.poll();
    let feed = ac_launcher_core::plugins::github::releases_feed(HELLO_REPO)
        .unwrap()
        .to_string();
    let releases = fixture.server.count(&feed);
    assert_eq!(releases, 1);
    window.plugins.add_from_url_text = "not a url".into();
    window.add_from_url();
    let error = window.plugins.error.clone();
    assert!(error.is_some());
    let lists = fixture.server.count(LIST_URI);

    window.check_for_updates();
    window.poll();

    assert_eq!(fixture.server.count(LIST_URI), lists + 1);
    assert_eq!(fixture.server.count(&feed), releases);
    assert_eq!(window.plugins.error, error);
    let shown = window.plugins.discover_view();
    assert_eq!(shown.len(), 1);
    assert_eq!(
        window.plugins.discover[shown[0]].latest_version(),
        Some("0.2.0")
    );
}

// --- Add a server -------------------------------------------------------------------------
// OpenAC `tests/AcDream.Launcher.Tests/AddServerDialogViewModelTests.cs`, over a real
// orchestrator and profile store as there.

mod add_server {
    use std::sync::Arc;
    use std::time::{Duration, SystemTime};

    use ac_host::local_time::LocalZone;
    use ac_launcher_core::orchestration::{LauncherExecutableSet, LauncherOrchestrator};
    use ac_launcher_core::profiles::LauncherProfileStore;
    use ac_launcher_core::status::{
        KnownServer, KnownServerCatalog, KnownServerList, POPULATION_URI, SERVERS_URI,
    };
    use ac_launcher_core::url::WebUrl;
    use ac_launcher_core::window::add_server::AddServerDialog;
    use ac_launcher_core::window::{InlineSpawner, LauncherWindow};

    use super::common::TempDir;
    use super::common::fixtures::{FakeServer, Route};

    struct Fixture {
        _root: TempDir,
        core: Arc<LauncherOrchestrator>,
        dialog: AddServerDialog,
    }

    impl Fixture {
        fn new() -> Self {
            let root = TempDir::new("add-server");
            let paths = root.path_set();
            let store = LauncherProfileStore::for_paths(&paths);
            let core = Arc::new(LauncherOrchestrator::new(
                store,
                paths,
                LauncherExecutableSet::new("unused-client", "unused-headless", None),
                None,
            ));
            core.load_profiles().unwrap();
            Self {
                _root: root,
                core,
                dialog: AddServerDialog::default(),
            }
        }

        fn show(&mut self, list: Option<&KnownServerList>) {
            let servers = self.core.snapshot().unwrap().servers;
            self.dialog.show_list(list, &servers, &LocalZone::fixed(0));
        }

        fn saved(&self) -> Vec<(String, String, i32)> {
            self.core
                .snapshot()
                .unwrap()
                .servers
                .into_iter()
                .map(|server| (server.name, server.host, server.port))
                .collect()
        }

        fn names(&self) -> Vec<String> {
            self.dialog
                .servers()
                .iter()
                .map(|row| row.name().to_owned())
                .collect()
        }
    }

    fn known(
        name: &str,
        host: &str,
        port: u16,
        kind: &str,
        about: &str,
        website: Option<&str>,
        players: Option<i32>,
    ) -> KnownServer {
        KnownServer {
            name: name.into(),
            host: host.into(),
            port,
            server_type: Some(kind.into()),
            software: Some("ACE".into()),
            description: Some(about.into()),
            website: website.and_then(WebUrl::parse),
            discord: None,
            player_count: players,
        }
    }

    /// OpenAC `List`.
    fn list(from_cache: bool) -> KnownServerList {
        KnownServerList {
            servers: vec![
                known(
                    "AChard",
                    "a-chard.ddns.net",
                    9000,
                    "PvP",
                    "PK server",
                    None,
                    Some(3),
                ),
                known(
                    "Coldeve",
                    "play.coldeve.ac",
                    9000,
                    "PvE",
                    "End of retail",
                    Some("https://coldeve.ac/"),
                    Some(675),
                ),
                known(
                    "Leafcull",
                    "leafcull.example",
                    9010,
                    "PvE",
                    "Retail-like",
                    None,
                    None,
                ),
            ],
            is_from_cache: from_cache,
            // 2026-09-20 12:00 UTC.
            saved_at: SystemTime::UNIX_EPOCH + Duration::from_secs(1_789_905_600),
        }
    }

    #[test]
    fn known_servers_show_their_type_players_and_whether_they_are_already_added() {
        let mut fixture = Fixture::new();
        fixture
            .core
            .add_server("coldeve", "PLAY.coldeve.ac", 9000)
            .unwrap();
        fixture.show(Some(&list(false)));
        assert_eq!(
            fixture.dialog.list_status,
            "3 servers · list and player counts from TreeStats"
        );
        assert_eq!(fixture.dialog.types, ["All types", "PvP", "PvE"]);
        assert_eq!(fixture.names(), ["AChard", "Coldeve", "Leafcull"]);
        let rows = fixture.dialog.servers();
        let coldeve = rows[1];
        assert_eq!(
            (coldeve.endpoint(), coldeve.server_type(), coldeve.players()),
            ("play.coldeve.ac:9000".to_owned(), "PvE", "675".to_owned())
        );
        assert!(coldeve.is_added);
        assert_eq!(rows[2].players(), "—");
        assert!(!rows[0].is_added);
    }

    #[test]
    fn adding_a_known_server_saves_it_and_marks_it_added() {
        let mut fixture = Fixture::new();
        fixture.show(Some(&list(false)));
        let core = Arc::clone(&fixture.core);
        assert!(fixture.dialog.add_known("Leafcull", core.as_ref()));
        assert_eq!(
            fixture.saved(),
            [("Leafcull".to_owned(), "leafcull.example".to_owned(), 9010)]
        );
        assert!(
            fixture
                .dialog
                .servers()
                .iter()
                .find(|row| row.name() == "Leafcull")
                .unwrap()
                .is_added
        );
        assert_eq!(
            fixture.dialog.notice.as_deref(),
            Some("Added Leafcull. Add your accounts on it with Accounts.")
        );
    }

    #[test]
    fn search_and_type_narrow_the_list() {
        let mut fixture = Fixture::new();
        fixture.show(Some(&list(false)));
        fixture.dialog.set_selected_type("PvE");
        assert_eq!(fixture.names(), ["Coldeve", "Leafcull"]);
        fixture.dialog.set_search("retail-like");
        assert_eq!(fixture.names(), ["Leafcull"]);
        fixture.dialog.set_search("coldeve.ac");
        assert_eq!(fixture.names(), ["Coldeve"]);
    }

    #[test]
    fn offline_only_your_own_server_can_be_added() {
        let mut fixture = Fixture::new();
        fixture.show(None);
        assert!(fixture.dialog.servers().is_empty());
        assert!(!fixture.dialog.has_known_servers());
        assert!(fixture.dialog.list_status.starts_with("Offline"));

        let core = Arc::clone(&fixture.core);
        fixture.dialog.name = "My local ACE".into();
        fixture.dialog.host = "127.0.0.1".into();
        fixture.dialog.port = "70000".into();
        assert!(!fixture.dialog.add_own(core.as_ref()));
        assert_eq!(
            fixture.dialog.error.as_deref(),
            Some("The port is a number from 1 to 65535.")
        );
        assert!(fixture.saved().is_empty());

        fixture.dialog.port = "9000".into();
        assert!(fixture.dialog.add_own(core.as_ref()));
        assert_eq!(fixture.dialog.error, None);
        assert_eq!(
            fixture.saved(),
            [("My local ACE".to_owned(), "127.0.0.1".to_owned(), 9000)]
        );
    }

    // OpenAC AnOfflineCopySaysWhenItWasSavedTheSameWayInEveryLanguage: the date is the
    // player's local date, written the same in every language.
    #[test]
    fn an_offline_copy_says_when_it_was_saved_the_same_way_in_every_language() {
        let mut fixture = Fixture::new();
        let servers = fixture.core.snapshot().unwrap().servers;
        fixture
            .dialog
            .show_list(Some(&list(true)), &servers, &LocalZone::fixed(2 * 3600));
        assert!(
            fixture
                .dialog
                .list_status
                .ends_with("offline copy from Sep 20, 2026"),
            "{}",
            fixture.dialog.list_status
        );
        // Twelve hours later in the player's zone, it is the next day.
        fixture
            .dialog
            .show_list(Some(&list(true)), &servers, &LocalZone::fixed(13 * 3600));
        assert!(fixture.dialog.list_status.ends_with("Sep 21, 2026"));
    }

    // OpenAC AListThatFailsToLoadLeavesYourOwnServerAndNeverThrows. Here a transport can only
    // fail as a failed download, which the catalog reads as no list: the dialog says it is
    // offline and stays usable for your own server.
    #[test]
    fn a_list_that_fails_to_load_leaves_your_own_server() {
        let root = TempDir::new("add-server-window");
        let server = FakeServer::new();
        server.add_route(SERVERS_URI, Route::Fail);
        server.add_route(POPULATION_URI, Route::Fail);
        let fake = super::batch();
        let mut parts = super::parts(fake, Arc::new(InlineSpawner));
        parts.known_servers = Some(Arc::new(KnownServerCatalog::new(
            server.transport(),
            &root.join("cache"),
        )));
        let mut window = LauncherWindow::new(parts);
        window.initialize();
        window.open_add_server();
        window.poll();
        assert!(window.add_server.is_open);
        assert!(window.add_server.servers().is_empty());
        assert!(!window.add_server.is_loading);
        assert!(window.add_server.list_status.starts_with("Offline"));
    }

    #[test]
    fn a_server_name_already_taken_is_refused_with_a_reason() {
        let mut fixture = Fixture::new();
        fixture
            .core
            .add_server("Coldeve", "other.example", 9001)
            .unwrap();
        fixture.show(Some(&list(false)));
        assert!(
            fixture
                .dialog
                .servers()
                .iter()
                .find(|row| row.name() == "Coldeve")
                .unwrap()
                .is_added
        );
        let core = Arc::clone(&fixture.core);
        fixture.dialog.name = "Coldeve".into();
        fixture.dialog.host = "somewhere.example".into();
        assert!(!fixture.dialog.add_own(core.as_ref()));
        assert_eq!(
            fixture.dialog.error.as_deref(),
            Some("A server named 'Coldeve' already exists.")
        );
        assert_eq!(fixture.saved().len(), 1);
    }

    // OpenAC LauncherWindowViewModel.Desktop.cs (InitializeDesktop): an added server shows at
    // once, with every account.
    #[test]
    fn an_added_server_shows_every_account_again() {
        let fake = super::batch();
        let mut window = super::create_initialized(&fake);
        window.set_show_only_checked_accounts(true);
        window.open_add_server();
        assert!(window.add_server.is_open);
        assert!(window.is_modal_open());
        window.add_server.name = "Mine".into();
        window.add_server.host = "127.0.0.1".into();
        window.add_own_server();
        assert!(!window.show_only_checked_accounts());
        assert!(
            window
                .snapshot()
                .unwrap()
                .servers
                .iter()
                .any(|server| server.name == "Mine")
        );
        window.close_active_modal();
        assert!(!window.add_server.is_open);
    }
}

// OpenAC LauncherWindowViewModelTests.InstallFromZipShowsReviewThenInstallsWithoutAGitHubRelease.
#[test]
fn install_from_zip_shows_review_then_installs_without_a_github_release() {
    let fixture = PanelFixture::new();
    let id = "mosswart.massacre";
    let manifest = format!(
        r#"{{
  "id": "{id}",
  "displayName": "{id}",
  "version": "0.1.0",
  "entry": "{id}.wasm",
  "apiVersion": "1.0",
  "minHostVersion": "0.1.0",
  "permissionsVersion": 1,
  "permissions": [{{ "name": "chat", "note": "Shares vitals." }}]
}}"#
    );
    let wasm = format!("{id}.wasm");
    let zip_bytes = common::fixtures::create_zip(
        &[
            ("plugin.json", manifest.as_bytes(), None),
            (wasm.as_str(), b"binary".as_slice(), None),
        ],
        common::fixtures::Compression::Stored,
    );
    let cache = fixture.paths().cache_directory;
    std::fs::create_dir_all(&cache).unwrap();
    let zip = cache.join("mosswart.zip");
    std::fs::write(&zip, zip_bytes).unwrap();
    let mut window = fixture.window();
    assert!(window.can_install_local_zip());

    window.open_local_zip(&zip);
    window.poll();

    let dialog = &window.plugins.install_dialog;
    assert!(dialog.is_open, "{:?}", window.plugins.error);
    let request = dialog.request.as_ref().unwrap();
    assert_eq!(request.plugin_id, id);
    assert_eq!(request.repo, "Local ZIP: mosswart.zip");
    assert!(
        dialog
            .permissions
            .iter()
            .any(|chip| chip.label == "use chat")
    );
    assert!(fixture.server.requests().is_empty());

    window.confirm_install();
    window.poll();

    assert!(!window.plugins.install_dialog.is_open);
    assert!(
        fixture
            .paths()
            .plugins_directory()
            .join(id)
            .join("plugin.json")
            .is_file()
    );
    assert!(
        !fixture
            .server
            .requests()
            .iter()
            .any(|url| url.contains("github.com"))
    );
}

// --- Switching a running row and closing the launcher -----------------------------------
//
// Additions: OpenAC's launcher can neither switch a running client between
// graphical and headless nor ask before closing.

/// A batch window whose `Alice` on `One` play session runs with a window.
fn running_graphical() -> (Arc<Fake>, LauncherWindow) {
    let fake = batch();
    *fake.session.lock().unwrap() = Some(live_session("session-1", "One", "Alice"));
    let window = create_initialized(&fake);
    (fake, window)
}

#[test]
fn a_running_rows_mode_box_switches_its_session_without_a_restart() {
    let (fake, mut window) = running_graphical();
    let row = window.row("Alice", "One").unwrap();
    assert!(row.is_active());
    assert!(row.can_switch_window());
    assert_eq!(row.displayed_launch_mode(), RowLaunchMode::Graphical);
    assert!(!row.can_open_console());

    window.set_row_launch_mode("Alice", "One", RowLaunchMode::Headless);

    // The running session is asked to close its window: no launch, no stop.
    assert_eq!(
        *fake.window_switches.lock().unwrap(),
        [("session-1".to_owned(), false)]
    );
    assert!(fake.launch_requests.lock().unwrap().is_empty());
    assert!(fake.stopped.lock().unwrap().is_empty());
    // The next launch follows the choice, as an idle row's pick does.
    assert_eq!(
        fake.saved_row_selection
            .lock()
            .unwrap()
            .as_ref()
            .map(|saved| saved.1),
        Some(LaunchMode::Headless)
    );
    // While the client has not answered, the box shows the choice and takes
    // no other.
    let row = window.row("Alice", "One").unwrap();
    assert_eq!(row.displayed_launch_mode(), RowLaunchMode::Headless);
    assert!(!row.can_switch_window());

    // The client reports the window closed: the row is headless now and its
    // console opens.
    fake.set_session(|session| {
        session.window.switching_to = None;
        session.window.is_open = false;
    });
    fake.raise_state_changed();
    window.poll();
    let row = window.row("Alice", "One").unwrap();
    assert_eq!(row.displayed_launch_mode(), RowLaunchMode::Headless);
    assert!(row.can_switch_window());
    assert!(row.can_open_console());
    assert_eq!(window.last_error, None);

    // And back: the window is asked for again.
    window.set_row_launch_mode("Alice", "One", RowLaunchMode::Graphical);
    assert_eq!(
        fake.window_switches.lock().unwrap().last(),
        Some(&("session-1".to_owned(), true))
    );
}

#[test]
fn a_switch_that_cannot_be_sent_puts_the_box_back_and_says_why() {
    let (fake, mut window) = running_graphical();
    *fake.window_switch_refusal.lock().unwrap() =
        Some("Could not switch the session: its client takes no console input.".into());

    window.set_row_launch_mode("Alice", "One", RowLaunchMode::Headless);

    let row = window.row("Alice", "One").unwrap();
    assert_eq!(row.displayed_launch_mode(), RowLaunchMode::Graphical);
    assert_eq!(row.selected_launch_mode, RowLaunchMode::Graphical);
    assert_eq!(*fake.saved_row_selection.lock().unwrap(), None);
    assert_eq!(
        window.last_error.as_deref(),
        Some("Could not switch the session: its client takes no console input.")
    );
}

#[test]
fn a_window_the_client_could_not_open_puts_the_box_back_and_says_why() {
    let fake = batch();
    let mut headless = live_session("session-1", "One", "Alice");
    headless.launch_mode = Some(LaunchMode::Headless);
    headless.window.is_open = false;
    *fake.session.lock().unwrap() = Some(headless);
    let mut window = create_initialized(&fake);
    assert_eq!(
        window.row("Alice", "One").unwrap().displayed_launch_mode(),
        RowLaunchMode::Headless
    );

    window.set_row_launch_mode("Alice", "One", RowLaunchMode::Graphical);
    assert_eq!(
        *fake.window_switches.lock().unwrap(),
        [("session-1".to_owned(), true)]
    );
    assert_eq!(
        window.row("Alice", "One").unwrap().displayed_launch_mode(),
        RowLaunchMode::Graphical
    );

    // The client answers that no window opened.
    fake.set_session(|session| {
        session.window.switching_to = None;
        session.window.error = Some("The game window could not open: no window support".to_owned());
    });
    fake.raise_state_changed();
    window.poll();
    assert_eq!(
        window.row("Alice", "One").unwrap().displayed_launch_mode(),
        RowLaunchMode::Headless
    );
    assert_eq!(
        window.last_error.as_deref(),
        Some("The game window could not open: no window support")
    );
}

#[test]
fn a_row_that_is_starting_does_not_switch() {
    let fake = batch();
    let mut starting = live_session("session-1", "One", "Alice");
    starting.startup_in_flight = true;
    *fake.session.lock().unwrap() = Some(starting);
    let mut window = create_initialized(&fake);
    assert!(!window.row("Alice", "One").unwrap().can_switch_window());
    window.set_row_launch_mode("Alice", "One", RowLaunchMode::Headless);
    assert!(fake.window_switches.lock().unwrap().is_empty());
}

#[test]
fn closing_the_launcher_while_clients_run_asks_first() {
    let (fake, mut window) = running_graphical();
    assert!(!window.request_close());
    let prompt = window.close_prompt.expect("a prompt");
    assert_eq!(prompt, ClosePrompt { running: 1 });
    assert_eq!(
        prompt.message(),
        "Closing the launcher will log off and close 1 running client."
    );
    assert_eq!(
        ClosePrompt { running: 3 }.message(),
        "Closing the launcher will log off and close 3 running clients."
    );
    assert!(window.is_modal_open());
    assert!(!window.is_close_confirmed());

    window.confirm_close();
    assert!(window.close_prompt.is_none());
    assert!(window.is_close_confirmed());
    // The window stays open on the closing while the client logs out.
    assert_eq!(*fake.stopped.lock().unwrap(), ["session-1"]);
    assert!(window.closing().is_some());
    assert!(!window.should_exit());
}

// Not an OpenAC test: closing over running clients stops them all at once
// on workers, shows one line per client and the overall progress, lets
// nothing start meanwhile, and closes the window once every client ended.
#[test]
fn closing_stops_every_client_at_once_and_shows_its_progress() {
    let fake = batch();
    let mut alice = live_session("session-1", "One", "Alice");
    alice.character_name = Some("Alicia".into());
    alice.state = LauncherActivityState::InWorld;
    let mut bob = live_session("session-2", "One", "Bob");
    bob.character_name = None;
    *fake.sessions_override.lock().unwrap() = Some(vec![alice.clone(), bob.clone()]);
    let spawner = Arc::new(QueueSpawner::default());
    let mut window = LauncherWindow::new(parts(fake.clone(), spawner.clone()));
    window.initialize();

    assert!(!window.request_close());
    window.confirm_close();
    assert!(*fake.closed_to_new_sessions.lock().unwrap());
    assert!(!window.can_interact());
    assert!(!window.can_start_rows());
    assert!(
        !window.request_close(),
        "a second close waits for the closing"
    );
    let closing = window.closing().unwrap();
    assert_eq!(closing.summary(), "Closing 2 of 2 clients…");
    let lines: Vec<(String, String)> = closing
        .clients()
        .iter()
        .map(|client| (client.title(), client.state.text()))
        .collect();
    assert_eq!(
        lines,
        [
            ("Alice / Alicia".to_owned(), "Logging out…".to_owned()),
            ("Bob / One".to_owned(), "Logging out…".to_owned()),
        ]
    );
    // Both stops run on workers, not on the window's thread.
    assert!(fake.stopped.lock().unwrap().is_empty());
    spawner.run_all();
    assert_eq!(*fake.stopped.lock().unwrap(), ["session-1", "session-2"]);
    assert_eq!(
        *fake.stop_timeouts.lock().unwrap(),
        [Duration::from_secs(30), Duration::from_secs(30)]
    );

    // Alice logs out; Bob did not respond and was killed.
    let mut alice_ended = alice;
    alice_ended.state = LauncherActivityState::Exited;
    alice_ended.has_live_process = Some(false);
    *fake.sessions_override.lock().unwrap() = Some(vec![alice_ended.clone(), bob.clone()]);
    window.poll();
    assert_eq!(
        window.closing().unwrap().summary(),
        "Closing 1 of 2 clients…"
    );
    assert!(!window.should_exit());
    let mut bob_ended = bob;
    bob_ended.state = LauncherActivityState::Exited;
    bob_ended.has_live_process = Some(false);
    bob_ended.stop = Some(ac_launcher_core::orchestration::LauncherSessionStop::Forced);
    *fake.sessions_override.lock().unwrap() = Some(vec![alice_ended, bob_ended]);
    window.poll();
    let closing = window.closing().unwrap();
    assert_eq!(closing.clients()[0].state.text(), "Closed");
    assert_eq!(
        closing.clients()[1].state.text(),
        "Did not respond, closed by force"
    );
    assert_eq!(closing.summary(), "All clients have closed.");
    assert!(window.should_exit());
}

// Not an OpenAC test: "Close now" kills every client still logging out.
#[test]
fn close_now_forces_the_clients_still_logging_out() {
    let (fake, mut window) = running_graphical();
    assert!(!window.request_close());
    window.confirm_close();
    window.close_now();
    assert_eq!(
        *fake.stop_timeouts.lock().unwrap(),
        [Duration::from_secs(30), Duration::ZERO]
    );
    assert!(window.closing().unwrap().is_force_requested());
}

// Not an OpenAC test: a client the timeout already killed keeps "Did not
// respond" after Close now; only those still logging out then read
// "Closed by force".
#[test]
fn close_now_keeps_what_the_timeout_did() {
    use ac_launcher_core::orchestration::LauncherSessionStop;
    let fake = batch();
    let alice = live_session("session-1", "One", "Alice");
    let mut bob = live_session("session-2", "One", "Bob");
    bob.character_name = None;
    *fake.sessions_override.lock().unwrap() = Some(vec![alice.clone(), bob.clone()]);
    let mut window = create_initialized(&fake);
    assert!(!window.request_close());
    window.confirm_close();
    let ended = |mut session: LauncherSessionSnapshot| {
        session.state = LauncherActivityState::Exited;
        session.has_live_process = Some(false);
        session.stop = Some(LauncherSessionStop::Forced);
        session
    };
    *fake.sessions_override.lock().unwrap() = Some(vec![alice.clone(), ended(bob.clone())]);
    window.poll();
    window.close_now();
    *fake.sessions_override.lock().unwrap() = Some(vec![ended(alice), ended(bob)]);
    window.poll();
    let states: Vec<String> = window
        .closing()
        .unwrap()
        .clients()
        .iter()
        .map(|client| client.state.text())
        .collect();
    assert_eq!(
        states,
        ["Closed by force", "Did not respond, closed by force"]
    );
}

#[test]
fn closing_the_launcher_with_no_client_running_closes_at_once() {
    let fake = batch();
    // A session that has just ended: reported exited, its process gone.
    let mut ended = live_session("session-1", "One", "Alice");
    ended.state = LauncherActivityState::Exited;
    ended.has_live_process = Some(false);
    *fake.session.lock().unwrap() = Some(ended);
    let mut window = create_initialized(&fake);
    assert!(window.request_close());
    assert!(window.close_prompt.is_none());

    // No session at all.
    let fake = Arc::new(Fake::default());
    let mut window = create_initialized(&fake);
    assert!(window.request_close());
    assert!(window.close_prompt.is_none());
}

#[test]
fn cancelling_the_close_keeps_everything_running() {
    let (fake, mut window) = running_graphical();
    assert!(!window.request_close());
    window.cancel_close();
    assert!(window.close_prompt.is_none());
    assert!(!window.is_close_confirmed());
    assert!(fake.stopped.lock().unwrap().is_empty());
    assert!(window.row("Alice", "One").unwrap().is_active());
    // Escape cancels too, and the button asks again next time.
    assert!(!window.request_close());
    window.close_active_modal();
    assert!(window.close_prompt.is_none());
    assert!(!window.is_close_confirmed());
}

// Not an OpenAC test: the session log tells a forced stop and a refusal
// in plain words, and says the server may hold the account only after a
// forced stop or a refusal because the account is still logged on.
#[test]
fn the_session_log_says_the_server_may_hold_the_account_only_when_it_may() {
    use ac_launcher_core::orchestration::LauncherSessionStop;
    use ac_launcher_core::window::sessions::describe_status;
    let mut ended = session(LauncherActivityState::Exited);
    ended.exit_code = Some(1);
    ended.stop = Some(LauncherSessionStop::Forced);
    assert_eq!(
        describe_status(&ended),
        "Stopped (did not respond, closed by force) — the server may hold this account for a \
         short while."
    );
    ended.stop = None;
    ended.exit_code = Some(6);
    ended.exit_reason = Some("the server ended the session".into());
    ended.server_message = Some("Another account is already logged on from this client.".into());
    ended.account_in_use = true;
    assert_eq!(
        describe_status(&ended),
        "Another account is already logged on from this client — the server may hold this \
         account for a short while."
    );
    ended.server_message = Some("The server is currently full. Please try again later.".into());
    ended.account_in_use = false;
    assert_eq!(
        describe_status(&ended),
        "The server is currently full. Please try again later."
    );
}
