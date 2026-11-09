//! The launcher window's state and commands (OpenAC's
//! `LauncherWindowViewModel` with its Accounts, Desktop and Plugins parts).
//! A command that has slow work starts it on a worker and returns; the
//! worker's result arrives as a message that [`LauncherWindow::poll`]
//! applies.

use std::collections::{BTreeMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use super::accounts::{
    AccountGroup, AccountServerRow, CHARACTER_SELECT, ProfileFilterChip, RowLaunchMode,
};
use super::add_server::AddServerDialog;
use super::classic_migration::ClassicMigrationDialog;
use super::closing::LauncherClosing;
use super::console::SessionConsole;
use super::first_run::FirstRunSetup;
use super::icon::{PluginIcon, decode_icon};
use super::install_dialog::{InstallDialogRequest, PluginCharacterOption};
use super::plugins::{
    PluginCheckOutcome, PluginComposition, PluginFeedState, PluginSingleCheckResult,
};
use super::plugins_panel::{
    AddFromUrlChoice, DiscoverChannelOption, DiscoverDetails, PluginsPanel,
};
use super::profile_editor::ProfileTextEditor;
use super::profiles_dialog::{ProfileListEntry, ProfilesDialog, describe_members};
use super::sessions::SessionRow;
use super::thwarg_import::ThwargImportDialog;
use super::update_prompt::UpdatePrompt;
use super::{Orchestration, ServerHealth, Spawner, Wake, safe_display_error};
use crate::cancel::CancelToken;
use crate::installation::{InstallRecordVerification, LauncherInstallRecord, RetailContentStore};
use crate::orchestration::{
    AccountCapabilities, DEFAULT_RELAUNCH_DELAY_SECONDS, LauncherAccountSnapshot,
    LauncherActivityKind, LauncherActivityState, LauncherCapability, LauncherCharacterSnapshot,
    LauncherRelaunchController, LauncherRelaunchSelection, LauncherSessionSnapshot,
    LauncherStateSnapshot, LauncherStopGate, MAXIMUM_RELAUNCH_DELAY_SECONDS,
    MINIMUM_RELAUNCH_DELAY_SECONDS, MonotonicClock, OrchestratorError, RelaunchAttempt,
};
use crate::plugins::{
    ICON_FILE_NAME, ICON_MAXIMUM_BYTES, InstallRules, InstalledPluginInfo, InstalledPluginSource,
    PermissionDeclaration, PluginInstallResult, PluginManifest, PluginReleaseCandidates,
    PluginReleaseChannel, PluginReleaseFetchResult, PluginReleaseResolution,
    PluginReleaseResolveResult, PluginReleaseResolver, PluginRepositoryResolution, describe,
    github, is_built_for_csharp, is_well_formed_id,
};
use crate::profiles::{
    LaunchMode, LauncherCheckedAccount, LauncherCheckedAccountStore, LauncherConsoleSettings,
    LauncherConsoleSettingsStore, LauncherRelaunchSettings, LauncherRelaunchSettingsStore,
    LauncherTextEditorKind, find_account, same_account,
};
use crate::status::{KnownServerCatalog, KnownServerList};
use crate::updates::{
    ClientVersionResolution, LauncherUpdateCheckResult, LauncherUpdateProgress, LauncherUpdater,
    LauncherVersion,
};
use ac_host_core::local_time::LocalZone;

/// How long Stop waits for a graceful logout before killing the process.
pub const GRACEFUL_STOP_TIMEOUT: Duration = Duration::from_secs(30);

/// How often an active window checks the servers.
pub const HEALTH_CHECK_INTERVAL: Duration = Duration::from_secs(10);

/// How many server checks run at once.
const HEALTH_CHECK_PARALLELISM: usize = 4;

/// The two panels the window switches between.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LauncherMainTab {
    /// Accounts and servers.
    Accounts,
    /// Installed and Discover.
    Plugins,
}

/// One plugin in the row options checklist: an installed plugin the
/// character's launch mode can load, or an id already saved on the
/// character that is no longer offered ("missing"), kept checked until it
/// is unticked.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RowOptionsChoice {
    /// The plugin id.
    pub id: String,
    /// What the checklist shows.
    pub display_name: String,
    /// Ticked.
    pub is_checked: bool,
    /// Saved but not offered.
    pub is_missing: bool,
}

/// One profile in a character's options: whether the character is in it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RowOptionsProfile {
    /// The profile's name.
    pub name: String,
    /// The character is in it.
    pub is_checked: bool,
}

/// The question asked when the launcher window is closed while clients it
/// started still run (OpenAC closes at once).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ClosePrompt {
    /// How many clients run.
    pub running: usize,
}

impl ClosePrompt {
    /// The dialog's title.
    pub const TITLE: &'static str = "Close the launcher?";

    /// The dialog's text.
    pub fn message(&self) -> String {
        if self.running == 1 {
            "Closing the launcher will log off and close 1 running client.".to_owned()
        } else {
            format!(
                "Closing the launcher will log off and close {} running clients.",
                self.running
            )
        }
    }
}

/// What the window is built from.
pub struct LauncherWindowParts {
    /// The state owner.
    pub orchestrator: Arc<dyn Orchestration>,
    /// Where slow work runs.
    pub spawner: Arc<dyn Spawner>,
    /// Asks the window to draw again.
    pub wake: Wake,
    /// The retail content record (first-run setup).
    pub content: Option<Arc<RetailContentStore>>,
    /// The client updater; `None` when there is no feed.
    pub updater: Option<Arc<LauncherUpdater>>,
    /// Why there is no updater.
    pub updater_unavailable_reason: String,
    /// The plugin backend.
    pub plugins: Option<Arc<PluginComposition>>,
    /// Server reachability and population.
    pub server_health: Option<Arc<dyn ServerHealth>>,
    /// The launcher's version (`0.1.0`).
    pub launcher_version: Option<String>,
    /// The installed client, for plugin compatibility and the version text.
    pub client_version: Arc<dyn Fn() -> Option<ClientVersionResolution> + Send + Sync>,
    /// Hands a just-staged launcher update to its helper (the bootstrap's
    /// `try_apply_staged_update_now`); true when the helper started and the
    /// launcher should close. `None`: the update waits for the next start.
    pub apply_launcher_update: Option<ApplyLauncherUpdate>,
    /// The install folder section of the settings; `None` hides it.
    pub install_folder: Option<super::install_folder::InstallFolderParts>,
    /// Which account rows were ticked, remembered across restarts; `None`
    /// keeps them for this run only.
    pub checked_accounts: Option<LauncherCheckedAccountStore>,
    /// Whether checked clients are kept running and the relaunch delay,
    /// remembered across restarts; `None` keeps them for this run only.
    pub relaunch_settings: Option<LauncherRelaunchSettingsStore>,
    /// Whether session consoles show every line (Verbose), remembered across
    /// restarts; `None` keeps it for this run only.
    pub console_settings: Option<LauncherConsoleSettingsStore>,
    /// The clock automatic relaunch times its delay on; `None`: the
    /// system's monotonic clock.
    pub relaunch_clock: Option<MonotonicClock>,
    /// The public server list Add server offers; `None`: only your own.
    pub known_servers: Option<Arc<KnownServerCatalog>>,
    /// The VirindiTank and UtilityBelt import; `None` hides it.
    pub classic_migration: Option<ClassicMigrationDialog>,
}

/// Starts the staged launcher update now; true when the launcher should
/// close for it.
pub type ApplyLauncherUpdate = Arc<dyn Fn(&CancelToken) -> Result<bool, String> + Send + Sync>;

impl std::fmt::Debug for LauncherWindowParts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LauncherWindowParts")
            .field("launcher_version", &self.launcher_version)
            .finish_non_exhaustive()
    }
}

enum Message {
    StateChanged,
    OperationStatus(String),
    LaunchFinished {
        launch: u64,
        rows: Vec<(String, String)>,
        started: usize,
        total: usize,
        errors: Vec<String>,
        cancelled: bool,
    },
    RelaunchFinished {
        attempt: Box<RelaunchAttempt>,
        result: Box<Result<LauncherSessionSnapshot, OrchestratorError>>,
    },
    StopFinished(Result<(), OrchestratorError>),
    /// A stop the closing started has returned.
    CloseStopFinished {
        session_id: String,
        result: Result<(), OrchestratorError>,
    },
    ContentVerified {
        verification: Box<InstallRecordVerification>,
        startup: bool,
    },
    UpdateCheckFinished {
        result: Box<Result<LauncherUpdateCheckResult, String>>,
        startup: bool,
        /// The button's check: open what it found.
        open_prompt: bool,
        /// The installed client was verified first.
        verified: bool,
    },
    PluginUpdatesChecked(Box<PluginCheckOutcome>),
    /// The installed plugins' release feeds were read.
    PluginFeedsWatched(Box<PluginFeedState>),
    KnownServersLoaded(Result<Option<KnownServerList>, String>),
    LocalZipInspected {
        path: PathBuf,
        result: Result<crate::plugins::LocalPluginZipPreview, String>,
    },
    UpdateProgress(LauncherUpdateProgress),
    UpdateInstalled(Result<(), String>),
    LauncherUpdateStaged(Result<bool, String>),
    HealthResult {
        server_name: String,
        endpoint: String,
        snapshot: crate::status::ServerHealthSnapshot,
    },
    HealthFinished {
        failed: bool,
    },
    PluginCheckFinished(Box<PluginCheckOutcome>),
    DiscoverResolved {
        id: String,
        details: Option<DiscoverDetails>,
    },
    DiscoverPassFinished {
        saw_rate_limited: bool,
        saw_transport_failure: bool,
    },
    PermissionsLoaded {
        generation: u64,
        result: Result<Vec<PermissionDeclaration>, String>,
    },
    InstallFinished(Result<PluginInstallResult, String>),
    AddFromUrlResolved(Result<PluginRepositoryResolution, String>),
    BetaToggled {
        id: String,
        is_beta: bool,
        result: Result<PluginSingleCheckResult, String>,
    },
    InstallMoveProgress(String),
    InstallMoved(crate::installation::InstallRootMoveResult),
    ClassicMigrationFinished {
        apply: bool,
        result: Result<Vec<crate::classic_migration::ClassicMigrationItem>, String>,
    },
    ThwargImportFinished {
        apply: bool,
        result: Result<crate::profiles::thwarg::ThwargImportReport, String>,
    },
}

/// The window's state.
pub struct LauncherWindow {
    orchestrator: Arc<dyn Orchestration>,
    spawner: Arc<dyn Spawner>,
    wake: Wake,
    sender: Sender<Message>,
    receiver: Receiver<Message>,
    content: Option<Arc<RetailContentStore>>,
    updater: Option<Arc<LauncherUpdater>>,
    updater_unavailable_reason: String,
    plugin_backend: Option<Arc<PluginComposition>>,
    server_health: Option<Arc<dyn ServerHealth>>,
    launcher_version: Option<String>,
    client_version: Arc<dyn Fn() -> Option<ClientVersionResolution> + Send + Sync>,
    apply_launcher_update: Option<ApplyLauncherUpdate>,
    shutdown_requested: bool,

    snapshot: Option<LauncherStateSnapshot>,
    /// One group per account name.
    pub accounts: Vec<AccountGroup>,
    /// The session log's rows.
    pub sessions: Vec<SessionRow>,
    /// The tab shown.
    pub selected_tab: LauncherMainTab,
    is_busy: bool,
    operation: Option<CancelToken>,
    /// The last failure, shown under the bottom bar.
    pub last_error: Option<String>,
    /// The bottom bar's status line.
    pub operation_status: String,
    is_installation_checking: bool,
    is_client_compatibility_check_blocking: bool,
    disposed: bool,
    startup_started: bool,
    startup_cancellation: CancelToken,

    /// The profile editors.
    pub text_editor: ProfileTextEditor,
    /// First-run setup.
    pub first_run: FirstRunSetup,
    /// The update prompt.
    pub update_prompt: UpdatePrompt,
    update_cancellation: Option<CancelToken>,
    /// The Plugins tab.
    pub plugins: PluginsPanel,
    plugin_cancellation: Option<CancelToken>,
    install_cancellation: Option<CancelToken>,

    /// The plugins dialog (an account's list, or one character's) is open.
    pub is_row_options_open: bool,
    row_options_account: Option<(String, String)>,
    row_options_character: Option<(String, String, String)>,
    /// The plugins dialog's checklist.
    pub row_options_choices: Vec<RowOptionsChoice>,
    /// A character's options: one box per profile, ticked when the
    /// character is in it. Empty for an account's plugins.
    pub row_options_profiles: Vec<RowOptionsProfile>,
    /// The profile list the boxes opened with: what is saved when the
    /// boxes are left alone.
    row_options_saved_profiles: Vec<String>,
    /// The Profiles dialog: every profile, New profile and Delete.
    pub profiles_dialog: ProfilesDialog,
    /// The character's launch mode in the dialog.
    pub character_launch_mode: LaunchMode,
    /// The character launches with its account's plugins; unticked, it
    /// keeps its own list, edited in the checklist.
    pub character_uses_account_plugins: bool,
    /// The character a Remove asks about: server, account, character.
    pub remove_character_target: Option<(String, String, String)>,
    /// What the profile load could not carry over from an older file,
    /// shown once.
    pub profile_migration_notice: Option<String>,
    /// The profile tag the accounts are filtered by, or `None` for all.
    pub profile_filter: Option<String>,
    /// "All" and one chip per profile tag, in the order the tags first
    /// appear.
    pub profile_filters: Vec<ProfileFilterChip>,
    checked_accounts: Option<LauncherCheckedAccountStore>,
    checked_accounts_loaded: bool,
    relaunch_settings: Option<LauncherRelaunchSettingsStore>,
    console_settings: Option<LauncherConsoleSettingsStore>,
    console_verbose: bool,
    /// The session log dialog is open.
    pub is_session_log_open: bool,
    /// The settings dialog is open.
    pub is_settings_open: bool,
    /// The install folder section of the settings.
    pub install_folder: Option<super::install_folder::InstallFolderPanel>,
    /// Open session consoles.
    pub consoles: Vec<SessionConsole>,
    /// Window switches sent from a row, by session, with the mode asked
    /// for, until the client answers (its failure goes to the status line).
    window_switches: Vec<(String, bool)>,
    /// The confirmation shown when the window is closed while clients run.
    pub close_prompt: Option<ClosePrompt>,
    close_confirmed: bool,
    /// The launcher is closing over running clients.
    closing: Option<LauncherClosing>,

    is_checking_server_health: bool,
    next_health_check: Option<Instant>,
    health_cancellation: CancelToken,

    /// Stops still completing, one per session.
    stop_gate: LauncherStopGate,
    automatic_relaunch: LauncherRelaunchController,
    relaunch_attempt_running: bool,
    show_only_checked_accounts: bool,
    account_search: String,
    relaunch_checked_clients_automatically: bool,
    relaunch_delay_seconds_text: String,
    /// (server, account) rows whose start runs: only those are locked.
    starting_rows: HashSet<(String, String)>,
    /// Each running row launch's cancellation, by launch number.
    row_launches: Vec<(u64, CancelToken)>,
    next_row_launch: u64,
    graphical_availability: AccountCapabilities,
    headless_availability: AccountCapabilities,
    character_select_availability: AccountCapabilities,
    /// Add a server.
    pub add_server: AddServerDialog,
    /// The VirindiTank and UtilityBelt import dialog; `None` when the
    /// launcher was built without it.
    pub classic_migration: Option<ClassicMigrationDialog>,
    known_servers: Option<Arc<KnownServerCatalog>>,
    /// The import from the Thwarg launcher.
    pub thwarg_import: ThwargImportDialog,
    time_zone: LocalZone,
    /// A check has verified the installed client.
    client_verified: bool,
    /// The startup content and update checks have finished.
    startup_initialization_complete: bool,
    next_update_check: Option<Instant>,
    next_plugin_watch: Option<Instant>,
    plugin_watch_running: bool,
    plugin_watch_backoff: bool,
    /// What the release feeds listed when the installed plugins were last
    /// checked against them.
    plugin_feeds_seen: Option<BTreeMap<String, Vec<String>>>,
    /// What they listed when a change started the check now running: seen
    /// once that check has read the plugin list.
    plugin_feeds_pending: Option<BTreeMap<String, Vec<String>>>,
}

impl std::fmt::Debug for LauncherWindow {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LauncherWindow")
            .field("selected_tab", &self.selected_tab)
            .field("is_busy", &self.is_busy)
            .field("operation_status", &self.operation_status)
            .finish_non_exhaustive()
    }
}

fn error_text(error: &OrchestratorError) -> String {
    safe_display_error(&error.to_string(), None)
}

/// Reads a plugin's `plugin.json` from its folder.
fn read_manifest(directory: &Path) -> Option<PluginManifest> {
    std::fs::read_to_string(directory.join("plugin.json"))
        .ok()
        .and_then(|text| PluginManifest::parse(&text).ok())
}

/// Why the list in effect blocks this release, if it does.
fn block_reason(backend: &PluginComposition, manifest: &PluginManifest) -> Option<String> {
    let version = LauncherVersion::try_parse(&manifest.version);
    backend.current_catalog().and_then(|catalog| {
        catalog
            .block_reason(&manifest.id, version.as_ref())
            .map(str::to_owned)
    })
}

/// `0.1.0` from `0.1.0+abc`.
fn short_version(version: &str) -> String {
    version.split('+').next().unwrap_or(version).to_owned()
}

/// How often an open launcher checks for updates.
pub const UPDATE_CHECK_INTERVAL: Duration = Duration::from_secs(20 * 60);

/// How often an open launcher reads the installed plugins' release feeds,
/// so a new release shows within half a minute without the full check.
pub const PLUGIN_RELEASE_WATCH_INTERVAL: Duration = Duration::from_secs(30);

/// How long the release watch waits after GitHub asks to slow down.
pub const PLUGIN_RELEASE_WATCH_BACKOFF: Duration = Duration::from_secs(10 * 60);

/// Whether `rows` (server, account pairs) hold `account` on `server`: the
/// server exactly, the account in any case.
fn is_account_in<'a>(
    rows: impl IntoIterator<Item = &'a (String, String)>,
    server: &str,
    account: &str,
) -> bool {
    rows.into_iter().any(|(known_server, known_account)| {
        known_server == server && same_account(known_account, account)
    })
}

