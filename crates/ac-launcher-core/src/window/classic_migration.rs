//! The import dialog that copies a classic VirindiTank and UtilityBelt
//! setup into this install's shared profile folder, where MossTank reads it
//! (OpenAC `ClassicMigrationViewModel`). The folders are typed (there is
//! no folder picker); Preview and Import run on a worker.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::classic_migration::{self, ClassicMigrationItem};

/// Finds a classic folder, or `None`.
pub type FindFolder = Arc<dyn Fn() -> Option<PathBuf> + Send + Sync>;

/// The dialog's state.
pub struct ClassicMigrationDialog {
    vtank_profiles_directory: PathBuf,
    find_virindi_tank: FindFolder,
    find_utility_belt: FindFolder,
    /// Shown.
    pub is_open: bool,
    is_busy: bool,
    /// The classic VirindiTank folder; empty to leave it out.
    pub virindi_tank_path: String,
    /// The classic UtilityBelt folder; empty to leave it out.
    pub utility_belt_path: String,
    /// Whether a different file already in the install is replaced.
    pub overwrite: bool,
    /// The outcome of the last preview or import.
    pub status: Option<String>,
    /// Why the last preview or import did not happen.
    pub error: Option<String>,
    /// One line per file of the last preview or import.
    pub lines: Vec<String>,
}

impl std::fmt::Debug for ClassicMigrationDialog {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClassicMigrationDialog")
            .field("vtank_profiles_directory", &self.vtank_profiles_directory)
            .field("is_open", &self.is_open)
            .field("is_busy", &self.is_busy)
            .finish_non_exhaustive()
    }
}

/// A preview or import the worker carries out.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClassicMigrationRun {
    virindi_tank: Option<PathBuf>,
    utility_belt: Option<PathBuf>,
    vtank_profiles_directory: PathBuf,
    overwrite: bool,
    /// Copies; otherwise only looks.
    pub apply: bool,
}

impl ClassicMigrationRun {
    /// Plans and, for an import, copies; the plan, or why it failed.
    pub fn run(&self) -> Result<Vec<ClassicMigrationItem>, String> {
        let plan = classic_migration::plan(
            self.virindi_tank.as_deref(),
            self.utility_belt.as_deref(),
            &self.vtank_profiles_directory,
            self.overwrite,
        )
        .map_err(|error| error.to_string())?;
        if self.apply {
            classic_migration::apply(&plan).map_err(|error| error.to_string())?;
        }
        Ok(plan)
    }
}

impl ClassicMigrationDialog {
    /// The dialog for the install's shared profile folder, finding the
    /// classic folders of a usual Windows install.
    pub fn new(vtank_profiles_directory: PathBuf) -> Self {
        Self::with_finders(
            vtank_profiles_directory,
            Arc::new(classic_migration::find_virindi_tank),
            Arc::new(classic_migration::find_utility_belt),
        )
    }

    /// The dialog with its own ways to find the classic folders.
    pub fn with_finders(
        vtank_profiles_directory: PathBuf,
        find_virindi_tank: FindFolder,
        find_utility_belt: FindFolder,
    ) -> Self {
        Self {
            vtank_profiles_directory,
            find_virindi_tank,
            find_utility_belt,
            is_open: false,
            is_busy: false,
            virindi_tank_path: String::new(),
            utility_belt_path: String::new(),
            overwrite: false,
            status: None,
            error: None,
            lines: Vec::new(),
        }
    }

