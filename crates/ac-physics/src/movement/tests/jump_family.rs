//! OpenAC `tests/AcDream.Core.Tests/Physics/MotionInterpreterJumpFamilyTests.cs`.
//! The leash case (`JumpIsAllowed_LeashArmedAndOverstrained`) is in
//! `position_tests`, beside the position manager it arms.

use std::rc::Rc;

use ac_core::physics::{PhysicsState, TransientState};
use ac_motion::motion_command::{
    CROUCH, FALLEN, FALLING, READY, RUN_FORWARD, SIDE_STEP_RIGHT, SITTING, SLEEPING, TURN_RIGHT,
    WALK_FORWARD,
};
use glam::{Quat, Vec3};

use super::support::{
    FakeWeenie, bodiless, body_with, eq_prec, flag, make_airborne, make_grounded, make_interp,
};
use crate::movement::{
    CROUCH_LOWER_BOUND, CROUCH_UPPER_EXCLUSIVE, DEFAULT_JUMP_VZ, JUMP_VZ_EPSILON, MotionInterp,
    PlayerWeenie, WeenieError, WeenieObject,
};

fn weenie(f: FakeWeenie) -> Option<Box<dyn WeenieObject>> {
    Some(Box::new(f))
}

fn cant_jump() -> Option<Box<dyn WeenieObject>> {
    weenie(FakeWeenie {
        can_jump_result: false,
        ..FakeWeenie::default()
    })
}

#[test]
fn charge_allowed_from_ready_without_a_weenie() {
    let interp = make_interp(None, None);
    assert_eq!(interp.jump_charge_is_allowed(0.5), WeenieError::NONE);
}

#[test]
fn charge_refused_when_loaded_down() {
    let interp = make_interp(None, cant_jump());
    assert_eq!(
        interp.jump_charge_is_allowed(0.5),
        WeenieError::CANT_JUMP_LOADED_DOWN
    );
}

#[test]
fn charge_refused_fallen_and_in_postures() {
    for forward in [FALLEN, CROUCH, SITTING, SLEEPING] {
        let mut interp = make_interp(None, None);
        interp.interp.interpreted_state.forward_command = forward;
        assert_eq!(
            interp.jump_charge_is_allowed(0.5),
            WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION,
            "{forward:#x}"
        );
    }
}

#[test]
fn charge_bounds_of_the_posture_range() {
    for forward in [CROUCH_LOWER_BOUND, CROUCH_UPPER_EXCLUSIVE, FALLING] {
        let mut interp = make_interp(None, None);
        interp.interp.interpreted_state.forward_command = forward;
        assert_eq!(interp.jump_charge_is_allowed(0.5), WeenieError::NONE);
    }
}

#[test]
fn can_jump_is_checked_before_the_posture() {
    let mut interp = make_interp(None, cant_jump());
    interp.interp.interpreted_state.forward_command = FALLING;
    assert_eq!(
        interp.jump_charge_is_allowed(0.5),
        WeenieError::CANT_JUMP_LOADED_DOWN
    );
}

#[test]
fn charge_jump_grounded_idle_arms_the_standing_long_jump() {
    let mut interp = make_interp(Some(make_grounded()), None);
    assert_eq!(interp.charge_jump(), WeenieError::NONE);
    assert!(interp.interp.standing_long_jump);
}

#[test]
fn charge_jump_moving_does_not_arm() {
    let cases: [(u32, u32, u32); 3] = [
        (RUN_FORWARD, 0, 0),
        (READY, SIDE_STEP_RIGHT, 0),
        (READY, 0, TURN_RIGHT),
    ];
    for (forward, side, turn) in cases {
        let mut interp = make_interp(Some(make_grounded()), None);
        let s = &mut interp.interp.interpreted_state;
        s.forward_command = forward;
        s.side_step_command = side;
        s.turn_command = turn;
        assert_eq!(interp.charge_jump(), WeenieError::NONE);
        assert!(!interp.interp.standing_long_jump);
    }
}

