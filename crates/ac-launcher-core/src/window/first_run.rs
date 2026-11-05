//! First-run setup: choose the retail DAT directory. OpenAC's wizard also
//! bakes a prepared content package (with a thread count, progress and
//! cancel); this client reads the DATs directly, so setup checks the four
//! files and remembers the directory.

use std::path::Path;

use crate::installation::{LauncherInstallRecord, RetailContentStore};

/// The setup dialog.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FirstRunSetup {
    /// Shown.
    pub is_open: bool,
    /// The directory field.
    pub dat_directory: String,
    /// The line under the form.
    pub status: String,
    /// What checking the directory found.
    pub validation_status: String,
    /// `Missing: ...`, or empty.
    pub missing_files: String,
    /// Why saving failed.
    pub error: Option<String>,
    /// The directory holds all four files.
    pub is_dat_directory_valid: bool,
    /// Setup finished; the dialog shows its completion page.
    pub is_completed: bool,
}

impl Default for FirstRunSetup {
    fn default() -> Self {
        Self {
            is_open: false,
            dat_directory: String::new(),
            status: "Choose the folder containing the retail DAT files.".into(),
            validation_status: "No DAT directory selected.".into(),
            missing_files: String::new(),
            error: None,
            is_dat_directory_valid: false,
            is_completed: false,
        }
    }
}

impl FirstRunSetup {
    /// The heading.
    pub fn title(&self) -> &'static str {
        "First-run setup"
    }

    /// The explanation.
    pub fn body(&self) -> &'static str {
        "Choose your Asheron's Call data folder. OpenAC will check that the four retail DAT \
         files are there. This client reads them directly, so there is nothing to build."
    }

    /// The confirm button.
    pub fn start_action_text(&self) -> &'static str {
        "Save"
    }

    /// The completion page's heading.
    pub fn completed_title(&self) -> &'static str {
        "Setup complete"
    }

    /// The completion page's text.
    pub fn completed_body(&self) -> &'static str {
        "OpenAC found your game files. You can play now."
    }

    /// Opens the dialog; an empty field is filled with the first usable
    /// detected directory (else the first detected one), then checked.
    pub fn open(&mut self, store: &RetailContentStore) {
        if self.dat_directory.trim().is_empty() {
            let candidates = store.locator().detect();
            if let Some(preferred) = candidates
                .iter()
                .find(|candidate| candidate.is_valid)
                .or_else(|| candidates.first())
            {
                self.dat_directory = preferred.directory.to_string_lossy().into_owned();
            }
        }
        self.validate(store);
        self.is_open = true;
    }

    /// Checks the field.
    pub fn validate(&mut self, store: &RetailContentStore) {
        let validation = store
            .locator()
            .validate(Some(Path::new(self.dat_directory.trim())));
        self.is_dat_directory_valid = validation.is_valid;
        self.validation_status = validation.message;
        self.missing_files = if validation.missing_file_names.is_empty() {
            String::new()
        } else {
            format!("Missing: {}", validation.missing_file_names.join(", "))
        };
        self.error = None;
    }

    /// Sets the field (the folder picker) and checks it.
    pub fn select_dat_directory(&mut self, directory: &str, store: &RetailContentStore) {
        self.dat_directory = directory.to_owned();
        self.validate(store);
    }

    /// Whether Save may run.
    pub fn can_start(&self) -> bool {
        self.is_open && self.is_dat_directory_valid && !self.is_completed
    }

    /// Saves the directory; the record to launch with on success.
    pub fn start(&mut self, store: &RetailContentStore) -> Option<LauncherInstallRecord> {
        if !self.can_start() {
            return None;
        }
        match store.save(Path::new(self.dat_directory.trim())) {
            Ok(record) => {
                self.status = "Client content configured. Launch is enabled.".into();
                self.error = None;
                self.is_completed = true;
                Some(record)
            }
            Err(error) => {
                self.error = Some(error);
                self.status = "Setup failed; nothing was saved.".into();
                None
            }
        }
    }

    /// Closes the dialog.
    pub fn close(&mut self) {
        self.is_open = false;
    }

    /// The completion page's OK.
    pub fn acknowledge_completion(&mut self) {
        self.is_completed = false;
        self.is_open = false;
    }
}
