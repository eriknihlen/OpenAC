//! OpenAC `tests/AcDream.Runtime.Tests/Gameplay/PlayerMouseLookMovementTests.cs`.

use ac_motion::HoldKey;
use ac_motion::motion_command::{
    SIDE_STEP_LEFT, SIDE_STEP_RIGHT, TURN_LEFT, TURN_RIGHT, WALK_FORWARD,
};

use super::player_tests::Rig;
use crate::body::PhysicsBody;
use crate::movement::{
    InboundInterpretedState, MotionInterp, MovementEnv, MovementInput, MovementResult, NoTargets,
    PlayerMovementController, PlayerState, SinkArg, build_raw_motion_state,
};

fn input() -> MovementInput {
    MovementInput::default()
}

fn create() -> Rig {
    Rig::seeded()
}

fn begin(r: &mut Rig, i: MovementInput) -> bool {
    let mut env = MovementEnv::new(&mut r.engine, &NoTargets);
    r.c.begin_mouse_look(&mut env, i, false)
}

fn begin_modern(r: &mut Rig, i: MovementInput) -> bool {
    let mut env = MovementEnv::new(&mut r.engine, &NoTargets);
    r.c.begin_mouse_look(&mut env, i, true)
}

fn end(r: &mut Rig, i: MovementInput) -> bool {
    let mut env = MovementEnv::new(&mut r.engine, &NoTargets);
    r.c.end_mouse_look(&mut env, i)
}

fn adjust(r: &mut Rig, a: f32, i: MovementInput) {
    let mut env = MovementEnv::new(&mut r.engine, &NoTargets);
    r.c.submit_mouse_turn_adjustment(&mut env, a, i);
}

fn stop_drift(r: &mut Rig, i: MovementInput) {
    let mut env = MovementEnv::new(&mut r.engine, &NoTargets);
    r.c.stop_mouse_drift(&mut env, i);
}

fn prepare_attack(r: &mut Rig) -> bool {
    let mut env = MovementEnv::new(&mut r.engine, &NoTargets);
    r.c.prepare_for_attack_request(&mut env)
}

fn capture(r: &Rig) -> MovementResult {
    r.c.capture_movement_result(false)
}

fn note_sent(r: &mut Rig, mouse: bool) {
    let now = r.c.sim_time_seconds();
    r.c.note_movement_sent(now, mouse);
}

fn server_posture(r: &mut Rig) {
    r.c.set_last_move_was_autonomous(false);
    let ims = InboundInterpretedState {
        current_style: 0x8000_0049,
        forward_command: ac_motion::motion_command::READY,
        forward_speed: 1.0,
        turn_command: 0,
        turn_speed: 1.0,
        ..InboundInterpretedState::default()
    };
    r.c.object_mut()
        .cx(&NoTargets)
        .move_to_interpreted_state(&ims, SinkArg::Given(None));
}

#[test]
fn keyboard_edges_request_immediate_events() {
    let mut r = create();
    let fwd = MovementInput {
        forward: true,
        ..input()
    };
    let pressed = r.update(1.0 / 60.0, fwd);
    let held = r.update(1.0 / 60.0, fwd);
    let released = r.update(1.0 / 60.0, input());
    assert!(pressed.should_send_movement_event);
    assert!(!held.should_send_movement_event);
    assert!(released.should_send_movement_event);
}

#[test]
fn a_mouse_adjustment_turns_by_motion() {
    let mut r = create();
    assert!(begin(&mut r, input()));
    note_sent(&mut r, false);
    let yaw_before = r.c.yaw();
    adjust(&mut r, -0.4, input());
    let turning = r.update(PhysicsBody::MIN_QUANTUM + 0.001, input());
    assert_eq!(turning.turn_command, Some(TURN_RIGHT));
    assert_eq!(turning.turn_speed, Some(0.8));
    assert!(r.c.yaw() < yaw_before);
    assert!(!turning.should_send_movement_event);
    assert!(turning.turn_uses_run_hold);
    let held = r.update(1.0 / 60.0, input());
    assert_eq!(held.turn_command, Some(TURN_RIGHT));
    stop_drift(&mut r, input());
    let stopped = r.update(1.0 / 60.0, input());
    assert_eq!(stopped.turn_command, None);
    assert!(!stopped.should_send_movement_event);
}

