//! The launcher's start once the install is in the current folder layout:
//! the stores, the orchestrator and the window model. Until the layout is
//! ready (clients of the install still run, or a move failed) the window
//! shows only why it waits, and nothing reads the install's folders.

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use ac_host_core::PathSet;
use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::control;
use ac_launcher_core::installation::{InstallRootMover, RetailContentStore};
use ac_launcher_core::launcher_log::{self, LauncherLog};
use ac_launcher_core::layout_gate::{LayoutGate, LayoutGateState};
use ac_launcher_core::net::HttpTransport;
use ac_launcher_core::orchestration::{
    LauncherExecutableSet, LauncherOrchestrator, OrchestratorParts,
};
use ac_launcher_core::profiles::LauncherProfileStore;
use ac_launcher_core::status::{KnownServerCatalog, ServerHealthService};
use ac_launcher_core::updates::{
    ClientVersionResolution, ClientVersionState, ClientVersionStore, LauncherInstallationLayout,
    LauncherSelfUpdateManager, LauncherUpdater, LauncherVersion, ReleaseManifestClient,
    self_update_bootstrap, suffix_for_current_os,
};
use ac_launcher_core::window::classic_migration::ClassicMigrationDialog;
use ac_launcher_core::window::install_folder::InstallFolderParts;
use ac_launcher_core::window::plugins::PluginComposition;
use ac_launcher_core::window::startup::LauncherStartupOptions;
use ac_launcher_core::window::{
    ApplyLauncherUpdate, LauncherWindow, LauncherWindowParts, Orchestration, ThreadSpawner,
};
use ac_launcher_net::UdpReachabilityProbe;
use egui::{Align, Layout, RichText};

use crate::app::LauncherApp;
use crate::script;
use crate::shell::{CapturedFrame, LauncherUi, Waker};

/// What the window says if it has no updater (it always gets one here).
const NO_UPDATER: &str = "The update check is unavailable.";

/// What the start knows before the install's folders are read.
pub struct StartContext {
    /// The install's folders.
    pub paths: PathSet,
    /// The parsed command line.
    pub options: LauncherStartupOptions,
    /// This platform's runtime identifier.
    pub rid: String,
    /// The HTTP transport.
    pub transport: Arc<dyn HttpTransport>,
    /// This program.
    pub executable: PathBuf,
    /// This program's folder.
    pub launcher_directory: PathBuf,
    /// The launcher's own updates.
    pub self_updates: Arc<LauncherSelfUpdateManager>,
    /// Where the launcher is installed.
    pub layout: LauncherInstallationLayout,
    /// The launcher's log.
    pub log: LauncherLog,
}

/// Everything the window model is built from.
pub struct Prepared {
    shared: Arc<dyn Orchestration>,
    paths: PathSet,
    script: Option<script::Script>,
    updater: Option<Arc<LauncherUpdater>>,
    plugins: Arc<PluginComposition>,
    health: Arc<ServerHealthService>,
    known_servers: Arc<KnownServerCatalog>,
    launcher_version: LauncherVersion,
    client_version: Arc<dyn Fn() -> Option<ClientVersionResolution> + Send + Sync>,
    apply_launcher_update: Option<ApplyLauncherUpdate>,
    install_folder: Option<InstallFolderParts>,
    control: Option<control::ControlServer>,
}

/// The updater over the release feed: the client and the launcher's own
/// update. `has_running_sessions` is asked before every change.
fn build_updater(
    context: &StartContext,
    versions: &Arc<ClientVersionStore>,
    launcher_version: &LauncherVersion,
    has_running_sessions: impl Fn() -> bool + Send + Sync + 'static,
) -> Result<LauncherUpdater, String> {
    // The update check reads the production release feed unless the command
    // line names another (`--update-manifest-uri`), as OpenAC's launcher
    // does. The launcher's own update rides on the same feed.
    let manifest = ReleaseManifestClient::for_feed(
        Arc::clone(&context.transport),
        &context.options.update_manifest_uri,
    )
    .map_err(|error| error.to_string())?;
    Ok(LauncherUpdater::new(
        Arc::new(manifest),
        Arc::clone(&context.transport),
        Arc::clone(versions),
        launcher_version.clone(),
        &context.rid,
        has_running_sessions,
    )
    .map_err(|error| error.to_string())?
    .with_self_updates(Arc::clone(&context.self_updates), context.layout.clone())
    // A release build carries the fingerprint of what the launcher is built
    // from (OPENAC_LAUNCHER_FINGERPRINT at compile time); a developer build
    // has none and updates by version.
    .with_launcher_fingerprint(option_env!("OPENAC_LAUNCHER_FINGERPRINT")))
}

