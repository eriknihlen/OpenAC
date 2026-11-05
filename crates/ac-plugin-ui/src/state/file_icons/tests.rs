//! Ported from OpenAC PR #271's `tests/AcDream.App.Tests/UI/PluginMarkupIconResolverTests.cs`:
//! one plugin window's file icons, read in the background, held in the
//! plugin's image table for as long as the window draws them, and let go
//! when the window goes, without taking them from another window. A draw is
//! the window saying which files it shows, then the session's clock moving;
//! the reader here is a stand-in that makes images of a size per path (the
//! real reader, its path rules and its spellings are tested in `ac-plugin`).

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use super::*;
use crate::file_images::{FileImage, FileImageResult};
use crate::images::ImageBudget;
use crate::model::{PanelDescriptor, UiInput, UiOwner};
use crate::value::BindingSchema;

/// Files by path: their size and version; a path not here does not exist.
#[derive(Debug, Default)]
struct Disk {
    files: Mutex<BTreeMap<String, (u32, u32, u32)>>,
    reads: AtomicUsize,
    hold: AtomicBool,
    held: Mutex<Vec<(FileImageRead, FileImageResult)>>,
}

#[derive(Debug, Clone)]
struct Reader(Arc<Disk>);

impl FileImageLoader for Reader {
    fn load(&self, _directory: &Path, path: &str) -> FileImageRead {
        self.0.reads.fetch_add(1, Ordering::Relaxed);
        let result = match self.0.files.lock().unwrap().get(path).copied() {
            Some((width, height, version)) => Ok(Arc::new(FileImage {
                path: path.to_owned(),
                version: version.to_string(),
                levels: crate::mips::build(vec![0; (width * height * 4) as usize], width, height),
                full_path: None,
            })),
            None => Err("does not exist".to_owned()),
        };
        if self.0.hold.load(Ordering::Relaxed) {
            let read = FileImageRead::pending();
            self.0.held.lock().unwrap().push((read.clone(), result));
            read
        } else {
            FileImageRead::finished(result)
        }
    }

    fn has_file(&self, _directory: &Path, path: &str) -> bool {
        self.0.files.lock().unwrap().contains_key(path)
    }

    fn is_current(&self, image: &FileImage) -> bool {
        self.0
            .files
            .lock()
            .unwrap()
            .get(&image.path)
            .is_some_and(|f| f.2.to_string() == image.version)
    }
}

impl Disk {
    fn write(&self, path: &str, width: u32, height: u32) {
        let mut files = self.files.lock().unwrap();
        let version = files.get(path).map_or(1, |f| f.2 + 1);
        files.insert(path.to_owned(), (width, height, version));
    }

    fn finish_reads(&self) {
        for (read, result) in self.held.lock().unwrap().drain(..) {
            read.finish(result);
        }
    }

    fn reads(&self) -> usize {
        self.reads.load(Ordering::Relaxed)
    }
}

struct Fixture {
    ui: PluginUi,
    disk: Arc<Disk>,
    now: u64,
}

fn owner() -> UiOwner {
    UiOwner::new("acme.hello", "Hello")
}

impl Fixture {
    fn new() -> Self {
        Self::with_budget(ImageBudget::default())
    }

    fn with_budget(budget: ImageBudget) -> Self {
        let disk = Arc::new(Disk::default());
        disk.write("icons/sword.png", 20, 10);
        let mut ui = PluginUi::new();
        ui.set_image_budget(budget);
        ui.set_window_attached(true);
        ui.set_file_images(Some(Arc::new(Reader(Arc::clone(&disk)))));
        ui.set_plugin_folder("acme.hello", Some(PathBuf::from("plugin")));
        Self {
            ui,
            disk,
            now: 1_000_000,
        }
    }

    fn window(&mut self, id: &str) -> u32 {
        self.ui
            .register_panel(
                &owner(),
                PanelDescriptor::new(id, id),
                "<panel/>",
                BindingSchema::new("Binding"),
            )
            .unwrap()
    }