#[test]
fn mouse_look_reports_start_cadence_and_stop() {
    let mut r = create();
    assert!(begin(&mut r, input()));
    let b = capture(&r);
    assert!(b.should_send_movement_event);
    assert!(!b.is_mouse_look_movement_event);
    note_sent(&mut r, false);
    adjust(&mut r, 0.5, input());
    let sample = r.update(0.01, input());
    assert_eq!(sample.turn_command, Some(TURN_LEFT));
    assert!(!sample.should_send_movement_event);
    r.update(
        PlayerMovementController::MOUSE_MOVEMENT_EVENT_INTERVAL,
        input(),
    );
    adjust(&mut r, 0.5, input());
    let cadence = r.update(0.001, input());
    assert!(cadence.should_send_movement_event);
    assert!(cadence.is_mouse_look_movement_event);
    note_sent(&mut r, true);
    assert!(end(&mut r, input()));
    let ended = capture(&r);
    assert!(ended.should_send_movement_event);
    assert!(!ended.is_mouse_look_movement_event);
    assert_eq!(ended.turn_command, None);
}

#[test]
fn the_mouse_event_is_retried_until_acknowledged() {
    let mut r = create();
    begin(&mut r, input());
    r.update(
        PlayerMovementController::MOUSE_MOVEMENT_EVENT_INTERVAL + 0.001,
        input(),
    );
    adjust(&mut r, 0.4, input());
    let run = MovementInput {
        run: true,
        ..input()
    };
    let first = r.update(0.01, run);
    let retry = r.update(0.01, run);
    assert!(first.should_send_movement_event);
    assert!(first.is_mouse_look_movement_event);
    assert!(retry.should_send_movement_event);
    note_sent(&mut r, true);
    assert!(!r.update(0.01, run).should_send_movement_event);
}

#[test]
fn a_rapid_press_and_release_are_both_captured() {
    let mut r = create();
    let run = MovementInput {
        run: true,
        ..input()
    };
    assert!(begin(&mut r, run));
    let press = capture(&r);
    assert!(end(&mut r, run));
    let release = capture(&r);
    assert!(press.should_send_movement_event && !press.is_mouse_look_movement_event);
    assert!(release.should_send_movement_event && !release.is_mouse_look_movement_event);
}

#[test]
fn the_cadence_boundary_is_strict() {
    let mut r = create();
    begin(&mut r, input());
    r.c.note_movement_sent(0.0, true);
    adjust(&mut r, 0.4, input());
    let boundary = r.update(
        PlayerMovementController::MOUSE_MOVEMENT_EVENT_INTERVAL,
        input(),
    );
    assert!(!boundary.should_send_movement_event);
    adjust(&mut r, 0.4, input());
    let over = r.update(0.0001, input());
    assert!(over.should_send_movement_event);
    assert!(over.is_mouse_look_movement_event);
}

#[test]
fn a_mouse_turn_carries_run_even_walking() {
    let mut r = create();
    begin(&mut r, input());
    r.c.note_movement_sent(0.0, true);
    adjust(&mut r, -0.4, input());
    let turning = r.update(0.01, input());
    assert_eq!(turning.turn_command, Some(TURN_RIGHT));
    assert!(!turning.is_running);
    assert!(turning.turn_uses_run_hold);
    assert_eq!(r.c.object().interp.raw_state.turn_hold_key, HoldKey::Run);
}

