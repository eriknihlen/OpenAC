//! The window's gameplay routing: OpenAC `DispatcherMovementInputSource`
//! (held movement from the dispatcher), `GameplayInputActionRouter`
//! (`ResolvePressedMovementCommand`, the alternate-camera scope, the
//! router's order), `MouseLookController` (begin and end with the
//! movement's toggles, samples to mouse turns, the cursor),
//! `CameraPointerInputController` (views, the wheel, the right-button
//! orbit) and `CameraFrameController` (held camera keys at the
//! adjustment speed, 40 steps a second by default).

use ac_runtime::movement::{MovementCommand, MovementKeys, Posture};
use ac_ui::{Key, Modifiers};
use glam::Vec3;

use super::*;
use crate::camera::{CameraController, CameraMode, ChaseSubject, OrbitCamera};

pub(super) struct Rig {
    pub(super) input: GameplayInput,
    pub(super) cameras: CameraController,
    pub(super) buttons: u32,
    pub(super) modifiers: Modifiers,
    pub(super) want_mouse: bool,
    pub(super) want_keyboard: bool,
    pub(super) now: f32,
    pub(super) out: Vec<GameplayOutput>,
}

impl Rig {
    pub(super) fn new() -> Self {
        let mut cameras = CameraController::new(OrbitCamera::default());
        cameras.follow_player(crate::camera::PlayerModeFacts {
            live_in_world: true,
            player_present: true,
            controller_ready: true,
            world_ready: true,
            world_entry: 1,
        });
        cameras
            .chase
            .update(&ChaseSubject::standing(Vec3::ZERO, 0.0), 0.0, None);
        Self {
            input: GameplayInput::new(KeyBindings::retail_defaults()),
            cameras,
            buttons: 0,
            modifiers: Modifiers::NONE,
            want_mouse: false,
            want_keyboard: false,
            now: 1.0,
            out: Vec::new(),
        }
    }

    pub(super) fn state(&self) -> DeviceState<'static> {
        DeviceState {
            keys: None,
            buttons: self.buttons,
            modifiers: self.modifiers,
            want_mouse: self.want_mouse,
            want_keyboard: self.want_keyboard,
        }
    }

    pub(super) fn key(&mut self, key: Key, pressed: bool) {
        let state = self.state();
        self.input.key(
            key,
            pressed,
            &state,
            Some(&mut self.cameras),
            self.now,
            &mut self.out,
        );
    }

    pub(super) fn tap(&mut self, key: Key) {
        self.key(key, true);
        self.key(key, false);
    }

    pub(super) fn button(&mut self, button: u8, pressed: bool) {
        if pressed {
            self.buttons |= 1 << button;
        } else {
            self.buttons &= !(1 << button);
        }
        let state = self.state();
        self.input.button(
            button,
            pressed,
            (self.now * 1000.0) as u64,
            &state,
            Some(&mut self.cameras),
            self.now,
            &mut self.out,
        );
    }

    pub(super) fn tick(&mut self, dt: f32) -> FlyInput {
        self.now += dt;
        let state = self.state();
        self.input
            .tick(dt, self.now, &state, Some(&mut self.cameras), &mut self.out)
    }

    pub(super) fn take(&mut self) -> Vec<GameplayOutput> {
        std::mem::take(&mut self.out)
    }

    pub(super) fn movement(&mut self) -> Vec<MovementCommand> {
        self.take()
            .into_iter()
            .filter_map(|o| match o {
                GameplayOutput::Movement(c) => Some(c),
                _ => None,
            })
            .collect()
    }
}

#[test]
fn held_retail_keys_reach_the_session_as_movement_keys_once_per_change() {
    let mut r = Rig::new();
    r.key(Key::W, true);
    r.key(Key::D, true);
    r.tick(1.0 / 60.0);
    assert_eq!(
        r.movement(),
        [MovementCommand::Keys(MovementKeys {
            forward: true,
            turn_right: true,
            ..MovementKeys::default()
        })]
    );
    r.tick(1.0 / 60.0);
    assert!(r.movement().is_empty(), "no change, nothing sent");
    // Shift held walks (the bare chord stays held with Shift added).
    r.modifiers = Modifiers::SHIFT;
    r.key(Key::ShiftLeft, true);
    r.tick(1.0 / 60.0);
    let sent = r.movement();
    assert!(matches!(sent.as_slice(), [MovementCommand::Keys(k)] if k.walk && k.forward));
    r.modifiers = Modifiers::NONE;
    r.key(Key::ShiftLeft, false);
    r.key(Key::W, false);
    r.key(Key::D, false);
    r.key(Key::X, true);
    r.key(Key::Z, true);
    r.key(Key::Space, true);
    r.tick(1.0 / 60.0);
    assert_eq!(
        r.movement(),
        [MovementCommand::Keys(MovementKeys {
            backward: true,
            strafe_left: true,
            jump: true,
            ..MovementKeys::default()
        })]
    );
}

