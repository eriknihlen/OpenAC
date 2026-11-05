//! The window's retail UI against the retail dats: the floating
//! chat windows and the other panels. Ignored by
//! default; `scripts/check.*` runs them when the dats are present.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Instant;

use ac_dat::DatCollection;
use ac_render::scene::Scene;
use ac_render::ui::WindowUi;
use ac_render::ui::chat_windows::FLOATING_CHAT_WINDOWS;
use ac_render::ui::window::{CHAT_WINDOW, UiScript};
use ac_runtime::communication::{ChatKind, ChatLine};
use ac_runtime::session::PresentationEvent;
use ac_ui::event::{Key, Modifiers};
use ac_ui::input::InputAction;
use ac_ui::panels::{chat, floating_chat};
use ac_ui::states;

fn dats() -> Arc<DatCollection> {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Arc::new(DatCollection::open(dir).expect("open the retail dats"))
}

fn line(sequence: u64, log_text_type: u32, text: &str) -> ChatLine {
    ChatLine {
        sequence,
        kind: ChatKind::System,
        log_text_type,
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

fn transcript(ui: &WindowUi, window: &str, id: u32) -> Vec<String> {
    let root = &ui.host().root;
    let frame = root.window(window).unwrap().frame;
    let t = root.find_descendant(frame, id).unwrap();
    root.widget::<ac_ui::widgets::Text>(t)
        .unwrap()
        .content_lines()
        .iter()
        .map(|l| l.text.clone())
        .collect()
}

/// OpenAC `RetailUiRuntime.MountFloatingChatWindows` (four hidden windows,
/// mounted at x 440, then centred by the layout reset that follows the
/// mounts, `WindowPlacementGeometry.Default`), `ToggleFloatingChatWindow` (Alt+1..4
/// and the main window's indicators) and `OnWindowVisibilityChanged` (the
/// indicator follows), with each window's own default filter.
#[test]
#[ignore = "needs the retail dat files"]
fn the_floating_chat_windows_mount_hidden_filter_their_lines_and_toggle() {
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let notices = ui.take_notices();
    assert!(notices.is_empty(), "{notices:?}");
    for name in FLOATING_CHAT_WINDOWS {
        let root = &ui.host().root;
        assert!(ui.mounted().contains(&name));
        assert!(!root.is_window_visible(name), "{name} starts hidden");
        let frame = root.element(root.window(name).unwrap().frame).unwrap();
        let (w, h) = (frame.width, frame.height);
        assert_eq!(
            (frame.left, frame.top),
            (((1280.0 - w) * 0.5).max(0.0), ((720.0 - h) * 0.5).max(0.0)),
            "{name}"
        );
    }

    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Chat(vec![
        line(1, 0x02, "a speech line"),
        line(2, 0x13, "a fellowship line"),
    ]));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    // Window 1 hears speech, window 3 the fellowship.
    let one = transcript(&ui, "chat-window-1", floating_chat::TRANSCRIPT_ID);
    assert_eq!(one, ["a speech line"]);
    let three = transcript(&ui, "chat-window-3", floating_chat::TRANSCRIPT_ID);
    assert_eq!(three, ["a fellowship line"]);

    // Alt+1 (the key map's action) shows window 1 and lights the main
    // window's indicator.
    assert!(ui.handle_input_action(InputAction::ToggleFloatingChatWindow1));
    ui.host_mut().pump();
    let out = ui.host_mut().take_outputs();
    ui.handle(out);
    assert!(ui.host().root.is_window_visible("chat-window-1"));
    let indicator = {
        let root = &ui.host().root;
        let frame = root.window(CHAT_WINDOW).unwrap().frame;
        root.find_descendant(frame, chat::INDICATOR_IDS[0]).unwrap()
    };
    assert_eq!(
        ui.host().root.retail_state(indicator),
        Some(states::HIGHLIGHT)
    );
    // Again: hidden, the indicator dims.
    assert!(ui.handle_input_action(InputAction::ToggleFloatingChatWindow1));
    ui.host_mut().pump();
    let out = ui.host_mut().take_outputs();
    ui.handle(out);
    assert!(!ui.host().root.is_window_visible("chat-window-1"));
    assert_eq!(ui.host().root.retail_state(indicator), Some(states::NORMAL));
}

/// OpenAC `ChatFontResolverTests.InstalledDat_ResolvesAllTwentyFivePairs_AndEachDidLoadsAsAFont`.
#[test]
#[ignore = "needs the retail dat files"]
fn every_chat_face_and_size_resolves_to_its_retail_font() {
    use ac_render::ui::options::chat_font_id;
    const PAIRS: [(i32, i32, u32); 25] = [
        (0, 0, 0x4000_0026),
        (0, 1, 0x4000_0027),
        (0, 2, 0x4000_0028),
        (0, 3, 0x4000_002E),
        (0, 4, 0x4000_002F),
        (1, 0, 0x4000_001E),
        (1, 1, 0x4000_001A),
        (1, 2, 0x4000_001F),
        (1, 3, 0x4000_0030),
        (1, 4, 0x4000_0031),
        (2, 0, 0x4000_0002),
        (2, 1, 0x4000_0000),
        (2, 2, 0x4000_0001),
        (2, 3, 0x4000_0004),
        (2, 4, 0x4000_0005),
        (3, 0, 0x4000_0008),
        (3, 1, 0x4000_0009),
        (3, 2, 0x4000_000A),
        (3, 3, 0x4000_000B),
        (3, 4, 0x4000_000C),
        (4, 0, 0x4000_002A),
        (4, 1, 0x4000_002B),
        (4, 2, 0x4000_0032),
        (4, 3, 0x4000_002C),
        (4, 4, 0x4000_002D),
    ];
    let dats = dats();
    for (face, size, expected) in PAIRS {
        assert_eq!(
            chat_font_id(&dats, face, size),
            Some(expected),
            "face {face} size {size}"
        );
        assert!(
            dats.get::<ac_dat::objects::Font>(expected)
                .ok()
                .flatten()
                .is_some(),
            "{expected:#010x} is a font"
        );
    }
}

const PLAYER: u32 = 0x5000_0001;
const CORPSE: u32 = 0x8000_0100;
const LOOT: u32 = 0x8000_0101;

/// The session's facts with a corpse open (holding one item) or not.
fn corpse_facts(scene: &mut Scene, open: bool, in_range: bool) {
    use ac_core::items::{ClientObject, ItemType, PublicWeenieFlags};
    use ac_runtime::session::interface::{InterfaceEvent, InterfaceState, ItemsUpdate};
    let mut player = ClientObject::new(PLAYER);
    player.name = "+Horan".to_owned();
    player.item_type = ItemType::CREATURE;
    let mut corpse = ClientObject::new(CORPSE);
    corpse.name = "Corpse of Drudge".to_owned();
    corpse.item_type = ItemType::CONTAINER;
    corpse.items_capacity = 120;
    corpse.containers_capacity = 10;
    corpse.public_weenie_bitfield = Some(PublicWeenieFlags::OPENABLE | PublicWeenieFlags::CORPSE);
    let mut loot = ClientObject::new(LOOT);
    loot.name = "Drudge Charm".to_owned();
    loot.item_type = ItemType::JEWELRY;
    loot.icon_id = 0x0600_11CB;
    loot.container_id = CORPSE;
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Items(
        ItemsUpdate {
            reset: true,
            objects: vec![player, corpse, loot],
            removed: Vec::new(),
            contents: vec![(CORPSE, Some(vec![LOOT]))],
        },
    )));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::State(
        Box::new(InterfaceState {
            player: PLAYER,
            can_make_request: true,
            external_container: if open { CORPSE } else { 0 },
            external_container_in_range: in_range,
            ..InterfaceState::default()
        }),
    )));
}

