//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/BSPStepUpTests.cs`
//! with the fixtures of `BSPStepUpFixtures.cs`.

use glam::{Quat, Vec3};

use super::support::*;
use crate::TransientState;
use crate::body::PhysicsBody;
use crate::bsp::PhysicsBsp;
use crate::engine::{PhysicsEngine, ResolveRequest};
use crate::geometry::{Plane, Sphere};
use crate::globals::{EPSILON, FLOOR_Z, LANDING_Z};
use crate::state::{ObjectInfoState, TransitionState};
use crate::transition::Transition;

pub(crate) const LOW_STEP_FLOOR_ID: u16 = 10;
pub(crate) const LOW_STEP_WALL_ID: u16 = 11;
pub(crate) const LOW_STEP_UPPER_FLOOR_ID: u16 = 12;
pub(crate) const TALL_WALL_FLOOR_ID: u16 = 20;
pub(crate) const TALL_WALL_WALL_ID: u16 = 21;
pub(crate) const FLAT_ROOF_FLOOR_ID: u16 = 30;
pub(crate) const FLAT_ROOF_ROOF_ID: u16 = 31;
pub(crate) const SLOPED_FLOOR_ID: u16 = 40;
pub(crate) const SLOPED_SLOPE_ID: u16 = 41;

/// Sphere radius used in every test.
pub(crate) const SPHERE_RADIUS: f32 = 0.2;

const SYNTHETIC_GFX_ID: u32 = 0xDEAD_BEEF;

fn v(x: f32, y: f32, z: f32) -> Vec3 {
    Vec3::new(x, y, z)
}

/// A floor at z 0 up to x 0.5, a 0.25 m step face at x 0.5 and an upper
/// floor at z 0.25.
pub(crate) fn low_step() -> PhysicsBsp {
    single_leaf(
        Vec3::ZERO,
        10.0,
        vec![
            floor(
                LOW_STEP_FLOOR_ID,
                &[
                    v(-2.0, -1.0, 0.0),
                    v(0.5, -1.0, 0.0),
                    v(0.5, 1.0, 0.0),
                    v(-2.0, 1.0, 0.0),
                ],
            ),
            quad(
                LOW_STEP_WALL_ID,
                &[
                    v(0.5, -1.0, 0.0),
                    v(0.5, -1.0, 0.25),
                    v(0.5, 1.0, 0.25),
                    v(0.5, 1.0, 0.0),
                ],
                v(-1.0, 0.0, 0.0),
            ),
            floor(
                LOW_STEP_UPPER_FLOOR_ID,
                &[
                    v(0.2, -1.0, 0.25),
                    v(2.0, -1.0, 0.25),
                    v(2.0, 1.0, 0.25),
                    v(0.2, 1.0, 0.25),
                ],
            ),
        ],
    )
}

/// A floor and a 5 m wall at x 0.5 with nothing beyond.
pub(crate) fn tall_wall() -> PhysicsBsp {
    single_leaf(
        v(0.0, 0.0, 2.5),
        10.0,
        vec![
            floor(
                TALL_WALL_FLOOR_ID,
                &[
                    v(-2.0, -1.0, 0.0),
                    v(0.5, -1.0, 0.0),
                    v(0.5, 1.0, 0.0),
                    v(-2.0, 1.0, 0.0),
                ],
            ),
            quad(
                TALL_WALL_WALL_ID,
                &[
                    v(0.5, -1.0, 0.0),
                    v(0.5, -1.0, 5.0),
                    v(0.5, 1.0, 5.0),
                    v(0.5, 1.0, 0.0),
                ],
                v(-1.0, 0.0, 0.0),
            ),
        ],
    )
}

