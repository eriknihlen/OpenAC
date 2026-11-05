//! The dock's behaviour, on a real root with a real plugin UI copy.

use std::cell::RefCell;
use std::rc::Rc;

use ac_host::settings::{UiWindowLayout, UiWindowPlacement};
use ac_plugin_ui::{BindingSchema, PanelDescriptor, PluginUi, UiOwner};

use super::layout::{self, ART, DockLayout};
use super::*;
use crate::draw::{DrawCmd, DrawList, NoSprites, TextureRef};
use crate::event::MouseButton;
use crate::host::UiHost;
use crate::layout_persistence::{LayoutPersistence, LayoutStore};
use crate::plugin::style::tests::{corners as draw_corners, has_color};
use crate::plugin::style::{hover, mix};
use crate::shapes::CornerRadii;

const FILE_TEXTURE: TextureRef = TextureRef::Runtime(7);
const SCREEN: (f32, f32) = (800.0, 600.0);

/// The window's textures as a test wants them: a file icon uploads as texture
/// 7; the surface resolver answers (counting its calls) or must not run.
struct StubIcons {
    surface: Option<Icon>,
    surface_must_not_run: bool,
    surface_calls: Vec<u32>,
}

impl StubIcons {
    fn none() -> Self {
        Self {
            surface: None,
            surface_must_not_run: false,
            surface_calls: Vec::new(),
        }
    }
}

impl DockIcons for StubIcons {
    fn file(&mut self, icon: &PluginIcon) -> Option<Icon> {
        Some((FILE_TEXTURE, icon.width as f32, icon.height as f32))
    }

    fn surface(&mut self, id: u32) -> Option<Icon> {
        assert!(!self.surface_must_not_run, "the dat resolver must not run");
        self.surface_calls.push(id);
        self.surface
    }
}

/// Where a slot is and whether it shows.
type SlotPlace = (f32, f32, bool);

/// A dock on a host, and the plugin UI copy it was synced from.
struct Rig {
    host: UiHost,
    ui: PluginUi,
    outbox: PluginOutbox,
    now: i64,
}

impl Rig {
    fn new(height: f32, theme: PluginTheme) -> Self {
        let mut host = UiHost::new();
        host.root.set_size(SCREEN.0, height);
        // Locked, so a layout has no grip unless a test asks for one.
        host.root.set_ui_locked(true);
        let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
        let mut dock = PluginDock::build(&mut host.root, None, None, Rc::clone(&outbox));
        let frame = dock.root;
        let top = host.root.root();
        host.root.add_child(top, frame);
        host.root
            .register_window(WINDOW, frame, Some(frame), 0)
            .unwrap();
        dock.set_theme(&mut host.root, theme);
        // At once, unless a test is about the time it takes.
        dock.reveal_seconds = 0.0;
        host.attach_controller(WINDOW, Box::new(dock)).unwrap();
        Self {
            host,
            ui: PluginUi::new(),
            outbox,
            now: 0,
        }
    }

    /// One window of plugin `owner`.
    fn add(&mut self, owner: &str, owner_name: &str, descriptor: PanelDescriptor) -> u32 {
        self.ui
            .register_panel(
                &UiOwner::new(owner, owner_name),
                descriptor,
                "<panel/>",
                BindingSchema::new("Binding"),
            )
            .unwrap()
    }

    /// `count` plugins with a window each.
    fn add_plugins(&mut self, count: usize) {
        for i in 0..count {
            self.add(
                &format!("test.{i}"),
                &format!("Plugin {i}"),
                PanelDescriptor::new("main", format!("Plugin {i}")),
            );
        }
    }

    fn sync(&mut self, icons: &mut StubIcons) {
        let panels: Vec<PanelView> = self.ui.panels().cloned().collect();
        self.host.with_controller::<PluginDock, _>(WINDOW, |c, ui| {
            c.sync(ui, &panels, icons);
        });
        self.tick();
    }

    fn sync_plain(&mut self) {
        self.sync(&mut StubIcons::none());
    }

    fn tick(&mut self) {
        self.now += 16;
        self.host.tick(0.016, self.now, None);
    }

    fn draw(&mut self) -> DrawList {
        self.host.draw(SCREEN, &NoSprites)
    }

    fn with<R>(&mut self, f: impl FnOnce(&mut PluginDock, &mut UiRoot) -> R) -> R {
        self.host
            .with_controller::<PluginDock, _>(WINDOW, f)
            .unwrap()
    }

    fn layout(&mut self) -> DockLayout {
        self.with(|c, _| c.layout().clone())
    }

    fn frame(&mut self) -> ElementId {
        self.with(|c, _| c.root)
    }

    fn at(&mut self) -> (f32, f32) {
        let frame = self.frame();
        let e = self.host.root.element(frame).unwrap();
        (e.left, e.top)
    }

    fn size(&mut self) -> (f32, f32) {
        let frame = self.frame();
        let e = self.host.root.element(frame).unwrap();
        (e.width, e.height)
    }

    /// `r` in the dock's space as a point on the screen, near its top-left.
    fn point(&mut self, r: Rect) -> (i32, i32) {
        let (left, top) = self.at();
        ((left + r.x + 4.0) as i32, (top + r.y + 4.0) as i32)
    }

    fn click(&mut self, r: Rect) {
        let (x, y) = self.point(r);
        self.host.mouse_move(x, y);
        self.host.mouse_down(MouseButton::Left, x, y, 0);
        self.host.mouse_up(MouseButton::Left, x, y, 0);
        self.tick();
    }

    fn hover(&mut self, r: Rect) {
        let (x, y) = self.point(r);
        self.host.mouse_move(x, y);
        self.tick();
    }

    /// Each slot's place and whether it shows.
    fn slot_rects(&mut self) -> Vec<Option<SlotPlace>> {
        let panels: Vec<u32> = self.ui.panels().map(|p| p.id).collect();
        panels
            .iter()
            .map(|panel| {
                let slot = self.with(|c, _| c.slot(*panel))?;
                let e = self.host.root.element(slot)?;
                Some((e.left, e.top, e.visible()))
            })
            .collect()
    }

    fn label(&mut self) -> Option<HoverLabel> {
        let frame = self.frame();
        self.host
            .root
            .widget::<DockFrame>(frame)
            .and_then(|f| f.label.clone())
    }
}

fn sprites_of(list: &DrawList, texture: TextureRef) -> Vec<(f32, f32, f32)> {
    list.commands()
        .filter_map(|c| match c {
            DrawCmd::Sprite {
                texture: t,
                quad,
                tint,
                ..
            } if *t == texture => Some((quad.w, quad.h, tint.a)),
            _ => None,
        })
        .collect()
}

fn icon_file() -> PluginIcon {
    PluginIcon {
        width: 64,
        height: 64,
        rgba: Arc::new(vec![255; 64 * 64 * 4]),
    }
}

// ── Letters and icons ───────────────────────────────────────────────────

#[test]
fn monogram_letters_come_from_the_plugin_or_the_title_two_at_most() {
    assert_eq!(initials(None, "Test Plugin"), "TP");
    assert_eq!(initials(Some("  MT "), "Tank"), "MT");
    assert_eq!(initials(Some("ABCD"), "Tank"), "AB");
    assert_eq!(initials(None, "tank"), "TA");
    assert_eq!(initials(None, "  "), "?");
    assert_eq!(initials(None, "mosswart massacre extra"), "MM");
    assert_eq!(initials(None, "Loot Editor"), "LE");
    assert_eq!(initials(None, "Golem"), "GO");
}

#[test]
fn with_a_file_icon_draws_it_and_never_calls_the_resolver() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Classic);
    rig.ui.set_file_icon("acdream.test", Some(icon_file()));
    let descriptor = PanelDescriptor {
        icon_surface_id: 0x165,
        ..PanelDescriptor::new("main", "Test Plugin")
    };
    rig.add("acdream.test", "Test Plugin", descriptor);
    let mut icons = StubIcons {
        surface: None,
        surface_must_not_run: true,
        surface_calls: Vec::new(),
    };
    rig.sync(&mut icons);
    let list = rig.draw();
    // Drawn at the art's size whatever the file's.
    let drawn = sprites_of(&list, FILE_TEXTURE);
    assert_eq!(drawn.len(), 1);
    assert_eq!((drawn[0].0, drawn[0].1), (ART, ART));
}

#[test]
fn with_no_file_icon_still_resolves_the_surface_icon() {
    let surface = TextureRef::Surface(0x0600_0165);
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Classic);
    let descriptor = PanelDescriptor {
        icon_surface_id: 0x165,
        ..PanelDescriptor::new("main", "Test Plugin")
    };
    rig.add("acdream.test", "Test Plugin", descriptor);
    let mut icons = StubIcons {
        surface: Some((surface, 32.0, 32.0)),
        surface_must_not_run: false,
        surface_calls: Vec::new(),
    };
    rig.sync(&mut icons);
    assert!(!icons.surface_calls.is_empty());
    let list = rig.draw();
    assert_eq!(sprites_of(&list, surface).len(), 1);
}

#[test]
fn with_neither_icon_a_plugin_gets_its_monogram_hue_in_every_theme() {
    for theme in [
        PluginTheme::Classic,
        PluginTheme::Brass,
        PluginTheme::Parchment,
    ] {
        let mut rig = Rig::new(SCREEN.1, theme);
        rig.add_plugins(1);
        rig.sync_plain();
        let list = rig.draw();
        assert!(has_color(
            &draw_corners(&list),
            style::monogram_hue("test.0")
        ));
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_dock_icon_id_resolves_to_a_real_installed_render_surface() {
    // The image MossTank and MosswartMassacre name as their dock icon.
    use crate::draw::SpriteSizes;
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(std::path::PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    let content = crate::content::DatContent::new(Arc::new(
        ac_dat::DatCollection::open(dir).expect("open the retail dats"),
    ))
    .expect("master property table");
    let id = normalize_icon(0x0600_2C41);
    let (w, h) = content.sprite_size(id).expect("a real render surface");
    assert!(w > 0 && h > 0);
}

#[test]
fn colour_art_is_dimmer_while_its_window_is_closed() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Classic);
    rig.ui.set_file_icon("acdream.test", Some(icon_file()));
    rig.add("acdream.test", "Test", PanelDescriptor::new("main", "Test"));
    rig.ui.show_panel("acdream.test", "main", false);
    rig.sync_plain();
    let closed = sprites_of(&rig.draw(), FILE_TEXTURE);
    assert_eq!(closed[0].2, style::CLOSED_ART_ALPHA);
    rig.ui.show_panel("acdream.test", "main", true);
    rig.sync_plain();
    let open = sprites_of(&rig.draw(), FILE_TEXTURE);
    assert_eq!(open[0].2, 1.0);
}

// ── Chrome ──────────────────────────────────────────────────────────────

#[test]
fn the_tile_follows_the_layout_open_and_closed_and_a_press_toggles_it() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Classic);
    rig.add_plugins(2);
    rig.sync_plain();
    let tile = rig.with(|c, _| c.tile);
    let rect = |rig: &Rig| {
        let e = rig.host.root.element(tile).unwrap();
        (e.left, e.top, e.width, e.height)
    };
    assert_eq!(rect(&rig), (6.0, 6.0, 36.0, 36.0));
    let target = rig.layout().tile;
    rig.click(target);
    assert!(rig.with(|c, _| c.is_minimized()));
    assert_eq!(rig.size(), (48.0, 48.0));
    assert_eq!(rect(&rig), (0.0, 0.0, 48.0, 48.0));
    let target = rig.layout().tile;
    rig.click(target);
    assert!(!rig.with(|c, _| c.is_minimized()));
    assert_eq!(rig.size().0, 48.0);
    assert!(rig.size().1 > 48.0);
}

#[test]
fn after_scrolling_every_visible_slot_matches_a_fresh_layout() {
    let mut rig = Rig::new(300.0, PluginTheme::Classic);
    rig.add_plugins(12);
    rig.sync_plain();
    let frame = rig.frame();
    rig.host.root.send_message(frame, ElementMessage::Wheel(-1));
    rig.host.root.send_message(frame, ElementMessage::Wheel(-1));
    rig.host.pump();
    rig.tick();

    let owners: Vec<String> = (0..12).map(|i| format!("test.{i}")).collect();
    let owners: Vec<&str> = owners.iter().map(String::as_str).collect();
    let longest = layout::max_length(DockMode::Floating, SCREEN.0, 300.0);
    let fresh = layout::compute(
        DockMode::Floating,
        DockSize::Default,
        false,
        false,
        &owners,
        longest,
        2,
    );
    assert_eq!(fresh.first, 2);
    for (slot, want) in rig.slot_rects().iter().zip(&fresh.slots) {
        let (x, y, visible) = slot.unwrap();
        assert_eq!(visible, want.is_some());
        if let Some(r) = want {
            assert_eq!((x, y), (r.x, r.y));
        }
    }
    assert_eq!(rig.size().1, fresh.height);
}

#[test]
fn a_plugin_going_leaves_every_other_slot_as_a_fresh_layout_has_it() {
    let mut rig = Rig::new(300.0, PluginTheme::Classic);
    rig.add_plugins(12);
    rig.sync_plain();
    let first = rig.ui.panels().next().unwrap().id;
    rig.ui.remove_panel("test.0", first);
    rig.sync_plain();
    let owners: Vec<String> = (1..12).map(|i| format!("test.{i}")).collect();
    let owners: Vec<&str> = owners.iter().map(String::as_str).collect();
    let longest = layout::max_length(DockMode::Floating, SCREEN.0, 300.0);
    let fresh = layout::compute(
        DockMode::Floating,
        DockSize::Default,
        false,
        false,
        &owners,
        longest,
        0,
    );
    assert_eq!(rig.layout(), fresh);
    assert_eq!(rig.with(|c, _| c.entry_count()), 11);
}

#[test]
fn a_dock_too_long_for_the_screen_scrolls_and_fits_wherever_it_is_placed() {
    let mut rig = Rig::new(300.0, PluginTheme::Classic);
    rig.add_plugins(12);
    rig.sync_plain();
    let l = rig.layout();
    assert!(l.max_first > 0);
    assert!(l.forward.is_some());
    assert!(l.height <= 300.0 - 2.0 * layout::FLOATING_MARGIN);
    rig.host.root.move_window(WINDOW, 10.0, 280.0);
    rig.tick();
    let (_, top) = rig.at();
    assert!(top + l.height <= 300.0);
    // Where it is placed changes nothing about how long it is.
    assert_eq!(rig.layout().height, l.height);
}

impl Rig {
    /// A dock too long for a short screen, in `mode`.
    fn overflowing(mode: DockMode) -> Self {
        let mut rig = Self::new(300.0, PluginTheme::Moss);
        rig.add_plugins(12);
        rig.sync_plain();
        rig.with(|c, ui| c.set_mode(ui, mode));
        rig.tick();
        rig
    }

    fn press_arrow(&mut self, forward: bool) {
        let l = self.layout();
        let arrow = if forward { l.forward } else { l.back };
        self.click(arrow.unwrap());
        self.tick();
    }
}

