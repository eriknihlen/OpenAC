//! The plugins' UI in the game window on the retail dats: a window
//! built from the session's snapshot mounts the plugin's window and its
//! dock slot, a press reaches the session as a command, and a plugin's
//! request to toggle a client window is carried out and reported back.
//! Ignored by default; `scripts/check.*` runs them when the dats are
//! present.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Instant;

use ac_dat::DatCollection;
use ac_plugin_ui::{
    ActionValue, BindingKind as K, BindingSchema, ClientWindow, ClientWindowAction,
    PanelDescriptor, PluginUi, UiEvent, UiInput, UiOwner, UiValue,
};
use ac_render::scene::Scene;
use ac_render::ui::WindowUi;
use ac_render::ui::window::{INVENTORY_WINDOW, UiScript};
use ac_runtime::session::interface::InterfaceEvent;
use ac_runtime::session::{PresentationEvent, SessionCommand};

fn dats() -> Arc<DatCollection> {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Arc::new(DatCollection::open(dir).expect("open the retail dats"))
}

const MARKUP: &str = r#"<panel x="300" y="200" w="240" h="120" title="Demo">
  <label name="Status" x="8" y="24" w="200" h="16" text="{Status}"/>
  <button name="Go" x="8" y="50" w="80" h="20" text="Go" onclick="{Go}"/>
</panel>"#;

/// The session's side: one plugin window, shown.
fn session() -> (PluginUi, u32) {
    let mut ui = PluginUi::new();
    ui.set_window_attached(true);
    let schema = BindingSchema::new("Demo")
        .with("Status", K::Text)
        .with("Go", K::Action);
    let id = ui
        .register_panel(
            &UiOwner::new("test.demo", "Demo"),
            PanelDescriptor::new("main", "Demo"),
            MARKUP,
            schema,
        )
        .unwrap();
    ui.set_values(
        "test.demo",
        id,
        vec![("Status".into(), UiValue::Text("idle".into()))],
    )
    .unwrap();
    ui.show_panel("test.demo", "main", true);
    (ui, id)
}

