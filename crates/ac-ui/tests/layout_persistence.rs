//! Window layout persistence.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/RetailWindowLayoutPersistenceTests.cs`
//! (with its `.Pregame`, `.SidePanels` and `.Viewport` parts). OpenAC saves
//! inside each window event; here the persistence follows the windows once
//! per frame, so the tests call [`LayoutPersistence::follow`] where OpenAC's
//! event would have fired, and set the screen size after following. The
//! markup-panel cases use plain frames with the revision OpenAC's markup
//! derives.

use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};

use ac_host::settings::{SettingsStore, UiWindowLayout};
use ac_ui::element_info::ElementInfo;
use ac_ui::geometry::ResizeEdges;
use ac_ui::host::{PanelController, UiHost};
use ac_ui::layout_persistence::{LayoutPersistence, WindowState};
use ac_ui::panels::main_panels::MainPanels;
use ac_ui::root::{UiOutput, UiRoot};
use ac_ui::tree::Element;
use ac_ui::widgets::Panel;
use ac_ui::windows::{WindowEvent, compute_authored_geometry_revision};

static NEXT: AtomicU32 = AtomicU32::new(0);

struct Dir(PathBuf);

impl Dir {
    fn new() -> Self {
        let n = NEXT.fetch_add(1, Ordering::Relaxed);
        let dir = std::env::temp_dir().join(format!("openac-ui-layout-{}-{n}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        Self(dir)
    }

    fn path(&self) -> PathBuf {
        self.0.join("settings.json")
    }

    fn store(&self) -> SettingsStore {
        SettingsStore::new(self.path())
    }

    fn text(&self) -> String {
        std::fs::read_to_string(self.path()).unwrap_or_default()
    }
}

impl Drop for Dir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn host(w: f32, h: f32) -> UiHost {
    let mut host = UiHost::new();
    host.root.set_size(w, h);
    host
}

fn mount(host: &mut UiHost, name: &str, revision: i32, width: f32, height: f32) {
    let mut e = Element::at(10.0, 20.0, width, height);
    e.min_width = 100.0;
    e.min_height = 80.0;
    e.max_width = 600.0;
    e.max_height = 400.0;
    e.draggable = true;
    e.resizable = true;
    let ui = &mut host.root;
    let root = ui.root();
    let frame = ui.add(root, e, Panel::default());
    ui.register_window(name, frame, None, revision).unwrap();
    host.pump();
    host.take_outputs();
}

fn mount_default(host: &mut UiHost, name: &str) {
    mount(host, name, 0, 200.0, 100.0);
}

fn geometry(host: &UiHost, name: &str) -> (f32, f32, f32, f32) {
    let e = host
        .root
        .element(host.root.window(name).unwrap().frame)
        .unwrap();
    (e.left, e.top, e.width, e.height)
}

fn frame_mut<'a>(host: &'a mut UiHost, name: &str) -> &'a mut Element {
    let f = host.root.window(name).unwrap().frame;
    host.root.element_mut(f).unwrap()
}

fn persist(
    host: &mut UiHost,
    store: Option<SettingsStore>,
    character: &str,
    screen: (i32, i32),
) -> LayoutPersistence {
    LayoutPersistence::new(
        host,
        store.map(|s| Box::new(s) as Box<dyn ac_ui::layout_persistence::LayoutStore>),
        character,
        screen,
        &[],
    )
}

fn layout(
    x: f32,
    y: f32,
    w: f32,
    h: f32,
    visible: bool,
    collapsed: bool,
    maximized: bool,
) -> UiWindowLayout {
    UiWindowLayout {
        x,
        y,
        width: w,
        height: h,
        visible,
        collapsed,
        maximized,
        authored_geometry_revision: 0,
    }
}

fn saved(
    store: &SettingsStore,
    character: &str,
    res: &str,
    window: &str,
) -> Option<UiWindowLayout> {
    store.load_window_layout(character, res, window, &UiWindowLayout::default())
}

/// Route the window events to the main panels as the game window does.
fn route(host: &mut UiHost, panels: &mut MainPanels) {
    loop {
        host.pump();
        let outputs = host.take_outputs();
        if outputs.is_empty() {
            return;
        }
        for o in outputs {
            if let UiOutput::Window { name, event } = o {
                match event {
                    WindowEvent::Moved | WindowEvent::Resized => {
                        panels.observe_geometry(&mut host.root, &name);
                    }
                    WindowEvent::Shown => panels.observe_visibility(&mut host.root, &name, true),
                    WindowEvent::Hidden => {
                        panels.observe_visibility(&mut host.root, &name, false);
                    }
                    _ => {}
                }
            }
        }
    }
}

#[test]
fn completed_interactions_save_the_whole_character_resolution_layout() {
    let dir = Dir::new();
    let mut host = host(800.0, 600.0);
    mount_default(&mut host, "chat");
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (800, 600));
    host.root.move_window("chat", 44.0, 55.0);
    host.root.resize_window("chat", 320.0, 180.0);
    host.root.hide_window("chat");
    p.follow(&mut host);
    let s = saved(&dir.store(), "Alice", "800x600", "chat").unwrap();
    assert_eq!((s.x, s.y, s.width, s.height), (44.0, 55.0, 320.0, 180.0));
    assert!(!s.visible);
}

