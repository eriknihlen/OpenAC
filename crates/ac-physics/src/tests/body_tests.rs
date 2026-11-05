//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/PhysicsBodyTests.cs`,
//! `PhysicsBodyCellSyncTests.cs` and `HandleAllCollisionsTests.cs`.

use std::f32::consts::PI;

use ac_core::physics::adjust_to_outside;
use glam::{Quat, Vec3};

use super::support::*;
use crate::body::PhysicsBody;
use crate::globals::{EPSILON, FLOOR_Z};
use crate::obj_update::{
    apply_set_position_contact, commit_set_position_transition, handle_all_collisions,
    is_walkable_contact,
};
use crate::{PhysicsState, TransientState};

fn airborne() -> PhysicsBody {
    let mut b = PhysicsBody::new();
    b.state = PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS;
    b.transient_state = TransientState::ACTIVE;
    b
}

fn grounded() -> PhysicsBody {
    let mut b = PhysicsBody::new();
    b.state = PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS;
    b.transient_state =
        TransientState::CONTACT | TransientState::ON_WALKABLE | TransientState::ACTIVE;
    b
}

#[track_caller]
fn assert_eq_prec64(expected: f64, actual: f64, precision: i32) {
    let scale = 10f64.powi(precision);
    assert_eq!(
        (expected * scale).round_ties_even(),
        (actual * scale).round_ties_even(),
        "expected {expected}, got {actual}"
    );
}

// ── PhysicsBodyTests ───────────────────────────────────────────────────

#[test]
fn calc_acceleration_airborne_gravity_sets_minus_9_8_on_z() {
    let mut b = airborne();
    b.calc_acceleration();
    assert_eq!(b.acceleration.x, 0.0);
    assert_eq!(b.acceleration.y, 0.0);
    assert_eq_prec(-9.8, b.acceleration.z, 6);
}

#[test]
fn calc_acceleration_grounded_zeros_acceleration_and_omega() {
    let mut b = grounded();
    b.acceleration = Vec3::new(1.0, 2.0, 3.0);
    b.omega = Vec3::splat(0.5);
    b.calc_acceleration();
    assert_eq!(b.acceleration, Vec3::ZERO);
    assert_eq!(b.omega, Vec3::ZERO);
}

#[test]
fn calc_acceleration_no_gravity_flag_zeros_acceleration() {
    let mut b = PhysicsBody::new();
    b.state = PhysicsState::NONE;
    b.transient_state = TransientState::ACTIVE;
    b.acceleration = Vec3::new(0.0, 0.0, -9.8);
    b.calc_acceleration();
    assert_eq!(b.acceleration, Vec3::ZERO);
}

#[test]
fn calc_acceleration_sledding_airborne_still_applies_gravity() {
    let mut b = PhysicsBody::new();
    b.state = PhysicsState::GRAVITY | PhysicsState::SLEDDING;
    b.transient_state = TransientState::ACTIVE;
    b.calc_acceleration();
    assert_eq_prec(-9.8, b.acceleration.z, 6);
}

#[test]
fn update_physics_internal_integrates_position_correctly_one_step() {
    let mut b = airborne();
    b.velocity = Vec3::new(1.0, 0.0, 0.0);
    b.acceleration = Vec3::new(0.0, 0.0, -9.8);
    b.update_physics_internal(0.1);
    assert_eq_prec(0.1, b.position().x, 5);
    assert_eq_prec(0.0, b.position().y, 5);
    assert_eq_prec(-0.049, b.position().z, 4);
}

#[test]
fn update_physics_internal_velocity_updated_by_acceleration_times_dt() {
    let mut b = airborne();
    b.velocity = Vec3::ZERO;
    b.acceleration = Vec3::new(0.0, 0.0, -9.8);
    b.update_physics_internal(0.5);
    assert_eq_prec(0.0, b.velocity.x, 5);
    assert_eq_prec(0.0, b.velocity.y, 5);
    assert_eq_prec(-4.9, b.velocity.z, 4);
}

