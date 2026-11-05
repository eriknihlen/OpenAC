//! Golden cases from OpenAC `MotionTableManagerTests`
//! (`tests/AcDream.Core.Tests/Physics/Motion/MotionTableManagerTests.cs`).

use glam::{Quat, Vec3};

use super::*;
use crate::motion_table::add_motion;
use crate::test_support::{MapLoader, TableBuilder, motion_data, uniform_anim};

const READY: u32 = 0x4100_0003;
const WALK_FORWARD: u32 = 0x4500_0005;
const RUN_FORWARD: u32 = 0x4400_0007;
const THRUST_MED: u32 = 0x1000_0058;
const NON_COMBAT: u32 = 0x8000_003D;
const HAND_COMBAT: u32 = 0x8000_003C;
const PURE_MODIFIER: u32 = 0x2000_0099;

fn loader(anims: &[(u32, usize)]) -> MapLoader {
    let mut loader = MapLoader::default();
    for &(id, frames) in anims {
        loader.add(id, uniform_anim(frames, 1, Vec3::ZERO, Quat::IDENTITY));
    }
    loader
}

fn empty_seq() -> Sequence {
    Sequence::new(MapLoader::default().shared())
}

fn table(builder: TableBuilder) -> Option<Arc<MotionTableRules>> {
    Some(Arc::new(MotionTableRules::new(Arc::new(builder.build()))))
}

fn counts(mgr: &MotionTableManager) -> Vec<u32> {
    mgr.pending_animations().map(|p| p.num_anims).collect()
}

fn queue(motions: &[(u32, u32)]) -> MotionTableManager {
    let mut mgr = MotionTableManager::new(None);
    let mut seq = empty_seq();
    for &(motion, ticks) in motions {
        mgr.add_to_queue(&mut seq, motion, ticks);
    }
    mgr
}

fn set_state(mgr: &mut MotionTableManager, style: u32, substate: u32) {
    let s = mgr.state_mut();
    s.style = style;
    s.substate = substate;
    s.substate_mod = 1.0;
}

#[test]
fn add_to_queue_appends_in_order() {
    let mgr = queue(&[(WALK_FORWARD, 3), (RUN_FORWARD, 5)]);
    let list: Vec<_> = mgr.pending_animations().copied().collect();
    assert_eq!(
        list,
        [
            PendingMotion {
                motion: WALK_FORWARD,
                num_anims: 3
            },
            PendingMotion {
                motion: RUN_FORWARD,
                num_anims: 5
            }
        ]
    );
}

#[test]
fn animation_done_can_complete_several_entries_in_one_call() {
    let mut mgr = queue(&[(WALK_FORWARD, 1), (RUN_FORWARD, 0)]);
    let mut sink = Vec::new();
    mgr.animation_done(true, &mut sink);
    assert_eq!(sink, [(WALK_FORWARD, true), (RUN_FORWARD, true)]);
    assert_eq!(mgr.pending_animations().len(), 0);
    assert_eq!(mgr.animation_counter(), 0);
}

#[test]
fn animation_done_counts_down_a_multi_tick_entry() {
    let mut mgr = queue(&[(WALK_FORWARD, 3)]);
    let mut sink = Vec::new();
    mgr.animation_done(true, &mut sink);
    assert!(sink.is_empty());
    assert_eq!(mgr.animation_counter(), 1);
    mgr.animation_done(true, &mut sink);
    assert_eq!(mgr.animation_counter(), 2);
    mgr.animation_done(true, &mut sink);
    assert_eq!(sink, [(WALK_FORWARD, true)]);
    assert_eq!(mgr.pending_animations().len(), 0);
    assert_eq!(mgr.animation_counter(), 0);
}

#[test]
fn animation_done_keeps_the_leftover_count_until_the_queue_drains() {
    let mut mgr = queue(&[(WALK_FORWARD, 1), (RUN_FORWARD, 5)]);
    let mut sink = Vec::new();
    mgr.animation_done(true, &mut sink);
    assert_eq!(sink, [(WALK_FORWARD, true)]);
    assert_eq!(mgr.pending_animations().next().unwrap().motion, RUN_FORWARD);
    assert_eq!(mgr.animation_counter(), 0);
}