#[test]
fn releasing_mouse_look_restores_a_held_keyboard_turn() {
    let mut r = create();
    let held = MovementInput {
        turn_left: true,
        run: true,
        ..input()
    };
    r.update(0.01, held);
    begin(&mut r, held);
    note_sent(&mut r, false);
    adjust(&mut r, -0.4, held);
    r.update(0.01, held);
    end(&mut r, held);
    let release = capture(&r);
    assert_eq!(release.turn_command, Some(TURN_LEFT));
    assert_eq!(release.turn_speed, Some(1.0));
    assert!(!release.turn_uses_run_hold);
}

#[test]
fn a_held_turn_becomes_a_sidestep_and_back() {
    let mut r = create();
    let held = MovementInput {
        turn_left: true,
        run: true,
        ..input()
    };
    r.update(0.01, held);
    assert!(begin(&mut r, held));
    let press = capture(&r);
    assert_eq!(press.turn_command, None);
    assert_eq!(press.sidestep_command, Some(SIDE_STEP_LEFT));
    assert_eq!(
        r.c.object().interp.raw_state.sidestep_hold_key,
        HoldKey::Run
    );
    assert!(end(&mut r, held));
    let release = capture(&r);
    assert_eq!(release.sidestep_command, None);
    assert_eq!(release.turn_command, Some(TURN_LEFT));
    assert!(!release.turn_uses_run_hold);
}

#[test]
fn entering_keeps_held_forward_and_run() {
    let mut r = create();
    let held = MovementInput {
        forward: true,
        run: true,
        ..input()
    };
    assert!(begin(&mut r, held));
    let press = capture(&r);
    assert_eq!(press.forward_command, Some(WALK_FORWARD));
    assert!(press.is_running);
    assert_eq!(r.c.object().interp.raw_state.current_hold_key, HoldKey::Run);
}

#[test]
fn entering_on_the_frame_forward_is_pressed_keeps_it() {
    let mut r = create();
    r.update(
        0.01,
        MovementInput {
            forward: true,
            ..input()
        },
    );
    r.update(0.01, input());
    begin(
        &mut r,
        MovementInput {
            forward: true,
            run: true,
            ..input()
        },
    );
    let press = capture(&r);
    assert_eq!(press.forward_command, Some(WALK_FORWARD));
    assert!(press.is_running);
    assert_eq!(r.c.object().interp.raw_state.current_hold_key, HoldKey::Run);
}

#[test]
fn entering_on_the_frame_run_changes_keeps_it() {
    let mut r = create();
    r.update(
        0.01,
        MovementInput {
            forward: true,
            ..input()
        },
    );
    r.update(0.01, input());
    begin(
        &mut r,
        MovementInput {
            run: true,
            ..input()
        },
    );
    let press = capture(&r);
    let wire = build_raw_motion_state(&press);
    assert_eq!(r.c.object().interp.raw_state.current_hold_key, HoldKey::Run);
    assert!(press.is_running);
    assert_eq!(wire.current_hold_key, HoldKey::Run);
    assert_eq!(wire.forward_hold_key, HoldKey::Invalid);
    assert_eq!(wire.sidestep_hold_key, HoldKey::Invalid);
    assert_eq!(wire.turn_hold_key, HoldKey::Invalid);
}

#[test]
fn entering_with_a_server_held_turn_never_queues_the_wrong_axis() {
    let mut r = create();
    begin(
        &mut r,
        MovementInput {
            turn_left: true,
            ..input()
        },
    );
    assert!(
        !r.c.object()
            .interp
            .pending_motions()
            .any(|n| n.motion == TURN_LEFT)
    );
    assert_eq!(
        r.c.object().interp.raw_state.sidestep_command,
        SIDE_STEP_LEFT
    );
}

#[test]
fn leaving_with_a_server_held_turn_never_queues_the_remapped_axis() {
    let mut r = create();
    let held = MovementInput {
        turn_left: true,
        ..input()
    };
    begin(&mut r, held);
    let remaining = r.c.object().interp.pending_motion_count();
    for _ in 0..remaining {
        r.c.object_mut().cx(&NoTargets).motion_done(0, true);
    }
    r.c.set_last_move_was_autonomous(false);
    end(&mut r, held);
    assert!(
        !r.c.object()
            .interp
            .pending_motions()
            .any(|n| n.motion == SIDE_STEP_LEFT)
    );
    assert_eq!(r.c.object().interp.raw_state.turn_command, TURN_LEFT);
}

