//! OpenAC `tests/AcDream.Core.Tests/Physics/MotionInterpreterPendingMotionsTests.cs`,
//! `MotionInterpreterHoldKeySeedTests.cs` and `Motion/MotionNodeTests.cs`.

use std::sync::Arc;

use ac_core::physics::{PhysicsState, TransientState};
use ac_motion::motion_command::{DEAD, READY, TURN_RIGHT};
use ac_motion::{HoldKey, MotionTableManager, MovementParameters, Sequence};

use super::motion_fixtures::NullLoader;
use super::support::{bodiless, body_with, flag, make_interp};
use crate::body::PhysicsBody;
use crate::movement::{
    MotionInterp, MotionInterpreter, MotionNode, StandaloneInterpreter, WeenieError,
};

fn grounded() -> StandaloneInterpreter {
    let body = body_with(
        PhysicsState::GRAVITY,
        TransientState::CONTACT | TransientState::ON_WALKABLE | TransientState::ACTIVE,
    );
    make_interp(Some(body), None)
}

fn node(context_id: u32, motion: u32, jump_error_code: u32) -> MotionNode {
    MotionNode {
        context_id,
        motion,
        jump_error_code,
    }
}

#[test]
fn motion_node_holds_its_fields_and_compares_by_value() {
    let n = node(7, 0x4100_0003, 0x48);
    assert_eq!(
        (n.context_id, n.motion, n.jump_error_code),
        (7, 0x4100_0003, 0x48)
    );
    assert_eq!(node(1, 2, 3), node(1, 2, 3));
    assert_ne!(node(1, 2, 3), node(1, 2, 4));
}

#[test]
fn add_to_queue_appends_in_order() {
    let mut mi = MotionInterpreter::new();
    mi.add_to_queue(1, 0x4100_0003, 0);
    mi.add_to_queue(2, 0x4400_0007, 0x48);
    mi.add_to_queue(3, 0x4500_0005, 0);
    let nodes: Vec<_> = mi.pending_motions().copied().collect();
    assert_eq!(
        nodes,
        vec![
            node(1, 0x4100_0003, 0),
            node(2, 0x4400_0007, 0x48),
            node(3, 0x4500_0005, 0)
        ]
    );
}

#[test]
fn motions_pending_tracks_the_queue() {
    let mut mi = MotionInterpreter::new();
    assert!(!mi.motions_pending());
    mi.add_to_queue(0, READY, 0);
    assert!(mi.motions_pending());
}

#[test]
fn motion_done_pops_the_head_whatever_it_is_told() {
    let mut mi = grounded();
    mi.interp.add_to_queue(1, 0x4400_0007, 0);
    mi.interp.add_to_queue(2, 0x4500_0005, 0);
    mi.motion_done(0xDEAD_BEEF, false);
    assert_eq!(mi.interp.pending_motion_count(), 1);
    assert_eq!(mi.interp.pending_motion_head().unwrap().motion, 0x4500_0005);
}

#[test]
fn motion_done_on_an_empty_queue_does_nothing() {
    let mut mi = bodiless();
    mi.motion_done(0, true);
    assert!(!mi.interp.motions_pending());
}

#[test]
fn motion_done_without_a_body_does_nothing() {
    let mut mi = bodiless();
    mi.interp.add_to_queue(0, READY, 0);
    mi.motion_done(READY, true);
    assert!(mi.interp.motions_pending());
}

#[test]
fn an_action_head_releases_the_sticky_target_and_pops_both_action_queues() {
    let mut mi = grounded();
    mi.interp
        .interpreted_state
        .add_action(0x1000_0062, 1.0, 5, false);
    mi.interp.raw_state.add_action(0x1000_0062, 1.0, 5, false);
    let (fired, f) = flag();
    mi.unstick_from_object = Some(f);
    mi.interp.add_to_queue(0, 0x1000_0062, 0);
    mi.motion_done(0x1000_0062, true);
    assert!(fired.get());
    assert_eq!(mi.interp.interpreted_state.get_num_actions(), 0);
    assert!(mi.interp.raw_state.actions().is_empty());
    assert!(!mi.interp.motions_pending());
}

