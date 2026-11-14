//! `openac run --config <file>` and `openac validate --config <file>`:
//! several sessions in one process, each on its own thread, with an optional
//! game window that can be moved from session to session.

use crate::stdio::{errln, outln};
use std::collections::BTreeMap;
use std::io::IsTerminal;
use std::process::ExitCode;
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::Duration;

use ac_host::console_kind::ConsoleLineKind;
use ac_host::{
    CharacterSelector as HostSelector, ConsoleStream, CredentialResolver, ExitCode as HostExit,
    RosterEntry, RosterReport, SessionMode, Startup, StatusWriter, SystemPlatform,
};
use ac_net::LossOptions;
use ac_plugin::PluginReport;
use ac_runtime::plugins::PluginEvent;
use ac_runtime::session::{
    self, CharacterSelector, HostEvent, NetDiagnostics, SessionCommand, SessionConfig, SessionEvent,
};

use crate::console::{self, ConsoleInput};
use crate::window_state::{WindowNote, WindowTracker};

/// How long a starting client waits for another process (the launcher, or
/// a client started at the same moment) to finish moving the install into
/// the current folder layout.
const LAYOUT_PATIENCE: Duration = Duration::from_secs(10);

/// Moves the install into the current folder layout if it is not there yet
/// (before anything reads its folders), then takes the install's session
/// lock shared for the caller to hold for its whole run; the reason to
/// refuse the start when the files could not be moved, or while an update
/// or a move holds the lock exclusively.
pub fn hold_install(paths: &ac_host::PathSet) -> Result<ac_host::InstallSessionLease, String> {
    let migration = ac_host::layout::migrate_waiting(paths, LAYOUT_PATIENCE)?;
    for note in &migration.notes {
        errln!("layout: {note}");
    }
    match ac_host::InstallSessionLease::try_acquire_shared(paths) {
        Ok(Some(lease)) => Ok(lease),
        Ok(None) => Err(ac_host::InstallSessionLease::busy_message(paths)),
        Err(error) => Err(format!(
            "The install's session lock could not be opened: {error}"
        )),
    }
}

