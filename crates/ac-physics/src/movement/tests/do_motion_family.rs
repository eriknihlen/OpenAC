//! OpenAC `tests/AcDream.Core.Tests/Physics/MotionInterpreterDoMotionFamilyTests.cs`.
//! The invalid-type case uses `MovementType::Invalid` (see `interp_tests`).

use ac_motion::motion_command::{
    CROUCH, DEAD, NON_COMBAT, READY, RUN_FORWARD, SIDE_STEP_RIGHT, SITTING, SLEEPING, TURN_RIGHT,
    WALK_BACKWARDS, WALK_FORWARD,
};
use ac_motion::{HoldKey, MovementParameters, MovementType};
use glam::Vec3;

use super::support::{bodiless, body_with, counter, flag, make_grounded, make_interp};
use crate::movement::{MotionInterp, MovementStruct, WeenieError};
use ac_core::physics::{PhysicsState, TransientState};

fn p() -> MovementParameters {
    MovementParameters::default()
}

#[test]
fn do_motion_without_a_body_is_8() {
    assert_eq!(
        bodiless().do_motion(WALK_FORWARD, &p()),
        WeenieError::NO_PHYSICS_OBJECT
    );
}

#[test]
fn the_cancel_bit_fires_the_interrupt() {
    let mut interp = make_interp(None, None);
    let (fired, f) = flag();
    interp.interrupt_current_movement = Some(f);
    interp.do_motion(
        WALK_FORWARD,
        &MovementParameters {
            cancel_move_to: true,
            ..p()
        },
    );
    assert!(fired.get());
}

#[test]
fn without_the_cancel_bit_no_interrupt() {
    let mut interp = make_interp(None, None);
    let (fired, f) = flag();
    interp.interrupt_current_movement = Some(f);
    interp.do_motion(
        WALK_FORWARD,
        &MovementParameters {
            cancel_move_to: false,
            ..p()
        },
    );
    assert!(!fired.get());
}

#[test]
fn the_set_hold_key_bit_sets_the_hold_key() {
    let mut interp = make_interp(None, None);
    interp.do_motion(
        WALK_FORWARD,
        &MovementParameters {
            set_hold_key: true,
            cancel_move_to: true,
            hold_key_to_apply: HoldKey::Run,
            ..p()
        },
    );
    assert_eq!(interp.interp.raw_state.current_hold_key, HoldKey::Run);
}

#[test]
fn the_hold_key_is_set_before_the_motion_is_adjusted() {
    let mut interp = make_interp(None, None);
    interp.do_motion(
        WALK_FORWARD,
        &MovementParameters {
            set_hold_key: true,
            hold_key_to_apply: HoldKey::Run,
            ..p()
        },
    );
    assert_eq!(interp.interp.interpreted_state.forward_command, RUN_FORWARD);
}

#[test]
fn without_the_set_hold_key_bit_the_hold_key_stays() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.current_hold_key = HoldKey::None;
    interp.do_motion(
        WALK_FORWARD,
        &MovementParameters {
            set_hold_key: false,
            hold_key_to_apply: HoldKey::Run,
            ..p()
        },
    );
    assert_eq!(interp.interp.raw_state.current_hold_key, HoldKey::None);
}

#[test]
fn the_callers_distances_and_heading_are_discarded() {
    let mut a = make_interp(None, None);
    let mut b = make_interp(None, None);
    let weird = MovementParameters {
        distance_to_object: 999.0,
        desired_heading: 12345.0,
        fail_distance: 1.0,
        ..p()
    };
    let r1 = a.do_motion(WALK_FORWARD, &weird);
    let r2 = b.do_motion(WALK_FORWARD, &p());
    assert_eq!(r1, r2);
    assert_eq!(
        a.interp.interpreted_state.forward_command,
        b.interp.interpreted_state.forward_command
    );
    assert_eq!(
        a.interp.interpreted_state.forward_speed,
        b.interp.interpreted_state.forward_speed
    );
}

