//! The window's retail UI against the retail dats: the chat, vitals and
//! radar windows import, mount where OpenAC's `RetailUiRuntime` mounts
//! them, take the session's chat, vitals and objects, draw only textures
//! the dats hold, and turn a typed line into the session command. Ignored
//! by default; `scripts/check.*` runs them when the dats are present.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_dat::DatCollection;
use ac_proto::{ModelData, Position, Quaternion, Vector3};
use ac_render::scene::Scene;
use ac_render::ui::batch::UiTexture;
use ac_render::ui::input::{self as ui_input, Routed};
use ac_render::ui::options::OPTIONS_WINDOW;
use ac_render::ui::window::{
    CHARACTER_WINDOW, CHAT_WINDOW, COMBAT_WINDOW, EXAMINATION_WINDOW, INVENTORY_WINDOW,
    RADAR_WINDOW, SIDE_VITALS_WINDOW, SPELLBOOK_WINDOW, TOOLBAR_WINDOW, VITALS_WINDOW, radar_scene,
};
use ac_render::ui::{UiBatch, UiTextureSource, WindowUi};
use ac_runtime::communication::{ChatEntryCommand, ChatKind, ChatLine};
use ac_runtime::session::interface::{InterfaceEvent, InterfaceState, ItemsUpdate, PlayerFacts};
use ac_runtime::session::{PresentationEvent, SessionCommand};
use ac_runtime::world::{
    ObjectKey, ObjectView, PlayerView, RadarFacts, VitalReading, WorldSnapshot,
};
use ac_ui::draw::{DrawCmd, TextureRef};
use ac_ui::event::{Key, Modifiers};
use ac_ui::panels::{chat, vitals};

fn dats() -> Arc<DatCollection> {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Arc::new(DatCollection::open(dir).expect("open the retail dats"))
}

fn at(cell: u32, x: f32, y: f32) -> Position {
    Position {
        cell_id: cell,
        origin: Vector3 { x, y, z: 0.0 },
        orientation: Quaternion::IDENTITY,
    }
}

fn object(
    local_id: u32,
    guid: u32,
    name: &str,
    position: Position,
    radar: RadarFacts,
) -> ObjectView {
    ObjectView {
        key: ObjectKey {
            local_id,
            incarnation: 1,
        },
        guid,
        name: Some(name.to_owned()),
        setup_id: None,
        position: Some(position),
        parent: None,
        placement: None,
        rest_placement: 0,
        scale: None,
        translucency: None,
        state: 0,
        velocity: None,
        model: ModelData::default(),
        motion_table_id: None,
        sound_table_id: None,
        radar,
        motion: None,
        effects: ac_runtime::world::EffectFacts::default(),
        step_blend: None,
    }
}

fn line(sequence: u64, text: &str) -> ChatLine {
    ChatLine {
        sequence,
        kind: ChatKind::System,
        log_text_type: 0x05,
        combat_kind: None,
        received_unix_ms: 0,
        text: text.to_owned(),
        channel_name: String::new(),
        sender: String::new(),
        sender_guid: 0,
        words: String::new(),
        display_text: String::new(),
        channel_id: 0,
        spans: None,
    }
}

const PLAYER: u32 = 0x5000_0001;
const DRUDGE: u32 = 0x8000_0010;

/// A scene with the player, a monster 10 m north of him, two chat lines
/// and the player's vitals.
fn scene() -> Scene {
    let mut scene = Scene::default();
    let creature = RadarFacts {
        item_type: 0x10,
        public_weenie_bitfield: 0,
        radar_behavior: Some(4),
        radar_blip_color: 0,
        monarch_id: 0,
    };
    scene.apply(PresentationEvent::Snapshot(WorldSnapshot {
        player_guid: Some(PLAYER),
        objects: vec![
            object(
                1,
                PLAYER,
                "+Horan",
                at(0xA9B4_0031, 100.0, 100.0),
                RadarFacts {
                    public_weenie_bitfield: 0x8,
                    ..creature
                },
            ),
            object(
                2,
                DRUDGE,
                "Drudge Skulker",
                at(0xA9B4_0031, 100.0, 110.0),
                creature,
            ),
        ],
    }));
    scene.apply(PresentationEvent::Chat(vec![
        line(1, "Welcome to Asheron's Call"),
        line(2, "  powered by ACEmulator"),
    ]));
    let vital = |current, max| VitalReading {
        current: Some(current),
        max: Some(max),
        percent: Some(current as f32 / max as f32),
    };
    scene.apply(PresentationEvent::Player(PlayerView {
        health: vital(42, 100),
        stamina: vital(50, 100),
        mana: vital(75, 100),
        selected: None,
        coordinates_on_radar: true,
        ui_locked: false,
        stay_in_chat_mode: false,
        disable_most_weather_effects: false,
        ..PlayerView::default()
    }));
    scene
}

fn press(ui: &mut WindowUi, key: Key, text: Option<&str>) -> Vec<SessionCommand> {
    let routed: Routed<u32> =
        ui_input::key(ui.host_mut(), 0, key, true, false, Modifiers::NONE, text);
    let mut commands = ui.handle(routed.outputs);
    let up: Routed<u32> = ui_input::key(ui.host_mut(), 0, key, false, false, Modifiers::NONE, None);
    commands.extend(ui.handle(up.outputs));
    commands
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_chat_vitals_and_radar_mount_feed_and_draw_from_the_dats() {
    let dats = dats();
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats, size).expect("the UI builds");
    assert_eq!(
        ui.mounted(),
        [
            VITALS_WINDOW,
            SIDE_VITALS_WINDOW,
            RADAR_WINDOW,
            CHAT_WINDOW,
            "chat-window-1",
            "chat-window-2",
            "chat-window-3",
            "chat-window-4",
            TOOLBAR_WINDOW,
            COMBAT_WINDOW,
            SPELLBOOK_WINDOW,
            EXAMINATION_WINDOW,
            CHARACTER_WINDOW,
            INVENTORY_WINDOW,
            ac_render::ui::container_window::EXTERNAL_CONTAINER_WINDOW,
            ac_render::ui::salvage_window::SALVAGE_WINDOW,
            ac_render::ui::book_window::BOOK_WINDOW,
            ac_render::ui::map_window::MAP_HOUSE_WINDOW,
            ac_render::ui::character_pages::CHARACTER_INFO_WINDOW,
            ac_render::ui::window::SOCIAL_WINDOW,
            ac_render::ui::window::JOURNAL_WINDOW,
            ac_render::ui::window::VENDOR_WINDOW,
            ac_render::ui::window::TRADE_WINDOW,
            ac_render::ui::window::HOUSE_PURCHASE_WINDOW,
            OPTIONS_WINDOW,
            ac_render::ui::pregame::PREGAME_SCREENS,
            ac_render::ui::indicator_panels::POSITIVE_EFFECTS_WINDOW,
            ac_render::ui::indicator_panels::NEGATIVE_EFFECTS_WINDOW,
            ac_render::ui::indicator_panels::LINK_STATUS_WINDOW,
            ac_render::ui::indicator_panels::VITAE_WINDOW,
            ac_render::ui::indicator_panels::MINI_GAME_WINDOW,
            ac_render::ui::indicator_panels::INDICATORS_WINDOW,
            ac_render::ui::status::JUMP_POWERBAR_WINDOW,
        ]
    );
    let notices = ui.take_notices();
    assert!(notices.is_empty(), "{notices:?}");
    let root = &ui.host().root;
    for name in [VITALS_WINDOW, RADAR_WINDOW, CHAT_WINDOW] {
        assert!(root.is_window_visible(name), "{name} is shown");
    }
    // OpenAC mounts at its corners, then puts every window at its default
    // place (`RetailWindowLayoutPersistence.ResetToDefaults`): the vitals
    // right of the indicator bar at the top, the radar 10 px from the
    // top-right corner, the chat 10 px from the bottom-left.
    let frame = |name| {
        let w = root.window(name).unwrap().frame;
        let e = root.element(w).unwrap();
        (e.left, e.top, e.width, e.height)
    };
    let indicators = root
        .window("indicators")
        .and_then(|w| root.element(w.frame))
        .map_or(0.0, |e| e.width);
    let (vl, vt, _, _) = frame(VITALS_WINDOW);
    assert_eq!((vl, vt), (indicators + 20.0, 10.0));
    let (rl, rt, rw, _) = frame(RADAR_WINDOW);
    assert_eq!((rl + rw, rt), (1280.0 - 10.0, 10.0));
    let (cl, ct, cw, ch) = frame(CHAT_WINDOW);
    assert_eq!((cl, ct, cw), (10.0, 720.0 - ch - 10.0, 410.0));

    let mut scene = scene();
    ui.feed(&mut scene);
    let start = Instant::now();
    // Two frames 50 ms apart: the radar shows its scene at its 25 ms step.
    ui.frame(start, size);
    let list = ui.frame(start + Duration::from_millis(50), size);
    assert!(!list.main.is_empty());

    // The vitals read the player's view.
    let text = |id: u32| {
        let root = &ui.host().root;
        let window = root.window(VITALS_WINDOW).unwrap().frame;
        let e = root.find_descendant(window, id).unwrap();
        root.widget::<ac_ui::widgets::Text>(e)
            .unwrap()
            .content_lines()
            .iter()
            .map(|l| l.text.clone())
            .collect::<Vec<_>>()
    };
    assert_eq!(text(vitals::HEALTH_TEXT), ["42/100"]);
    assert_eq!(text(vitals::MANA_TEXT), ["75/100"]);

    // The transcript holds the session's lines.
    {
        let root = &ui.host().root;
        let window = root.window(CHAT_WINDOW).unwrap().frame;
        let t = root.find_descendant(window, chat::TRANSCRIPT_ID).unwrap();
        let lines: Vec<String> = root
            .widget::<ac_ui::widgets::Text>(t)
            .unwrap()
            .content_lines()
            .iter()
            .map(|l| l.text.clone())
            .collect();
        assert!(
            lines
                .iter()
                .any(|l| l.contains("Welcome to Asheron's Call")),
            "{lines:?}"
        );
    }

    // The radar scene places the monster 10 m north of the player.
    let radar = radar_scene(&scene, scene.player_view(), None);
    assert_eq!(radar.objects.len(), 1);
    let player = radar.player.unwrap();
    assert_eq!(
        radar.objects[0].position - player.position,
        glam::Vec3::new(0.0, 10.0, 0.0)
    );
    assert!(radar.coordinates_on_radar);

    // Every texture drawn is in the dats, glyphs included; none is id 0.
    let batch = UiBatch::build(&list);
    assert_eq!(batch.skipped_zero_ids, 0);
    let mut surfaces = ui.surfaces();
    let mut glyph_atlases = 0;
    for cmd in list.commands() {
        if let DrawCmd::Glyphs { texture, .. } = cmd {
            glyph_atlases += 1;
            assert!(matches!(texture, TextureRef::Surface(id) if *id >> 24 == 0x06));
        }
    }
    assert!(glyph_atlases > 0, "text is drawn");
    for run in &batch.runs {
        if let UiTexture::Named(t @ TextureRef::Surface(id)) = run.key.texture {
            let pixels = surfaces
                .decode(t)
                .unwrap_or_else(|| panic!("{id:#010x} decodes"));
            assert!(pixels.is_whole(), "{id:#010x} fills its size");
        }
    }

    // A line typed into the chat entry becomes the session command.
    assert!(
        press(&mut ui, Key::Enter, Some("\r")).is_empty(),
        "Enter opens the entry"
    );
    for c in "hello".chars() {
        let key = match c {
            'h' => Key::H,
            'e' => Key::E,
            'l' => Key::L,
            _ => Key::O,
        };
        // Each key is the draft of the session's chat entry (OpenAC: the
        // field's text-changed hook sets the entry's draft).
        let typed = press(&mut ui, key, Some(&c.to_string()));
        assert!(
            matches!(
                typed.as_slice(),
                [SessionCommand::ChatEntry(ChatEntryCommand::SetDraft(_))]
            ),
            "{typed:?}"
        );
    }
    // The line goes to the session's chat entry, which routes it with its
    // talk focus.
    let sent = press(&mut ui, Key::Enter, Some("\r"));
    assert!(
        matches!(
            sent.first(),
            Some(SessionCommand::ChatEntry(ChatEntryCommand::Submit(Some(text))))
                if text == "hello"
        ),
        "{sent:?}"
    );
}