/// Why a launch from a row would be refused (OpenAC's `GetRowLaunchBlock`):
/// judged against `current` with `capability` for the account's launch
/// availability. The window's rows use its displayed snapshot and the
/// availability read at the last refresh; a launch checks the newest state.
#[allow(clippy::too_many_arguments)]
fn row_launch_block(
    current: Option<&LauncherStateSnapshot>,
    capability: &mut dyn FnMut(LaunchMode) -> Result<LauncherCapability, OrchestratorError>,
    installation_blocking: bool,
    server_name: &str,
    account_name: &str,
    character_name: Option<&str>,
    mode: LaunchMode,
) -> Option<String> {
    if installation_blocking {
        return Some("Checking installation and client compatibility…".into());
    }
    let active = "This account already has an active session on this server.";
    let newest = current.and_then(|current| {
        current
            .sessions
            .iter()
            .filter(|session| {
                session.server_name == server_name
                    && same_account(&session.account_name, account_name)
            })
            .max_by_key(|session| session.created_at)
    });
    if newest.is_some_and(LauncherSessionSnapshot::is_process_or_start_active) {
        return Some(active.into());
    }
    if mode == LaunchMode::Headless && character_name.is_none() {
        return Some("Choose a character for Headless mode.".into());
    }
    let account = current.and_then(|current| {
        current
            .servers
            .iter()
            .find(|server| server.name == server_name)
            .and_then(|server| {
                find_account(
                    &server.accounts,
                    |account| &account.account_name,
                    account_name,
                )
                .map(|index| &server.accounts[index])
            })
    });
    let (Some(current), Some(account)) = (current, account) else {
        return Some("This account is no longer configured on this server.".into());
    };
    if account.has_running_activity
        || current.sessions.iter().any(|session| {
            session.is_process_or_start_active()
                && session.server_name == server_name
                && same_account(&session.account_name, account_name)
        })
    {
        return Some(active.into());
    }
    if let Some(name) = character_name
        && !account
            .characters
            .iter()
            .any(|character| character.name == name)
    {
        return Some("Choose a current character.".into());
    }
    match capability(mode) {
        Ok(capability) if capability.is_available => None,
        Ok(capability) => Some(
            capability
                .reason
                .unwrap_or_else(|| "Launch unavailable.".into()),
        ),
        Err(error) => Some(error_text(&error)),
    }
}

/// [`row_launch_block`] against the orchestrator's newest state.
fn fresh_row_launch_block(
    orchestrator: &dyn Orchestration,
    installation_blocking: bool,
    server_name: &str,
    account_name: &str,
    character_name: Option<&str>,
    mode: LaunchMode,
) -> Option<String> {
    let current = if installation_blocking {
        None
    } else {
        match orchestrator.snapshot() {
            Ok(snapshot) => Some(snapshot),
            Err(error) => return Some(error_text(&error)),
        }
    };
    row_launch_block(
        current.as_ref(),
        &mut |mode| orchestrator.account_launch_capability(server_name, account_name, mode),
        installation_blocking,
        server_name,
        account_name,
        character_name,
        mode,
    )
}

/// A whole number of seconds from 5 to 3600.
fn parse_relaunch_delay(text: &str) -> Option<u32> {
    text.parse::<u32>().ok().filter(|seconds| {
        (MINIMUM_RELAUNCH_DELAY_SECONDS..=MAXIMUM_RELAUNCH_DELAY_SECONDS).contains(seconds)
    })
}

impl LauncherWindow {
    /// A window over `parts`; call [`initialize`](Self::initialize) next.
    pub fn new(parts: LauncherWindowParts) -> Self {
        let (sender, receiver) = mpsc::channel();
        let listener_sender = Mutex::new(sender.clone());
        let listener_wake = Arc::clone(&parts.wake);
        parts.orchestrator.subscribe(Arc::new(move || {
            let sender = listener_sender
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            if sender.send(Message::StateChanged).is_ok() {
                listener_wake();
            }
        }));
        Self {
            orchestrator: parts.orchestrator,
            spawner: parts.spawner,
            wake: parts.wake,
            sender,
            receiver,
            content: parts.content,
            updater: parts.updater,
            updater_unavailable_reason: parts.updater_unavailable_reason,
            plugin_backend: parts.plugins,
            server_health: parts.server_health,
            launcher_version: parts.launcher_version,
            client_version: parts.client_version,
            apply_launcher_update: parts.apply_launcher_update,
            shutdown_requested: false,
            snapshot: None,
            accounts: Vec::new(),
            sessions: Vec::new(),
            selected_tab: LauncherMainTab::Accounts,
            is_busy: false,
            operation: None,
            last_error: None,
            operation_status: "Ready".into(),
            is_installation_checking: true,
            is_client_compatibility_check_blocking: false,
            disposed: false,
            startup_started: false,
            startup_cancellation: CancelToken::new(),
            text_editor: ProfileTextEditor::default(),
            first_run: FirstRunSetup::default(),
            update_prompt: UpdatePrompt::default(),
            update_cancellation: None,
            plugins: PluginsPanel::default(),
            plugin_cancellation: None,
            install_cancellation: None,
            is_row_options_open: false,
            row_options_account: None,
            row_options_character: None,
            row_options_choices: Vec::new(),
            row_options_profiles: Vec::new(),
            row_options_saved_profiles: Vec::new(),
            profiles_dialog: ProfilesDialog::default(),
            character_launch_mode: LaunchMode::GuiSelect,
            character_uses_account_plugins: false,
            remove_character_target: None,
            profile_migration_notice: None,
            profile_filter: None,
            profile_filters: Vec::new(),
            checked_accounts: parts.checked_accounts,
            checked_accounts_loaded: false,
            relaunch_settings: parts.relaunch_settings,
            console_settings: parts.console_settings,
            console_verbose: false,
            is_session_log_open: false,
            is_settings_open: false,
            install_folder: parts
                .install_folder
                .map(super::install_folder::InstallFolderPanel::new),
            consoles: Vec::new(),
            window_switches: Vec::new(),
            close_prompt: None,
            close_confirmed: false,
            closing: None,
            is_checking_server_health: false,
            next_health_check: None,
            health_cancellation: CancelToken::new(),
            stop_gate: LauncherStopGate::new(),
            automatic_relaunch: parts
                .relaunch_clock
                .map(LauncherRelaunchController::new)
                .unwrap_or_default(),
            relaunch_attempt_running: false,
            show_only_checked_accounts: false,
            account_search: String::new(),
            relaunch_checked_clients_automatically: false,
            relaunch_delay_seconds_text: DEFAULT_RELAUNCH_DELAY_SECONDS.to_string(),
            starting_rows: HashSet::new(),
            row_launches: Vec::new(),
            next_row_launch: 0,
            graphical_availability: AccountCapabilities::new(),
            headless_availability: AccountCapabilities::new(),
            character_select_availability: AccountCapabilities::new(),
            add_server: AddServerDialog::default(),
            classic_migration: parts.classic_migration,
            thwarg_import: ThwargImportDialog::default(),
            known_servers: parts.known_servers,
            time_zone: LocalZone::system(),
            client_verified: false,
            startup_initialization_complete: false,
            next_update_check: None,
            next_plugin_watch: None,
            plugin_watch_running: false,
            plugin_watch_backoff: false,
            plugin_feeds_seen: None,
            plugin_feeds_pending: None,
        }
    }

    fn run(&self, job: impl FnOnce(Sender<Message>, Wake) + Send + 'static) {
        let sender = self.sender.clone();
        let wake = Arc::clone(&self.wake);
        self.spawner.spawn(Box::new(move || job(sender, wake)));
    }

    /// The state owner.
    pub fn orchestrator(&self) -> &Arc<dyn Orchestration> {
        &self.orchestrator
    }

    /// The plugin backend, when there is one.
    pub fn plugin_backend(&self) -> Option<&Arc<PluginComposition>> {
        self.plugin_backend.as_ref()
    }

    /// The last snapshot.
    pub fn snapshot(&self) -> Option<&LauncherStateSnapshot> {
        self.snapshot.as_ref()
    }

    // --- Startup ---------------------------------------------------------------------------

    /// Loads the profiles and builds the rows.
    pub fn initialize(&mut self) {
        match self.orchestrator.load_profiles() {
            Ok(()) => {
                if let Some(store) = self.checked_accounts.as_mut() {
                    match store.load() {
                        Ok(()) => self.checked_accounts_loaded = true,
                        Err(error) => self.last_error = Some(error.0),
                    }
                } else {
                    self.checked_accounts_loaded = true;
                }
                self.profile_migration_notice = self
                    .orchestrator
                    .take_profile_migration_notice()
                    .ok()
                    .flatten();
                // The plugin panel read the unloaded store's default; take the saved value.
                self.restore_show_beta_plugins_from_profile();
                if self.checked_accounts_loaded {
                    self.last_error = None;
                }
                // After the clear: a settings file that cannot be read says so.
                self.restore_relaunch_settings();
                self.restore_console_settings();
            }
            Err(error) => self.last_error = Some(error_text(&error)),
        }
        if let Some(plugins) = &self.plugin_backend {
            // Recovery runs once before the inventory is first read; it never blocks the window.
            let _ = plugins.recover();
        }
        self.refresh_from_core();
    }

    /// Checks installed content, then the update feed; opens first-run
    /// setup afterwards when it is needed. Runs once.
    pub fn start_background_initialization(&mut self) {
        if self.startup_started {
            return;
        }
        self.startup_started = true;
        self.is_client_compatibility_check_blocking = true;
        self.operation_status = "Checking installed game content…".into();
        let content = self.content.clone();
        let cancel = self.startup_cancellation.clone();
        self.run(move |sender, wake| {
            let verification = match &content {
                Some(store) => store.load_and_verify(),
                None => InstallRecordVerification {
                    state: crate::installation::InstallRecordVerificationState::Missing,
                    record: None,
                    status: "The installer service is unavailable in this host.".into(),
                    required_content_work: None,
                },
            };
            if !cancel.is_cancelled() {
                let _ = sender.send(Message::ContentVerified {
                    verification: Box::new(verification),
                    startup: true,
                });
                wake();
            }
        });
    }

    /// Reads the release feed. Only the startup check verifies the installed
    /// client (or a later one, while no check has managed to): verifying
    /// again would lock and hash a client that may be running.
    fn start_update_check(&mut self, startup: bool, open_prompt: bool) {
        self.update_prompt.begin_check();
        let updater = self.updater.clone();
        let unavailable = self.updater_unavailable_reason.clone();
        let cancel = CancelToken::new();
        self.update_cancellation = Some(cancel.clone());
        let verify = startup || !self.client_verified;
        self.run(move |sender, wake| {
            let mut verified = false;
            let result = match updater {
                Some(updater) => {
                    let initialized = if verify {
                        updater.initialize(&cancel).map(|_| verified = true)
                    } else {
                        Ok(())
                    };
                    initialized
                        .and_then(|()| updater.check(&cancel))
                        .map_err(|error| error.to_string())
                }
                None => Err(unavailable),
            };
            let _ = sender.send(Message::UpdateCheckFinished {
                result: Box::new(result),
                startup,
                open_prompt,
                verified,
            });
            wake();
        });
    }

    // --- Polling -----------------------------------------------------------------------------

    /// Applies every finished piece of background work and reads the
    /// sessions' status files. Call it every frame.
    pub fn poll(&mut self) {
        if self.disposed {
            return;
        }
        while let Ok(message) = self.receiver.try_recv() {
            self.apply(message);
        }
        if let Err(error) = self.orchestrator.poll_status() {
            self.last_error = Some(error_text(&error));
        }
        // Applying the status changes, and a reconnect hold that ran out without an event.
        while let Ok(message) = self.receiver.try_recv() {
            self.apply(message);
        }
        self.refresh_row_states();
        if self.closing.is_some() {
            self.follow_closing();
            return;
        }
        if !self.relaunch_attempt_running {
            self.run_automatic_relaunch();
        }
    }

    fn apply(&mut self, message: Message) {
        match message {
            Message::StateChanged => self.refresh_from_core(),
            Message::OperationStatus(status) => self.operation_status = status,
            Message::LaunchFinished {
                launch,
                rows,
                started,
                total,
                errors,
                cancelled,
            } => {
                self.operation_status = if cancelled {
                    format!("Launch cancelled. Started {started} sessions.")
                } else {
                    format!("Started {started} of {total} selected sessions.")
                };
                self.last_error = (!errors.is_empty()).then(|| errors.join("\n"));
                self.row_launches.retain(|(id, _)| *id != launch);
                for (server, account) in rows {
                    self.starting_rows.remove(&(server, account));
                }
                if !self.disposed {
                    self.refresh_from_core();
                }
            }
            Message::RelaunchFinished { attempt, result } => {
                self.relaunch_attempt_running = false;
                if let Some(error) = self.automatic_relaunch.finish(&attempt, *result)
                    && !self.disposed
                {
                    self.on_automatic_relaunch_failed(&error);
                }
            }
            Message::StopFinished(result) => {
                match result {
                    Ok(()) => self.operation_status = "Stop requested.".into(),
                    Err(OrchestratorError::Cancelled) => {
                        self.operation_status = "Stop cancelled.".into();
                    }
                    Err(error) => {
                        self.last_error = Some(error_text(&error));
                        self.operation_status = "Stop failed.".into();
                    }
                }
                if !self.disposed {
                    self.refresh_from_core();
                }
            }
            Message::CloseStopFinished { session_id, result } => {
                if let Err(error) = result
                    && let Some(closing) = self.closing.as_mut()
                {
                    closing.stop_failed(&session_id, error_text(&error));
                }
                self.follow_closing();
            }
            Message::ContentVerified {
                verification,
                startup,
            } => self.apply_content_verification(*verification, startup),
            Message::PluginUpdatesChecked(outcome) => self.apply_installed_plugin_updates(*outcome),
            Message::PluginFeedsWatched(state) => self.apply_plugin_feeds(*state),
            Message::LocalZipInspected { path, result } => self.apply_local_zip(&path, result),
            Message::KnownServersLoaded(result) => match result {
                Ok(list) => {
                    let servers = self
                        .orchestrator
                        .snapshot()
                        .map(|snapshot| snapshot.servers)
                        .unwrap_or_default();
                    self.add_server
                        .show_list(list.as_ref(), &servers, &self.time_zone);
                }
                Err(reason) => self.add_server.show_failure(&reason),
            },
            Message::UpdateCheckFinished {
                result,
                startup,
                open_prompt,
                verified,
            } => {
                self.update_cancellation = None;
                if verified {
                    self.client_verified = true;
                }
                self.update_prompt.finish_check(*result);
                if startup {
                    self.startup_initialization_complete = true;
                    self.operation_status = if self.update_prompt.startup_check_succeeded {
                        String::new()
                    } else {
                        "Update check unavailable; the launcher works offline.".into()
                    };
                    self.is_client_compatibility_check_blocking = false;
                    self.is_installation_checking = false;
                    self.refresh_from_core();
                    if self.is_first_run_required() {
                        self.open_first_run();
                    }
                } else if open_prompt && self.show_update_banner() {
                    self.update_prompt.open_available_update();
                }
            }
            Message::UpdateProgress(progress) => self.update_prompt.apply_progress(&progress),
            Message::UpdateInstalled(result) => {
                self.update_cancellation = None;
                self.update_prompt.is_busy = false;
                self.update_prompt.is_progress_indeterminate = false;
                match result {
                    Ok(()) => {
                        self.update_prompt.status = "Update installed.".into();
                        self.update_prompt.check = None;
                        self.update_prompt.is_open = false;
                        self.operation_status = "Versioned client activation changed.".into();
                        self.last_error = None;
                        self.refresh_from_core();
                    }
                    Err(error) if error == crate::updates::UpdateError::Cancelled.to_string() => {
                        self.update_prompt.status = "Update cancelled.".into();
                    }
                    Err(error) => {
                        self.update_prompt.error = Some(error);
                        self.update_prompt.status = "The update could not be installed.".into();
                    }
                }
            }
            Message::LauncherUpdateStaged(result) => {
                self.update_cancellation = None;
                self.update_prompt.is_busy = false;
                self.update_prompt.is_progress_indeterminate = false;
                match result {
                    Ok(true) => self.shutdown_requested = true,
                    Ok(false) => {
                        self.update_prompt.status = "The launcher update is ready. Close and \
                                                     reopen the launcher to finish it."
                            .into();
                    }
                    Err(error) if error == crate::updates::UpdateError::Cancelled.to_string() => {
                        self.update_prompt.status = "Update cancelled.".into();
                    }
                    Err(error) => {
                        self.update_prompt.error = Some(error);
                        self.update_prompt.status = "The update could not be installed.".into();
                    }
                }
            }
            Message::HealthResult {
                server_name,
                endpoint,
                snapshot,
            } => {
                let mut count = snapshot.player_count.map_or_else(
                    || "— players".to_owned(),
                    |value| format!("{} players", group_thousands(value)),
                );
                if snapshot.is_player_count_stale {
                    count.push_str(" (stale)");
                }
                // The dot beside the server name already says whether it answered.
                let status = if snapshot.is_reachable {
                    ""
                } else {
                    "No response · "
                };
                for row in self.rows_mut() {
                    if row.server_name == server_name && row.endpoint == endpoint {
                        row.is_server_online = Some(snapshot.is_reachable);
                        row.server_status_text = format!("{status}{count}");
                        row.latency_milliseconds = snapshot.latency_milliseconds;
                    }
                }
            }
            Message::HealthFinished { failed } => {
                self.is_checking_server_health = false;
                self.next_health_check = Instant::now().checked_add(HEALTH_CHECK_INTERVAL);
                if failed && !self.disposed {
                    self.operation_status =
                        "Server status could not be checked. You can still launch.".into();
                }
            }
            Message::PluginCheckFinished(outcome) => self.apply_plugin_outcome(*outcome),
            Message::DiscoverResolved { id, details } => self.apply_discover_details(&id, details),
            Message::DiscoverPassFinished {
                saw_rate_limited,
                saw_transport_failure,
            } => {
                self.plugins.discover_pending_count = 0;
                self.plugins.update_discover_checking_state();
                self.plugins.discover_status_line = if saw_rate_limited {
                    Some(
                        "GitHub is rate limiting; some plugins could not be checked. Try Refresh \
                         list in a minute."
                            .into(),
                    )
                } else if saw_transport_failure {
                    Some(
                        "Some plugins could not be checked. Check your connection and Refresh \
                         list."
                            .into(),
                    )
                } else {
                    None
                };
                if self.plugin_cancellation.is_none() {
                    self.plugins.is_busy = false;
                }
            }
            Message::PermissionsLoaded { generation, result } => {
                self.plugins
                    .install_dialog
                    .permissions_loaded(generation, result);
            }
            Message::InstallFinished(result) => self.apply_install_result(result),
            Message::AddFromUrlResolved(result) => self.apply_add_from_url(result),
            Message::BetaToggled {
                id,
                is_beta,
                result,
            } => {
                self.plugins.is_busy = false;
                match result {
                    Ok(check) => self.replace_installed_row(&id, check),
                    Err(error) => {
                        if let Some(row) = self
                            .plugins
                            .installed
                            .iter_mut()
                            .find(|row| row.info.id.eq_ignore_ascii_case(&id))
                        {
                            row.is_beta_channel = !is_beta;
                        }
                        self.plugins.error = Some(if error.trim().is_empty() {
                            "The plugin's channel could not be changed.".into()
                        } else {
                            error
                        });
                    }
                }
            }
            Message::InstallMoveProgress(entry) => {
                if let Some(panel) = self.install_folder.as_mut() {
                    panel.progress(&entry);
                }
            }
            Message::InstallMoved(result) => {
                if let Some(panel) = self.install_folder.as_mut() {
                    let moved = result.moved;
                    panel.finish_move(result);
                    if moved {
                        self.shutdown_requested = true;
                    }
                }
            }
            Message::ClassicMigrationFinished { apply, result } => {
                if let Some(dialog) = self.classic_migration.as_mut() {
                    dialog.finish(apply, result);
                }
            }
            Message::ThwargImportFinished { apply, result } => {
                let imported = apply && result.as_ref().is_ok_and(|report| report.has_changes());
                self.thwarg_import.finish(apply, result);
                if imported {
                    // New accounts show at once, not only the checked ones.
                    self.on_server_added();
                }
            }
        }
    }

