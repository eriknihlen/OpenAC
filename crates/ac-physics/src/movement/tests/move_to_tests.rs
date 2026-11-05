//! OpenAC `tests/AcDream.Core.Tests/Physics/Motion/MoveToManager*Tests.cs`
//! (Lifecycle, NodePlan, BeginMoveForward, TurnToHeading, ArrivalAndProgress,
//! AuxTurn, CompletionSeam, HandleUpdateTarget, UseTimeGate,
//! StickyAndCancel, EndToEndTableDrive) and `MovementStructWideningTests`.

use ac_motion::motion_command::{RUN_FORWARD, TURN_LEFT, TURN_RIGHT, WALK_BACKWARDS, WALK_FORWARD};
use ac_motion::move_to_math::get_heading;
use ac_motion::{HoldKey, MovementParameters, MovementType};
use glam::{Quat, Vec3};

use super::move_to_harness::{MoveToHarness, pos};
use super::support::eq_prec;
use crate::body::CellPosition;
use crate::movement::{MoveToOps, MovementStruct, TargetInfo, TargetStatus, WeenieError};

fn p() -> MovementParameters {
    MovementParameters::default()
}

fn info(id: u32, status: TargetStatus, target: CellPosition, interp: CellPosition) -> TargetInfo {
    TargetInfo::new(id, status, target, interp)
}

fn armed_facing(heading: f32) -> MoveToHarness {
    let mut h = MoveToHarness::new();
    h.world_position = pos(0.0, 0.0, 0.0);
    h.heading = heading;
    h
}

// ── Lifecycle ─────────────────────────────────────────────────────────────

#[test]
fn a_fresh_manager_is_idle() {
    let h = MoveToHarness::new();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
    assert!(!h.manager.is_moving_to());
    assert_eq!(h.manager.previous_distance, f32::MAX);
    assert_eq!(h.manager.original_distance, f32::MAX);
    assert!(!h.manager.params.can_walk);
    assert!(!h.manager.params.can_run);
    assert!(!h.manager.params.move_towards);
    assert!(!h.manager.params.cancel_move_to);
    assert_eq!(h.manager.params.context_id, 0);
    let expected = CellPosition::new(0, Vec3::ZERO, Quat::IDENTITY);
    assert_eq!(h.manager.sought_position, expected);
    assert_eq!(h.manager.current_target_position, expected);
    assert_eq!(
        get_heading(h.manager.sought_position.frame.orientation),
        0.0
    );
    assert_eq!(h.pending_count(), 0);
}

#[test]
fn destroy_drains_and_reinitialises() {
    let mut h = MoveToHarness::new();
    h.manager.add_move_to_position_node();
    h.manager.add_turn_to_heading_node(90.0);
    assert_eq!(h.pending_count(), 2);
    h.move_to_destroy();
    assert_eq!(h.pending_count(), 0);
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn is_moving_to_until_cancelled() {
    let mut h = MoveToHarness::new();
    h.move_to_position_request(pos(10.0, 0.0, 0.0), &p());
    assert!(h.manager.is_moving_to());
    h.cancel_move_to(WeenieError::ACTION_CANCELLED);
    assert!(!h.manager.is_moving_to());
}

// ── NodePlan ──────────────────────────────────────────────────────────────

#[test]
fn move_to_position_queues_turn_then_move() {
    let mut h = armed_facing(0.0);
    h.move_to_position_request(pos(10.0, 0.0, 0.0), &p());
    let nodes = h.nodes();
    assert_eq!(nodes.len(), 2);
    assert_eq!(nodes[0].kind, MovementType::TurnToHeading);
    eq_prec(90.0, nodes[0].heading, 2);
    assert_eq!(nodes[1].kind, MovementType::MoveToPosition);
}

#[test]
fn move_to_position_in_range_queues_nothing() {
    let mut h = armed_facing(0.0);
    h.move_to_position_request(pos(0.1, 0.0, 0.0), &p());
    assert_eq!(h.pending_count(), 0);
}

#[test]
fn move_to_position_final_heading() {
    let mut h = armed_facing(0.0);
    let pp = MovementParameters {
        use_final_heading: true,
        desired_heading: 270.0,
        ..p()
    };
    h.move_to_position_request(pos(10.0, 0.0, 0.0), &pp);
    let nodes = h.nodes();
    assert_eq!(nodes.len(), 3);
    assert_eq!(nodes[2].kind, MovementType::TurnToHeading);
    eq_prec(270.0, nodes[2].heading, 2);

    let mut h = armed_facing(0.0);
    let pp = MovementParameters {
        use_final_heading: true,
        desired_heading: 45.0,
        ..p()
    };
    h.move_to_position_request(pos(0.1, 0.0, 0.0), &pp);
    let nodes = h.nodes();
    assert_eq!(nodes.len(), 1);
    assert_eq!(nodes[0].kind, MovementType::TurnToHeading);
    eq_prec(45.0, nodes[0].heading, 2);
}

#[test]
fn position_moves_never_stick() {
    let mut h = MoveToHarness::new();
    h.move_to_position_request(
        pos(0.1, 0.0, 0.0),
        &MovementParameters {
            sticky: true,
            ..p()
        },
    );
    assert!(!h.manager.params.sticky);
}

#[test]
fn move_to_position_sets_type_and_start() {
    let mut h = MoveToHarness::new();
    h.world_position = CellPosition::new(2, Vec3::new(1.0, 2.0, 3.0), Quat::IDENTITY);
    h.move_to_position_request(
        CellPosition::new(2, Vec3::new(1.0, 12.0, 3.0), Quat::IDENTITY),
        &p(),
    );
    assert_eq!(h.manager.movement_type_state, MovementType::MoveToPosition);
    assert_eq!(h.manager.starting_position, h.world_position);
}

#[test]
fn turn_to_heading_queues_one_node() {
    let mut h = armed_facing(0.0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 123.0,
        ..p()
    });
    let nodes = h.nodes();
    assert_eq!(h.manager.movement_type_state, MovementType::TurnToHeading);
    assert_ne!(h.manager.current_command, 0);
    assert_eq!(nodes.len(), 1);
    eq_prec(123.0, nodes[0].heading, 2);
}