#[test]
fn update_physics_internal_multiple_frames_accumulates_correctly() {
    let mut b = airborne();
    b.velocity = Vec3::ZERO;
    b.acceleration = Vec3::new(0.0, 0.0, PhysicsBody::GRAVITY);
    let frames = 10;
    let dt = 0.1f32;
    for _ in 0..frames {
        b.update_physics_internal(dt);
    }
    let t = frames as f32 * dt;
    let expected = 0.5 * PhysicsBody::GRAVITY * t * t;
    assert!(
        (b.position().z - expected).abs() < 0.15,
        "z {}",
        b.position().z
    );
}

#[test]
fn update_physics_internal_zero_velocity_clears_active_flag_when_grounded() {
    let mut b = grounded();
    b.velocity = Vec3::ZERO;
    b.transient_state.insert(TransientState::ACTIVE);
    b.update_physics_internal(0.1);
    assert!(!b.is_active());
}

#[test]
fn update_physics_internal_zeroes_small_velocity_even_when_airborne() {
    let mut b = airborne();
    b.set_velocity(Vec3::new(0.1, 0.0, 0.0));
    b.acceleration = Vec3::new(0.0, 0.0, PhysicsBody::GRAVITY);
    b.update_physics_internal(1.0 / 30.0);
    assert!(b.velocity.x.abs() < 1e-4);
    assert!(b.velocity.z < 0.0);
}

#[test]
fn transient_state_flags_has_stationary_bits() {
    assert_eq!(TransientState::STATIONARY_FALL.bits(), 0x10);
    assert_eq!(TransientState::STATIONARY_STOP.bits(), 0x20);
    assert_eq!(TransientState::STATIONARY_STUCK.bits(), 0x40);
}

#[test]
fn physics_body_has_fsf_and_cached_velocity_defaults() {
    let b = PhysicsBody::new();
    assert_eq!(b.frames_stationary_fall, 0);
    assert_eq!(b.cached_velocity, Vec3::ZERO);
}

#[test]
fn set_velocity_below_max_stores_velocity_unchanged() {
    let mut b = PhysicsBody::new();
    let v = Vec3::new(10.0, 5.0, 2.0);
    b.set_velocity(v);
    assert_eq!(b.velocity, v);
}

#[test]
fn set_velocity_above_max_clamps_to_max_velocity_magnitude() {
    let mut b = PhysicsBody::new();
    b.set_velocity(Vec3::new(100.0, 0.0, 0.0));
    assert!(b.velocity.length() <= PhysicsBody::MAX_VELOCITY + 1e-4);
    assert_eq_prec(PhysicsBody::MAX_VELOCITY, b.velocity.length(), 4);
}

#[test]
fn set_velocity_diagonal_above_max_clamps_and_preserves_direction() {
    let mut b = PhysicsBody::new();
    let dir = Vec3::new(3.0, 4.0, 0.0).normalize();
    b.set_velocity(dir * 80.0);
    assert_eq_prec(PhysicsBody::MAX_VELOCITY, b.velocity.length(), 3);
    let r = b.velocity.normalize();
    assert_eq_prec(dir.x, r.x, 4);
    assert_eq_prec(dir.y, r.y, 4);
}

#[test]
fn set_velocity_sets_active_flag() {
    let mut b = PhysicsBody::new();
    b.transient_state = TransientState::NONE;
    b.set_velocity(Vec3::X);
    assert!(b.is_active());
}

#[test]
fn set_velocity_exactly_at_max_is_not_clamped() {
    let mut b = PhysicsBody::new();
    let v = Vec3::new(PhysicsBody::MAX_VELOCITY, 0.0, 0.0);
    b.set_velocity(v);
    assert_eq_prec(v.x, b.velocity.x, 4);
    assert_eq_prec(0.0, b.velocity.y, 4);
    assert_eq_prec(0.0, b.velocity.z, 4);
}

#[test]
fn set_local_velocity_identity_orientation_passes_through() {
    let mut b = PhysicsBody::new();
    b.orientation = Quat::IDENTITY;
    b.set_local_velocity(Vec3::X, false);
    assert_eq_prec(1.0, b.velocity.x, 5);
    assert_eq_prec(0.0, b.velocity.y, 5);
    assert_eq_prec(0.0, b.velocity.z, 5);
}

