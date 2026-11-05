//! Ported from OpenAC
//! `tests/AcDream.Runtime.Tests/World/RuntimeWorldTransitStateTests.cs`
//! (all but the host-projection cases, which are not ported).

use ac_proto::{Quaternion, Vector3};

use super::*;

const OUTDOOR: u32 = 0xA9B4_0031;
const OTHER: u32 = 0x7D64_0020;
const INDOOR: u32 = 0x1134_0100;

fn destination(cell: u32, sequence: u16, x: f32) -> TeleportDestination {
    TeleportDestination {
        guid: 0x5000_0001,
        instance_sequence: 1,
        position_sequence: 1,
        teleport_sequence: sequence,
        force_position_sequence: 1,
        position: Position {
            cell_id: cell,
            origin: Vector3 { x, y: 2.0, z: 3.0 },
            orientation: Quaternion {
                w: 1.0,
                x: 0.0,
                y: 0.0,
                z: 0.0,
            },
        },
    }
}

fn ready(generation: u64, cell: u32) -> DestinationReadiness {
    DestinationReadiness::immediate(generation, cell)
}

fn begin_portal(state: &mut WorldTransit, cell: u32, sequence: u16) -> u64 {
    assert!(state.try_queue_teleport_start(sequence));
    assert!(state.activate_queued_teleport());
    assert!(state.offer_teleport_destination(destination(cell, sequence, 1.0), true));
    state.try_begin_portal_reveal(sequence, cell).unwrap()
}

#[test]
fn a_reveal_owns_its_generation_destination_and_the_simulation_gate() {
    let mut state = WorldTransit::new();
    let generation = state.begin_login_reveal(OUTDOOR);
    assert_eq!(generation, 1);
    let s = state.snapshot();
    assert_eq!((s.generation, s.kind), (1, PortalKind::Login));
    assert_eq!(s.destination_cell(), OUTDOOR);
    assert!(!state.is_world_simulation_available());
    assert!(!s.is_ready());
}

#[test]
fn readiness_for_another_generation_or_cell_is_refused() {
    let mut state = WorldTransit::new();
    let generation = begin_portal(&mut state, OUTDOOR, 1);
    assert!(!state.acknowledge_destination_readiness(ready(generation + 1, OUTDOOR)));
    assert!(!state.acknowledge_destination_readiness(ready(generation, OTHER)));
    assert_eq!(state.snapshot().destination_cell(), OUTDOOR);
    assert!(!state.snapshot().is_ready());
    assert_eq!(state.snapshot().invariant_failure_count, 1);
}

#[test]
fn materialization_releases_the_simulation_before_the_viewport_and_completion() {
    let mut state = WorldTransit::new();
    let generation = begin_portal(&mut state, OUTDOOR, 1);
    assert!(state.acknowledge_destination_readiness(ready(generation, OUTDOOR)));
    assert!(state.acknowledge_portal_materialized(generation, 1, OUTDOOR));
    assert!(state.is_world_simulation_available());
    let s = state.snapshot();
    assert!(s.materialized && !s.world_viewport_observed && !s.completed);
    assert_eq!(s.portal_materialization_count, 1);
    assert!(!state.acknowledge_portal_materialized(generation, 1, OUTDOOR));
    assert!(state.acknowledge_world_viewport_visible(generation));
    assert!(state.complete(generation));
    let s = state.snapshot();
    assert!(s.world_viewport_observed && s.completed);
    assert_eq!(s.portal_materialization_count, 1);
}

#[test]
fn materializing_before_readiness_is_refused() {
    let mut state = WorldTransit::new();
    let generation = begin_portal(&mut state, OUTDOOR, 1);
    assert!(!state.acknowledge_portal_materialized(generation, 1, OUTDOOR));
    assert!(!state.is_world_simulation_available());
    assert!(!state.snapshot().materialized);
    assert_eq!(state.snapshot().invariant_failure_count, 1);
    assert_eq!(state.snapshot().portal_materialization_count, 0);
}

#[test]
fn a_login_completes_before_the_first_visible_frame() {
    let mut state = WorldTransit::new();
    let generation = state.begin_login_reveal(OUTDOOR);
    state.acknowledge_destination_readiness(ready(generation, OUTDOOR));
    assert!(state.complete(generation));
    assert!(state.is_world_simulation_available());
    assert!(!state.snapshot().world_viewport_observed);
    assert!(state.acknowledge_world_viewport_visible(generation));
    assert_eq!(state.snapshot().invariant_failure_count, 0);
}

