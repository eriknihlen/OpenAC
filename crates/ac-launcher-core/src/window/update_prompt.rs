//! The update prompt: what the release feed offers, installing the client
//! from it and staging a newer launcher, which restarts itself to apply it.

use crate::updates::{LauncherUpdateCheckResult, LauncherUpdateProgress};

/// The prompt's state.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct UpdatePrompt {
    /// Shown.
    pub is_open: bool,
    /// A check or install runs.
    pub is_busy: bool,
    /// What the last step said.
    pub status: String,
    /// Why the install failed.
    pub error: Option<String>,
    /// Install progress, 0 to 100.
    pub progress_percent: f64,
    /// No total is known yet.
    pub is_progress_indeterminate: bool,
    /// The last successful check.
    pub check: Option<LauncherUpdateCheckResult>,
    /// The startup check has run.
    pub is_startup_check_complete: bool,
    /// The startup check reached the feed.
    pub startup_check_succeeded: bool,
    /// Open the prompt by itself when a check finds an update.
    pub auto_open_discovered_updates: bool,
}

impl UpdatePrompt {
    /// The heading.
    pub fn title(&self) -> &'static str {
        "Update available"
    }

    /// What is out of date, in one sentence.
    pub fn body(&self) -> String {
        let Some(check) = &self.check else {
            return String::new();
        };
        let version = check.manifest.version.value();
        match (
            check.is_launcher_update_available,
            check.is_client_update_available,
        ) {
            (true, true) => format!(
                "A new version is available ({version}). The launcher updates first and \
                 restarts itself, then the game updates."
            ),
            (true, false) => format!(
                "A new launcher is available ({version}). It will restart itself once installed."
            ),
            _ => format!("A new version of the game is available ({version})."),
        }
    }

    /// The feed offers a newer client.
    pub fn is_client_update_available(&self) -> bool {
        self.check
            .as_ref()
            .is_some_and(|check| check.is_client_update_available)
    }

    /// The feed offers a newer launcher.
    pub fn is_launcher_update_available(&self) -> bool {
        self.check
            .as_ref()
            .is_some_and(|check| check.is_launcher_update_available)
    }

    /// Either is newer.
    pub fn has_something_to_update(&self) -> bool {
        self.is_client_update_available() || self.is_launcher_update_available()
    }

    /// Opens the prompt when there is something to update.
    pub fn open_available_update(&mut self) {
        if self.has_something_to_update() {
            self.is_open = true;
        }
    }

    /// Opens a discovered update once nothing else asks first.
    pub fn try_open_pending_update(&mut self) {
        if self.auto_open_discovered_updates && self.has_something_to_update() {
            self.is_open = true;
        }
    }

    /// Closes, unless something runs.
    pub fn close(&mut self) {
        if !self.is_busy {
            self.is_open = false;
        }
    }

    /// A check starts.
    pub fn begin_check(&mut self) {
        self.is_startup_check_complete = false;
        self.startup_check_succeeded = false;
        self.error = None;
        self.status = "Checking for updates…".into();
        self.is_busy = true;
    }

    /// A check finished.
    pub fn finish_check(&mut self, result: Result<LauncherUpdateCheckResult, String>) {
        self.is_busy = false;
        self.is_startup_check_complete = true;
        match result {
            Ok(check) => {
                self.check = Some(check);
                self.startup_check_succeeded = true;
                self.status = if self.has_something_to_update() {
                    "An update is available.".into()
                } else {
                    "OpenAC is up to date.".into()
                };
                if self.has_something_to_update() {
                    self.try_open_pending_update();
                }
            }
            Err(_) => {
                self.check = None;
                self.status =
                    "Updates could not be checked. Try again from Installation & updates.".into();
            }
        }
    }

    /// Install progress.
    pub fn apply_progress(&mut self, progress: &LauncherUpdateProgress) {
        self.status = progress.status.clone();
        if progress.total > 0 {
            self.is_progress_indeterminate = false;
            self.progress_percent = progress.percent();
        } else {
            self.is_progress_indeterminate = true;
        }
    }
}