#[test]
fn postures_are_refused_in_a_combat_stance() {
    for (motion, expected) in [
        (CROUCH, WeenieError::CROUCH_IN_COMBAT_STANCE),
        (SITTING, WeenieError::SIT_IN_COMBAT_STANCE),
        (SLEEPING, WeenieError::SLEEP_IN_COMBAT_STANCE),
    ] {
        let mut interp = make_interp(None, None);
        interp.interp.interpreted_state.current_style = 0x8000_0001;
        assert_eq!(interp.do_motion(motion, &p()), expected);
    }
}

#[test]
fn postures_are_allowed_in_non_combat() {
    for motion in [CROUCH, SITTING, SLEEPING] {
        let mut interp = make_interp(None, None);
        interp.interp.interpreted_state.current_style = NON_COMBAT;
        let r = interp.do_motion(motion, &p());
        assert_ne!(r, WeenieError::CROUCH_IN_COMBAT_STANCE);
        assert_ne!(r, WeenieError::SIT_IN_COMBAT_STANCE);
        assert_ne!(r, WeenieError::SLEEP_IN_COMBAT_STANCE);
    }
}

#[test]
fn chat_emotes_are_refused_in_a_combat_stance() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.current_style = 0x8000_0001;
    assert_eq!(
        interp.do_motion(0x1000_0000 | 0x0200_0000, &p()),
        WeenieError::CHAT_EMOTE_OUTSIDE_NON_COMBAT
    );
}

#[test]
fn chat_emotes_are_allowed_in_non_combat() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.current_style = NON_COMBAT;
    assert_ne!(
        interp.do_motion(0x1000_0000 | 0x0200_0000, &p()),
        WeenieError::CHAT_EMOTE_OUTSIDE_NON_COMBAT
    );
}

#[test]
fn walking_is_not_gated_by_the_stance() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.current_style = 0x8000_0001;
    assert_eq!(interp.do_motion(WALK_FORWARD, &p()), WeenieError::NONE);
}

#[test]
fn the_action_depth_cap_is_six() {
    for (queued, refused) in [(5, false), (6, true)] {
        let mut interp = make_interp(None, None);
        for i in 0..queued {
            interp
                .interp
                .interpreted_state
                .add_action(0x1000_0050, 1.0, i, false);
        }
        let r = interp.do_motion(0x1000_0060, &p());
        assert_eq!(r == WeenieError::ACTION_DEPTH_EXCEEDED, refused, "{queued}");
    }
}

#[test]
fn non_actions_ignore_the_depth_cap() {
    let mut interp = make_interp(None, None);
    for i in 0..10 {
        interp
            .interp
            .interpreted_state
            .add_action(0x1000_0050, 1.0, i, false);
    }
    assert_ne!(
        interp.do_motion(WALK_FORWARD, &p()),
        WeenieError::ACTION_DEPTH_EXCEEDED
    );
}

#[test]
fn the_raw_state_gets_the_original_motion_the_interpreted_the_adjusted() {
    let mut interp = make_interp(None, None);
    interp.do_motion(
        WALK_BACKWARDS,
        &MovementParameters {
            modify_raw_state: true,
            speed: 1.0,
            ..p()
        },
    );
    assert_eq!(interp.interp.raw_state.forward_command, WALK_BACKWARDS);
    assert_eq!(
        interp.interp.interpreted_state.forward_command,
        WALK_FORWARD
    );
}

#[test]
fn without_the_raw_bit_the_raw_state_stays() {
    let mut interp = make_interp(None, None);
    interp.do_motion(
        WALK_FORWARD,
        &MovementParameters {
            modify_raw_state: false,
            ..p()
        },
    );
    assert_eq!(interp.interp.raw_state.forward_command, READY);
}

#[test]
fn a_refused_motion_does_not_reach_the_raw_state() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.current_style = 0x8000_0001;
    let r = interp.do_motion(
        CROUCH,
        &MovementParameters {
            modify_raw_state: true,
            ..p()
        },
    );
    assert_eq!(r, WeenieError::CROUCH_IN_COMBAT_STANCE);
    assert_eq!(interp.interp.raw_state.forward_command, READY);
}