/// GitHub issue #38: clicking the chat window gives its entry the keyboard,
/// and the character running on a held forward key stopped. The game ends
/// a held movement key on its release only, so the run goes on while the
/// entry has the keyboard and stops when the key comes up; a key typed
/// into the entry moves nothing, then or after the entry lets go.
#[test]
fn a_field_taking_the_keyboard_keeps_the_held_movement_keys() {
    let mut r = Rig::new();
    r.key(Key::W, true);
    r.tick(1.0 / 60.0);
    let forward = MovementKeys {
        forward: true,
        ..MovementKeys::default()
    };
    assert_eq!(r.movement(), [MovementCommand::Keys(forward)]);

    // The chat entry takes the keyboard: the run goes on.
    r.want_keyboard = true;
    r.tick(1.0 / 60.0);
    r.tick(1.0 / 60.0);
    assert!(r.movement().is_empty(), "the held forward key was let go");

    // A key pressed while the entry has the keyboard adds nothing.
    r.key(Key::X, true);
    r.tick(1.0 / 60.0);
    assert!(r.movement().is_empty());

    // The forward key comes up while the entry has the keyboard.
    r.key(Key::W, false);
    r.tick(1.0 / 60.0);
    assert_eq!(
        r.movement(),
        [MovementCommand::Keys(MovementKeys::default())]
    );

    // The entry lets go with the typed key still down: it moves nothing.
    r.want_keyboard = false;
    r.tick(1.0 / 60.0);
    assert!(r.movement().is_empty(), "a typed key started a move");
}

#[test]
fn presses_toggle_the_run_lock_stop_and_take_postures() {
    let mut r = Rig::new();
    for (key, command) in [
        (Key::Q, MovementCommand::ToggleRunLock),
        (Key::S, MovementCommand::Stop),
        (Key::Y, MovementCommand::Posture(Posture::Ready)),
        (Key::G, MovementCommand::Posture(Posture::Sit)),
        (Key::H, MovementCommand::Posture(Posture::Crouch)),
        (Key::B, MovementCommand::Posture(Posture::Sleep)),
    ] {
        r.tap(key);
        assert_eq!(r.movement(), [command], "{key:?}");
    }
}

#[test]
fn the_client_commands_sit_on_ctrl_f_keys_and_the_retail_keys_stay_retail() {
    // OpenAC `KeyBindings.RetailDefaults`: the client's commands on Ctrl+F7,
    // Ctrl+F10 and Ctrl+M; F3 and F4 open the allegiance and fellowship
    // panels (the interface's actions) and Tab the chat entry (the UI's,
    // not the camera's), and no key toggles the fly camera or player mode.
    let mut r = Rig::new();
    r.modifiers = Modifiers::CTRL;
    r.tap(Key::F7);
    r.tap(Key::F10);
    r.tap(Key::M);
    r.modifiers = Modifiers::NONE;
    r.tap(Key::F3);
    r.tap(Key::F4);
    r.tap(Key::Tab);
    r.tap(Key::F7);
    assert_eq!(
        r.take(),
        [
            GameplayOutput::CycleTimeOfDay,
            GameplayOutput::CycleWeather,
            GameplayOutput::ToggleMute,
            GameplayOutput::Interface(InputAction::ToggleAllegiancePanel),
            GameplayOutput::Interface(InputAction::ToggleFellowshipPanel),
            GameplayOutput::ChatEntry(InputAction::ToggleChatEntry, Some(Key::Tab)),
        ]
    );
    let bindings = KeyBindings::retail_defaults();
    for (key, action) in [
        (Key::F3, InputAction::ToggleAllegiancePanel),
        (Key::F4, InputAction::ToggleFellowshipPanel),
        (Key::Tab, InputAction::ToggleChatEntry),
    ] {
        let bound: Vec<_> = bindings.for_action(action).map(|b| b.chord.key).collect();
        assert!(
            bound.contains(&ac_ui::input::InputKey::Key(key)),
            "{action:?}: {bound:?}"
        );
    }
    for action in [
        InputAction::AcdreamToggleFlyMode,
        InputAction::AcdreamTogglePlayerMode,
    ] {
        assert!(bindings.for_action(action).next().is_none(), "{action:?}");
    }
}