#[test]
fn turn_to_heading_clears_sticky() {
    let mut h = MoveToHarness::new();
    h.turn_to_heading(&MovementParameters {
        sticky: true,
        desired_heading: 45.0,
        ..p()
    });
    assert!(!h.manager.params.sticky);
}

#[test]
fn turn_to_heading_already_facing_completes_at_once() {
    let mut h = armed_facing(90.0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 90.0,
        ..p()
    });
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
    assert_eq!(h.pending_count(), 0);
    assert!(h.stop_completely_calls >= 1);
}

#[test]
fn move_to_object_waits_for_the_target() {
    let mut h = MoveToHarness::new();
    h.move_to_object(0x5000_2222, 0x5000_2222, 1.0, 2.0, &p());
    assert_eq!(h.pending_count(), 0);
    assert_eq!(h.manager.movement_type_state, MovementType::MoveToObject);
    assert!(!h.manager.initialized);
    assert_eq!(h.set_target_calls, vec![(0, 0x5000_2222, 0.5, 0.0)]);
}

#[test]
fn move_to_object_keeps_sticky() {
    let mut h = MoveToHarness::new();
    h.move_to_object(
        0x5000_2222,
        0x5000_2222,
        1.0,
        2.0,
        &MovementParameters {
            sticky: true,
            ..p()
        },
    );
    assert!(h.manager.params.sticky);
}

#[test]
fn move_to_object_plans_on_the_first_report() {
    let mut h = armed_facing(0.0);
    h.move_to_object(0x5000_2222, 0x5000_2222, 0.5, 2.0, &p());
    let target = pos(10.0, 0.0, 0.0);
    h.move_to_handle_update_target(info(0x5000_2222, TargetStatus::Ok, target, target));
    assert!(h.manager.initialized);
    let nodes = h.nodes();
    assert_eq!(nodes.len(), 2);
    assert_eq!(nodes[0].kind, MovementType::TurnToHeading);
    eq_prec(90.0, nodes[0].heading, 2);
    assert_eq!(nodes[1].kind, MovementType::MoveToPosition);
}

#[test]
fn move_to_object_final_heading_is_relative_to_the_target() {
    let mut h = armed_facing(0.0);
    let pp = MovementParameters {
        use_final_heading: true,
        desired_heading: 10.0,
        ..p()
    };
    h.move_to_object(0x5000_2222, 0x5000_2222, 0.5, 2.0, &pp);
    let target = pos(10.0, 0.0, 0.0);
    h.move_to_handle_update_target(info(0x5000_2222, TargetStatus::Ok, target, target));
    let nodes = h.nodes();
    assert_eq!(nodes.len(), 3);
    eq_prec(100.0, nodes[2].heading, 2);
}

#[test]
fn moving_or_turning_to_oneself_ends_at_once() {
    let mut h = MoveToHarness::new();
    let me = h.self_id;
    h.move_to_object(me, me, 1.0, 2.0, &p());
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
    assert!(h.set_target_calls.is_empty());

    let mut h = MoveToHarness::new();
    let me = h.self_id;
    h.turn_to_object(me, me, &p());
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
    assert!(h.set_target_calls.is_empty());
}

#[test]
fn turn_to_object_waits_for_the_target() {
    let mut h = MoveToHarness::new();
    h.turn_to_object(0x5000_3333, 0x5000_3333, &p());
    assert_eq!(h.pending_count(), 0);
    assert!(!h.manager.initialized);
    assert_eq!(h.set_target_calls.len(), 1);
}

#[test]
fn turn_to_object_faces_the_object_not_the_requested_heading() {
    let mut h = armed_facing(0.0);
    h.turn_to_object(
        0x5000_3333,
        0x5000_3333,
        &MovementParameters {
            desired_heading: 200.0,
            ..p()
        },
    );
    h.turn_to_object_internal(pos(10.0, 0.0, 0.0));
    let nodes = h.nodes();
    assert_eq!(nodes.len(), 1);
    assert_eq!(nodes[0].kind, MovementType::TurnToHeading);
    eq_prec(90.0, nodes[0].heading, 2);
    assert!(h.manager.initialized);
}

