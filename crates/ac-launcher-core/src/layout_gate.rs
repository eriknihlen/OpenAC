//! The launcher's start up to the install being in the current folder
//! layout ([`ac_host_core::layout`]): it moves the files first, before it reads
//! any of them or starts a client. While clients of the install run it
//! waits for them to close (they hold the install's session lock); when a
//! move fails it says so and starts nothing. Once moved, logs older than a
//! week are cleared out.

use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime};

use ac_host_core::PathSet;
use ac_host_core::layout::{MigrationError, clean_old_logs, migrate};
use ac_host_core::local_time::LocalZone;

use crate::launcher_log::LauncherLog;

/// How long the launcher waits between tries while clients run.
pub const RETRY_EVERY: Duration = Duration::from_secs(1);

/// Where the start stands.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LayoutGateState {
    /// The install is in the current layout; the launcher may go on.
    Ready,
    /// The files are being moved.
    Moving,
    /// Clients of the install run; `clients` is how many this computer
    /// shows, when it can tell.
    Waiting {
        /// The running clients counted, if they could be.
        clients: Option<usize>,
    },
    /// A move failed; the text says what to do. The launcher starts no
    /// client and tries again at its next start.
    Failed(String),
}

impl LayoutGateState {
    /// The line the window shows while it is not ready.
    pub fn message(&self) -> Option<String> {
        match self {
            Self::Ready => None,
            Self::Moving => Some(MOVING_MESSAGE.to_owned()),
            Self::Waiting { clients } => Some(waiting_message(*clients)),
            Self::Failed(message) => Some(message.clone()),
        }
    }
}

/// What the window says while the files are moved.
pub const MOVING_MESSAGE: &str = "Moving your files to the new layout…";

/// "Waiting for N clients to close before moving files".
pub fn waiting_message(clients: Option<usize>) -> String {
    match clients {
        Some(1) => "Waiting for 1 client to close before moving files".to_owned(),
        Some(count) if count > 1 => {
            format!("Waiting for {count} clients to close before moving files")
        }
        _ => "Waiting for the running clients to close before moving files".to_owned(),
    }
}

/// The move, run on a thread of its own and, while clients run (or another
/// program is moving the files), tried again every [`RETRY_EVERY`]; a
/// failed move is tried again when the player asks ([`Self::retry`]). Once
/// stopped ([`Self::stop`]) no try starts, and the one running finishes.
#[derive(Clone)]
pub struct LayoutGate {
    inner: Arc<GateInner>,
}

struct GateInner {
    paths: PathSet,
    log: LauncherLog,
    count_clients: Arc<dyn Fn() -> Option<usize> + Send + Sync>,
    state: Mutex<LayoutGateState>,
    tries: Mutex<Tries>,
}

/// Whether a try runs, and whether more may start.
#[derive(Debug, Default)]
struct Tries {
    running: bool,
    stopped: bool,
}

impl std::fmt::Debug for LayoutGate {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LayoutGate")
            .field("state", &self.state())
            .finish_non_exhaustive()
    }
}

impl LayoutGate {
    /// Starts the move on a thread of its own and returns at once: a
    /// window can show it while it runs ([`LayoutGateState::Moving`]).
    /// An install already in the current layout is ready at once (its old
    /// logs are cleared out in the background). While clients hold the
    /// move off, it is tried again every [`RETRY_EVERY`]. `count_clients`
    /// counts the running clients for the waiting line.
    pub fn begin(
        paths: &PathSet,
        log: &LauncherLog,
        count_clients: Arc<dyn Fn() -> Option<usize> + Send + Sync>,
    ) -> Self {
        let current = ac_host_core::layout::is_current(paths);
        let inner = Arc::new(GateInner {
            paths: paths.clone(),
            log: log.clone(),
            count_clients,
            state: Mutex::new(if current {
                LayoutGateState::Ready
            } else {
                LayoutGateState::Moving
            }),
            tries: Mutex::new(Tries::default()),
        });
        let gate = Self { inner };
        if current {
            let paths = paths.clone();
            let log = log.clone();
            let _ = std::thread::Builder::new()
                .name("launcher-log-cleanup".into())
                .spawn(move || clean_out(&paths, &log));
        } else {
            gate.keep_trying(false);
        }
        gate
    }

    /// Where the start stands now.
    pub fn state(&self) -> LayoutGateState {
        self.inner.lock().clone()
    }

