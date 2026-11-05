//! OpenAC `tests/AcDream.UI.Abstractions.Tests/Input/InputDispatcherTests.cs`,
//! `InputDispatcherCaptureTests.cs`, `InputDispatcherClickTests.cs`,
//! `InputDispatcherDoubleClickTests.cs`, `InputDispatcherIsActionHeldTests.cs`,
//! `MuteChordDispatchTests.cs` and the deactivation case of
//! `InputDispatcherLifetimeTests.cs` (the rest of that file is .NET event
//! subscription rollback; the dispatcher here is fed directly). The fake
//! devices follow `FakeKeyboardSource`/`FakeMouseSource`: an emitted key
//! sets the current modifiers.

use std::collections::HashSet;

use super::*;
use crate::input::chord::mouse;

use InputAction as A;

#[derive(Default)]
struct Devices {
    keys: HashSet<Key>,
    buttons: HashSet<u8>,
    modifiers: Modifiers,
    want_mouse: bool,
    want_keyboard: bool,
}

impl InputDevices for Devices {
    fn is_key_held(&self, key: Key) -> bool {
        self.keys.contains(&key)
    }
    fn is_button_held(&self, button: u8) -> bool {
        self.buttons.contains(&button)
    }
    fn modifiers(&self) -> Modifiers {
        self.modifiers
    }
    fn want_capture_mouse(&self) -> bool {
        self.want_mouse
    }
    fn want_capture_keyboard(&self) -> bool {
        self.want_keyboard
    }
}

type Fired = (InputAction, ActivationType);

struct Rig {
    d: InputDispatcher,
    dev: Devices,
    fired: Vec<Fired>,
    captured: Vec<Option<KeyChord>>,
    now_ms: u64,
}

impl Rig {
    fn new(bindings: KeyBindings) -> Self {
        Self {
            d: InputDispatcher::new(bindings),
            dev: Devices::default(),
            fired: Vec::new(),
            captured: Vec::new(),
            now_ms: 0,
        }
    }

    fn with(bindings: &[Binding]) -> Self {
        let mut b = KeyBindings::new();
        for binding in bindings {
            b.add(*binding);
        }
        Self::new(b)
    }

    fn record<'a>(
        fired: &'a mut Vec<Fired>,
        captured: &'a mut Vec<Option<KeyChord>>,
    ) -> impl FnMut(&mut InputDispatcher, DispatchEvent) + 'a {
        move |_, event| match event {
            DispatchEvent::Fired(a, t) => fired.push((a, t)),
            DispatchEvent::Captured(c) => captured.push(c),
        }
    }

    fn key_down(&mut self, key: Key, modifiers: Modifiers) {
        self.dev.modifiers = modifiers;
        self.dev.keys.insert(key);
        let mut sink = Self::record(&mut self.fired, &mut self.captured);
        self.d.key_down(key, modifiers, &self.dev, &mut sink);
    }

    fn key_up(&mut self, key: Key, modifiers: Modifiers) {
        self.dev.modifiers = modifiers;
        self.dev.keys.remove(&key);
        let mut sink = Self::record(&mut self.fired, &mut self.captured);
        self.d.key_up(key, modifiers, &mut sink);
    }

    fn mouse_down(&mut self, button: u8, modifiers: Modifiers) {
        self.dev.buttons.insert(button);
        let mut sink = Self::record(&mut self.fired, &mut self.captured);
        self.d
            .mouse_down(button, modifiers, self.now_ms, &self.dev, &mut sink);
    }

    fn mouse_up(&mut self, button: u8, modifiers: Modifiers) {
        self.dev.buttons.remove(&button);
        let mut sink = Self::record(&mut self.fired, &mut self.captured);
        self.d.mouse_up(button, modifiers, &self.dev, &mut sink);
    }

    fn mouse_move(&mut self, dx: f32, dy: f32) {
        self.d.mouse_move(dx, dy, &self.dev);
    }

    fn scroll(&mut self, delta: f32) {
        let mut sink = Self::record(&mut self.fired, &mut self.captured);
        self.d.scroll(delta, &self.dev, &mut sink);
    }

    fn tick(&mut self) {
        let mut sink = Self::record(&mut self.fired, &mut self.captured);
        self.d.tick(&self.dev, &mut sink);
    }

    fn dispatch(&mut self) {
        let mut sink = Self::record(&mut self.fired, &mut self.captured);
        self.d.dispatch(&mut sink);
    }

    fn held(&self, action: InputAction) -> bool {
        self.d.is_action_held(action, &self.dev)
    }
}