/// OpenAC `RetailUiRuntime` (F11 toggles the options window;
/// `MountOptionsPanel` places it at (150, 80) in the nine-piece frame) and
/// `ConfigOptionsPageController` (a row's change is live, then saved):
/// F11 opens the window, a vsync click there reaches the window's settings
/// and the list of sections to save, F11 again closes it.
#[test]
#[ignore = "needs the retail dat files"]
fn f11_opens_the_options_window_and_its_changes_reach_the_window() {
    use ac_ui::panels::options::Section;
    use ac_ui::widgets::Button;
    let dats = dats();
    let size = (1280, 720);
    let settings = ac_host::settings::PlayerSettings::default();
    let dats_for_strings = dats.clone();
    let mut ui = WindowUi::build_with(dats, size, &settings, vec!["1280x720".into()])
        .expect("the UI builds");
    assert!(!ui.host().root.is_window_visible(OPTIONS_WINDOW));
    assert!(ui.run_hotkey(ac_render::ui::window::Hotkey::Options));
    let root = &ui.host().root;
    assert!(root.is_window_visible(OPTIONS_WINDOW));
    let frame = root.window(OPTIONS_WINDOW).unwrap().frame;
    let e = root.element(frame).unwrap();
    // Every window starts at its default place: a main panel is centred.
    assert_eq!(
        (e.left, e.top),
        ((1280.0 - e.width) / 2.0, (720.0 - e.height) / 2.0)
    );

    // To the Configuration tab, then the vsync checkbox.
    let vsync = configuration_vsync_row(&mut ui, dats_for_strings);
    assert!(ui.host().root.widget::<Button>(vsync).unwrap().selected());
    uncheck_row(&mut ui, vsync);
    let commands = ui.drain();
    assert!(commands.is_empty(), "{commands:?}");
    assert!(!ui.settings().display.vsync);
    assert_eq!(ui.take_settings_changes(), [(Section::Display, true)]);

    assert!(ui.run_hotkey(ac_render::ui::window::Hotkey::Options));
    ui.host_mut().pump();
    assert!(ui.drain().is_empty());
    assert!(!ui.host().root.is_window_visible(OPTIONS_WINDOW));
    // Closing without Apply put the change back (live, and to be saved).
    assert!(ui.settings().display.vsync);
    assert_eq!(ui.take_settings_changes(), [(Section::Display, true)]);
}

/// The open options window's Configuration page, its vsync row.
fn configuration_vsync_row(ui: &mut WindowUi, dats: Arc<DatCollection>) -> ac_ui::tree::ElementId {
    options_row(
        ui,
        dats,
        ac_ui::panels::options::OptionsPage::Configuration,
        "ID_Rendering_SyncToDisplayRefresh",
    )
}

/// The options window opened on `page`, its row captioned by string `key`
/// (the options' string table).
fn options_row(
    ui: &mut WindowUi,
    dats: Arc<DatCollection>,
    page: ac_ui::panels::options::OptionsPage,
    key: &str,
) -> ac_ui::tree::ElementId {
    use ac_ui::widgets::Button;
    assert!(ui.open_options_page(page));
    ui.drain();
    let content = ac_ui::content::DatContent::new(dats).unwrap();
    let caption = content
        .strings()
        .resolve(0x2300_0003, ac_ui::strings::compute_hash(key), 0);
    assert!(caption.is_some(), "{key} resolves");
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
    row.unwrap_or_else(|| panic!("the {key} row"))
}

/// Uncheck an options row as a click does.
fn uncheck_row(ui: &mut WindowUi, row: ac_ui::tree::ElementId) {
    set_row(ui, row, false);
}

/// Check or uncheck an options row as a click does.
fn set_row(ui: &mut WindowUi, row: ac_ui::tree::ElementId, checked: bool) {
    use ac_ui::widgets::Button;
    let ui_root = &mut ui.host_mut().root;
    ui_root.with::<Button, _>(row, |b, ui| b.set_selected(ui, row, checked));
    ui_root.send_message(row, ac_ui::root::ElementMessage::Clicked { x: 1, y: 1 });
    ui.host_mut().pump();
}

/// OpenAC `RuntimeSettingsController.ToggleFrameRate` saves only the
/// display and the options window saves only the section it changed:
/// `/framerate` typed while the options window is open survives the
/// window's next change. The window hands its copy of the settings back
/// whole, so it must follow the console's change (an acceptance run saw
/// the frame rate vanish at the next Apply).
#[test]
#[ignore = "needs the retail dat files"]
fn a_console_display_change_survives_the_options_windows_next_change() {
    use ac_runtime::communication::WindowRequest;
    let dats = dats();
    let size = (1280, 720);
    let settings = ac_host::settings::PlayerSettings::default();
    let mut ui = WindowUi::build_with(dats.clone(), size, &settings, vec!["1280x720".into()])
        .expect("the UI builds");
    assert!(ui.run_hotkey(ac_render::ui::window::Hotkey::Options));
    let vsync = configuration_vsync_row(&mut ui, dats);

    let mut scene = scene();
    scene.apply(PresentationEvent::WindowRequest(
        WindowRequest::ToggleFrameRate,
    ));
    ui.feed(&mut scene);
    assert!(ui.settings().display.show_fps);
    ui.take_settings_changes();

    uncheck_row(&mut ui, vsync);
    ui.drain();
    assert!(!ui.settings().display.vsync);
    assert!(ui.settings().display.show_fps, "the frame rate stays on");
}

const SWORD: u32 = 0x8000_0020;

