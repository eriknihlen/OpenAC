//! The install folder section of the launcher settings: where everything
//! lives, a row per folder the player may want to open, and moving the
//! whole install (OpenAC `InstallFolderViewModel`). Opening a folder and
//! the clipboard are the window's; the move runs on a worker.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use ac_host_core::PathSet;

use crate::installation::{InstallRootMoveResult, InstallRootMover, MOVE_SESSION_REFUSAL};

/// One folder the player can open.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallFolderRow {
    /// What the folder holds, in the player's words.
    pub label: &'static str,
    /// The folder's full path.
    pub path: PathBuf,
    /// Opening the folder failed, so the row offers its path to copy.
    pub open_failed: bool,
}

impl InstallFolderRow {
    fn new(label: &'static str, path: PathBuf) -> Self {
        Self {
            label,
            path,
            open_failed: false,
        }
    }

    /// The line shown when the folder could not be opened.
    pub const OPEN_FAILED_TEXT: &'static str = "Could not open this folder. Copy its path instead.";

    /// Before the window opens the folder: a folder nothing has written to
    /// yet still opens, empty. False when it cannot be made.
    pub fn prepare_open(&self) -> bool {
        std::fs::create_dir_all(&self.path).is_ok()
    }
}

/// Starts the launcher again once the install moved.
pub type RestartLauncher = Arc<dyn Fn() + Send + Sync>;

/// What the window needs to show and move the install.
#[derive(Clone)]
pub struct InstallFolderParts {
    /// The install as this launcher resolved it.
    pub paths: PathSet,
    /// Moves it.
    pub mover: Arc<InstallRootMover>,
    /// Starts the launcher again after a move.
    pub restart_launcher: RestartLauncher,
}

impl std::fmt::Debug for InstallFolderParts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("InstallFolderParts")
            .field("paths", &self.paths)
            .finish_non_exhaustive()
    }
}

/// The section's state.
pub struct InstallFolderPanel {
    /// The install folder itself.
    pub root: InstallFolderRow,
    /// The folders inside it a player opens most: Game, Plugins, Settings,
    /// Logs.
    pub folders: Vec<InstallFolderRow>,
    /// The folder typed for Move... (there is no folder picker).
    pub move_target: String,
    /// True while the install is moving.
    pub is_moving: bool,
    /// Progress or the outcome of the last move.
    pub move_status: Option<String>,
    /// Why the last move did not happen.
    pub move_error: Option<String>,
    mover: Arc<InstallRootMover>,
    restart_launcher: RestartLauncher,
}

impl std::fmt::Debug for InstallFolderPanel {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("InstallFolderPanel")
            .field("root", &self.root)
            .field("is_moving", &self.is_moving)
            .finish_non_exhaustive()
    }
}

impl InstallFolderPanel {
    /// The section for `parts`.
    pub fn new(parts: InstallFolderParts) -> Self {
        let paths = &parts.paths;
        Self {
            root: InstallFolderRow::new("Install folder", paths.root_directory().to_path_buf()),
            folders: vec![
                InstallFolderRow::new("Game", paths.app_directory()),
                InstallFolderRow::new("Plugins", paths.plugins_directory()),
                InstallFolderRow::new("Settings", paths.config_directory.clone()),
                InstallFolderRow::new("Logs", paths.logs_directory()),
            ],
            move_target: String::new(),
            is_moving: false,
            move_status: None,
            move_error: None,
            mover: parts.mover,
            restart_launcher: parts.restart_launcher,
        }
    }

    /// The install folder's full path.
    pub fn root_path(&self) -> &Path {
        &self.root.path
    }

