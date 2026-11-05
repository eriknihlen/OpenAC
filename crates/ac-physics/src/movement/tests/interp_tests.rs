//! OpenAC `tests/AcDream.Core.Tests/Physics/MotionInterpreterTests.cs` and
//! `RemoteWeenieRunRateTests.cs`. The invalid-type case uses
//! `MovementType::Invalid` (a Rust enum cannot hold OpenAC's 99; both fall
//! to the same refusing arm).

use std::cell::Cell;
use std::rc::Rc;

use ac_core::physics::{PhysicsState, TransientState};
use ac_motion::motion_command::{
    CROUCH, DEAD, FALLEN, FALLING, READY, RUN_FORWARD, SIDE_STEP_RIGHT, SITTING, SLEEPING,
    TURN_LEFT, TURN_RIGHT, WALK_BACKWARDS, WALK_FORWARD,
};
use ac_motion::{MovementType, RUN_ANIM_SPEED, SIDESTEP_ANIM_SPEED, WALK_ANIM_SPEED};
use glam::Vec3;

use super::support::{
    FakeWeenie, bodiless, body_with, eq_prec, make_airborne, make_grounded, make_interp,
};
use crate::body::PhysicsBody;
use crate::movement::{MotionInterp, MovementStruct, RemoteWeenie, WeenieError};

fn weenie(rate: f32) -> Option<Box<dyn crate::movement::WeenieObject>> {
    Some(Box::new(FakeWeenie {
        run_rate: rate,
        ..FakeWeenie::default()
    }))
}

#[test]
fn perform_movement_valid_types_return_none() {
    for kind in [
        MovementType::RawCommand,
        MovementType::InterpretedCommand,
        MovementType::StopRawCommand,
        MovementType::StopInterpretedCommand,
        MovementType::StopCompletely,
    ] {
        let mut interp = make_interp(None, None);
        let mvs = MovementStruct {
            kind,
            motion: WALK_FORWARD,
            speed: 1.0,
            modify_interpreted_state: true,
            modify_raw_state: false,
            ..MovementStruct::default()
        };
        assert_eq!(interp.perform_movement(&mvs), WeenieError::NONE, "{kind:?}");
    }
}

#[test]
fn perform_movement_invalid_type_returns_general_failure() {
    let mut interp = make_interp(None, None);
    let mvs = MovementStruct {
        kind: MovementType::Invalid,
        motion: WALK_FORWARD,
        ..MovementStruct::default()
    };
    assert_eq!(
        interp.perform_movement(&mvs),
        WeenieError::GENERAL_MOVEMENT_FAILURE
    );
}

#[test]
fn perform_movement_stop_completely_resets_to_ready() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.forward_command = WALK_FORWARD;
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.perform_movement(&MovementStruct {
        kind: MovementType::StopCompletely,
        ..MovementStruct::default()
    });
    assert_eq!(interp.interp.raw_state.forward_command, READY);
    assert_eq!(interp.interp.interpreted_state.forward_command, READY);
}

#[test]
fn perform_movement_without_a_body_is_no_physics_object() {
    let mut interp = bodiless();
    let r = interp.perform_movement(&MovementStruct {
        kind: MovementType::RawCommand,
        motion: WALK_FORWARD,
        ..MovementStruct::default()
    });
    assert_eq!(r, WeenieError::NO_PHYSICS_OBJECT);
}

#[test]
fn do_motion_walk_forward_sets_the_raw_forward_command() {
    let mut interp = make_interp(None, None);
    interp.do_motion_at(WALK_FORWARD, 0.8);
    assert_eq!(interp.interp.raw_state.forward_command, WALK_FORWARD);
    eq_prec(0.8, interp.interp.raw_state.forward_speed, 5);
}

#[test]
fn do_motion_without_a_body_is_no_physics_object() {
    let mut interp = bodiless();
    assert_eq!(
        interp.do_motion_at(WALK_FORWARD, 1.0),
        WeenieError::NO_PHYSICS_OBJECT
    );
}