/// OpenAC `RetailUiRuntime.MountExternalContainer` and
/// `ExternalContainerController`: the strip shows while the session has a
/// container open, a double click loots, walking off closes it once.
#[test]
#[ignore = "needs the retail dat files"]
fn the_external_container_shows_loots_and_closes_out_of_range() {
    use ac_render::ui::container_window::EXTERNAL_CONTAINER_WINDOW;
    use ac_runtime::inventory::{ItemCommand, ObjectRef};
    use ac_runtime::session::SessionCommand;
    use ac_ui::panels::external_container;
    use ac_ui::widgets::{ItemList, ItemSlot};
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    assert!(ui.mounted().contains(&EXTERNAL_CONTAINER_WINDOW));
    assert!(!ui.host().root.is_window_visible(EXTERNAL_CONTAINER_WINDOW));
    let mut scene = Scene::default();
    corpse_facts(&mut scene, true, true);
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    assert!(ui.host().root.is_window_visible(EXTERNAL_CONTAINER_WINDOW));
    let cell = {
        let root = &ui.host().root;
        let frame = root.window(EXTERNAL_CONTAINER_WINDOW).unwrap().frame;
        let list = root
            .find_descendant(frame, external_container::CONTENTS_LIST_ID)
            .unwrap();
        let cell = root.widget::<ItemList>(list).unwrap().item(0).unwrap();
        assert_eq!(root.widget::<ItemSlot>(cell).unwrap().object_id, LOOT);
        cell
    };
    // A double click on the loot takes it.
    let (x, y) = ui.host().root.screen_position(cell);
    let out = ui.script(&UiScript::Click {
        x: x as i32 + 8,
        y: y as i32 + 8,
        button: 0,
        double: true,
    });
    let sent = ui.handle(out);
    assert!(
        sent.iter().any(|c| matches!(
            c,
            SessionCommand::Item(ItemCommand::Use(ObjectRef::Guid(LOOT)))
        )),
        "{sent:?}"
    );
    // Out of range: the corpse is used once (the server closes it).
    corpse_facts(&mut scene, true, false);
    ui.feed(&mut scene);
    ui.feed(&mut scene);
    let sent = ui.drain();
    let closes = sent
        .iter()
        .filter(|c| {
            matches!(
                c,
                SessionCommand::Item(ItemCommand::CloseContainer(ObjectRef::Guid(CORPSE)))
            )
        })
        .count();
    assert_eq!(closes, 1, "{sent:?}");
    assert!(!ui.host().root.is_window_visible(EXTERNAL_CONTAINER_WINDOW));
}

