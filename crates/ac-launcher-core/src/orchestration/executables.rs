//! Where the client executable is and how each kind of session starts it.
//!
//! There is one client program, `openac`. A play session with a window runs
//! `openac --session-config <file> --console`; a windowless session runs
//! `openac run --config <file> --console`. Both keep their console on and
//! their input open, so the launcher can talk to them: its console window
//! types lines, and its row switches a running session between headless and
//! graphical with `/window <id>` and `/close` (both keep window support for
//! that). A character refresh runs
//! `openac run --config <file>` with `OPENAC_HEADLESS=1`.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use super::capabilities::LauncherCapability;
use super::orchestrator::OrchestratorError;
use crate::fsutil;
use crate::launching::LauncherProcessSpec;
use crate::profiles::LaunchMode;
use crate::updates::{
    ClientVersionResolution, ClientVersionStore, detect_rid, graphical_host_for_rid,
    headless_host_for_rid, predates_the_folder_layout, predates_the_folder_layout_message,
    suffix_for_current_os,
};

/// The variable that makes `openac run` start without window support.
pub const HEADLESS_ENVIRONMENT_VARIABLE: &str = "OPENAC_HEADLESS";

/// A yes/no question about a file (injectable in tests).
pub type PathPredicate = Arc<dyn Fn(&Path) -> bool + Send + Sync>;

type Resolver = Arc<dyn Fn() -> Result<ExecutablePaths, String> + Send + Sync>;

/// An installed client older than the install's folder layout is never
/// started: it would read and write the folders the layout moved.
fn refuse_old_client(resolution: &ClientVersionResolution) -> Result<(), String> {
    match resolution.version.as_ref() {
        Some(version) if resolution.is_verified() && predates_the_folder_layout(version) => {
            Err(predates_the_folder_layout_message(version))
        }
        _ => Ok(()),
    }
}

/// The executables and the directory they run in.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ExecutablePaths {
    /// What a session with a window runs.
    pub graphical_host_path: PathBuf,
    /// What a windowless session and a character refresh run.
    pub headless_host_path: PathBuf,
    /// The working directory, when one is set.
    pub working_directory: Option<PathBuf>,
}

/// The executables a launch uses, resolved each time they are asked for
/// (the active client version can change between launches).
#[derive(Clone)]
pub struct LauncherExecutableSet {
    resolve: Resolver,
    file_exists: PathPredicate,
    has_unix_execute_permission: PathPredicate,
}

impl std::fmt::Debug for LauncherExecutableSet {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LauncherExecutableSet")
            .field("paths", &(self.resolve)())
            .finish_non_exhaustive()
    }
}

impl LauncherExecutableSet {
    /// Fixed paths, checked against the real file system.
    pub fn new(
        graphical_host_path: impl Into<PathBuf>,
        headless_host_path: impl Into<PathBuf>,
        working_directory: Option<PathBuf>,
    ) -> Self {
        let paths = ExecutablePaths {
            graphical_host_path: graphical_host_path.into(),
            headless_host_path: headless_host_path.into(),
            working_directory,
        };
        Self::from_resolver(Arc::new(move || Ok(paths.clone())))
    }

    fn from_resolver(resolve: Resolver) -> Self {
        Self {
            resolve,
            file_exists: Arc::new(Path::is_file),
            has_unix_execute_permission: Arc::new(has_unix_execute_permission),
        }
    }

    /// Replaces the file-exists check.
    pub fn with_file_exists(mut self, file_exists: PathPredicate) -> Self {
        self.file_exists = file_exists;
        self
    }

    /// Replaces the Unix execute-permission check.
    pub fn with_unix_execute_permission(mut self, check: PathPredicate) -> Self {
        self.has_unix_execute_permission = check;
        self
    }

    /// The client in `directory`, run from there.
    pub fn from_directory(directory: &Path) -> Self {
        let paths = paths_in(directory);
        Self::from_resolver(Arc::new(move || Ok(paths.clone())))
    }

    /// The client in the active version of `store`; unavailable while no
    /// verified version is active.
    pub fn from_current_version_store(store: Arc<ClientVersionStore>) -> Self {
        Self::from_resolver(Arc::new(move || {
            let resolution = store.cached_resolution();
            refuse_old_client(&resolution)?;
            match (&resolution.directory, resolution.is_verified()) {
                (Some(directory), true) => Ok(paths_in(directory)),
                _ => Err(resolution.status),
            }
        }))
    }

    /// The client in the active version of `store`; while none is verified,
    /// the client in `side_by_side` when its executable is there (the
    /// launcher and client built together, before this client has a release
    /// feed); otherwise unavailable as
    /// [`from_current_version_store`](Self::from_current_version_store) is.
    pub fn from_current_version_store_or_directory(
        store: Arc<ClientVersionStore>,
        side_by_side: &Path,
    ) -> Self {
        let beside = paths_in(side_by_side);
        Self::from_resolver(Arc::new(move || {
            let resolution = store.cached_resolution();
            // The client beside the launcher is the launcher's own build, so
            // it also stands in for an installed one too old for the
            // install's folder layout.
            let usable = refuse_old_client(&resolution);
            match (&resolution.directory, resolution.is_verified(), usable) {
                (Some(directory), true, Ok(())) => Ok(paths_in(directory)),
                _ if beside.graphical_host_path.is_file() => Ok(beside.clone()),
                (_, _, Err(old)) => Err(old),
                _ => Err(resolution.status),
            }
        }))
    }