#[test]
fn the_keypad_star_takes_a_screenshot() {
    // OpenAC `KeyBindings.RetailDefaults`: KeypadMultiply is CaptureScreenshot.
    let mut r = Rig::new();
    r.tap(Key::KeypadMultiply);
    assert_eq!(r.take(), [GameplayOutput::CaptureScreenshot]);
}

#[test]
fn the_chat_entry_and_split_keys_go_to_the_interface() {
    // OpenAC `RetailUiRuntime.HandleInputAction`: Enter (EnterChatMode) and
    // Tab (ToggleChatEntry) go to the chat window with the dispatcher's
    // physical chord, so the key types nothing into the entry it focused;
    // automation presses them with no key. T (SelectionSplitStack) is the
    // interface's (`LivePresentationComposition`: the selected stack's
    // split entry); the give and drop keys are the session's
    // (`SelectionInteractionController`, `selection_combat_and_log_out_*`).
    let mut r = Rig::new();
    r.tap(Key::Enter);
    r.tap(Key::Tab);
    r.tap(Key::T);
    assert_eq!(
        r.take(),
        [
            GameplayOutput::ChatEntry(InputAction::EnterChatMode, Some(Key::Enter)),
            GameplayOutput::ChatEntry(InputAction::ToggleChatEntry, Some(Key::Tab)),
            GameplayOutput::Interface(InputAction::SelectionSplitStack),
        ]
    );
    let mut out = Vec::new();
    let state = DeviceState::default();
    assert!(r.input.automation_action(
        InputAction::EnterChatMode,
        None,
        &state,
        None,
        0.0,
        &mut out
    ));
    assert_eq!(
        out,
        [GameplayOutput::ChatEntry(InputAction::EnterChatMode, None)]
    );
    assert!(crate::ui::status::is_interface_action(
        InputAction::EnterChatMode
    ));
    assert!(crate::ui::status::is_interface_action(
        InputAction::ToggleChatEntry
    ));
    assert!(crate::ui::status::is_interface_action(
        InputAction::SelectionSplitStack
    ));
}

#[test]
fn a_key_the_player_binds_to_the_fly_camera_toggles_it() {
    let mut bindings = KeyBindings::retail_defaults();
    bindings.add(ac_ui::input::Binding::new(
        ac_ui::input::KeyChord::key(Key::F13, Modifiers::NONE),
        InputAction::AcdreamToggleFlyMode,
    ));
    let mut r = Rig::new();
    r.input = GameplayInput::new(bindings);
    r.tap(Key::F13);
    assert_eq!(r.take(), [GameplayOutput::ToggleFly]);
}

/// OpenAC `RetailUiRuntime.HandleInputAction` hands the physical chord to
/// the chat entry keys whatever the activation: a keyboard click fires as
/// the key goes down, so a chat key bound as a click carries its key too
/// (the entry it focuses ignores that key until it is let go).
#[test]
fn a_chat_entry_key_bound_as_a_click_carries_its_key() {
    let mut bindings = KeyBindings::retail_defaults();
    let enter = ac_ui::input::KeyChord::key(Key::Enter, Modifiers::NONE);
    let press = bindings
        .find(enter, ActivationType::Press)
        .expect("the retail key map binds Enter");
    assert!(bindings.remove(&press));
    bindings.add(
        ac_ui::input::Binding::new(enter, InputAction::EnterChatMode)
            .activation(ActivationType::Click),
    );
    let mut r = Rig::new();
    r.input = GameplayInput::new(bindings);
    r.key(Key::Enter, true);
    assert_eq!(
        r.take(),
        [GameplayOutput::ChatEntry(
            InputAction::EnterChatMode,
            Some(Key::Enter)
        )]
    );
    r.key(Key::Enter, false);
    assert!(r.take().is_empty());
}

