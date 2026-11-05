//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/ProjectilePhysicsStepperTests.cs`
//! (including its `RetailFrameMath.SetVectorHeading` cases).

use glam::{Quat, Vec3};

use super::support::*;
use crate::body::PhysicsBody;
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::PhysicsEngine;
use crate::frame_math::set_vector_heading;
use crate::geometry::Plane;
use crate::polygon::ResolvedPolygon;
use crate::projectile::{
    ProjectileCollisionSphere, ProjectileError, advance, begin_quantum, complete_quantum,
};
use crate::shadow::ShadowCollisionType;
use crate::terrain::TerrainSurface;
use crate::world::CollisionWorld;
use crate::{PhysicsState, TransientState};

const LANDBLOCK: u32 = 0xA9B4_0000;
const CELL: u32 = 0xA9B4_0001;
const PROJECTILE_ID: u32 = 0x7000;

fn arrow() -> ProjectileCollisionSphere {
    ProjectileCollisionSphere::new(Vec3::ZERO, 0.10)
}

fn assert_vector_near(expected: Vec3, actual: Vec3, eps: f32) {
    let d = expected.distance(actual);
    assert!((0.0..=eps).contains(&d), "{expected} vs {actual} ({d})");
}

fn assert_quat_equivalent(expected: Quat, actual: Quat, eps: f32) {
    let dot = expected.dot(actual).abs();
    assert!(
        (1.0 - eps..=1.0 + eps).contains(&dot),
        "{expected} vs {actual}"
    );
}

fn make_body(
    position: Vec3,
    velocity: Vec3,
    state: PhysicsState,
    cell_id: u32,
    cell_local: Option<Vec3>,
) -> PhysicsBody {
    let mut b = PhysicsBody::new();
    b.state = state;
    b.set_position(position);
    b.orientation = Quat::IDENTITY;
    b.last_update_time = 0.0;
    b.snap_to_cell(cell_id, position, cell_local.unwrap_or(position));
    b.velocity = velocity;
    b.transient_state = TransientState::ACTIVE;
    b
}

fn body(position: Vec3, velocity: Vec3) -> PhysicsBody {
    make_body(position, velocity, PhysicsState::MISSILE, CELL, None)
}

fn body_state(position: Vec3, velocity: Vec3, state: PhysicsState) -> PhysicsBody {
    make_body(position, velocity, state, CELL, None)
}

fn flat(height: f32) -> TerrainSurface {
    TerrainSurface::new(&[0u8; 81], &[height; 256], 0, 0, None)
}

fn terrain_engine(height: f32) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(LANDBLOCK, flat(height), Vec::new(), Vec::new(), 0.0, 0.0);
    engine
}

fn empty_engine() -> PhysicsEngine {
    terrain_engine(-1000.0)
}

fn boundary_engine() -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    for (id, x, y) in [
        (0xA9B4_FFFF, 0.0, 0.0),
        (0xA8B4_FFFF, -192.0, 0.0),
        (0xAAB4_FFFF, 192.0, 0.0),
        (0xA9B3_FFFF, 0.0, -192.0),
        (0xA9B5_FFFF, 0.0, 192.0),
    ] {
        engine.add_landblock(id, flat(-1000.0), Vec::new(), Vec::new(), x, y);
    }
    engine
}

fn register_obstacle(
    engine: &mut PhysicsEngine,
    id: u32,
    position: Vec3,
    radius: f32,
    state: u32,
    flags: u8,
) {
    register_sphere(
        engine,
        id,
        position,
        radius,
        LANDBLOCK,
        state,
        EntityCollisionFlags(flags),
        0,
        state & PhysicsState::STATIC.bits() != 0,
    );
}

fn register_static_sphere(engine: &mut PhysicsEngine, id: u32, position: Vec3, radius: f32) {
    register_obstacle(engine, id, position, radius, PhysicsState::STATIC.bits(), 0);
}

