//! OpenAC `tests/AcDream.Core.Tests/Physics/Motion/MovementManagerTests.cs`.
//! The move-to manager "exists" once the facade's factory made it (OpenAC's
//! nullable `MoveTo`); out-of-range kinds are `MovementType::Invalid` here.

use std::cell::Cell;
use std::rc::Rc;

use ac_core::physics::PhysicsState;
use ac_motion::motion_command::{TURN_RIGHT, WALK_FORWARD};
use ac_motion::{HoldKey, MovementParameters, MovementType};

use super::move_to_harness::{MoveToHarness, pos};
use super::support::FakeWeenie;
use crate::movement::{MovementOps, MovementStruct, TargetInfo, TargetStatus, WeenieError};

fn move_to_position(x: f32, params: MovementParameters) -> MovementStruct {
    MovementStruct {
        kind: MovementType::MoveToPosition,
        pos: pos(x, 0.0, 0.0),
        params: Some(params),
        ..MovementStruct::default()
    }
}

#[test]
fn make_move_to_manager_uses_the_factory_once() {
    let mut mm = MoveToHarness::facade();
    assert!(!mm.move_to_made);
    assert!(crate::movement::MovementManagerHost::make_move_to_manager(
        &mut mm
    ));
    assert_eq!(mm.factory_calls, 1);
    assert!(crate::movement::MovementManagerHost::make_move_to_manager(
        &mut mm
    ));
    assert_eq!(mm.factory_calls, 1);
}

#[test]
fn make_move_to_manager_without_a_factory_does_nothing() {
    let mut mm = MoveToHarness::facade();
    mm.has_factory = false;
    assert!(!crate::movement::MovementManagerHost::make_move_to_manager(
        &mut mm
    ));
    assert!(!mm.move_to_made);
}

#[test]
fn commands_go_to_the_interpreter() {
    let mut mm = MoveToHarness::facade();
    let r = mm.movement_perform(&MovementStruct {
        kind: MovementType::InterpretedCommand,
        motion: WALK_FORWARD,
        speed: 1.0,
        modify_interpreted_state: true,
        ..MovementStruct::default()
    });
    assert_eq!(r, WeenieError::NONE);
    assert_eq!(mm.forward_command(), WALK_FORWARD);
    assert_eq!(mm.factory_calls, 0);
    assert!(!mm.move_to_made);
}

#[test]
fn commands_keep_their_own_parameters() {
    let mut mm = MoveToHarness::facade();
    let interrupts = Rc::new(Cell::new(0));
    let i = Rc::clone(&interrupts);
    mm.solo.interrupt_current_movement = Some(Box::new(move || i.set(i.get() + 1)));
    let parameters = MovementParameters {
        speed: 0.37,
        set_hold_key: true,
        hold_key_to_apply: HoldKey::Run,
        cancel_move_to: false,
        modify_raw_state: true,
        modify_interpreted_state: true,
        ..MovementParameters::default()
    };
    let r = mm.movement_perform(&MovementStruct {
        kind: MovementType::RawCommand,
        motion: TURN_RIGHT,
        speed: 9.0,
        modify_raw_state: false,
        modify_interpreted_state: false,
        params: Some(parameters),
        ..MovementStruct::default()
    });
    assert_eq!(r, WeenieError::NONE);
    assert_eq!(mm.solo.interp.raw_state.turn_speed, 0.37);
    assert_eq!(mm.solo.interp.raw_state.current_hold_key, HoldKey::Run);
    assert_eq!(interrupts.get(), 0);
    assert_eq!(mm.factory_calls, 0);
}

#[test]
fn move_to_requests_make_the_manager_and_go_to_it() {
    let mut mm = MoveToHarness::facade();
    let r = mm.movement_perform(&move_to_position(20.0, MovementParameters::default()));
    assert_eq!(r, WeenieError::NONE);
    assert_eq!(mm.factory_calls, 1);
    assert_eq!(mm.manager.movement_type_state, MovementType::MoveToPosition);
}

#[test]
fn invalid_kinds_fail_0x47() {
    let mut mm = MoveToHarness::facade();
    assert_eq!(
        mm.movement_perform(&MovementStruct::default()),
        WeenieError::GENERAL_MOVEMENT_FAILURE
    );
    assert_eq!(mm.factory_calls, 0);
}

#[test]
fn every_request_wakes_the_object_first() {
    let mut mm = MoveToHarness::facade();
    assert_eq!(
        mm.movement_perform(&MovementStruct {
            kind: MovementType::StopCompletely,
            ..MovementStruct::default()
        }),
        WeenieError::NONE
    );
    assert_eq!(
        mm.movement_perform(&MovementStruct::default()),
        WeenieError::GENERAL_MOVEMENT_FAILURE
    );
    assert_eq!(mm.activations, 2);
}