/// OpenAC `RetailUiRuntime.MountBookPanel` and `BookPanelController`: a
/// book the session opens shows the reader on its first page; next asks
/// the session to turn (saving nothing on a page not the reader's); the
/// book closing hides it.
#[test]
#[ignore = "needs the retail dat files"]
fn the_book_reader_opens_turns_and_closes_with_the_session() {
    use ac_proto::book::BookPage;
    use ac_render::ui::book_window::BOOK_WINDOW;
    use ac_runtime::gameplay::{BookCommand, BookView, GameplayCommand};
    use ac_runtime::session::SessionCommand;
    use ac_runtime::session::interface::InterfaceEvent;
    use ac_ui::panels::book;
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    assert!(ui.mounted().contains(&BOOK_WINDOW));
    let page = |text: &str| BookPage {
        author_id: 0x5000_0099,
        author_name: "F.P.".into(),
        author_account: "prewritten".into(),
        text_included: 1,
        ignore_author: 0,
        page_text: text.into(),
    };
    let open = BookView {
        revision: 1,
        book_guid: 0x8000_0200,
        max_num_pages: 4,
        max_num_chars_per_page: 1000,
        pages: vec![page("In the beginning"), page("and then")],
        current_page: 0,
        inscription: "A Guide".into(),
        scribe_id: 1,
        scribe_name: "F.P.".into(),
        request_pending: false,
    };
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Book(
        Box::new(open.clone()),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    assert!(ui.host().root.is_window_visible(BOOK_WINDOW));
    let (page_text, next, title) = {
        let root = &ui.host().root;
        let frame = root.window(BOOK_WINDOW).unwrap().frame;
        (
            root.find_descendant(frame, book::PAGE_TEXT_ID).unwrap(),
            root.find_descendant(frame, book::NEXT_BUTTON_ID).unwrap(),
            root.find_descendant(frame, book::TITLE_TEXT_ID).unwrap(),
        )
    };
    let root = &ui.host().root;
    assert_eq!(
        root.widget::<ac_ui::widgets::Field>(page_text)
            .unwrap()
            .text(),
        "In the beginning"
    );
    assert_eq!(transcript_of(root, title), ["A Guide"]);
    let (x, y) = root.screen_position(next);
    let out = ui.script(&UiScript::Click {
        x: x as i32 + 4,
        y: y as i32 + 4,
        button: 0,
        double: false,
    });
    let sent = ui.handle(out);
    assert!(
        sent.iter().any(|c| matches!(
            c,
            SessionCommand::Gameplay(GameplayCommand::Book(BookCommand::TurnWriting {
                page: 1,
                typed: None
            }))
        )),
        "{sent:?}"
    );
    // The session closed it.
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Book(
        Box::new(BookView {
            revision: 2,
            current_page: -1,
            ..BookView::default()
        }),
    )));
    ui.feed(&mut scene);
    assert!(!ui.host().root.is_window_visible(BOOK_WINDOW));
    assert!(ui.drain().is_empty(), "a closed book sends nothing");
}

fn transcript_of(root: &ac_ui::root::UiRoot, id: ac_ui::ElementId) -> Vec<String> {
    root.widget::<ac_ui::widgets::Text>(id)
        .unwrap()
        .content_lines()
        .iter()
        .map(|l| l.text.clone())
        .collect()
}

/// Every text line under `id`.
fn texts_under(root: &ac_ui::root::UiRoot, id: ac_ui::ElementId) -> Vec<String> {
    let mut found = Vec::new();
    let mut stack = vec![id];
    while let Some(e) = stack.pop() {
        if let Some(t) = root.widget::<ac_ui::widgets::Text>(e) {
            found.extend(t.content_lines().into_iter().map(|l| l.text));
        }
        stack.extend(root.children(e));
    }
    found
}

/// The player standing at Arwic (cell 0x11CE0001).
fn player_at_arwic(scene: &mut Scene) {
    use ac_proto::{ModelData, Position, Quaternion, Vector3};
    use ac_runtime::session::interface::{InterfaceEvent, InterfaceState};
    use ac_runtime::world::{ObjectKey, ObjectView, RadarFacts, WorldSnapshot};
    scene.apply(PresentationEvent::Snapshot(WorldSnapshot {
        player_guid: Some(PLAYER),
        objects: vec![ObjectView {
            key: ObjectKey {
                local_id: 1,
                incarnation: 1,
            },
            guid: PLAYER,
            name: Some("+Horan".to_owned()),
            setup_id: None,
            position: Some(Position {
                cell_id: 0x11CE_0001,
                origin: Vector3 {
                    x: 10.0,
                    y: 10.0,
                    z: 0.0,
                },
                orientation: Quaternion::IDENTITY,
            }),
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
            radar: RadarFacts::default(),
            motion: None,
            effects: ac_runtime::world::EffectFacts::default(),
            step_blend: None,
        }],
    }));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::State(
        Box::new(InterfaceState {
            player: PLAYER,
            ..InterfaceState::default()
        }),
    )));
}