#[test]
fn a_main_panel_move_saves_the_shared_place_of_every_main_panel() {
    let dir = Dir::new();
    let mut host = host(800.0, 600.0);
    mount_default(&mut host, "inventory");
    mount_default(&mut host, "character");
    host.root.hide_window("inventory");
    host.root.hide_window("character");
    let mut panels = MainPanels::new();
    panels.register(&mut host.root, 7, "inventory", false);
    panels.register(&mut host.root, 11, "character", false);
    route(&mut host, &mut panels);
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (800, 600));

    panels.set_visibility(&mut host.root, 7, true);
    route(&mut host, &mut panels);
    host.root.move_window("inventory", 244.0, 133.0);
    route(&mut host, &mut panels);
    p.follow(&mut host);

    let store = dir.store();
    let inv = saved(&store, "Alice", "800x600", "inventory").unwrap();
    let chr = saved(&store, "Alice", "800x600", "character").unwrap();
    assert_eq!((inv.x, inv.y), (244.0, 133.0));
    assert_eq!((chr.x, chr.y), (244.0, 133.0));
}

/// A window keeping a maximize state of its own (as the chat window does).
#[derive(Debug, Default)]
struct StateKeeper {
    restored: std::rc::Rc<std::cell::Cell<(WindowState, u32)>>,
    hidden: std::rc::Rc<std::cell::Cell<u32>>,
}

impl PanelController for StateKeeper {
    fn on_hidden(&mut self, _ui: &mut UiRoot) {
        self.hidden.set(self.hidden.get() + 1);
    }
    fn capture_window_state(&self, _ui: &UiRoot) -> Option<WindowState> {
        Some(self.restored.get().0)
    }
    fn restore_window_state(&mut self, _ui: &mut UiRoot, state: &WindowState) -> bool {
        let (_, n) = self.restored.get();
        self.restored.set((*state, n + 1));
        true
    }
}

#[test]
fn restore_clamps_the_bounds_and_puts_back_state_and_visibility() {
    let dir = Dir::new();
    dir.store()
        .save_window_layout(
            "Alice",
            "640x480",
            "chat",
            &layout(900.0, 700.0, 500.0, 300.0, false, false, true),
        )
        .unwrap();
    let mut host = host(640.0, 480.0);
    mount_default(&mut host, "chat");
    let keeper = StateKeeper::default();
    let restored = keeper.restored.clone();
    host.attach_controller("chat", Box::new(keeper)).unwrap();
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (640, 480));
    p.restore_all(&mut host);
    assert_eq!(geometry(&host, "chat"), (140.0, 180.0, 500.0, 300.0));
    assert!(!host.root.is_window_visible("chat"));
    assert!(restored.get().0.maximized);
}

#[test]
fn an_older_authored_geometry_revision_resets_only_the_saved_extent() {
    let dir = Dir::new();
    dir.store()
        .save_window_layout(
            "Alice",
            "800x600",
            "examination",
            &layout(44.0, 55.0, 310.0, 545.0, true, false, false),
        )
        .unwrap();
    let mut host = host(800.0, 600.0);
    mount(&mut host, "examination", 1, 310.0, 400.0);
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (800, 600));
    p.restore_all(&mut host);
    assert_eq!(geometry(&host, "examination"), (44.0, 55.0, 310.0, 400.0));
    let migrated = saved(&dir.store(), "Alice", "800x600", "examination").unwrap();
    assert_eq!(migrated.authored_geometry_revision, 1);
}

#[test]
fn a_named_profile_of_an_older_revision_takes_the_authored_extent_not_the_current_size() {
    let dir = Dir::new();
    dir.store()
        .save_named_window_layout(
            "legacy",
            "examination",
            &layout(44.0, 55.0, 310.0, 545.0, true, false, false),
        )
        .unwrap();
    let mut host = host(800.0, 600.0);
    mount(&mut host, "examination", 1, 310.0, 400.0);
    host.root.resize_window("examination", 500.0, 300.0);
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (800, 600));
    p.restore_named(&mut host, "legacy");
    assert_eq!(geometry(&host, "examination"), (44.0, 55.0, 310.0, 400.0));
}

#[test]
fn the_pre_login_default_key_never_saves() {
    let dir = Dir::new();
    let mut host = host(800.0, 600.0);
    mount_default(&mut host, "radar");
    let mut p = persist(&mut host, Some(dir.store()), "default", (800, 600));
    host.root.move_window("radar", 123.0, 234.0);
    p.follow(&mut host);
    assert!(saved(&dir.store(), "default", "800x600", "radar").is_none());
}

#[test]
fn restore_leaves_a_state_managed_windows_visibility_to_the_game() {
    let dir = Dir::new();
    dir.store()
        .save_window_layout(
            "Alice",
            "800x600",
            "combat",
            &layout(44.0, 55.0, 200.0, 100.0, true, false, false),
        )
        .unwrap();
    let mut host = host(800.0, 600.0);
    mount_default(&mut host, "combat");
    host.root.hide_window("combat");
    let mut p = LayoutPersistence::new(
        &mut host,
        Some(Box::new(dir.store())),
        "Alice",
        (800, 600),
        &["combat"],
    );
    p.restore_all(&mut host);
    let (l, t, _, _) = geometry(&host, "combat");
    assert_eq!((l, t), (44.0, 55.0));
    assert!(!host.root.is_window_visible("combat"));

    host.root.show_window("combat");
    p.follow(&mut host);
    assert!(
        !saved(&dir.store(), "Alice", "800x600", "combat")
            .unwrap()
            .visible
    );
}