fn key(k: Key, m: Modifiers) -> KeyChord {
    KeyChord::key(k, m)
}

fn bare(k: Key) -> KeyChord {
    key(k, Modifiers::NONE)
}

const NONE: Modifiers = Modifiers::NONE;
const PRESS: ActivationType = ActivationType::Press;
const HOLD: ActivationType = ActivationType::Hold;
const RELEASE: ActivationType = ActivationType::Release;

// ── InputDispatcherTests ───────────────────────────────────────────────

#[test]
fn key_down_with_matching_chord_fires_press_action() {
    let mut r = Rig::with(&[Binding::new(bare(Key::W), A::MovementForward)]);
    r.key_down(Key::W, NONE);
    assert_eq!(r.fired, [(A::MovementForward, PRESS)]);
}

#[test]
fn key_down_with_mismatched_modifier_does_not_fire() {
    let mut r = Rig::with(&[Binding::new(
        key(Key::A, Modifiers::CTRL),
        A::SelectionExamine,
    )]);
    r.key_down(Key::A, NONE);
    assert!(r.fired.is_empty());
    r.key_down(Key::A, Modifiers::SHIFT | Modifiers::CTRL);
    assert!(r.fired.is_empty());
    r.key_down(Key::A, Modifiers::CTRL);
    assert_eq!(r.fired.len(), 1);
}

#[test]
fn want_capture_keyboard_suppresses_key_down() {
    let mut r = Rig::with(&[Binding::new(bare(Key::W), A::MovementForward)]);
    r.dev.want_keyboard = true;
    r.key_down(Key::W, NONE);
    assert!(r.fired.is_empty());
}

#[test]
fn want_capture_mouse_suppresses_mouse_down_and_scroll() {
    let mut r = Rig::with(&[Binding::new(
        KeyChord::mouse(mouse::LEFT, NONE),
        A::SelectLeft,
    )]);
    r.dev.want_mouse = true;
    r.mouse_down(mouse::LEFT, NONE);
    r.scroll(1.0);
    assert!(r.fired.is_empty());
}

#[test]
fn scopes_push_pop_and_default_to_game() {
    let mut r = Rig::with(&[]);
    assert_eq!(r.d.active_scope(), InputScope::Game);
    r.d.push_scope(InputScope::Chat);
    assert_eq!(r.d.active_scope(), InputScope::Chat);
    assert!(
        !r.d.pop_scope(InputScope::Dialog),
        "a mismatched pop is refused"
    );
    assert!(r.d.pop_scope(InputScope::Chat));
    assert_eq!(r.d.active_scope(), InputScope::Game);
}

#[test]
fn magic_combat_scope_shadows_game_binding_for_same_chord() {
    let chord = bare(Key::Number1);
    let mut r = Rig::with(&[
        Binding::new(chord, A::UseQuickSlot_1),
        Binding::new(chord, A::UseSpellSlot_1).scope(InputScope::MagicCombat),
    ]);
    r.key_down(Key::Number1, NONE);
    r.d.set_combat_scope(Some(InputScope::MagicCombat));
    r.key_down(Key::Number1, NONE);
    assert_eq!(
        r.fired,
        [(A::UseQuickSlot_1, PRESS), (A::UseSpellSlot_1, PRESS)]
    );
}

#[test]
fn same_scope_duplicate_chord_fires_every_distinct_action() {
    let chord = key(Key::Number1, Modifiers::ALT);
    let mut r = Rig::with(&[
        Binding::new(chord, A::ToggleFloatingChatWindow1),
        Binding::new(chord, A::UseQuickSlot_10),
    ]);
    r.key_down(Key::Number1, Modifiers::ALT);
    assert_eq!(
        r.fired,
        [
            (A::ToggleFloatingChatWindow1, PRESS),
            (A::UseQuickSlot_10, PRESS)
        ]
    );
}

#[test]
fn changing_combat_scope_releases_hold_resolved_in_previous_scope() {
    let mut r = Rig::with(&[Binding::new(bare(Key::Delete), A::CombatLowAttack)
        .activation(HOLD)
        .scope(InputScope::MeleeCombat)]);
    r.d.set_combat_scope(Some(InputScope::MeleeCombat));
    r.key_down(Key::Delete, NONE);
    r.d.set_combat_scope(Some(InputScope::MagicCombat));
    r.dispatch();
    assert_eq!(
        r.fired,
        [(A::CombatLowAttack, PRESS), (A::CombatLowAttack, RELEASE)]
    );
}