#[test]
fn turn_to_object_already_facing_completes_at_once() {
    let mut h = armed_facing(0.0);
    h.turn_to_object(0x5000_3333, 0x5000_3333, &p());
    h.turn_to_object_internal(pos(0.0, 10.0, 0.0));
    assert_eq!(h.pending_count(), 0);
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn turn_to_object_stops_only_when_asked() {
    let mut h = MoveToHarness::new();
    h.turn_to_object(
        0x5000_3333,
        0x5000_3333,
        &MovementParameters {
            stop_completely: false,
            ..p()
        },
    );
    assert_eq!(h.stop_completely_calls, 0);
    let mut h = MoveToHarness::new();
    h.turn_to_object(
        0x5000_3333,
        0x5000_3333,
        &MovementParameters {
            stop_completely: true,
            ..p()
        },
    );
    assert_eq!(h.stop_completely_calls, 1);
}

// ── BeginMoveForward ──────────────────────────────────────────────────────

#[test]
fn far_away_runs() {
    let mut h = armed_facing(90.0);
    let pp = MovementParameters {
        distance_to_object: 0.6,
        walk_run_threshold: 15.0,
        ..p()
    };
    h.move_to_position_request(pos(100.0, 0.0, 0.0), &pp);
    assert_eq!(h.forward_command(), RUN_FORWARD);
    assert_eq!(h.manager.params.hold_key_to_apply, HoldKey::Run);
    assert_eq!(h.manager.current_command, WALK_FORWARD);
}

#[test]
fn within_the_walk_run_threshold_walks() {
    let mut h = armed_facing(90.0);
    let pp = MovementParameters {
        distance_to_object: 0.6,
        walk_run_threshold: 15.0,
        ..p()
    };
    h.move_to_position_request(pos(10.0, 0.0, 0.0), &pp);
    assert_eq!(h.forward_command(), WALK_FORWARD);
    assert_eq!(h.manager.params.hold_key_to_apply, HoldKey::None);
}

#[test]
fn can_charge_runs_even_close() {
    let mut h = armed_facing(90.0);
    let pp = MovementParameters {
        distance_to_object: 0.6,
        walk_run_threshold: 15.0,
        can_charge: true,
        ..p()
    };
    h.move_to_position_request(pos(2.0, 0.0, 0.0), &pp);
    assert_eq!(h.forward_command(), RUN_FORWARD);
    assert_eq!(h.manager.params.hold_key_to_apply, HoldKey::Run);
}

#[test]
fn the_hold_key_is_written_back_to_the_stored_params() {
    let mut h = armed_facing(90.0);
    let pp = MovementParameters {
        distance_to_object: 0.6,
        walk_run_threshold: 15.0,
        hold_key_to_apply: HoldKey::Invalid,
        ..p()
    };
    h.move_to_position_request(pos(100.0, 0.0, 0.0), &pp);
    assert_eq!(h.manager.params.hold_key_to_apply, HoldKey::Run);
}

#[test]
fn the_progress_clock_is_seeded() {
    let mut h = armed_facing(90.0);
    h.cur_time = 5.0;
    let pp = MovementParameters {
        distance_to_object: 0.6,
        use_spheres: false,
        ..p()
    };
    h.move_to_position_request(pos(20.0, 0.0, 0.0), &pp);
    eq_prec(20.0, h.manager.previous_distance, 2);
    eq_prec(20.0, h.manager.original_distance, 2);
    assert_eq!(h.manager.previous_distance_time, 5.0);
    assert_eq!(h.manager.original_distance_time, 5.0);

    let mut h = armed_facing(90.0);
    h.own_radius = 0.5;
    h.cur_time = 5.0;
    h.move_to_position_request(
        pos(20.0, 0.0, 0.0),
        &MovementParameters {
            distance_to_object: 0.6,
            ..p()
        },
    );
    eq_prec(19.5, h.manager.previous_distance, 2);
    eq_prec(19.5, h.manager.original_distance, 2);
}

#[test]
fn the_local_cancel_bit_does_not_cancel_itself() {
    let mut h = armed_facing(90.0);
    h.move_to_position_request(pos(20.0, 0.0, 0.0), &p());
    assert!(h.manager.is_moving_to());
}

#[test]
fn begin_move_forward_without_a_body_cancels() {
    let mut h = armed_facing(0.0);
    h.move_to_position_request(pos(20.0, 0.0, 0.0), &p());
    assert!(h.manager.is_moving_to());
    h.manager.has_physics_obj = false;
    h.begin_move_forward();
    assert!(!h.manager.is_moving_to());
}

// ── TurnToHeading ─────────────────────────────────────────────────────────

#[test]
fn the_turn_direction_follows_the_shorter_way() {
    for (target, expected) in [
        (90.0, TURN_RIGHT),
        (170.0, TURN_RIGHT),
        (190.0, TURN_LEFT),
        (270.0, TURN_LEFT),
        (180.0, TURN_RIGHT),
    ] {
        let mut h = armed_facing(0.0);
        h.turn_to_heading(&MovementParameters {
            desired_heading: target,
            ..p()
        });
        assert_eq!(h.manager.current_command, expected, "{target}");
    }
}

#[test]
fn already_facing_pops_without_dispatch() {
    let mut h = armed_facing(90.0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 90.0,
        ..p()
    });
    assert_eq!(h.manager.current_command, 0);
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
    assert_eq!(h.pending_count(), 0);

    let mut h = armed_facing(0.0001);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 0.0,
        ..p()
    });
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn a_turn_waits_for_pending_animations() {
    let mut h = armed_facing(0.0);
    h.solo.interp.add_to_queue(0, WALK_FORWARD, 0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 90.0,
        ..p()
    });
    assert_eq!(h.manager.current_command, 0);
    assert_eq!(h.pending_count(), 1);
    assert_eq!(h.manager.movement_type_state, MovementType::TurnToHeading);
}

#[test]
fn begin_turn_on_an_empty_queue_cancels() {
    let mut h = MoveToHarness::new();
    h.begin_turn_to_heading();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn previous_heading_is_seeded_with_the_difference() {
    let mut h = armed_facing(0.0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 90.0,
        ..p()
    });
    eq_prec(90.0, h.manager.previous_heading, 2);
    let mut h = armed_facing(30.0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 90.0,
        ..p()
    });
    eq_prec(60.0, h.manager.previous_heading, 2);
}

#[test]
fn handle_turn_rearms_after_the_queue_drains() {
    let mut h = armed_facing(0.0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 90.0,
        ..p()
    });
    h.drain_pending_motions();
    assert!(matches!(h.manager.current_command, TURN_RIGHT | TURN_LEFT));
}

#[test]
fn passing_the_heading_snaps_to_it() {
    let mut h = armed_facing(0.0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 90.0,
        ..p()
    });
    h.drain_pending_motions();
    assert_eq!(h.manager.current_command, TURN_RIGHT);
    h.heading = 91.0;
    h.handle_turn_to_heading();
    assert!(h.set_heading_calls.contains(&(90.0, true)));
    eq_prec(90.0, h.heading, 2);
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn turning_progress_resets_the_fail_counter() {
    let mut h = armed_facing(0.0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 170.0,
        ..p()
    });
    h.drain_pending_motions();
    assert_eq!(h.manager.current_command, TURN_RIGHT);
    eq_prec(170.0, h.manager.previous_heading, 2);
    h.handle_turn_to_heading();
    assert_eq!(h.manager.fail_progress_count, 1);
    eq_prec(0.0, h.manager.previous_heading, 2);
    h.heading = 90.0;
    h.handle_turn_to_heading();
    assert_eq!(h.manager.fail_progress_count, 0);
    eq_prec(90.0, h.manager.previous_heading, 2);
}