/// The panels' facts: the player holding a sword, the sword on the first
/// shortcut, and `state` otherwise as given.
fn interface(scene: &mut Scene, state: InterfaceState) {
    use ac_core::items::{ClientObject, ItemType, ShortcutEntry};
    let mut player = ClientObject::new(PLAYER);
    player.name = "+Horan".to_owned();
    player.item_type = ItemType::CREATURE;
    player.properties.ints.insert(0x19, 12);
    let mut sword = ClientObject::new(SWORD);
    sword.name = "Sword".to_owned();
    sword.item_type = ItemType::MELEE_WEAPON;
    sword.icon_id = 0x0600_11CB;
    sword.container_id = PLAYER;
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Items(
        ItemsUpdate {
            reset: true,
            objects: vec![player, sword],
            removed: Vec::new(),
            contents: vec![(PLAYER, Some(vec![SWORD]))],
        },
    )));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Player(
        Box::new(PlayerFacts {
            revision: 1,
            numbers: ac_core::player::LocalPlayerState::default(),
            session_seconds: 0.0,
        }),
    )));
    let mut shortcuts = ac_core::items::ShortcutStore::new();
    shortcuts.set(ShortcutEntry {
        index: 0,
        object_id: SWORD,
        spell_id: 0,
    });
    scene.apply(PresentationEvent::Interface(InterfaceEvent::State(
        Box::new(InterfaceState {
            player: PLAYER,
            can_make_request: true,
            shortcuts,
            ..state
        }),
    )));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_item_character_and_magic_panels_mount_feed_and_answer_from_the_dats() {
    use ac_render::ui::window::{Hotkey, session_command};
    use ac_runtime::communication::ClientConfirmation;
    use ac_ui::panels::PanelOutput;
    use ac_ui::panels::character::CharacterTab;
    use ac_ui::panels::toolbar;
    use ac_ui::root::UiOutput;
    use ac_ui::widgets::{ItemList, ItemSlot};

    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = scene();
    interface(&mut scene, InterfaceState::default());
    ui.feed(&mut scene);
    let start = Instant::now();
    ui.frame(start, size);

    // The toolbar shows the sword on its first slot.
    let slot = |ui: &WindowUi| {
        let root = &ui.host().root;
        let frame = root.window(TOOLBAR_WINDOW).unwrap().frame;
        let list = root.find_descendant(frame, toolbar::SLOT_IDS[0]).unwrap();
        let cell = root.widget::<ItemList>(list).unwrap().item(0).unwrap();
        root.widget::<ItemSlot>(cell).unwrap().object_id
    };
    assert_eq!(slot(&ui), SWORD);

    // F12 opens the inventory; F8 the character window in its place (one
    // main panel at a time); F8 again closes it.
    assert!(ui.run_hotkey(Hotkey::Inventory));
    let commands = ui.drain();
    assert!(commands.is_empty(), "{commands:?}");
    ui.feed(&mut scene);
    assert!(ui.host().root.is_window_visible(INVENTORY_WINDOW));
    assert!(ui.run_hotkey(Hotkey::Character(CharacterTab::Attributes)));
    ui.drain();
    ui.feed(&mut scene);
    let root = &ui.host().root;
    assert!(root.is_window_visible(CHARACTER_WINDOW));
    assert!(!root.is_window_visible(INVENTORY_WINDOW));
    let place = |name| {
        let f = root.window(name).unwrap().frame;
        let e = root.element(f).unwrap();
        (e.left, e.top)
    };
    assert_eq!(place(CHARACTER_WINDOW), place(INVENTORY_WINDOW));
    let list = ui.frame(start + Duration::from_millis(16), size);

    // Every texture drawn decodes: dat surfaces and the composed icons.
    let batch = UiBatch::build(&list);
    assert_eq!(batch.skipped_zero_ids, 0);
    let mut surfaces = ui.surfaces();
    let mut runtime = 0;
    for run in &batch.runs {
        if let UiTexture::Named(t) = run.key.texture {
            let decoded = surfaces.decode(t);
            match t {
                TextureRef::Surface(id) => {
                    assert!(decoded.is_some(), "{id:#010x} decodes");
                }
                TextureRef::Runtime(_) => runtime += usize::from(decoded.is_some()),
            }
        }
    }

    assert!(runtime > 0, "the sword's composed icon is drawn");
    assert!(ui.run_hotkey(Hotkey::Character(CharacterTab::Attributes)));
    ui.drain();
    assert!(!ui.host().root.is_window_visible(CHARACTER_WINDOW));

    // The session's question shows as the dialog; yes answers it.
    interface(
        &mut scene,
        InterfaceState {
            confirmations: vec![ClientConfirmation::Server {
                confirmation_type: 7,
                context_id: 42,
                message: "Ready?".to_owned(),
            }],
            ..InterfaceState::default()
        },
    );
    ui.feed(&mut scene);
    let modal = ui.host().root.modal.expect("the dialog is modal");
    // The UI probe lists the open dialog and its buttons, as OpenAC's
    // probe dump (the whole tree) does.
    let probe = ui.probe();
    assert!(
        probe
            .iter()
            .any(|l| l.starts_with("ui-probe: screen dialog ")),
        "{probe:?}"
    );
    let accept_line = format!(
        "ui-probe: button dialog {:#010x} ",
        ac_ui::panels::dialog::ACCEPT_BUTTON_ID
    );
    assert!(
        probe.iter().any(|l| l.starts_with(&accept_line)),
        "{probe:?}"
    );
    let accept = ui
        .host()
        .root
        .find_descendant(modal, ac_ui::panels::dialog::ACCEPT_BUTTON_ID)
        .expect("the yes button");
    ui.host_mut()
        .root
        .fire_event(ac_ui::event::kind::CLICK, accept, None);
    ui.host_mut().pump();
    let commands = ui.drain();
    assert!(
        matches!(
            commands.as_slice(),
            [SessionCommand::AnswerConfirmation {
                confirmation: ClientConfirmation::Server { context_id: 42, .. },
                accepted: true
            }]
        ),
        "{commands:?}"
    );
    assert!(ui.host().root.modal.is_none());

    // A training request is asked first; no sends nothing.
    let asked = ui.handle(vec![UiOutput::Panel(PanelOutput::TrainSkill {
        skill: 33,
        credits: 4,
    })]);
    assert!(asked.is_empty());
    let modal = ui.host().root.modal.expect("the training question");
    let reject = ui
        .host()
        .root
        .find_descendant(modal, ac_ui::panels::dialog::REJECT_BUTTON_ID)
        .unwrap();
    ui.host_mut()
        .root
        .fire_event(ac_ui::event::kind::CLICK, reject, None);
    ui.host_mut().pump();
    assert!(ui.drain().is_empty());
    assert!(session_command(&PanelOutput::TogglePanel(7)).is_none());
    let notices = ui.take_notices();
    assert!(notices.is_empty(), "{notices:?}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_selected_items_mana_query_goes_out_and_its_answer_fills_the_strips_bar() {
    // OpenAC `SelectedObjectController` bound by `RetailUiRuntime` to
    // `WorldSession.SendQueryItemMana` and `ItemManaState` (its
    // `ItemManaChanged` event and `GetManaPercent`).
    use ac_core::items::ItemManaChange;
    use ac_runtime::inventory::{ItemCommand, ObjectRef};
    use ac_runtime::session::interface::SelectedMana;
    use ac_ui::panels::selected_object::MANA_METER_ID;
    use ac_ui::widgets::Meter;

    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = scene();
    interface(&mut scene, InterfaceState::default());
    let start = Instant::now();
    let mut frames = 0;
    let mut step = |ui: &mut WindowUi, scene: &mut Scene| {
        frames += 1;
        ui.feed(scene);
        ui.frame(start + Duration::from_millis(16 * frames), size);
        ui.drain()
    };
    step(&mut ui, &mut scene);
    // The mana queries among the session commands, by the item asked about.
    let queries = |commands: &[SessionCommand]| {
        commands
            .iter()
            .filter_map(|c| match c {
                SessionCommand::Item(ItemCommand::QueryMana(ObjectRef::Guid(g))) => Some(*g),
                SessionCommand::Item(ItemCommand::QueryMana(other)) => {
                    panic!("a mana query by {other:?}")
                }
                _ => None,
            })
            .collect::<Vec<_>>()
    };
    let select = |scene: &mut Scene, guid| {
        let view = *scene.player_view().unwrap();
        scene.apply(PresentationEvent::Player(PlayerView {
            selected: guid,
            ..view
        }));
    };
    let meter = |ui: &WindowUi| {
        let root = &ui.host().root;
        let toolbar = root.window(TOOLBAR_WINDOW).unwrap().frame;
        let m = root.find_descendant(toolbar, MANA_METER_ID).unwrap();
        (
            root.element(m).unwrap().visible(),
            root.widget::<Meter>(m).unwrap().fill.unwrap_or(0.0),
        )
    };

    // Selecting the sword in the pack asks for its mana.
    select(&mut scene, Some(SWORD));
    let commands = step(&mut ui, &mut scene);
    assert_eq!(queries(&commands), [SWORD]);
    assert!(!meter(&ui).0);

    // The answer comes in the session's stream: the bar shows, filled from
    // the selection's mana as the session knows it.
    interface(
        &mut scene,
        InterfaceState {
            selected_mana: Some(SelectedMana {
                object_id: SWORD,
                fraction: 0.625,
            }),
            ..InterfaceState::default()
        },
    );
    scene.apply(PresentationEvent::Interface(InterfaceEvent::ItemMana(
        ItemManaChange {
            item_id: SWORD,
            fraction: 0.625,
            valid: true,
        },
    )));
    let commands = step(&mut ui, &mut scene);
    assert!(queries(&commands).is_empty(), "{commands:?}");
    assert_eq!(meter(&ui), (true, 0.625));

    // Selecting away from a shown bar closes the query.
    select(&mut scene, None);
    let commands = step(&mut ui, &mut scene);
    assert_eq!(queries(&commands), [0]);
    assert!(!meter(&ui).0);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_examination_follows_a_selection_changed_away_and_back_in_one_frame() {
    // OpenAC `AppraisalUiController.HandleSelectionChanged` runs on every
    // selection change: away and back before the window's next frame
    // examines both, in order, though the frame ends on the first again.
    use ac_runtime::inventory::{ItemCommand, ObjectRef};
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = scene();
    interface(&mut scene, InterfaceState::default());
    let view = *scene.player_view().unwrap();
    scene.apply(PresentationEvent::Player(PlayerView {
        selected: Some(SWORD),
        ..view
    }));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Selection(
        Some(SWORD),
    )));
    ui.feed(&mut scene);
    ui.host_mut().root.show_window(EXAMINATION_WINDOW);
    ui.host_mut().pump();
    let _ = ui.drain();
    for selected in [Some(DRUDGE), Some(SWORD)] {
        scene.apply(PresentationEvent::Interface(InterfaceEvent::Selection(
            selected,
        )));
    }
    ui.feed(&mut scene);
    let appraised: Vec<u32> = ui
        .drain()
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::Item(ItemCommand::Appraise(ObjectRef::Guid(g))) => Some(g),
            _ => None,
        })
        .collect();
    assert_eq!(appraised, [DRUDGE, SWORD]);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_spell_bar_cannot_cast_a_targeted_spell_without_a_fitting_target() {
    // OpenAC `SpellcastingUiController.ComputeSpellCastState` over
    // `RuntimeSpellCastState.EvaluateCastGate`, in the window: the cast
    // button is disabled with the retail reason while the selection does
    // not fit the spell.
    use ac_core::spells::{SpellTable, Spellbook};
    use ac_ui::panels::magic::CAST_BUTTON_ID;
    use ac_ui::widgets::Button;

    let size = (1600, 900);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = scene();
    interface(
        &mut scene,
        InterfaceState {
            combat_mode: ac_core::combat::CombatMode::MAGIC,
            ..InterfaceState::default()
        },
    );
    // A war spell aimed at creatures, the first favourite.
    let mut book = Spellbook::new(Some(Arc::new(SpellTable::load_from_csv(
        "Spell ID,Name,School,TargetMask [Hex]\n42,Flame Bolt I,War Magic,0x10\n",
    ))));
    book.on_spell_learned(42, 1.0);
    book.set_favorite(0, 0, 42);
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Player(
        Box::new(PlayerFacts {
            revision: 2,
            numbers: ac_core::player::LocalPlayerState::new(Some(book)),
            session_seconds: 0.0,
        }),
    )));
    ui.feed(&mut scene);
    ui.host_mut().root.show_window(COMBAT_WINDOW);
    let start = Instant::now();
    let mut frames = 0;
    let mut step = |ui: &mut WindowUi, scene: &mut Scene| {
        frames += 1;
        ui.feed(scene);
        ui.frame(start + Duration::from_millis(50 * frames), size);
        ui.drain();
    };
    step(&mut ui, &mut scene);
    // The bar selects its first favourite.
    assert!(ui.handle_input_action(ac_ui::input::InputAction::CombatFirstSpell));
    step(&mut ui, &mut scene);
    let cast = |ui: &WindowUi| {
        let root = &ui.host().root;
        let frame = root.window(COMBAT_WINDOW).unwrap().frame;
        let b = root.find_descendant(frame, CAST_BUTTON_ID).unwrap();
        (
            root.element(b).unwrap().enabled(),
            root.widget::<Button>(b).unwrap().tooltip_text.clone(),
        )
    };
    assert_eq!(
        cast(&ui),
        (
            false,
            Some("You must select a target for Flame Bolt I".to_owned())
        )
    );

    // The sword is no creature.
    let view = *scene.player_view().unwrap();
    scene.apply(PresentationEvent::Player(PlayerView {
        selected: Some(SWORD),
        ..view
    }));
    step(&mut ui, &mut scene);
    assert_eq!(
        cast(&ui),
        (
            false,
            Some("You must select an appropriate target for Flame Bolt I".to_owned())
        )
    );
}

const VENDOR: u32 = 0x8000_0030;
const BREAD: u32 = 0x8000_0031;
const PARTNER: u32 = 0x5000_0002;