#[test]
fn hold_binding_fires_press_down_hold_per_tick_and_release_up() {
    let mut r =
        Rig::with(&[Binding::new(bare(Key::ShiftLeft), A::MovementRunLock).activation(HOLD)]);
    r.key_down(Key::ShiftLeft, NONE);
    assert_eq!(r.fired, [(A::MovementRunLock, PRESS)]);
    r.fired.clear();
    r.tick();
    r.tick();
    assert_eq!(
        r.fired,
        [(A::MovementRunLock, HOLD), (A::MovementRunLock, HOLD)]
    );
    r.key_up(Key::ShiftLeft, NONE);
    assert_eq!(r.fired.last(), Some(&(A::MovementRunLock, RELEASE)));
    r.fired.clear();
    r.tick();
    assert!(r.fired.is_empty());
}

#[test]
fn retail_bare_left_shift_binding_drops_the_keys_own_modifier_bit() {
    let mut r = Rig::new(KeyBindings::retail_defaults());
    r.key_down(Key::ShiftLeft, Modifiers::SHIFT);
    r.key_up(Key::ShiftLeft, Modifiers::SHIFT);
    assert!(r.fired.contains(&(A::MovementWalkMode, PRESS)));
    assert!(r.fired.contains(&(A::MovementWalkMode, RELEASE)));
}

#[test]
fn a_scope_change_inside_a_hold_does_not_dispatch_the_stale_snapshot_chord() {
    let melee = InputScope::MeleeCombat;
    let mut r = Rig::with(&[
        Binding::new(bare(Key::Delete), A::CombatLowAttack)
            .activation(HOLD)
            .scope(melee),
        Binding::new(bare(Key::End), A::CombatHighAttack)
            .activation(HOLD)
            .scope(melee),
    ]);
    r.d.set_combat_scope(Some(melee));
    r.key_down(Key::Delete, NONE);
    r.key_down(Key::End, NONE);
    r.fired.clear();
    let mut changed = false;
    let mut fired = Vec::new();
    let mut sink = |d: &mut InputDispatcher, event: DispatchEvent| {
        if let DispatchEvent::Fired(a, t) = event {
            fired.push((a, t));
            if t == HOLD && !changed {
                changed = true;
                d.set_combat_scope(Some(InputScope::MagicCombat));
            }
        }
    };
    r.d.tick(&r.dev, &mut sink);
    assert_eq!(fired.iter().filter(|(_, t)| *t == HOLD).count(), 1);
    assert_eq!(fired.iter().filter(|(_, t)| *t == RELEASE).count(), 2);
}

#[test]
fn release_binding_fires_only_on_key_up() {
    let mut r = Rig::with(&[Binding::new(bare(Key::W), A::MovementStop).activation(RELEASE)]);
    r.key_down(Key::W, NONE);
    assert!(r.fired.is_empty());
    r.key_up(Key::W, NONE);
    assert_eq!(r.fired, [(A::MovementStop, RELEASE)]);
}

#[test]
fn mouse_down_with_matching_chord_fires_action() {
    let mut r = Rig::with(&[Binding::new(
        KeyChord::mouse(mouse::LEFT, NONE),
        A::SelectLeft,
    )]);
    r.mouse_down(mouse::LEFT, NONE);
    assert_eq!(r.fired, [(A::SelectLeft, PRESS)]);
}

#[test]
fn tick_does_nothing_with_nothing_held_and_the_wheel_presses_scroll() {
    let mut r = Rig::with(&[]);
    r.tick();
    assert!(r.fired.is_empty());
    r.scroll(1.0);
    r.scroll(-1.0);
    assert_eq!(r.fired, [(A::ScrollUp, PRESS), (A::ScrollDown, PRESS)]);
}