#[test]
fn a_named_profile_saves_and_restores_every_window() {
    let dir = Dir::new();
    let mut host = host(800.0, 600.0);
    mount_default(&mut host, "chat");
    mount_default(&mut host, "radar");
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (800, 600));
    host.root.move_window("chat", 44.0, 55.0);
    host.root.move_window("radar", 300.0, 120.0);
    host.root.hide_window("radar");
    p.save_named(&mut host, "hunting");

    host.root.move_window("chat", 1.0, 2.0);
    host.root.move_window("radar", 3.0, 4.0);
    host.root.show_window("radar");
    p.restore_named(&mut host, "hunting");

    let (cl, ct, _, _) = geometry(&host, "chat");
    let (rl, rt, _, _) = geometry(&host, "radar");
    assert_eq!((cl, ct), (44.0, 55.0));
    assert_eq!((rl, rt), (300.0, 120.0));
    assert!(!host.root.is_window_visible("radar"));
}

#[test]
fn loading_a_named_profile_does_not_save_it_as_the_characters_layout() {
    // OpenAC `RestoreNamed` applies the profile under its `_restoring` guard,
    // so no window event saves the restored places; retail's `@loadui`
    // likewise only loads (it saves on `@saveui`/`@saveautoui` alone). The
    // character's layout keeps the player's last drag until the next change
    // or `/saveautoui` (smoke 2, finding 3).
    let dir = Dir::new();
    let mut host = host(800.0, 600.0);
    mount_default(&mut host, "vitals");
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (800, 600));
    host.root.move_window("vitals", 170.0, 10.0);
    p.save_named(&mut host, "smoke");
    host.root.move_window("vitals", 370.0, 196.0);
    p.follow(&mut host);
    let dragged = saved(&dir.store(), "Alice", "800x600", "vitals").unwrap();
    assert_eq!((dragged.x, dragged.y), (370.0, 196.0));

    p.restore_named(&mut host, "smoke");
    p.follow(&mut host);

    let (l, t, _, _) = geometry(&host, "vitals");
    assert_eq!((l, t), (170.0, 10.0));
    let kept = saved(&dir.store(), "Alice", "800x600", "vitals").unwrap();
    assert_eq!((kept.x, kept.y), (370.0, 196.0));
}

#[test]
fn clamp_all_to_screen_pulls_stranded_windows_in_top_left_first() {
    let dir = Dir::new();
    let mut host = host(2560.0, 1440.0);
    mount(&mut host, "examination", 0, 310.0, 400.0);
    host.root.move_window("examination", 2200.0, 1000.0);
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (2560, 1440));
    p.follow(&mut host);
    p.set_screen_size(1280, 720);
    p.clamp_all_to_screen(&mut host);
    let (l, t, _, _) = geometry(&host, "examination");
    assert_eq!((l, t), (1280.0 - 310.0, 720.0 - 400.0));
}

#[test]
fn clamp_all_to_screen_pins_an_oversized_window_to_the_corner() {
    let mut host = host(800.0, 600.0);
    mount(&mut host, "examination", 0, 310.0, 400.0);
    host.root.move_window("examination", 400.0, 200.0);
    let mut p = persist(&mut host, None, "Alice", (800, 600));
    p.follow(&mut host);
    p.set_screen_size(200, 200);
    p.clamp_all_to_screen(&mut host);
    let (l, t, _, _) = geometry(&host, "examination");
    assert_eq!((l, t), (0.0, 0.0));
}

#[test]
fn clamp_all_to_screen_leaves_a_window_inside_where_it_is() {
    let mut host = host(2560.0, 1440.0);
    mount(&mut host, "examination", 0, 310.0, 400.0);
    host.root.move_window("examination", 100.0, 120.0);
    let mut p = persist(&mut host, None, "Alice", (1280, 720));
    p.clamp_all_to_screen(&mut host);
    let (l, t, _, _) = geometry(&host, "examination");
    assert_eq!((l, t), (100.0, 120.0));
}

#[test]
fn clamp_all_to_screen_does_not_save_the_clamped_places() {
    let dir = Dir::new();
    let mut host = host(2560.0, 1440.0);
    mount(&mut host, "examination", 0, 310.0, 400.0);
    host.root.move_window("examination", 2200.0, 1000.0);
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (2560, 1440));
    p.set_screen_size(1280, 720);
    p.clamp_all_to_screen(&mut host);
    p.follow(&mut host);
    assert_eq!(geometry(&host, "examination").0, 1280.0 - 310.0);
    assert!(saved(&dir.store(), "Alice", "1280x720", "examination").is_none());
}

#[test]
fn restore_after_a_display_change_writes_nothing() {
    let dir = Dir::new();
    let mut host = host(800.0, 600.0);
    mount(&mut host, "examination", 0, 310.0, 400.0);
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (800, 600));
    p.restore_after_display_change(&mut host);
    p.follow(&mut host);
    assert!(saved(&dir.store(), "Alice", "800x600", "examination").is_none());
}