fn register_thin_bsp_wall(engine: &mut PhysicsEngine, y: f32) {
    const GFX_ID: u32 = 0x0100_F001;
    const ENTITY_ID: u32 = 0x8600;
    let polygon = ResolvedPolygon::new(
        0,
        vec![
            Vec3::new(-2.0, 0.0, -2.0),
            Vec3::new(2.0, 0.0, -2.0),
            Vec3::new(2.0, 0.0, 2.0),
            Vec3::new(-2.0, 0.0, 2.0),
        ],
        Plane::new(Vec3::new(0.0, -1.0, 0.0), 0.0),
    );
    engine.world_mut().unwrap().insert_gfx_obj(gfx_obj(
        GFX_ID,
        single_leaf(Vec3::ZERO, 4.0, vec![polygon]),
        4.0,
    ));
    register_single(
        engine,
        ENTITY_ID,
        GFX_ID,
        Vec3::new(12.0, y, 2.0),
        Quat::IDENTITY,
        4.0,
        LANDBLOCK,
        ShadowCollisionType::Bsp,
        0.0,
        1.0,
        (PhysicsState::STATIC | PhysicsState::HAS_PHYSICS_BSP).bits(),
        EntityCollisionFlags::default(),
        0,
    );
}

fn run(
    engine: &PhysicsEngine,
    b: &mut PhysicsBody,
    t: f64,
) -> crate::projectile::ProjectileAdvanceResult {
    advance(engine, b, t, CELL, arrow(), PROJECTILE_ID, 0, false).unwrap()
}

#[test]
fn collision_sphere_scales_offset_and_radius_without_centering_force_bolt() {
    let s = ProjectileCollisionSphere {
        setup_local_origin: Vec3::new(0.0, -0.165, 0.1045),
        setup_radius: 0.102,
        object_scale: 2.0,
    };
    assert_eq!(s.local_origin(), Vec3::new(0.0, -0.33, 0.209));
    assert_eq_prec(0.204, s.radius(), 5);
    assert!(s.is_valid());
}

#[test]
fn collision_sphere_rejects_invalid_computed_shape() {
    let s = |o: Vec3, r: f32, k: f32| ProjectileCollisionSphere {
        setup_local_origin: o,
        setup_radius: r,
        object_scale: k,
    };
    assert!(!s(Vec3::ZERO, f32::MAX, 2.0).is_valid());
    assert!(!s(Vec3::new(f32::MAX, 0.0, 0.0), 0.1, 2.0).is_valid());
    assert!(!s(Vec3::ZERO, 0.0003, 0.5).is_valid());
    assert!(!s(Vec3::ZERO, 0.1, -1.0).is_valid());
    assert!(!s(Vec3::ONE, -0.1, -1.0).is_valid());
}

#[test]
fn set_vector_heading_maps_local_forward_to_full_3d_direction() {
    for (x, y, z) in [
        (0.0, 1.0, 0.0),
        (1.0, 0.0, 0.0),
        (0.0, 0.0, 1.0),
        (0.0, 0.0, -1.0),
        (1.0, 2.0, 3.0),
    ] {
        let direction = Vec3::new(x, y, z).normalize();
        let orientation = set_vector_heading(Quat::IDENTITY, direction);
        assert_vector_near(direction, orientation * Vec3::Y, 0.0001);
    }
}

#[test]
fn set_vector_heading_zero_direction_preserves_orientation() {
    let original = Quat::from_axis_angle(Vec3::Z, 0.7);
    assert_eq!(set_vector_heading(original, Vec3::ZERO), original);
}

#[test]
fn set_vector_heading_nearly_vertical_preserves_nonzero_compass() {
    for (x, y, z) in [(0.0001, 0.0, 1.0), (0.000_001, -0.000_002, -1.0)] {
        let expected = Vec3::new(x, y, z).normalize();
        let orientation = set_vector_heading(Quat::IDENTITY, expected);
        assert_vector_near(expected, orientation * Vec3::Y, 0.000_001);
    }
}

#[test]
fn advance_clamps_velocity_to_fifty_before_integrating() {
    let engine = empty_engine();
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::new(0.0, 100.0, 0.0));
    let result = run(&engine, &mut b, 0.04);
    assert!(result.simulated);
    assert_eq_prec(50.0, b.velocity.length(), 4);
    assert_eq_prec(12.0, b.position().y, 3);
    assert_eq_prec(50.0, b.cached_velocity.y, 3);
}

#[test]
fn split_quantum_holds_begin_frame_across_hook_slot_then_commits_candidate() {
    let engine = empty_engine();
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::new(10.0, 0.0, 0.0));
    let begin = b.position();
    let preparation = begin_quantum(&mut b, 0.1, CELL, arrow(), false).unwrap();
    assert!(preparation.simulated);
    assert!(preparation.requires_transition);
    assert_eq!(b.position(), begin);
    assert_eq!(preparation.candidate_position, begin + Vec3::X);
    let result = complete_quantum(&engine, &mut b, &preparation, arrow(), PROJECTILE_ID, 0);
    assert!(result.simulated);
    assert!((begin + Vec3::X).distance(b.position()) <= 0.00001);
}