#[test]
fn stop_completely_resets_raw_and_interpreted_to_ready() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.forward_command = WALK_FORWARD;
    interp.interp.raw_state.sidestep_command = SIDE_STEP_RIGHT;
    interp.interp.raw_state.turn_command = TURN_RIGHT;
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.turn_command = TURN_LEFT;
    assert_eq!(interp.stop_completely(), WeenieError::NONE);
    let raw = &interp.interp.raw_state;
    assert_eq!(raw.forward_command, READY);
    eq_prec(1.0, raw.forward_speed, 5);
    assert_eq!((raw.sidestep_command, raw.turn_command), (0, 0));
    let s = &interp.interp.interpreted_state;
    assert_eq!(s.forward_command, READY);
    eq_prec(1.0, s.forward_speed, 5);
    assert_eq!((s.side_step_command, s.turn_command), (0, 0));
}

#[test]
fn stop_completely_zeroes_the_body_velocity() {
    let mut body = make_grounded();
    body.set_velocity(Vec3::new(5.0, 3.0, 0.0));
    let mut interp = make_interp(Some(body), None);
    interp.stop_completely();
    assert_eq!(interp.physics_obj.as_ref().unwrap().velocity, Vec3::ZERO);
}

#[test]
fn stop_completely_without_a_body_is_no_physics_object() {
    assert_eq!(bodiless().stop_completely(), WeenieError::NO_PHYSICS_OBJECT);
}

#[test]
fn state_velocity_idle_is_zero() {
    let mut interp = make_interp(None, None);
    assert_eq!(interp.get_state_velocity(), Vec3::ZERO);
}

#[test]
fn state_velocity_walk_forward_is_walk_speed() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    let v = interp.get_state_velocity();
    eq_prec(0.0, v.x, 5);
    eq_prec(WALK_ANIM_SPEED, v.y, 4);
    eq_prec(0.0, v.z, 5);
}

#[test]
fn state_velocity_run_forward_is_run_speed() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    eq_prec(RUN_ANIM_SPEED, interp.get_state_velocity().y, 4);
}

#[test]
fn state_velocity_sidestep_right_is_positive_x() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.side_step_command = SIDE_STEP_RIGHT;
    interp.interp.interpreted_state.side_step_speed = 1.0;
    let v = interp.get_state_velocity();
    eq_prec(SIDESTEP_ANIM_SPEED, v.x, 4);
    eq_prec(0.0, v.y, 5);
}

#[test]
fn state_velocity_is_capped_at_the_run_speed() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.interp.interpreted_state.forward_speed = 10.0;
    let v = interp.get_state_velocity();
    let max_speed = RUN_ANIM_SPEED * interp.interp.my_run_rate;
    assert!(v.length() <= max_speed + 1e-4);
}

#[test]
fn state_velocity_uses_the_weenie_run_rate() {
    let mut interp = make_interp(None, weenie(2.0));
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    eq_prec(RUN_ANIM_SPEED, interp.get_state_velocity().y, 4);
}

#[test]
fn the_cycle_velocity_overrides_the_run_speed() {
    let mut interp = make_interp(None, weenie(2.0));
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 2.0;
    interp.get_cycle_velocity = Some(Box::new(|| Vec3::new(0.0, 6.0, 0.0)));
    eq_prec(6.0, interp.get_state_velocity().y, 4);
}

#[test]
fn the_cycle_velocity_overrides_the_walk_speed() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    interp.get_cycle_velocity = Some(Box::new(|| Vec3::new(0.0, 2.5, 0.0)));
    eq_prec(2.5, interp.get_state_velocity().y, 4);
}

#[test]
fn a_zero_cycle_velocity_falls_back_to_the_constant() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    interp.get_cycle_velocity = Some(Box::new(|| Vec3::ZERO));
    eq_prec(RUN_ANIM_SPEED, interp.get_state_velocity().y, 4);
}

#[test]
fn the_cap_still_applies_to_the_cycle_velocity() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    interp.get_cycle_velocity = Some(Box::new(|| Vec3::new(0.0, 20.0, 0.0)));
    let v = interp.get_state_velocity();
    assert!(v.length() <= RUN_ANIM_SPEED * interp.interp.my_run_rate + 1e-4);
}