/// The social facts: in the world, no fellowship or allegiance.
fn social(in_world: bool) -> ac_runtime::session::interface::SocialFacts {
    ac_runtime::session::interface::SocialFacts {
        in_world,
        options: ac_runtime::character::CharacterOptions::new(),
        friends: ac_core::social::FriendsState::new(),
        squelch: ac_core::social::SquelchState::new(),
        fellowship: ac_runtime::gameplay::FellowshipView::default(),
        allegiance: None,
        allegiance_rank: 0,
        allegiance_revision: 0,
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_trade_vendor_social_and_journal_panels_mount_feed_and_answer_from_the_dats() {
    use ac_core::items::{ItemType, VendorShopItem, VendorShopProfile};
    use ac_render::ui::window::{
        Hotkey, JOURNAL_WINDOW, SOCIAL_WINDOW, SocialPage, TRADE_WINDOW, VENDOR_WINDOW,
    };
    use ac_runtime::gameplay::{FellowshipCommand, GameplayCommand};
    use ac_runtime::inventory::shop::VendorListsView;
    use ac_runtime::inventory::{TradeView, VendorView, VendorViewReason};
    use ac_runtime::session::interface::ContractFacts;
    use ac_ui::panels::PanelOutput;
    use ac_ui::panels::social::SocialPanelController;
    use ac_ui::root::UiOutput;

    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    for name in [SOCIAL_WINDOW, JOURNAL_WINDOW, VENDOR_WINDOW, TRADE_WINDOW] {
        assert!(ui.mounted().contains(&name), "{name} mounted");
        assert!(!ui.host().root.is_window_visible(name), "{name} hidden");
    }
    let mut scene = scene();
    interface(&mut scene, InterfaceState::default());
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Social(
        Box::new(social(true)),
    )));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Contracts(
        Box::new(ContractFacts {
            contracts: Vec::new(),
            display_contract_id: 0,
            revision: 1,
            catalog: Default::default(),
        }),
    )));
    ui.feed(&mut scene);
    let start = Instant::now();
    ui.frame(start, size);

    // F4 opens the social panel on the fellowship page, which declares
    // itself open to the session; F4 again closes it, and F3 opens the
    // allegiance page.
    assert!(ui.run_hotkey(Hotkey::Social(SocialPage::Fellowship)));
    let commands = ui.drain();
    assert!(ui.host().root.is_window_visible(SOCIAL_WINDOW));
    assert!(
        commands.iter().any(|c| matches!(
            c,
            SessionCommand::Gameplay(GameplayCommand::Fellowship(
                FellowshipCommand::SetPanelOpen(true)
            ))
        )),
        "{commands:?}"
    );
    ui.feed(&mut scene);
    assert!(ui.run_hotkey(Hotkey::Social(SocialPage::Fellowship)));
    ui.drain();
    assert!(!ui.host().root.is_window_visible(SOCIAL_WINDOW));
    assert!(ui.run_hotkey(Hotkey::Social(SocialPage::Allegiance)));
    ui.drain();
    ui.feed(&mut scene);
    assert!(ui.host().root.is_window_visible(SOCIAL_WINDOW));
    let on_allegiance = ui
        .host_mut()
        .with_controller::<SocialPanelController, _>(SOCIAL_WINDOW, |c, ui| {
            c.is_showing_allegiance(ui)
        });
    assert_eq!(on_allegiance, Some(true));

    // The toolbar's social and journal buttons are live and toggle their
    // panels.
    let buttons = ui
        .host_mut()
        .with_controller::<ac_ui::panels::toolbar::ToolbarWindowController, _>(
            ac_render::ui::window::TOOLBAR_WINDOW,
            |c, _| c.toolbar.panel_buttons().to_vec(),
        )
        .unwrap();
    for panel in [
        ac_ui::panels::panel_id::SOCIAL,
        ac_ui::panels::panel_id::JOURNAL,
    ] {
        let (_, button) = buttons
            .iter()
            .find(|(p, _)| *p == panel)
            .unwrap_or_else(|| panic!("a toolbar button for panel {panel}"));
        assert!(ui.host().root.element(*button).unwrap().enabled);
    }

    // The journal opens on its contracts page in the social panel's place
    // (one main panel at a time).
    assert!(ui.open_contracts());
    ui.drain();
    ui.feed(&mut scene);
    assert!(ui.host().root.is_window_visible(JOURNAL_WINDOW));
    assert!(!ui.host().root.is_window_visible(SOCIAL_WINDOW));

    // A trade opening shows the trade window; its closing hides it.
    let trade = |open: bool, revision| TradeView {
        open,
        partner_guid: PARTNER,
        partner_name: Some("+Acdream".into()),
        revision,
        ..TradeView::default()
    };
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Trade(
        Box::new(trade(true, 1)),
    )));
    ui.feed(&mut scene);
    assert!(ui.host().root.is_window_visible(TRADE_WINDOW));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Trade(
        Box::new(trade(false, 2)),
    )));
    ui.feed(&mut scene);
    assert!(!ui.host().root.is_window_visible(TRADE_WINDOW));

    // A shop opening shows the vendor window with its listing.
    let listing = VendorShopItem {
        item_guid: BREAD,
        stack_size: -1,
        name: Some("Bread".into()),
        item_type: Some(ItemType::FOOD.bits()),
        icon_id: 0x0600_11CB,
        value: Some(5),
        ..VendorShopItem::default()
    };
    let shop = VendorView {
        vendor_guid: VENDOR,
        name: Some("Grocer".into()),
        profile: VendorShopProfile {
            merchandise_item_types: ItemType::FOOD.bits(),
            sell_price: 1.5,
            buy_price: 0.5,
            ..VendorShopProfile::default()
        },
        items: vec![listing],
        lists: VendorListsView::default(),
        reason: VendorViewReason::Opened,
    };
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Vendor(Some(
        Box::new(shop),
    ))));
    ui.feed(&mut scene);
    assert!(ui.host().root.is_window_visible(VENDOR_WINDOW));
    let list = ui.frame(start + Duration::from_millis(16), size);
    let batch = UiBatch::build(&list);
    assert_eq!(batch.skipped_zero_ids, 0);
    let mut surfaces = ui.surfaces();
    for run in &batch.runs {
        if let UiTexture::Named(t @ TextureRef::Surface(id)) = run.key.texture {
            assert!(surfaces.decode(t).is_some(), "{id:#010x} decodes");
        }
    }

    // The shop closing hides it; the vendor's partial-lift refusal puts
    // the split quantity back (answered by the window, no command).
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Vendor(None)));
    ui.feed(&mut scene);
    assert!(!ui.host().root.is_window_visible(VENDOR_WINDOW));
    ui.drain();
    let none = ui.handle(vec![UiOutput::Panel(PanelOutput::ResetSplitQuantity {
        stack: 10,
    })]);
    assert!(none.is_empty());

    // Leaving the world and entering again: the social panel's
    // declarations are made again.
    assert!(ui.run_hotkey(Hotkey::Social(SocialPage::Fellowship)));
    ui.drain();
    ui.feed(&mut scene);
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Social(
        Box::new(social(false)),
    )));
    ui.feed(&mut scene);
    ui.drain();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Social(
        Box::new(social(true)),
    )));
    ui.feed(&mut scene);
    let commands = ui.drain();
    assert!(
        commands.iter().any(|c| matches!(
            c,
            SessionCommand::Gameplay(GameplayCommand::Fellowship(
                FellowshipCommand::SetPanelOpen(true)
            ))
        )),
        "{commands:?}"
    );
    let notices = ui.take_notices();
    assert!(notices.is_empty(), "{notices:?}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_status_box_frame_rate_strip_indicators_and_effects_work_in_the_window() {
    use ac_core::spells::{ActiveEnchantmentRecord, SpellTable, Spellbook};
    use ac_render::ui::indicator_panels::{INDICATORS_WINDOW, POSITIVE_EFFECTS_WINDOW};
    use ac_runtime::communication::WindowRequest;
    use ac_runtime::session::interface::SelectedHealth;
    use ac_ui::input::InputAction;
    use ac_ui::panels::indicators as ind;
    use ac_ui::widgets::{ItemList, Text};

    let dats = dats();
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats, size).expect("the UI builds");
    assert!(ui.take_notices().is_empty());
    let mut scene = scene();
    // A status line from the session shows at the top of the screen.
    scene.apply(PresentationEvent::StatusText(
        "You can't jump while in the air".into(),
    ));
    // The player's numbers with a helpful enchantment.
    let mut book = Spellbook::new(Some(Arc::new(SpellTable::load_from_csv(
        "Spell ID,Name,Flags [Hex]\n42,Boon,0x4\n",
    ))));
    book.on_enchantment_added(ActiveEnchantmentRecord {
        bucket: 1,
        ..ActiveEnchantmentRecord::new(42, 1, 3600.0, PLAYER)
    });
    // The drudge is selected, its health streamed.
    interface(
        &mut scene,
        InterfaceState {
            player: PLAYER,
            selected_health: Some(SelectedHealth {
                object_id: DRUDGE,
                percent: Some(0.5),
            }),
            ..InterfaceState::default()
        },
    );
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Player(
        Box::new(PlayerFacts {
            revision: 2,
            numbers: ac_core::player::LocalPlayerState::new(Some(book)),
            session_seconds: 0.0,
        }),
    )));
    scene.apply(PresentationEvent::Player(PlayerView {
        selected: Some(DRUDGE),
        ..*scene.player_view().unwrap()
    }));
    scene.apply(PresentationEvent::WindowRequest(
        WindowRequest::ToggleFrameRate,
    ));
    // `/render radius 12`, `/render fov 70`: the display settings.
    scene.apply(PresentationEvent::WindowRequest(
        WindowRequest::SetLandscapeRadius(12),
    ));
    scene.apply(PresentationEvent::WindowRequest(
        WindowRequest::SetFieldOfView(70),
    ));
    ui.feed(&mut scene);
    assert_eq!(ui.settings().display.landscape_draw_distance, 12);
    assert_eq!(ui.settings().display.field_of_view, 70.0);
    let start = Instant::now();
    ui.frame(start, size);
    ui.frame(start + Duration::from_millis(50), size);

    let root = &ui.host().root;
    let spew_lines: Vec<String> = root
        .children(root.root())
        .into_iter()
        .filter(|&c| {
            root.element(c)
                .is_some_and(|e| e.name.as_deref() == Some("SpewBox"))
        })
        .flat_map(|c| root.widget::<Text>(c).unwrap().content_lines())
        .map(|l| l.text)
        .collect();
    assert_eq!(spew_lines, ["You can't jump while in the air"]);

    // /framerate showed the display.
    assert!(ui.settings().display.show_fps);

    // The selected-object strip names the drudge and shows its health.
    let toolbar = root.window(TOOLBAR_WINDOW).unwrap().frame;
    let named = root
        .find_descendant(toolbar, ac_ui::panels::selected_object::NAME_ID)
        .unwrap();
    let name: Vec<String> = root
        .children(named)
        .into_iter()
        .filter_map(|c| root.widget::<Text>(c))
        .flat_map(Text::content_lines)
        .map(|l| l.text)
        .collect();
    assert_eq!(name, ["Drudge Skulker"]);
    let health = root
        .find_descendant(toolbar, ac_ui::panels::selected_object::HEALTH_METER_ID)
        .unwrap();
    assert!(root.element(health).unwrap().visible());

    // The helpful-effects indicator is lit; the key map's action opens the
    // positive effects panel with the enchantment's row.
    let bar = root.window(INDICATORS_WINDOW).unwrap().frame;
    let helpful = root.find_descendant(bar, ind::HELPFUL_BUTTON_ID).unwrap();
    assert!(root.element(helpful).unwrap().enabled());
    assert!(ui.handle_input_action(InputAction::TogglePositiveMagicPanel));
    ui.handle(Vec::new());
    ui.feed(&mut scene);
    ui.feed(&mut scene);
    let root = &ui.host().root;
    assert!(root.is_window_visible(POSITIVE_EFFECTS_WINDOW));
    let effects = root.window(POSITIVE_EFFECTS_WINDOW).unwrap().frame;
    let list_host = root.find_descendant(effects, ind::LIST_ID).unwrap();
    let list = root
        .children(list_host)
        .into_iter()
        .find(|c| root.widget::<ItemList>(*c).is_some())
        .unwrap();
    assert_eq!(root.widget::<ItemList>(list).unwrap().cells().len(), 1);

    // A charged jump shows the power bar; the jump ends and it hides.
    let view = *scene.player_view().unwrap();
    scene.apply(PresentationEvent::Player(PlayerView {
        jump_charge: Some(0.5),
        ..view
    }));
    ui.feed(&mut scene);
    assert!(
        ui.host()
            .root
            .is_window_visible(ac_render::ui::status::JUMP_POWERBAR_WINDOW)
    );
    scene.apply(PresentationEvent::Player(PlayerView {
        jump_charge: None,
        ..view
    }));
    ui.feed(&mut scene);
    assert!(
        !ui.host()
            .root
            .is_window_visible(ac_render::ui::status::JUMP_POWERBAR_WINDOW)
    );

    // The compass toggles the radar.
    assert!(ui.handle_input_action(InputAction::ToggleCompass));
    assert!(!ui.host().root.is_window_visible(RADAR_WINDOW));
    assert!(ui.handle_input_action(InputAction::ToggleCompass));
    assert!(ui.host().root.is_window_visible(RADAR_WINDOW));

    // The page keys (OpenAC `HandleInputAction`): a page opens its window,
    // the same key again on that page closes it; the journal's keys only
    // open; the message keys answer in the chat.
    let shown = |ui: &WindowUi, name: &str| ui.host().root.is_window_visible(name);
    for (action, window) in [
        (InputAction::ToggleFriendsPage, "social-panel"),
        (InputAction::ToggleCharacterTitlesPage, "character"),
        (InputAction::ToggleMapPage, "map-house"),
        (InputAction::ToggleHousePage, "map-house"),
        (
            InputAction::ToggleConfigurationPage,
            ac_render::ui::options::OPTIONS_WINDOW,
        ),
    ] {
        assert!(!shown(&ui, window), "{action:?}");
        assert!(ui.handle_input_action(action), "{action:?}");
        assert!(shown(&ui, window), "{action:?}");
        assert!(ui.handle_input_action(action), "{action:?}");
        assert!(!shown(&ui, window), "{action:?}");
    }
    assert!(ui.handle_input_action(InputAction::ToggleContractsPage));
    assert!(shown(&ui, "journal"));
    assert!(ui.handle_input_action(InputAction::ToggleQuestJournalPage));
    assert!(shown(&ui, "journal"));
    for action in [
        InputAction::ToggleHelp,
        InputAction::TogglePluginManager,
        InputAction::ToggleAbuseReportingPanel,
        InputAction::ToggleUrgentAssistancePanel,
    ] {
        assert!(ac_render::ui::status::is_interface_action(action));
        assert!(ui.handle_input_action(action), "{action:?}");
    }
}

// ── The combat window's horizontal resize ────────────────────────────────
// OpenAC `tests/AcDream.App.Tests/UI/Layout/CombatWindowResizeTests.cs`, in
// the window on the retail dats: the spell bars' window is mounted with
// its horizontal resize, placed at the left edge, and dragged by its right
// edge.

struct CombatFixture {
    ui: WindowUi,
    scene: Scene,
    size: (u32, u32),
    start: Instant,
    frames: u64,
}

impl CombatFixture {
    fn new(favorites: u32) -> Self {
        use ac_core::spells::Spellbook;
        let dats = dats();
        let size = (1600, 900);
        let ui = WindowUi::build(dats, size).expect("the UI builds");
        let mut scene = scene();
        let mut book = Spellbook::new(None);
        for id in 1..=favorites {
            book.on_spell_learned(id, 1.0);
            book.set_favorite(0, id as usize - 1, id);
        }
        scene.apply(PresentationEvent::Interface(InterfaceEvent::Player(
            Box::new(PlayerFacts {
                revision: 2,
                numbers: ac_core::player::LocalPlayerState::new(Some(book)),
                session_seconds: 0.0,
            }),
        )));
        // The spell bars show in magic mode (the attack controls in melee
        // and missile).
        scene.apply(PresentationEvent::Interface(InterfaceEvent::State(
            Box::new(InterfaceState {
                combat_mode: ac_core::combat::CombatMode::MAGIC,
                ..InterfaceState::default()
            }),
        )));
        let mut f = Self {
            ui,
            scene,
            size,
            start: Instant::now(),
            frames: 0,
        };
        f.ui.feed(&mut f.scene);
        f.ui.host_mut().root.show_window(COMBAT_WINDOW);
        let frame = f.frame_id();
        f.ui.host_mut().root.element_mut(frame).unwrap().left = 0.0;
        f.step();
        f
    }

