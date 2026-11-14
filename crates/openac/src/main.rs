//! The openAC client. Runs game sessions; a window can attach later.

mod console;
mod crash;
mod graphics;
mod movement_verbs;
mod multi;
mod options;
mod plugins;
mod remote;
mod remote_settings;
mod session_config;
mod stdio;
mod window_state;

use crate::stdio::{errln, outln};
use std::process::ExitCode;
use std::time::{Duration, Instant};

use ac_net::{LossDirection, LossOptions};
use ac_runtime::session::{
    self, CharacterSelector, HostEvent, NetDiagnostics, ObjectSummary, SessionCommand,
    SessionConfig, SessionEvent, SessionSummary,
};
use console::ConsoleInput;
use options::{Command, DropDirection, LogLevel, Options, USAGE};

/// Exit code for a session that ended with an error.
const EXIT_SESSION_FAILED: u8 = 1;
/// Exit code for bad options.
const EXIT_USAGE: u8 = 2;

/// `openac plugin-api`: print [`plugin_api_file_text`].
const PLUGIN_API_VERB: &str = "plugin-api";

/// The `plugin-api.json` of this build: the plugin API versions its plugin
/// host loads.
fn plugin_api_file_text() -> String {
    ac_plugin::abi::API_RANGE.to_file_text()
}

fn main() -> ExitCode {
    // A panic on any thread leaves a local crash report in the crash folder
    // under the logs, with what the open window (if any) knew.
    let facts = ac_render::facts::SharedFacts::default();
    let paths =
        ac_host::PathSet::resolve(&ac_host::PathOverrides::default(), &ac_host::SystemPlatform)
            .ok();
    let crash = crash::CrashReporter::install(
        paths
            .as_ref()
            .map(ac_host::PathSet::crash_reports_directory),
        facts.clone(),
    );
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    // The peer hub the sessions on this computer share (OpenAC
    // `acdream-headless peer-hub <peer-directory>`): started by the first
    // session that wants peers, it runs until nobody has used it for 15 s.
    if let [verb, directory] = arguments.as_slice()
        && verb == "peer-hub"
    {
        let endpoint =
            ac_runtime::plugins::peer_transport::endpoint_for(std::path::Path::new(directory));
        return match ac_runtime::plugins::peer_hub::serve(&endpoint, |line| errln!("{line}")) {
            Ok(()) => ExitCode::SUCCESS,
            Err(error) => {
                errln!("openac: the peer hub could not run: {error}");
                ExitCode::from(EXIT_SESSION_FAILED)
            }
        };
    }
    // The plugin API versions this client loads, as the `plugin-api.json`
    // its release carries beside it (the launcher judges plugins by it).
    if let [verb] = arguments.as_slice()
        && verb == PLUGIN_API_VERB
    {
        outln!("{}", plugin_api_file_text().trim_end());
        return ExitCode::SUCCESS;
    }
    // `run` and `validate` take a session-config document.
    if matches!(
        arguments.first().map(String::as_str),
        Some("run" | "validate")
    ) {
        return multi::run(&arguments, &crash, &facts);
    }
    // The launcher's windowed start: one session, a window on it at once.
    if arguments.first().map(String::as_str) == Some(session_config::FLAG) {
        return session_config::run(&arguments, &crash, &facts);
    }
    match Command::parse(std::env::args().skip(1), |name| std::env::var(name).ok()) {
        Ok(Command::Help) => {
            outln!("{USAGE}");
            ExitCode::SUCCESS
        }
        Ok(Command::Run(options)) if options.headless => {
            run_session(&options, None, graphics::DisplayPrefs::load(None))
        }
        Ok(Command::Run(options)) => run_with_window_support(options, paths.as_ref(), &facts),
        Err(error) => {
            errln!("openac: {error}\n\n{USAGE}");
            ExitCode::from(EXIT_USAGE)
        }
    }
}