/// Run a `run`/`validate`/`--help` command line through the host
/// configuration.
pub fn run(
    arguments: &[String],
    crash: &crate::crash::CrashReporter,
    facts: &ac_render::facts::SharedFacts,
) -> ExitCode {
    let platform = SystemPlatform;
    let mut plan = match ac_host::start(arguments, &platform, std::io::stdin().is_terminal()) {
        Ok(Startup::Help) => {
            outln!("{}", ac_host::HELP);
            return exit(HostExit::Success);
        }
        Ok(Startup::Validated { summary, .. }) => {
            outln!("{summary}");
            return exit(HostExit::Success);
        }
        Ok(Startup::Run(plan)) => plan,
        Err(error) => {
            errln!("{}", error.report_line());
            return exit(error.exit_code());
        }
    };
    crash.set_directory(Some(plan.paths.crash_reports_directory()));
    // Held for the whole run, so a client update or a move of the install
    // folder waits for this process, however it was started.
    let _install_session = match hold_install(&plan.paths) {
        Ok(lease) => lease,
        Err(message) => {
            errln!("{message}");
            return exit(HostExit::Configuration);
        }
    };
    // A launcher session's folder (none for a document written by hand),
    // held for the whole run so the launcher's cleanup leaves it alone.
    let session_folder =
        ac_host::layout::LauncherSessionFolder::claim(&plan.paths, &plan.config_path);

    let (logins, mut relays) = {
        let stdin = std::io::stdin();
        let mut lock = stdin.lock();
        let mut resolver =
            CredentialResolver::new(&mut lock, &plan.paths.config_directory, &platform);
        let logins = match ac_host::resolve_session_logins(
            &plan.config,
            plan.direct_credentials.take(),
            &mut resolver,
        ) {
            Ok(logins) => logins,
            Err(error) => {
                errln!("openac: {error}");
                return exit(HostExit::Credential);
            }
        };
        // The relays of the sessions that offer themselves to one.
        let relays = crate::remote::resolve(&plan.config.sessions, &mut resolver);
        (logins, relays)
    };

    let prefs = crate::graphics::DisplayPrefs::load(Some(&plan.paths));
    let headless = std::env::var("OPENAC_HEADLESS").is_ok_and(|v| v == "1");
    let window = if headless {
        None
    } else {
        let graphics = match crate::graphics::GraphicsEnv::parse(|name| std::env::var(name).ok()) {
            Ok(graphics) => graphics,
            Err(error) => {
                errln!("openac: {error}");
                return exit(HostExit::Usage);
            }
        };
        // The window reads the dats the configuration names, else
        // OPENAC_DAT_DIR, else the usual install place.
        let dat_dir = plan
            .config
            .process
            .content
            .as_ref()
            .map(|c| c.dat_directory.clone());
        let mut host_options = graphics.host_options(
            dat_dir,
            None,
            Some(plan.paths.diagnostics_directory()),
            &prefs.settings,
        );
        host_options.graphics.facts = facts.clone();
        prefs.apply_to(&mut host_options);
        match ac_render::WindowHost::new(host_options) {
            Ok(parts) => Some(parts),
            Err(error) => {
                errln!("openac: no window support here ({error}); running headless");
                None
            }
        }
    };

    let mut sessions = BTreeMap::new();
    let mut monitors = Vec::new();
    let host_handle = window.as_ref().map(|(_, handle, _)| handle.clone());
    let exit_codes: Arc<Mutex<Vec<HostExit>>> = Arc::default();
    // With the console on, chat prints on its stream; with several
    // sessions every line says which session it came from.
    let chat_stream = plan.console.then_some(plan.console_stream);
    let several = logins.len() > 1;
    let mut answers = ConsoleAnswers::requested();
    // Which session the window is on, told to each session's status file.
    let (window_tracker, mut window_notes) =
        WindowTracker::new(logins.iter().map(|login| login.session.id.as_str()));
    let tables = console::game_tables(
        plan.config
            .process
            .content
            .as_ref()
            .map(|c| c.dat_directory.as_path()),
    );
    let content = console::world_content(
        plan.config
            .process
            .content
            .as_ref()
            .map(|c| c.dat_directory.as_path()),
    );
    // One plugin engine for the process; each session runs its own plugins.
    let plugin_engine = crate::plugins::engine(Some(&plan.paths));
    for login in logins {
        let descriptor = login.session;
        let (character, enter_world, policy_id) = match &descriptor.mode {
            SessionMode::Play { character, policy } => (
                selector(character.as_ref()),
                true,
                policy.as_ref().map(|p| p.id.clone()),
            ),
            SessionMode::Probe => (CharacterSelector::FirstAvailable, false, None),
        };
        // The bot policy driving the session, if the document names one.
        let policy = match policy_id
            .as_deref()
            .map(|id| (id, session::policy::create(id)))
        {
            None => None,
            Some((_, Some(policy))) => Some(policy),
            Some((id, None)) => {
                errln!(
                    "openac: session {}: unknown bot policy '{id}'",
                    descriptor.id
                );
                return exit(HostExit::Configuration);
            }
        };
        let server = match resolve_endpoint(&descriptor.endpoint.host, descriptor.endpoint.port) {
            Some(server) => server,
            None => {
                errln!(
                    "openac: session {}: cannot resolve {}:{}",
                    descriptor.id,
                    descriptor.endpoint.host,
                    descriptor.endpoint.port
                );
                return exit(HostExit::Configuration);
            }
        };
        let plugin_settings =
            match crate::plugins::settings(descriptor.plugin_settings.as_ref(), |name| {
                std::env::var(name).ok()
            }) {
                Ok(settings) => settings,
                Err(error) => {
                    errln!("openac: session {}: {error}", descriptor.id);
                    return exit(HostExit::Configuration);
                }
            };
        let config = SessionConfig {
            server,
            account: descriptor.account.clone(),
            password: login.password.reveal().to_owned(),
            character,
            loss: LossOptions::default(),
            enter_world,
            await_character_selection: false,
            data_directory: Some(plan.paths.data_directory.clone()),
            tables: tables.clone(),
            content: content.clone(),
            diagnostics: NetDiagnostics::from_env(),
            login: login_automation(&descriptor),
            plugins: Some(crate::plugins::setup(
                &plugin_engine,
                Some(&plan.paths),
                descriptor.plugins.clone(),
                plugin_settings,
                crate::plugins::peer_tags(descriptor.plugin_tags.as_deref(), |name| {
                    std::env::var(name).ok()
                }),
            )),
        };
        let handle = match session::spawn(config) {
            Ok(handle) => handle,
            Err(error) => {
                errln!("openac: session {}: {error}", descriptor.id);
                return exit(HostExit::Runtime);
            }
        };
        sessions.insert(descriptor.id.clone(), handle.commands());
        // A probe has nothing to show.
        let relay = relays.remove(&descriptor.id);
        let remote = enter_world.then(|| {
            crate::remote::RemoteControl::new(
                &descriptor,
                &plan.config_path,
                &plan.paths.config_directory,
                relay,
                handle.commands(),
            )
        });
        let status = StatusWriter::for_file(descriptor.status_file.as_deref());
        let codes = Arc::clone(&exit_codes);
        let id = descriptor.id.clone();
        let printer = ChatPrinter {
            stream: chat_stream,
            session: several.then(|| id.clone()),
            plugin_logs: session_folder
                .as_ref()
                .map(ac_host::layout::LauncherSessionFolder::plugin_logs_directory),
            marks: answers.marks,
            answers: answers.session(&id),
        };
        let notes = window_notes.remove(&id);
        let liveness = Liveness::requested(host_handle.clone());
        monitors.push(
            std::thread::Builder::new()
                .name(format!("monitor-{id}"))
                .spawn(move || {
                    let beside = Beside {
                        window: notes,
                        liveness,
                        remote,
                    };
                    let code = monitor(&id, handle, status, &printer, policy, beside);
                    if let Ok(mut codes) = codes.lock() {
                        codes.push(code);
                    }
                }),
        );
    }
    let monitors: Vec<JoinHandle<()>> = monitors.into_iter().filter_map(Result::ok).collect();

    let all: Vec<Sender<SessionCommand>> = sessions.values().cloned().collect();
    let shutdown_all = all.clone();
    // The console stop (Ctrl+C and Ctrl+Break; SIGINT, SIGTERM and SIGHUP on
    // Unix) logs every session off before the process exits.
    if let Err(error) = ctrlc::set_handler(move || {
        for s in &shutdown_all {
            let _ = s.send(SessionCommand::Shutdown);
        }
    }) {
        errln!("openac: Ctrl+C will not log off gracefully: {error}");
    }
    if plan.console {
        spawn_console(
            sessions.clone(),
            host_handle.clone(),
            prefs,
            window_tracker.clone(),
            answers,
        );
    }

    match window {
        Some((host, handle, events)) => {
            std::thread::Builder::new()
                .name("window-events".into())
                .spawn(move || {
                    for event in events {
                        crate::graphics::print_event(&event);
                        // A window the player closed leaves its session playing.
                        if let Some(id) = window_tracker.follow(&event)
                            && let Some(session) = sessions.get(&id)
                        {
                            let _ = session.send(SessionCommand::Detach);
                        }
                    }
                })
                .ok();
            // When every session has ended, stop the window host.
            let codes = Arc::clone(&exit_codes);
            let sessions_done = std::thread::Builder::new()
                .name("sessions-done".into())
                .spawn(move || {
                    wait_for_monitors(monitors, &codes);
                    let _ = handle.shutdown();
                });
            if let Err(error) = host.run() {
                errln!("openac: window host stopped: {error}");
            }
            // The window host can stop before the sessions (it failed): the
            // process still ends only after every session has ended.
            match sessions_done {
                Ok(done) => {
                    let _ = done.join();
                }
                Err(error) => errln!("openac: could not wait for the sessions: {error}"),
            }
        }
        None => wait_for_monitors(monitors, &exit_codes),
    }
    let codes = exit_codes.lock().map(|c| c.clone()).unwrap_or_default();
    exit(
        codes
            .into_iter()
            .find(|c| *c != HostExit::Success)
            .unwrap_or(HostExit::Success),
    )
}