#[test]
fn login_materialization_follows_readiness_and_refuses_portal_reveals() {
    let mut state = WorldTransit::new();
    let generation = state.begin_login_reveal(OUTDOOR);
    assert!(!state.acknowledge_login_materialized(generation));
    assert_eq!(state.snapshot().invariant_failure_count, 1);
    assert!(state.acknowledge_destination_readiness(ready(generation, OUTDOOR)));
    assert!(state.acknowledge_login_materialized(generation));
    assert!(state.is_world_simulation_available());
    assert_eq!(state.snapshot().portal_materialization_count, 0);
    assert!(!state.acknowledge_login_materialized(generation));
    assert!(state.acknowledge_world_viewport_visible(generation));
    assert!(state.complete(generation));

    let mut state = WorldTransit::new();
    let generation = begin_portal(&mut state, OUTDOOR, 1);
    assert!(state.acknowledge_destination_readiness(ready(generation, OUTDOOR)));
    assert!(!state.acknowledge_login_materialized(generation));
    assert!(!state.acknowledge_login_materialized(0));
    assert!(!state.acknowledge_login_materialized(generation + 1));
    assert!(!state.is_world_simulation_available());
    assert_eq!(state.snapshot().invariant_failure_count, 0);
}

#[test]
fn an_early_viewport_is_refused() {
    let mut state = WorldTransit::new();
    let generation = state.begin_login_reveal(OUTDOOR);
    assert!(!state.acknowledge_world_viewport_visible(generation));
    assert!(!state.snapshot().world_viewport_observed);
    assert_eq!(state.snapshot().invariant_failure_count, 1);
}

#[test]
fn readiness_shape_must_match_the_cell() {
    let shapes = [
        (OUTDOOR, true, 0, false),
        (OUTDOOR, false, 0, false),
        (INDOOR, true, 1, false),
        (INDOOR, true, 0, true),
    ];
    for (cell, indoor, radius, accepted) in shapes {
        let mut state = WorldTransit::new();
        let generation = state.begin_login_reveal(cell);
        let shape = DestinationReadiness {
            is_indoor: indoor,
            required_render_radius: radius,
            ..ready(generation, cell)
        };
        assert_eq!(state.acknowledge_destination_readiness(shape), accepted);
        assert_eq!(
            state.snapshot().invariant_failure_count,
            u32::from(!accepted)
        );
    }
    for radius in [1, 2, 5, 8, 12, 15, 25] {
        let mut state = WorldTransit::new();
        let generation = state.begin_login_reveal(OUTDOOR);
        let shape = DestinationReadiness {
            required_render_radius: radius,
            ..ready(generation, OUTDOOR)
        };
        assert!(state.acknowledge_destination_readiness(shape));
        assert_eq!(state.snapshot().readiness.required_render_radius, radius);
    }
}

#[test]
fn accepted_readiness_sticks() {
    let mut state = WorldTransit::new();
    let generation = begin_portal(&mut state, OUTDOOR, 1);
    let accepted = ready(generation, OUTDOOR);
    assert!(state.acknowledge_destination_readiness(accepted));
    let regressed = DestinationReadiness {
        is_collision_ready: false,
        ..accepted
    };
    assert!(!state.acknowledge_destination_readiness(regressed));
    assert_eq!(state.snapshot().readiness, accepted);
    assert_eq!(state.snapshot().invariant_failure_count, 0);
}

#[test]
fn completion_refuses_reordered_edges() {
    let mut state = WorldTransit::new();
    let login = state.begin_login_reveal(OUTDOOR);
    assert!(!state.complete(login));
    let portal = begin_portal(&mut state, OTHER, 2);
    assert!(state.acknowledge_destination_readiness(ready(portal, OTHER)));
    assert!(!state.complete(portal));
    assert!(!state.snapshot().completed);
    assert_eq!(state.snapshot().invariant_failure_count, 2);
}

#[test]
fn a_newer_reveal_supersedes_and_old_acknowledgements_are_stale() {
    let mut state = WorldTransit::new();
    let first = state.begin_login_reveal(OUTDOOR);
    let second = begin_portal(&mut state, OTHER, 1);
    assert!(second > first);
    assert!(!state.acknowledge_destination_readiness(ready(first, OUTDOOR)));
    assert!(!state.acknowledge_portal_materialized(first, 1, OUTDOOR));
    let s = state.snapshot();
    assert_eq!((s.generation, s.destination_cell()), (second, OTHER));
    assert!(!s.is_ready() && !state.is_world_simulation_available());
    assert_eq!(s.invariant_failure_count, 0);
}

