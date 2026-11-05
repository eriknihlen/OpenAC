//! The launcher window's logic, without widgets: what OpenAC keeps in
//! `AcDream.Launcher`'s view models and composition classes. The window
//! crate draws these and calls their methods; nothing here knows how a
//! button looks.
//!
//! - [`startup`]: the launcher's command line.
//! - [`LauncherWindow`]: the window's state, its commands, and the work it
//!   runs in the background.
//! - [`accounts`]: the account groups and their per-server rows.
//! - [`sessions`]: how a session reads in the session log.
//! - [`profile_editor`]: the Edit accounts / Edit servers / Logon commands
//!   editors; [`profiles_dialog`]: the Profiles dialog (create and delete
//!   profiles).
//! - [`plugins`] and [`plugins_panel`]: the plugin check pipeline and the
//!   Plugins tab; [`install_dialog`]: the install and update dialog.
//! - [`first_run`], [`update_prompt`], [`console`]: content setup, the
//!   client update prompt, a windowless session's console.
//! - [`icon`]: decoding plugin icons.
//! - [`install_folder`]: the install folder section of the settings.
//! - [`closing`]: closing the launcher over running clients.
//! - [`classic_migration`]: the classic VirindiTank and UtilityBelt import.
//! - [`thwarg_import`]: the import from the Thwarg launcher.
//!
//! Every slow step (a launch, a stop, an HTTP request, hashing) runs on a
//! worker through a [`Spawner`] and reports back as a message the window
//! applies on its own thread in [`LauncherWindow::poll`]; the window is
//! single-threaded state, like OpenAC's view models on the UI dispatcher.

pub mod accounts;
pub mod add_server;
pub mod classic_migration;
pub mod closing;
pub mod console;
pub mod first_run;
pub mod icon;
pub mod install_dialog;
pub mod install_folder;
mod launcher;
pub mod plugins;
pub mod plugins_panel;
pub mod profile_editor;
pub mod profiles_dialog;
pub mod sessions;
pub mod startup;
pub mod thwarg_import;
pub mod update_prompt;

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

pub use launcher::{
    ApplyLauncherUpdate, ClosePrompt, LauncherMainTab, LauncherWindow, LauncherWindowParts,
    RowOptionsChoice, RowOptionsProfile,
};

use crate::cancel::{CancelToken, Cancelled};
use crate::installation::LauncherInstallRecord;
use crate::orchestration::{
    AccountCapabilities, LauncherCapability, LauncherOrchestrator, LauncherSessionSnapshot,
    LauncherStateSnapshot, OrchestratorError, StateChangedListener,
};
use crate::plugins::PluginCatalog;
use crate::profiles::thwarg::{ThwargFiles, ThwargImportReport};
use crate::profiles::{LaunchMode, LauncherTextEditorKind};
use crate::status::{ServerHealthService, ServerHealthSnapshot};

/// Runs a job somewhere other than the window's thread (a new thread in
/// the launcher; the calling thread in tests, so a test sees the job's
/// message on its next `poll`).
pub trait Spawner: Send + Sync {
    /// Runs `job`.
    fn spawn(&self, job: Box<dyn FnOnce() + Send>);
}

/// One OS thread per job.
#[derive(Debug, Clone, Copy, Default)]
pub struct ThreadSpawner;

impl Spawner for ThreadSpawner {
    fn spawn(&self, job: Box<dyn FnOnce() + Send>) {
        std::thread::Builder::new()
            .name("launcher-worker".into())
            .spawn(job)
            .map(drop)
            .unwrap_or_else(|error| {
                // Without a thread the job cannot run; the window keeps its busy state until the
                // next start, which is what a lost task would do in OpenAC too.
                eprintln!("launcher: could not start a worker thread: {error}");
            });
    }
}

/// Runs each job at once on the calling thread.
#[derive(Debug, Clone, Copy, Default)]
pub struct InlineSpawner;

impl Spawner for InlineSpawner {
    fn spawn(&self, job: Box<dyn FnOnce() + Send>) {
        job();
    }
}

/// Asks the window to draw again (a worker finished something).
pub type Wake = Arc<dyn Fn() + Send + Sync>;

