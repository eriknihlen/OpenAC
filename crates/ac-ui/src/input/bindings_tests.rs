//! OpenAC `tests/AcDream.UI.Abstractions.Tests/Input/KeyBindingsTests.cs`,
//! `KeyBindingsRetailTests.cs` and `KeyBindingsJsonTests.cs`. The cases on
//! OpenAC's legacy WASD preset (`AcdreamCurrentDefaults`, kept there only as
//! a regression anchor) are not ported.

use std::path::PathBuf;

use super::*;

fn chord(key: Key, modifiers: Modifiers) -> KeyChord {
    KeyChord::key(key, modifiers)
}

fn bare(key: Key) -> KeyChord {
    chord(key, Modifiers::NONE)
}

fn actions(b: &KeyBindings, action: InputAction) -> Vec<Binding> {
    b.for_action(action).copied().collect()
}

// ── KeyBindingsTests ───────────────────────────────────────────────────

#[test]
fn add_remove_clear() {
    let mut b = KeyBindings::new();
    let binding = Binding::new(bare(Key::W), A::MovementForward);
    b.add(binding);
    assert_eq!(b.all(), &[binding]);
    assert!(b.remove(&binding));
    assert!(b.all().is_empty());
    b.add(binding);
    b.add(Binding::new(bare(Key::S), A::MovementBackup));
    b.clear();
    assert!(b.all().is_empty());
}

#[test]
fn find_returns_matching_binding_by_chord_and_activation() {
    let mut b = KeyBindings::new();
    let press = Binding::new(bare(Key::W), A::MovementForward);
    let release = press.activation(ActivationType::Release);
    b.add(press);
    b.add(release);
    assert_eq!(b.find(bare(Key::W), ActivationType::Press), Some(press));
    assert_eq!(b.find(bare(Key::W), ActivationType::Release), Some(release));
    assert_eq!(b.find(bare(Key::S), ActivationType::Press), None);
}

#[test]
fn retail_defaults_resolve_shared_number_key_by_scope() {
    let b = KeyBindings::retail_defaults();
    let one = bare(Key::Number1);
    assert_eq!(
        b.find_in(one, ActivationType::Press, InputScope::Game)
            .map(|b| b.action),
        Some(A::UseQuickSlot_1)
    );
    assert_eq!(
        b.find_in(one, ActivationType::Press, InputScope::MagicCombat)
            .map(|b| b.action),
        Some(A::UseSpellSlot_1)
    );
}

#[test]
fn for_action_returns_all_chords_bound_to_action() {
    let mut b = KeyBindings::new();
    b.add(Binding::new(bare(Key::W), A::MovementForward));
    b.add(Binding::new(bare(Key::Up), A::MovementForward));
    b.add(Binding::new(bare(Key::S), A::MovementBackup));
    let forward = actions(&b, A::MovementForward);
    assert_eq!(forward.len(), 2);
    assert!(forward.iter().any(|x| x.chord.key == InputKey::Key(Key::W)));
    assert!(
        forward
            .iter()
            .any(|x| x.chord.key == InputKey::Key(Key::Up))
    );
}

// ── KeyBindingsRetailTests ─────────────────────────────────────────────

