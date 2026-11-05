//! The orchestrator, the executable set, platform capabilities and the
//! character-screen plugin list. Ported from OpenAC
//! `tests/AcDream.Launcher.Core.Tests/Orchestration/` (LauncherOrchestratorTests,
//! LauncherExecutableSetTests, LauncherPlatformCapabilitiesTests,
//! CharacterScreenPluginListTests). The orchestrator's async methods are
//! blocking here; a test that needs two at once uses threads.

mod common;

use std::collections::VecDeque;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, SystemTime};

use ac_host_core::PathSet;
use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::installation::LauncherInstallRecord;
use ac_launcher_core::launching::{
    ComposeError, ComposedSessionConfig, LauncherProcessSpec, LauncherSessionState,
    ProcessSupervisor, ProcessSupervisorFactory, SessionConfigService, StateListener, StopOutcome,
    SupervisorError, compose, compose_probe, serialize,
};
use ac_launcher_core::orchestration::{
    Clock, LauncherActivityKind, LauncherActivityState, LauncherExecutableSet,
    LauncherOrchestrator, LauncherPlatformCapabilities, LauncherSessionStop, OrchestratorError,
    OrchestratorParts, SERVER_SESSION_HOLD, UNSUPPORTED_PLATFORM_GRAPHICAL_LAUNCH_DISABLED_REASON,
    WINDOW_SWITCH_TIMEOUT,
};
use ac_launcher_core::plugins::PluginCatalog;
use ac_launcher_core::profiles::{
    AccountProfile, CharacterProfile, LaunchMode, LauncherProfileStore, LauncherTextEditorKind,
    ServerProfile,
};
use ac_launcher_core::status::{
    StatusCharacterEntry, StatusEnvelope, StatusEvent, StatusEventKind, StatusEventSource,
    StatusEventSourceFactory,
};
use ac_launcher_core::updates::UpdateSessionBarrier;
use common::TempDir;

const PASSWORD: &str = "launcher-only-secret";

fn none() -> CancelToken {
    CancelToken::none()
}

fn lock<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
}

/// A one-shot signal.
#[derive(Default)]
struct Signal {
    set: Mutex<bool>,
    condvar: Condvar,
}

impl Signal {
    fn set(&self) {
        *lock(&self.set) = true;
        self.condvar.notify_all();
    }

    fn wait(&self, timeout: Option<Duration>) -> bool {
        let deadline = timeout.map(|timeout| std::time::Instant::now() + timeout);
        let mut set = lock(&self.set);
        while !*set {
            match deadline {
                None => set = self.condvar.wait(set).unwrap(),
                Some(deadline) => {
                    let now = std::time::Instant::now();
                    if now >= deadline {
                        return false;
                    }
                    set = self.condvar.wait_timeout(set, deadline - now).unwrap().0;
                }
            }
        }
        true
    }
}

// ---- fakes -----------------------------------------------------------------

#[derive(Default)]
struct Recorded {
    play_calls: usize,
    probe_calls: usize,
    last_character: Option<CharacterProfile>,
    last_account_password: Option<String>,
    last_account_login_commands: Option<Vec<String>>,
    last_composed: Option<ComposedSessionConfig>,
}

type BeforeCompose = Box<dyn Fn() -> Result<(), std::io::Error> + Send + Sync>;

/// Composes (without writing) and records what it was asked; `before_compose`
/// runs first (it may block or fail).
#[derive(Default)]
struct RecordingConfigService {
    recorded: Mutex<Recorded>,
    before_compose: Option<BeforeCompose>,
}

impl RecordingConfigService {
    fn play_calls(&self) -> usize {
        lock(&self.recorded).play_calls
    }

    fn probe_calls(&self) -> usize {
        lock(&self.recorded).probe_calls
    }

    fn last_character(&self) -> CharacterProfile {
        lock(&self.recorded).last_character.clone().unwrap()
    }

    fn last_account_login_commands(&self) -> Vec<String> {
        lock(&self.recorded)
            .last_account_login_commands
            .clone()
            .unwrap()
    }

    fn last_password(&self) -> String {
        lock(&self.recorded).last_account_password.clone().unwrap()
    }

    fn last_composed(&self) -> ComposedSessionConfig {
        lock(&self.recorded).last_composed.clone().unwrap()
    }
}

impl SessionConfigService for RecordingConfigService {
    fn compose_and_write(
        &self,
        server: &ServerProfile,
        account: &AccountProfile,
        character: &CharacterProfile,
        install: &LauncherInstallRecord,
        paths: &PathSet,
        session_id: &str,
        login_command_delay_ms: Option<u32>,
        catalog: Option<&PluginCatalog>,
    ) -> Result<ComposedSessionConfig, ComposeError> {
        if let Some(before) = &self.before_compose {
            before()?;
        }
        let composed = compose(
            server,
            account,
            character,
            install,
            paths,
            session_id,
            login_command_delay_ms,
            catalog,
        )?;
        let mut recorded = lock(&self.recorded);
        recorded.play_calls += 1;
        recorded.last_character = Some(character.clone());
        recorded.last_account_password = Some(account.password.clone());
        recorded.last_account_login_commands = Some(account.login_commands.clone());
        recorded.last_composed = Some(composed.clone());
        Ok(composed)
    }

    fn compose_probe_and_write(
        &self,
        server: &ServerProfile,
        account: &AccountProfile,
        install: &LauncherInstallRecord,
        paths: &PathSet,
        session_id: &str,
    ) -> Result<ComposedSessionConfig, ComposeError> {
        let composed = compose_probe(server, account, install, paths, session_id)?;
        let mut recorded = lock(&self.recorded);
        recorded.probe_calls += 1;
        recorded.last_account_password = Some(account.password.clone());
        recorded.last_composed = Some(composed.clone());
        Ok(composed)
    }
}

type StartError = Arc<dyn Fn(Option<&str>) -> std::io::Error + Send + Sync>;

struct FakeSupervisorState {
    state: LauncherSessionState,
    exit_code: Option<i32>,
    spec: Option<LauncherProcessSpec>,
    password: Option<String>,
    stop_calls: usize,
    /// Console lines sent to the child.
    lines: Vec<String>,
    /// The child takes no console input.
    refuses_lines: bool,
    /// The child does not log out when asked: a stop kills it.
    ignores_stop: bool,
}

/// Records the spec and password; `exit` ends it. With
/// `fail_after_started`, the start fails after the child runs.
struct FakeSupervisor {
    state: Mutex<FakeSupervisorState>,
    listeners: Mutex<Vec<StateListener>>,
    start_error: Option<StartError>,
    fail_after_started: bool,
}

impl FakeSupervisor {
    fn new(start_error: Option<StartError>, fail_after_started: bool) -> Self {
        Self {
            state: Mutex::new(FakeSupervisorState {
                state: LauncherSessionState::Starting,
                exit_code: None,
                spec: None,
                password: None,
                stop_calls: 0,
                lines: Vec::new(),
                refuses_lines: false,
                ignores_stop: false,
            }),
            listeners: Mutex::new(Vec::new()),
            start_error,
            fail_after_started,
        }
    }

    fn publish(&self, state: LauncherSessionState) {
        let listeners = lock(&self.listeners).clone();
        for listener in listeners {
            listener(state);
        }
    }

    fn exit(&self, code: i32) {
        {
            let mut state = lock(&self.state);
            state.state = LauncherSessionState::Exited;
            state.exit_code = Some(code);
        }
        self.publish(LauncherSessionState::Exited);
    }

    fn spec(&self) -> LauncherProcessSpec {
        lock(&self.state).spec.clone().unwrap()
    }

    fn password(&self) -> Option<String> {
        lock(&self.state).password.clone()
    }

    fn stop_calls(&self) -> usize {
        lock(&self.state).stop_calls
    }

    fn lines(&self) -> Vec<String> {
        lock(&self.state).lines.clone()
    }

    fn refuse_lines(&self) {
        lock(&self.state).refuses_lines = true;
    }

    fn ignore_stop(&self) {
        lock(&self.state).ignores_stop = true;
    }
}

impl ProcessSupervisor for FakeSupervisor {
    fn state(&self) -> LauncherSessionState {
        lock(&self.state).state
    }

    fn exit_code(&self) -> Option<i32> {
        lock(&self.state).exit_code
    }

    fn subscribe(&self, listener: StateListener) {
        lock(&self.listeners).push(listener);
    }

    fn start(
        &self,
        spec: &LauncherProcessSpec,
        password: Option<&str>,
    ) -> Result<(), SupervisorError> {
        {
            let mut state = lock(&self.state);
            state.spec = Some(spec.clone());
            state.password = password.map(str::to_owned);
        }
        if let Some(error) = &self.start_error {
            return Err(SupervisorError::Io(error(password)));
        }
        lock(&self.state).state = LauncherSessionState::Running;
        self.publish(LauncherSessionState::Running);
        if self.fail_after_started {
            return Err(SupervisorError::Io(std::io::Error::other(
                "Fixture startup failed after creating child.",
            )));
        }
        Ok(())
    }

    fn stop(&self, _timeout: Duration) -> Result<StopOutcome, SupervisorError> {
        let killed = {
            let mut state = lock(&self.state);
            state.stop_calls += 1;
            state.state = LauncherSessionState::Exited;
            // A killed child ends with the code a kill gives it.
            state.exit_code = Some(if state.ignores_stop { 1 } else { 0 });
            state.ignores_stop
        };
        self.publish(LauncherSessionState::Exited);
        Ok(if killed {
            StopOutcome::Killed
        } else {
            StopOutcome::Exited
        })
    }

    fn try_send_line(&self, line: &str) -> bool {
        let mut state = lock(&self.state);
        if state.refuses_lines || state.state != LauncherSessionState::Running {
            return false;
        }
        state.lines.push(line.to_owned());
        true
    }

    fn dispose(&self) {}
}

#[derive(Default)]
struct FakeSupervisorFactory {
    created: Mutex<Vec<Arc<FakeSupervisor>>>,
    start_error: Option<StartError>,
    fail_after_started: bool,
}

impl FakeSupervisorFactory {
    fn failing(start_error: StartError) -> Self {
        Self {
            created: Mutex::new(Vec::new()),
            start_error: Some(start_error),
            fail_after_started: false,
        }
    }

    fn created(&self) -> Vec<Arc<FakeSupervisor>> {
        lock(&self.created).clone()
    }

    fn single(&self) -> Arc<FakeSupervisor> {
        let created = self.created();
        assert_eq!(created.len(), 1);
        Arc::clone(&created[0])
    }
}

impl ProcessSupervisorFactory for FakeSupervisorFactory {
    fn create(&self) -> Arc<dyn ProcessSupervisor> {
        let supervisor = Arc::new(FakeSupervisor::new(
            self.start_error.clone(),
            self.fail_after_started,
        ));
        lock(&self.created).push(Arc::clone(&supervisor));
        supervisor
    }
}

/// A supervisor whose stop blocks until allowed.
#[derive(Default)]
struct BlockingStopSupervisor {
    stop_entered: Signal,
    allow_terminal: Signal,
    state: Mutex<Option<LauncherSessionState>>,
    listeners: Mutex<Vec<StateListener>>,
    disposed: Mutex<bool>,
}

impl BlockingStopSupervisor {
    fn publish(&self, state: LauncherSessionState) {
        *lock(&self.state) = Some(state);
        let listeners = lock(&self.listeners).clone();
        for listener in listeners {
            listener(state);
        }
    }
}

impl ProcessSupervisor for BlockingStopSupervisor {
    fn state(&self) -> LauncherSessionState {
        lock(&self.state).unwrap_or(LauncherSessionState::Starting)
    }

    fn exit_code(&self) -> Option<i32> {
        (self.state() == LauncherSessionState::Exited).then_some(0)
    }

    fn subscribe(&self, listener: StateListener) {
        lock(&self.listeners).push(listener);
    }

    fn start(&self, _: &LauncherProcessSpec, _: Option<&str>) -> Result<(), SupervisorError> {
        self.publish(LauncherSessionState::Running);
        Ok(())
    }

    fn stop(&self, _timeout: Duration) -> Result<StopOutcome, SupervisorError> {
        self.stop_entered.set();
        self.allow_terminal.wait(None);
        self.publish(LauncherSessionState::Exited);
        Ok(StopOutcome::Exited)
    }

    fn try_send_line(&self, _line: &str) -> bool {
        false
    }

    fn dispose(&self) {
        *lock(&self.disposed) = true;
    }
}

#[derive(Default)]
struct BlockingStopSupervisorFactory {
    created: Mutex<Vec<Arc<BlockingStopSupervisor>>>,
}

impl BlockingStopSupervisorFactory {
    fn allow_every_stop(&self) {
        for supervisor in lock(&self.created).iter() {
            supervisor.allow_terminal.set();
        }
    }
}