#[test]
fn no_turning_progress_counts_a_failure() {
    let mut h = armed_facing(0.0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 170.0,
        ..p()
    });
    h.drain_pending_motions();
    h.handle_turn_to_heading();
    assert_eq!(h.manager.fail_progress_count, 1);
}

#[test]
fn turning_left_uses_the_mirrored_difference() {
    let mut h = armed_facing(0.0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 190.0,
        ..p()
    });
    h.drain_pending_motions();
    assert_eq!(h.manager.current_command, TURN_LEFT);
    h.heading = 350.0;
    h.handle_turn_to_heading();
    assert!(h.manager.fail_progress_count <= 1);
}

// ── ArrivalAndProgress ────────────────────────────────────────────────────

fn chase(dto: f32, fail_distance: f32) -> MoveToHarness {
    let mut h = armed_facing(90.0);
    h.move_to_position_request(
        pos(20.0, 0.0, 0.0),
        &MovementParameters {
            distance_to_object: dto,
            fail_distance,
            use_spheres: false,
            ..p()
        },
    );
    h
}

#[test]
fn progress_is_not_judged_within_a_second() {
    let mut h = chase(0.6, f32::MAX);
    h.advance(0.5);
    assert!(h.check_progress_made(19.9));
    let mut h = chase(0.6, f32::MAX);
    h.advance(1.0);
    assert!(h.check_progress_made(19.9));
}

#[test]
fn progress_after_the_window() {
    let mut h = chase(0.6, f32::MAX);
    h.advance(2.0);
    assert!(h.check_progress_made(18.0));
    assert_eq!(h.manager.fail_progress_count, 0);
    eq_prec(18.0, h.manager.previous_distance, 2);
}

#[test]
fn too_little_progress_keeps_the_checkpoint() {
    let mut h = chase(0.6, f32::MAX);
    h.advance(2.0);
    assert!(!h.check_progress_made(19.9));
    eq_prec(20.0, h.manager.previous_distance, 2);
}

#[test]
fn good_recent_but_bad_overall_progress_fails() {
    let mut h = chase(0.6, f32::MAX);
    h.advance(2.0);
    assert!(h.check_progress_made(19.0));
    h.advance(200.0);
    assert!(!h.check_progress_made(18.9));
}

#[test]
fn moving_away_measures_the_opening_distance() {
    let mut h = armed_facing(90.0);
    h.move_to_position_request(
        pos(20.0, 0.0, 0.0),
        &MovementParameters {
            move_towards: false,
            move_away: true,
            min_distance: 50.0,
            use_spheres: false,
            ..p()
        },
    );
    assert!(h.manager.moving_away);
    h.advance(2.0);
    assert!(h.check_progress_made(25.0));
}