#[test]
fn movement_keys_are_the_retail_ones() {
    let b = KeyBindings::retail_defaults();
    let fwd = actions(&b, A::MovementForward);
    assert!(fwd.iter().any(|x| x.chord == bare(Key::W)));
    assert!(fwd.iter().any(|x| x.chord == bare(Key::Up)));
    let back = actions(&b, A::MovementBackup);
    assert!(back.iter().any(|x| x.chord == bare(Key::X)));
    assert!(back.iter().any(|x| x.chord == bare(Key::Down)));
    assert!(!back.iter().any(|x| x.chord.key == InputKey::Key(Key::S)));
    assert_eq!(
        b.find(bare(Key::S), ActivationType::Press)
            .map(|b| b.action),
        Some(A::MovementStop)
    );
    let left = actions(&b, A::MovementStrafeLeft);
    assert!(left.iter().any(|x| x.chord == bare(Key::Z)));
    assert!(
        left.iter()
            .any(|x| x.chord == chord(Key::A, Modifiers::ALT))
    );
    assert!(
        left.iter()
            .any(|x| x.chord == chord(Key::Left, Modifiers::ALT))
    );
    let right = actions(&b, A::MovementStrafeRight);
    assert!(right.iter().any(|x| x.chord == bare(Key::C)));
    assert!(
        right
            .iter()
            .any(|x| x.chord == chord(Key::D, Modifiers::ALT))
    );
    assert!(
        right
            .iter()
            .any(|x| x.chord == chord(Key::Right, Modifiers::ALT))
    );
    assert!(
        actions(&b, A::Sleeping)
            .iter()
            .any(|x| x.chord == bare(Key::B))
    );
}

#[test]
fn movement_walk_mode_uses_hold_activation() {
    let b = KeyBindings::retail_defaults();
    let binds = actions(&b, A::MovementWalkMode);
    assert_eq!(binds.len(), 1);
    assert_eq!(binds[0].activation, ActivationType::Hold);
    assert_eq!(binds[0].chord, bare(Key::ShiftLeft));
}

#[test]
fn tab_escape_and_shift_escape_are_retail() {
    let b = KeyBindings::retail_defaults();
    let action = |c| b.find(c, ActivationType::Press).map(|b| b.action);
    assert_eq!(action(bare(Key::Tab)), Some(A::ToggleChatEntry));
    assert_eq!(
        action(chord(Key::Escape, Modifiers::SHIFT)),
        Some(A::LOGOUT)
    );
    assert_eq!(action(bare(Key::Escape)), Some(A::EscapeKey));
    assert_eq!(action(bare(Key::F1)), Some(A::ToggleHelp));
    assert_eq!(action(bare(Key::F11)), Some(A::ToggleOptionsPanel));
}

#[test]
fn select_right_is_a_release_completed_click() {
    let b = KeyBindings::retail_defaults();
    let binds = actions(&b, A::SelectRight);
    assert_eq!(binds.len(), 1);
    assert_eq!(binds[0].chord.device, 1);
    assert_eq!(binds[0].chord.key, InputKey::Mouse(mouse::RIGHT));
    assert_eq!(binds[0].activation, ActivationType::Click);
}

#[test]
fn combat_attack_height_bindings_emit_hold_transitions() {
    let b = KeyBindings::retail_defaults();
    for (key, action) in [
        (Key::Delete, A::CombatLowAttack),
        (Key::End, A::CombatMediumAttack),
        (Key::PageDown, A::CombatHighAttack),
    ] {
        assert_eq!(
            b.find(bare(key), ActivationType::Hold).map(|b| b.action),
            Some(action)
        );
    }
    for action in [A::CombatAimLow, A::CombatAimMedium, A::CombatAimHigh] {
        let binds = actions(&b, action);
        assert_eq!(binds.len(), 1);
        assert_eq!(binds[0].activation, ActivationType::Hold);
        assert_eq!(binds[0].scope, InputScope::MissileCombat);
    }
}

#[test]
fn quick_slots_are_retail() {
    let b = KeyBindings::retail_defaults();
    let action = |c| b.find(c, ActivationType::Press).map(|b| b.action);
    assert_eq!(action(bare(Key::Number5)), Some(A::UseQuickSlot_5));
    assert_eq!(
        action(chord(Key::Number5, Modifiers::CTRL)),
        Some(A::UseQuickSlot_5)
    );
    assert!(actions(&b, A::SelectQuickSlot_5).is_empty());
    assert!(
        actions(&b, A::UseQuickSlot_18)
            .iter()
            .any(|x| x.chord == chord(Key::Number9, Modifiers::ALT))
    );
}