#[test]
fn advance_gravity_uses_final_physics_state_not_parsed_acceleration() {
    let engine = empty_engine();
    let mut b = body_state(
        Vec3::new(12.0, 10.0, 10.0),
        Vec3::new(0.0, 10.0, 0.0),
        PhysicsState::MISSILE | PhysicsState::GRAVITY,
    );
    b.acceleration = Vec3::new(100.0, 200.0, 300.0);
    run(&engine, &mut b, 0.04);
    assert_eq_prec(12.0, b.position().x, 4);
    assert_eq_prec(10.4, b.position().y, 4);
    assert_eq_prec(
        10.0 + 0.5 * PhysicsBody::GRAVITY * 0.04 * 0.04,
        b.position().z,
        4,
    );
    assert_eq_prec(PhysicsBody::GRAVITY * 0.04, b.velocity.z, 4);
}

#[test]
fn advance_no_gravity_clears_stale_acceleration() {
    let engine = empty_engine();
    let mut b = body(Vec3::new(12.0, 10.0, 10.0), Vec3::new(0.0, 10.0, 0.0));
    b.acceleration = Vec3::new(100.0, 200.0, 300.0);
    run(&engine, &mut b, 0.04);
    assert_eq!(b.acceleration, Vec3::ZERO);
    assert_eq_prec(10.0, b.position().z, 4);
}

#[test]
fn advance_align_path_uses_actual_three_dimensional_displacement() {
    for (x, y, z) in [
        (0.0, 10.0, 0.0),
        (10.0, 0.0, 0.0),
        (0.0, 0.0, 10.0),
        (3.0, 4.0, 5.0),
    ] {
        let engine = empty_engine();
        let velocity = Vec3::new(x, y, z);
        let mut b = body_state(
            Vec3::new(12.0, 10.0, 10.0),
            velocity,
            PhysicsState::MISSILE | PhysicsState::ALIGN_PATH,
        );
        run(&engine, &mut b, 0.04);
        assert_vector_near(velocity.normalize(), b.orientation * Vec3::Y, 0.0002);
    }
}

#[test]
fn update_physics_internal_angular_velocity_premultiplies_in_world_space() {
    let mut b = PhysicsBody::new();
    b.orientation = Quat::from_axis_angle(Vec3::X, 0.5);
    b.omega = Vec3::new(0.0, 0.0, 2.0);
    let expected = (Quat::from_axis_angle(Vec3::Z, 0.2) * b.orientation).normalize();
    b.update_physics_internal(0.1);
    assert_quat_equivalent(expected, b.orientation, 0.0001);
}

#[test]
fn advance_rotating_projectile_without_align_path_keeps_omega_spin() {
    let engine = empty_engine();
    let mut b = body(Vec3::new(12.0, 10.0, 10.0), Vec3::new(0.0, 2.0, 0.0));
    b.omega = Vec3::new(0.0, 0.0, 4.0);
    run(&engine, &mut b, 0.04);
    assert_quat_equivalent(Quat::from_axis_angle(Vec3::Z, 0.16), b.orientation, 0.0001);
}

#[test]
fn advance_exclusions_clear_active_and_do_not_move() {
    let engine = empty_engine();
    let mut cases = [
        (body(Vec3::new(12.0, 10.0, 2.0), Vec3::Y), true),
        (
            body_state(
                Vec3::new(12.0, 10.0, 2.0),
                Vec3::Y,
                PhysicsState::MISSILE | PhysicsState::FROZEN,
            ),
            false,
        ),
        (
            body_state(
                Vec3::new(12.0, 10.0, 2.0),
                Vec3::Y,
                PhysicsState::MISSILE | PhysicsState::STATIC,
            ),
            false,
        ),
    ];
    cases[2].0.in_world = true;
    for (b, parented) in &mut cases {
        let before = b.position();
        let result = advance(&engine, b, 0.04, CELL, arrow(), PROJECTILE_ID, 0, *parented).unwrap();
        assert!(!result.simulated);
        assert!(!b.is_active());
        assert_eq!(b.position(), before);
    }
    let mut out_of_world = body(Vec3::new(12.0, 10.0, 2.0), Vec3::Y);
    out_of_world.in_world = false;
    assert!(!run(&engine, &mut out_of_world, 0.04).simulated);
    assert!(!out_of_world.is_active());
}

