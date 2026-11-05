//! Scripted movement against sampled bodies.
//!
//! Ported from OpenAC
//! `tests/AcDream.Runtime.Tests/Gameplay/RuntimeScriptedMovementTests.cs`.
//! The four cases there that go through the local player's movement state
//! and its input source (`TheFramesOwnInputPassesThroughWhenNothingIsScripted`,
//! `AScriptedMoveTakesTheFrameUntilThePlayerMoves`,
//! `ATurnLandsTheBodyOnItsAngle`, `ATurnEndingChangesTheMovementRevision`)
//! belong to the runtime wiring and are ported with it. The two invalid
//! enum values of `AMoveIsPossibleOnlyWithinItsLimits` cannot be written in
//! Rust.

use ac_nav::motor::{
    MoveChannel, MoveDirection, MovePace, MoveRequest, ScriptedMoveOutput, ScriptedMoveSample,
    ScriptedMoveState, ScriptedMovement,
};
use ac_physics::movement::MovementInput;
use glam::Vec3;

#[derive(Default, Clone, Copy)]
struct At {
    east: f32,
    north: f32,
    heading: f32,
    portal: bool,
    manual: bool,
}

fn sample(time: f64, at: At) -> ScriptedMoveSample {
    ScriptedMoveSample {
        offset_from_start: Vec3::new(at.east, at.north, 0.0),
        heading_degrees: at.heading,
        time_seconds: time,
        in_portal_space: at.portal,
        manual_movement_held: at.manual,
    }
}

fn still(time: f64) -> ScriptedMoveSample {
    sample(time, At::default())
}

fn north(time: f64, north: f32) -> ScriptedMoveSample {
    sample(
        time,
        At {
            north,
            ..At::default()
        },
    )
}

fn heading(time: f64, heading: f32) -> ScriptedMoveSample {
    sample(
        time,
        At {
            heading,
            ..At::default()
        },
    )
}

fn input(output: ScriptedMoveOutput) -> MovementInput {
    output.input.expect("scripted input")
}

fn moving(direction: MoveDirection, amount: f32) -> ScriptedMovement {
    let mut movement = ScriptedMovement::new();
    assert!(movement.begin(MoveRequest::new(direction, MovePace::Run, amount)));
    movement
}

fn close(actual: f32, expected: f32, decimals: i32) {
    let tolerance = 0.5 * 10f32.powi(-decimals);
    assert!(
        (actual - expected).abs() <= tolerance,
        "{actual} is not {expected} to {decimals} decimals"
    );
}

#[test]
fn a_walk_that_ends_is_let_go_of_at_a_walk_so_the_body_stops_without_lurching_into_a_run() {
    let mut movement = ScriptedMovement::new();
    assert!(movement.begin(MoveRequest::new(
        MoveDirection::Forward,
        MovePace::Walk,
        0.0
    )));
    let first = input(movement.advance(&still(0.0)));
    assert!(first.forward && !first.run);

    assert!(movement.stop_channel(MoveChannel::Travel));

    let letting_go = input(movement.advance(&still(0.1)));
    assert!(!letting_go.forward && !letting_go.run && letting_go.is_persistent_command);
    assert!(movement.advance(&still(0.2)).input.is_none());
}

#[test]
fn a_run_that_ends_is_let_go_of_at_a_run() {
    let mut movement = ScriptedMovement::new();
    assert!(movement.begin(MoveRequest::new(MoveDirection::Forward, MovePace::Run, 0.0)));
    let first = input(movement.advance(&still(0.0)));
    assert!(first.forward && first.run);

    assert!(movement.stop_channel(MoveChannel::Travel));

    let letting_go = input(movement.advance(&still(0.1)));
    assert!(!letting_go.forward && letting_go.run && letting_go.is_persistent_command);
}

#[test]
fn a_move_holds_its_direction_and_completes_at_its_distance() {
    let mut movement = ScriptedMovement::new();
    assert!(movement.begin(MoveRequest::new(
        MoveDirection::Forward,
        MovePace::Walk,
        5.0
    )));

    let first = input(movement.advance(&still(0.0)));
    assert!(first.forward && !first.run && first.is_persistent_command);
    movement.advance(&north(1.0, 2.0));
    let arriving = input(movement.advance(&north(2.0, 5.0)));

    assert_eq!(
        movement.snapshot().travel.state,
        ScriptedMoveState::Completed
    );
    close(movement.snapshot().travel.covered, 5.0, 3);
    assert!(!arriving.forward && arriving.is_persistent_command);
    assert!(movement.advance(&north(2.1, 5.0)).input.is_none());
}