    /// The window draws `paths` in `panel` this frame.
    fn draw(&mut self, panel: u32, paths: &[&str]) {
        self.ui.input(UiInput::FileIconsShown {
            panel,
            paths: paths.iter().map(|p| (*p).to_owned()).collect(),
        });
        self.ui.tick_file_icons(self.now);
    }

    /// The window draws one file: the image it gets, if any.
    fn resolve(&mut self, panel: u32, path: &str) -> Option<(u32, u32, u32)> {
        self.draw(panel, &[path]);
        self.ui.file_icon(panel, path)
    }

    fn advance(&mut self, ms: u64) {
        self.now += ms;
        self.ui.tick_file_icons(self.now);
    }

    fn reports(&mut self) -> Vec<String> {
        self.ui
            .take_reports()
            .into_iter()
            .map(|(_, line)| line)
            .collect()
    }

    fn count(&self) -> usize {
        self.ui.image_count("acme.hello")
    }

    fn owned_bytes(&self) -> u64 {
        self.ui
            .images
            .get("acme.hello")
            .map_or(0, |t| t.owned_bytes())
    }

    fn added(&mut self) -> usize {
        self.ui
            .take_changes()
            .iter()
            .filter(|c| matches!(c, UiChange::ImageAdded(_)))
            .count()
    }
}

// AFileDrawsNothingWhileItIsReadAndDrawsOnceItIsReady.
#[test]
fn a_file_draws_nothing_while_it_is_read_and_draws_once_it_is_ready() {
    let mut f = Fixture::new();
    f.disk.hold.store(true, Ordering::Relaxed);
    let panel = f.window("hello");
    assert_eq!(f.resolve(panel, "icons/sword.png"), None);
    assert_eq!(f.count(), 0);
    f.disk.finish_reads();
    let (handle, w, h) = f.resolve(panel, "icons/sword.png").unwrap();
    assert_ne!(handle, 0);
    assert_eq!((w, h), (20, 10));
    // The window hears which image the path draws.
    let changes = f.ui.take_changes();
    assert!(
        changes
            .iter()
            .any(|c| matches!(c, UiChange::ImageAdded(v) if v.handle == handle))
    );
    assert!(changes.iter().any(|c| matches!(c,
        UiChange::FileIcon { panel: p, path, handle: h } if *p == panel && path == "icons/sword.png" && *h == handle)));
}

// AFileIsReadAndUploadedOnceHoweverOftenItIsDrawn.
#[test]
fn a_file_is_read_and_held_once_however_often_it_is_drawn() {
    let mut f = Fixture::new();
    let panel = f.window("hello");
    let first = f.resolve(panel, "icons/sword.png").unwrap();
    assert_eq!(f.resolve(panel, "icons/sword.png"), Some(first));
    f.advance(16);
    assert_eq!(f.disk.reads(), 1);
    assert_eq!(f.added(), 1);
    assert_eq!(f.count(), 1);
}

// AFileIsUploadedWithItsSmallerLevelsInThePluginsBudget.
#[test]
fn a_file_is_held_with_its_smaller_levels_in_the_plugins_budget() {
    let mut f = Fixture::new();
    let panel = f.window("hello");
    f.resolve(panel, "icons/sword.png").unwrap();
    // 20x10, 10x5, 5x2, 2x1, 1x1.
    assert_eq!(f.owned_bytes(), (20 * 10 + 10 * 5 + 5 * 2 + 2 + 1) * 4);
}

// AMissingFileIsLookedUpOnceAndReportedOnce.
#[test]
fn a_missing_file_is_looked_up_once_and_reported_once() {
    let mut f = Fixture::new();
    let panel = f.window("hello");
    assert_eq!(f.resolve(panel, "icons/gone.png"), None);
    assert_eq!(f.resolve(panel, "icons/gone.png"), None);
    assert_eq!(f.disk.reads(), 1);
    let reports = f.reports();
    assert_eq!(reports.len(), 1, "{reports:?}");
    assert!(
        reports[0].contains("acme.hello/icons/gone.png"),
        "{reports:?}"
    );
    assert!(reports[0].contains("does not exist"), "{reports:?}");
}