/// A ground floor and a flat roof at z 3.
pub(crate) fn flat_roof() -> PhysicsBsp {
    single_leaf(
        v(0.0, 0.0, 1.5),
        10.0,
        vec![
            floor(
                FLAT_ROOF_FLOOR_ID,
                &[
                    v(-2.0, -1.0, 0.0),
                    v(2.0, -1.0, 0.0),
                    v(2.0, 1.0, 0.0),
                    v(-2.0, 1.0, 0.0),
                ],
            ),
            floor(
                FLAT_ROOF_ROOF_ID,
                &[
                    v(-2.0, -1.0, 3.0),
                    v(2.0, -1.0, 3.0),
                    v(2.0, 1.0, 3.0),
                    v(-2.0, 1.0, 3.0),
                ],
            ),
        ],
    )
}

/// A floor and a 63° slope rising from x 0 to x 1 (too steep to walk).
pub(crate) fn sloped_unwalkable() -> PhysicsBsp {
    let (v0, v1, v2, v3) = (
        v(0.0, -1.0, 0.0),
        v(1.0, -1.0, 2.0),
        v(1.0, 1.0, 2.0),
        v(0.0, 1.0, 0.0),
    );
    let mut slope_normal = (v1 - v0).cross(v3 - v0).normalize();
    if slope_normal.x > 0.0 {
        slope_normal = -slope_normal;
    }
    single_leaf(
        v(0.5, 0.0, 1.0),
        10.0,
        vec![
            floor(
                SLOPED_FLOOR_ID,
                &[
                    v(-2.0, -1.0, 0.0),
                    v(0.0, -1.0, 0.0),
                    v(0.0, 1.0, 0.0),
                    v(-2.0, 1.0, 0.0),
                ],
            ),
            quad(SLOPED_SLOPE_ID, &[v0, v1, v2, v3], slope_normal),
        ],
    )
}

pub(crate) fn make_grounded_transition(from: Vec3, to: Vec3, step_up_height: f32) -> Transition {
    make_grounded_transition_in(from, to, step_up_height, 0xA9B4_0001)
}

pub(crate) fn make_grounded_transition_in(
    from: Vec3,
    to: Vec3,
    step_up_height: f32,
    cell_id: u32,
) -> Transition {
    let mut t = Transition::new();
    t.sphere_path
        .init_path_simple(from, to, cell_id, SPHERE_RADIUS);
    t.object_info.state = ObjectInfoState(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE);
    t.object_info.step_up_height = step_up_height;
    t.object_info.step_down_height = 0.04;
    t.object_info.step_down = true;
    t.collision_info.last_known_contact_plane = Plane::new(Vec3::Z, 0.0);
    t.collision_info.last_known_contact_plane_valid = true;
    t
}

pub(crate) fn make_airborne_transition(from: Vec3, to: Vec3) -> Transition {
    let mut t = Transition::new();
    t.sphere_path
        .init_path_simple(from, to, 0xA9B4_0001, SPHERE_RADIUS);
    t.object_info.state = ObjectInfoState(ObjectInfoState::NONE);
    t.object_info.step_up_height = 0.04;
    t.object_info.step_down_height = 0.04;
    t.object_info.step_down = false;
    t
}

pub(crate) fn make_test_engine(bsp: PhysicsBsp, terrain_z: f32) -> PhysicsEngine {
    engine_with_object(gfx_obj(SYNTHETIC_GFX_ID, bsp, 15.0), Vec3::ZERO, terrain_z)
}

fn sphere(center: Vec3) -> Sphere {
    Sphere::new(center, SPHERE_RADIUS)
}

// ── Group A: baselines ─────────────────────────────────────────────────

#[test]
fn a1_null_root_returns_ok() {
    let from = v(0.0, 0.0, SPHERE_RADIUS);
    let to = v(0.1, 0.0, SPHERE_RADIUS);
    let mut t = make_grounded_transition(from, to, 0.30);
    let result = bsp_find(
        &PhysicsBsp::default(),
        &mut t,
        sphere(to),
        None,
        from,
        Vec3::Z,
        1.0,
        Quat::IDENTITY,
        None,
    );
    assert_eq!(result, TransitionState::Ok);
}