    /// Whether the VirindiTank and UtilityBelt import is offered: only
    /// MossTank reads what it imports, so it waits until MossTank is
    /// installed, and follows the installed list as it changes.
    pub fn show_classic_migration(&self) -> bool {
        self.classic_migration.is_some()
            && self.plugins.installed.iter().any(|row| {
                row.info
                    .id
                    .eq_ignore_ascii_case(crate::classic_migration::MOSSTANK_PLUGIN_ID)
            })
    }

    /// Opens the import dialog.
    pub fn open_classic_migration(&mut self) {
        if let Some(dialog) = self.classic_migration.as_mut() {
            dialog.open();
        }
    }

    /// Preview (`apply` false) or Import, off the window's thread.
    pub fn run_classic_migration(&mut self, apply: bool) {
        let Some(run) = self
            .classic_migration
            .as_mut()
            .and_then(|dialog| dialog.begin_run(apply))
        else {
            return;
        };
        self.run(move |sender, wake| {
            let result = run.run();
            let _ = sender.send(Message::ClassicMigrationFinished {
                apply: run.apply,
                result,
            });
            wake();
        });
    }

    /// Opens the Import from Thwarg launcher dialog.
    pub fn open_thwarg_import(&mut self) {
        if self.can_interact() {
            self.thwarg_import.open();
        }
    }

    /// The Thwarg import's Preview (`apply` false) or Import, off the
    /// window's thread.
    pub fn run_thwarg_import(&mut self, apply: bool) {
        let Some(run) = self.thwarg_import.begin_run(apply) else {
            return;
        };
        let orchestrator = Arc::clone(&self.orchestrator);
        self.run(move |sender, wake| {
            let result = run.run(orchestrator.as_ref());
            let _ = sender.send(Message::ThwargImportFinished {
                apply: run.apply,
                result,
            });
            wake();
        });
    }

    /// Whether Move... can run now: not while a session runs or the
    /// launcher is busy.
    pub fn can_move_install(&self) -> bool {
        self.install_folder
            .as_ref()
            .is_some_and(|panel| panel.can_move(self.is_lifecycle_idle()))
    }

    /// Nothing runs that an installation change must wait for: no
    /// operation, start, stop, automatic relaunch or session.
    pub fn is_lifecycle_idle(&self) -> bool {
        self.row_launches.is_empty()
            && !self.is_busy
            && !self.stop_gate.is_pending()
            && !self.automatic_relaunch.is_launching()
            && !self.has_active_sessions()
    }

    /// Moves the install to the folder typed in the settings, off the
    /// window's thread; on success the launcher restarts.
    pub fn move_install(&mut self) {
        let idle = self.is_lifecycle_idle();
        let Some(panel) = self.install_folder.as_mut() else {
            return;
        };
        let Some(target) = panel.begin_move(idle) else {
            return;
        };
        let mover = panel.mover();
        self.run(move |sender, wake| {
            let progress_sender = sender.clone();
            let progress_wake = Arc::clone(&wake);
            let result = mover.move_to(&target, &mut |entry| {
                let _ = progress_sender.send(Message::InstallMoveProgress(entry.to_owned()));
                progress_wake();
            });
            let _ = sender.send(Message::InstallMoved(result));
            wake();
        });
    }

    fn finish_operation(&mut self) {
        self.operation = None;
        if !self.disposed {
            self.is_busy = false;
            self.refresh_from_core();
        }
    }

    fn apply_content_verification(
        &mut self,
        verification: InstallRecordVerification,
        startup: bool,
    ) {
        let record = verification
            .record
            .clone()
            .filter(|_| verification.is_verified());
        if let Err(error) = self
            .orchestrator
            .set_installation_state(record, &verification.status)
        {
            self.last_error = Some(error_text(&error));
        }
        self.operation_status = verification.status.clone();
        if startup {
            self.operation_status = "Checking for game updates…".into();
            self.start_update_check(true, false);
        } else {
            if !verification.is_verified() {
                self.last_error = Some(verification.status);
            }
            self.finish_operation();
        }
    }

    // --- Core state -----------------------------------------------------------------------

    fn refresh_from_core(&mut self) {
        let snapshot = match self.orchestrator.snapshot() {
            Ok(snapshot) => snapshot,
            Err(error) => {
                self.last_error = Some(error_text(&error));
                return;
            }
        };
        self.refresh_account_rows(&snapshot);
        self.observe_session_controls(&snapshot);
        self.sessions = snapshot.sessions.iter().map(SessionRow::new).collect();
        self.snapshot = Some(snapshot);
        self.refresh_row_states();
    }

    /// Follows the stops still completing and the sessions automatic
    /// relaunch watches.
    fn observe_session_controls(&mut self, snapshot: &LauncherStateSnapshot) {
        self.stop_gate.observe(snapshot);
        let enabled = self.relaunch_checked_clients_automatically;
        let checked: Vec<LauncherRelaunchSelection> = if enabled {
            self.rows()
                .filter(|row| row.is_checked)
                .map(|row| {
                    LauncherRelaunchSelection::new(
                        &row.server_name,
                        &row.account_name,
                        row.character_name(),
                        row.mode(),
                    )
                })
                .collect()
        } else {
            Vec::new()
        };
        self.automatic_relaunch.observe(snapshot, &checked, enabled);
    }

    /// [`Self::observe_session_controls`] against the orchestrator's newest
    /// state.
    fn observe_current_session_controls(&mut self) {
        match self.orchestrator.snapshot() {
            Ok(snapshot) => self.observe_session_controls(&snapshot),
            Err(error) => self.last_error = Some(error_text(&error)),
        }
    }

    /// A row's check, character or launch mode changed.
    fn on_account_row_changed(&mut self) {
        self.apply_profile_filter();
        if self.relaunch_checked_clients_automatically {
            self.observe_current_session_controls();
        }
    }

    /// Starts a due automatic relaunch, if any, on a worker.
    fn run_automatic_relaunch(&mut self) {
        let may_launch = self.can_interact()
            && self.is_relaunch_delay_valid()
            && !self.is_installation_checking
            && !self.is_client_compatibility_check_blocking;
        let orchestrator = Arc::clone(&self.orchestrator);
        let attempt = self.automatic_relaunch.poll(may_launch, &mut |choice| {
            orchestrator.account_launch_capability(&choice.server, &choice.account, choice.mode)
        });
        let attempt = match attempt {
            Ok(Some(attempt)) => attempt,
            Ok(None) => return,
            Err(error) => {
                if !self.disposed {
                    self.on_automatic_relaunch_failed(&error);
                }
                return;
            }
        };
        self.relaunch_attempt_running = true;
        self.run(move |sender, wake| {
            let choice = &attempt.selection;
            let result = orchestrator.launch(
                &choice.server,
                &choice.account,
                choice.character.as_deref(),
                choice.mode,
                &attempt.cancel,
            );
            let _ = sender.send(Message::RelaunchFinished {
                attempt: Box::new(attempt),
                result: Box::new(result),
            });
            wake();
        });
    }

    fn on_automatic_relaunch_failed(&mut self, error: &OrchestratorError) {
        self.last_error = Some(error_text(error));
        self.operation_status =
            "Automatic relaunch failed; another attempt waits the full delay.".into();
    }

    /// Show only checked accounts.
    pub fn show_only_checked_accounts(&self) -> bool {
        self.show_only_checked_accounts
    }

    /// Shows only the checked accounts (with the profile filter), or all.
    pub fn set_show_only_checked_accounts(&mut self, value: bool) {
        if self.show_only_checked_accounts != value {
            self.show_only_checked_accounts = value;
            self.apply_profile_filter();
        }
    }

    /// Relaunch checked clients automatically.
    pub fn relaunch_checked_clients_automatically(&self) -> bool {
        self.relaunch_checked_clients_automatically
    }

    /// Turns keeping checked clients running on or off, and remembers it.
    /// On starts every checked account with a character and no client; off
    /// cancels every pending relaunch.
    pub fn set_relaunch_checked_clients_automatically(&mut self, value: bool) {
        if self.relaunch_checked_clients_automatically != value {
            self.relaunch_checked_clients_automatically = value;
            self.save_relaunch_settings();
            self.observe_current_session_controls();
        }
    }

    /// Takes the remembered relaunch settings (at start-up, before the
    /// first observation).
    fn restore_relaunch_settings(&mut self) {
        let Some(store) = &self.relaunch_settings else {
            return;
        };
        match store.load() {
            Ok(settings) => {
                self.relaunch_checked_clients_automatically = settings.enabled;
                self.relaunch_delay_seconds_text = settings.delay_seconds.to_string();
                // In range by construction.
                let _ = self
                    .automatic_relaunch
                    .set_delay_seconds(settings.delay_seconds);
            }
            Err(error) => self.last_error = Some(error.0),
        }
    }

    /// Remembers the relaunch settings; a delay box that is not a valid
    /// delay keeps the last valid one.
    fn save_relaunch_settings(&mut self) {
        let Some(store) = &self.relaunch_settings else {
            return;
        };
        let settings = LauncherRelaunchSettings {
            enabled: self.relaunch_checked_clients_automatically,
            delay_seconds: self.automatic_relaunch.delay_seconds(),
        };
        if let Err(error) = store.save(settings) {
            self.last_error = Some(error.0);
        }
    }

    /// The delay box's text.
    pub fn relaunch_delay_seconds_text(&self) -> &str {
        &self.relaunch_delay_seconds_text
    }

    /// Types into the delay box; a whole number from 5 to 3600 becomes the
    /// delay.
    pub fn set_relaunch_delay_seconds_text(&mut self, text: &str) {
        if self.relaunch_delay_seconds_text == text {
            return;
        }
        text.clone_into(&mut self.relaunch_delay_seconds_text);
        if let Some(seconds) = parse_relaunch_delay(text) {
            // In range by construction.
            let _ = self.automatic_relaunch.set_delay_seconds(seconds);
            self.save_relaunch_settings();
        }
    }

    /// The delay box holds a whole number from 5 to 3600; automatic
    /// relaunch waits while it does not.
    pub fn is_relaunch_delay_valid(&self) -> bool {
        parse_relaunch_delay(&self.relaunch_delay_seconds_text).is_some()
    }

    fn refresh_account_rows(&mut self, snapshot: &LauncherStateSnapshot) {
        // One group per account, whatever the case of its name, in the order
        // the accounts first appear and under the first spelling; one row
        // per saved entry on each server (a file may hold two spellings of
        // one account on a server).
        let mut order: Vec<String> = Vec::new();
        for account in snapshot
            .servers
            .iter()
            .flat_map(|server| server.accounts.iter())
        {
            if !order
                .iter()
                .any(|name| same_account(name, &account.account_name))
            {
                order.push(account.account_name.clone());
            }
        }
        let mut groups = std::mem::take(&mut self.accounts);
        for name in &order {
            // Reused only under the same spelling, so a renamed account
            // shows its new name.
            let mut group = match groups.iter().position(|group| &group.account_name == name) {
                Some(index) => groups.remove(index),
                None => AccountGroup::new(name.clone()),
            };
            let mut rows = std::mem::take(&mut group.servers);
            let mut entries: Vec<&LauncherAccountSnapshot> = Vec::new();
            for server in &snapshot.servers {
                for account in server
                    .accounts
                    .iter()
                    .filter(|account| same_account(&account.account_name, name))
                {
                    entries.push(account);
                    let mut row = match rows.iter().position(|row| {
                        row.server_name == server.name && row.account_name == account.account_name
                    }) {
                        Some(index) => rows.remove(index),
                        None => {
                            let mut row = AccountServerRow::new(
                                account.account_name.clone(),
                                server.name.clone(),
                            );
                            row.is_checked = self.checked_accounts_loaded
                                && self.checked_accounts.as_ref().is_some_and(|store| {
                                    store.contains(&server.name, &account.account_name)
                                });
                            row
                        }
                    };
                    let session = snapshot
                        .sessions
                        .iter()
                        .filter(|session| {
                            session.server_name == server.name
                                && same_account(&session.account_name, &account.account_name)
                        })
                        .max_by_key(|session| session.created_at);
                    row.update(server, account, session);
                    group.servers.push(row);
                }
            }
            let names = self.plugin_display_names();
            group.update(&entries, |id| {
                names
                    .iter()
                    .find(|(known, _)| known.eq_ignore_ascii_case(id))
                    .map_or_else(|| id.to_owned(), |(_, name)| name.clone())
            });
            self.accounts.push(group);
        }
        self.refresh_profile_filters(snapshot);
        self.report_window_switches(snapshot);
    }

    /// Installed plugins by id and name, for the account summaries.
    fn plugin_display_names(&self) -> Vec<(String, String)> {
        self.plugins
            .installed
            .iter()
            .map(|row| (row.info.id.clone(), row.info.display_name.clone()))
            .collect()
    }

    /// Rebuilds the chips from the profile list (an empty profile has its
    /// chip too), keeping the chosen one while it exists.
    fn refresh_profile_filters(&mut self, snapshot: &LauncherStateSnapshot) {
        let tags = snapshot.profiles.clone();
        if let Some(filter) = &self.profile_filter
            && !tags.iter().any(|tag| tag.eq_ignore_ascii_case(filter))
        {
            self.profile_filter = None;
        }
        let wanted: Vec<Option<String>> = std::iter::once(None)
            .chain(tags.into_iter().map(Some))
            .collect();
        if self
            .profile_filters
            .iter()
            .map(|chip| chip.profile.clone())
            .ne(wanted.iter().cloned())
        {
            self.profile_filters = wanted
                .into_iter()
                .map(|profile| ProfileFilterChip {
                    label: profile.clone().unwrap_or_else(|| "All".into()),
                    profile,
                    is_selected: false,
                })
                .collect();
        }
        self.apply_profile_filter();
    }

    fn apply_profile_filter(&mut self) {
        let filter = self.profile_filter.clone();
        let matches = |profile: &Option<String>| match (profile, &filter) {
            (None, None) => true,
            (Some(profile), Some(filter)) => profile.eq_ignore_ascii_case(filter),
            _ => false,
        };
        for chip in &mut self.profile_filters {
            chip.is_selected = matches(&chip.profile);
        }
        let only_checked = self.show_only_checked_accounts;
        let query = self.account_search.trim().to_lowercase();
        for group in &mut self.accounts {
            for row in &mut group.servers {
                row.is_visible = filter
                    .as_deref()
                    .is_none_or(|filter| row.has_profile(filter))
                    && (!only_checked || row.is_checked);
                row.matches_search = query.is_empty() || row.matches(&query);
            }
            group.is_visible = group.servers.iter().any(|row| row.is_visible);
            group.matches_search = group.servers.iter().any(AccountServerRow::is_shown);
        }
    }

    /// The search box's text.
    pub fn account_search(&self) -> &str {
        &self.account_search
    }

    /// Shows only the accounts whose name, or one of whose characters'
    /// names, holds `text` (ignoring case); empty shows all. It only hides
    /// rows: Play selected still starts every checked row the profile filter
    /// shows.
    pub fn set_account_search(&mut self, text: &str) {
        if self.account_search != text {
            text.clone_into(&mut self.account_search);
            self.apply_profile_filter();
        }
    }

    /// Checked rows that Play selected starts but the search hides.
    pub fn checked_rows_hidden_by_search(&self) -> usize {
        self.checked_rows().filter(|row| !row.is_shown()).count()
    }

    /// Filters the accounts by a profile tag (`None`: all accounts): a row
    /// shows when its account or one of its characters is in the profile.
    /// Choosing a profile also sets each row whose account has a character
    /// in it to that character (saved, as choosing it by hand), so Play
    /// selected starts the profile's characters; a row shown for its
    /// account's tag keeps its character, so does a row whose session runs
    /// or starts, and "All" changes none.
    pub fn set_profile_filter(&mut self, profile: Option<&str>) {
        self.profile_filter = profile.map(str::to_owned);
        if let Some(profile) = profile {
            let switches: Vec<(String, String, String)> = self
                .rows()
                // A row whose session runs or starts keeps its pick (its box
                // is locked too), so a relaunch brings back the character
                // that was running.
                .filter(|row| {
                    !row.is_active() && !self.is_row_starting(&row.account_name, &row.server_name)
                })
                .filter_map(|row| {
                    row.character_in_profile(profile).map(|character| {
                        (
                            row.account_name.clone(),
                            row.server_name.clone(),
                            character.to_owned(),
                        )
                    })
                })
                .collect();
            // A row already on its profile's character saves nothing: the
            // pick is the same.
            for (account, server, character) in switches {
                self.set_row_character(&account, &server, &character);
            }
        }
        self.apply_profile_filter();
    }

    /// There is a tag to filter by.
    pub fn has_profile_filters(&self) -> bool {
        self.profile_filters.len() > 1
    }

    /// The ticked rows the profile filter shows: what Play selected starts.
    fn checked_rows(&self) -> impl Iterator<Item = &AccountServerRow> {
        self.accounts
            .iter()
            .filter(|group| group.is_visible)
            .flat_map(|group| group.servers.iter())
            .filter(|row| row.is_visible && row.is_checked)
    }

    /// Reads every account's launch availability (once a frame: a
    /// reconnect hold can run out without an event) and recomputes every
    /// row's Play state from it and the displayed snapshot.
    pub fn refresh_row_states(&mut self) {
        self.refresh_account_availability();
        let can_start = self.can_start_rows();
        let blocking = self.is_installation_checking || self.is_client_compatibility_check_blocking;
        let snapshot = self.snapshot.take();
        let availability = [
            (
                LaunchMode::Gui,
                std::mem::take(&mut self.graphical_availability),
            ),
            (
                LaunchMode::Headless,
                std::mem::take(&mut self.headless_availability),
            ),
            (
                LaunchMode::GuiSelect,
                std::mem::take(&mut self.character_select_availability),
            ),
        ];
        let starting = std::mem::take(&mut self.starting_rows);
        for row in self.rows_mut() {
            let key = (row.server_name.clone(), row.account_name.clone());
            row.disabled_reason = if !can_start {
                "Finish the current operation or close the dialog first.".into()
            } else if is_account_in(&starting, &row.server_name, &row.account_name) {
                "This account is starting.".into()
            } else {
                row_launch_block(
                    snapshot.as_ref(),
                    &mut |mode| {
                        Ok(availability
                            .iter()
                            .find(|(known, _)| *known == mode)
                            .and_then(|(_, map)| map.get(&key).cloned())
                            .unwrap_or_else(|| {
                                LauncherCapability::unavailable(
                                    "Launch availability has not been checked.",
                                )
                            }))
                    },
                    blocking,
                    &row.server_name,
                    &row.account_name,
                    row.character_name(),
                    row.mode(),
                )
                .unwrap_or_default()
            };
            // Only the server's hold blocks Play: the row shows its
            // countdown, not just a tooltip.
            let mode = row.mode();
            row.hold_text = availability
                .iter()
                .find(|(known, _)| *known == mode)
                .and_then(|(_, map)| map.get(&key))
                .filter(|capability| capability.held_for.is_some())
                .and_then(|capability| capability.reason.clone())
                .filter(|reason| *reason == row.disabled_reason);
        }
        self.starting_rows = starting;
        self.snapshot = snapshot;
        let [(_, graphical), (_, headless), (_, select)] = availability;
        self.graphical_availability = graphical;
        self.headless_availability = headless;
        self.character_select_availability = select;
    }