/// OpenAC `RetailUiRuntime.MountMapHousePanel` and `MapPageController`:
/// F10 opens the map at (240, 160); it reads the date, the player's place
/// and the house's lines; F10 again closes it.
#[test]
#[ignore = "needs the retail dat files"]
fn f10_opens_the_map_with_the_date_the_player_and_the_house() {
    use ac_core::world::{CalendarDate, HourName, MonthName};
    use ac_render::ui::map_window::MAP_HOUSE_WINDOW;
    use ac_runtime::gameplay::{HouseLineColor, HouseView};
    use ac_runtime::session::interface::InterfaceEvent;
    use ac_ui::panels::map_house::{self, MapHousePanelController};
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    assert!(ui.mounted().contains(&MAP_HOUSE_WINDOW));
    let mut scene = Scene::default();
    player_at_arwic(&mut scene);
    // (The journal test below shares the player's place.)
    scene.apply(PresentationEvent::Interface(InterfaceEvent::House(
        Box::new(HouseView {
            revision: 1,
            has_received_notice: true,
            owns_house: false,
            lines: vec![(
                "You may buy another house immediately.".to_owned(),
                HouseLineColor::Normal,
            )],
            position_cell: None,
        }),
    )));
    ui.set_calendar(Some(CalendarDate {
        year: 119,
        month: MonthName::Frostfell,
        day: 27,
        hour: HourName::Dawnsong,
    }));
    // Entering the world restores the saved layout first (the panel starts
    // hidden), then F10 opens it.
    ui.feed(&mut scene);
    let out = ui.script(&UiScript::Key {
        key: Key::F10,
        modifiers: Modifiers::NONE,
    });
    ui.handle(out);
    assert!(ui.host().root.is_window_visible(MAP_HOUSE_WINDOW));
    ui.frame(Instant::now(), size);
    let root = &ui.host().root;
    let frame = root.window(MAP_HOUSE_WINDOW).unwrap().frame;
    let f = root.element(frame).unwrap();
    // Mounted at (240, 160), then centred by the layout reset after the
    // mounts (OpenAC `WindowPlacementGeometry.Default`).
    assert_eq!(
        (f.left, f.top),
        (
            ((size.0 as f32 - f.width) * 0.5).max(0.0),
            ((size.1 as f32 - f.height) * 0.5).max(0.0)
        )
    );
    let text = |id| transcript_of(root, root.find_descendant(frame, id).unwrap());
    assert_eq!(text(map_house::COORDINATE_TEXT_ID), ["62.9N,88.3W"]);
    assert_eq!(
        text(map_house::DATE_TIME_TEXT_ID),
        ["Date: Frostfell 27, 119 P.Y.", "Time: Dawnsong"]
    );
    let player_icon = root
        .find_descendant(frame, map_house::PLAYER_ICON_ID)
        .unwrap();
    assert!(root.element(player_icon).unwrap().visible());
    ui.host_mut()
        .with_controller::<MapHousePanelController, _>(MAP_HOUSE_WINDOW, |c, ui| {
            c.show_house(ui);
        });
    ui.frame(Instant::now(), size);
    let root = &ui.host().root;
    let list = root
        .find_descendant(frame, map_house::HOUSE_TEXT_BOX_ID)
        .unwrap();
    let found = texts_under(root, list);
    assert!(
        found
            .iter()
            .any(|l| l == "You may buy another house immediately."),
        "{found:?}"
    );
    let out = ui.script(&UiScript::Key {
        key: Key::F10,
        modifiers: Modifiers::NONE,
    });
    ui.handle(out);
    assert!(!ui.host().root.is_window_visible(MAP_HOUSE_WINDOW));
}