    /// Starts no further try; true while one is still running (the caller
    /// waits for [`Self::is_running`] to turn false before it exits, so
    /// the launcher never ends in the middle of a move).
    pub fn stop(&self) -> bool {
        let mut tries = self.inner.tries();
        tries.stopped = true;
        tries.running
    }

    /// Whether a try is running now.
    pub fn is_running(&self) -> bool {
        self.inner.tries().running
    }

    /// Tries a failed move again, in the background; does nothing unless
    /// the move failed (nor once the gate is stopped).
    pub fn retry(&self) {
        {
            let tries = self.inner.tries();
            if tries.stopped || tries.running {
                return;
            }
            let mut state = self.inner.lock();
            if !matches!(*state, LayoutGateState::Failed(_)) {
                return;
            }
            *state = LayoutGateState::Moving;
        }
        self.inner.log.line("layout: trying the move again");
        self.keep_trying(false);
    }

    /// Tries on a thread of its own until the move is done or fails,
    /// waiting [`RETRY_EVERY`] first when `pause_first`; no try starts once
    /// the gate is stopped.
    fn keep_trying(&self, pause_first: bool) {
        let inner = Arc::clone(&self.inner);
        let started = std::thread::Builder::new()
            .name("launcher-layout".into())
            .spawn(move || {
                let mut pause = pause_first;
                let mut logged_waiting = false;
                loop {
                    if pause {
                        std::thread::sleep(RETRY_EVERY);
                    }
                    pause = true;
                    {
                        let mut tries = inner.tries();
                        if tries.stopped {
                            break;
                        }
                        tries.running = true;
                    }
                    let next = inner.attempt();
                    let waiting = matches!(next, LayoutGateState::Waiting { .. });
                    if let LayoutGateState::Waiting { clients } = next
                        && !logged_waiting
                    {
                        inner.log.line(&waiting_message(clients));
                        logged_waiting = true;
                    }
                    *inner.lock() = next;
                    inner.tries().running = false;
                    if !waiting {
                        break;
                    }
                }
            });
        if let Err(error) = started {
            let message = format!("The launcher could not move its files: {error}");
            self.inner.log.say(&message);
            *self.inner.lock() = LayoutGateState::Failed(message);
        }
    }
}

/// Clears out the logs older than a week, saying what could not go.
fn clean_out(paths: &PathSet, log: &LauncherLog) {
    for failure in clean_old_logs(paths, SystemTime::now(), &LocalZone::system()) {
        log.line(&format!("cleanup: {failure}"));
    }
}

impl GateInner {
    fn lock(&self) -> std::sync::MutexGuard<'_, LayoutGateState> {
        self.state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    fn tries(&self) -> std::sync::MutexGuard<'_, Tries> {
        self.tries
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    /// One try: the move, then (once moved) the cleanup.
    fn attempt(&self) -> LayoutGateState {
        let (paths, log) = (&self.paths, &self.log);
        let zone = LocalZone::system();
        match migrate(paths, SystemTime::now(), &zone) {
            Ok(migration) => {
                if migration.moved {
                    log.line("layout: the install is in the current folder layout");
                }
                for note in &migration.notes {
                    log.line(&format!("layout: {note}"));
                }
                for failure in clean_old_logs(paths, SystemTime::now(), &zone) {
                    log.line(&format!("cleanup: {failure}"));
                }
                LayoutGateState::Ready
            }
            // A client moving the files at the same moment is waited for
            // like the clients that hold them.
            Err(MigrationError::Busy | MigrationError::Moving) => LayoutGateState::Waiting {
                clients: (self.count_clients)(),
            },
            Err(error) => {
                if let MigrationError::Failed { notes, .. } = &error {
                    for note in notes {
                        log.line(&format!("layout: {note}"));
                    }
                }
                log.line(&format!("layout: {error}"));
                let message = error.message(paths);
                log.say(&message);
                LayoutGateState::Failed(message)
            }
        }
    }
}

/// How many clients run on this computer (processes of the client
/// program, `openac`), or `None` where that cannot be asked.
pub fn running_clients() -> Option<usize> {
    #[cfg(windows)]
    {
        windows::running_clients()
    }
    #[cfg(target_os = "linux")]
    {
        linux::running_clients()
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        None
    }
}

