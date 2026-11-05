//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/Ts46SphereListConformanceTests.cs`,
//! `Ts4SteepRoofWedgeCaptureTests.cs`, `Ts4Path6ConformanceTests.cs` and
//! `Ts4ProductionQuantumConformanceTests.cs`.
//!
//! OpenAC also runs the last two over its prepared-flat trees and requires
//! bit-identical results; with one tree representation here each runs once
//! and keeps every exact (bit-pattern) golden value.

use glam::{Quat, Vec3};

use super::bsp_step_up::{SLOPED_SLOPE_ID, SPHERE_RADIUS, make_test_engine, sloped_unwalkable};
use super::support::*;
use crate::bsp::PhysicsBsp;
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::{PhysicsEngine, ResolveRequest, ResolveResult};
use crate::geometry::{Plane, Sphere};
use crate::globals::{DUMMY_SPHERE_RADIUS, FLOOR_Z, LANDING_Z};
use crate::obj_update::commit_set_position_transition;
use crate::polygon::ResolvedPolygon;
use crate::shadow::ShadowCollisionType;
use crate::state::{ObjectInfoState, TransitionState};
use crate::terrain::TerrainSurface;
use crate::transition::{CollisionSphere, SpherePath, Transition};
use crate::world::CollisionWorld;
use crate::{PhysicsBody, PhysicsState, TransientState};

fn player() -> ObjectInfoState {
    ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE)
}

/// .NET's `Vector3.Normalize`: divide by the length (glam multiplies by
/// its reciprocal, which can differ in the last bit).
fn net_normalize(v: Vec3) -> Vec3 {
    v / v.length()
}

#[track_caller]
fn assert_bits(expected: f32, actual: f32) {
    assert_eq!(
        expected.to_bits(),
        actual.to_bits(),
        "expected {expected}, got {actual}"
    );
}

#[track_caller]
fn assert_vec_bits(expected: Vec3, actual: Vec3) {
    assert_bits(expected.x, actual.x);
    assert_bits(expected.y, actual.y);
    assert_bits(expected.z, actual.z);
}

// ── Ts46SphereListConformanceTests ─────────────────────────────────────

const TS46_LB: u32 = 0xA9C5_0000;
const TS46_CELL: u32 = TS46_LB | 0x0001;

fn human_setup_spheres() -> Vec<CollisionSphere> {
    vec![
        CollisionSphere::new(Vec3::new(0.0, 0.0, 0.475), 0.48),
        CollisionSphere::new(Vec3::new(0.0, 0.0, 1.350), 0.48),
    ]
}

#[test]
fn sphere_list_init_path_matches_dat_spheres_exactly() {
    let mut sp = SpherePath::default();
    sp.init_path_spheres(
        Vec3::ZERO,
        Vec3::X,
        TS46_CELL,
        &human_setup_spheres(),
        1.0,
        None,
        None,
    );
    assert_eq!(sp.num_sphere, 2);
    assert_eq!(sp.local_sphere[0].center, Vec3::new(0.0, 0.0, 0.475));
    assert_eq!(sp.local_sphere[0].radius, 0.48);
    assert_eq!(sp.local_sphere[1].center, Vec3::new(0.0, 0.0, 1.350));
    assert_eq!(sp.local_sphere[1].radius, 0.48);
    assert_ne!(sp.local_sphere[1].center.z, 1.355);
}

#[test]
fn sphere_list_init_path_applies_scale_to_origin_and_radius() {
    let mut sp = SpherePath::default();
    sp.init_path_spheres(
        Vec3::ZERO,
        Vec3::ZERO,
        TS46_CELL,
        &human_setup_spheres(),
        2.0,
        None,
        None,
    );
    assert_eq!(sp.local_sphere[0].center, Vec3::new(0.0, 0.0, 0.95));
    assert_eq!(sp.local_sphere[0].radius, 0.96);
    assert_eq!(sp.local_sphere[1].center, Vec3::new(0.0, 0.0, 2.70));
    assert_eq!(sp.local_sphere[1].radius, 0.96);
}

#[test]
fn sphere_list_init_path_caps_at_two_spheres() {
    let mut spheres = human_setup_spheres();
    spheres.push(CollisionSphere::new(Vec3::new(0.0, 0.0, 2.0), 0.2));
    let mut sp = SpherePath::default();
    sp.init_path_spheres(Vec3::ZERO, Vec3::ZERO, TS46_CELL, &spheres, 1.0, None, None);
    assert_eq!(sp.num_sphere, 2);
    assert_eq!(sp.local_sphere[1].center.z, 1.350);
}