/// OpenAC `JournalPanelController`, `JournalNotesPageController` and
/// `JournalPageListController` in the window: the journal's pages show on
/// the notes page; next commits and turns; record asks for the player's
/// place.
#[test]
#[ignore = "needs the retail dat files"]
fn the_journal_notes_follow_the_session() {
    use ac_core::journal::JournalPage;
    use ac_render::ui::window::JOURNAL_WINDOW;
    use ac_runtime::communication::{JournalCommand, JournalView};
    use ac_runtime::session::SessionCommand;
    use ac_runtime::session::interface::InterfaceEvent;
    use ac_ui::panels::journal::JournalPanelController;
    use ac_ui::panels::journal_notes as notes;
    use ac_ui::panels::{PanelOutput, panel_id};
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = Scene::default();
    player_at_arwic(&mut scene);
    let page = |title: &str| JournalPage {
        title: title.into(),
        notes: format!("notes of {title}"),
        ..JournalPage::default()
    };
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Journal(
        Box::new(JournalView {
            pages: vec![page("Aerlinthe"), page("Holtburg")],
            current_page: 1,
            revision: 1,
            ..JournalView::default()
        }),
    )));
    ui.feed(&mut scene);
    ui.host_mut()
        .with_controller::<JournalPanelController, _>(JOURNAL_WINDOW, |c, ui| {
            c.show_notes(ui);
        });
    ui.host_mut()
        .root
        .emit(ac_ui::root::UiOutput::Panel(PanelOutput::TogglePanel(
            panel_id::JOURNAL,
        )));
    ui.host_mut().pump();
    let out = ui.host_mut().take_outputs();
    ui.handle(out);
    assert!(ui.host().root.is_window_visible(JOURNAL_WINDOW));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let (next, record) = {
        let root = &ui.host().root;
        let frame = root.window(JOURNAL_WINDOW).unwrap().frame;
        let field = |id| {
            root.widget::<ac_ui::widgets::Field>(root.find_descendant(frame, id).unwrap())
                .unwrap()
                .text()
        };
        assert_eq!(field(notes::TITLE_FIELD_ID), "Aerlinthe");
        assert_eq!(field(notes::NOTES_FIELD_ID), "notes of Aerlinthe");
        let number = root.find_descendant(frame, notes::PAGE_NUMBER_ID).unwrap();
        assert_eq!(transcript_of(root, number), ["~ 1 ~"]);
        (
            root.find_descendant(frame, notes::NEXT_BUTTON_ID).unwrap(),
            root.find_descendant(frame, notes::RECORD_BUTTON_ID)
                .unwrap(),
        )
    };
    let click = |ui: &mut WindowUi, id: ac_ui::ElementId| {
        let (x, y) = ui.host().root.screen_position(id);
        let out = ui.script(&UiScript::Click {
            x: x as i32 + 3,
            y: y as i32 + 3,
            button: 0,
            double: false,
        });
        ui.handle(out)
    };
    let sent = click(&mut ui, next);
    let journal: Vec<&JournalCommand> = sent
        .iter()
        .filter_map(|c| match c {
            SessionCommand::Journal(j) => Some(j),
            _ => None,
        })
        .collect();
    assert!(
        matches!(
            journal.as_slice(),
            [
                JournalCommand::Edit { title, .. },
                JournalCommand::SetTimer { .. },
                JournalCommand::GotoPage(2)
            ] if title == "Aerlinthe"
        ),
        "{journal:?}"
    );
    let sent = click(&mut ui, record);
    assert!(
        sent.iter().any(|c| matches!(
            c,
            SessionCommand::Journal(JournalCommand::RecordLocation { .. })
        )),
        "{sent:?}"
    );
}

/// The player's object, numbers and interaction facts.
fn player_facts(scene: &mut Scene) {
    use ac_core::items::{ClientObject, ItemType};
    use ac_runtime::session::interface::{
        InterfaceEvent, InterfaceState, ItemsUpdate, PlayerFacts,
    };
    let mut player = ClientObject::new(PLAYER);
    player.name = "+Horan".to_owned();
    player.item_type = ItemType::CREATURE;
    player.properties.ints.insert(0x19, 12);
    player.properties.ints.insert(0x2B, 0);
    player.properties.ints.insert(0xB5, 7);
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Items(
        ItemsUpdate {
            reset: true,
            objects: vec![player],
            removed: Vec::new(),
            contents: Vec::new(),
        },
    )));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Player(
        Box::new(PlayerFacts {
            revision: 1,
            numbers: ac_core::player::LocalPlayerState::default(),
            session_seconds: 0.0,
        }),
    )));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::State(
        Box::new(InterfaceState {
            player: PLAYER,
            ..InterfaceState::default()
        }),
    )));
}