/// Window support: the platform event loop owns the main thread; the
/// session and its monitor run beside it. No window opens until /window.
fn run_with_window_support(
    options: Options,
    paths: Option<&ac_host::PathSet>,
    facts: &ac_render::facts::SharedFacts,
) -> ExitCode {
    let graphics = match graphics::GraphicsEnv::parse(|name| std::env::var(name).ok()) {
        Ok(graphics) => graphics,
        Err(error) => {
            errln!("openac: {error}\n\n{USAGE}");
            return ExitCode::from(EXIT_USAGE);
        }
    };
    let prefs = graphics::DisplayPrefs::load(paths);
    let mut host_options = graphics.host_options(
        options.dat_dir.clone(),
        options.landblock_radius,
        paths.map(ac_host::PathSet::diagnostics_directory),
        &prefs.settings,
    );
    host_options.graphics.facts = facts.clone();
    prefs.apply_to(&mut host_options);
    let (host, window, host_events) = match ac_render::WindowHost::new(host_options) {
        Ok(parts) => parts,
        Err(error) => {
            errln!("openac: no window support here ({error}); use --headless");
            return ExitCode::from(EXIT_SESSION_FAILED);
        }
    };
    std::thread::Builder::new()
        .name("window-events".into())
        .spawn(move || {
            for event in host_events {
                graphics::print_event(&event);
            }
        })
        .ok();
    let session_window = window.clone();
    let session = std::thread::Builder::new()
        .name("session-monitor".into())
        .spawn(move || {
            let code = run_session(&options, Some(session_window.clone()), prefs);
            // The session is over: close any window and end the event loop.
            let _ = session_window.shutdown();
            code
        });
    let session = match session {
        Ok(thread) => thread,
        Err(error) => {
            errln!("openac: could not start the session: {error}");
            return ExitCode::from(EXIT_SESSION_FAILED);
        }
    };
    if let Err(error) = host.run() {
        errln!("openac: window host stopped: {error}");
    }
    session
        .join()
        .unwrap_or_else(|_| ExitCode::from(EXIT_SESSION_FAILED))
}

