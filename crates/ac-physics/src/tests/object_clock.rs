//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/RetailObjectQuantumClockTests.cs`,
//! `RetailObjectActivityGateTests.cs`, `RetailPhysicsStateTransitionTests.cs`
//! and `SpawnPlacementSettlerTests.cs`.

use glam::{Quat, Vec3};

use crate::body::PhysicsBody;
use crate::engine::PhysicsEngine;
use crate::quantum::{
    RetailObjectActivityResult as R, RetailObjectQuantumClock, evaluate_activity,
};
use crate::settle::try_settle;
use crate::state::ObjectInfoState;
use crate::state_transition::{CONSTRUCTOR_STATE, RetailHiddenTransition, apply};
use crate::terrain::TerrainSurface;
use crate::world::CollisionWorld;
use crate::{PhysicsState, TransientState};

#[track_caller]
fn assert_close64(expected: f64, actual: f64, precision: i32) {
    let scale = 10f64.powi(precision);
    assert_eq!(
        (expected * scale).round_ties_even(),
        (actual * scale).round_ties_even(),
        "expected {expected}, got {actual}"
    );
}

// ── RetailObjectQuantumClockTests ──────────────────────────────────────

#[test]
fn advance_sub_minimum_time_is_retained_until_strict_threshold_is_crossed() {
    let mut clock = RetailObjectQuantumClock::new();
    let half = f64::from(PhysicsBody::MIN_QUANTUM) * 0.5;
    assert_eq!(clock.advance(half).count(), 0);
    assert_eq!(clock.advance(half).count(), 0);
    let batch = clock.advance(0.00001);
    assert_eq!(batch.count(), 1);
    assert!(batch.get_quantum(0).unwrap() > PhysicsBody::MIN_QUANTUM);
    assert_close64(0.0, clock.pending_seconds(), 8);
}

#[test]
fn advance_exact_four_tenths_equals_two_retail_max_quanta() {
    let mut clock = RetailObjectQuantumClock::new();
    let batch = clock.advance(0.4);
    assert_eq!(batch.count(), 2);
    assert_eq!(batch.get_quantum(0).unwrap(), PhysicsBody::MAX_QUANTUM);
    super::support::assert_eq_prec(PhysicsBody::MAX_QUANTUM, batch.get_quantum(1).unwrap(), 6);
}

#[test]
fn advance_huge_plus_epsilon_discards_without_object_tick() {
    let mut clock = RetailObjectQuantumClock::new();
    let batch = clock.advance(f64::from(PhysicsBody::HUGE_QUANTUM) + 0.00001);
    assert!(batch.discarded);
    assert_eq!(batch.count(), 0);
    assert_eq!(clock.pending_seconds(), 0.0);
}

#[test]
fn advance_exact_huge_quantum_is_subdivided_not_discarded() {
    let mut clock = RetailObjectQuantumClock::new();
    let batch = clock.advance(f64::from(PhysicsBody::HUGE_QUANTUM));
    assert!(!batch.discarded);
    assert_eq!(batch.count(), 10);
    for i in 0..batch.count() {
        super::support::assert_eq_prec(PhysicsBody::MAX_QUANTUM, batch.get_quantum(i).unwrap(), 6);
    }
}

#[test]
fn advance_micro_quantum_is_consumed_rather_than_accumulated() {
    let mut clock = RetailObjectQuantumClock::new();
    for _ in 0..100 {
        assert_eq!(clock.advance(0.0001).count(), 0);
    }
    assert_eq!(clock.pending_seconds(), 0.0);
}

#[test]
fn deactivate_then_activate_rebases_once_and_does_not_catch_up() {
    let mut clock = RetailObjectQuantumClock::new();
    assert_eq!(clock.advance(0.02).count(), 0);
    clock.deactivate();
    assert!(!clock.is_active());
    assert_close64(0.02, clock.pending_seconds(), 8);
    assert!(clock.activate());
    assert!(clock.is_active());
    assert_close64(0.0, clock.pending_seconds(), 8);
    assert!(!clock.activate());
}

#[test]
fn reset_for_enter_world_discards_fragment_and_activates_immediately() {
    let mut clock = RetailObjectQuantumClock::new();
    assert_eq!(clock.advance(0.02).count(), 0);
    clock.reset_for_enter_world(false);
    assert!(clock.is_active());
    assert_close64(0.0, clock.pending_seconds(), 8);
    assert!(!clock.activate());
}

#[test]
fn reset_for_enter_world_static_object_rebases_but_retains_inactive_state() {
    let mut clock = RetailObjectQuantumClock::new();
    assert_eq!(clock.advance(0.02).count(), 0);
    clock.deactivate();
    clock.reset_for_enter_world(true);
    assert!(!clock.is_active());
    assert_close64(0.0, clock.pending_seconds(), 8);
}

// ── RetailObjectActivityGateTests ──────────────────────────────────────

