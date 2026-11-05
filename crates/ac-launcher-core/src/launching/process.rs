//! Starting and stopping a client process: the spec, the child-process
//! abstraction the supervisor drives, and the real implementation on
//! `std::process`.
//!
//! A child that supports a console stop is started in its own process group
//! (Windows) so a Ctrl+Break reaches only it, or is sent SIGINT (Unix); the
//! client treats either as "log off gracefully and exit".

use std::io::{self, Read, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use super::output_capture::BoundedProcessOutputCapture;

/// What to start. Carries no credential: the password goes through
/// standard input only.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct LauncherProcessSpec {
    /// The program.
    pub executable_path: PathBuf,
    /// Its arguments.
    pub arguments: Vec<String>,
    /// Its working directory.
    pub working_directory: Option<PathBuf>,
    /// Environment variables set for it.
    pub environment: Vec<(String, String)>,
    /// Whether a console stop (Ctrl+Break / SIGINT) asks it to exit.
    pub supports_console_graceful_stop: bool,
    /// Where its standard output and error are captured together, capped.
    pub output_log_path: Option<PathBuf>,
    /// Keep standard input open after the password (a console session
    /// reads typed lines from it for as long as it runs).
    pub keep_standard_input_open: bool,
}

impl LauncherProcessSpec {
    /// A spec with a console stop and nothing else set.
    pub fn new(executable_path: impl Into<PathBuf>, arguments: Vec<String>) -> Self {
        Self {
            executable_path: executable_path.into(),
            arguments,
            supports_console_graceful_stop: true,
            ..Self::default()
        }
    }
}

/// Called once, from another thread, when the child exits; carries the
/// exit code when there is one.
pub type ExitCallback = Box<dyn FnOnce(Option<i32>) + Send>;

/// A child process as the supervisor sees it. Every method is safe to call
/// from any thread.
pub trait ChildProcess: Send + Sync {
    /// Starts it; `on_exit` runs once when it ends.
    fn start(&self, on_exit: ExitCallback) -> io::Result<()>;
    /// True once the child exists, even if later start-up setup failed.
    fn has_started(&self) -> bool {
        false
    }
    /// Whether it has ended.
    fn has_exited(&self) -> bool;
    /// Its exit code, once ended.
    fn exit_code(&self) -> Option<i32>;
    /// Writes to its standard input.
    fn write_input(&self, bytes: &[u8]) -> io::Result<()>;
    /// Closes its standard input.
    fn close_input(&self);
    /// Sends the console stop; false when unsupported or it failed.
    fn try_request_graceful_stop(&self) -> bool;
    /// Asks its main window to close; false when it has none.
    fn close_main_window(&self) -> bool;
    /// Ends it now.
    fn kill(&self) -> io::Result<()>;
    /// Waits for it to end; `None` waits forever. True when it ended.
    fn wait_for_exit(&self, timeout: Option<Duration>) -> bool;
}

/// Creates child processes.
pub trait ChildProcessFactory: Send + Sync {
    /// A child for `spec`, not yet started.
    fn create(&self, spec: &LauncherProcessSpec) -> Arc<dyn ChildProcess>;
}

/// Creates real processes.
#[derive(Debug, Clone, Copy, Default)]
pub struct SystemChildProcessFactory;

impl ChildProcessFactory for SystemChildProcessFactory {
    fn create(&self, spec: &LauncherProcessSpec) -> Arc<dyn ChildProcess> {
        Arc::new(SystemChildProcess::new(spec.clone()))
    }
}

#[derive(Debug, Default)]
struct ExitState {
    exited: bool,
    code: Option<i32>,
}

/// A real process on `std::process`.
pub struct SystemChildProcess {
    spec: LauncherProcessSpec,
    child: Arc<Mutex<Option<Child>>>,
    stdin: Mutex<Option<ChildStdin>>,
    pid: Mutex<Option<u32>>,
    exit: Arc<(Mutex<ExitState>, Condvar)>,
}

impl std::fmt::Debug for SystemChildProcess {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SystemChildProcess")
            .field("spec", &self.spec)
            .finish_non_exhaustive()
    }
}

fn lock<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
}

/// How often a child's exit is checked.
const EXIT_POLL: Duration = Duration::from_millis(20);