#[test]
fn charge_jump_airborne_does_not_arm() {
    let mut interp = make_interp(Some(make_airborne()), None);
    assert_eq!(interp.charge_jump(), WeenieError::NONE);
    assert!(!interp.interp.standing_long_jump);
}

#[test]
fn charge_jump_refusals_do_not_arm() {
    let mut interp = make_interp(Some(make_grounded()), cant_jump());
    assert_eq!(interp.charge_jump(), WeenieError::CANT_JUMP_LOADED_DOWN);
    assert!(!interp.interp.standing_long_jump);

    for forward in [FALLEN, SITTING] {
        let mut interp = make_interp(Some(make_grounded()), None);
        interp.interp.interpreted_state.forward_command = forward;
        assert_eq!(
            interp.charge_jump(),
            WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION
        );
        assert!(!interp.interp.standing_long_jump);
    }
}

#[test]
fn contact_allows_move_leaves_the_standing_long_jump_alone() {
    let mut interp = make_interp(Some(make_grounded()), None);
    interp.contact_allows_move(WALK_FORWARD);
    assert!(!interp.interp.standing_long_jump);
    interp.interp.standing_long_jump = true;
    interp.contact_allows_move(WALK_FORWARD);
    assert!(interp.interp.standing_long_jump);
}

#[test]
fn jump_vz_epsilon_is_the_retail_literal() {
    assert_eq!(JUMP_VZ_EPSILON, 0.000_199_999_995);
}

#[test]
fn jump_vz_by_extent() {
    let mut interp = make_interp(None, weenie(FakeWeenie::default()));
    interp.interp.jump_extent = 0.0001;
    eq_prec(0.0, interp.get_jump_vz(), 6);
    interp.interp.jump_extent = 0.0003;
    eq_prec(10.0 * 0.0003, interp.get_jump_vz(), 4);
    interp.interp.jump_extent = 0.0005;
    assert_ne!(interp.get_jump_vz(), 0.0);
    eq_prec(10.0 * 0.0005, interp.get_jump_vz(), 4);
    interp.interp.jump_extent = 5.0;
    eq_prec(10.0, interp.get_jump_vz(), 4);
}

#[test]
fn jump_vz_without_a_weenie_is_the_default() {
    let mut interp = make_interp(None, None);
    interp.interp.jump_extent = 0.5;
    eq_prec(DEFAULT_JUMP_VZ, interp.get_jump_vz(), 4);
}

#[test]
fn jump_vz_is_zero_when_the_weenie_cannot_say() {
    let mut interp = make_interp(
        None,
        weenie(FakeWeenie {
            inq_jump_velocity_result: false,
            ..FakeWeenie::default()
        }),
    );
    interp.interp.jump_extent = 0.5;
    eq_prec(0.0, interp.get_jump_vz(), 5);
}

#[test]
fn leave_ground_velocity_walking_forward_goes_up_and_ahead() {
    let mut interp = make_interp(None, weenie(FakeWeenie::default()));
    interp.interp.jump_extent = 1.0;
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    let v = interp.get_leave_ground_velocity();
    assert!(v.y > 0.0);
    assert!(v.z > 0.0);
}

#[test]
fn leave_ground_velocity_falls_back_to_the_body_velocity() {
    let mut body = make_grounded();
    body.velocity = Vec3::new(2.0, 3.0, 0.0);
    body.orientation = Quat::IDENTITY;
    let mut interp = make_interp(Some(body), None);
    interp.interp.jump_extent = 0.0;
    let v = interp.get_leave_ground_velocity();
    eq_prec(2.0, v.x, 4);
    eq_prec(3.0, v.y, 4);
    eq_prec(0.0, v.z, 4);

    let mut body = make_grounded();
    body.velocity = Vec3::ZERO;
    let mut interp = make_interp(Some(body), None);
    let v = interp.get_leave_ground_velocity();
    assert_eq!((v.x, v.y, v.z), (0.0, 0.0, 0.0));
}

