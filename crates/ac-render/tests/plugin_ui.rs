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

/// The options window's Configuration page opened, its vsync row unchecked
/// as a click does.
fn uncheck_vsync_in_the_options_window(ui: &mut WindowUi) {
    use ac_render::ui::options::OPTIONS_WINDOW;
    use ac_ui::widgets::Button;
    assert!(ui.open_options_page(ac_ui::panels::options::OptionsPage::Configuration));
    // The page shows (and applies) before the click.
    ui.drain();
    ui.host_mut().pump();
    ui.drain();
    let content = ac_ui::content::DatContent::new(dats()).unwrap();
    let caption = content.strings().resolve(
        0x2300_0003,
        ac_ui::strings::compute_hash("ID_Rendering_SyncToDisplayRefresh"),
        0,
    );
    assert!(caption.is_some(), "the vsync caption resolves");
    let root = &ui.host().root;
    let mut stack = vec![root.window(OPTIONS_WINDOW).unwrap().frame];
    let mut row = None;
    while let Some(id) = stack.pop() {
        if root
            .widget::<Button>(id)
            .is_some_and(|b| b.label.is_some() && b.label == caption)
        {
            row = Some(id);
        }
        stack.extend(root.children(id));
    }
    let row = row.expect("the vsync row");
    let root = &mut ui.host_mut().root;
    root.with::<Button, _>(row, |b, ui| b.set_selected(ui, row, false));
    root.send_message(row, ac_ui::root::ElementMessage::Clicked { x: 1, y: 1 });
    ui.host_mut().pump();
    ui.drain();
}

/// The options window edits only what it changed: the dock's place, saved
/// by a drag after the options window was built, survives the options
/// window's next change (vsync), so the next save writes the new place.
#[test]
#[ignore = "needs the retail dat files"]
fn the_docks_dragged_place_survives_an_options_window_change() {
    use ac_host::settings::PlayerSettings;
    use ac_ui::event::MouseButton;
    use ac_ui::panels::options::Section;
    use ac_ui::plugin::dock::{DockMode, PluginDock, WINDOW};
    let size = (1280, 720);
    let (state, _) = session();
    let mut saved = PlayerSettings::default();
    saved.plugin_ui.dock = "top".to_owned();
    let mut ui = WindowUi::build_with(dats(), size, &saved, vec!["1280x720".into()], None)
        .expect("the UI builds");
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
    assert_eq!(mode(&mut ui), DockMode::Top);

    // Drag the dock down off the top edge: its new place is saved.
    let frame = ui.host().root.window(WINDOW).unwrap().frame;
    let left = ui.host().root.element(frame).unwrap().left;
    let (x, y) = (left as i32 + 5, 10);
    let host = ui.host_mut();
    host.mouse_move(x, y);
    host.mouse_down(MouseButton::Left, x, y, 0);
    host.mouse_move(x, y + 100);
    ui.frame(Instant::now(), size);
    ui.feed(&mut scene);
    ui.host_mut().mouse_up(MouseButton::Left, x, y + 100, 0);
    ui.frame(Instant::now(), size);
    ui.feed(&mut scene);
    assert_eq!(ui.settings().plugin_ui.dock, "floating-horizontal");
    ui.take_settings_changes();

    uncheck_vsync_in_the_options_window(&mut ui);
    assert!(!ui.settings().display.vsync, "the options change applies");
    assert_eq!(
        ui.settings().plugin_ui.dock,
        "floating-horizontal",
        "the options window put the dock's old place back"
    );
    assert_eq!(mode(&mut ui), DockMode::FloatingHorizontal);
    assert_eq!(ui.take_settings_changes(), [(Section::Display, true)]);
}