    /// Captures every account's launch availability for this refresh.
    fn refresh_account_availability(&mut self) {
        let read = |mode| self.orchestrator.account_launch_capabilities(mode);
        match (
            read(LaunchMode::Gui),
            read(LaunchMode::Headless),
            read(LaunchMode::GuiSelect),
        ) {
            (Ok(graphical), Ok(headless), Ok(select)) => {
                self.graphical_availability = graphical;
                self.headless_availability = headless;
                self.character_select_availability = select;
            }
            (Err(error), _, _) | (_, Err(error), _) | (_, _, Err(error)) => {
                self.last_error = Some(error_text(&error));
            }
        }
    }

    fn rows_mut(&mut self) -> impl Iterator<Item = &mut AccountServerRow> {
        self.accounts
            .iter_mut()
            .flat_map(|group| group.servers.iter_mut())
    }

    fn rows(&self) -> impl Iterator<Item = &AccountServerRow> {
        self.accounts.iter().flat_map(|group| group.servers.iter())
    }

    /// The position, among all rows, of the row for `account` on `server`:
    /// the server exactly, the account spelled exactly so or else the first
    /// row of that account in any case.
    fn row_index(&self, account: &str, server: &str) -> Option<usize> {
        let on_server: Vec<(usize, &AccountServerRow)> = self
            .rows()
            .enumerate()
            .filter(|(_, row)| row.server_name == server)
            .collect();
        find_account(&on_server, |(_, row)| &row.account_name, account)
            .map(|index| on_server[index].0)
    }

    /// The row for `account` on `server`.
    pub fn row(&self, account: &str, server: &str) -> Option<&AccountServerRow> {
        let index = self.row_index(account, server)?;
        self.rows().nth(index)
    }

    fn row_mut(&mut self, account: &str, server: &str) -> Option<&mut AccountServerRow> {
        let index = self.row_index(account, server)?;
        self.rows_mut().nth(index)
    }

    // --- Derived state --------------------------------------------------------------------

    /// A command or launch runs.
    pub fn is_busy(&self) -> bool {
        self.is_busy
    }

    /// Any dialog is open.
    pub fn is_modal_open(&self) -> bool {
        self.text_editor.is_open
            || self.add_server.is_open
            || self.thwarg_import.is_open
            || self
                .classic_migration
                .as_ref()
                .is_some_and(|dialog| dialog.is_open)
            || self.first_run.is_open
            || self.update_prompt.is_open
            || self.is_row_options_open
            || self.is_session_log_open
            || self.is_settings_open
            || self.plugins.install_dialog.is_open
            || self.plugins.is_remove_dialog_open
            || !self.plugins.add_from_url_choices.is_empty()
            || self.remove_character_target.is_some()
            || self.profiles_dialog.is_open
            || self.profile_migration_notice.is_some()
            || self.close_prompt.is_some()
    }

    /// Commands that change things may run.
    pub fn can_interact(&self) -> bool {
        !self.is_busy && !self.is_modal_open() && self.closing.is_none()
    }

    /// Rows may start: another account's start or a stop does not prevent
    /// it.
    pub fn can_start_rows(&self) -> bool {
        self.can_interact()
    }

    /// The row's own controls (its selection, its options) may change: not
    /// while its account starts.
    pub fn can_interact_with_row(&self, account: &str, server: &str) -> bool {
        self.can_interact() && !self.is_row_starting(account, server)
    }

    /// The row's account is starting.
    pub fn is_row_starting(&self, account: &str, server: &str) -> bool {
        is_account_in(&self.starting_rows, server, account)
    }

    /// Stop may run for `session_id`: only that session's own pending stop
    /// prevents it.
    pub fn can_stop_session(&self, session_id: &str) -> bool {
        !self.is_busy
            && !self.is_modal_open()
            && self.closing.is_none()
            && !self.stop_gate.is_stopping(session_id)
    }

    /// The row's Stop may run.
    pub fn can_stop_row(&self, account: &str, server: &str) -> bool {
        self.row(account, server)
            .and_then(AccountServerRow::active_session_id)
            .is_some_and(|id| self.can_stop_session(id))
    }

    /// Something Cancel can cancel runs: an operation or a row start.
    pub fn has_cancelable_operation(&self) -> bool {
        (self.operation.is_some() && self.is_busy) || !self.row_launches.is_empty()
    }

    /// Cancel may run.
    pub fn can_cancel_operation(&self) -> bool {
        !self.is_modal_open() && self.has_cancelable_operation()
    }

    /// The plugin commands may run: not while rows start.
    fn can_interact_with_plugins(&self) -> bool {
        self.can_interact() && self.row_launches.is_empty()
    }

    /// Whether there is any account group.
    pub fn has_accounts(&self) -> bool {
        !self.accounts.is_empty()
    }

    /// `N selected · M ready`.
    pub fn checked_selection_summary(&self) -> String {
        let checked = self.checked_rows().count();
        let ready = self.checked_rows().filter(|row| row.can_play()).count();
        format!("{checked} selected · {ready} ready")
    }

    /// `Play selected (2)`.
    pub fn play_checked_text(&self) -> String {
        format!("Play selected ({})", self.checked_rows().count())
    }

    /// Play selected would start something.
    pub fn has_playable_checked_rows(&self) -> bool {
        self.checked_rows().any(AccountServerRow::can_play)
    }

    /// Whether Play selected may run.
    pub fn can_launch_checked(&self) -> bool {
        self.can_start_rows() && self.has_playable_checked_rows()
    }

    /// The installation is being checked.
    pub fn is_installation_checking(&self) -> bool {
        self.is_installation_checking
    }

    /// First-run setup is needed.
    pub fn is_first_run_required(&self) -> bool {
        !self.is_installation_checking
            && self
                .snapshot
                .as_ref()
                .is_some_and(|snapshot| !snapshot.is_installation_ready)
    }

    /// The installation banner is shown.
    pub fn show_installation_banner(&self) -> bool {
        self.is_installation_checking || self.is_first_run_required()
    }