#[test]
fn an_arrow_moves_a_page_keeping_one_slot_and_stops_at_the_end() {
    let mut rig = Rig::overflowing(DockMode::Floating);
    let start = rig.layout();
    assert!(start.visible >= 2 && start.max_first > 0);
    let page = start.visible - 1;
    let mut want = 0;
    while want < start.max_first {
        rig.press_arrow(true);
        want = (want + page).min(start.max_first);
        assert_eq!(rig.layout().first, want);
    }
    rig.press_arrow(true);
    assert_eq!(rig.layout().first, start.max_first);
    rig.press_arrow(false);
    assert_eq!(rig.layout().first, start.max_first - page);
    assert!(rig.outbox.borrow().is_empty(), "no window opened");
}

#[test]
fn the_wheel_on_an_arrow_scrolls_a_slot_like_the_wheel_on_a_slot() {
    let mut rig = Rig::overflowing(DockMode::Floating);
    let forward = rig.layout().forward.unwrap();
    rig.hover(forward);
    rig.host.scroll(-1);
    rig.host.pump();
    rig.tick();
    assert_eq!(rig.layout().first, 1);
    rig.host.scroll(1);
    rig.host.pump();
    rig.tick();
    assert_eq!(rig.layout().first, 0);
}

#[test]
fn an_arrow_says_how_many_more_and_says_nothing_with_none() {
    let mut rig = Rig::overflowing(DockMode::Floating);
    let l = rig.layout();
    rig.hover(l.forward.unwrap());
    let after = 12 - l.visible;
    assert_eq!(rig.label().unwrap().title, format!("{after} more plugins"));
    rig.hover(l.back.unwrap());
    assert!(rig.label().is_none());

    rig.with(|c, ui| c.scroll_to(ui, 1));
    rig.tick();
    let l = rig.layout();
    rig.hover(l.back.unwrap());
    assert_eq!(rig.label().unwrap().title, "1 more plugin");
}

#[test]
fn an_arrows_label_stays_while_the_pointer_rests_past_the_tooltip_time() {
    let mut rig = Rig::overflowing(DockMode::Floating);
    let forward = rig.layout().forward.unwrap();
    rig.hover(forward);
    let wait = rig.host.root.tooltip_delay_ms + rig.host.root.tooltip_duration_ms;
    // Two frames: one fires the tooltip time, the next would end it.
    rig.pass(wait as f64 / 1000.0);
    rig.pass(wait as f64 / 1000.0);
    assert!(rig.label().is_some());
}

#[test]
fn an_arrow_dims_with_no_more_that_way_and_the_list_that_fits_has_none() {
    let mut rig = Rig::overflowing(DockMode::Floating);
    let more = |rig: &mut Rig, forward: bool| {
        rig.with(|c, ui| {
            let id = if forward { c.forward } else { c.back };
            ui.widget::<DockArrow>(id).unwrap().more
        })
    };
    assert_eq!(more(&mut rig, false), 0);
    assert!(more(&mut rig, true) > 0);
    let max = rig.layout().max_first;
    rig.with(|c, ui| c.scroll_to(ui, max));
    rig.tick();
    assert_eq!(more(&mut rig, false), max);
    assert_eq!(more(&mut rig, true), 0);

    let (back, forward) = rig.with(|c, _| (c.back, c.forward));
    assert!(rig.host.root.element(back).unwrap().visible());
    let mut fits = Rig::new(SCREEN.1, PluginTheme::Moss);
    fits.add_plugins(2);
    fits.sync_plain();
    let (back, forward_2) = fits.with(|c, _| (c.back, c.forward));
    assert!(!fits.host.root.element(back).unwrap().visible());
    assert!(!fits.host.root.element(forward_2).unwrap().visible());
    assert!(rig.host.root.element(forward).unwrap().visible());
}

#[test]
fn closing_an_overflowing_dock_keeps_its_place_in_the_list() {
    let mut rig = Rig::overflowing(DockMode::Floating);
    rig.with(|c, ui| c.scroll_to(ui, 3));
    rig.tick();
    assert_eq!(rig.layout().first, 3);
    rig.with(|c, ui| c.set_minimized(ui, true));
    rig.tick();
    rig.with(|c, ui| c.set_minimized(ui, false));
    rig.tick();
    assert_eq!(rig.layout().first, 3);
}

#[test]
fn only_an_overflowing_dock_draws_its_bar() {
    let mut rig = Rig::overflowing(DockMode::Left);
    let bar = rig.layout().scroll;
    assert!(bar.is_some());
    let drawn_bar =
        |rig: &mut Rig| rig.with(|c, ui| ui.widget::<DockFrame>(c.root).unwrap().scroll);
    assert_eq!(drawn_bar(&mut rig), bar);

    let mut fits = Rig::new(SCREEN.1, PluginTheme::Moss);
    fits.add_plugins(2);
    fits.sync_plain();
    assert_eq!(drawn_bar(&mut fits), None);
}

#[test]
fn pressing_a_slot_toggles_its_window_and_the_gear_asks_for_appearance() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.add_plugins(1);
    rig.sync_plain();
    let l = rig.layout();
    rig.click(l.slots[0].unwrap());
    assert!(matches!(
        rig.outbox.borrow().as_slice(),
        [UiInput::PanelShown { .. }]
    ));
    assert!(!rig.with(|c, _| c.take_appearance_request()));
    rig.outbox.borrow_mut().clear();
    rig.click(l.gear);
    assert!(rig.outbox.borrow().is_empty());
    assert!(rig.with(|c, _| c.take_appearance_request()));
}

#[test]
fn the_dock_stays_over_a_window_that_was_raised() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.add_plugins(1);
    rig.sync_plain();
    let frame = rig.frame();
    let other = rig.window("other", Rect::new(0.0, 0.0, 100.0, 100.0));
    rig.host.root.bring_to_front(other);
    rig.tick();
    assert!(rig.z(frame) > rig.z(other));
}

impl Rig {
    /// A registered window at `r`, shown and raised.
    fn window(&mut self, name: &str, r: Rect) -> ElementId {
        let ui = &mut self.host.root;
        let e = Element::at(r.x, r.y, r.w, r.h);
        let frame = ui.insert(e, crate::widgets::Panel::default());
        let top = ui.root();
        ui.add_child(top, frame);
        ui.register_window(name, frame, None, 0).unwrap();
        ui.show_window(name);
        frame
    }

    fn z(&self, id: ElementId) -> i32 {
        self.host.root.element(id).unwrap().z_order()
    }

    /// The middle of the dock, on the screen.
    fn middle(&mut self) -> (f32, f32) {
        let (left, top) = self.at();
        let (w, h) = self.size();
        (left + w / 2.0, top + h / 2.0)
    }
}

#[test]
fn a_plugin_dialog_over_the_dock_stays_over_it_and_takes_the_press() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.add_plugins(1);
    rig.sync_plain();
    let dock = rig.frame();
    // The dialog's window is centred on the dock, so the dialog is too.
    let (mx, my) = rig.middle();
    let owner = rig.window("owner", Rect::new(mx - 100.0, my - 50.0, 200.0, 100.0));
    rig.tick();
    assert!(rig.z(dock) > rig.z(owner));
    let before = rig.host.root.children(rig.host.root.root());
    let dialog = ac_plugin_ui::DialogView {
        handle: 1,
        owner: UiOwner::new("test.0", "Plugin 0"),
        title: "Delete".into(),
        message: "Delete the profile?".into(),
        buttons: vec!["Yes".into(), "No".into()],
        owner_panel: Some(7),
    };
    let mut dialogs = crate::plugin::dialog::DialogPresenter::default();
    let frames = std::collections::HashMap::from([(7, owner)]);
    let outbox = Rc::clone(&rig.outbox);
    dialogs.sync(
        &mut rig.host.root,
        &[dialog],
        &frames,
        &std::collections::HashMap::new(),
        None,
        None,
        &outbox,
    );
    let shown = rig.host.root.children(rig.host.root.root());
    let dialog = *shown.iter().find(|c| !before.contains(c)).unwrap();
    for _ in 0..3 {
        rig.tick();
    }
    assert!(rig.z(dialog) > rig.z(dock));
    // Another window raised over the dock: the dock goes back over it, and
    // the dialog stays over the dock.
    let other = rig.window("other", Rect::new(400.0, 300.0, 100.0, 100.0));
    rig.tick();
    assert!(rig.z(dock) > rig.z(other));
    assert!(rig.z(dialog) > rig.z(dock));
    // A press where both are goes to the dialog, not the dock.
    let (x, y) = (mx as i32, my as i32);
    let hit = rig.host.root.pick(x, y).unwrap();
    assert!(rig.host.root.is_within(Some(hit), dialog));
    rig.host.mouse_move(x, y);
    rig.host.mouse_down(MouseButton::Left, x, y, 0);
    rig.host.mouse_up(MouseButton::Left, x, y, 0);
    rig.tick();
    assert!(rig.outbox.borrow().is_empty());
    assert!(!rig.closed());
}

#[test]
fn a_modal_plugin_window_and_a_tooltip_stay_over_the_dock() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.add_plugins(1);
    rig.sync_plain();
    let dock = rig.frame();
    let owner = rig.window("owner", Rect::new(100.0, 100.0, 200.0, 100.0));
    let modal = rig.window("modal", Rect::new(0.0, 100.0, 200.0, 200.0));
    rig.tick();
    let mut dialogs = crate::plugin::dialog::DialogPresenter::default();
    let frames = std::collections::HashMap::from([(1, owner), (2, modal)]);
    dialogs.sync_modals(&mut rig.host.root, &[(2, 1)], &frames);
    // A tooltip-like overlay, which is not a window, raised once.
    let tip = rig
        .host
        .root
        .insert(Element::at(20.0, 120.0, 80.0, 20.0), DockGear::default());
    let top = rig.host.root.root();
    rig.host.root.add_child(top, tip);
    rig.host.root.bring_to_front(tip);
    for _ in 0..3 {
        rig.tick();
    }
    assert!(rig.z(modal) > rig.z(dock));
    assert!(rig.z(tip) > rig.z(modal));
    let other = rig.window("other", Rect::new(400.0, 300.0, 100.0, 100.0));
    rig.tick();
    assert!(rig.z(dock) > rig.z(other));
    assert!(rig.z(modal) > rig.z(dock));
    assert!(rig.z(tip) > rig.z(modal));
}

#[test]
fn a_covered_dock_stays_as_the_covering_screen_left_it_and_comes_back_after() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.add_plugins(1);
    rig.sync_plain();
    assert!(rig.host.root.is_window_visible(WINDOW));
    let frame = rig.frame();
    let other = rig
        .host
        .root
        .insert(Element::at(0.0, 0.0, 100.0, 100.0), DockGear::default());
    let top = rig.host.root.root();
    rig.host.root.add_child(top, other);

    rig.with(|c, _| c.set_covered(true));
    rig.host.root.set_visible(frame, false);
    rig.host.root.bring_to_front(other);
    let z = rig.host.root.element(frame).unwrap().z_order();
    rig.tick();
    assert!(!rig.host.root.is_window_visible(WINDOW));
    // Neither a new window nor a change of screen brings it back.
    rig.add("test.new", "New", PanelDescriptor::new("main", "New"));
    rig.sync_plain();
    rig.host.root.set_size(900.0, 700.0);
    rig.tick();
    assert!(!rig.host.root.is_window_visible(WINDOW));
    assert_eq!(rig.host.root.element(frame).unwrap().z_order(), z);

    rig.with(|c, _| c.set_covered(false));
    rig.tick();
    assert!(rig.host.root.is_window_visible(WINDOW));
    let z = |rig: &Rig, id| rig.host.root.element(id).unwrap().z_order();
    assert!(z(&rig, frame) > z(&rig, other));
}

#[test]
fn hiding_keeps_it_hidden_and_showing_again_also_expands_it() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.add_plugins(1);
    rig.sync_plain();
    let tile = rig.layout().tile;
    rig.click(tile);
    assert_eq!(rig.size().1, layout::FLOATING_THICKNESS);
    rig.with(|c, ui| c.set_hidden(ui, true));
    rig.tick();
    assert!(!rig.host.root.is_window_visible(WINDOW));
    rig.with(|c, ui| c.set_hidden(ui, false));
    rig.tick();
    assert!(rig.host.root.is_window_visible(WINDOW));
    assert!(rig.size().1 > layout::FLOATING_THICKNESS);
}

// ── Closing to the tile ─────────────────────────────────────────────────

const ICON: TextureRef = TextureRef::Runtime(9);

impl Rig {
    fn closed(&mut self) -> bool {
        self.with(|c, _| c.is_minimized())
    }

    fn press_tile(&mut self) {
        let tile = self.layout().tile;
        self.click(tile);
    }

    fn tile_count(&mut self) -> usize {
        let tile = self.with(|c, _| c.tile);
        self.host.root.widget::<DockTile>(tile).unwrap().count
    }
}

#[test]
fn closed_it_is_a_square_tile_in_every_mode_on_its_edge() {
    for (mode, side) in [
        (DockMode::Floating, 48.0),
        (DockMode::FloatingHorizontal, 48.0),
        (DockMode::Left, 46.0),
        (DockMode::Right, 46.0),
        (DockMode::Top, 46.0),
        (DockMode::Bottom, 46.0),
    ] {
        let mut rig = Rig::in_mode(mode);
        rig.host.root.set_ui_locked(true);
        rig.tick();
        rig.press_tile();
        assert_eq!(rig.size(), (side, side), "{mode:?}");
        let (left, top) = rig.at();
        match mode {
            DockMode::Left => assert_eq!(left, 0.0),
            DockMode::Right => assert_eq!(left, 800.0 - side),
            DockMode::Top => assert_eq!(top, 0.0),
            DockMode::Bottom => assert_eq!(top, 600.0 - side),
            _ => {}
        }
        assert!(rig.slot_rects().iter().flatten().all(|s| !s.2), "{mode:?}");
        // The plugin windows stay as they are.
        assert!(rig.outbox.borrow().is_empty());
    }
}

#[test]
fn the_tile_shows_the_icon_open_and_closed() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.add_plugins(1);
    rig.sync_plain();
    rig.with(|c, ui| {
        c.set_art(
            ui,
            DockArt {
                icon: Some((ICON, 256.0, 256.0)),
                ..DockArt::default()
            },
        )
    });
    rig.tick();
    assert_eq!(sprites_of(&rig.draw(), ICON).len(), 1);
    rig.press_tile();
    assert_eq!(sprites_of(&rig.draw(), ICON).len(), 1);
}

#[test]
fn a_closed_tile_counts_the_open_windows_and_shows_nothing_at_none() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.add("test", "Test", PanelDescriptor::new("a", "A"));
    rig.add("test", "Test", PanelDescriptor::new("b", "B"));
    rig.add("test", "Test", PanelDescriptor::new("c", "C"));
    rig.ui.show_panel("test", "a", true);
    rig.ui.show_panel("test", "b", true);
    rig.ui.show_panel("test", "c", false);
    rig.sync_plain();
    // An open dock marks each open window, so only a closed one counts.
    assert_eq!(rig.tile_count(), 0);
    rig.press_tile();
    assert_eq!(rig.tile_count(), 2);
    let list = rig.draw();
    assert!(has_color(&draw_corners(&list), Palette::MOSS.accent));
    rig.ui.show_panel("test", "a", false);
    rig.ui.show_panel("test", "b", false);
    rig.sync_plain();
    assert_eq!(rig.tile_count(), 0);
    let list = rig.draw();
    assert!(!has_color(&draw_corners(&list), Palette::MOSS.accent));
}