#[test]
fn do_motion_at_a_speed_dispatches() {
    let mut interp = make_interp(None, None);
    assert_eq!(interp.do_motion_at(WALK_FORWARD, 0.8), WeenieError::NONE);
    assert_eq!(
        interp.interp.interpreted_state.forward_command,
        WALK_FORWARD
    );
}

#[test]
fn stop_motion_without_a_body_is_8() {
    assert_eq!(
        bodiless().stop_motion(WALK_FORWARD, &p()),
        WeenieError::NO_PHYSICS_OBJECT
    );
}

#[test]
fn crouch_stops_in_a_combat_stance() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.current_style = 0x8000_0001;
    interp.interp.interpreted_state.forward_command = CROUCH;
    assert_eq!(interp.stop_motion(CROUCH, &p()), WeenieError::NONE);
}

#[test]
fn stop_motion_removes_the_original_motion_from_the_raw_state() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.forward_command = WALK_BACKWARDS;
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.stop_motion(
        WALK_BACKWARDS,
        &MovementParameters {
            modify_raw_state: true,
            ..p()
        },
    );
    assert_eq!(interp.interp.raw_state.forward_command, READY);
}

#[test]
fn stop_motion_without_the_raw_bit_leaves_the_raw_state() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.forward_command = WALK_FORWARD;
    interp.stop_motion(
        WALK_FORWARD,
        &MovementParameters {
            modify_raw_state: false,
            ..p()
        },
    );
    assert_eq!(interp.interp.raw_state.forward_command, WALK_FORWARD);
}

#[test]
fn stop_motion_with_default_options() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    assert_eq!(interp.stop_motion(WALK_FORWARD, &p()), WeenieError::NONE);
    assert_eq!(interp.interp.interpreted_state.forward_command, READY);
}

#[test]
fn stop_completely_zeroes_every_axis_of_both_states() {
    let mut interp = make_interp(None, None);
    let i = &mut interp.interp;
    i.raw_state.forward_command = RUN_FORWARD;
    i.raw_state.sidestep_command = SIDE_STEP_RIGHT;
    i.raw_state.turn_command = TURN_RIGHT;
    i.interpreted_state.forward_command = RUN_FORWARD;
    i.interpreted_state.side_step_command = SIDE_STEP_RIGHT;
    i.interpreted_state.turn_command = TURN_RIGHT;
    interp.stop_completely();
    let i = &interp.interp;
    assert_eq!(i.raw_state.forward_command, READY);
    assert_eq!(i.raw_state.forward_speed, 1.0);
    assert_eq!(
        (i.raw_state.sidestep_command, i.raw_state.turn_command),
        (0, 0)
    );
    assert_eq!(i.interpreted_state.forward_command, READY);
    assert_eq!(i.interpreted_state.forward_speed, 1.0);
    assert_eq!(
        (
            i.interpreted_state.side_step_command,
            i.interpreted_state.turn_command
        ),
        (0, 0)
    );
}

#[test]
fn stop_completely_keeps_sidestep_and_turn_speeds() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.sidestep_speed = 2.5;
    interp.interp.raw_state.turn_speed = 3.5;
    interp.interp.interpreted_state.side_step_speed = 4.5;
    interp.interp.interpreted_state.turn_speed = 5.5;
    interp.stop_completely();
    assert_eq!(interp.interp.raw_state.sidestep_speed, 2.5);
    assert_eq!(interp.interp.raw_state.turn_speed, 3.5);
    assert_eq!(interp.interp.interpreted_state.side_step_speed, 4.5);
    assert_eq!(interp.interp.interpreted_state.turn_speed, 5.5);
}

#[test]
fn stop_completely_zeroes_velocity() {
    let mut body = make_grounded();
    body.set_velocity(Vec3::new(5.0, 3.0, 0.0));
    let mut interp = make_interp(Some(body), None);
    interp.stop_completely();
    assert_eq!(interp.physics_obj.as_ref().unwrap().velocity, Vec3::ZERO);
}