#[test]
fn advance_accumulates_small_remainder_until_retail_minimum_quantum() {
    let engine = empty_engine();
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::Y);
    let first = run(&engine, &mut b, 0.02);
    assert!(!first.simulated);
    assert_eq!(b.last_update_time, 0.0);
    let second = run(&engine, &mut b, 0.04);
    assert!(second.simulated);
    assert_eq_prec(10.04, b.position().y, 4);
    assert!((b.last_update_time - 0.04).abs() < 1e-8);
}

#[test]
fn advance_exact_minimum_quantum_remains_accumulated() {
    let engine = empty_engine();
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::Y);
    let result = run(&engine, &mut b, f64::from(PhysicsBody::MIN_QUANTUM));
    assert!(!result.simulated);
    assert_eq!(b.last_update_time, 0.0);
    assert_eq!(b.position().y, 10.0);
}

#[test]
fn advance_exact_maximum_quantum_uses_one_quantum() {
    let engine = empty_engine();
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::Y);
    let result = run(&engine, &mut b, f64::from(PhysicsBody::MAX_QUANTUM));
    assert_eq!(result.quantum_count, 1);
    assert_eq_prec(10.2, b.position().y, 4);
    assert!((b.last_update_time - f64::from(PhysicsBody::MAX_QUANTUM)).abs() < 1e-7);
}

#[test]
fn advance_exact_huge_quantum_is_still_simulated() {
    let engine = empty_engine();
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::Y);
    let result = run(&engine, &mut b, f64::from(PhysicsBody::HUGE_QUANTUM));
    assert_eq!(result.quantum_count, 10);
    assert_eq_prec(12.0, b.position().y, 3);
    assert!((b.last_update_time - f64::from(PhysicsBody::HUGE_QUANTUM)).abs() < 1e-7);
}

#[test]
fn advance_non_finite_clock_is_rejected_without_mutation() {
    for t in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
        let engine = empty_engine();
        let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::Y);
        let before = b.position();
        assert_eq!(
            advance(&engine, &mut b, t, CELL, arrow(), PROJECTILE_ID, 0, false),
            Err(ProjectileError::ClockNotFinite)
        );
        assert_eq!(b.position(), before);
        assert_eq!(b.last_update_time, 0.0);
    }
}

#[test]
fn advance_non_finite_prior_clock_is_rejected_without_mutation() {
    for prior in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
        let engine = empty_engine();
        let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::Y);
        b.last_update_time = prior;
        let before = b.position();
        assert_eq!(
            advance(&engine, &mut b, 1.0, CELL, arrow(), PROJECTILE_ID, 0, false),
            Err(ProjectileError::LastUpdateNotFinite)
        );
        assert_eq!(b.position(), before);
        assert!(b.last_update_time == prior || (prior.is_nan() && b.last_update_time.is_nan()));
    }
}

#[test]
fn advance_catchup_uses_point_two_second_quanta_and_retains_no_large_remainder() {
    let engine = empty_engine();
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::new(0.0, 50.0, 0.0));
    let result = run(&engine, &mut b, 0.45);
    assert_eq!(result.quantum_count, 3);
    assert_eq_prec(32.5, b.position().y, 3);
    assert!((b.last_update_time - 0.45).abs() < 1e-8);
}

#[test]
fn advance_gap_above_two_seconds_is_discarded() {
    let engine = empty_engine();
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::Y);
    let result = run(&engine, &mut b, 2.01);
    assert!(!result.simulated);
    assert_eq!(b.position(), Vec3::new(12.0, 10.0, 2.0));
    assert!((b.last_update_time - 2.01).abs() < 1e-8);
}

#[test]
fn sweep_at_maximum_speed_does_not_tunnel_through_thin_sphere() {
    let mut engine = empty_engine();
    register_static_sphere(&mut engine, 0x8100, Vec3::new(12.0, 11.0, 2.0), 0.01);
    let mut b = body_state(
        Vec3::new(12.0, 10.0, 2.0),
        Vec3::new(0.0, 50.0, 0.0),
        PhysicsState::MISSILE | PhysicsState::INELASTIC,
    );
    let result = run(&engine, &mut b, 0.04);
    assert!(result.collision_normal_valid);
    assert!(b.position().y < 11.1, "tunneled to {}", b.position().y);
    assert_eq!(b.velocity, Vec3::ZERO);
}

#[test]
fn sweep_elastic_projectile_reflects_from_object_surface() {
    let mut engine = empty_engine();
    register_static_sphere(&mut engine, 0x8101, Vec3::new(12.0, 11.0, 2.0), 0.01);
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::new(0.0, 50.0, 0.0));
    b.elasticity = 0.5;
    let result = run(&engine, &mut b, 0.04);
    assert!(result.collision_normal_valid);
    assert_eq_prec(-25.0, b.velocity.y, 2);
}

