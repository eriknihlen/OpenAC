//! The Configure Keyboard screen in the game window on the retail dats.
//!
//! OpenAC `KeyboardConfigLiveMountProbeTests`: the
//! screen mounts, opens from its key, rebinds a key through a capture, and
//! OK saves the profile and the bindings mirror.

use std::path::PathBuf;
use std::sync::Arc;

use ac_dat::DatCollection;
use ac_render::ui::WindowUi;
use ac_render::ui::keyboard_window::{KEYBOARD_WINDOW, KeyboardFiles, KeyboardRequest};
use ac_ui::event::{Key, Modifiers};
use ac_ui::input::{InputAction, KeyBindings, KeyChord};
use ac_ui::panels::keyboard_config::{self as kb, KeyboardConfigController};
use ac_ui::root::ElementMessage;
use ac_ui::tree::ElementId;

fn dats() -> Arc<DatCollection> {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Arc::new(DatCollection::open(dir).expect("open the retail dats"))
}

fn click(ui: &mut WindowUi, element: ElementId) {
    ui.host_mut()
        .with_controller::<KeyboardConfigController, _>(KEYBOARD_WINDOW, |c, ui| {
            assert!(c.handle(ui, element, &ElementMessage::Clicked { x: 0, y: 0 }));
        });
    let commands = ui.drain();
    assert!(commands.is_empty(), "{commands:?}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_configure_keyboard_screen_rebinds_a_key_and_saves_it() {
    let dir = std::env::temp_dir().join(format!(
        "openac-keyboard-config-test-{}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    let files = KeyboardFiles {
        bindings_file: dir.join("keybinds.json"),
        keymap_directory: dir.join("keymaps"),
    };
    let mut ui = WindowUi::build(dats(), (1280, 720)).expect("the UI builds");
    ui.attach_key_bindings(&KeyBindings::retail_defaults(), Some(files.clone()));
    assert!(
        ui.mounted().contains(&KEYBOARD_WINDOW),
        "{:?}",
        ui.take_notices()
    );
    assert!(!ui.host().root.is_window_visible(KEYBOARD_WINDOW));
    assert!(ui.handle_input_action(InputAction::ToggleKeyboardConfiguration));
    assert!(ui.host().root.is_window_visible(KEYBOARD_WINDOW));

    // The forward row's third key button: capture F13.
    let button = ui
        .host_mut()
        .with_controller::<KeyboardConfigController, _>(KEYBOARD_WINDOW, |c, _| {
            c.rows()
                .iter()
                .find(|r| r.input_map == 4 && r.action_id == 0x29)
                .expect("the forward row")
                .key_buttons[2]
        })
        .unwrap();
    click(&mut ui, button);
    assert_eq!(ui.take_keyboard_requests(), [KeyboardRequest::BeginCapture]);
    let f13 = KeyChord::key(Key::F13, Modifiers::NONE);
    assert!(!ui.keyboard_captured(Some(f13)));
    let _ = ui.drain();
    let requests = ui.take_keyboard_requests();
    let Some(KeyboardRequest::Apply(live)) = requests.last() else {
        panic!("the new bindings go live: {requests:?}");
    };
    assert!(
        live.for_action(InputAction::MovementForward)
            .any(|b| b.chord == f13)
    );

    // OK: saved to the active profile, mirrored to the bindings file, and
    // the screen closes.
    let root = ui.host().root.window(KEYBOARD_WINDOW).unwrap().frame;
    let ok = ui
        .host()
        .root
        .find_descendant(root, kb::OK_BUTTON_ID)
        .expect("the OK button");
    click(&mut ui, ok);
    assert!(!ui.host().root.is_window_visible(KEYBOARD_WINDOW));
    let profile = std::fs::read_to_string(files.keymap_directory.join("openac.keymap"))
        .expect("the profile was written");
    assert!(profile.contains("DIK_F13"), "{profile}");
    let (saved, error) = KeyBindings::load_or_default(&files.bindings_file);
    assert!(error.is_none(), "{error:?}");
    assert!(
        saved
            .for_action(InputAction::MovementForward)
            .any(|b| b.chord == f13)
    );
    let _ = std::fs::remove_dir_all(&dir);
}