#[test]
fn leave_ground_velocity_x_alone_prevents_the_fallback() {
    let mut body = make_grounded();
    body.velocity = Vec3::new(99.0, 99.0, 99.0);
    let mut interp = make_interp(Some(body), None);
    interp.interp.interpreted_state.side_step_command = SIDE_STEP_RIGHT;
    interp.interp.interpreted_state.side_step_speed = 1.0;
    let v = interp.get_leave_ground_velocity();
    assert_ne!(v.x, 99.0);
    assert!(v.x > 0.0);
}

#[test]
fn leave_ground_velocity_below_epsilon_takes_the_fallback() {
    let mut body = make_grounded();
    body.velocity = Vec3::new(5.0, 0.0, 0.0);
    body.orientation = Quat::IDENTITY;
    let mut interp = make_interp(Some(body), None);
    interp.interp.jump_extent = 0.0001;
    eq_prec(5.0, interp.get_leave_ground_velocity().x, 4);
}

#[test]
fn jump_is_allowed_without_a_body_is_not_grounded() {
    assert_eq!(bodiless().jump_is_allowed(0.5).0, WeenieError::NOT_GROUNDED);
}

#[test]
fn jump_is_allowed_airborne_is_not_grounded() {
    let interp = make_interp(Some(make_airborne()), None);
    assert_eq!(interp.jump_is_allowed(0.5).0, WeenieError::NOT_GROUNDED);
}

#[test]
fn jump_is_allowed_grounded() {
    let interp = make_interp(Some(make_grounded()), None);
    assert_eq!(interp.jump_is_allowed(0.5), (WeenieError::NONE, 0));
}

#[test]
fn jump_is_allowed_non_creature_skips_the_ground_gate() {
    let mut interp = make_interp(Some(make_airborne()), weenie(FakeWeenie::default()));
    interp.weenie_obj = weenie(FakeWeenie {
        is_creature_result: false,
        ..FakeWeenie::default()
    });
    assert_eq!(interp.jump_is_allowed(0.5).0, WeenieError::NONE);
}

#[test]
fn jump_is_allowed_without_gravity_skips_the_ground_gate() {
    let body = body_with(PhysicsState::NONE, TransientState::NONE);
    let interp = make_interp(Some(body), None);
    assert_eq!(interp.jump_is_allowed(0.5).0, WeenieError::NONE);
}

#[test]
fn jump_is_allowed_fully_leashed_is_general_failure() {
    let mut body = make_grounded();
    body.is_fully_constrained = true;
    let mut interp = make_interp(Some(body), None);
    assert_eq!(
        interp.jump_is_allowed(0.5).0,
        WeenieError::GENERAL_MOVEMENT_FAILURE
    );
    interp
        .interp
        .add_to_queue(0, WALK_FORWARD, WeenieError::CANT_JUMP_LOADED_DOWN.code());
    assert_eq!(
        interp.jump_is_allowed(0.5).0,
        WeenieError::GENERAL_MOVEMENT_FAILURE
    );
}

#[test]
fn a_queued_jump_code_short_circuits() {
    let w = Rc::new(FakeWeenie::default());
    let mut interp = make_interp(Some(make_grounded()), Some(Box::new(Rc::clone(&w))));
    interp.interp.add_to_queue(
        0,
        WALK_FORWARD,
        WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION.code(),
    );
    assert_eq!(
        interp.jump_is_allowed(0.5).0,
        WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION
    );
    assert_eq!(w.jump_stamina_cost_calls.get(), 0);
}

#[test]
fn a_clear_queued_code_or_empty_queue_falls_through() {
    let mut interp = make_interp(Some(make_grounded()), None);
    interp.interp.add_to_queue(0, WALK_FORWARD, 0);
    assert_eq!(interp.jump_is_allowed(0.5).0, WeenieError::NONE);
    let interp = make_interp(Some(make_grounded()), None);
    assert_eq!(interp.jump_is_allowed(0.5).0, WeenieError::NONE);
}