#[test]
fn set_local_velocity_90_degree_yaw_rotates_forward_to_right() {
    let mut b = PhysicsBody::new();
    b.orientation = Quat::from_axis_angle(Vec3::Z, PI / 2.0);
    b.set_local_velocity(Vec3::X, false);
    assert!(b.velocity.x.abs() < 1e-4);
    assert!((b.velocity.y - 1.0).abs() < 1e-4);
    assert!(b.velocity.z.abs() < 1e-4);
}

#[test]
fn set_local_velocity_180_degree_yaw_reverses_horizontal_forward() {
    let mut b = PhysicsBody::new();
    b.orientation = Quat::from_axis_angle(Vec3::Z, PI);
    b.set_local_velocity(Vec3::X, false);
    assert!((b.velocity.x + 1.0).abs() < 1e-4);
    assert!(b.velocity.y.abs() < 1e-4);
}

#[test]
fn set_local_velocity_magnitude_preserved_after_rotation() {
    let mut b = PhysicsBody::new();
    b.orientation = Quat::from_axis_angle(Vec3::Z, 1.23);
    let local = Vec3::new(3.0, 4.0, 0.0);
    b.set_local_velocity(local, false);
    assert_eq_prec(local.length(), b.velocity.length(), 4);
}

#[test]
fn set_on_walkable_true_sets_on_walkable_flag() {
    let mut b = airborne();
    b.set_on_walkable(true);
    assert!(b.on_walkable());
}

#[test]
fn set_on_walkable_false_clears_on_walkable_flag() {
    let mut b = grounded();
    b.set_on_walkable(false);
    assert!(!b.on_walkable());
}

#[test]
fn set_on_walkable_true_also_calls_calc_acceleration_zeroing_accel() {
    let mut b = PhysicsBody::new();
    b.state = PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS;
    b.transient_state = TransientState::CONTACT;
    b.acceleration = Vec3::new(0.0, 0.0, -9.8);
    b.set_on_walkable(true);
    assert_eq!(b.acceleration, Vec3::ZERO);
}

#[test]
fn set_on_walkable_false_allows_gravity_to_apply() {
    let mut b = grounded();
    b.set_on_walkable(false);
    assert_eq_prec(-9.8, b.acceleration.z, 6);
}

#[test]
fn calc_friction_not_on_walkable_does_nothing() {
    let mut b = airborne();
    b.velocity = Vec3::new(5.0, 0.0, 0.0);
    let before = b.velocity;
    let m = b.velocity.length_squared();
    b.calc_friction(0.1, m);
    assert_eq!(b.velocity, before);
}

#[test]
fn calc_friction_velocity_parallel_to_ground_reduces_magnitude() {
    let mut b = grounded();
    b.ground_normal = Vec3::Z;
    b.velocity = Vec3::new(5.0, 0.0, -0.1);
    let m = b.velocity.length_squared();
    b.calc_friction(0.1, m);
    assert!(b.velocity.length() < 5.0);
}

#[test]
fn calc_friction_velocity_moving_away_from_normal_no_change() {
    let mut b = grounded();
    b.ground_normal = Vec3::Z;
    b.velocity = Vec3::new(5.0, 0.0, 1.0);
    let before = b.velocity;
    let m = b.velocity.length_squared();
    b.calc_friction(0.1, m);
    assert_eq!(b.velocity, before);
}

#[test]
fn calc_friction_zero_friction_coefficient_no_reduction() {
    let mut b = grounded();
    b.ground_normal = Vec3::Z;
    b.velocity = Vec3::new(5.0, 0.0, -0.01);
    b.friction = 0.0;
    let m = b.velocity.length_squared();
    b.calc_friction(0.1, m);
    assert!(b.velocity.length() > 4.9);
}

#[test]
fn calc_friction_removes_normal_component_from_velocity() {
    let mut b = grounded();
    b.ground_normal = Vec3::Z;
    b.friction = 0.0;
    b.velocity = Vec3::new(1.0, 0.0, -1.0);
    let m = b.velocity.length_squared();
    b.calc_friction(1.0, m);
    assert!(b.velocity.z.abs() < 1e-4);
    assert_eq_prec(1.0, b.velocity.x, 4);
}