// AWindowWithNoPluginFolderDrawsNoFilesAndSaysWhyOnce.
#[test]
fn a_window_with_no_plugin_folder_draws_no_files_and_says_why_once() {
    let mut f = Fixture::new();
    f.ui.set_plugin_folder("acme.hello", None);
    let panel = f.window("hello");
    assert_eq!(f.resolve(panel, "icons/sword.png"), None);
    assert_eq!(f.resolve(panel, "icons/sword.png"), None);
    let reports = f.reports();
    assert_eq!(reports.len(), 1, "{reports:?}");
    assert!(reports[0].contains("no plugin folder"), "{reports:?}");
    assert_eq!(f.disk.reads(), 0);
}

// AnUnmountedWindowLetsGoOfItsImages.
#[test]
fn an_unmounted_window_lets_go_of_its_images() {
    let mut f = Fixture::new();
    let panel = f.window("hello");
    let (handle, ..) = f.resolve(panel, "icons/sword.png").unwrap();
    f.ui.take_changes();
    assert!(f.ui.remove_panel("acme.hello", panel));
    assert!(!f.ui.remove_panel("acme.hello", panel));
    assert!(f.ui.take_changes().iter().any(|c| matches!(c,
        UiChange::ImageRemoved { handle: h, .. } if *h == handle)));
    assert_eq!(f.count(), 0);
    assert_eq!(f.owned_bytes(), 0);
    assert_eq!(f.resolve(panel, "icons/sword.png"), None);
}

// AWindowUnmountedWhileItsFileIsReadNeverHoldsIt.
#[test]
fn a_window_unmounted_while_its_file_is_read_never_holds_it() {
    let mut f = Fixture::new();
    f.disk.hold.store(true, Ordering::Relaxed);
    let panel = f.window("hello");
    f.resolve(panel, "icons/sword.png");
    f.ui.remove_panel("acme.hello", panel);
    f.disk.finish_reads();
    f.resolve(panel, "icons/sword.png");
    assert_eq!(f.count(), 0);
    assert_eq!(f.added(), 0);
}

// TwoWindowsShowingAFileThatChangesEachKeepDrawingUntilRebuilt.
#[test]
fn two_windows_showing_a_file_that_changes_each_keep_drawing_until_rebuilt() {
    let mut f = Fixture::new();
    let first = f.window("first");
    let second = f.window("second");
    let old = f.resolve(first, "icons/sword.png").unwrap();
    assert_eq!(f.resolve(second, "icons/sword.png"), Some(old));
    f.disk.write("icons/sword.png", 40, 20);
    // The first window is rebuilt: the old one goes, a new one reads the
    // new file.
    f.ui.remove_panel("acme.hello", first);
    let rebuilt = f.window("first");
    let fresh = f.resolve(rebuilt, "icons/sword.png").unwrap();
    assert_ne!(fresh.0, old.0);
    assert_eq!((fresh.1, fresh.2), (40, 20));
    assert_eq!(f.ui.file_icon(second, "icons/sword.png"), Some(old));
    assert_eq!(f.count(), 2);
}

fn budget(count: usize) -> ImageBudget {
    ImageBudget {
        maximum_count: count,
        maximum_bytes: 1024 * 1024,
        maximum_dimension: 2048,
    }
}

// FileIconsAreRefusedPastThePluginsImageCount.
#[test]
fn file_icons_are_refused_past_the_plugins_image_count() {
    let mut f = Fixture::with_budget(budget(1));
    f.disk.write("icons/shield.png", 4, 4);
    let panel = f.window("hello");
    assert!(f.resolve(panel, "icons/sword.png").is_some());
    f.draw(panel, &["icons/sword.png", "icons/shield.png"]);
    assert_eq!(f.ui.file_icon(panel, "icons/shield.png"), None);
    let reports = f.reports();
    assert!(
        reports.iter().any(|r| r.contains("1 images")),
        "{reports:?}"
    );
}