    /// The installation banner's heading.
    pub fn installation_banner_title(&self) -> &'static str {
        if self.is_installation_checking {
            "Checking installation"
        } else {
            "Client setup required"
        }
    }

    /// The installation line.
    pub fn installation_status(&self) -> String {
        self.snapshot.as_ref().map_or_else(
            || "Installation state is loading.".into(),
            |snapshot| snapshot.installation_status.clone(),
        )
    }

    /// The update banner is shown.
    pub fn show_update_banner(&self) -> bool {
        self.update_prompt.has_something_to_update()
    }

    /// Any session is running.
    pub fn has_active_sessions(&self) -> bool {
        self.sessions.iter().any(|session| session.is_active)
    }

    /// The installed client's version at the top right, without build
    /// metadata, or `not installed`. The launcher and the client can differ:
    /// the launcher updates only when it changed.
    pub fn client_version_text(&self) -> String {
        (self.client_version)()
            .and_then(|resolution| resolution.version)
            .map_or_else(
                || "not installed".to_owned(),
                |version| short_version(version.value()),
            )
    }

    /// The launcher's version at the top right, without build metadata.
    pub fn launcher_version_text(&self) -> String {
        self.launcher_version
            .as_deref()
            .map(short_version)
            .unwrap_or_default()
    }

    /// The versions are shown.
    pub fn has_versions(&self) -> bool {
        self.launcher_version.is_some()
    }

    /// The plugins line at the top right: `all up to date`, `2 updates`,
    /// `checking…` or `none installed`.
    pub fn plugins_update_text(&self) -> String {
        if self.plugins.is_busy {
            return "checking…".into();
        }
        if self.plugins.installed.is_empty() {
            return "none installed".into();
        }
        match self.plugin_update_count() {
            0 => "all up to date".into(),
            1 => "1 update".into(),
            updates => format!("{updates} updates"),
        }
    }

    /// Some installed plugin has an update.
    pub fn has_plugin_updates(&self) -> bool {
        self.plugin_update_count() > 0
    }

    fn plugin_update_count(&self) -> usize {
        self.plugins
            .installed
            .iter()
            .filter(|row| row.update.is_some())
            .count()
    }

    /// Checks for client, launcher and plugin updates every 20 minutes
    /// while the launcher is open, the startup check being the first; a
    /// check by the button restarts the wait. Call it every frame.
    pub fn poll_update_check(&mut self, now: Instant) {
        if self.disposed || !self.startup_initialization_complete {
            return;
        }
        let next = *self
            .next_update_check
            .get_or_insert_with(|| now + UPDATE_CHECK_INTERVAL);
        if now < next || self.update_prompt.is_busy {
            return;
        }
        self.check_for_updates_at(false, now);
    }

    /// Reads the installed plugins' release feeds every 30 seconds while the
    /// launcher is open (10 minutes after GitHub asks to slow down), and
    /// runs the plugin part of the update check when one changed. Call it
    /// every frame.
    pub fn poll_plugin_release_watch(&mut self, now: Instant) {
        if self.disposed || !self.startup_initialization_complete || self.plugin_watch_running {
            return;
        }
        let Some(backend) = self.plugin_backend.clone() else {
            return;
        };
        let wait = if std::mem::take(&mut self.plugin_watch_backoff) {
            PLUGIN_RELEASE_WATCH_BACKOFF
        } else {
            PLUGIN_RELEASE_WATCH_INTERVAL
        };
        let next = *self.next_plugin_watch.get_or_insert(now + wait);
        if now < next {
            return;
        }
        self.next_plugin_watch = None;
        self.plugin_watch_running = true;
        self.run(move |sender, wake| {
            let state = backend.release_feeds(&CancelToken::new());
            let _ = sender.send(Message::PluginFeedsWatched(Box::new(state)));
            wake();
        });
    }

    fn apply_plugin_feeds(&mut self, state: PluginFeedState) {
        self.plugin_watch_running = false;
        let PluginFeedState::Read(read) = state else {
            self.plugin_watch_backoff = true;
            return;
        };
        let Some(seen) = &self.plugin_feeds_seen else {
            // The first read: the launcher's start-up check covered it.
            self.plugin_feeds_seen = Some(
                read.into_iter()
                    .filter_map(|(repo, tags)| tags.map(|tags| (repo, tags)))
                    .collect(),
            );
            return;
        };
        // A feed that could not be read this time keeps what it listed.
        let feeds: BTreeMap<String, Vec<String>> = read
            .into_iter()
            .filter_map(|(repo, tags)| {
                let tags = tags.or_else(|| seen.get(&repo).cloned())?;
                Some((repo, tags))
            })
            .collect();
        if feeds == *seen || self.plugins.is_busy {
            // Unchanged, or another check runs and the next read sees the change again.
            return;
        }
        self.plugin_feeds_pending = Some(feeds);
        self.check_installed_plugin_updates();
    }

    /// The client and launcher from the release feed, and the installed
    /// plugins. A background check only raises the banner; the button also
    /// opens what it found.
    fn check_for_updates_at(&mut self, open_prompt: bool, now: Instant) {
        self.next_update_check = Some(now + UPDATE_CHECK_INTERVAL);
        self.check_installed_plugin_updates();
        if !self.update_prompt.is_busy {
            self.start_update_check(false, open_prompt);
        }
    }

    /// The update check's plugin part: the plugin list and the installed
    /// plugins' releases only. The panel's messages and Discover stay as
    /// they are, and nothing changes when the list cannot be reached.
    fn check_installed_plugin_updates(&mut self) {
        let Some(backend) = self.plugin_backend.clone() else {
            return;
        };
        if self.plugins.is_busy {
            return;
        }
        let cancel = CancelToken::new();
        self.plugin_cancellation = Some(cancel.clone());
        self.plugins.is_busy = true;
        let client = (self.client_version)();
        self.run(move |sender, wake| {
            let outcome = backend.check(client.as_ref(), &cancel);
            let _ = sender.send(Message::PluginUpdatesChecked(Box::new(outcome)));
            wake();
        });
    }

    fn apply_installed_plugin_updates(&mut self, outcome: PluginCheckOutcome) {
        self.plugin_cancellation = None;
        self.plugins.is_busy = false;
        let pending = self.plugin_feeds_pending.take();
        if outcome.rate_limited || outcome.catalog.is_none() {
            // Not checked: the release watch sees the change again.
            return;
        }
        if pending.is_some() {
            self.plugin_feeds_seen = pending;
        }
        if let Err(error) = self
            .orchestrator
            .set_plugin_catalog(outcome.catalog.clone())
        {
            self.last_error = Some(error_text(&error));
        }
        self.plugins.list_age = outcome.list_age;
        let mut installed = Vec::new();
        for info in outcome.installed.iter().cloned() {
            let key = info.id.to_lowercase();
            let update = outcome.updates_available.get(&key).cloned();
            let withheld = outcome.update_withheld_reasons.get(&key).cloned();
            installed.push(self.build_installed_row(info, update, withheld));
        }
        self.plugins.installed = installed;
    }

    // --- Accounts tab ---------------------------------------------------------------------

    /// Ticks or unticks a row, and remembers every ticked row (shown or
    /// filtered out) for the next start.
    pub fn set_row_checked(&mut self, account: &str, server: &str, checked: bool) {
        let Some(row) = self.row_mut(account, server) else {
            return;
        };
        if row.is_checked == checked {
            return;
        }
        row.is_checked = checked;
        self.save_checked_accounts();
        self.on_account_row_changed();
        self.refresh_row_states();
    }

    fn save_checked_accounts(&mut self) {
        if self.checked_accounts.is_none() {
            return;
        }
        if !self.checked_accounts_loaded {
            self.last_error =
                Some("Checked accounts could not be loaded; selections cannot be saved.".into());
            return;
        }
        let rows: Vec<LauncherCheckedAccount> = self
            .rows()
            .filter(|row| row.is_checked)
            .map(|row| LauncherCheckedAccount::new(&row.server_name, &row.account_name))
            .collect();
        if let Some(store) = self.checked_accounts.as_mut()
            && let Err(error) = store.save(rows)
        {
            self.last_error = Some(error.0);
        }
    }

    /// Picks a row's character and saves the choice.
    pub fn set_row_character(&mut self, account: &str, server: &str, character: &str) {
        let Some(row) = self.row_mut(account, server) else {
            return;
        };
        if row.selected_character == character {
            return;
        }
        row.selected_character = if row.character_choices.iter().any(|c| c == character) {
            character.to_owned()
        } else {
            CHARACTER_SELECT.to_owned()
        };
        self.save_row_selection(account, server);
    }

    /// Picks a row's launch mode and saves the choice. While the row's play
    /// session runs, the pick also switches it between graphical and
    /// headless without a restart: the client's
    /// console is told `/window <id>` or `/close`, and the box follows what
    /// the client reports. A switch that cannot be sent leaves the box and
    /// the saved choice as they were and says why on the status line.
    pub fn set_row_launch_mode(&mut self, account: &str, server: &str, mode: RowLaunchMode) {
        let Some(row) = self.row(account, server) else {
            return;
        };
        let switched = row.is_active();
        if switched {
            if !row.can_switch_window() || row.displayed_launch_mode() == mode {
                return;
            }
            let Some(session_id) = row.active_session_id().map(str::to_owned) else {
                return;
            };
            let open = mode == RowLaunchMode::Graphical;
            if let Err(error) = self.orchestrator.set_session_window(&session_id, open) {
                self.last_error = Some(error_text(&error));
                self.refresh_from_orchestrator();
                return;
            }
            self.window_switches.retain(|(id, _)| *id != session_id);
            self.window_switches.push((session_id, open));
            self.last_error = None;
        }
        let Some(row) = self.row_mut(account, server) else {
            return;
        };
        if row.selected_launch_mode != mode {
            row.selected_launch_mode = mode;
            self.save_row_selection(account, server);
        }
        if switched {
            self.refresh_from_orchestrator();
        }
    }

    /// Reads the orchestrator's state into the rows now.
    fn refresh_from_orchestrator(&mut self) {
        match self.orchestrator.snapshot() {
            Ok(snapshot) => self.refresh_account_rows(&snapshot),
            Err(error) => self.last_error = Some(error_text(&error)),
        }
    }

    /// Puts on the status line why a switch a row asked for did not happen,
    /// once its client has answered or the switch was given up.
    fn report_window_switches(&mut self, snapshot: &LauncherStateSnapshot) {
        let mut failures = Vec::new();
        self.window_switches.retain(|(id, open)| {
            let Some(session) = snapshot
                .sessions
                .iter()
                .find(|session| session.session_id == *id)
            else {
                return false;
            };
            if !session.is_process_or_start_active() {
                return false;
            }
            if session.window.switching_to.is_some() {
                return true;
            }
            if session.window.is_open != *open {
                failures.push(session.window.error.clone().unwrap_or_else(|| {
                    "The client did not switch the session's window.".to_owned()
                }));
            }
            false
        });
        if let Some(failure) = failures.pop() {
            self.last_error = Some(failure);
        }
    }

    // --- Closing the launcher -------------------------------------------------------------

    /// The window's close button: true when it may close now. While any
    /// client the launcher started still runs, it asks first (OpenAC closes
    /// at once) and returns false; a session that
    /// has ended does not count.
    pub fn request_close(&mut self) -> bool {
        if self.closing.is_some() {
            // The closing view is up; it closes the window when done.
            return false;
        }
        if self.close_confirmed {
            return true;
        }
        let running = match self.orchestrator.snapshot() {
            Ok(snapshot) => snapshot
                .sessions
                .iter()
                .filter(|session| session.is_process_or_start_active())
                .count(),
            // Nothing is known to run; closing stops whatever does.
            Err(_) => 0,
        };
        if running == 0 {
            self.close_prompt = None;
            return true;
        }
        self.close_prompt = Some(ClosePrompt { running });
        false
    }

    /// The confirmation's Close: every client is logged out at once while
    /// the window shows the closing; it closes when all have ended.
    pub fn confirm_close(&mut self) {
        if self.close_prompt.take().is_some() {
            self.close_confirmed = true;
            self.begin_closing();
        }
    }

    /// The closing in progress, when the launcher is closing over clients.
    pub fn closing(&self) -> Option<&LauncherClosing> {
        self.closing.as_ref()
    }

    /// Whether the window should close now: the player confirmed closing,
    /// or a staged launcher update or install move asked for a restart,
    /// and every client has ended. The first call starts the closing.
    pub fn should_exit(&mut self) -> bool {
        if !self.close_confirmed && !self.shutdown_requested {
            return false;
        }
        self.begin_closing();
        self.closing
            .as_ref()
            .is_some_and(LauncherClosing::is_finished)
    }

    /// Starts closing the launcher: nothing new may start (no Play, no
    /// automatic relaunch, no control-endpoint start) and every running
    /// client is stopped at once, each on its own worker with Stop's
    /// graceful wait before a kill. Runs once.
    fn begin_closing(&mut self) {
        if self.closing.is_some() {
            return;
        }
        self.closing = Some(LauncherClosing::new());
        self.close_prompt = None;
        if let Err(error) = self.orchestrator.close_to_new_sessions() {
            self.last_error = Some(error_text(&error));
        }
        self.automatic_relaunch.dispose();
        for (_, cancel) in &self.row_launches {
            cancel.cancel();
        }
        self.operation_status = "Closing…".into();
        self.follow_closing();
    }

    /// Reads the newest state into the closing and starts the stops it
    /// asks for: a graceful one for each client that joined, a forced one
    /// for each after "Close now".
    fn follow_closing(&mut self) {
        let Some(closing) = self.closing.as_mut() else {
            return;
        };
        let snapshot = match self.orchestrator.snapshot() {
            Ok(snapshot) => snapshot,
            // The orchestrator is gone: every process it had was stopped.
            Err(_) => return,
        };
        let joined = closing.observe(&snapshot);
        let timeout = if closing.is_force_requested() {
            Duration::ZERO
        } else {
            GRACEFUL_STOP_TIMEOUT
        };
        for session_id in joined {
            self.start_closing_stop(session_id, timeout);
        }
    }

    /// "Close now": kills every client still logging out.
    pub fn close_now(&mut self) {
        let Some(closing) = self.closing.as_mut() else {
            return;
        };
        for session_id in closing.request_force() {
            self.start_closing_stop(session_id, Duration::ZERO);
        }
        self.follow_closing();
    }

    fn start_closing_stop(&self, session_id: String, timeout: Duration) {
        let orchestrator = Arc::clone(&self.orchestrator);
        self.run(move |sender, wake| {
            // Not cancellable: a closing stop ends in a kill.
            let result = orchestrator.stop_session(&session_id, timeout, &CancelToken::new());
            let _ = sender.send(Message::CloseStopFinished { session_id, result });
            wake();
        });
    }

    /// The confirmation's Cancel: nothing closes and every client runs on.
    pub fn cancel_close(&mut self) {
        self.close_prompt = None;
    }

    /// Whether the player confirmed closing.
    pub fn is_close_confirmed(&self) -> bool {
        self.close_confirmed
    }

    fn save_row_selection(&mut self, account: &str, server: &str) {
        let Some(row) = self.row(account, server) else {
            return;
        };
        let (character, mode) = (row.character_name().map(str::to_owned), row.mode());
        if let Err(error) =
            self.orchestrator
                .update_account_selection(server, account, character.as_deref(), mode)
        {
            self.last_error = Some(error_text(&error));
        }
        self.on_account_row_changed();
        self.refresh_row_states();
    }

    /// Expands or collapses a group.
    pub fn set_group_expanded(&mut self, account: &str, expanded: bool) {
        if let Some(group) = self
            .accounts
            .iter_mut()
            .find(|group| same_account(&group.account_name, account))
        {
            group.is_expanded = expanded;
        }
    }

    /// Play selected.
    pub fn launch_checked(&mut self) {
        if !self.can_launch_checked() {
            return;
        }
        let rows: Vec<(String, String)> = self
            .checked_rows()
            .map(|row| (row.account_name.clone(), row.server_name.clone()))
            .collect();
        self.launch_rows(&rows);
    }

    /// A row's Play.
    pub fn launch_row(&mut self, account: &str, server: &str) {
        // The row shows the state of the last refresh; the launch checks the newest.
        if self
            .row(account, server)
            .is_some_and(AccountServerRow::can_play)
        {
            self.launch_rows(&[(account.to_owned(), server.to_owned())]);
        }
    }

    /// Starts each row as its own supervised session, one after another; a
    /// failed or refused row is reported without stopping the others. Only
    /// the rows' accounts are locked while they start: other accounts can
    /// start or stop meanwhile.
    fn launch_rows(&mut self, rows: &[(String, String)]) {
        if !self.can_start_rows() {
            return;
        }
        // One start per account: two rows of one account in different case
        // start once.
        let mut seen: Vec<(String, String)> = Vec::new();
        let pending: Vec<(String, String, Option<String>, LaunchMode)> = rows
            .iter()
            .filter(|(account, server)| {
                if self.is_row_starting(account, server) || is_account_in(&seen, server, account) {
                    return false;
                }
                seen.push((server.clone(), account.clone()));
                true
            })
            .filter_map(|(account, server)| self.row(account, server))
            .map(|row| {
                (
                    row.account_name.clone(),
                    row.server_name.clone(),
                    row.character_name().map(str::to_owned),
                    row.mode(),
                )
            })
            .collect();
        if pending.is_empty() {
            return;
        }
        let cancel = CancelToken::new();
        let launch = self.next_row_launch;
        self.next_row_launch += 1;
        self.row_launches.push((launch, cancel.clone()));
        let rows: Vec<(String, String)> = pending
            .iter()
            .map(|(account, server, _, _)| (server.clone(), account.clone()))
            .collect();
        self.starting_rows.extend(rows.iter().cloned());
        self.last_error = None;
        let orchestrator = Arc::clone(&self.orchestrator);
        let blocking = self.is_installation_checking || self.is_client_compatibility_check_blocking;
        self.run(move |sender, wake| {
            let total = pending.len();
            let mut started = 0;
            let mut errors = Vec::new();
            let mut cancelled = false;
            for (account, server, character, mode) in pending {
                if cancel.is_cancelled() {
                    cancelled = true;
                    break;
                }
                if let Some(block) = fresh_row_launch_block(
                    orchestrator.as_ref(),
                    blocking,
                    &server,
                    &account,
                    character.as_deref(),
                    mode,
                ) {
                    errors.push(format!("{account} / {server}: {block}"));
                    continue;
                }
                let _ = sender.send(Message::OperationStatus(format!(
                    "Launching {} of {total}…",
                    started + 1
                )));
                wake();
                match orchestrator.launch(&server, &account, character.as_deref(), mode, &cancel) {
                    Ok(result)
                        if !matches!(
                            result.state,
                            LauncherActivityState::Failed | LauncherActivityState::Cancelled
                        ) && result.exit_code.is_none_or(|code| code == 0) =>
                    {
                        started += 1;
                    }
                    Ok(result) => errors.push(format!(
                        "{account} / {server}: {}",
                        result.error.unwrap_or(result.status)
                    )),
                    Err(OrchestratorError::Cancelled) if cancel.is_cancelled() => {
                        cancelled = true;
                        break;
                    }
                    Err(error) => {
                        errors.push(format!("{account} / {server}: {}", error_text(&error)))
                    }
                }
            }
            let _ = sender.send(Message::LaunchFinished {
                launch,
                rows,
                started,
                total,
                errors,
                cancelled,
            });
            wake();
        });
    }

    /// A row's Stop: a graceful logout, then a kill after 30 s.
    pub fn stop_row(&mut self, account: &str, server: &str) {
        let Some(id) = self
            .row(account, server)
            .and_then(|row| row.active_session_id().map(str::to_owned))
        else {
            return;
        };
        self.stop_session(&id);
    }

    /// A session's Stop (the row's or the session log's). Only that
    /// session's Stop waits until its process and start-up end; other
    /// sessions can be stopped or started meanwhile.
    pub fn stop_session(&mut self, session_id: &str) {
        if !self.can_stop_session(session_id) {
            return;
        }
        let snapshot = match self.orchestrator.snapshot() {
            Ok(snapshot) => snapshot,
            Err(error) => {
                self.last_error = Some(error_text(&error));
                return;
            }
        };
        if !self.stop_gate.try_begin(session_id, &snapshot) {
            return;
        }
        // A stop is not cancelled by Cancel; it ends in a kill.
        let cancel = CancelToken::new();
        self.last_error = None;
        self.operation_status = "Logging out…".into();
        let orchestrator = Arc::clone(&self.orchestrator);
        let id = session_id.to_owned();
        self.run(move |sender, wake| {
            let result = orchestrator.stop_session(&id, GRACEFUL_STOP_TIMEOUT, &cancel);
            let _ = sender.send(Message::StopFinished(result));
            wake();
        });
    }

    /// The clients Stop all would stop: every play session whose process or
    /// start-up runs and whose Stop may run.
    fn stoppable_sessions(&self) -> Vec<String> {
        self.snapshot
            .iter()
            .flat_map(|snapshot| &snapshot.sessions)
            .filter(|session| {
                session.kind == LauncherActivityKind::Play
                    && session.is_process_or_start_active()
                    && self.can_stop_session(&session.session_id)
            })
            .map(|session| session.session_id.clone())
            .collect()
    }

    /// Stop all may run: some client is running or starting, or a start is
    /// on its way (a Play selected row not reached yet, a relaunch).
    pub fn can_stop_all(&self) -> bool {
        !self.stoppable_sessions().is_empty()
            || !self.row_launches.is_empty()
            || self.automatic_relaunch.is_launching()
    }

    /// Stops every running client at once, each as its row's Stop does: a
    /// graceful logout, then a kill after 30 s. Starts still on their way
    /// are cancelled and pending relaunches dropped, so everything stays
    /// down until it is launched again.
    pub fn stop_all(&mut self) {
        for (_, cancel) in &self.row_launches {
            cancel.cancel();
        }
        self.automatic_relaunch.stop_all();
        let sessions = self.stoppable_sessions();
        for id in &sessions {
            self.stop_session(id);
        }
        if !sessions.is_empty() {
            self.operation_status = match sessions.len() {
                1 => "Logging out 1 client…".into(),
                count => format!("Logging out {count} clients…"),
            };
        }
    }

    /// The bottom bar's Cancel: the running operation and every pending
    /// row start.
    pub fn cancel_operation(&mut self) {
        if let Some(cancel) = &self.operation {
            cancel.cancel();
        }
        for (_, cancel) in &self.row_launches {
            cancel.cancel();
        }
    }

    /// Opens a windowless session's console.
    pub fn open_console(&mut self, account: &str, server: &str) {
        let Some(row) = self.row(account, server) else {
            return;
        };
        let Some(id) = row.active_session_id().filter(|_| row.can_open_console()) else {
            return;
        };
        if self.consoles.iter().any(|console| console.session_id == id) {
            return;
        }
        let who = row
            .active_character_name
            .clone()
            .unwrap_or_else(|| row.account_name.clone());
        let title = format!("Console · {who} · {}", row.server_name);
        self.consoles.push(SessionConsole::new(id, title));
    }

    /// Refreshes every open console and drops closed ones.
    pub fn refresh_consoles(&mut self) {
        self.consoles.retain(|console| console.is_open);
        let orchestrator = Arc::clone(&self.orchestrator);
        for console in &mut self.consoles {
            console.refresh(orchestrator.as_ref());
        }
    }

    /// Whether the consoles show every line the client writes (Verbose) or
    /// only what a player would see: chat, the status box, typed lines and
    /// their answers.
    pub fn console_verbose(&self) -> bool {
        self.console_verbose
    }

    /// Turns the consoles' Verbose on or off, and remembers it.
    pub fn set_console_verbose(&mut self, verbose: bool) {
        if self.console_verbose == verbose {
            return;
        }
        self.console_verbose = verbose;
        if let Some(store) = &self.console_settings
            && let Err(error) = store.save(LauncherConsoleSettings { verbose })
        {
            self.last_error = Some(error.0);
        }
    }

    /// Takes the remembered console settings (at start-up).
    fn restore_console_settings(&mut self) {
        let Some(store) = &self.console_settings else {
            return;
        };
        match store.load() {
            Ok(settings) => self.console_verbose = settings.verbose,
            Err(error) => self.last_error = Some(error.0),
        }
    }

    /// Sends console `index`'s typed line.
    pub fn send_console_line(&mut self, index: usize) {
        let orchestrator = Arc::clone(&self.orchestrator);
        if let Some(console) = self.consoles.get_mut(index) {
            console.send(orchestrator.as_ref());
        }
    }

    // --- Server health --------------------------------------------------------------------

    /// Whether Check servers may run.
    pub fn can_check_servers(&self) -> bool {
        self.server_health.is_some() && !self.disposed
    }

    /// Checks the servers every ten seconds while the window is active; an
    /// inactive window sends nothing, and one that comes back past the
    /// interval checks on the next poll.
    pub fn poll_server_health(&mut self, window_is_active: bool, now: Instant) {
        if !window_is_active
            || self.disposed
            || self.server_health.is_none()
            || self.is_checking_server_health
            || self.next_health_check.is_some_and(|next| now < next)
        {
            return;
        }
        self.next_health_check = now.checked_add(HEALTH_CHECK_INTERVAL);
        self.check_servers();
    }

    /// Check servers.
    pub fn check_servers(&mut self) {
        let Some(service) = self.server_health.clone() else {
            return;
        };
        if self.is_checking_server_health {
            return;
        }
        let servers: Vec<(String, String, i32)> = match self.orchestrator.snapshot() {
            Ok(snapshot) => snapshot
                .servers
                .iter()
                .map(|server| (server.name.clone(), server.host.clone(), server.port))
                .collect(),
            Err(_) => return,
        };
        self.is_checking_server_health = true;
        let cancel = self.health_cancellation.clone();
        self.run(move |sender, wake| {
            let failed = std::sync::atomic::AtomicBool::new(false);
            for chunk in servers.chunks(HEALTH_CHECK_PARALLELISM) {
                std::thread::scope(|scope| {
                    for (name, host, port) in chunk {
                        let service = Arc::clone(&service);
                        let (sender, wake, cancel, failed) = (&sender, &wake, &cancel, &failed);
                        scope.spawn(move || {
                            let Ok(port16) = u16::try_from(*port) else {
                                failed.store(true, std::sync::atomic::Ordering::Relaxed);
                                return;
                            };
                            if let Ok(snapshot) = service.check(host, port16, name, cancel) {
                                let _ = sender.send(Message::HealthResult {
                                    server_name: name.clone(),
                                    endpoint: format!("{host}:{port}"),
                                    snapshot,
                                });
                                wake();
                            }
                        });
                    }
                });
            }
            let _ = sender.send(Message::HealthFinished {
                failed: failed.load(std::sync::atomic::Ordering::Relaxed),
            });
            wake();
        });
    }

    // --- Dialogs: editors, options, log, settings -------------------------------------------

    /// Add server: opens the dialog and loads the known servers on a
    /// worker.
    pub fn open_add_server(&mut self) {
        if !self.can_interact() {
            return;
        }
        self.add_server.open();
        let Some(catalog) = self.known_servers.clone() else {
            self.add_server.list_status =
                "The known server list is not available; add your own server above.".into();
            return;
        };
        self.add_server.is_loading = true;
        self.add_server.list_status = "Loading the known servers…".into();
        self.run(move |sender, wake| {
            let result = catalog
                .load(&CancelToken::new())
                .map_err(|cancelled| cancelled.to_string());
            let _ = sender.send(Message::KnownServersLoaded(result));
            wake();
        });
    }

    /// A known server's Add.
    pub fn add_known_server(&mut self, name: &str) {
        if self.add_server.add_known(name, self.orchestrator.as_ref()) {
            self.on_server_added();
        }
    }

    /// Your own server's Add.
    pub fn add_own_server(&mut self) {
        if self.add_server.add_own(self.orchestrator.as_ref()) {
            self.on_server_added();
        }
    }

    /// A new server shows at once, with every account, not only the
    /// checked ones.
    fn on_server_added(&mut self) {
        self.set_show_only_checked_accounts(false);
        self.refresh_from_core();
    }

    /// Opens Edit accounts, Edit servers or Logon commands.
    pub fn open_text_editor(&mut self, kind: LauncherTextEditorKind) {
        if !self.can_interact() {
            return;
        }
        let orchestrator = Arc::clone(&self.orchestrator);
        if let Err(error) = self.text_editor.open(orchestrator.as_ref(), kind) {
            self.last_error = Some(error_text(&error));
        }
    }

    /// The editor's Save.
    pub fn save_text_editor(&mut self) {
        let orchestrator = Arc::clone(&self.orchestrator);
        self.text_editor.save(orchestrator.as_ref());
    }

    /// The plugins dialog's heading.
    pub fn row_options_title(&self) -> String {
        match (&self.row_options_account, &self.row_options_character) {
            (Some((_, account)), _) => format!("Plugins for {account}"),
            (None, Some((_, _, character))) => format!("Options for {character}"),
            _ => "Select a profile".into(),
        }
    }

    /// The dialog edits an account's list rather than one character's.
    pub fn is_account_row_options(&self) -> bool {
        self.row_options_account.is_some()
    }

    /// The character dialog's choice between its account's plugins and its
    /// own list.
    pub fn show_character_plugin_choice(&self) -> bool {
        !self.is_account_row_options()
    }

    /// The checklist is editable except while a character follows its
    /// account.
    pub fn can_edit_plugin_choices(&self) -> bool {
        self.is_account_row_options() || !self.character_uses_account_plugins
    }

    /// The checklist's caption.
    pub fn row_options_plugins_caption(&self) -> &'static str {
        if self.is_account_row_options() {
            "Every character on this account starts with these plugins."
        } else {
            "This character can use its account's plugins or a list of its own."
        }
    }

    fn account_snapshot(&self, server: &str, account: &str) -> Option<LauncherAccountSnapshot> {
        self.orchestrator.snapshot().ok().and_then(|snapshot| {
            snapshot
                .servers
                .into_iter()
                .find(|candidate| candidate.name == server)
                .and_then(|mut server| {
                    find_account(
                        &server.accounts,
                        |candidate| &candidate.account_name,
                        account,
                    )
                    .map(|index| server.accounts.swap_remove(index))
                })
        })
    }

    fn character_snapshot(
        &self,
        server: &str,
        account: &str,
        character: &str,
    ) -> Option<LauncherCharacterSnapshot> {
        self.account_snapshot(server, account).and_then(|account| {
            account
                .characters
                .into_iter()
                .find(|candidate| candidate.name == character)
        })
    }

    fn installed_plugins_sorted(&self) -> Vec<InstalledPluginInfo> {
        let Some(backend) = &self.plugin_backend else {
            return Vec::new();
        };
        let catalog = backend.current_catalog();
        let mut installed = backend.installed(None, catalog.as_ref());
        installed.sort_by_key(|info| info.display_name.to_lowercase());
        installed
    }

    /// An account's Edit plugins (also a row's Options › Plugins for this
    /// account): one list for every character on it.
    pub fn open_account_plugins(&mut self, account: &str, server: &str) {
        if !self.can_interact() {
            return;
        }
        let Some(snapshot) = self.account_snapshot(server, account) else {
            return;
        };
        self.row_options_account = Some((server.to_owned(), account.to_owned()));
        self.row_options_character = None;
        self.row_options_profiles.clear();
        self.row_options_saved_profiles.clear();
        self.load_plugin_choices(&snapshot.plugins);
        self.last_error = None;
        self.is_row_options_open = true;
    }

    /// A row's Options › Options for this character: its account's plugin
    /// list or one of its own, and the profiles it is in.
    pub fn open_character_plugins(&mut self, account: &str, server: &str) {
        if !self.can_interact_with_row(account, server) {
            return;
        }
        let Some(row) = self.row(account, server) else {
            return;
        };
        let (Some(name), mode) = (row.character_name().map(str::to_owned), row.mode()) else {
            return;
        };
        let Some(snapshot) = self.character_snapshot(server, account, &name) else {
            return;
        };
        self.row_options_account = None;
        self.row_options_character = Some((server.to_owned(), account.to_owned(), name));
        self.character_uses_account_plugins = !snapshot.has_own_plugins;
        self.load_plugin_choices(&snapshot.plugins);
        let names = self
            .orchestrator
            .snapshot()
            .map(|state| state.profiles)
            .unwrap_or_default();
        self.row_options_profiles = names
            .into_iter()
            .map(|name| RowOptionsProfile {
                is_checked: snapshot
                    .profiles
                    .iter()
                    .any(|tag| tag.eq_ignore_ascii_case(&name)),
                name,
            })
            .collect();
        self.row_options_saved_profiles
            .clone_from(&snapshot.profiles);
        self.character_launch_mode = mode;
        self.last_error = None;
        self.is_row_options_open = true;
    }

    /// A row's Options › Logon commands: every account's, in one text.
    pub fn open_logon_commands(&mut self) {
        self.open_text_editor(LauncherTextEditorKind::LogonCommands);
    }

    /// Profiles…: every profile, with New profile and Delete.
    pub fn open_profiles(&mut self) {
        if !self.can_interact() {
            return;
        }
        self.profiles_dialog = ProfilesDialog {
            is_open: true,
            ..ProfilesDialog::default()
        };
        self.refresh_profiles_dialog();
    }

    /// Lists every profile with what is in it.
    fn refresh_profiles_dialog(&mut self) {
        let snapshot = match self.orchestrator.snapshot() {
            Ok(snapshot) => snapshot,
            Err(error) => {
                self.profiles_dialog.error = Some(error_text(&error));
                return;
            }
        };
        let carries =
            |tags: &[String], name: &str| tags.iter().any(|tag| tag.eq_ignore_ascii_case(name));
        let accounts = || {
            snapshot
                .servers
                .iter()
                .flat_map(|server| server.accounts.iter())
        };
        self.profiles_dialog.entries = snapshot
            .profiles
            .iter()
            .map(|name| ProfileListEntry {
                name: name.clone(),
                members: describe_members(
                    accounts()
                        .filter(|account| carries(&account.profiles, name))
                        .count(),
                    accounts()
                        .flat_map(|account| account.characters.iter())
                        .filter(|character| carries(&character.profiles, name))
                        .count(),
                ),
            })
            .collect();
    }

    /// The Profiles dialog's New profile: adds the typed name (trimmed) as
    /// an empty profile; an empty name, one already used (in any case) or
    /// one the tag rules refuse is refused with why.
    pub fn create_profile(&mut self) {
        let name = self.profiles_dialog.new_name.trim().to_owned();
        match self.orchestrator.create_profile(&name) {
            Ok(()) => {
                self.profiles_dialog.new_name.clear();
                self.profiles_dialog.error = None;
                self.operation_status = format!("Added the profile {name}.");
                self.refresh_from_core();
                self.refresh_profiles_dialog();
            }
            Err(error) => self.profiles_dialog.error = Some(error_text(&error)),
        }
    }

    /// The Profiles dialog's Delete: asks first.
    pub fn request_delete_profile(&mut self, name: &str) {
        if self.profiles_dialog.is_open {
            self.profiles_dialog.delete_target = Some(name.to_owned());
        }
    }

    /// Keeps the profile the question was about.
    pub fn cancel_delete_profile(&mut self) {
        self.profiles_dialog.delete_target = None;
    }

    /// Deletes the profile the question was about, from every account and
    /// character; a filter on it goes back to All.
    pub fn confirm_delete_profile(&mut self) {
        let Some(name) = self.profiles_dialog.delete_target.take() else {
            return;
        };
        match self.orchestrator.delete_profile(&name) {
            Ok(()) => {
                self.profiles_dialog.error = None;
                self.operation_status = format!("Deleted the profile {name}.");
                self.refresh_from_core();
                self.refresh_profiles_dialog();
            }
            Err(error) => self.profiles_dialog.error = Some(error_text(&error)),
        }
    }

    /// Closes the Profiles dialog.
    pub fn close_profiles(&mut self) {
        self.profiles_dialog = ProfilesDialog::default();
    }

    /// The checklist for `configured`: every usable installed plugin (every
    /// plugin runs in every launch mode, so nothing is filtered by mode).
    /// Ids already saved that are not offered (not installed, refused,
    /// blocked) stay as checked "missing" rows so a save never drops them
    /// silently.
    fn load_plugin_choices(&mut self, configured_ids: &[String]) {
        self.row_options_choices.clear();
        let configured: HashSet<String> =
            configured_ids.iter().map(|id| id.to_lowercase()).collect();
        let mut placed: HashSet<String> = HashSet::new();
        let mut refused = Vec::new();
        let mut blocked = Vec::new();
        for info in self.installed_plugins_sorted() {
            let key = info.id.to_lowercase();
            if info.refusal.is_some() || info.has_duplicate {
                refused.push((key, info.display_name));
                continue;
            }
            // A blocked plugin is dropped at launch, so it is not offered as a working choice.
            if info.blocked.is_some() {
                blocked.push((key, info.display_name));
                continue;
            }
            if !placed.insert(key.clone()) {
                continue;
            }
            self.row_options_choices.push(RowOptionsChoice {
                id: info.id.clone(),
                display_name: info.display_name,
                is_checked: configured.contains(&key),
                is_missing: false,
            });
        }
        let name_in = |list: &[(String, String)], key: &str| {
            list.iter()
                .find(|(id, _)| id == key)
                .map(|(_, name)| name.clone())
        };
        for id in configured_ids {
            let key = id.to_lowercase();
            if key == "none" || !placed.insert(key.clone()) {
                continue;
            }
            let display_name = if let Some(name) = name_in(&refused, &key) {
                format!("{name} (refused)")
            } else if let Some(name) = name_in(&blocked, &key) {
                format!("{name} (blocked)")
            } else {
                format!("{id} (missing)")
            };
            self.row_options_choices.push(RowOptionsChoice {
                id: id.clone(),
                display_name,
                is_checked: true,
                is_missing: true,
            });
        }
    }

    /// Blank means none: unchecking every plugin saves an empty list.
    fn checked_plugin_ids(&self) -> Vec<String> {
        self.row_options_choices
            .iter()
            .filter(|choice| choice.is_checked)
            .map(|choice| choice.id.clone())
            .collect()
    }

    /// The plugins dialog's Save.
    pub fn save_row_options(&mut self) {
        if !self.is_row_options_open || self.is_busy {
            return;
        }
        if let Some((server, account)) = self.row_options_account.clone() {
            self.save_account_plugin_choices(&server, &account);
        } else {
            self.save_character_settings();
        }
        if self.last_error.is_none() {
            self.is_row_options_open = false;
        }
    }

    fn save_character_settings(&mut self) {
        let Some((server, account, name)) = self.row_options_character.clone() else {
            self.last_error = Some("Select a character first.".into());
            return;
        };
        let plugins = (!self.character_uses_account_plugins).then(|| self.checked_plugin_ids());
        let profiles: Vec<String> = self
            .row_options_profiles
            .iter()
            .filter(|profile| profile.is_checked)
            .map(|profile| profile.name.clone())
            .collect();
        let same = |left: &[String], right: &[String]| {
            left.len() == right.len()
                && left
                    .iter()
                    .all(|tag| right.iter().any(|other| other.eq_ignore_ascii_case(tag)))
        };
        let profiles_changed = !same(&profiles, &self.row_options_saved_profiles);
        let saved = self
            .orchestrator
            .update_character_settings(
                &server,
                &account,
                &name,
                self.character_launch_mode,
                plugins.as_deref(),
            )
            .and_then(|()| {
                if profiles_changed {
                    self.orchestrator
                        .update_character_profiles(&server, &account, &name, &profiles)
                } else {
                    Ok(())
                }
            });
        match saved {
            Ok(()) => {
                self.last_error = None;
                self.operation_status = format!("Saved launch settings for {name}.");
                self.refresh_from_core();
            }
            Err(error) => self.last_error = Some(error_text(&error)),
        }
    }

    /// Saves the account list, keeping the order of what was already there
    /// and adding newly ticked plugins after it.
    fn save_account_plugin_choices(&mut self, server: &str, account: &str) {
        let Some(snapshot) = self.account_snapshot(server, account) else {
            self.last_error = Some("That account no longer exists.".into());
            return;
        };
        let chosen = self.checked_plugin_ids();
        let mut plugins: Vec<String> = snapshot
            .plugins
            .iter()
            .filter(|id| chosen.iter().any(|chosen| chosen.eq_ignore_ascii_case(id)))
            .cloned()
            .collect();
        for id in chosen {
            if !plugins.iter().any(|kept| kept.eq_ignore_ascii_case(&id)) {
                plugins.push(id);
            }
        }
        match self
            .orchestrator
            .update_account_plugins(server, account, &plugins)
        {
            Ok(()) => {
                self.last_error = None;
                self.operation_status = format!("Saved plugins for {account}.");
                self.refresh_from_core();
            }
            Err(error) => self.last_error = Some(error_text(&error)),
        }
    }

    /// A row's Options › Remove character: asks first.
    pub fn request_remove_character(&mut self, account: &str, server: &str) {
        if !self.can_interact_with_row(account, server) {
            return;
        }
        let Some(row) = self.row(account, server) else {
            return;
        };
        if row.is_active() {
            return;
        }
        if let Some(name) = row.character_name() {
            self.remove_character_target =
                Some((server.to_owned(), account.to_owned(), name.to_owned()));
        }
    }

    /// The remove-character question's heading.
    pub fn remove_character_title(&self) -> String {
        self.remove_character_target
            .as_ref()
            .map_or_else(String::new, |(_, _, name)| format!("Remove {name}?"))
    }

    /// What removing the character takes with it.
    pub fn remove_character_message(&self) -> String {
        self.remove_character_target
            .as_ref()
            .map_or_else(String::new, |(server, account, name)| {
                format!(
                    "This removes {name} from {account} on {server}, with its own plugin list if \
                     it has one. The next login to the account brings the character back, using \
                     the account's plugins."
                )
            })
    }

    /// Removes the character the question was about.
    pub fn confirm_remove_character(&mut self) {
        let Some((server, account, name)) = self.remove_character_target.take() else {
            return;
        };
        match self.orchestrator.remove_character(&server, &account, &name) {
            Ok(()) => {
                self.last_error = None;
                self.operation_status =
                    format!("Removed {name}. Refreshing the account's characters brings it back.");
                self.refresh_from_core();
            }
            Err(error) => self.last_error = Some(error_text(&error)),
        }
    }

    /// Dismisses the one-time profile migration notice.
    pub fn dismiss_migration_notice(&mut self) {
        self.profile_migration_notice = None;
    }

    /// Opens the session log.
    pub fn open_session_log(&mut self) {
        if self.can_interact() {
            self.is_session_log_open = true;
        }
    }

    /// Opens launcher settings.
    pub fn open_settings(&mut self) {
        if self.can_interact() {
            self.is_settings_open = true;
        }
    }

    /// Closes the row options, session log and settings dialogs.
    pub fn close_desktop_dialogs(&mut self) {
        self.is_row_options_open = false;
        self.remove_character_target = None;
        self.is_session_log_open = false;
        self.is_settings_open = false;
    }

    /// Escape: closes the dialog on top.
    pub fn close_active_modal(&mut self) {
        if self.close_prompt.is_some() {
            self.cancel_close();
            return;
        }
        self.close_desktop_dialogs();
        if self.profiles_dialog.delete_target.is_some() {
            self.cancel_delete_profile();
        } else if self.profiles_dialog.is_open {
            self.close_profiles();
        } else if self.text_editor.is_open {
            self.text_editor.close();
        } else if self.add_server.is_open {
            self.add_server.close();
        } else if self.thwarg_import.is_open {
            self.thwarg_import.close();
        } else if let Some(dialog) = self.classic_migration.as_mut().filter(|d| d.is_open) {
            dialog.close();
        } else if self.first_run.is_open {
            self.first_run.close();
        } else if self.update_prompt.is_open {
            self.update_prompt.close();
        } else if self.plugins.install_dialog.is_open {
            self.close_install_dialog();
        } else if self.plugins.is_remove_dialog_open && !self.plugins.is_busy {
            self.plugins.is_remove_dialog_open = false;
        } else if !self.plugins.add_from_url_choices.is_empty() {
            self.cancel_add_from_url_choice();
        }
    }

    // --- Installation and updates ---------------------------------------------------------

    /// Opens first-run setup.
    pub fn open_first_run(&mut self) {
        if self.disposed || !self.can_interact() {
            return;
        }
        if let Some(store) = &self.content {
            self.first_run.open(store);
        }
    }

    /// The setup form's directory field changed.
    pub fn set_first_run_directory(&mut self, directory: &str) {
        if let Some(store) = self.content.clone() {
            self.first_run.select_dat_directory(directory, &store);
        }
    }

    /// The setup form's Validate.
    pub fn validate_first_run(&mut self) {
        if let Some(store) = self.content.clone() {
            self.first_run.validate(&store);
        }
    }

    /// The setup form's Save: remembers the directory and enables launching.
    pub fn start_first_run(&mut self) {
        if !self.is_lifecycle_idle() {
            return;
        }
        let Some(store) = self.content.clone() else {
            return;
        };
        if let Some(record) = self.first_run.start(&store) {
            self.on_install_completed(record);
        }
    }

    fn on_install_completed(&mut self, record: LauncherInstallRecord) {
        let status = format!(
            "Retail DAT files found in {}.",
            record.dat_directory.display()
        );
        if let Err(error) = self
            .orchestrator
            .set_installation_state(Some(record), &status)
        {
            self.last_error = Some(error_text(&error));
            return;
        }
        self.operation_status = "Client content configured.".into();
        self.last_error = None;
        self.refresh_from_core();
    }

    /// Verify files: checks the DAT directory again.
    pub fn verify_content(&mut self) {
        if !self.can_interact() || self.has_active_sessions() {
            return;
        }
        let Some(store) = self.content.clone() else {
            return;
        };
        let cancel = CancelToken::new();
        self.operation = Some(cancel);
        self.is_busy = true;
        self.last_error = None;
        self.operation_status = "Verifying client content…".into();
        self.run(move |sender, wake| {
            let verification = store.load_and_verify();
            let _ = sender.send(Message::ContentVerified {
                verification: Box::new(verification),
                startup: false,
            });
            wake();
        });
    }

    /// Check for updates: the client, the launcher and the installed
    /// plugins at once, opening what it found; the 20-minute wait restarts.
    pub fn check_for_updates(&mut self) {
        if !self.can_interact() || self.update_prompt.is_busy {
            return;
        }
        self.check_for_updates_at(true, Instant::now());
    }

    /// The update banner's Review update.
    pub fn review_update(&mut self) {
        if self.can_interact() && self.show_update_banner() {
            self.update_prompt.open_available_update();
        }
    }

    /// Whether the launcher should close (a staged launcher update's helper
    /// took over).
    pub fn is_shutdown_requested(&self) -> bool {
        self.shutdown_requested
    }

    /// The prompt's Update. A newer launcher is staged first and handed to
    /// its helper, which restarts the launcher (the game updates after the
    /// restart); otherwise the client is installed.
    pub fn install_update(&mut self) {
        if !self.update_prompt.is_open || self.update_prompt.is_busy || !self.is_lifecycle_idle() {
            return;
        }
        let (Some(updater), Some(check)) = (self.updater.clone(), self.update_prompt.check.clone())
        else {
            return;
        };
        let cancel = CancelToken::new();
        self.update_cancellation = Some(cancel.clone());
        self.update_prompt.is_busy = true;
        self.update_prompt.error = None;
        self.update_prompt.progress_percent = 0.0;
        self.update_prompt.is_progress_indeterminate = true;
        if check.is_launcher_update_available {
            self.update_prompt.status = "Downloading the new launcher…".into();
            let apply = self.apply_launcher_update.clone();
            self.run(move |sender, wake| {
                let progress_sender = sender.clone();
                let progress_wake = Arc::clone(&wake);
                let mut report = move |progress: LauncherUpdateProgress| {
                    let _ = progress_sender.send(Message::UpdateProgress(progress));
                    progress_wake();
                };
                let result = updater
                    .stage_launcher(&check, &mut report, &cancel)
                    .map_err(|error| error.to_string())
                    .and_then(|_| {
                        report(LauncherUpdateProgress {
                            phase: crate::updates::LauncherUpdatePhase::StagingLauncher,
                            status: "Restarting the launcher…".into(),
                            completed: 0,
                            total: 0,
                        });
                        apply.map_or(Ok(false), |apply| apply(&cancel))
                    });
                let _ = sender.send(Message::LauncherUpdateStaged(result));
                wake();
            });
            return;
        }
        self.update_prompt.status = "Downloading the game update…".into();
        self.run(move |sender, wake| {
            let progress_sender = sender.clone();
            let progress_wake = Arc::clone(&wake);
            let mut report = move |progress: LauncherUpdateProgress| {
                let _ = progress_sender.send(Message::UpdateProgress(progress));
                progress_wake();
            };
            let result = updater
                .install_client(&check, &mut report, &cancel)
                .map(|_| ())
                .map_err(|error| error.to_string());
            let _ = sender.send(Message::UpdateInstalled(result));
            wake();
        });
    }

    /// The prompt's Cancel.
    pub fn cancel_update(&mut self) {
        if let Some(cancel) = &self.update_cancellation {
            cancel.cancel();
        }
    }

    // --- Plugins tab ----------------------------------------------------------------------

    /// Switches to the Plugins tab, which starts Discover's release checks.
    pub fn select_plugins_tab(&mut self) {
        self.selected_tab = LauncherMainTab::Plugins;
        self.refresh_discover_details();
    }

    fn restore_show_beta_plugins_from_profile(&mut self) {
        let saved = self
            .orchestrator
            .snapshot()
            .map(|snapshot| snapshot.show_beta_plugins)
            .unwrap_or(false);
        self.plugins.show_beta_plugins = saved;
        for row in &mut self.plugins.installed {
            row.show_beta_plugins = saved;
        }
        for row in &mut self.plugins.discover {
            row.show_channel_picker = saved;
        }
    }

    /// Show beta plugins: saved, and Discover is checked again on the new
    /// channel (never a full Check).
    pub fn set_show_beta_plugins(&mut self, value: bool) {
        if self.plugins.show_beta_plugins == value {
            return;
        }
        self.plugins.show_beta_plugins = value;
        if let Err(error) = self.orchestrator.set_show_beta_plugins(value) {
            self.last_error = Some(error_text(&error));
        }
        for row in &mut self.plugins.installed {
            row.show_beta_plugins = value;
        }
        for row in &mut self.plugins.discover {
            self.plugins
                .discover_details_cache
                .remove(&row.entry.id.to_lowercase());
            row.show_channel_picker = value;
            row.clear_channel_options();
            // The new channel's own check decides visibility.
            row.is_visible = false;
        }
        self.refresh_discover_details();
    }

    /// Whether Refresh list may run.
    pub fn can_check_plugins(&self) -> bool {
        self.can_interact_with_plugins() && !self.plugins.is_busy && self.plugin_backend.is_some()
    }

    /// Refresh list (also run once at startup): the curated list, the
    /// installed plugins and their update checks.
    pub fn plugins_check_now(&mut self) {
        let Some(backend) = self.plugin_backend.clone() else {
            return;
        };
        if self.plugins.is_busy {
            return;
        }
        let cancel = CancelToken::new();
        self.plugin_cancellation = Some(cancel.clone());
        self.plugins.is_busy = true;
        self.plugins.error = None;
        self.plugins.status_text = None;
        self.plugins.is_rate_limited = false;
        if self.plugins.discover_details_requested {
            // A fresh release should replace what an earlier look found.
            self.plugins.discover_details_cache.clear();
        }
        let client = (self.client_version)();
        self.run(move |sender, wake| {
            let outcome = backend.check(client.as_ref(), &cancel);
            let _ = sender.send(Message::PluginCheckFinished(Box::new(outcome)));
            wake();
        });
    }

    fn apply_plugin_outcome(&mut self, outcome: PluginCheckOutcome) {
        self.plugin_cancellation = None;
        self.plugins.is_busy = false;
        // The list the next launched session filters blocked ids against.
        if let Err(error) = self
            .orchestrator
            .set_plugin_catalog(outcome.catalog.clone())
        {
            self.last_error = Some(error_text(&error));
        }
        self.plugins.is_rate_limited = outcome.rate_limited;
        self.plugins.list_age = outcome.list_age;
        if outcome.rate_limited {
            self.plugins.error = Some("GitHub is rate limiting; try later.".into());
        } else if outcome.catalog.is_none() {
            self.plugins.error = Some("Could not reach the plugin list.".into());
        }
        let mut installed = Vec::new();
        for info in outcome.installed.iter().cloned() {
            let key = info.id.to_lowercase();
            let update = outcome.updates_available.get(&key).cloned();
            let withheld = outcome.update_withheld_reasons.get(&key).cloned();
            installed.push(self.build_installed_row(info, update, withheld));
        }
        self.plugins.installed = installed;
        self.plugins.apply_discover(&outcome);
        if self.plugins.discover_details_requested {
            self.refresh_discover_details();
        }
    }

    fn build_installed_row(
        &mut self,
        info: InstalledPluginInfo,
        update: Option<super::plugins::PluginUpdateAvailability>,
        withheld: Option<String>,
    ) -> super::plugins_panel::PluginInstalledRow {
        let record_is_beta = self.plugin_backend.as_ref().is_some_and(|backend| {
            backend
                .find_record(&info.id)
                .is_some_and(|record| record.channel == PluginReleaseChannel::Beta)
        });
        let permissions = read_manifest(&info.directory)
            .map(|manifest| manifest.permissions)
            .unwrap_or_default();
        let icon = self.installed_icon(&info.id, &info.version, &info.directory);
        self.plugins
            .build_installed_row(info, update, withheld, record_is_beta, permissions, icon)
    }

    fn installed_icon(&mut self, id: &str, version: &str, directory: &Path) -> Option<PluginIcon> {
        let key = (id.to_lowercase(), version.to_owned());
        if let Some(cached) = self.plugins.installed_icon_cache.get(&key) {
            return cached.clone();
        }
        let icon =
            crate::fsutil::read_at_most(&directory.join(ICON_FILE_NAME), ICON_MAXIMUM_BYTES + 1)
                .ok()
                .and_then(|bytes| decode_icon(&bytes));
        self.plugins.installed_icon_cache.insert(key, icon.clone());
        icon
    }

    /// Discover's own request: one `plugin.json` per listed, not-installed
    /// plugin still missing its details. A row shows once its release is
    /// found usable on the current channel.
    pub fn refresh_discover_details(&mut self) {
        let Some(backend) = self.plugin_backend.clone() else {
            return;
        };
        self.plugins.discover_details_requested = true;
        let pending: Vec<(String, String)> = self
            .plugins
            .discover
            .iter()
            .filter(|row| {
                !row.is_visible
                    && !self
                        .plugins
                        .discover_details_cache
                        .contains_key(&row.entry.id.to_lowercase())
            })
            .map(|row| (row.entry.id.clone(), row.entry.repo.clone()))
            .collect();
        self.plugins.discover_pending_count = pending.len();
        self.plugins.update_discover_checking_state();
        if pending.is_empty() {
            return;
        }
        let channel = self.plugins.discover_channel();
        let show_beta = self.plugins.show_beta_plugins;
        let client = (self.client_version)();
        self.run(move |sender, wake| {
            let mut saw_rate_limited = false;
            let mut saw_transport_failure = false;
            let catalog = backend.current_catalog();
            let resolver = backend.release_resolver.pass();
            for (id, repo) in pending {
                let fetch = fetch_plugin_manifest(&backend, &resolver, &repo, &id, channel);
                match &fetch {
                    ManifestFetch::RateLimited => saw_rate_limited = true,
                    ManifestFetch::Failed => saw_transport_failure = true,
                    _ => {}
                }
                let details = match fetch {
                    ManifestFetch::Found(found) => {
                        let FoundManifest {
                            manifest,
                            tag,
                            candidates,
                        } = *found;
                        let remote = LauncherVersion::try_parse(&manifest.version);
                        // A version-specific block can be judged only now the version is known.
                        if catalog
                            .as_ref()
                            .is_some_and(|catalog| catalog.is_blocked(&id, remote.as_ref()))
                        {
                            None
                        } else {
                            Some(discover_details(
                                &backend,
                                &id,
                                &repo,
                                &manifest,
                                &tag,
                                &candidates,
                                show_beta,
                                client.as_ref(),
                            ))
                        }
                    }
                    _ => None,
                };
                let _ = sender.send(Message::DiscoverResolved { id, details });
                wake();
            }
            let _ = sender.send(Message::DiscoverPassFinished {
                saw_rate_limited,
                saw_transport_failure,
            });
            wake();
        });
    }

    fn apply_discover_details(&mut self, id: &str, details: Option<DiscoverDetails>) {
        self.plugins.discover_pending_count = self.plugins.discover_pending_count.saturating_sub(1);
        if let Some(details) = details {
            self.plugins
                .discover_details_cache
                .insert(id.to_lowercase(), details.clone());
            let show_beta = self.plugins.show_beta_plugins;
            if let Some(row) = self
                .plugins
                .discover
                .iter_mut()
                .find(|row| row.entry.id.eq_ignore_ascii_case(id))
            {
                row.icon = details.icon;
                row.show_channel_picker = show_beta;
                row.set_channel_options(details.stable, details.beta);
                row.is_visible = true;
            }
        }
        self.plugins.update_discover_checking_state();
    }

    /// The install dialog's enable choices: one per account, named
    /// "account (server)".
    fn character_options(&self) -> Vec<PluginCharacterOption> {
        self.snapshot
            .iter()
            .flat_map(|snapshot| snapshot.servers.iter())
            .flat_map(|server| server.accounts.iter())
            .map(|account| PluginCharacterOption {
                server_name: account.server_name.clone(),
                account_name: account.account_name.clone(),
                character_name: String::new(),
                display_name: format!("{} ({})", account.account_name, account.server_name),
            })
            .collect()
    }

    /// Whether plugin `id` of `repo` is on the list: the list names plugins,
    /// and a repository can publish several, listed or not.
    fn is_listed(&self, repo: &str, id: &str) -> bool {
        self.plugin_backend
            .as_ref()
            .and_then(|backend| backend.current_catalog())
            .is_some_and(|catalog| {
                catalog.entries_for_this_client().any(|entry| {
                    entry.repo.eq_ignore_ascii_case(repo) && entry.id.eq_ignore_ascii_case(id)
                })
            })
    }

    /// Discover's Install: pins to the row's picked channel.
    pub fn open_discover_install(&mut self, plugin_id: &str) {
        if !self.can_interact_with_plugins() || self.plugins.is_busy {
            return;
        }
        let Some(row) = self
            .plugins
            .discover
            .iter()
            .find(|row| row.entry.id.eq_ignore_ascii_case(plugin_id))
        else {
            return;
        };
        let Some(option) = row.selected_option().cloned() else {
            self.plugins.error = row.channel_unavailable_text().map(str::to_owned);
            return;
        };
        let request = InstallDialogRequest {
            repo: row.entry.repo.clone(),
            plugin_id: row.entry.id.clone(),
            display_name: row.entry.name.clone(),
            is_listed: self.is_listed(&row.entry.repo, &row.entry.id),
            is_update: false,
            offered_version: Some(option.version.clone()),
            pinned_tag: Some(option.tag.clone()),
            channel: Some(row.selected_channel),
            characters: self.character_options(),
            permissions: Some(option.permissions.clone()),
            installed_permissions: None,
            affected_characters: Vec::new(),
            local_zip: None,
        };
        self.open_install_dialog(request);
    }

    /// An installed plugin's Update.
    pub fn open_update_install(&mut self, plugin_id: &str) {
        if !self.can_interact_with_plugins() || self.plugins.is_busy {
            return;
        }
        let Some(row) = self
            .plugins
            .installed
            .iter()
            .find(|row| row.info.id.eq_ignore_ascii_case(plugin_id))
        else {
            return;
        };
        let (Some(update), Some(repo)) = (row.update.clone(), row.info.repo.clone()) else {
            return;
        };
        let affected = self.characters_with_plugin_enabled(&row.info.id);
        let request = InstallDialogRequest {
            is_listed: self.is_listed(&repo, &row.info.id),
            repo,
            plugin_id: row.info.id.clone(),
            display_name: row.info.display_name.clone(),
            is_update: true,
            offered_version: Some(update.version.clone()),
            pinned_tag: Some(update.tag.clone()),
            channel: None,
            characters: self.character_options(),
            permissions: Some(update.permissions.clone()),
            installed_permissions: Some(row.permissions.clone()),
            affected_characters: affected,
            local_zip: None,
        };
        self.open_install_dialog(request);
    }

    /// Install from ZIP may run.
    pub fn can_install_local_zip(&self) -> bool {
        self.plugin_backend.is_some() && !self.plugins.is_busy
    }

    /// Install from ZIP: checks the local archive on a worker, then shows
    /// the same permission and character dialog as a release. The install
    /// checks the archive again, its reviewed hash included.
    pub fn open_local_zip(&mut self, path: &Path) {
        let Some(backend) = self.plugin_backend.clone() else {
            return;
        };
        if self.plugins.is_busy || !self.can_interact_with_plugins() {
            return;
        }
        self.plugins.is_busy = true;
        self.plugins.error = None;
        self.plugins.status_text = None;
        let path = path.to_path_buf();
        self.run(move |sender, wake| {
            let result = backend
                .installer
                .inspect_local_zip(&path, &CancelToken::new())
                .map_err(|error| error.to_string());
            let _ = sender.send(Message::LocalZipInspected { path, result });
            wake();
        });
    }

    fn apply_local_zip(
        &mut self,
        path: &Path,
        result: Result<crate::plugins::LocalPluginZipPreview, String>,
    ) {
        self.plugins.is_busy = false;
        let preview = match result {
            Ok(preview) => preview,
            Err(error) => {
                self.plugins.error = Some(if error.trim().is_empty() {
                    "Could not read that plugin ZIP.".into()
                } else {
                    error
                });
                return;
            }
        };
        let manifest = &preview.manifest;
        let installed = self.plugin_backend.as_ref().and_then(|backend| {
            let catalog = backend.current_catalog();
            let client = (self.client_version)();
            backend.find_installed(&manifest.id, client.as_ref(), catalog.as_ref())
        });
        if let Some(installed) = &installed {
            if installed.source != crate::plugins::InstalledPluginSource::Direct
                || installed.refusal.is_some()
                || installed.has_duplicate
            {
                self.plugins.error = Some(format!(
                    "'{}' already has a conflicting installation.",
                    manifest.id
                ));
                return;
            }
            if let (Some(current), Some(offered)) = (
                LauncherVersion::try_parse(&installed.version),
                LauncherVersion::try_parse(&manifest.version),
            ) && offered <= current
            {
                self.plugins.error = Some(format!(
                    "'{}' {} is not newer than the installed {}.",
                    manifest.id, manifest.version, installed.version
                ));
                return;
            }
        }
        let file_name = path
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default();
        let installed_permissions = installed.as_ref().map(|info| {
            read_manifest(&info.directory).map_or_else(Vec::new, |found| found.permissions)
        });
        let request = InstallDialogRequest {
            repo: format!("Local ZIP: {file_name}"),
            plugin_id: manifest.id.clone(),
            display_name: manifest.display_name.clone(),
            is_listed: false,
            is_update: installed.is_some(),
            offered_version: Some(manifest.version.clone()),
            pinned_tag: None,
            channel: None,
            characters: self.character_options(),
            permissions: Some(manifest.permissions.clone()),
            installed_permissions,
            affected_characters: if installed.is_some() {
                self.characters_with_plugin_enabled(&manifest.id)
            } else {
                Vec::new()
            },
            local_zip: Some(super::install_dialog::LocalZipSource {
                path: path.to_path_buf(),
                sha256: preview.sha256.clone(),
            }),
        };
        self.open_install_dialog(request);
    }

    /// Who to name in the update dialog's keep-enabled choice: every account
    /// whose list has this id, and every character whose own list has it.
    fn characters_with_plugin_enabled(&self, plugin_id: &str) -> Vec<String> {
        let has = |plugins: &[String]| plugins.iter().any(|id| id.eq_ignore_ascii_case(plugin_id));
        let mut names = Vec::new();
        for account in self
            .snapshot
            .iter()
            .flat_map(|snapshot| snapshot.servers.iter())
            .flat_map(|server| server.accounts.iter())
        {
            if has(&account.plugins) {
                names.push(format!(
                    "{} ({})",
                    account.account_name, account.server_name
                ));
            }
            names.extend(
                account
                    .characters
                    .iter()
                    .filter(|character| character.has_own_plugins && has(&character.plugins))
                    .map(|character| {
                        format!(
                            "{} ({}@{})",
                            character.name, account.account_name, account.server_name
                        )
                    }),
            );
        }
        names
    }

    fn open_install_dialog(&mut self, request: InstallDialogRequest) {
        let needs_permissions = request.permissions.is_none();
        let repo = request.repo.clone();
        let id = request.plugin_id.clone();
        let channel = request.channel.unwrap_or(PluginReleaseChannel::Stable);
        let generation = self.plugins.install_dialog.open(request);
        if needs_permissions && let Some(backend) = self.plugin_backend.clone() {
            self.run(move |sender, wake| {
                let result = match fetch_plugin_manifest(
                    &backend,
                    &backend.release_resolver,
                    &repo,
                    &id,
                    channel,
                ) {
                    ManifestFetch::Found(found) => Ok(found.manifest.permissions),
                    ManifestFetch::RateLimited => Err("GitHub is rate limiting; try later.".into()),
                    ManifestFetch::Message(message) => Err(message),
                    ManifestFetch::Failed => {
                        Err("This plugin's details could not be checked.".into())
                    }
                };
                let _ = sender.send(Message::PermissionsLoaded { generation, result });
                wake();
            });
        }
    }

    /// The install dialog's Cancel.
    pub fn close_install_dialog(&mut self) {
        if let Some(cancel) = &self.install_cancellation {
            cancel.cancel();
        }
        self.plugins.install_dialog.close();
        self.ask_again_for_add_from_url();
    }

    /// The install dialog's Install / Update.
    pub fn confirm_install(&mut self) {
        let dialog = &self.plugins.install_dialog;
        if !dialog.can_confirm() {
            return;
        }
        let (Some(backend), Some(request)) = (self.plugin_backend.clone(), dialog.request.clone())
        else {
            return;
        };
        let displayed = dialog.displayed_permissions.clone();
        let cancel = CancelToken::new();
        self.install_cancellation = Some(cancel.clone());
        self.plugins.install_dialog.is_busy = true;
        self.plugins.install_dialog.error = None;
        let client = (self.client_version)();
        self.run(move |sender, wake| {
            let result = (|| {
                if let Some(local) = &request.local_zip {
                    let catalog = backend.current_catalog();
                    return backend
                        .installer
                        .install_local_zip(
                            &local.path,
                            &local.sha256,
                            catalog.as_ref(),
                            client.as_ref(),
                            &displayed,
                            &cancel,
                        )
                        .map_err(|error| {
                            if error.is_cancelled() {
                                "Install cancelled.".to_owned()
                            } else {
                                let text = error.to_string();
                                if text.trim().is_empty() {
                                    "The install failed.".to_owned()
                                } else {
                                    text
                                }
                            }
                        });
                }
                let tag = match request.pinned_tag.clone() {
                    Some(tag) => tag,
                    None => resolve_latest_tag(
                        &backend,
                        &request.repo,
                        &request.plugin_id,
                        request.channel.unwrap_or(PluginReleaseChannel::Stable),
                        &cancel,
                    )?,
                };
                let catalog = backend.current_catalog();
                backend
                    .installer
                    .install_or_update(
                        &request.repo,
                        &tag,
                        catalog.as_ref(),
                        client.as_ref(),
                        Some(&displayed),
                        request.channel,
                        &cancel,
                    )
                    .map_err(|error| {
                        if error.is_cancelled() {
                            "Install cancelled.".to_owned()
                        } else {
                            let text = error.to_string();
                            if text.trim().is_empty() {
                                "The install failed.".to_owned()
                            } else {
                                text
                            }
                        }
                    })
            })();
            let _ = sender.send(Message::InstallFinished(result));
            wake();
        });
    }

    fn apply_install_result(&mut self, result: Result<PluginInstallResult, String>) {
        self.install_cancellation = None;
        self.plugins.install_dialog.is_busy = false;
        match result {
            Ok(installed) => {
                // Closed before touching profiles, so a failing enable can never leave Confirm
                // there to run the install twice.
                let chosen = self.plugins.install_dialog.chosen_characters();
                let disable = self.plugins.install_dialog.disables_after_update();
                self.plugins.install_dialog.is_open = false;
                if !chosen.is_empty() {
                    self.enable_for_characters(&installed.id, &chosen);
                }
                if disable {
                    self.strip_from_every_character(&installed.id);
                }
                self.plugins_check_now();
                self.ask_again_for_add_from_url();
            }
            Err(error) => self.plugins.install_dialog.error = Some(error),
        }
    }

    /// The install dialog's only profile write: the plugin joins the plugin
    /// list of each account chosen there (every account, on a first
    /// install), which every character on it without a list of its own
    /// launches with, and the own list of each of its characters that has
    /// one. Turning it off again is the character's or account's own
    /// choice. Runs after the install succeeded, so trouble is reported on
    /// the panel.
    fn enable_for_characters(&mut self, plugin_id: &str, accounts: &[PluginCharacterOption]) {
        let snapshots: Vec<LauncherAccountSnapshot> = self
            .orchestrator
            .snapshot()
            .map(|snapshot| {
                snapshot
                    .servers
                    .into_iter()
                    .flat_map(|server| server.accounts)
                    .collect()
            })
            .unwrap_or_default();
        for option in accounts {
            let on_server: Vec<&LauncherAccountSnapshot> = snapshots
                .iter()
                .filter(|candidate| candidate.server_name == option.server_name)
                .collect();
            let Some(account) = find_account(
                &on_server,
                |candidate| &candidate.account_name,
                &option.account_name,
            )
            .map(|index| on_server[index]) else {
                continue;
            };
            let has =
                |plugins: &[String]| plugins.iter().any(|id| id.eq_ignore_ascii_case(plugin_id));
            let with = |plugins: &[String]| -> Vec<String> {
                let mut plugins = plugins.to_vec();
                plugins.push(plugin_id.to_owned());
                plugins
            };
            let mut result = Ok(());
            if !has(&account.plugins) {
                result = self.orchestrator.update_account_plugins(
                    &account.server_name,
                    &account.account_name,
                    &with(&account.plugins),
                );
            }
            for character in account
                .characters
                .iter()
                .filter(|character| character.has_own_plugins && !has(&character.plugins))
            {
                if result.is_err() {
                    break;
                }
                result = self.orchestrator.update_character_settings(
                    &character.server_name,
                    &character.account_name,
                    &character.name,
                    character.launch_mode,
                    Some(&with(&character.plugins)),
                );
            }
            if let Err(error) = result {
                let text = error_text(&error);
                self.plugins.error = Some(if text.trim().is_empty() {
                    "The plugin installed, but could not be turned on for every account and character."
                        .into()
                } else {
                    text
                });
                return;
            }
        }
    }

    /// Every account list and every character's own list still holding
    /// `plugin_id` loses it, so a reinstall never inherits an old enable.
    fn strip_from_every_character(&mut self, plugin_id: &str) {
        let accounts: Vec<LauncherAccountSnapshot> = self
            .orchestrator
            .snapshot()
            .map(|snapshot| {
                snapshot
                    .servers
                    .into_iter()
                    .flat_map(|server| server.accounts)
                    .collect()
            })
            .unwrap_or_default();
        let has = |plugins: &[String]| plugins.iter().any(|id| id.eq_ignore_ascii_case(plugin_id));
        let without = |plugins: &[String]| -> Vec<String> {
            plugins
                .iter()
                .filter(|id| !id.eq_ignore_ascii_case(plugin_id))
                .cloned()
                .collect()
        };
        let mut result = Ok(());
        'accounts: for account in &accounts {
            if has(&account.plugins) {
                result = self.orchestrator.update_account_plugins(
                    &account.server_name,
                    &account.account_name,
                    &without(&account.plugins),
                );
                if result.is_err() {
                    break;
                }
            }
            for character in account
                .characters
                .iter()
                .filter(|character| character.has_own_plugins && has(&character.plugins))
            {
                result = self.orchestrator.update_character_settings(
                    &character.server_name,
                    &character.account_name,
                    &character.name,
                    character.launch_mode,
                    Some(&without(&character.plugins)),
                );
                if result.is_err() {
                    break 'accounts;
                }
            }
        }
        if let Err(error) = result {
            let text = error_text(&error);
            self.plugins.error = Some(if text.trim().is_empty() {
                "The plugin was removed, but could not be unchecked everywhere.".into()
            } else {
                text
            });
        }
    }

    /// An installed plugin's Remove.
    pub fn open_remove_dialog(&mut self, plugin_id: &str) {
        if !self.can_interact_with_plugins() || self.plugins.is_busy {
            return;
        }
        let Some(row) = self
            .plugins
            .installed
            .iter()
            .find(|row| row.info.id.eq_ignore_ascii_case(plugin_id) && row.can_remove)
        else {
            return;
        };
        self.plugins.remove_target = Some(row.info.clone());
        self.plugins.remove_delete_storage = false;
        self.plugins.error = None;
        self.plugins.is_remove_dialog_open = true;
    }

    /// The remove dialog's Remove.
    pub fn confirm_remove(&mut self) {
        if !self.plugins.is_remove_dialog_open || self.plugins.is_busy {
            return;
        }
        let (Some(backend), Some(target)) = (
            self.plugin_backend.clone(),
            self.plugins.remove_target.clone(),
        ) else {
            return;
        };
        let result = if target.source == InstalledPluginSource::Direct {
            backend
                .installer
                .remove_direct(&target.directory, self.plugins.remove_delete_storage)
        } else {
            backend
                .installer
                .remove(&target.id, self.plugins.remove_delete_storage)
        };
        if let Err(error) = result {
            let text = error.to_string();
            self.plugins.error = Some(if text.trim().is_empty() {
                "The plugin could not be removed.".into()
            } else {
                text
            });
            return;
        }
        self.plugins.is_remove_dialog_open = false;
        self.plugins_check_now();
        self.strip_from_every_character(&target.id);
    }

    /// Whether Add from URL may run.
    pub fn can_add_from_url(&self) -> bool {
        self.can_interact_with_plugins()
            && !self.plugins.is_busy
            && self.plugin_backend.is_some()
            && !self.plugins.add_from_url_text.trim().is_empty()
    }

    /// Add from URL (`https://github.com/owner/name`). The repository is
    /// read before anything is said: it may publish several plugins, some
    /// installed and some not.
    pub fn add_from_url(&mut self) {
        let Some(backend) = self.plugin_backend.clone() else {
            return;
        };
        if self.plugins.is_busy {
            return;
        }
        let Some(repo) = github::try_parse_repo_url(&self.plugins.add_from_url_text) else {
            self.plugins.error = Some("Enter a URL like https://github.com/owner/name.".into());
            return;
        };
        self.plugins.is_busy = true;
        self.plugins.error = None;
        self.plugins.status_text = None;
        self.plugins.add_from_url_choices.clear();
        self.plugins.add_from_url_offered.clear();
        let channel = self.plugins.discover_channel();
        let pending_repo = repo.clone();
        self.plugins.add_from_url_text = repo;
        self.run(move |sender, wake| {
            let result = backend
                .release_resolver
                .resolve_repository(&pending_repo, channel, &CancelToken::new())
                .map_err(|error| error.to_string());
            let _ = sender.send(Message::AddFromUrlResolved(result));
            wake();
        });
    }

    fn apply_add_from_url(&mut self, result: Result<PluginRepositoryResolution, String>) {
        self.plugins.is_busy = false;
        let repo = self.plugins.add_from_url_text.clone();
        let Some(backend) = self.plugin_backend.clone() else {
            return;
        };
        let found = match result {
            Ok(found) => found,
            Err(error) => {
                self.plugins.error = Some(if error.trim().is_empty() {
                    "Could not add that plugin.".into()
                } else {
                    error
                });
                return;
            }
        };
        backend.log_index_warning_text(&repo, found.index_warning.as_deref());
        backend.log_plugins_not_searched(&repo, found.not_searched);
        if found.plugins.is_empty() {
            self.report_add_from_url_failure(found.failure);
            return;
        }
        self.plugins.add_from_url_text.clear();
        let (installed, fresh): (Vec<_>, Vec<_>) = found
            .plugins
            .into_iter()
            .partition(|plugin| backend.find_record(&plugin.manifest.id).is_some());
        // A blocked plugin is never one of the choices; it is said only when
        // nothing else could be added.
        let (mut blocked, mut fresh): (Vec<_>, Vec<_>) = fresh
            .into_iter()
            .partition(|plugin| block_reason(&backend, &plugin.manifest).is_some());
        match fresh.len() {
            0 if !blocked.is_empty() => self.offer_add_from_url(repo, blocked.remove(0)),
            0 => {
                let [only] = installed.as_slice() else {
                    self.plugins.status_text =
                        Some("Every plugin of that repository is already installed.".into());
                    return;
                };
                let manifest = &only.manifest;
                match backend.find_record(&manifest.id) {
                    Some(record) if !record.repo.eq_ignore_ascii_case(&repo) => {
                        self.plugins.error = Some(format!(
                            "'{}' is already installed from '{}'.",
                            manifest.id, record.repo
                        ));
                    }
                    _ => {
                        self.plugins.status_text =
                            Some(format!("{} is already installed.", manifest.display_name));
                    }
                }
            }
            1 => self.offer_add_from_url(repo, fresh.remove(0)),
            _ => {
                let mut choices: Vec<AddFromUrlChoice> = fresh
                    .into_iter()
                    .map(|resolution| AddFromUrlChoice {
                        repo: repo.clone(),
                        display_name: resolution.manifest.display_name.clone(),
                        version: resolution.manifest.version.clone(),
                        resolution: Box::new(resolution),
                    })
                    .collect();
                choices.sort_by_key(|choice| choice.display_name.to_lowercase());
                self.plugins.add_from_url_choices = choices;
            }
        }
    }

    /// Picks one plugin of a repository publishing several.
    pub fn choose_add_from_url(&mut self, index: usize) {
        let Some(choice) = self.plugins.add_from_url_choices.get(index).cloned() else {
            return;
        };
        self.plugins.add_from_url_offered = std::mem::take(&mut self.plugins.add_from_url_choices);
        self.offer_add_from_url(choice.repo, *choice.resolution);
        if !self.plugins.install_dialog.is_open {
            self.ask_again_for_add_from_url();
        }
    }

    /// Closes the pick of one plugin without offering any.
    pub fn cancel_add_from_url_choice(&mut self) {
        self.plugins.add_from_url_choices.clear();
        self.plugins.add_from_url_offered.clear();
    }

    /// Brings the pick back, once the install dialog it opened has closed (or
    /// the choice was refused), with the plugins it offered that are still
    /// not installed and not blocked.
    fn ask_again_for_add_from_url(&mut self) {
        let offered = std::mem::take(&mut self.plugins.add_from_url_offered);
        let Some(backend) = self.plugin_backend.clone() else {
            return;
        };
        self.plugins.add_from_url_choices = offered
            .into_iter()
            .filter(|choice| {
                let manifest = &choice.resolution.manifest;
                backend.find_record(&manifest.id).is_none()
                    && block_reason(&backend, manifest).is_none()
            })
            .collect();
    }

    /// Opens the install dialog for a plugin Add from URL found, unless it is
    /// blocked.
    fn offer_add_from_url(&mut self, repo: String, resolution: PluginReleaseResolution) {
        let Some(backend) = self.plugin_backend.clone() else {
            return;
        };
        let manifest = resolution.manifest;
        if let Some(reason) = block_reason(&backend, &manifest) {
            self.plugins.error = Some(format!("'{}' is blocked: {reason}", manifest.id));
            return;
        }
        let request = InstallDialogRequest {
            is_listed: self.is_listed(&repo, &manifest.id),
            repo,
            plugin_id: manifest.id.clone(),
            display_name: manifest.display_name.clone(),
            is_update: false,
            offered_version: Some(manifest.version.clone()),
            pinned_tag: Some(resolution.tag),
            channel: None,
            characters: self.character_options(),
            permissions: Some(manifest.permissions.clone()),
            installed_permissions: None,
            affected_characters: Vec::new(),
            local_zip: None,
        };
        self.open_install_dialog(request);
    }

    fn report_add_from_url_failure(&mut self, failure: PluginReleaseResolveResult) {
        self.plugins.error = Some(match failure {
            PluginReleaseResolveResult::RateLimited => "GitHub is rate limiting; try later.".into(),
            PluginReleaseResolveResult::Invalid(reason) => {
                if is_built_for_csharp(&crate::plugins::ManifestError(reason)) {
                    "That repository's release is built for OpenAC (C#), not this client.".into()
                } else {
                    "That repository's plugin.json could not be read.".into()
                }
            }
            PluginReleaseResolveResult::Prerelease(message) => message,
            PluginReleaseResolveResult::Success(_) | PluginReleaseResolveResult::Unavailable => {
                "No release for this client was found for that repository (its releases must \
                 be tagged wasm-v<version>, or wasm-<plugin id>-v<version>)."
                    .into()
            }
        });
    }

    /// A managed plugin's Beta updates toggle: sets the channel, then
    /// checks that one plugin again.
    pub fn toggle_plugin_beta(&mut self, plugin_id: &str, is_beta: bool) {
        let Some(backend) = self.plugin_backend.clone() else {
            return;
        };
        if self.plugins.is_busy || !self.can_interact_with_plugins() {
            return;
        }
        let channel = if is_beta {
            PluginReleaseChannel::Beta
        } else {
            PluginReleaseChannel::Stable
        };
        if let Err(error) = backend.installer.set_channel(plugin_id, channel) {
            let text = error.to_string();
            self.plugins.error = Some(if text.trim().is_empty() {
                "The plugin's channel could not be changed.".into()
            } else {
                text
            });
            return;
        }
        if let Some(row) = self
            .plugins
            .installed
            .iter_mut()
            .find(|row| row.info.id.eq_ignore_ascii_case(plugin_id))
        {
            row.is_beta_channel = is_beta;
        }
        self.plugins.is_busy = true;
        self.plugins.error = None;
        let id = plugin_id.to_owned();
        let client = (self.client_version)();
        self.run(move |sender, wake| {
            let result = Ok(backend.check_single(&id, client.as_ref(), &CancelToken::new()));
            let _ = sender.send(Message::BetaToggled {
                id,
                is_beta,
                result,
            });
            wake();
        });
    }

    fn replace_installed_row(&mut self, id: &str, check: PluginSingleCheckResult) {
        let Some(backend) = self.plugin_backend.clone() else {
            return;
        };
        let catalog = backend.current_catalog();
        let Some(info) =
            backend.find_installed(id, (self.client_version)().as_ref(), catalog.as_ref())
        else {
            return;
        };
        let updated = self.build_installed_row(info, check.available, check.withheld_reason);
        if let Some(row) = self
            .plugins
            .installed
            .iter_mut()
            .find(|row| row.info.id.eq_ignore_ascii_case(id))
        {
            *row = updated;
        }
    }

    // --- Lifetime -----------------------------------------------------------------------

    /// Stops background work. Sessions keep running until the orchestrator
    /// itself is disposed.
    pub fn dispose(&mut self) {
        if self.disposed {
            return;
        }
        self.disposed = true;
        self.automatic_relaunch.dispose();
        self.startup_cancellation.cancel();
        self.health_cancellation.cancel();
        for (_, cancel) in self.row_launches.drain(..) {
            cancel.cancel();
        }
        for token in [
            self.operation.take(),
            self.update_cancellation.take(),
            self.plugin_cancellation.take(),
            self.install_cancellation.take(),
        ]
        .into_iter()
        .flatten()
        {
            token.cancel();
        }
        self.text_editor.close();
    }
}