    fn step(&mut self) {
        self.frames += 1;
        self.ui.feed(&mut self.scene);
        self.ui.frame(
            self.start + Duration::from_millis(50 * self.frames),
            self.size,
        );
    }

    fn frame_id(&self) -> ac_ui::ElementId {
        self.ui.host().root.window(COMBAT_WINDOW).unwrap().frame
    }

    fn rect(&self) -> (f32, f32, f32, f32) {
        let e = self.ui.host().root.element(self.frame_id()).unwrap();
        (e.left, e.top, e.width, e.height)
    }

    fn find(
        &self,
        under: ac_ui::ElementId,
        wanted: impl Fn(ac_ui::ElementId) -> bool,
    ) -> Option<ac_ui::ElementId> {
        let root = &self.ui.host().root;
        let mut stack = vec![under];
        while let Some(e) = stack.pop() {
            if wanted(e) {
                return Some(e);
            }
            let mut children = root.children(e);
            children.reverse();
            stack.extend(children);
        }
        None
    }

    /// The favourite list of the bar group `group`.
    fn list_of(&self, group: u32) -> ac_ui::ElementId {
        let root = &self.ui.host().root;
        let g = self
            .find(self.frame_id(), |e| {
                root.element(e).unwrap().dat_element_id == group
            })
            .unwrap();
        self.find(g, |e| root.widget::<ac_ui::widgets::ItemList>(e).is_some())
            .unwrap()
    }

    fn list(&self) -> ac_ui::ElementId {
        self.list_of(0x1000_00AA)
    }

    fn list_width(&self) -> f32 {
        self.ui.host().root.element(self.list()).unwrap().width
    }

    fn scroll(&self) -> (i32, i32) {
        let l = self
            .ui
            .host()
            .root
            .widget::<ac_ui::widgets::ItemList>(self.list())
            .unwrap();
        (l.scroll.scroll_y(), l.scroll.max_scroll())
    }

    fn cells(&self, list: ac_ui::ElementId) -> Vec<ac_ui::ElementId> {
        self.ui
            .host()
            .root
            .widget::<ac_ui::widgets::ItemList>(list)
            .unwrap()
            .cells()
            .to_vec()
    }

    fn visible_slots(&self) -> usize {
        let root = &self.ui.host().root;
        self.cells(self.list())
            .into_iter()
            .filter(|c| root.element(*c).unwrap().visible())
            .count()
    }

    fn item_visible(&self, index: usize) -> bool {
        let c = self.cells(self.list())[index];
        self.ui.host().root.element(c).unwrap().visible()
    }

    fn item_selected(&self, index: usize) -> bool {
        let c = self.cells(self.list())[index];
        self.ui
            .host()
            .root
            .widget::<ac_ui::widgets::CatalogSlot>(c)
            .unwrap()
            .selected
    }