#[test]
fn the_cycle_velocity_only_affects_the_forward_axis() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = READY;
    interp.interp.interpreted_state.side_step_command = SIDE_STEP_RIGHT;
    interp.interp.interpreted_state.side_step_speed = 1.0;
    interp.get_cycle_velocity = Some(Box::new(|| Vec3::new(99.0, 0.0, 0.0)));
    eq_prec(SIDESTEP_ANIM_SPEED, interp.get_state_velocity().x, 4);
}

#[test]
fn the_cycle_velocity_is_not_used_when_idle() {
    let calls = Rc::new(Cell::new(0));
    let c = Rc::clone(&calls);
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = READY;
    interp.get_cycle_velocity = Some(Box::new(move || {
        c.set(c.get() + 1);
        Vec3::new(0.0, 5.0, 0.0)
    }));
    eq_prec(0.0, interp.get_state_velocity().y, 4);
}

#[test]
fn contact_allows_move_grounded() {
    let interp = make_interp(Some(make_grounded()), None);
    assert!(interp.contact_allows_move(WALK_FORWARD));
}

#[test]
fn contact_allows_move_rejects_walking_in_the_air() {
    let interp = make_interp(Some(make_airborne()), None);
    assert!(!interp.contact_allows_move(WALK_FORWARD));
}

#[test]
fn turns_are_always_allowed_even_in_the_air() {
    let interp = make_interp(Some(make_airborne()), None);
    assert!(interp.contact_allows_move(TURN_RIGHT));
    assert!(interp.contact_allows_move(TURN_LEFT));
}

#[test]
fn grounded_postures_still_allow_moves() {
    for posture in [FALLEN, DEAD, CROUCH, SITTING, SLEEPING] {
        let mut interp = make_interp(Some(make_grounded()), None);
        interp.interp.interpreted_state.forward_command = posture;
        assert!(interp.contact_allows_move(WALK_FORWARD), "{posture:#x}");
    }
}

#[test]
fn an_airborne_creature_accepts_falling_and_turns_only() {
    let body = body_with(PhysicsState::GRAVITY, TransientState::NONE);
    let interp = make_interp(Some(body), None);
    assert!(interp.contact_allows_move(FALLING));
    assert!(interp.contact_allows_move(0x4000_0011));
    assert!(interp.contact_allows_move(TURN_RIGHT));
    assert!(interp.contact_allows_move(TURN_LEFT));
    assert!(!interp.contact_allows_move(WALK_FORWARD));
    assert!(!interp.contact_allows_move(0x8000_003D));
}

#[test]
fn without_gravity_everything_is_allowed() {
    let body = body_with(PhysicsState::NONE, TransientState::NONE);
    let interp = make_interp(Some(body), None);
    assert!(interp.contact_allows_move(WALK_FORWARD));
}

#[test]
fn contact_allows_move_never_arms_the_standing_long_jump() {
    let mut interp = make_interp(Some(make_grounded()), None);
    interp.interp.standing_long_jump = false;
    interp.contact_allows_move(WALK_FORWARD);
    assert!(!interp.interp.standing_long_jump);
}

#[test]
fn apply_current_movement_walking_sets_velocity() {
    let mut interp = make_interp(Some(make_grounded()), None);
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    interp.apply_current_movement(false, false);
    assert!(interp.physics_obj.as_ref().unwrap().velocity.length() > 0.0);
}

#[test]
fn apply_current_movement_idle_sets_zero_velocity() {
    let mut interp = make_interp(Some(make_grounded()), None);
    interp.apply_current_movement(false, false);
    assert_eq!(interp.physics_obj.as_ref().unwrap().velocity, Vec3::ZERO);
}

#[test]
fn apply_current_movement_run_updates_my_run_rate() {
    let mut interp = make_interp(Some(make_grounded()), None);
    interp.interp.my_run_rate = 1.0;
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 2.0;
    interp.apply_current_movement(false, false);
    eq_prec(2.0, interp.interp.my_run_rate, 5);
}