#[test]
fn sphere_list_init_path_empty_list_falls_back_to_dummy_sphere() {
    let mut sp = SpherePath::default();
    sp.init_path_spheres(Vec3::ZERO, Vec3::ZERO, TS46_CELL, &[], 1.0, None, None);
    assert_eq!(sp.num_sphere, 1);
    assert_eq!(sp.local_sphere[0].radius, DUMMY_SPHERE_RADIUS);
}

#[test]
fn scalar_init_path_reconstructs_head_from_height() {
    let mut sp = SpherePath::default();
    sp.init_path(
        Vec3::ZERO,
        Vec3::ZERO,
        TS46_CELL,
        0.48,
        1.835,
        None,
        None,
        None,
    );
    assert_eq!(sp.num_sphere, 2);
    assert_eq!(sp.local_sphere[0].center, Vec3::new(0.0, 0.0, 0.48));
    assert_eq!(sp.local_sphere[0].radius, 0.48);
    assert_eq!(sp.local_sphere[1].center, Vec3::new(0.0, 0.0, 1.355));
    assert_eq!(sp.local_sphere[1].radius, 0.48);
    assert_ne!(sp.local_sphere[1].center.z, 1.350);
}

fn ts46_engine(obstacle_id: u32) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(
        TS46_LB,
        TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    register_shadow(
        &mut engine,
        obstacle_id,
        0,
        Vec3::new(12.0, 11.0, 1.350),
        Quat::IDENTITY,
        0.30,
        TS46_LB,
        ShadowCollisionType::Sphere,
        0.0,
        1.0,
        0,
        EntityCollisionFlags(EntityCollisionFlags::IS_CREATURE),
        0,
        false,
    );
    engine
}

fn ts46_walk(engine: &PhysicsEngine, sphere_list: Vec<CollisionSphere>) -> Vec3 {
    let mut pos = Vec3::new(12.0, 10.0, 0.0);
    let mut cell = TS46_CELL;
    let mut grounded = true;
    for _ in 0..30 {
        let r = engine.resolve_with_transition(
            &ResolveRequest {
                current_pos: pos,
                target_pos: pos + Vec3::new(0.0, 0.08, 0.0),
                cell_id: cell,
                sphere_radius: 0.05,
                sphere_height: 0.10,
                step_up_height: 0.4,
                step_down_height: 0.4,
                is_on_ground: grounded,
                mover_flags: player(),
                sphere_list: sphere_list.clone(),
                sphere_scale: 1.0,
                ..ResolveRequest::default()
            },
            None,
        );
        pos = r.position;
        cell = r.cell_id;
        grounded = r.is_on_ground;
    }
    pos
}

#[test]
fn resolve_honors_sphere_list_over_scalar_decoy() {
    let pos = ts46_walk(&ts46_engine(0xD0D0), human_setup_spheres());
    assert!(pos.y < 10.6, "head sphere not blocked, y {}", pos.y);
    assert!(pos.y > 9.9, "stopped early, y {}", pos.y);
}

#[test]
fn resolve_empty_sphere_list_falls_back_to_scalar_reconstruction() {
    let pos = ts46_walk(&ts46_engine(0xD0D1), Vec::new());
    assert!(pos.y > 11.9, "decoy capsule blocked, y {}", pos.y);
}

// ── Ts4SteepRoofWedgeCaptureTests ──────────────────────────────────────