#[test]
fn a_saved_state_restores_closed_and_stays_on_a_rails_edge() {
    let mut rig = Rig::in_mode(DockMode::Right);
    assert!(!rig.host.capture_window_state(WINDOW).collapsed);
    rig.host.root.set_size(1000.0, 600.0);
    rig.tick();
    rig.host.restore_window_state(
        WINDOW,
        WindowState {
            collapsed: true,
            ..WindowState::default()
        },
    );
    rig.tick();
    assert!(rig.host.capture_window_state(WINDOW).collapsed);
    assert_eq!(rig.at().0, 1000.0 - layout::RAIL_THICKNESS);
}

#[test]
fn a_closed_dock_drags_and_snaps_and_opens_as_the_rail_on_that_edge() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.press_tile();
    let (left, _) = rig.at();
    rig.drag(ON_HANDLE, (-(left as i32) + 2, 40));
    assert_eq!(rig.mode(), DockMode::Left);
    assert!(rig.closed());
    assert_eq!(rig.size(), (46.0, 46.0 + layout::GRIP_LENGTH));
    rig.press_tile();
    assert_eq!(rig.size().0, layout::RAIL_THICKNESS);
    assert!(rig.size().1 > 46.0 + layout::GRIP_LENGTH);
    assert_eq!(rig.at().0, 0.0);
}

#[test]
fn a_press_still_opens_and_closes_it_while_the_interface_is_locked() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.host.root.set_ui_locked(true);
    rig.press_tile();
    assert!(rig.closed());
    rig.press_tile();
    assert!(!rig.closed());
}

#[test]
fn opening_near_the_far_end_slides_to_fit_and_closing_goes_back() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.press_tile();
    rig.host.root.move_window(WINDOW, 300.0, 600.0);
    rig.tick();
    let closed_at = rig.at();
    let (_, closed_height) = rig.size();
    assert_eq!(closed_at.1, 600.0 - closed_height);
    rig.press_tile();
    let (_, h) = rig.size();
    assert_eq!(rig.at().1, 600.0 - h);
    let tile = rig.layout().tile;
    rig.click(tile);
    assert_eq!(rig.at(), closed_at);
}

#[test]
fn hovering_the_tile_labels_it_and_the_grip_has_no_label() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    let tile = rig.layout().tile;
    rig.hover(tile);
    assert_eq!(rig.label().unwrap().title, TILE_LABEL);
    let grip = rig.layout().grip.unwrap();
    rig.hover(grip);
    assert!(rig.label().is_none());
}

// ── The limit on its length ─────────────────────────────────────────────

impl Rig {
    /// `count` plugins with a window each, in `mode`, locked.
    fn limited(mode: DockMode, count: usize, limit: Option<usize>) -> Self {
        let mut rig = Self::new(SCREEN.1, PluginTheme::Moss);
        rig.add_plugins(count);
        rig.sync_plain();
        rig.with(|c, ui| {
            c.set_mode(ui, mode);
            c.set_slot_limit(ui, limit);
        });
        rig.tick();
        rig
    }

    fn shown_slots(&mut self) -> usize {
        self.layout().slots.iter().flatten().count()
    }
}

#[test]
fn a_limit_shows_that_many_slots_and_scrolls_the_rest_at_every_size() {
    for size in DockSize::ALL {
        let mut rig = Rig::limited(DockMode::Floating, 8, Some(3));
        rig.with(|c, ui| c.set_size(ui, size));
        rig.tick();
        assert_eq!(rig.shown_slots(), 3, "{size:?}");
        assert!(rig.layout().forward.is_some(), "{size:?}");
        assert_eq!(rig.layout().max_first, 5, "{size:?}");
    }
}

#[test]
fn a_limit_the_list_fits_in_lays_it_out_as_with_none() {
    let mut rig = Rig::limited(DockMode::Floating, 4, None);
    let plain = rig.layout();
    rig.with(|c, ui| c.set_slot_limit(ui, Some(4)));
    assert_eq!(rig.layout(), plain);
    rig.with(|c, ui| c.set_slot_limit(ui, Some(9)));
    assert_eq!(rig.layout(), plain);
}

#[test]
fn a_limit_keeps_its_slots_through_the_lock_and_gives_way_to_a_small_screen() {
    let mut rig = Rig::limited(DockMode::Left, 12, Some(5));
    assert_eq!(rig.shown_slots(), 5);
    rig.host.root.set_ui_locked(false);
    rig.tick();
    assert_eq!(rig.shown_slots(), 5);
    rig.host.root.set_size(SCREEN.0, 250.0);
    rig.tick();
    assert!(rig.shown_slots() < 5);
    assert!(rig.size().1 <= 250.0);
    rig.host.root.set_size(SCREEN.0, SCREEN.1);
    rig.tick();
    assert_eq!(rig.shown_slots(), 5);
}

#[test]
fn a_limit_keeps_the_place_in_the_list_when_it_can_and_the_closed_dock_is_still_the_tile() {
    let mut rig = Rig::limited(DockMode::Floating, 12, Some(4));
    rig.with(|c, ui| c.scroll_to(ui, 3));
    assert_eq!(rig.layout().first, 3);
    rig.with(|c, ui| c.set_slot_limit(ui, Some(6)));
    assert_eq!(rig.layout().first, 3);
    rig.with(|c, ui| c.set_slot_limit(ui, None));
    let unlimited = rig.layout().first;
    assert!(unlimited <= 3);

    let closed = rig.layout().tile;
    rig.with(|c, ui| c.set_slot_limit(ui, Some(2)));
    rig.click(closed);
    rig.tick();
    assert!(rig.closed());
    assert_eq!(rig.size().1, rig.size().0);
}

// ── The length handle ───────────────────────────────────────────────────

impl Rig {
    /// `count` plugins with a window each, in `mode`, unlocked and placed.
    fn stretchy(mode: DockMode, count: usize) -> Self {
        let mut rig = Self::limited(mode, count, None);
        rig.host.root.set_ui_locked(false);
        rig.tick();
        rig
    }

    /// The pointer on the middle of the length handle, on the screen.
    fn on_length_handle(&mut self) -> (i32, i32) {
        let handle = self.layout().length_handle.unwrap();
        let (left, top) = self.at();
        (
            (left + handle.x + handle.w / 2.0) as i32,
            (top + handle.y + handle.h / 2.0) as i32,
        )
    }

    fn grab_length_handle(&mut self) -> (i32, i32) {
        let (x, y) = self.on_length_handle();
        self.host.mouse_move(x, y);
        self.host.mouse_down(MouseButton::Left, x, y, 0);
        (x, y)
    }

    /// A press on the length handle, one move and a release.
    fn stretch(&mut self, by: (i32, i32)) {
        let (x, y) = self.grab_length_handle();
        self.move_to(x + by.0, y + by.1);
        self.release(x + by.0, y + by.1);
    }

    fn limit(&mut self) -> Option<usize> {
        self.with(|c, _| c.slot_limit())
    }

    fn stretched(&mut self) -> Option<Option<usize>> {
        self.with(|c, _| c.take_stretched_limit())
    }
}

#[test]
fn dragging_the_handle_up_shows_fewer_slots_a_whole_slot_at_a_time() {
    let mut rig = Rig::stretchy(DockMode::Floating, 12);
    rig.host.root.move_window(WINDOW, 300.0, 30.0);
    rig.tick();
    let (_, tall) = rig.size();
    assert_eq!(rig.at(), (300.0, 30.0));
    let (x, y) = rig.grab_length_handle();
    let mut lengths = vec![tall];
    for up in (5..=300).step_by(5) {
        rig.move_to(x, y - up);
        lengths.push(rig.size().1);
        // Only the far end moves.
        assert_eq!(rig.at(), (300.0, 30.0), "{up}");
    }
    rig.release(x, y - 300);
    assert!(rig.limit().is_some());
    let mut steps: Vec<f32> = lengths.windows(2).map(|w| w[0] - w[1]).collect();
    steps.retain(|s| *s != 0.0);
    assert!(!steps.is_empty());
    assert!(steps[1..].iter().all(|s| *s == 45.0), "{steps:?}");
    // It went by whole slots, and nothing is cut in half.
    assert!(rig.shown_slots() >= 1);
}

#[test]
fn the_handle_follows_the_pointer_to_within_half_a_slot_and_goes_by_the_main_axis_only() {
    for (mode, along_x) in [
        (DockMode::Floating, false),
        (DockMode::Left, false),
        (DockMode::Right, false),
        (DockMode::FloatingHorizontal, true),
        (DockMode::Top, true),
        (DockMode::Bottom, true),
    ] {
        let mut rig = Rig::stretchy(mode, 14);
        let before = rig.at();
        let across_before = if along_x { rig.size().1 } else { rig.size().0 };
        let (x, y) = rig.on_length_handle();
        let length_before = if along_x { rig.size().0 } else { rig.size().1 };
        // The screen may have kept the dock from being as long as it wants, so
        // this goes a good way back.
        let by = 200;
        let delta = if along_x { (-by, 0) } else { (0, -by) };
        let wander = if along_x { (0, 37) } else { (37, 0) };
        rig.grab_length_handle();
        rig.move_to(x + delta.0 + wander.0, y + delta.1 + wander.1);
        rig.release(x + delta.0 + wander.0, y + delta.1 + wander.1);
        let length_after = if along_x { rig.size().0 } else { rig.size().1 };
        let wanted = length_before - by as f32;
        assert!(
            (length_after - wanted).abs() <= 45.0 / 2.0 + 1.0,
            "{mode:?} {length_after} {wanted}"
        );
        let across_after = if along_x { rig.size().1 } else { rig.size().0 };
        assert_eq!(across_after, across_before, "{mode:?}");
        // Its near end and its edge stay where they were.
        assert_eq!(rig.at(), before, "{mode:?}");
        assert_eq!(rig.mode(), mode, "{mode:?}");
    }
}

#[test]
fn a_dock_touching_the_far_edge_of_the_screen_gives_way_from_that_end_and_grows_back() {
    // Too long for the screen, a floating dock is pushed up until it is flush
    // with the bottom. Shrinking it must not keep it there by moving its top.
    let mut rig = Rig::stretchy(DockMode::Floating, 40);
    let (left, top) = rig.at();
    let (_, tall) = rig.size();
    assert_eq!(top + tall, SCREEN.1);
    let (x, y) = rig.grab_length_handle();
    rig.move_to(x, y - 200);
    assert_eq!(rig.at(), (left, top));
    assert!(rig.size().1 < tall);
    let shrunk = rig.size().1;
    assert!(top + shrunk < SCREEN.1);
    // It grows back in the same drag, and the limit goes when it gets there.
    rig.move_to(x, y);
    assert_eq!(rig.at(), (left, top));
    assert_eq!(rig.size().1, tall);
    rig.release(x, y);
    assert_eq!(rig.limit(), None);
    assert_eq!(rig.stretched(), None);
}

#[test]
fn a_rail_as_long_as_the_screen_shrinks_from_its_far_end_too() {
    let mut rig = Rig::stretchy(DockMode::Right, 40);
    let at = rig.at();
    let (_, tall) = rig.size();
    assert_eq!(at.1 + tall, SCREEN.1);
    rig.stretch((0, -300));
    assert_eq!(rig.at(), at);
    assert!(rig.size().1 < tall - 250.0);
    assert!(rig.limit().is_some());
}

#[test]
fn a_bottom_rail_keeps_its_edge_as_its_far_end_moves_along_it() {
    let mut rig = Rig::stretchy(DockMode::Bottom, 40);
    let bottom = |rig: &mut Rig| rig.at().1 + rig.size().1;
    assert_eq!(bottom(&mut rig), SCREEN.1);
    let (left, _) = rig.at();
    rig.stretch((-400, 0));
    assert_eq!(bottom(&mut rig), SCREEN.1);
    assert_eq!(rig.at().0, left);
    assert!(rig.limit().is_some());
}

#[test]
fn dragged_out_until_every_slot_shows_it_has_no_limit_and_a_new_window_grows_it() {
    let mut rig = Rig::stretchy(DockMode::Floating, 6);
    rig.host.root.move_window(WINDOW, 300.0, 10.0);
    rig.tick();
    let (_, tall) = rig.size();
    rig.stretch((0, -150));
    assert!(rig.limit().is_some());
    assert!(rig.size().1 < tall);
    assert!(rig.stretched().is_some());
    rig.stretch((0, 400));
    assert_eq!(rig.limit(), None);
    assert_eq!(rig.stretched(), Some(None));
    assert_eq!(rig.size().1, tall);
    rig.add("new", "New", PanelDescriptor::new("main", "New"));
    rig.sync_plain();
    assert_eq!(rig.size().1, tall + 45.0);
    assert!(rig.layout().forward.is_none());
}

#[test]
fn it_cannot_be_dragged_past_the_screen_and_a_low_dock_stays_where_it_is() {
    let mut rig = Rig::stretchy(DockMode::Floating, 12);
    rig.with(|c, ui| c.set_slot_limit(ui, Some(2)));
    rig.host.root.move_window(WINDOW, 300.0, 200.0);
    rig.tick();
    let (x, _) = rig.grab_length_handle();
    rig.move_to(x, SCREEN.1 as i32 - 1);
    rig.release(x, SCREEN.1 as i32 - 1);
    assert_eq!(rig.at().1, 200.0);
    assert!(200.0 + rig.size().1 <= SCREEN.1);
    // 14 + 6 + 36 + 9 + 16 + five slots (216 with dividers) + 16 + 9 + 36 + 12 = 370.
    assert_eq!(rig.limit(), Some(5));
    assert_eq!(rig.size().1, 370.0);
}

#[test]
fn a_press_in_place_changes_nothing_and_the_limit_is_told_once_when_the_drag_ends() {
    let mut rig = Rig::stretchy(DockMode::Floating, 12);
    rig.with(|c, ui| c.set_slot_limit(ui, Some(5)));
    rig.tick();
    let (x, y) = rig.grab_length_handle();
    rig.tick();
    rig.release(x, y);
    assert_eq!(rig.limit(), Some(5));
    assert_eq!(rig.stretched(), None);

    let (x, y) = rig.grab_length_handle();
    rig.move_to(x, y - 90);
    assert_eq!(rig.limit(), Some(3));
    assert_eq!(rig.stretched(), None);
    rig.release(x, y - 90);
    assert_eq!(rig.stretched(), Some(Some(3)));
    assert_eq!(rig.stretched(), None);
}