/// The same for the theme picked in the Appearance window: it survives the
/// options window's next change.
#[test]
#[ignore = "needs the retail dat files"]
fn the_picked_plugin_theme_survives_an_options_window_change() {
    use ac_plugin_ui::ThemeKind;
    use ac_ui::plugin::dock::{PluginDock, WINDOW};
    use ac_ui::widgets::Menu;
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

    // The dock's gear opens the Appearance window.
    let gear = ui
        .host_mut()
        .with_controller::<PluginDock, _>(WINDOW, |c, _| c.layout().gear)
        .unwrap();
    let frame = ui.host().root.window(WINDOW).unwrap().frame;
    let e = ui.host().root.element(frame).unwrap();
    ui.script(&UiScript::Click {
        x: (e.left + gear.x + 4.0) as i32,
        y: (e.top + gear.y + 4.0) as i32,
        button: 0,
        double: false,
    });
    for _ in 0..2 {
        ui.feed(&mut scene);
        ui.frame(Instant::now(), size);
    }
    assert!(ui.host().root.is_window_visible(APPEARANCE));

    // Its theme menu picks Moss.
    let moss = ThemeKind::Moss.display_name();
    let root = &ui.host().root;
    let mut stack = vec![root.window(APPEARANCE).unwrap().frame];
    let mut pick = None;
    while let Some(id) = stack.pop() {
        if let Some(item) = root
            .widget::<Menu>(id)
            .and_then(|m| m.items.iter().find(|i| i.label == moss))
        {
            pick = Some((id, item.payload));
        }
        stack.extend(root.children(id));
    }
    let (menu, payload) = pick.expect("the theme menu offers Moss");
    ui.host_mut()
        .root
        .send_message(menu, ac_ui::root::ElementMessage::MenuSelected(payload));
    ui.host_mut().pump();
    for _ in 0..2 {
        ui.feed(&mut scene);
        ui.frame(Instant::now(), size);
    }
    let theme = ThemeKind::Moss.saved_name();
    assert_eq!(ui.settings().plugin_ui.theme, theme);
    ui.take_settings_changes();

    uncheck_vsync_in_the_options_window(&mut ui);
    assert!(!ui.settings().display.vsync, "the options change applies");
    assert_eq!(
        ui.settings().plugin_ui.theme,
        theme,
        "the options window put the old theme back"
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
                in_markup: false,
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

/// A character in the world, so the window restores, reflows and saves its
/// layouts.
fn in_world(name: &str) -> PresentationEvent {
    use ac_runtime::world::{ObjectKey, ObjectView, WorldSnapshot};
    PresentationEvent::Snapshot(WorldSnapshot {
        player_guid: Some(0x5000_0001),
        objects: vec![ObjectView {
            key: ObjectKey {
                local_id: 1,
                incarnation: 1,
            },
            guid: 0x5000_0001,
            name: Some(name.to_owned()),
            setup_id: None,
            position: None,
            parent: None,
            placement: None,
            rest_placement: 0,
            scale: None,
            translucency: None,
            state: 0,
            velocity: None,
            model: ac_proto::ModelData::default(),
            motion_table_id: None,
            sound_table_id: None,
            radar: ac_runtime::world::RadarFacts::default(),
            motion: None,
            effects: ac_runtime::world::EffectFacts::default(),
            step_blend: None,
        }],
    })
}

/// The last report of the screen and the registered windows among the
/// window's commands.
fn windows_report(
    commands: &[SessionCommand],
) -> Option<((i32, i32), Vec<ac_plugin_ui::WindowReport>)> {
    commands.iter().rev().find_map(|c| match c {
        SessionCommand::PluginUi(UiInput::Windows {
            screen, windows, ..
        }) => Some((*screen, windows.clone())),
        _ => None,
    })
}

/// The window tells the session its screen and every registered window
/// (whose each is, its limits, which the game shows and hides); a plugin
/// places a client window and its own as a saved layout is restored, the
/// result is saved for the character as the player's own change, a window
/// the game shows and hides keeps its shown state, and a new screen
/// arrives with the windows already reflowed onto it.
#[test]
#[ignore = "needs the retail dat files"]
fn a_plugin_reads_and_places_the_registered_windows() {
    use ac_plugin_ui::{WindowLayout, WindowOwner, WindowParts};
    use ac_render::ui::window::RADAR_WINDOW;
    const VENDOR_WINDOW: &str = "vendor";
    let settings =
        std::env::temp_dir().join(format!("openac-plugin-windows-{}.json", std::process::id()));
    let _ = std::fs::remove_file(&settings);
    let size = (1280, 720);
    let (mut state, _) = session();
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    ui.set_stores(
        Some(Box::new(ac_host::settings::SettingsStore::new(
            settings.clone(),
        ))),
        None,
    );
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.snapshot(),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    // Before the character's layout is restored the windows are reported,
    // but a placing is refused: the restore would undo it.
    deliver(&mut state, ui.drain());
    assert_eq!(state.screen_size(), Some((1280, 720)));
    assert!(!state.windows_ready());
    assert_eq!(state.take_screen_change(), Some((1280, 720)));
    assert!(state.take_windows_change());
    let early = state.registered_windows()[0].clone();
    assert!(
        state
            .apply_window(&early.name, &early.layout, WindowParts::ALL)
            .is_err()
    );

    // In the world: the layout is restored, and both are told again.
    scene.apply(in_world("Alice"));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let commands = ui.drain();
    let (screen, windows) = windows_report(&commands).expect("the window reports its windows");
    assert_eq!(screen, (1280, 720));
    let find = |windows: &[ac_plugin_ui::WindowReport], name: &str| {
        windows.iter().find(|w| w.name == name).cloned()
    };
    let plugin = find(&windows, "plugin:test.demo:main").unwrap();
    assert_eq!(plugin.owner, WindowOwner::Plugin("test.demo".into()));
    assert_eq!(plugin.title.as_deref(), Some("Demo"));
    let dock = find(&windows, ac_ui::plugin::dock::WINDOW).unwrap();
    assert_eq!(dock.owner, WindowOwner::Dock);
    let radar = find(&windows, RADAR_WINDOW).unwrap();
    assert_eq!(radar.owner, WindowOwner::Client);
    assert!(!radar.state_managed);
    let vendor = find(&windows, VENDOR_WINDOW).unwrap();
    assert!(vendor.state_managed);
    assert!(!vendor.layout.visible);
    deliver(&mut state, commands);
    assert!(state.windows_ready());
    assert_eq!(state.take_screen_change(), Some((1280, 720)));
    assert!(state.take_windows_change());

    // The plugin moves the radar and its own window, and asks to show the
    // vendor window, which the game shows by itself.
    let place = WindowParts {
        place: true,
        ..WindowParts::default()
    };
    let radar_at = WindowLayout {
        x: 40.0,
        y: 60.0,
        ..radar.layout
    };
    let applied = state.apply_window(RADAR_WINDOW, &radar_at, place).unwrap();
    assert_eq!((applied.layout.x, applied.layout.y), (40.0, 60.0));
    let plugin_at = WindowLayout {
        x: 500.0,
        y: 300.0,
        ..plugin.layout
    };
    state
        .apply_window("plugin:test.demo:main", &plugin_at, place)
        .unwrap();
    let vendor_at = WindowLayout {
        x: 100.0,
        y: 120.0,
        visible: true,
        ..vendor.layout
    };
    let vendor_applied = state
        .apply_window(VENDOR_WINDOW, &vendor_at, WindowParts::ALL)
        .unwrap();
    assert!(vendor_applied.shown_ignored);
    assert!(!vendor_applied.layout.visible);
    assert!(state.apply_window("nowhere", &radar_at, place).is_err());
    // A hidden main panel placed, sized and shown in one go: shown through
    // the panel switcher first, so the switcher's shared place does not
    // undo the placing, and the other main panels take its place.
    let inventory = find(&windows, INVENTORY_WINDOW).unwrap();
    assert!(!inventory.layout.visible);
    let inventory_at = WindowLayout {
        x: 60.0,
        y: 50.0,
        width: inventory.layout.width,
        height: 500.0,
        visible: true,
        ..inventory.layout
    };
    let inventory_applied = state
        .apply_window(INVENTORY_WINDOW, &inventory_at, WindowParts::ALL)
        .unwrap();
    // The toolbar: a size it cannot take on a fixed axis is not predicted.
    let toolbar = find(&windows, "toolbar").unwrap();
    let toolbar_size = WindowParts {
        size: true,
        ..WindowParts::default()
    };
    let toolbar_applied = state
        .apply_window(
            "toolbar",
            &WindowLayout {
                width: 600.0,
                height: 60.0,
                ..toolbar.layout
            },
            toolbar_size,
        )
        .unwrap();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    // Only the windows that changed or were placed are reported, not the
    // whole list: the toolbar's placing changed nothing, and it is
    // reported all the same.
    let commands = ui.drain();
    assert!(windows_report(&commands).is_none(), "no whole report");
    let changed: Vec<String> = commands
        .iter()
        .find_map(|c| match c {
            SessionCommand::PluginUi(UiInput::WindowsChanged(windows)) => {
                Some(windows.iter().map(|w| w.name.clone()).collect())
            }
            _ => None,
        })
        .expect("the changed windows are reported");
    for name in [
        RADAR_WINDOW,
        "plugin:test.demo:main",
        VENDOR_WINDOW,
        INVENTORY_WINDOW,
        "toolbar",
        ac_render::ui::window::CHARACTER_WINDOW,
    ] {
        assert!(changed.iter().any(|n| n == name), "{name}: {changed:?}");
    }
    assert!(!changed.iter().any(|n| n == "chat"), "{changed:?}");
    deliver(&mut state, commands);
    let windows = state.registered_windows().to_vec();
    // A frame with nothing changed reports nothing.
    ui.frame(Instant::now(), size);
    assert!(
        !ui.drain().iter().any(|c| matches!(
            c,
            SessionCommand::PluginUi(UiInput::Windows { .. } | UiInput::WindowsChanged(_))
        )),
        "nothing changed"
    );
    // Each answer was where the window went.
    let landed = |name: &str| find(&windows, name).unwrap().layout;
    assert_eq!(landed(INVENTORY_WINDOW), inventory_applied.layout);
    // The toolbar is fixed in width, as predicted; its height snaps to the
    // stop its own state keeps (expanded), which the answer cannot know.
    let toolbar_landed = landed("toolbar");
    assert!(!toolbar.resizable.0);
    assert_eq!(
        (toolbar_landed.x, toolbar_landed.width),
        (toolbar_applied.layout.x, toolbar_applied.layout.width)
    );
    assert_eq!(toolbar_landed.width, toolbar.layout.width);
    assert_eq!(
        toolbar_landed.height, toolbar.layout.height,
        "its expanded stop"
    );
    assert_eq!(landed(RADAR_WINDOW), applied.layout);
    let shared = landed(ac_render::ui::window::CHARACTER_WINDOW);
    assert_eq!(
        (shared.x, shared.y, shared.height),
        (60.0, 50.0, 500.0),
        "the other main panels share the placed one's place and size"
    );
    assert!(ui.host().root.is_window_visible(INVENTORY_WINDOW));
    let place_of = |name: &str| {
        let w = find(&windows, name).unwrap();
        (w.layout.x, w.layout.y, w.layout.visible)
    };
    assert_eq!(place_of(RADAR_WINDOW), (40.0, 60.0, radar.layout.visible));
    assert_eq!(
        place_of("plugin:test.demo:main"),
        (500.0, 300.0, plugin.layout.visible)
    );
    assert_eq!(place_of(VENDOR_WINDOW), (100.0, 120.0, false));
    assert!(!ui.host().root.is_window_visible(VENDOR_WINDOW));

    // Saved for the character at this screen size, as a drag would be.
    let store = ac_host::settings::SettingsStore::new(settings.clone());
    let saved = |name: &str| {
        store
            .load_window_layout(
                "Alice",
                "1280x720",
                name,
                &ac_host::settings::UiWindowLayout::default(),
            )
            .map(|l| (l.x, l.y))
    };
    assert_eq!(saved(RADAR_WINDOW), Some((40.0, 60.0)));
    assert_eq!(saved(INVENTORY_WINDOW), Some((60.0, 50.0)));
    assert!(
        store
            .load_window_layout(
                "Alice",
                "1280x720",
                INVENTORY_WINDOW,
                &ac_host::settings::UiWindowLayout::default(),
            )
            .unwrap()
            .visible,
        "shown, and saved shown"
    );
    assert_eq!(saved("plugin:test.demo:main"), Some((500.0, 300.0)));

    // A smaller screen: the report that carries it has the windows already
    // reflowed onto it.
    let small = (800, 600);
    ui.frame(Instant::now(), small);
    let (screen, windows) = windows_report(&ui.drain()).expect("the new screen is reported");
    assert_eq!(screen, (800, 600));
    for w in &windows {
        let l = w.layout;
        assert!(
            l.x + l.width.min(800.0) <= 800.5 && l.y + l.height.min(600.0) <= 600.5,
            "{} is off the 800x600 screen: {l:?}",
            w.name
        );
    }
    let plugin = find(&windows, "plugin:test.demo:main").unwrap();
    assert!(plugin.layout.x < 500.0, "reflowed: {:?}", plugin.layout);
    let _ = std::fs::remove_file(&settings);
}

/// The window and the session in the world as "Alice", the layouts saved to
/// `settings`, the window's reports delivered and the changes taken.
fn in_world_with_layouts(
    settings: &std::path::Path,
    size: (u32, u32),
) -> (PluginUi, WindowUi, Scene) {
    let (mut state, _) = session();
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    ui.set_stores(
        Some(Box::new(ac_host::settings::SettingsStore::new(
            settings.to_path_buf(),
        ))),
        None,
    );
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.snapshot(),
    )));
    scene.apply(in_world("Alice"));
    for _ in 0..2 {
        ui.feed(&mut scene);
        ui.frame(Instant::now(), size);
        deliver(&mut state, ui.drain());
    }
    assert!(state.windows_ready());
    state.take_changes();
    (state, ui, scene)
}