#[test]
fn stop_completely_interrupts() {
    let mut interp = make_interp(None, None);
    let (fired, f) = flag();
    interp.interrupt_current_movement = Some(f);
    interp.stop_completely();
    assert!(fired.get());
}

#[test]
fn stop_completely_queues_ready_with_the_old_forward_jump_code() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = CROUCH;
    interp.stop_completely();
    let nodes: Vec<_> = interp.interp.pending_motions().copied().collect();
    assert_eq!(nodes.len(), 1);
    assert_eq!(nodes[0].context_id, 0);
    assert_eq!(nodes[0].motion, READY);
    assert_eq!(
        nodes[0].jump_error_code,
        WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION.code()
    );

    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = READY;
    interp.stop_completely();
    assert_eq!(
        interp.interp.pending_motion_head().unwrap().jump_error_code,
        0
    );
}

#[test]
fn stop_completely_out_of_the_world_drops_link_animations() {
    let mut interp = make_interp(None, None);
    let (fired, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.stop_completely();
    assert!(fired.get());

    let mut body = make_grounded();
    body.snap_to_cell(0x1234_0001, Vec3::ZERO, Vec3::ZERO);
    let mut interp = make_interp(Some(body), None);
    let (fired, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.stop_completely();
    assert!(!fired.get());
}

#[test]
fn stop_completely_always_succeeds() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = CROUCH;
    assert_eq!(interp.stop_completely(), WeenieError::NONE);
}

#[test]
fn a_charging_standing_jump_only_records_walk_run_and_sidestep() {
    for (motion, check) in [
        (WALK_FORWARD, 0usize),
        (RUN_FORWARD, 0),
        (SIDE_STEP_RIGHT, 1),
    ] {
        let mut interp = make_interp(None, None);
        interp.interp.standing_long_jump = true;
        interp.default_sink = None;
        let before = interp.interp.pending_motion_count();
        assert_eq!(
            interp.do_interpreted_motion(motion, &p()),
            WeenieError::NONE
        );
        if check == 0 {
            assert_eq!(interp.interp.interpreted_state.forward_command, motion);
        } else {
            assert_eq!(interp.interp.interpreted_state.side_step_command, motion);
        }
        assert_eq!(interp.interp.pending_motion_count(), before);
    }
}

#[test]
fn a_charging_standing_jump_dispatches_other_motions() {
    let mut interp = make_interp(None, None);
    interp.interp.standing_long_jump = true;
    assert_eq!(
        interp.do_interpreted_motion(TURN_RIGHT, &p()),
        WeenieError::NONE
    );
    assert_eq!(interp.interp.pending_motion_count(), 1);
}

#[test]
fn not_charging_walk_dispatches_and_queues() {
    let mut interp = make_interp(None, None);
    interp.interp.standing_long_jump = false;
    assert_eq!(
        interp.do_interpreted_motion(WALK_FORWARD, &p()),
        WeenieError::NONE
    );
    assert_eq!(interp.interp.pending_motion_count(), 1);
}

#[test]
fn disable_jump_during_link_forces_0x48() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = READY;
    interp.do_interpreted_motion(
        WALK_FORWARD,
        &MovementParameters {
            disable_jump_during_link: true,
            ..p()
        },
    );
    assert_eq!(
        interp.interp.pending_motion_head().unwrap().jump_error_code,
        WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION.code()
    );

    let mut interp = make_interp(None, None);
    interp.do_interpreted_motion(
        WALK_FORWARD,
        &MovementParameters {
            disable_jump_during_link: false,
            ..p()
        },
    );
    assert_eq!(
        interp.interp.pending_motion_head().unwrap().jump_error_code,
        0
    );
}

#[test]
fn dead_drops_link_animations() {
    let mut interp = make_interp(None, None);
    let (fired, f) = flag();
    interp.remove_link_animations = Some(f);
    interp.do_interpreted_motion(DEAD, &p());
    assert!(fired.get());
}