#[test]
fn jump_is_allowed_charge_refusals() {
    let mut interp = make_interp(Some(make_grounded()), None);
    interp.interp.interpreted_state.forward_command = FALLEN;
    assert_eq!(
        interp.jump_is_allowed(0.5).0,
        WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION
    );
    let interp = make_interp(Some(make_grounded()), cant_jump());
    assert_eq!(
        interp.jump_is_allowed(0.5).0,
        WeenieError::CANT_JUMP_LOADED_DOWN
    );
}

#[test]
fn jump_is_allowed_motion_refusal() {
    let mut interp = make_interp(Some(make_grounded()), None);
    interp.interp.interpreted_state.forward_command = 0x4000_0020;
    assert_eq!(
        interp.jump_is_allowed(0.5).0,
        WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION
    );
}

#[test]
fn jump_is_allowed_stamina() {
    let w = Rc::new(FakeWeenie {
        jump_stamina_cost_result: false,
        ..FakeWeenie::default()
    });
    let interp = make_interp(Some(make_grounded()), Some(Box::new(Rc::clone(&w))));
    assert_eq!(
        interp.jump_is_allowed(0.5).0,
        WeenieError::GENERAL_MOVEMENT_FAILURE
    );
    assert_eq!(w.jump_stamina_cost_calls.get(), 1);

    let w = Rc::new(FakeWeenie::default());
    let interp = make_interp(Some(make_grounded()), Some(Box::new(Rc::clone(&w))));
    assert_eq!(interp.jump_is_allowed(0.5).0, WeenieError::NONE);
    assert_eq!(w.jump_stamina_cost_calls.get(), 1);
}

#[test]
fn jump_grounded_leaves_walkable() {
    let mut interp = make_interp(Some(make_grounded()), None);
    assert_eq!(interp.jump(0.5), WeenieError::NONE);
    eq_prec(0.5, interp.interp.jump_extent, 5);
    assert!(!interp.physics_obj.as_ref().unwrap().on_walkable());
}

#[test]
fn jump_airborne_is_not_grounded() {
    let mut interp = make_interp(Some(make_airborne()), None);
    assert_eq!(interp.jump(0.5), WeenieError::NOT_GROUNDED);
}

#[test]
fn a_refused_jump_clears_the_standing_long_jump() {
    let mut interp = make_interp(Some(make_grounded()), cant_jump());
    interp.interp.standing_long_jump = true;
    assert_eq!(interp.jump(0.5), WeenieError::CANT_JUMP_LOADED_DOWN);
    assert!(!interp.interp.standing_long_jump);
}

#[test]
fn jump_without_a_body_is_no_physics_object() {
    assert_eq!(bodiless().jump(0.5), WeenieError::NO_PHYSICS_OBJECT);
}

#[test]
fn a_jump_keeps_the_standing_long_jump() {
    let mut interp = make_interp(Some(make_grounded()), None);
    interp.interp.standing_long_jump = true;
    assert_eq!(interp.jump(0.5), WeenieError::NONE);
    assert!(interp.interp.standing_long_jump);
}

#[test]
fn jump_interrupts_even_when_refused() {
    let mut interp = make_interp(Some(make_grounded()), None);
    let (called, f) = flag();
    interp.interrupt_current_movement = Some(f);
    interp.jump(0.5);
    assert!(called.get());

    let mut interp = make_interp(Some(make_airborne()), None);
    let (called, f) = flag();
    interp.interrupt_current_movement = Some(f);
    interp.jump(0.5);
    assert!(called.get());
}

#[test]
fn only_the_player_weenie_is_the_player() {
    assert!(!FakeWeenie::default().is_the_player());
    assert!(PlayerWeenie::default().is_the_player());
}