#[test]
fn fall_onto_steep_slope_pure_vertical_never_wedges_within_three_seconds() {
    let engine = make_test_engine(sloped_unwalkable(), -1000.0);
    const DT: f32 = 1.0 / 30.0;
    let mut body = PhysicsBody::new();
    body.transient_state = TransientState::ACTIVE;
    let start = Vec3::new(0.5, 0.0, 3.0);
    let mut pos = start;
    let mut fall_z = 0.0f32;
    let mut cell = 0xA9B4_0001;
    let mut frozen = 0;
    for _ in 0..90 {
        fall_z += -9.8 * DT;
        let grounded = body.on_walkable();
        let r = engine.resolve_with_transition(
            &ResolveRequest {
                current_pos: pos,
                target_pos: pos + Vec3::new(0.0, 0.0, fall_z * DT),
                cell_id: cell,
                sphere_radius: SPHERE_RADIUS,
                sphere_height: SPHERE_RADIUS * 2.0,
                step_up_height: 0.30,
                step_down_height: 0.04,
                is_on_ground: grounded,
                mover_flags: player(),
                moving_entity_id: 0x0100_0000,
                ..ResolveRequest::default()
            },
            Some(&mut body),
        );
        frozen = if r.position.distance(pos) < 0.001 {
            frozen + 1
        } else {
            0
        };
        pos = r.position;
        cell = r.cell_id;
        body.set_position(pos);
        if r.is_on_ground {
            fall_z = 0.0;
        }
        body.transient_state
            .remove(TransientState::CONTACT | TransientState::ON_WALKABLE);
        if r.in_contact {
            body.transient_state.insert(TransientState::CONTACT);
        }
        if r.on_walkable {
            body.transient_state.insert(TransientState::ON_WALKABLE);
        }
        assert!(frozen <= 15, "froze {frozen} ticks at {pos}");
    }
    assert!(
        pos.x < start.x - 0.10,
        "no downhill progress: {start} -> {pos}"
    );
}

// ── Ts4Path6ConformanceTests ───────────────────────────────────────────

const CELL: u32 = 0xA9B4_0001;

struct SiteOutcome {
    state: TransitionState,
    collide: bool,
    check_pos: Vec3,
    check_cell_id: u32,
    backup_check_pos: Vec3,
    backup_check_cell_id: u32,
    step_up_normal: Vec3,
    walk_interp: f32,
    walkable_allowance: f32,
    collision_normal_valid: bool,
    collision_normal: Vec3,
    sliding_normal_valid: bool,
    sliding_normal: Vec3,
}

fn run_site(
    bsp: &PhysicsBsp,
    current: Vec3,
    target: Vec3,
    foot: Sphere,
    head: Option<Sphere>,
    seed_sliding: bool,
) -> SiteOutcome {
    let mut t = Transition::new();
    t.sphere_path.init_path(
        current,
        target,
        CELL,
        SPHERE_RADIUS,
        if head.is_none() { 0.0 } else { 1.0 },
        None,
        None,
        None,
    );
    t.sphere_path.set_check_pos(target, CELL);
    t.sphere_path.backup_check_pos = Vec3::new(91.0, 92.0, 93.0);
    t.sphere_path.backup_check_cell_id = 0xA9B4_0077;
    t.sphere_path.walk.walk_interp = 0.625;
    t.sphere_path.walk.walkable_allowance = 0.8125;
    if seed_sliding {
        t.collision_info.set_sliding_normal(Vec3::Y);
    }
    let state = bsp_find(
        bsp,
        &mut t,
        foot,
        head,
        current,
        Vec3::Z,
        1.0,
        Quat::IDENTITY,
        None,
    );
    let (sp, ci) = (&t.sphere_path, &t.collision_info);
    SiteOutcome {
        state,
        collide: sp.collide,
        check_pos: sp.check_pos,
        check_cell_id: sp.check_cell_id,
        backup_check_pos: sp.backup_check_pos,
        backup_check_cell_id: sp.backup_check_cell_id,
        step_up_normal: sp.step_up_normal,
        walk_interp: sp.walk.walk_interp,
        walkable_allowance: sp.walk.walkable_allowance,
        collision_normal_valid: ci.collision_normal_valid,
        collision_normal: ci.collision_normal,
        sliding_normal_valid: ci.sliding_normal_valid,
        sliding_normal: ci.sliding_normal,
    }
}

#[test]
fn primary_steep_hit_sets_collide_with_exact_state() {
    for seed in [false, true] {
        let bsp = sloped_unwalkable();
        let current = Vec3::new(0.5, 0.0, 1.1);
        let target = Vec3::new(0.5, 0.0, 0.9);
        let foot = Sphere::new(target + Vec3::new(0.0, 0.0, SPHERE_RADIUS), SPHERE_RADIUS);
        let expected_normal = bsp.polygon(SLOPED_SLOPE_ID).unwrap().plane.normal;
        let o = run_site(&bsp, current, target, foot, None, seed);
        assert_eq!(o.state, TransitionState::Adjusted);
        assert!(o.collide);
        assert_vec_bits(target, o.check_pos);
        assert_eq!(o.check_cell_id, CELL);
        assert_vec_bits(target, o.backup_check_pos);
        assert_eq!(o.backup_check_cell_id, CELL);
        assert_vec_bits(expected_normal, o.step_up_normal);
        assert_bits(1.0, o.walk_interp);
        assert_bits(LANDING_Z, o.walkable_allowance);
        assert!(!o.collision_normal_valid);
        assert_eq!(o.sliding_normal_valid, seed);
        assert_vec_bits(if seed { Vec3::Y } else { Vec3::ZERO }, o.sliding_normal);
    }
}