#[test]
fn apply_current_movement_run_forward_sets_my_run_rate() {
    let mut interp = make_interp(Some(PhysicsBody::default()), None);
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 2.375;
    interp.apply_current_movement(false, true);
    eq_prec(2.375, interp.interp.my_run_rate, 3);
    eq_prec(4.0 * 2.375, interp.get_state_velocity().y, 2);
}

#[test]
fn max_speed_is_the_run_speed_at_the_run_rate() {
    let mut interp = make_interp(None, weenie(1.5));
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    eq_prec(RUN_ANIM_SPEED * 1.5, interp.get_max_speed(), 4);
}

#[test]
fn max_speed_ignores_the_forward_command() {
    for command in [WALK_FORWARD, WALK_BACKWARDS, READY, RUN_FORWARD] {
        let mut interp = make_interp(None, weenie(1.75));
        interp.interp.interpreted_state.forward_command = command;
        eq_prec(RUN_ANIM_SPEED * 1.75, interp.get_max_speed(), 4);
    }
}

#[test]
fn adjusted_max_speed_not_running_is_the_bare_run_rate() {
    for command in [WALK_FORWARD, WALK_BACKWARDS, READY] {
        let mut interp = make_interp(None, weenie(2.94));
        interp.interp.interpreted_state.forward_command = command;
        eq_prec(2.94, interp.get_adjusted_max_speed(), 4);
    }
}

#[test]
fn adjusted_max_speed_running_is_forward_speed_times_run_speed() {
    let mut interp = make_interp(None, weenie(99.0));
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 2.5;
    eq_prec(2.5 * RUN_ANIM_SPEED, interp.get_adjusted_max_speed(), 4);
}

#[test]
fn adjusted_max_speed_without_a_weenie_is_one() {
    let mut interp = make_interp(None, None);
    interp.interp.my_run_rate = 3.5;
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    eq_prec(1.0, interp.get_adjusted_max_speed(), 4);
}

#[test]
fn adjusted_max_speed_falls_back_to_my_run_rate() {
    let w = FakeWeenie {
        run_rate: 5.0,
        inq_run_rate_result: false,
        ..FakeWeenie::default()
    };
    let mut interp = make_interp(None, Some(Box::new(w)));
    interp.interp.my_run_rate = 2.4;
    interp.interp.interpreted_state.forward_command = READY;
    eq_prec(2.4, interp.get_adjusted_max_speed(), 4);
}

#[test]
fn max_speed_without_a_weenie_is_rate_one() {
    let mut interp = make_interp(None, None);
    interp.interp.my_run_rate = 1.75;
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    eq_prec(RUN_ANIM_SPEED, interp.get_max_speed(), 4);
}

#[test]
fn max_speed_falls_back_to_my_run_rate() {
    let w = FakeWeenie {
        run_rate: 9.9,
        inq_run_rate_result: false,
        ..FakeWeenie::default()
    };
    let mut interp = make_interp(None, Some(Box::new(w)));
    interp.interp.my_run_rate = 1.75;
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    eq_prec(RUN_ANIM_SPEED * 1.75, interp.get_max_speed(), 4);
}

// ── RemoteWeenieRunRateTests ─────────────────────────────────────────────

#[test]
fn run_to_command_scales_a_remote_by_its_wire_run_rate() {
    let mut interp = make_interp(Some(PhysicsBody::default()), Some(Box::new(RemoteWeenie)));
    interp.interp.my_run_rate = 4.5;
    let (mut motion, mut speed) = (WALK_FORWARD, 1.0);
    interp.apply_run_to_command(&mut motion, &mut speed);
    assert_eq!(motion, RUN_FORWARD);
    eq_prec(4.5, speed, 3);
}

#[test]
fn run_to_command_without_a_weenie_keeps_speed_one() {
    let mut interp = make_interp(Some(PhysicsBody::default()), None);
    interp.interp.my_run_rate = 4.5;
    let (mut motion, mut speed) = (WALK_FORWARD, 1.0);
    interp.apply_run_to_command(&mut motion, &mut speed);
    assert_eq!(motion, RUN_FORWARD);
    eq_prec(1.0, speed, 3);
}