// ABoundIconRunningThroughManyFilesKeepsOnlyRecentOnesHeld.
#[test]
fn a_bound_icon_running_through_many_files_keeps_only_recent_ones_held() {
    // A small budget, so a window holding every file it ever drew would
    // fill it.
    let mut f = Fixture::with_budget(budget(8));
    for i in 0..40 {
        f.disk.write(&format!("icons/status{i}.png"), 4, 4);
    }
    let panel = f.window("hello");
    // The bound path names a new file every second, and the window draws it.
    for i in 0..40 {
        let path = format!("icons/status{i}.png");
        assert!(f.resolve(panel, &path).is_some(), "file {i}");
        f.advance(RELEASE_CHECK_INTERVAL_MS);
        assert!(
            f.count() <= 6,
            "the table holds {} after file {i}",
            f.count()
        );
    }
    let reports = f.reports();
    assert!(
        !reports.iter().any(|r| r.contains("refused")),
        "{reports:?}"
    );
    assert!(f.ui.remembered_file_count(panel) <= 6);
}

// AFileLetGoForNotBeingDrawnIsReadAgainWhenDrawnAgain.
#[test]
fn a_file_let_go_for_not_being_drawn_is_read_again_when_drawn_again() {
    let mut f = Fixture::new();
    let panel = f.window("hello");
    let first = f.resolve(panel, "icons/sword.png").unwrap();
    // The window stops drawing it.
    f.draw(panel, &[]);
    // Not drawn for four one-second ticks: kept.
    for _ in 0..4 {
        f.advance(RELEASE_CHECK_INTERVAL_MS);
    }

    assert_eq!(f.count(), 1);
    // Not drawn for the whole wait: let go.
    f.ui.take_changes();
    f.advance(RELEASE_CHECK_INTERVAL_MS);
    assert_eq!(f.count(), 0);
    assert_eq!(f.ui.remembered_file_count(panel), 0);
    let changes = f.ui.take_changes();
    assert!(changes.iter().any(|c| matches!(c,
        UiChange::FileIcon { handle: 0, path, .. } if path == "icons/sword.png")));
    let again = f.resolve(panel, "icons/sword.png").unwrap();
    assert_ne!(again.0, first.0);
    assert_eq!(f.disk.reads(), 2);
    assert_eq!(f.count(), 1);
}

// A window that goes on drawing a file keeps it however long it draws it.
#[test]
fn a_file_drawn_on_is_never_let_go() {
    let mut f = Fixture::new();
    let panel = f.window("hello");
    let first = f.resolve(panel, "icons/sword.png").unwrap();
    for _ in 0..20 {
        f.advance(RELEASE_CHECK_INTERVAL_MS);
    }
    assert_eq!(f.ui.file_icon(panel, "icons/sword.png"), Some(first));
    assert_eq!(f.disk.reads(), 1);
}

// AFileRefusedForWantOfRoomDrawsOnceAnotherIsLetGo.
#[test]
fn a_file_refused_for_want_of_room_draws_once_another_is_let_go() {
    let mut f = Fixture::with_budget(budget(1));
    f.disk.write("icons/shield.png", 4, 4);
    let panel = f.window("hello");
    assert!(f.resolve(panel, "icons/sword.png").is_some());
    // The sword stops being drawn; the shield goes on being drawn.
    assert_eq!(f.resolve(panel, "icons/shield.png"), None);
    let mut waited = 0;
    while waited <= RELEASE_AFTER_MS {
        f.advance(RELEASE_CHECK_INTERVAL_MS);
        waited += RELEASE_CHECK_INTERVAL_MS;
    }
    let (_, w, h) = f.resolve(panel, "icons/shield.png").unwrap();
    assert_eq!((w, h), (4, 4));
    assert_eq!(f.count(), 1);
}

// AFileRefusedForWantOfRoomDrawsOnceAnotherWindowGoes.
#[test]
fn a_file_refused_for_want_of_room_draws_once_another_window_goes() {
    let mut f = Fixture::with_budget(budget(1));
    f.disk.write("icons/shield.png", 4, 4);
    let first = f.window("first");
    let second = f.window("second");
    assert!(f.resolve(first, "icons/sword.png").is_some());
    assert_eq!(f.resolve(second, "icons/shield.png"), None);
    f.ui.remove_panel("acme.hello", first);
    f.advance(RELEASE_CHECK_INTERVAL_MS);
    let (_, w, h) = f.resolve(second, "icons/shield.png").unwrap();
    assert_eq!((w, h), (4, 4));
}