#[test]
fn cancel_releases_the_simulation_and_refuses_later_work() {
    let mut state = WorldTransit::new();
    let generation = begin_portal(&mut state, OUTDOOR, 1);
    assert!(state.cancel(generation));
    assert!(state.is_world_simulation_available() && state.snapshot().cancelled);
    assert!(!state.acknowledge_destination_readiness(ready(generation, OUTDOOR)));
    assert!(!state.acknowledge_portal_materialized(generation, 1, OUTDOOR));
    assert!(!state.complete(generation));

    let mut state = WorldTransit::new();
    let generation = begin_portal(&mut state, OUTDOOR, 1);
    state.acknowledge_destination_readiness(ready(generation, OUTDOOR));
    state.acknowledge_portal_materialized(generation, 1, OUTDOOR);
    assert!(state.cancel(generation));
    assert!(state.snapshot().materialized && state.snapshot().cancelled);
    assert!(!state.complete(generation));
    assert_eq!(state.snapshot().portal_materialization_count, 1);
}

#[test]
fn the_wait_cue_shows_at_five_seconds_without_completing() {
    let mut state = WorldTransit::new();
    let generation = begin_portal(&mut state, OUTDOOR, 1);
    let edge = RETAIL_WAIT_CUE_DELAY;
    assert!(!state.observe_wait(generation, edge - Duration::from_millis(1)));
    assert!(state.observe_wait(generation, edge));
    assert!(state.observe_wait(generation, edge + Duration::from_secs(20)));
    assert!(state.snapshot().wait_cue_shown && !state.snapshot().completed);
    assert!(!state.is_world_simulation_available());
}

#[test]
fn an_unloadable_destination_counts_as_ready() {
    let mut state = WorldTransit::new();
    let generation = begin_portal(&mut state, OUTDOOR, 1);
    let readiness = DestinationReadiness {
        is_unhydratable: true,
        is_render_neighborhood_ready: false,
        are_composite_textures_ready: false,
        is_collision_ready: false,
        ..ready(generation, OUTDOOR)
    };
    assert!(state.acknowledge_destination_readiness(readiness));
    assert!(state.snapshot().is_ready());
    assert!(state.acknowledge_portal_materialized(generation, 1, OUTDOOR));
}

#[test]
fn a_session_reset_clears_state_but_never_reuses_a_generation() {
    let mut state = WorldTransit::new();
    let first = begin_portal(&mut state, OUTDOOR, 1);
    state.reset_session();
    assert_eq!(state.snapshot(), PortalSnapshot::IDLE);
    assert!(state.is_world_simulation_available());
    let second = state.begin_login_reveal(OTHER);
    assert!(second > first);
    assert!(!state.acknowledge_destination_readiness(ready(first, OUTDOOR)));
    assert_eq!(state.snapshot().destination_cell(), OTHER);

    let mut state = WorldTransit::new();
    assert!(!state.offer_teleport_destination(destination(OUTDOOR, 7, 1.0), true));
    assert!(state.try_queue_teleport_start(8));
    state.reset_session();
    assert!(!state.is_teleport_active() && !state.has_pending_teleport_start());
    assert!(!state.has_accepted_teleport_destination());
    assert_eq!(state.buffered_teleport_destination_count(), 0);
    assert!(state.try_queue_teleport_start(8));
}

#[test]
fn a_new_teleport_refuses_the_old_destination_and_takes_its_own() {
    let mut state = WorldTransit::new();
    assert!(state.try_queue_teleport_start(10));
    assert!(state.activate_queued_teleport());
    state.end_teleport();
    assert!(state.try_queue_teleport_start(11));
    assert!(state.activate_queued_teleport());
    assert!(!state.offer_teleport_destination(destination(OUTDOOR, 10, 10.0), true));
    assert!(state.offer_teleport_destination(destination(OTHER, 11, 11.0), true));
    let accepted = state.accepted_teleport_destination().unwrap();
    assert!((accepted.position.origin.x - 11.0).abs() < f32::EPSILON);
    assert_eq!(accepted.cell_id(), OTHER);
}