    fn resize_by(&mut self, pixels: i32) {
        use ac_ui::event::MouseButton;
        let (l, t, w, h) = self.rect();
        let (x, y) = ((l + w - 2.0) as i32, (t + h / 2.0) as i32);
        let host = self.ui.host_mut();
        host.mouse_down(MouseButton::Left, x, y, 0);
        host.mouse_move(x + pixels, y + 20);
        host.mouse_up(MouseButton::Left, x + pixels, y + 20, 0);
        self.step();
        self.step();
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_wider_combat_window_shows_more_favourites_and_keeps_its_height() {
    let mut f = CombatFixture::new(30);
    let height = f.rect().3;
    assert_eq!(f.rect().2, 747.0);
    f.resize_by(6 * 32);
    assert_eq!(f.rect().2, 747.0 + 6.0 * 32.0);
    assert_eq!(f.rect().3, height);
    assert_eq!(f.list_width(), 24.0 * 32.0);
    assert_eq!(f.visible_slots(), 24);
    assert_eq!(f.scroll().1, 6 * 32);
    assert!(f.item_visible(23));
    assert!(!f.item_visible(24));
}

#[test]
#[ignore = "needs the retail dat files"]
fn resizing_keeps_the_selected_favourite_and_clamps_the_scroll() {
    let mut f = CombatFixture::new(30);
    assert!(f.ui.handle_input_action(ac_ui::input::InputAction::CombatLastSpell));
    f.step();
    f.resize_by(6 * 32);
    assert_eq!(f.scroll().0, 192);
    assert!(f.item_visible(29) && f.item_selected(29));
    f.resize_by(-15 * 32);
    assert_eq!(f.list_width(), 9.0 * 32.0);
    assert_eq!(f.scroll().0, 672);
    assert!(f.item_visible(29) && f.item_selected(29));
    f.resize_by(21 * 32);
    assert_eq!(f.scroll(), (0, 0));
    assert_eq!(f.visible_slots(), 30);
}

#[test]
#[ignore = "needs the retail dat files"]
fn empty_slots_follow_the_width_on_every_bar() {
    use ac_ui::tree::Widget;
    let mut f = CombatFixture::new(2);
    f.resize_by(6 * 32);
    let root_empty = |f: &CombatFixture, list| {
        f.cells(list)
            .into_iter()
            .filter(|c| {
                f.ui.host()
                    .root
                    .widget::<ac_ui::widgets::CatalogSlot>(*c)
                    .is_some_and(|s| s.is_empty_slot())
            })
            .count()
    };
    let list = f.list();
    assert_eq!(f.cells(list).len(), 24);
    assert_eq!(root_empty(&f, list), 22);
    assert!(f.ui.handle_input_action(ac_ui::input::InputAction::CombatNextSpellTab));
    f.step();
    let second = f.list_of(0x1000_00AB);
    assert_eq!(f.cells(second).len(), 24);
    assert_eq!(root_empty(&f, second), 24);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_resize_keeps_nine_slots_and_stops_at_the_screen_edge() {
    let mut f = CombatFixture::new(30);
    f.resize_by(-1000);
    assert_eq!(f.rect().2, 459.0);
    assert_eq!(f.list_width(), 9.0 * 32.0);
    f.resize_by(10_000);
    let (l, _, w, _) = f.rect();
    assert_eq!(l + w, 1600.0);
    let frame = f.ui.host().root.element(f.frame_id()).unwrap();
    assert!(!frame.resize_y);
    assert_eq!(
        frame.resizable_edges,
        ac_ui::geometry::ResizeEdges::LEFT | ac_ui::geometry::ResizeEdges::RIGHT
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_panel_catalog_names_the_options_and_social_windows() {
    // OpenAC `tests/AcDream.App.Tests/UI/RetailPanelCatalogTests.cs`: the
    // panel ids (the toolbar buttons' authored property), the windows they
    // name and back, and the toolbar's options button.
    use ac_ui::panels::panel_id;
    use ac_ui::panels::toolbar::ToolbarWindowController;
    assert_eq!(panel_id::OPTIONS, 10);
    assert_eq!(panel_id::SOCIAL, 12);
    let mut ui = WindowUi::build(dats(), (1280, 720)).expect("the UI builds");
    let panels = ui.main_panels();
    assert_eq!(panels.window_of(panel_id::OPTIONS), Some(OPTIONS_WINDOW));
    assert_eq!(panels.panel_of(OPTIONS_WINDOW), Some(panel_id::OPTIONS));
    assert_eq!(panels.window_of(panel_id::SOCIAL), Some("social-panel"));
    assert_eq!(panels.panel_of("social-panel"), Some(panel_id::SOCIAL));
    let buttons: Vec<u32> = ui
        .host_mut()
        .with_controller::<ToolbarWindowController, _>(TOOLBAR_WINDOW, |c, _| {
            c.toolbar.panel_buttons().iter().map(|(p, _)| *p).collect()
        })
        .unwrap();
    // OpenAC's toolbar list leaves the social panel out only when syncing
    // button states; its button binds like the others (`BindPanelButtons`).
    assert!(buttons.contains(&panel_id::OPTIONS));
}

#[test]
#[ignore = "needs the retail dat files"]
fn hovering_an_authored_tooltip_shows_its_popup_after_the_delay() {
    // OpenAC `RetailUiRuntime.MountTooltips` with the default settings
    // (tooltips on, 0.25 s): the paperdoll's drag mask authors its popup
    // and text.
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = scene();
    ui.feed(&mut scene);
    assert!(ui.handle_input_action(ac_ui::input::InputAction::ToggleInventoryPanel));
    let start = Instant::now();
    ui.frame(start, size);
    let frame = ui.host().root.window(INVENTORY_WINDOW).unwrap().frame;
    let mask = {
        let root = &ui.host().root;
        let mut stack = vec![frame];
        let mut found = None;
        while let Some(e) = stack.pop() {
            if root.element(e).unwrap().dat_element_id == 0x1000_01D6 {
                found = Some(e);
                break;
            }
            stack.extend(root.children(e));
        }
        found.expect("the paperdoll's drag mask")
    };
    let (x, y) = {
        let root = &ui.host().root;
        let (x, y) = root.screen_position(mask);
        let e = root.element(mask).unwrap();
        ((x + e.width / 2.0) as i32, (y + e.height / 2.0) as i32)
    };
    ui.host_mut().mouse_move(x, y);
    ui.frame(start + Duration::from_millis(50), size);
    assert!(
        ui.host_mut().tooltips_mut().unwrap().popup().is_none(),
        "not before the delay"
    );
    ui.frame(start + Duration::from_millis(400), size);
    let popup = ui.host_mut().tooltips_mut().unwrap().popup();
    let popup = popup.expect("the tooltip shows after the delay");
    let root = &ui.host().root;
    assert!(root.element(popup).unwrap().visible());
    let mut stack = vec![popup];
    let mut text = String::new();
    while let Some(e) = stack.pop() {
        if let Some(t) = root.widget::<ac_ui::widgets::Text>(e) {
            for l in t.content_lines() {
                text.push_str(&l.text);
                text.push(' ');
            }
        }
        stack.extend(root.children(e));
    }
    assert!(
        text.contains("Drag clothing and armor"),
        "the popup carries the authored text: {text:?}"
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_window_decides_the_retail_cursor_from_the_ui_and_the_session() {
    // OpenAC `CursorFeedbackController.Update` in the window: the default
    // arrow over nothing, the found arrow over the toolbar's backpack (the
    // player), the busy cursor while a use is in flight.
    use ac_ui::cursor::{GlobalCursorKind, resolve_global_cursor};
    let size = (1280, 720);
    let dats = dats();
    let mut ui = WindowUi::build(dats.clone(), size).expect("the UI builds");
    let mut scene = scene();
    interface(&mut scene, InterfaceState::default());
    ui.feed(&mut scene);
    let start = Instant::now();
    ui.frame(start, size);
    ui.host_mut().mouse_move(640, 300);
    assert_eq!(ui.cursor_feedback().global, GlobalCursorKind::Default);
    assert!(resolve_global_cursor(&dats, GlobalCursorKind::Default).is_some());

    let frame = ui.host().root.window(TOOLBAR_WINDOW).unwrap().frame;
    let backpack = {
        let root = &ui.host().root;
        let mut stack = vec![frame];
        let mut found = None;
        while let Some(e) = stack.pop() {
            if root.element(e).unwrap().dat_element_id == 0x1000_01B1 {
                found = Some(e);
                break;
            }
            stack.extend(root.children(e));
        }
        found.expect("the backpack button")
    };
    let (x, y) = {
        let root = &ui.host().root;
        let (x, y) = root.screen_position(backpack);
        let e = root.element(backpack).unwrap();
        ((x + e.width / 2.0) as i32, (y + e.height / 2.0) as i32)
    };
    ui.host_mut().mouse_move(x, y);
    assert_eq!(ui.cursor_feedback().global, GlobalCursorKind::DefaultFound);

    interface(
        &mut scene,
        InterfaceState {
            player: PLAYER,
            busy_count: 1,
            ..InterfaceState::default()
        },
    );
    ui.feed(&mut scene);
    ui.host_mut().mouse_move(640, 300);
    assert_eq!(ui.cursor_feedback().global, GlobalCursorKind::Busy);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_vivid_target_brackets_the_selection_when_the_option_is_on() {
    // OpenAC `VividTargetIndicatorController.Tick` in the window: the four
    // corners around the selected object's sphere while the character
    // option is on; nothing with the option off.
    use ac_ui::overlay::{OverlayCamera, VividTargetInfo};
    use glam::Vec3;
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = scene();
    let base = *scene.player_view().unwrap();
    let view = |options1| PlayerView {
        selected: Some(DRUDGE),
        options1,
        ..base
    };
    scene.apply(PresentationEvent::Player(view(0x0000_8000)));
    ui.feed(&mut scene);
    let camera = OverlayCamera {
        view: glam::camera::rh::view::look_at_mat4(Vec3::ZERO, Vec3::Y, Vec3::Z),
        projection: glam::camera::rh::proj::directx::perspective(
            std::f32::consts::FRAC_PI_2,
            1280.0 / 720.0,
            0.1,
            100.0,
        ),
        viewport: (1280.0, 720.0),
    };
    let target = VividTargetInfo {
        center: Vec3::new(0.0, 10.0, 0.0),
        radius: 1.0,
        item_type: 0x10,
        description_flags: 0x10,
        blip_color: 0,
    };
    let on_screen = |ui: &WindowUi| {
        let root = &ui.host().root;
        root.children(root.root())
            .into_iter()
            .find(|c| {
                root.element(*c).unwrap().name.as_deref() == Some("VividTargetIndicatorOnScreen")
            })
            .map(|c| {
                (
                    root.element(c).unwrap().visible(),
                    root.element(c).unwrap().left,
                )
            })
    };
    ui.set_world_view(Some(camera), Some(target));
    let start = Instant::now();
    ui.frame(start, size);
    let (shown, left) = on_screen(&ui).expect("the indicator is mounted");
    assert!(shown);
    assert!(
        left > 500.0 && left < 640.0,
        "around the screen's centre: {left}"
    );
    scene.apply(PresentationEvent::Player(view(0)));
    ui.feed(&mut scene);
    ui.frame(start + Duration::from_millis(50), size);
    assert_eq!(on_screen(&ui).map(|(v, _)| v), Some(false));
}

/// OpenAC `RetailUiRuntime.MountSideVitals` and
/// `VitalsSideBySideController.Tick` in the window: the
/// side-by-side vitals mount hidden at the vitals' default place; the
/// options window's SideBySideVitals row asks the session to set the
/// character option, and the option's word coming back swaps the two
/// windows, both ways. The side row reads the same vitals.
#[test]
#[ignore = "needs the retail dat files"]
fn the_side_by_side_vitals_option_swaps_the_vitals_windows() {
    use ac_runtime::character::option_id;
    let dats = dats();
    let size = (1280, 720);
    let settings = ac_host::settings::PlayerSettings::default();
    let mut ui = WindowUi::build_with(dats.clone(), size, &settings, vec!["1280x720".into()])
        .expect("the UI builds");
    let notices = ui.take_notices();
    assert!(notices.is_empty(), "{notices:?}");
    let visible = |ui: &WindowUi| {
        let root = &ui.host().root;
        (
            root.is_window_visible(VITALS_WINDOW),
            root.is_window_visible(SIDE_VITALS_WINDOW),
        )
    };
    let place = |ui: &WindowUi, name| {
        let root = &ui.host().root;
        let e = root.element(root.window(name).unwrap().frame).unwrap();
        (e.left, e.top)
    };
    assert_eq!(visible(&ui), (true, false));
    assert_eq!(place(&ui, SIDE_VITALS_WINDOW), place(&ui, VITALS_WINDOW));
    {
        let root = &ui.host().root;
        let e = root
            .element(root.window(SIDE_VITALS_WINDOW).unwrap().frame)
            .unwrap();
        assert!(e.resize_x && !e.resize_y);
    }

    let mut scene = scene();
    ui.feed(&mut scene);
    assert_eq!(visible(&ui), (true, false), "the option starts off");

    // The row asks the session to turn the option on.
    let row = options_row(
        &mut ui,
        dats.clone(),
        ac_ui::panels::options::OptionsPage::Character,
        "ID_PlayerOption_SideBySideVitals",
    );
    set_row(&mut ui, row, true);
    let commands = ui.drain();
    assert!(
        matches!(
            commands.as_slice(),
            [SessionCommand::SetCharacterOption {
                option: option_id::SIDE_BY_SIDE_VITALS,
                value: true
            }]
        ),
        "{commands:?}"
    );
    let mask = ac_runtime::character::option(option_id::SIDE_BY_SIDE_VITALS)
        .unwrap()
        .mask;
    let base = *scene.player_view().unwrap();
    // The session's word with the bit set comes back.
    scene.apply(PresentationEvent::Player(PlayerView {
        options1: base.options1 | mask,
        ..base
    }));
    ui.feed(&mut scene);
    assert_eq!(visible(&ui), (false, true));
    let side_text = |ui: &WindowUi, id: u32| {
        let root = &ui.host().root;
        let window = root.window(SIDE_VITALS_WINDOW).unwrap().frame;
        let e = root.find_descendant(window, id).unwrap();
        root.widget::<ac_ui::widgets::Text>(e)
            .unwrap()
            .content_lines()
            .iter()
            .map(|l| l.text.clone())
            .collect::<Vec<_>>()
    };
    assert_eq!(side_text(&ui, vitals::HEALTH_TEXT), ["42/100"]);
    assert_eq!(side_text(&ui, vitals::STAMINA_TEXT), ["50/100"]);
    assert_eq!(side_text(&ui, vitals::MANA_TEXT), ["75/100"]);

    // Off again: the stacked window comes back.
    set_row(&mut ui, row, false);
    let commands = ui.drain();
    assert!(
        matches!(
            commands.as_slice(),
            [SessionCommand::SetCharacterOption {
                option: option_id::SIDE_BY_SIDE_VITALS,
                value: false
            }]
        ),
        "{commands:?}"
    );
    scene.apply(PresentationEvent::Player(PlayerView {
        options1: base.options1 & !mask,
        ..base
    }));
    ui.feed(&mut scene);
    assert_eq!(visible(&ui), (true, false));
}

/// OpenAC `RetailUiRuntime.MountInventory` and
/// `InventoryController.ConfigureResizeLayout` in the window: the bottom
/// edge of the inventory drags its height (never its
/// width); the contents grid, its scrollbar and the backdrop stretch with
/// it, the grid shows more rows, and the scroll offset is kept.
#[test]
#[ignore = "needs the retail dat files"]
fn dragging_the_inventory_bottom_edge_reflows_the_grid_and_keeps_the_scroll() {
    use ac_render::ui::window::Hotkey;
    use ac_ui::event::MouseButton;
    use ac_ui::panels::inventory;
    use ac_ui::widgets::ItemList;

    let size = (1280, 1024);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = scene();
    interface(&mut scene, InterfaceState::default());
    ui.feed(&mut scene);
    assert!(ui.run_hotkey(Hotkey::Inventory));
    ui.drain();
    let start = Instant::now();
    let mut frames = 0;
    let mut step = |ui: &mut WindowUi, scene: &mut Scene| {
        frames += 1;
        ui.feed(scene);
        ui.frame(start + Duration::from_millis(50 * frames), size);
    };
    step(&mut ui, &mut scene);
    assert!(ui.host().root.is_window_visible(INVENTORY_WINDOW));

    let frame_id = ui.host().root.window(INVENTORY_WINDOW).unwrap().frame;
    let rect = |ui: &WindowUi, id| {
        let e = ui.host().root.element(id).unwrap();
        (e.left, e.top, e.width, e.height)
    };
    let grid = ui
        .host()
        .root
        .find_descendant(frame_id, inventory::CONTENTS_GRID_ID)
        .expect("the contents grid");
    let bar = ui
        .host()
        .root
        .find_descendant(frame_id, inventory::CONTENTS_SCROLLBAR_ID)
        .expect("the contents scrollbar");
    // (scroll offset, max scroll, view height, cells shown)
    let scroll = |ui: &WindowUi| {
        let root = &ui.host().root;
        let l = root.widget::<ItemList>(grid).unwrap();
        let shown = l
            .cells()
            .iter()
            .filter(|c| root.element(**c).unwrap().visible())
            .count();
        (
            l.scroll.scroll_y(),
            l.scroll.max_scroll(),
            l.scroll.view_height,
            shown,
        )
    };
    // Every pack slot is a cell: the grid overflows its authored height.
    let (frame0, grid0, bar0) = (rect(&ui, frame_id), rect(&ui, grid), rect(&ui, bar));
    let (_, max0, view0, shown0) = scroll(&ui);
    assert!(max0 > 64, "the grid scrolls");
    assert_eq!(
        ui.host()
            .root
            .widget::<ItemList>(grid)
            .unwrap()
            .cells()
            .len(),
        inventory::MAIN_PACK_SLOTS
    );

    // Scroll one row down.
    ui.host_mut()
        .root
        .widget_mut::<ItemList>(grid)
        .unwrap()
        .scroll
        .set_scroll_y(32);
    step(&mut ui, &mut scene);
    assert_eq!(scroll(&ui).0, 32);

    let drag = |ui: &mut WindowUi, dy: i32| {
        let (l, t, w, h) = rect(ui, frame_id);
        let (x, y) = ((l + w / 2.0) as i32, (t + h - 2.0) as i32);
        let host = ui.host_mut();
        host.mouse_down(MouseButton::Left, x, y, 0);
        host.mouse_move(x + 40, y + dy);
        host.mouse_up(MouseButton::Left, x + 40, y + dy, 0);
    };

    // Two rows taller: the frame, the grid and the scrollbar grow by
    // 64 px, the width stays, two more rows show, the offset is kept.
    drag(&mut ui, 64);
    step(&mut ui, &mut scene);
    step(&mut ui, &mut scene);
    let frame1 = rect(&ui, frame_id);
    assert_eq!(
        (frame1.0, frame1.1, frame1.2),
        (frame0.0, frame0.1, frame0.2)
    );
    assert_eq!(frame1.3, frame0.3 + 64.0);
    let grid1 = rect(&ui, grid);
    assert_eq!((grid1.0, grid1.1, grid1.2), (grid0.0, grid0.1, grid0.2));
    assert_eq!(grid1.3, grid0.3 + 64.0);
    assert_eq!(rect(&ui, bar).3, bar0.3 + 64.0);
    let (offset, max1, view1, shown1) = scroll(&ui);
    assert_eq!(offset, 32, "the scroll is kept");
    assert_eq!(view1, view0 + 64);
    assert_eq!(max1, max0 - 64);
    assert_eq!(shown1, shown0 + 2 * inventory::CONTENTS_COLUMNS);
    assert_eq!(
        ui.host().root.widget::<ItemList>(grid).unwrap().columns,
        inventory::CONTENTS_COLUMNS
    );

    // A refresh of the items keeps the offset at the new size.
    interface(&mut scene, InterfaceState::default());
    step(&mut ui, &mut scene);
    assert_eq!(scroll(&ui).0, 32);

    // Back to the authored height: everything as it was.
    drag(&mut ui, -64);
    step(&mut ui, &mut scene);
    step(&mut ui, &mut scene);
    assert_eq!(rect(&ui, frame_id), frame0);
    assert_eq!(rect(&ui, grid), grid0);
    assert_eq!(scroll(&ui), (32, max0, view0, shown0));
}

/// OpenAC `InventoryController.ResizePackColumn` in the window: a player
/// with more than seven side packs grows the pack column and the window,
/// and the contents grid and its scrollbar still reach the window's
/// bottom, as at the authored height.
#[test]
#[ignore = "needs the retail dat files"]
fn a_longer_pack_column_keeps_the_contents_grid_filling_the_window() {
    use ac_render::ui::window::Hotkey;
    use ac_ui::panels::inventory;

    let size = (1280, 1024);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = scene();
    interface(&mut scene, InterfaceState::default());
    ui.feed(&mut scene);
    assert!(ui.run_hotkey(Hotkey::Inventory));
    ui.drain();
    let start = Instant::now();
    let mut frames = 0;
    let mut step = |ui: &mut WindowUi, scene: &mut Scene| {
        frames += 1;
        ui.feed(scene);
        ui.frame(start + Duration::from_millis(50 * frames), size);
    };
    step(&mut ui, &mut scene);
    let frame_id = ui.host().root.window(INVENTORY_WINDOW).unwrap().frame;
    let find = |ui: &WindowUi, id| {
        ui.host()
            .root
            .find_descendant(frame_id, id)
            .expect("an inventory element")
    };
    let (grid, bar) = (
        find(&ui, inventory::CONTENTS_GRID_ID),
        find(&ui, inventory::CONTENTS_SCROLLBAR_ID),
    );
    // How far above the frame's bottom edge an element ends.
    let gap = |ui: &WindowUi, id| {
        let root = &ui.host().root;
        let bottom = |id| root.screen_position(id).1 + root.element(id).unwrap().height;
        bottom(frame_id) - bottom(id)
    };
    let (frame_height0, grid_gap0, bar_gap0) = (
        ui.host().root.element(frame_id).unwrap().height,
        gap(&ui, grid),
        gap(&ui, bar),
    );

    // Nine side packs.
    let mut player = ac_core::items::ClientObject::new(PLAYER);
    player.name = "+Horan".to_owned();
    player.item_type = ac_core::items::ItemType::CREATURE;
    player.properties.ints.insert(0x19, 12);
    player.containers_capacity = 9;
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Items(
        ItemsUpdate {
            reset: false,
            objects: vec![player],
            removed: Vec::new(),
            contents: Vec::new(),
        },
    )));
    step(&mut ui, &mut scene);
    step(&mut ui, &mut scene);

    assert!(
        ui.host().root.element(frame_id).unwrap().height > frame_height0,
        "the window grew for the longer pack column"
    );
    assert_eq!(gap(&ui, grid), grid_gap0, "the grid reaches the bottom");
    assert_eq!(gap(&ui, bar), bar_gap0, "the scrollbar reaches the bottom");

    // Dragged taller, it still does.
    let (l, t, w, h) = {
        let e = ui.host().root.element(frame_id).unwrap();
        (e.left, e.top, e.width, e.height)
    };
    let (x, y) = ((l + w / 2.0) as i32, (t + h - 2.0) as i32);
    let host = ui.host_mut();
    host.mouse_down(ac_ui::event::MouseButton::Left, x, y, 0);
    host.mouse_move(x, y + 96);
    host.mouse_up(ac_ui::event::MouseButton::Left, x, y + 96, 0);
    step(&mut ui, &mut scene);
    step(&mut ui, &mut scene);
    assert_eq!(
        gap(&ui, grid),
        grid_gap0,
        "dragged: the grid reaches the bottom"
    );
    assert_eq!(
        gap(&ui, bar),
        bar_gap0,
        "dragged: the scrollbar reaches the bottom"
    );

    // Closed and opened again, it still does.
    assert!(ui.run_hotkey(Hotkey::Inventory));
    step(&mut ui, &mut scene);
    assert!(ui.run_hotkey(Hotkey::Inventory));
    step(&mut ui, &mut scene);
    step(&mut ui, &mut scene);
    assert_eq!(
        gap(&ui, grid),
        grid_gap0,
        "reopened: the grid reaches the bottom"
    );
}

/// The same with the nine side packs known before the inventory is first
/// opened (they arrive with the character, as at login).
#[test]
#[ignore = "needs the retail dat files"]
fn a_longer_pack_column_known_at_login_keeps_the_contents_grid_filling_the_window() {
    use ac_render::ui::window::Hotkey;
    use ac_ui::panels::inventory;

    let size = (1280, 1024);
    let start = Instant::now();
    let mut frames = 0;
    let mut step = |ui: &mut WindowUi, scene: &mut Scene| {
        frames += 1;
        ui.feed(scene);
        ui.frame(start + Duration::from_millis(50 * frames), size);
    };
    // The gaps at the authored height, from a seven-pack character.
    let gaps = |nine: bool, step: &mut dyn FnMut(&mut WindowUi, &mut Scene)| {
        let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
        let mut scene = scene();
        interface(&mut scene, InterfaceState::default());
        if nine {
            let mut player = ac_core::items::ClientObject::new(PLAYER);
            player.name = "+Horan".to_owned();
            player.item_type = ac_core::items::ItemType::CREATURE;
            player.properties.ints.insert(0x19, 12);
            player.containers_capacity = 9;
            scene.apply(PresentationEvent::Interface(InterfaceEvent::Items(
                ItemsUpdate {
                    reset: false,
                    objects: vec![player],
                    removed: Vec::new(),
                    contents: Vec::new(),
                },
            )));
        }
        step(&mut ui, &mut scene);
        step(&mut ui, &mut scene);
        assert!(ui.run_hotkey(Hotkey::Inventory));
        step(&mut ui, &mut scene);
        step(&mut ui, &mut scene);
        let root = &ui.host().root;
        let frame_id = root.window(INVENTORY_WINDOW).unwrap().frame;
        let bottom = |id| root.screen_position(id).1 + root.element(id).unwrap().height;
        let grid = root
            .find_descendant(frame_id, inventory::CONTENTS_GRID_ID)
            .unwrap();
        let bar = root
            .find_descendant(frame_id, inventory::CONTENTS_SCROLLBAR_ID)
            .unwrap();
        (
            root.element(frame_id).unwrap().height,
            bottom(frame_id) - bottom(grid),
            bottom(frame_id) - bottom(bar),
        )
    };
    let seven = gaps(false, &mut step);
    let nine = gaps(true, &mut step);
    assert!(nine.0 > seven.0, "the window grew: {seven:?} {nine:?}");
    assert_eq!((nine.1, nine.2), (seven.1, seven.2), "{seven:?} {nine:?}");
}

/// OpenAC `RetailUiRuntime.HandleInputAction`'s chat keys, in the window:
/// the reply key starts a tell to the last teller the session reported,
/// the command key starts a command, and tell-to-selected starts a tell to
/// the selected player by his item-table name (nothing for a creature);
/// each change of the entry's text is the session's chat entry's draft.
#[test]
#[ignore = "needs the retail dat files"]
fn the_chat_keys_start_replies_commands_and_tells_in_the_chat_entry() {
    use ac_runtime::session::interface::ChatEntryView;
    use ac_ui::input::InputAction;
    let dats = dats();
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats, size).expect("the UI builds");
    let mut scene = scene();
    interface(
        &mut scene,
        InterfaceState {
            player: PLAYER,
            ..InterfaceState::default()
        },
    );
    scene.apply(PresentationEvent::Interface(InterfaceEvent::ChatEntry(
        Box::new(ChatEntryView {
            last_incoming_tell_sender: Some("Dww".into()),
            ..ChatEntryView::default()
        }),
    )));
    ui.feed(&mut scene);
    ui.drain();
    let entry = |ui: &WindowUi| {
        let root = &ui.host().root;
        let window = root.window(CHAT_WINDOW).unwrap().frame;
        let field = root.find_descendant(window, chat::INPUT_ID).unwrap();
        root.widget::<ac_ui::widgets::Field>(field).unwrap().text()
    };
    for action in [
        InputAction::ChatReply,
        InputAction::ChatMonarchReply,
        InputAction::ChatPatronReply,
        InputAction::ChatStartCommand,
        InputAction::ChatTellToSelected,
    ] {
        assert!(
            ac_render::ui::status::is_interface_action(action),
            "{action:?}"
        );
    }
    assert!(ui.handle_input_action(InputAction::ChatReply));
    assert_eq!(entry(&ui), "@tell Dww, ");
    let sent = ui.drain();
    assert!(
        matches!(
            sent.as_slice(),
            [SessionCommand::ChatEntry(ChatEntryCommand::SetDraft(d))] if d == "@tell Dww, "
        ),
        "{sent:?}"
    );
    // Nobody spoke on the monarch channel: the entry stays as it is.
    assert!(ui.handle_input_action(InputAction::ChatMonarchReply));
    assert_eq!(entry(&ui), "@tell Dww, ");
    assert!(ui.handle_input_action(InputAction::ChatStartCommand));
    assert_eq!(entry(&ui), "/");
    // The drudge is selected: no tell.
    scene.apply(PresentationEvent::Player(PlayerView {
        selected: Some(DRUDGE),
        ..*scene.player_view().unwrap()
    }));
    ui.feed(&mut scene);
    assert!(ui.handle_input_action(InputAction::ChatTellToSelected));
    assert_eq!(entry(&ui), "/");
    // The player himself is selected: a tell to his item-table name.
    scene.apply(PresentationEvent::Player(PlayerView {
        selected: Some(PLAYER),
        ..*scene.player_view().unwrap()
    }));
    ui.feed(&mut scene);
    assert!(ui.handle_input_action(InputAction::ChatTellToSelected));
    assert_eq!(entry(&ui), "@tell +Horan, ");
}

/// OpenAC `ChatWindowController.EnterChatMode` and `ToggleChatEntry`
/// (`RetailUiRuntime.HandleInputAction`): Enter Chat Mode focuses the chat
/// entry and selects its text; Toggle Chat Entry focuses it, or gives the
/// focus up when it has it; the key that did it types nothing until it is
/// let go. The split key with nothing selected changes nothing.
#[test]
#[ignore = "needs the retail dat files"]
fn the_chat_entry_keys_focus_and_release_the_chat_entry() {
    use ac_ui::input::InputAction;
    let mut ui = WindowUi::build(dats(), (1280, 720)).expect("the UI builds");
    let mut scene = scene();
    ui.feed(&mut scene);
    ui.drain();
    let field = {
        let root = &ui.host().root;
        let window = root.window(CHAT_WINDOW).unwrap().frame;
        root.find_descendant(window, chat::INPUT_ID).unwrap()
    };
    assert_ne!(ui.host().root.keyboard_focus(), Some(field));
    assert!(ui.handle_input_action(InputAction::SelectionSplitStack));
    assert_ne!(ui.host().root.keyboard_focus(), Some(field));

    assert!(ui.chat_entry_key(InputAction::ToggleChatEntry, Some(ac_ui::Key::Tab)));
    assert_eq!(ui.host().root.keyboard_focus(), Some(field));
    // The Tab still held types nothing into the entry.
    ui.host_mut()
        .root
        .on_key_down(ac_ui::Key::Tab, ac_ui::Modifiers::NONE);
    assert_eq!(ui.host().root.keyboard_focus(), Some(field));
    assert!(ui.chat_entry_key(InputAction::ToggleChatEntry, None));
    assert_eq!(ui.host().root.keyboard_focus(), None);

    assert!(ui.handle_input_action(InputAction::EnterChatMode));
    assert_eq!(ui.host().root.keyboard_focus(), Some(field));
    assert!(ui.handle_input_action(InputAction::EnterChatMode));
    assert_eq!(ui.host().root.keyboard_focus(), Some(field));
}

/// Enter bound as a click to Enter Chat Mode (a keyboard click fires as
/// the key goes down): the gameplay input hands the chat window the key,
/// the entry takes the focus, and the same keystroke, still down, does not
/// reach the entry (it would send the empty line and give the focus up).
#[test]
#[ignore = "needs the retail dat files"]
fn enter_bound_as_a_click_focuses_the_chat_entry_without_reaching_it() {
    use ac_render::gameplay_input::{DeviceState, GameplayInput, GameplayOutput};
    use ac_ui::input::{ActivationType, Binding, InputAction, KeyBindings, KeyChord};
    let mut bindings = KeyBindings::retail_defaults();
    let enter = KeyChord::key(Key::Enter, Modifiers::NONE);
    let press = bindings.find(enter, ActivationType::Press).unwrap();
    assert!(bindings.remove(&press));
    bindings.add(Binding::new(enter, InputAction::EnterChatMode).activation(ActivationType::Click));
    let mut input = GameplayInput::new(bindings);
    let mut out = Vec::new();
    input.key(
        Key::Enter,
        true,
        &DeviceState::default(),
        None,
        1.0,
        &mut out,
    );
    let [GameplayOutput::ChatEntry(action, key)] = out[..] else {
        panic!("{out:?}");
    };
    assert_eq!(key, Some(Key::Enter));

    let mut ui = WindowUi::build(dats(), (1280, 720)).expect("the UI builds");
    let mut scene = scene();
    ui.feed(&mut scene);
    ui.drain();
    let field = {
        let root = &ui.host().root;
        let window = root.window(CHAT_WINDOW).unwrap().frame;
        root.find_descendant(window, chat::INPUT_ID).unwrap()
    };
    assert!(ui.chat_entry_key(action, key));
    assert_eq!(ui.host().root.keyboard_focus(), Some(field));
    ui.host_mut().root.on_key_down(Key::Enter, Modifiers::NONE);
    assert_eq!(ui.host().root.keyboard_focus(), Some(field));
    assert!(ui.drain().is_empty(), "the keystroke sent nothing");
}

/// OpenAC `InteractionRetainedUiComposition`: the interface's system
/// messages (`DisplaySystemMessage`) are the session's client-local text
/// (`Communication.AddText(text, ClientLocal)`), so they outlive the window
/// and the plugins' filters see them; the window's transcript does not
/// write them itself.
#[test]
#[ignore = "needs the retail dat files"]
fn the_interface_messages_go_to_the_sessions_communication() {
    use ac_core::chat::LogTextType;
    use ac_ui::input::InputAction;
    let dats = dats();
    let mut ui = WindowUi::build(dats, (1280, 720)).expect("the UI builds");
    let mut scene = scene();
    ui.feed(&mut scene);
    ui.drain();
    assert!(ui.handle_input_action(InputAction::ToggleUrgentAssistancePanel));
    let sent = ui.drain();
    assert!(
        matches!(
            sent.as_slice(),
            [SessionCommand::ShowText { text, log_text_type: LogTextType::ClientLocal }]
                if *text == ac_ui::panels::options::urgent_assistance_unavailable()
        ),
        "{sent:?}"
    );
    ui.client_line("Screenshot saved to here");
    let sent = ui.drain();
    assert!(
        matches!(
            sent.as_slice(),
            [SessionCommand::ShowText { text, log_text_type: LogTextType::ClientLocal }]
                if text == "Screenshot saved to here"
        ),
        "{sent:?}"
    );
}

/// OpenAC `FpsRuntimeBindings(() => buildingDegrades.Fps, () =>
/// buildingDegrades.ActiveMultiplier, ...)`: the frame-rate display shows
/// the world renderer's level-of-detail owner, its rate and the degrade
/// multiplier in force.
#[test]
#[ignore = "needs the retail dat files"]
fn the_frame_rate_display_shows_the_renderers_level_of_detail() {
    use ac_runtime::communication::WindowRequest;
    use ac_ui::panels::fps;
    let dats = dats();
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats, size).expect("the UI builds");
    let mut scene = scene();
    scene.apply(PresentationEvent::WindowRequest(
        WindowRequest::ToggleFrameRate,
    ));
    ui.feed(&mut scene);
    ui.set_level_of_detail(42.0, -0.4);
    ui.frame(Instant::now(), size);
    let root = &ui.host().root;
    let display = root
        .find_descendant(root.root(), fps::DISPLAY_ELEMENT_ID)
        .expect("the display is mounted");
    let lines: Vec<String> = root
        .widget::<ac_ui::widgets::Text>(display)
        .unwrap()
        .content_lines()
        .iter()
        .map(|l| l.text.clone())
        .collect();
    assert_eq!(lines, ["FPS: 42.00", "DEG: -0.40"]);
}

/// OpenAC `LivePresentationComposition` (`RadarSnapshotProvider`'s
/// `relationshipFor`): the fellowship the session reports reaches the
/// window's radar, where the fellowship's leader takes his triangle.
#[test]
#[ignore = "needs the retail dat files"]
fn the_radar_marks_the_fellowship_the_session_reports() {
    use ac_ui::radar::BlipShape;
    use ac_ui::widgets::Radar;
    let dats = dats();
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats, size).expect("the UI builds");
    let mut scene = scene();
    let mut facts = social(true);
    facts.fellowship = ac_runtime::gameplay::FellowshipView {
        in_fellowship: true,
        leader_guid: DRUDGE,
        ..ac_runtime::gameplay::FellowshipView::default()
    };
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Social(
        Box::new(facts),
    )));
    ui.feed(&mut scene);
    let start = Instant::now();
    ui.frame(start, size);
    ui.frame(start + Duration::from_millis(50), size);
    let radar = ui
        .host_mut()
        .with_controller::<ac_ui::panels::radar::RadarController, _>(RADAR_WINDOW, |c, _| c.radar())
        .unwrap();
    let blips = &ui
        .host()
        .root
        .widget::<Radar>(radar)
        .unwrap()
        .snapshot()
        .blips;
    let drudge = blips.iter().find(|b| b.object_id == DRUDGE).unwrap();
    assert_eq!(drudge.shape, BlipShape::Triangle);
}

/// A War spell (scarab 1, lowest taper 63) the player knows, examined from
/// the spellbook: its component names as the examination window lists them
/// and its formula cells.
struct ExaminedSpell {
    ui: WindowUi,
    scene: Scene,
    names: std::collections::HashMap<u32, String>,
    focus_class: u32,
    class_of: std::collections::HashMap<u32, u32>,
}

const EXAMINED_SPELL: u32 = 0x7FFF;
const FOCUS: u32 = 0x8000_0030;
const SCARAB: u32 = 0x8000_0031;
const PRISMATIC_TAPER: u32 = 0x8000_0032;

impl ExaminedSpell {
    fn new() -> Self {
        use ac_core::spells::{MagicSchool, SpellMetadata, SpellTable, Spellbook};
        use ac_ui::panels::PanelOutput;
        use ac_ui::root::UiOutput;
        let dats = dats();
        // Names, focus and component classes as the dats hold them.
        let names = dats
            .get::<ac_dat::objects::SpellComponentTable>(ac_dat::objects::SPELL_COMPONENT_TABLE_ID)
            .unwrap()
            .unwrap()
            .components
            .into_iter()
            .map(|(id, c)| (id, c.name))
            .collect();
        let focus_class = ac_runtime::gameplay::tables::magic_packs(&dats).unwrap()[&1];
        let map = dats
            .get::<ac_dat::objects::EnumIdMap>(0x2700_0002)
            .unwrap()
            .unwrap();
        let class_of = [1, 0xBC]
            .into_iter()
            .map(|scid| (scid, map.client_id(scid).unwrap()))
            .collect();
        let size = (1280, 720);
        let mut ui = WindowUi::build(dats, size).expect("the UI builds");
        let mut scene = scene();
        interface(
            &mut scene,
            InterfaceState {
                account: "testaccount".to_owned(),
                ..InterfaceState::default()
            },
        );
        let spell = SpellMetadata {
            school: "War Magic".to_owned(),
            school_id: MagicSchool::WAR_MAGIC,
            formula_components: vec![1, 63],
            ..SpellMetadata::new(EXAMINED_SPELL, "Test Bolt", 0)
        };
        let mut book = Spellbook::new(Some(Arc::new(SpellTable::create([spell]).unwrap())));
        book.on_spell_learned(EXAMINED_SPELL, 0.0);
        scene.apply(PresentationEvent::Interface(InterfaceEvent::Player(
            Box::new(PlayerFacts {
                revision: 2,
                numbers: ac_core::player::LocalPlayerState::new(Some(book)),
                session_seconds: 0.0,
            }),
        )));
        ui.feed(&mut scene);
        ui.handle(vec![UiOutput::Panel(PanelOutput::ExamineSpell(
            EXAMINED_SPELL,
        ))]);
        ui.feed(&mut scene);
        Self {
            ui,
            scene,
            names,
            focus_class,
            class_of,
        }
    }

