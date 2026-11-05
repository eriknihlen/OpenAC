//! OpenAC `tests/AcDream.Core.Tests/Physics/MotionInterpreterGroundLifecycleTests.cs`.
//! The family's fake weenie answers a flat jump velocity of 10.

use ac_core::physics::PhysicsState;
use ac_motion::motion_command::{READY, RUN_FORWARD, TURN_RIGHT, WALK_BACKWARDS, WALK_FORWARD};
use ac_motion::{BACKWARDS_FACTOR, HoldKey, RUN_ANIM_SPEED, WALK_ANIM_SPEED};
use glam::Vec3;

use super::support::{FakeWeenie, bodiless, eq_prec, flag, make_grounded, make_interp};
use crate::movement::{
    MotionInterp, MotionNode, PlayerWeenie, StandaloneInterpreter, WeenieObject,
};

fn fake(is_the_player: bool, is_creature: bool) -> Option<Box<dyn WeenieObject>> {
    Some(Box::new(FakeWeenie {
        is_the_player_result: is_the_player,
        is_creature_result: is_creature,
        scale_jump_by_extent: false,
        ..FakeWeenie::default()
    }))
}

fn velocity(interp: &StandaloneInterpreter) -> Vec3 {
    interp.physics_obj.as_ref().unwrap().velocity
}