fn raised_wall() -> PhysicsBsp {
    single_leaf(
        Vec3::new(0.5, 0.0, 1.5),
        4.0,
        vec![ResolvedPolygon::new(
            1,
            vec![
                Vec3::new(0.5, -1.0, 0.55),
                Vec3::new(0.5, -1.0, 2.5),
                Vec3::new(0.5, 1.0, 2.5),
                Vec3::new(0.5, 1.0, 0.55),
            ],
            Plane::new(-Vec3::X, 0.5),
        )],
    )
}

#[test]
fn secondary_only_hit_hard_stops_without_set_collide() {
    for seed in [false, true] {
        let bsp = raised_wall();
        let current = Vec3::new(0.1, 0.0, 0.0);
        let target = Vec3::new(0.35, 0.0, 0.0);
        let foot = Sphere::new(target + Vec3::new(0.0, 0.0, SPHERE_RADIUS), SPHERE_RADIUS);
        let head = Sphere::new(target + Vec3::new(0.0, 0.0, 0.8), SPHERE_RADIUS);
        let o = run_site(&bsp, current, target, foot, Some(head), seed);
        assert_eq!(o.state, TransitionState::Collided);
        assert!(!o.collide);
        assert_vec_bits(target, o.check_pos);
        assert_eq!(o.check_cell_id, CELL);
        assert_vec_bits(Vec3::new(91.0, 92.0, 93.0), o.backup_check_pos);
        assert_eq!(o.backup_check_cell_id, 0xA9B4_0077);
        assert_vec_bits(Vec3::ZERO, o.step_up_normal);
        assert_bits(0.625, o.walk_interp);
        assert_bits(0.8125, o.walkable_allowance);
        assert!(o.collision_normal_valid);
        assert_bits(-1.0, o.collision_normal.x);
        assert_eq!(o.collision_normal.y, 0.0);
        assert_eq!(o.collision_normal.z, 0.0);
        assert_eq!(o.sliding_normal_valid, seed);
        assert_vec_bits(if seed { Vec3::Y } else { Vec3::ZERO }, o.sliding_normal);
    }
}

// ── Ts4ProductionQuantumConformanceTests ───────────────────────────────

const QUANTUM_GFX: u32 = 0x0100_E1B0;
const DT: f32 = 1.0 / 30.0;
const RADIUS: f32 = 0.48;

fn wide_steep_roof() -> PhysicsBsp {
    let v = [
        Vec3::new(-64.0, -64.0, -128.0),
        Vec3::new(64.0, -64.0, 128.0),
        Vec3::new(64.0, 64.0, 128.0),
        Vec3::new(-64.0, 64.0, -128.0),
    ];
    let mut normal = net_normalize((v[1] - v[0]).cross(v[3] - v[0]));
    if normal.x > 0.0 {
        normal = -normal;
    }
    let d = -normal.dot(v[0]);
    single_leaf(
        Vec3::ZERO,
        192.0,
        vec![ResolvedPolygon::new(1, v.to_vec(), Plane::new(normal, d))],
    )
}

fn elevated_head_wall() -> PhysicsBsp {
    single_leaf(
        Vec3::new(0.0, 0.0, 32.0),
        96.0,
        vec![ResolvedPolygon::new(
            1,
            vec![
                Vec3::new(0.0, 64.0, 1.10),
                Vec3::new(0.0, -64.0, 1.10),
                Vec3::new(0.0, -64.0, 64.0),
                Vec3::new(0.0, 64.0, 64.0),
            ],
            Plane::new(-Vec3::X, 0.0),
        )],
    )
}

