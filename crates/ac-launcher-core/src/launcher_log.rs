//! The launcher's own log, `logs/launcher/launcher.log`: what it reports
//! about itself (start, the folder layout, cleanup, its window) and what
//! its sessions do, one line each with the local time, appended across
//! starts. Past [`ROLL_AT_BYTES`] the file is renamed `launcher.old.log`
//! (replacing the one before) and a new one begins, so the two together
//! stay bounded.

use std::collections::HashMap;
use std::fs::File;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::SystemTime;

use ac_host::PathSet;
use ac_host::local_time::LocalZone;

use crate::orchestration::{LauncherSessionSnapshot, LauncherStateSnapshot};

/// The size at which the log starts over.
pub const ROLL_AT_BYTES: u64 = 4 * 1024 * 1024;

#[derive(Debug)]
struct State {
    path: PathBuf,
    roll_at: u64,
    zone: LocalZone,
    file: Option<File>,
    written: u64,
    /// The last failure was said on standard error; the next is not until
    /// a write succeeds again.
    failing: bool,
}

/// The launcher's log; clones share one file.
#[derive(Debug, Clone)]
pub struct LauncherLog {
    state: Arc<Mutex<State>>,
}

impl LauncherLog {
    /// The log of an install.
    pub fn open(paths: &PathSet) -> Self {
        Self::at(
            &paths.launcher_log_file(),
            ROLL_AT_BYTES,
            LocalZone::system(),
        )
    }

    /// A log at `path` that starts over past `roll_at` bytes.
    pub fn at(path: &Path, roll_at: u64, zone: LocalZone) -> Self {
        Self {
            state: Arc::new(Mutex::new(State {
                path: path.to_path_buf(),
                roll_at,
                zone,
                file: None,
                written: 0,
                failing: false,
            })),
        }
    }

    /// The file written to.
    pub fn path(&self) -> PathBuf {
        self.lock().path.clone()
    }