fn exit(code: HostExit) -> ExitCode {
    ExitCode::from(code.code())
}

/// Waits for every session's monitor. A monitor that panicked has still
/// logged its session off (dropping the session's handle does that) and
/// counts as a runtime failure.
fn wait_for_monitors(monitors: Vec<JoinHandle<()>>, codes: &Mutex<Vec<HostExit>>) {
    for monitor in monitors {
        if monitor.join().is_err()
            && let Ok(mut codes) = codes.lock()
        {
            codes.push(HostExit::Runtime);
        }
    }
}

/// The document's declared character options and login commands, as the
/// runtime takes them.
pub(crate) fn login_automation(
    descriptor: &ac_host::config::SessionDescriptor,
) -> ac_runtime::session::LoginAutomation {
    ac_runtime::session::LoginAutomation {
        declared_options: descriptor
            .character_options
            .iter()
            .flatten()
            .filter_map(|(option, value)| {
                ac_runtime::character::option_by_name(option.name()).map(|e| (e.id, *value))
            })
            .collect(),
        commands: descriptor.login_commands.clone().unwrap_or_default(),
        command_delay: Duration::from_millis(u64::from(descriptor.login_command_delay_ms)),
    }
}

pub(crate) fn selector(character: Option<&HostSelector>) -> CharacterSelector {
    match character {
        None => CharacterSelector::FirstAvailable,
        Some(HostSelector::Index(i)) => CharacterSelector::Index(*i as usize),
        Some(HostSelector::Id(id)) => CharacterSelector::Id(*id),
        Some(HostSelector::Name(name)) => CharacterSelector::Name(name.clone()),
    }
}

pub(crate) fn resolve_endpoint(host: &str, port: u16) -> Option<std::net::SocketAddr> {
    use std::net::ToSocketAddrs;
    (host, port).to_socket_addrs().ok()?.next()
}

/// Where one session's chat is printed: the console stream when the
/// console is on, nowhere otherwise.
#[derive(Debug)]
pub(crate) struct ChatPrinter {
    pub(crate) stream: Option<ConsoleStream>,
    /// The session id put in front of each line when several run.
    pub(crate) session: Option<String>,
    /// The launcher session folder's plugin log folder: every line a plugin
    /// logs is appended to `<plugin id>.log` there, and its debug lines go
    /// only there. `None` for a client started by hand.
    pub(crate) plugin_logs: Option<std::path::PathBuf>,
    /// The launcher asked for every line a player would see to carry the
    /// mark of its kind (see `ac_host::console_kind`).
    pub(crate) marks: bool,
    /// When a command typed on the session's console waits for answers.
    pub(crate) answers: Arc<console::AnswerWindow>,
}

impl ChatPrinter {
    /// A line of the client's log.
    fn print(&self, line: &str) {
        self.print_as(None, line);
    }

    /// A line of `kind` (`None`: the client's log).
    fn print_as(&self, kind: Option<ConsoleLineKind>, line: &str) {
        // The console's own lines are dimmed on a terminal, so they never
        // read as chat.
        let dimmed;
        let line = if line.starts_with(console::NOTICE_PREFIX) && Self::color() {
            dimmed = format!("{}{line}{}", console::ANSI_DIM, console::ANSI_RESET);
            dimmed.as_str()
        } else {
            line
        };
        let line = self.mark(kind, line);
        match self.stream {
            Some(ConsoleStream::Stdout) => outln!("{line}"),
            Some(ConsoleStream::Stderr) => errln!("{line}"),
            None => {}
        }
    }

    /// Whether a line for the client log (standard error, which the
    /// launcher keeps in `client.log` with standard output) must be written
    /// there too: not when this printer already writes to it, nor when a
    /// person reads the console on the terminal standard error also shows.
    fn client_log_unseen(&self) -> bool {
        match self.stream {
            Some(ConsoleStream::Stderr) => false,
            Some(ConsoleStream::Stdout) => !std::io::stderr().is_terminal(),
            None => true,
        }
    }

    /// A line the client log must keep (the session's lifecycle and portal
    /// space, which explain a character left stuck): printed as the console
    /// prints, and on standard error too when the console does not write
    /// there.
    fn print_logged(&self, kind: Option<ConsoleLineKind>, line: &str) {
        self.print_as(kind, line);
        if self.client_log_unseen() {
            errln!("{line}");
        }
    }

    /// A chat line, as the main chat window would show it.
    fn print_chat(&self, line: &ac_runtime::communication::ChatLine) {
        if let Some(text) = console::console_chat_line(line, self.session.as_deref(), Self::color())
        {
            self.print_as(Some(ConsoleLineKind::Chat), &text);
        }
    }