#[test]
fn animation_done_on_an_empty_queue_does_nothing() {
    let mut mgr = queue(&[]);
    let mut sink = Vec::new();
    mgr.animation_done(true, &mut sink);
    assert!(sink.is_empty());
    assert_eq!(mgr.animation_counter(), 0);
}

#[test]
fn check_for_completed_motions_pops_only_zero_tick_heads() {
    let mut mgr = queue(&[(WALK_FORWARD, 0), (RUN_FORWARD, 2)]);
    let mut sink = Vec::new();
    mgr.check_for_completed_motions(&mut sink);
    assert_eq!(sink, [(WALK_FORWARD, true)]);
    assert_eq!(mgr.pending_animations().len(), 1);
    assert_eq!(mgr.animation_counter(), 0);

    let mut mgr = queue(&[(WALK_FORWARD, 4)]);
    let mut sink = Vec::new();
    mgr.check_for_completed_motions(&mut sink);
    assert!(sink.is_empty());
    assert_eq!(mgr.pending_animations().len(), 1);
}

#[test]
fn use_time_checks_for_completed_motions() {
    let mut mgr = queue(&[(WALK_FORWARD, 0)]);
    let mut sink = Vec::new();
    mgr.use_time(&mut sink);
    assert_eq!(sink, [(WALK_FORWARD, true)]);
}

#[test]
fn only_animation_done_counts() {
    let mut a = queue(&[(WALK_FORWARD, 1)]);
    let mut sink = Vec::new();
    a.check_for_completed_motions(&mut sink);
    assert!(sink.is_empty());
    let mut b = queue(&[(WALK_FORWARD, 1)]);
    b.animation_done(true, &mut sink);
    assert_eq!(sink.len(), 1);
}

#[test]
fn completing_an_action_pops_the_action_fifo() {
    let mut mgr = queue(&[]);
    mgr.state_mut().add_action(THRUST_MED, 1.0);
    mgr.add_to_queue(&mut empty_seq(), THRUST_MED, 0);
    let mut sink = Vec::new();
    mgr.animation_done(true, &mut sink);
    assert!(mgr.state().actions().is_empty());
    assert_eq!(sink, [(THRUST_MED, true)]);

    let mut mgr = queue(&[]);
    mgr.state_mut().add_action(THRUST_MED, 1.0);
    mgr.add_to_queue(&mut empty_seq(), THRUST_MED, 0);
    mgr.check_for_completed_motions(&mut sink);
    assert!(mgr.state().actions().is_empty());

    let mut mgr = queue(&[]);
    mgr.state_mut().add_action(THRUST_MED, 1.0);
    mgr.add_to_queue(&mut empty_seq(), WALK_FORWARD, 0);
    mgr.animation_done(true, &mut sink);
    assert_eq!(mgr.state().actions().len(), 1);
}

#[test]
fn exit_world_abandons_every_entry() {
    let mut mgr = queue(&[(WALK_FORWARD, 0), (RUN_FORWARD, 0), (THRUST_MED, 0)]);
    let mut sink = Vec::new();
    mgr.handle_exit_world(&mut sink);
    assert_eq!(sink.len(), 3);
    assert!(sink.iter().all(|&(_, ok)| !ok));
    assert_eq!(mgr.pending_animations().len(), 0);

    let mut mgr = queue(&[(WALK_FORWARD, 3)]);
    let mut sink = Vec::new();
    mgr.handle_exit_world(&mut sink);
    assert_eq!(sink, [(WALK_FORWARD, false)]);
}

#[test]
fn exit_world_completes_a_wrapped_count_instead_of_looping() {
    // A motion with no animations waits for u32::MAX completions; compared
    // signed (-1) it completes at once.
    let mut mgr = queue(&[(READY, u32::MAX)]);
    let mut sink = Vec::new();
    mgr.handle_exit_world(&mut sink);
    assert_eq!(sink, [(READY, false)]);
}

fn link_and_cycle_seq(link: u32, cycle: u32) -> Sequence {
    let mut seq = Sequence::new(loader(&[(link, 2), (cycle, 4)]).shared());
    add_motion(&mut seq, Some(&motion_data(link, 30.0)), 1.0);
    add_motion(&mut seq, Some(&motion_data(cycle, 30.0)), 1.0);
    seq
}