/// OpenAC `GameplayInputActionRouter.Route`: a press, a double click and a
/// click all go on to the interface, the character options and the
/// selection; a player may bind examine, a panel or use to a click.
#[test]
fn an_action_bound_to_a_click_reaches_the_interface_and_the_session() {
    use ac_runtime::inventory::ItemCommand as I;
    let mut bindings = KeyBindings::retail_defaults();
    for (key, action) in [
        (Key::F13, InputAction::SelectionExamine),
        (Key::F14, InputAction::ToggleInventoryPanel),
        (Key::F15, InputAction::UseSelected),
    ] {
        bindings.add(
            ac_ui::input::Binding::new(ac_ui::input::KeyChord::key(key, Modifiers::NONE), action)
                .activation(ActivationType::Click),
        );
    }
    let mut r = Rig::new();
    r.input = GameplayInput::new(bindings);
    r.tap(Key::F13);
    r.tap(Key::F14);
    r.tap(Key::F15);
    assert_eq!(
        r.take(),
        [
            GameplayOutput::Interface(InputAction::SelectionExamine),
            GameplayOutput::Interface(InputAction::ToggleInventoryPanel),
            GameplayOutput::Item(I::UseSelected),
        ]
    );
}

#[test]
fn the_camera_settings_drive_the_held_keys_and_the_mouse() {
    // OpenAC `RuntimeSettingsTargets.ApplyCameraTurning`: the adjustment
    // speed moves the held camera keys; the mouse-look sensitivity and the
    // inverted vertical axis turn the right-button orbit and mouse look.
    let mut r = Rig::new();
    r.cameras.chase.tuning = ChaseTuning {
        adjustment_speed: 80.0,
        mouse_look_sensitivity: 1.0,
        invert_mouse_look_y_axis: true,
        ..ChaseTuning::default()
    };
    let distance = r.cameras.chase.distance;
    r.key(Key::KeypadAdd, true);
    r.tick(1.0 / 80.0);
    r.key(Key::KeypadAdd, false);
    assert!((r.cameras.chase.distance - distance * 1.2).abs() < 1e-3);

    // 15 px right at sensitivity 1 is one unit (8 degrees) of swing; OpenAC
    // flips the horizontal sense with the inverted axis too.
    r.button(mouse::RIGHT, true);
    let yaw = r.cameras.chase.yaw_offset;
    let state = r.state();
    r.input
        .pointer_moved(15.0, 0.0, &state, Some(&mut r.cameras), r.now + 10.0);
    assert!((r.cameras.chase.yaw_offset - yaw - crate::camera::OFFSET_ANGLE_RADIANS).abs() < 1e-4);
    // Down with the axis inverted lowers the boom (it raises it otherwise).
    let pitch = r.cameras.chase.pitch;
    r.input
        .pointer_moved(0.0, 15.0, &state, Some(&mut r.cameras), r.now + 20.0);
    assert!(
        r.cameras.chase.pitch < pitch,
        "inverted: down lowers the boom"
    );
    r.button(mouse::RIGHT, false);
}

#[test]
fn the_middle_button_mouse_looks_and_turns_the_character() {
    let mut r = Rig::new();
    assert_eq!(r.cameras.mode(), CameraMode::Chase);
    r.button(mouse::MIDDLE, true);
    assert!(r.input.mouse_look_active());
    assert_eq!(
        r.take(),
        [
            GameplayOutput::Movement(MovementCommand::BeginMouseLook),
            GameplayOutput::GrabCursor
        ]
    );
    // The first five samples of a motion are ignored; the sixth turns.
    let mut turns = Vec::new();
    for _ in 0..6 {
        r.input.raw_motion(10.0, 0.0, Some(&mut r.cameras));
        r.tick(0.01);
        turns.extend(r.movement());
    }
    assert_eq!(turns.len(), 1, "{turns:?}");
    assert!(matches!(turns[0], MovementCommand::MouseTurn(a) if a < 0.0));
    // Still for longer than 0.2 s: one zero sample stops the drift.
    r.tick(0.25);
    assert_eq!(r.movement(), [MovementCommand::StopMouseDrift]);
    r.button(mouse::MIDDLE, false);
    assert!(!r.input.mouse_look_active());
    assert_eq!(
        r.take(),
        [
            GameplayOutput::Movement(MovementCommand::EndMouseLook),
            GameplayOutput::ReleaseCursor
        ]
    );
}