    /// `line` marked as `kind` when the launcher asked for marks.
    fn mark<'a>(&self, kind: Option<ConsoleLineKind>, line: &'a str) -> std::borrow::Cow<'a, str> {
        console::marked(kind, line, self.marks)
    }

    /// The kind of the lines for `event`, now.
    fn kind_of(&self, event: &HostEvent) -> Option<ConsoleLineKind> {
        console::event_kind(event, &self.answers, std::time::Instant::now())
    }

    /// Colour when standard output is a terminal (as OpenAC's console).
    fn color() -> bool {
        std::io::stdout().is_terminal()
    }
}

/// What a session's monitor keeps beside the session.
pub(crate) struct Beside {
    /// What the window host says of the session's window.
    pub(crate) window: Option<std::sync::mpsc::Receiver<WindowNote>>,
    /// What the session's heartbeat watches.
    pub(crate) liveness: Option<Liveness>,
    /// The session's remote window (none for a probe); its endpoint stops
    /// with the monitor.
    pub(crate) remote: Option<crate::remote::RemoteControl>,
}

/// Print one session's events with its id, keep its status file, run its
/// bot policy, and return its exit code when it ends.
pub(crate) fn monitor(
    id: &str,
    handle: session::SessionHandle,
    mut status: StatusWriter<ac_host::FileSink>,
    printer: &ChatPrinter,
    mut policy: Option<Box<dyn session::policy::Policy>>,
    beside: Beside,
) -> HostExit {
    let Beside {
        window,
        liveness,
        mut remote,
    } = beside;
    status.started(id);
    // A notice names its session only when several share the console, as
    // OpenAC's console does; with one session the id is noise.
    let tag = printer.session.as_deref();
    // The server re-sends the character list after a log off; a connection
    // is reported once, until it closes (a reconnect opens another).
    let mut reported_connection = false;
    let commands = handle.commands();
    let mut policy_done = false;
    let mut policy_failure: Option<String> = None;
    let started = std::time::Instant::now();
    let mut plugin_logs = printer
        .plugin_logs
        .clone()
        .map(crate::plugins::PluginLogFiles::new);
    // When the launcher asked for them, a heartbeat goes out every interval
    // in which the session made progress (and, while the window shows it,
    // the window's event loop too): a stuck loop stops them, and the
    // launcher shows the client as not responding.
    let mut heartbeat = HeartbeatGate::new(std::time::Instant::now(), handle.progress());
    let mut window_open = false;
    // A policy acts on its clock between events too; it is stepped at least
    // every 50 ms (OpenAC ticks it once per host frame).
    let poll = if policy.is_some() {
        Duration::from_millis(50)
    } else {
        Duration::from_millis(250)
    };
    let summary = loop {
        let event = handle.next_event(poll);
        for note in crate::window_state::drain(window.as_ref()) {
            window_open = match note {
                WindowNote::Opened => true,
                WindowNote::Closed(_) | WindowNote::Failed(_) => false,
            };
            note.report(id, &mut status);
        }
        if let Some(liveness) = &liveness {
            let probe = liveness.window.as_ref().filter(|_| window_open);
            let answered = probe.map(ac_render::HostHandle::answered_pings);
            if let Some(beat) =
                heartbeat.check(std::time::Instant::now(), handle.progress(), answered)
            {
                if let Some(host) = probe {
                    let _ = host.ping();
                }
                if beat {
                    status.heartbeat(id);
                }
            }
        }
        if let Some(policy) = policy.as_mut().filter(|_| !policy_done) {
            if let Some(event) = event.as_ref() {
                for command in policy.observe(event) {
                    let _ = commands.send(command);
                }
            }
            for command in policy.tick(started.elapsed().as_secs_f64()) {
                let _ = commands.send(command);
            }
            for line in policy.take_lines() {
                outln!("[{id}] {line}");
            }
            if let Some(failure) = policy.failure() {
                // A policy that found the session wrong ends it as a
                // failure (OpenAC: the policy throws and the session is
                // logged off with a runtime error).
                policy_done = true;
                errln!("[{id}] policy failed: {failure}");
                policy_failure = Some(format!("policy failed: {failure}"));
                let _ = commands.send(SessionCommand::Shutdown);
            } else if policy.is_complete() {
                // OpenAC: a completed policy ends its session gracefully.
                policy_done = true;
                outln!("[{id}] policy complete; logging off");
                let _ = commands.send(SessionCommand::Shutdown);
            }
        }
        // The session's relay follows what it is doing. The endpoint stops
        // with this monitor, when the session has ended.
        if let Some(remote) = &mut remote {
            if let Some(event) = &event {
                remote.observe(event);
            }
            remote.print_notes(id);
        }
        // What a player would see of the event, when the launcher marks lines.
        let kind = event.as_ref().and_then(|event| printer.kind_of(event));
        match event {
            Some(HostEvent::Lifecycle(lifecycle)) => {
                if lifecycle.state == session::LifecycleState::Stopped {
                    reported_connection = false;
                }
                for line in console::event_lines(&HostEvent::Lifecycle(lifecycle), tag) {
                    printer.print_logged(kind, &line);
                }
            }
            Some(
                event @ (HostEvent::Portal(_)
                | HostEvent::Logout(_)
                | HostEvent::CharacterSelection(_)
                | HostEvent::CharacterCreation(_)),
            ) => {
                for line in console::event_lines(&event, tag) {
                    printer.print_logged(kind, &line);
                }
            }
            Some(HostEvent::Session(event)) => {
                match &event {
                    SessionEvent::CharacterCreated { guid, name } => {
                        status.character_created(id, *guid, name);
                    }
                    SessionEvent::CreationFailed { code, reason, name } => {
                        status.creation_failed(id, *code, reason, name);
                    }
                    SessionEvent::CharacterList(list) if !reported_connection => {
                        reported_connection = true;
                        status.connected(id);
                        status.character_list(
                            id,
                            &RosterReport {
                                account_name: list.account_name.clone(),
                                slot_count: list.slot_count,
                                entries: list
                                    .characters
                                    .iter()
                                    .map(|c| RosterEntry {
                                        id: c.id,
                                        name: c.name.clone(),
                                        seconds_greyed_out: c.seconds_greyed_out,
                                    })
                                    .collect(),
                            },
                        );
                    }
                    SessionEvent::EnteredWorld(c) => status.entered_world(id, c.id, &c.name),
                    _ => {}
                }
                outln!("[{id}] {event:?}");
            }
            Some(HostEvent::Objects(objects)) => {
                outln!(
                    "{}",
                    printer.mark(kind, &format!("[{id}] {} objects known", objects.len()))
                );
                for o in objects.iter().take(40) {
                    let line = format!(
                        "[{id}]   0x{:08X} {:<32} {}",
                        o.guid,
                        o.name.as_deref().unwrap_or("?"),
                        o.distance.map_or_else(String::new, |d| format!("{d:.1} m"))
                    );
                    outln!("{}", printer.mark(kind, &line));
                }
            }
            Some(HostEvent::GameTime(date)) => {
                outln!(
                    "{}",
                    printer.mark(kind, &format!("[{id}] game time: {date:?}"))
                );
            }
            Some(HostEvent::LoginCommandFailed(failure)) => {
                // Reported, never fatal (OpenAC's loginCommandFailed).
                status.login_command_failed(
                    id,
                    i32::try_from(failure.command_index).unwrap_or(i32::MAX),
                    &failure.command,
                    &failure.error,
                );
                printer.print(&console::notice(
                    &format!(
                        "login command {} ({:?}) failed: {}",
                        failure.command_index, failure.command, failure.error
                    ),
                    tag,
                ));
            }
            Some(HostEvent::Plugin(event)) => {
                // pluginLoaded / pluginFailed, as OpenAC's status file.
                match &event {
                    PluginEvent::Report(PluginReport::Loaded { id: plugin }) => {
                        status.plugin_loaded(id, plugin);
                    }
                    PluginEvent::Report(PluginReport::Failed { id: plugin, error }) => {
                        status.plugin_failed(id, plugin, error);
                    }
                    PluginEvent::Report(PluginReport::SlowCall { .. })
                    | PluginEvent::Log { .. } => {}
                }
                if let (
                    Some(logs),
                    PluginEvent::Log {
                        plugin,
                        level,
                        message,
                    },
                ) = (plugin_logs.as_mut(), &event)
                {
                    logs.append(plugin, *level, message);
                }
                if !crate::plugins::only_in_plugin_log(&event, plugin_logs.is_some()) {
                    printer.print(&console::notice(
                        &crate::plugins::event_line(&event),
                        printer.session.as_deref(),
                    ));
                }
                if crate::plugins::for_client_log(&event) && printer.client_log_unseen() {
                    errln!(
                        "{}",
                        console::notice(&crate::plugins::event_line(&event), Some(id))
                    );
                }
            }
            Some(HostEvent::Chat(line)) => {
                printer.print_chat(&line);
            }
            Some(HostEvent::InterfaceText(text)) => {
                printer.print_as(
                    kind,
                    &console::interface_text(&text, printer.session.as_deref()),
                );
            }
            Some(HostEvent::Contracts(contracts)) => {
                for line in console::contract_lines(&contracts, tag) {
                    printer.print_as(kind, &line);
                }
            }
            Some(HostEvent::Journal {
                pages,
                current_page,
            }) => {
                for line in console::journal_lines(&pages, current_page, tag) {
                    printer.print_as(kind, &line);
                }
            }
            Some(HostEvent::Relogin(relogin)) => {
                // The lost connection is closed as any other end (OpenAC
                // records it as `connection-lost`); the next login opens
                // another.
                if matches!(relogin, session::Relogin::Scheduled { .. }) && reported_connection {
                    status.disconnected(id, "connection-lost");
                    reported_connection = false;
                }
                for line in console::event_lines(&HostEvent::Relogin(relogin), tag) {
                    errln!("{line}");
                }
            }
            Some(HostEvent::Ended(summary)) => break summary,
            None if handle.is_finished() => {
                // The session thread stopped without ending: it faulted.
                // Quarantine it; the other sessions keep running.
                let fault = handle.join_quarantining() == session::SessionEnd::Faulted;
                let reason = if fault {
                    "quarantined: the session faulted"
                } else {
                    "the session stopped without a summary"
                };
                errln!("[{id}] {reason}");
                status.disconnected(id, reason);
                status.exited(id, i32::from(HostExit::Runtime.code()), reason);
                status.close();
                return HostExit::Runtime;
            }
            Some(other) => {
                for line in console::event_lines(&other, tag) {
                    printer.print_as(kind, &line);
                }
            }
            None => {}
        }
    };
    handle.join();
    let error = summary.error.clone().or(policy_failure);
    let code = end_code(error.as_deref());
    status.disconnected(id, error.as_deref().unwrap_or("logoff"));
    let refusal = summary
        .server_refusal
        .as_ref()
        .map(|refusal| ac_host::ServerRefusalNote {
            message: &refusal.message,
            account_in_use: refusal.still_logged_on,
        });
    status.exited_by(
        id,
        i32::from(code.code()),
        error.as_deref().unwrap_or("graceful"),
        refusal,
    );
    status.close();
    outln!(
        "[{id}] ended: {} ({} objects, parse failures {:?})",
        summary.error.as_deref().unwrap_or("cleanly"),
        summary.world_objects,
        summary.parse_failures
    );
    // The link's health, as the single-session form reports it; the
    // connection count shows whether the session ever reconnected.
    if let Some(t) = &summary.transport {
        outln!(
            "[{id}] link: connections {}, packets in {} out {}, resends {}, NAKs in {} out {}, \
             checksum failures {}, sanity drops {}, uncached NAK ids {}, disconnect sent {}",
            summary.connections,
            t.packets_received,
            t.packets_sent,
            t.resends_sent,
            t.nak_requests_received,
            t.naks_sent,
            t.checksum_failures,
            t.inbound_sanity_drops,
            t.uncached_nak_ids,
            summary.disconnect_sent
        );
    }
    code
}

