//! `openac --session-config <file>`: the windowed start the launcher uses.
//!
//! One session from a session-config document read as a window reads it
//! (exactly one session; the character may be absent; `policy` ignored;
//! `mode: "probe"` refused), its password from the provider the document
//! names (the launcher sends one line on standard input), and a game window
//! opened on that session at once. Ctrl+C, Ctrl+Break, SIGINT, SIGTERM and
//! SIGHUP log the character off before the process exits, and so does closing the
//! window, which is the session's only interface here. The session's status
//! file is written as `openac run` writes it, with the window's openings and
//! closings.
//!
//! With `--console` (the launcher's start) the console reads typed lines and
//! chat prints on standard error, as `openac run --console`; `/close` closes
//! the window and the session plays on, and `/window` opens it again, so the
//! launcher's row can switch the running session between graphical and
//! headless. The player closing the window still logs the character off
//! and ends the client, as closing OpenAC's game window does.

use crate::stdio::errln;
use std::process::ExitCode;
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex};

use ac_host::{
    ConfigReader, ConsoleStream, CredentialResolver, ExitCode as HostExit, PathSet, SessionMode,
    StatusWriter, SystemPlatform,
};
use ac_net::LossOptions;
use ac_render::{DetachReason, HostEvent};
use ac_runtime::session::{self, NetDiagnostics, SessionCommand, SessionConfig};

use crate::multi::{self, ChatPrinter};
use crate::window_state::WindowTracker;

/// The flag that selects this entry.
pub const FLAG: &str = "--session-config";

fn exit(code: HostExit) -> ExitCode {
    ExitCode::from(code.code())
}

/// The flag that turns the console on.
const CONSOLE_FLAG: &str = "--console";

/// The document path from `--session-config <file> [--console]`, the whole
/// command line, and whether the console is on.
fn parse(arguments: &[String]) -> Result<(&str, bool), &'static str> {
    match arguments {
        [flag, path] if flag == FLAG && !path.trim().is_empty() => Ok((path, false)),
        [flag, path, console]
            if flag == FLAG && console == CONSOLE_FLAG && !path.trim().is_empty() =>
        {
            Ok((path, true))
        }
        [flag] | [flag, _] if flag == FLAG => {
            Err("--session-config requires a value (a path to the session-config document).")
        }
        _ => Err("usage: openac --session-config <file> [--console] (no other arguments)."),
    }
}

/// Whether a window event means the window is gone for good: closed by the
/// player, dropped after a GPU failure, or never opened.
fn window_gone(event: &HostEvent) -> bool {
    matches!(
        event,
        HostEvent::AttachFailed(_)
            | HostEvent::Detached {
                reason: DetachReason::ClosedByUser | DetachReason::RenderFailed,
                ..
            }
    )
}

/// Whether a window event logs the character off and ends the client. The
/// player closing the window always does, as closing OpenAC's game window
/// shuts its client down, with the console on or off (`closed_by_player`:
/// the tracker named the session whose window the player closed). Without
/// the console the window is the session's only interface, so a window that
/// failed or never opened does too; with it a window the console closed
/// (`/close`, the launcher's switch to headless) or could not open leaves
/// the session playing.
fn logs_off(event: &HostEvent, closed_by_player: bool, console: bool) -> bool {
    closed_by_player || (!console && window_gone(event))
}

/// The character the session enters, and whether it waits at character
/// selection for the player instead: a document without a character (the
/// launcher's "Character select" mode) starts at the selection screen, as
/// OpenAC's graphical client does. `None` for a probe.
fn character_of(mode: &SessionMode) -> Option<(session::CharacterSelector, bool)> {
    match mode {
        SessionMode::Play { character, .. } => {
            Some((multi::selector(character.as_ref()), character.is_none()))
        }
        SessionMode::Probe => None,
    }
}