#[test]
fn a_whole_drag_between_two_frames_still_counts() {
    let mut rig = Rig::stretchy(DockMode::Floating, 12);
    rig.with(|c, ui| c.set_slot_limit(ui, Some(5)));
    rig.tick();
    let (x, y) = rig.grab_length_handle();
    rig.host.mouse_move(x, y - 90);
    rig.host.mouse_up(MouseButton::Left, x, y - 90, 0);
    rig.tick();
    assert_eq!(rig.limit(), Some(3));
    assert_eq!(rig.stretched(), Some(Some(3)));
}

#[test]
fn the_handle_is_not_there_locked_closed_or_with_one_slot_and_a_press_there_does_nothing() {
    let mut rig = Rig::stretchy(DockMode::Floating, 6);
    assert!(rig.layout().length_handle.is_some());
    rig.host.root.set_ui_locked(true);
    rig.tick();
    assert_eq!(rig.layout().length_handle, None);
    rig.host.root.set_ui_locked(false);
    rig.tick();
    let tile = rig.layout().tile;
    rig.click(tile);
    rig.tick();
    assert!(rig.closed());
    assert_eq!(rig.layout().length_handle, None);

    let mut rig = Rig::stretchy(DockMode::Floating, 1);
    assert_eq!(rig.layout().length_handle, None);
}

#[test]
fn the_handle_shows_the_resize_cursor_that_runs_along_the_dock_and_moves_nothing_else() {
    use crate::cursor::{
        CursorFacts, CursorFeedbackController, CursorFeedbackKind, CursorTargetMode,
    };
    use ac_core::combat::CombatMode;
    for (mode, kind) in [
        (DockMode::Floating, CursorFeedbackKind::ResizeVertical),
        (DockMode::Right, CursorFeedbackKind::ResizeVertical),
        (DockMode::Top, CursorFeedbackKind::ResizeHorizontal),
    ] {
        let mut rig = Rig::stretchy(mode, 6);
        let (x, y) = rig.on_length_handle();
        rig.host.mouse_move(x, y);
        rig.tick();
        let never = |_: u32| false;
        let facts = CursorFacts {
            target_mode: CursorTargetMode::None,
            busy_count: 0,
            combat_mode: CombatMode::NON_COMBAT,
            world_target: 0,
            target_compatible: &never,
        };
        let mut cursor = CursorFeedbackController::new();
        assert_eq!(
            cursor.update(&mut rig.host.root, &facts).kind,
            kind,
            "{mode:?}"
        );
        assert!(!rig.host.root.hover_window_move());
        // Off it, the cursor goes back: the rest of the dock is not resizable.
        let gear = rig.layout().gear;
        rig.hover(gear);
        assert_ne!(
            cursor.update(&mut rig.host.root, &facts).kind,
            kind,
            "{mode:?}"
        );
    }
}

#[test]
fn the_handle_draws_a_bar_and_a_well_only_under_the_pointer() {
    let p = PluginTheme::Moss.palette().unwrap();
    let r = Rect::new(0.0, 0.0, 48.0, layout::LENGTH_HANDLE);
    let rest = drawn(|rc| style::length_handle(rc, &p, r, false, 4.0, ButtonLook::Normal));
    let over = drawn(|rc| style::length_handle(rc, &p, r, false, 4.0, ButtonLook::Hovered));
    assert!(has_color(&rest, p.muted));
    assert!(!has_color(&rest, hover(&p, p.field)));
    assert!(has_color(&over, p.text));
    assert!(has_color(&over, hover(&p, p.field)));
}

#[test]
fn a_peeking_dock_stays_open_while_its_handle_is_held_and_the_pointer_wanders() {
    let mut rig = Rig::stretchy(DockMode::Floating, 8);
    rig.host.root.move_window(WINDOW, 300.0, 100.0);
    rig.tick();
    rig.press_tile();
    rig.with(|c, ui| c.set_opens(ui, DockOpens::Hover));
    rig.host.mouse_move(700, 500);
    rig.pass(1.0);
    assert!(rig.closed_view());
    let tile = rig.layout().tile;
    rig.hover(tile);
    rig.pass(0.3);
    assert!(!rig.closed_view());
    let (x, y) = rig.grab_length_handle();
    rig.move_to(x + 300, y - 60);
    rig.pass(2.0);
    assert!(!rig.closed_view());
    rig.release(x + 300, y - 60);
    assert!(rig.stretched().flatten().is_some());
    rig.pass(1.0);
    assert!(rig.closed_view());
}

#[test]
fn hiding_the_dock_while_its_handle_is_held_ends_the_drag() {
    let mut rig = Rig::stretchy(DockMode::Floating, 12);
    rig.with(|c, ui| c.set_slot_limit(ui, Some(5)));
    rig.tick();
    let (x, y) = rig.grab_length_handle();
    rig.move_to(x, y - 90);
    assert_eq!(rig.limit(), Some(3));
    rig.with(|c, ui| c.set_hidden(ui, true));
    rig.tick();
    assert_eq!(rig.stretched(), Some(Some(3)));
    rig.move_to(x, y - 200);
    rig.release(x, y - 200);
    assert_eq!(rig.limit(), Some(3));
    rig.with(|c, ui| c.set_hidden(ui, false));
    rig.tick();
    assert_eq!(rig.limit(), Some(3));
    assert!(!rig.with(|c, ui| c.length_handle_held(ui)));
}

#[test]
fn taking_hold_of_the_handle_on_a_dock_opening_finishes_the_opening() {
    let mut rig = Rig::stretchy(DockMode::Floating, 8);
    rig.host.root.move_window(WINDOW, 300.0, 100.0);
    rig.tick();
    let tile = rig.layout().tile;
    rig.click(tile);
    rig.tick();
    assert!(rig.closed());
    rig.with(|c, _| c.reveal_seconds = 10.0);
    let tile = rig.layout().tile;
    rig.click(tile);
    rig.tick();
    assert!(rig.with(|c, _| c.reveal.is_some()));
    let handle = rig.layout().length_handle.unwrap();
    rig.with(|c, ui| {
        if let Some(h) = ui.widget_mut::<DockLengthHandle>(c.length_handle) {
            h.grabbed = Some(LengthGrab {
                along: handle.h / 2.0,
                press: (0, 0),
            });
        }
    });
    rig.tick();
    assert!(rig.with(|c, _| c.reveal.is_none()));
    assert_eq!(rig.size().1, rig.with(|c, _| c.layout().height),);
}

// ── The grip ────────────────────────────────────────────────────────────

impl Rig {
    fn grip_shows(&mut self) -> bool {
        let grip = self.with(|c, _| c.grip);
        self.host.root.element(grip).unwrap().visible()
    }

    fn move_cursor_over(&mut self, r: Rect) -> bool {
        self.hover(r);
        self.host.root.hover_window_move()
    }
}

#[test]
fn the_grip_shows_only_while_windows_are_unlocked_and_the_dock_changes_to_fit() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.host.root.move_window(WINDOW, 300.0, 100.0);
    rig.tick();
    assert!(rig.grip_shows());
    let (w, h) = rig.size();
    let tile = rig.layout().tile;
    assert_eq!(
        rig.layout().grip,
        Some(Rect::new(0.0, 0.0, w, layout::GRIP_LENGTH))
    );

    rig.host.root.set_ui_locked(true);
    rig.tick();
    assert!(!rig.grip_shows());
    assert_eq!(rig.layout().grip, None);
    // The grip goes, and the handle gives way to the padding it stood for.
    let locked = h - layout::GRIP_LENGTH - (layout::LENGTH_HANDLE - layout::PADDING);
    assert_eq!(rig.size(), (w, locked));
    assert_eq!(rig.layout().tile.y, tile.y - layout::GRIP_LENGTH);
    // The dock stays where it was, whichever way the lock goes.
    assert_eq!(rig.at(), (300.0, 100.0));

    rig.host.root.set_ui_locked(false);
    rig.tick();
    assert!(rig.grip_shows());
    assert_eq!(rig.size(), (w, h));
    assert_eq!(rig.at(), (300.0, 100.0));
}

#[test]
fn only_the_grip_shows_the_move_cursor() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    let (grip, tile) = {
        let l = rig.layout();
        (l.grip.unwrap(), l.tile)
    };
    assert!(rig.move_cursor_over(grip));
    assert!(!rig.move_cursor_over(tile));
    rig.press_tile();
    let (grip, tile) = {
        let l = rig.layout();
        (l.grip.unwrap(), l.tile)
    };
    assert!(rig.move_cursor_over(grip));
    assert!(!rig.move_cursor_over(tile));
}

#[test]
fn the_grip_moves_the_dock_open_and_closed_and_never_opens_or_closes_it() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.host.root.move_window(WINDOW, 300.0, 100.0);
    rig.tick();
    rig.drag(ON_HANDLE, (20, 30));
    assert_eq!(rig.at(), (320.0, 130.0));
    assert!(!rig.closed());
    // A press in place does nothing at all.
    let (x, y) = rig.grab(ON_HANDLE);
    rig.release(x, y);
    assert_eq!(rig.at(), (320.0, 130.0));
    assert!(!rig.closed());

    rig.press_tile();
    assert!(rig.closed());
    rig.drag(ON_HANDLE, (-20, -30));
    assert_eq!(rig.at(), (300.0, 100.0));
    assert!(rig.closed());
    let (x, y) = rig.grab(ON_HANDLE);
    rig.release(x, y);
    assert!(rig.closed());
}

#[test]
fn the_tile_acts_the_same_locked_or_not_and_never_moves_the_dock() {
    for locked in [false, true] {
        let mut rig = Rig::in_mode(DockMode::Floating);
        rig.host.root.move_window(WINDOW, 300.0, 100.0);
        rig.host.root.set_ui_locked(locked);
        rig.tick();
        // Pressed, it opens and closes.
        rig.press_tile();
        assert!(rig.closed(), "locked {locked}");
        rig.press_tile();
        assert!(!rig.closed(), "locked {locked}");
        // Dragged off itself, it leaves the dock where it is. A press and
        // release on two places is not a click.
        let tile = rig.layout().tile;
        let (x, y) = rig.point(tile);
        rig.host.mouse_move(x, y);
        rig.host.mouse_down(MouseButton::Left, x, y, 0);
        rig.move_to(x + 120, y + 90);
        rig.release(x + 120, y + 90);
        assert_eq!(rig.at(), (300.0, 100.0), "locked {locked}");
        assert!(!rig.closed(), "locked {locked}");
        assert_eq!(rig.mode(), DockMode::Floating);
    }
}

#[test]
fn the_grip_draws_its_dots_and_a_well_only_under_the_pointer() {
    let p = PluginTheme::Moss.palette().unwrap();
    let r = Rect::new(0.0, 0.0, 48.0, layout::GRIP_LENGTH);
    let rest = drawn(|rc| style::grip(rc, &p, r, false, 4.0, ButtonLook::Normal));
    let over = drawn(|rc| style::grip(rc, &p, r, false, 4.0, ButtonLook::Hovered));
    assert!(has_color(&rest, p.muted));
    assert!(!has_color(&rest, hover(&p, p.field)));
    assert!(has_color(&over, p.text));
    assert!(has_color(&over, hover(&p, p.field)));
}

#[test]
fn a_horizontal_docks_grip_stands_across_it() {
    let mut rig = Rig::in_mode(DockMode::Top);
    let (w, h) = rig.size();
    assert_eq!(
        rig.layout().grip,
        Some(Rect::new(0.0, 0.0, layout::GRIP_LENGTH, h))
    );
    rig.host.root.set_ui_locked(true);
    rig.tick();
    let locked = w - layout::GRIP_LENGTH - (layout::LENGTH_HANDLE - layout::PADDING);
    assert_eq!(rig.size(), (locked, h));
}

// ── Peeking open ────────────────────────────────────────────────────────

impl Rig {
    /// A closed dock that opens on hover, with the pointer away from it.
    fn hover_mode() -> Self {
        let mut rig = Self::in_mode(DockMode::Floating);
        rig.press_tile();
        rig.with(|c, ui| c.set_opens(ui, DockOpens::Hover));
        rig.host.mouse_move(600, 500);
        rig.pass(1.0);
        assert!(rig.closed_view());
        rig
    }

    /// A frame `seconds` long.
    fn pass(&mut self, seconds: f64) {
        self.now += (seconds * 1000.0) as i64;
        self.host.tick(seconds, self.now, None);
    }

    fn closed_view(&mut self) -> bool {
        self.with(|c, _| c.closed())
    }

    fn pointer_on_dock(&mut self) {
        let tile = self.layout().tile;
        self.hover(tile);
    }
}

#[test]
fn with_hover_on_the_pointer_peeks_it_open_and_leaving_closes_it() {
    let mut rig = Rig::hover_mode();
    rig.pointer_on_dock();
    rig.pass(0.05);
    assert!(rig.closed_view(), "not before a moment has passed");
    rig.pass(0.1);
    assert!(!rig.closed_view());
    assert!(rig.size().1 > layout::FLOATING_THICKNESS);
    // Across the opened dock it stays open.
    let slot = rig.layout().slots[0].unwrap();
    rig.hover(slot);
    rig.pass(1.0);
    assert!(!rig.closed_view());
    rig.host.mouse_move(600, 500);
    rig.pass(0.3);
    assert!(!rig.closed_view(), "not straight away");
    rig.pass(0.3);
    assert!(rig.closed_view());
}

#[test]
fn a_peek_is_not_saved_as_open() {
    let mut rig = Rig::hover_mode();
    rig.pointer_on_dock();
    rig.pass(0.2);
    assert!(!rig.closed_view());
    assert!(rig.host.capture_window_state(WINDOW).collapsed);
}

#[test]
fn with_hover_off_the_pointer_does_nothing() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.press_tile();
    rig.pointer_on_dock();
    rig.pass(2.0);
    assert!(rig.closed_view());
}

#[test]
fn with_hover_on_a_press_on_a_peek_keeps_it_open_and_the_next_closes_it_for_good() {
    let mut rig = Rig::hover_mode();
    rig.pointer_on_dock();
    rig.pass(0.2);
    rig.press_tile();
    assert!(!rig.with(|c, _| c.is_minimized()));
    rig.host.mouse_move(600, 500);
    rig.pass(2.0);
    assert!(!rig.closed_view(), "kept open");

    // Closing it leaves the pointer on the tile, which does not reopen it.
    rig.press_tile();
    assert!(rig.closed_view());
    rig.pass(2.0);
    assert!(rig.closed_view());
    // Until the pointer has left and come back.
    rig.host.mouse_move(600, 500);
    rig.pass(0.1);
    rig.pointer_on_dock();
    rig.pass(0.2);
    assert!(!rig.closed_view());
}

#[test]
fn nothing_peeks_it_open_while_a_window_is_moved_or_it_is_covered() {
    let mut rig = Rig::hover_mode();
    let other = rig.host.root.insert(
        {
            let mut e = Element::at(400.0, 300.0, 100.0, 100.0);
            e.window_move_handle = true;
            e
        },
        DockGear::default(),
    );
    let top = rig.host.root.root();
    rig.host.root.add_child(top, other);
    rig.host.mouse_move(420, 320);
    rig.host.mouse_down(MouseButton::Left, 420, 320, 0);
    rig.pointer_on_dock();
    rig.pass(1.0);
    assert!(rig.closed_view());
    rig.host.mouse_up(MouseButton::Left, 420, 320, 0);
    rig.host.mouse_move(600, 500);
    rig.pass(0.1);

    rig.with(|c, _| c.set_covered(true));
    rig.pointer_on_dock();
    rig.pass(1.0);
    assert!(rig.closed_view());
}