#[cfg(windows)]
#[allow(unsafe_code)]
mod windows {
    use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW,
        TH32CS_SNAPPROCESS,
    };

    pub(super) fn running_clients() -> Option<usize> {
        // SAFETY: a snapshot of the process list; the handle is checked and
        // closed below.
        let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
        if snapshot == INVALID_HANDLE_VALUE {
            return None;
        }
        let mut entry = PROCESSENTRY32W {
            dwSize: u32::try_from(std::mem::size_of::<PROCESSENTRY32W>()).unwrap_or(0),
            ..PROCESSENTRY32W::default()
        };
        let mut count = 0;
        // SAFETY: `entry` is a valid, writable PROCESSENTRY32W with its size
        // set, as the call requires.
        let mut more = unsafe { Process32FirstW(snapshot, &raw mut entry) } != 0;
        while more {
            let length = entry
                .szExeFile
                .iter()
                .position(|&unit| unit == 0)
                .unwrap_or(entry.szExeFile.len());
            let name = String::from_utf16_lossy(&entry.szExeFile[..length]);
            if name.eq_ignore_ascii_case("openac.exe") {
                count += 1;
            }
            // SAFETY: as above.
            more = unsafe { Process32NextW(snapshot, &raw mut entry) } != 0;
        }
        // SAFETY: the snapshot handle is open and closed once.
        unsafe { CloseHandle(snapshot) };
        Some(count)
    }
}

#[cfg(target_os = "linux")]
mod linux {
    pub(super) fn running_clients() -> Option<usize> {
        let entries = std::fs::read_dir("/proc").ok()?;
        Some(
            entries
                .flatten()
                .filter(|entry| {
                    entry
                        .file_name()
                        .to_str()
                        .is_some_and(|name| name.bytes().all(|b| b.is_ascii_digit()))
                })
                .filter(|entry| {
                    std::fs::read_to_string(entry.path().join("comm"))
                        .is_ok_and(|name| name.trim_end() == "openac")
                })
                .count(),
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_host_core::{InstallSessionLease, RootSource};

    fn root(name: &str) -> (std::path::PathBuf, PathSet) {
        let path = std::env::temp_dir()
            .join("openac-layout-gate-tests")
            .join(format!("{name}-{}", crate::ids::unique_hex32()));
        std::fs::create_dir_all(&path).unwrap();
        let paths = PathSet::for_root(&path, RootSource::Explicit);
        (path, paths)
    }

    /// The gate's state once its first try has run.
    fn settled(gate: &LayoutGate) -> LayoutGateState {
        let deadline = std::time::Instant::now() + Duration::from_secs(20);
        while gate.state() == LayoutGateState::Moving && std::time::Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        gate.state()
    }

    #[test]
    fn the_waiting_line_counts_the_clients() {
        assert_eq!(
            waiting_message(Some(3)),
            "Waiting for 3 clients to close before moving files"
        );
        assert_eq!(
            waiting_message(Some(1)),
            "Waiting for 1 client to close before moving files"
        );
        assert_eq!(
            waiting_message(None),
            "Waiting for the running clients to close before moving files"
        );
    }

    #[test]
    fn the_launcher_waits_for_running_clients_then_moves_the_files() {
        let (folder, paths) = root("wait");
        std::fs::create_dir_all(paths.root_directory().join("vtank")).unwrap();
        let log = LauncherLog::open(&paths);
        let client = InstallSessionLease::try_acquire_shared(&paths)
            .unwrap()
            .unwrap();
        let gate = LayoutGate::begin(&paths, &log, Arc::new(|| Some(2)));
        assert_eq!(
            settled(&gate),
            LayoutGateState::Waiting { clients: Some(2) }
        );
        assert_eq!(
            gate.state().message().unwrap(),
            "Waiting for 2 clients to close before moving files"
        );
        assert!(paths.root_directory().join("vtank").is_dir());
        drop(client);
        let deadline = std::time::Instant::now() + Duration::from_secs(20);
        while gate.state() != LayoutGateState::Ready && std::time::Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(50));
        }
        assert_eq!(gate.state(), LayoutGateState::Ready);
        assert!(paths.vtank_profiles_directory().is_dir());
        assert!(ac_host_core::layout::is_current(&paths));
        let text = std::fs::read_to_string(paths.launcher_log_file()).unwrap();
        assert!(text.contains("Waiting for 2 clients"), "{text}");
        let _ = std::fs::remove_dir_all(&folder);
    }