#[test]
fn calc_friction_dot_between_zero_and_quarter_now_engages_friction() {
    let mut b = grounded();
    b.ground_normal = Vec3::Z;
    b.friction = 0.95;
    b.velocity = Vec3::new(5.0, 0.0, 0.1);
    let m = b.velocity.length_squared();
    b.calc_friction(1.0 / 60.0, m);
    assert!(b.velocity.length() < 5.0);
}

#[test]
fn calc_friction_dot_at_quarter_threshold_returns_early_no_change() {
    let mut b = grounded();
    b.ground_normal = Vec3::Z;
    b.velocity = Vec3::new(5.0, 0.0, 0.25);
    let before = b.velocity;
    let m = b.velocity.length_squared();
    b.calc_friction(1.0 / 60.0, m);
    assert_eq!(b.velocity, before);
}

#[test]
fn grounded_root_motion_friction_threshold_does_not_hammer_locomotion_tests() {
    let mut b = grounded();
    b.ground_normal = Vec3::Z;
    b.friction = 0.95;
    b.velocity = Vec3::new(0.0, 0.0, -0.05);
    let m = b.velocity.length_squared();
    b.calc_friction(1.0 / 60.0, m);
    assert_eq_prec(0.0, b.velocity.x, 5);
    assert_eq_prec(0.0, b.velocity.y, 5);
    assert!(b.velocity.z.abs() < 0.05);
}

#[test]
fn calc_friction_sledding_state_gate_reachable_with_new_threshold() {
    let mut b = grounded();
    b.ground_normal = Vec3::Z;
    b.state.insert(PhysicsState::SLEDDING);
    b.velocity = Vec3::new(3.0, 0.0, -0.5);
    let m = b.velocity.length_squared();
    b.calc_friction(1.0 / 60.0, m);
    assert!(b.velocity.length() > 2.9);
}

#[test]
fn calc_friction_sledding_fast_override_engages_at_5_degrees_from_flat() {
    let (s, c) = (5.0f32 * PI / 180.0).sin_cos();
    let mut b = grounded();
    b.ground_normal = Vec3::new(s, 0.0, c);
    b.state.insert(PhysicsState::SLEDDING);
    b.velocity = Vec3::new(0.0, 3.0, 0.0);
    let m = b.velocity.length_squared();
    let dt = 1.0 / 60.0;
    b.calc_friction(dt, m);
    assert_eq_prec(3.0 * 0.8f32.powf(dt), b.velocity.length(), 3);
}

#[test]
fn calc_friction_sledding_fast_override_does_not_engage_at_15_degrees_from_flat() {
    let (s, c) = (15.0f32 * PI / 180.0).sin_cos();
    let mut b = grounded();
    b.ground_normal = Vec3::new(s, 0.0, c);
    b.state.insert(PhysicsState::SLEDDING);
    b.velocity = Vec3::new(0.0, 3.0, 0.0);
    let m = b.velocity.length_squared();
    let dt = 1.0 / 60.0;
    b.calc_friction(dt, m);
    assert_eq_prec(
        3.0 * (1.0 - PhysicsBody::DEFAULT_FRICTION).powf(dt),
        b.velocity.length(),
        3,
    );
}

#[test]
fn update_object_dt_below_min_quantum_accumulates_without_advancing() {
    let mut b = airborne();
    b.velocity = Vec3::X;
    b.acceleration = Vec3::ZERO;
    b.last_update_time = 0.0;
    b.update_object(f64::from(PhysicsBody::MIN_QUANTUM) * 0.5);
    assert_eq!(b.position(), Vec3::ZERO);
    assert_eq!(b.last_update_time, 0.0);
}

#[test]
fn update_object_dt_above_huge_quantum_consumes_time_without_simulating() {
    let mut b = airborne();
    b.velocity = Vec3::X;
    b.acceleration = Vec3::ZERO;
    b.last_update_time = 0.0;
    b.update_object(f64::from(PhysicsBody::HUGE_QUANTUM) + 0.5);
    assert_eq!(b.position(), Vec3::ZERO);
    assert_eq_prec64(
        f64::from(PhysicsBody::HUGE_QUANTUM) + 0.5,
        b.last_update_time,
        10,
    );
}