/// What a session's heartbeat watches, when the launcher asked for
/// heartbeats: the session loop, and the process's window host while its
/// window shows the session.
#[derive(Debug, Clone)]
pub(crate) struct Liveness {
    /// The window host, when the process has one.
    pub(crate) window: Option<ac_render::HostHandle>,
}

impl Liveness {
    /// Liveness when the launcher asked for heartbeats (its environment
    /// variable), otherwise `None`.
    pub(crate) fn requested(window: Option<ac_render::HostHandle>) -> Option<Self> {
        ac_host::heartbeat_requested(|name| std::env::var(name).ok()).then_some(Self { window })
    }
}

/// Decides when a heartbeat is written: once per interval, when the session
/// loop moved since the last check and, while a window shows the session,
/// its event loop answered the ping sent at the last check. A window that
/// is minimized still answers (a ping wakes its loop); a hung one does not.
#[derive(Debug)]
pub(crate) struct HeartbeatGate {
    last: std::time::Instant,
    progress: u64,
    /// The answered count when the last ping went out.
    ping_sent: Option<u64>,
}

impl HeartbeatGate {
    pub(crate) fn new(now: std::time::Instant, progress: u64) -> Self {
        Self {
            last: now,
            progress,
            ping_sent: None,
        }
    }

    /// At `now`, with the session loop's `progress` and, while a window
    /// shows the session, how many pings its event loop has `answered`:
    /// `None` until an interval has passed, then whether to write a
    /// heartbeat (and a new ping is due when `answered` was given).
    pub(crate) fn check(
        &mut self,
        now: std::time::Instant,
        progress: u64,
        answered: Option<u64>,
    ) -> Option<bool> {
        if now.saturating_duration_since(self.last) < ac_host::HEARTBEAT_INTERVAL {
            return None;
        }
        self.last = now;
        let moved = progress != self.progress;
        self.progress = progress;
        let turning = match answered {
            Some(answered) => {
                let turning = self.ping_sent.is_none_or(|sent| answered > sent);
                self.ping_sent = Some(answered);
                turning
            }
            None => {
                self.ping_sent = None;
                true
            }
        };
        Some(moved && turning)
    }
}