#[test]
fn a_remap_pressed_after_entry_reaches_the_outbound_result() {
    let mut r = create();
    begin(&mut r, input());
    let result = r.update(
        0.01,
        MovementInput {
            turn_left: true,
            ..input()
        },
    );
    assert_eq!(result.sidestep_command, Some(SIDE_STEP_LEFT));
    assert_eq!(result.turn_command, None);
    assert!(result.sidestep_uses_run_hold);
    assert!(!result.is_running);
    assert!(result.should_send_movement_event);
}

#[test]
fn a_walk_mode_remap_carries_run_on_its_axis() {
    let mut r = create();
    let held = MovementInput {
        turn_right: true,
        run: false,
        ..input()
    };
    begin(&mut r, held);
    let press = capture(&r);
    let wire = build_raw_motion_state(&press);
    assert_eq!(press.sidestep_command, Some(SIDE_STEP_RIGHT));
    assert!(press.sidestep_uses_run_hold);
    assert!(!press.is_running);
    assert_eq!(
        r.c.object().interp.raw_state.sidestep_hold_key,
        HoldKey::Run
    );
    assert_eq!(wire.current_hold_key, HoldKey::None);
    assert_eq!(wire.sidestep_hold_key, HoldKey::Run);
}

#[test]
fn the_mouse_handler_retakes_control_keeping_held_input() {
    let mut r = create();
    let held = MovementInput {
        forward: true,
        turn_left: true,
        run: true,
        ..input()
    };
    begin(&mut r, held);
    r.c.set_last_move_was_autonomous(false);
    adjust(&mut r, -0.4, held);
    let result = r.update(0.01, held);
    assert_eq!(result.forward_command, Some(WALK_FORWARD));
    assert_eq!(result.sidestep_command, Some(SIDE_STEP_LEFT));
    assert_eq!(result.turn_command, Some(TURN_RIGHT));
    assert!(result.is_running);
    assert!(result.sidestep_uses_run_hold);
    assert!(result.turn_uses_run_hold);
}

#[test]
fn release_uses_the_current_input() {
    let mut r = create();
    let held = MovementInput {
        forward: true,
        turn_left: true,
        run: true,
        ..input()
    };
    r.update(0.01, held);
    begin(&mut r, held);
    assert!(end(&mut r, input()));
    let release = capture(&r);
    assert_eq!(release.forward_command, None);
    assert_eq!(release.sidestep_command, None);
    assert_eq!(release.turn_command, None);
    assert!(!release.is_running);
}

#[test]
fn mouse_look_cannot_begin_in_portal_space() {
    let mut r = create();
    r.c.state = PlayerState::PortalSpace;
    assert!(!begin(
        &mut r,
        MovementInput {
            forward: true,
            ..input()
        }
    ));
    let result = capture(&r);
    assert_eq!(result.forward_command, None);
    assert_eq!(result.sidestep_command, None);
    assert_eq!(result.turn_command, None);
}

#[test]
fn an_attack_stops_the_mouse_turn_and_publishes_the_heading() {
    let mut r = create();
    begin(&mut r, input());
    r.update(0.01, input());
    adjust(&mut r, -0.5, input());
    let turning = r.update(1.0 / 60.0, input());
    assert_eq!(turning.turn_command, Some(TURN_RIGHT));
    assert!(prepare_attack(&mut r));
    let final_yaw = r.c.yaw();
    let stopped = capture(&r);
    assert_eq!(r.c.yaw(), final_yaw);
    assert_eq!(stopped.turn_command, None);
    assert!(stopped.should_send_movement_event);
}