#[test]
fn far_part_array_object_consumes_inactive_clock_and_rebases_when_near_again() {
    let mut clock = RetailObjectQuantumClock::new();
    let mut body = PhysicsBody::new();
    body.transient_state = TransientState::ACTIVE;
    assert_eq!(clock.advance(0.02).count(), 0);
    assert_eq!(
        evaluate_activity(
            &mut clock,
            Some(&mut body),
            true,
            true,
            false,
            Vec3::new(96.01, 0.0, 0.0),
            Some(Vec3::ZERO),
            0.02
        ),
        R::Inactive
    );
    assert!(!clock.is_active());
    assert_close64(0.0, clock.pending_seconds(), 8);
    assert!(!body.transient_state.contains(TransientState::ACTIVE));
    assert_eq!(
        evaluate_activity(
            &mut clock,
            Some(&mut body),
            true,
            true,
            false,
            Vec3::new(95.99, 0.0, 0.0),
            Some(Vec3::ZERO),
            0.02
        ),
        R::Reactivated
    );
    assert!(clock.is_active());
    assert_close64(0.0, clock.pending_seconds(), 8);
    assert!(body.transient_state.contains(TransientState::ACTIVE));
}

#[test]
fn frozen_interval_is_discarded_by_inactive_to_active_rebase() {
    let mut clock = RetailObjectQuantumClock::new();
    assert_eq!(clock.advance(0.02).count(), 0);
    assert_eq!(
        evaluate_activity(
            &mut clock,
            None,
            false,
            true,
            false,
            Vec3::ZERO,
            Some(Vec3::ZERO),
            0.5
        ),
        R::Suspended
    );
    assert_eq!(
        evaluate_activity(
            &mut clock,
            None,
            true,
            true,
            false,
            Vec3::ZERO,
            Some(Vec3::ZERO),
            0.01
        ),
        R::Reactivated
    );
    assert_close64(0.0, clock.pending_seconds(), 8);
    assert_eq!(clock.advance(0.02).count(), 0);
}

#[test]
fn part_array_less_object_remains_active_beyond_distance_gate() {
    let mut clock = RetailObjectQuantumClock::new();
    assert_eq!(
        evaluate_activity(
            &mut clock,
            None,
            true,
            false,
            false,
            Vec3::new(500.0, 0.0, 0.0),
            Some(Vec3::ZERO),
            0.02
        ),
        R::Active
    );
    assert!(clock.is_active());
}

#[test]
fn missing_player_preserves_prior_inactive_state_without_reactivation() {
    let mut clock = RetailObjectQuantumClock::new();
    clock.deactivate();
    assert_eq!(
        evaluate_activity(&mut clock, None, true, true, false, Vec3::ZERO, None, 0.04),
        R::Inactive
    );
    assert!(!clock.is_active());
    assert_close64(0.0, clock.pending_seconds(), 8);
}

#[test]
fn near_static_object_does_not_reactivate_ordinary_object_path() {
    let mut clock = RetailObjectQuantumClock::new();
    clock.deactivate();
    let mut body = PhysicsBody::new();
    body.state = PhysicsState::STATIC;
    body.transient_state = TransientState::NONE;
    assert_eq!(
        evaluate_activity(
            &mut clock,
            Some(&mut body),
            true,
            true,
            true,
            Vec3::ZERO,
            Some(Vec3::ZERO),
            0.04
        ),
        R::Inactive
    );
    assert!(!clock.is_active());
    assert_close64(0.0, clock.pending_seconds(), 8);
    assert!(!body.transient_state.contains(TransientState::ACTIVE));
}

// ── RetailPhysicsStateTransitionTests ──────────────────────────────────

#[test]
fn constructor_state_matches_retail_and_is_not_hidden() {
    assert_eq!(CONSTRUCTOR_STATE.bits(), 0x0040_0C08);
    assert!((CONSTRUCTOR_STATE & PhysicsState::HIDDEN).is_empty());
}

#[test]
fn visible_to_hidden_plays_hidden_shape_and_rewrites_collision_bits() {
    let requested = PhysicsState::HIDDEN | PhysicsState::REPORT_COLLISIONS | PhysicsState::LIGHTING;
    let result = apply(CONSTRUCTOR_STATE, requested);
    assert_eq!(
        result.hidden_transition,
        RetailHiddenTransition::BecameHidden
    );
    assert!(!result.lighting_changed);
    assert!((result.final_state & PhysicsState::REPORT_COLLISIONS).is_empty());
    assert!(!(result.final_state & PhysicsState::IGNORE_COLLISIONS).is_empty());
    assert!(!(result.final_state & PhysicsState::HIDDEN).is_empty());
}

#[test]
fn hidden_to_visible_restores_report_collisions_even_when_wire_omits_it() {
    let hidden = PhysicsState::HIDDEN | PhysicsState::IGNORE_COLLISIONS;
    let result = apply(hidden, PhysicsState::NONE);
    assert_eq!(
        result.hidden_transition,
        RetailHiddenTransition::BecameVisible
    );
    assert!((result.final_state & PhysicsState::HIDDEN).is_empty());
    assert!((result.final_state & PhysicsState::IGNORE_COLLISIONS).is_empty());
    assert!(!(result.final_state & PhysicsState::REPORT_COLLISIONS).is_empty());
}