#[test]
fn restore_after_a_display_change_restores_geometry_but_keeps_what_shows() {
    let dir = Dir::new();
    dir.store()
        .save_window_layout(
            "Alice",
            "1280x720",
            "options",
            &layout(700.0, 200.0, 310.0, 400.0, false, true, true),
        )
        .unwrap();
    let mut host = host(1280.0, 720.0);
    mount(&mut host, "options", 0, 300.0, 300.0);
    let keeper = StateKeeper::default();
    let (restored, hidden) = (keeper.restored.clone(), keeper.hidden.clone());
    host.attach_controller("options", Box::new(keeper)).unwrap();
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (1280, 720));
    p.restore_after_display_change(&mut host);
    assert_eq!(geometry(&host, "options"), (700.0, 200.0, 310.0, 400.0));
    assert!(host.root.is_window_visible("options"));
    assert_eq!(hidden.get(), 0);
    let (state, count) = restored.get();
    assert_eq!(count, 1);
    assert!(state.collapsed && state.maximized);
}

#[test]
fn a_window_registered_later_still_round_trips() {
    let dir = Dir::new();
    let mut host = host(800.0, 600.0);
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (800, 600));
    mount_default(&mut host, "late-window");
    p.follow(&mut host);
    host.root.move_window("late-window", 77.0, 88.0);
    p.follow(&mut host);
    let s = saved(&dir.store(), "Alice", "800x600", "late-window").unwrap();
    assert_eq!((s.x, s.y), (77.0, 88.0));
}

fn resizable_panel(host: &mut UiHost, name: &str, w: f32, h: f32, min_w: f32, min_h: f32) {
    resizable_panel_at(host, name, w, h, min_w, min_h, true);
}

fn resizable_panel_at(
    host: &mut UiHost,
    name: &str,
    w: f32,
    h: f32,
    min_w: f32,
    min_h: f32,
    derived_revision: bool,
) {
    // An OpenAC markup panel `<panel w h resizable minw minh>` registered
    // with the revision its authored geometry derives.
    let mut e = Element::at(0.0, 0.0, w, h);
    e.min_width = min_w;
    e.min_height = min_h;
    e.resizable = true;
    let revision = if derived_revision {
        compute_authored_geometry_revision(w, h, min_w, min_h, true)
    } else {
        0
    };
    let ui = &mut host.root;
    let root = ui.root();
    let frame = ui.add(root, e, Panel::default());
    ui.register_window(name, frame, None, revision).unwrap();
    host.pump();
    host.take_outputs();
}

#[test]
fn a_resized_panel_round_trips_its_size() {
    let dir = Dir::new();
    let mut host = host(800.0, 600.0);
    resizable_panel(
        &mut host,
        "plugin-resizable-panel",
        300.0,
        200.0,
        200.0,
        150.0,
    );
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (800, 600));
    host.root.move_window("plugin-resizable-panel", 60.0, 70.0);
    host.root
        .resize_window("plugin-resizable-panel", 420.0, 260.0);
    p.follow(&mut host);
    let s = saved(&dir.store(), "Alice", "800x600", "plugin-resizable-panel").unwrap();
    assert_eq!((s.x, s.y, s.width, s.height), (60.0, 70.0, 420.0, 260.0));

    let mut fresh = self::host(800.0, 600.0);
    resizable_panel(
        &mut fresh,
        "plugin-resizable-panel",
        300.0,
        200.0,
        200.0,
        150.0,
    );
    let mut q = persist(&mut fresh, Some(dir.store()), "Alice", (800, 600));
    q.restore_all(&mut fresh);
    assert_eq!(
        geometry(&fresh, "plugin-resizable-panel"),
        (60.0, 70.0, 420.0, 260.0)
    );
}

#[test]
fn restore_clamps_a_size_below_the_floor_to_the_authored_minimum() {
    let dir = Dir::new();
    dir.store()
        .save_window_layout(
            "Alice",
            "800x600",
            "plugin-resizable-floor",
            &layout(60.0, 70.0, 80.0, 60.0, true, false, false),
        )
        .unwrap();
    let mut host = host(800.0, 600.0);
    // Registered without a revision, as OpenAC's case does.
    resizable_panel_at(
        &mut host,
        "plugin-resizable-floor",
        300.0,
        200.0,
        200.0,
        150.0,
        false,
    );
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (800, 600));
    p.restore_all(&mut host);
    let (_, _, w, h) = geometry(&host, "plugin-resizable-floor");
    assert_eq!((w, h), (200.0, 150.0));
}

#[test]
fn a_changed_authored_size_replaces_the_stored_size_but_keeps_the_place() {
    let dir = Dir::new();
    {
        let mut old = host(1280.0, 720.0);
        resizable_panel(&mut old, "moss-tank", 856.0, 236.0, 400.0, 150.0);
        let mut p = persist(&mut old, Some(dir.store()), "Alice", (1280, 720));
        old.root.move_window("moss-tank", 120.0, 90.0);
        p.follow(&mut old);
    }
    let mut new = host(1280.0, 720.0);
    resizable_panel(&mut new, "moss-tank", 984.0, 271.0, 400.0, 150.0);
    let mut p = persist(&mut new, Some(dir.store()), "Alice", (1280, 720));
    p.restore_all(&mut new);
    assert_eq!(geometry(&new, "moss-tank"), (120.0, 90.0, 984.0, 271.0));
}