/// The launcher's own update, offered while the move of the files has
/// failed: a later release that fixes what stopped the move can be
/// installed without the rest of the launcher, which the move holds back.
struct RescueUpdate {
    status: Mutex<String>,
    busy: AtomicBool,
    /// The staged update's helper was started; this launcher closes for it.
    handed_over: AtomicBool,
}

impl RescueUpdate {
    fn new() -> Arc<Self> {
        Arc::new(Self {
            status: Mutex::new(String::new()),
            busy: AtomicBool::new(false),
            handed_over: AtomicBool::new(false),
        })
    }

    fn status(&self) -> String {
        self.status
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clone()
    }

    fn set_status(&self, text: impl Into<String>) {
        *self
            .status
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner) = text.into();
    }

    /// Checks the feed for a newer launcher and, when there is one, stages
    /// it and hands it to its helper, on a thread of its own.
    fn start(self: &Arc<Self>, context: &StartContext, gate: &LayoutGate, waker: Waker) {
        let gate = gate.clone();
        if self.busy.swap(true, Ordering::AcqRel) {
            return;
        }
        self.set_status("Checking for a launcher update…");
        let rescue = Arc::clone(self);
        let updater = LauncherVersion::try_parse(env!("CARGO_PKG_VERSION"))
            .ok_or_else(|| "The launcher's version is not SemVer 2.0.".to_owned())
            .and_then(|version| {
                let versions = Arc::new(ClientVersionStore::new(&context.paths));
                build_updater(context, &versions, &version, || false)
            });
        let manager = Arc::clone(&context.self_updates);
        let installed_root = context.layout.installed_root.clone();
        let executable = context.executable.clone();
        let arguments = context.options.public_arguments.clone();
        let log = context.log.clone();
        let spawned = std::thread::Builder::new()
            .name("launcher-rescue-update".into())
            .spawn(move || {
                let outcome = (|| -> Result<String, String> {
                    let updater = updater?;
                    let cancel = CancelToken::new();
                    let check = updater.check(&cancel).map_err(|e| e.to_string())?;
                    if !check.is_launcher_update_available {
                        return Ok("No newer launcher is available yet.".to_owned());
                    }
                    let progress_rescue = Arc::clone(&rescue);
                    updater
                        .stage_launcher(
                            &check,
                            &mut |progress| progress_rescue.set_status(progress.status),
                            &cancel,
                        )
                        .map_err(|e| e.to_string())?;
                    // The helper replaces this launcher as soon as it starts:
                    // no move may be running then, nor start after.
                    gate.stop();
                    while gate.is_running() {
                        std::thread::sleep(std::time::Duration::from_millis(100));
                    }
                    let applied = self_update_bootstrap::try_apply_staged_update_now(
                        &manager,
                        &installed_root,
                        &executable,
                        &arguments,
                    )
                    .map_err(|e| e.to_string())?;
                    if applied {
                        rescue.handed_over.store(true, Ordering::Release);
                        Ok("Installing the launcher update…".to_owned())
                    } else {
                        Ok("The launcher update is ready; start the launcher again to \
                            install it."
                            .to_owned())
                    }
                })();
                let text = outcome.unwrap_or_else(|error| {
                    format!("The launcher update could not be installed: {error}")
                });
                log.line(&format!("layout: {text}"));
                rescue.set_status(text);
                rescue.busy.store(false, Ordering::Release);
                waker();
            });
        if let Err(error) = spawned {
            self.set_status(format!("The update check could not start: {error}"));
            self.busy.store(false, Ordering::Release);
        }
    }
}