/// OpenAC `CharacterTitlesController` and `CharacterController` in the
/// window: the titles page lists the earned titles by name (the header
/// shows the displayed one), a choice asks the session; the information
/// panel (main panel 3) reports from the player's numbers.
#[test]
#[ignore = "needs the retail dat files"]
fn the_titles_page_and_the_information_panel_follow_the_session() {
    use ac_render::ui::character_pages::CHARACTER_INFO_WINDOW;
    use ac_render::ui::window::{CHARACTER_WINDOW, Hotkey};
    use ac_runtime::gameplay::GameplayCommand;
    use ac_runtime::session::SessionCommand;
    use ac_runtime::session::interface::InterfaceEvent;
    use ac_runtime::session::interface::TitleFacts;
    use ac_ui::panels::character::{CharacterStatController, CharacterTab};
    use ac_ui::panels::character_info;
    use ac_ui::panels::character_titles as titles;
    use ac_ui::panels::{PanelOutput, panel_id};
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    assert!(ui.mounted().contains(&CHARACTER_INFO_WINDOW));
    let mut scene = Scene::default();
    player_facts(&mut scene);
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Titles(
        Box::new(TitleFacts {
            display_title_id: 13,
            title_ids: vec![13, 1, 5],
            revision: 1,
            table_generation: 1,
        }),
    )));
    ui.feed(&mut scene);
    assert!(ui.run_hotkey(Hotkey::Character(CharacterTab::Titles)));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let rows = ui
        .host_mut()
        .with_controller::<CharacterStatController, _>(CHARACTER_WINDOW, |c, _| {
            c.titles_mut().unwrap().rows().to_vec()
        })
        .unwrap();
    assert_eq!(rows.iter().map(|r| r.1).collect::<Vec<_>>(), [1, 5, 13]);
    let sheet_title = ui
        .host_mut()
        .with_controller::<CharacterStatController, _>(CHARACTER_WINDOW, |c, _| {
            c.sheet().title.clone()
        })
        .unwrap();
    assert_eq!(sheet_title.as_deref(), Some("War Mage"));
    // Pick Adventurer and ask to display it.
    let (row, button) = {
        let root = &ui.host().root;
        let frame = root.window(CHARACTER_WINDOW).unwrap().frame;
        (
            rows[0].0,
            root.find_descendant(frame, titles::SET_DISPLAY_BUTTON_ID)
                .unwrap(),
        )
    };
    for id in [row, button] {
        let (x, y) = ui.host().root.screen_position(id);
        let out = ui.script(&UiScript::Click {
            x: x as i32 + 4,
            y: y as i32 + 4,
            button: 0,
            double: false,
        });
        let sent = ui.handle(out);
        if id == button {
            assert!(
                sent.iter()
                    .any(|c| matches!(c, SessionCommand::Gameplay(GameplayCommand::SetTitle(1)))),
                "{sent:?}"
            );
        }
    }
    // The information panel.
    ui.host_mut()
        .root
        .emit(ac_ui::root::UiOutput::Panel(PanelOutput::TogglePanel(
            panel_id::CHARACTER_INFORMATION,
        )));
    ui.host_mut().pump();
    let out = ui.host_mut().take_outputs();
    ui.handle(out);
    assert!(ui.host().root.is_window_visible(CHARACTER_INFO_WINDOW));
    // The frame's own events (shown) reach the window, as each frame.
    ui.host_mut().pump();
    ui.drain();
    ui.feed(&mut scene);
    let root = &ui.host().root;
    let frame = root.window(CHARACTER_INFO_WINDOW).unwrap().frame;
    let f = root.element(frame).unwrap();
    // A main panel: it takes the place of the one it replaced.
    let replaced = root
        .element(root.window(CHARACTER_WINDOW).unwrap().frame)
        .unwrap();
    assert_eq!((f.left, f.top), (replaced.left, replaced.top));
    let text = root
        .find_descendant(frame, character_info::MAIN_TEXT_ID)
        .unwrap();
    let report = texts_under(root, text).join("\n");
    assert!(report.contains("You have never died!"), "{report}");
    assert!(report.contains("Chess Rank: 7"), "{report}");
}

