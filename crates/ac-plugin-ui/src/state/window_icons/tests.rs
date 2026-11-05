//! A window's own dock icon and its failure rules, on a fake folder.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use crate::file_images::{FileImageLoader, FileImageRead};
use crate::icon::PluginIcon;
use crate::model::{PanelDescriptor, UiOwner};
use crate::state::PluginUi;
use crate::value::BindingSchema;

/// What a file on the fake folder comes to.
#[derive(Debug, Clone)]
enum Entry {
    Icon(u8),
    Refused(&'static str),
}

#[derive(Debug, Default)]
struct Disk {
    files: Mutex<BTreeMap<String, Entry>>,
    reads: AtomicUsize,
}

#[derive(Debug, Clone)]
struct Reader(Arc<Disk>);

impl FileImageLoader for Reader {
    fn load(&self, _directory: &Path, _path: &str) -> FileImageRead {
        FileImageRead::finished(Err("not used here".to_owned()))
    }

    fn read_dock_icon(&self, _directory: &Path, path: &str) -> Result<Option<PluginIcon>, String> {
        self.0.reads.fetch_add(1, Ordering::Relaxed);
        match self.0.files.lock().unwrap().get(path) {
            Some(Entry::Icon(fill)) => Ok(Some(icon(*fill))),
            Some(Entry::Refused(reason)) => Err((*reason).to_owned()),
            None => Ok(None),
        }
    }
}

fn icon(fill: u8) -> PluginIcon {
    PluginIcon {
        width: 64,
        height: 64,
        rgba: Arc::new(vec![fill; 64 * 64 * 4]),
    }
}

struct Fixture {
    ui: PluginUi,
    disk: Arc<Disk>,
}

const OWNER: &str = "acme.hello";

impl Fixture {
    fn new(files: &[(&str, Entry)]) -> Self {
        let disk = Arc::new(Disk::default());
        for (path, entry) in files {
            let mut held = disk.files.lock().unwrap();
            held.insert((*path).to_owned(), entry.clone());
        }
        let mut ui = PluginUi::new();
        ui.set_file_images(Some(Arc::new(Reader(Arc::clone(&disk)))));
        ui.set_plugin_folder(OWNER, Some(PathBuf::from("plugin")));
        Self { ui, disk }
    }

    fn register(&mut self, window: &str, file: Option<&str>) -> u32 {
        let descriptor = PanelDescriptor {
            icon_file: file.map(str::to_owned),
            ..PanelDescriptor::new(window, window)
        };
        self.ui
            .register_panel(
                &UiOwner::new(OWNER, "Hello"),
                descriptor,
                "<panel/>",
                BindingSchema::new("B"),
            )
            .unwrap()
    }

    fn window_icon(&self, panel: u32) -> Option<crate::model::WindowIcon> {
        let view = self.ui.panels().find(|p| p.id == panel).unwrap();
        view.window_icon.clone()
    }