/// Builds the launcher's parts. Reads the install's folders, so it runs
/// only once the install is in the current layout.
///
/// # Errors
/// A part could not be made; the launcher cannot start.
pub fn prepare(context: &StartContext) -> Result<Prepared, String> {
    let paths = &context.paths;
    let log = &context.log;
    // A move of the install folder can leave the install record naming the
    // old folder's content; the install check that follows would reject it.
    match ac_launcher_core::installation::repair_content_records(paths) {
        Ok(repaired) => {
            for record in repaired {
                let line = format!(
                    "install folder: {} now names this folder's content",
                    record.display()
                );
                println!("{line}");
                log.line(&line);
            }
        }
        // The install check that follows reports the record as it is.
        Err(error) => {
            log.say(&format!(
                "install folder: the install record could not be updated: {error}"
            ));
        }
    }

    let script = script::Script::from_environment().transpose()?;
    let launcher_version = LauncherVersion::try_parse(env!("CARGO_PKG_VERSION"))
        .ok_or("The launcher's version is not SemVer 2.0.")?;

    let versions = Arc::new(ClientVersionStore::new(paths));
    let executables = LauncherExecutableSet::from_current_version_store_or_directory(
        Arc::clone(&versions),
        &context.launcher_directory,
    );
    let orchestrator = Arc::new(LauncherOrchestrator::with_parts(
        LauncherProfileStore::for_paths(paths),
        paths.clone(),
        executables,
        None,
        OrchestratorParts {
            installation_status: Some("Checking installed game content…".into()),
            update_session_barrier: Some(versions.barrier().clone()),
            ..OrchestratorParts::default()
        },
    ));

    // The update check reads the production release feed unless the command line names
    // another (`--update-manifest-uri`), as OpenAC's launcher does. The launcher's own
    // update rides on the same feed.
    let apply_launcher_update: Option<ApplyLauncherUpdate> = {
        let manager = Arc::clone(&context.self_updates);
        let installed_root = context.layout.installed_root.clone();
        let executable = context.executable.clone();
        let public_arguments = context.options.public_arguments.clone();
        Some(Arc::new(move |_: &CancelToken| {
            self_update_bootstrap::try_apply_staged_update_now(
                &manager,
                &installed_root,
                &executable,
                &public_arguments,
            )
            .map_err(|error| error.to_string())
        }) as ApplyLauncherUpdate)
    };
    let sessions = Arc::clone(&orchestrator);
    let updater = Some(Arc::new(build_updater(
        context,
        &versions,
        &launcher_version,
        move || {
            sessions
                .snapshot()
                .is_ok_and(|snapshot| snapshot.sessions.iter().any(|session| session.is_active()))
        },
    )?));

    let client_version = {
        let versions = Arc::clone(&versions);
        let beside = context
            .launcher_directory
            .join(format!("openac{}", suffix_for_current_os()));
        let directory = context.launcher_directory.clone();
        let version = launcher_version.clone();
        Arc::new(move || {
            let resolution = versions.cached_resolution();
            if resolution.is_verified() || !beside.is_file() {
                return Some(resolution);
            }
            // The client built with this launcher has the launcher's version.
            Some(ClientVersionResolution {
                state: ClientVersionState::Verified,
                status: "The client beside the launcher.".into(),
                version: Some(version.clone()),
                directory: Some(directory.clone()),
                previous_version: None,
                record: None,
            })
        })
    };
    let plugins = Arc::new(PluginComposition::new(
        paths,
        context.options.plugin_list_uri.clone(),
        Arc::clone(&context.transport),
        log.clone(),
    ));
    let health = Arc::new(ServerHealthService::new(
        Arc::clone(&context.transport),
        Arc::new(UdpReachabilityProbe::new()),
    ));
    let known_servers = Arc::new(KnownServerCatalog::new(
        Arc::clone(&context.transport),
        &paths.cache_directory,
    ));
    let shared: Arc<dyn Orchestration> = orchestrator;
    // What the sessions do goes to the launcher's log as it happens.
    launcher_log::follow_sessions(&shared, log.clone()).map_err(|error| error.to_string())?;
    // `--control-pipe`: the optional same-user endpoint test runs drive this
    // launcher through; it lives until the window closes.
    let control = match &context.options.control_pipe {
        Some(name) => Some(crate::start_control(
            name,
            &shared,
            &versions,
            &context.rid,
            log,
        )?),
        None => None,
    };
    let install_folder = PathSet::default_root(&ac_host_core::SystemPlatform)
        .ok()
        .map(|default_root| {
            let restart_arguments = context.options.public_arguments.clone();
            let restart_executable = context.executable.clone();
            let restart_log = log.clone();
            InstallFolderParts {
                paths: paths.clone(),
                mover: Arc::new(InstallRootMover::new(paths, &default_root, None)),
                // Every store this launcher opened names the old folder, so a
                // fresh launcher takes over once the install moved.
                restart_launcher: Arc::new(move || {
                    if let Err(error) = std::process::Command::new(&restart_executable)
                        .args(&restart_arguments)
                        .spawn()
                    {
                        restart_log.say(&format!(
                            "install folder: the launcher could not be started again: {error}"
                        ));
                    }
                }),
            }
        });
    Ok(Prepared {
        shared,
        paths: paths.clone(),
        script,
        updater,
        plugins,
        health,
        known_servers,
        launcher_version,
        client_version,
        apply_launcher_update,
        install_folder,
        control,
    })
}