impl ProcessSupervisorFactory for BlockingStopSupervisorFactory {
    fn create(&self) -> Arc<dyn ProcessSupervisor> {
        let supervisor = Arc::new(BlockingStopSupervisor::default());
        lock(&self.created).push(Arc::clone(&supervisor));
        supervisor
    }
}

type EventQueue = Arc<Mutex<VecDeque<StatusEvent>>>;

/// A queue of events and how often it was read.
struct QueueStatusSource(EventQueue, Arc<AtomicUsize>);

impl StatusEventSource for QueueStatusSource {
    fn read_new_events(&mut self) -> Vec<StatusEvent> {
        self.1.fetch_add(1, Ordering::SeqCst);
        lock(&self.0).drain(..).collect()
    }
}

#[derive(Default)]
struct QueueStatusSourceFactory {
    created: Mutex<Vec<(EventQueue, Arc<AtomicUsize>)>>,
}

impl QueueStatusSourceFactory {
    fn single(&self) -> EventQueue {
        let created = lock(&self.created);
        assert_eq!(created.len(), 1);
        Arc::clone(&created[0].0)
    }

    /// How often the single source was read.
    fn single_reads(&self) -> usize {
        let created = lock(&self.created);
        assert_eq!(created.len(), 1);
        created[0].1.load(Ordering::SeqCst)
    }
}

impl StatusEventSourceFactory for QueueStatusSourceFactory {
    fn create(&self, _path: &Path) -> Box<dyn StatusEventSource> {
        let queue = EventQueue::default();
        let reads = Arc::new(AtomicUsize::new(0));
        lock(&self.created).push((Arc::clone(&queue), Arc::clone(&reads)));
        Box::new(QueueStatusSource(queue, reads))
    }
}

fn enqueue(queue: &EventQueue, event: StatusEvent) {
    lock(queue).push_back(event);
}

fn event(session_id: &str, name: &str, kind: StatusEventKind) -> StatusEvent {
    StatusEvent {
        envelope: StatusEnvelope {
            v: 1,
            e: name.into(),
            t: Some(SystemTime::now()),
            session_id: session_id.into(),
        },
        kind,
    }
}

fn connected(session_id: &str) -> StatusEvent {
    event(session_id, "connected", StatusEventKind::Connected)
}

fn entered_world(session_id: &str, character_name: &str) -> StatusEvent {
    event(
        session_id,
        "enteredWorld",
        StatusEventKind::EnteredWorld {
            character_id: 0x5000_000A,
            character_name: character_name.into(),
        },
    )
}

fn exited(session_id: &str, code: i32, reason: &str) -> StatusEvent {
    refused(session_id, code, reason, None, false)
}

/// An `exited` naming the server's refusal.
fn refused(
    session_id: &str,
    code: i32,
    reason: &str,
    server_message: Option<&str>,
    account_in_use: bool,
) -> StatusEvent {
    event(
        session_id,
        "exited",
        StatusEventKind::Exited {
            code,
            reason: reason.into(),
            server_message: server_message.map(str::to_owned),
            account_in_use,
        },
    )
}

fn roster(session_id: &str) -> StatusEvent {
    event(
        session_id,
        "characterList",
        StatusEventKind::CharacterList {
            account_name: "testaccount".into(),
            slot_count: 6,
            characters: vec![
                StatusCharacterEntry {
                    id: 0x5000_000A,
                    name: "+Acdream".into(),
                    seconds_greyed_out: 0,
                },
                StatusCharacterEntry {
                    id: 0x5000_000B,
                    name: "+Second".into(),
                    seconds_greyed_out: 0,
                },
            ],
        },
    )
}

// ---- setup -----------------------------------------------------------------

fn always(value: bool) -> Arc<dyn Fn(&Path) -> bool + Send + Sync> {
    Arc::new(move |_| value)
}

fn test_executables() -> LauncherExecutableSet {
    LauncherExecutableSet::new("gui-host", "headless-host", None)
        .with_file_exists(always(true))
        .with_unix_execute_permission(always(true))
}

fn install() -> LauncherInstallRecord {
    LauncherInstallRecord::new("dats", "pak", "", 0, 0)
}

fn windows_capabilities() -> LauncherPlatformCapabilities {
    LauncherPlatformCapabilities::for_operating_system(true, false, false)
}

struct Setup {
    include_character: bool,
    platform: LauncherPlatformCapabilities,
    config: Option<Arc<dyn SessionConfigService>>,
    supervisors: Arc<dyn ProcessSupervisorFactory>,
    statuses: Arc<dyn StatusEventSourceFactory>,
    executables: LauncherExecutableSet,
    barrier: Option<UpdateSessionBarrier>,
    clock: Option<Clock>,
    /// Also saves `TestAccount`, the same account in another case, as a
    /// launcher that compared names exactly could have.
    case_twin: bool,
}

impl Default for Setup {
    fn default() -> Self {
        Self {
            include_character: true,
            platform: windows_capabilities(),
            config: None,
            supervisors: Arc::new(FakeSupervisorFactory::default()),
            statuses: Arc::new(QueueStatusSourceFactory::default()),
            executables: test_executables(),
            barrier: None,
            clock: None,
            case_twin: false,
        }
    }
}

fn profile_path(paths: &PathSet) -> PathBuf {
    paths.config_directory.join("launcher-profiles.json")
}

fn create_orchestrator(root: &TempDir, setup: Setup) -> LauncherOrchestrator {
    let paths = root.path_set();
    let mut store = LauncherProfileStore::new(profile_path(&paths), None).unwrap();
    store.load().unwrap();
    store.add_server("Local ACE", "127.0.0.1", 9000).unwrap();
    store
        .add_account("Local ACE", "testaccount", PASSWORD)
        .unwrap();
    if setup.include_character {
        store
            .add_character(
                "Local ACE",
                "testaccount",
                "+Acdream",
                Some("0x5000000A"),
                LaunchMode::GuiSelect,
                Some(&["ExamplePlugin".to_owned()]),
            )
            .unwrap();
        store
            .set_account_login_commands("Local ACE", "testaccount", &["/vt start".to_owned()])
            .unwrap();
    }
    if setup.case_twin {
        let mut twin = AccountProfile::new("TestAccount", PASSWORD);
        twin.characters.push(CharacterProfile::new("+Twin"));
        store.document_mut().servers[0].accounts.push(twin);
    }
    store.save().unwrap();

    let next = Arc::new(AtomicUsize::new(0));
    let orchestrator = LauncherOrchestrator::with_parts(
        store,
        paths,
        setup.executables,
        Some(install()),
        OrchestratorParts {
            platform: Some(setup.platform),
            config_service: setup.config,
            supervisor_factory: Some(setup.supervisors),
            status_source_factory: Some(setup.statuses),
            session_id_factory: Some(Arc::new(move || {
                format!("s{}", next.fetch_add(1, Ordering::SeqCst) + 1)
            })),
            update_session_barrier: setup.barrier,
            clock: setup.clock,
            ..OrchestratorParts::default()
        },
    );
    orchestrator.load_profiles().unwrap();
    orchestrator
}

fn launch(
    orchestrator: &LauncherOrchestrator,
    character: Option<&str>,
    mode: LaunchMode,
) -> Result<ac_launcher_core::orchestration::LauncherSessionSnapshot, OrchestratorError> {
    orchestrator.launch("Local ACE", "testaccount", character, mode, &none())
}

fn single_session(
    orchestrator: &LauncherOrchestrator,
) -> ac_launcher_core::orchestration::LauncherSessionSnapshot {
    let sessions = orchestrator.snapshot().unwrap().sessions;
    assert_eq!(sessions.len(), 1);
    sessions.into_iter().next().unwrap()
}

// ---- orchestrator ----------------------------------------------------------

#[test]
fn set_show_beta_plugins_persists_to_disk_for_a_fresh_orchestrator_instance() {
    let root = TempDir::new("orchestrator");
    let orchestrator = create_orchestrator(&root, Setup::default());
    assert!(!orchestrator.snapshot().unwrap().show_beta_plugins);

    orchestrator.set_show_beta_plugins(true).unwrap();
    assert!(orchestrator.snapshot().unwrap().show_beta_plugins);

    let paths = root.path_set();
    let reloaded = LauncherOrchestrator::new(
        LauncherProfileStore::new(profile_path(&paths), None).unwrap(),
        paths,
        test_executables(),
        Some(install()),
    );
    reloaded.load_profiles().unwrap();
    assert!(reloaded.snapshot().unwrap().show_beta_plugins);
}

fn thwarg_files() -> ac_launcher_core::profiles::thwarg::ThwargFiles {
    use ac_launcher_core::profiles::thwarg::{ThwargFile, ThwargFiles};
    ThwargFiles {
        accounts: Some("Name=testaccount,Password=other\nName=Newcomer,Password=np\n".into()),
        user_servers: Some(
            "<ArrayOfServerItem><ServerItem><name>Local ACE</name>\
             <connect_string>127.0.0.1:9000</connect_string></ServerItem></ArrayOfServerItem>"
                .into(),
        ),
        characters: vec![
            ThwargFile::new(
                "characters_Local ACE_Newcomer.txt",
                r#"{"Local ACE-Newcomer": {"ZoneId": "newcomer", "CharacterList": [{"Id": 1342177290, "Name": "Fresh Face"}]}}"#,
            ),
            ThwargFile::new(
                "characters_Local ACE_testaccount.txt",
                r#"{"Local ACE-testaccount": {"ZoneId": "testaccount", "CharacterList": [{"Id": 1342177291, "Name": "Second"}]}}"#,
            ),
        ],
        ..ThwargFiles::default()
    }
}

#[test]
fn the_thwarg_import_saves_through_the_profile_store_and_its_preview_does_not() {
    let root = TempDir::new("orchestrator");
    let orchestrator = create_orchestrator(&root, Setup::default());
    let paths = root.path_set();
    let saved = || std::fs::read_to_string(profile_path(&paths));
    let before = saved().unwrap();

    let preview = orchestrator.import_thwarg(&thwarg_files(), false).unwrap();
    assert_eq!(
        preview.accounts_added,
        ["Newcomer on Local ACE: 1 character"]
    );
    assert_eq!(saved().unwrap(), before, "a preview writes nothing");

    let report = orchestrator.import_thwarg(&thwarg_files(), true).unwrap();
    assert_eq!(report, preview);
    let reloaded = LauncherOrchestrator::new(
        LauncherProfileStore::new(profile_path(&paths), None).unwrap(),
        paths.clone(),
        test_executables(),
        Some(install()),
    );
    reloaded.load_profiles().unwrap();
    let server = reloaded.snapshot().unwrap().servers.remove(0);
    let accounts: Vec<(String, Vec<String>)> = server
        .accounts
        .iter()
        .map(|account| {
            (
                account.account_name.clone(),
                account.characters.iter().map(|c| c.name.clone()).collect(),
            )
        })
        .collect();
    assert_eq!(
        accounts,
        [
            (
                "testaccount".into(),
                vec!["+Acdream".into(), "Second".into()]
            ),
            ("Newcomer".into(), vec!["Fresh Face".into()]),
        ]
    );
    // The account already here keeps its password.
    let text = saved().unwrap();
    assert!(text.contains(PASSWORD) && !text.contains("\"other\""));
}

#[test]
fn the_exit_diagnosis_finds_its_line_after_a_lot_of_standard_output() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Gui).unwrap();
    let supervisor = supervisors.single();
    let path = supervisor.spec().output_log_path.unwrap();
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    let mut log = "window: a line the client printed on standard output
"
    .repeat(4000);
    log.push_str(
        "prepared asset package does not match: bake tool 10 != 6
",
    );
    std::fs::write(&path, log).unwrap();
    supervisor.exit(1);
    let error = single_session(&orchestrator).error.unwrap();
    assert!(error.contains("Update the client"), "{error}");
}

#[test]
fn asset_version_crash_produces_actionable_error_without_exposing_stderr() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Gui).unwrap();
    let supervisor = supervisors.single();
    let path = supervisor.spec().output_log_path.unwrap();
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(
        &path,
        format!("prepared asset package does not match: bake tool 10 != 6 {PASSWORD}"),
    )
    .unwrap();
    supervisor.exit(1);
    let error = single_session(&orchestrator).error.unwrap();
    assert!(error.contains("Update the client"), "{error}");
    assert!(!error.contains(PASSWORD));
}

#[test]
fn running_host_holds_shared_update_lease_until_process_terminal_state() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let barrier = UpdateSessionBarrier::new(&root.path_set().data_directory);
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            barrier: Some(barrier.clone()),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Gui).unwrap();

    assert!(barrier.acquire_exclusive().is_err());
    supervisors.single().exit(0);
    let _update = barrier.acquire_exclusive().unwrap();
}