/// The session's changes to the window, and one frame.
fn pass(state: &mut PluginUi, ui: &mut WindowUi, scene: &mut Scene, size: (u32, u32)) {
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        state.take_changes(),
    )));
    ui.feed(scene);
    ui.frame(Instant::now(), size);
    deliver(state, ui.drain());
}

/// Every main panel reports the place it shares; a window on its own
/// reports none.
#[test]
#[ignore = "needs the retail dat files"]
fn the_main_panels_report_the_place_they_share() {
    let settings =
        std::env::temp_dir().join(format!("openac-plugin-shared-{}.json", std::process::id()));
    let _ = std::fs::remove_file(&settings);
    let size = (1280, 720);
    let (state, _ui, _scene) = in_world_with_layouts(&settings, size);
    let shares = |name: &str| {
        state
            .registered_windows()
            .iter()
            .find(|w| w.name == name)
            .unwrap_or_else(|| panic!("{name} is registered"))
            .shares_place
            .clone()
    };
    for name in [
        INVENTORY_WINDOW,
        ac_render::ui::window::CHARACTER_WINDOW,
        "spellbook",
    ] {
        assert_eq!(
            shares(name).as_deref(),
            Some(ac_plugin_ui::MAIN_PANELS),
            "{name}"
        );
    }
    assert_eq!(shares(ac_render::ui::window::CHAT_WINDOW), None);
    assert_eq!(shares(ac_render::ui::window::VITALS_WINDOW), None);
    assert_eq!(shares("plugin:test.demo:main"), None);
    let _ = std::fs::remove_file(&settings);
}