/// The window's commands handed to the session.
fn deliver(state: &mut PluginUi, commands: Vec<SessionCommand>) -> usize {
    let mut n = 0;
    for command in commands {
        if let SessionCommand::PluginUi(input) = command {
            state.input(input);
            n += 1;
        }
    }
    n
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_plugin_window_mounts_from_the_snapshot_and_reports_presses() {
    let size = (1280, 720);
    let (mut state, panel) = session();
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.snapshot(),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let name = "plugin:test.demo:main";
    assert_eq!(ui.plugin_windows(), [name]);
    assert!(ui.host().root.is_window_visible(name));
    assert!(
        ui.host()
            .root
            .is_window_visible(ac_ui::plugin::dock::WINDOW)
    );
    // The window tells the session which client windows it has.
    let commands = ui.drain();
    assert!(commands.iter().any(|c| matches!(
        c,
        SessionCommand::PluginUi(UiInput::ClientWindows { mounted, .. })
            if mounted & ClientWindow::Inventory.bit() != 0
    )));
    deliver(&mut state, commands);

    // The button at (8, 50) in a window at (300, 200).
    let out = ui.script(&UiScript::Click {
        x: 300 + 20,
        y: 200 + 58,
        button: 0,
        double: false,
    });
    let commands = ui.handle(out);
    assert!(commands.iter().any(|c| matches!(
        c,
        SessionCommand::PluginUi(UiInput::Action { panel: p, handler, value: ActionValue::None })
            if *p == panel && handler == "Go"
    )));
    deliver(&mut state, commands);
    let events = state.take_events();
    assert!(
        events.iter().any(|(owner, e)| owner == "test.demo"
            && matches!(e, UiEvent::Action { handler, .. } if handler == "Go")),
        "{events:?}"
    );

    // The plugin opens the inventory; the window does and says so.
    assert!(!ui.host().root.is_window_visible(INVENTORY_WINDOW));
    assert!(state.client_window(ClientWindow::Inventory, ClientWindowAction::Toggle));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    assert!(ui.host().root.is_window_visible(INVENTORY_WINDOW));
    deliver(&mut state, ui.drain());
    assert!(state.is_client_window_visible(ClientWindow::Inventory));

    // The plugin goes: its window and its dock slot go with it.
    state.release("test.demo");
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    assert!(ui.plugin_windows().is_empty());
    assert!(ui.host().root.window(name).is_none());
    assert!(
        !ui.host()
            .root
            .is_window_visible(ac_ui::plugin::dock::WINDOW)
    );
}

/// Plugin hotkeys in the window: the window names its key map's
/// chords, the session binds around them and sends the bound table back,
/// and a press of a bound chord reaches the plugin (a client chord never
/// binds). Toggle Plugin Manager (Shift+Ctrl+F1) hides and shows the
/// dock (OpenAC `RetailUiRuntime.TogglePluginManager`).
#[test]
#[ignore = "needs the retail dat files"]
fn plugin_hotkeys_fire_from_the_window_and_the_dock_toggles() {
    use ac_plugin_ui::hotkeys::Chord;
    use ac_ui::event::{Key, Modifiers};
    use ac_ui::input::{InputAction, KeyBindings};
    const H: u32 = 8;
    const ESCAPE: u32 = 51;
    // A key the retail key map leaves free.
    const PAUSE: u32 = 92;
    let size = (1280, 720);
    let (mut state, _) = session();
    let heal = Chord::key(H).with_ctrl();
    state
        .register_hotkey("test.demo", "heal", "Heal", heal)
        .unwrap();
    state
        .register_hotkey("test.demo", "escape", "Escape", Chord::key(ESCAPE))
        .unwrap();
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.snapshot(),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    ui.report_client_chords(&KeyBindings::retail_defaults());
    deliver(&mut state, ui.take_plugin_commands());
    assert!(state.hotkeys().state("test.demo", "heal").unwrap().is_bound);
    assert!(
        !state
            .hotkeys()
            .state("test.demo", "escape")
            .unwrap()
            .is_bound
    );
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    ui.plugin_hotkeys(Key::H, Modifiers::CTRL, false);
    ui.plugin_hotkeys(Key::H, Modifiers::NONE, false);
    ui.plugin_hotkeys(Key::Escape, Modifiers::NONE, false);
    ui.plugin_hotkeys(Key::H, Modifiers::CTRL, true);
    deliver(&mut state, ui.take_plugin_commands());
    let events = state.take_events();
    assert_eq!(
        events,
        [("test.demo".to_owned(), UiEvent::Hotkey("heal".into()))]
    );

    // OpenAC `HotkeyUiFocusTests`: the
    // interface's own focus and modal decide. While any element holds
    // keyboard focus only a chord with Ctrl or Alt fires; an open modal
    // lets nothing fire; with both gone, plain chords fire again.
    let plain = Chord::key(PAUSE);
    state
        .register_hotkey("test.demo", "plain", "Plain", plain)
        .unwrap();
    ui.report_client_chords(&KeyBindings::retail_defaults());
    deliver(&mut state, ui.take_plugin_commands());
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    let element = ui.host().root.root();
    ui.host_mut().root.set_keyboard_focus(Some(element));
    ui.plugin_hotkeys(Key::Pause, Modifiers::NONE, false);
    ui.plugin_hotkeys(Key::H, Modifiers::CTRL, false);
    ui.host_mut().root.set_keyboard_focus(None);
    ui.host_mut().root.modal = Some(element);
    ui.plugin_hotkeys(Key::H, Modifiers::CTRL, false);
    ui.plugin_hotkeys(Key::Pause, Modifiers::NONE, false);
    ui.host_mut().root.modal = None;
    ui.plugin_hotkeys(Key::Pause, Modifiers::NONE, false);
    deliver(&mut state, ui.take_plugin_commands());
    assert_eq!(
        state.take_events(),
        [
            ("test.demo".to_owned(), UiEvent::Hotkey("heal".into())),
            ("test.demo".to_owned(), UiEvent::Hotkey("plain".into())),
        ]
    );

    let dock = ac_ui::plugin::dock::WINDOW;
    assert!(ui.host().root.is_window_visible(dock));
    assert!(ui.handle_input_action(InputAction::TogglePluginManager));
    ui.frame(Instant::now(), size);
    assert!(!ui.host().root.is_window_visible(dock));
    assert!(ui.handle_input_action(InputAction::TogglePluginManager));
    ui.frame(Instant::now(), size);
    assert!(ui.host().root.is_window_visible(dock));
}

/// The dock's gear opens the Appearance window on the first press and closes it
/// on the next, and a right click does the same.
#[test]
#[ignore = "needs the retail dat files"]
fn the_docks_gear_opens_and_closes_the_appearance_window() {
    use ac_ui::plugin::dock::{PluginDock, WINDOW};
    const APPEARANCE: &str = "plugin:openac:appearance";
    let size = (1280, 720);
    let (state, _) = session();
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.snapshot(),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let gear = ui
        .host_mut()
        .with_controller::<PluginDock, _>(WINDOW, |c, _| c.layout().gear)
        .unwrap();
    let frame = ui.host().root.window(WINDOW).unwrap().frame;
    let e = ui.host().root.element(frame).unwrap();
    let (x, y) = (
        (e.left + gear.x + 4.0) as i32,
        (e.top + gear.y + 4.0) as i32,
    );
    let mut press = |ui: &mut WindowUi, button| {
        ui.script(&UiScript::Click {
            x,
            y,
            button,
            double: false,
        });
        ui.feed(&mut scene);
        ui.frame(Instant::now(), size);
        ui.feed(&mut scene);
        ui.frame(Instant::now(), size);
        ui.host().root.is_window_visible(APPEARANCE)
    };
    assert!(press(&mut ui, 0), "the first press opens it");
    assert!(!press(&mut ui, 0), "the second closes it");
    assert!(press(&mut ui, 1), "a right click opens it");
    assert!(!press(&mut ui, 1), "and closes it");
}

/// The dock's place: a saved one applies, a drag is saved only as it ends, and
/// the Appearance window offers all six.
#[test]
#[ignore = "needs the retail dat files"]
fn the_dock_takes_its_saved_place_and_a_drag_is_saved_when_it_ends() {
    use ac_host::settings::PlayerSettings;
    use ac_ui::event::MouseButton;
    use ac_ui::plugin::dock::{DockMode, PluginDock, WINDOW};
    let size = (1280, 720);
    let (state, _) = session();
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.snapshot(),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let mode = |ui: &mut WindowUi| {
        ui.host_mut()
            .with_controller::<PluginDock, _>(WINDOW, |c, _| c.mode())
            .unwrap()
    };
    assert_eq!(mode(&mut ui), DockMode::Floating);

    let mut saved = PlayerSettings::default();
    saved.plugin_ui.dock = "top".to_owned();
    saved.plugin_ui.dock_size = "bigger".to_owned();
    saved.plugin_ui.dock_opens = "hover".to_owned();
    ui.settings_changed_elsewhere(&saved);
    ui.frame(Instant::now(), size);
    assert_eq!(mode(&mut ui), DockMode::Top);
    assert_eq!(
        ui.host_mut()
            .with_controller::<PluginDock, _>(WINDOW, |c, _| c.size()),
        Some(ac_ui::plugin::dock::DockSize::Bigger)
    );
    assert_eq!(
        ui.host_mut()
            .with_controller::<PluginDock, _>(WINDOW, |c, _| c.opens()),
        Some(ac_ui::plugin::dock::DockOpens::Hover)
    );
    let frame = ui.host().root.window(WINDOW).unwrap().frame;
    let at = |ui: &WindowUi| {
        let e = ui.host().root.element(frame).unwrap();
        (e.left, e.top)
    };
    assert_eq!(at(&ui).1, 0.0);

    // Pull it down off the top, then to the left edge.
    let (left, _) = at(&ui);
    let (x, y) = (left as i32 + 5, 10);
    let host = ui.host_mut();
    host.mouse_move(x, y);
    host.mouse_down(MouseButton::Left, x, y, 0);
    host.mouse_move(x, y + 100);
    ui.frame(Instant::now(), size);
    ui.feed(&mut scene);
    assert_eq!(mode(&mut ui), DockMode::FloatingHorizontal);
    assert_eq!(
        ui.settings().plugin_ui.dock,
        "top",
        "not until the drag ends"
    );
    ui.host_mut().mouse_up(MouseButton::Left, x, y + 100, 0);
    ui.frame(Instant::now(), size);
    ui.feed(&mut scene);
    assert_eq!(ui.settings().plugin_ui.dock, "floating-horizontal");
    assert!(
        ui.take_settings_changes()
            .iter()
            .any(|(s, _)| *s == ac_ui::panels::options::Section::PluginUi)
    );
}

/// The limit on the dock's slots: a saved one applies, and a drag of its handle
/// is saved only as it ends.
#[test]
#[ignore = "needs the retail dat files"]
fn the_dock_takes_its_saved_limit_and_a_drag_of_its_handle_is_saved_when_it_ends() {
    use ac_host::settings::PlayerSettings;
    use ac_ui::event::MouseButton;
    use ac_ui::plugin::dock::{PluginDock, WINDOW};
    let size = (1280, 720);
    let (mut state, _) = session();
    for i in 0..8 {
        state
            .register_panel(
                &UiOwner::new(format!("test.{i}"), format!("Plugin {i}")),
                PanelDescriptor::new("main", format!("Plugin {i}")),
                "<panel/>",
                BindingSchema::new("Binding"),
            )
            .unwrap();
    }
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.snapshot(),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let limit = |ui: &mut WindowUi| {
        ui.host_mut()
            .with_controller::<PluginDock, _>(WINDOW, |c, _| c.slot_limit())
            .unwrap()
    };
    assert_eq!(limit(&mut ui), None);

    let mut saved = PlayerSettings::default();
    saved.plugin_ui.dock_slots = 3;
    ui.settings_changed_elsewhere(&saved);
    ui.frame(Instant::now(), size);
    assert_eq!(limit(&mut ui), Some(3));
    saved.plugin_ui.dock_slots = -1;
    ui.settings_changed_elsewhere(&saved);
    ui.frame(Instant::now(), size);
    assert_eq!(limit(&mut ui), None);
    saved.plugin_ui.dock_slots = 5;
    ui.settings_changed_elsewhere(&saved);
    ui.frame(Instant::now(), size);
    assert_eq!(limit(&mut ui), Some(5));

    let on_handle = |ui: &mut WindowUi| {
        let handle = ui
            .host_mut()
            .with_controller::<PluginDock, _>(WINDOW, |c, _| c.layout().length_handle)
            .unwrap()
            .unwrap();
        let frame = ui.host().root.window(WINDOW).unwrap().frame;
        let e = ui.host().root.element(frame).unwrap();
        (
            (e.left + handle.x + handle.w / 2.0) as i32,
            (e.top + handle.y + handle.h / 2.0) as i32,
        )
    };

    // Two slots back from its far end.
    ui.host_mut().root.set_ui_locked(false);
    ui.frame(Instant::now(), size);
    let (x, y) = on_handle(&mut ui);
    let host = ui.host_mut();
    host.mouse_move(x, y);
    host.mouse_down(MouseButton::Left, x, y, 0);
    host.mouse_move(x, y - 90);
    ui.frame(Instant::now(), size);
    ui.feed(&mut scene);
    assert_eq!(limit(&mut ui), Some(3));
    assert_eq!(
        ui.settings().plugin_ui.dock_slots,
        5,
        "not until the drag ends"
    );
    ui.host_mut().mouse_up(MouseButton::Left, x, y - 90, 0);
    ui.frame(Instant::now(), size);
    ui.feed(&mut scene);
    assert_eq!(ui.settings().plugin_ui.dock_slots, 3);
    assert!(
        ui.take_settings_changes()
            .iter()
            .any(|(s, _)| *s == ac_ui::panels::options::Section::PluginUi)
    );

    // A press in place saves nothing.
    let (x, y) = on_handle(&mut ui);
    let host = ui.host_mut();
    host.mouse_move(x, y);
    host.mouse_down(MouseButton::Left, x, y, 0);
    host.mouse_up(MouseButton::Left, x, y, 0);
    ui.frame(Instant::now(), size);
    ui.feed(&mut scene);
    assert!(ui.take_settings_changes().is_empty());
}

/// A reader that hands out a 4 x 4 image for every path (OpenAC PR #271's
/// file icons; the real reader is `ac-plugin-ui`'s).
#[derive(Debug)]
struct SquareFiles;

impl ac_plugin_ui::FileImageLoader for SquareFiles {
    fn load(&self, _directory: &std::path::Path, path: &str) -> ac_plugin_ui::FileImageRead {
        ac_plugin_ui::FileImageRead::finished(Ok(Arc::new(ac_plugin_ui::FileImage {
            path: path.to_owned(),
            version: "1".to_owned(),
            levels: ac_plugin_ui::mips::build(vec![255; 4 * 4 * 4], 4, 4),
            full_path: None,
        })))
    }
}

/// A reader that hands out one 64 x 64 dock icon, filled by the file's name.
#[derive(Debug)]
struct DockIconFiles;

impl ac_plugin_ui::FileImageLoader for DockIconFiles {
    fn load(&self, _directory: &std::path::Path, _path: &str) -> ac_plugin_ui::FileImageRead {
        ac_plugin_ui::FileImageRead::finished(Err("not used here".to_owned()))
    }

    fn read_dock_icon(
        &self,
        _directory: &std::path::Path,
        path: &str,
    ) -> Result<Option<ac_plugin_ui::PluginIcon>, String> {
        let fill = match path {
            "icons/a.png" => 40,
            "icons/b.png" => 80,
            _ => return Ok(None),
        };
        Ok(Some(ac_plugin_ui::PluginIcon {
            width: 64,
            height: 64,
            rgba: Arc::new(vec![fill; 64 * 64 * 4]),
        }))
    }
}

/// Three windows name two files: the dock draws them from two textures, as a
/// picture uploads once however many slots show it. When the plugin goes, the
/// window gives both textures back.
#[test]
#[ignore = "needs the retail dat files"]
fn the_dock_uploads_each_windows_picture_once_and_gives_it_back() {
    let size = (1280, 720);
    let owner = UiOwner::new("test.files", "Files");
    let mut state = PluginUi::new();
    state.set_window_attached(true);
    state.set_file_images(Some(Arc::new(DockIconFiles)));
    state.set_plugin_folder("test.files", Some(PathBuf::from("plugin")));
    for (window, file) in [
        ("one", "icons/a.png"),
        ("two", "icons/a.png"),
        ("three", "icons/b.png"),
    ] {
        let descriptor = PanelDescriptor {
            icon_file: Some(file.to_owned()),
            start_visible: false,
            ..PanelDescriptor::new(window, window)
        };
        state
            .register_panel(&owner, descriptor, "<panel/>", BindingSchema::new("Files"))
            .unwrap();
    }
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.snapshot(),
    )));
    ui.feed(&mut scene);
    let list = ui.frame(Instant::now(), size);
    let mut textures: Vec<u32> = list
        .main
        .iter()
        .filter_map(|c| match c {
            ac_ui::draw::DrawCmd::Sprite {
                texture: ac_ui::draw::TextureRef::Runtime(id),
                ..
            } => Some(*id),
            _ => None,
        })
        .collect();
    assert_eq!(textures.len(), 4, "the tile and three slots");
    textures.sort_unstable();
    textures.dedup();
    assert_eq!(textures.len(), 3, "the tile and two pictures");

    state.release("test.files");
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    let _ = ui.frame(Instant::now(), size);
    let released = ac_render::ui::UiTextureSource::released(&mut ui.surfaces());
    assert_eq!(
        released
            .iter()
            .filter(|t| matches!(t, ac_ui::draw::TextureRef::Runtime(id) if textures.contains(id)))
            .count(),
        2,
        "{released:?}"
    );
}