#[test]
fn a_chase_arrives_within_the_distance() {
    let mut h = chase(5.0, f32::MAX);
    h.drain_pending_motions();
    h.world_position = pos(16.0, 0.0, 0.0);
    h.advance(2.0);
    h.handle_move_to_position();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn a_chase_short_of_the_distance_continues() {
    let mut h = chase(0.6, f32::MAX);
    h.drain_pending_motions();
    h.world_position = pos(5.0, 0.0, 0.0);
    h.advance(2.0);
    h.handle_move_to_position();
    assert_eq!(h.manager.movement_type_state, MovementType::MoveToPosition);
}

#[test]
fn a_flee_arrives_past_the_minimum() {
    let mut h = armed_facing(90.0);
    h.move_to_position_request(
        pos(5.0, 0.0, 0.0),
        &MovementParameters {
            move_towards: false,
            move_away: true,
            min_distance: 10.0,
            use_spheres: false,
            ..p()
        },
    );
    assert!(h.manager.moving_away);
    h.drain_pending_motions();
    h.world_position = pos(-7.0, 0.0, 0.0);
    h.advance(2.0);
    h.handle_move_to_position();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn past_the_fail_distance_gives_up() {
    let mut h = chase(0.6, 5.0);
    h.drain_pending_motions();
    h.world_position = pos(8.0, 0.0, 0.0);
    h.advance(2.0);
    h.handle_move_to_position();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn no_progress_within_the_fail_distance_continues() {
    let mut h = chase(0.6, 100.0);
    h.drain_pending_motions();
    h.world_position = pos(0.0, 1.0, 0.0);
    h.advance(2.0);
    h.handle_move_to_position();
    assert_eq!(h.manager.movement_type_state, MovementType::MoveToPosition);
}

#[test]
fn stalls_count_but_never_give_up() {
    let mut h = chase(0.6, f32::MAX);
    h.drain_pending_motions();
    for _ in 0..20 {
        h.advance(2.0);
        h.handle_move_to_position();
    }
    assert!(h.manager.fail_progress_count >= 20);
    assert_eq!(h.manager.movement_type_state, MovementType::MoveToPosition);
}

#[test]
fn stalls_while_interpolating_do_not_count() {
    let mut h = armed_facing(90.0);
    h.is_interpolating_value = true;
    h.move_to_position_request(
        pos(20.0, 0.0, 0.0),
        &MovementParameters {
            distance_to_object: 0.6,
            fail_distance: f32::MAX,
            use_spheres: false,
            ..p()
        },
    );
    h.drain_pending_motions();
    h.advance(2.0);
    h.handle_move_to_position();
    assert_eq!(h.manager.fail_progress_count, 0);
}

// ── AuxTurn ───────────────────────────────────────────────────────────────

fn arm_moving(initial_heading: f32, target: Vec3) -> MoveToHarness {
    let mut h = armed_facing(initial_heading);
    h.move_to_position_request(
        CellPosition::new(1, target, Quat::IDENTITY),
        &MovementParameters {
            distance_to_object: 0.6,
            use_spheres: false,
            ..p()
        },
    );
    h.drain_pending_motions();
    h
}

#[test]
fn steering_dead_band() {
    let mut h = arm_moving(90.0, Vec3::new(20.0, 0.0, 0.0));
    h.heading = 85.0;
    h.handle_move_to_position();
    assert_eq!(h.manager.aux_command, 0);

    let mut h = arm_moving(90.0, Vec3::new(20.0, 0.0, 0.0));
    h.heading = 40.0;
    h.handle_move_to_position();
    assert_eq!(h.manager.aux_command, TURN_RIGHT);

    let mut h = arm_moving(90.0, Vec3::new(20.0, 0.0, 0.0));
    h.heading = 260.0;
    h.handle_move_to_position();
    assert_eq!(h.manager.aux_command, TURN_LEFT);

    let mut h = arm_moving(0.0, Vec3::new(0.0, 20.0, 0.0));
    h.heading = 20.0;
    h.handle_move_to_position();
    assert_eq!(h.manager.aux_command, 0);

    let mut h = arm_moving(0.0, Vec3::new(0.0, 20.0, 0.0));
    h.heading = 340.0;
    h.handle_move_to_position();
    assert_eq!(h.manager.aux_command, 0);
}

#[test]
fn the_same_steering_turn_is_not_reissued() {
    let mut h = arm_moving(90.0, Vec3::new(20.0, 0.0, 0.0));
    h.heading = 40.0;
    h.handle_move_to_position();
    assert_eq!(h.manager.aux_command, TURN_RIGHT);
    h.drain_pending_motions();
    let stops = h.stop_completely_calls;
    h.handle_move_to_position();
    assert_eq!(h.manager.aux_command, TURN_RIGHT);
    assert_eq!(h.stop_completely_calls, stops);
}

#[test]
fn pending_animations_stop_the_steering_turn() {
    let mut h = arm_moving(90.0, Vec3::new(20.0, 0.0, 0.0));
    h.heading = 40.0;
    h.handle_move_to_position();
    assert_eq!(h.manager.aux_command, TURN_RIGHT);
    h.solo.interp.add_to_queue(0, WALK_FORWARD, 0);
    h.handle_move_to_position();
    assert_eq!(h.manager.aux_command, 0);
}

// ── CompletionSeam ────────────────────────────────────────────────────────

fn drive_to_arrival() -> MoveToHarness {
    let mut h = armed_facing(90.0);
    h.move_to_position_request(
        pos(5.0, 0.0, 0.0),
        &MovementParameters {
            use_spheres: false,
            distance_to_object: 0.6,
            ..p()
        },
    );
    h.drain_pending_motions();
    let mut i = 0;
    while i < 200 && h.manager.is_moving_to() {
        h.tick();
        let cur = h.world_position.frame.origin;
        h.world_position = CellPosition::new(1, cur + Vec3::new(0.2, 0.0, 0.0), Quat::IDENTITY);
        h.move_to_use_time();
        h.drain_pending_motions();
        i += 1;
    }
    assert!(!h.manager.is_moving_to());
    h
}

#[test]
fn arrival_reports_completion_once() {
    let mut h = drive_to_arrival();
    assert_eq!(h.move_to_complete_calls, vec![WeenieError::NONE]);
    for _ in 0..10 {
        h.tick();
        h.move_to_use_time();
    }
    assert_eq!(h.move_to_complete_calls.len(), 1);
}

#[test]
fn a_cancel_is_not_a_completion() {
    let mut h = armed_facing(90.0);
    h.move_to_position_request(
        pos(20.0, 0.0, 0.0),
        &MovementParameters {
            use_spheres: false,
            ..p()
        },
    );
    assert!(h.manager.is_moving_to());
    h.cancel_move_to(WeenieError::ACTION_CANCELLED);
    assert!(!h.manager.is_moving_to());
    assert!(h.move_to_complete_calls.is_empty());
}

#[test]
fn the_cancellation_report_fires_only_for_an_active_move() {
    let mut h = MoveToHarness::new();
    h.move_to_cancelled_calls = Some(Vec::new());
    h.cancel_move_to(WeenieError::ACTION_CANCELLED);
    assert!(h.move_to_cancelled_calls.as_ref().unwrap().is_empty());
    h.world_position = pos(0.0, 0.0, 0.0);
    h.move_to_position_request(
        pos(20.0, 0.0, 0.0),
        &MovementParameters {
            use_spheres: false,
            ..p()
        },
    );
    h.cancel_move_to(WeenieError::OBJECT_GONE);
    h.cancel_move_to(WeenieError::ACTION_CANCELLED);
    assert_eq!(
        h.move_to_cancelled_calls.as_deref(),
        Some(&[WeenieError::OBJECT_GONE][..])
    );
    assert!(h.move_to_complete_calls.is_empty());
}

#[test]
fn sticky_completion_hands_off_then_completes() {
    let mut h = armed_facing(90.0);
    h.move_to_object(
        0x1000,
        0x1000,
        0.5,
        1.0,
        &MovementParameters {
            sticky: true,
            distance_to_object: 5.0,
            ..p()
        },
    );
    h.drain_pending_motions();
    let target = pos(1.0, 0.0, 0.0);
    h.move_to_handle_update_target(info(0x1000, TargetStatus::Ok, target, target));
    h.drain_pending_motions();
    let mut i = 0;
    while i < 50 && h.manager.is_moving_to() {
        h.tick();
        h.move_to_use_time();
        h.drain_pending_motions();
        i += 1;
    }
    assert!(!h.manager.is_moving_to());
    assert_eq!(h.stick_to_calls, vec![(0x1000, 0.5, 1.0)]);
    assert_eq!(h.move_to_complete_calls, vec![WeenieError::NONE]);
}

// ── HandleUpdateTarget ────────────────────────────────────────────────────

const TARGET_ID: u32 = 0x5000_4444;

fn arm_move_to_object() -> MoveToHarness {
    let mut h = armed_facing(0.0);
    h.own_radius = 0.5;
    h.own_height = 2.0;
    h.move_to_object(TARGET_ID, TARGET_ID, 1.0, 2.0, &p());
    h
}

#[test]
fn a_report_for_another_target_is_ignored() {
    let mut h = arm_move_to_object();
    let wrong = pos(5.0, 5.0, 0.0);
    h.move_to_handle_update_target(info(0x5999_9999, TargetStatus::Ok, wrong, wrong));
    assert!(!h.manager.initialized);
    assert_eq!(h.pending_count(), 0);
}

#[test]
fn a_bad_first_report_cancels() {
    let mut h = arm_move_to_object();
    let p0 = pos(0.0, 0.0, 0.0);
    h.move_to_handle_update_target(info(TARGET_ID, TargetStatus::ExitWorld, p0, p0));
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn a_good_first_report_plans_without_completing() {
    let mut h = arm_move_to_object();
    let target = pos(10.0, 0.0, 0.0);
    h.move_to_handle_update_target(info(TARGET_ID, TargetStatus::Ok, target, target));
    assert!(h.manager.initialized);
    assert!(h.pending_count() > 0);
    assert!(h.move_to_complete_calls.is_empty());
}

#[test]
fn a_bad_later_report_cancels() {
    let mut h = arm_move_to_object();
    let target = pos(10.0, 0.0, 0.0);
    h.move_to_handle_update_target(info(TARGET_ID, TargetStatus::Ok, target, target));
    assert!(h.manager.initialized);
    h.move_to_handle_update_target(info(TARGET_ID, TargetStatus::ExitWorld, target, target));
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn a_later_report_retargets_without_replanning() {
    let mut h = arm_move_to_object();
    let target1 = pos(10.0, 0.0, 0.0);
    h.move_to_handle_update_target(info(TARGET_ID, TargetStatus::Ok, target1, target1));
    let count = h.pending_count();
    assert!(count > 0);
    h.advance(3.0);
    let target2 = pos(20.0, 5.0, 0.0);
    let interp2 = pos(19.0, 5.0, 0.0);
    h.move_to_handle_update_target(info(TARGET_ID, TargetStatus::Ok, target2, interp2));
    assert_eq!(h.manager.current_target_position, target2);
    assert_eq!(h.manager.sought_position, interp2);
    assert_eq!(h.manager.previous_distance, f32::MAX);
    assert_eq!(h.manager.original_distance, f32::MAX);
    assert_eq!(h.pending_count(), count);
}

#[test]
fn a_turn_to_object_ignores_later_reports() {
    let mut h = armed_facing(0.0);
    h.turn_to_object(TARGET_ID, TARGET_ID, &p());
    h.turn_to_object_internal(pos(10.0, 0.0, 0.0));
    assert!(h.manager.initialized);
    let sought = h.manager.sought_position;
    let target2 = pos(0.0, 10.0, 0.0);
    h.move_to_handle_update_target(info(TARGET_ID, TargetStatus::Ok, target2, target2));
    assert_eq!(h.manager.sought_position, sought);
}

#[test]
fn a_report_without_a_body_cancels() {
    let mut h = arm_move_to_object();
    h.manager.has_physics_obj = false;
    let target = pos(10.0, 0.0, 0.0);
    h.move_to_handle_update_target(info(TARGET_ID, TargetStatus::Ok, target, target));
    assert!(!h.manager.is_moving_to());
}

// ── UseTimeGate ───────────────────────────────────────────────────────────

#[test]
fn use_time_with_nothing_queued_does_nothing() {
    let mut h = MoveToHarness::new();
    h.move_to_use_time();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn use_time_waits_for_contact() {
    let mut h = armed_facing(90.0);
    h.contact_value = false;
    h.move_to_position_request(pos(20.0, 0.0, 0.0), &p());
    h.drain_pending_motions();
    let before = h.manager.current_command;
    h.world_position = pos(10.0, 0.0, 0.0);
    h.advance(5.0);
    h.move_to_use_time();
    assert_eq!(h.manager.current_command, before);
    assert_eq!(h.manager.movement_type_state, MovementType::MoveToPosition);
}

#[test]
fn use_time_drives_a_move_step() {
    let mut h = armed_facing(90.0);
    h.move_to_position_request(
        pos(20.0, 0.0, 0.0),
        &MovementParameters {
            distance_to_object: 0.6,
            use_spheres: false,
            ..p()
        },
    );
    h.drain_pending_motions();
    h.world_position = pos(19.7, 0.0, 0.0);
    h.advance(2.0);
    h.move_to_use_time();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn use_time_drives_a_turn_step() {
    let mut h = armed_facing(0.0);
    h.turn_to_heading(&MovementParameters {
        desired_heading: 90.0,
        ..p()
    });
    h.drain_pending_motions();
    assert_eq!(h.manager.current_command, TURN_RIGHT);
    h.heading = 91.0;
    h.move_to_use_time();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn an_unplanned_object_move_stalls() {
    let mut h = MoveToHarness::new();
    h.move_to_object(0x5000_AAAA, 0x5000_AAAA, 1.0, 2.0, &p());
    assert!(!h.manager.initialized);
    assert_eq!(h.pending_count(), 0);
    h.move_to_use_time();
    assert_eq!(h.manager.movement_type_state, MovementType::MoveToObject);
    assert!(!h.manager.initialized);
}

#[test]
fn a_planned_object_move_runs() {
    let mut h = armed_facing(90.0);
    h.move_to_object(
        0x5000_BBBB,
        0x5000_BBBB,
        0.5,
        2.0,
        &MovementParameters {
            use_spheres: false,
            ..p()
        },
    );
    let target = pos(20.0, 0.0, 0.0);
    h.move_to_handle_update_target(info(0x5000_BBBB, TargetStatus::Ok, target, target));
    assert!(h.manager.initialized);
    h.drain_pending_motions();
    h.world_position = pos(19.5, 0.0, 0.0);
    h.advance(2.0);
    h.move_to_use_time();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn a_position_move_passes_the_object_gate() {
    let mut h = armed_facing(90.0);
    h.move_to_position_request(
        pos(20.0, 0.0, 0.0),
        &MovementParameters {
            distance_to_object: 0.6,
            use_spheres: false,
            ..p()
        },
    );
    h.drain_pending_motions();
    assert_eq!(h.manager.top_level_object_id, 0);
    assert!(!h.manager.initialized);
    h.world_position = pos(19.7, 0.0, 0.0);
    h.advance(2.0);
    h.move_to_use_time();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

// ── StickyAndCancel ───────────────────────────────────────────────────────

#[test]
fn sticky_arrival_hands_off_the_target_size() {
    let mut h = armed_facing(0.0);
    h.move_to_object(
        0x5000_5555,
        0x5000_5555,
        1.5,
        2.5,
        &MovementParameters {
            sticky: true,
            ..p()
        },
    );
    assert!(h.manager.params.sticky);
    let target = pos(0.1, 0.0, 0.0);
    h.move_to_handle_update_target(info(0x5000_5555, TargetStatus::Ok, target, target));
    assert_eq!(h.stick_to_calls, vec![(0x5000_5555, 1.5, 2.5)]);
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);

    let mut h = armed_facing(0.0);
    h.own_radius = 99.0;
    h.own_height = 99.0;
    h.move_to_object(
        0x5000_6666,
        0x5000_6666,
        3.0,
        4.0,
        &MovementParameters {
            sticky: true,
            ..p()
        },
    );
    let target = pos(0.1, 0.0, 0.0);
    h.move_to_handle_update_target(info(0x5000_6666, TargetStatus::Ok, target, target));
    assert_eq!(h.stick_to_calls[0], (0x5000_6666, 3.0, 4.0));
}

#[test]
fn non_sticky_arrival_does_not_stick() {
    let mut h = armed_facing(0.0);
    h.move_to_object(
        0x5000_5555,
        0x5000_5555,
        1.5,
        2.5,
        &MovementParameters {
            sticky: false,
            ..p()
        },
    );
    let target = pos(0.1, 0.0, 0.0);
    h.move_to_handle_update_target(info(0x5000_5555, TargetStatus::Ok, target, target));
    assert!(h.stick_to_calls.is_empty());
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);

    let mut h = armed_facing(0.0);
    h.move_to_position_request(
        pos(0.1, 0.0, 0.0),
        &MovementParameters {
            sticky: true,
            distance_to_object: 0.6,
            use_spheres: false,
            ..p()
        },
    );
    assert!(h.stick_to_calls.is_empty());
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn cancel_when_idle_does_nothing() {
    let mut h = MoveToHarness::new();
    h.cancel_move_to(WeenieError::ACTION_CANCELLED);
    assert_eq!(h.stop_completely_calls, 0);
    assert_eq!(h.clear_target_calls, 0);
}

#[test]
fn cancel_drains_the_queue() {
    let mut h = armed_facing(0.0);
    h.move_to_position_request(pos(20.0, 0.0, 0.0), &p());
    assert!(h.pending_count() > 0);
    h.cancel_move_to(WeenieError::ACTION_CANCELLED);
    assert_eq!(h.pending_count(), 0);
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn the_cancel_code_does_not_change_the_behaviour() {
    let mut h1 = armed_facing(0.0);
    h1.move_to_position_request(pos(20.0, 0.0, 0.0), &p());
    h1.cancel_move_to(WeenieError::YOU_CHARGED_TOO_FAR);
    let mut h2 = armed_facing(0.0);
    h2.move_to_position_request(pos(20.0, 0.0, 0.0), &p());
    h2.cancel_move_to(WeenieError::NO_OBJECT);
    assert_eq!(
        h1.manager.movement_type_state,
        h2.manager.movement_type_state
    );
    assert_eq!(h1.stop_completely_calls, h2.stop_completely_calls);
}

#[test]
fn cancel_clears_an_object_target_only() {
    let mut h = MoveToHarness::new();
    h.move_to_object(0x5000_7777, 0x5000_7777, 1.0, 2.0, &p());
    assert_eq!(h.set_target_calls.len(), 1);
    h.cancel_move_to(WeenieError::ACTION_CANCELLED);
    assert_eq!(h.clear_target_calls, 1);

    let mut h = MoveToHarness::new();
    h.move_to_position_request(pos(20.0, 0.0, 0.0), &p());
    h.cancel_move_to(WeenieError::ACTION_CANCELLED);
    assert_eq!(h.clear_target_calls, 0);
}

#[test]
fn a_reentrant_cancel_from_stop_completely_is_a_no_op() {
    let mut h = MoveToHarness::new();
    h.reenter_cancel_on_stop = true;
    h.move_to_position_request(pos(20.0, 0.0, 0.0), &p());
    let stops = h.stop_completely_calls;
    h.cancel_move_to(WeenieError::ACTION_CANCELLED);
    assert_eq!(h.stop_completely_calls, stops + 1);
    assert_eq!(h.reentrant_cancel_calls, 2);
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn clean_up_and_call_weenie_completes_with_the_code() {
    let mut h = MoveToHarness::new();
    h.move_to_position_request(pos(20.0, 0.0, 0.0), &p());
    h.clean_up_and_call_weenie(WeenieError::NONE);
    assert_eq!(h.move_to_complete_calls, vec![WeenieError::NONE]);
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn the_move_is_already_idle_when_stop_completely_runs() {
    let mut h = MoveToHarness::new();
    h.move_to_position_request(pos(20.0, 0.0, 0.0), &p());
    h.clean_up_and_call_weenie(WeenieError::NONE);
    assert_eq!(h.state_during_stop, Some(MovementType::Invalid));
}

#[test]
fn clean_up_stops_motions_clears_the_target_and_resets() {
    let mut h = MoveToHarness::new();
    h.move_to_object(0x5000_8888, 0x5000_8888, 1.0, 2.0, &p());
    let target = pos(20.0, 0.0, 0.0);
    h.move_to_handle_update_target(info(0x5000_8888, TargetStatus::Ok, target, target));
    assert_ne!(h.manager.current_command, 0);
    h.move_to_clean_up();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
    assert_eq!(h.clear_target_calls, 1);
    assert_eq!(h.manager.current_command, 0);
}

// ── EndToEndTableDrive ────────────────────────────────────────────────────

#[test]
fn a_chase_turns_runs_walks_and_arrives() {
    let mut h = armed_facing(0.0);
    let pp = MovementParameters {
        distance_to_object: 0.6,
        walk_run_threshold: 15.0,
        use_spheres: false,
        ..p()
    };
    h.move_to_position_request(pos(100.0, 0.0, 0.0), &pp);
    assert_eq!(h.pending_count(), 2);
    assert_eq!(h.manager.current_command, TURN_RIGHT);
    h.drain_pending_motions();
    h.heading = 91.0;
    h.handle_turn_to_heading();
    h.drain_pending_motions();
    assert_eq!(h.manager.movement_type_state, MovementType::MoveToPosition);
    assert_eq!(h.pending_count(), 1);
    assert_eq!(h.forward_command(), RUN_FORWARD);
    assert_eq!(h.manager.params.hold_key_to_apply, HoldKey::Run);

    h.world_position = pos(90.0, 0.0, 0.0);
    h.heading = 90.0;
    h.move_to_perform_movement(&MovementStruct {
        kind: MovementType::MoveToPosition,
        pos: pos(100.0, 0.0, 0.0),
        params: Some(pp),
        ..MovementStruct::default()
    });
    h.drain_pending_motions();
    h.begin_next_node();
    h.drain_pending_motions();
    assert_eq!(h.pending_count(), 1);
    assert_eq!(h.forward_command(), WALK_FORWARD);
    assert_eq!(h.manager.params.hold_key_to_apply, HoldKey::None);

    h.world_position = pos(99.7, 0.0, 0.0);
    h.advance(2.0);
    h.handle_move_to_position();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
    assert_eq!(h.pending_count(), 0);
}

#[test]
fn a_flee_walks_backwards_inside_the_band() {
    let mut h = MoveToHarness::new();
    h.world_position = pos(3.0, 0.0, 0.0);
    h.heading = 270.0;
    h.move_to_position_request(
        pos(0.0, 0.0, 0.0),
        &MovementParameters {
            move_towards: true,
            move_away: true,
            min_distance: 5.0,
            distance_to_object: 8.0,
            use_spheres: false,
            ..p()
        },
    );
    assert!(h.manager.moving_away);
    assert_eq!(h.manager.current_command, WALK_BACKWARDS);
    assert_eq!(h.forward_command(), WALK_FORWARD);
    assert!(h.forward_speed() < 0.0);
    h.drain_pending_motions();
    h.world_position = pos(6.0, 0.0, 0.0);
    h.advance(2.0);
    h.handle_move_to_position();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
}

#[test]
fn a_turn_to_object_ignores_a_retarget_and_snaps_to_its_heading() {
    let mut h = armed_facing(0.0);
    const ID: u32 = 0x5000_CCCC;
    h.turn_to_object(ID, ID, &p());
    assert_eq!(h.pending_count(), 0);
    let target = pos(10.0, 0.0, 0.0);
    h.move_to_handle_update_target(info(ID, TargetStatus::Ok, target, target));
    assert!(h.manager.initialized);
    assert_eq!(h.pending_count(), 1);
    assert_eq!(h.manager.current_command, TURN_RIGHT);
    h.drain_pending_motions();
    let target2 = pos(0.0, 10.0, 0.0);
    h.move_to_handle_update_target(info(ID, TargetStatus::Ok, target2, target2));
    h.heading = 91.0;
    h.handle_turn_to_heading();
    assert_eq!(h.manager.movement_type_state, MovementType::Invalid);
    eq_prec(90.0, h.heading, 2);
}

// ── MovementStructWideningTests ───────────────────────────────────────────

#[test]
fn movement_struct_carries_every_field() {
    let mvs = MovementStruct {
        kind: MovementType::MoveToObject,
        object_id: 0x5000_1234,
        top_level_id: 0x5000_5678,
        radius: 0.75,
        height: 1.8,
        ..MovementStruct::default()
    };
    assert_eq!(
        (mvs.object_id, mvs.top_level_id),
        (0x5000_1234, 0x5000_5678)
    );
    assert_eq!((mvs.radius, mvs.height), (0.75, 1.8));
    let at = CellPosition::new(0x1234_0001, Vec3::new(10.0, 20.0, 3.0), Quat::IDENTITY);
    let mvs = MovementStruct {
        kind: MovementType::MoveToPosition,
        pos: at,
        ..MovementStruct::default()
    };
    assert_eq!(mvs.pos, at);
    assert_eq!(mvs.pos.obj_cell_id, 0x1234_0001);
    let pp = MovementParameters {
        can_charge: true,
        ..p()
    };
    let mvs = MovementStruct {
        kind: MovementType::TurnToHeading,
        params: Some(pp),
        ..MovementStruct::default()
    };
    assert_eq!(mvs.params, Some(pp));
    let mvs = MovementStruct {
        kind: MovementType::RawCommand,
        motion: 0x4500_0005,
        speed: 1.5,
        autonomous: true,
        modify_interpreted_state: true,
        modify_raw_state: false,
        ..MovementStruct::default()
    };
    assert_eq!(mvs.kind, MovementType::RawCommand);
    assert_eq!((mvs.motion, mvs.speed), (0x4500_0005, 1.5));
    assert!(mvs.autonomous && mvs.modify_interpreted_state && !mvs.modify_raw_state);
}

#[test]
fn movement_type_values_match_retail() {
    for (t, v) in [
        (MovementType::Invalid, 0),
        (MovementType::RawCommand, 1),
        (MovementType::InterpretedCommand, 2),
        (MovementType::StopRawCommand, 3),
        (MovementType::StopInterpretedCommand, 4),
        (MovementType::StopCompletely, 5),
        (MovementType::MoveToObject, 6),
        (MovementType::MoveToPosition, 7),
        (MovementType::TurnToObject, 8),
        (MovementType::TurnToHeading, 9),
    ] {
        assert_eq!(t as u32, v);
    }
}