#[test]
fn automation_held_action_uses_fired_transitions_and_held_polling() {
    let mut r = Rig::with(&[]);
    let mut sink = Rig::record(&mut r.fired, &mut r.captured);
    assert!(r.d.try_set_automation_action_held(A::MovementForward, true, &r.dev, &mut sink));
    assert!(r.d.is_action_held(A::MovementForward, &r.dev));
    assert!(r.d.try_set_automation_action_held(A::MovementForward, false, &r.dev, &mut sink));
    assert!(!r.d.is_action_held(A::MovementForward, &r.dev));
    drop(sink);
    assert_eq!(
        r.fired,
        [(A::MovementForward, PRESS), (A::MovementForward, RELEASE)]
    );
}

#[test]
fn automation_press_obeys_keyboard_capture_but_its_release_always_goes() {
    let mut r = Rig::with(&[]);
    r.dev.want_keyboard = true;
    {
        let mut sink = Rig::record(&mut r.fired, &mut r.captured);
        assert!(!r.d.try_invoke_automation_action(A::CombatToggleCombat, &r.dev, &mut sink));
        assert!(!r.d.try_set_automation_action_held(A::MovementForward, true, &r.dev, &mut sink));
    }
    assert!(r.fired.is_empty());
    r.dev.want_keyboard = false;
    {
        let mut sink = Rig::record(&mut r.fired, &mut r.captured);
        assert!(r.d.try_invoke_automation_action(A::CombatToggleCombat, &r.dev, &mut sink));
        assert!(r.d.try_set_automation_action_held(A::MovementForward, true, &r.dev, &mut sink));
    }
    // A hold begun before a field took the keyboard goes on, as a held key
    // does, until it is let go.
    r.dev.want_keyboard = true;
    assert!(r.held(A::MovementForward));
    {
        let mut sink = Rig::record(&mut r.fired, &mut r.captured);
        assert!(r.d.try_set_automation_action_held(A::MovementForward, false, &r.dev, &mut sink));
    }
    assert!(!r.held(A::MovementForward));
    assert_eq!(
        r.fired,
        [
            (A::CombatToggleCombat, PRESS),
            (A::MovementForward, PRESS),
            (A::MovementForward, RELEASE)
        ]
    );
}

// ── InputDispatcherCaptureTests ────────────────────────────────────────

#[test]
fn a_capture_consumes_the_next_chord_without_firing() {
    let mut r = Rig::with(&[Binding::new(bare(Key::W), A::MovementForward)]);
    assert!(!r.d.is_capturing());
    r.d.begin_capture();
    r.key_down(Key::W, NONE);
    assert_eq!(r.captured, [Some(bare(Key::W))]);
    assert!(r.fired.is_empty());
    assert!(!r.d.is_capturing());
}

#[test]
fn escape_cancels_a_capture() {
    let mut r = Rig::with(&[]);
    r.d.begin_capture();
    r.key_down(Key::Escape, NONE);
    assert_eq!(r.captured, [None]);
    assert!(r.fired.is_empty());
    assert!(!r.d.is_capturing());
}

#[test]
fn modifier_keys_alone_do_not_complete_a_capture() {
    let mut r = Rig::with(&[]);
    r.d.begin_capture();
    r.key_down(Key::ShiftLeft, Modifiers::SHIFT);
    assert!(r.captured.is_empty() && r.d.is_capturing());
    r.key_down(Key::ControlLeft, Modifiers::SHIFT | Modifiers::CTRL);
    assert!(r.captured.is_empty() && r.d.is_capturing());
    r.d.cancel_capture();
    r.dispatch();
    r.captured.clear();
    r.d.begin_capture();
    r.key_down(Key::A, Modifiers::SHIFT | Modifiers::CTRL);
    assert_eq!(
        r.captured,
        [Some(key(Key::A, Modifiers::SHIFT | Modifiers::CTRL))]
    );
}

#[test]
fn a_modifier_released_alone_becomes_a_bare_primary_key() {
    let mut r = Rig::with(&[]);
    r.d.begin_capture();
    r.key_down(Key::ShiftLeft, Modifiers::SHIFT);
    assert!(r.captured.is_empty());
    r.key_up(Key::ShiftLeft, Modifiers::SHIFT);
    assert_eq!(r.captured, [Some(bare(Key::ShiftLeft))]);
    assert!(r.fired.is_empty());
}

#[test]
fn a_capture_completes_with_the_modifier_state() {
    let mut r = Rig::with(&[]);
    r.d.begin_capture();
    r.key_down(Key::A, Modifiers::CTRL);
    assert_eq!(r.captured, [Some(key(Key::A, Modifiers::CTRL))]);
}