    #[test]
    fn the_first_try_runs_off_the_calling_thread() {
        let (folder, paths) = root("off-thread");
        std::fs::create_dir_all(paths.root_directory().join("vtank")).unwrap();
        let log = LauncherLog::open(&paths);
        let client = InstallSessionLease::try_acquire_shared(&paths)
            .unwrap()
            .unwrap();
        // A try that takes a while (here: counting the clients) must not
        // hold up the window that shows it.
        let started = std::time::Instant::now();
        let gate = LayoutGate::begin(
            &paths,
            &log,
            Arc::new(|| {
                std::thread::sleep(Duration::from_secs(2));
                Some(1)
            }),
        );
        assert!(
            started.elapsed() < Duration::from_secs(1),
            "{:?}",
            started.elapsed()
        );
        assert_eq!(gate.state(), LayoutGateState::Moving);
        assert_eq!(gate.state().message().unwrap(), MOVING_MESSAGE);
        let deadline = std::time::Instant::now() + Duration::from_secs(20);
        while gate.state() == LayoutGateState::Moving && std::time::Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(50));
        }
        assert_eq!(gate.state(), LayoutGateState::Waiting { clients: Some(1) });
        drop(client);
        let _ = gate.stop();
        while gate.is_running() {
            std::thread::sleep(Duration::from_millis(50));
        }
        let _ = std::fs::remove_dir_all(&folder);
    }

    #[test]
    fn a_stopped_gate_finishes_its_running_try_and_starts_no_other() {
        let (folder, paths) = root("stop");
        std::fs::create_dir_all(paths.root_directory().join("vtank")).unwrap();
        let log = LauncherLog::open(&paths);
        let client = InstallSessionLease::try_acquire_shared(&paths)
            .unwrap()
            .unwrap();
        let gate = LayoutGate::begin(
            &paths,
            &log,
            Arc::new(|| {
                std::thread::sleep(Duration::from_millis(800));
                Some(1)
            }),
        );
        std::thread::sleep(Duration::from_millis(200));
        // The window closes while a try runs: it is told so, and waits.
        assert!(gate.stop(), "a try was running");
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        while gate.is_running() && std::time::Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        assert!(!gate.is_running());
        // Free to move now, but no try starts after the stop.
        drop(client);
        std::thread::sleep(RETRY_EVERY * 3);
        assert!(!ac_host_core::layout::is_current(&paths));
        assert!(!gate.stop());
        let _ = std::fs::remove_dir_all(&folder);
    }

    #[test]
    fn another_program_moving_the_files_is_waited_for_not_a_failure() {
        let (folder, paths) = root("moving");
        std::fs::create_dir_all(paths.root_directory()).unwrap();
        let lock = std::fs::OpenOptions::new()
            .create(true)
            .truncate(false)
            .write(true)
            .open(paths.layout_lock_file())
            .unwrap();
        lock.lock().unwrap();
        let log = LauncherLog::open(&paths);
        let gate = LayoutGate::begin(&paths, &log, Arc::new(|| Some(0)));
        assert!(
            matches!(settled(&gate), LayoutGateState::Waiting { .. }),
            "{:?}",
            gate.state()
        );
        drop(lock);
        let deadline = std::time::Instant::now() + Duration::from_secs(20);
        while gate.state() != LayoutGateState::Ready && std::time::Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(50));
        }
        assert_eq!(gate.state(), LayoutGateState::Ready);
        let _ = std::fs::remove_dir_all(&folder);
    }

    #[test]
    fn a_failed_move_can_be_tried_again() {
        let (folder, paths) = root("retry");
        // The marker cannot be written while a folder has its name.
        std::fs::create_dir_all(paths.layout_marker_file()).unwrap();
        let log = LauncherLog::open(&paths);
        let gate = LayoutGate::begin(&paths, &log, Arc::new(|| None));
        let LayoutGateState::Failed(message) = settled(&gate) else {
            panic!("{:?}", gate.state());
        };
        assert!(message.contains("layout.json"), "{message}");
        std::fs::remove_dir(paths.layout_marker_file()).unwrap();
        gate.retry();
        let deadline = std::time::Instant::now() + Duration::from_secs(20);
        while gate.state() != LayoutGateState::Ready && std::time::Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(50));
        }
        assert_eq!(gate.state(), LayoutGateState::Ready);
        assert!(ac_host_core::layout::is_current(&paths));
        let _ = std::fs::remove_dir_all(&folder);
    }

    #[test]
    fn a_current_install_is_ready_at_once() {
        let (folder, paths) = root("ready");
        let log = LauncherLog::open(&paths);
        let gate = LayoutGate::begin(&paths, &log, Arc::new(|| None));
        assert_eq!(settled(&gate), LayoutGateState::Ready);
        let again = LayoutGate::begin(&paths, &log, Arc::new(|| None));
        assert_eq!(again.state(), LayoutGateState::Ready);
        let _ = std::fs::remove_dir_all(&folder);
    }
}
