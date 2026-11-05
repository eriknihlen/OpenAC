//! The Import from Thwarg launcher dialog: the Thwarg launcher's data folder
//! (typed; there is no folder picker), Preview, then Import. The report of
//! the last preview or import stays shown until the dialog is opened again.
//! Reading the folder and the import run on a worker.

use std::path::PathBuf;
use std::sync::Arc;

use super::Orchestration;
use crate::orchestration::OrchestratorError;
use crate::profiles::ProfileError;
use crate::profiles::thwarg::{self, ThwargImportReport};

/// Finds the Thwarg launcher's data folder, or `None`.
pub type FindFolder = Arc<dyn Fn() -> Option<PathBuf> + Send + Sync>;

/// The dialog's state.
pub struct ThwargImportDialog {
    find_folder: FindFolder,
    /// Shown.
    pub is_open: bool,
    is_busy: bool,
    /// The Thwarg launcher's data folder.
    pub folder: String,
    /// The outcome of the last preview or import.
    pub status: Option<String>,
    /// Why the last preview or import did not happen.
    pub error: Option<String>,
    /// The report's lines.
    pub lines: Vec<String>,
    /// The folder the shown preview is of, while it may be imported.
    previewed: Option<String>,
}

impl std::fmt::Debug for ThwargImportDialog {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ThwargImportDialog")
            .field("is_open", &self.is_open)
            .field("is_busy", &self.is_busy)
            .field("folder", &self.folder)
            .finish_non_exhaustive()
    }
}

impl Default for ThwargImportDialog {
    fn default() -> Self {
        Self::with_finder(Arc::new(thwarg::find_folder))
    }
}

/// A preview or import the worker carries out.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ThwargImportRun {
    folder: PathBuf,
    /// Imports; otherwise only looks.
    pub apply: bool,
}

impl ThwargImportRun {
    /// Reads the folder and previews or imports; the report, or why not.
    pub fn run(&self, orchestrator: &dyn Orchestration) -> Result<ThwargImportReport, String> {
        let files = thwarg::read_folder(&self.folder).map_err(|error| error.to_string())?;
        orchestrator
            .import_thwarg(&files, self.apply)
            .map_err(|error| match error {
                OrchestratorError::Profile(ProfileError::Io(_)) => {
                    "Unable to save. Check that the profile folder is writable.".into()
                }
                other => other.to_string(),
            })
    }
}

impl ThwargImportDialog {
    /// The dialog with its own way to find the folder.
    pub fn with_finder(find_folder: FindFolder) -> Self {
        Self {
            find_folder,
            is_open: false,
            is_busy: false,
            folder: String::new(),
            status: None,
            error: None,
            lines: Vec::new(),
            previewed: None,
        }
    }

    /// A preview or import runs.
    pub fn is_busy(&self) -> bool {
        self.is_busy
    }

    /// Whether the error line shows.
    pub fn has_error(&self) -> bool {
        self.error
            .as_deref()
            .is_some_and(|error| !error.trim().is_empty())
    }

    /// Opens the dialog, filling in the folder when it is empty and found.
    pub fn open(&mut self) {
        if self.folder.trim().is_empty()
            && let Some(folder) = (self.find_folder)()
        {
            self.folder = folder.display().to_string();
        }
        self.status = None;
        self.error = None;
        self.lines.clear();
        self.previewed = None;
        self.is_open = true;
    }

    /// Closes it, unless a preview or import runs.
    pub fn close(&mut self) {
        if !self.is_busy {
            self.is_open = false;
        }
    }

    /// Close may be pressed.
    pub fn can_close(&self) -> bool {
        !self.is_busy
    }

    /// Preview may run: nothing runs and a folder is named.
    pub fn can_preview(&self) -> bool {
        !self.is_busy && !self.folder.trim().is_empty()
    }

    /// Import may run: the shown preview is of the folder named and found
    /// something to import.
    pub fn can_import(&self) -> bool {
        self.can_preview() && self.previewed.as_deref() == Some(self.folder.trim())
    }

    /// Marks the run started; the run, or `None` when it may not run now.
    pub fn begin_run(&mut self, apply: bool) -> Option<ThwargImportRun> {
        if !(if apply {
            self.can_import()
        } else {
            self.can_preview()
        }) {
            return None;
        }
        self.error = None;
        self.status = Some(if apply {
            "Importing…".into()
        } else {
            "Reading the Thwarg launcher's files…".into()
        });
        self.is_busy = true;
        Some(ThwargImportRun {
            folder: PathBuf::from(self.folder.trim()),
            apply,
        })
    }

    /// The run ended: its report, or why it failed. A preview with
    /// something to import allows Import; an import shows what it did.
    pub fn finish(&mut self, apply: bool, result: Result<ThwargImportReport, String>) {
        self.is_busy = false;
        self.previewed = None;
        match result {
            Ok(report) => {
                self.lines = report.lines(apply);
                self.status = Some(report.summary(apply));
                if !apply && report.has_changes() {
                    self.previewed = Some(self.folder.trim().to_owned());
                }
            }
            Err(error) => {
                self.status = None;
                self.lines.clear();
                self.error = Some(error);
            }
        }
    }
}