#[test]
fn a_peek_near_the_far_end_slides_to_fit_and_goes_back() {
    let mut rig = Rig::hover_mode();
    rig.host.root.move_window(WINDOW, 300.0, 600.0);
    rig.pass(0.016);
    let closed_at = rig.at();
    rig.pointer_on_dock();
    rig.pass(0.2);
    assert!(rig.at().1 < closed_at.1);
    rig.host.mouse_move(100, 100);
    rig.pass(1.0);
    assert_eq!(rig.at(), closed_at);
}

#[test]
fn the_wheel_scrolls_a_peeking_dock() {
    let mut rig = Rig::new(300.0, PluginTheme::Moss);
    rig.add_plugins(12);
    rig.sync_plain();
    rig.press_tile();
    rig.with(|c, ui| c.set_opens(ui, DockOpens::Hover));
    rig.host.mouse_move(600, 250);
    rig.pass(0.016);
    rig.pointer_on_dock();
    rig.pass(0.2);
    assert!(!rig.closed_view());
    let frame = rig.frame();
    rig.host.root.send_message(frame, ElementMessage::Wheel(-1));
    rig.host.pump();
    rig.pass(0.016);
    assert_eq!(rig.layout().first, 1);
}

#[test]
fn the_grip_of_a_closed_dock_does_not_peek_it_open_and_it_moves_closed() {
    let mut rig = Rig::hover_mode();
    let grip = rig.layout().grip.unwrap();
    rig.hover(grip);
    rig.pass(1.0);
    assert!(rig.closed_view());
    rig.host.root.move_window(WINDOW, 300.0, 100.0);
    rig.tick();
    rig.drag(ON_HANDLE, (20, 30));
    rig.pass(1.0);
    assert!(rig.closed_view());
    assert_eq!(rig.at(), (320.0, 130.0));
}

#[test]
fn a_peeking_dock_stays_open_on_its_grip() {
    let mut rig = Rig::hover_mode();
    let tile = rig.layout().tile;
    rig.hover(tile);
    rig.pass(1.0);
    assert!(!rig.closed_view());
    let grip = rig.layout().grip.unwrap();
    rig.hover(grip);
    rig.pass(1.0);
    assert!(!rig.closed_view());
}

#[test]
fn turning_hover_off_ends_a_peek() {
    let mut rig = Rig::hover_mode();
    rig.pointer_on_dock();
    rig.pass(0.2);
    assert!(!rig.closed_view());
    rig.with(|c, ui| c.set_opens(ui, DockOpens::Click));
    assert!(rig.closed_view());
}

// ── Sizes ───────────────────────────────────────────────────────────────

#[test]
fn a_size_changes_the_dock_and_its_art_and_keeps_its_edge() {
    const FILE: TextureRef = FILE_TEXTURE;
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.ui.set_file_icon("acdream.test", Some(icon_file()));
    rig.add("acdream.test", "Test", PanelDescriptor::new("main", "Test"));
    rig.sync_plain();
    assert_eq!(rig.size().0, 48.0);
    for (size, thick, art) in [
        (DockSize::Smaller, 38.0, 18.0),
        (DockSize::Bigger, 58.0, 32.0),
        (DockSize::Default, 48.0, 24.0),
    ] {
        rig.with(|c, ui| c.set_size(ui, size));
        rig.tick();
        assert_eq!(rig.size().0, thick, "{size:?}");
        let drawn = sprites_of(&rig.draw(), FILE);
        assert_eq!((drawn[0].0, drawn[0].1), (art, art), "{size:?}");
    }
    rig.with(|c, ui| c.set_mode(ui, DockMode::Right));
    rig.with(|c, ui| c.set_size(ui, DockSize::Bigger));
    rig.tick();
    assert_eq!(rig.at().0, 800.0 - 56.0);
}

#[test]
fn a_bigger_dock_too_long_for_the_screen_scrolls_sooner() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.add_plugins(10);
    rig.sync_plain();
    let at_default = rig.layout().visible;
    rig.with(|c, ui| c.set_size(ui, DockSize::Bigger));
    rig.tick();
    let bigger = rig.layout();
    assert!(bigger.visible < at_default);
    assert!(bigger.forward.is_some());
    assert!(bigger.height <= 600.0 - 2.0 * layout::FLOATING_MARGIN);
}

// ── Snapping ────────────────────────────────────────────────────────────

impl Rig {
    /// A dock of two windows of one plugin in `mode`, away from the corners,
    /// with windows unlocked so that it can be moved.
    fn in_mode(mode: DockMode) -> Self {
        let mut rig = Self::new(SCREEN.1, PluginTheme::Moss);
        rig.host.root.set_ui_locked(false);
        rig.add("test", "Test", PanelDescriptor::new("a", "Window A"));
        rig.add("test", "Test", PanelDescriptor::new("b", "Window B"));
        rig.sync_plain();
        rig.with(|c, ui| c.set_mode(ui, mode));
        rig.tick();
        rig
    }

    fn mode(&mut self) -> DockMode {
        self.with(|c, _| c.mode())
    }

    /// Presses on the dock, `offset` in from its corner, and returns
    /// where.
    fn grab(&mut self, offset: (i32, i32)) -> (i32, i32) {
        let (left, top) = self.at();
        let (x, y) = (left as i32 + offset.0, top as i32 + offset.1);
        self.host.mouse_move(x, y);
        self.host.mouse_down(MouseButton::Left, x, y, 0);
        (x, y)
    }

    fn move_to(&mut self, x: i32, y: i32) {
        self.host.mouse_move(x, y);
        self.tick();
    }

    fn release(&mut self, x: i32, y: i32) {
        self.host.mouse_up(MouseButton::Left, x, y, 0);
        self.tick();
    }

    /// A press, one move and a release.
    fn drag(&mut self, offset: (i32, i32), by: (i32, i32)) {
        let (x, y) = self.grab(offset);
        self.move_to(x + by.0, y + by.1);
        self.release(x + by.0, y + by.1);
    }

    /// The modes a drag goes through in `count` steps of `step`.
    fn steps(&mut self, from: (i32, i32), step: (i32, i32), count: i32) -> Vec<DockMode> {
        (1..=count)
            .map(|i| {
                self.move_to(from.0 + step.0 * i, from.1 + step.1 * i);
                self.mode()
            })
            .collect()
    }
}

fn changes(modes: &[DockMode], start: DockMode) -> usize {
    let mut last = start;
    let mut n = 0;
    for mode in modes {
        if *mode != last {
            n += 1;
        }
        last = *mode;
    }
    n
}

const ON_HANDLE: (i32, i32) = (10, 10);
const ON_TURNED_HANDLE: (i32, i32) = (10, 10);

#[test]
fn picking_a_mode_moves_the_dock_and_keeps_its_place_along_the_edge() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    let (_, top) = rig.at();
    rig.with(|c, ui| c.set_mode(ui, DockMode::Left));
    rig.tick();
    assert_eq!(
        (rig.at(), rig.size().0),
        ((0.0, top), layout::RAIL_THICKNESS)
    );
    rig.with(|c, ui| c.set_mode(ui, DockMode::Right));
    rig.tick();
    assert_eq!(rig.at(), (800.0 - layout::RAIL_THICKNESS, top));
    rig.with(|c, ui| c.set_mode(ui, DockMode::Floating));
    rig.tick();
    assert_eq!(rig.at(), (800.0 - layout::FLOATING_THICKNESS - 10.0, top));
    rig.with(|c, ui| c.set_mode(ui, DockMode::Left));
    rig.tick();
    rig.with(|c, ui| c.set_mode(ui, DockMode::Floating));
    rig.tick();
    assert_eq!(rig.at().0, 10.0);
}

#[test]
fn top_and_bottom_rails_lie_along_their_edges_and_float_ten_in() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.host.root.move_window(WINDOW, 300.0, 100.0);
    rig.tick();
    rig.with(|c, ui| c.set_mode(ui, DockMode::Top));
    rig.tick();
    assert_eq!(rig.at(), (300.0, 0.0));
    assert_eq!(rig.size().1, layout::RAIL_THICKNESS);
    assert!(rig.size().0 > rig.size().1);
    rig.with(|c, ui| c.set_mode(ui, DockMode::Bottom));
    rig.tick();
    assert_eq!(rig.at(), (300.0, 600.0 - layout::RAIL_THICKNESS));
    rig.with(|c, ui| c.set_mode(ui, DockMode::FloatingHorizontal));
    rig.tick();
    assert_eq!(rig.at(), (300.0, 600.0 - layout::FLOATING_THICKNESS - 10.0));
    assert_eq!(rig.size().1, layout::FLOATING_THICKNESS);
}

#[test]
fn dragging_near_the_left_edge_snaps_at_once_and_the_mode_is_saved_when_the_drag_ends() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    let (x, y) = rig.grab(ON_HANDLE);
    rig.move_to(x - 4, y + 30);
    assert_eq!(rig.mode(), DockMode::Left);
    assert_eq!(rig.at().0, 0.0);
    assert_eq!(rig.size().0, layout::RAIL_THICKNESS);
    assert_eq!(rig.with(|c, _| c.take_dragged_mode()), None);
    rig.release(x - 4, y + 30);
    assert_eq!(rig.with(|c, _| c.take_dragged_mode()), Some(DockMode::Left));
    assert_eq!(rig.with(|c, _| c.take_dragged_mode()), None);
    assert_eq!(rig.mode(), DockMode::Left);
    assert_eq!(rig.at().1, DEFAULT_TOP + 30.0);
}

#[test]
fn dragging_near_the_right_edge_snaps_to_the_right() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.drag(ON_HANDLE, (800 - 48 - 10 - 4, 0));
    assert_eq!(rig.mode(), DockMode::Right);
    assert_eq!(rig.at().0, 800.0 - layout::RAIL_THICKNESS);
}

#[test]
fn dragging_near_the_top_edge_turns_the_dock_into_a_top_rail() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.host.root.move_window(WINDOW, 300.0, 100.0);
    rig.tick();
    rig.drag(ON_HANDLE, (20, -96));
    assert_eq!(rig.mode(), DockMode::Top);
    assert_eq!(rig.at(), (320.0, 0.0));
    assert_eq!(rig.size().1, layout::RAIL_THICKNESS);
}

#[test]
fn dragging_past_the_bottom_edge_turns_the_dock_into_a_bottom_rail() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.host.root.move_window(WINDOW, 100.0, 100.0);
    rig.tick();
    let (x, _) = rig.grab(ON_HANDLE);
    // The dock's bottom reaches the edge first, with the pointer nearer the
    // left: it stays floating until the bottom is the edge the pointer is near.
    let (_, h) = rig.size();
    rig.move_to(x, 600 - h as i32 + 5);
    assert_eq!(rig.mode(), DockMode::Floating);
    rig.move_to(x, 590);
    assert_eq!(rig.mode(), DockMode::Bottom);
    rig.release(x, 590);
    assert_eq!(rig.at().1, 600.0 - layout::RAIL_THICKNESS);
}

#[test]
fn a_drag_that_ends_in_the_mode_it_began_saves_nothing() {
    let mut rig = Rig::in_mode(DockMode::Left);
    rig.drag(ON_HANDLE, (5, 60));
    assert_eq!(rig.with(|c, _| c.take_dragged_mode()), None);
    // Pulled off and back on within one drag.
    let (x, y) = rig.grab(ON_HANDLE);
    rig.move_to(x + 100, y);
    assert_eq!(rig.mode(), DockMode::Floating);
    rig.move_to(x, y);
    assert_eq!(rig.mode(), DockMode::Left);
    rig.release(x, y);
    assert_eq!(rig.with(|c, _| c.take_dragged_mode()), None);
}

#[test]
fn a_rail_slides_along_its_edge_and_floats_when_pulled_more_than_32_away() {
    let mut rig = Rig::in_mode(DockMode::Left);
    let top = rig.at().1;
    rig.drag(ON_HANDLE, (20, 60));
    assert_eq!(rig.mode(), DockMode::Left);
    assert_eq!(rig.at(), (0.0, top + 60.0));
    rig.drag(ON_HANDLE, (40, 0));
    assert_eq!(rig.mode(), DockMode::Floating);
    assert_eq!(rig.size().0, layout::FLOATING_THICKNESS);
    assert_eq!(rig.at().0, 40.0);
}

#[test]
fn a_top_or_bottom_rail_pulled_away_floats_horizontally() {
    let mut rig = Rig::in_mode(DockMode::Top);
    rig.host.root.move_window(WINDOW, 300.0, 0.0);
    rig.tick();
    let left = rig.at().0;
    rig.drag(ON_TURNED_HANDLE, (50, 20));
    assert_eq!(rig.mode(), DockMode::Top);
    assert_eq!(rig.at(), (left + 50.0, 0.0));
    rig.drag(ON_TURNED_HANDLE, (0, 40));
    assert_eq!(rig.mode(), DockMode::FloatingHorizontal);
    assert_eq!(rig.size().1, layout::FLOATING_THICKNESS);

    let mut rig = Rig::in_mode(DockMode::Bottom);
    rig.host.root.move_window(WINDOW, 300.0, 600.0);
    rig.tick();
    rig.drag(ON_TURNED_HANDLE, (0, -40));
    assert_eq!(rig.mode(), DockMode::FloatingHorizontal);
}

#[test]
fn rails_stay_on_their_edges_when_the_screen_resizes() {
    let mut rig = Rig::in_mode(DockMode::Right);
    rig.host.root.set_size(1000.0, 600.0);
    rig.tick();
    assert_eq!(rig.at().0, 1000.0 - layout::RAIL_THICKNESS);

    let mut rig = Rig::in_mode(DockMode::Bottom);
    rig.host.root.set_size(800.0, 700.0);
    rig.tick();
    assert_eq!(rig.at().1, 700.0 - layout::RAIL_THICKNESS);
    rig.host.root.set_size(800.0, 500.0);
    rig.tick();
    assert_eq!(rig.at().1, 500.0 - layout::RAIL_THICKNESS);
}

#[test]
fn a_right_rail_grabbed_near_its_inner_edge_slides_and_stays_a_rail() {
    let mut rig = Rig::in_mode(DockMode::Right);
    let top = rig.at().1;
    let (x, y) = rig.grab((5, 5));
    let modes = rig.steps((x, y), (0, 6), 8);
    assert!(modes.iter().all(|m| *m == DockMode::Right));
    assert_eq!(rig.at(), (800.0 - layout::RAIL_THICKNESS, top + 48.0));
    rig.release(x, y + 48);
    assert_eq!(rig.mode(), DockMode::Right);
}