#[test]
fn update_object_advances_position_over_valid_dt() {
    let mut b = airborne();
    b.state = PhysicsState::NONE;
    b.velocity = Vec3::new(10.0, 0.0, 0.0);
    b.last_update_time = 0.0;
    b.update_object(0.1);
    assert!(b.position().x > 0.0);
}

#[test]
fn update_object_updates_last_update_time() {
    let mut b = airborne();
    b.last_update_time = 0.0;
    b.state = PhysicsState::NONE;
    b.update_object(0.05);
    assert_eq_prec64(0.05, b.last_update_time, 10);
}

#[test]
fn update_object_micro_fragment_is_consumed() {
    let mut b = airborne();
    let t = f64::from(EPSILON) * 0.5;
    b.update_object(t);
    assert_eq_prec64(t, b.last_update_time, 10);
    assert_eq!(b.position(), Vec3::ZERO);
}

#[test]
fn update_object_gravity_free_fall_accumulates_downward_velocity() {
    let mut b = airborne();
    b.last_update_time = 0.0;
    b.update_object(f64::from(PhysicsBody::MIN_QUANTUM) * 2.0);
    assert!(b.velocity.z < 0.0);
}

// ── PhysicsBodyCellSyncTests ───────────────────────────────────────────

#[test]
fn snap_to_cell_consistent_outdoor_pair_seeds_as_given() {
    let mut b = PhysicsBody::new();
    b.snap_to_cell(
        0xC95B_0023,
        Vec3::new(1000.0, 2000.0, 12.0),
        Vec3::new(100.0, 50.0, 12.0),
    );
    assert_eq!(b.cell_position().obj_cell_id, 0xC95B_0023);
    assert_eq!(b.cell_position().frame.origin, Vec3::new(100.0, 50.0, 12.0));
    assert_eq!(b.position(), Vec3::new(1000.0, 2000.0, 12.0));
}

#[test]
fn snap_to_cell_inconsistent_outdoor_pair_canonicalizes_cell_from_local() {
    let mut b = PhysicsBody::new();
    b.snap_to_cell(
        0xC95B_0001,
        Vec3::new(1000.0, 2000.0, 12.0),
        Vec3::new(100.0, 50.0, 12.0),
    );
    assert_eq!(b.cell_position().obj_cell_id, 0xC95B_0023);
    assert_eq!(b.cell_position().frame.origin, Vec3::new(100.0, 50.0, 12.0));
}

#[test]
fn position_delta_within_landblock_updates_cell_index() {
    let mut b = PhysicsBody::new();
    b.snap_to_cell(
        0xC95B_0023,
        Vec3::new(1000.0, 2000.0, 12.0),
        Vec3::new(100.0, 50.0, 12.0),
    );
    b.set_position(b.position() + Vec3::new(0.0, 24.0, 0.0));
    assert_eq!(b.cell_position().frame.origin, Vec3::new(100.0, 74.0, 12.0));
    assert_eq!(b.cell_position().obj_cell_id, 0xC95B_0024);
    assert_eq!(b.position(), Vec3::new(1000.0, 2024.0, 12.0));
}

#[test]
fn position_delta_across_south_landblock_edge_bumps_cell_and_rewraps() {
    let mut b = PhysicsBody::new();
    b.snap_to_cell(
        0xC95B_0001,
        Vec3::new(1000.0, 2000.0, 12.0),
        Vec3::new(100.0, 0.3, 12.0),
    );
    b.set_position(b.position() + Vec3::new(0.0, -1.0, 0.0));
    let lb_y = (b.cell_position().obj_cell_id >> 16) & 0xFF;
    assert_eq!(lb_y, 0x5A);
    let y = b.cell_position().frame.origin.y;
    assert!((190.0..=192.0).contains(&y), "y {y}");
}