impl SystemChildProcess {
    /// A process for `spec`, not yet started.
    pub fn new(spec: LauncherProcessSpec) -> Self {
        Self {
            spec,
            child: Arc::new(Mutex::new(None)),
            stdin: Mutex::new(None),
            pid: Mutex::new(None),
            exit: Arc::new((Mutex::new(ExitState::default()), Condvar::new())),
        }
    }

    fn command(&self) -> Command {
        let mut command = Command::new(&self.spec.executable_path);
        command.args(&self.spec.arguments).stdin(Stdio::piped());
        if self.spec.output_log_path.is_some() {
            command.stdout(Stdio::piped()).stderr(Stdio::piped());
        } else {
            command.stdout(Stdio::inherit()).stderr(Stdio::inherit());
        }
        for (name, value) in &self.spec.environment {
            command.env(name, value);
        }
        if let Some(directory) = &self.spec.working_directory {
            command.current_dir(directory);
        }
        command
    }

    fn spawn(&self) -> io::Result<Child> {
        let mut command = self.command();
        if self.spec.supports_console_graceful_stop {
            return spawn_stoppable(&mut command);
        }
        command.spawn()
    }
}

/// Starts `command` so that [`request_console_stop`] reaches it and only
/// it: on Windows in its own process group, attached to a console (a
/// hidden one is borrowed while it starts when this process has none); on
/// Unix as is. The launcher starts its clients this way.
pub fn spawn_stoppable(command: &mut Command) -> io::Result<Child> {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;
        command.creation_flags(CREATE_NEW_PROCESS_GROUP);
        // A console stop reaches only processes sharing a console with the
        // sender, so the child must start attached to one.
        super::windows_console::with_console(|| command.spawn())
    }
    #[cfg(not(windows))]
    {
        command.spawn()
    }
}

/// Sends the launcher's console stop to a child started by
/// [`spawn_stoppable`]: Ctrl+Break to its process group on Windows, SIGINT
/// on Unix. False when it could not be sent.
pub fn request_console_stop(pid: u32) -> bool {
    send_console_stop(pid)
}

/// The most a line may grow before it is written without its end, so a
/// stream that never ends a line cannot hold memory without bound.
const LONGEST_HELD_LINE: usize = 64 * 1024;

/// Copies `stream` into `capture` on a thread of its own, a whole line at a
/// time (a line longer than [`LONGEST_HELD_LINE`] in pieces), until the
/// stream ends; without a capture the stream is read and dropped.
fn spawn_line_pump(
    name: &str,
    mut stream: impl Read + Send + 'static,
    capture: Option<Arc<BoundedProcessOutputCapture>>,
) -> io::Result<()> {
    let append = move |bytes: &[u8]| {
        if let Some(capture) = &capture {
            capture.append(bytes);
        }
    };
    std::thread::Builder::new()
        .name(name.into())
        .spawn(move || {
            let mut buffer = [0u8; 4096];
            let mut pending: Vec<u8> = Vec::new();
            while let Ok(read) = stream.read(&mut buffer) {
                if read == 0 {
                    break;
                }
                pending.extend_from_slice(&buffer[..read]);
                if let Some(last_break) = pending.iter().rposition(|&byte| byte == b'\n') {
                    append(&pending[..=last_break]);
                    pending.drain(..=last_break);
                } else if pending.len() > LONGEST_HELD_LINE {
                    append(&pending);
                    pending.clear();
                }
            }
            append(&pending);
        })?;
    Ok(())
}