/// The window bakes a sharper bundled font in each weight when it is on a
/// high-density display, one a frame so no frame waits for them all, lets go
/// of the bakes for a density it leaves, and draws from none on a standard
/// display.
#[test]
#[ignore = "needs the retail dat files"]
fn the_window_follows_the_displays_density_with_sharper_fonts() {
    let mut ui = WindowUi::build(dats(), (1280, 720)).expect("the UI builds");
    ui.set_pixels_per_unit(1.0);
    assert!(ui.host().root.sharp_fonts().is_none());

    let textures = |ui: &WindowUi| {
        let mut bakes: Vec<_> = ui
            .host()
            .root
            .sharp_fonts()
            .expect("sharper bakes")
            .values()
            .map(|s| s.font.foreground.texture)
            .collect();
        bakes.sort_by_key(|t| format!("{t:?}"));
        bakes
    };
    let frames = |ui: &mut WindowUi, pixels: f32, count: usize| {
        for _ in 0..count {
            ui.set_pixels_per_unit(pixels);
        }
    };
    ui.set_pixels_per_unit(2.0);
    assert_eq!(textures(&ui).len(), 1, "one bake a frame");
    frames(&mut ui, 2.0, 4);
    let first = textures(&ui);
    // The regular and SemiBold fonts, and the dock's count and two sizes of
    // letters.
    assert_eq!(first.len(), 5);
    assert!(first.windows(2).all(|t| t[0] != t[1]));
    frames(&mut ui, 2.0, 3);
    assert_eq!(textures(&ui), first, "nothing more to bake");

    // Leaving the density lets its bakes go; coming back bakes them again.
    ui.set_pixels_per_unit(1.0);
    assert!(ui.host().root.sharp_fonts().is_none());
    frames(&mut ui, 2.0, 5);
    let again = textures(&ui);
    assert_eq!(again.len(), 5);
    assert!(again.iter().all(|t| !first.contains(t)), "baked anew");

    // No bake goes past what the largest allows, so a bigger font stops sooner
    // and a smaller one has room for more. Bakes the new density still wants
    // are kept.
    frames(&mut ui, 3.0, 5);
    let mut scales: Vec<f32> = ui
        .host()
        .root
        .sharp_fonts()
        .expect("sharper bakes")
        .values()
        .map(|s| s.scale)
        .collect();
    scales.sort_by(f32::total_cmp);
    assert_eq!(scales, [1.5, 2.0, 2.0, 2.5, 3.0]);
    let at_three = textures(&ui);
    assert_eq!(again.iter().filter(|t| at_three.contains(t)).count(), 3);
}
fn runtime_sprites(list: &ac_ui::draw::DrawList) -> usize {
    list.main
        .iter()
        .filter(|c| {
            matches!(
                c,
                ac_ui::draw::DrawCmd::Sprite {
                    texture: ac_ui::draw::TextureRef::Runtime(_),
                    ..
                }
            )
        })
        .count()
}

