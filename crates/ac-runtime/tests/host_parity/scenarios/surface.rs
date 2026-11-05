//! Selecting things, and the parts of the surface shaped like a window
//! (OpenAC `Scenarios/SurfaceParityTests.cs`). What is compared is
//! behaviour: what a plugin is answered and what it then hears.
//!
//! OpenAC catches exceptions to show that the window-shaped surfaces are
//! "quiet"; a WebAssembly plugin cannot be thrown at, so here every call
//! is answered with a value, and the values are compared. Of the calls the
//! import census marks window-dependent, only `client_set_clipboard`
//! answers differently here: a hotkey binds only under a window with a key
//! map, which the stand-in window does not have, so both arms answer it
//! unbound.

use ac_plugin::abi::calls;
use ac_plugin::abi::storage::{StorageKey, StoragePlace, StorageRoot, StorageWrite};
use ac_plugin::abi::ui::{HotkeyRegistration, KeyChord, LineSet, PanelRegistration};
use ac_plugin::abi::{Event, Import};

use crate::host_parity::arm::{Setup, run, run_with};
use crate::host_parity::transcript::Difference;
use crate::host_parity::world;

// OpenAC `SurfaceParityTests.SelectingAndClearingBehavesTheSameOnBothClients`.
#[test]
fn selecting_and_clearing_behaves_the_same_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        arm.mark_events();

        transcript.step("select a creature");
        let first = arm.call::<calls::SelectionSelect>(&world::MONSTER);
        let state = arm.call::<calls::SelectionState>(&());
        transcript.record("accepted", first);
        transcript.record("state", state.clone());
        assert!(first, "{} would not select the creature", arm.name());
        assert_eq!(
            (state.selected, state.previous),
            (Some(world::MONSTER), None)
        );

        transcript.step("select another");
        let second = arm.call::<calls::SelectionSelect>(&world::SECOND_MONSTER);
        let state = arm.call::<calls::SelectionState>(&());
        transcript.record("accepted", second);
        transcript.record("state", state.clone());
        assert!(second);
        assert_eq!(
            (state.selected, state.previous),
            (Some(world::SECOND_MONSTER), Some(world::MONSTER))
        );

        transcript.step("select the same again");
        let again = arm.call::<calls::SelectionSelect>(&world::SECOND_MONSTER);
        let state = arm.call::<calls::SelectionState>(&());
        transcript.record("accepted", again);
        transcript.record("state", state.clone());
        // Re-selecting what is selected changes nothing and says so.
        assert!(!again, "{} counted a re-selection as a change", arm.name());
        assert_eq!(state.selected, Some(world::SECOND_MONSTER));

        transcript.step("clear");
        let _ = arm.call::<calls::SelectionClear>(&());
        let state = arm.call::<calls::SelectionState>(&());
        transcript.record("state", state.clone());
        assert_eq!(
            (state.selected, state.previous),
            (None, Some(world::SECOND_MONSTER))
        );

        transcript.step("what the plugin was told");
        // The clear is heard on the pass after it.
        arm.sync();
        let seen: Vec<(Option<u32>, Option<u32>)> = arm
            .events()
            .into_iter()
            .filter_map(|e| match e {
                Event::SelectionChanged(change) => Some((change.previous, change.selected)),
                _ => None,
            })
            .collect();
        transcript.record("changes", &seen);
        // The two selections and the clear, and no more.
        assert_eq!(
            seen,
            [
                (None, Some(world::MONSTER)),
                (Some(world::MONSTER), Some(world::SECOND_MONSTER)),
                (Some(world::SECOND_MONSTER), None),
            ],
            "{}",
            arm.name()
        );
    });
}

fn data(key: &str) -> StorageKey {
    StorageKey {
        place: StoragePlace::global(StorageRoot::PluginData),
        key: key.to_owned(),
    }
}

// OpenAC `SurfaceParityTests.TheWindowShapedSurfacesAreInertAndSafeOnBothClients`.
#[test]
fn the_window_shaped_surfaces_are_inert_and_safe_on_both_clients() {
    let expected = [Difference {
        key: "clipboard/set",
        import: Import::ClientSetClipboard,
    }];
    run_with(&Setup::default(), &expected, true, |arm, transcript| {
        transcript.step("ui");
        let panel = arm.call::<calls::UiPanelRegister>(&PanelRegistration {
            window_id: "parity".to_owned(),
            title: "Parity".to_owned(),
            markup: "<panel/>".to_owned(),
            ..PanelRegistration::default()
        });
        transcript.record("panel", panel.is_ok());

        transcript.step("hotkeys");
        let registered = arm.call::<calls::UiHotkeyRegister>(&HotkeyRegistration {
            id: "parity.test".to_owned(),
            display_name: "Parity".to_owned(),
            chord: KeyChord::default(),
        });
        transcript.record("registered", &registered);
        // A plugin does something with what it is handed back.
        assert!(registered.is_ok(), "{}: {registered:?}", arm.name());
        transcript.record(
            "unregistered",
            arm.call::<calls::UiHotkeyUnregister>(&"parity.test".to_owned()),
        );

        transcript.step("window");
        transcript.record("minimized", arm.call::<calls::ClientIsMinimized>(&()));

        transcript.step("world lines");
        let layer = arm.call::<calls::UiLinesCreate>(&());
        transcript.record("layer", layer);
        transcript.record(
            "layer.used",
            arm.call::<calls::UiLinesSet>(&LineSet {
                layer,
                lines: Vec::new(),
            }),
        );
        transcript.record("layer.released", arm.call::<calls::UiLinesRemove>(&layer));

        transcript.step("clipboard");
        transcript.record(
            "set",
            arm.call::<calls::ClientSetClipboard>(&"parity".to_owned()),
        );

        transcript.step("storage");
        let missing = arm.call::<calls::StorageReadText>(&data("parity"));
        transcript.record("missing", &missing);
        let written = arm.call::<calls::StorageWrite>(&StorageWrite {
            place: StoragePlace::global(StorageRoot::PluginData),
            key: "parity".to_owned(),
            bytes: b"kept".to_vec(),
        });
        transcript.record("written", &written);
        // What a plugin saves is still there on both.
        let read_back = arm.call::<calls::StorageReadText>(&data("parity"));
        transcript.record("read.back", &read_back);
        assert_eq!(read_back, Ok(Some("kept".to_owned())), "{}", arm.name());
        let profiles = arm.call::<calls::StorageReadText>(&StorageKey {
            place: StoragePlace::global(StorageRoot::Profiles),
            key: "parity".to_owned(),
        });
        transcript.record("vtank", &profiles);
    });
}