#[test]
fn an_unchanged_authored_size_keeps_the_players_size() {
    let dir = Dir::new();
    {
        let mut old = host(1280.0, 720.0);
        resizable_panel(&mut old, "moss-tank", 856.0, 236.0, 400.0, 150.0);
        let mut p = persist(&mut old, Some(dir.store()), "Alice", (1280, 720));
        old.root.move_window("moss-tank", 50.0, 50.0);
        old.root.resize_window("moss-tank", 900.0, 300.0);
        p.follow(&mut old);
    }
    let mut fresh = host(1280.0, 720.0);
    resizable_panel(&mut fresh, "moss-tank", 856.0, 236.0, 400.0, 150.0);
    let mut p = persist(&mut fresh, Some(dir.store()), "Alice", (1280, 720));
    p.restore_all(&mut fresh);
    let (_, _, w, h) = geometry(&fresh, "moss-tank");
    assert_eq!((w, h), (900.0, 300.0));
}

#[test]
fn a_changed_authored_size_clamps_the_place_to_the_screen() {
    let dir = Dir::new();
    {
        let mut old = host(800.0, 600.0);
        resizable_panel(&mut old, "moss-tank", 200.0, 100.0, 100.0, 80.0);
        let mut p = persist(&mut old, Some(dir.store()), "Alice", (800, 600));
        old.root.move_window("moss-tank", 590.0, 490.0);
        p.follow(&mut old);
    }
    let mut new = host(800.0, 600.0);
    resizable_panel(&mut new, "moss-tank", 220.0, 110.0, 100.0, 80.0);
    let mut p = persist(&mut new, Some(dir.store()), "Alice", (800, 600));
    p.restore_all(&mut new);
    assert_eq!(geometry(&new, "moss-tank"), (580.0, 490.0, 220.0, 110.0));
}

// ── .Pregame ────────────────────────────────────────────────────────────

#[test]
fn a_temporary_pregame_viewport_does_not_change_the_characters_arrangement() {
    let dir = Dir::new();
    let mut host = host(1600.0, 900.0);
    mount(&mut host, "options", 0, 500.0, 300.0);
    frame_mut(&mut host, "options").max_width = 600.0;
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (1600, 900));
    host.root.move_window("options", 1000.0, 550.0);
    p.follow(&mut host);
    let before = dir.text();
    p.set_gameplay_active(&mut host, false);
    p.set_screen_size(800, 600);
    host.root.set_size(800.0, 600.0);
    p.reflow_to_screen(&mut host);
    let (l, t, _, _) = geometry(&host, "options");
    assert_eq!((l, t), (1000.0, 550.0));
    host.root.move_window("options", 1.0, 2.0);
    host.root.resize_window("options", 300.0, 200.0);
    host.root.hide_window("options");
    p.save_all(&mut host);
    p.restore_all(&mut host);
    p.save_named(&mut host, "temporary");
    assert_eq!(dir.text(), before);

    p.set_screen_size(1600, 900);
    host.root.set_size(1600.0, 900.0);
    p.set_gameplay_active(&mut host, true);
    p.restore_all(&mut host);
    assert_eq!(geometry(&host, "options"), (1000.0, 550.0, 500.0, 300.0));
    assert!(host.root.is_window_visible("options"));
    host.root.move_window("options", 900.0, 500.0);
    p.follow(&mut host);
    assert_ne!(dir.text(), before);
}

// ── The plugin dock ─────────────────────────────────────────────────────

/// A plugin dock on a new host, as the window mounts it.
fn dock_host() -> UiHost {
    use ac_ui::plugin::dock::{PluginDock, WINDOW};
    use std::cell::RefCell;
    use std::rc::Rc;
    let mut host = host(1280.0, 720.0);
    let dock = PluginDock::build(
        &mut host.root,
        None,
        None,
        Rc::new(RefCell::new(Vec::new())),
    );
    let frame = dock.root;
    let top = host.root.root();
    host.root.add_child(top, frame);
    host.root
        .register_window(WINDOW, frame, Some(frame), 0)
        .unwrap();
    host.attach_controller(WINDOW, Box::new(dock)).unwrap();
    host.pump();
    host.take_outputs();
    host
}

#[test]
fn the_plugin_dock_remembers_being_minimized_per_character_and_keeps_its_saved_name() {
    use ac_ui::plugin::dock::{PluginDock, WINDOW};
    assert_eq!(WINDOW, "plugin-shelf");
    let dir = Dir::new();
    let minimized = |host: &mut UiHost| {
        host.with_controller::<PluginDock, _>(WINDOW, |c, _| c.is_minimized())
            .unwrap()
    };
    {
        let mut host = dock_host();
        let mut p = persist(&mut host, Some(dir.store()), "Alice", (1280, 720));
        p.set_gameplay_active(&mut host, true);
        p.restore_all(&mut host);
        assert!(!minimized(&mut host));
        let tile = host
            .with_controller::<PluginDock, _>(WINDOW, |c, _| c.layout().tile)
            .unwrap();
        // The dock is hidden with no plugin windows; the state is the point.
        host.root.show_window(WINDOW);
        let frame = host.root.window(WINDOW).unwrap().frame;
        let at = host.root.element(frame).map(|e| (e.left, e.top)).unwrap();
        let (x, y) = ((at.0 + tile.x + 4.0) as i32, (at.1 + tile.y + 4.0) as i32);
        host.mouse_move(x, y);
        host.mouse_down(ac_ui::event::MouseButton::Left, x, y, 0);
        host.mouse_up(ac_ui::event::MouseButton::Left, x, y, 0);
        host.tick(0.016, 16, None);
        assert!(minimized(&mut host));
        p.follow(&mut host);
    }
    // A new run for the same character comes back minimized.
    let mut host = dock_host();
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (1280, 720));
    p.set_gameplay_active(&mut host, true);
    p.restore_all(&mut host);
    assert!(minimized(&mut host));
    // Another character has its own.
    p.set_character(&mut host, "Bob");
    p.restore_all(&mut host);
    assert!(!minimized(&mut host));
}