/// The textures canvas text was drawn from with a plugin's font.
fn runtime_glyph_textures(list: &ac_ui::draw::DrawList) -> Vec<ac_ui::draw::TextureRef> {
    list.commands()
        .filter_map(|c| match c {
            ac_ui::draw::DrawCmd::Glyphs { texture, .. }
                if matches!(texture, ac_ui::draw::TextureRef::Runtime(_)) =>
            {
                Some(*texture)
            }
            _ => None,
        })
        .collect()
}

/// A plugin's font in the real window: canvas text in it draws from an atlas
/// this window uploaded, from a sharper one once the window says its display is
/// dense, and not at all once the plugin lets go.
#[test]
#[ignore = "needs the retail dat files"]
fn a_plugins_font_draws_on_its_canvas_and_sharper_on_a_dense_display() {
    use ac_plugin_ui::fonts::FontWeight;
    use ac_plugin_ui::{CanvasAnchor, CanvasDescriptor, Color8, PaintCmd};
    let size = (1280, 720);
    let owner = UiOwner::new("test.font", "Font");
    let mut state = PluginUi::new();
    state.set_window_attached(true);
    let canvas = state
        .register_canvas(
            &owner,
            CanvasDescriptor {
                canvas_id: "c".into(),
                width: 200,
                height: 60,
                anchor: CanvasAnchor::from_index(0).unwrap(),
                offset: (100.0, 100.0),
                start_visible: true,
                accepts_pointer_input: false,
            },
        )
        .unwrap();
    let font = state.font_bundled("test.font", 16.0, FontWeight::SemiBold);
    assert_ne!(font.handle, 0);
    state
        .paint_canvas(
            "test.font",
            canvas,
            vec![PaintCmd::Text {
                text: "Golem".into(),
                at: (4.0, 4.0),
                color: Color8::new(255, 255, 255, 255),
                outline: false,
                font: font.handle,
            }],
        )
        .unwrap();
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    ui.set_pixels_per_unit(1.0);
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.snapshot(),
    )));
    ui.feed(&mut scene);
    let plain = runtime_glyph_textures(&ui.frame(Instant::now(), size));
    assert_eq!(plain.len(), 1, "one run from one atlas");
    deliver(&mut state, ui.drain());

    ui.set_pixels_per_unit(2.0);
    let commands = ui.drain();
    assert!(commands.iter().any(|c| matches!(
        c,
        SessionCommand::PluginUi(UiInput::DisplayDensity(d)) if *d == 2.0
    )));
    deliver(&mut state, commands);
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    let sharp = runtime_glyph_textures(&ui.frame(Instant::now(), size));
    assert_eq!(sharp.len(), 1);
    assert_ne!(sharp, plain, "the sharper atlas");

    assert!(state.font_release("test.font", font.handle));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    assert!(runtime_glyph_textures(&ui.frame(Instant::now(), size)).is_empty());
}