    /// Never available, because `reason`.
    pub fn unavailable(reason: impl Into<String>) -> Self {
        let reason = reason.into();
        Self::from_resolver(Arc::new(move || Err(reason.clone())))
    }

    /// The paths as they resolve now.
    pub fn paths(&self) -> Result<ExecutablePaths, String> {
        (self.resolve)()
    }

    /// Whether `mode` can be started: the executable resolves, exists, and
    /// on Unix may be executed.
    pub fn availability(&self, mode: LaunchMode) -> LauncherCapability {
        let paths = match (self.resolve)() {
            Ok(paths) => paths,
            Err(reason) => {
                return LauncherCapability::unavailable(format!(
                    "The active versioned client is unavailable: {reason}"
                ));
            }
        };
        let (path, host) = if mode == LaunchMode::Headless {
            (&paths.headless_host_path, "headless host")
        } else {
            (&paths.graphical_host_path, "graphical client")
        };
        if !(self.file_exists)(path) {
            return LauncherCapability::unavailable(format!(
                "The co-deployed {host} is missing at '{}'. Reinstall or update the client \
                 before launching.",
                path.display()
            ));
        }
        if cfg!(unix) && !(self.has_unix_execute_permission)(path) {
            return LauncherCapability::unavailable(format!(
                "The co-deployed {host} at '{}' exists but is not executable. Restore its \
                 executable permission (for example, chmod +x) or reinstall/update the client \
                 before launching.",
                path.display()
            ));
        }
        LauncherCapability::AVAILABLE
    }

    /// How a play session in `mode` starts.
    pub fn create_play_spec(
        &self,
        mode: LaunchMode,
        config_file_path: &Path,
        output_log_path: Option<&Path>,
    ) -> Result<LauncherProcessSpec, OrchestratorError> {
        require_path(config_file_path)?;
        let paths = self.require_available(mode)?;
        let config = config_file_path.display().to_string();
        let spec = if mode == LaunchMode::Headless {
            LauncherProcessSpec {
                executable_path: paths.headless_host_path,
                arguments: vec!["run".into(), "--config".into(), config, "--console".into()],
                working_directory: paths.working_directory,
                // Window support stays on, so the row can open a window on it.
                environment: Vec::new(),
                supports_console_graceful_stop: true,
                output_log_path: output_log_path.map(Path::to_path_buf),
                keep_standard_input_open: true,
            }
        } else {
            LauncherProcessSpec {
                executable_path: paths.graphical_host_path,
                // The console lets the row close the window and open it again.
                arguments: vec!["--session-config".into(), config, "--console".into()],
                working_directory: paths.working_directory,
                environment: Vec::new(),
                // The client logs off and exits on the console stop with or
                // without a window (a deliberate divergence from OpenAC).
                supports_console_graceful_stop: true,
                output_log_path: output_log_path.map(Path::to_path_buf),
                keep_standard_input_open: true,
            }
        };
        Ok(spec)
    }

    /// How a character refresh starts.
    pub fn create_probe_spec(
        &self,
        config_file_path: &Path,
        output_log_path: Option<&Path>,
    ) -> Result<LauncherProcessSpec, OrchestratorError> {
        require_path(config_file_path)?;
        let paths = self.require_available(LaunchMode::Headless)?;
        Ok(LauncherProcessSpec {
            executable_path: paths.headless_host_path,
            arguments: vec![
                "run".into(),
                "--config".into(),
                config_file_path.display().to_string(),
            ],
            working_directory: paths.working_directory,
            environment: vec![(HEADLESS_ENVIRONMENT_VARIABLE.into(), "1".into())],
            supports_console_graceful_stop: true,
            output_log_path: output_log_path.map(Path::to_path_buf),
            keep_standard_input_open: false,
        })
    }

    fn require_available(&self, mode: LaunchMode) -> Result<ExecutablePaths, OrchestratorError> {
        let capability = self.availability(mode);
        if !capability.is_available {
            return Err(OrchestratorError::Operation(
                capability
                    .reason
                    .unwrap_or_else(|| "The selected launcher host is unavailable.".to_owned()),
            ));
        }
        (self.resolve)().map_err(OrchestratorError::Operation)
    }
}

fn require_path(path: &Path) -> Result<(), OrchestratorError> {
    if path.as_os_str().to_string_lossy().trim().is_empty() {
        return Err(OrchestratorError::BlankArgument("configFilePath"));
    }
    Ok(())
}

fn paths_in(directory: &Path) -> ExecutablePaths {
    let directory = fsutil::full_path(directory);
    let suffix = suffix_for_current_os();
    let rid = detect_rid().unwrap_or_default();
    ExecutablePaths {
        graphical_host_path: directory.join(format!("{}{suffix}", graphical_host_for_rid(&rid))),
        headless_host_path: directory.join(format!("{}{suffix}", headless_host_for_rid(&rid))),
        working_directory: Some(directory),
    }
}

#[cfg(unix)]
fn has_unix_execute_permission(path: &Path) -> bool {
    use std::os::unix::fs::PermissionsExt;
    std::fs::metadata(path).is_ok_and(|metadata| metadata.permissions().mode() & 0o111 != 0)
}

#[cfg(not(unix))]
fn has_unix_execute_permission(_path: &Path) -> bool {
    true
}