#[test]
fn a_position_before_the_start_is_held_and_an_equal_one_cannot_replace_it() {
    let mut state = WorldTransit::new();
    assert!(!state.offer_teleport_destination(destination(OUTDOOR, 11, 110.0), true));
    assert_eq!(state.buffered_teleport_destination_count(), 1);
    assert!(state.try_queue_teleport_start(11));
    assert!(state.activate_queued_teleport());
    assert_eq!(
        state.accepted_teleport_destination().unwrap().cell_id(),
        OUTDOOR
    );
    assert!(!state.offer_teleport_destination(destination(OTHER, 11, 111.0), false));
    assert_eq!(
        state.accepted_teleport_destination().unwrap().cell_id(),
        OUTDOOR
    );
}

#[test]
fn an_ordinary_position_with_no_teleport_is_never_held() {
    let mut state = WorldTransit::new();
    assert!(!state.offer_teleport_destination(destination(OUTDOOR, 11, 1.0), false));
    assert_eq!(state.buffered_teleport_destination_count(), 0);
    assert!(state.try_queue_teleport_start(11));
    assert!(state.activate_queued_teleport());
    assert!(!state.has_accepted_teleport_destination());
}

#[test]
fn a_duplicate_start_is_refused_but_a_wrapped_one_is_new() {
    let mut state = WorldTransit::new();
    assert!(state.try_queue_teleport_start(u16::MAX));
    assert!(state.activate_queued_teleport());
    state.end_teleport();
    assert!(!state.can_queue_teleport_start(u16::MAX));
    assert!(!state.try_queue_teleport_start(u16::MAX));
    assert!(state.try_queue_teleport_start(0));
    assert!(state.has_pending_teleport_start());

    let mut state = WorldTransit::new();
    assert!(state.try_queue_teleport_start(0x8000));
    assert!(state.activate_queued_teleport());
    state.end_teleport();
    assert!(state.try_queue_teleport_start(0));
}

#[test]
fn a_pending_start_takes_an_equal_position_once_activated() {
    let mut state = WorldTransit::new();
    assert!(state.try_queue_teleport_start(11));
    assert!(!state.offer_teleport_destination(destination(OUTDOOR, 11, 110.0), false));
    assert!(state.activate_queued_teleport());
    let held = state.accepted_teleport_destination().unwrap();
    assert!((held.position.origin.x - 110.0).abs() < f32::EPSILON);
    assert!(state.is_teleport_active());
}

#[test]
fn an_advancing_position_drops_the_skipped_one_and_wrap_cannot_revive_it() {
    let mut state = WorldTransit::new();
    assert!(!state.offer_teleport_destination(destination(OUTDOOR, 11, 1.0), true));
    assert!(!state.offer_teleport_destination(destination(OTHER, 12, 1.0), true));
    assert_eq!(state.buffered_teleport_destination_count(), 1);
    assert!(state.try_queue_teleport_start(12));
    assert!(state.activate_queued_teleport());
    assert_eq!(
        state.accepted_teleport_destination().unwrap().cell_id(),
        OTHER
    );

    let mut state = WorldTransit::new();
    assert!(!state.offer_teleport_destination(destination(OUTDOOR, u16::MAX, 1.0), true));
    assert!(!state.offer_teleport_destination(destination(OTHER, 0, 1.0), true));
    assert!(state.try_queue_teleport_start(0));
    assert!(state.activate_queued_teleport());
    assert_eq!(
        state.accepted_teleport_destination().unwrap().cell_id(),
        OTHER
    );
    state.end_teleport();
    for sequence in [0x7FFF, 0xFFFE] {
        assert!(state.try_queue_teleport_start(sequence));
        assert!(state.activate_queued_teleport());
        state.end_teleport();
    }
    assert!(state.try_queue_teleport_start(u16::MAX));
    assert!(state.activate_queued_teleport());
    assert!(!state.has_accepted_teleport_destination());
}

#[test]
fn a_portal_reveal_claims_only_the_accepted_sequence_and_cell() {
    let mut state = WorldTransit::new();
    assert!(state.try_queue_teleport_start(7));
    assert!(state.activate_queued_teleport());
    assert!(state.offer_teleport_destination(destination(OUTDOOR, 7, 1.0), true));
    assert!(state.try_begin_portal_reveal(8, OUTDOOR).is_none());
    assert!(state.try_begin_portal_reveal(7, OTHER).is_none());
    assert_eq!(state.snapshot().generation, 0);
    assert!(state.has_accepted_teleport_destination());
    let generation = state.try_begin_portal_reveal(7, OUTDOOR).unwrap();
    assert!(generation > 0);
    assert!(!state.has_accepted_teleport_destination());
    assert!(state.can_place_portal_destination(generation, 7, OUTDOOR));
    assert!(!state.can_place_portal_destination(generation, 8, OUTDOOR));
    assert!(!state.can_place_portal_destination(generation, 7, OTHER));
}