#[test]
fn a_slow_drag_to_any_edge_changes_mode_once() {
    // To the right, grabbed near the dock's near edge.
    let mut rig = Rig::in_mode(DockMode::Floating);
    let from = rig.grab((8, 8));
    let modes = rig.steps(from, (2, 0), 400);
    assert_eq!(changes(&modes, DockMode::Floating), 1);
    assert_eq!(rig.mode(), DockMode::Right);

    // To the left, with a far grab.
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.host.root.move_window(WINDOW, 600.0, DEFAULT_TOP);
    rig.tick();
    let from = rig.grab((28, 8));
    let modes = rig.steps(from, (-2, 0), 330);
    assert_eq!(changes(&modes, DockMode::Floating), 1);
    assert_eq!(rig.mode(), DockMode::Left);

    // Up to the top.
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.host.root.move_window(WINDOW, 300.0, 300.0);
    rig.tick();
    let from = rig.grab(ON_HANDLE);
    let modes = rig.steps(from, (0, -2), 200);
    assert_eq!(changes(&modes, DockMode::Floating), 1);
    assert_eq!(rig.mode(), DockMode::Top);

    // Down to the bottom.
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.host.root.move_window(WINDOW, 300.0, 100.0);
    rig.tick();
    let from = rig.grab(ON_HANDLE);
    let modes = rig.steps(from, (0, 2), 280);
    assert_eq!(changes(&modes, DockMode::Floating), 1);
    assert_eq!(rig.mode(), DockMode::Bottom);
}

#[test]
fn a_dock_that_snaps_by_its_far_end_holds_on_as_the_pointer_carries_on() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    rig.host.root.move_window(WINDOW, 300.0, 100.0);
    rig.tick();
    let from = rig.grab(ON_HANDLE);
    let (_, h) = rig.size();
    // Down until its bottom meets the edge, far from the pointer.
    rig.move_to(from.0, 600 - h as i32 + 5);
    assert_eq!(rig.mode(), DockMode::Bottom);
    // The rail is now well below the pointer; moving on does not pull it off.
    let y = 600 - h as i32 + 5;
    for i in 1..=4 {
        rig.move_to(from.0, y - i * 5);
        assert_eq!(rig.mode(), DockMode::Bottom);
    }
    rig.move_to(from.0, y - 40);
    assert_eq!(rig.mode(), DockMode::FloatingHorizontal);
}

#[test]
fn a_rail_pulled_away_floats_and_stays_floating_on_further_small_moves() {
    let mut rig = Rig::in_mode(DockMode::Left);
    let from = rig.grab(ON_HANDLE);
    let modes = rig.steps(from, (3, 1), 30);
    let first = modes.iter().position(|m| *m == DockMode::Floating).unwrap();
    assert!(first > 0);
    assert!(modes[first..].iter().all(|m| *m == DockMode::Floating));
}

#[test]
fn dragging_another_window_near_an_edge_does_not_snap_the_dock() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    let other = rig.host.root.insert(
        {
            let mut e = Element::at(400.0, 300.0, 100.0, 100.0);
            e.window_move_handle = true;
            e
        },
        DockGear::default(),
    );
    let top = rig.host.root.root();
    rig.host.root.add_child(top, other);
    // A dock left touching an edge by a saved place, with the pointer near it.
    rig.host.root.move_window(WINDOW, 4.0, 200.0);
    rig.tick();
    rig.host.mouse_move(420, 320);
    rig.host.mouse_down(MouseButton::Left, 420, 320, 0);
    assert!(rig.host.root.is_window_move_active());
    rig.move_to(5, 320);
    rig.release(5, 320);
    assert_eq!(rig.mode(), DockMode::Floating);
}

#[test]
fn a_press_that_never_moves_the_dock_leaves_its_mode_alone() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    // Left touching an edge by a saved place.
    rig.host.root.move_window(WINDOW, 4.0, 200.0);
    rig.tick();
    let (x, y) = rig.grab(ON_HANDLE);
    rig.tick();
    rig.release(x, y);
    assert_eq!(rig.mode(), DockMode::Floating);
    // The first real move is what snaps it.
    rig.drag(ON_HANDLE, (0, 6));
    assert_eq!(rig.mode(), DockMode::Left);
}

#[test]
fn a_whole_drag_between_two_frames_still_snaps_and_saves() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    let (_, y) = rig.grab(ON_HANDLE);
    rig.host.mouse_move(4, y);
    rig.host.mouse_up(MouseButton::Left, 4, y, 0);
    rig.tick();
    assert_eq!(rig.mode(), DockMode::Left);
    assert_eq!(rig.at().0, 0.0);
    assert_eq!(rig.with(|c, _| c.take_dragged_mode()), Some(DockMode::Left));
}

#[test]
fn a_press_and_release_between_frames_is_not_taken_for_another_windows_drag() {
    let mut rig = Rig::in_mode(DockMode::Floating);
    let other = rig.host.root.insert(
        {
            let mut e = Element::at(400.0, 300.0, 100.0, 100.0);
            e.window_move_handle = true;
            e
        },
        DockGear::default(),
    );
    let top = rig.host.root.root();
    rig.host.root.add_child(top, other);
    rig.host.root.move_window(WINDOW, 4.0, 200.0);
    rig.tick();
    let (x, y) = rig.grab(ON_HANDLE);
    rig.host.mouse_up(MouseButton::Left, x, y, 0);
    rig.tick();
    rig.host.mouse_move(420, 320);
    rig.host.mouse_down(MouseButton::Left, 420, 320, 0);
    rig.move_to(30, 320);
    rig.release(30, 320);
    assert_eq!(rig.mode(), DockMode::Floating);
}

#[test]
fn edge_pills_ease_to_their_lengths_and_the_front_window_has_the_long_one() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    let a = rig.add("test", "Test", PanelDescriptor::new("a", "A"));
    let b = rig.add("test", "Test", PanelDescriptor::new("b", "B"));
    rig.add("test", "Test", PanelDescriptor::new("c", "C"));
    rig.ui.show_panel("test", "a", true);
    rig.ui.show_panel("test", "b", true);
    rig.ui.show_panel("test", "c", false);
    let frames: Vec<ElementId> = ["a", "b", "c"]
        .iter()
        .map(|view| {
            let id = rig
                .host
                .root
                .insert(Element::at(300.0, 100.0, 100.0, 100.0), DockGear::default());
            let top = rig.host.root.root();
            rig.host.root.add_child(top, id);
            rig.host
                .root
                .register_window(&format!("plugin:test:{view}"), id, Some(id), 0)
                .unwrap();
            id
        })
        .collect();
    rig.sync_plain();
    rig.with(|c, ui| c.set_mode(ui, DockMode::Left));
    rig.host.root.bring_to_front(frames[0]);
    let pills =
        |rig: &mut Rig| rig.with(|c, _| c.entries.iter().map(|e| e.pill).collect::<Vec<f32>>());
    rig.now += 60;
    rig.host.tick(0.06, rig.now, None);
    let easing = pills(&mut rig)[0];
    assert!((1.0..19.0).contains(&easing), "{easing}");
    for _ in 0..10 {
        rig.tick();
    }
    assert_eq!(pills(&mut rig), [style::PILL_FRONT, style::PILL_OPEN, 0.0]);
    let _ = (a, b);
    rig.host.root.bring_to_front(frames[1]);
    for _ in 0..10 {
        rig.tick();
    }
    assert_eq!(pills(&mut rig), [style::PILL_OPEN, style::PILL_FRONT, 0.0]);
}

#[test]
fn a_horizontal_dock_shows_its_label_away_from_its_edge_and_scrolls_along_its_length() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.add_plugins(30);
    rig.sync_plain();
    rig.with(|c, ui| c.set_mode(ui, DockMode::Top));
    rig.tick();
    let l = rig.layout();
    assert_eq!(l.width, rig.size().0);
    assert!(l.width <= 800.0);
    assert!(l.max_first > 0 && l.forward.is_some());
    let slot = l.slots[0].unwrap();
    rig.hover(slot);
    let label = rig.label().unwrap().rect;
    assert_eq!(label.y, l.height + style::LABEL_GAP);
    rig.with(|c, ui| c.set_mode(ui, DockMode::Bottom));
    rig.tick();
    let l = rig.layout();
    let slot = l.slots[0].unwrap();
    rig.hover(slot);
    let label = rig.label().unwrap().rect;
    assert_eq!(label.y, -style::LABEL_GAP - label.h);

    let frame = rig.frame();
    rig.host.root.send_message(frame, ElementMessage::Wheel(-1));
    rig.host.pump();
    rig.tick();
    let scrolled = rig.layout();
    assert_eq!(scrolled.first, 1);
    assert_eq!(scrolled.slots[1].unwrap().x, scrolled.list_start);
}

#[test]
fn a_label_on_a_horizontal_dock_is_kept_on_the_screen_at_its_ends() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Classic);
    rig.add(
        "test",
        "A plugin with a long name",
        PanelDescriptor::new("a", "A window with a long title"),
    );
    rig.sync_plain();
    rig.with(|c, ui| c.set_mode(ui, DockMode::Top));
    rig.host.root.move_window(WINDOW, 0.0, 0.0);
    rig.tick();
    rig.with(|c, ui| {
        let r = c.label_rect(
            ui,
            DockSide::Top,
            Rect::new(0.0, 0.0, 10.0, 46.0),
            "A long title that runs well past the slot it names",
            None,
        );
        assert!(r.x >= 0.0);
    });
}

// ── Hover labels ────────────────────────────────────────────────────────

/// A window titled `title` of a plugin named `owner`, at the default place.
fn labelled(title: &str, owner: &str) -> Rig {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Classic);
    rig.add("test", owner, PanelDescriptor::new("main", title));
    rig.sync_plain();
    rig
}

#[test]
fn hovering_a_slot_shows_its_label_and_it_has_no_tooltip() {
    let mut rig = labelled("Loot Editor", "MossTank");
    assert!(rig.label().is_none());
    let slot = rig.layout().slots[0].unwrap();
    rig.hover(slot);
    let label = rig.label().unwrap();
    assert_eq!(label.title, "Loot Editor");
    assert_eq!(label.subtitle.as_deref(), Some("MossTank"));
    let panel = rig.ui.panels().next().unwrap().id;
    let id = rig.with(|c, _| c.slot(panel)).unwrap();
    assert!(
        rig.host
            .root
            .element(id)
            .unwrap()
            .runtime_tooltip_text
            .is_none()
    );
}

#[test]
fn a_label_and_its_look_stay_while_the_pointer_rests_past_the_tooltip_time() {
    let mut rig = labelled("Loot Editor", "MossTank");
    let slot = rig.layout().slots[0].unwrap();
    rig.hover(slot);
    let wait = rig.host.root.tooltip_delay_ms + rig.host.root.tooltip_duration_ms;
    // Two frames: one fires the tooltip time, the next would end it.
    rig.pass(wait as f64 / 1000.0);
    rig.pass(wait as f64 / 1000.0);
    assert_eq!(rig.label().unwrap().title, "Loot Editor");
    let panel = rig.ui.panels().next().unwrap().id;
    let id = rig.with(|c, _| c.slot(panel)).unwrap();
    let look = rig
        .host
        .root
        .widget::<DockSlot>(id)
        .unwrap()
        .pointer
        .look(true);
    assert_eq!(look, ButtonLook::Hovered);
    // The tile and the gear keep theirs too.
    for part in [rig.layout().tile, rig.layout().gear] {
        rig.hover(part);
        rig.pass(wait as f64 / 1000.0);
        rig.pass(wait as f64 / 1000.0);
        assert!(rig.label().is_some());
    }
}

#[test]
fn a_label_is_one_line_when_the_title_is_the_plugins_name() {
    let mut rig = labelled("GoArrow", "GoArrow");
    let slot = rig.layout().slots[0].unwrap();
    rig.hover(slot);
    assert_eq!(rig.label().unwrap().subtitle, None);
}

#[test]
fn the_label_sits_away_from_the_screen_edge() {
    let mut rig = labelled("Loot Editor", "MossTank");
    let slot = rig.layout().slots[0].unwrap();
    rig.hover(slot);
    let (w, _) = rig.size();
    let left = rig.label().unwrap().rect;
    assert_eq!(left.x, w + style::LABEL_GAP);
    assert!(((left.y + left.h / 2.0) - (slot.y + slot.h / 2.0)).abs() <= 1.0);

    // Floating in the right half of the screen, it opens to the left.
    rig.host.root.move_window(WINDOW, 700.0, 100.0);
    rig.hover(slot);
    let right = rig.label().unwrap().rect;
    assert_eq!(right.x, -style::LABEL_GAP - right.w);
}

#[test]
fn a_label_is_kept_on_the_screen() {
    let mut rig = labelled("Loot Editor", "MossTank");
    let (_, h) = rig.size();
    rig.host.root.move_window(WINDOW, 10.0, SCREEN.1 - h);
    let gear = rig.layout().gear;
    rig.tick();
    let (_, top) = rig.at();
    // Near the bottom, a tall label would run off without the clamp.
    rig.with(|c, ui| {
        let r = c.label_rect(
            ui,
            DockSide::Left,
            Rect::new(gear.x, h, gear.w, 40.0),
            "Title",
            Some("Sub"),
        );
        assert!(top + r.y + r.h <= SCREEN.1);
        assert!(top + r.y >= 0.0);
    });
}

#[test]
fn the_label_is_not_hit_and_is_drawn_in_the_overlay() {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Moss);
    rig.add(
        "test",
        "MossTank",
        PanelDescriptor::new("main", "Loot Editor"),
    );
    rig.sync_plain();
    let slot = rig.layout().slots[0].unwrap();
    rig.hover(slot);
    let label = rig.label().unwrap().rect;
    let (left, top) = rig.at();
    let (x, y) = (
        left + label.x + label.w / 2.0,
        top + label.y + label.h / 2.0,
    );
    let frame = rig.frame();
    let root = rig.host.root.root();
    let hit = rig.host.root.hit_test(root, x, y);
    assert!(hit.is_none_or(|h| h != frame));
    let list = rig.draw();
    let overlay = DrawList {
        main: list.overlay.clone(),
        ..DrawList::default()
    };
    let moss = Palette::MOSS;
    assert!(
        draw_corners(&overlay)
            .iter()
            .any(|(cx, _, c)| *cx > left + 48.0 && *c == moss.background)
    );
}

#[test]
fn hovering_the_gear_labels_it_plugin_appearance() {
    let mut rig = labelled("Loot Editor", "MossTank");
    let gear = rig.layout().gear;
    rig.hover(gear);
    let label = rig.label().unwrap();
    assert_eq!(label.title, APPEARANCE_LABEL);
    assert_eq!(label.subtitle, None);
}

// ── Shapes ──────────────────────────────────────────────────────────────