#[test]
fn enter_world_drains_and_strips_link_animations() {
    let mut seq = link_and_cycle_seq(0x0300_0200, 0x0300_0201);
    assert_eq!(seq.count(), 2);
    let mut mgr = MotionTableManager::new(None);
    set_state(&mut mgr, NON_COMBAT, READY);
    mgr.add_to_queue(&mut seq, WALK_FORWARD, 0);
    let mut sink = Vec::new();
    mgr.handle_enter_world(&mut seq, &mut sink);
    assert_eq!(sink, [(WALK_FORWARD, false)]);
    assert_eq!(mgr.pending_animations().len(), 0);
    assert_eq!(seq.count(), 1);

    let mut seq = link_and_cycle_seq(0x0300_0210, 0x0300_0211);
    let mut mgr = MotionTableManager::new(None);
    let mut sink = Vec::new();
    mgr.handle_enter_world(&mut seq, &mut sink);
    assert!(sink.is_empty());
    assert_eq!(seq.count(), 1);
}

#[test]
fn redundant_links_collapse_and_block_as_retail() {
    // (queue, expected counts), each case from the OpenAC test of the same
    // shape.
    type Case = (&'static [(u32, u32)], &'static [u32]);
    let cases: &[Case] = &[
        // Same cycle again, nothing between: the new tail itself is zeroed.
        (&[(WALK_FORWARD, 5), (WALK_FORWARD, 3)], &[5, 0]),
        // A zero-tick node between does not block.
        (
            &[(WALK_FORWARD, 5), (THRUST_MED, 0), (WALK_FORWARD, 3)],
            &[5, 0, 0],
        ),
        // A different cycle between does not block.
        (
            &[(WALK_FORWARD, 5), (RUN_FORWARD, 4), (WALK_FORWARD, 3)],
            &[5, 0, 0],
        ),
        // A non-zero modifier, action or stance between blocks.
        (
            &[(WALK_FORWARD, 5), (PURE_MODIFIER, 2), (WALK_FORWARD, 3)],
            &[5, 2, 3],
        ),
        (
            &[(WALK_FORWARD, 5), (THRUST_MED, 2), (WALK_FORWARD, 3)],
            &[5, 2, 3],
        ),
        (
            &[(WALK_FORWARD, 5), (HAND_COMBAT, 2), (WALK_FORWARD, 3)],
            &[5, 2, 3],
        ),
        // Stance tail.
        (
            &[(HAND_COMBAT, 5), (THRUST_MED, 0), (HAND_COMBAT, 3)],
            &[5, 0, 0],
        ),
        (
            &[(HAND_COMBAT, 5), (PURE_MODIFIER, 2), (HAND_COMBAT, 3)],
            &[5, 2, 3],
        ),
        (
            &[(HAND_COMBAT, 5), (WALK_FORWARD, 4), (HAND_COMBAT, 3)],
            &[5, 4, 3],
        ),
        // No earlier match.
        (&[(RUN_FORWARD, 4), (WALK_FORWARD, 3)], &[4, 3]),
        // Trailing zero-tick nodes are skipped when finding the tail.
        (&[(WALK_FORWARD, 5), (WALK_FORWARD, 0)], &[5, 0]),
        (&[(WALK_FORWARD, 0), (RUN_FORWARD, 0)], &[0, 0]),
        // Modifier tails are never scanned.
        (&[(PURE_MODIFIER, 5), (PURE_MODIFIER, 3)], &[5, 3]),
        // Rapid re-issue collapses the middle.
        (
            &[
                (WALK_FORWARD, 5),
                (RUN_FORWARD, 4),
                (WALK_FORWARD, 3),
                (RUN_FORWARD, 2),
            ],
            &[5, 0, 0, 2],
        ),
        (
            &[(WALK_FORWARD, 5), (WALK_FORWARD, 0), (WALK_FORWARD, 3)],
            &[5, 0, 0],
        ),
        // Walk then run: nothing to collapse.
        (&[(WALK_FORWARD, 3), (RUN_FORWARD, 4)], &[3, 4]),
    ];
    for (motions, expected) in cases {
        assert_eq!(counts(&queue(motions)), *expected, "{motions:x?}");
    }
}