#[test]
fn a_capture_takes_a_mouse_button_even_over_the_ui() {
    let left = KeyChord::mouse(mouse::LEFT, Modifiers::CTRL);
    let mut r = Rig::with(&[Binding::new(left, A::ToggleInventoryPanel)]);
    r.dev.want_mouse = true;
    r.d.begin_capture();
    r.mouse_down(mouse::LEFT, Modifiers::CTRL);
    assert_eq!(r.captured, [Some(left)]);
    assert!(!r.d.is_capturing());
    assert!(r.fired.is_empty());
}

#[test]
fn cancel_capture_reports_a_cancel_and_clears_the_state() {
    let mut r = Rig::with(&[]);
    r.d.begin_capture();
    assert!(r.d.is_capturing());
    r.d.cancel_capture();
    r.dispatch();
    assert_eq!(r.captured, [None]);
    assert!(!r.d.is_capturing());
}

#[test]
fn set_bindings_replaces_the_table() {
    let mut r = Rig::with(&[]);
    r.key_down(Key::W, NONE);
    assert!(r.fired.is_empty());
    let mut swapped = KeyBindings::new();
    swapped.add(Binding::new(bare(Key::W), A::MovementForward));
    r.d.set_bindings(swapped);
    r.key_down(Key::W, NONE);
    assert_eq!(r.fired, [(A::MovementForward, PRESS)]);
}

// ── InputDispatcherClickTests ──────────────────────────────────────────

fn right_click_rig() -> Rig {
    Rig::with(&[
        Binding::new(KeyChord::mouse(mouse::RIGHT, NONE), A::SelectRight)
            .activation(ActivationType::Click),
    ])
}

#[test]
fn a_mouse_click_fires_on_release_after_a_stationary_press() {
    let mut r = right_click_rig();
    r.mouse_down(mouse::RIGHT, NONE);
    assert!(r.fired.is_empty());
    r.mouse_up(mouse::RIGHT, NONE);
    assert_eq!(r.fired, [(A::SelectRight, ActivationType::Click)]);
}

#[test]
fn a_click_survives_three_pixels_but_not_four() {
    let mut r = right_click_rig();
    r.mouse_down(mouse::RIGHT, NONE);
    r.mouse_move(4.0, 0.0);
    r.mouse_up(mouse::RIGHT, NONE);
    assert!(r.fired.is_empty());
    r.mouse_down(mouse::RIGHT, NONE);
    r.mouse_move(3.0, 0.0);
    r.mouse_up(mouse::RIGHT, NONE);
    assert_eq!(r.fired.len(), 1);
}

#[test]
fn a_press_over_the_ui_never_starts_a_world_click() {
    let mut r = right_click_rig();
    r.dev.want_mouse = true;
    r.mouse_down(mouse::RIGHT, NONE);
    r.dev.want_mouse = false;
    r.mouse_up(mouse::RIGHT, NONE);
    assert!(r.fired.is_empty());
}

#[test]
fn a_keyboard_rebind_of_a_click_action_fires_on_key_down() {
    let mut r =
        Rig::with(&[Binding::new(bare(Key::Q), A::SelectRight).activation(ActivationType::Click)]);
    r.key_down(Key::Q, NONE);
    assert_eq!(r.fired, [(A::SelectRight, ActivationType::Click)]);
}

// ── InputDispatcherDoubleClickTests ────────────────────────────────────

fn double_click_rig() -> Rig {
    let lmb = KeyChord::mouse(mouse::LEFT, NONE);
    let rmb = KeyChord::mouse(mouse::RIGHT, NONE);
    Rig::with(&[
        Binding::new(lmb, A::SelectLeft),
        Binding::new(lmb, A::SelectDblLeft).activation(ActivationType::DoubleClick),
        Binding::new(rmb, A::SelectRight),
    ])
}

fn count(r: &Rig, event: Fired) -> usize {
    r.fired.iter().filter(|e| **e == event).count()
}

#[test]
fn a_second_click_within_the_threshold_fires_a_double_click() {
    let mut r = double_click_rig();
    r.mouse_down(mouse::LEFT, NONE);
    r.now_ms += 10;
    r.mouse_down(mouse::LEFT, NONE);
    assert_eq!(count(&r, (A::SelectLeft, PRESS)), 2);
    assert_eq!(
        count(&r, (A::SelectDblLeft, ActivationType::DoubleClick)),
        1
    );
}