#[test]
fn a_non_action_head_touches_no_action_queue() {
    let mut mi = grounded();
    mi.interp
        .interpreted_state
        .add_action(0x1000_0062, 1.0, 5, false);
    mi.interp.raw_state.add_action(0x1000_0062, 1.0, 5, false);
    let (fired, f) = flag();
    mi.unstick_from_object = Some(f);
    mi.interp.add_to_queue(0, READY, 0);
    mi.motion_done(READY, true);
    assert!(!fired.get());
    assert_eq!(mi.interp.interpreted_state.get_num_actions(), 1);
    assert_eq!(mi.interp.raw_state.actions().len(), 1);
}

#[test]
fn an_action_head_without_an_unstick_callback_still_pops() {
    let mut mi = grounded();
    mi.interp.add_to_queue(0, 0x1000_0062, 0);
    mi.motion_done(0x1000_0062, true);
    assert!(!mi.interp.motions_pending());
}

#[test]
fn motion_done_pops_in_fifo_order() {
    let mut mi = grounded();
    mi.interp.add_to_queue(1, 0x4100_0003, 0);
    mi.interp.add_to_queue(2, 0x4400_0007, 0);
    mi.interp.add_to_queue(3, 0x4500_0005, 0);
    let ids = |mi: &StandaloneInterpreter| {
        mi.interp
            .pending_motions()
            .map(|n| n.context_id)
            .collect::<Vec<_>>()
    };
    mi.motion_done(0, true);
    assert_eq!(ids(&mi), vec![2, 3]);
    mi.motion_done(0, true);
    assert_eq!(ids(&mi), vec![3]);
    mi.motion_done(0, true);
    assert!(ids(&mi).is_empty());
    assert!(!mi.interp.motions_pending());
}

#[test]
fn handle_exit_world_drains_the_queue() {
    let mut mi = bodiless();
    mi.interp.add_to_queue(1, 0x4100_0003, 0);
    mi.interp.add_to_queue(2, 0x4400_0007, 0);
    mi.interp.add_to_queue(3, 0x1000_0062, 0);
    mi.handle_exit_world();
    assert!(!mi.interp.motions_pending());
}

#[test]
fn handle_exit_world_pops_the_action_queues_per_action() {
    let mut mi = grounded();
    mi.interp
        .interpreted_state
        .add_action(0x1000_0062, 1.0, 1, false);
    mi.interp
        .interpreted_state
        .add_action(0x1000_0063, 1.0, 2, false);
    mi.interp.raw_state.add_action(0x1000_0062, 1.0, 1, false);
    mi.interp.raw_state.add_action(0x1000_0063, 1.0, 2, false);
    mi.interp.add_to_queue(0, 0x1000_0062, 0);
    mi.interp.add_to_queue(0, 0x1000_0063, 0);
    mi.handle_exit_world();
    assert_eq!(mi.interp.interpreted_state.get_num_actions(), 0);
    assert!(mi.interp.raw_state.actions().is_empty());
}

#[test]
fn handle_exit_world_on_an_empty_queue_does_nothing() {
    let mut mi = bodiless();
    mi.handle_exit_world();
    assert!(!mi.interp.motions_pending());
}

#[test]
fn is_standing_still_truth_table() {
    for (contact, on_walkable, fwd, side, turn, expected) in [
        (true, true, READY, 0, 0, true),
        (true, true, READY, 0x6500_000F, 0, false),
        (true, true, READY, 0, 0x6500_000D, false),
        (true, true, 0x4400_0007, 0, 0, false),
        (true, false, READY, 0, 0, false),
        (false, true, READY, 0, 0, false),
    ] {
        let mut transient = TransientState::NONE;
        if contact {
            transient.insert(TransientState::CONTACT);
        }
        if on_walkable {
            transient.insert(TransientState::ON_WALKABLE);
        }
        let mut mi = make_interp(Some(body_with(PhysicsState::GRAVITY, transient)), None);
        mi.interp.interpreted_state.forward_command = fwd;
        mi.interp.interpreted_state.side_step_command = side;
        mi.interp.interpreted_state.turn_command = turn;
        assert_eq!(mi.is_standing_still(), expected);
    }
    assert!(!bodiless().is_standing_still());
}