#[test]
fn dispose_keeps_update_lease_until_live_child_is_observed_terminal() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(BlockingStopSupervisorFactory::default());
    let barrier = UpdateSessionBarrier::new(&root.path_set().data_directory);
    let orchestrator = Arc::new(create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            barrier: Some(barrier.clone()),
            ..Setup::default()
        },
    ));
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();
    let supervisor = {
        let created = lock(&supervisors.created);
        assert_eq!(created.len(), 1);
        Arc::clone(&created[0])
    };

    let disposing = Arc::clone(&orchestrator);
    let disposal = std::thread::spawn(move || disposing.dispose());
    assert!(supervisor.stop_entered.wait(Some(Duration::from_secs(5))));

    assert!(!disposal.is_finished());
    assert!(barrier.acquire_exclusive().is_err());

    supervisor.allow_terminal.set();
    disposal.join().unwrap();
    let _update = barrier.acquire_exclusive().unwrap();
    assert_eq!(supervisor.state(), LauncherSessionState::Exited);
    assert!(*lock(&supervisor.disposed));
    supervisors.allow_every_stop();
}

// Not an OpenAC test: disposing stops every session at once (each stop on
// its own thread), not one after another.
#[test]
fn dispose_stops_every_session_at_once() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(BlockingStopSupervisorFactory::default());
    let orchestrator = Arc::new(create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            ..Setup::default()
        },
    ));
    orchestrator
        .add_account("Local ACE", "otheraccount", PASSWORD)
        .unwrap();
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();
    orchestrator
        .launch(
            "Local ACE",
            "otheraccount",
            None,
            LaunchMode::GuiSelect,
            &none(),
        )
        .unwrap();
    let created: Vec<Arc<BlockingStopSupervisor>> = lock(&supervisors.created).clone();
    assert_eq!(created.len(), 2);

    let disposing = Arc::clone(&orchestrator);
    let disposal = std::thread::spawn(move || disposing.dispose());
    // Both stops are entered while neither may finish.
    for supervisor in &created {
        assert!(supervisor.stop_entered.wait(Some(Duration::from_secs(5))));
    }
    assert!(!disposal.is_finished());
    supervisors.allow_every_stop();
    disposal.join().unwrap();
    for supervisor in &created {
        assert!(*lock(&supervisor.disposed));
    }
}

// Not an OpenAC test: once the launcher closes, nothing new starts.
#[test]
fn a_closing_launcher_starts_nothing_new() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            ..Setup::default()
        },
    );
    orchestrator.close_to_new_sessions().unwrap();
    let refused = launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap_err();
    assert_eq!(refused.to_string(), "The launcher is closing.");
    assert!(
        orchestrator
            .probe("Local ACE", "testaccount", &none())
            .is_err()
    );
    let capability = orchestrator
        .account_launch_capability("Local ACE", "testaccount", LaunchMode::Headless)
        .unwrap();
    assert_eq!(
        capability.reason.as_deref(),
        Some("The launcher is closing.")
    );
    assert!(supervisors.created().is_empty());
}

#[test]
fn snapshot_projects_the_full_hierarchy_without_the_credential() {
    let root = TempDir::new("orchestrator");
    let orchestrator = create_orchestrator(&root, Setup::default());

    let snapshot = orchestrator.snapshot().unwrap();

    assert_eq!(snapshot.servers.len(), 1);
    let server = &snapshot.servers[0];
    assert_eq!(server.accounts.len(), 1);
    let account = &server.accounts[0];
    assert_eq!(account.characters.len(), 1);
    assert_eq!(server.name, "Local ACE");
    assert_eq!(account.account_name, "testaccount");
    assert_eq!(account.characters[0].name, "+Acdream");

    // Every field is in the debug form; none holds or names a credential.
    let serialized = format!("{snapshot:?}");
    assert!(!serialized.contains(PASSWORD));
    assert!(!serialized.to_lowercase().contains("password"));
    assert!(!serialized.to_lowercase().contains("credential"));
}

#[test]
fn a_launch_runs_the_accounts_shared_commands_then_the_characters_own() {
    let root = TempDir::new("orchestrator");
    let config = Arc::new(RecordingConfigService::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            config: Some(config.clone()),
            ..Setup::default()
        },
    );
    let original = orchestrator
        .read_profile_text(LauncherTextEditorKind::LogonCommands)
        .unwrap();
    orchestrator
        .save_profile_text(
            LauncherTextEditorKind::LogonCommands,
            "#Local ACE\n##testaccount\n/vt start\n###+Acdream\n/mm ws enable\n",
            &original,
        )
        .unwrap();
    let snapshot = orchestrator.snapshot().unwrap();
    let account = &snapshot.servers[0].accounts[0];
    assert_eq!(account.login_commands, ["/vt start"]);
    assert_eq!(
        account.characters[0].login_commands,
        ["/vt start", "/mm ws enable"]
    );

    launch(&orchestrator, Some("+Acdream"), LaunchMode::Gui).unwrap();

    // The launch's copy of the character keeps its own commands.
    assert_eq!(config.last_character().login_commands, ["/mm ws enable"]);
    let composed = config.last_composed();
    assert_eq!(
        composed.document.sessions[0].login_commands.as_deref(),
        Some(&["/vt start".to_owned(), "/mm ws enable".to_owned()][..])
    );
}

#[test]
fn launch_composes_the_selected_mode_and_spawns_through_the_injected_seam() {
    let root = TempDir::new("orchestrator");
    let config = Arc::new(RecordingConfigService::default());
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            config: Some(config.clone()),
            supervisors: supervisors.clone(),
            statuses: statuses.clone(),
            ..Setup::default()
        },
    );

    let launched = launch(&orchestrator, Some("+Acdream"), LaunchMode::Gui).unwrap();

    assert_eq!(launched.state, LauncherActivityState::Running);
    assert_eq!(config.play_calls(), 1);
    assert_eq!(config.probe_calls(), 0);
    let character = config.last_character();
    assert_eq!(character.launch_mode, LaunchMode::Gui);
    assert_eq!(
        character.plugins.as_deref(),
        Some(&["ExamplePlugin".to_owned()][..])
    );
    assert_eq!(config.last_account_login_commands(), ["/vt start"]);
    assert_eq!(config.last_password(), "");

    let supervisor = supervisors.single();
    let spec = supervisor.spec();
    let composed = config.last_composed();
    assert_eq!(spec.executable_path, PathBuf::from("gui-host"));
    assert_eq!(
        spec.arguments,
        [
            "--session-config".to_owned(),
            composed.config_file_path.display().to_string(),
            "--console".to_owned()
        ]
    );
    assert_eq!(supervisor.password().as_deref(), Some(PASSWORD));
    assert!(!spec.arguments.join(" ").contains(PASSWORD));
    assert!(!serialize(&composed.document).contains(PASSWORD));

    let source = statuses.single();
    enqueue(&source, connected("s1"));
    enqueue(&source, entered_world("s1", "+Acdream"));
    orchestrator.poll_status().unwrap();

    let in_world = single_session(&orchestrator);
    assert_eq!(in_world.state, LauncherActivityState::InWorld);
    assert_eq!(in_world.character_name.as_deref(), Some("+Acdream"));
    assert_eq!(in_world.status, "In world.");
}

#[test]
fn login_command_failure_is_visible_but_does_not_make_the_session_terminal() {
    let root = TempDir::new("orchestrator");
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            statuses: statuses.clone(),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();

    let source = statuses.single();
    enqueue(&source, connected("s1"));
    enqueue(&source, entered_world("s1", "+Acdream"));
    enqueue(
        &source,
        event(
            "s1",
            "loginCommandFailed",
            StatusEventKind::LoginCommandFailed {
                command_index: 2,
                command: "/version".into(),
                error: "not available in the headless host".into(),
            },
        ),
    );
    orchestrator.poll_status().unwrap();

    let session = single_session(&orchestrator);
    assert_eq!(session.state, LauncherActivityState::InWorld);
    assert_eq!(
        session.error.as_deref(),
        Some("Login command 2 failed: not available in the headless host")
    );
    assert_eq!(session.error.as_deref(), Some(session.status.as_str()));
    assert_eq!(session.exit_code, None);
}

#[test]
fn blocked_plugin_notice_survives_later_status_updates() {
    let root = TempDir::new("orchestrator");
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            statuses: statuses.clone(),
            ..Setup::default()
        },
    );
    orchestrator
        .set_plugin_catalog(Some(
            PluginCatalog::parse(
                r#"{
                  "schemaVersion": 1,
                  "plugins": [
                    { "id": "ExamplePlugin", "name": "Example", "author": "Shane Edwards",
                      "description": "Test fixture.", "repo": "shaneedwards/openac-plugin-hello" }
                  ],
                  "blocked": [
                    { "id": "ExamplePlugin", "versions": ["*"], "reason": "test" }
                  ]
                }"#,
            )
            .unwrap(),
        ))
        .unwrap();

    let launched = launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();

    let notice = "Plugin 'ExamplePlugin' is blocked and was not loaded.";
    assert_eq!(launched.plugin_notice.as_deref(), Some(notice));

    let source = statuses.single();
    enqueue(&source, connected("s1"));
    enqueue(&source, entered_world("s1", "+Acdream"));
    orchestrator.poll_status().unwrap();

    let in_world = single_session(&orchestrator);
    assert_eq!(in_world.state, LauncherActivityState::InWorld);
    assert_eq!(in_world.plugin_notice.as_deref(), Some(notice));
}

#[test]
fn account_gui_select_does_not_require_a_cached_character_or_emit_a_selector() {
    let root = TempDir::new("orchestrator");
    let config = Arc::new(RecordingConfigService::default());
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            include_character: false,
            config: Some(config.clone()),
            supervisors: supervisors.clone(),
            ..Setup::default()
        },
    );

    assert!(
        orchestrator
            .account_launch_capability("Local ACE", "testaccount", LaunchMode::GuiSelect)
            .unwrap()
            .is_available
    );

    let launched = launch(&orchestrator, None, LaunchMode::GuiSelect).unwrap();

    assert_eq!(launched.character_name, None);
    let character = config.last_character();
    assert_eq!(character.launch_mode, LaunchMode::GuiSelect);
    assert_eq!(character.name, "");
    let json = serialize(&config.last_composed().document);
    assert!(!json.contains("\"character\""), "{json}");
    assert!(!json.contains(PASSWORD));
    assert_eq!(
        supervisors.single().spec().executable_path,
        PathBuf::from("gui-host")
    );
}

#[test]
fn account_launch_without_a_character_only_accepts_gui_select() {
    for mode in [LaunchMode::Gui, LaunchMode::Headless] {
        let root = TempDir::new("orchestrator");
        let config = Arc::new(RecordingConfigService::default());
        let supervisors = Arc::new(FakeSupervisorFactory::default());
        let orchestrator = create_orchestrator(
            &root,
            Setup {
                include_character: false,
                config: Some(config.clone()),
                supervisors: supervisors.clone(),
                ..Setup::default()
            },
        );

        assert!(matches!(
            launch(&orchestrator, None, mode),
            Err(OrchestratorError::Operation(_))
        ));
        assert_eq!(config.play_calls(), 0);
        assert!(supervisors.created().is_empty());
    }
}

#[test]
fn probe_is_refused_while_the_account_has_a_running_launcher_activity() {
    let root = TempDir::new("orchestrator");
    let orchestrator = create_orchestrator(&root, Setup::default());
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();

    let capability = orchestrator
        .probe_capability("Local ACE", "testaccount")
        .unwrap();

    assert!(!capability.is_available);
    assert!(capability.reason.unwrap().to_lowercase().contains("stop"));
    assert!(matches!(
        orchestrator.probe("Local ACE", "testaccount", &none()),
        Err(OrchestratorError::Operation(_))
    ));
}

#[test]
fn launch_is_refused_while_the_account_probe_is_running() {
    let root = TempDir::new("orchestrator");
    let orchestrator = create_orchestrator(&root, Setup::default());
    orchestrator
        .probe("Local ACE", "testaccount", &none())
        .unwrap();

    let capability = orchestrator
        .account_launch_capability("Local ACE", "testaccount", LaunchMode::Headless)
        .unwrap();

    assert!(!capability.is_available);
    assert!(matches!(
        launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless),
        Err(OrchestratorError::Operation(_))
    ));
}