#[test]
fn plugin_manager_debug_actions_and_misc_retail_keys() {
    let b = KeyBindings::retail_defaults();
    assert!(actions(&b, A::TogglePluginManager).iter().any(|x| {
        x.chord.key == InputKey::Key(Key::F1)
            && x.chord.modifiers.contains(Modifiers::SHIFT)
            && x.chord.modifiers.contains(Modifiers::CTRL)
    }));
    for (key, action) in [
        (Key::F1, A::AcdreamToggleDebugPanel),
        (Key::F2, A::AcdreamToggleCollisionWires),
        (Key::F3, A::AcdreamDumpNearby),
    ] {
        assert!(
            actions(&b, action)
                .iter()
                .any(|x| x.chord == chord(key, Modifiers::CTRL))
        );
    }
    assert!(b.all().len() >= 80, "{} bindings", b.all().len());
    assert!(actions(&b, A::Cry).iter().any(|x| x.chord == bare(Key::U)));
    assert!(
        actions(&b, A::CombatToggleCombat)
            .iter()
            .any(|x| x.chord == bare(Key::GraveAccent))
    );
    let alternate = actions(&b, A::CameraActivateAlternateMode);
    assert!(alternate.iter().any(|x| x.chord == bare(Key::F2)));
    assert!(alternate.iter().any(|x| x.chord == bare(Key::KeypadDivide)));
    assert!(
        alternate
            .iter()
            .all(|x| x.activation == ActivationType::Hold)
    );
    assert!(
        actions(&b, A::ScrollUp)
            .iter()
            .any(|x| x.chord == chord(Key::Up, Modifiers::CTRL))
    );
}

#[test]
fn no_default_binding_reaches_the_free_fly_camera() {
    let b = KeyBindings::retail_defaults();
    assert!(!b.all().iter().any(|x| x.action == A::AcdreamToggleFlyMode));
}

// ── KeyBindingsJsonTests ───────────────────────────────────────────────

struct TempFile(PathBuf);

impl TempFile {
    fn new(name: &str) -> Self {
        let path = std::env::temp_dir().join(format!(
            "openac_keybinds_{name}_{}_{}.json",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos())
        ));
        Self(path)
    }
}