    /// `items` put directly in the player's pack (beside the sword).
    fn carry(&mut self, items: &[(u32, u32)]) {
        use ac_core::items::ClientObject;
        let objects: Vec<ClientObject> = items
            .iter()
            .map(|&(guid, class)| {
                let mut o = ClientObject::new(guid);
                o.weenie_class_id = class;
                o.container_id = PLAYER;
                o
            })
            .collect();
        let mut contents = vec![SWORD];
        contents.extend(items.iter().map(|&(guid, _)| guid));
        self.scene
            .apply(PresentationEvent::Interface(InterfaceEvent::Items(
                ItemsUpdate {
                    reset: false,
                    objects,
                    removed: Vec::new(),
                    contents: vec![(PLAYER, Some(contents))],
                },
            )));
        self.ui.feed(&mut self.scene);
    }

    fn display(&mut self) -> String {
        use ac_ui::panels::appraisal;
        use ac_ui::widgets::Text;
        let root = &self.ui.host().root;
        let frame = root.window(EXAMINATION_WINDOW).unwrap().frame;
        let text = root
            .find_descendant(frame, appraisal::SPELL_DISPLAY_TEXT_ID)
            .unwrap();
        root.widget::<Text>(text)
            .unwrap()
            .content_lines()
            .into_iter()
            .map(|l| l.text)
            .collect::<Vec<_>>()
            .join("\n")
    }