#[test]
fn position_delta_without_seed_leaves_cell_position_default() {
    let mut b = PhysicsBody::new();
    b.set_position(b.position() + Vec3::new(10.0, 10.0, 0.0));
    assert_eq!(b.cell_position().obj_cell_id, 0);
}

#[test]
fn position_delta_inside_env_cell_advances_canonical_local_frame() {
    let mut b = PhysicsBody::new();
    b.snap_to_cell(
        0x8C04_01AD,
        Vec3::new(12.0, -30.0, 4.0),
        Vec3::new(80.0, 40.0, 4.0),
    );
    b.set_position(b.position() + Vec3::new(3.0, -2.0, 0.5));
    assert_eq!(b.cell_position().obj_cell_id, 0x8C04_01AD);
    assert_eq!(b.cell_position().frame.origin, Vec3::new(83.0, 38.0, 4.5));
}

#[test]
fn commit_transition_position_inside_dungeon_adopts_resolved_env_cell_and_frame() {
    let mut b = PhysicsBody::new();
    b.snap_to_cell(
        0x8C04_01AD,
        Vec3::new(12.0, -30.0, 4.0),
        Vec3::new(80.0, 40.0, 4.0),
    );
    b.commit_transition_position(0x8C04_01AE, Vec3::new(17.0, -27.0, 4.0));
    assert_eq!(b.cell_position().obj_cell_id, 0x8C04_01AE);
    assert_eq!(b.cell_position().frame.origin, Vec3::new(85.0, 43.0, 4.0));
    assert_eq!(b.position(), Vec3::new(17.0, -27.0, 4.0));
}

#[test]
fn commit_transition_position_without_seed_does_not_invent_canonical_cell() {
    let mut b = PhysicsBody::new();
    b.commit_transition_position(0x8C04_01AD, Vec3::new(1.0, 2.0, 3.0));
    assert_eq!(b.cell_position().obj_cell_id, 0);
    assert_eq!(b.position(), Vec3::new(1.0, 2.0, 3.0));
}

#[test]
fn continuous_tracking_long_walk_across_cells_and_block_never_goes_stale() {
    let mut b = PhysicsBody::new();
    b.snap_to_cell(
        0xC95B_0001,
        Vec3::new(1000.0, 2000.0, 12.0),
        Vec3::new(10.0, 10.0, 12.0),
    );
    for step in [
        Vec3::new(50.0, 0.0, 0.0),
        Vec3::new(50.0, 0.0, 0.0),
        Vec3::new(0.0, 100.0, 0.0),
        Vec3::new(0.0, 100.0, 0.0),
    ] {
        b.set_position(b.position() + step);
    }
    assert_eq!(b.cell_position().obj_cell_id, 0xC95C_0021);
    assert_eq!(b.cell_position().frame.origin, Vec3::new(110.0, 18.0, 12.0));
    let mut cell = b.cell_position().obj_cell_id;
    let mut local = b.cell_position().frame.origin.to_array();
    assert!(adjust_to_outside(&mut cell, &mut local));
    assert_eq!(cell, b.cell_position().obj_cell_id);
}

// ── HandleAllCollisionsTests ───────────────────────────────────────────

fn airborne_with(v: Vec3, fsf: i32) -> PhysicsBody {
    let mut b = PhysicsBody::new();
    b.state = PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS;
    b.transient_state = TransientState::NONE;
    b.velocity = v;
    b.frames_stationary_fall = fsf;
    b
}

#[test]
fn set_position_contact_steep_surface_is_contact_but_not_walkable() {
    let mut b = airborne_with(Vec3::new(0.0, 0.0, -1.0), 0);
    let steep = Vec3::new(1.0, 0.0, 0.5).normalize();
    let on_walkable = is_walkable_contact(true, steep);
    apply_set_position_contact(&mut b, true, on_walkable);
    assert!(b.in_contact());
    assert!(!b.on_walkable());
    assert_eq!(b.acceleration.z, PhysicsBody::GRAVITY);
}

#[test]
fn set_position_contact_walkable_surface_sets_both_facts() {
    let mut b = airborne_with(Vec3::new(0.0, 0.0, -1.0), 0);
    let on_walkable = is_walkable_contact(true, Vec3::new(0.0, 0.0, FLOOR_Z));
    apply_set_position_contact(&mut b, true, on_walkable);
    assert!(b.in_contact());
    assert!(b.on_walkable());
    assert_eq!(b.acceleration, Vec3::ZERO);
}