#[test]
fn a_saved_rail_restores_to_its_edge_with_its_place_along_it() {
    use ac_ui::plugin::dock::{DockMode, PluginDock, WINDOW};
    let dir = Dir::new();
    {
        let mut host = dock_host();
        host.with_controller::<PluginDock, _>(WINDOW, |c, ui| c.set_mode(ui, DockMode::Right));
        let mut p = persist(&mut host, Some(dir.store()), "Alice", (1280, 720));
        p.set_gameplay_active(&mut host, true);
        p.restore_all(&mut host);
        host.root.move_window(WINDOW, 300.0, 222.0);
        p.follow(&mut host);
    }
    // The saved mode comes first in a new run, then the place.
    let mut host = dock_host();
    host.with_controller::<PluginDock, _>(WINDOW, |c, ui| c.set_mode(ui, DockMode::Right));
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (1280, 720));
    p.set_gameplay_active(&mut host, true);
    p.restore_all(&mut host);
    host.tick(0.016, 16, None);
    let (left, top, width, _) = geometry(&host, WINDOW);
    assert_eq!(width, 46.0);
    assert_eq!((left, top), (1280.0 - 46.0, 222.0));
}

#[test]
fn a_limit_on_the_docks_slots_leaves_its_saved_place_and_size_alone() {
    use ac_ui::plugin::dock::{PluginDock, WINDOW};
    let dir = Dir::new();
    {
        let mut host = dock_host();
        host.with_controller::<PluginDock, _>(WINDOW, |c, ui| c.set_slot_limit(ui, Some(2)));
        let mut p = persist(&mut host, Some(dir.store()), "Alice", (1280, 720));
        p.set_gameplay_active(&mut host, true);
        p.restore_all(&mut host);
        host.root.move_window(WINDOW, 300.0, 222.0);
        p.follow(&mut host);
    }
    // The limit is a client setting, so it comes first in a new run, then the
    // place, and the saved height of the dock is never taken for one.
    let mut host = dock_host();
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (1280, 720));
    p.set_gameplay_active(&mut host, true);
    p.restore_all(&mut host);
    host.tick(0.016, 16, None);
    let limit = host
        .with_controller::<PluginDock, _>(WINDOW, |c, _| c.slot_limit())
        .unwrap();
    assert_eq!(limit, None);
    let (left, top, _, _) = geometry(&host, WINDOW);
    assert_eq!((left, top), (300.0, 222.0));
}

// ── .SidePanels ─────────────────────────────────────────────────────────

fn side_panel_size_survives_a_map_switch_and_a_fresh_login(chosen: f32) {
    let names = [
        "inventory",
        "character",
        "spellbook",
        "map-house",
        "options",
        "social-panel",
        "journal",
    ];
    let dir = Dir::new();
    for login in 0..2 {
        let mut host = host(1280.0, 900.0);
        let mut panels = MainPanels::new();
        panels.configure_frame(&ElementInfo {
            width: 310.0,
            height: 372.0,
            min_width: Some(310),
            max_width: Some(310),
            min_height: Some(372),
            max_height: Some(1000),
            ..ElementInfo::default()
        });
        for (i, name) in names.iter().enumerate() {
            mount(&mut host, name, 0, 310.0, 420.0 + i as f32 * 20.0);
            host.root.hide_window(name);
            let h = geometry(&host, name).3;
            let f = frame_mut(&mut host, name);
            f.min_height = h;
            f.max_height = 1000.0;
            f.resize_y = *name != "map-house";
            panels.register(&mut host.root, i as u32 + 1, name, false);
            route(&mut host, &mut panels);
            assert_eq!(geometry(&host, name).3, 372.0);
        }
        let mut p = persist(&mut host, Some(dir.store()), "Alice", (1280, 900));
        p.restore_all(&mut host);
        route(&mut host, &mut panels);
        if login == 0 {
            panels.set_visibility(&mut host.root, 1, true);
            route(&mut host, &mut panels);
            host.root.resize_window("inventory", 310.0, chosen);
            route(&mut host, &mut panels);
        }
        for index in [3usize, 2, 4, 5, 6, 1, 0, 3] {
            panels.set_visibility(&mut host.root, index as u32 + 1, true);
            route(&mut host, &mut panels);
            for name in names {
                assert_eq!(geometry(&host, name).3, chosen, "{name} after {index}");
            }
            assert_eq!(
                frame_mut(&mut host, names[index]).resizable_edges,
                ResizeEdges::BOTTOM
            );
        }
        if login == 0 {
            frame_mut(&mut host, "map-house").resize_y = true;
            host.root.resize_window("map-house", 310.0, chosen + 20.0);
            frame_mut(&mut host, "map-house").resize_y = false;
            route(&mut host, &mut panels);
            for name in names {
                assert_eq!(geometry(&host, name).3, chosen + 20.0);
            }
            frame_mut(&mut host, "map-house").resize_y = true;
            host.root.resize_window("map-house", 310.0, chosen);
            frame_mut(&mut host, "map-house").resize_y = false;
            route(&mut host, &mut panels);
        }
        p.save_all(&mut host);
    }
}