/// The launcher running: its app and, when asked for, its control endpoint.
pub struct Running {
    app: LauncherApp,
    _control: Option<control::ControlServer>,
}

impl Prepared {
    /// The window model over these parts, initialized.
    fn open(self, waker: Waker) -> Running {
        let paths = self.paths;
        let mut window = LauncherWindow::new(LauncherWindowParts {
            orchestrator: Arc::clone(&self.shared),
            spawner: Arc::new(ThreadSpawner),
            wake: Arc::new(move || waker()),
            content: Some(Arc::new(RetailContentStore::new(&paths))),
            updater: self.updater,
            updater_unavailable_reason: NO_UPDATER.into(),
            plugins: Some(self.plugins),
            server_health: Some(self.health),
            launcher_version: Some(self.launcher_version.value().to_owned()),
            client_version: self.client_version,
            apply_launcher_update: self.apply_launcher_update,
            install_folder: self.install_folder,
            relaunch_settings: Some(
                ac_launcher_core::profiles::LauncherRelaunchSettingsStore::for_paths(&paths),
            ),
            checked_accounts: Some(
                ac_launcher_core::profiles::LauncherCheckedAccountStore::for_paths(&paths),
            ),
            relaunch_clock: None,
            known_servers: Some(self.known_servers),
            classic_migration: Some(ClassicMigrationDialog::new(
                paths.vtank_profiles_directory(),
            )),
        });
        window.initialize();
        Running {
            app: LauncherApp::new(window, self.shared, self.script),
            _control: self.control,
        }
    }
}

/// Where the start stands.
enum Stage {
    /// Waiting for the layout (or showing why it failed).
    Gate {
        gate: LayoutGate,
        context: Box<StartContext>,
        /// The parts could not be made once the layout was ready.
        failure: Option<String>,
        /// The launcher's own update, offered while the move has failed.
        rescue: Arc<RescueUpdate>,
    },
    /// The launcher runs.
    Running(Box<Running>),
}

/// The window's UI: the waiting screen until the layout is ready, then the
/// launcher.
pub struct StartupApp {
    stage: Stage,
    waker: Waker,
    exit: bool,
    /// The player closed the window while a move ran: it closes once the
    /// move is done.
    closing: bool,
}

impl StartupApp {
    /// The app for a start whose layout gate is `gate`; `prepared` when the
    /// layout was ready before the window opened.
    pub fn new(
        gate: LayoutGate,
        context: StartContext,
        prepared: Option<Prepared>,
        waker: Waker,
    ) -> Self {
        let stage = match prepared {
            Some(prepared) => Stage::Running(Box::new(prepared.open(Arc::clone(&waker)))),
            None => Stage::Gate {
                gate,
                context: Box::new(context),
                failure: None,
                rescue: RescueUpdate::new(),
            },
        };
        Self {
            stage,
            waker,
            exit: false,
            closing: false,
        }
    }

    fn running(&mut self) -> Option<&mut LauncherApp> {
        match &mut self.stage {
            Stage::Running(running) => Some(&mut running.app),
            Stage::Gate { .. } => None,
        }
    }