#[test]
fn a_second_click_beyond_the_threshold_or_on_another_button_does_not() {
    let mut r = double_click_rig();
    r.mouse_down(mouse::LEFT, NONE);
    r.now_ms += 600;
    r.mouse_down(mouse::LEFT, NONE);
    r.now_ms += 10;
    r.mouse_down(mouse::RIGHT, NONE);
    assert!(
        !r.fired
            .iter()
            .any(|(_, t)| *t == ActivationType::DoubleClick)
    );
}

#[test]
fn a_third_click_after_a_double_click_needs_a_fresh_pair() {
    let mut r = double_click_rig();
    for _ in 0..3 {
        r.mouse_down(mouse::LEFT, NONE);
        r.now_ms += 10;
    }
    assert_eq!(count(&r, (A::SelectLeft, PRESS)), 3);
    assert_eq!(
        count(&r, (A::SelectDblLeft, ActivationType::DoubleClick)),
        1
    );
}

// ── InputDispatcherIsActionHeldTests ───────────────────────────────────

#[test]
fn an_action_is_held_while_its_key_is() {
    let mut r = Rig::with(&[Binding::new(bare(Key::W), A::MovementForward)]);
    assert!(!r.held(A::MovementForward));
    r.key_down(Key::W, NONE);
    assert!(r.held(A::MovementForward));
    assert!(!r.held(A::MovementBackup));
    r.key_up(Key::W, NONE);
    assert!(!r.held(A::MovementForward));
    assert!(!r.held(A::None));
}

#[test]
fn a_combat_binding_on_the_same_chord_shadows_game_polling() {
    let chord = bare(Key::W);
    let mut r = Rig::with(&[
        Binding::new(chord, A::MovementForward),
        Binding::new(chord, A::CombatCastCurrentSpell).scope(InputScope::MagicCombat),
    ]);
    r.d.set_combat_scope(Some(InputScope::MagicCombat));
    r.key_down(Key::W, NONE);
    assert!(!r.held(A::MovementForward));
    assert!(r.held(A::CombatCastCurrentSpell));
    r.d.set_combat_scope(None);
    assert!(r.held(A::MovementForward));
    assert!(!r.held(A::CombatCastCurrentSpell));
}

#[test]
fn an_explicit_shift_combat_chord_shadows_the_bare_movement_fallback() {
    let mut r = Rig::with(&[
        Binding::new(bare(Key::W), A::MovementForward),
        Binding::new(key(Key::W, Modifiers::SHIFT), A::CombatCastCurrentSpell)
            .scope(InputScope::MagicCombat),
    ]);
    r.d.set_combat_scope(Some(InputScope::MagicCombat));
    r.key_down(Key::W, Modifiers::SHIFT);
    assert!(!r.held(A::MovementForward));
    assert!(r.held(A::CombatCastCurrentSpell));
}

#[test]
fn a_modifier_mismatch_is_not_held() {
    let mut r = Rig::with(&[Binding::new(
        key(Key::A, Modifiers::CTRL),
        A::SelectionExamine,
    )]);
    r.key_down(Key::A, NONE);
    assert!(!r.held(A::SelectionExamine));
    r.key_up(Key::A, NONE);
    r.key_down(Key::A, Modifiers::CTRL);
    assert!(r.held(A::SelectionExamine));
}

#[test]
fn any_of_several_bindings_holds_an_action() {
    let mut r = Rig::with(&[
        Binding::new(bare(Key::W), A::MovementForward),
        Binding::new(bare(Key::Up), A::MovementForward),
    ]);
    r.key_down(Key::Up, NONE);
    assert!(r.held(A::MovementForward));
    r.key_up(Key::Up, NONE);
    assert!(!r.held(A::MovementForward));
    r.key_down(Key::W, NONE);
    assert!(r.held(A::MovementForward));
}

#[test]
fn a_mouse_button_chord_is_held() {
    let mut r =
        Rig::with(&[
            Binding::new(KeyChord::mouse(mouse::RIGHT, NONE), A::AcdreamRmbOrbitHold)
                .activation(HOLD),
        ]);
    assert!(!r.held(A::AcdreamRmbOrbitHold));
    r.mouse_down(mouse::RIGHT, NONE);
    assert!(r.held(A::AcdreamRmbOrbitHold));
    r.mouse_up(mouse::RIGHT, NONE);
    assert!(!r.held(A::AcdreamRmbOrbitHold));
}