#[test]
fn a_side_panel_height_of_400_survives_a_map_switch_and_a_fresh_login() {
    side_panel_size_survives_a_map_switch_and_a_fresh_login(400.0);
}

#[test]
fn a_side_panel_height_of_580_survives_a_map_switch_and_a_fresh_login() {
    side_panel_size_survives_a_map_switch_and_a_fresh_login(580.0);
}

// ── .Viewport ───────────────────────────────────────────────────────────

#[test]
fn a_new_character_gets_the_defaults_not_the_previous_characters_arrangement() {
    let dir = Dir::new();
    let mut host = host(1200.0, 800.0);
    mount(&mut host, "chat", 0, 400.0, 200.0);
    frame_mut(&mut host, "chat").max_width = 1000.0;
    mount(&mut host, "radar", 0, 160.0, 160.0);
    mount(&mut host, "toolbar", 0, 300.0, 90.0);
    mount(&mut host, "combat", 0, 600.0, 110.0);
    mount(&mut host, "indicators", 0, 180.0, 30.0);
    mount(&mut host, "vitals", 0, 200.0, 50.0);
    mount(&mut host, "plugin-shelf", 0, 40.0, 200.0);
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (1200, 800));
    p.reset_to_defaults(&mut host);
    p.restore_all(&mut host);
    host.root.move_window("radar", 123.0, 234.0);
    host.root.resize_window("chat", 650.0, 300.0);
    host.root.move_window("chat", 500.0, 20.0);
    p.set_character(&mut host, "Bob");
    p.restore_all(&mut host);

    assert_eq!(geometry(&host, "chat"), (10.0, 590.0, 400.0, 200.0));
    let at = |host: &UiHost, n| {
        let g = geometry(host, n);
        (g.0, g.1)
    };
    assert_eq!(at(&host, "radar"), (1030.0, 10.0));
    assert_eq!(at(&host, "toolbar"), (890.0, 700.0));
    assert_eq!(at(&host, "combat"), (590.0, 580.0));
    assert_eq!(at(&host, "indicators"), (10.0, 10.0));
    assert_eq!(at(&host, "vitals"), (200.0, 10.0));
    assert_eq!(at(&host, "plugin-shelf"), (10.0, 300.0));

    p.set_character(&mut host, "Alice");
    p.restore_all(&mut host);
    assert_eq!(at(&host, "radar"), (123.0, 234.0));
    assert_eq!(geometry(&host, "chat"), (500.0, 20.0, 650.0, 300.0));
}

fn a_viewport_change_keeps_the_relative_place(x: f32, y: f32, ex: f32, ey: f32) {
    let mut host = host(800.0, 600.0);
    mount(&mut host, "options", 0, 200.0, 100.0);
    let mut p = persist(&mut host, None, "Alice", (800, 600));
    host.root.move_window("options", x, y);
    p.follow(&mut host);
    p.set_screen_size(1600, 1200);
    host.root.set_size(1600.0, 1200.0);
    p.reflow_to_screen(&mut host);
    assert_eq!(geometry(&host, "options"), (ex, ey, 200.0, 100.0));
    p.set_screen_size(800, 600);
    host.root.set_size(800.0, 600.0);
    p.reflow_to_screen(&mut host);
    let (l, t, _, _) = geometry(&host, "options");
    assert_eq!((l, t), (x, y));
}

#[test]
fn a_viewport_change_keeps_the_relative_place_at_the_corner() {
    a_viewport_change_keeps_the_relative_place(0.0, 0.0, 0.0, 0.0);
}

#[test]
fn a_viewport_change_keeps_the_relative_place_at_the_far_corner() {
    a_viewport_change_keeps_the_relative_place(600.0, 500.0, 1400.0, 1100.0);
}

#[test]
fn a_viewport_change_keeps_the_relative_place_in_the_middle() {
    a_viewport_change_keeps_the_relative_place(300.0, 250.0, 700.0, 550.0);
}

#[test]
fn shrink_and_grow_restore_size_and_place_without_saving_the_clamps() {
    let dir = Dir::new();
    let mut host = host(1200.0, 800.0);
    mount(&mut host, "options", 0, 700.0, 300.0);
    {
        let f = frame_mut(&mut host, "options");
        f.max_width = 1000.0;
        f.constrain_resize_to_parent = true;
    }
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (1200, 800));
    host.root.move_window("options", 450.0, 400.0);
    p.follow(&mut host);
    p.set_screen_size(400, 250);
    host.root.set_size(400.0, 250.0);
    p.reflow_to_screen(&mut host);
    assert_eq!(geometry(&host, "options"), (0.0, 0.0, 400.0, 250.0));
    host.root.hide_window("options");
    p.save_all(&mut host);
    p.set_screen_size(1200, 800);
    host.root.set_size(1200.0, 800.0);
    p.reflow_to_screen(&mut host);
    assert_eq!(geometry(&host, "options"), (450.0, 400.0, 700.0, 300.0));
    assert!(!host.root.is_window_visible("options"));
    p.restore_all(&mut host);
    assert_eq!(geometry(&host, "options"), (450.0, 400.0, 700.0, 300.0));
}