/// A plugin window's file icon: the window tells the session the path it
/// draws, the session holds the image and says which, the window draws
/// it; hidden, the window draws nothing and says so.
#[test]
#[ignore = "needs the retail dat files"]
fn a_plugin_windows_file_icon_is_held_while_drawn_and_drawn_once_held() {
    let size = (1280, 720);
    let mut state = PluginUi::new();
    state.set_window_attached(true);
    state.set_file_images(Some(Arc::new(SquareFiles)));
    state.set_plugin_folder("test.files", Some(PathBuf::from("plugin")));
    let panel = state
        .register_panel(
            &UiOwner::new("test.files", "Files"),
            PanelDescriptor::new("main", "Files"),
            r#"<panel x="300" y="200" w="240" h="120" title="Files">
  <group x="8" y="30" w="100" h="40" visible="{PageOpen}">
    <icon x="0" y="0" w="24" h="24" file="icons/sword.png"/>
  </group>
</panel>"#,
            BindingSchema::new("Files").with("PageOpen", K::Bool),
        )
        .unwrap();
    state
        .set_values(
            "test.files",
            panel,
            vec![("PageOpen".into(), UiValue::Bool(true))],
        )
        .unwrap();
    state.show_panel("test.files", "main", true);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.snapshot(),
    )));
    ui.feed(&mut scene);
    let before = runtime_sprites(&ui.frame(Instant::now(), size));
    let commands = ui.drain();
    assert!(commands.iter().any(|c| matches!(c,
        SessionCommand::PluginUi(UiInput::FileIconsShown { panel: p, paths })
            if *p == panel && paths == &["icons/sword.png".to_owned()])));
    deliver(&mut state, commands);
    assert!(state.file_icon(panel, "icons/sword.png").is_some());

    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    let after = runtime_sprites(&ui.frame(Instant::now(), size));
    assert_eq!(after, before + 1, "the file icon draws once held");

    // Its page closes while the window stays shown: the window says it no
    // longer draws it, and the session lets go of it 5 s later.
    state
        .set_values(
            "test.files",
            panel,
            vec![("PageOpen".into(), UiValue::Bool(false))],
        )
        .unwrap();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let commands = ui.drain();
    assert!(commands.iter().any(|c| matches!(c,
        SessionCommand::PluginUi(UiInput::FileIconsShown { panel: p, paths })
            if *p == panel && paths.is_empty())));
    deliver(&mut state, commands);
    assert!(state.panels().next().unwrap().shown());
    for second in 1..=6 {
        state.tick_file_icons(second * 1000);
    }
    assert_eq!(state.image_count("test.files"), 0);
    assert!(state.file_icon(panel, "icons/sword.png").is_none());

    // Open again: drawn, said, read and held again.
    state
        .set_values(
            "test.files",
            panel,
            vec![("PageOpen".into(), UiValue::Bool(true))],
        )
        .unwrap();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    deliver(&mut state, ui.drain());
    assert!(state.file_icon(panel, "icons/sword.png").is_some());

    // Hidden: the window draws no file icons and tells the session.
    state.show_panel("test.files", "main", false);
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let commands = ui.drain();
    assert!(commands.iter().any(|c| matches!(c,
        SessionCommand::PluginUi(UiInput::FileIconsShown { panel: p, paths })
            if *p == panel && paths.is_empty())));
}