// OpenAC LauncherOrchestratorTests.ASecondSpellingOfARunningAccountIsTheSameAccount.
#[test]
fn a_second_session_is_refused_for_the_same_account_in_another_case() {
    let root = TempDir::new("orchestrator");
    let orchestrator = create_orchestrator(&root, Setup::default());
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();

    let capability = orchestrator
        .account_launch_capability("Local ACE", "TESTACCOUNT", LaunchMode::Headless)
        .unwrap();
    assert!(!capability.is_available);
    let refused = orchestrator.launch(
        "Local ACE",
        "TESTACCOUNT",
        Some("+Acdream"),
        LaunchMode::Headless,
        &none(),
    );
    assert!(
        matches!(&refused, Err(OrchestratorError::Operation(message)) if message.contains("already running")),
        "{refused:?}"
    );
    assert_eq!(orchestrator.snapshot().unwrap().sessions.len(), 1);
}

// OpenAC LauncherOrchestratorTests.ASecondSpellingOfARunningAccountIsTheSameAccount.
#[test]
fn two_saved_cases_of_one_account_cannot_run_at_once() {
    let root = TempDir::new("orchestrator");
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            case_twin: true,
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();

    let snapshot = orchestrator.snapshot().unwrap();
    assert!(
        snapshot.servers[0]
            .accounts
            .iter()
            .all(|account| account.has_running_activity)
    );
    assert!(
        orchestrator
            .account_launch_capabilities(LaunchMode::Headless)
            .unwrap()
            .values()
            .all(|capability| !capability.is_available)
    );
    assert!(
        !orchestrator
            .probe_capability("Local ACE", "TestAccount")
            .unwrap()
            .is_available
    );
    assert!(matches!(
        orchestrator.remove_account("Local ACE", "TestAccount"),
        Err(OrchestratorError::Operation(_))
    ));
    let refused = orchestrator.launch(
        "Local ACE",
        "TestAccount",
        Some("+Twin"),
        LaunchMode::Headless,
        &none(),
    );
    assert!(
        matches!(refused, Err(OrchestratorError::Operation(_))),
        "{refused:?}"
    );
    assert_eq!(orchestrator.snapshot().unwrap().sessions.len(), 1);
}

#[test]
fn concurrent_play_reservations_allow_exactly_one_activity_per_account() {
    let root = TempDir::new("orchestrator");
    let orchestrator = Arc::new(create_orchestrator(&root, Setup::default()));

    let attempts: Vec<_> = (0..2)
        .map(|_| {
            let orchestrator = Arc::clone(&orchestrator);
            std::thread::spawn(move || {
                launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless)
            })
        })
        .collect();
    let results: Vec<_> = attempts
        .into_iter()
        .map(|attempt| attempt.join().unwrap())
        .collect();

    assert_eq!(results.iter().filter(|result| result.is_ok()).count(), 1);
    assert_eq!(
        results
            .iter()
            .filter(|result| matches!(result, Err(OrchestratorError::Operation(_))))
            .count(),
        1
    );
    assert_eq!(orchestrator.snapshot().unwrap().sessions.len(), 1);
}

fn reloaded_character_count(root: &TempDir) -> usize {
    let mut reloaded = LauncherProfileStore::new(profile_path(&root.path_set()), None).unwrap();
    assert!(reloaded.load().unwrap());
    reloaded.document().servers[0].accounts[0].characters.len()
}

#[test]
fn an_ordinary_login_folds_the_reported_roster_into_the_store() {
    let root = TempDir::new("orchestrator");
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            include_character: false,
            statuses: statuses.clone(),
            ..Setup::default()
        },
    );
    launch(&orchestrator, None, LaunchMode::GuiSelect).unwrap();

    enqueue(&statuses.single(), roster("s1"));
    orchestrator.poll_status().unwrap();

    let snapshot = orchestrator.snapshot().unwrap();
    assert_eq!(snapshot.servers[0].accounts[0].characters.len(), 2);
    // Persisted, so the tree still shows them on the next launcher start.
    assert_eq!(reloaded_character_count(&root), 2);
}

#[test]
fn probe_uses_the_probe_shape_and_folds_the_reported_roster_into_the_store() {
    let root = TempDir::new("orchestrator");
    let config = Arc::new(RecordingConfigService::default());
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            include_character: false,
            config: Some(config.clone()),
            statuses: statuses.clone(),
            ..Setup::default()
        },
    );

    let probe = orchestrator
        .probe("Local ACE", "testaccount", &none())
        .unwrap();

    assert_eq!(probe.kind, LauncherActivityKind::Probe);
    assert_eq!(config.play_calls(), 0);
    assert_eq!(config.probe_calls(), 1);
    let json = serialize(&config.last_composed().document);
    assert!(json.contains("\"mode\": \"probe\""), "{json}");
    assert!(!json.contains("\"character\""));
    assert!(!json.contains(PASSWORD));

    enqueue(&statuses.single(), roster("s1"));
    orchestrator.poll_status().unwrap();

    let snapshot = orchestrator.snapshot().unwrap();
    let characters = &snapshot.servers[0].accounts[0].characters;
    assert_eq!(characters.len(), 2);
    assert!(
        characters
            .iter()
            .any(|character| character.name == "+Acdream")
    );
    assert!(
        characters
            .iter()
            .any(|character| character.name == "+Second")
    );
    assert_eq!(reloaded_character_count(&root), 2);
}

#[test]
fn unsupported_platform_rejects_both_graphical_modes_before_composition_or_spawn() {
    let root = TempDir::new("orchestrator");
    let config = Arc::new(RecordingConfigService::default());
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            platform: LauncherPlatformCapabilities::for_operating_system(false, false, false),
            config: Some(config.clone()),
            supervisors: supervisors.clone(),
            ..Setup::default()
        },
    );

    for mode in [LaunchMode::Gui, LaunchMode::GuiSelect] {
        match launch(&orchestrator, Some("+Acdream"), mode) {
            Err(OrchestratorError::Operation(message)) => {
                assert!(message.contains("Windows, Linux, and macOS"), "{message}");
            }
            other => panic!("expected a refusal, got {other:?}"),
        }
    }

    assert!(
        !orchestrator
            .launch_capability(LaunchMode::Headless)
            .unwrap()
            .is_available
    );
    assert_eq!(config.play_calls(), 0);
    assert!(supervisors.created().is_empty());
}

#[test]
fn missing_co_deployed_hosts_disable_actions_before_composition_or_spawn() {
    let root = TempDir::new("orchestrator");
    let config = Arc::new(RecordingConfigService::default());
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            config: Some(config.clone()),
            supervisors: supervisors.clone(),
            executables: LauncherExecutableSet::new("missing-gui", "missing-headless", None)
                .with_file_exists(always(false)),
            ..Setup::default()
        },
    );

    let gui = orchestrator
        .account_launch_capability("Local ACE", "testaccount", LaunchMode::GuiSelect)
        .unwrap();
    let headless = orchestrator
        .account_launch_capability("Local ACE", "testaccount", LaunchMode::Headless)
        .unwrap();
    let probe = orchestrator
        .probe_capability("Local ACE", "testaccount")
        .unwrap();

    assert!(!gui.is_available);
    assert!(gui.reason.unwrap().contains("missing-gui"));
    assert!(!headless.is_available);
    assert!(headless.reason.unwrap().contains("missing-headless"));
    assert!(!probe.is_available);
    assert!(matches!(
        launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless),
        Err(OrchestratorError::Operation(_))
    ));
    assert_eq!(config.play_calls(), 0);
    assert!(supervisors.created().is_empty());
}

#[test]
fn process_exit_is_terminal_and_late_status_cannot_resurrect_the_account() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            statuses: statuses.clone(),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();

    supervisors.single().exit(23);
    let source = statuses.single();
    // As in OpenAC: reading continues until the client's own exit report.
    orchestrator.poll_status().unwrap();
    orchestrator.poll_status().unwrap();
    assert_eq!(statuses.single_reads(), 2);
    enqueue(&source, connected("s1"));
    enqueue(&source, entered_world("s1", "+Acdream"));
    enqueue(&source, exited("s1", 23, "host crash detail"));
    orchestrator.poll_status().unwrap();

    let session = single_session(&orchestrator);
    assert_eq!(session.state, LauncherActivityState::Exited);
    assert_eq!(session.exit_code, Some(23));
    assert!(
        session.status.contains("host crash detail"),
        "{}",
        session.status
    );
    assert!(
        orchestrator
            .probe_capability("Local ACE", "testaccount")
            .unwrap()
            .is_available
    );
    // Both the process exit and the exit report arrived: nothing is read again.
    let reads = statuses.single_reads();
    orchestrator.poll_status().unwrap();
    orchestrator.poll_status().unwrap();
    assert_eq!(statuses.single_reads(), reads);
}

#[test]
fn host_exit_reason_survives_the_later_process_exit_callback() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            statuses: statuses.clone(),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();

    enqueue(
        &statuses.single(),
        exited("s1", 0, "graceful host shutdown"),
    );
    orchestrator.poll_status().unwrap();
    // As in OpenAC: the exit report alone does not free the account
    // while the process lives.
    let before_process_exit = single_session(&orchestrator);
    assert!(!before_process_exit.is_active());
    assert_eq!(before_process_exit.has_live_process, Some(true));
    assert!(before_process_exit.is_process_or_start_active());
    assert!(
        !orchestrator
            .account_launch_capability("Local ACE", "testaccount", LaunchMode::Headless)
            .unwrap()
            .is_available
    );
    orchestrator.clear_finished_sessions().unwrap();
    assert_eq!(orchestrator.snapshot().unwrap().sessions.len(), 1);
    let reads = statuses.single_reads();
    orchestrator.poll_status().unwrap();
    assert_eq!(statuses.single_reads(), reads + 1);
    supervisors.single().exit(0);
    orchestrator.poll_status().unwrap();
    assert_eq!(statuses.single_reads(), reads + 1);

    let session = single_session(&orchestrator);
    assert_eq!(session.state, LauncherActivityState::Exited);
    assert!(session.status.contains("graceful host shutdown"));
    assert!(!session.is_process_or_start_active());
    assert!(
        orchestrator
            .account_launch_capability("Local ACE", "testaccount", LaunchMode::Headless)
            .unwrap()
            .is_available
    );
}

// Not an OpenAC test: a failed client says why in plain words, not just its
// exit code; the server's refusal is shown in the server's own words, even
// when the process is seen to end before the client's report is read.
#[test]
fn a_failed_client_says_why_in_plain_words() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let start = SystemTime::UNIX_EPOCH + Duration::from_secs(1_800_000_000);
    let now = Arc::new(Mutex::new(start));
    let clock_now = Arc::clone(&now);
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            statuses: statuses.clone(),
            clock: Some(Arc::new(move || *lock(&clock_now))),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();
    supervisors.single().exit(-1);
    assert_eq!(
        single_session(&orchestrator).error.as_deref(),
        Some("The client closed unexpectedly. (exit code -1)")
    );
    orchestrator.clear_finished_sessions().unwrap();
    // Clearing the list does not lift the server's hold.
    assert!(
        launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless)
            .unwrap_err()
            .to_string()
            .starts_with("The server may still hold this account.")
    );
    *lock(&now) = start + SERVER_SESSION_HOLD;

    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();
    let refused_supervisor = Arc::clone(&supervisors.created()[1]);
    refused_supervisor.exit(6);
    let queue = {
        let created = lock(&statuses.created);
        Arc::clone(&created[1].0)
    };
    enqueue(
        &queue,
        refused(
            "s2",
            6,
            "the server ended the session: Another account is already logged on from this \
             client. (error 0x1)",
            Some("Another account is already logged on from this client."),
            true,
        ),
    );
    orchestrator.poll_status().unwrap();
    let session = single_session(&orchestrator);
    assert_eq!(
        session.error.as_deref(),
        Some("Another account is already logged on from this client. (exit code 6)")
    );
    assert_eq!(
        session.server_message.as_deref(),
        Some("Another account is already logged on from this client.")
    );
    assert!(session.account_in_use);
}

#[test]
fn start_failure_is_visible_but_redacts_the_credential_everywhere() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::failing(Arc::new(|password| {
        std::io::Error::other(format!(
            "simulated pipe failure containing {}",
            password.unwrap_or_default()
        ))
    })));
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            ..Setup::default()
        },
    );

    let message = match launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless) {
        Err(OrchestratorError::Operation(message)) => message,
        other => panic!("expected a failure, got {other:?}"),
    };

    assert!(!message.contains(PASSWORD));
    assert!(message.contains("[redacted]"), "{message}");
    let failed = single_session(&orchestrator);
    assert_eq!(failed.state, LauncherActivityState::Failed);
    assert!(!failed.error.unwrap_or_default().contains(PASSWORD));
}