#[test]
fn returning_to_an_old_resolution_keeps_the_current_arrangement() {
    let dir = Dir::new();
    dir.store()
        .save_window_layout(
            "Alice",
            "1600x1200",
            "options",
            &layout(20.0, 30.0, 200.0, 100.0, false, false, false),
        )
        .unwrap();
    let mut host = host(800.0, 600.0);
    mount(&mut host, "options", 0, 200.0, 100.0);
    let mut p = persist(&mut host, Some(dir.store()), "Alice", (800, 600));
    host.root.move_window("options", 300.0, 250.0);
    p.follow(&mut host);
    p.set_screen_size(1600, 1200);
    host.root.set_size(1600.0, 1200.0);
    p.reflow_to_screen(&mut host);
    let (l, t, _, _) = geometry(&host, "options");
    assert_eq!((l, t), (700.0, 550.0));
    assert!(host.root.is_window_visible("options"));
    p.restore_all(&mut host);
    let (l, t, _, _) = geometry(&host, "options");
    assert_eq!((l, t), (700.0, 550.0));
    assert!(host.root.is_window_visible("options"));
}

/// A settings store that counts how the persistence calls it.
#[derive(Clone)]
struct Counting {
    store: std::rc::Rc<SettingsStore>,
    calls: std::rc::Rc<std::cell::RefCell<Vec<&'static str>>>,
}

impl Counting {
    fn new(store: SettingsStore) -> Self {
        Self {
            store: std::rc::Rc::new(store),
            calls: std::rc::Rc::default(),
        }
    }

    fn calls(&self) -> Vec<&'static str> {
        std::mem::take(&mut *self.calls.borrow_mut())
    }
}

impl ac_ui::layout_persistence::LayoutStore for Counting {
    fn load_window_placement(
        &self,
        character: &str,
        resolution: &str,
        window: &str,
        fallback: &UiWindowLayout,
    ) -> Option<ac_host::settings::UiWindowPlacement> {
        self.calls.borrow_mut().push("load one");
        self.store
            .load_window_placement(character, resolution, window, fallback)
    }
    fn save_window_placement(
        &self,
        character: &str,
        window: &str,
        placement: &ac_host::settings::UiWindowPlacement,
    ) -> std::io::Result<()> {
        self.calls.borrow_mut().push("save one");
        self.store
            .save_window_placement(character, window, placement)
    }
    fn load_window_placements(
        &self,
        character: &str,
        resolution: &str,
        windows: &[(&str, UiWindowLayout)],
    ) -> std::collections::HashMap<String, ac_host::settings::UiWindowPlacement> {
        self.calls.borrow_mut().push("load all");
        self.store
            .load_window_placements(character, resolution, windows)
    }
    fn save_window_placements(
        &self,
        character: &str,
        placements: &[(&str, ac_host::settings::UiWindowPlacement)],
    ) -> std::io::Result<()> {
        self.calls.borrow_mut().push("save all");
        self.store.save_window_placements(character, placements)
    }
    fn load_named_window_layout(
        &self,
        profile: &str,
        window: &str,
        fallback: &UiWindowLayout,
    ) -> Option<UiWindowLayout> {
        self.store
            .load_named_window_layout(profile, window, fallback)
    }
    fn save_named_window_layout(
        &self,
        profile: &str,
        window: &str,
        layout: &UiWindowLayout,
    ) -> std::io::Result<()> {
        self.store.save_named_window_layout(profile, window, layout)
    }
}

const BATCH_WINDOWS: [&str; 5] = ["chat", "radar", "combat", "vitals", "toolbar"];

/// Restoring or saving every window reads the settings file once and
/// writes it once, however many windows there are: the file holds every
/// section, so a read and a write per window froze the first world frame.
#[test]
fn restore_all_and_save_all_load_once_and_save_once() {
    let d = Dir::new();
    let seed = d.store();
    for (i, name) in BATCH_WINDOWS.iter().enumerate() {
        let i = i as f32;
        seed.save_window_placement(
            "Alice",
            name,
            &ac_host::settings::UiWindowPlacement {
                layout: layout(30.0 + i, 40.0 + i, 200.0, 100.0, true, false, false),
                screen_width: 800,
                screen_height: 600,
            },
        )
        .unwrap();
    }
    let mut h = host(800.0, 600.0);
    for name in BATCH_WINDOWS {
        mount_default(&mut h, name);
    }
    let store = Counting::new(d.store());
    let mut p = LayoutPersistence::new(
        &mut h,
        Some(Box::new(store.clone()) as Box<dyn ac_ui::layout_persistence::LayoutStore>),
        "Alice",
        (800, 600),
        &[],
    );

    p.restore_all(&mut h);
    assert_eq!(store.calls(), ["load all", "save all"]);
    for (i, name) in BATCH_WINDOWS.iter().enumerate() {
        let i = i as f32;
        assert_eq!(geometry(&h, name), (30.0 + i, 40.0 + i, 200.0, 100.0));
    }

    p.save_all(&mut h);
    assert_eq!(store.calls(), ["save all"]);

    p.restore_after_display_change(&mut h);
    assert_eq!(store.calls(), ["load all"]);
    assert!(p.take_failures().is_empty());
}