/// A plugin holds windows out of sight: a held window is not drawn and
/// takes no input, but stays shown as the game has it, and nothing saved
/// changes. The game goes on showing a held main panel underneath, which
/// shows once the hold ends. A placing of a held window applies and is
/// saved. Unloading the plugin, and the character leaving the world, end
/// its hold.
#[test]
#[ignore = "needs the retail dat files"]
fn a_plugin_holds_windows_out_of_sight_without_saving_anything() {
    use ac_plugin_ui::{WindowLayout, WindowParts};
    use ac_render::ui::window::VITALS_WINDOW;
    let settings =
        std::env::temp_dir().join(format!("openac-plugin-holds-{}.json", std::process::id()));
    let _ = std::fs::remove_file(&settings);
    let size = (1280, 720);
    let (mut state, mut ui, mut scene) = in_world_with_layouts(&settings, size);
    let drawn = |ui: &mut WindowUi| {
        ui.host_mut()
            .draw((1280.0, 720.0), &ac_ui::draw::NoSprites)
            .commands()
            .count()
    };
    let reported = |state: &PluginUi, name: &str| {
        state
            .registered_windows()
            .iter()
            .find(|w| w.name == name)
            .unwrap()
            .clone()
    };
    assert!(ui.host().root.is_window_visible(VITALS_WINDOW));
    assert!(!ui.host().root.is_window_visible(INVENTORY_WINDOW));
    let saved_before = std::fs::read(&settings).ok();
    let shown = drawn(&mut ui);

    let held = state
        .hold_windows(
            "test.demo",
            &[VITALS_WINDOW.to_owned(), INVENTORY_WINDOW.to_owned()],
        )
        .unwrap();
    assert_eq!(held.held.len(), 2);
    pass(&mut state, &mut ui, &mut scene, size);
    let root = &ui.host().root;
    assert!(root.is_window_held(VITALS_WINDOW));
    assert!(root.is_window_visible(VITALS_WINDOW), "still shown");
    let vitals = root.window(VITALS_WINDOW).unwrap().frame;
    assert!(!root.element(vitals).unwrap().drawn());
    assert!(drawn(&mut ui) < shown, "the vitals are not drawn");
    assert!(reported(&state, VITALS_WINDOW).layout.visible);
    assert!(state.is_window_held(VITALS_WINDOW));

    // The game opens the held inventory underneath: shown, still held.
    assert!(state.client_window(ClientWindow::Inventory, ClientWindowAction::Show));
    pass(&mut state, &mut ui, &mut scene, size);
    assert!(ui.host().root.is_window_visible(INVENTORY_WINDOW));
    assert!(ui.host().root.is_window_held(INVENTORY_WINDOW));
    assert!(reported(&state, INVENTORY_WINDOW).layout.visible);
    assert!(state.client_window(ClientWindow::Inventory, ClientWindowAction::Hide));
    pass(&mut state, &mut ui, &mut scene, size);
    assert_eq!(std::fs::read(&settings).ok(), saved_before, "nothing saved");

    // A placing of the held vitals applies and is saved; they stay held.
    let vitals_now = reported(&state, VITALS_WINDOW);
    let at = WindowLayout {
        x: 30.0,
        y: 40.0,
        ..vitals_now.layout
    };
    let place = WindowParts {
        place: true,
        ..WindowParts::default()
    };
    state.apply_window(VITALS_WINDOW, &at, place).unwrap();
    pass(&mut state, &mut ui, &mut scene, size);
    let placed = reported(&state, VITALS_WINDOW).layout;
    assert_eq!((placed.x, placed.y), (30.0, 40.0));
    assert!(ui.host().root.is_window_held(VITALS_WINDOW));
    let store = ac_host::settings::SettingsStore::new(settings.clone());
    let saved = store
        .load_window_layout(
            "Alice",
            "1280x720",
            VITALS_WINDOW,
            &ac_host::settings::UiWindowLayout::default(),
        )
        .unwrap();
    assert_eq!((saved.x, saved.y), (30.0, 40.0));

    // The plugin unloads: its hold ends, and the vitals are drawn again.
    state.release("test.demo");
    pass(&mut state, &mut ui, &mut scene, size);
    assert!(!ui.host().root.is_window_held(VITALS_WINDOW));
    assert!(!ui.host().root.is_window_held(INVENTORY_WINDOW));
    let root = &ui.host().root;
    assert!(root.element(vitals).unwrap().drawn(), "drawn again");

    // Held again, then the character leaves the world: the hold ends.
    let (mut state, _) = {
        let (mut fresh, id) = session();
        // The window keeps its windows; the session hears them again.
        fresh.input(UiInput::Windows {
            screen: (1280, 720),
            ready: true,
            windows: state.registered_windows().to_vec(),
        });
        (fresh, id)
    };
    state
        .hold_windows("test.demo", &[VITALS_WINDOW.to_owned()])
        .unwrap();
    pass(&mut state, &mut ui, &mut scene, size);
    assert!(ui.host().root.is_window_held(VITALS_WINDOW));
    scene.apply(PresentationEvent::Snapshot(
        ac_runtime::world::WorldSnapshot {
            player_guid: None,
            objects: Vec::new(),
        },
    ));
    for _ in 0..2 {
        pass(&mut state, &mut ui, &mut scene, size);
    }
    assert!(!state.windows_ready());
    assert!(!state.is_window_held(VITALS_WINDOW));
    assert!(!ui.host().root.is_window_held(VITALS_WINDOW));
    let _ = std::fs::remove_file(&settings);
}