#[test]
fn the_alternate_mode_key_mouse_looks_and_moves_the_arrows_to_the_camera() {
    let mut r = Rig::new();
    r.key(Key::F2, true);
    assert!(r.input.mouse_look_active());
    assert_eq!(
        r.input.dispatcher().active_scope(),
        ac_ui::input::InputScope::Camera
    );
    r.take();
    let pitch = r.cameras.chase.pitch;
    r.key(Key::Up, true);
    r.tick(0.1);
    assert!(r.cameras.chase.pitch > pitch, "the arrow raises the camera");
    r.key(Key::Up, false);
    r.key(Key::F2, false);
    assert!(!r.input.mouse_look_active());
    assert_eq!(
        r.input.dispatcher().active_scope(),
        ac_ui::input::InputScope::Game
    );
}

#[test]
fn keypad_keys_choose_views_and_hold_to_move_the_camera() {
    let mut r = Rig::new();
    r.tap(Key::KeypadDecimal);
    assert!(r.cameras.chase.is_in_head());
    r.tap(Key::Keypad0);
    assert!(!r.cameras.chase.is_in_head());
    r.tap(Key::Keypad5);
    assert!(r.cameras.chase.is_looking_down());
    r.tap(Key::Keypad5);
    assert!(!r.cameras.chase.is_looking_down());
    r.tap(Key::KeypadEnter);
    assert!(r.cameras.chase.is_map_mode());
    r.tap(Key::KeypadEnter);
    let distance = r.cameras.chase.distance;
    r.key(Key::KeypadAdd, true);
    r.tick(1.0 / 40.0);
    assert!(
        (r.cameras.chase.distance - distance * 1.2).abs() < 1e-3,
        "one step a 40th of a second: {} -> {}",
        distance,
        r.cameras.chase.distance
    );
    r.key(Key::KeypadAdd, false);
    let yaw = r.cameras.chase.yaw_offset;
    r.key(Key::Keypad4, true);
    r.tick(1.0 / 40.0);
    assert!(r.cameras.chase.yaw_offset < yaw, "keypad 4 swings left");
    // A camera key held when the chat entry takes the keyboard goes on
    // swinging until it is released, as a held movement key does.
    r.want_keyboard = true;
    let yaw = r.cameras.chase.yaw_offset;
    r.tick(1.0 / 40.0);
    assert!(r.cameras.chase.yaw_offset < yaw, "the held key was let go");
    r.key(Key::Keypad4, false);
    let yaw = r.cameras.chase.yaw_offset;
    r.tick(1.0 / 40.0);
    assert_eq!(r.cameras.chase.yaw_offset, yaw);
}

#[test]
fn the_wheel_zooms_the_chase_boom_and_the_orbit() {
    let mut r = Rig::new();
    let distance = r.cameras.chase.distance;
    let state = r.state();
    r.input
        .wheel(1.0, &state, Some(&mut r.cameras), r.now, &mut r.out);
    assert!(
        r.cameras.chase.distance < distance,
        "away from the user zooms in"
    );
    r.cameras.toggle_player_mode();
    assert_eq!(r.cameras.mode(), CameraMode::Orbit);
    r.cameras.orbit.distance = 300.0;
    r.input
        .wheel(-1.0, &state, Some(&mut r.cameras), r.now, &mut r.out);
    assert_eq!(r.cameras.orbit.distance, 320.0);
}

#[test]
fn the_right_button_orbits_the_chase_camera() {
    let mut r = Rig::new();
    r.button(mouse::RIGHT, true);
    assert!(r.input.rmb_orbit_held());
    let yaw = r.cameras.chase.yaw_offset;
    let state = r.state();
    r.input
        .pointer_moved(30.0, 0.0, &state, Some(&mut r.cameras), r.now);
    assert!(
        r.cameras.chase.yaw_offset < yaw,
        "dragging right swings the boom"
    );
    r.button(mouse::RIGHT, false);
    assert!(!r.input.rmb_orbit_held());
}