#[test]
fn a2_grounded_mover_no_wall_near_returns_ok() {
    let bsp = low_step();
    let from = v(-1.0, 0.0, SPHERE_RADIUS);
    let to = v(-1.5, 0.0, SPHERE_RADIUS);
    let mut t = make_grounded_transition(from, to, 0.30);
    let result = bsp_find(
        &bsp,
        &mut t,
        sphere(to),
        None,
        from,
        Vec3::Z,
        1.0,
        Quat::IDENTITY,
        None,
    );
    assert_eq!(result, TransitionState::Ok);
}

#[test]
fn a3_airborne_mover_above_roof_returns_ok() {
    let bsp = flat_roof();
    let high_z = 6.0;
    let from = v(0.0, 0.0, high_z + SPHERE_RADIUS);
    let to = v(0.0, 0.0, high_z + SPHERE_RADIUS - 0.01);
    let mut t = make_airborne_transition(from, to);
    let result = bsp_find(
        &bsp,
        &mut t,
        sphere(to),
        None,
        from,
        Vec3::Z,
        1.0,
        Quat::IDENTITY,
        None,
    );
    assert_eq!(result, TransitionState::Ok);
}

#[test]
fn a4_sloped_fixture_normal_below_floor_z() {
    let bsp = sloped_unwalkable();
    let slope = bsp.polygon(SLOPED_SLOPE_ID).unwrap();
    assert!(slope.plane.normal.z < FLOOR_Z);
    assert!(slope.plane.normal.z > 0.0);
}

#[test]
fn a5_low_step_upper_floor_normal_above_floor_z() {
    let bsp = low_step();
    let upper = bsp.polygon(LOW_STEP_UPPER_FLOOR_ID).unwrap();
    assert!(upper.plane.normal.z >= FLOOR_Z);
}

#[test]
fn a6_flat_roof_polygon_normal_above_landing_z() {
    let bsp = flat_roof();
    let roof = bsp.polygon(FLAT_ROOF_ROOF_ID).unwrap();
    assert!(roof.plane.normal.z >= LANDING_Z);
}

// ── Group B: grounded step-up ──────────────────────────────────────────

#[test]
fn b1_grounded_mover_low_step_steps_up() {
    let from = v(0.1, 0.0, 0.0);
    let to = v(0.6, 0.0, 0.0);
    let mut t = make_grounded_transition(from, to, 0.30);
    let engine = make_test_engine(low_step(), 0.0);
    let _ = t.find_transitional_position(&engine);
    let expected_min_z = 0.25 - EPSILON * 10.0;
    assert!(
        t.sphere_path.cur_pos.z >= expected_min_z,
        "expected z >= {expected_min_z} (stepped up), got {}",
        t.sphere_path.cur_pos.z
    );
}

#[test]
fn b2_grounded_mover_tall_wall_blocked_or_slides() {
    let from = v(0.1, 0.0, 0.0);
    let to = v(0.6, 0.0, 0.0);
    let mut t = make_grounded_transition(from, to, 0.04);
    let engine = make_test_engine(tall_wall(), 0.0);
    let _ = t.find_transitional_position(&engine);
    let wall_face = 0.5 - SPHERE_RADIUS;
    assert!(
        t.sphere_path.cur_pos.x <= wall_face + EPSILON * 20.0,
        "expected blocked before the wall, got x {}",
        t.sphere_path.cur_pos.x
    );
}

#[test]
fn b3_path5_direct_call_contact_hits_low_wall_not_slid() {
    let bsp = low_step();
    let r = SPHERE_RADIUS;
    let check_pos = v(0.5 - r * 0.5, 0.0, r);
    let curr_pos = v(0.1, 0.0, r);
    let mut t = Transition::new();
    t.sphere_path
        .init_path_simple(curr_pos, check_pos, 0xA9B4_0001, r);
    t.sphere_path.set_check_pos(check_pos, 0xA9B4_0001);
    t.object_info.state = ObjectInfoState(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE);
    t.object_info.step_up_height = 0.30;
    t.object_info.step_down_height = 0.04;
    t.collision_info.last_known_contact_plane = Plane::new(Vec3::Z, 0.0);
    t.collision_info.last_known_contact_plane_valid = true;
    let engine = make_test_engine(low_step(), 0.0);
    let result = bsp_find(
        &bsp,
        &mut t,
        sphere(check_pos),
        None,
        curr_pos,
        Vec3::Z,
        1.0,
        Quat::IDENTITY,
        Some(&engine),
    );
    assert_ne!(result, TransitionState::Slid);
}