#[test]
fn sweep_downward_projectile_collides_with_terrain_floor() {
    let engine = terrain_engine(0.0);
    let mut b = body_state(
        Vec3::new(12.0, 10.0, 0.3),
        Vec3::new(0.0, 0.0, -10.0),
        PhysicsState::MISSILE | PhysicsState::INELASTIC,
    );
    let result = run(&engine, &mut b, 0.04);
    assert!(result.collision_normal_valid);
    assert!(b.position().z >= 0.09);
    assert_eq!(b.velocity, Vec3::ZERO);
    assert!(b.in_contact());
    assert!(b.on_walkable());

    let settled = b.position();
    let settle_tick = run(&engine, &mut b, 0.08);
    assert!(settle_tick.simulated);
    assert_eq!(b.position(), settled);
    assert!(!b.is_active());

    let inactive_tick = run(&engine, &mut b, 0.12);
    assert!(!inactive_tick.simulated);
    assert_eq!(b.position(), settled);
}

#[test]
fn sweep_failed_transition_keeps_candidate_frame_and_current_cell() {
    let engine = boundary_engine();
    let start = Vec3::new(10.0, 191.0, 50.0);
    let mut b = make_body(
        start,
        Vec3::new(0.0, 50.0, 0.0),
        PhysicsState::MISSILE | PhysicsState::INELASTIC,
        0xA9B4_0008,
        Some(start),
    );
    b.sliding_normal = -Vec3::Y;
    b.transient_state
        .insert(TransientState::SLIDING | TransientState::STATIONARY_STOP);
    b.frames_stationary_fall = 2;
    b.contact_plane_valid = true;
    b.contact_plane = Plane::new(Vec3::Z, -50.0);
    b.contact_plane_cell_id = 0xA9B4_0008;
    b.contact_plane_is_water = true;
    b.walkable_polygon_valid = true;
    b.walkable_plane = Plane::new(Vec3::Z, -50.0);
    b.walkable_vertices = Some(vec![
        Vec3::new(0.0, 0.0, 50.0),
        Vec3::new(1.0, 0.0, 50.0),
        Vec3::new(0.0, 1.0, 50.0),
    ]);
    b.walkable_up = Vec3::Z;
    let prior_plane = b.contact_plane;
    let prior_vertices = b.walkable_vertices.clone();
    let prior_transient = b.transient_state;

    let result = advance(
        &engine,
        &mut b,
        0.04,
        0xA9B4_0008,
        arrow(),
        PROJECTILE_ID,
        0,
        false,
    )
    .unwrap();
    assert!(!result.transition_ok);
    assert_eq!(result.cell_id, 0xA9B4_0008);
    assert_eq_prec(193.0, b.position().y, 3);
    assert_eq!(b.cell_position().obj_cell_id, 0xA9B4_0008);
    assert_eq_prec(193.0, b.cell_position().frame.origin.y, 3);
    assert_eq!(b.cached_velocity, Vec3::ZERO);
    assert_eq!(b.velocity.y, 50.0);
    assert!(b.contact_plane_valid);
    assert_eq!(b.contact_plane, prior_plane);
    assert_eq!(b.contact_plane_cell_id, 0xA9B4_0008);
    assert!(b.contact_plane_is_water);
    assert!(b.walkable_polygon_valid);
    assert_eq!(b.walkable_vertices, prior_vertices);
    assert_eq!(b.walkable_up, Vec3::Z);
    assert_eq!(b.frames_stationary_fall, 2);
    assert_eq!(b.transient_state, prior_transient);
}

#[test]
fn sweep_self_shadow_is_excluded() {
    let mut engine = empty_engine();
    register_static_sphere(&mut engine, PROJECTILE_ID, Vec3::new(12.0, 11.0, 2.0), 0.01);
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::new(0.0, 50.0, 0.0));
    run(&engine, &mut b, 0.04);
    assert_eq_prec(12.0, b.position().y, 3);
}

#[test]
fn sweep_other_missile_is_ignored() {
    let mut engine = empty_engine();
    register_obstacle(
        &mut engine,
        0x8200,
        Vec3::new(12.0, 11.0, 2.0),
        0.01,
        PhysicsState::MISSILE.bits(),
        EntityCollisionFlags::HAS_WEENIE,
    );
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::new(0.0, 50.0, 0.0));
    run(&engine, &mut b, 0.04);
    assert_eq_prec(12.0, b.position().y, 3);
}