#[test]
fn the_fly_camera_takes_the_movement_keys_and_the_character_stops() {
    let mut r = Rig::new();
    r.key(Key::W, true);
    r.tick(0.01);
    r.take();
    r.cameras.toggle_fly();
    let fly = r.tick(0.01);
    assert!(fly.forward);
    assert_eq!(
        r.movement(),
        [MovementCommand::Keys(MovementKeys::default())]
    );
}

#[test]
fn losing_the_focus_releases_everything() {
    let mut r = Rig::new();
    r.key(Key::W, true);
    r.button(mouse::MIDDLE, true);
    r.tick(0.01);
    r.take();
    r.input.focus_lost(&mut r.out);
    let out = r.take();
    assert!(out.contains(&GameplayOutput::Movement(MovementCommand::EndMouseLook)));
    assert!(
        out.contains(&GameplayOutput::Movement(MovementCommand::Keys(
            MovementKeys::default()
        )))
    );
    r.tick(0.01);
    assert!(r.movement().is_empty(), "nothing is held any more");
}

#[test]
fn the_digits_are_quick_slots_outside_magic_and_spell_slots_in_it() {
    // OpenAC `GameplayInputActionRouter.SetCombatScope`: the magic scope's
    // bindings take the digit keys while the player is in magic mode.
    use ac_core::combat::CombatMode;
    let mut r = Rig::new();
    r.tap(Key::Number3);
    r.input.set_combat_mode(CombatMode::MAGIC);
    r.tap(Key::Number3);
    r.input.set_combat_mode(CombatMode::NON_COMBAT);
    r.tap(Key::Number3);
    assert_eq!(
        r.take(),
        [
            GameplayOutput::Interface(InputAction::UseQuickSlot_3),
            GameplayOutput::Interface(InputAction::UseSpellSlot_3),
            GameplayOutput::Interface(InputAction::UseQuickSlot_3),
        ]
    );
}

/// OpenAC `SelectionInteractionController.HandleInputAction`: the retail
/// key map's mouse selects reach the world pick (left press selects, a
/// left double click uses, a right click examines).
#[test]
fn the_mouse_selects_examines_and_uses_through_the_world_pick() {
    use crate::world_click::WorldClick;
    let clicks = |out: Vec<GameplayOutput>| -> Vec<WorldClick> {
        out.into_iter()
            .filter_map(|o| match o {
                GameplayOutput::WorldClick(c) => Some(c),
                _ => None,
            })
            .collect()
    };
    let mut r = Rig::new();
    r.button(mouse::LEFT, true);
    r.button(mouse::LEFT, false);
    assert_eq!(clicks(r.take()), [WorldClick::Select]);
    r.now += 0.1;
    r.button(mouse::LEFT, true);
    r.button(mouse::LEFT, false);
    assert!(clicks(r.take()).contains(&WorldClick::Use));
    r.now += 2.0;
    r.button(mouse::RIGHT, true);
    r.button(mouse::RIGHT, false);
    assert_eq!(clicks(r.take()), [WorldClick::Examine]);
}

/// OpenAC `GameplayInputActionRouter.HandlePressedMovementAction`: an
/// emote key plays its motion as the player's own (J waves, O cheers).
#[test]
fn the_emote_keys_play_their_motions() {
    let mut r = Rig::new();
    r.tap(Key::J);
    r.tap(Key::O);
    assert_eq!(
        r.movement(),
        [
            MovementCommand::Motion(ac_motion::motion_command::WAVE),
            MovementCommand::Motion(ac_motion::motion_command::CHEER)
        ]
    );
}