// ── Group C: airborne landing ──────────────────────────────────────────

#[test]
fn c1_path6_airborne_mover_hits_roof_sets_collide_flag_and_adjusted() {
    let bsp = flat_roof();
    let r = SPHERE_RADIUS;
    let check_pos = v(0.0, 0.0, 3.0 + r * 0.5);
    let curr_pos = v(0.0, 0.0, 3.0 + r + 0.1);
    let mut t = Transition::new();
    t.sphere_path
        .init_path_simple(curr_pos, check_pos, 0xA9B4_0001, r);
    t.sphere_path.set_check_pos(check_pos, 0xA9B4_0001);
    t.object_info.state = ObjectInfoState(ObjectInfoState::NONE);
    let result = bsp_find(
        &bsp,
        &mut t,
        sphere(check_pos),
        None,
        curr_pos,
        Vec3::Z,
        1.0,
        Quat::IDENTITY,
        None,
    );
    assert_eq!(result, TransitionState::Adjusted);
    assert!(t.sphere_path.collide);
    assert_near(LANDING_Z, t.sphere_path.walk.walkable_allowance, 1e-5);
}

#[test]
fn c2_airborne_mover_lands_on_flat_roof_contact_plane_set() {
    let roof_z = 3.0;
    let r = SPHERE_RADIUS;
    let from = v(0.0, 0.0, roof_z - r + 0.3);
    let to = v(0.0, 0.0, roof_z - r - 0.05);
    let mut t = make_airborne_transition(from, to);
    let engine = make_test_engine(flat_roof(), -50.0);
    let _ = t.find_transitional_position(&engine);
    let ci = &t.collision_info;
    let plane_set = ci.contact_plane_valid || ci.last_known_contact_plane_valid;
    assert!(
        plane_set,
        "expected a contact plane after landing on the roof"
    );
    let plane = if ci.contact_plane_valid {
        ci.contact_plane
    } else {
        ci.last_known_contact_plane
    };
    assert!(plane.normal.z >= LANDING_Z);
}

#[test]
fn c3_path6_airborne_mover_hits_steep_slope_defers_through_set_collide() {
    let bsp = sloped_unwalkable();
    let r = SPHERE_RADIUS;
    let check_pos = v(0.5, 0.0, 1.0 + r * 0.5);
    let curr_pos = v(0.5, 0.0, 1.0 + r + 0.1);
    let mut t = Transition::new();
    t.sphere_path
        .init_path_simple(curr_pos, check_pos, 0xA9B4_0001, r);
    t.sphere_path.set_check_pos(check_pos, 0xA9B4_0001);
    t.object_info.state = ObjectInfoState(ObjectInfoState::NONE);
    let result = bsp_find(
        &bsp,
        &mut t,
        sphere(check_pos),
        None,
        curr_pos,
        Vec3::Z,
        1.0,
        Quat::IDENTITY,
        None,
    );
    assert_eq!(result, TransitionState::Adjusted);
    assert!(t.sphere_path.collide);
    assert_eq!(t.sphere_path.walk.walkable_allowance, LANDING_Z);
    assert!(!t.collision_info.collision_normal_valid);
    assert!(!t.collision_info.sliding_normal_valid);
}

// ── Group D: failed step-ups ───────────────────────────────────────────