#[test]
fn a_move_to_without_a_factory_fails_0x47() {
    let mut mm = MoveToHarness::facade();
    mm.has_factory = false;
    assert_eq!(
        mm.movement_perform(&MovementStruct {
            kind: MovementType::TurnToHeading,
            ..MovementStruct::default()
        }),
        WeenieError::GENERAL_MOVEMENT_FAILURE
    );
}

#[test]
fn use_time_before_the_manager_exists_makes_nothing() {
    let mut mm = MoveToHarness::facade();
    mm.movement_use_time();
    assert_eq!(mm.factory_calls, 0);
    assert!(!mm.move_to_made);
}

#[test]
fn use_time_drives_the_move_to() {
    let mut mm = MoveToHarness::facade();
    mm.contact_value = true;
    mm.world_position = pos(0.0, 0.0, 0.0);
    mm.heading = 90.0;
    mm.movement_perform(&move_to_position(
        20.0,
        MovementParameters {
            distance_to_object: 0.6,
            use_spheres: false,
            ..MovementParameters::default()
        },
    ));
    mm.drain_pending_motions();
    mm.world_position = pos(19.7, 0.0, 0.0);
    mm.advance(2.0);
    mm.movement_use_time();
    assert_eq!(mm.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn hit_ground_reaches_the_interpreter_without_a_move_to() {
    let mut mm = MoveToHarness::facade();
    mm.body().state.insert(PhysicsState::GRAVITY);
    mm.movement_hit_ground();
    assert_eq!(mm.remove_link_calls, 1);
}

#[test]
fn hit_ground_runs_the_interpreter_then_the_move_to() {
    let mut mm = MoveToHarness::facade();
    mm.body().state.insert(PhysicsState::GRAVITY);
    mm.contact_value = true;
    mm.world_position = pos(0.0, 0.0, 0.0);
    mm.heading = 90.0;
    mm.movement_perform(&move_to_position(20.0, MovementParameters::default()));
    mm.drain_pending_motions();
    mm.record_queue_on_remove_link = true;
    mm.movement_hit_ground();
    assert_eq!(mm.queue_empty_at_remove_link, Some(true));
    assert!(mm.solo.interp.motions_pending());
}

#[test]
fn hit_ground_reaches_the_move_to_when_the_interpreter_skips() {
    let mut mm = MoveToHarness::facade();
    mm.solo.weenie_obj = Some(Box::new(FakeWeenie {
        is_creature_result: false,
        can_jump_result: false,
        inq_run_rate_result: false,
        inq_jump_velocity_result: false,
        ..FakeWeenie::default()
    }));
    mm.contact_value = true;
    mm.world_position = pos(0.0, 0.0, 0.0);
    mm.heading = 90.0;
    mm.movement_perform(&move_to_position(20.0, MovementParameters::default()));
    mm.drain_pending_motions();
    mm.movement_hit_ground();
    assert!(mm.solo.interp.motions_pending());
}

#[test]
fn exit_world_drains_the_interpreter_only() {
    let mut mm = MoveToHarness::facade();
    mm.contact_value = true;
    mm.world_position = pos(0.0, 0.0, 0.0);
    mm.heading = 90.0;
    mm.movement_perform(&move_to_position(20.0, MovementParameters::default()));
    assert!(mm.solo.interp.motions_pending());
    mm.movement_handle_exit_world();
    assert!(!mm.solo.interp.motions_pending());
    assert_eq!(mm.manager.movement_type_state, MovementType::MoveToPosition);
}

#[test]
fn cancel_reaches_the_move_to() {
    let mut mm = MoveToHarness::facade();
    mm.movement_cancel_move_to(WeenieError::ACTION_CANCELLED);
    mm.contact_value = true;
    mm.world_position = pos(0.0, 0.0, 0.0);
    mm.movement_perform(&move_to_position(20.0, MovementParameters::default()));
    assert!(mm.movement_is_moving_to());
    mm.movement_cancel_move_to(WeenieError::ACTION_CANCELLED);
    assert_eq!(mm.manager.movement_type_state, MovementType::Invalid);
    assert!(!mm.movement_is_moving_to());
    assert!(mm.stop_completely_calls > 0);
}

#[test]
fn not_moving_before_the_manager_exists() {
    assert!(!MoveToHarness::facade().movement_is_moving_to());
}

#[test]
fn a_target_report_reaches_the_deferred_move() {
    let mut mm = MoveToHarness::facade();
    let target = pos(10.0, 0.0, 0.0);
    let info = TargetInfo::new(0x5000_AAAA, TargetStatus::Ok, target, target);
    mm.movement_handle_update_target(info);
    mm.contact_value = true;
    mm.world_position = pos(0.0, 0.0, 0.0);
    mm.movement_perform(&MovementStruct {
        kind: MovementType::MoveToObject,
        object_id: 0x5000_AAAA,
        top_level_id: 0x5000_AAAA,
        pos: target,
        params: Some(MovementParameters::default()),
        ..MovementStruct::default()
    });
    assert!(!mm.manager.initialized);
    mm.movement_handle_update_target(info);
    assert!(mm.manager.initialized);
}