/// OpenAC `GameplayInputCommandControllerTests.RecognizedCommand_RoutesToTypedOwner`
/// (the combat toggle, log out) and `UnknownCommand_IsNotClaimed`, with the
/// selection keys of `RuntimeGameplayInputPriorityTargets.HandleSelectionAction`
/// and `SelectionInteractionController.HandleInputAction`.
#[test]
fn selection_combat_and_log_out_actions_go_to_the_session() {
    use ac_runtime::gameplay::GameplayCommand as G;
    use ac_runtime::gameplay::selection_cycle::{
        SelectionCycleDirection as D, SelectionCycleKind as K,
    };
    use ac_runtime::inventory::ItemCommand as I;
    let cases = [
        (
            InputAction::CombatToggleCombat,
            GameplayOutput::Gameplay(G::ToggleCombat),
        ),
        (InputAction::LOGOUT, GameplayOutput::LogOut),
        (
            InputAction::SelectionClosestMonster,
            GameplayOutput::Gameplay(G::SelectNearestMonster),
        ),
        (
            InputAction::SelectionPreviousSelection,
            GameplayOutput::Gameplay(G::SelectPrevious),
        ),
        (
            InputAction::SelectionNextMonster,
            GameplayOutput::Gameplay(G::CycleSelection {
                kind: K::Monster,
                direction: D::Next,
            }),
        ),
        (
            InputAction::SelectionUseClosestUnopenedCorpse,
            GameplayOutput::Gameplay(G::UseUnopenedCorpse(D::Closest)),
        ),
        (
            InputAction::SelectionNextFellow,
            GameplayOutput::Gameplay(G::CycleFellow { previous: false }),
        ),
        (
            InputAction::SelectionExamine,
            GameplayOutput::Item(I::ExamineSelected),
        ),
        (
            InputAction::UseSelected,
            GameplayOutput::Item(I::UseSelected),
        ),
        (
            InputAction::SelectionPickUp,
            GameplayOutput::Item(I::PickUpSelected),
        ),
        (
            InputAction::SelectionPlaceInInventory,
            GameplayOutput::Item(I::PlaceSelectedInPack),
        ),
        (
            InputAction::SelectionPlaceInMainPack,
            GameplayOutput::Item(I::PickUpSelectedToMainPack),
        ),
        (
            InputAction::SelectionGiveToTarget,
            GameplayOutput::Item(I::GiveSelectedToPrevious { amount: 0 }),
        ),
        (
            InputAction::SelectionDrop,
            GameplayOutput::Item(I::DropSelected { amount: 0 }),
        ),
    ];
    for (action, expected) in cases {
        assert_eq!(session_output(action), Some(expected), "{action:?}");
    }
    assert_eq!(session_output(InputAction::MovementForward), None);
    assert_eq!(session_output(InputAction::ToggleInventoryPanel), None);
}

#[test]
fn the_retail_selection_keys_reach_the_session() {
    // The retail key map: ' closest monster, P previous selection, R use,
    // E examine (the interface's first: an examination that shows closes
    // on it; else it goes on to the session), Shift+Escape log out.
    use ac_runtime::gameplay::GameplayCommand as G;
    use ac_runtime::inventory::ItemCommand as I;
    let mut r = Rig::new();
    r.tap(Key::Apostrophe);
    r.tap(Key::P);
    r.tap(Key::R);
    r.tap(Key::E);
    r.modifiers = Modifiers::SHIFT;
    r.key(Key::Escape, true);
    let out: Vec<GameplayOutput> = r
        .take()
        .into_iter()
        .filter(|o| !matches!(o, GameplayOutput::Movement(_)))
        .collect();
    assert_eq!(
        out,
        [
            GameplayOutput::Gameplay(G::SelectNearestMonster),
            GameplayOutput::Gameplay(G::SelectPrevious),
            GameplayOutput::Item(I::UseSelected),
            GameplayOutput::Interface(InputAction::SelectionExamine),
            GameplayOutput::LogOut,
        ]
    );
}

/// OpenAC `CombatAttackInputFrameAdapter`: in melee the retail Delete key
/// presses and releases the low attack, Page Up raises the power, and a
/// movement press ends a repeating attack before it moves.
#[test]
fn the_attack_keys_and_a_movement_press_reach_the_session() {
    use ac_core::combat::CombatMode;
    use ac_runtime::gameplay::{
        CombatAttackCommand as C, GameplayCommand as G, InputActivation as I,
    };
    let mut r = Rig::new();
    r.input.set_combat_mode(CombatMode::MELEE);
    r.key(Key::Delete, true);
    r.tick(0.1);
    r.key(Key::Delete, false);
    r.tap(Key::PageUp);
    r.tap(Key::S);
    let out: Vec<GameplayOutput> = r
        .take()
        .into_iter()
        .filter(|o| !matches!(o, GameplayOutput::Movement(_)))
        .collect();
    assert_eq!(
        out,
        [
            GameplayOutput::Gameplay(G::AttackInput(C::LowAttack, I::Press)),
            GameplayOutput::Gameplay(G::AttackInput(C::LowAttack, I::Release)),
            GameplayOutput::Gameplay(G::AttackInput(C::IncreasePower, I::Press)),
            GameplayOutput::Gameplay(G::AttackInput(C::AbortForMovement, I::Press)),
        ]
    );
    assert!(!ends_repeating_attack(InputAction::MovementWalkMode));
    assert!(ends_repeating_attack(InputAction::Sitting));
    assert_eq!(
        combat_attack_command(InputAction::CombatDecreaseMissileAccuracy),
        Some(C::DecreasePower)
    );
    assert_eq!(
        combat_attack_command(InputAction::CombatAimHigh),
        Some(C::HighAttack)
    );
}