fn a_distance_ends_within_half_a_step_of_its_amount(amount: f32, expected: f32) {
    let mut movement = moving(MoveDirection::Forward, amount);
    movement.advance(&still(0.0));

    let mut step = 1;
    while step <= 20 && movement.snapshot().travel.state == ScriptedMoveState::Moving {
        movement.advance(&north(f64::from(step) / 30.0, step as f32 * 0.5));
        step += 1;
    }

    assert_eq!(
        movement.snapshot().travel.state,
        ScriptedMoveState::Completed
    );
    close(movement.snapshot().travel.covered, expected, 3);
}

#[test]
fn a_distance_just_past_a_step_ends_at_the_step_before() {
    a_distance_ends_within_half_a_step_of_its_amount(5.2, 5.0);
}

#[test]
fn a_distance_past_half_a_step_ends_at_the_step_after() {
    a_distance_ends_within_half_a_step_of_its_amount(5.3, 5.5);
}

#[test]
fn a_travel_move_counts_only_movement_along_its_facing() {
    let mut movement = moving(MoveDirection::Forward, 0.0);

    movement.advance(&heading(0.0, 90.0));
    movement.advance(&sample(
        0.5,
        At {
            east: 2.0,
            north: 3.0,
            heading: 90.0,
            ..At::default()
        },
    ));

    close(movement.snapshot().travel.covered, 2.0, 3);
}

#[test]
fn a_strafe_counts_only_sideways_movement() {
    let mut movement = moving(MoveDirection::StrafeLeft, 0.0);

    movement.advance(&still(0.0));
    movement.advance(&sample(
        0.5,
        At {
            east: -1.5,
            north: 4.0,
            ..At::default()
        },
    ));

    close(movement.snapshot().strafe.covered, 1.5, 3);
}

#[test]
fn running_in_a_circle_counts_the_path_and_keeps_going() {
    const RADIUS: f32 = 3.0;
    let mut movement = ScriptedMovement::new();
    assert!(movement.begin(MoveRequest::new(MoveDirection::Forward, MovePace::Run, 0.0)));
    assert!(movement.begin(MoveRequest::new(
        MoveDirection::TurnRight,
        MovePace::Run,
        0.0
    )));

    movement.advance(&still(0.0));
    for step in 1..=36 {
        let heading_degrees = step as f32 * 10.0;
        let radians = heading_degrees * std::f32::consts::PI / 180.0;
        movement.advance(&sample(
            f64::from(step) * 0.2,
            At {
                east: RADIUS - RADIUS * radians.cos(),
                north: RADIUS * radians.sin(),
                heading: heading_degrees % 360.0,
                ..At::default()
            },
        ));
    }

    assert_eq!(movement.snapshot().travel.state, ScriptedMoveState::Moving);
    let covered = movement.snapshot().travel.covered;
    assert!((18.0..=19.0).contains(&covered), "{covered}");
    close(movement.snapshot().turn.covered, 360.0, 1);
}

#[test]
fn a_move_that_stops_making_progress_is_blocked() {
    let mut movement = moving(MoveDirection::Forward, 20.0);

    movement.advance(&still(0.0));
    movement.advance(&north(1.6, 0.1));

    assert_eq!(movement.snapshot().travel.state, ScriptedMoveState::Blocked);
}

#[test]
fn a_move_making_progress_keeps_going() {
    let mut movement = moving(MoveDirection::Forward, 20.0);

    movement.advance(&still(0.0));
    for step in 1..=4 {
        movement.advance(&north(f64::from(step) * 1.5, step as f32 * 2.0));
    }

    assert_eq!(movement.snapshot().travel.state, ScriptedMoveState::Moving);
}

#[test]
fn a_move_without_an_amount_ends_at_the_time_limit() {
    let mut movement = moving(MoveDirection::StrafeLeft, 0.0);

    movement.advance(&still(0.0));
    for step in 1..=20 {
        movement.advance(&sample(
            f64::from(step) * 1.5,
            At {
                east: -(step as f32) * 3.0,
                ..At::default()
            },
        ));
    }

    assert_eq!(
        movement.snapshot().strafe.state,
        ScriptedMoveState::TimeLimit
    );
}