#[test]
fn sweep_ethereal_weenie_is_ignored() {
    let mut engine = empty_engine();
    register_obstacle(
        &mut engine,
        0x8300,
        Vec3::new(12.0, 11.0, 2.0),
        0.01,
        PhysicsState::ETHEREAL.bits(),
        EntityCollisionFlags::HAS_WEENIE,
    );
    let mut b = body(Vec3::new(12.0, 10.0, 2.0), Vec3::new(0.0, 50.0, 0.0));
    run(&engine, &mut b, 0.04);
    assert_eq_prec(12.0, b.position().y, 3);
}

#[test]
fn sweep_with_known_target_ignores_other_creature_and_hits_designated_creature() {
    let mut engine = empty_engine();
    let target_id = 0x8401;
    let creature = EntityCollisionFlags::HAS_WEENIE | EntityCollisionFlags::IS_CREATURE;
    register_obstacle(
        &mut engine,
        0x8400,
        Vec3::new(12.0, 10.8, 2.0),
        0.05,
        0,
        creature,
    );
    register_obstacle(
        &mut engine,
        target_id,
        Vec3::new(12.0, 11.5, 2.0),
        0.05,
        0,
        creature,
    );
    let mut b = body_state(
        Vec3::new(12.0, 10.0, 2.0),
        Vec3::new(0.0, 50.0, 0.0),
        PhysicsState::MISSILE | PhysicsState::INELASTIC,
    );
    let result = advance(
        &engine,
        &mut b,
        0.04,
        CELL,
        arrow(),
        PROJECTILE_ID,
        target_id,
        false,
    )
    .unwrap();
    assert!(result.collision_normal_valid);
    assert!(b.position().y > 10.8);
    assert!(b.position().y < 11.6);
}

#[test]
fn sweep_unknown_target_collides_with_creature() {
    let mut engine = empty_engine();
    register_obstacle(
        &mut engine,
        0x8500,
        Vec3::new(12.0, 11.0, 2.0),
        0.05,
        0,
        EntityCollisionFlags::HAS_WEENIE | EntityCollisionFlags::IS_CREATURE,
    );
    let mut b = body_state(
        Vec3::new(12.0, 10.0, 2.0),
        Vec3::new(0.0, 50.0, 0.0),
        PhysicsState::MISSILE | PhysicsState::INELASTIC,
    );
    assert!(run(&engine, &mut b, 0.04).collision_normal_valid);
}

#[test]
fn sweep_thin_bsp_wall_stops_inelastic_projectile() {
    let mut engine = empty_engine();
    register_thin_bsp_wall(&mut engine, 11.0);
    let mut b = body_state(
        Vec3::new(12.0, 10.0, 2.0),
        Vec3::new(0.0, 50.0, 0.0),
        PhysicsState::MISSILE | PhysicsState::INELASTIC,
    );
    let result = run(&engine, &mut b, 0.04);
    assert!(result.collision_normal_valid);
    assert!(b.position().y < 11.1);
    assert_eq!(b.velocity, Vec3::ZERO);
}

#[test]
fn sweep_crosses_loaded_landblock_boundary_in_every_direction() {
    for (sx, sy, vx, vy, start_cell, expected_cell) in [
        (191.0, 10.0, 40.0, 0.0, 0xA9B4_0039, 0xAAB4_0001),
        (1.0, 10.0, -40.0, 0.0, 0xA9B4_0001, 0xA8B4_0039),
        (10.0, 191.0, 0.0, 40.0, 0xA9B4_0008, 0xA9B5_0001),
        (10.0, 1.0, 0.0, -40.0, 0xA9B4_0001, 0xA9B3_0008),
    ] {
        let engine = boundary_engine();
        let start = Vec3::new(sx, sy, 50.0);
        let mut b = make_body(
            start,
            Vec3::new(vx, vy, 0.0),
            PhysicsState::MISSILE,
            start_cell,
            Some(start),
        );
        let result = advance(
            &engine,
            &mut b,
            0.05,
            start_cell,
            ProjectileCollisionSphere::new(Vec3::ZERO, 0.5),
            PROJECTILE_ID,
            0,
            false,
        )
        .unwrap();
        assert_eq!(result.cell_id, expected_cell, "from {start_cell:#010X}");
        assert_eq_prec(sx + vx * 0.05, b.position().x, 3);
        assert_eq_prec(sy + vy * 0.05, b.position().y, 3);
    }
}