#[test]
fn pre_cancelled_launch_never_composes_or_spawns_and_ends_cancelled() {
    let root = TempDir::new("orchestrator");
    let config = Arc::new(RecordingConfigService::default());
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            config: Some(config.clone()),
            supervisors: supervisors.clone(),
            ..Setup::default()
        },
    );
    let cancellation = CancelToken::new();
    cancellation.cancel();

    assert!(matches!(
        orchestrator.launch(
            "Local ACE",
            "testaccount",
            Some("+Acdream"),
            LaunchMode::Headless,
            &cancellation
        ),
        Err(OrchestratorError::Cancelled)
    ));

    assert_eq!(config.play_calls(), 0);
    assert!(supervisors.created().is_empty());
    assert_eq!(
        single_session(&orchestrator).state,
        LauncherActivityState::Cancelled
    );
}

#[test]
fn stop_runs_through_the_supervisor_and_makes_the_account_probeable_again() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            ..Setup::default()
        },
    );
    let launched = launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();

    orchestrator
        .stop_session(&launched.session_id, Duration::from_millis(10), &none())
        .unwrap();

    assert_eq!(supervisors.single().stop_calls(), 1);
    assert_eq!(
        single_session(&orchestrator).state,
        LauncherActivityState::Exited
    );
    assert!(
        orchestrator
            .probe_capability("Local ACE", "testaccount")
            .unwrap()
            .is_available
    );
}

// Not an OpenAC test: a client in the world that writes heartbeats and then
// falls silent for 30 s shows as not responding until its next line; a
// client that never wrote one (an older build) is not judged.
#[test]
fn a_silent_client_in_the_world_shows_as_not_responding() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let start = SystemTime::UNIX_EPOCH + Duration::from_secs(1_800_000_000);
    let now = Arc::new(Mutex::new(start));
    let clock_now = Arc::clone(&now);
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            statuses: statuses.clone(),
            clock: Some(Arc::new(move || *lock(&clock_now))),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();
    let queue = statuses.single();
    enqueue(&queue, connected("s1"));
    enqueue(&queue, entered_world("s1", "+Acdream"));
    orchestrator.poll_status().unwrap();
    // No heartbeat yet: silence is not judged.
    *lock(&now) = start + Duration::from_secs(60);
    orchestrator.poll_status().unwrap();
    assert!(!single_session(&orchestrator).not_responding);

    let heartbeat = |queue: &EventQueue| {
        enqueue(queue, event("s1", "heartbeat", StatusEventKind::Heartbeat));
    };
    heartbeat(&queue);
    orchestrator.poll_status().unwrap();
    *lock(&now) = start + Duration::from_secs(60 + 29);
    orchestrator.poll_status().unwrap();
    let session = single_session(&orchestrator);
    assert!(!session.not_responding);
    assert_eq!(session.status, "In world.");

    *lock(&now) = start + Duration::from_secs(60 + 30);
    orchestrator.poll_status().unwrap();
    let session = single_session(&orchestrator);
    assert!(session.not_responding);
    assert_eq!(session.state, LauncherActivityState::InWorld);
    assert_eq!(session.status, "Not responding.");
    assert_eq!(supervisors.single().stop_calls(), 0, "detection only");

    heartbeat(&queue);
    orchestrator.poll_status().unwrap();
    let session = single_session(&orchestrator);
    assert!(!session.not_responding);
    assert_eq!(session.status, "In world.");
}

// Not an OpenAC test: a Stop that has to kill the client is a stop, not a
// crash (no crash error), but the server may still hold the login, so the
// hold applies.
#[test]
fn a_stop_that_has_to_kill_is_a_stop_not_a_crash() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            ..Setup::default()
        },
    );
    let launched = launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();
    supervisors.single().ignore_stop();
    orchestrator
        .stop_session(&launched.session_id, Duration::from_millis(10), &none())
        .unwrap();

    let session = single_session(&orchestrator);
    assert_eq!(session.state, LauncherActivityState::Exited);
    assert_eq!(session.stop, Some(LauncherSessionStop::Forced));
    assert_eq!(session.error, None);
    assert_eq!(
        session.status,
        "Stopped (did not respond, closed by force)."
    );
    let hold = orchestrator
        .account_launch_capability("Local ACE", "testaccount", LaunchMode::Headless)
        .unwrap();
    assert!(hold.held_for.is_some(), "{hold:?}");
}

// OpenAC LauncherOrchestratorTests.BatchAvailabilityChecksHostFilesOnceForManyAccountsAndSeesChanges.
#[test]
fn batch_availability_checks_host_files_once_for_many_accounts_and_sees_changes() {
    let root = TempDir::new("orchestrator");
    let reads = Arc::new(AtomicUsize::new(0));
    let exists = Arc::new(std::sync::atomic::AtomicBool::new(true));
    let (counter, present) = (Arc::clone(&reads), Arc::clone(&exists));
    let executables = LauncherExecutableSet::new("gui-host", "headless-host", None)
        .with_file_exists(Arc::new(move |_| {
            counter.fetch_add(1, Ordering::SeqCst);
            present.load(Ordering::SeqCst)
        }))
        .with_unix_execute_permission(always(true));
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            executables,
            ..Setup::default()
        },
    );
    for index in 0..50 {
        orchestrator
            .add_account("Local ACE", &format!("account{index}"), "test-only")
            .unwrap();
    }
    reads.store(0, Ordering::SeqCst);
    let available = orchestrator
        .account_launch_capabilities(LaunchMode::Headless)
        .unwrap();
    assert_eq!(available.len(), 51);
    assert!(available.values().all(|capability| capability.is_available));
    assert!((1..=2).contains(&reads.load(Ordering::SeqCst)));
    exists.store(false, Ordering::SeqCst);
    let missing = orchestrator
        .account_launch_capabilities(LaunchMode::Headless)
        .unwrap();
    assert!(missing.values().all(|capability| !capability.is_available));
    assert_eq!(
        orchestrator
            .account_launch_capability("Local ACE", "testaccount", LaunchMode::Headless)
            .unwrap(),
        missing[&("Local ACE".to_owned(), "testaccount".to_owned())]
    );
    exists.store(true, Ordering::SeqCst);
    orchestrator.set_install_record(None).unwrap();
    assert!(
        orchestrator
            .account_launch_capabilities(LaunchMode::Headless)
            .unwrap()
            .values()
            .all(|capability| !capability.is_available)
    );
}

// OpenAC LauncherOrchestratorTests.BatchAvailabilityPreservesActiveAndReconnectRefusals (with
// the process-exit step).
#[test]
fn batch_availability_preserves_active_and_reconnect_refusals() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            statuses: statuses.clone(),
            ..Setup::default()
        },
    );
    let check = |reason: &str| {
        for mode in [LaunchMode::Gui, LaunchMode::GuiSelect, LaunchMode::Headless] {
            let single = orchestrator
                .account_launch_capability("Local ACE", "testaccount", mode)
                .unwrap();
            let batch = orchestrator.account_launch_capabilities(mode).unwrap()
                [&("Local ACE".to_owned(), "testaccount".to_owned())]
                .clone();
            assert!(!batch.is_available);
            assert!(
                batch.reason.as_deref().unwrap().contains(reason),
                "{batch:?}"
            );
            assert!(!single.is_available);
            assert!(
                single.reason.as_deref().unwrap().contains(reason),
                "{single:?}"
            );
        }
    };
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();
    check("Stop the running");
    enqueue(&statuses.single(), exited("s1", 23, "connection lost"));
    orchestrator.poll_status().unwrap();
    check("Stop the running");
    supervisors.single().exit(23);
    assert!(!single_session(&orchestrator).is_process_or_start_active());
    check("Ready in");
}

// OpenAC LauncherOrchestratorTests.StopDuringStartupRemainsOwnedUntilCancelledOrFailedStartFinishes.
#[test]
fn stop_during_startup_remains_owned_until_cancelled_or_failed_start_finishes() {
    for fail in [false, true] {
        let root = TempDir::new("orchestrator");
        let entered = Arc::new(Signal::default());
        let release = Arc::new(Signal::default());
        let (hook_entered, hook_release) = (Arc::clone(&entered), Arc::clone(&release));
        let config = Arc::new(RecordingConfigService {
            before_compose: Some(Box::new(move || {
                hook_entered.set();
                assert!(hook_release.wait(Some(Duration::from_secs(5))));
                if fail {
                    Err(std::io::Error::other("Fixture configuration failed."))
                } else {
                    Ok(())
                }
            })),
            ..RecordingConfigService::default()
        });
        let orchestrator = Arc::new(create_orchestrator(
            &root,
            Setup {
                config: Some(config),
                ..Setup::default()
            },
        ));
        let launching = Arc::clone(&orchestrator);
        let launch = std::thread::spawn(move || {
            launching.launch(
                "Local ACE",
                "testaccount",
                Some("+Acdream"),
                LaunchMode::Headless,
                &none(),
            )
        });
        assert!(entered.wait(Some(Duration::from_secs(5))));
        let starting = single_session(&orchestrator);
        assert!(starting.startup_in_flight);
        assert_eq!(starting.has_live_process, Some(false));
        orchestrator
            .stop_session(&starting.session_id, Duration::ZERO, &none())
            .unwrap();
        assert!(single_session(&orchestrator).is_process_or_start_active());
        orchestrator.clear_finished_sessions().unwrap();
        assert_eq!(orchestrator.snapshot().unwrap().sessions.len(), 1);
        release.set();
        let result = launch.join().unwrap();
        if fail {
            assert!(
                matches!(result, Err(OrchestratorError::Operation(_))),
                "{result:?}"
            );
        } else {
            assert!(
                matches!(result, Err(OrchestratorError::Cancelled)),
                "{result:?}"
            );
        }
        let finished = single_session(&orchestrator);
        assert!(!finished.startup_in_flight);
        assert!(!finished.is_process_or_start_active());
    }
}

// OpenAC LauncherOrchestratorTests.StopStillReachesProcessAfterHostAlreadyReportedExit.
#[test]
fn stop_still_reaches_process_after_host_already_reported_exit() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            statuses: statuses.clone(),
            ..Setup::default()
        },
    );
    let started = launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();
    enqueue(&statuses.single(), exited(&started.session_id, 0, "done"));
    orchestrator.poll_status().unwrap();
    orchestrator
        .stop_session(&started.session_id, Duration::ZERO, &none())
        .unwrap();
    assert_eq!(supervisors.single().stop_calls(), 1);
    assert!(!single_session(&orchestrator).is_process_or_start_active());
}

// OpenAC LauncherOrchestratorTests.FailedStartupWithLiveChildRetainsAccountAndUpdateOwnership.
#[test]
fn failed_startup_with_live_child_retains_account_and_update_ownership() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory {
        fail_after_started: true,
        ..FakeSupervisorFactory::default()
    });
    let paths = root.path_set();
    let barrier = UpdateSessionBarrier::new(&paths.data_directory);
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            barrier: Some(barrier.clone()),
            ..Setup::default()
        },
    );
    assert!(matches!(
        launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless),
        Err(OrchestratorError::Operation(_))
    ));
    let failed = single_session(&orchestrator);
    assert_eq!(failed.state, LauncherActivityState::Failed);
    assert!(!failed.startup_in_flight);
    assert_eq!(failed.has_live_process, Some(true));
    assert!(failed.is_process_or_start_active());
    assert!(barrier.acquire_exclusive().is_err());
    orchestrator.clear_finished_sessions().unwrap();
    assert_eq!(orchestrator.snapshot().unwrap().sessions.len(), 1);
    supervisors.single().exit(23);
    assert!(!single_session(&orchestrator).is_process_or_start_active());
    let _update = barrier.acquire_exclusive().unwrap();
}

// Not an OpenAC test: the reconnect hold OpenAC's orchestrator applies after
// an unclean exit (here 30 s: one login attempt makes the server drop a
// stale login), checked with a controlled clock; a launch obeys it too.
#[test]
fn an_unclean_exit_holds_the_account_for_thirty_seconds() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let start = SystemTime::UNIX_EPOCH + Duration::from_secs(1_800_000_000);
    let now = Arc::new(Mutex::new(start));
    let clock_now = Arc::clone(&now);
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            clock: Some(Arc::new(move || *lock(&clock_now))),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();
    supervisors.single().exit(1);

    let held = orchestrator
        .account_launch_capability("Local ACE", "testaccount", LaunchMode::Headless)
        .unwrap();
    assert!(!held.is_available);
    assert_eq!(held.held_for, Some(Duration::from_secs(30)));
    assert_eq!(
        held.reason.as_deref(),
        Some("The server may still hold this account. Ready in 30 s.")
    );
    let refused = launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap_err();
    assert_eq!(
        refused.to_string(),
        "The server may still hold this account. Ready in 30 s."
    );
    assert_eq!(supervisors.created().len(), 1, "nothing started");

    *lock(&now) = start + Duration::from_millis(500);
    let reason = orchestrator
        .account_launch_capability("Local ACE", "testaccount", LaunchMode::Headless)
        .unwrap()
        .reason
        .unwrap();
    assert!(reason.ends_with("Ready in 30 s."), "{reason}");
    *lock(&now) = start + Duration::from_secs(5);
    let reason = orchestrator
        .account_launch_capability("Local ACE", "testaccount", LaunchMode::Headless)
        .unwrap()
        .reason
        .unwrap();
    assert!(reason.ends_with("Ready in 25 s."), "{reason}");

    *lock(&now) = start + SERVER_SESSION_HOLD;
    assert_eq!(SERVER_SESSION_HOLD, Duration::from_secs(30));
    assert!(
        orchestrator
            .account_launch_capability("Local ACE", "testaccount", LaunchMode::Headless)
            .unwrap()
            .is_available
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();
}