/// Run one session to its end; with window support, the console can open
/// and close a window on it.
fn run_session(
    options: &Options,
    window: Option<ac_render::HostHandle>,
    prefs: graphics::DisplayPrefs,
) -> ExitCode {
    let started = Instant::now();
    let log = Logger {
        level: options.log_level,
        started,
    };
    let paths = resolve_paths(options, &log);
    // Held for the whole run, so a client update or a move of the install
    // folder waits for this client.
    let _install_session = match paths.as_ref().map(multi::hold_install).transpose() {
        Ok(lease) => lease,
        Err(message) => {
            log.error(&message);
            return ExitCode::from(EXIT_SESSION_FAILED);
        }
    };
    let data_directory = paths.as_ref().map(|p| p.data_directory.clone());
    // The single-session form has no document: every plugin found loads,
    // with the settings file's settings, if one is named.
    let plugin_settings = match plugins::settings(None, |name| std::env::var(name).ok()) {
        Ok(settings) => settings,
        Err(error) => {
            log.error(&error);
            return ExitCode::from(EXIT_USAGE);
        }
    };
    let plugins = plugins::setup(
        &plugins::engine(paths.as_ref()),
        paths.as_ref(),
        None,
        plugin_settings,
        plugins::peer_tags(None, |name| std::env::var(name).ok()),
    );
    let config = SessionConfig {
        server: options.server,
        account: options.account.clone(),
        password: options.password.expose().to_owned(),
        character: options
            .character
            .clone()
            .map_or(CharacterSelector::FirstAvailable, CharacterSelector::Name),
        loss: options
            .net_loss
            .map_or_else(LossOptions::default, |loss| LossOptions {
                drop_percent: loss.percent,
                seed: u64::from(loss.seed),
                direction: match loss.direction {
                    DropDirection::In => LossDirection::In,
                    DropDirection::Out => LossDirection::Out,
                    DropDirection::Both => LossDirection::Both,
                },
            }),
        enter_world: true,
        // Without a character the session waits at character selection
        // (the window's screen, the console's /enter), as OpenAC's client
        // does when its launch names none.
        await_character_selection: options.character.is_none(),
        data_directory,
        tables: console::game_tables(options.dat_dir.as_deref()),
        content: console::world_content(options.dat_dir.as_deref()),
        diagnostics: NetDiagnostics::from_env(),
        login: ac_runtime::session::LoginAutomation::default(),
        plugins: Some(plugins),
    };
    if let Some(loss) = options.net_loss {
        log.info(&format!(
            "simulated packet loss: {}% seed {} direction {:?}",
            loss.percent, loss.seed, loss.direction
        ));
    }
    log.info(&format!(
        "connecting to {} as {}",
        options.server, options.account
    ));
    if options.character.is_none() {
        log.info("no --character: the session waits at character selection (/enter [character])");
    }

    let handle = match session::spawn(config) {
        Ok(handle) => handle,
        Err(error) => {
            log.error(&format!("could not start the session thread: {error}"));
            return ExitCode::from(EXIT_SESSION_FAILED);
        }
    };

    // Ctrl+C and Ctrl+Break (Windows), SIGINT, SIGTERM and SIGHUP (Unix)
    // log the character off instead of killing the process, which would
    // leave it in the world on the server.
    let shutdown = handle.commands();
    if let Err(error) = ctrlc::set_handler(move || {
        let _ = shutdown.send(SessionCommand::Shutdown);
    }) {
        log.warn(&format!("Ctrl+C will not log off gracefully: {error}"));
    }
    spawn_console(handle.commands(), window, prefs);

    let mut in_world_since: Option<Instant> = None;
    let mut shutdown_sent = false;
    let summary = loop {
        if let (Some(since), Some(duration)) = (in_world_since, options.duration)
            && !shutdown_sent
            && since.elapsed() >= duration
        {
            log.info(&format!(
                "stayed {} s in the world; logging off",
                duration.as_secs()
            ));
            let _ = handle.commands().send(SessionCommand::Shutdown);
            shutdown_sent = true;
        }
        match handle.next_event(Duration::from_millis(250)) {
            Some(HostEvent::Session(event)) => {
                if matches!(event, SessionEvent::EnteredWorld(_)) {
                    in_world_since = Some(Instant::now());
                }
                log.session_event(&event);
            }
            Some(HostEvent::Objects(objects)) => log.objects(&objects),
            Some(HostEvent::GameTime(Some(date))) => log.info(&format!(
                "game time: {:?} {} of year {}, {:?}",
                date.month, date.day, date.year, date.hour
            )),
            Some(HostEvent::GameTime(None)) => log.info("game time: not synced yet"),
            Some(HostEvent::Chat(line)) => {
                if let Some(text) = console::console_chat_line(&line, None, false) {
                    outln!("{text}");
                }
            }
            Some(HostEvent::InterfaceText(text)) => {
                outln!("{}", console::interface_text(&text, None));
            }
            Some(HostEvent::Contracts(contracts)) => {
                for line in console::contract_lines(&contracts, None) {
                    outln!("{line}");
                }
            }
            Some(HostEvent::Journal {
                pages,
                current_page,
            }) => {
                for line in console::journal_lines(&pages, current_page, None) {
                    outln!("{line}");
                }
            }
            Some(HostEvent::Ended(summary)) => break summary,
            None if handle.is_finished() => {
                // The session thread stopped without ending: it faulted
                // (the crash report says where).
                let end = handle.join_quarantining();
                log.error(&format!("the session stopped without ending ({end:?})"));
                return ExitCode::from(EXIT_SESSION_FAILED);
            }
            Some(other) => {
                for line in console::event_lines(&other, None) {
                    outln!("{line}");
                }
            }
            None => {}
        }
    };
    handle.join();
    log.summary(&summary);
    if summary.error.is_some() {
        ExitCode::from(EXIT_SESSION_FAILED)
    } else {
        ExitCode::SUCCESS
    }
}

/// Prints progress with the time since start; stdout for progress, stderr
/// for problems.
struct Logger {
    level: LogLevel,
    started: Instant,
}

impl Logger {
    fn stamp(&self) -> String {
        format!("[{:>8.3}]", self.started.elapsed().as_secs_f64())
    }

    fn error(&self, text: &str) {
        errln!("{} error: {text}", self.stamp());
    }

