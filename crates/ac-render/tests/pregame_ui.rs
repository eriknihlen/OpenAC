//! The pre-game screens in the game window against the retail dats: a
//! window attached at character selection shows the selection screen
//! (the in-game windows hidden, on the 800x600 canvas scaled to the window),
//! draws only textures the dats hold, keeps Enter from the chat entry, and
//! turns a click on the authored ENTER button into the session's enter
//! request. Ignored by default; `scripts/check.*` runs them when the dats
//! are present.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_dat::DatCollection;
use ac_render::scene::Scene;
use ac_render::ui::batch::UiTexture;
use ac_render::ui::input::{self as ui_input, Routed};
use ac_render::ui::window::UiScript;
use ac_render::ui::{UiBatch, UiTextureSource, WindowUi};
use ac_runtime::session::pregame::{ConnectionStatus, ConnectionView, PreGameEvent};
use ac_runtime::session::selection::{
    SelectionButtons, SelectionEntry, SelectionLifecycle, SelectionOperation, SelectionSnapshot,
};
use ac_runtime::session::{Generation, LifecycleCommand, PresentationEvent, SessionCommand};
use ac_ui::draw::TextureRef;
use ac_ui::event::{Key, Modifiers};

fn dats() -> Arc<DatCollection> {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Arc::new(DatCollection::open(dir).expect("open the retail dats"))
}