#[test]
fn a_bare_chord_stays_held_with_shift_added_but_not_with_ctrl() {
    let mut r = Rig::with(&[Binding::new(bare(Key::W), A::MovementForward)]);
    r.key_down(Key::W, NONE);
    r.dev.modifiers = Modifiers::SHIFT;
    assert!(r.held(A::MovementForward));
    r.dev.modifiers = Modifiers::CTRL;
    assert!(!r.held(A::MovementForward));
}

/// A key held before a text field takes the keyboard (the chat entry
/// clicked while running) stays held until it comes up: the game ends a
/// held action on its key's release only, whatever has the keyboard then.
#[test]
fn a_key_held_before_a_field_takes_the_keyboard_stays_held_until_released() {
    let mut r = Rig::with(&[Binding::new(bare(Key::W), A::MovementForward)]);
    r.key_down(Key::W, NONE);
    assert!(r.held(A::MovementForward));
    r.dev.want_keyboard = true;
    assert!(r.held(A::MovementForward), "the field took the keyboard");
    r.dev.want_mouse = true;
    assert!(r.held(A::MovementForward));
    r.key_up(Key::W, NONE);
    assert!(
        !r.held(A::MovementForward),
        "released while the field has it"
    );
}

/// A key pressed while a text field has the keyboard types into the field
/// and holds nothing, then or after the field lets the keyboard go.
#[test]
fn a_key_pressed_while_a_field_has_the_keyboard_holds_nothing() {
    let mut r = Rig::with(&[Binding::new(bare(Key::W), A::MovementForward)]);
    r.dev.want_keyboard = true;
    r.key_down(Key::W, NONE);
    assert!(!r.held(A::MovementForward));
    r.dev.want_keyboard = false;
    assert!(
        !r.held(A::MovementForward),
        "a key typed into the field does not move once the field lets go"
    );
    r.key_up(Key::W, NONE);
    r.key_down(Key::W, NONE);
    assert!(r.held(A::MovementForward), "a fresh press holds again");
}

/// A key pressed into a rebind capture is the capture's, not a held action.
#[test]
fn a_key_taken_by_a_capture_holds_nothing() {
    let mut r = Rig::with(&[Binding::new(bare(Key::W), A::MovementForward)]);
    r.d.begin_capture();
    r.key_down(Key::W, NONE);
    assert_eq!(r.captured, [Some(bare(Key::W))]);
    assert!(!r.held(A::MovementForward));
}

/// A button pressed over the interface is the interface's: an action bound
/// to it is not held.
#[test]
fn a_button_pressed_over_the_ui_holds_nothing() {
    let mut r =
        Rig::with(&[
            Binding::new(KeyChord::mouse(mouse::RIGHT, NONE), A::AcdreamRmbOrbitHold)
                .activation(HOLD),
        ]);
    r.dev.want_mouse = true;
    r.mouse_down(mouse::RIGHT, NONE);
    r.dev.want_mouse = false;
    assert!(!r.held(A::AcdreamRmbOrbitHold));
}

// ── MuteChordDispatchTests, deactivation ───────────────────────────────

#[test]
fn ctrl_m_mutes_unless_a_field_has_the_keyboard() {
    let mut r = Rig::new(KeyBindings::retail_defaults());
    r.key_down(Key::M, Modifiers::CTRL);
    assert!(r.fired.contains(&(A::AcdreamToggleAudioMute, PRESS)));
    let mut r = Rig::new(KeyBindings::retail_defaults());
    r.dev.want_keyboard = true;
    r.key_down(Key::M, Modifiers::CTRL);
    assert!(!r.fired.contains(&(A::AcdreamToggleAudioMute, PRESS)));
}

#[test]
fn a_deactivated_dispatcher_fires_nothing() {
    let mut r = Rig::with(&[Binding::new(bare(Key::W), A::MovementForward)]);
    r.key_down(Key::W, NONE);
    r.d.deactivate();
    r.key_down(Key::W, NONE);
    assert_eq!(r.fired.len(), 1);
    assert!(!r.held(A::MovementForward));
    // Deactivating lets go of every key: one still down when dispatching
    // resumes holds nothing until it is pressed again.
    r.d.activate();
    assert!(!r.held(A::MovementForward));
    r.key_up(Key::W, NONE);
    r.key_down(Key::W, NONE);
    assert!(r.held(A::MovementForward));
}