/// OpenAC `RuntimeGameplayInputPriorityTargets.HandleCharacterOptionAction`:
/// a character-option action flips its option (the window reads the value).
#[test]
fn a_character_option_key_flips_its_option() {
    use ac_ui::input::{Binding, KeyChord};
    let chord = Modifiers::ALT | Modifiers::SHIFT;
    let mut bindings = KeyBindings::retail_defaults();
    bindings.add(Binding::new(
        KeyChord::key(Key::F7, chord),
        InputAction::ToggleCharacterOptionToggleRun,
    ));
    let mut r = Rig::new();
    r.input = GameplayInput::new(bindings);
    r.modifiers = chord;
    r.tap(Key::F7);
    let option =
        ac_ui::input::identity::character_option_id(InputAction::ToggleCharacterOptionToggleRun)
            .unwrap();
    assert_eq!(
        r.take()
            .into_iter()
            .filter(|o| !matches!(o, GameplayOutput::Movement(_)))
            .collect::<Vec<_>>(),
        [GameplayOutput::ToggleCharacterOption(option)]
    );
}

/// OpenAC `GameplayInputCommandControllerTests.RecognizedCommand_RoutesToTypedOwner`
/// (chat-window-1..4) and `Escape_PreservesRetailTargetThenGameplayOptionsPriority`:
/// Alt+1 toggles floating chat window 1 and uses quick slot 10 (both
/// bindings fire), and Escape goes on (to the session, which decides what
/// it stops, cancels or opens).
#[test]
fn alt_digits_toggle_the_floating_chat_windows_and_escape_goes_on() {
    let mut r = Rig::new();
    r.modifiers = Modifiers::ALT;
    r.tap(Key::Number1);
    r.tap(Key::Number4);
    let out = r.take();
    for action in [
        InputAction::ToggleFloatingChatWindow1,
        InputAction::UseQuickSlot_10,
        InputAction::ToggleFloatingChatWindow4,
        InputAction::UseQuickSlot_13,
    ] {
        assert!(
            out.contains(&GameplayOutput::Interface(action)),
            "{action:?} in {out:?}"
        );
    }
    r.modifiers = Modifiers::NONE;
    r.tap(Key::Escape);
    let out: Vec<GameplayOutput> = r
        .take()
        .into_iter()
        .filter(|o| !matches!(o, GameplayOutput::Movement(_)))
        .collect();
    assert_eq!(out, [GameplayOutput::Escape]);
}

/// What plugins listening for input actions hear: every action
/// the dispatcher fires, oldest first, with how it fired, but not a held
/// action's per-tick repeats; taking them empties the list.
#[test]
fn fired_actions_are_kept_for_the_plugins_without_hold_repeats() {
    let mut r = Rig::new();
    r.key(Key::W, true);
    r.tick(1.0 / 60.0);
    r.tick(1.0 / 60.0);
    r.key(Key::W, false);
    r.tap(Key::F1);
    let fired: Vec<(&str, &str)> = r
        .input
        .take_fired()
        .into_iter()
        .map(|(action, activation)| (action.name(), activation.name()))
        .collect();
    // The retail key map: W presses MovementForward (held movement is
    // polled, not repeated), F1 presses ToggleHelp.
    assert_eq!(
        fired,
        [("MovementForward", "Press"), ("ToggleHelp", "Press")]
    );
    assert!(r.input.take_fired().is_empty());
}