/// What the console needs to mark the lines a player would see: whether the
/// launcher asked for marks, and each session's answer window.
#[derive(Debug, Default)]
pub(crate) struct ConsoleAnswers {
    pub(crate) marks: bool,
    windows: BTreeMap<String, Arc<console::AnswerWindow>>,
}

impl ConsoleAnswers {
    /// Marks when the launcher asked for them (its environment variable).
    pub(crate) fn requested() -> Self {
        Self {
            marks: ac_host::console_kind::console_kinds_requested(|name| std::env::var(name).ok()),
            windows: BTreeMap::new(),
        }
    }

    /// Session `id`'s answer window.
    pub(crate) fn session(&mut self, id: &str) -> Arc<console::AnswerWindow> {
        Arc::clone(self.windows.entry(id.to_owned()).or_default())
    }

    /// A command was typed for session `id`: its answers are shown for a while.
    fn typed(&self, id: &str) {
        if let Some(window) = self.windows.get(id) {
            window.open(std::time::Instant::now());
        }
    }

    /// `line` marked as `kind` when the launcher asked for marks.
    fn mark<'a>(&self, kind: ConsoleLineKind, line: &'a str) -> std::borrow::Cow<'a, str> {
        console::marked(Some(kind), line, self.marks)
    }
}

/// Console: `/sessions`, `/session <id>` (where unaddressed lines go),
/// `@<id> <line>` (one line to one session), `/window [id]` (opens or moves
/// the window to that session, else to the current one), `/close`,
/// `/screenshot [file.png]`,
/// `/objects [id]`, `/time [id]`, `/contracts [id]`, `/journal [id]`,
/// `/quit [id]`; any other line is typed into the session's chat box.
pub(crate) fn spawn_console(
    sessions: BTreeMap<String, Sender<SessionCommand>>,
    window: Option<ac_render::HostHandle>,
    mut prefs: crate::graphics::DisplayPrefs,
    tracker: WindowTracker,
    answers: ConsoleAnswers,
) {
    let spawned = std::thread::Builder::new()
        .name("console".into())
        .spawn(move || {
            let ids: Vec<String> = sessions.keys().cloned().collect();
            let id_refs: Vec<&str> = ids.iter().map(String::as_str).collect();
            let several = ids.len() > 1;
            let mut default = ids.first().cloned().unwrap_or_default();
            // The console's answers are shown with or without the launcher's
            // Verbose.
            let say = |text: &str, session: Option<&str>| {
                let line = console::notice(text, session.filter(|_| several));
                errln!("{}", answers.mark(ConsoleLineKind::Reply, &line));
            };
            for line in std::io::stdin().lines() {
                let Ok(line) = line else { break };
                // The launcher's console shows what was typed; a terminal
                // already does.
                if answers.marks && !line.trim().is_empty() {
                    errln!("{}", answers.mark(ConsoleLineKind::Typed, line.trim()));
                }
                // Window-only commands (/fly, /vsync) go to the window host first.
                if let Some(host) = &window
                    && crate::graphics::window_command(host, &line, &mut prefs, &|usage| {
                        errln!("{}", answers.mark(ConsoleLineKind::Reply, usage));
                    })
                {
                    continue;
                }
                let input = console::parse(&line, &id_refs);
                // The session a line is for: the one it names, else the default.
                let pick = |named: Option<&str>| {
                    let id = named.unwrap_or(&default).to_owned();
                    sessions.get(&id).map(|s| (id, s))
                };
                match input {
                    ConsoleInput::Empty => {
                        // Enter on an empty line sends the staged draft, if any.
                        if let Some((_, s)) = pick(None) {
                            let _ = s.send(console::typed_line(None));
                        }
                    }
                    ConsoleInput::Sessions => {
                        for id in sessions.keys() {
                            let mut marks = String::new();
                            if *id == default {
                                marks.push_str(" (talking to this one)");
                            }
                            if tracker.session().as_deref() == Some(id.as_str()) {
                                marks.push_str(" (window)");
                            }
                            say(&format!("session {id}{marks}"), None);
                        }
                    }
                    ConsoleInput::Session(None) => say(&format!("talking to {default}"), None),
                    ConsoleInput::Session(Some(id)) => {
                        if sessions.contains_key(id) {
                            id.clone_into(&mut default);
                            say(&format!("now talking to {id}"), None);
                        } else {
                            say(&format!("no session is called {id}"), None);
                        }
                    }
                    ConsoleInput::Usage(usage) => say(usage, None),
                    ConsoleInput::Command { session, command } => match pick(session) {
                        Some((id, s)) => {
                            answers.typed(&id);
                            let _ = s.send(command.into_session_command());
                        }
                        None => say(&format!("no session is called {default}"), None),
                    },
                    ConsoleInput::Chat { session, line } => match pick(session) {
                        Some((id, s)) => {
                            // A client command typed into the chat box (`/nav`,
                            // `/plugin`) is answered too.
                            answers.typed(&id);
                            let _ = s.send(console::typed_line(Some(line)));
                        }
                        None => say(&format!("no session is called {default}"), None),
                    },
                    ConsoleInput::Objects { session }
                    | ConsoleInput::Time { session }
                    | ConsoleInput::Contracts { session }
                    | ConsoleInput::Journal { session } => {
                        let command = match input {
                            ConsoleInput::Objects { .. } => SessionCommand::ListObjects,
                            ConsoleInput::Time { .. } => SessionCommand::GameTime,
                            ConsoleInput::Contracts { .. } => SessionCommand::ListContracts,
                            _ => SessionCommand::ShowJournal,
                        };
                        match pick(session) {
                            Some((id, s)) => {
                                answers.typed(&id);
                                let _ = s.send(command);
                            }
                            None => say(console::HELP, None),
                        }
                    }
                    ConsoleInput::Quit { session } => {
                        let targets: Vec<_> = match session {
                            Some(id) => sessions.get(id).into_iter().collect(),
                            None => sessions.values().collect(),
                        };
                        say("quitting (graceful logout)", session);
                        for s in targets {
                            let _ = s.send(SessionCommand::Shutdown);
                        }
                    }
                    ConsoleInput::Window { session } => {
                        let id =
                            match window_session(window.is_some(), session, &default, &sessions) {
                                Ok(id) => id.to_owned(),
                                Err(why) => {
                                    say(&why, None);
                                    // The launcher's row reads the refusal from the
                                    // session's status file.
                                    let named = session.unwrap_or(&default);
                                    if window.is_none() && sessions.contains_key(named) {
                                        tracker.refused(
                                            named,
                                            "this client runs without window support",
                                        );
                                    }
                                    continue;
                                }
                            };
                        let (Some(host), Some(s)) = (&window, sessions.get(&id)) else {
                            continue;
                        };
                        if let Some(old) = tracker.session()
                            && let Some(old_session) = sessions.get(&old)
                        {
                            let _ = host.detach();
                            let _ = old_session.send(SessionCommand::Detach);
                        }
                        let (to_window, from_session) = mpsc::channel();
                        let spec = prefs.window_spec();
                        tracker.attaching(&id);
                        if host
                            .attach_to_session(spec, from_session, s.clone())
                            .is_err()
                            || s.send(SessionCommand::Attach(to_window)).is_err()
                        {
                            tracker.refused(&id, "the window host has stopped");
                        }
                    }
                    ConsoleInput::Close => {
                        if let (Some(host), Some(id)) = (&window, tracker.session()) {
                            let _ = host.detach();
                            if let Some(s) = sessions.get(&id) {
                                let _ = s.send(SessionCommand::Detach);
                            }
                        }
                    }
                    ConsoleInput::Screenshot(file) => {
                        if let Some(host) = &window {
                            let _ = host.screenshot(ac_render::screenshot_path(file));
                        }
                    }
                }
            }
        });
    if let Err(error) = spawned {
        errln!("openac: console unavailable: {error}");
    }
}