#[test]
fn remove_redundant_links_on_an_empty_or_all_zero_queue_does_nothing() {
    let mut mgr = queue(&[]);
    mgr.remove_redundant_links(&mut empty_seq());
    assert_eq!(mgr.pending_animations().len(), 0);
    let mut mgr = queue(&[(WALK_FORWARD, 0), (RUN_FORWARD, 0)]);
    mgr.remove_redundant_links(&mut empty_seq());
    assert_eq!(counts(&mgr), [0, 0]);
}

#[test]
fn truncation_keeps_the_nodes_and_their_motions() {
    let mgr = queue(&[(WALK_FORWARD, 5), (RUN_FORWARD, 2), (WALK_FORWARD, 3)]);
    let list: Vec<_> = mgr.pending_animations().copied().collect();
    assert_eq!(list.len(), 3);
    assert_eq!(list[1].motion, RUN_FORWARD);
    assert_eq!(list[1].num_anims, 0);
}

#[test]
fn truncation_strips_link_animations_by_the_removed_ticks() {
    let mut seq =
        Sequence::new(loader(&[(0x0300_0300, 1), (0x0300_0301, 1), (0x0300_0302, 1)]).shared());
    for id in [0x0300_0300, 0x0300_0301, 0x0300_0302] {
        add_motion(&mut seq, Some(&motion_data(id, 30.0)), 1.0);
    }
    assert_eq!(seq.count(), 3);
    let mut mgr = MotionTableManager::new(None);
    mgr.add_to_queue(&mut seq, WALK_FORWARD, 2);
    mgr.add_to_queue(&mut seq, WALK_FORWARD, 2);
    assert_eq!(seq.count(), 1);
}

fn ready_table() -> TableBuilder {
    TableBuilder::new(NON_COMBAT).style_default(NON_COMBAT, READY)
}

#[test]
fn initialize_state_queues_ready_with_the_default_state_ticks() {
    let mut mgr = MotionTableManager::new(table(ready_table().cycle(
        NON_COMBAT,
        READY,
        motion_data(0x0300_0400, 30.0),
    )));
    let mut seq = Sequence::new(loader(&[(0x0300_0400, 4)]).shared());
    mgr.initialize_state(&mut seq);
    assert_eq!(
        (mgr.state().style, mgr.state().substate),
        (NON_COMBAT, READY)
    );
    let list: Vec<_> = mgr.pending_animations().copied().collect();
    assert_eq!(
        list,
        [PendingMotion {
            motion: READY_SENTINEL,
            num_anims: 0
        }]
    );

    let mut mgr = MotionTableManager::new(None);
    mgr.initialize_state(&mut empty_seq());
    assert_eq!(counts(&mgr), [0]);
    assert_eq!(
        mgr.pending_animations().next().unwrap().motion,
        READY_SENTINEL
    );
}

#[test]
fn perform_movement_without_a_table_is_error_7() {
    let mut mgr = MotionTableManager::new(None);
    let err = mgr
        .perform_movement(
            &mut empty_seq(),
            MotionTableMovement::interpreted(WALK_FORWARD, 1.0),
        )
        .unwrap_err();
    assert_eq!(err, MotionTableError::NoTable);
    assert_eq!(err.code(), 7);
}

#[test]
fn perform_interpreted_queues_the_motion_with_its_ticks() {
    let mut mgr = MotionTableManager::new(table(
        ready_table()
            .cycle(NON_COMBAT, READY, motion_data(0x0300_0500, 30.0))
            .cycle(NON_COMBAT, WALK_FORWARD, motion_data(0x0300_0501, 30.0))
            .link(
                NON_COMBAT,
                READY,
                WALK_FORWARD,
                motion_data(0x0300_0502, 30.0),
            ),
    ));
    set_state(&mut mgr, NON_COMBAT, READY);
    let mut seq =
        Sequence::new(loader(&[(0x0300_0500, 4), (0x0300_0501, 4), (0x0300_0502, 2)]).shared());
    mgr.perform_movement(
        &mut seq,
        MotionTableMovement::interpreted(WALK_FORWARD, 1.0),
    )
    .unwrap();
    assert_eq!(mgr.state().substate, WALK_FORWARD);
    let list: Vec<_> = mgr.pending_animations().copied().collect();
    assert_eq!(
        list,
        [PendingMotion {
            motion: WALK_FORWARD,
            num_anims: 1
        }]
    );
}