#[test]
fn hit_ground_without_a_body_does_nothing() {
    let mut interp = bodiless();
    let (called, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.hit_ground();
    assert!(!called.get());
}

#[test]
fn hit_ground_skips_non_creatures() {
    let mut interp = make_interp(None, fake(false, false));
    let (called, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.hit_ground();
    assert!(!called.get());
}

#[test]
fn hit_ground_without_a_weenie_proceeds() {
    let mut interp = make_interp(None, None);
    let (called, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.hit_ground();
    assert!(called.get());
}

#[test]
fn hit_ground_without_gravity_does_nothing() {
    let mut body = make_grounded();
    body.state.remove(PhysicsState::GRAVITY);
    let mut interp = make_interp(Some(body), None);
    let (called, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.hit_ground();
    assert!(!called.get());
}

#[test]
fn hit_ground_drops_links_then_reapplies() {
    let mut interp = make_interp(None, fake(false, true));
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    let (called, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.hit_ground();
    assert!(called.get());
    assert!(velocity(&interp).length() > 0.0);
}

#[test]
fn leave_ground_without_a_body_does_nothing() {
    let mut interp = bodiless();
    let (called, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.leave_ground();
    assert!(!called.get());
}

#[test]
fn leave_ground_skips_non_creatures() {
    let mut interp = make_interp(None, fake(false, false));
    interp.interp.standing_long_jump = true;
    interp.interp.jump_extent = 0.7;
    let (called, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.leave_ground();
    assert!(!called.get());
    assert!(interp.interp.standing_long_jump);
    assert_eq!(interp.interp.jump_extent, 0.7);
}

#[test]
fn leave_ground_without_gravity_does_nothing() {
    let mut body = make_grounded();
    body.state.remove(PhysicsState::GRAVITY);
    let mut interp = make_interp(Some(body), None);
    interp.interp.standing_long_jump = true;
    let (called, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.leave_ground();
    assert!(!called.get());
    assert!(interp.interp.standing_long_jump);
}

#[test]
fn leave_ground_resets_the_jump() {
    let mut interp = make_interp(None, None);
    interp.interp.standing_long_jump = true;
    interp.interp.jump_extent = 0.5;
    interp.leave_ground();
    assert!(!interp.interp.standing_long_jump);
    eq_prec(0.0, interp.interp.jump_extent, 5);
}

#[test]
fn leave_ground_drops_links_then_reapplies() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    let (called, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.leave_ground();
    assert!(called.get());
}

#[test]
fn leave_ground_velocity_carries_the_take_off_speed() {
    let mut body = make_grounded();
    body.set_on_walkable(false);
    let mut interp = make_interp(Some(body), Some(Box::new(PlayerWeenie::default())));
    interp.interp.jump_extent = 0.5;
    let expected = interp.get_leave_ground_velocity();
    interp.leave_ground();
    eq_prec(expected.z, velocity(&interp).z, 3);
}

#[test]
fn leave_ground_sets_the_velocity_as_autonomous() {
    let mut body = make_grounded();
    body.last_move_was_autonomous = false;
    let mut interp = make_interp(Some(body), None);
    interp.leave_ground();
    assert!(
        interp
            .physics_obj
            .as_ref()
            .unwrap()
            .last_move_was_autonomous
    );
}

#[test]
fn enter_default_state_appends_ready_behind_queued_nodes() {
    let mut interp = make_interp(None, None);
    interp.interp.add_to_queue(1, RUN_FORWARD, 0);
    interp.interp.add_to_queue(2, WALK_FORWARD, 0x48);
    interp.enter_default_state();
    let nodes: Vec<_> = interp.interp.pending_motions().copied().collect();
    assert_eq!(
        nodes,
        vec![
            MotionNode {
                context_id: 1,
                motion: RUN_FORWARD,
                jump_error_code: 0
            },
            MotionNode {
                context_id: 2,
                motion: WALK_FORWARD,
                jump_error_code: 0x48
            },
            MotionNode {
                context_id: 0,
                motion: READY,
                jump_error_code: 0
            },
        ]
    );
}

#[test]
fn enter_default_state_sets_initted() {
    let mut interp = StandaloneInterpreter {
        physics_obj: Some(make_grounded()),
        ..StandaloneInterpreter::default()
    };
    interp.interp.initted = false;
    interp.enter_default_state();
    assert!(interp.interp.initted);
}

#[test]
fn enter_default_state_resets_both_states() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.forward_command = RUN_FORWARD;
    interp.interp.raw_state.forward_speed = 2.94;
    interp.interp.raw_state.current_hold_key = HoldKey::Run;
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.turn_command = TURN_RIGHT;
    interp.enter_default_state();
    assert_eq!(interp.interp.raw_state.forward_command, READY);
    assert_eq!(interp.interp.raw_state.forward_speed, 1.0);
    assert_eq!(interp.interp.raw_state.current_hold_key, HoldKey::None);
    assert_eq!(interp.interp.interpreted_state.forward_command, READY);
    assert_eq!(interp.interp.interpreted_state.turn_command, 0);
}

#[test]
fn enter_default_state_resets_the_motion_tables() {
    let mut interp = make_interp(None, None);
    let (called, f) = flag();
    interp.initialize_motion_tables = Some(f);
    interp.enter_default_state();
    assert!(called.get());
}

#[test]
fn enter_default_state_ends_with_leave_ground() {
    let mut interp = make_interp(None, None);
    interp.interp.standing_long_jump = true;
    interp.interp.jump_extent = 0.9;
    interp.enter_default_state();
    assert!(!interp.interp.standing_long_jump);
    eq_prec(0.0, interp.interp.jump_extent, 5);
}

#[test]
fn apply_current_movement_needs_initted() {
    let mut interp = make_interp(None, None);
    interp.interp.initted = false;
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.apply_current_movement(false, false);
    assert_eq!(velocity(&interp), Vec3::ZERO);

    let mut interp = make_interp(None, None);
    assert!(interp.interp.initted);
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.apply_current_movement(false, false);
    assert!(velocity(&interp).length() > 0.0);
}

#[test]
fn report_exhaustion_needs_initted_and_a_body() {
    let mut interp = make_interp(None, None);
    interp.interp.initted = false;
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.report_exhaustion();
    assert_eq!(velocity(&interp), Vec3::ZERO);

    bodiless().report_exhaustion();
}

fn dual_dispatch_case(
    weenie: Option<Box<dyn WeenieObject>>,
    autonomous: bool,
    exhaustion: bool,
) -> f32 {
    let mut body = make_grounded();
    body.last_move_was_autonomous = autonomous;
    let mut interp = make_interp(Some(body), weenie);
    interp.interp.raw_state.forward_command = WALK_FORWARD;
    interp.interp.raw_state.forward_speed = 1.0;
    interp.interp.raw_state.forward_hold_key = HoldKey::None;
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    if exhaustion {
        interp.report_exhaustion();
    } else {
        interp.apply_current_movement(false, false);
    }
    velocity(&interp).y
}

#[test]
fn apply_current_movement_dual_dispatch_truth_table() {
    for (is_the_player, autonomous, expect_raw) in [
        (true, true, true),
        (true, false, false),
        (false, true, false),
        (false, false, false),
    ] {
        let y = dual_dispatch_case(fake(is_the_player, true), autonomous, false);
        let expected = if expect_raw {
            WALK_ANIM_SPEED
        } else {
            RUN_ANIM_SPEED
        };
        eq_prec(expected, y, 2);
    }
}

#[test]
fn without_a_weenie_autonomous_dispatches_raw() {
    eq_prec(WALK_ANIM_SPEED, dual_dispatch_case(None, true, false), 2);
}

#[test]
fn report_exhaustion_dual_dispatch() {
    eq_prec(
        WALK_ANIM_SPEED,
        dual_dispatch_case(fake(true, true), true, true),
        2,
    );
    eq_prec(
        RUN_ANIM_SPEED,
        dual_dispatch_case(fake(false, true), true, true),
        2,
    );
}

#[test]
fn set_weenie_object_reapplies_when_bound_and_initted() {
    let mut interp = StandaloneInterpreter {
        physics_obj: Some(make_grounded()),
        ..StandaloneInterpreter::default()
    };
    interp.interp.initted = true;
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    interp.set_weenie_object(fake(false, true));
    assert!(velocity(&interp).length() > 0.0);
}

#[test]
fn set_weenie_object_without_a_body_does_not_reapply() {
    let mut interp = bodiless();
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.set_weenie_object(fake(false, true));
    assert!(interp.physics_obj.is_none());
}

#[test]
fn set_weenie_object_not_initted_does_not_reapply() {
    let mut interp = StandaloneInterpreter {
        physics_obj: Some(make_grounded()),
        ..StandaloneInterpreter::default()
    };
    interp.interp.initted = false;
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    interp.set_weenie_object(fake(false, true));
    assert_eq!(velocity(&interp), Vec3::ZERO);
}

#[test]
fn set_physics_object_binds_and_reapplies() {
    let mut interp = bodiless();
    interp.interp.initted = true;
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    interp.set_physics_object(Some(make_grounded()));
    assert!(interp.physics_obj.is_some());
    assert!(velocity(&interp).length() > 0.0);

    let mut interp = bodiless();
    interp.set_physics_object(None);
    assert!(interp.physics_obj.is_none());
}

#[test]
fn set_hold_run_toggles() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.current_hold_key = HoldKey::None;
    interp.set_hold_run(true, false);
    assert_eq!(interp.interp.raw_state.current_hold_key, HoldKey::Run);

    let mut interp = make_interp(None, None);
    interp.interp.raw_state.current_hold_key = HoldKey::Run;
    interp.set_hold_run(false, false);
    assert_eq!(interp.interp.raw_state.current_hold_key, HoldKey::None);
}

#[test]
fn set_hold_run_without_a_change_does_nothing() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.current_hold_key = HoldKey::Run;
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    interp.physics_obj.as_mut().unwrap().velocity = Vec3::ZERO;
    interp.set_hold_run(true, false);
    assert_eq!(interp.interp.raw_state.current_hold_key, HoldKey::Run);
    assert_eq!(velocity(&interp), Vec3::ZERO);
}

#[test]
fn set_hold_run_change_reapplies() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.current_hold_key = HoldKey::None;
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    interp.set_hold_run(true, true);
    assert!(velocity(&interp).length() > 0.0);
}

#[test]
fn set_hold_key_rules() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.current_hold_key = HoldKey::Run;
    interp.set_hold_key(HoldKey::None, false);
    assert_eq!(interp.interp.raw_state.current_hold_key, HoldKey::None);

    let mut interp = make_interp(None, None);
    interp.interp.raw_state.current_hold_key = HoldKey::Invalid;
    interp.set_hold_key(HoldKey::None, false);
    assert_eq!(interp.interp.raw_state.current_hold_key, HoldKey::Invalid);

    let mut interp = make_interp(None, None);
    interp.interp.raw_state.current_hold_key = HoldKey::None;
    interp.set_hold_key(HoldKey::None, false);
    assert_eq!(interp.interp.raw_state.current_hold_key, HoldKey::None);

    let mut interp = make_interp(None, None);
    interp.interp.raw_state.current_hold_key = HoldKey::None;
    interp.set_hold_key(HoldKey::Run, false);
    assert_eq!(interp.interp.raw_state.current_hold_key, HoldKey::Run);
}

#[test]
fn set_hold_key_run_when_already_run_does_nothing() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.current_hold_key = HoldKey::Run;
    interp.interp.interpreted_state.forward_command = RUN_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    interp.physics_obj.as_mut().unwrap().velocity = Vec3::ZERO;
    interp.set_hold_key(HoldKey::Run, false);
    assert_eq!(interp.interp.raw_state.current_hold_key, HoldKey::Run);
    assert_eq!(velocity(&interp), Vec3::ZERO);
}

#[test]
fn set_hold_key_change_reapplies() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.current_hold_key = HoldKey::None;
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.interp.interpreted_state.forward_speed = 1.0;
    interp.set_hold_key(HoldKey::Run, false);
    assert!(velocity(&interp).length() > 0.0);
}

#[test]
fn adjust_motion_creature_rules() {
    let interp = make_interp(None, fake(false, false));
    let (mut m, mut s) = (WALK_BACKWARDS, 1.0);
    interp.adjust_motion(&mut m, &mut s, HoldKey::Invalid);
    assert_eq!((m, s), (WALK_BACKWARDS, 1.0));

    let interp = make_interp(None, fake(false, true));
    let (mut m, mut s) = (WALK_BACKWARDS, 1.0);
    interp.adjust_motion(&mut m, &mut s, HoldKey::Invalid);
    assert_eq!(m, WALK_FORWARD);
    eq_prec(-BACKWARDS_FACTOR, s, 5);

    let interp = make_interp(None, None);
    let (mut m, mut s) = (WALK_BACKWARDS, 1.0);
    interp.adjust_motion(&mut m, &mut s, HoldKey::Invalid);
    assert_eq!(m, WALK_FORWARD);
}
