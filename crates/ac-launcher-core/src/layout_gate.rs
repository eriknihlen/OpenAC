//! The launcher's start up to the install being in the current folder
//! layout ([`ac_host::layout`]): it moves the files first, before it reads
//! any of them or starts a client. While clients of the install run it
//! waits for them to close (they hold the install's session lock); when a
//! move fails it says so and starts nothing. Once moved, logs older than a
//! week are cleared out.

use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime};

use ac_host::PathSet;
use ac_host::layout::{MigrationError, clean_old_logs, migrate};
use ac_host::local_time::LocalZone;

use crate::launcher_log::LauncherLog;

/// How long the launcher waits between tries while clients run.
pub const RETRY_EVERY: Duration = Duration::from_secs(1);

/// Where the start stands.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LayoutGateState {
    /// The install is in the current layout; the launcher may go on.
    Ready,
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
            Self::Waiting { clients } => Some(waiting_message(*clients)),
            Self::Failed(message) => Some(message.clone()),
        }
    }
}

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

/// The move, tried at once and, while clients run, again every
/// [`RETRY_EVERY`] on a thread of its own.
#[derive(Debug, Clone)]
pub struct LayoutGate {
    state: Arc<Mutex<LayoutGateState>>,
}

impl LayoutGate {
    /// Tries the move now; when clients hold it off, keeps trying in the
    /// background. `count_clients` counts the running clients for the
    /// waiting line.
    pub fn begin(
        paths: &PathSet,
        log: &LauncherLog,
        count_clients: Arc<dyn Fn() -> Option<usize> + Send + Sync>,
    ) -> Self {
        let first = attempt(paths, log, count_clients.as_ref());
        let gate = Self {
            state: Arc::new(Mutex::new(first.clone())),
        };
        if let LayoutGateState::Waiting { clients } = first {
            log.line(&waiting_message(clients));
            let state = Arc::clone(&gate.state);
            let paths = paths.clone();
            let thread_log = log.clone();
            let started = std::thread::Builder::new()
                .name("launcher-layout".into())
                .spawn(move || {
                    loop {
                        std::thread::sleep(RETRY_EVERY);
                        let next = attempt(&paths, &thread_log, count_clients.as_ref());
                        let waiting = matches!(next, LayoutGateState::Waiting { .. });
                        *state
                            .lock()
                            .unwrap_or_else(std::sync::PoisonError::into_inner) = next;
                        if !waiting {
                            break;
                        }
                    }
                });
            if let Err(error) = started {
                let message = format!("The launcher could not wait for its clients: {error}");
                log.say(&message);
                *gate.lock() = LayoutGateState::Failed(message);
            }
        }
        gate
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, LayoutGateState> {
        self.state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    /// Where the start stands now.
    pub fn state(&self) -> LayoutGateState {
        self.lock().clone()
    }
}

/// One try: the move, then (once moved) the cleanup.
fn attempt(
    paths: &PathSet,
    log: &LauncherLog,
    count_clients: &dyn Fn() -> Option<usize>,
) -> LayoutGateState {
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
        Err(MigrationError::Busy) => LayoutGateState::Waiting {
            clients: count_clients(),
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
    use ac_host::{InstallSessionLease, RootSource};

    fn root(name: &str) -> (std::path::PathBuf, PathSet) {
        let path = std::env::temp_dir()
            .join("openac-layout-gate-tests")
            .join(format!("{name}-{}", crate::ids::unique_hex32()));
        std::fs::create_dir_all(&path).unwrap();
        let paths = PathSet::for_root(&path, RootSource::Explicit);
        (path, paths)
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
        assert_eq!(gate.state(), LayoutGateState::Waiting { clients: Some(2) });
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
        assert!(ac_host::layout::is_current(&paths));
        let text = std::fs::read_to_string(paths.launcher_log_file()).unwrap();
        assert!(text.contains("Waiting for 2 clients"), "{text}");
        let _ = std::fs::remove_dir_all(&folder);
    }

    #[test]
    fn a_current_install_is_ready_at_once() {
        let (folder, paths) = root("ready");
        let log = LauncherLog::open(&paths);
        let gate = LayoutGate::begin(&paths, &log, Arc::new(|| None));
        assert_eq!(gate.state(), LayoutGateState::Ready);
        let again = LayoutGate::begin(&paths, &log, Arc::new(|| None));
        assert_eq!(again.state(), LayoutGateState::Ready);
        let _ = std::fs::remove_dir_all(&folder);
    }
}