    fn cells(&mut self) -> Vec<ac_ui::ElementId> {
        use ac_ui::panels::appraisal::AppraisalController;
        self.ui
            .host_mut()
            .with_controller::<AppraisalController, _>(EXAMINATION_WINDOW, |c, _| {
                c.formula_cells().to_vec()
            })
            .unwrap()
    }

    /// Whether each formula cell's missing overlay shows.
    fn missing(&mut self) -> Vec<bool> {
        use ac_ui::panels::appraisal::SPELL_COMPONENT_MISSING_ID;
        let cells = self.cells();
        let root = &self.ui.host().root;
        cells
            .into_iter()
            .map(|c| {
                let m = root
                    .find_descendant(c, SPELL_COMPONENT_MISSING_ID)
                    .expect("the authored cell's missing overlay");
                root.element(m).unwrap().visible
            })
            .collect()
    }
}

// OpenAC `AppraisalUiController.RefreshSpellComponents`, run on the item
// table's changes: picking up the school's focus while the spell shows
// lists its scarab and prismatic taper at once.
#[test]
#[ignore = "needs the retail dat files"]
fn picking_up_a_focus_while_a_spell_is_examined_shows_its_prismatic_taper() {
    let mut s = ExaminedSpell::new();
    assert!(s.ui.host().root.is_window_visible(EXAMINATION_WINDOW));
    let taper = s.names[&63].clone();
    let prismatic = s.names[&0xBC].clone();
    let display = s.display();
    assert!(display.contains(&taper), "{display}");
    assert!(!display.contains(&prismatic), "{display}");
    assert_eq!(s.cells().len(), 2);

    let focus = s.focus_class;
    s.carry(&[(FOCUS, focus)]);
    let display = s.display();
    assert!(display.contains(&prismatic), "{display}");
    assert!(!display.contains(&taper), "{display}");
    assert_eq!(s.cells().len(), 2);
}

// OpenAC `SpellExamineComponentTemplateFactory.Create`: each formula cell
// is the examination layout's authored cell, its missing overlay shown
// over a component the player does not carry.
#[test]
#[ignore = "needs the retail dat files"]
fn components_the_player_does_not_carry_show_the_missing_overlay() {
    use ac_ui::widgets::DatElement;
    let mut s = ExaminedSpell::new();
    assert_eq!(s.missing(), [true, true]);
    let (focus, scarab, prismatic) = (s.focus_class, s.class_of[&1], s.class_of[&0xBC]);
    s.carry(&[(FOCUS, focus), (SCARAB, scarab)]);
    assert_eq!(s.missing(), [false, true]);
    s.carry(&[
        (FOCUS, focus),
        (SCARAB, scarab),
        (PRISMATIC_TAPER, prismatic),
    ]);
    assert_eq!(s.missing(), [false, false]);
    let cells = s.cells();
    let root = &s.ui.host().root;
    for c in cells {
        let cell = root.widget::<DatElement>(c).expect("the authored cell");
        assert!(
            matches!(cell.runtime_image, Some(Some(_))),
            "the icon shows"
        );
    }
}