#[test]
fn an_attack_does_not_interrupt_server_movement() {
    let mut r = create();
    r.c.set_last_move_was_autonomous(false);
    assert!(!prepare_attack(&mut r));
    let result = r.update(
        0.01,
        MovementInput {
            run: true,
            ..input()
        },
    );
    assert!(!result.should_send_movement_event);
}

#[test]
fn an_attack_in_the_default_server_state_does_nothing() {
    let mut r = create();
    assert!(!prepare_attack(&mut r));
}

#[test]
fn a_persistent_command_retakes_the_turn_after_a_server_posture() {
    let mut r = create();
    let command = MovementInput {
        turn_right: true,
        is_persistent_command: true,
        ..input()
    };
    let started = r.update(1.0 / 60.0, command);
    assert_eq!(started.turn_command, Some(TURN_RIGHT));
    server_posture(&mut r);
    assert_eq!(r.c.object().interp.interpreted_state.turn_command, 0);
    let resumed = r.update(1.0 / 60.0, command);
    assert!(resumed.should_send_movement_event);
    assert_eq!(
        r.c.object().interp.interpreted_state.turn_command,
        TURN_RIGHT
    );
    assert_eq!(resumed.turn_command, Some(TURN_RIGHT));
}

#[test]
fn a_physically_held_key_keeps_retail_edges_after_a_server_posture() {
    let mut r = create();
    let physical = MovementInput {
        turn_right: true,
        ..input()
    };
    let _ = r.update(1.0 / 60.0, physical);
    server_posture(&mut r);
    let held = r.update(1.0 / 60.0, physical);
    assert!(!held.should_send_movement_event);
    assert_eq!(r.c.object().interp.interpreted_state.turn_command, 0);
}

// -- modern mouse turning ---------------------------------------------------
// OpenAC `PlayerMouseLookMovementTests` (modern facing turn).

fn facing(r: &mut Rig, yaw: f32) {
    r.c.set_mouse_look_facing_target(yaw);
}

fn wrap(angle: f32) -> f32 {
    use std::f32::consts::{PI, TAU};
    let w = (angle + PI).rem_euclid(TAU) - PI;
    if w <= -PI { w + TAU } else { w }
}

#[test]
fn modern_mouse_look_keyboard_turn_wins_without_sidestep_remapping() {
    let mut r = create();
    let keyboard = MovementInput {
        turn_left: true,
        run: true,
        ..input()
    };
    assert!(begin_modern(&mut r, keyboard));
    let entry = capture(&r);
    assert_eq!(entry.turn_command, Some(TURN_LEFT));
    assert_eq!(entry.sidestep_command, None);

    let yaw = r.c.yaw();
    facing(&mut r, yaw - 0.5);
    let combined = r.update(0.01, keyboard);
    assert_eq!(combined.turn_command, Some(TURN_LEFT));
    assert_eq!(combined.sidestep_command, None);
    assert!(!combined.turn_uses_run_hold);

    let released = MovementInput {
        run: true,
        ..input()
    };
    let yaw = r.c.yaw();
    facing(&mut r, yaw - 0.5);
    let mouse_only = r.update(0.01, released);
    assert_eq!(mouse_only.turn_command, Some(TURN_RIGHT));
    assert!(mouse_only.turn_uses_run_hold);

    assert!(end(&mut r, keyboard));
    let mouse_released = capture(&r);
    assert_eq!(mouse_released.turn_command, Some(TURN_LEFT));
    assert_eq!(mouse_released.sidestep_command, None);
}