fn quantum_engine(bsp: PhysicsBsp) -> PhysicsEngine {
    let root = *bsp.nodes()[0].sphere();
    let mut gfx = gfx_obj(QUANTUM_GFX, bsp, root.radius);
    gfx.bounding_sphere = root;
    let mut world = CollisionWorld::new();
    world.insert_gfx_obj(gfx);
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    let terrain = || TerrainSurface::new(&[0u8; 81], &[-1000.0; 256], 0, 0, None);
    engine.add_landblock(0xA9B4_0000, terrain(), Vec::new(), Vec::new(), 0.0, 0.0);
    engine.add_landblock(0xA8B4_0000, terrain(), Vec::new(), Vec::new(), -192.0, 0.0);
    register_single(
        &mut engine,
        QUANTUM_GFX,
        QUANTUM_GFX,
        Vec3::ZERO,
        Quat::IDENTITY,
        root.radius,
        0xA9B4_FFFF,
        ShadowCollisionType::Bsp,
        1.0,
        1.0,
        0,
        EntityCollisionFlags::default(),
        0,
    );
    engine
}

#[derive(Clone, Copy)]
struct QuantumFrame {
    candidate_velocity: Vec3,
    pre_response_velocity: Vec3,
    pre_response_stationary_fall: i32,
    candidate_moved: bool,
    result_ok: bool,
    commit_applied: bool,
    position: Vec3,
    velocity: Vec3,
    transient_state: TransientState,
    body_in_contact: bool,
    body_on_walkable: bool,
    sliding_normal: Vec3,
    collision_normal_valid: bool,
    collision_normal: Vec3,
    previous_on_walkable: bool,
    contact_plane_valid: bool,
    contact_plane: Plane,
}

struct QuantumTrace {
    frames: Vec<QuantumFrame>,
    peak_z: f32,
    first_contact: Option<usize>,
}

fn run_quantum(
    bsp: PhysicsBsp,
    initial_position: Vec3,
    initial_velocity: Vec3,
    ticks: usize,
) -> QuantumTrace {
    let engine = quantum_engine(bsp);
    let mut body = PhysicsBody::new();
    body.set_position(initial_position);
    body.orientation = Quat::IDENTITY;
    body.velocity = initial_velocity;
    body.state = PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS;
    body.transient_state = TransientState::ACTIVE;
    body.snap_to_cell(CELL, initial_position, initial_position);

    let mut frames = Vec::with_capacity(ticks);
    let mut peak_z = body.position().z;
    let mut first_contact = None;
    let mut cell = CELL;
    for tick in 0..ticks {
        let pre = body.position();
        body.calc_acceleration();
        body.update_physics_internal(DT);
        let candidate = body.position();
        let candidate_velocity = body.velocity;
        let candidate_moved = candidate != pre;
        let on_ground = body.on_walkable();
        let r: ResolveResult = engine.resolve_with_transition(
            &ResolveRequest {
                current_pos: pre,
                target_pos: candidate,
                cell_id: cell,
                sphere_radius: RADIUS,
                sphere_height: 1.835,
                step_up_height: 0.4,
                step_down_height: 0.4,
                is_on_ground: on_ground,
                mover_flags: player(),
                moving_entity_id: 0x0100_0000,
                sphere_list: vec![
                    CollisionSphere::new(Vec3::new(0.0, 0.0, 0.475), RADIUS),
                    CollisionSphere::new(Vec3::new(0.0, 0.0, 1.350), RADIUS),
                ],
                sphere_scale: 1.0,
                ..ResolveRequest::default()
            },
            Some(&mut body),
        );
        let pre_response_velocity = body.velocity;
        let pre_response_stationary_fall = body.frames_stationary_fall;
        let previous_contact = body.in_contact();
        let previous_on_walkable = body.on_walkable();
        body.cached_velocity = if candidate_moved {
            (r.position - pre) / DT
        } else {
            Vec3::ZERO
        };
        body.commit_transition_position(r.cell_id, r.position);
        cell = r.cell_id;
        let commit_applied = r.ok && candidate_moved;
        if commit_applied {
            commit_set_position_transition(
                &mut body,
                r.in_contact,
                r.on_walkable,
                r.collision_normal_valid,
                r.collision_normal,
                previous_contact,
                previous_on_walkable,
                None,
                None,
                None,
                None,
            );
        }
        peak_z = peak_z.max(body.position().z);
        if first_contact.is_none() && r.in_contact {
            first_contact = Some(tick);
        }
        frames.push(QuantumFrame {
            candidate_velocity,
            pre_response_velocity,
            pre_response_stationary_fall,
            candidate_moved,
            result_ok: r.ok,
            commit_applied,
            position: body.position(),
            velocity: body.velocity,
            transient_state: body.transient_state,
            body_in_contact: body.in_contact(),
            body_on_walkable: body.on_walkable(),
            sliding_normal: body.sliding_normal,
            collision_normal_valid: r.collision_normal_valid,
            collision_normal: r.collision_normal,
            previous_on_walkable,
            contact_plane_valid: body.contact_plane_valid,
            contact_plane: body.contact_plane,
        });
    }
    QuantumTrace {
        frames,
        peak_z,
        first_contact,
    }
}

