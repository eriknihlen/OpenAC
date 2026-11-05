//! Launching a client session: the session-config document, the supervised
//! process, its capped error log, and its console text.

mod console_text;
mod document;
mod output_capture;
mod process;
mod supervisor;
#[cfg(windows)]
mod windows_console;

pub use console_text::{
    SessionConsoleLine, SessionConsoleRun, TYPED_PREFIX, color_for_tag, parse_line, read_line,
};
pub use document::{
    ComposeError, ComposedSessionConfig, LauncherSessionConfigService, STANDARD_INPUT_REFERENCE,
    SessionConfigService, compose, compose_probe, serialize, write,
};
pub use output_capture::{
    BoundedProcessOutputCapture, CaptureError, DEFAULT_MAX_BYTES, DEFAULT_MAX_FILES,
};
pub use process::{
    ChildProcess, ChildProcessFactory, ExitCallback, LauncherProcessSpec, SystemChildProcess,
    SystemChildProcessFactory, request_console_stop, spawn_stoppable,
};
pub use supervisor::{
    LauncherProcessSupervisor, LauncherProcessSupervisorFactory, LauncherSessionState,
    ProcessSupervisor, ProcessSupervisorFactory, StateListener, StopOutcome, SupervisorError,
};

/// The variables every client and windowless host the launcher starts gets,
/// so a child resolves exactly the folders the launcher itself uses rather
/// than guessing from a pointer file or whatever the shell exported: the
/// root, and each member spelled out so none can drift. It also asks the client
/// for `heartbeat` status lines, which this launcher reads to tell a client
/// that stopped responding, and for console lines marked with their kind, so
/// a session's console can show only what a player would see.
pub fn child_environment(paths: &ac_host_core::PathSet) -> Vec<(String, String)> {
    use ac_host_core::paths::{CACHE_DIR_VAR, CONFIG_DIR_VAR, DATA_DIR_VAR, ROOT_DIR_VAR};
    let text = |path: &std::path::Path| path.to_string_lossy().into_owned();
    vec![
        (ROOT_DIR_VAR.to_owned(), text(paths.root_directory())),
        (CONFIG_DIR_VAR.to_owned(), text(&paths.config_directory)),
        (DATA_DIR_VAR.to_owned(), text(&paths.data_directory)),
        (CACHE_DIR_VAR.to_owned(), text(&paths.cache_directory)),
        (ac_host_core::HEARTBEAT_VARIABLE.to_owned(), "1".to_owned()),
        (
            ac_host_core::console_kind::CONSOLE_KINDS_VARIABLE.to_owned(),
            "1".to_owned(),
        ),
    ]
}