fn drawn(f: impl FnOnce(&mut RenderContext<'_>)) -> Vec<(f32, f32, Color)> {
    let mut list = DrawList::default();
    let mut rc = RenderContext::new(&mut list, (800.0, 600.0), &NoSprites);
    f(&mut rc);
    drop(rc);
    draw_corners(&list)
}

/// What a draw put down, without its transparent soft edge.
fn ink(corners: &[(f32, f32, Color)]) -> Vec<(f32, f32)> {
    corners
        .iter()
        .filter(|(_, _, c)| c.a > 0.5)
        .map(|(x, y, _)| (*x, *y))
        .collect()
}

#[test]
fn a_floating_body_fills_in_the_background_with_a_rim_and_a_shadow_below() {
    let moss = Palette::MOSS;
    let corners = drawn(|rc| {
        rc.push_transform(100.0, 100.0);
        style::body(
            rc,
            &moss,
            DockMode::Floating,
            &DockSize::Default.metrics(),
            48.0,
            200.0,
        );
    });
    assert!(has_color(&corners, moss.background));
    assert!(has_color(&corners, mix(moss.border, moss.text, 0.08)));
    assert!(corners.iter().any(|(_, y, c)| *y > 300.0 && c.a > 0.0));
}

#[test]
fn a_rail_is_square_on_its_edge_side() {
    let m = DockSize::Default.metrics();
    let r = m.rail_radius;
    assert_eq!(
        style::corners(DockMode::Left, &m),
        CornerRadii::new(0.0, r, r, 0.0)
    );
    assert_eq!(
        style::corners(DockMode::Right, &m),
        CornerRadii::new(r, 0.0, 0.0, r)
    );
    assert_eq!(
        style::corners(DockMode::Top, &m),
        CornerRadii::new(0.0, 0.0, r, r)
    );
    assert_eq!(
        style::corners(DockMode::Bottom, &m),
        CornerRadii::new(r, r, 0.0, 0.0)
    );
    assert_eq!(
        style::corners(DockMode::Floating, &m),
        CornerRadii::all(m.radius)
    );
}

#[test]
fn an_edge_pill_sits_on_the_screen_edge_centred_on_its_slot() {
    let moss = Palette::MOSS;
    let dock = (46.0, 200.0);
    let slot = Rect::new(0.0, 40.0, 46.0, 38.0);
    for (side, min_x, max_x) in [(DockSide::Left, 0.0, 3.0), (DockSide::Right, 43.0, 46.0)] {
        let corners =
            drawn(|rc| style::edge_pill(rc, moss.accent, side, dock, slot, style::PILL_OPEN));
        let points = ink(&corners);
        assert!(!points.is_empty());
        assert!(
            points
                .iter()
                .all(|(x, _)| *x >= min_x - 0.5 && *x <= max_x + 0.5)
        );
        // 8 long, centred on a slot from 40 to 78: 55 to 63.
        let (ys_min, ys_max) = points.iter().fold((f32::MAX, f32::MIN), |(a, b), (_, y)| {
            (a.min(*y), b.max(*y))
        });
        assert!((54.0..=56.0).contains(&ys_min), "{ys_min}");
        assert!((62.0..=64.0).contains(&ys_max), "{ys_max}");
    }
    // Along a top or bottom rail.
    let dock = (200.0, 46.0);
    let slot = Rect::new(40.0, 0.0, 38.0, 46.0);
    for (side, min_y, max_y) in [(DockSide::Top, 0.0, 3.0), (DockSide::Bottom, 43.0, 46.0)] {
        let corners =
            drawn(|rc| style::edge_pill(rc, moss.accent, side, dock, slot, style::PILL_OPEN));
        let points = ink(&corners);
        assert!(
            points
                .iter()
                .all(|(_, y)| *y >= min_y - 0.5 && *y <= max_y + 0.5)
        );
        assert!(points.iter().all(|(x, _)| (54.0..=64.0).contains(x)));
    }
}

#[test]
fn an_edge_pill_of_no_length_draws_nothing() {
    let moss = Palette::MOSS;
    let corners = drawn(|rc| {
        style::edge_pill(
            rc,
            moss.accent,
            DockSide::Left,
            (46.0, 200.0),
            Rect::new(0.0, 40.0, 46.0, 38.0),
            0.0,
        );
    });
    assert!(corners.is_empty());
}

#[test]
fn an_open_dot_sits_on_the_side_facing_the_screen_edge() {
    let moss = Palette::MOSS;
    let slot = Rect::new(6.0, 16.0, 36.0, 36.0);
    for (side, min_x, max_x) in [(DockSide::Left, 1.5, 4.5), (DockSide::Right, 43.5, 46.5)] {
        let corners = drawn(|rc| style::open_dot(rc, &moss, side, (48.0, 200.0), slot));
        assert!(has_color(&corners, moss.accent));
        assert!(
            ink(&corners)
                .iter()
                .all(|(x, _)| *x >= min_x - 0.5 && *x <= max_x + 0.5)
        );
    }
    let slot = Rect::new(16.0, 6.0, 36.0, 36.0);
    for (side, min_y, max_y) in [(DockSide::Top, 1.5, 4.5), (DockSide::Bottom, 43.5, 46.5)] {
        let corners = drawn(|rc| style::open_dot(rc, &moss, side, (200.0, 48.0), slot));
        assert!(
            ink(&corners)
                .iter()
                .all(|(_, y)| *y >= min_y - 0.5 && *y <= max_y + 0.5)
        );
    }
}

#[test]
fn the_scroll_bar_is_on_the_side_away_from_the_screen_edge_clear_of_the_wells() {
    let moss = Palette::MOSS;
    let bar = layout::ScrollBar {
        start: 60.0,
        end: 160.0,
        thumb_start: 80.0,
        thumb_end: 105.0,
    };
    let thick = style::SCROLL_BAR_THICKNESS;
    let inset = style::SCROLL_BAR_INSET;
    let dock = (46.0, 200.0);
    for (side, from) in [
        (DockSide::Left, 46.0 - inset - thick),
        (DockSide::Right, inset),
    ] {
        let corners = drawn(|rc| style::scroll_bar(rc, &moss, side, dock, &bar));
        assert!(has_color(&corners, moss.muted));
        let ink = ink(&corners);
        assert!(!ink.is_empty());
        assert!(ink.iter().all(|(x, y)| {
            *x >= from - 0.5 && *x <= from + thick + 0.5 && *y >= 59.5 && *y <= 160.5
        }));
        assert!(from + thick <= style::RAIL_WELL_ACROSS || from >= 46.0 - style::RAIL_WELL_ACROSS);
    }
    let dock = (200.0, 46.0);
    for (side, from) in [
        (DockSide::Top, 46.0 - inset - thick),
        (DockSide::Bottom, inset),
    ] {
        let corners = drawn(|rc| style::scroll_bar(rc, &moss, side, dock, &bar));
        assert!(ink(&corners).iter().all(|(x, y)| {
            *y >= from - 0.5 && *y <= from + thick + 0.5 && *x >= 59.5 && *x <= 160.5
        }));
    }
}

#[test]
fn an_arrows_chevron_is_centred_whichever_way_it_points() {
    let moss = Palette::MOSS;
    let spread = |horizontal, forward| {
        let corners = drawn(|rc| {
            style::arrow(rc, moss.text, horizontal, forward, (36.0, 16.0));
        });
        ink(&corners)
    };
    for (horizontal, forward) in [(false, false), (false, true), (true, false), (true, true)] {
        let ink = spread(horizontal, forward);
        assert!(!ink.is_empty());
        let (mut lo_x, mut hi_x, mut lo_y, mut hi_y) = (f32::MAX, f32::MIN, f32::MAX, f32::MIN);
        for (x, y) in &ink {
            lo_x = lo_x.min(*x);
            hi_x = hi_x.max(*x);
            lo_y = lo_y.min(*y);
            hi_y = hi_y.max(*y);
        }
        assert!(
            ((lo_x + hi_x) / 2.0 - 18.0).abs() < 1.0,
            "{horizontal} {forward}"
        );
        assert!(
            ((lo_y + hi_y) / 2.0 - 8.0).abs() < 1.0,
            "{horizontal} {forward}"
        );
    }
}

#[test]
fn a_slot_well_is_empty_at_rest_and_filled_under_the_pointer() {
    let moss = Palette::MOSS;
    let r = Rect::new(0.0, 0.0, 36.0, 36.0);
    assert!(drawn(|rc| style::slot_well(rc, &moss, r, 9.0, ButtonLook::Normal)).is_empty());
    let hovered = drawn(|rc| style::slot_well(rc, &moss, r, 9.0, ButtonLook::Hovered));
    assert!(has_color(&hovered, hover(&moss, moss.field)));
}

#[test]
fn a_rail_slots_well_leaves_the_edge_pill_uncovered_and_a_floating_one_spans_the_slot() {
    let vertical = style::slot_well_rect(DockMode::Left, 46.0, 38.0);
    assert_eq!(vertical, Rect::new(5.0, 1.0, 36.0, 36.0));
    assert!(vertical.x >= style::PILL_HOVER);
    let horizontal = style::slot_well_rect(DockMode::Top, 38.0, 46.0);
    assert_eq!(horizontal, Rect::new(1.0, 5.0, 36.0, 36.0));
    assert_eq!(
        style::slot_well_rect(DockMode::Floating, 40.0, 30.0),
        Rect::new(0.0, 0.0, 40.0, 30.0)
    );
}

#[test]
fn a_monogram_hue_is_stable_for_an_id_and_one_of_eight() {
    assert_eq!(
        style::monogram_hue("acdream.mosstank"),
        style::monogram_hue("acdream.mosstank")
    );
    let mut seen: Vec<(u32, u32, u32)> = Vec::new();
    for id in ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l"] {
        let c = style::monogram_hue(id);
        let key = (
            (c.r * 255.0) as u32,
            (c.g * 255.0) as u32,
            (c.b * 255.0) as u32,
        );
        if !seen.contains(&key) {
            seen.push(key);
        }
    }
    assert!(seen.len() > 1 && seen.len() <= 8);
    // FNV-1a of "a" is 0xE40C292C, and that % 8 is 4: the fifth hue, #8B8259.
    assert_eq!(
        style::monogram_hue("a"),
        Color::rgba(
            0x8B as f32 / 255.0,
            0x82 as f32 / 255.0,
            0x59 as f32 / 255.0,
            1.0
        )
    );
}

#[test]
fn a_hover_label_draws_its_box_and_a_shadow() {
    let moss = Palette::MOSS;
    let corners = drawn(|rc| style::hover_label(rc, &moss, Rect::new(60.0, 20.0, 90.0, 36.0)));
    assert!(has_color(&corners, moss.background));
    assert!(has_color(&corners, moss.border));
    assert!(corners.iter().any(|(_, y, c)| *y > 56.0 && c.a > 0.0));
}

#[test]
fn a_divider_is_a_one_point_hairline_faded_from_the_border_toward_the_fill() {
    for p in [Palette::MOSS, Palette::CLASSIC_DOCK] {
        let expected = mix(p.border, p.background, style::DIVIDER_FADE);
        assert_ne!(expected, p.border);
        let corners = drawn(|rc| style::divider(rc, &p, DockMode::Floating, (48.0, 200.0), 50.0));
        assert!(corners.iter().all(|(_, _, c)| *c == expected));
        let ys: Vec<f32> = corners.iter().map(|(_, y, _)| *y).collect();
        let span = ys.iter().cloned().fold(f32::MIN, f32::max)
            - ys.iter().cloned().fold(f32::MAX, f32::min);
        assert_eq!(span, 1.0);
        let turned = drawn(|rc| style::divider(rc, &p, DockMode::Top, (200.0, 46.0), 50.0));
        let xs: Vec<f32> = turned.iter().map(|(x, _, _)| *x).collect();
        let span = xs.iter().cloned().fold(f32::MIN, f32::max)
            - xs.iter().cloned().fold(f32::MAX, f32::min);
        assert_eq!(span, 1.0);
    }
}

#[test]
fn the_classic_dock_uses_its_own_colours_and_is_still_the_black_and_gold() {
    let p = Palette::CLASSIC_DOCK;
    assert_eq!(p.background.a, 0.88);
    assert_eq!(p.background.r, 0.0);
    assert!((p.border.r - 0x9E as f32 / 255.0).abs() < 0.001);
    assert!((p.accent.r - 0xDB as f32 / 255.0).abs() < 0.001);
    assert!((p.selected.g - 0x30 as f32 / 255.0).abs() < 0.001);
    assert_eq!(PluginTheme::Classic.palette(), None);
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Classic);
    rig.add_plugins(1);
    rig.sync_plain();
    let list = rig.draw();
    assert!(
        has_color(&draw_corners(&list), p.border)
            || has_color(&draw_corners(&list), mix(p.border, p.text, 0.08))
    );
}

// ── A window's own PNG ──────────────────────────────────────────────────

/// A file icon uploads as a texture named after its first byte, so a test can
/// tell which picture a slot draws; the surface resolver answers `SURFACE`.
#[derive(Default)]
struct FillIcons {
    surface_calls: usize,
}

const SURFACE: TextureRef = TextureRef::Surface(0x0600_0165);

impl DockIcons for FillIcons {
    fn file(&mut self, icon: &PluginIcon) -> Option<Icon> {
        let texture = TextureRef::Runtime(u32::from(icon.rgba[0]));
        Some((texture, icon.width as f32, icon.height as f32))
    }

    fn surface(&mut self, _id: u32) -> Option<Icon> {
        self.surface_calls += 1;
        Some((SURFACE, 32.0, 32.0))
    }
}

fn filled(fill: u8) -> PluginIcon {
    PluginIcon {
        width: 64,
        height: 64,
        rgba: Arc::new(vec![fill; 64 * 64 * 4]),
    }
}

/// A rig with one window, its plugin's `icon.png` filled with 1, and its own
/// file as `window_icon`.
fn panels_with(window_icon: Option<ac_plugin_ui::WindowIcon>) -> (Vec<PanelView>, Rig) {
    let mut rig = Rig::new(SCREEN.1, PluginTheme::Classic);
    rig.ui.set_file_icon("acdream.test", Some(filled(1)));
    let descriptor = PanelDescriptor {
        icon_surface_id: 0x165,
        ..PanelDescriptor::new("main", "Test Plugin")
    };
    rig.add("acdream.test", "Test Plugin", descriptor);
    let mut panels: Vec<PanelView> = rig.ui.panels().cloned().collect();
    panels[0].window_icon = window_icon;
    (panels, rig)
}

/// Syncs the dock with `panels` and the sprites that draws from the files.
fn drawn_fills(rig: &mut Rig, panels: &[PanelView], icons: &mut FillIcons) -> Vec<u32> {
    rig.host.with_controller::<PluginDock, _>(WINDOW, |c, ui| {
        c.sync(ui, panels, icons);
    });
    rig.tick();
    let list = rig.draw();
    list.main
        .iter()
        .chain(list.overlay.iter())
        .filter_map(|cmd| match cmd {
            DrawCmd::Sprite {
                texture: TextureRef::Runtime(fill),
                ..
            } if *fill < 100 => Some(*fill),
            _ => None,
        })
        .collect()
}

#[test]
fn a_windows_own_png_comes_ahead_of_the_plugins_and_the_surface() {
    let icon = ac_plugin_ui::WindowIcon {
        named: Some(filled(2)),
        ..Default::default()
    };
    let (panels, mut rig) = panels_with(Some(icon));
    let mut icons = FillIcons::default();
    assert_eq!(drawn_fills(&mut rig, &panels, &mut icons), [2]);
    assert_eq!(icons.surface_calls, 0);
}