/// `1,234` (the player count, as OpenAC's `N0` writes it).
fn group_thousands(value: i32) -> String {
    let digits = value.unsigned_abs().to_string();
    let mut out = String::new();
    for (index, digit) in digits.chars().enumerate() {
        if index > 0 && (digits.len() - index).is_multiple_of(3) {
            out.push(',');
        }
        out.push(digit);
    }
    if value < 0 { format!("-{out}") } else { out }
}

struct FoundManifest {
    manifest: PluginManifest,
    tag: String,
    candidates: PluginReleaseCandidates,
}

enum ManifestFetch {
    Found(Box<FoundManifest>),
    RateLimited,
    Message(String),
    Failed,
}

/// Resolves plugin `id` of `repo` on `channel`, as the update check and
/// Discover do.
fn fetch_plugin_manifest(
    backend: &PluginComposition,
    resolver: &PluginReleaseResolver,
    repo: &str,
    id: &str,
    channel: PluginReleaseChannel,
) -> ManifestFetch {
    let Ok(candidates) = resolver.resolve_plugin_candidates(repo, id, channel, &CancelToken::new())
    else {
        return ManifestFetch::Failed;
    };
    backend.log_index_warning(repo, &candidates);
    match candidates.for_channel(channel).clone() {
        PluginReleaseResolveResult::Success(resolution) => {
            ManifestFetch::Found(Box::new(FoundManifest {
                manifest: resolution.manifest,
                tag: resolution.tag,
                candidates,
            }))
        }
        PluginReleaseResolveResult::RateLimited => ManifestFetch::RateLimited,
        PluginReleaseResolveResult::Prerelease(message) => ManifestFetch::Message(message),
        PluginReleaseResolveResult::Invalid(_) | PluginReleaseResolveResult::Unavailable => {
            ManifestFetch::Message("This plugin's details could not be checked.".into())
        }
    }
}