impl ChildProcess for SystemChildProcess {
    fn start(&self, on_exit: ExitCallback) -> io::Result<()> {
        let mut slot = lock(&self.child);
        if slot.is_some() {
            return Err(io::Error::other("The child process already started."));
        }
        let mut child = self.spawn()?;
        *lock(&self.pid) = Some(child.id());
        *lock(&self.stdin) = child.stdin.take();
        let stdout = child.stdout.take();
        let stderr = child.stderr.take();
        // Kept before any further setup, so a kill still reaches a child
        // whose setup fails.
        *slot = Some(child);
        drop(slot);
        // Both streams go to the one log, whole lines at a time, so a line of
        // one never lands inside a line of the other. They are read to their
        // end even when the log cannot be made: a pipe nobody reads would
        // stall the child, or break its writes once closed.
        let capture = self
            .spec
            .output_log_path
            .as_deref()
            .and_then(|path| BoundedProcessOutputCapture::with_default_cap(path).ok())
            .map(Arc::new);
        if let Some(stdout) = stdout {
            spawn_line_pump("launcher-stdout-pump", stdout, capture.clone())?;
        }
        if let Some(stderr) = stderr {
            spawn_line_pump("launcher-stderr-pump", stderr, capture)?;
        }

        // The exit watcher: polls so `kill` can still reach the child.
        let exit = Arc::clone(&self.exit);
        let child_slot = ChildSlot(Arc::clone(&self.child));
        std::thread::Builder::new()
            .name("launcher-exit-watcher".into())
            .spawn(move || {
                let code = loop {
                    match child_slot.try_wait() {
                        Some(code) => break code,
                        None => std::thread::sleep(EXIT_POLL),
                    }
                };
                {
                    let (state, signal) = &*exit;
                    let mut state = lock(state);
                    state.exited = true;
                    state.code = code;
                    signal.notify_all();
                }
                on_exit(code);
            })?;
        Ok(())
    }

    fn has_started(&self) -> bool {
        lock(&self.pid).is_some()
    }

    fn has_exited(&self) -> bool {
        lock(&self.exit.0).exited
    }

    fn exit_code(&self) -> Option<i32> {
        lock(&self.exit.0).code
    }

    fn write_input(&self, bytes: &[u8]) -> io::Result<()> {
        match lock(&self.stdin).as_mut() {
            Some(stdin) => {
                stdin.write_all(bytes)?;
                stdin.flush()
            }
            None => Err(io::Error::new(
                io::ErrorKind::BrokenPipe,
                "The child's input is closed.",
            )),
        }
    }

    fn close_input(&self) {
        lock(&self.stdin).take();
    }

    fn try_request_graceful_stop(&self) -> bool {
        if !self.spec.supports_console_graceful_stop || self.has_exited() {
            return false;
        }
        let Some(pid) = *lock(&self.pid) else {
            return false;
        };
        send_console_stop(pid)
    }

    fn close_main_window(&self) -> bool {
        // The client stops on the console stop in both modes; no window
        // message is sent (a deliberate divergence from OpenAC).
        false
    }

    fn kill(&self) -> io::Result<()> {
        match lock(&self.child).as_mut() {
            Some(child) if !self.has_exited() => child.kill(),
            _ => Ok(()),
        }
    }

    fn wait_for_exit(&self, timeout: Option<Duration>) -> bool {
        let (state, signal) = &*self.exit;
        let deadline = timeout.map(|timeout| Instant::now() + timeout);
        let mut guard = lock(state);
        while !guard.exited {
            match deadline {
                None => {
                    guard = signal
                        .wait(guard)
                        .unwrap_or_else(std::sync::PoisonError::into_inner);
                }
                Some(deadline) => {
                    let now = Instant::now();
                    if now >= deadline {
                        return false;
                    }
                    guard = signal
                        .wait_timeout(guard, deadline - now)
                        .unwrap_or_else(std::sync::PoisonError::into_inner)
                        .0;
                }
            }
        }
        true
    }
}

/// The watcher thread's handle to the child.
struct ChildSlot(Arc<Mutex<Option<Child>>>);

impl ChildSlot {
    /// `Some(code)` once the child ended (`Some(None)` when it ended
    /// without a code or cannot be asked); `None` while it runs.
    fn try_wait(&self) -> Option<Option<i32>> {
        let mut guard = lock(&self.0);
        match guard.as_mut().map(Child::try_wait) {
            Some(Ok(Some(status))) => Some(status.code()),
            Some(Ok(None)) => None,
            Some(Err(_)) | None => Some(None),
        }
    }
}

#[cfg(unix)]
fn send_console_stop(pid: u32) -> bool {
    use nix::sys::signal::{Signal, kill};
    use nix::unistd::Pid;
    i32::try_from(pid).is_ok_and(|pid| kill(Pid::from_raw(pid), Signal::SIGINT).is_ok())
}

#[cfg(windows)]
fn send_console_stop(pid: u32) -> bool {
    super::windows_console::send_ctrl_break(pid)
}

#[cfg(not(any(unix, windows)))]
fn send_console_stop(_pid: u32) -> bool {
    false
}