#[test]
fn a_move_too_slow_for_its_distance_ends_at_its_time_limit() {
    let mut movement = moving(MoveDirection::Backward, 3.0);

    movement.advance(&still(0.0));
    for step in 1..=4 {
        movement.advance(&north(f64::from(step) * 1.5, -(step as f32) * 0.5));
    }

    assert_eq!(
        movement.snapshot().travel.state,
        ScriptedMoveState::TimeLimit
    );
}

#[test]
fn a_move_for_seconds_completes_when_its_time_is_up() {
    let mut movement = ScriptedMovement::new();
    assert!(movement.begin(MoveRequest::for_seconds(
        MoveDirection::Forward,
        MovePace::Run,
        20.0
    )));

    movement.advance(&still(0.0));
    for step in 1..=39 {
        movement.advance(&north(f64::from(step) * 0.5, step as f32 * 5.0));
    }
    assert_eq!(movement.snapshot().travel.state, ScriptedMoveState::Moving);

    movement.advance(&north(20.0, 200.0));

    assert_eq!(
        movement.snapshot().travel.state,
        ScriptedMoveState::Completed
    );
    close(movement.snapshot().travel.covered, 200.0, 3);
    close(movement.snapshot().travel.elapsed_seconds, 20.0, 3);
}

#[test]
fn a_move_for_seconds_can_outlast_the_limit_for_a_move_without_an_amount() {
    let mut movement = ScriptedMovement::new();
    assert!(movement.begin(MoveRequest::for_seconds(
        MoveDirection::Forward,
        MovePace::Run,
        60.0
    )));

    movement.advance(&still(0.0));
    for step in 1..=90 {
        movement.advance(&north(f64::from(step) * 0.5, step as f32 * 5.0));
    }

    assert_eq!(movement.snapshot().travel.state, ScriptedMoveState::Moving);
}

#[test]
fn moves_on_different_channels_are_held_together() {
    let mut movement = ScriptedMovement::new();
    assert!(movement.begin(MoveRequest::new(MoveDirection::Forward, MovePace::Run, 0.0)));
    assert!(movement.begin(MoveRequest::new(
        MoveDirection::StrafeLeft,
        MovePace::Run,
        0.0
    )));
    assert!(movement.begin(MoveRequest::new(
        MoveDirection::TurnRight,
        MovePace::Run,
        0.0
    )));

    let held = input(movement.advance(&still(0.0)));
    assert!(held.forward && held.strafe_left && held.turn_right && held.run);
    assert!(!held.backward && !held.strafe_right && !held.turn_left);
}

#[test]
fn a_new_move_replaces_only_the_move_on_its_own_channel() {
    let mut movement = ScriptedMovement::new();
    movement.begin(MoveRequest::new(MoveDirection::Forward, MovePace::Run, 0.0));
    movement.begin(MoveRequest::new(
        MoveDirection::TurnLeft,
        MovePace::Run,
        0.0,
    ));

    assert!(movement.begin(MoveRequest::new(
        MoveDirection::Backward,
        MovePace::Walk,
        3.0
    )));

    let snapshot = movement.snapshot();
    assert_eq!(snapshot.travel.sequence, 3);
    assert_eq!(snapshot.travel.request.direction, MoveDirection::Backward);
    assert_eq!(snapshot.turn.sequence, 2);
    assert_eq!(snapshot.turn.state, ScriptedMoveState::Moving);
    assert_eq!(snapshot.strafe.state, ScriptedMoveState::None);
}

#[test]
fn ending_one_move_keeps_the_others_held() {
    let mut movement = ScriptedMovement::new();
    movement.begin(MoveRequest::new(MoveDirection::Forward, MovePace::Run, 0.0));
    movement.begin(MoveRequest::new(
        MoveDirection::TurnRight,
        MovePace::Run,
        30.0,
    ));

    movement.advance(&still(0.0));
    let output = movement.advance(&sample(
        0.1,
        At {
            north: 1.0,
            heading: 35.0,
            ..At::default()
        },
    ));

    assert_eq!(movement.snapshot().turn.state, ScriptedMoveState::Completed);
    let held = input(output);
    assert!(held.forward && !held.turn_right);
    close(output.heading.unwrap(), 30.0, 3);
}