#[test]
fn perform_interpreted_failure_is_0x43_and_queues_nothing() {
    let mut mgr = MotionTableManager::new(table(TableBuilder::new(NON_COMBAT)));
    set_state(&mut mgr, NON_COMBAT, READY);
    let err = mgr
        .perform_movement(
            &mut empty_seq(),
            MotionTableMovement::interpreted(WALK_FORWARD, 1.0),
        )
        .unwrap_err();
    assert_eq!(err.code(), 0x43);
    assert_eq!(mgr.pending_animations().len(), 0);
}

fn run_ready_table() -> Option<Arc<MotionTableRules>> {
    table(
        ready_table()
            .cycle(NON_COMBAT, RUN_FORWARD, motion_data(0x0300_0510, 30.0))
            .cycle(NON_COMBAT, READY, motion_data(0x0300_0511, 30.0))
            .link(
                NON_COMBAT,
                RUN_FORWARD,
                READY,
                motion_data(0x0300_0512, 30.0),
            ),
    )
}

fn running(mgr: &mut MotionTableManager) -> Sequence {
    set_state(mgr, NON_COMBAT, RUN_FORWARD);
    let mut seq =
        Sequence::new(loader(&[(0x0300_0510, 4), (0x0300_0511, 4), (0x0300_0512, 2)]).shared());
    let rules = mgr.table().cloned().unwrap();
    rules
        .do_object_motion(RUN_FORWARD, mgr.state_mut(), &mut seq, 1.0)
        .unwrap();
    seq
}

#[test]
fn perform_stop_interpreted_returns_to_ready_and_queues_the_sentinel() {
    let mut mgr = MotionTableManager::new(run_ready_table());
    let mut seq = running(&mut mgr);
    mgr.perform_movement(
        &mut seq,
        MotionTableMovement::stop_interpreted(RUN_FORWARD, 1.0),
    )
    .unwrap();
    assert_eq!(mgr.state().substate, READY);
    assert_eq!(mgr.pending_animations().len(), 1);
    assert_eq!(
        mgr.pending_animations().next().unwrap().motion,
        READY_SENTINEL
    );
}

#[test]
fn perform_stop_interpreted_failure_is_0x43() {
    let mut mgr = MotionTableManager::new(table(TableBuilder::new(NON_COMBAT)));
    set_state(&mut mgr, NON_COMBAT, READY);
    let err = mgr
        .perform_movement(
            &mut empty_seq(),
            MotionTableMovement::stop_interpreted(PURE_MODIFIER, 1.0),
        )
        .unwrap_err();
    assert_eq!(err, MotionTableError::MotionFailed);
    assert_eq!(mgr.pending_animations().len(), 0);
}

#[test]
fn perform_stop_completely_always_queues_ready() {
    let mut mgr = MotionTableManager::new(table(TableBuilder::new(NON_COMBAT)));
    mgr.perform_movement(&mut empty_seq(), MotionTableMovement::stop_completely())
        .unwrap();
    assert_eq!(
        mgr.pending_animations().next().unwrap().motion,
        READY_SENTINEL
    );

    let mut mgr = MotionTableManager::new(run_ready_table());
    let mut seq = running(&mut mgr);
    mgr.perform_movement(&mut seq, MotionTableMovement::stop_completely())
        .unwrap();
    assert_eq!(mgr.state().substate, READY);
    assert_eq!(mgr.pending_animations().len(), 1);
}

#[test]
fn raw_and_other_requests_are_not_handled() {
    let mut mgr = MotionTableManager::new(table(TableBuilder::new(NON_COMBAT)));
    for kind in [MovementType::RawCommand, MovementType::StopRawCommand] {
        let err = mgr
            .perform_movement(
                &mut empty_seq(),
                MotionTableMovement {
                    kind,
                    motion: WALK_FORWARD,
                    speed: 1.0,
                },
            )
            .unwrap_err();
        assert_eq!(err, MotionTableError::NotHandled);
        assert_eq!(err.code(), u32::MAX);
    }
    assert_eq!(mgr.pending_animations().len(), 0);
}