/// What the window needs from the launcher's state owner (OpenAC's
/// `ILauncherOrchestrator`): implemented by [`LauncherOrchestrator`], and by
/// fakes in tests. Every method may be called from any thread.
#[allow(missing_docs)]
pub trait Orchestration: Send + Sync {
    fn subscribe(&self, listener: StateChangedListener);
    fn load_profiles(&self) -> Result<(), OrchestratorError>;
    fn snapshot(&self) -> Result<LauncherStateSnapshot, OrchestratorError>;
    fn launch_capability(&self, mode: LaunchMode) -> Result<LauncherCapability, OrchestratorError>;
    fn account_launch_capability(
        &self,
        server_name: &str,
        account_name: &str,
        mode: LaunchMode,
    ) -> Result<LauncherCapability, OrchestratorError>;
    /// Every account's launch availability for one window refresh.
    fn account_launch_capabilities(
        &self,
        mode: LaunchMode,
    ) -> Result<AccountCapabilities, OrchestratorError> {
        let mut result = AccountCapabilities::new();
        for server in self.snapshot()?.servers {
            for account in server.accounts {
                let capability =
                    self.account_launch_capability(&server.name, &account.account_name, mode)?;
                result.insert((server.name.clone(), account.account_name), capability);
            }
        }
        Ok(result)
    }
    fn set_install_record(
        &self,
        record: Option<LauncherInstallRecord>,
    ) -> Result<(), OrchestratorError>;
    fn set_installation_state(
        &self,
        record: Option<LauncherInstallRecord>,
        status: &str,
    ) -> Result<(), OrchestratorError>;
    fn set_plugin_catalog(&self, catalog: Option<PluginCatalog>) -> Result<(), OrchestratorError>;
    fn set_show_beta_plugins(&self, value: bool) -> Result<(), OrchestratorError>;
    fn read_profile_text(&self, kind: LauncherTextEditorKind) -> Result<String, OrchestratorError>;
    fn save_profile_text(
        &self,
        kind: LauncherTextEditorKind,
        text: &str,
        original_text: &str,
    ) -> Result<(), OrchestratorError>;
    fn describe_profile_text_removals(
        &self,
        kind: LauncherTextEditorKind,
        text: &str,
    ) -> Result<Vec<String>, OrchestratorError>;
    fn update_character_settings(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: &str,
        launch_mode: LaunchMode,
        plugins: Option<&[String]>,
    ) -> Result<(), OrchestratorError>;
    fn update_account_plugins(
        &self,
        server_name: &str,
        account_name: &str,
        plugins: &[String],
    ) -> Result<(), OrchestratorError>;
    /// The profiles a character is in; each is taken off the account's
    /// other characters.
    fn update_character_profiles(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: &str,
        profiles: &[String],
    ) -> Result<(), OrchestratorError>;
    /// Adds an empty profile.
    fn create_profile(&self, name: &str) -> Result<(), OrchestratorError>;
    /// Deletes a profile everywhere.
    fn delete_profile(&self, name: &str) -> Result<(), OrchestratorError>;
    fn remove_character(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: &str,
    ) -> Result<(), OrchestratorError>;
    fn add_server(&self, name: &str, host: &str, port: i32) -> Result<(), OrchestratorError>;
    /// Imports the Thwarg launcher's files (`apply`), or reports what that
    /// would do.
    fn import_thwarg(
        &self,
        files: &ThwargFiles,
        apply: bool,
    ) -> Result<ThwargImportReport, OrchestratorError>;
    fn take_profile_migration_notice(&self) -> Result<Option<String>, OrchestratorError>;
    fn update_account_selection(
        &self,
        server_name: &str,
        account_name: &str,
        selected_character: Option<&str>,
        selected_launch_mode: LaunchMode,
    ) -> Result<(), OrchestratorError>;
    fn launch(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: Option<&str>,
        mode: LaunchMode,
        cancel: &CancelToken,
    ) -> Result<LauncherSessionSnapshot, OrchestratorError>;
    fn stop_session(
        &self,
        session_id: &str,
        timeout: Duration,
        cancel: &CancelToken,
    ) -> Result<(), OrchestratorError>;
    fn poll_status(&self) -> Result<(), OrchestratorError>;
    fn try_send_console_line(&self, session_id: &str, line: &str) -> bool;
    /// Switches a running play session between graphical (`open`) and
    /// headless without restarting it.
    fn set_session_window(&self, session_id: &str, open: bool) -> Result<(), OrchestratorError>;
    fn session_log_path(&self, session_id: &str) -> Option<PathBuf>;
    /// Refuses every new session from now on: the launcher is closing.
    fn close_to_new_sessions(&self) -> Result<(), OrchestratorError>;
    fn dispose(&self);
}