impl Drop for TempFile {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

fn load_json(text: &str) -> KeyBindings {
    KeyBindings::parse(text.as_bytes()).expect("a bindings document")
}

#[test]
fn a_missing_file_is_the_retail_defaults() {
    let file = TempFile::new("missing");
    let (loaded, error) = KeyBindings::load_or_default(&file.0);
    assert!(error.is_none());
    assert_eq!(loaded, KeyBindings::retail_defaults());
}

#[test]
fn round_trip_preserves_every_binding() {
    let file = TempFile::new("roundtrip");
    let original = KeyBindings::retail_defaults();
    original.save_to_file(&file.0).unwrap();
    let (loaded, error) = KeyBindings::load_or_default(&file.0);
    assert!(error.is_none(), "{error:?}");
    assert_eq!(loaded.all().len(), original.all().len());
    for b in original.all() {
        assert!(loaded.all().contains(b), "{b:?}");
    }
}

#[test]
fn a_corrupt_file_is_the_defaults_and_is_left_alone() {
    let file = TempFile::new("corrupt");
    const CORRUPT: &str = "{ this is not valid JSON :: garbage";
    std::fs::write(&file.0, CORRUPT).unwrap();
    let (loaded, error) = KeyBindings::load_or_default(&file.0);
    assert!(error.is_some());
    assert_eq!(loaded, KeyBindings::retail_defaults());
    assert_eq!(std::fs::read_to_string(&file.0).unwrap(), CORRUPT);
}

#[test]
fn a_custom_binding_for_one_action_keeps_defaults_for_others() {
    let loaded =
        load_json(r#"{ "version": 6, "actions": { "MovementForward": [ { "key": "Q" } ] } }"#);
    let fwd = actions(&loaded, A::MovementForward);
    assert_eq!(fwd.len(), 1);
    assert_eq!(fwd[0].chord, bare(Key::Q));
    assert!(
        actions(&loaded, A::MovementBackup)
            .iter()
            .any(|x| x.chord == bare(Key::X))
    );
}

#[test]
fn round_trip_preserves_an_explicitly_unbound_retail_action() {
    let file = TempFile::new("unbound");
    let mut customized = KeyBindings::new();
    for b in KeyBindings::retail_defaults().all() {
        if b.action != A::ToggleHelp {
            customized.add(*b);
        }
    }
    customized.save_to_file(&file.0).unwrap();
    let (loaded, _) = KeyBindings::load_or_default(&file.0);
    assert!(actions(&loaded, A::ToggleHelp).is_empty());
    assert!(!actions(&loaded, A::ToggleOptionsPanel).is_empty());
}

#[test]
fn a_version_zero_file_still_loads() {
    let loaded = load_json(
        r#"{ "version": 0, "actions": { "MovementForward": [ { "key": "W", "mod": "None" } ] } }"#,
    );
    assert_eq!(actions(&loaded, A::MovementForward).len(), 1);
    assert!(!actions(&loaded, A::MovementBackup).is_empty());
}

#[test]
fn hold_activation_is_preserved() {
    let file = TempFile::new("hold");
    let mut original = KeyBindings::new();
    original.add(
        Binding::new(bare(Key::ShiftLeft), A::MovementWalkMode).activation(ActivationType::Hold),
    );
    original.save_to_file(&file.0).unwrap();
    let (loaded, _) = KeyBindings::load_or_default(&file.0);
    assert!(actions(&loaded, A::MovementWalkMode).iter().any(|x| {
        x.chord.key == InputKey::Key(Key::ShiftLeft) && x.activation == ActivationType::Hold
    }));
}

#[test]
fn v5_ctrl_number_select_slot_migrates_to_retail_use() {
    let loaded = load_json(
        r#"{ "version": 5, "actions": { "SelectQuickSlot_5": [ { "key": "Number5", "mod": "Ctrl" } ] } }"#,
    );
    assert_eq!(
        loaded
            .find(chord(Key::Number5, Modifiers::CTRL), ActivationType::Press)
            .map(|b| b.action),
        Some(A::UseQuickSlot_5)
    );
    assert!(actions(&loaded, A::SelectQuickSlot_5).is_empty());
}

#[test]
fn v2_combat_press_binding_migrates_to_hold() {
    let loaded =
        load_json(r#"{ "version": 2, "actions": { "CombatHighAttack": [ { "key": "F7" } ] } }"#);
    assert_eq!(
        loaded
            .find(bare(Key::F7), ActivationType::Hold)
            .map(|b| b.action),
        Some(A::CombatHighAttack)
    );
    assert_eq!(loaded.find(bare(Key::F7), ActivationType::Press), None);
}

#[test]
fn v4_select_right_press_binding_migrates_to_click() {
    let loaded = load_json(
        r#"{ "version": 4, "actions": { "SelectRight": [ { "key": "-1002", "device": 1 } ] } }"#,
    );
    let binds = actions(&loaded, A::SelectRight);
    assert_eq!(binds.len(), 1);
    assert_eq!(binds[0].chord.key, InputKey::Mouse(mouse::RIGHT));
    assert_eq!(binds[0].activation, ActivationType::Click);
}

#[test]
fn an_unknown_action_name_is_skipped() {
    let loaded = load_json(
        r#"{ "version": 1, "actions": {
            "NotARealActionName": [ { "key": "W", "mod": "None" } ],
            "MovementForward": [ { "key": "Q", "mod": "None" } ] } }"#,
    );
    assert!(
        actions(&loaded, A::MovementForward)
            .iter()
            .any(|x| x.chord.key == InputKey::Key(Key::Q))
    );
}