fn channel_option(
    result: &PluginReleaseResolveResult,
    client: Option<&ClientVersionResolution>,
) -> Option<DiscoverChannelOption> {
    let resolution = result.resolution()?;
    let compatibility = describe(&resolution.manifest, client);
    Some(DiscoverChannelOption {
        tag: resolution.tag.clone(),
        version: resolution.manifest.version.clone(),
        compatibility: compatibility.text,
        compatibility_is_warning: compatibility.is_warning,
        permissions: resolution.manifest.permissions.clone(),
    })
}

/// A fetched manifest's compatibility, permissions and icon: the stable
/// option always, the beta option only with Show beta plugins on.
#[allow(clippy::too_many_arguments)]
fn discover_details(
    backend: &PluginComposition,
    id: &str,
    repo: &str,
    manifest: &PluginManifest,
    tag: &str,
    candidates: &PluginReleaseCandidates,
    show_beta: bool,
    client: Option<&ClientVersionResolution>,
) -> DiscoverDetails {
    DiscoverDetails {
        stable: channel_option(&candidates.stable, client),
        beta: show_beta
            .then(|| channel_option(candidates.for_channel(PluginReleaseChannel::Beta), client))
            .flatten(),
        icon: discover_icon(backend, id, repo, manifest, tag),
    }
}