#[test]
fn a_turn_lands_exactly_on_its_angle_across_north() {
    let mut movement = moving(MoveDirection::TurnRight, 90.0);

    assert!(input(movement.advance(&heading(0.0, 300.0))).turn_right);
    let midway = movement.advance(&heading(0.5, 350.0)).heading;
    let landed = movement.advance(&heading(1.0, 36.0)).heading;

    assert!(midway.is_none());
    assert_eq!(movement.snapshot().turn.state, ScriptedMoveState::Completed);
    close(movement.snapshot().turn.covered, 90.0, 3);
    close(landed.unwrap(), 30.0, 3);
}

#[test]
fn a_left_turn_lands_exactly_on_its_angle() {
    let mut movement = moving(MoveDirection::TurnLeft, 5.0);

    movement.advance(&heading(0.0, 100.0));
    movement.advance(&heading(1.0 / 30.0, 95.5));
    let landed = movement.advance(&heading(2.0 / 30.0, 91.0)).heading;

    close(landed.unwrap(), 95.0, 3);
}

#[test]
fn turning_the_wrong_way_is_not_progress() {
    let mut movement = moving(MoveDirection::TurnLeft, 45.0);

    movement.advance(&heading(0.0, 90.0));
    movement.advance(&heading(0.5, 120.0));

    assert_eq!(movement.snapshot().turn.covered, 0.0);
}

#[test]
fn stop_ends_every_move_and_releases_once() {
    let mut movement = ScriptedMovement::new();
    movement.begin(MoveRequest::new(
        MoveDirection::Backward,
        MovePace::Run,
        0.0,
    ));
    movement.begin(MoveRequest::new(
        MoveDirection::TurnLeft,
        MovePace::Run,
        0.0,
    ));
    movement.advance(&still(0.0));

    assert!(movement.stop());

    assert_eq!(movement.snapshot().travel.state, ScriptedMoveState::Stopped);
    assert_eq!(movement.snapshot().turn.state, ScriptedMoveState::Stopped);
    let released = input(movement.advance(&still(0.1)));
    assert!(!released.backward && !released.turn_left && released.is_persistent_command);
    assert!(movement.advance(&still(0.2)).input.is_none());
    assert!(!movement.stop());
}

#[test]
fn stopping_one_channel_keeps_the_others_held() {
    let mut movement = ScriptedMovement::new();
    movement.begin(MoveRequest::new(MoveDirection::Forward, MovePace::Run, 0.0));
    movement.begin(MoveRequest::new(
        MoveDirection::StrafeRight,
        MovePace::Run,
        0.0,
    ));
    movement.advance(&still(0.0));

    assert!(movement.stop_channel(MoveChannel::Strafe));
    assert!(!movement.stop_channel(MoveChannel::Turn));

    assert_eq!(movement.snapshot().strafe.state, ScriptedMoveState::Stopped);
    let held = input(movement.advance(&still(0.1)));
    assert!(held.forward && !held.strafe_right);
}

#[test]
fn the_players_own_movement_interrupts_every_move() {
    let mut movement = ScriptedMovement::new();
    movement.begin(MoveRequest::new(
        MoveDirection::Forward,
        MovePace::Run,
        10.0,
    ));
    movement.begin(MoveRequest::new(
        MoveDirection::TurnLeft,
        MovePace::Run,
        0.0,
    ));

    movement.advance(&still(0.0));
    movement.advance(&sample(
        0.5,
        At {
            manual: true,
            ..At::default()
        },
    ));

    assert_eq!(
        movement.snapshot().travel.state,
        ScriptedMoveState::Interrupted
    );
    assert_eq!(
        movement.snapshot().turn.state,
        ScriptedMoveState::Interrupted
    );
}

#[test]
fn portal_space_loses_every_move_and_the_jump() {
    let mut movement = ScriptedMovement::new();
    movement.begin(MoveRequest::new(
        MoveDirection::Forward,
        MovePace::Run,
        10.0,
    ));
    movement.begin(MoveRequest::new(
        MoveDirection::StrafeLeft,
        MovePace::Run,
        0.0,
    ));
    movement.begin_jump(1.0, None);

    movement.advance(&sample(
        0.0,
        At {
            portal: true,
            ..At::default()
        },
    ));

    assert_eq!(movement.snapshot().travel.state, ScriptedMoveState::Lost);
    assert_eq!(movement.snapshot().strafe.state, ScriptedMoveState::Lost);
    assert!(!movement.snapshot().jump_charging);
}