// ---- profile text while sessions run ----------------------------------------

// OpenAC LauncherOrchestratorTests
// LogonCommandsAndAccountsCanBeEditedWhileASessionRunsButARunningAccountStays.
#[test]
fn logon_commands_and_accounts_can_be_edited_while_a_session_runs_but_a_running_account_stays() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Gui).unwrap();

    let commands = orchestrator
        .read_profile_text(LauncherTextEditorKind::LogonCommands)
        .unwrap();
    orchestrator
        .save_profile_text(
            LauncherTextEditorKind::LogonCommands,
            "#Local ACE\n##testaccount\n/vt start\n",
            &commands,
        )
        .unwrap();
    let accounts = orchestrator
        .read_profile_text(LauncherTextEditorKind::Accounts)
        .unwrap();
    orchestrator
        .save_profile_text(
            LauncherTextEditorKind::Accounts,
            &format!("#Local ACE\nName=testaccount,Password={PASSWORD},Profiles=Bots\n"),
            &accounts,
        )
        .unwrap();

    let snapshot = orchestrator.snapshot().unwrap();
    let account = &snapshot.servers[0].accounts[0];
    assert_eq!(account.login_commands, ["/vt start"]);
    assert_eq!(account.profiles, ["Bots"]);
    let before = orchestrator
        .read_profile_text(LauncherTextEditorKind::Accounts)
        .unwrap();
    let refused = orchestrator
        .save_profile_text(LauncherTextEditorKind::Accounts, "#Local ACE\n", &before)
        .unwrap_err();
    assert!(refused.to_string().contains("Stop testaccount's session"));
    assert_eq!(
        orchestrator
            .read_profile_text(LauncherTextEditorKind::Accounts)
            .unwrap(),
        before
    );
}

// ---- platform capabilities --------------------------------------------------

#[test]
fn macos_provides_both_launcher_modes() {
    let capabilities = LauncherPlatformCapabilities::for_operating_system(false, false, true);

    assert!(capabilities.is_macos);
    assert!(!capabilities.is_windows);
    assert!(!capabilities.is_linux);
    assert_eq!(capabilities.platform_name, "macOS");
    assert!(capabilities.can_launch_graphical_client);
    assert!(capabilities.can_run_headless);
    for mode in [LaunchMode::Gui, LaunchMode::GuiSelect, LaunchMode::Headless] {
        assert!(capabilities.for_launch_mode(mode).is_available);
    }
}

#[test]
fn an_unknown_platform_launches_nothing_and_says_why() {
    let capabilities = LauncherPlatformCapabilities::for_operating_system(false, false, false);
    assert_eq!(
        capabilities
            .for_launch_mode(LaunchMode::Gui)
            .reason
            .as_deref(),
        Some(UNSUPPORTED_PLATFORM_GRAPHICAL_LAUNCH_DISABLED_REASON)
    );
    assert!(
        !capabilities
            .for_launch_mode(LaunchMode::Headless)
            .is_available
    );
}

// ---- executable set --------------------------------------------------------

fn program_name() -> String {
    format!("openac{}", std::env::consts::EXE_SUFFIX)
}

#[cfg(unix)]
fn set_mode(path: &Path, mode: u32) {
    use std::os::unix::fs::PermissionsExt;
    std::fs::set_permissions(path, std::fs::Permissions::from_mode(mode)).unwrap();
}

#[test]
fn from_directory_resolves_the_published_co_deployment_layout() {
    let root = TempDir::new("launcher-layout");
    let program = root.join(program_name());
    std::fs::write(&program, b"").unwrap();
    #[cfg(unix)]
    set_mode(&program, 0o755);

    let set = LauncherExecutableSet::from_directory(&root.path);
    let paths = set.paths().unwrap();
    let session = Path::new("session.json");

    assert_eq!(
        paths.working_directory.as_deref(),
        Some(ac_launcher_core::fsutil::full_path(&root.path).as_path())
    );
    // One program serves both kinds of session.
    assert_eq!(paths.graphical_host_path, program);
    assert_eq!(paths.headless_host_path, program);
    for mode in [LaunchMode::Gui, LaunchMode::GuiSelect, LaunchMode::Headless] {
        assert!(set.availability(mode).is_available);
    }
    assert_eq!(
        set.create_play_spec(LaunchMode::GuiSelect, session, None)
            .unwrap()
            .executable_path,
        program
    );
    assert_eq!(
        set.create_probe_spec(session, None)
            .unwrap()
            .executable_path,
        program
    );
    // Every kind stops on the console stop (OpenAC closes a graphical
    // client's window instead; a deliberate divergence).
    for mode in [LaunchMode::Gui, LaunchMode::GuiSelect, LaunchMode::Headless] {
        assert!(
            set.create_play_spec(mode, session, None)
                .unwrap()
                .supports_console_graceful_stop
        );
    }
    assert!(
        set.create_probe_spec(session, None)
            .unwrap()
            .supports_console_graceful_stop
    );

    // A windowless session is started with its console on and its input
    // left open, so the launcher's console window can talk to it, and with
    // window support, so its row can open a window on it. A probe is not.
    let headless = set
        .create_play_spec(LaunchMode::Headless, session, None)
        .unwrap();
    assert!(
        headless
            .arguments
            .iter()
            .any(|argument| argument == "--console")
    );
    assert!(headless.keep_standard_input_open);
    assert!(headless.environment.is_empty());
    let probe = set.create_probe_spec(session, None).unwrap();
    assert!(
        !probe
            .arguments
            .iter()
            .any(|argument| argument == "--console")
    );
    assert!(!probe.keep_standard_input_open);
    assert_eq!(
        probe.environment,
        [("OPENAC_HEADLESS".to_owned(), "1".to_owned())]
    );
    // A graphical launch starts with its console too, so its row can close
    // the window and open it again.
    for mode in [LaunchMode::Gui, LaunchMode::GuiSelect] {
        let windowed = set.create_play_spec(mode, session, None).unwrap();
        assert_eq!(
            windowed.arguments,
            [
                "--session-config".to_owned(),
                session.display().to_string(),
                "--console".to_owned()
            ]
        );
        assert!(windowed.keep_standard_input_open);
        assert!(windowed.environment.is_empty());
    }
}

#[test]
fn missing_published_hosts_have_specific_unavailable_reasons() {
    let root = TempDir::new("launcher-layout");
    let set = LauncherExecutableSet::from_directory(&root.path);
    let paths = set.paths().unwrap();

    let gui = set.availability(LaunchMode::Gui);
    let probe = set.availability(LaunchMode::Headless);

    assert!(!gui.is_available);
    let reason = gui.reason.unwrap();
    assert!(reason.contains("graphical client"));
    assert!(reason.contains(&paths.graphical_host_path.display().to_string()));
    assert!(!probe.is_available);
    let reason = probe.reason.unwrap();
    assert!(reason.contains("headless host"));
    assert!(reason.contains(&paths.headless_host_path.display().to_string()));
    let session = Path::new("session.json");
    assert!(matches!(
        set.create_play_spec(LaunchMode::Gui, session, None),
        Err(OrchestratorError::Operation(_))
    ));
    assert!(matches!(
        set.create_probe_spec(session, None),
        Err(OrchestratorError::Operation(_))
    ));
}

#[cfg(unix)]
#[test]
fn unix_requires_execute_permission_for_the_co_deployed_host() {
    let root = TempDir::new("launcher-layout");
    let program = root.join(program_name());
    std::fs::write(&program, b"").unwrap();
    set_mode(&program, 0o644);
    let set = LauncherExecutableSet::from_directory(&root.path);

    let gui = set.availability(LaunchMode::Gui);
    let headless = set.availability(LaunchMode::Headless);

    assert!(!gui.is_available);
    let reason = gui.reason.unwrap();
    assert!(reason.contains("not executable"));
    assert!(reason.contains("chmod +x"));
    assert!(!headless.is_available);
    assert!(headless.reason.unwrap().contains("not executable"));
    let session = Path::new("session.json");
    assert!(
        set.create_play_spec(LaunchMode::GuiSelect, session, None)
            .is_err()
    );
    assert!(set.create_probe_spec(session, None).is_err());

    set_mode(&program, 0o755);
    assert!(set.availability(LaunchMode::GuiSelect).is_available);
    assert!(set.availability(LaunchMode::Headless).is_available);
}

#[test]
fn availability_requires_execute_permission_only_on_unix() {
    let set = LauncherExecutableSet::new("graphical.exe", "headless.exe", None)
        .with_file_exists(always(true))
        .with_unix_execute_permission(always(false));

    assert_eq!(set.availability(LaunchMode::Gui).is_available, !cfg!(unix));
    assert_eq!(
        set.availability(LaunchMode::Headless).is_available,
        !cfg!(unix)
    );
}

// ---- automatic relaunch and the stop gate ------------------------------------

/// OpenAC `tests/AcDream.Launcher.Core.Tests/Orchestration/LauncherRelaunchControllerTests.cs`.
/// `PollAsync` is [`relaunch::poll`]: the controller's poll, the launch it
/// hands out, then its finish.
mod relaunch {
    use std::sync::Arc;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{Duration, SystemTime};

    use ac_launcher_core::cancel::CancelToken;
    use ac_launcher_core::orchestration::{
        LauncherAccountSnapshot, LauncherActivityKind, LauncherActivityState, LauncherCapability,
        LauncherPlatformCapabilities, LauncherRelaunchController, LauncherRelaunchSelection,
        LauncherServerSnapshot, LauncherSessionSnapshot, LauncherSessionStop,
        LauncherStateSnapshot, LauncherStopGate, OrchestratorError,
    };
    use ac_launcher_core::profiles::LaunchMode;

    type Launch =
        Box<dyn FnMut(&CancelToken) -> Result<LauncherSessionSnapshot, OrchestratorError>>;

    fn choice() -> LauncherRelaunchSelection {
        LauncherRelaunchSelection::new("World", "Account", Some("Character"), LaunchMode::Headless)
    }

    /// OpenAC `Session(id, live)`.
    fn session(id: u64, live: bool) -> LauncherSessionSnapshot {
        LauncherSessionSnapshot {
            session_id: id.to_string(),
            kind: LauncherActivityKind::Play,
            server_name: "World".into(),
            account_name: "Account".into(),
            character_name: Some("Character".into()),
            launch_mode: Some(LaunchMode::Headless),
            state: if live {
                LauncherActivityState::InWorld
            } else {
                LauncherActivityState::Exited
            },
            status: "Status".into(),
            exit_code: (!live).then_some(0),
            error: None,
            created_at: SystemTime::UNIX_EPOCH + Duration::from_secs(id),
            exit_reason: None,
            exited_gracefully: false,
            server_message: None,
            account_in_use: false,
            stop: None,
            not_responding: false,
            plugin_notice: None,
            has_live_process: Some(live),
            startup_in_flight: false,
            window: Default::default(),
        }
    }

    /// OpenAC's `Clock`: whole seconds, advanced by the test.
    #[derive(Clone, Default)]
    struct Clock(Arc<AtomicU64>);

    impl Clock {
        fn advance(&self, seconds: u64) {
            self.0.fetch_add(seconds, Ordering::SeqCst);
        }

        fn controller(&self) -> LauncherRelaunchController {
            let seconds = Arc::clone(&self.0);
            LauncherRelaunchController::new(Arc::new(move || {
                Duration::from_secs(seconds.load(Ordering::SeqCst))
            }))
        }
    }

    /// OpenAC's `Host`.
    struct Host {
        sessions: Vec<LauncherSessionSnapshot>,
        has_account: bool,
        capability: LauncherCapability,
        launches: Vec<LauncherRelaunchSelection>,
        launch: Option<Launch>,
        errors: usize,
    }

    impl Host {
        fn new() -> Self {
            Self {
                sessions: Vec::new(),
                has_account: true,
                capability: LauncherCapability::AVAILABLE,
                launches: Vec::new(),
                launch: None,
                errors: 0,
            }
        }