#[test]
fn fsf0_airborne_wall_hit_reflects_into_wall_component() {
    let mut b = airborne_with(Vec3::new(3.0, 0.0, 0.0), 0);
    handle_all_collisions(&mut b, true, -Vec3::X, false, false, false);
    assert_eq_prec(-0.15, b.velocity.x, 3);
}

#[test]
fn fsf0_moving_away_from_surface_does_not_reflect() {
    let mut b = airborne_with(Vec3::new(0.0, 0.0, 2.0), 0);
    handle_all_collisions(&mut b, true, Vec3::Z, false, false, false);
    assert_eq!(b.velocity.z, 2.0);
}

#[test]
fn fsf2_zeroes_velocity_the_airborne_stuck_bleed() {
    let mut b = airborne_with(Vec3::new(0.0, 0.0, 18.0), 2);
    handle_all_collisions(
        &mut b,
        true,
        Vec3::new(-0.96, -0.25, -0.15),
        false,
        false,
        false,
    );
    assert_eq!(b.velocity, Vec3::ZERO);
}

#[test]
fn fsf3_zeroes_velocity_even_without_collision_normal() {
    let mut b = airborne_with(Vec3::new(1.0, 2.0, 18.0), 3);
    handle_all_collisions(&mut b, false, Vec3::ZERO, false, false, false);
    assert_eq!(b.velocity, Vec3::ZERO);
}

#[test]
fn staying_on_walkable_does_not_reflect_corridor_wall_slide_preserved() {
    let mut b = PhysicsBody::new();
    b.velocity = Vec3::new(3.0, 0.0, 0.0);
    b.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    handle_all_collisions(&mut b, true, -Vec3::X, true, true, true);
    assert_eq!(b.velocity.x, 3.0);
}

#[test]
fn inelastic_zeroes_instead_of_reflecting() {
    let mut b = airborne_with(Vec3::new(3.0, 0.0, 0.0), 0);
    b.state.insert(PhysicsState::INELASTIC);
    handle_all_collisions(&mut b, true, -Vec3::X, false, false, false);
    assert_eq!(b.velocity, Vec3::ZERO);
}

#[test]
fn landing_reflects_retail_rule_restored_not_suppressed() {
    let mut b = airborne_with(Vec3::new(0.0, 0.0, -5.0), 0);
    handle_all_collisions(&mut b, true, Vec3::Z, false, false, true);
    assert_eq_prec(0.25, b.velocity.z, 3);
}

#[test]
fn set_position_transition_hidden_landing_callback_runs_before_collision_response() {
    let mut b = airborne_with(Vec3::ZERO, 0);
    let mut calls = 0;
    let mut hit = |body: &mut PhysicsBody| {
        calls += 1;
        body.velocity = Vec3::new(0.0, 0.0, -4.0);
    };
    commit_set_position_transition(
        &mut b,
        true,
        true,
        true,
        Vec3::Z,
        false,
        false,
        Some(&mut hit),
        None,
        None,
        None,
    );
    assert_eq!(calls, 1);
    assert!(b.in_contact());
    assert!(b.on_walkable());
    assert_eq_prec(0.2, b.velocity.z, 3);
}

#[test]
fn set_position_transition_hidden_walk_off_callback_runs_before_collision_response() {
    let mut b = airborne_with(Vec3::ZERO, 0);
    b.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    let mut calls = 0;
    let mut leave = |body: &mut PhysicsBody| {
        calls += 1;
        body.velocity = Vec3::new(3.0, 0.0, 0.0);
    };
    commit_set_position_transition(
        &mut b,
        false,
        false,
        true,
        -Vec3::X,
        true,
        true,
        None,
        Some(&mut leave),
        None,
        None,
    );
    assert_eq!(calls, 1);
    assert!(!b.in_contact());
    assert!(!b.on_walkable());
    assert_eq_prec(-0.15, b.velocity.x, 3);
}
