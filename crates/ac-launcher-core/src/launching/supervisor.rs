//! Supervising one client process: start it with the password on standard
//! input, report Starting / Running / Exited in order, send console lines,
//! and stop it gracefully (console stop, then window close, then a kill
//! after the timeout).

use std::collections::VecDeque;
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;

use super::process::{
    ChildProcess, ChildProcessFactory, LauncherProcessSpec, SystemChildProcessFactory,
};

/// Where a supervised process is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LauncherSessionState {
    /// Being started.
    Starting,
    /// Running.
    Running,
    /// Ended (terminal; nothing follows).
    Exited,
}

/// How a stop ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StopOutcome {
    /// The process ended by itself within the timeout, or was not running.
    Exited,
    /// It did not end within the timeout and was killed.
    Killed,
}

/// Why a supervisor operation failed.
#[derive(Debug, thiserror::Error)]
pub enum SupervisorError {
    /// This supervisor already owns a process.
    #[error("This supervisor already owns a process; start a new supervisor per launched session.")]
    AlreadyStarted,
    /// It was disposed.
    #[error("The supervisor was disposed.")]
    Disposed,
    /// Starting or feeding the process failed.
    #[error("{0}")]
    Io(#[from] std::io::Error),
    /// A killed process could not be seen to end.
    #[error("The launcher child could not be observed terminal after it was killed.")]
    NotTerminal,
    /// The child started, its setup failed, and it could not be killed: the
    /// supervisor still owns it.
    #[error("The client failed to start and could not be terminated.")]
    StartedButNotTerminated {
        /// Why the start failed.
        start: Box<SupervisorError>,
        /// Why the kill failed.
        stop: std::io::Error,
    },
}

/// Hears state changes, in order, exactly once each.
pub type StateListener = Arc<dyn Fn(LauncherSessionState) + Send + Sync>;

/// A supervisor as the orchestrator uses it (a fake in tests).
pub trait ProcessSupervisor: Send + Sync {
    /// The current state.
    fn state(&self) -> LauncherSessionState;
    /// The exit code, once exited.
    fn exit_code(&self) -> Option<i32>;
    /// Whether the owned process has started and has not actually exited
    /// (it can outlive a failed start until a kill takes effect).
    fn has_live_process(&self) -> bool {
        self.state() == LauncherSessionState::Running
    }
    /// Adds a state listener.
    fn subscribe(&self, listener: StateListener);
    /// Starts the process, writing `password` and a newline to its input.
    fn start(
        &self,
        spec: &LauncherProcessSpec,
        password: Option<&str>,
    ) -> Result<(), SupervisorError>;
    /// Stops it, waiting up to `timeout` before killing; says whether it
    /// had to kill.
    fn stop(&self, timeout: Duration) -> Result<StopOutcome, SupervisorError>;
    /// Sends one line to a console session's input; false when it takes none.
    fn try_send_line(&self, line: &str) -> bool;
    /// Stops the process (if running) and lets it go.
    fn dispose(&self);
}

/// Creates supervisors.
pub trait ProcessSupervisorFactory: Send + Sync {
    /// A new supervisor.
    fn create(&self) -> Arc<dyn ProcessSupervisor>;
}

/// Creates real supervisors.
pub struct LauncherProcessSupervisorFactory {
    children: Arc<dyn ChildProcessFactory>,
}

impl std::fmt::Debug for LauncherProcessSupervisorFactory {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("LauncherProcessSupervisorFactory")
    }
}

impl Default for LauncherProcessSupervisorFactory {
    fn default() -> Self {
        Self::new(Arc::new(SystemChildProcessFactory))
    }
}

impl LauncherProcessSupervisorFactory {
    /// Supervisors over `children`.
    pub fn new(children: Arc<dyn ChildProcessFactory>) -> Self {
        Self { children }
    }
}

impl ProcessSupervisorFactory for LauncherProcessSupervisorFactory {
    fn create(&self) -> Arc<dyn ProcessSupervisor> {
        LauncherProcessSupervisor::new(Arc::clone(&self.children))
    }
}

struct SupervisorState {
    process: Option<Arc<dyn ChildProcess>>,
    state: LauncherSessionState,
    exit_code: Option<i32>,
    disposed: bool,
    input_open: bool,
    process_started: bool,
    pending: VecDeque<LauncherSessionState>,
    publishing: bool,
}

/// The real supervisor.
pub struct LauncherProcessSupervisor {
    me: std::sync::Weak<Self>,
    factory: Arc<dyn ChildProcessFactory>,
    state: Mutex<SupervisorState>,
    listeners: Mutex<Vec<StateListener>>,
}

impl std::fmt::Debug for LauncherProcessSupervisor {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LauncherProcessSupervisor")
            .field("state", &self.state())
            .finish_non_exhaustive()
    }
}

/// How long dispose waits before killing.
const DISPOSE_STOP_TIMEOUT: Duration = Duration::from_secs(5);

impl LauncherProcessSupervisor {
    /// A supervisor creating its child through `factory`.
    pub fn new(factory: Arc<dyn ChildProcessFactory>) -> Arc<Self> {
        Arc::new_cyclic(|me| Self {
            me: me.clone(),
            factory,
            state: Mutex::new(SupervisorState {
                process: None,
                state: LauncherSessionState::Starting,
                exit_code: None,
                disposed: false,
                input_open: false,
                process_started: false,
                pending: VecDeque::new(),
                publishing: false,
            }),
            listeners: Mutex::new(Vec::new()),
        })
    }