#[test]
fn d1_grounded_mover_too_tall_wall_preserves_contact_plane() {
    let mut t = make_grounded_transition(v(0.1, 0.0, 0.0), v(0.6, 0.0, 0.0), 0.04);
    let engine = make_test_engine(tall_wall(), 0.0);
    let _ = t.find_transitional_position(&engine);
    let still_grounded = t.collision_info.contact_plane_valid
        || t.collision_info.last_known_contact_plane_valid
        || t.object_info.state.contains(ObjectInfoState::ON_WALKABLE);
    assert!(still_grounded);
}

#[test]
fn d1b_last_known_plane_collision_recovery_kills_velocity() {
    let mut t = make_grounded_transition(v(0.1, 0.0, 0.0), v(0.6, 0.0, 0.0), 0.04);
    let engine = make_test_engine(tall_wall(), 0.0);
    let _ = t.find_transitional_position(&engine);
    assert!(t.object_info.velocity_killed);
}

#[test]
fn d1c_last_known_plane_clean_advance_does_not_kill_or_reground() {
    let mut t = make_airborne_transition(v(-2.0, 0.0, 2.0), v(-2.0, 0.0, 2.25));
    t.collision_info.last_known_contact_plane = Plane::new(Vec3::Z, -1.52);
    t.collision_info.last_known_contact_plane_valid = true;
    let engine = make_test_engine(tall_wall(), -50.0);
    let _ = t.find_transitional_position(&engine);
    assert!(!t.object_info.velocity_killed);
    assert!(!t.collision_info.contact_plane_valid);
    assert!(!t.collision_info.last_known_contact_plane_valid);
    assert!(!t.object_info.on_walkable());
}

#[test]
fn d2_grounded_mover_tall_wall_does_not_recurse_infinitely() {
    let mut t = make_grounded_transition(v(0.1, 0.0, 0.0), v(0.6, 0.0, 0.0), 0.04);
    let engine = make_test_engine(tall_wall(), 0.0);
    let start = std::time::Instant::now();
    let _ = t.find_transitional_position(&engine);
    assert!(start.elapsed().as_millis() < 100);
}

#[test]
fn d3_airborne_mover_tall_wall_preserves_vertical_motion() {
    let from = v(0.1, 0.0, 2.0);
    let to = v(0.6, 0.0, 1.5);
    let mut t = make_airborne_transition(from, to);
    let engine = make_test_engine(tall_wall(), -50.0);
    let _ = t.find_transitional_position(&engine);
    assert!(
        t.sphere_path.cur_pos.z < from.z - 0.1,
        "z {}",
        t.sphere_path.cur_pos.z
    );
    assert!(t.sphere_path.cur_pos.x <= 0.5 - SPHERE_RADIUS + EPSILON * 20.0);
}

#[test]
fn d4_airborne_mover_tall_wall_persists_sliding_normal_across_frames() {
    let engine = make_test_engine(tall_wall(), -50.0);
    let mut body = PhysicsBody::new();
    body.set_position(v(0.25, 0.0, 2.0));
    body.transient_state = TransientState::ACTIVE;

    let request = |current: Vec3, target: Vec3| ResolveRequest {
        current_pos: current,
        target_pos: target,
        cell_id: 0xA9B4_0001,
        sphere_radius: SPHERE_RADIUS,
        sphere_height: 0.0,
        step_up_height: 0.04,
        step_down_height: 0.04,
        is_on_ground: false,
        ..ResolveRequest::default()
    };

    let frame1 = engine.resolve_with_transition(
        &request(body.position(), v(0.36, 0.0, 1.92)),
        Some(&mut body),
    );
    body.set_position(frame1.position);
    assert!(body.transient_state.contains(TransientState::SLIDING));
    // The primary sphere's set-collide hard-stops this first frame; the
    // persisted normal then permits the downward tangent on frame two.
    assert_near(2.0, frame1.position.z, 1e-3);

    let frame2 = engine.resolve_with_transition(
        &request(body.position(), body.position() + v(0.11, 0.0, -0.08)),
        Some(&mut body),
    );
    assert!(frame2.position.z < frame1.position.z - 0.05);
    assert!(
        (0.24..=0.31).contains(&frame2.position.x),
        "x {}",
        frame2.position.x
    );
}