        fn snapshot(&self) -> LauncherStateSnapshot {
            let accounts = if self.has_account {
                vec![LauncherAccountSnapshot {
                    server_name: "World".into(),
                    account_name: "Account".into(),
                    characters: Vec::new(),
                    has_running_activity: false,
                    activity_status: "Ready".into(),
                    selected_character: None,
                    selected_launch_mode: None,
                    plugins: Vec::new(),
                    profiles: Vec::new(),
                    login_commands: Vec::new(),
                }]
            } else {
                Vec::new()
            };
            LauncherStateSnapshot {
                servers: vec![LauncherServerSnapshot {
                    name: "World".into(),
                    host: "localhost".into(),
                    port: 9000,
                    accounts,
                }],
                sessions: self.sessions.clone(),
                platform: LauncherPlatformCapabilities::for_operating_system(true, false, false),
                is_installation_ready: true,
                installation_status: "Ready".into(),
                show_beta_plugins: false,
                profiles: Vec::new(),
            }
        }

        fn observe(
            &self,
            scheduler: &mut LauncherRelaunchController,
            checked: &[LauncherRelaunchSelection],
        ) {
            scheduler.observe(&self.snapshot(), checked, true);
        }

        /// OpenAC `PollAsync`: one due attempt, launched and finished.
        fn poll(&mut self, scheduler: &mut LauncherRelaunchController, may_launch: bool) {
            let capability = self.capability.clone();
            let attempt = scheduler
                .poll(may_launch, &mut |_| Ok(capability.clone()))
                .unwrap();
            let Some(attempt) = attempt else {
                return;
            };
            self.launches.push(attempt.selection.clone());
            let result = match self.launch.as_mut() {
                Some(launch) => launch(&attempt.cancel),
                None => Ok(session(100 + self.launches.len() as u64, true)),
            };
            if scheduler.finish(&attempt, result).is_some() {
                self.errors += 1;
            }
        }
    }

    #[test]
    fn host_exit_does_not_start_delay_until_physical_exit_and_startup_completion() {
        let mut host = Host::new();
        let clock = Clock::default();
        let mut scheduler = clock.controller();
        host.sessions = vec![session(1, true)];
        host.observe(&mut scheduler, &[choice()]);
        let mut exited = session(1, true);
        exited.state = LauncherActivityState::Exited;
        host.sessions = vec![exited];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(500);
        host.poll(&mut scheduler, true);
        assert!(host.launches.is_empty());
        let mut starting = session(1, false);
        starting.startup_in_flight = true;
        host.sessions = vec![starting];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(500);
        host.poll(&mut scheduler, true);
        assert!(host.launches.is_empty());
        host.sessions = vec![session(1, false)];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(29);
        host.poll(&mut scheduler, true);
        assert!(host.launches.is_empty());
        clock.advance(1);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches, [choice()]);
    }

    #[test]
    fn an_exit_or_a_crash_restarts_using_current_selection_after_delay() {
        for exit_code in [0, 23] {
            let mut host = Host::new();
            let clock = Clock::default();
            let mut scheduler = clock.controller();
            scheduler.set_delay_seconds(5).unwrap();
            host.sessions = vec![session(1, true)];
            host.observe(&mut scheduler, &[choice()]);
            let mut ended = session(1, false);
            ended.exit_code = Some(exit_code);
            ended.exited_gracefully = exit_code == 0;
            host.sessions = vec![ended];
            let changed = LauncherRelaunchSelection::new(
                "World",
                "Account",
                Some("Different"),
                LaunchMode::Gui,
            );
            host.observe(&mut scheduler, std::slice::from_ref(&changed));
            clock.advance(5);
            host.poll(&mut scheduler, true);
            assert_eq!(host.launches, [changed]);
        }
    }

    // Changed from OpenAC, whose launcher never starts a client just
    // because its account is checked: a checked account with a character
    // and no client is started at once (when the launcher opens, when it is
    // checked, when keeping clients running is turned on), unless its last
    // client was stopped.
    #[test]
    fn a_checked_account_without_a_client_starts_at_once_unless_it_was_stopped() {
        let mut probe = session(2, false);
        probe.kind = LauncherActivityKind::Probe;
        let mut stopped = session(1, false);
        stopped.stop = Some(LauncherSessionStop::Requested);
        let cases = [
            ("no history", Vec::new(), 1),
            ("a crashed client", vec![session(1, false)], 1),
            ("only a probe", vec![probe], 1),
            ("a stopped client", vec![stopped], 0),
        ];
        for (case, sessions, launches) in cases {
            let mut host = Host::new();
            let clock = Clock::default();
            let mut scheduler = clock.controller();
            scheduler.set_delay_seconds(60).unwrap();
            host.sessions = sessions;
            host.observe(&mut scheduler, &[choice()]);
            host.poll(&mut scheduler, true);
            assert_eq!(host.launches.len(), launches, "{case}");
            // Once is enough: the started client is followed from here on.
            clock.advance(600);
            host.poll(&mut scheduler, true);
            assert_eq!(host.launches.len(), launches, "{case}, later");
        }
    }

    #[test]
    fn an_account_without_a_character_waits_for_one_before_it_starts() {
        let mut host = Host::new();
        let clock = Clock::default();
        let mut scheduler = clock.controller();
        let select = LauncherRelaunchSelection::new("World", "Account", None, LaunchMode::Gui);
        host.observe(&mut scheduler, std::slice::from_ref(&select));
        clock.advance(600);
        host.poll(&mut scheduler, true);
        assert!(host.launches.is_empty());
        host.observe(&mut scheduler, &[choice()]);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches, [choice()]);
    }

    #[test]
    fn a_start_that_fails_waits_the_full_delay_before_the_next() {
        let mut host = Host::new();
        let clock = Clock::default();
        let mut scheduler = clock.controller();
        scheduler.set_delay_seconds(60).unwrap();
        host.launch = Some(Box::new(|_| {
            Err(OrchestratorError::Operation("Failure".into()))
        }));
        host.observe(&mut scheduler, &[choice()]);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches.len(), 1);
        clock.advance(59);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches.len(), 1);
        clock.advance(1);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches.len(), 2);
    }

    #[test]
    fn a_stopped_account_is_followed_again_once_launched_by_hand() {
        let mut host = Host::new();
        let clock = Clock::default();
        let mut scheduler = clock.controller();
        scheduler.set_delay_seconds(5).unwrap();
        let mut stopped = session(1, false);
        stopped.stop = Some(LauncherSessionStop::Requested);
        host.sessions = vec![stopped.clone()];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(20);
        host.poll(&mut scheduler, true);
        assert!(host.launches.is_empty());
        // Play by hand; that client crashes.
        host.sessions = vec![session(3, true), stopped.clone()];
        host.observe(&mut scheduler, &[choice()]);
        host.sessions = vec![session(3, false), stopped];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(5);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches.len(), 1);
    }

    #[test]
    fn failed_attempt_waits_full_delay_and_capability_hold_is_not_bypassed() {
        let mut host = Host::new();
        let clock = Clock::default();
        let mut scheduler = clock.controller();
        scheduler.set_delay_seconds(5).unwrap();
        host.sessions = vec![session(1, true)];
        host.observe(&mut scheduler, &[choice()]);
        host.sessions = vec![session(1, false)];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(5);
        host.capability = LauncherCapability::unavailable("Reconnect hold");
        host.poll(&mut scheduler, true);
        assert!(host.launches.is_empty());
        host.capability = LauncherCapability::AVAILABLE;
        host.poll(&mut scheduler, false);
        assert!(host.launches.is_empty());
        host.launch = Some(Box::new(|_| {
            Err(OrchestratorError::Operation("Failure".into()))
        }));
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches.len(), 1);
        clock.advance(4);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches.len(), 1);
        clock.advance(1);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches.len(), 2);
        assert_eq!(host.errors, 2);
    }

    #[test]
    fn removing_intent_cancels_inflight_and_never_queues_another_attempt() {
        for action in ["disable", "uncheck", "remove", "dispose"] {
            let mut host = Host::new();
            let clock = Clock::default();
            let mut scheduler = clock.controller();
            scheduler.set_delay_seconds(5).unwrap();
            host.sessions = vec![session(1, true)];
            host.observe(&mut scheduler, &[choice()]);
            host.sessions = vec![session(1, false)];
            host.observe(&mut scheduler, &[choice()]);
            clock.advance(5);
            // The attempt is handed out and its launch is still pending.
            let attempt = scheduler
                .poll(true, &mut |_| Ok(LauncherCapability::AVAILABLE))
                .unwrap()
                .expect("a due attempt");
            host.launches.push(attempt.selection.clone());
            assert!(scheduler.is_launching());
            host.poll(&mut scheduler, true);
            assert_eq!(host.launches.len(), 1, "{action}");
            match action {
                "dispose" => scheduler.dispose(),
                _ => {
                    if action == "remove" {
                        host.has_account = false;
                    }
                    let checked = if action == "uncheck" {
                        Vec::new()
                    } else {
                        vec![choice()]
                    };
                    scheduler.observe(&host.snapshot(), &checked, action != "disable");
                }
            }
            assert!(attempt.cancel.is_cancelled(), "{action}");
            assert!(scheduler.finish(&attempt, Ok(session(2, false))).is_none());
            clock.advance(100);
            host.poll(&mut scheduler, true);
            assert_eq!(host.launches.len(), 1, "{action}");
        }
    }

    #[test]
    fn clearing_finished_history_keeps_armed_intent_without_resurrecting_older_sessions() {
        let mut host = Host::new();
        let clock = Clock::default();
        let mut scheduler = clock.controller();
        scheduler.set_delay_seconds(5).unwrap();
        host.sessions = vec![session(1, false), session(2, true)];
        host.observe(&mut scheduler, &[choice()]);
        host.sessions = vec![session(1, false), session(2, false)];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(3);
        host.sessions = vec![session(1, false)];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(2);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches.len(), 1);
    }

    // Not an OpenAC test: a relaunch the server refuses because the account
    // is still logged on is tried again once the server's hold ends (the
    // capability), not after the full delay, for as long as the server keeps
    // answering.
    #[test]
    fn a_refused_relaunch_retries_after_the_hold_while_the_server_answers() {
        let mut host = Host::new();
        let clock = Clock::default();
        let mut scheduler = clock.controller();
        scheduler.set_delay_seconds(60).unwrap();
        host.sessions = vec![session(1, true)];
        host.observe(&mut scheduler, &[choice()]);
        // A crash: the first relaunch waits the full delay.
        host.sessions = vec![session(1, false)];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(59);
        host.poll(&mut scheduler, true);
        assert!(host.launches.is_empty());
        clock.advance(1);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches.len(), 1);
        for attempt in 1..=20u64 {
            // The relaunched client is refused: the account is still held.
            let mut refused = session(100 + attempt, false);
            refused.account_in_use = true;
            refused.server_message =
                Some("Another account is already logged on from this client.".into());
            host.sessions = vec![refused];
            host.observe(&mut scheduler, &[choice()]);
            // The hold is the only wait.
            host.capability = LauncherCapability::held(Duration::from_secs(30));
            host.poll(&mut scheduler, true);
            assert_eq!(host.launches.len() as u64, attempt, "held");
            host.capability = LauncherCapability::AVAILABLE;
            clock.advance(30);
            host.poll(&mut scheduler, true);
            assert_eq!(host.launches.len() as u64, attempt + 1, "retried");
        }
        // A client that could not reach the server ends like any other: the
        // next try waits the full delay.
        host.sessions = vec![session(300, false)];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(30);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches.len(), 21, "a plain end waits the delay");
        clock.advance(30);
        host.poll(&mut scheduler, true);
        assert_eq!(host.launches.len(), 22);
    }

    // Not an OpenAC test: the window hears of a relaunched session (the
    // launch's state change) before the attempt is finished, and often
    // sees a session end before its exit report says the server refused
    // it. In every order a refusal is retried after the hold alone.
    #[test]
    fn refusals_retry_after_the_hold_whatever_order_the_window_sees_them_in() {
        for (observe_before_finish, report_after_end) in
            [(false, false), (true, false), (false, true), (true, true)]
        {
            let case = format!("early {observe_before_finish}, late {report_after_end}");
            let mut host = Host::new();
            let clock = Clock::default();
            let mut scheduler = clock.controller();
            scheduler.set_delay_seconds(60).unwrap();
            host.sessions = vec![session(1, true)];
            host.observe(&mut scheduler, &[choice()]);
            host.sessions = vec![session(1, false)];
            host.observe(&mut scheduler, &[choice()]);
            clock.advance(60);
            let mut launches = 0u64;
            while let Some(attempt) = scheduler
                .poll(true, &mut |_| Ok(LauncherCapability::AVAILABLE))
                .unwrap()
            {
                launches += 1;
                if launches == 10 {
                    break;
                }
                // Starting: a refused login never reaches the world.
                let mut launched = session(100 + launches, true);
                launched.state = LauncherActivityState::Starting;
                if observe_before_finish {
                    host.sessions = vec![launched.clone()];
                    host.observe(&mut scheduler, &[choice()]);
                }
                assert!(scheduler.finish(&attempt, Ok(launched)).is_none());
                let mut ended = session(100 + launches, false);
                if report_after_end {
                    host.sessions = vec![ended.clone()];
                    host.observe(&mut scheduler, &[choice()]);
                }
                ended.account_in_use = true;
                ended.server_message =
                    Some("Another account is already logged on from this client.".into());
                host.sessions = vec![ended];
                host.observe(&mut scheduler, &[choice()]);
                host.observe(&mut scheduler, &[choice()]);
                // Less than the delay: only the hold is waited out.
                clock.advance(30);
            }
            assert_eq!(launches, 10, "{case}");
        }
    }

    // Changed from OpenAC, whose launcher relaunches a client after a
    // graceful Stop: a Stop keeps the client down, whether it logged out or
    // had to be killed.
    #[test]
    fn a_graceful_stop_is_not_relaunched() {
        let mut host = Host::new();
        let clock = Clock::default();
        let mut scheduler = clock.controller();
        scheduler.set_delay_seconds(5).unwrap();
        host.sessions = vec![session(1, true)];
        host.observe(&mut scheduler, &[choice()]);
        let mut stopped = session(1, false);
        stopped.stop = Some(LauncherSessionStop::Requested);
        stopped.exited_gracefully = true;
        host.sessions = vec![stopped];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(600);
        host.poll(&mut scheduler, true);
        assert!(host.launches.is_empty());
    }

    // Not an OpenAC test: a Stop that had to kill the client is the player's
    // stop, so it is not relaunched either.
    #[test]
    fn a_forced_stop_is_not_relaunched() {
        let mut host = Host::new();
        let clock = Clock::default();
        let mut scheduler = clock.controller();
        scheduler.set_delay_seconds(5).unwrap();
        host.sessions = vec![session(1, true)];
        host.observe(&mut scheduler, &[choice()]);
        // The process is seen to end before the stop records the kill.
        let mut stopping = session(1, false);
        stopping.stop = Some(LauncherSessionStop::Requested);
        host.sessions = vec![stopping.clone()];
        host.observe(&mut scheduler, &[choice()]);
        stopping.stop = Some(LauncherSessionStop::Forced);
        host.sessions = vec![stopping];
        host.observe(&mut scheduler, &[choice()]);
        clock.advance(600);
        host.poll(&mut scheduler, true);
        assert!(host.launches.is_empty());
    }

    // Not an OpenAC test: Stop all drops a relaunch waiting out its delay
    // and a start not made yet; each stays down until launched again.
    #[test]
    fn stop_all_drops_pending_relaunches_and_starts() {
        let mut host = Host::new();
        let clock = Clock::default();
        let mut scheduler = clock.controller();
        scheduler.set_delay_seconds(5).unwrap();
        host.sessions = vec![session(1, true)];
        host.observe(&mut scheduler, &[choice()]);
        host.sessions = vec![session(1, false)];
        host.observe(&mut scheduler, &[choice()]);
        scheduler.stop_all();
        clock.advance(600);
        host.observe(&mut scheduler, &[choice()]);
        host.poll(&mut scheduler, true);
        assert!(host.launches.is_empty(), "crashed");

        let mut fresh = clock.controller();
        host.sessions = Vec::new();
        host.observe(&mut fresh, &[choice()]);
        fresh.stop_all();
        host.observe(&mut fresh, &[choice()]);
        host.poll(&mut fresh, true);
        assert!(host.launches.is_empty(), "not started yet");
    }

    #[test]
    fn delay_rejects_out_of_range() {
        for seconds in [4, 3601] {
            let mut scheduler = LauncherRelaunchController::default();
            assert_eq!(scheduler.delay_seconds(), 30);
            assert!(scheduler.set_delay_seconds(seconds).is_err());
        }
    }

    #[test]
    fn stop_gate_survives_host_exit_and_startup_until_physical_termination() {
        let mut host = Host::new();
        let mut gate = LauncherStopGate::new();
        let mut other = session(2, true);
        other.account_name = "Other".into();
        host.sessions = vec![session(1, true), other];
        assert!(gate.try_begin("1", &host.snapshot()));
        assert!(gate.try_begin("2", &host.snapshot()));
        assert!(!gate.try_begin("1", &host.snapshot()));
        let mut exited = session(1, true);
        exited.state = LauncherActivityState::Exited;
        host.sessions[0] = exited;
        gate.observe(&host.snapshot());
        assert!(gate.is_pending());
        let mut starting = session(1, false);
        starting.startup_in_flight = true;
        host.sessions[0] = starting;
        gate.observe(&host.snapshot());
        assert!(gate.is_pending());
        host.sessions[0] = session(1, false);
        gate.observe(&host.snapshot());
        assert!(gate.is_pending());
        assert!(!gate.is_stopping("1"));
        assert!(gate.is_stopping("2"));
        let mut other_ended = session(2, false);
        other_ended.account_name = "Other".into();
        host.sessions[1] = other_ended;
        gate.observe(&host.snapshot());
        assert!(!gate.is_pending());
    }
}