#[test]
fn materialization_needs_the_exact_generation_sequence_and_cell() {
    let mut state = WorldTransit::new();
    let generation = begin_portal(&mut state, OUTDOOR, 17);
    assert!(state.acknowledge_destination_readiness(ready(generation, OUTDOOR)));
    assert!(!state.acknowledge_portal_materialized(generation + 1, 17, OUTDOOR));
    assert!(!state.acknowledge_portal_materialized(generation, 18, OUTDOOR));
    assert!(!state.acknowledge_portal_materialized(generation, 17, OTHER));
    assert!(!state.snapshot().materialized);
    assert!(state.acknowledge_portal_materialized(generation, 17, OUTDOOR));
}

#[test]
fn logout_runs_request_hold_presentation_confirm_complete() {
    let mut state = WorldTransit::new();
    assert_eq!(state.logout_stage(), LogoutStage::None);
    assert!(state.try_begin_logout_request(false));
    assert_eq!(state.logout_stage(), LogoutStage::Requested);
    assert!(!state.is_idle());
    assert!(!state.try_begin_logout_request(false));
    assert!(!state.advance_logout_hold(2.9));
    assert!(state.advance_logout_hold(0.2));
    assert_eq!(state.logout_stage(), LogoutStage::PresentationActive);
    assert!(!state.advance_logout_hold(1.0));
    assert!(state.acknowledge_logout_confirmed());
    assert!(!state.acknowledge_logout_confirmed());
    assert!(state.complete_logout());
    assert!(!state.complete_logout());
    assert!(state.is_idle());
}

#[test]
fn a_player_killer_holds_twenty_seconds_longer() {
    let mut state = WorldTransit::new();
    assert!(state.try_begin_logout_request(true));
    assert!(!state.advance_logout_hold(3.5));
    assert!(!state.advance_logout_hold(19.0));
    assert!(state.advance_logout_hold(0.6));
}

#[test]
fn confirmation_may_come_during_the_hold() {
    let mut state = WorldTransit::new();
    assert!(state.try_begin_logout_request(false));
    assert!(state.acknowledge_logout_confirmed());
    assert!(!state.advance_logout_hold(10.0));
    assert!(state.complete_logout());
}

#[test]
fn logout_is_refused_during_a_teleport_and_cancel_rolls_back() {
    let mut state = WorldTransit::new();
    begin_portal(&mut state, OUTDOOR, 3);
    assert!(!state.try_begin_logout_request(false));
    let mut idle = WorldTransit::new();
    assert!(idle.try_begin_logout_request(false));
    assert!(idle.cancel_logout_request());
    assert!(idle.is_idle());
    assert!(!idle.cancel_logout_request());
    assert!(idle.try_begin_logout_request(false));
    idle.reset_session();
    assert!(idle.is_idle());
    assert!(idle.try_begin_logout_request(false));
}

/// OpenAC `RuntimeWorldTransitDirectHostTests.
/// DirectHost_CompletesLoginPortalCancellationAndReset`: a host with no
/// presentation drives a login, a portal arrival and a cancelled login
/// through the transit owner alone, each session reset leaving it idle
/// (the host-projection acknowledgements are not kept).
#[test]
fn a_direct_host_completes_login_portal_cancellation_and_reset() {
    let mut state = WorldTransit::new();

    let generation = state.begin_login_reveal(OUTDOOR);
    assert!(state.acknowledge_destination_readiness(ready(generation, OUTDOOR)));
    assert!(state.complete(generation));
    assert!(state.acknowledge_world_viewport_visible(generation));
    assert!(state.snapshot().completed);
    assert!(state.snapshot().world_viewport_observed);

    state.reset_session();
    let generation = begin_portal(&mut state, INDOOR, 1);
    assert!(state.acknowledge_destination_readiness(ready(generation, INDOOR)));
    assert!(state.acknowledge_portal_materialized(generation, 1, INDOOR));
    assert!(state.acknowledge_world_viewport_visible(generation));
    assert!(state.complete(generation));
    let s = state.snapshot();
    assert!(s.completed && s.materialized);
    assert_eq!(s.portal_materialization_count, 1);

    state.end_teleport();
    state.reset_session();
    let generation = state.begin_login_reveal(OUTDOOR);
    assert!(state.cancel(generation));
    assert!(state.snapshot().cancelled);

    state.reset_session();
    assert_eq!(state.snapshot(), PortalSnapshot::IDLE);
    assert!(state.is_idle());
}