/// Modern mouse turning turns the character like a held turn key: one
/// full-rate turn that the server hears start and stop, never re-issued at
/// a new speed, and it ends exactly on the view without turning past it.
#[test]
fn a_modern_facing_turn_is_one_steady_turn_that_stops_exactly_on_the_view() {
    let mut r = create();
    let run = MovementInput {
        run: true,
        ..input()
    };
    assert!(begin_modern(&mut r, run));
    note_sent(&mut r, false);
    let target = r.c.yaw() - 0.6; // About 34 degrees to the right.
    let mut sent = Vec::new();
    let mut turns = Vec::new();
    for _ in 0..144 {
        facing(&mut r, target);
        let result = r.update(1.0 / 144.0, run);
        turns.push((result.turn_command, result.turn_speed));
        if result.should_send_movement_event {
            sent.push(result);
            note_sent(&mut r, false);
        }
    }
    assert!(
        turns
            .iter()
            .filter(|t| t.0.is_some())
            .all(|t| *t == (Some(TURN_RIGHT), Some(1.0))),
        "{turns:?}"
    );
    assert_eq!(sent.len(), 2, "the start and the stop");
    assert_eq!(sent[0].turn_command, Some(TURN_RIGHT));
    assert!(sent[0].turn_uses_run_hold);
    assert_eq!(sent[1].turn_command, None);
    assert_eq!(turns.last().unwrap().0, None);
    assert!(wrap(target - r.c.yaw()).abs() < 1e-4);
    assert!(r.c.mouse_look_facing_settled());
}

#[test]
fn a_view_swung_back_past_the_facing_turns_the_other_way() {
    let mut r = create();
    let run = MovementInput {
        run: true,
        ..input()
    };
    assert!(begin_modern(&mut r, run));
    let start = r.c.yaw();
    facing(&mut r, start + 1.0);
    assert_eq!(r.update(1.0 / 30.0, run).turn_command, Some(TURN_LEFT));
    facing(&mut r, start - 1.0);
    assert_eq!(r.update(1.0 / 144.0, run).turn_command, Some(TURN_RIGHT));
    assert!(!r.c.mouse_look_facing_settled());
}

#[test]
fn the_facing_target_is_ignored_outside_a_modern_mouse_look() {
    let mut r = create();
    let run = MovementInput {
        run: true,
        ..input()
    };
    let yaw = r.c.yaw();
    facing(&mut r, yaw + 1.0);
    assert_eq!(r.update(1.0 / 30.0, run).turn_command, None);
    assert!(begin(&mut r, run));
    let yaw = r.c.yaw();
    facing(&mut r, yaw + 1.0);
    assert_eq!(r.update(1.0 / 30.0, run).turn_command, None);
}

/// The cause of the stutter the turn must not have: a view resting between
/// two physics turn steps. The body turns once, lands on the view, and
/// never turns back (no hunting), frame by frame.
#[test]
fn a_view_resting_between_turn_steps_causes_no_reversal() {
    let mut r = create();
    let run = MovementInput {
        run: true,
        ..input()
    };
    assert!(begin_modern(&mut r, run));
    note_sent(&mut r, false);
    // Prime the turn rate: one step's turn at the full rate.
    let start = r.c.yaw();
    facing(&mut r, start - 1.0);
    r.update(PhysicsBody::MIN_QUANTUM + 0.001, run);
    let step = (start - r.c.yaw()).abs();
    assert!(step > 0.01, "the body turned one step: {step}");
    // Rest the view a third of a step past the body, then keep it there.
    let target = r.c.yaw() - step / 3.0;
    let mut turns = Vec::new();
    let mut sent = 0;
    for _ in 0..240 {
        facing(&mut r, target);
        let result = r.update(1.0 / 144.0, run);
        turns.push(result.turn_command);
        if result.should_send_movement_event {
            sent += 1;
            note_sent(&mut r, false);
        }
    }
    assert!(turns.iter().all(|t| *t != Some(TURN_LEFT)), "{turns:?}");
    let edges = turns.windows(2).filter(|w| w[0] != w[1]).count();
    assert!(edges <= 1, "one stop at most, no restart: {turns:?}");
    assert!(sent <= 1, "at most the stop is sent: {sent}");
    assert!(wrap(target - r.c.yaw()).abs() < 1e-4);
    assert!(r.c.mouse_look_facing_settled());
}