#[test]
fn a_jump_is_held_for_its_charge_then_released() {
    let mut movement = ScriptedMovement::new();
    assert!(movement.begin_jump(0.5, None));

    let charging = input(movement.advance(&still(10.0)));
    assert!(charging.jump && charging.jump_extent.is_none());
    let still_charging = input(movement.advance(&still(10.4)));
    assert!(still_charging.jump && still_charging.jump_extent.is_none());
    let released = input(movement.advance(&still(10.5)));
    assert!(!released.jump && released.is_persistent_command);
    assert_eq!(released.jump_extent, Some(0.5));
    assert!(!movement.snapshot().jump_charging);
    assert!(movement.advance(&still(10.6)).input.is_none());
}

#[test]
fn a_jump_left_at_a_pace_charges_standing_and_presses_forward_as_it_releases() {
    let mut movement = ScriptedMovement::new();
    assert!(movement.begin_jump(0.5, Some(MovePace::Run)));

    let charging = input(movement.advance(&still(10.0)));
    assert!(charging.jump && !charging.forward);
    let still_charging = input(movement.advance(&still(10.4)));
    assert!(still_charging.jump && !still_charging.forward);
    let released = input(movement.advance(&still(10.5)));
    assert!(!released.jump && released.forward && released.run);
    assert_eq!(released.jump_extent, Some(0.5));
    assert_eq!(movement.snapshot().travel.state, ScriptedMoveState::Moving);
    assert_eq!(movement.snapshot().travel.request.pace, MovePace::Run);
}

#[test]
fn a_jump_during_moves_keeps_them_held() {
    let mut movement = ScriptedMovement::new();
    movement.begin(MoveRequest::new(MoveDirection::Forward, MovePace::Run, 0.0));
    movement.begin(MoveRequest::new(
        MoveDirection::StrafeLeft,
        MovePace::Run,
        0.0,
    ));

    movement.begin_jump(1.0, None);

    let held = input(movement.advance(&still(0.0)));
    assert!(held.forward && held.strafe_left && held.jump);
}

#[test]
fn the_pace_comes_from_the_travel_move_then_the_strafe_then_the_turn() {
    let mut movement = ScriptedMovement::new();

    movement.begin(MoveRequest::new(
        MoveDirection::TurnLeft,
        MovePace::Walk,
        0.0,
    ));
    assert!(!input(movement.advance(&still(0.0))).run);

    movement.begin(MoveRequest::new(
        MoveDirection::StrafeLeft,
        MovePace::Run,
        0.0,
    ));
    assert!(input(movement.advance(&still(0.1))).run);

    movement.begin(MoveRequest::new(
        MoveDirection::Forward,
        MovePace::Walk,
        0.0,
    ));
    assert!(!input(movement.advance(&still(0.2))).run);
}

#[test]
fn a_move_is_possible_only_within_its_limits() {
    let cases = [
        (MoveDirection::Forward, 500.0, false, true),
        (MoveDirection::TurnLeft, 3600.0, false, true),
        (MoveDirection::Backward, 300.0, true, true),
        (MoveDirection::Forward, -1.0, false, false),
        (MoveDirection::Forward, 501.0, false, false),
        (MoveDirection::StrafeLeft, f32::NAN, false, false),
        (MoveDirection::TurnRight, 3601.0, false, false),
        (MoveDirection::Forward, 301.0, true, false),
    ];
    for (direction, amount, seconds, possible) in cases {
        let request = if seconds {
            MoveRequest::for_seconds(direction, MovePace::Run, amount)
        } else {
            MoveRequest::new(direction, MovePace::Run, amount)
        };
        assert_eq!(
            ScriptedMovement::new().begin(request),
            possible,
            "{direction:?} {amount}"
        );
    }
}

#[test]
fn an_impossible_jump_is_refused() {
    assert!(!ScriptedMovement::new().begin_jump(0.0, None));
    assert!(!ScriptedMovement::new().begin_jump(1.5, None));
}

#[test]
fn a_step_too_long_for_one_frame_is_not_progress() {
    let mut movement = moving(MoveDirection::Forward, 0.0);

    movement.advance(&still(0.0));
    movement.advance(&north(0.1, 50.0));

    assert_eq!(movement.snapshot().travel.covered, 0.0);
}