    /// Where the imported profiles go.
    pub fn target_path(&self) -> PathBuf {
        self.vtank_profiles_directory
            .join(classic_migration::MOSSTANK_FOLDER)
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

    /// Opens the dialog, filling in a folder left empty with the one found.
    pub fn open(&mut self) {
        if self.virindi_tank_path.is_empty() {
            self.virindi_tank_path = path_text((self.find_virindi_tank)());
        }
        if self.utility_belt_path.is_empty() {
            self.utility_belt_path = path_text((self.find_utility_belt)());
        }
        self.status = None;
        self.error = None;
        self.lines.clear();
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

    /// Preview and Import may run: nothing runs and a folder is named.
    pub fn can_run(&self) -> bool {
        !self.is_busy
            && (!self.virindi_tank_path.trim().is_empty()
                || !self.utility_belt_path.trim().is_empty())
    }

    /// Checks the named folders exist and marks the run started; the run,
    /// or `None` with the reason shown.
    pub fn begin_run(&mut self, apply: bool) -> Option<ClassicMigrationRun> {
        if !self.can_run() {
            return None;
        }
        let virindi_tank = non_empty(&self.virindi_tank_path);
        let utility_belt = non_empty(&self.utility_belt_path);
        for (path, name) in [
            (&virindi_tank, "VirindiTank"),
            (&utility_belt, "UtilityBelt"),
        ] {
            if let Some(path) = path
                && !path.is_dir()
            {
                self.error = Some(format!(
                    "The {name} folder {} does not exist.",
                    path.display()
                ));
                return None;
            }
        }
        self.error = None;
        self.status = Some(if apply {
            "Importing…".into()
        } else {
            "Looking through the folders…".into()
        });
        self.is_busy = true;
        Some(ClassicMigrationRun {
            virindi_tank,
            utility_belt,
            vtank_profiles_directory: self.vtank_profiles_directory.clone(),
            overwrite: self.overwrite,
            apply,
        })
    }

    /// The run ended: its lines and counts, or why it failed.
    pub fn finish(&mut self, apply: bool, result: Result<Vec<ClassicMigrationItem>, String>) {
        self.is_busy = false;
        match result {
            Ok(items) => {
                self.lines = items.iter().map(classic_migration::describe).collect();
                let mut status = classic_migration::summarize(&items, apply);
                if classic_migration::has_conflicts(&items) {
                    status.push_str(
                        " Tick \"Replace files that differ\" to replace the conflicting files.",
                    );
                }
                self.status = Some(status);
            }
            Err(error) => {
                self.status = None;
                self.error = Some(error);
            }
        }
    }
}

fn path_text(path: Option<PathBuf>) -> String {
    path.map(|path| path.display().to_string())
        .unwrap_or_default()
}

fn non_empty(value: &str) -> Option<PathBuf> {
    let value = value.trim();
    (!value.is_empty()).then(|| Path::new(value).to_path_buf())
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Scratch {
        root: PathBuf,
        vtank: PathBuf,
        profiles: PathBuf,
    }

    impl Scratch {
        fn new() -> Self {
            let root = std::env::temp_dir()
                .join(format!("openac-classic-vm-{}", crate::ids::unique_hex32()));
            let vtank = root.join("VirindiTank");
            std::fs::create_dir_all(&vtank).unwrap();
            std::fs::write(vtank.join("Hunt.met"), "meta").unwrap();
            Self {
                profiles: root.join("OpenAC").join("vtank"),
                vtank,
                root,
            }
        }

        fn dialog(&self, finds_vtank: bool) -> ClassicMigrationDialog {
            let vtank = self.vtank.clone();
            ClassicMigrationDialog::with_finders(
                self.profiles.clone(),
                Arc::new(move || finds_vtank.then(|| vtank.clone())),
                Arc::new(|| None),
            )
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.root);
        }
    }

    /// Runs Preview or Import as the window does, the worker inline.
    fn run(dialog: &mut ClassicMigrationDialog, apply: bool) {
        if let Some(run) = dialog.begin_run(apply) {
            let result = run.run();
            dialog.finish(run.apply, result);
        }
    }

    // OpenAC ClassicMigrationViewModelTests.OpeningFillsInTheFoldersThatWereFound.
    #[test]
    fn opening_fills_in_the_folders_that_were_found() {
        let scratch = Scratch::new();
        let mut dialog = scratch.dialog(true);

        assert!(!dialog.can_run());
        dialog.open();

        assert!(dialog.is_open);
        assert_eq!(
            dialog.virindi_tank_path,
            scratch.vtank.display().to_string()
        );
        assert_eq!(dialog.utility_belt_path, "");
        assert!(dialog.can_run());
    }

    // OpenAC ClassicMigrationViewModelTests.PreviewListsTheFilesAndImportCopiesThem.
    #[test]
    fn preview_lists_the_files_and_import_copies_them() {
        let scratch = Scratch::new();
        let mut dialog = scratch.dialog(true);
        dialog.open();
        let copied = scratch
            .profiles
            .join("mosstank")
            .join("metas")
            .join("Hunt.met");

        run(&mut dialog, false);

        assert!(!copied.exists());
        assert!(
            dialog
                .status
                .as_deref()
                .unwrap()
                .starts_with("Would copy 1 file(s)")
        );
        assert_eq!(dialog.lines.len(), 1);

        run(&mut dialog, true);

        assert_eq!(std::fs::read_to_string(&copied).unwrap(), "meta");
        assert!(
            dialog
                .status
                .as_deref()
                .unwrap()
                .starts_with("Copied 1 file(s)")
        );
        assert!(!dialog.is_busy());
    }

    // OpenAC ClassicMigrationViewModelTests.AMissingFolderIsReportedAndNothingIsCopied.
    #[test]
    fn a_missing_folder_is_reported_and_nothing_is_copied() {
        let scratch = Scratch::new();
        let mut dialog = scratch.dialog(false);
        dialog.utility_belt_path = scratch.root.join("missing").display().to_string();

        run(&mut dialog, true);

        assert!(dialog.has_error());
        assert!(dialog.error.as_deref().unwrap().contains("does not exist"));
        assert!(!scratch.profiles.exists());
    }

    #[test]
    fn a_conflict_says_how_to_replace_it_and_close_waits_for_a_run() {
        let scratch = Scratch::new();
        let metas = scratch.profiles.join("mosstank").join("metas");
        std::fs::create_dir_all(&metas).unwrap();
        std::fs::write(metas.join("Hunt.met"), "edited").unwrap();
        let mut dialog = scratch.dialog(true);
        dialog.open();

        let started = dialog.begin_run(true).unwrap();
        dialog.close();
        assert!(dialog.is_open, "a running import keeps the dialog open");
        assert!(!dialog.can_run());
        let result = started.run();
        dialog.finish(true, result);

        assert_eq!(
            dialog.status.as_deref(),
            Some(
                "Copied 0 file(s); 0 already present, 1 conflict(s), 0 skipped. \
                 Tick \"Replace files that differ\" to replace the conflicting files."
            )
        );
        assert_eq!(
            std::fs::read_to_string(metas.join("Hunt.met")).unwrap(),
            "edited"
        );
        dialog.close();
        assert!(!dialog.is_open);
    }
}