/// OpenAC `RetailUiRuntime.MountSalvage` and `SalvageUiController`: the
/// use of a carried salvaging tool opens the window on it, a dropped item
/// is staged and the button salvages it.
#[test]
#[ignore = "needs the retail dat files"]
fn a_used_salvage_tool_opens_the_window_and_salvages() {
    use ac_core::items::{ClientObject, ItemType};
    use ac_render::ui::salvage_window::SALVAGE_WINDOW;
    use ac_runtime::inventory::{ItemCommand, ObjectRef};
    use ac_runtime::session::SessionCommand;
    use ac_runtime::session::interface::{InterfaceEvent, InterfaceState, ItemsUpdate};
    use ac_ui::event::{DragPayload, ItemDragPayload, ItemDragSource, kind};
    use ac_ui::panels::salvage;
    use ac_ui::widgets::ItemList;
    const UST: u32 = 0x6000_0301;
    const SCRAP: u32 = 0x6000_0302;
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    assert!(ui.mounted().contains(&SALVAGE_WINDOW));
    assert!(!ui.host().root.is_window_visible(SALVAGE_WINDOW));
    let mut player = ClientObject::new(PLAYER);
    player.name = "+Horan".to_owned();
    player.item_type = ItemType::CREATURE;
    let mut ust = ClientObject::new(UST);
    ust.name = "Ust".to_owned();
    ust.item_type = ItemType::TINKERING_TOOL;
    ust.container_id = PLAYER;
    let mut scrap = ClientObject::new(SCRAP);
    scrap.name = "Gold Ring".to_owned();
    scrap.material_type = Some(12);
    scrap.container_id = PLAYER;
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Items(
        ItemsUpdate {
            reset: true,
            objects: vec![player, ust, scrap],
            removed: Vec::new(),
            contents: vec![(PLAYER, Some(vec![UST, SCRAP]))],
        },
    )));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::State(
        Box::new(InterfaceState {
            player: PLAYER,
            can_make_request: true,
            ..InterfaceState::default()
        }),
    )));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::OpenSalvage(
        UST,
    )));
    ui.feed(&mut scene);
    assert!(ui.host().root.is_window_visible(SALVAGE_WINDOW));
    let cell = {
        let root = &ui.host().root;
        let frame = root.window(SALVAGE_WINDOW).unwrap().frame;
        let list = root.find_descendant(frame, salvage::ITEM_LIST_ID).unwrap();
        root.widget::<ItemList>(list).unwrap().item(0).unwrap()
    };
    let payload = ItemDragPayload::new(SCRAP, ItemDragSource::Inventory, 0);
    ui.host_mut()
        .root
        .fire_event(kind::DROP_RELEASED, cell, Some(DragPayload::new(payload)));
    ui.host_mut().pump();
    ui.feed(&mut scene);
    let button = {
        let root = &ui.host().root;
        let frame = root.window(SALVAGE_WINDOW).unwrap().frame;
        root.find_descendant(frame, salvage::SALVAGE_BUTTON_ID)
            .unwrap()
    };
    assert!(ui.host().root.element(button).unwrap().enabled);
    ui.host_mut().root.fire_event(kind::CLICK, button, None);
    ui.host_mut().pump();
    ui.feed(&mut scene);
    let sent = ui.drain();
    assert!(
        sent.iter().any(|c| matches!(
            c,
            SessionCommand::Item(ItemCommand::Salvage { tool: ObjectRef::Guid(UST), items })
                if items == &[ObjectRef::Guid(SCRAP)]
        )),
        "{sent:?}"
    );
}

/// The salvage window takes an item dragged with the mouse from the
/// inventory (the live path: press, move, release).
#[test]
#[ignore = "needs the retail dat files"]
fn an_item_dragged_from_the_inventory_is_staged_for_salvage() {
    use ac_core::items::{ClientObject, ItemType};
    use ac_render::ui::salvage_window::SALVAGE_WINDOW;
    use ac_render::ui::window::{Hotkey, INVENTORY_WINDOW};
    use ac_runtime::session::interface::{InterfaceEvent, InterfaceState, ItemsUpdate};
    use ac_ui::panels::salvage::{self, SalvageController};
    use ac_ui::widgets::{ItemList, ItemSlot};
    const UST: u32 = 0x6000_0301;
    const RING: u32 = 0x6000_0302;
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut player = ClientObject::new(PLAYER);
    player.name = "+Horan".to_owned();
    player.item_type = ItemType::CREATURE;
    player.items_capacity = 102;
    player.containers_capacity = 7;
    let mut ust = ClientObject::new(UST);
    ust.name = "Ust".to_owned();
    ust.item_type = ItemType::TINKERING_TOOL;
    ust.container_id = PLAYER;
    ust.icon_id = 0x0600_11CB;
    let mut ring = ClientObject::new(RING);
    ring.name = "Circlet".to_owned();
    ring.item_type = ItemType::JEWELRY;
    ring.material_type = Some(59);
    ring.container_id = PLAYER;
    ring.icon_id = 0x0600_11CB;
    let ring_again = ring.clone();
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Items(
        ItemsUpdate {
            reset: true,
            objects: vec![player, ring, ust],
            removed: Vec::new(),
            contents: vec![(PLAYER, Some(vec![RING, UST]))],
        },
    )));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::State(
        Box::new(InterfaceState {
            player: PLAYER,
            can_make_request: true,
            ..InterfaceState::default()
        }),
    )));
    scene.apply(PresentationEvent::Interface(InterfaceEvent::OpenSalvage(
        UST,
    )));
    ui.feed(&mut scene);
    ui.run_hotkey(Hotkey::Inventory);
    ui.frame(Instant::now(), size);
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    let (from, to) = {
        let root = &ui.host().root;
        let inv = root.window(INVENTORY_WINDOW).unwrap().frame;
        let mut found = None;
        let mut stack = vec![inv];
        while let Some(e) = stack.pop() {
            if root
                .widget::<ItemSlot>(e)
                .is_some_and(|s| s.object_id == RING)
            {
                found = Some(e);
            }
            stack.extend(root.children(e).iter().copied());
        }
        let ring_cell = found.expect("the ring shows in the inventory");
        let frame = root.window(SALVAGE_WINDOW).unwrap().frame;
        let list = root.find_descendant(frame, salvage::ITEM_LIST_ID).unwrap();
        let cell = root.widget::<ItemList>(list).unwrap().item(0).unwrap();
        (root.screen_position(ring_cell), root.screen_position(cell))
    };
    let (fx, fy) = (from.0 as i32 + 16, from.1 as i32 + 16);
    let (tx, ty) = (to.0 as i32 + 16, to.1 as i32 + 16);
    let mut out = ui.script(&UiScript::Press { x: fx, y: fy });
    out.extend(ui.script(&UiScript::Move {
        x: fx - 20,
        y: fy - 20,
    }));
    out.extend(ui.script(&UiScript::Move {
        x: tx + 4,
        y: ty + 4,
    }));
    out.extend(ui.script(&UiScript::Move { x: tx, y: ty }));
    out.extend(ui.script(&UiScript::Release { x: tx, y: ty }));
    ui.handle(out);
    ui.feed(&mut scene);
    // The session updates the staged item (a new record in the window's
    // copy): it stays staged.
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Items(
        ItemsUpdate {
            reset: false,
            objects: vec![ring_again],
            removed: Vec::new(),
            contents: Vec::new(),
        },
    )));
    ui.feed(&mut scene);
    let staged = ui
        .host_mut()
        .with_controller::<SalvageController, _>(SALVAGE_WINDOW, |c, _| {
            c.staged().collect::<Vec<_>>()
        })
        .unwrap();
    assert_eq!(staged, [RING]);
}