#[test]
fn motion_allows_jump_block_list() {
    for (motion, blocked) in [
        (0x1000_006F, true),
        (0x1000_0070, true),
        (0x1000_0078, true),
        (0x1000_006E, false),
        (0x1000_0079, false),
        (0x1000_0128, true),
        (0x1000_0130, true),
        (0x1000_0131, true),
        (0x1000_0127, false),
        (0x1000_0132, false),
        (0x4000_0008, true),
        (0x4000_0015, false),
        (0x4000_0016, true),
        (0x4000_0017, true),
        (0x4000_0018, true),
        (0x4000_0019, false),
        (0x4000_001E, true),
        (0x4000_0030, true),
        (0x4000_0039, true),
        (0x4000_001D, false),
        (0x4000_003A, false),
        (0x4100_0012, true),
        (0x4100_0013, true),
        (0x4100_0014, true),
        (0x4100_0011, false),
        (0x4100_0015, false),
        (READY, false),
        (DEAD, false),
        (0x6500_000D, false),
        (0x6500_000F, false),
        (0x4400_0007, false),
    ] {
        let expected = if blocked {
            WeenieError::YOU_CANT_JUMP_FROM_THIS_POSITION
        } else {
            WeenieError::NONE
        };
        assert_eq!(
            MotionInterpreter::motion_allows_jump(motion),
            expected,
            "{motion:#x}"
        );
    }
}

#[test]
fn a_finished_animation_pops_the_interpreter_head_at_once() {
    let mut interp = grounded();
    let mut sequence = Sequence::new(Arc::new(NullLoader));
    let mut manager = MotionTableManager::new(None);
    interp.interp.add_to_queue(7, 0x4400_0007, 0);
    manager.add_to_queue(&mut sequence, 0x4400_0007, 1);
    assert!(interp.interp.motions_pending());
    manager.animation_done(true, &mut interp);
    assert_eq!(manager.pending_animations().count(), 0);
    assert!(!interp.interp.motions_pending());
}

// ── MotionInterpreterHoldKeySeedTests ─────────────────────────────────────

fn autonomous_grounded() -> StandaloneInterpreter {
    let mut body = PhysicsBody::default();
    body.state.insert(PhysicsState::GRAVITY);
    body.transient_state
        .insert(TransientState::CONTACT | TransientState::ON_WALKABLE | TransientState::ACTIVE);
    body.last_move_was_autonomous = true;
    make_interp(Some(body), None)
}

#[test]
fn seeding_the_hold_key_keeps_a_motion_the_keys_never_held() {
    let mut motion = autonomous_grounded();
    motion.do_interpreted_motion(TURN_RIGHT, &MovementParameters::default());
    assert_eq!(motion.interp.interpreted_state.turn_command, TURN_RIGHT);
    assert_eq!(motion.interp.raw_state.turn_command, 0);
    motion.interp.seed_hold_run(true);
    assert_eq!(motion.interp.raw_state.current_hold_key, HoldKey::Run);
    assert_eq!(motion.interp.interpreted_state.turn_command, TURN_RIGHT);
}

#[test]
fn a_real_run_key_edge_rederives_the_state_from_the_keys() {
    let mut motion = autonomous_grounded();
    motion.do_interpreted_motion(TURN_RIGHT, &MovementParameters::default());
    assert_eq!(motion.interp.interpreted_state.turn_command, TURN_RIGHT);
    motion.set_hold_run(true, false);
    assert_eq!(motion.interp.raw_state.current_hold_key, HoldKey::Run);
    assert_eq!(motion.interp.interpreted_state.turn_command, 0);
}