impl Orchestration for LauncherOrchestrator {
    fn subscribe(&self, listener: StateChangedListener) {
        LauncherOrchestrator::subscribe(self, listener);
    }
    fn load_profiles(&self) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::load_profiles(self)
    }
    fn snapshot(&self) -> Result<LauncherStateSnapshot, OrchestratorError> {
        LauncherOrchestrator::snapshot(self)
    }
    fn launch_capability(&self, mode: LaunchMode) -> Result<LauncherCapability, OrchestratorError> {
        LauncherOrchestrator::launch_capability(self, mode)
    }
    fn account_launch_capability(
        &self,
        server_name: &str,
        account_name: &str,
        mode: LaunchMode,
    ) -> Result<LauncherCapability, OrchestratorError> {
        LauncherOrchestrator::account_launch_capability(self, server_name, account_name, mode)
    }
    fn account_launch_capabilities(
        &self,
        mode: LaunchMode,
    ) -> Result<AccountCapabilities, OrchestratorError> {
        LauncherOrchestrator::account_launch_capabilities(self, mode)
    }
    fn set_install_record(
        &self,
        record: Option<LauncherInstallRecord>,
    ) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::set_install_record(self, record)
    }
    fn set_installation_state(
        &self,
        record: Option<LauncherInstallRecord>,
        status: &str,
    ) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::set_installation_state(self, record, status)
    }
    fn set_plugin_catalog(&self, catalog: Option<PluginCatalog>) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::set_plugin_catalog(self, catalog)
    }
    fn set_show_beta_plugins(&self, value: bool) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::set_show_beta_plugins(self, value)
    }
    fn read_profile_text(&self, kind: LauncherTextEditorKind) -> Result<String, OrchestratorError> {
        LauncherOrchestrator::read_profile_text(self, kind)
    }
    fn save_profile_text(
        &self,
        kind: LauncherTextEditorKind,
        text: &str,
        original_text: &str,
    ) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::save_profile_text(self, kind, text, original_text)
    }
    fn describe_profile_text_removals(
        &self,
        kind: LauncherTextEditorKind,
        text: &str,
    ) -> Result<Vec<String>, OrchestratorError> {
        LauncherOrchestrator::describe_profile_text_removals(self, kind, text)
    }
    fn update_character_settings(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: &str,
        launch_mode: LaunchMode,
        plugins: Option<&[String]>,
    ) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::update_character_settings(
            self,
            server_name,
            account_name,
            character_name,
            launch_mode,
            plugins,
        )
    }
    fn update_account_plugins(
        &self,
        server_name: &str,
        account_name: &str,
        plugins: &[String],
    ) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::update_account_plugins(self, server_name, account_name, plugins)
    }
    fn update_character_profiles(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: &str,
        profiles: &[String],
    ) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::update_character_profiles(
            self,
            server_name,
            account_name,
            character_name,
            profiles,
        )
    }
    fn create_profile(&self, name: &str) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::create_profile(self, name)
    }
    fn delete_profile(&self, name: &str) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::delete_profile(self, name)
    }
    fn remove_character(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: &str,
    ) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::remove_character(self, server_name, account_name, character_name)
    }
    fn add_server(&self, name: &str, host: &str, port: i32) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::add_server(self, name, host, port)
    }
    fn import_thwarg(
        &self,
        files: &ThwargFiles,
        apply: bool,
    ) -> Result<ThwargImportReport, OrchestratorError> {
        LauncherOrchestrator::import_thwarg(self, files, apply)
    }
    fn take_profile_migration_notice(&self) -> Result<Option<String>, OrchestratorError> {
        LauncherOrchestrator::take_profile_migration_notice(self)
    }
    fn update_account_selection(
        &self,
        server_name: &str,
        account_name: &str,
        selected_character: Option<&str>,
        selected_launch_mode: LaunchMode,
    ) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::update_account_selection(
            self,
            server_name,
            account_name,
            selected_character,
            selected_launch_mode,
        )
    }
    fn launch(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: Option<&str>,
        mode: LaunchMode,
        cancel: &CancelToken,
    ) -> Result<LauncherSessionSnapshot, OrchestratorError> {
        LauncherOrchestrator::launch(
            self,
            server_name,
            account_name,
            character_name,
            mode,
            cancel,
        )
    }
    fn stop_session(
        &self,
        session_id: &str,
        timeout: Duration,
        cancel: &CancelToken,
    ) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::stop_session(self, session_id, timeout, cancel)
    }
    fn poll_status(&self) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::poll_status(self)
    }
    fn try_send_console_line(&self, session_id: &str, line: &str) -> bool {
        LauncherOrchestrator::try_send_console_line(self, session_id, line)
    }
    fn set_session_window(&self, session_id: &str, open: bool) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::set_session_window(self, session_id, open)
    }
    fn session_log_path(&self, session_id: &str) -> Option<PathBuf> {
        LauncherOrchestrator::session_log_path(self, session_id)
    }
    fn close_to_new_sessions(&self) -> Result<(), OrchestratorError> {
        LauncherOrchestrator::close_to_new_sessions(self)
    }
    fn dispose(&self) {
        LauncherOrchestrator::dispose(self);
    }
}

/// Checks one server's reachability and population (OpenAC's
/// `IServerHealthService`).
pub trait ServerHealth: Send + Sync {
    /// Checks `host:port`, matching `server_name` in the population list.
    /// Only the caller's cancellation is an error.
    fn check(
        &self,
        host: &str,
        port: u16,
        server_name: &str,
        cancel: &CancelToken,
    ) -> Result<ServerHealthSnapshot, Cancelled>;
}

impl ServerHealth for ServerHealthService {
    fn check(
        &self,
        host: &str,
        port: u16,
        server_name: &str,
        cancel: &CancelToken,
    ) -> Result<ServerHealthSnapshot, Cancelled> {
        ServerHealthService::check(self, host, port, server_name, cancel)
    }
}

/// The text an error shows the player: its message with `secret` replaced,
/// or a generic line when it has none (OpenAC's `SafeDisplayError`).
pub fn safe_display_error(message: &str, secret: Option<&str>) -> String {
    let message = match secret {
        Some(secret) if !secret.is_empty() => message.replace(secret, "[redacted]"),
        _ => message.to_owned(),
    };
    if message.trim().is_empty() {
        "The launcher operation failed.".to_owned()
    } else {
        message
    }
}