    fn reports(&mut self) -> Vec<String> {
        self.ui
            .take_reports()
            .into_iter()
            .map(|(_, line)| line)
            .collect()
    }
}

fn fill(icon: Option<&PluginIcon>) -> Option<u8> {
    icon.map(|i| i.rgba[0])
}

#[test]
fn a_window_that_names_no_file_reads_nothing() {
    let mut f = Fixture::new(&[]);
    let panel = f.register("main", None);
    assert!(f.window_icon(panel).is_none());
    assert_eq!(f.disk.reads.load(Ordering::Relaxed), 0);
    assert!(f.reports().is_empty());
}

#[test]
fn the_named_file_is_carried_for_every_theme() {
    let mut f = Fixture::new(&[("icons/a.png", Entry::Icon(10))]);
    let panel = f.register("main", Some("icons/a.png"));
    let icon = f.window_icon(panel).unwrap();
    assert_eq!(fill(icon.for_theme(false)), Some(10));
    assert_eq!(fill(icon.for_theme(true)), Some(10));
    assert!(f.reports().is_empty(), "a missing version is not reported");
}

#[test]
fn each_theme_takes_its_version_when_it_is_there() {
    let mut f = Fixture::new(&[
        ("icons/a.png", Entry::Icon(10)),
        ("icons/a.light.png", Entry::Icon(20)),
        ("icons/a.dark.png", Entry::Icon(30)),
    ]);
    let panel = f.register("main", Some("icons/a.png"));
    let icon = f.window_icon(panel).unwrap();
    assert_eq!(fill(icon.for_theme(true)), Some(20));
    assert_eq!(fill(icon.for_theme(false)), Some(30));
}

#[test]
fn a_version_that_cannot_be_used_falls_back_to_the_named_file_and_is_reported() {
    let mut f = Fixture::new(&[
        ("icons/a.png", Entry::Icon(10)),
        ("icons/a.light.png", Entry::Refused("must be 64x64")),
    ]);
    let panel = f.register("main", Some("icons/a.png"));
    let icon = f.window_icon(panel).unwrap();
    assert_eq!(fill(icon.for_theme(true)), Some(10));
    assert_eq!(
        f.reports(),
        ["plugin icon 'acme.hello/icons/a.light.png' ignored: must be 64x64"]
    );
}

#[test]
fn a_version_alone_is_used_under_its_theme_and_leaves_the_other_to_the_next_icon() {
    let mut f = Fixture::new(&[("icons/a.light.png", Entry::Icon(20))]);
    let panel = f.register("main", Some("icons/a.png"));
    let icon = f.window_icon(panel).unwrap();
    assert_eq!(fill(icon.for_theme(true)), Some(20));
    assert_eq!(fill(icon.for_theme(false)), None);
    assert!(f.reports().is_empty());
}

#[test]
fn a_file_that_cannot_be_used_leaves_nothing_and_is_logged_once() {
    let mut f = Fixture::new(&[("bad.png", Entry::Refused("must be 64x64"))]);
    let first = f.register("one", Some("bad.png"));
    let second = f.register("two", Some("bad.png"));
    assert!(f.window_icon(first).is_none());
    assert!(f.window_icon(second).is_none());
    assert_eq!(
        f.reports(),
        ["plugin icon 'acme.hello/bad.png' ignored: must be 64x64"]
    );
}

#[test]
fn a_path_refused_with_its_versions_for_the_same_reason_is_logged_once() {
    let mut f = Fixture::new(&[
        ("a.svg", Entry::Refused("is not a .png file")),
        ("a.light.svg", Entry::Refused("is not a .png file")),
        ("a.dark.svg", Entry::Refused("is not a .png file")),
    ]);
    let panel = f.register("main", Some("a.svg"));
    assert!(f.window_icon(panel).is_none());
    assert_eq!(
        f.reports(),
        ["plugin icon 'acme.hello/a.svg' ignored: is not a .png file"]
    );
}

#[test]
fn a_missing_file_is_logged_once_with_its_path_as_written() {
    let mut f = Fixture::new(&[]);
    let panel = f.register("main", Some(r"icons\gone.png"));
    assert!(f.window_icon(panel).is_none());
    assert_eq!(
        f.reports(),
        ["plugin icon 'acme.hello/icons/gone.png' ignored: does not exist"]
    );
}

#[test]
fn a_plugin_with_no_folder_ignores_the_file_and_says_nothing() {
    let mut f = Fixture::new(&[("icons/a.png", Entry::Icon(10))]);
    f.ui.set_plugin_folder(OWNER, None);
    let panel = f.register("main", Some("icons/a.png"));
    assert!(f.window_icon(panel).is_none());
    assert_eq!(f.disk.reads.load(Ordering::Relaxed), 0);
    assert!(f.reports().is_empty());
}

#[test]
fn a_host_that_reads_no_files_ignores_the_file() {
    let mut f = Fixture::new(&[("icons/a.png", Entry::Icon(10))]);
    f.ui.set_file_images(None);
    let panel = f.register("main", Some("icons/a.png"));
    assert!(f.window_icon(panel).is_none());
    assert!(f.reports().is_empty());
}

#[test]
fn windows_naming_the_same_file_share_its_pixels_until_it_changes() {
    let mut f = Fixture::new(&[("a.png", Entry::Icon(10))]);
    let first = f.register("one", Some("a.png"));
    let second = f.register("two", Some("a.png"));
    let held = |f: &Fixture, panel| f.window_icon(panel).unwrap().named.unwrap().rgba;
    assert!(Arc::ptr_eq(&held(&f, first), &held(&f, second)));

    f.disk
        .files
        .lock()
        .unwrap()
        .insert("a.png".to_owned(), Entry::Icon(11));
    let third = f.register("three", Some("a.png"));
    assert_eq!(held(&f, third)[0], 11);
    assert_eq!(held(&f, first)[0], 10, "an open window keeps what it has");
}

#[test]
fn releasing_the_plugin_lets_go_of_its_icons() {
    let mut f = Fixture::new(&[("a.png", Entry::Icon(10))]);
    let panel = f.register("main", Some("a.png"));
    let pixels = f.window_icon(panel).unwrap().named.unwrap().rgba;
    f.ui.release(OWNER);
    // The window's copies of the changes hold it until they are taken.
    let _ = f.ui.take_changes();
    assert_eq!(Arc::strong_count(&pixels), 1);
}