#[test]
fn with_no_own_png_the_order_goes_on_to_the_plugins_then_the_surface() {
    let (panels, mut rig) = panels_with(None);
    let mut icons = FillIcons::default();
    assert_eq!(drawn_fills(&mut rig, &panels, &mut icons), [1]);
    assert_eq!(icons.surface_calls, 0);

    let (mut panels, mut rig) = panels_with(None);
    panels[0].file_icon = None;
    let mut icons = FillIcons::default();
    assert!(drawn_fills(&mut rig, &panels, &mut icons).is_empty());
    assert_eq!(icons.surface_calls, 1);
    let list = rig.draw();
    assert_eq!(sprites_of(&list, SURFACE).len(), 1);
}

#[test]
fn a_version_for_one_theme_alone_leaves_the_other_to_the_next_icon() {
    let icon = ac_plugin_ui::WindowIcon {
        light: Some(filled(3)),
        ..Default::default()
    };
    let (panels, mut rig) = panels_with(Some(icon));
    let mut icons = FillIcons::default();
    assert_eq!(drawn_fills(&mut rig, &panels, &mut icons), [1]);
    rig.with(|c, ui| c.set_theme(ui, PluginTheme::Parchment));
    assert_eq!(drawn_fills(&mut rig, &panels, &mut icons), [3]);
}

#[test]
fn a_change_of_theme_swaps_the_version_only_between_light_and_dark() {
    let icon = ac_plugin_ui::WindowIcon {
        named: Some(filled(2)),
        light: Some(filled(3)),
        dark: Some(filled(4)),
    };
    let (panels, mut rig) = panels_with(Some(icon));
    let mut icons = FillIcons::default();
    // Classic's dock is dark, and so is Moss.
    assert_eq!(drawn_fills(&mut rig, &panels, &mut icons), [4]);
    rig.with(|c, ui| c.set_theme(ui, PluginTheme::Moss));
    assert_eq!(drawn_fills(&mut rig, &panels, &mut icons), [4]);
    rig.with(|c, ui| c.set_theme(ui, PluginTheme::Parchment));
    assert_eq!(drawn_fills(&mut rig, &panels, &mut icons), [3]);
    rig.with(|c, ui| c.set_theme(ui, PluginTheme::Classic));
    assert_eq!(drawn_fills(&mut rig, &panels, &mut icons), [4]);
}

#[test]
fn a_windows_own_png_dims_while_its_window_is_closed() {
    let icon = ac_plugin_ui::WindowIcon {
        named: Some(filled(2)),
        ..Default::default()
    };
    let (mut panels, mut rig) = panels_with(Some(icon));
    let alpha_of = |rig: &mut Rig, panels: &[PanelView]| {
        let mut icons = FillIcons::default();
        rig.host.with_controller::<PluginDock, _>(WINDOW, |c, ui| {
            c.sync(ui, panels, &mut icons);
        });
        rig.tick();
        rig.draw()
            .main
            .iter()
            .find_map(|cmd| match cmd {
                DrawCmd::Sprite {
                    texture: TextureRef::Runtime(2),
                    tint,
                    ..
                } => Some(tint.a),
                _ => None,
            })
            .unwrap()
    };
    assert_eq!(alpha_of(&mut rig, &panels), 1.0);
    panels[0].requested_visible = false;
    assert_eq!(alpha_of(&mut rig, &panels), style::CLOSED_ART_ALPHA);
}

// ── Saved layout ────────────────────────────────────────────────────────

/// Layouts kept in memory for one character, counting the saves.
#[derive(Clone, Default)]
struct MemoryLayouts {
    placements: Rc<RefCell<std::collections::HashMap<String, UiWindowPlacement>>>,
    saves: Rc<std::cell::Cell<usize>>,
}

impl LayoutStore for MemoryLayouts {
    fn load_window_placement(
        &self,
        _character: &str,
        _resolution: &str,
        window: &str,
        _fallback: &UiWindowLayout,
    ) -> Option<UiWindowPlacement> {
        self.placements.borrow().get(window).copied()
    }

    fn save_window_placement(
        &self,
        _character: &str,
        window: &str,
        placement: &UiWindowPlacement,
    ) -> std::io::Result<()> {
        self.saves.set(self.saves.get() + 1);
        self.placements
            .borrow_mut()
            .insert(window.to_owned(), *placement);
        Ok(())
    }

    fn load_named_window_layout(
        &self,
        _profile: &str,
        _window: &str,
        _fallback: &UiWindowLayout,
    ) -> Option<UiWindowLayout> {
        None
    }

    fn save_named_window_layout(
        &self,
        _profile: &str,
        _window: &str,
        _layout: &UiWindowLayout,
    ) -> std::io::Result<()> {
        Ok(())
    }
}

impl Rig {
    /// The layout persistence over this dock, restored from `store` as on
    /// login.
    fn persist(&mut self, store: &MemoryLayouts) -> LayoutPersistence {
        let screen = (SCREEN.0 as i32, SCREEN.1 as i32);
        let mut p = LayoutPersistence::new(
            &mut self.host,
            Some(Box::new(store.clone())),
            "Tester",
            screen,
            &[],
        );
        p.set_gameplay_active(&mut self.host, true);
        p.restore_all(&mut self.host);
        self.tick();
        p.follow(&mut self.host);
        p
    }
}

#[test]
fn a_closed_dock_in_a_corner_comes_back_to_that_corner() {
    for mode in [
        DockMode::Floating,
        DockMode::FloatingHorizontal,
        DockMode::Right,
        DockMode::Bottom,
    ] {
        let store = MemoryLayouts::default();
        let mut rig = Rig::in_mode(mode);
        let mut p = rig.persist(&store);
        rig.press_tile();
        rig.host.root.move_window(WINDOW, SCREEN.0, SCREEN.1);
        rig.tick();
        p.follow(&mut rig.host);
        let (w, h) = rig.size();
        let corner = rig.at();
        assert_eq!(corner, (SCREEN.0 - w, SCREEN.1 - h), "{mode:?}");
        // The next run, with the mode the settings keep.
        let mut rig = Rig::in_mode(mode);
        rig.persist(&store);
        assert!(rig.closed(), "{mode:?}");
        assert_eq!(rig.at(), corner, "{mode:?}");
    }
}

#[test]
fn a_peek_open_and_closed_saves_nothing() {
    let store = MemoryLayouts::default();
    let mut rig = Rig::in_mode(DockMode::Floating);
    let mut p = rig.persist(&store);
    rig.press_tile();
    rig.with(|c, ui| c.set_opens(ui, DockOpens::Hover));
    // Near the far end, so that the peek also slides to fit.
    rig.host.root.move_window(WINDOW, 300.0, 600.0);
    rig.host.mouse_move(100, 100);
    rig.pass(1.0);
    p.follow(&mut rig.host);
    let saves = store.saves.get();
    let closed_at = rig.at();
    rig.pointer_on_dock();
    rig.pass(0.2);
    assert!(!rig.closed_view());
    assert_ne!(rig.at(), closed_at);
    p.follow(&mut rig.host);
    rig.host.mouse_move(100, 100);
    rig.pass(1.0);
    assert!(rig.closed_view());
    p.follow(&mut rig.host);
    assert_eq!(store.saves.get(), saves);
    // Opening it for good is saved.
    rig.press_tile();
    p.follow(&mut rig.host);
    assert_eq!(store.saves.get(), saves + 1);
}

#[test]
fn a_closed_tile_in_a_corner_keeps_it_when_the_grip_comes_and_goes() {
    for mode in [
        DockMode::Floating,
        DockMode::FloatingHorizontal,
        DockMode::Right,
        DockMode::Bottom,
    ] {
        let mut rig = Rig::in_mode(mode);
        rig.press_tile();
        rig.host.root.move_window(WINDOW, SCREEN.0, SCREEN.1);
        rig.tick();
        for locked in [true, false] {
            rig.host.root.set_ui_locked(locked);
            rig.tick();
            let (w, h) = rig.size();
            let corner = (SCREEN.0 - w, SCREEN.1 - h);
            assert_eq!(rig.at(), corner, "{mode:?} locked {locked}");
        }
    }
}

impl Rig {
    /// An open dock of two windows that takes its time opening and closing.
    fn revealing() -> Self {
        let mut rig = Self::in_mode(DockMode::Floating);
        rig.with(|c, _| c.reveal_seconds = REVEAL_SECONDS);
        rig
    }

    fn placement(&mut self) -> Rect {
        self.with(|c, ui| c.placement(ui)).unwrap()
    }

    fn revealing_now(&mut self) -> bool {
        self.with(|c, _| c.reveal.is_some())
    }

    /// Long enough for any opening or closing to finish.
    fn settle(&mut self) {
        self.pass(f64::from(REVEAL_SECONDS) * 2.0);
    }
}

#[test]
fn closing_shrinks_the_dock_with_its_slots_still_there_then_lays_it_out_closed() {
    let mut rig = Rig::revealing();
    let open = rig.placement();
    rig.press_tile();
    assert!(rig.closed_view(), "closed at once, as far as anyone asks");
    let during = rig.placement();
    assert!(during.h < open.h, "{} of {}", during.h, open.h);
    assert!(during.h > layout::FLOATING_THICKNESS, "{}", during.h);
    assert!(rig.layout().slots[0].is_some(), "the slots go with it");
    assert!(rig.layout().gear.h > 0.0);

    rig.settle();
    assert!(!rig.revealing_now());
    assert!(rig.layout().slots[0].is_none());
    let closed = rig.placement();
    assert_eq!(
        (closed.w, closed.h),
        (rig.layout().width, rig.layout().height)
    );
}

#[test]
fn opening_grows_the_dock_from_its_tile_to_its_whole_length() {
    let mut rig = Rig::revealing();
    rig.press_tile();
    rig.settle();
    let closed = rig.placement();
    rig.press_tile();
    assert!(!rig.closed_view());
    assert!(rig.layout().slots[0].is_some(), "laid out open at once");
    let during = rig.placement();
    let whole = rig.layout().height;
    assert!(during.h > closed.h && during.h < whole, "{}", during.h);
    assert_eq!((during.x, during.y), (closed.x, closed.y));

    rig.settle();
    assert_eq!(rig.placement().h, whole);
    assert!(!rig.revealing_now());
}

#[test]
fn it_is_quick_at_first_and_settles_at_the_end() {
    let mut rig = Rig::revealing();
    let open = rig.placement().h;
    rig.press_tile();
    let closed = rig.layout_closed_height();
    let mut last = rig.placement().h;
    let mut steps = Vec::new();
    while rig.revealing_now() {
        rig.tick();
        let h = rig.placement().h;
        assert!(h <= last, "only ever shrinks");
        steps.push(last - h);
        last = h;
    }
    assert_eq!(last, closed);
    assert!(open > closed);
    assert!(steps.len() >= 5, "{} frames", steps.len());
    assert!(steps[0] > steps[steps.len() - 2]);
}

impl Rig {
    fn layout_closed_height(&mut self) -> f32 {
        self.with(|c, _| c.reveal.map(|r| r.to.h)).unwrap()
    }
}

#[test]
fn a_press_on_the_way_turns_it_round_from_where_it_is() {
    let mut rig = Rig::revealing();
    let open = rig.placement().h;
    rig.press_tile();
    rig.tick();
    let on_the_way = rig.placement().h;
    assert!(on_the_way < open);
    rig.press_tile();
    assert!(!rig.closed_view());
    let turned = rig.placement().h;
    assert!(
        turned >= on_the_way && turned < open,
        "{on_the_way} then {turned}, whole {open}"
    );
    rig.settle();
    assert_eq!(rig.placement().h, open);
}

#[test]
fn a_dock_that_slid_to_fit_eases_there_and_back() {
    let mut rig = Rig::revealing();
    rig.press_tile();
    rig.settle();
    let low = rig.host.root.size().1 - rig.layout().height - 2.0;
    rig.with(|c, ui| ui.element_mut(c.root).unwrap().top = low);
    rig.tick();
    let closed = rig.placement();
    rig.press_tile();
    let during = rig.placement();
    assert!(during.y < closed.y, "on its way up");
    rig.settle();
    let open = rig.placement();
    assert!(open.y < during.y, "{} then {}", during.y, open.y);
    assert!(open.y + open.h <= rig.host.root.size().1);

    rig.press_tile();
    rig.settle();
    assert_eq!(rig.placement().y, closed.y, "back where it was");
}

#[test]
fn what_is_saved_on_the_way_is_where_it_is_going() {
    let mut rig = Rig::revealing();
    rig.press_tile();
    assert!(rig.revealing_now());
    let saved = rig.host.capture_window_state(WINDOW);
    let going = rig.with(|c, _| c.reveal.unwrap().to);
    assert!(saved.collapsed);
    assert_eq!(saved.persisted_width, Some(going.w));
    assert_eq!(saved.persisted_height, Some(going.h));
    assert_eq!(saved.persisted_top, Some(going.y));
}

#[test]
fn a_new_screen_size_or_mode_on_the_way_ends_it_where_the_layout_says() {
    let mut rig = Rig::revealing();
    rig.press_tile();
    assert!(rig.revealing_now());
    rig.host.root.set_size(SCREEN.0, SCREEN.1 - 40.0);
    rig.tick();
    assert!(!rig.revealing_now());
    let l = rig.layout();
    let at = rig.placement();
    assert_eq!((at.w, at.h), (l.width, l.height));

    rig.press_tile();
    assert!(rig.revealing_now());
    rig.with(|c, ui| c.set_mode(ui, DockMode::Left));
    rig.tick();
    assert!(!rig.revealing_now());
    let l = rig.layout();
    let at = rig.placement();
    assert_eq!((at.w, at.h), (l.width, l.height));
    assert_eq!(at.x, 0.0);

    // A tile near the bottom slides up to fit as it opens and back down as
    // it closes; a new screen size halfway down still puts it back on its
    // spot, not where the slide had got to.
    let mut rig = Rig::revealing();
    rig.press_tile();
    rig.settle();
    let low = rig.host.root.size().1 - rig.layout().height - 2.0;
    rig.with(|c, ui| ui.element_mut(c.root).unwrap().top = low);
    rig.tick();
    let tile = rig.placement();
    rig.press_tile();
    rig.settle();
    let open = rig.placement();
    assert!(open.y < tile.y, "it slid up to fit");
    rig.press_tile();
    rig.tick();
    let during = rig.placement();
    assert!(
        during.y > open.y && during.y < tile.y,
        "{} on the way from {} to {}",
        during.y,
        open.y,
        tile.y
    );
    rig.host.root.set_size(SCREEN.0 - 40.0, SCREEN.1);
    rig.tick();
    assert!(!rig.revealing_now());
    let at = rig.placement();
    assert_eq!((at.x, at.y, at.w, at.h), (tile.x, tile.y, tile.w, tile.h));
}

#[test]
fn putting_the_dock_back_from_saved_state_does_not_animate() {
    let mut rig = Rig::revealing();
    let mut state = rig.host.capture_window_state(WINDOW);
    state.collapsed = true;
    rig.host.restore_window_state(WINDOW, state);
    assert!(!rig.revealing_now());
    assert_eq!(rig.placement().h, rig.layout().height);
}