/// The Discover icon: only for a tag naming this exact version; the disk
/// cache (`<cache>/plugin-icons/<id>/v<version>.png`) first.
fn discover_icon(
    backend: &PluginComposition,
    id: &str,
    repo: &str,
    manifest: &PluginManifest,
    tag: &str,
) -> Option<PluginIcon> {
    if !manifest.matches_tag(tag)
        || LauncherVersion::try_parse(&manifest.version).is_none()
        || !is_well_formed_id(id)
    {
        return None;
    }
    let directory = backend
        .paths()
        .cache_directory
        .join("plugin-icons")
        .join(id);
    let path = directory.join(format!("v{}.png", manifest.version));
    if let Ok(bytes) = std::fs::read(&path) {
        return decode_icon(&bytes);
    }
    let url = github::tagged_asset(repo, tag, ICON_FILE_NAME)?;
    let Ok(PluginReleaseFetchResult::Success(document)) =
        backend
            .release_client
            .fetch_document(&url, ICON_MAXIMUM_BYTES as u64, &CancelToken::new())
    else {
        return None;
    };
    let icon = decode_icon(&document.content)?;
    // One file per id: an update's icon replaces the previous version's.
    if std::fs::create_dir_all(&directory).is_ok() {
        if let Ok(entries) = std::fs::read_dir(&directory) {
            for entry in entries.flatten() {
                let name = entry.file_name().to_string_lossy().into_owned();
                if name.starts_with('v') && name.ends_with(".png") {
                    let _ = std::fs::remove_file(entry.path());
                }
            }
        }
        let _ = crate::fsutil::write_via_tmp(&path, &document.content, false);
    }
    Some(icon)
}

/// Resolves the latest tag right before an install that had none pinned.
fn resolve_latest_tag(
    backend: &PluginComposition,
    repo: &str,
    id: &str,
    channel: PluginReleaseChannel,
    cancel: &CancelToken,
) -> Result<String, String> {
    match backend
        .release_resolver
        .resolve_plugin(repo, id, channel, cancel)
        .map_err(|error| error.to_string())?
    {
        PluginReleaseResolveResult::Success(resolution) => Ok(resolution.tag),
        PluginReleaseResolveResult::RateLimited => {
            Err("GitHub is rate limiting; try later.".into())
        }
        PluginReleaseResolveResult::Prerelease(message) => Err(message),
        PluginReleaseResolveResult::Invalid(_) => {
            Err("That repository's plugin.json could not be read.".into())
        }
        PluginReleaseResolveResult::Unavailable => Err("The plugin release is unavailable.".into()),
    }
}