// AnImageThePluginsTableLetGoOfDrawsNothing: here the client window
// leaving, which lets go of everything it drew (OpenAC: the interface
// unbinding the table).
#[test]
fn a_detached_window_lets_go_of_every_file_icon() {
    let mut f = Fixture::new();
    let panel = f.window("hello");
    f.resolve(panel, "icons/sword.png").unwrap();
    f.ui.set_window_attached(false);
    assert_eq!(f.count(), 0);
    assert_eq!(f.ui.file_icon(panel, "icons/sword.png"), None);
    // Without a window nothing draws, so nothing is read.
    assert_eq!(f.resolve(panel, "icons/sword.png"), None);
    assert_eq!(f.disk.reads(), 1);
}

// A plugin unloaded (or reloaded) lets go of its windows' file icons.
#[test]
fn an_unloaded_plugin_lets_go_of_its_file_icons() {
    let mut f = Fixture::new();
    let panel = f.window("hello");
    f.resolve(panel, "icons/sword.png").unwrap();
    f.ui.release("acme.hello");
    assert_eq!(f.count(), 0);
    assert_eq!(f.ui.remembered_file_count(panel), 0);
}

// A window that attaches hears which images the file icons draw.
#[test]
fn a_snapshot_carries_the_file_icons_held() {
    let mut f = Fixture::new();
    let panel = f.window("hello");
    let (handle, ..) = f.resolve(panel, "icons/sword.png").unwrap();
    let mut view = crate::view::PluginUiView::new();
    view.apply_all(f.ui.snapshot());
    assert_eq!(
        view.file_icon(panel, "icons/sword.png").map(|v| v.handle),
        Some(handle)
    );
}

// AFileRefusedForWantOfRoomIsReadOnceAndOfferedAtMostOncePerInterval.
#[test]
fn a_file_refused_for_want_of_room_is_read_once_and_offered_at_most_once_per_interval() {
    let mut f = Fixture::with_budget(budget(1));
    f.disk.write("icons/shield.png", 4, 4);
    let panel = f.window("hello");
    let own = |ui: &mut PluginUi| ui.image_rgba("acme.hello", "own", 2, 2, vec![0; 16]).0;
    let mut held = own(&mut f.ui);
    assert_ne!(held, 0);
    assert_eq!(f.resolve(panel, "icons/shield.png"), None);
    // The plugin lets go of its own image and takes it back every tick, so
    // the table keeps moving and stays full. Three seconds of 16 ms ticks.
    for _ in 0..3000 / 16 {
        f.advance(16);
        assert!(f.ui.image_release("acme.hello", held));
        held = own(&mut f.ui);
        assert_eq!(f.resolve(panel, "icons/shield.png"), None);
    }
    assert_eq!(f.disk.reads(), 1);
    let retries = f.ui.file_room_retries(panel);
    assert!((1..=3).contains(&retries), "{retries} retries");
    // Room at last: the kept image goes in at the next offer, still without
    // a read.
    assert!(f.ui.image_release("acme.hello", held));
    f.advance(RELEASE_CHECK_INTERVAL_MS);
    assert!(f.resolve(panel, "icons/shield.png").is_some());
    assert_eq!(f.disk.reads(), 1);
}

// AKeptImageWhoseFileChangedIsReadAgain.
#[test]
fn a_kept_image_whose_file_changed_is_read_again() {
    let mut f = Fixture::with_budget(budget(1));
    f.disk.write("icons/shield.png", 4, 4);
    let panel = f.window("hello");
    let (own, ..) = f.ui.image_rgba("acme.hello", "own", 2, 2, vec![0; 16]);
    assert_eq!(f.resolve(panel, "icons/shield.png"), None);
    f.disk.write("icons/shield.png", 8, 8);
    assert!(f.ui.image_release("acme.hello", own));
    f.advance(RELEASE_CHECK_INTERVAL_MS);
    let (_, w, h) = f.resolve(panel, "icons/shield.png").unwrap();
    assert_eq!((w, h), (8, 8));
    assert_eq!(f.disk.reads(), 2);
}