#[test]
fn identical_hidden_state_does_not_replay_transition() {
    let state = PhysicsState::HIDDEN | PhysicsState::IGNORE_COLLISIONS;
    let result = apply(state, state);
    assert_eq!(result.hidden_transition, RetailHiddenTransition::None);
    assert!(!result.lighting_changed);
    assert!(!result.no_draw_changed);
    assert_eq!(result.final_state, state);
}

#[test]
fn lighting_no_draw_and_hidden_change_flags_are_derived_from_low_word() {
    let result = apply(
        PhysicsState::NONE,
        PhysicsState::LIGHTING | PhysicsState::NO_DRAW | PhysicsState::HIDDEN,
    );
    assert!(result.lighting_changed);
    assert!(result.no_draw_changed);
    assert_eq!(
        result.hidden_transition,
        RetailHiddenTransition::BecameHidden
    );
}

// ── SpawnPlacementSettlerTests ─────────────────────────────────────────

const LANDBLOCK: u32 = 0xA9B4_0000;
const CELL: u32 = LANDBLOCK | 0x0001;
const RADIUS: f32 = 0.48;
const HEIGHT: f32 = 1.835;

fn airborne_body(position: Vec3) -> PhysicsBody {
    let mut b = PhysicsBody::new();
    b.set_position(position);
    b.orientation = Quat::IDENTITY;
    b.state = PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS;
    b.transient_state = TransientState::NONE;
    b
}

fn add_flat_landblock(engine: &mut PhysicsEngine) {
    engine.add_landblock(
        LANDBLOCK,
        TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
}

fn flat_engine() -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    add_flat_landblock(&mut engine);
    engine
}

fn settle(
    engine: &PhysicsEngine,
    body: &mut PhysicsBody,
    hits: &mut i32,
    leaves: &mut i32,
) -> bool {
    let position = body.position();
    let mut hit = |_: &mut PhysicsBody| *hits += 1;
    let mut leave = |_: &mut PhysicsBody| *leaves += 1;
    try_settle(
        engine,
        body,
        position,
        CELL,
        RADIUS,
        HEIGHT,
        ObjectInfoState(ObjectInfoState::EDGE_SLIDE),
        0x7000_0001,
        &mut hit,
        &mut leave,
    )
}

#[test]
fn floor_within_first_gravity_frame_commits_ground_contact_once() {
    let engine = flat_engine();
    let mut body = airborne_body(Vec3::new(12.0, 12.0, 0.25));
    let (mut hits, mut leaves) = (0, 0);
    assert!(settle(&engine, &mut body, &mut hits, &mut leaves));
    assert!(body.in_contact());
    assert!(body.on_walkable());
    assert_eq!(hits, 1);
    assert_eq!(leaves, 0);
}

#[test]
fn missing_cell_can_retry_after_hydration_without_reconstructing_body() {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    let mut body = airborne_body(Vec3::new(12.0, 12.0, 0.25));
    let original = body.position();
    let (mut hits, mut leaves) = (0, 0);
    assert!(!settle(&engine, &mut body, &mut hits, &mut leaves));
    assert!(!body.in_contact());
    assert_eq!(body.position(), original);
    add_flat_landblock(&mut engine);
    assert!(settle(&engine, &mut body, &mut hits, &mut leaves));
    assert!(body.in_contact());
    assert!(body.on_walkable());
}

#[test]
fn floor_outside_settle_distance_leaves_genuinely_airborne_body_unchanged() {
    let engine = flat_engine();
    let mut body = airborne_body(Vec3::new(12.0, 12.0, 1.25));
    let original = body.position();
    let (mut hits, mut leaves) = (0, 0);
    assert!(!settle(&engine, &mut body, &mut hits, &mut leaves));
    assert!(!body.in_contact());
    assert!(!body.on_walkable());
    assert_eq!(body.position(), original);
}

#[test]
fn settle_leaving_an_env_cell_adopts_the_transitions_resolved_outdoor_cell() {
    const STALE_ENV_CELL: u32 = LANDBLOCK | 0x0100;
    let engine = flat_engine();
    let mut body = airborne_body(Vec3::new(12.0, 12.0, 0.25));
    let p = body.position();
    body.stage_dormant_cell_frame(STALE_ENV_CELL, p, p);
    assert_eq!(body.cell_position().obj_cell_id, STALE_ENV_CELL);
    let (mut hits, mut leaves) = (0, 0);
    assert!(settle(&engine, &mut body, &mut hits, &mut leaves));
    assert!(body.in_contact());
    let cell = body.cell_position().obj_cell_id;
    assert_ne!(cell, STALE_ENV_CELL);
    assert_eq!(cell & 0xFFFF_0000, LANDBLOCK);
    assert!((1..=0x40).contains(&(cell & 0xFFFF)));
}