/// OpenAC `MiniGameUiControllerTests` through the window: a used board
/// shows the Game Center, the game the session sends is drawn from the
/// player's side with the indicator lit, a click on a piece then a square
/// asks the session for the move, and a game that ends with no result
/// hides the window.
#[test]
#[ignore = "needs the retail dat files"]
fn a_used_board_shows_the_game_center_and_plays_through_the_session() {
    use ac_core::chess::{ChessBoard, ChessSquare, ChessTeam};
    use ac_render::ui::indicator_panels::{INDICATORS_WINDOW, MINI_GAME_WINDOW};
    use ac_runtime::gameplay::{ChessCommand, ChessStage, ChessView, GameplayCommand};
    use ac_runtime::session::SessionCommand;
    use ac_runtime::session::interface::InterfaceEvent;
    use ac_ui::draw::TextureRef;
    use ac_ui::event::kind;
    use ac_ui::panels::indicators::MINI_GAME_BUTTON_ID;
    use ac_ui::panels::mini_game::MiniGameController;
    use ac_ui::widgets::DatElement;
    const BOARD: u32 = 0x7A9B_4001;
    let mut ui = WindowUi::build(dats(), (1280, 720)).expect("the UI builds");
    assert!(ui.mounted().contains(&MINI_GAME_WINDOW));
    assert!(!ui.host().root.is_window_visible(MINI_GAME_WINDOW));
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::BeginGame(
        BOARD,
    )));
    ui.feed(&mut scene);
    assert!(ui.host().root.is_window_visible(MINI_GAME_WINDOW));

    let mut view = ChessView {
        revision: 1,
        stage: ChessStage::MyTurn,
        board_guid: BOARD,
        team: Some(ChessTeam::White),
        offering_stalemate: false,
        games_ended: 0,
        board: ChessBoard::standard(),
    };
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Chess(
        Box::new(view.clone()),
    )));
    ui.feed(&mut scene);
    let indicator_state = |ui: &WindowUi| {
        let root = &ui.host().root;
        let frame = root.window(INDICATORS_WINDOW).unwrap().frame;
        let button = root.find_descendant(frame, MINI_GAME_BUTTON_ID).unwrap();
        root.retail_state(button)
    };
    assert_eq!(indicator_state(&ui), Some(states::NORMAL));
    let squares = ui
        .host_mut()
        .with_controller::<MiniGameController, _>(MINI_GAME_WINDOW, |c, _| c.squares().to_vec())
        .unwrap();
    assert_eq!(squares.len(), 64);
    let image = |ui: &WindowUi, i: usize| {
        ui.host()
            .root
            .widget::<DatElement>(squares[i])
            .unwrap()
            .runtime_image
    };
    // e1, the white king, along the bottom.
    assert_eq!(image(&ui, 60), Some(Some(TextureRef::Surface(0x0600_4D02))));

    for square in [squares[52], squares[36]] {
        ui.host_mut().root.fire_event(kind::CLICK, square, None);
        ui.host_mut().pump();
        ui.feed(&mut scene);
    }
    let sent = ui.drain();
    assert!(
        sent.iter().any(|c| matches!(
            c,
            SessionCommand::Gameplay(GameplayCommand::Chess(ChessCommand::Move { from, to }))
                if *from == ChessSquare::new(4, 1) && *to == ChessSquare::new(4, 3)
        )),
        "{sent:?}"
    );

    view.revision = 2;
    view.stage = ChessStage::NotPlaying;
    view.board_guid = 0;
    view.team = None;
    view.games_ended = 1;
    scene.apply(PresentationEvent::Interface(InterfaceEvent::Chess(
        Box::new(view),
    )));
    ui.feed(&mut scene);
    assert!(!ui.host().root.is_window_visible(MINI_GAME_WINDOW));
    assert_eq!(indicator_state(&ui), Some(states::GHOSTED));
}