    /// Moves on to the launcher once the layout is ready.
    fn advance(&mut self) {
        // A launcher closing after its move does not start up first.
        if self.closing {
            return;
        }
        let Stage::Gate {
            gate,
            context,
            failure,
            ..
        } = &mut self.stage
        else {
            return;
        };
        if failure.is_some() || gate.state() != LayoutGateState::Ready {
            return;
        }
        match prepare(context) {
            Ok(prepared) => {
                self.stage = Stage::Running(Box::new(prepared.open(Arc::clone(&self.waker))));
            }
            Err(error) => {
                let message = format!("Launcher startup failed safely: {error}");
                context.log.say(&message);
                *failure = Some(message);
            }
        }
    }
}

impl StartupApp {
    /// Closes the waiting window, unless a move is running: then it closes
    /// once the move is done, so the files are never left half moved.
    /// True when it may close now.
    fn close_gate(&mut self) -> bool {
        let Stage::Gate { gate, .. } = &self.stage else {
            return true;
        };
        if gate.stop() {
            self.closing = true;
            false
        } else {
            true
        }
    }
}

impl LauncherUi for StartupApp {
    fn update(&mut self, root: &mut egui::Ui, window_active: bool) {
        self.advance();
        if let Some(app) = self.running() {
            app.update(root, window_active);
            return;
        }
        let Stage::Gate {
            gate,
            context,
            failure,
            rescue,
        } = &self.stage
        else {
            return;
        };
        let ctx = root.ctx().clone();
        crate::theme::apply(&ctx);
        let state = gate.state();
        let moving = gate.is_running() || state == LayoutGateState::Moving;
        if self.closing && !gate.is_running() {
            self.exit = true;
        }
        let message = if self.closing {
            "Finishing the move…".to_owned()
        } else {
            failure
                .clone()
                .or_else(|| state.message())
                .unwrap_or_default()
        };
        let move_failed = matches!(state, LayoutGateState::Failed(_));
        let stopped = !self.closing && (failure.is_some() || move_failed);
        let rescue_busy = rescue.busy.load(Ordering::Acquire);
        // The update's helper starts only once no move runs (see
        // `RescueUpdate::start`), so the launcher can close for it.
        let handed_over = rescue.handed_over.load(Ordering::Acquire);
        let mut close = false;
        root.vertical_centered(|ui| {
            ui.add_space(ui.available_height() / 3.0);
            ui.label(RichText::new("OpenAC").heading().strong());
            ui.add_space(12.0);
            ui.label(&message);
            if !stopped {
                ui.add_space(8.0);
                ui.spinner();
            }
            let rescue_status = rescue.status();
            if !rescue_status.is_empty() {
                ui.add_space(8.0);
                ui.label(&rescue_status);
            }
            ui.add_space(16.0);
            ui.with_layout(Layout::top_down(Align::Center), |ui| {
                if move_failed && failure.is_none() && !self.closing {
                    let try_again = egui::Button::new("Try again");
                    if ui.add_enabled(!moving && !rescue_busy, try_again).clicked() {
                        gate.retry();
                    }
                    let check = egui::Button::new("Check for a launcher update");
                    if ui.add_enabled(!moving && !rescue_busy, check).clicked() {
                        rescue.start(context, gate, Arc::clone(&self.waker));
                    }
                }
                if ui
                    .add_enabled(!self.closing, egui::Button::new("Close"))
                    .clicked()
                {
                    close = true;
                }
            });
        });
        if close && self.close_gate() {
            self.exit = true;
        }
        self.exit |= handed_over;
        // The gate's thread tries again every second; look again soon.
        ctx.request_repaint_after(std::time::Duration::from_millis(250));
    }

    fn close_requested(&mut self) -> bool {
        match self.running() {
            Some(app) => app.close_requested(),
            None => self.close_gate(),
        }
    }

    fn on_exit(&mut self) {
        if let Some(app) = self.running() {
            app.on_exit();
        }
    }

    fn frame_captured(&mut self, image: CapturedFrame) {
        if let Some(app) = self.running() {
            app.frame_captured(image);
        }
    }

    fn wants_screenshot(&mut self) -> bool {
        self.running().is_some_and(LauncherApp::wants_screenshot)
    }

    fn wants_gpu_rebuild(&mut self) -> bool {
        self.running().is_some_and(LauncherApp::wants_gpu_rebuild)
    }

    fn wants_exit(&mut self) -> bool {
        self.exit || self.running().is_some_and(LauncherApp::wants_exit)
    }
}