#[test]
fn the_interpreted_bit_gates_the_state_write() {
    let mut interp = make_interp(None, None);
    interp.do_interpreted_motion(
        WALK_FORWARD,
        &MovementParameters {
            modify_interpreted_state: false,
            ..p()
        },
    );
    assert_eq!(interp.interp.interpreted_state.forward_command, READY);

    let mut interp = make_interp(None, None);
    interp.do_interpreted_motion(
        WALK_FORWARD,
        &MovementParameters {
            modify_interpreted_state: true,
            speed: 1.5,
            ..p()
        },
    );
    assert_eq!(
        interp.interp.interpreted_state.forward_command,
        WALK_FORWARD
    );
}

#[test]
fn an_action_off_the_ground_is_not_grounded() {
    let body = body_with(PhysicsState::GRAVITY, TransientState::NONE);
    let mut interp = make_interp(Some(body), None);
    assert_eq!(
        interp.do_interpreted_motion(0x1000_0060, &p()),
        WeenieError::NOT_GROUNDED
    );
}

#[test]
fn stop_interpreted_motion_without_a_body_is_8() {
    assert_eq!(
        bodiless().stop_interpreted_motion(WALK_FORWARD, &p()),
        WeenieError::NO_PHYSICS_OBJECT
    );
}

#[test]
fn stop_interpreted_motion_queues_ready_with_the_context() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.stop_interpreted_motion(
        WALK_FORWARD,
        &MovementParameters {
            context_id: 42,
            ..p()
        },
    );
    let nodes: Vec<_> = interp.interp.pending_motions().copied().collect();
    assert_eq!(nodes.len(), 1);
    assert_eq!((nodes[0].context_id, nodes[0].motion), (42, READY));
}

#[test]
fn stop_interpreted_motion_without_the_bit_leaves_the_state() {
    let mut interp = make_interp(None, None);
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    interp.stop_interpreted_motion(
        WALK_FORWARD,
        &MovementParameters {
            modify_interpreted_state: false,
            ..p()
        },
    );
    assert_eq!(
        interp.interp.interpreted_state.forward_command,
        WALK_FORWARD
    );
}

#[test]
fn every_dispatched_request_checks_completions_once() {
    for kind in [
        MovementType::RawCommand,
        MovementType::InterpretedCommand,
        MovementType::StopRawCommand,
        MovementType::StopInterpretedCommand,
        MovementType::StopCompletely,
    ] {
        let mut interp = make_interp(None, None);
        let (count, f) = counter();
        interp.check_for_completed_motions = Some(f);
        interp.perform_movement(&MovementStruct {
            kind,
            motion: WALK_FORWARD,
            speed: 1.0,
            ..MovementStruct::default()
        });
        assert_eq!(count.get(), 1, "{kind:?}");
    }
}

#[test]
fn an_invalid_request_does_not_check_completions() {
    let mut interp = make_interp(None, None);
    let (count, f) = counter();
    interp.check_for_completed_motions = Some(f);
    let r = interp.perform_movement(&MovementStruct {
        kind: MovementType::Invalid,
        ..MovementStruct::default()
    });
    assert_eq!(r, WeenieError::GENERAL_MOVEMENT_FAILURE);
    assert_eq!(count.get(), 0);
}

#[test]
fn without_a_completion_callback_stop_completely_succeeds() {
    let mut interp = make_interp(None, None);
    interp.check_for_completed_motions = None;
    assert_eq!(
        interp.perform_movement(&MovementStruct {
            kind: MovementType::StopCompletely,
            ..MovementStruct::default()
        }),
        WeenieError::NONE
    );
}

#[test]
fn stop_completely_request_resets_and_checks_completions() {
    let mut interp = make_interp(None, None);
    interp.interp.raw_state.forward_command = WALK_FORWARD;
    interp.interp.interpreted_state.forward_command = WALK_FORWARD;
    let (count, f) = counter();
    interp.check_for_completed_motions = Some(f);
    interp.perform_movement(&MovementStruct {
        kind: MovementType::StopCompletely,
        ..MovementStruct::default()
    });
    assert_eq!(interp.interp.raw_state.forward_command, READY);
    assert_eq!(interp.interp.interpreted_state.forward_command, READY);
    assert_eq!(count.get(), 1);
}