// AStallOfTheWholeInterfaceDoesNotLetGoOfWhatWasDrawnBeforeIt.
#[test]
fn a_stall_of_the_whole_session_does_not_let_go_of_what_was_drawn_before_it() {
    let mut f = Fixture::new();
    let panel = f.window("hello");
    let first = f.resolve(panel, "icons/sword.png").unwrap();
    f.advance(16);
    f.resolve(panel, "icons/sword.png");
    // The window stops drawing it just as the session stops for ten
    // seconds; the next tick finds it drawn just before.
    f.draw(panel, &[]);
    f.advance(10_000);
    assert_eq!(f.count(), 1);
    assert_eq!(f.ui.file_icon(panel, "icons/sword.png"), Some(first));
    // Truly not drawn for five seconds of ticks afterwards: let go.
    for _ in 0..4 {
        f.advance(RELEASE_CHECK_INTERVAL_MS);
    }
    assert_eq!(f.count(), 0);
}

// AReasonIsReportedOncePerFileAndReason.
#[test]
fn a_reason_is_reported_once_per_file_and_reason() {
    let mut f = Fixture::new();
    f.ui.report_file("acme.hello", "icons/missing.png", "does not exist");
    f.ui.report_file("acme.hello", r"icons\missing.png", "does not exist");
    f.ui.report_file("acme.hello", "icons/missing.png", "could not be read");
    let reports = f.reports();
    assert_eq!(reports.len(), 2, "{reports:?}");
    assert!(
        reports[0].contains("acme.hello/icons/missing.png"),
        "{reports:?}"
    );
    assert!(reports[0].contains("does not exist"), "{reports:?}");
}

// PluginMarkupIconResolverTests: a file's light and dark versions, and the
// PluginUiThemeTests rule that only a themed window follows the theme.
impl Fixture {
    fn themed_window(&mut self, id: &str) -> u32 {
        self.ui
            .register_panel(
                &owner(),
                PanelDescriptor::new(id, id),
                r#"<panel theme="plugin"/>"#,
                BindingSchema::new("Binding"),
            )
            .unwrap()
    }

    fn theme(&mut self, theme: crate::theme::ThemeKind) {
        self.ui.input(UiInput::Theme(theme));
    }
}

#[test]
fn a_light_theme_draws_the_light_file_when_there_is_one() {
    use crate::theme::ThemeKind;
    let mut f = Fixture::new();
    f.disk.write("icons/sword.light.png", 12, 12);
    let panel = f.themed_window("hello");
    f.draw(panel, &["icons/sword.png"]);
    let size = |f: &Fixture| f.ui.file_icon(panel, "icons/sword.png").map(|i| (i.1, i.2));
    assert_eq!(size(&f), Some((20, 10)));
    f.theme(ThemeKind::Parchment);
    f.draw(panel, &["icons/sword.png"]);
    assert_eq!(size(&f), Some((12, 12)));
    f.theme(ThemeKind::Moss);
    f.draw(panel, &["icons/sword.png"]);
    assert_eq!(size(&f), Some((20, 10)));
    assert!(f.ui.take_reports().is_empty());
}

#[test]
fn a_dark_theme_and_classic_draw_the_dark_file_when_there_is_one() {
    use crate::theme::ThemeKind;
    let mut f = Fixture::new();
    f.disk.write("icons/sword.dark.png", 14, 14);
    let panel = f.themed_window("hello");
    let size = |f: &Fixture| f.ui.file_icon(panel, "icons/sword.png").map(|i| (i.1, i.2));
    f.draw(panel, &["icons/sword.png"]);
    assert_eq!(size(&f), Some((14, 14)), "Classic");
    f.theme(ThemeKind::Parchment);
    f.draw(panel, &["icons/sword.png"]);
    assert_eq!(size(&f), Some((20, 10)), "no light file");
    f.theme(ThemeKind::Glacier);
    f.draw(panel, &["icons/sword.png"]);
    assert_eq!(size(&f), Some((14, 14)));
}