// ---- switching a running session's window ----------------------------------
//
// An addition: OpenAC's launcher cannot switch a running client between
// graphical and headless. The switch is a console line to the client; the
// row's state follows the client's `window*` status events.

fn window_event(session_id: &str, kind: StatusEventKind) -> StatusEvent {
    let name = match &kind {
        StatusEventKind::WindowOpened => "windowOpened",
        StatusEventKind::WindowClosed { .. } => "windowClosed",
        _ => "windowFailed",
    };
    event(session_id, name, kind)
}

fn window_closed(session_id: &str, reason: &str) -> StatusEvent {
    window_event(
        session_id,
        StatusEventKind::WindowClosed {
            reason: reason.into(),
        },
    )
}

/// An orchestrator with one running `+Acdream` session in `mode`.
fn running(
    root: &TempDir,
    mode: LaunchMode,
) -> (
    LauncherOrchestrator,
    Arc<FakeSupervisorFactory>,
    Arc<QueueStatusSourceFactory>,
) {
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let statuses = Arc::new(QueueStatusSourceFactory::default());
    let orchestrator = create_orchestrator(
        root,
        Setup {
            supervisors: supervisors.clone(),
            statuses: statuses.clone(),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), mode).unwrap();
    (orchestrator, supervisors, statuses)
}

#[test]
fn a_running_headless_session_opens_a_window_with_its_id_and_closes_it_again() {
    let root = TempDir::new("orchestrator");
    let (orchestrator, supervisors, statuses) = running(&root, LaunchMode::Headless);
    let session = single_session(&orchestrator);
    assert!(!session.window.is_open);
    assert_eq!(session.window.switching_to, None);

    orchestrator.set_session_window("s1", true).unwrap();
    let supervisor = supervisors.single();
    assert_eq!(supervisor.lines(), ["/window s1"]);
    let switching = single_session(&orchestrator).window;
    assert_eq!(switching.switching_to, Some(true));
    assert!(!switching.is_open);
    assert!(switching.shows_window());
    // No restart and no reconnect: the one process keeps running.
    assert_eq!(supervisors.created().len(), 1);
    assert_eq!(supervisor.stop_calls(), 0);

    // The client reports the window open.
    let source = statuses.single();
    enqueue(&source, window_event("s1", StatusEventKind::WindowOpened));
    orchestrator.poll_status().unwrap();
    let opened = single_session(&orchestrator).window;
    assert!(opened.is_open);
    assert_eq!(opened.switching_to, None);
    assert_eq!(opened.error, None);

    // And back to headless.
    orchestrator.set_session_window("s1", false).unwrap();
    assert_eq!(supervisor.lines(), ["/window s1", "/close"]);
    enqueue(&source, window_closed("s1", "requested"));
    orchestrator.poll_status().unwrap();
    let closed = single_session(&orchestrator);
    assert!(!closed.window.is_open);
    assert_eq!(closed.window.switching_to, None);
    assert!(closed.is_active());
    assert_eq!(supervisor.stop_calls(), 0);

    // Asking for the state it is in sends nothing.
    orchestrator.set_session_window("s1", false).unwrap();
    assert_eq!(supervisor.lines().len(), 2);
}

#[test]
fn a_graphical_launch_counts_as_open_and_follows_the_player_closing_the_window() {
    let root = TempDir::new("orchestrator");
    let (orchestrator, supervisors, statuses) = running(&root, LaunchMode::Gui);
    assert!(single_session(&orchestrator).window.is_open);

    orchestrator.set_session_window("s1", false).unwrap();
    assert_eq!(supervisors.single().lines(), ["/close"]);
    let source = statuses.single();
    enqueue(&source, window_closed("s1", "requested"));
    orchestrator.poll_status().unwrap();
    assert!(!single_session(&orchestrator).window.is_open);

    // Opened again from the row, then closed by the player.
    orchestrator.set_session_window("s1", true).unwrap();
    enqueue(&source, window_event("s1", StatusEventKind::WindowOpened));
    enqueue(&source, window_closed("s1", "closedByUser"));
    orchestrator.poll_status().unwrap();
    let session = single_session(&orchestrator);
    assert!(!session.window.is_open);
    assert_eq!(session.window.switching_to, None);
    assert!(session.is_active());
}

#[test]
fn a_switch_the_client_cannot_take_fails_and_changes_nothing() {
    let root = TempDir::new("orchestrator");
    let (orchestrator, supervisors, _) = running(&root, LaunchMode::Headless);
    supervisors.single().refuse_lines();

    let error = orchestrator.set_session_window("s1", true).unwrap_err();
    assert!(
        error.to_string().contains("takes no console input"),
        "{error}"
    );
    let window = single_session(&orchestrator).window;
    assert!(!window.is_open);
    assert_eq!(window.switching_to, None);
    assert!(window.error.is_some());
}

#[test]
fn a_window_the_client_could_not_open_reverts_the_switch_with_its_reason() {
    let root = TempDir::new("orchestrator");
    let (orchestrator, _, statuses) = running(&root, LaunchMode::Headless);
    orchestrator.set_session_window("s1", true).unwrap();
    enqueue(
        &statuses.single(),
        window_event(
            "s1",
            StatusEventKind::WindowFailed {
                error: "this client runs without window support".into(),
            },
        ),
    );
    orchestrator.poll_status().unwrap();
    let window = single_session(&orchestrator).window;
    assert!(!window.is_open);
    assert_eq!(window.switching_to, None);
    assert_eq!(
        window.error.as_deref(),
        Some("The game window could not open: this client runs without window support")
    );
}

#[test]
fn an_unanswered_switch_is_given_up_after_the_timeout() {
    let root = TempDir::new("orchestrator");
    let supervisors = Arc::new(FakeSupervisorFactory::default());
    let start = SystemTime::UNIX_EPOCH + Duration::from_secs(1_800_000_000);
    let now = Arc::new(Mutex::new(start));
    let clock_now = Arc::clone(&now);
    let orchestrator = create_orchestrator(
        &root,
        Setup {
            supervisors: supervisors.clone(),
            clock: Some(Arc::new(move || *lock(&clock_now))),
            ..Setup::default()
        },
    );
    launch(&orchestrator, Some("+Acdream"), LaunchMode::Headless).unwrap();
    orchestrator.set_session_window("s1", true).unwrap();

    *lock(&now) = start + WINDOW_SWITCH_TIMEOUT - Duration::from_secs(1);
    orchestrator.poll_status().unwrap();
    assert_eq!(
        single_session(&orchestrator).window.switching_to,
        Some(true)
    );
    // A second switch waits for the first.
    assert!(orchestrator.set_session_window("s1", false).is_err());

    *lock(&now) = start + WINDOW_SWITCH_TIMEOUT;
    orchestrator.poll_status().unwrap();
    let window = single_session(&orchestrator).window;
    assert_eq!(window.switching_to, None);
    assert!(!window.is_open);
    assert!(window.error.is_some());
    assert_eq!(supervisors.single().lines(), ["/window s1"]);
}

#[test]
fn only_a_running_play_session_switches() {
    let root = TempDir::new("orchestrator");
    let (orchestrator, supervisors, _) = running(&root, LaunchMode::Headless);
    assert!(orchestrator.set_session_window("missing", true).is_err());
    supervisors.single().exit(0);
    assert!(orchestrator.set_session_window("s1", true).is_err());
    assert!(supervisors.single().lines().is_empty());
}