fn over_slope(p: Vec3) -> bool {
    (-64.0..=64.0).contains(&p.x) && p.y.abs() <= 64.0
}

#[track_caller]
fn assert_no_steep_fixed_point(trace: &QuantumTrace, label: &str) {
    let mut frozen = 0;
    for i in 1..trace.frames.len() {
        let (prev, cur) = (&trace.frames[i - 1], &trace.frames[i]);
        let on_slope = over_slope(cur.position)
            && cur.contact_plane_valid
            && cur.contact_plane.normal.z < FLOOR_Z;
        frozen = if on_slope && prev.position.distance(cur.position) < 0.001 {
            frozen + 1
        } else {
            0
        };
        assert!(
            frozen <= 15,
            "{label} fixed on the roof for {frozen} ticks at frame {i}"
        );
    }
}

#[track_caller]
fn assert_no_slope_penetration(trace: &QuantumTrace, label: &str) {
    let slope = wide_steep_roof().polygon(1).unwrap().plane;
    for (i, f) in trace.frames.iter().enumerate() {
        assert!(f.position.is_finite(), "{label} frame {i}: {}", f.position);
        if !over_slope(f.position) {
            continue;
        }
        let foot = f.position + Vec3::new(0.0, 0.0, 0.475);
        let distance = slope.normal.dot(foot) + slope.d;
        assert!(
            distance >= RADIUS - 0.015,
            "{label} penetrated at frame {i}: {distance}"
        );
    }
}

#[track_caller]
fn assert_collision_response_exact(hit: &QuantumFrame) {
    assert!(hit.result_ok);
    assert!(hit.candidate_moved);
    assert!(hit.commit_applied);
    let mut expected = hit.pre_response_velocity;
    let should_reflect = !hit.previous_on_walkable || !hit.body_on_walkable;
    if hit.pre_response_stationary_fall > 1 {
        expected = Vec3::ZERO;
    } else if should_reflect && hit.collision_normal_valid {
        let dot = expected.dot(hit.collision_normal);
        if dot < 0.0 {
            expected += hit.collision_normal * (-(dot * 1.05));
        }
    }
    assert_vec_bits(expected, hit.velocity);
}

#[track_caller]
fn assert_terminal_state_exact(trace: &QuantumTrace, position: [u32; 3], velocity: [u32; 3]) {
    let t = trace.frames.last().unwrap();
    assert_eq!(
        [
            t.position.x.to_bits(),
            t.position.y.to_bits(),
            t.position.z.to_bits()
        ],
        position,
        "terminal position {}",
        t.position
    );
    assert_eq!(
        [
            t.velocity.x.to_bits(),
            t.velocity.y.to_bits(),
            t.velocity.z.to_bits()
        ],
        velocity,
        "terminal velocity {}",
        t.velocity
    );
    assert_eq!(
        t.transient_state,
        TransientState::ACTIVE | TransientState::CONTACT
    );
    assert!(t.body_in_contact);
    assert!(!t.body_on_walkable);
    assert!(!t.transient_state.contains(TransientState::SLIDING));
    assert_eq!(t.sliding_normal, Vec3::ZERO);
    assert!(!t.collision_normal_valid);
    assert!(t.contact_plane_valid);
    assert_eq!(t.contact_plane.normal.x.to_bits(), 0xBF64_F92F);
    assert_eq!(t.contact_plane.normal.y.to_bits(), 0);
    assert_eq!(t.contact_plane.normal.z.to_bits(), 0x3EE4_F92F);
    assert_eq!(t.contact_plane.d.to_bits(), 0x8000_0000);
}