#[test]
fn a_window_that_did_not_opt_in_always_draws_the_dark_file() {
    use crate::theme::ThemeKind;
    let mut f = Fixture::new();
    f.disk.write("icons/sword.light.png", 12, 12);
    f.disk.write("icons/sword.dark.png", 14, 14);
    let panel = f.window("hello");
    f.theme(ThemeKind::Parchment);
    f.draw(panel, &["icons/sword.png"]);
    let icon = f.ui.file_icon(panel, "icons/sword.png").unwrap();
    assert_eq!((icon.1, icon.2), (14, 14));
}

#[test]
fn without_a_version_the_file_itself_is_drawn_and_nothing_is_reported() {
    use crate::theme::ThemeKind;
    let mut f = Fixture::new();
    let panel = f.themed_window("hello");
    f.theme(ThemeKind::Parchment);
    f.draw(panel, &["icons/sword.png"]);
    f.draw(panel, &["icons/sword.png"]);
    let icon = f.ui.file_icon(panel, "icons/sword.png").unwrap();
    assert_eq!((icon.1, icon.2), (20, 10));
    assert_eq!(f.disk.reads(), 1);
    assert!(f.ui.take_reports().is_empty());
}

#[test]
fn a_change_of_theme_keeps_the_old_picture_until_the_new_one_is_ready() {
    use crate::theme::ThemeKind;
    let mut f = Fixture::new();
    f.disk.write("icons/sword.light.png", 12, 12);
    let panel = f.themed_window("hello");
    f.draw(panel, &["icons/sword.png"]);
    let dark = f.ui.file_icon(panel, "icons/sword.png").unwrap();
    f.disk.hold.store(true, Ordering::Relaxed);
    f.theme(ThemeKind::Parchment);
    f.draw(panel, &["icons/sword.png"]);
    assert_eq!(f.ui.file_icon(panel, "icons/sword.png"), Some(dark));
    f.disk.finish_reads();
    f.draw(panel, &["icons/sword.png"]);
    let light = f.ui.file_icon(panel, "icons/sword.png").unwrap();
    assert_eq!((light.1, light.2), (12, 12));
    // The old picture goes once it has not been drawn for a while, and the
    // window keeps the new one.
    for _ in 0..7 {
        f.now += RELEASE_CHECK_INTERVAL_MS;
        f.draw(panel, &["icons/sword.png"]);
    }
    assert_eq!(f.ui.file_icon(panel, "icons/sword.png"), Some(light));
    assert_eq!(f.ui.image_count("acme.hello"), 1);
}

/// A reader for which the light version is there and cannot be used.
#[derive(Debug)]
struct BadLightFile(Reader);

impl FileImageLoader for BadLightFile {
    fn load(&self, directory: &Path, path: &str) -> FileImageRead {
        if path.ends_with(".light.png") {
            return FileImageRead::finished(Err("is not an image".to_owned()));
        }
        self.0.load(directory, path)
    }
    fn has_file(&self, _directory: &Path, path: &str) -> bool {
        path.ends_with(".light.png") || self.0.0.files.lock().unwrap().contains_key(path)
    }
    fn is_current(&self, image: &FileImage) -> bool {
        self.0.is_current(image)
    }
}

#[test]
fn a_version_that_cannot_be_used_falls_back_to_the_file_and_is_reported_once() {
    use crate::theme::ThemeKind;
    let mut f = Fixture::new();
    f.ui.set_file_images(Some(Arc::new(BadLightFile(Reader(Arc::clone(&f.disk))))));
    let panel = f.themed_window("hello");
    f.theme(ThemeKind::Parchment);
    f.draw(panel, &["icons/sword.png"]);
    f.draw(panel, &["icons/sword.png"]);
    let icon = f.ui.file_icon(panel, "icons/sword.png").unwrap();
    assert_eq!((icon.1, icon.2), (20, 10));
    let reports = f.ui.take_reports();
    assert_eq!(reports.len(), 1, "{reports:?}");
    assert!(reports[0].1.contains("sword.light.png"), "{reports:?}");
}