/// Runs the single windowed session; returns its exit code.
pub fn run(
    arguments: &[String],
    crash: &crate::crash::CrashReporter,
    facts: &ac_render::facts::SharedFacts,
) -> ExitCode {
    let (path, console) = match parse(arguments) {
        Ok(parsed) => parsed,
        Err(message) => {
            errln!("openac: {message}");
            return exit(HostExit::Usage);
        }
    };
    let platform = SystemPlatform;
    let config = match ac_host::load_config_file(std::path::Path::new(path), ConfigReader::Window) {
        Ok(config) => config,
        Err(error) => {
            errln!("Configuration invalid: {error}");
            return exit(HostExit::Configuration);
        }
    };
    let paths = match PathSet::resolve(&config.process.paths, &platform) {
        Ok(paths) => paths,
        Err(error) => {
            errln!("Configuration invalid: {error}");
            return exit(HostExit::Configuration);
        }
    };
    crash.set_directory(Some(paths.crash_reports_directory()));
    // Held for the whole run, so a client update or a move of the install
    // folder waits for this client.
    let _install_session = match crate::multi::hold_install(&paths) {
        Ok(lease) => lease,
        Err(message) => {
            errln!("{message}");
            return exit(HostExit::Configuration);
        }
    };
    // A launcher session's folder (none for a document written by hand),
    // held for the whole run so the launcher's cleanup leaves it alone.
    let session_folder =
        ac_host::layout::LauncherSessionFolder::claim(&paths, std::path::Path::new(path));
    // The window reader guarantees exactly one session, never a probe.
    let Some(descriptor) = config.sessions.first().cloned() else {
        errln!("Configuration invalid: the document names no session.");
        return exit(HostExit::Configuration);
    };
    let Some((character, await_character_selection)) = character_of(&descriptor.mode) else {
        errln!("Configuration invalid: a window cannot run a probe session.");
        return exit(HostExit::Configuration);
    };
    let (password, relay) = {
        let stdin = std::io::stdin();
        let mut lock = stdin.lock();
        let mut resolver = CredentialResolver::new(&mut lock, &paths.config_directory, &platform);
        let password = match resolver.resolve(&descriptor.id, &descriptor.credential) {
            Ok(password) => password,
            Err(error) => {
                errln!("Credential unavailable: {error}");
                return exit(HostExit::Credential);
            }
        };
        let relay = crate::remote::resolve(std::slice::from_ref(&descriptor), &mut resolver)
            .remove(&descriptor.id);
        (password, relay)
    };
    let Some(server) = multi::resolve_endpoint(&descriptor.endpoint.host, descriptor.endpoint.port)
    else {
        errln!(
            "openac: session {}: cannot resolve {}:{}",
            descriptor.id,
            descriptor.endpoint.host,
            descriptor.endpoint.port
        );
        return exit(HostExit::Configuration);
    };

    // The window host first: without one there is nothing to start.
    let graphics = match crate::graphics::GraphicsEnv::parse(|name| std::env::var(name).ok()) {
        Ok(graphics) => graphics,
        Err(error) => {
            errln!("openac: {error}");
            return exit(HostExit::Usage);
        }
    };
    let prefs = crate::graphics::DisplayPrefs::load(Some(&paths));
    let dat_dir = config
        .process
        .content
        .as_ref()
        .map(|content| content.dat_directory.clone());
    let mut host_options = graphics.host_options(
        dat_dir,
        None,
        Some(paths.diagnostics_directory()),
        &prefs.settings,
    );
    host_options.graphics.facts = facts.clone();
    prefs.apply_to(&mut host_options);
    let (host, window, window_events) = match ac_render::WindowHost::new(host_options) {
        Ok(parts) => parts,
        Err(error) => {
            errln!("openac: no window support here ({error})");
            return exit(HostExit::Runtime);
        }
    };

    let tables = crate::console::game_tables(
        config
            .process
            .content
            .as_ref()
            .map(|c| c.dat_directory.as_path()),
    );
    let content = crate::console::world_content(
        config
            .process
            .content
            .as_ref()
            .map(|c| c.dat_directory.as_path()),
    );
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
    let handle = match session::spawn(SessionConfig {
        server,
        account: descriptor.account.clone(),
        password: password.reveal().to_owned(),
        character,
        loss: LossOptions::default(),
        enter_world: true,
        await_character_selection,
        data_directory: Some(paths.data_directory.clone()),
        tables,
        content,
        diagnostics: NetDiagnostics::from_env(),
        login: crate::multi::login_automation(&descriptor),
        plugins: Some(crate::plugins::setup(
            &crate::plugins::engine(Some(&paths)),
            Some(&paths),
            descriptor.plugins.clone(),
            plugin_settings,
            crate::plugins::peer_tags(descriptor.plugin_tags.as_deref(), |name| {
                std::env::var(name).ok()
            }),
        )),
    }) {
        Ok(handle) => handle,
        Err(error) => {
            errln!("openac: session {}: {error}", descriptor.id);
            return exit(HostExit::Runtime);
        }
    };
    drop(password);
    let commands = handle.commands();
    let remote = Some(crate::remote::RemoteControl::new(
        &descriptor,
        std::path::Path::new(path),
        &paths.config_directory,
        relay,
        commands.clone(),
    ));
    // Which session the window is on, told to the status file.
    let (tracker, mut notes) = WindowTracker::new([descriptor.id.as_str()]);
    let notes = notes.remove(&descriptor.id);

    // The window opens at once on the session.
    let (to_window, from_session) = mpsc::channel();
    tracker.attaching(&descriptor.id);
    if window
        .attach_to_session(prefs.window_spec(), from_session, commands.clone())
        .is_err()
        || commands.send(SessionCommand::Attach(to_window)).is_err()
    {
        errln!("openac: the window could not be attached to the session");
        let _ = commands.send(SessionCommand::Shutdown);
    }

    let shutdown: Sender<SessionCommand> = commands.clone();
    if let Err(error) = ctrlc::set_handler(move || {
        let _ = shutdown.send(SessionCommand::Shutdown);
    }) {
        errln!("openac: Ctrl+C will not log off gracefully: {error}");
    }
    let on_window_gone = commands.clone();
    let events_tracker = tracker.clone();
    std::thread::Builder::new()
        .name("window-events".into())
        .spawn(move || {
            for event in window_events {
                crate::graphics::print_event(&event);
                let closed_by_player = events_tracker.follow(&event).is_some();
                if logs_off(&event, closed_by_player, console) {
                    let _ = on_window_gone.send(SessionCommand::Shutdown);
                }
            }
        })
        .ok();
    let mut answers = multi::ConsoleAnswers::requested();
    let marks = answers.marks;
    let window_answers = answers.session(&descriptor.id);
    if console {
        let sessions = std::iter::once((descriptor.id.clone(), commands.clone())).collect();
        multi::spawn_console(sessions, Some(window.clone()), prefs, tracker, answers);
    }

    let plugin_logs = session_folder
        .as_ref()
        .map(ac_host::layout::LauncherSessionFolder::plugin_logs_directory);
    let code: Arc<Mutex<HostExit>> = Arc::new(Mutex::new(HostExit::Runtime));
    let monitor = {
        let code = Arc::clone(&code);
        let window = window.clone();
        let id = descriptor.id.clone();
        let status = StatusWriter::for_file(descriptor.status_file.as_deref());
        std::thread::Builder::new()
            .name(format!("monitor-{id}"))
            .spawn(move || {
                // With the console, chat prints on standard error, which the
                // launcher keeps as the session's log and its console shows.
                let printer = ChatPrinter {
                    stream: console.then_some(ConsoleStream::Stderr),
                    session: None,
                    plugin_logs,
                    marks,
                    answers: window_answers,
                };
                // A window ignores the document's bot policy.
                let liveness = multi::Liveness::requested(Some(window.clone()));
                let beside = multi::Beside {
                    window: notes,
                    liveness,
                    remote,
                };
                let result = multi::monitor(&id, handle, status, &printer, None, beside);
                if let Ok(mut code) = code.lock() {
                    *code = result;
                }
                // The session is over: close the window and end the event loop.
                let _ = window.shutdown();
            })
    };
    let monitor = match monitor {
        Ok(monitor) => monitor,
        Err(error) => {
            errln!("openac: could not watch the session: {error}");
            let _ = commands.send(SessionCommand::Shutdown);
            return exit(HostExit::Runtime);
        }
    };
    if let Err(error) = host.run() {
        errln!("openac: window host stopped: {error}");
        // The event loop is gone; log off rather than leave the character.
        let _ = commands.send(SessionCommand::Shutdown);
    }
    let _ = monitor.join();
    let code = code.lock().map_or(HostExit::Runtime, |code| *code);
    exit(code)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn args(list: &[&str]) -> Vec<String> {
        list.iter().map(|&a| a.to_owned()).collect()
    }

    #[test]
    fn the_command_line_is_the_flag_one_path_and_an_optional_console() {
        assert_eq!(
            parse(&args(&["--session-config", "s.json"])),
            Ok(("s.json", false))
        );
        assert_eq!(
            parse(&args(&["--session-config", "s.json", "--console"])),
            Ok(("s.json", true))
        );
        for bad in [
            &["--session-config"][..],
            &["--session-config", " "],
            &["--session-config", "a.json", "extra"],
            &["--session-config", " ", "--console"],
            &["--session-config", "a.json", "--console", "--console"],
            &["run", "--config", "a.json"],
        ] {
            assert!(parse(&args(bad)).is_err(), "{bad:?}");
        }
    }

    #[test]
    fn a_document_without_a_character_waits_at_character_selection() {
        // OpenAC `SessionPlayerComposition`: `AwaitCharacterSelection` is
        // set when the launch names no character.
        use ac_host::CharacterSelector as Named;
        let play = |character| SessionMode::Play {
            character,
            policy: None,
        };
        assert_eq!(
            character_of(&play(None)),
            Some((session::CharacterSelector::FirstAvailable, true))
        );
        assert_eq!(
            character_of(&play(Some(Named::Name("+Horan".into())))),
            Some((session::CharacterSelector::Name("+Horan".into()), false))
        );
        assert_eq!(character_of(&SessionMode::Probe), None);
    }

    /// OpenAC `GameWindow.OnClosing` -> `CompleteShutdown`: closing the game
    /// window of a graphical client shuts the client down (the session logs
    /// off, the status file says so, the process exits). The launcher starts
    /// it with the console; the player's close after a return to character
    /// selection and a second entry (no window event in between: the window
    /// stays open, so the tracker still names the session) logs off as well,
    /// where it used to leave the session playing headless. A close the
    /// console asked for (`/close`, the launcher's
    /// switch to headless) and a window it could not open do not.
    #[test]
    fn the_player_closing_a_launched_graphical_clients_window_logs_it_off() {
        use crate::window_state::Change;
        let detached = |reason| HostEvent::Detached {
            reason,
            frames_presented: 0,
        };
        let opened = |tracker: &WindowTracker| {
            tracker.attaching("s1");
            tracker.change(Change::Opened);
        };
        for console in [true, false] {
            let (tracker, _notes) = WindowTracker::new(["s1"]);
            opened(&tracker);
            // Log out to character selection and enter again: the session
            // does it; the window reports nothing.
            let closed = detached(DetachReason::ClosedByUser);
            let by_player = tracker.follow(&closed).is_some();
            assert!(by_player, "the tracker names the session the player closed");
            assert!(logs_off(&closed, by_player, console), "console {console}");
        }

        let (tracker, _notes) = WindowTracker::new(["s1"]);
        opened(&tracker);
        let requested = detached(DetachReason::Requested);
        let by_player = tracker.follow(&requested).is_some();
        assert!(!logs_off(&requested, by_player, true), "/close plays on");
        tracker.attaching("s1");
        let rejected = HostEvent::AttachRejected;
        let by_player = tracker.follow(&rejected).is_some();
        assert!(!logs_off(&rejected, by_player, true), "a refused /window");
        // Without the console the window is the only interface.
        assert!(logs_off(
            &detached(DetachReason::RenderFailed),
            false,
            false
        ));
        assert!(!logs_off(
            &detached(DetachReason::RenderFailed),
            false,
            true
        ));
    }

    #[test]
    fn the_session_logs_off_when_the_window_is_gone_for_good() {
        let detached = |reason| HostEvent::Detached {
            reason,
            frames_presented: 0,
        };
        assert!(window_gone(&detached(DetachReason::ClosedByUser)));
        assert!(window_gone(&detached(DetachReason::RenderFailed)));
        assert!(!window_gone(&detached(DetachReason::Requested)));
        assert!(!window_gone(&detached(DetachReason::Shutdown)));
        assert!(!window_gone(&HostEvent::AttachRejected));
    }
}
