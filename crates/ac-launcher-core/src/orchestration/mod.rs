//! The launcher's one owner of launch state: profiles, the install record,
//! and every running session or character refresh, with what may be started
//! when, and the read-only snapshots the window draws from.

mod capabilities;
mod executables;
mod exit_reason;
mod orchestrator;
mod relaunch;
mod snapshot;
mod stop_gate;

pub use capabilities::{
    LauncherCapability, LauncherPlatformCapabilities,
    UNSUPPORTED_PLATFORM_GRAPHICAL_LAUNCH_DISABLED_REASON,
};
pub use executables::{
    ExecutablePaths, HEADLESS_ENVIRONMENT_VARIABLE, LauncherExecutableSet, PathPredicate,
};
pub use exit_reason::{ClientExit, describe_client_exit};
pub use orchestrator::{
    AccountCapabilities, Clock, FIRST_RUN_REQUIRED, LauncherOrchestrator, NOT_RESPONDING_AFTER,
    OrchestratorError, OrchestratorParts, SERVER_SESSION_HOLD, SessionIdFactory,
    StateChangedListener, WINDOW_SWITCH_TIMEOUT, create_session_id,
};
pub use relaunch::{
    DEFAULT_RELAUNCH_DELAY_SECONDS, LauncherRelaunchController, LauncherRelaunchSelection,
    MAXIMUM_REFUSED_RELAUNCHES, MAXIMUM_RELAUNCH_DELAY_SECONDS, MINIMUM_RELAUNCH_DELAY_SECONDS,
    MonotonicClock, RelaunchAttempt, RelaunchDelayOutOfRange,
};
pub use snapshot::{
    LauncherAccountSnapshot, LauncherActivityKind, LauncherActivityState,
    LauncherCharacterSnapshot, LauncherServerSnapshot, LauncherSessionSnapshot,
    LauncherSessionStop, LauncherSessionWindow, LauncherStateSnapshot,
};
pub use stop_gate::LauncherStopGate;