    fn warn(&self, text: &str) {
        if self.level >= LogLevel::Warn {
            errln!("{} warn: {text}", self.stamp());
        }
    }

    fn info(&self, text: &str) {
        if self.level >= LogLevel::Info {
            outln!("{} {text}", self.stamp());
        }
    }

    fn debug(&self, text: &str) {
        if self.level >= LogLevel::Debug {
            outln!("{} {text}", self.stamp());
        }
    }

    fn session_event(&self, event: &SessionEvent) {
        match event {
            SessionEvent::PhaseChanged(phase) => self.info(&format!("phase: {phase:?}")),
            SessionEvent::CharacterList(list) => {
                let names: Vec<&str> = list.characters.iter().map(|c| c.name.as_str()).collect();
                self.info(&format!(
                    "account {} has characters: {}",
                    list.account_name,
                    names.join(", ")
                ));
            }
            SessionEvent::ServerName(name) => self.info(&format!(
                "world {} ({} of {} connections)",
                name.world_name, name.current_connections, name.max_connections
            )),
            SessionEvent::EnteredWorld(character) => self.info(&format!(
                "entered the world as {} (0x{:08X})",
                character.name, character.id
            )),
            SessionEvent::LoginCompleteSent => self.debug("login complete sent"),
            SessionEvent::LoggedOff { confirmed: true } => self.info("log off confirmed"),
            SessionEvent::LoggedOff { confirmed: false } => {
                self.warn("log off not confirmed in time; disconnecting anyway");
            }
            SessionEvent::ReturnedToCharacterSelect(generation) => self.info(&format!(
                "back at character selection (generation {generation})"
            )),
            SessionEvent::CharacterCreated { guid, name } => {
                self.info(&format!("created character {name} (0x{guid:08X})"));
            }
            SessionEvent::CreationFailed { code, reason, name } => self.warn(&format!(
                "the server refused to create {name}: {reason} ({code})"
            )),
            SessionEvent::Failed(error) => self.error(&error.to_string()),
        }
    }

    fn summary(&self, summary: &SessionSummary) {
        if let Some(error) = &summary.error {
            self.error(&format!("session ended: {error}"));
        } else {
            self.info("session ended cleanly");
        }
        if let Some(t) = &summary.transport {
            self.info(&format!(
                "link: packets in {} out {}, resends {}, NAKs in {} out {}, acks out {}, \
                 dup drops {}, checksum failures {}, sanity drops {}, uncached NAK ids {}, \
                 keys parked {}, disconnect sent {}",
                t.packets_received,
                t.packets_sent,
                t.resends_sent,
                t.nak_requests_received,
                t.naks_sent,
                t.acks_sent,
                t.inbound_dups_dropped,
                t.checksum_failures,
                t.inbound_sanity_drops,
                t.uncached_nak_ids,
                t.keys_parked,
                summary.disconnect_sent
            ));
        }
        self.info(&format!(
            "world: {} objects, player description {}, parse failures {:?}",
            summary.world_objects,
            if summary.player_described {
                "received"
            } else {
                "missing"
            },
            summary.parse_failures
        ));
        if let Some(loss) = &summary.loss {
            self.info(&format!("simulated loss: {loss:?}"));
        }
    }
}

/// The host's directories, from `OPENAC_DATA_DIR` or the platform
/// default, resolved by the same rules as `openac run`: chat files go to
/// the data directory, plugin storage under it and the configuration
/// directory. Without them the journal stays in memory, `@log` reports it
/// cannot open a file and plugins have no storage.
fn resolve_paths(options: &Options, log: &Logger) -> Option<ac_host::PathSet> {
    let overrides = ac_host::PathOverrides {
        data_directory: options.data_dir.clone(),
        ..ac_host::PathOverrides::default()
    };
    match ac_host::PathSet::resolve(&overrides, &ac_host::SystemPlatform) {
        Ok(paths) => Some(paths),
        Err(error) => {
            log.warn(&format!(
                "no data directory ({error}); chat logs and the journal are not saved"
            ));
            None
        }
    }
}