/// Direction, initial velocity, terminal position bits, terminal velocity bits.
type RoofCase = (&'static str, [f32; 3], [u32; 3], [u32; 3]);

#[test]
fn roof_directions_production_quantum_exact_and_never_wedge() {
    let cases: [RoofCase; 4] = [
        (
            "vertical",
            [0.0, 0.0, 0.0],
            [0xC184_15BE, 0, 0xC201_B112],
            [0, 0, 0xC1EB_3325],
        ),
        (
            "inward",
            [0.536_656_4, 0.0, -0.268_328_2],
            [0xC184_64EA, 0, 0xC202_003E],
            [0x3F09_6250, 0, 0xC1ED_58AE],
        ),
        (
            "tangent",
            [0.0, 0.30, 0.0],
            [0xC184_15BE, 0x3F63_F3DB, 0xC201_B112],
            [0, 0x3E99_999A, 0xC1EB_3325],
        ),
        (
            "downhill",
            [-0.268_328_2, 0.0, -0.536_656_4],
            [0xC18A_74DB, 0, 0xC208_102F],
            [0xBE89_6250, 0, 0xC1EF_7E37],
        ),
    ];
    for (direction, v, position, velocity) in cases {
        let trace = run_quantum(
            wide_steep_roof(),
            Vec3::new(0.5, 0.0, 3.0),
            Vec3::from_array(v),
            90,
        );
        assert_no_steep_fixed_point(&trace, direction);
        assert_no_slope_penetration(&trace, direction);
        let contact = trace
            .first_contact
            .unwrap_or_else(|| panic!("{direction} never touched the roof"));
        assert_collision_response_exact(&trace.frames[contact]);
        assert_terminal_state_exact(&trace, position, velocity);
    }
}

#[test]
fn uphill_positive_z_jump_production_quantum_exact_without_launch() {
    let trace = run_quantum(
        wide_steep_roof(),
        Vec3::new(-0.20, 0.0, 0.422),
        Vec3::new(0.447_213_6, 0.0, 0.894_427_2),
        90,
    );
    let contact = trace.first_contact.expect("the jump never hit the roof");
    assert_no_steep_fixed_point(&trace, "uphill-jump");
    assert_no_slope_penetration(&trace, "uphill-jump");
    let hit = trace.frames[contact];
    let peak_frame = trace
        .frames
        .iter()
        .position(|f| f.position.z == trace.peak_z)
        .unwrap();
    assert!(
        (1..contact).contains(&peak_frame),
        "peak frame {peak_frame}, contact {contact}"
    );
    assert!(hit.candidate_velocity.z < 0.0);
    assert!(
        trace.frames[contact..]
            .iter()
            .all(|f| f.candidate_velocity.z <= 0.0)
    );
    let post_peak = trace.frames[contact..]
        .iter()
        .map(|f| f.position.z)
        .fold(f32::NEG_INFINITY, f32::max);
    assert!(post_peak <= trace.peak_z + 0.001);
    assert_collision_response_exact(&hit);
    assert_eq!(trace.peak_z.to_bits(), 0x3EEC_C54B);
    assert_eq!(contact, 6);
    assert_terminal_state_exact(
        &trace,
        [0xC183_34B2, 0, 0xC200_D006],
        [0x3EE4_F92E, 0, 0xC1E4_0B5D],
    );
}

#[test]
fn positive_z_jump_head_only_collision_uses_exact_retail_elastic_reflection() {
    let trace = run_quantum(
        elevated_head_wall(),
        Vec3::new(-0.60, 0.0, 0.0),
        Vec3::new(2.0, 0.0, 2.0),
        12,
    );
    let hit_index = trace
        .frames
        .iter()
        .position(|f| f.collision_normal_valid)
        .expect("no collision");
    let hit = trace.frames[hit_index];
    assert!(hit.candidate_velocity.z > 0.0);
    assert!(hit.pre_response_velocity.dot(hit.collision_normal) < 0.0);
    assert_collision_response_exact(&hit);
    assert_bits(hit.pre_response_velocity.z, hit.velocity.z);
    assert!(hit.velocity.x < 0.0);
    for i in hit_index + 1..trace.frames.len() {
        assert!(trace.frames[i].candidate_velocity.z < trace.frames[i - 1].candidate_velocity.z);
    }
}