fn at_selection() -> Scene {
    let mut scene = Scene::default();
    scene.apply(PresentationEvent::PreGame(PreGameEvent::Connection(
        ConnectionView::advance(
            None,
            ConnectionStatus::Ready,
            None,
            Instant::now() - Duration::from_secs(60),
            false,
        ),
    )));
    let entry = SelectionEntry {
        active_index: 0,
        character_id: 0x5000_0001,
        name: "+Horan".into(),
        seconds_greyed_out: 0,
    };
    scene.apply(PresentationEvent::PreGame(PreGameEvent::Selection(
        Box::new(SelectionSnapshot {
            generation: Generation(2),
            lifecycle: SelectionLifecycle::AwaitingSelection,
            revision: 1,
            account_name: "testaccount2".into(),
            slot_count: 11,
            entries: vec![entry],
            world_name: "ACEmulator".into(),
            server_population: None,
            highlighted_character_id: 0x5000_0001,
            highlighted_display_index: Some(0),
            pending_delete_character_id: 0,
            last_restore_requested_character_id: 0,
            operation: SelectionOperation::None,
            error: None,
            buttons: SelectionButtons {
                can_enter: true,
                can_delete: true,
                can_restore: false,
                delete_visible: true,
                restore_visible: false,
                can_create: true,
            },
        }),
    )));
    scene
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_window_at_character_selection_shows_the_screen_and_enters_from_it() {
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    assert!(
        ui.mounted()
            .contains(&ac_render::ui::pregame::PREGAME_SCREENS)
    );
    let notices = ui.take_notices();
    assert!(notices.is_empty(), "{notices:?}");
    let mut scene = at_selection();
    ui.feed(&mut scene);
    let list = ui.frame(Instant::now(), size);
    assert!(ui.pregame_active());
    assert_eq!(ui.host().root.fixed_canvas(), Some((800.0, 600.0)));

    // Every texture drawn is in the dats.
    let batch = UiBatch::build(&list);
    assert_eq!(batch.skipped_zero_ids, 0);
    let mut surfaces = ui.surfaces();
    for run in &batch.runs {
        if let UiTexture::Named(t @ TextureRef::Surface(id)) = run.key.texture {
            assert!(surfaces.decode(t).is_some(), "{id:#010x} decodes");
        }
    }

    // Enter with nothing focused does not open the chat entry here.
    let down: Routed<u32> = ui_input::key(
        ui.host_mut(),
        0,
        Key::Enter,
        true,
        false,
        Modifiers::NONE,
        None,
    );
    ui.handle(down.outputs);
    assert_eq!(ui.host().root.keyboard_focus(), None);

    // The probe names the screen's buttons in window pixels; a click on
    // ENTER asks the session to enter with the highlighted character.
    ui.script(&UiScript::Probe);
    let lines = ui.take_notices();
    assert!(
        lines
            .iter()
            .any(|l| l.starts_with("ui-probe: character 0 ")),
        "{lines:?}"
    );
    let enter = lines
        .iter()
        .find(|l| l.contains("button characters 0x100003a2"))
        .unwrap_or_else(|| panic!("the enter button: {lines:?}"));
    let numbers: Vec<i32> = enter
        .split_whitespace()
        .skip(4)
        .take(4)
        .map(|n| n.parse().unwrap())
        .collect();
    let (x, y) = (numbers[0] + numbers[2] / 2, numbers[1] + numbers[3] / 2);
    let outputs = ui.script(&UiScript::Click {
        x,
        y,
        button: 0,
        double: false,
    });
    let commands = ui.handle(outputs);
    assert!(
        matches!(
            commands.as_slice(),
            [SessionCommand::Lifecycle(LifecycleCommand::Enter(None))]
        ),
        "{commands:?}"
    );
    // The wait shows over the screen.
    ui.feed(&mut scene);
    assert!(ui.pregame().unwrap().dialogs().is_open());
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_credits_draw_their_pictures_and_text_over_the_window() {
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut scene = at_selection();
    ui.feed(&mut scene);
    let start = Instant::now();
    ui.frame(start, size);
    let credits = ui
        .pregame()
        .unwrap()
        .characters()
        .unwrap()
        .button(ac_ui::pregame::characters::CREDITS_ID)
        .unwrap();
    ui.host_mut()
        .root
        .fire_event(ac_ui::event::kind::CLICK, credits, None);
    ui.host_mut().pump();
    let outputs = ui.host_mut().take_outputs();
    ui.handle(outputs);
    assert!(ui.pregame().unwrap().credits().unwrap().is_active());
    std::thread::sleep(Duration::from_millis(1500));
    ui.feed(&mut scene);
    let list = ui.frame(Instant::now(), size);
    let batch = UiBatch::build(&list);
    let picture = batch
        .runs
        .iter()
        .find(|r| {
            matches!(r.key.texture, UiTexture::Named(TextureRef::Surface(id)) if (0x0600_5F14..=0x0600_5F1A).contains(&id))
        })
        .expect("a picture draws");
    let first = &batch.vertices[picture.first as usize];
    // No in-game window shows through the credits' open half.
    let root = &ui.host().root;
    for w in root.windows() {
        assert!(!root.is_window_visible(&w.name), "{} shows", w.name);
    }
    let mut surfaces = ui.surfaces();
    if let UiTexture::Named(t) = picture.key.texture {
        let pixels = surfaces.decode(t).expect("the picture decodes");
        assert_eq!(pixels.size(), (400, 300));
    }
    eprintln!("picture vertex {:?} count {}", first, picture.count);
}

#[test]
#[ignore = "needs the retail dat files"]
fn no_in_game_window_shows_under_the_selection_screen() {
    // Retail has no in-game windows before the world: they are hidden while
    // a pre-game screen shows and come back where they were in the world.
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let vitals = ac_render::ui::window::VITALS_WINDOW;
    assert!(ui.host().root.is_window_visible(vitals));
    let mut scene = at_selection();
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    assert!(ui.pregame_active());
    let root = &ui.host().root;
    for w in root.windows() {
        assert!(!root.is_window_visible(&w.name), "{} shows", w.name);
    }
    assert!(ui.hidden_under_pregame().iter().any(|n| n == vitals));

    let mut in_world = scene.pregame().selection.clone().expect("a selection");
    in_world.lifecycle = SelectionLifecycle::InWorld;
    scene.apply(PresentationEvent::PreGame(PreGameEvent::Selection(
        Box::new(in_world),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    assert!(!ui.pregame_active());
    assert!(ui.host().root.is_window_visible(vitals));
    assert!(ui.hidden_under_pregame().is_empty());
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_plugin_dock_stays_hidden_under_the_selection_screen_and_comes_back_in_the_world() {
    use ac_plugin_ui::{BindingSchema, PanelDescriptor, PluginUi, UiOwner};
    use ac_runtime::session::interface::InterfaceEvent;
    let dock = ac_ui::plugin::dock::WINDOW;
    let size = (1280, 720);
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    let mut plugins = PluginUi::new();
    plugins.set_window_attached(true);
    plugins
        .register_panel(
            &UiOwner::new("test.demo", "Demo"),
            PanelDescriptor::new("main", "Demo"),
            "<panel/>",
            BindingSchema::new("Demo"),
        )
        .unwrap();
    let mut scene = at_selection();
    scene.apply(PresentationEvent::Interface(InterfaceEvent::PluginUi(
        plugins.snapshot(),
    )));
    // Several frames: the screen hides it once, and it must not come back.
    for _ in 0..3 {
        ui.feed(&mut scene);
        ui.frame(Instant::now(), size);
        assert!(ui.pregame_active());
        assert!(
            !ui.host().root.is_window_visible(dock),
            "the dock shows under the selection screen"
        );
    }

    let mut in_world = scene.pregame().selection.clone().expect("a selection");
    in_world.lifecycle = SelectionLifecycle::InWorld;
    scene.apply(PresentationEvent::PreGame(PreGameEvent::Selection(
        Box::new(in_world),
    )));
    ui.feed(&mut scene);
    ui.frame(Instant::now(), size);
    assert!(!ui.pregame_active());
    assert!(ui.host().root.is_window_visible(dock));
}

/// The session's identity snapshot as the character starts entering: who
/// the player is, before the server has created him.
fn horan_entering() -> PresentationEvent {
    PresentationEvent::Snapshot(ac_runtime::world::WorldSnapshot {
        player_guid: Some(0x5000_0001),
        objects: Vec::new(),
    })
}

/// The player's object at Holtburg, created by the server later.
fn horan_created() -> PresentationEvent {
    use ac_proto::{ModelData, Position, Quaternion, Vector3};
    use ac_runtime::world::{ObjectDelta, ObjectKey, ObjectView, RadarFacts};
    PresentationEvent::Deltas(vec![ObjectDelta::Upsert(Box::new(ObjectView {
        key: ObjectKey {
            local_id: 1,
            incarnation: 1,
        },
        guid: 0x5000_0001,
        name: Some("+Horan".into()),
        setup_id: None,
        position: Some(Position {
            cell_id: 0xA9B4_0031,
            origin: Vector3 {
                x: 100.0,
                y: 100.0,
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
    }))])
}

/// The selection screen's lifecycle moves on (a new snapshot revision).
fn set_lifecycle(scene: &mut Scene, lifecycle: SelectionLifecycle) {
    let mut selection = scene.pregame().selection.clone().expect("a selection");
    selection.lifecycle = lifecycle;
    selection.revision += 1;
    scene.apply(PresentationEvent::PreGame(PreGameEvent::Selection(
        Box::new(selection),
    )));
}

/// The in-game windows that show, by name.
fn shown_windows(ui: &WindowUi) -> Vec<String> {
    let root = &ui.host().root;
    let mut names: Vec<String> = root
        .windows()
        .iter()
        .filter(|w| root.is_window_visible(&w.name))
        .map(|w| w.name.clone())
        .collect();
    names.sort();
    names
}

/// A reported bug: after logging out to character
/// selection and entering again, the inventory, the status (indicator)
/// bar, the chat and the radar were missing; the combat bar (whose
/// visibility the game decides, never saved) showed. The windows hidden
/// under the selection screen were saved hidden in the
/// character's layout, as if the player had closed them, and the second
/// entry restored that layout once the player's object arrived, after the
/// screen had shown them again (before the fix this showed only the
/// vitals). OpenAC's layouts stop counting window changes as the player's,
/// before anything else moves in a frame, once the game no longer shows
/// (`RetailUiRuntime.Tick`: `SetGameplayActive(IsGameplayDisplay)`, the
/// character in the world), and its screens hide no window: a second entry
/// shows what the first did, and a window built later for the same
/// character restores the same.
#[test]
#[ignore = "needs the retail dat files"]
fn a_return_to_selection_and_a_second_entry_show_the_windows_the_first_entry_showed() {
    use ac_render::ui::window::{CHAT_WINDOW, RADAR_WINDOW, TOOLBAR_WINDOW};
    let size = (1280, 720);
    let dir = std::env::temp_dir().join(format!(
        "openac-relogin-layout-{}-{:?}",
        std::process::id(),
        std::thread::current().id()
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    let settings = dir.join("settings.json");
    let store = || Some(ac_host::settings::SettingsStore::new(settings.clone()));
    let frames = |ui: &mut WindowUi, scene: &mut Scene, n: usize| {
        for _ in 0..n {
            ui.feed(scene);
            ui.frame(Instant::now(), size);
        }
    };
    // An entry as the session presents it: the identity as the character
    // starts entering (the screen still up), the world once the server
    // says the character is in it, his object after that.
    let enter = |ui: &mut WindowUi, scene: &mut Scene| {
        set_lifecycle(scene, SelectionLifecycle::EnteringWorld);
        scene.apply(horan_entering());
        frames(ui, scene, 1);
        assert!(ui.pregame_active());
        set_lifecycle(scene, SelectionLifecycle::InWorld);
        frames(ui, scene, 1);
        assert!(!ui.pregame_active());
        scene.apply(horan_created());
        frames(ui, scene, 3);
    };
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    ui.set_layout_store(store());

    // The first entry from the selection screen.
    let mut scene = at_selection();
    frames(&mut ui, &mut scene, 2);
    assert!(ui.pregame_active());
    enter(&mut ui, &mut scene);
    let first = shown_windows(&ui);
    for name in [CHAT_WINDOW, RADAR_WINDOW, TOOLBAR_WINDOW] {
        assert!(first.iter().any(|n| n == name), "{name} shows: {first:?}");
    }

    // The log out completes: the session's new generation forgets the
    // player and shows the roster, in the same pass.
    scene.apply(PresentationEvent::Snapshot(
        ac_runtime::world::WorldSnapshot {
            player_guid: None,
            objects: Vec::new(),
        },
    ));
    set_lifecycle(&mut scene, SelectionLifecycle::AwaitingSelection);
    frames(&mut ui, &mut scene, 3);
    assert!(ui.pregame_active());
    assert!(shown_windows(&ui).is_empty(), "{:?}", shown_windows(&ui));

    // The second entry.
    enter(&mut ui, &mut scene);
    assert_eq!(shown_windows(&ui), first, "the second entry");

    // The character's saved layout is what the player left: a window built
    // later for him shows the same.
    let mut later = WindowUi::build(dats(), size).expect("the UI builds");
    later.set_layout_store(store());
    let mut scene = at_selection();
    frames(&mut later, &mut scene, 1);
    enter(&mut later, &mut scene);
    assert_eq!(shown_windows(&later), first, "a later window");
    let _ = std::fs::remove_dir_all(&dir);
}

/// A live run: a character launched from the launcher, its
/// window opened 0.44 s after the character was in the world, and within a
/// second the character's saved layout had the chat, the radar, the
/// toolbar and the indicators saved hidden. A window attaching to a session
/// already in the world is sent the world first and then the pre-game
/// screens as they stand: the connecting screen, still inside its two
/// seconds, covers the world for a moment. Its hiding the in-game windows
/// is not the player's choice (OpenAC's layouts count a change as the
/// player's only while the game shows, `RetailUiRuntime.Tick`:
/// `SetGameplayActive(IsGameplayDisplay)` first each frame, and its screens
/// hide no window): the saved layout stays as it was, and the windows it
/// saved shown show once the world does.
#[test]
#[ignore = "needs the retail dat files"]
fn a_window_attaching_in_the_world_under_the_connecting_screen_keeps_the_saved_layout() {
    use ac_render::ui::window::{CHAT_WINDOW, RADAR_WINDOW, TOOLBAR_WINDOW};
    let size = (1280, 720);
    let dir = std::env::temp_dir().join(format!(
        "openac-late-attach-layout-{}-{:?}",
        std::process::id(),
        std::thread::current().id()
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    let settings = dir.join("settings.json");
    let store = || Some(ac_host::settings::SettingsStore::new(settings.clone()));
    let frames = |ui: &mut WindowUi, scene: &mut Scene, n: usize| {
        for _ in 0..n {
            ui.feed(scene);
            ui.frame(Instant::now(), size);
        }
    };

    // The character's layout, saved by an entry from the selection screen.
    let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
    ui.set_layout_store(store());
    let mut scene = at_selection();
    frames(&mut ui, &mut scene, 2);
    set_lifecycle(&mut scene, SelectionLifecycle::EnteringWorld);
    scene.apply(horan_entering());
    frames(&mut ui, &mut scene, 1);
    set_lifecycle(&mut scene, SelectionLifecycle::InWorld);
    scene.apply(horan_created());
    frames(&mut ui, &mut scene, 3);
    let saved = shown_windows(&ui);
    for name in [CHAT_WINDOW, RADAR_WINDOW, TOOLBAR_WINDOW] {
        assert!(saved.iter().any(|n| n == name), "{name} shows: {saved:?}");
    }
    drop(ui);
    let before = std::fs::read_to_string(&settings).expect("the layout was saved");

    // A window attaches to the session already in the world, as the
    // session's attach sends it: the world, then the pre-game screens (the
    // connecting screen started a second ago, the selection in the world).
    let late = |check_saved: bool| {
        let mut ui = WindowUi::build(dats(), size).expect("the UI builds");
        ui.set_layout_store(store());
        let mut scene = Scene::default();
        scene.apply(horan_entering());
        scene.apply(horan_created());
        scene.apply(PresentationEvent::PreGame(PreGameEvent::Connection(
            ConnectionView::advance(
                None,
                ConnectionStatus::Ready,
                None,
                Instant::now() - Duration::from_secs(1),
                // In the world the session no longer enters by itself.
                false,
            ),
        )));
        let mut selection = at_selection()
            .pregame()
            .selection
            .clone()
            .expect("a selection");
        selection.lifecycle = SelectionLifecycle::InWorld;
        scene.apply(PresentationEvent::PreGame(PreGameEvent::Selection(
            Box::new(selection),
        )));
        frames(&mut ui, &mut scene, 2);
        assert!(
            ui.pregame_active(),
            "the connecting screen covers the world"
        );
        if check_saved {
            assert_eq!(
                std::fs::read_to_string(&settings).unwrap(),
                before,
                "the screen's hiding is not saved"
            );
        }
        let deadline = Instant::now() + Duration::from_secs(5);
        while ui.pregame_active() {
            assert!(Instant::now() < deadline, "the connecting screen stays");
            std::thread::sleep(Duration::from_millis(50));
            frames(&mut ui, &mut scene, 1);
        }
        frames(&mut ui, &mut scene, 3);
        shown_windows(&ui)
    };
    assert_eq!(late(true), saved, "the window that attached late");
    assert_eq!(late(false), saved, "a window built after it");
    let _ = std::fs::remove_dir_all(&dir);
}