    /// A supervisor over real processes.
    pub fn system() -> Arc<Self> {
        Self::new(Arc::new(SystemChildProcessFactory))
    }

    fn lock(&self) -> MutexGuard<'_, SupervisorState> {
        self.state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    /// Moves to `state` unless already exited; publishes in order, one
    /// publisher at a time (a listener that causes another change has it
    /// queued, not delivered re-entrantly).
    fn set_state(&self, state: LauncherSessionState, exit_code: Option<i32>) {
        let publish = {
            let mut guard = self.lock();
            if guard.state == LauncherSessionState::Exited {
                return;
            }
            guard.state = state;
            if state == LauncherSessionState::Exited {
                guard.exit_code = exit_code;
            }
            guard.pending.push_back(state);
            let publish = !guard.publishing;
            if publish {
                guard.publishing = true;
            }
            publish
        };
        if publish {
            self.publish_pending();
        }
    }

    fn publish_pending(&self) {
        loop {
            let state = {
                let mut guard = self.lock();
                match guard.pending.pop_front() {
                    Some(state) => state,
                    None => {
                        guard.publishing = false;
                        return;
                    }
                }
            };
            let listeners: Vec<StateListener> = self
                .listeners
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .clone();
            for listener in listeners {
                listener(state);
            }
        }
    }

    fn stop_process(
        process: &Arc<dyn ChildProcess>,
        timeout: Duration,
    ) -> Result<StopOutcome, SupervisorError> {
        process.try_request_graceful_stop();
        process.close_main_window();
        if process.wait_for_exit(Some(timeout)) || process.has_exited() {
            return Ok(StopOutcome::Exited);
        }
        process.kill()?;
        if !process.wait_for_exit(None) && !process.has_exited() {
            return Err(SupervisorError::NotTerminal);
        }
        Ok(StopOutcome::Killed)
    }
}

impl ProcessSupervisor for LauncherProcessSupervisor {
    fn state(&self) -> LauncherSessionState {
        LauncherProcessSupervisor::state(self)
    }

    fn exit_code(&self) -> Option<i32> {
        self.lock().exit_code
    }

    fn has_live_process(&self) -> bool {
        let guard = self.lock();
        guard.process_started
            && guard
                .process
                .as_ref()
                .is_some_and(|process| !process.has_exited())
    }

    fn subscribe(&self, listener: StateListener) {
        self.listeners
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .push(listener);
    }

    fn start(
        &self,
        spec: &LauncherProcessSpec,
        password: Option<&str>,
    ) -> Result<(), SupervisorError> {
        let process = {
            let mut guard = self.lock();
            if guard.disposed {
                return Err(SupervisorError::Disposed);
            }
            if guard.process.is_some() {
                return Err(SupervisorError::AlreadyStarted);
            }
            let process = self.factory.create(spec);
            guard.process = Some(Arc::clone(&process));
            process
        };
        self.set_state(LauncherSessionState::Starting, None);

        let weak = self.me.clone();
        let on_exit = Box::new(move |code: Option<i32>| {
            if let Some(supervisor) = weak.upgrade() {
                supervisor.set_state(LauncherSessionState::Exited, code);
            }
        });
        let mut started = false;
        let result = (|| -> Result<(), SupervisorError> {
            process.start(on_exit)?;
            started = true;
            self.lock().process_started = true;
            if let Some(password) = password {
                process.write_input(password.as_bytes())?;
                process.write_input(b"\n")?;
            }
            if spec.keep_standard_input_open {
                self.lock().input_open = true;
            } else {
                process.close_input();
            }
            Ok(())
        })();
        if let Err(error) = result {
            // A child can exist even when its start reported an error.
            started |= process.has_started();
            self.lock().process_started = started;
            if started && let Err(stop) = process.kill() {
                // Ownership stays while the child may still run: callers
                // must keep seeing a live process.
                return Err(SupervisorError::StartedButNotTerminated {
                    start: Box::new(error),
                    stop,
                });
            }
            // A killed child stays owned until it is seen to end.
            if !started || process.has_exited() {
                self.lock().process = None;
            }
            return Err(error);
        }
        self.set_state(LauncherSessionState::Running, None);
        Ok(())
    }

    fn stop(&self, timeout: Duration) -> Result<StopOutcome, SupervisorError> {
        let process = self.lock().process.clone();
        match process {
            Some(process) if !process.has_exited() => {
                LauncherProcessSupervisor::stop_process(&process, timeout)
            }
            _ => Ok(StopOutcome::Exited),
        }
    }

    fn try_send_line(&self, line: &str) -> bool {
        let process = {
            let guard = self.lock();
            if guard.disposed || !guard.input_open {
                return false;
            }
            match &guard.process {
                Some(process) if !process.has_exited() => Arc::clone(process),
                _ => return false,
            }
        };
        let sent = process
            .write_input(line.as_bytes())
            .and_then(|()| process.write_input(b"\n"));
        if sent.is_err() {
            // The process went away between the check and the write.
            self.lock().input_open = false;
            return false;
        }
        true
    }

    fn dispose(&self) {
        let process = {
            let mut guard = self.lock();
            if guard.disposed {
                return;
            }
            guard.disposed = true;
            guard.process.take()
        };
        if let Some(process) = process
            && !process.has_exited()
        {
            let _ = LauncherProcessSupervisor::stop_process(&process, DISPOSE_STOP_TIMEOUT);
        }
    }
}

impl LauncherProcessSupervisor {
    /// The current state.
    pub fn state(&self) -> LauncherSessionState {
        self.lock().state
    }
}