/// Read console lines from standard input on their own thread. The
/// console's own verbs (`/objects`, `/time`, `/contracts`, `/journal`,
/// `/quit`, and with window support `/window`, `/close`, `/screenshot`) are
/// answered here; every other line is typed into the session's chat box.
fn spawn_console(
    commands: std::sync::mpsc::Sender<SessionCommand>,
    window: Option<ac_render::HostHandle>,
    mut prefs: graphics::DisplayPrefs,
) {
    let spawned = std::thread::Builder::new()
        .name("console".into())
        .spawn(move || {
            for line in std::io::stdin().lines() {
                let Ok(line) = line else { break };
                // Window-only commands (/fly, /vsync) go to the window host first.
                if let Some(host) = &window
                    && graphics::window_command(host, &line, &mut prefs, &|usage| errln!("{usage}"))
                {
                    continue;
                }
                let command = match (console::parse(&line, &[]), &window) {
                    (ConsoleInput::Window { .. }, Some(host)) => {
                        let (to_window, from_session) = std::sync::mpsc::channel();
                        let spec = prefs.window_spec();
                        if host
                            .attach_to_session(spec, from_session, commands.clone())
                            .is_err()
                        {
                            errln!("the window host has stopped");
                            continue;
                        }
                        SessionCommand::Attach(to_window)
                    }
                    (ConsoleInput::Close, Some(host)) => {
                        let _ = host.detach();
                        SessionCommand::Detach
                    }
                    (ConsoleInput::Screenshot(file), Some(host)) => {
                        let _ = host.screenshot(ac_render::screenshot_path(file));
                        continue;
                    }
                    (
                        ConsoleInput::Window { .. }
                        | ConsoleInput::Close
                        | ConsoleInput::Screenshot(_),
                        None,
                    ) => {
                        outln!(
                            "{}",
                            console::notice("no window support (--headless)", None)
                        );
                        continue;
                    }
                    (ConsoleInput::Objects { .. }, _) => SessionCommand::ListObjects,
                    (ConsoleInput::Time { .. }, _) => SessionCommand::GameTime,
                    (ConsoleInput::Contracts { .. }, _) => SessionCommand::ListContracts,
                    (ConsoleInput::Journal { .. }, _) => SessionCommand::ShowJournal,
                    (ConsoleInput::Quit { .. }, _) => {
                        outln!("{}", console::notice("quitting (graceful logout)", None));
                        SessionCommand::Shutdown
                    }
                    (ConsoleInput::Chat { line, .. }, _) => console::typed_line(Some(line)),
                    (ConsoleInput::Command { command, .. }, _) => command.into_session_command(),
                    (ConsoleInput::Usage(usage), _) => {
                        outln!("{}", console::notice(usage, None));
                        continue;
                    }
                    (ConsoleInput::Sessions | ConsoleInput::Session(_), _) => {
                        outln!("{}", console::notice(console::HELP, None));
                        continue;
                    }
                    (ConsoleInput::Empty, _) => console::typed_line(None),
                };
                if commands.send(command).is_err() {
                    break;
                }
            }
        });
    if let Err(error) = spawned {
        errln!("openac: console unavailable: {error}");
    }
}

impl Logger {
    fn objects(&self, objects: &[ObjectSummary]) {
        self.info(&format!("{} objects known:", objects.len()));
        for o in objects {
            let place = match (o.cell_id, o.origin, o.parent) {
                (Some(cell), Some([x, y, z]), _) => {
                    format!("cell 0x{cell:08X} ({x:.1}, {y:.1}, {z:.1})")
                }
                (_, _, Some(parent)) => format!("held by 0x{parent:08X}"),
                _ => "not in the world".to_owned(),
            };
            let distance = o
                .distance
                .map_or_else(String::new, |d| format!(" {d:.1} m"));
            let monarch = o
                .monarch
                .map_or_else(String::new, |m| format!(", monarch 0x{m:08X}"));
            outln!(
                "    0x{:08X} #{:<5} {:<32} {place}{distance}{monarch}",
                o.guid,
                o.local_id,
                o.name.as_deref().unwrap_or("?")
            );
        }
    }
}