    /// The file the log rolls over to.
    pub fn old_path(&self) -> PathBuf {
        old_path(&self.lock().path)
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, State> {
        self.state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    /// Appends `text` as one line. A write that fails is said once on
    /// standard error; the launcher goes on.
    pub fn line(&self, text: &str) {
        let mut state = self.lock();
        let line = format!(
            "{} {text}\n",
            local_time_text(SystemTime::now(), &state.zone)
        );
        match write_line(&mut state, line.as_bytes()) {
            Ok(()) => state.failing = false,
            Err(error) => {
                state.file = None;
                if !state.failing {
                    state.failing = true;
                    eprintln!(
                        "launcher: the log {} could not be written: {error}",
                        state.path.display()
                    );
                }
            }
        }
    }

    /// Says `text` on standard error, as the launcher always has, and
    /// keeps it in the log.
    pub fn say(&self, text: &str) {
        eprintln!("{text}");
        self.line(text);
    }
}

fn old_path(path: &Path) -> PathBuf {
    path.with_file_name("launcher.old.log")
}

fn write_line(state: &mut State, line: &[u8]) -> std::io::Result<()> {
    if state.file.is_none() {
        let file = ac_host::layout::open_append(&state.path)?;
        state.written = file.metadata()?.len();
        state.file = Some(file);
    }
    if state.written > 0 && state.written + line.len() as u64 > state.roll_at {
        state.file = None;
        std::fs::rename(&state.path, old_path(&state.path))?;
        state.file = Some(ac_host::layout::open_append(&state.path)?);
        state.written = 0;
    }
    if let Some(file) = state.file.as_mut() {
        file.write_all(line)?;
        state.written += line.len() as u64;
    }
    Ok(())
}

/// `yyyy-MM-dd HH:mm:ss` on the local clock.
fn local_time_text(at: SystemTime, zone: &LocalZone) -> String {
    let utc_ms = at.duration_since(std::time::UNIX_EPOCH).map_or(0, |since| {
        i64::try_from(since.as_millis()).unwrap_or(i64::MAX)
    });
    let local_ms = utc_ms + i64::from(zone.utc_offset_seconds(utc_ms)) * 1000;
    let name = ac_host::layout::session_folder_name(local_ms);
    // The folder name's `HH-mm-ss` read as a time of day.
    let (date, time) = name.split_at(10);
    format!("{date} {}", time.trim_start().replace('-', ":"))
}

/// Words the sessions' changes for the log: a line when a session first
/// appears, when its state changes, and when its error changes.
#[derive(Debug, Default)]
pub struct SessionChanges {
    seen: HashMap<String, (String, Option<String>)>,
}

impl SessionChanges {
    /// The lines for what changed since the last snapshot.
    pub fn lines(&mut self, snapshot: &LauncherStateSnapshot) -> Vec<String> {
        let mut lines = Vec::new();
        for session in &snapshot.sessions {
            let state = format!("{:?}", session.state);
            let error = session
                .error
                .clone()
                .filter(|error| !error.trim().is_empty());
            let now = (state.clone(), error.clone());
            let before = self.seen.insert(session.session_id.clone(), now);
            let (state_changed, error_changed) = match &before {
                None => (true, error.is_some()),
                Some((old_state, old_error)) => (*old_state != state, *old_error != error),
            };
            if state_changed {
                lines.push(format!("{}: {state}", describe(session)));
            }
            if error_changed && let Some(error) = &error {
                lines.push(format!("{}: {error}", describe(session)));
            }
        }
        lines
    }
}

/// Keeps the sessions' changes in `log` from now on. The orchestrator's
/// listener only passes a note to a thread of its own, which reads the
/// state and writes the lines, so nothing slow runs inside a state change.
pub fn follow_sessions(
    orchestrator: &Arc<dyn crate::window::Orchestration>,
    log: LauncherLog,
) -> std::io::Result<()> {
    let (sender, receiver) = std::sync::mpsc::channel::<()>();
    let weak = Arc::downgrade(orchestrator);
    std::thread::Builder::new()
        .name("launcher-session-log".into())
        .spawn(move || {
            let mut changes = SessionChanges::default();
            while receiver.recv().is_ok() {
                // Several changes in a row are read as one.
                while receiver.try_recv().is_ok() {}
                let Some(orchestrator) = weak.upgrade() else {
                    break;
                };
                if let Ok(snapshot) = orchestrator.snapshot() {
                    for line in changes.lines(&snapshot) {
                        log.line(&line);
                    }
                }
            }
        })?;
    let sender = Mutex::new(sender);
    orchestrator.subscribe(Arc::new(move || {
        let _ = sender
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .send(());
    }));
    Ok(())
}

/// `server / account / character (session id)`.
fn describe(session: &LauncherSessionSnapshot) -> String {
    let character = session
        .character_name
        .as_deref()
        .unwrap_or("(character select)");
    format!(
        "session {} / {} / {character} ({})",
        session.server_name, session.account_name, session.session_id
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp(name: &str) -> PathBuf {
        let path = std::env::temp_dir()
            .join("openac-launcher-log-tests")
            .join(format!("{name}-{}", crate::ids::unique_hex32()));
        std::fs::create_dir_all(&path).unwrap();
        path
    }

    #[test]
    fn lines_are_appended_with_the_local_time_across_starts() {
        let folder = temp("append");
        let path = folder.join("logs").join("launcher").join("launcher.log");
        LauncherLog::at(&path, ROLL_AT_BYTES, LocalZone::fixed(0)).line("first");
        LauncherLog::at(&path, ROLL_AT_BYTES, LocalZone::fixed(0)).line("second");
        let text = std::fs::read_to_string(&path).unwrap();
        let lines: Vec<&str> = text.lines().collect();
        assert_eq!(lines.len(), 2);
        assert!(lines[0].ends_with(" first"), "{}", lines[0]);
        assert!(lines[1].ends_with(" second"), "{}", lines[1]);
        // `yyyy-MM-dd HH:mm:ss text`
        let stamp = &lines[0][..19];
        assert_eq!(&stamp[4..5], "-");
        assert_eq!(&stamp[10..11], " ");
        assert_eq!(&stamp[13..14], ":");
        let _ = std::fs::remove_dir_all(&folder);
    }

    #[test]
    fn the_log_rolls_over_to_the_old_log_and_stays_bounded() {
        let folder = temp("roll");
        let path = folder.join("launcher.log");
        let log = LauncherLog::at(&path, 200, LocalZone::fixed(0));
        for index in 0..40 {
            log.line(&format!("line {index:02} of the launcher log"));
        }
        let current = std::fs::metadata(&path).unwrap().len();
        let old = std::fs::metadata(log.old_path()).unwrap().len();
        assert!(current <= 200, "{current}");
        assert!(old <= 200, "{old}");
        assert_eq!(log.old_path(), folder.join("launcher.old.log"));
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(text.contains("line 39"));
        let _ = std::fs::remove_dir_all(&folder);
    }
}