/// The session `/window [id]` opens the window on (or moves it to): the one
/// named, else the one unaddressed lines go to. Without window support, or
/// for an id no session has, the notice that says which.
fn window_session<'a, T>(
    window_support: bool,
    named: Option<&'a str>,
    current: &'a str,
    sessions: &BTreeMap<String, T>,
) -> Result<&'a str, String> {
    if !window_support {
        return Err(
            "no window support in this process (OPENAC_HEADLESS=1, as the launcher's \
                    character refresh runs, or no display): /window cannot open a window"
                .to_owned(),
        );
    }
    let id = named.unwrap_or(current);
    if sessions.contains_key(id) {
        Ok(id)
    } else {
        Err(format!(
            "no session is called {id}: /window [session id] (/sessions lists them)"
        ))
    }
}

/// How a session that ended reports itself: no error is success, a
/// failure to connect or a login that found no character to enter is a
/// connection error (as OpenAC's headless client: any start that does
/// not reach the world), anything else a runtime error.
fn end_code(error: Option<&str>) -> HostExit {
    match error {
        None => HostExit::Success,
        Some(error) if error.contains("connect") || error.starts_with("no available character") => {
            HostExit::Connection
        }
        Some(_) => HostExit::Runtime,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Not an OpenAC test: a heartbeat needs the session loop to have moved
    // and, while a window shows the session, its event loop to have
    // answered the last ping; a closed window is not asked.
    #[test]
    fn a_heartbeat_needs_the_session_and_its_window_to_make_progress() {
        let start = std::time::Instant::now();
        let at = |seconds: u64| start + Duration::from_secs(seconds);
        let mut gate = HeartbeatGate::new(start, 0);
        assert_eq!(gate.check(at(5), 10, None), None, "not yet due");
        assert_eq!(gate.check(at(10), 10, None), Some(true));
        assert_eq!(gate.check(at(20), 10, None), Some(false), "session stuck");
        assert_eq!(gate.check(at(30), 11, None), Some(true));
        // A window opens: its first check sends the first ping.
        assert_eq!(gate.check(at(40), 12, Some(0)), Some(true));
        assert_eq!(gate.check(at(50), 13, Some(1)), Some(true), "answered");
        assert_eq!(gate.check(at(60), 14, Some(1)), Some(false), "window hung");
        assert_eq!(gate.check(at(70), 15, Some(1)), Some(false), "still hung");
        assert_eq!(gate.check(at(80), 16, Some(2)), Some(true), "turning again");
        // The window closed: only the session counts.
        assert_eq!(gate.check(at(90), 17, None), Some(true));
    }

    // OpenAC HeadlessSessionHost.ResolveTerminalStatus: a start that does
    // not reach the world (no character included) exits 5.
    #[test]
    fn a_login_that_finds_no_character_is_a_connection_error() {
        assert_eq!(
            end_code(Some("no available character matches Id(1342177999)")),
            HostExit::Connection
        );
        assert_eq!(
            end_code(Some("could not connect to 127.0.0.1:9000")),
            HostExit::Connection
        );
        assert_eq!(
            end_code(Some("the world thread panicked")),
            HostExit::Runtime
        );
        assert_eq!(end_code(None), HostExit::Success);
    }

    fn two_sessions() -> BTreeMap<String, ()> {
        [("alpha".to_owned(), ()), ("beta".to_owned(), ())].into()
    }

    #[test]
    fn window_with_no_id_opens_on_the_session_the_console_talks_to() {
        let sessions = two_sessions();
        let input = console::parse("/window", &["alpha", "beta"]);
        let ConsoleInput::Window { session } = input else {
            panic!("not /window: {input:?}");
        };
        assert_eq!(session, None);
        // After `/session beta` the current session is not the first one.
        assert_eq!(window_session(true, session, "beta", &sessions), Ok("beta"));
        assert_eq!(
            window_session(true, session, "alpha", &sessions),
            Ok("alpha")
        );
        // One session: plain `/window` opens on it.
        let one: BTreeMap<String, ()> = [("bot".to_owned(), ())].into();
        let ConsoleInput::Window { session } = console::parse("/window", &["bot"]) else {
            panic!("not /window");
        };
        assert_eq!(window_session(true, session, "bot", &one), Ok("bot"));
    }

    #[test]
    fn window_with_an_id_opens_on_that_session() {
        let sessions = two_sessions();
        let input = console::parse("/window beta", &["alpha", "beta"]);
        let ConsoleInput::Window { session } = input else {
            panic!("not /window: {input:?}");
        };
        assert_eq!(session, Some("beta"));
        assert_eq!(
            window_session(true, session, "alpha", &sessions),
            Ok("beta")
        );
    }

    #[test]
    fn window_with_an_unknown_id_says_no_session_has_it() {
        let sessions = two_sessions();
        let why = window_session(true, Some("gamma"), "alpha", &sessions).unwrap_err();
        assert!(why.starts_with("no session is called gamma"), "{why}");
        assert!(!why.contains("window support"), "{why}");
    }

    #[test]
    fn window_without_window_support_says_so_whatever_the_id() {
        let sessions = two_sessions();
        for named in [None, Some("beta"), Some("gamma")] {
            let why = window_session(false, named, "alpha", &sessions).unwrap_err();
            assert!(why.starts_with("no window support"), "{why}");
            assert!(why.contains("OPENAC_HEADLESS=1"), "{why}");
            assert!(!why.contains("no session is called"), "{why}");
        }
    }

    #[test]
    fn the_documents_options_and_login_commands_reach_the_runtime() {
        // OpenAC HeadlessSessionHost: RuntimeDeclaredCharacterOptions.Parse
        // into option ids, the descriptor's LoginCommands and
        // LoginCommandDelayMs.
        let document = r#"{
          "version": 1,
          "sessions": [{
            "id": "bot",
            "endpoint": { "host": "127.0.0.1", "port": 9000 },
            "account": "testaccount",
            "character": { "index": 0 },
            "policy": { "id": "idle" },
            "credential": { "provider": "environment", "reference": "BOT_PASSWORD" },
            "characterOptions": { "AutoRepeatAttack": false, "IgnoreTradeRequests": true },
            "loginCommands": ["/day", "hello"],
            "loginCommandDelayMs": 750
          }]
        }"#;
        let config =
            ac_host::load_config(document.as_bytes(), ac_host::ConfigReader::Headless).unwrap();
        let login = login_automation(&config.sessions[0]);
        let mut declared = login.declared_options.clone();
        declared.sort_unstable();
        assert_eq!(
            declared,
            [
                (ac_runtime::character::option_id::AUTO_REPEAT_ATTACK, false),
                (
                    ac_runtime::character::option_id::IGNORE_TRADE_REQUESTS,
                    true
                )
            ]
        );
        assert_eq!(login.commands, ["/day", "hello"]);
        assert_eq!(login.command_delay, Duration::from_millis(750));
    }
}