    /// Why the install cannot be moved from here, or `None`.
    pub fn move_unavailable_reason(&self) -> Option<&'static str> {
        self.mover.unavailable_reason()
    }

    /// Whether Move... can run now; `launcher_idle` is false while a
    /// session runs or another operation is busy.
    pub fn can_move(&self, launcher_idle: bool) -> bool {
        !self.is_moving && self.mover.unavailable_reason().is_none() && launcher_idle
    }

    /// Records whether the window could open row `index` (`None`: the
    /// install folder itself).
    pub fn set_open_result(&mut self, index: Option<usize>, opened: bool) {
        let row = match index {
            None => Some(&mut self.root),
            Some(index) => self.folders.get_mut(index),
        };
        if let Some(row) = row {
            row.open_failed = !opened;
        }
    }

    /// Checks a move to the typed folder may start, and marks it started;
    /// the folder to move to, or `None` with the reason shown.
    pub(super) fn begin_move(&mut self, launcher_idle: bool) -> Option<PathBuf> {
        if !self.can_move(launcher_idle) {
            self.move_error = Some(
                self.mover
                    .unavailable_reason()
                    .unwrap_or(MOVE_SESSION_REFUSAL)
                    .to_owned(),
            );
            return None;
        }
        let target = PathBuf::from(self.move_target.trim());
        if let Some(invalid) = self.mover.validate_target(&target) {
            self.move_error = Some(invalid);
            return None;
        }
        self.move_error = None;
        self.is_moving = true;
        self.move_status = Some("Moving the install folder…".into());
        Some(target)
    }

    /// The mover, for the worker.
    pub(super) fn mover(&self) -> Arc<InstallRootMover> {
        Arc::clone(&self.mover)
    }

    /// Progress from the worker.
    pub(super) fn progress(&mut self, entry: &str) {
        if self.is_moving {
            self.move_status = Some(format!("Moving {entry}…"));
        }
    }

    /// The move ended: on success the launcher restarts, since every store
    /// it opened names the old folder.
    pub(super) fn finish_move(&mut self, result: InstallRootMoveResult) {
        self.is_moving = false;
        if !result.moved {
            self.move_status = None;
            self.move_error = Some(result.message);
            return;
        }
        self.move_status = Some(format!("{} The launcher restarts now.", result.message));
        (self.restart_launcher)();
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicUsize, Ordering};

    use super::*;
    use ac_host_core::RootSource;

    struct Fixture {
        scratch: PathBuf,
        default_root: PathBuf,
        restarts: Arc<AtomicUsize>,
    }

    impl Fixture {
        fn new(name: &str) -> Self {
            let scratch = std::env::temp_dir().join(format!(
                "openac-folder-vm-{name}-{}",
                crate::ids::unique_hex32()
            ));
            let default_root = scratch.join("Local").join("OpenAC");
            Self {
                scratch,
                default_root,
                restarts: Arc::new(AtomicUsize::new(0)),
            }
        }

        fn panel(&self) -> InstallFolderPanel {
            let paths = PathSet::for_root(&self.default_root, RootSource::Default);
            let restarts = Arc::clone(&self.restarts);
            InstallFolderPanel::new(InstallFolderParts {
                mover: Arc::new(InstallRootMover::new(
                    &paths,
                    &self.default_root,
                    Some(Box::new(|_: &Path, _: &Path| true)),
                )),
                paths,
                restart_launcher: Arc::new(move || {
                    restarts.fetch_add(1, Ordering::SeqCst);
                }),
            })
        }

        fn seed(&self) {
            let settings = self.default_root.join("settings");
            std::fs::create_dir_all(&settings).unwrap();
            std::fs::write(settings.join("settings.json"), "{}").unwrap();
        }

        fn moved(&self, panel: &mut InstallFolderPanel, target: &Path, idle: bool) {
            panel.move_target = target.display().to_string();
            if let Some(target) = panel.begin_move(idle) {
                let result = panel.mover().move_to(&target, &mut |_| {});
                panel.finish_move(result);
            }
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.scratch);
        }
    }

    // OpenAC InstallFolderViewModelTests.RowsNameTheRootAndTheFoldersAPlayerOpens.
    #[test]
    fn the_rows_name_the_root_and_the_folders_a_player_opens() {
        let fixture = Fixture::new("rows");
        let panel = fixture.panel();
        assert_eq!(panel.root_path(), fixture.default_root);
        let rows: Vec<(&str, PathBuf)> = panel
            .folders
            .iter()
            .map(|row| (row.label, row.path.clone()))
            .collect();
        assert_eq!(
            rows,
            [
                ("Game", fixture.default_root.join("app")),
                ("Plugins", fixture.default_root.join("plugins")),
                ("Settings", fixture.default_root.join("settings")),
                ("Logs", fixture.default_root.join("logs")),
            ]
        );
    }

    // OpenAC InstallFolderViewModelTests.AFolderThatWillNotOpenOffersItsPathToCopy.
    #[test]
    fn a_folder_that_will_not_open_offers_its_path_to_copy() {
        let fixture = Fixture::new("open");
        let mut panel = fixture.panel();
        assert!(panel.folders[3].prepare_open());
        panel.set_open_result(Some(3), true);
        assert!(!panel.folders[3].open_failed);
        panel.set_open_result(Some(3), false);
        assert!(panel.folders[3].open_failed);
    }

    // OpenAC InstallFolderViewModelTests.MoveIsRefusedWhileSomethingRuns.
    #[test]
    fn a_move_is_refused_while_something_runs() {
        let fixture = Fixture::new("busy");
        fixture.seed();
        let mut panel = fixture.panel();
        let target = fixture.scratch.join("Games").join("OpenAC");

        fixture.moved(&mut panel, &target, false);

        assert_eq!(panel.move_error.as_deref(), Some(MOVE_SESSION_REFUSAL));
        assert_eq!(fixture.restarts.load(Ordering::SeqCst), 0);
        let settings = fixture.default_root.join("settings").join("settings.json");
        assert!(settings.is_file());
    }

    // OpenAC InstallFolderViewModelTests.PickingAFolderMovesTheInstallAndRestartsTheLauncher.
    #[test]
    fn a_move_restarts_the_launcher() {
        let fixture = Fixture::new("moves");
        fixture.seed();
        let mut panel = fixture.panel();
        let target = fixture.scratch.join("Games").join("OpenAC");

        fixture.moved(&mut panel, &target, true);

        assert_eq!(panel.move_error, None);
        assert_eq!(fixture.restarts.load(Ordering::SeqCst), 1);
        assert!(target.join("settings").join("settings.json").is_file());
    }

    // OpenAC InstallFolderViewModelTests.AnOccupiedFolderIsRefusedWithAReason.
    #[test]
    fn an_occupied_folder_is_refused_with_a_reason() {
        let fixture = Fixture::new("occupied");
        fixture.seed();
        let occupied = fixture.scratch.join("occupied");
        std::fs::create_dir_all(&occupied).unwrap();
        std::fs::write(occupied.join("x"), "x").unwrap();
        let mut panel = fixture.panel();

        fixture.moved(&mut panel, &occupied, true);

        assert!(panel.move_error.is_some());
        assert_eq!(fixture.restarts.load(Ordering::SeqCst), 0);
    }

    // OpenAC InstallFolderViewModelTests.AnUnusablePathIsReportedNotThrown.
    #[test]
    fn an_unusable_path_is_reported_not_thrown() {
        let fixture = Fixture::new("unusable");
        let mut panel = fixture.panel();

        fixture.moved(&mut panel, &fixture.scratch.join("bad\0name"), true);

        assert!(panel.move_error.is_some());
        assert_eq!(fixture.restarts.load(Ordering::SeqCst), 0);
    }
}
