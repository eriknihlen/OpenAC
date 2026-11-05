//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/SphereCollisionFamilyTests.cs`,
//! `CylSphereFamilyTests.cs`, `ObstructionEtherealTests.cs`,
//! `BroadphaseReachFilterTests.cs` and `CrowdJumpTests.cs`: walking, falling
//! and jumping into sphere, cylinder and model objects.
//!
//! `SphereIntersectsSphereConformanceTests.cs` is covered by the primitive
//! tests of slice 1.

use glam::{Quat, Vec3};

use super::support::*;
use crate::collision_flags::{EntityCollisionFlags, should_skip};
use crate::engine::{PhysicsEngine, ResolveRequest, ResolveResult};
use crate::geometry::Plane;
use crate::obj_update::handle_all_collisions;
use crate::polygon::ResolvedPolygon;
use crate::shadow::ShadowCollisionType;
use crate::state::ObjectInfoState;
use crate::terrain::TerrainSurface;
use crate::transition::SpherePath;
use crate::world::CollisionWorld;
use crate::{PhysicsBody, PhysicsState, TransientState};

const LANDBLOCK: u32 = 0xA9B4_0000;
const CELL: u32 = LANDBLOCK | 0x0001;
const RADIUS: f32 = 0.48;
const HEIGHT: f32 = 1.20;
const STEP_UP: f32 = 0.60;
const STEP_DOWN: f32 = 0.04;
const ETHEREAL_PS: u32 = 0x4;
const IGNORE_COLLISIONS_PS: u32 = 0x10;

fn player() -> ObjectInfoState {
    ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE)
}

/// An engine over flat terrain at height `z` (OpenAC's all-zero or all
/// -1000 height table) with a world attached first.
fn engine_at(landblock: u32, z: f32) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(
        landblock,
        TerrainSurface::new(&[0u8; 81], &[z; 256], 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    engine
}

#[allow(clippy::too_many_arguments)]
fn step(
    engine: &PhysicsEngine,
    pos: Vec3,
    delta: Vec3,
    cell: u32,
    height: f32,
    grounded: bool,
    body: Option<&mut PhysicsBody>,
) -> ResolveResult {
    engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: pos,
            target_pos: pos + delta,
            cell_id: cell,
            sphere_radius: RADIUS,
            sphere_height: height,
            step_up_height: STEP_UP,
            step_down_height: STEP_DOWN,
            is_on_ground: grounded,
            mover_flags: player(),
            ..ResolveRequest::default()
        },
        body,
    )
}

fn grounded_body(position: Vec3) -> PhysicsBody {
    let floor = Plane::new(Vec3::Z, 0.0);
    let mut body = PhysicsBody::new();
    body.set_position(position);
    body.orientation = Quat::IDENTITY;
    body.contact_plane_valid = true;
    body.contact_plane = floor;
    body.contact_plane_cell_id = CELL;
    body.walkable_polygon_valid = true;
    body.walkable_plane = floor;
    body.walkable_vertices = Some(vec![
        Vec3::new(-100.0, -100.0, 0.0),
        Vec3::new(100.0, -100.0, 0.0),
        Vec3::new(100.0, 100.0, 0.0),
        Vec3::new(-100.0, 100.0, 0.0),
    ]);
    body.walkable_up = Vec3::Z;
    body.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    body
}

/// Walks a grounded body `ticks` times by `per_tick`; returns the final
/// position and whether it is still grounded.
fn walk(engine: &PhysicsEngine, start: Vec3, per_tick: Vec3, ticks: usize) -> (Vec3, bool) {
    let mut body = grounded_body(start);
    let mut pos = start;
    let mut cell = CELL;
    let mut grounded = true;
    for _ in 0..ticks {
        let r = step(
            engine,
            pos,
            per_tick,
            cell,
            HEIGHT,
            grounded,
            Some(&mut body),
        );
        body.set_position(r.position);
        pos = r.position;
        cell = r.cell_id;
        grounded = r.is_on_ground;
    }
    (pos, grounded)
}

// ── SphereCollisionFamilyTests ─────────────────────────────────────────

fn register_creature_sphere(
    engine: &mut PhysicsEngine,
    id: u32,
    x: f32,
    y: f32,
    state: u32,
    creature: bool,
) {
    let flags = if creature {
        EntityCollisionFlags::IS_CREATURE
    } else {
        0
    };
    register_shadow(
        engine,
        id,
        0,
        Vec3::new(x, y, RADIUS),
        Quat::IDENTITY,
        0.48,
        LANDBLOCK,
        ShadowCollisionType::Sphere,
        0.0,
        1.0,
        state,
        EntityCollisionFlags(flags),
        0,
        false,
    );
}

#[test]
fn grounded_diagonal_approach_slides_past_offset_creature() {
    let mut engine = engine_at(LANDBLOCK, 0.0);
    register_creature_sphere(&mut engine, 0xC0C0, 10.35, 12.0, 0, true);
    let (pos, _) = walk(
        &engine,
        Vec3::new(10.0, 10.0, 0.0),
        Vec3::new(0.0, 0.08, 0.0),
        60,
    );
    assert!(
        pos.y > 12.5,
        "must slide around the creature, got y {}",
        pos.y
    );
}

#[test]
fn grounded_single_creature_head_on_push_blocks_without_penetration() {
    let mut engine = engine_at(LANDBLOCK, 0.0);
    register_creature_sphere(&mut engine, 0xC0B0, 12.0, 11.5, 0, true);
    let (pos, _) = walk(
        &engine,
        Vec3::new(12.0, 10.0, 0.0),
        Vec3::new(0.0, 0.08, 0.0),
        30,
    );
    assert!(pos.y < 10.7, "penetrated to y {}", pos.y);
    assert!(pos.y > 10.3, "stopped early at y {}", pos.y);
}

#[test]
fn grounded_ethereal_sphere_is_fully_passable() {
    let mut engine = engine_at(LANDBLOCK, 0.0);
    register_creature_sphere(&mut engine, 0xC0E0, 12.0, 11.5, ETHEREAL_PS, false);
    let (pos, _) = walk(
        &engine,
        Vec3::new(12.0, 10.0, 0.0),
        Vec3::new(0.0, 0.08, 0.0),
        45,
    );
    assert!(pos.y > 12.5, "ethereal sphere blocked at y {}", pos.y);
}

// ── CylSphereFamilyTests ───────────────────────────────────────────────

const PLATFORM_RADIUS: f32 = 2.597;
const PLATFORM_HEIGHT: f32 = 0.256;

fn register_cylinder(
    engine: &mut PhysicsEngine,
    id: u32,
    pos: Vec3,
    radius: f32,
    height: f32,
    state: u32,
) {
    register_shadow(
        engine,
        id,
        0,
        pos,
        Quat::IDENTITY,
        radius,
        LANDBLOCK,
        ShadowCollisionType::Cylinder,
        height,
        1.0,
        state,
        EntityCollisionFlags::default(),
        0,
        true,
    );
}

#[test]
fn grounded_walk_into_wide_low_cylinder_steps_up_onto_top() {
    let mut engine = engine_at(LANDBLOCK, 0.0);
    register_cylinder(
        &mut engine,
        0xCAFE,
        Vec3::new(12.0, 14.0, 0.0),
        PLATFORM_RADIUS,
        PLATFORM_HEIGHT,
        0,
    );
    let (pos, grounded) = walk(
        &engine,
        Vec3::new(12.0, 10.4, 0.0),
        Vec3::new(0.0, 0.10, 0.0),
        40,
    );
    assert!(pos.y > 11.5, "pinned at the rim, y {}", pos.y);
    assert!(
        (pos.z - PLATFORM_HEIGHT).abs() < 0.05,
        "not on the top, z {}",
        pos.z
    );
    assert!(grounded);
}

#[test]
fn grounded_walk_into_tall_cylinder_blocks_before_axis() {
    let mut engine = engine_at(LANDBLOCK, 0.0);
    register_cylinder(&mut engine, 0xF00D, Vec3::new(12.0, 14.0, 0.0), 0.2, 2.2, 0);
    let (pos, _) = walk(
        &engine,
        Vec3::new(12.0, 12.6, 0.0),
        Vec3::new(0.0, 0.10, 0.0),
        30,
    );
    assert!(pos.y < 13.4, "passed through, y {}", pos.y);
    assert!(pos.z < 0.5, "climbed on top, z {}", pos.z);
}

#[test]
fn airborne_fall_onto_wide_cylinder_lands_on_top() {
    let mut engine = engine_at(LANDBLOCK, 0.0);
    register_cylinder(
        &mut engine,
        0xCAFE,
        Vec3::new(12.0, 14.0, 0.0),
        PLATFORM_RADIUS,
        PLATFORM_HEIGHT,
        0,
    );
    let mut pos = Vec3::new(12.0, 14.0, 1.0);
    let mut cell = CELL;
    let mut grounded = false;
    for _ in 0..20 {
        let r = step(
            &engine,
            pos,
            Vec3::new(0.0, 0.0, -0.25),
            cell,
            HEIGHT,
            grounded,
            None,
        );
        pos = r.position;
        cell = r.cell_id;
        grounded = r.is_on_ground;
        if grounded {
            break;
        }
    }
    assert!(grounded);
    assert!(
        (pos.z - PLATFORM_HEIGHT).abs() < 0.05,
        "landed at z {}",
        pos.z
    );
}

#[test]
fn grounded_ethereal_cylinder_is_fully_passable() {
    let mut engine = engine_at(LANDBLOCK, 0.0);
    register_cylinder(
        &mut engine,
        0xE7E7,
        Vec3::new(12.0, 14.0, 0.0),
        0.2,
        2.2,
        ETHEREAL_PS,
    );
    let (pos, _) = walk(
        &engine,
        Vec3::new(12.0, 12.6, 0.0),
        Vec3::new(0.0, 0.10, 0.0),
        30,
    );
    assert!(pos.y > 14.5, "ethereal cylinder blocked at y {}", pos.y);
}

// ── ObstructionEtherealTests ───────────────────────────────────────────

#[test]
fn should_skip_both_bits_instant_skip() {
    assert!(should_skip(
        ETHEREAL_PS | IGNORE_COLLISIONS_PS,
        EntityCollisionFlags::default(),
        ObjectInfoState(ObjectInfoState::IS_PLAYER)
    ));
}

#[test]
fn should_skip_ethereal_alone_not_instant_skip() {
    assert!(!should_skip(
        ETHEREAL_PS,
        EntityCollisionFlags::default(),
        ObjectInfoState(ObjectInfoState::IS_PLAYER)
    ));
}

#[test]
fn should_skip_ignore_collisions_alone_not_skipped() {
    assert!(!should_skip(
        IGNORE_COLLISIONS_PS,
        EntityCollisionFlags::default(),
        ObjectInfoState(ObjectInfoState::IS_PLAYER)
    ));
}

#[test]
fn sphere_path_obstruction_ethereal_defaults_false() {
    let mut sp = SpherePath::default();
    assert!(!sp.obstruction_ethereal);
    sp.obstruction_ethereal = true;
    assert!(sp.obstruction_ethereal);
}

const TEST_ENTITY: u32 = 0xF4201;
const TEST_GFX: u32 = 0xF4202;

fn engine_with_single_shadow(
    ty: ShadowCollisionType,
    state: u32,
    pos: Vec3,
    radius: f32,
    cyl_height: f32,
) -> PhysicsEngine {
    let mut engine = engine_at(LANDBLOCK, -1000.0);
    register_single(
        &mut engine,
        TEST_ENTITY,
        TEST_GFX,
        pos,
        Quat::IDENTITY,
        radius,
        LANDBLOCK,
        ty,
        cyl_height,
        1.0,
        state,
        EntityCollisionFlags::default(),
        0,
    );
    engine
}

/// Airborne sweeps of `per_tick` until a collision normal is reported.
fn sweep_until_blocked(
    engine: &PhysicsEngine,
    start: Vec3,
    per_tick: Vec3,
    max_ticks: usize,
    start_cell: u32,
) -> (bool, Vec3) {
    let mut pos = start;
    let mut cell = start_cell;
    let mut grounded = false;
    for _ in 0..max_ticks {
        let r = step(engine, pos, per_tick, cell, HEIGHT, grounded, None);
        if r.collision_normal_valid {
            return (true, r.position);
        }
        pos = r.position;
        cell = r.cell_id;
        grounded = r.is_on_ground;
    }
    (false, pos)
}

#[test]
fn cylinder_ethereal_alone_is_passable() {
    let engine = engine_with_single_shadow(
        ShadowCollisionType::Cylinder,
        ETHEREAL_PS,
        Vec3::new(12.0, 12.0, 0.0),
        0.3,
        1.5,
    );
    let (blocked, at) = sweep_until_blocked(
        &engine,
        Vec3::new(12.0, 11.0, 0.48),
        Vec3::new(0.0, 0.1, 0.0),
        20,
        CELL,
    );
    assert!(!blocked, "stopped at {at}");
}

#[test]
fn cylinder_non_ethereal_still_blocks() {
    let engine = engine_with_single_shadow(
        ShadowCollisionType::Cylinder,
        0,
        Vec3::new(12.0, 12.0, 0.0),
        0.3,
        1.5,
    );
    let (blocked, at) = sweep_until_blocked(
        &engine,
        Vec3::new(12.0, 11.0, 0.48),
        Vec3::new(0.0, 0.1, 0.0),
        20,
        CELL,
    );
    assert!(blocked, "reached {at}");
}

#[test]
fn sphere_ethereal_alone_is_passable() {
    let engine = engine_with_single_shadow(
        ShadowCollisionType::Sphere,
        ETHEREAL_PS,
        Vec3::new(12.0, 12.0, 0.48),
        0.4,
        0.0,
    );
    let (blocked, at) = sweep_until_blocked(
        &engine,
        Vec3::new(12.0, 11.0, 0.48),
        Vec3::new(0.0, 0.1, 0.0),
        20,
        CELL,
    );
    assert!(!blocked, "stopped at {at}");
}

#[test]
fn sphere_non_ethereal_still_blocks() {
    let engine = engine_with_single_shadow(
        ShadowCollisionType::Sphere,
        0,
        Vec3::new(12.0, 12.0, 0.48),
        0.4,
        0.0,
    );
    let (blocked, at) = sweep_until_blocked(
        &engine,
        Vec3::new(12.0, 11.0, 0.48),
        Vec3::new(0.0, 0.1, 0.0),
        20,
        CELL,
    );
    assert!(blocked, "reached {at}");
}

const LAYER2_LANDBLOCK: u32 = 0xA9B5_0000;
const LAYER2_CELL: u32 = LAYER2_LANDBLOCK | 0x0001;
const LAYER2_ENTITY: u32 = 0xF4300;
const LAYER2_GFX: u32 = 0xF4301;

fn engine_with_bsp_slab(state: u32) -> PhysicsEngine {
    let mut engine = engine_at(LAYER2_LANDBLOCK, -1000.0);
    let wall = ResolvedPolygon::new(
        1,
        vec![
            Vec3::new(-1.0, 0.0, -2.0),
            Vec3::new(1.0, 0.0, -2.0),
            Vec3::new(1.0, 0.0, 5.0),
            Vec3::new(-1.0, 0.0, 5.0),
        ],
        Plane::new(-Vec3::Y, 0.0),
    );
    engine.world_mut().unwrap().insert_gfx_obj(gfx_obj(
        LAYER2_GFX,
        single_leaf(Vec3::ZERO, 6.0, vec![wall]),
        6.0,
    ));
    register_single(
        &mut engine,
        LAYER2_ENTITY,
        LAYER2_GFX,
        Vec3::new(12.0, 12.0, 0.0),
        Quat::IDENTITY,
        10.0,
        LAYER2_LANDBLOCK,
        ShadowCollisionType::Bsp,
        0.0,
        1.0,
        state,
        EntityCollisionFlags::default(),
        0,
    );
    engine
}

#[test]
fn bsp_ethereal_non_static_is_passable() {
    let engine = engine_with_bsp_slab(ETHEREAL_PS);
    let (blocked, at) = sweep_until_blocked(
        &engine,
        Vec3::new(12.0, 11.0, 0.48),
        Vec3::new(0.0, 0.1, 0.0),
        30,
        LAYER2_CELL,
    );
    assert!(!blocked, "stopped at {at}");
}

#[test]
fn bsp_ethereal_static_still_blocks() {
    let engine = engine_with_bsp_slab(ETHEREAL_PS | PhysicsState::STATIC.bits());
    let (blocked, at) = sweep_until_blocked(
        &engine,
        Vec3::new(12.0, 11.0, 0.48),
        Vec3::new(0.0, 0.1, 0.0),
        30,
        LAYER2_CELL,
    );
    assert!(blocked, "reached {at}");
}

#[test]
fn bsp_non_ethereal_not_passable() {
    let engine = engine_with_bsp_slab(0);
    let (blocked, at) = sweep_until_blocked(
        &engine,
        Vec3::new(12.0, 11.0, 0.48),
        Vec3::new(0.0, 0.1, 0.0),
        30,
        LAYER2_CELL,
    );
    assert!(blocked, "reached {at}");
}

// ── BroadphaseReachFilterTests ─────────────────────────────────────────

const REACH_LANDBLOCK: u32 = 0xA9B6_0000;
const REACH_CELL: u32 = REACH_LANDBLOCK | 0x0001;
const REACH_ENTITY: u32 = 0x333B_EEF1;
const REACH_GFX: u32 = 0x333B_EEF2;
const PART_ORIGIN: Vec3 = Vec3::new(12.0, 12.0, 0.0);
const ROOT_SPHERE_RADIUS: f32 = 6.0;
const TALL: f32 = 1.835;

fn engine_with_floor_slab(slab_z: f32) -> PhysicsEngine {
    let mut engine = engine_at(REACH_LANDBLOCK, -1000.0);
    let floor = ResolvedPolygon::new(
        1,
        vec![
            Vec3::new(-4.0, -4.0, slab_z),
            Vec3::new(4.0, -4.0, slab_z),
            Vec3::new(4.0, 4.0, slab_z),
            Vec3::new(-4.0, 4.0, slab_z),
        ],
        Plane::new(Vec3::Z, -slab_z),
    );
    let mut gfx = gfx_obj(
        REACH_GFX,
        single_leaf(Vec3::new(0.0, 0.0, slab_z), ROOT_SPHERE_RADIUS, vec![floor]),
        ROOT_SPHERE_RADIUS,
    );
    gfx.bounding_sphere.center = Vec3::new(0.0, 0.0, slab_z);
    engine.world_mut().unwrap().insert_gfx_obj(gfx);
    register_single(
        &mut engine,
        REACH_ENTITY,
        REACH_GFX,
        PART_ORIGIN,
        Quat::IDENTITY,
        ROOT_SPHERE_RADIUS,
        REACH_LANDBLOCK,
        ShadowCollisionType::Bsp,
        0.0,
        1.0,
        PhysicsState::STATIC.bits(),
        EntityCollisionFlags::default(),
        0,
    );
    engine
}

fn drop_onto(engine: &PhysicsEngine, start_feet_z: f32) -> (f32, bool) {
    let mut pos = Vec3::new(PART_ORIGIN.x, PART_ORIGIN.y, start_feet_z);
    let mut blocked = false;
    for _ in 0..12 {
        let r = step(
            engine,
            pos,
            Vec3::new(0.0, 0.0, -0.30),
            REACH_CELL,
            TALL,
            false,
            None,
        );
        blocked |= r.collision_normal_valid;
        pos = r.position;
    }
    (pos.z, blocked)
}

#[test]
fn off_centre_bsp_floor_stops_a_falling_mover() {
    let engine = engine_with_floor_slab(40.0);
    assert!(!engine.shadows.objects_in_cell(REACH_CELL).is_empty());
    let (z, blocked) = drop_onto(&engine, 41.4);
    assert!(z > 39.5, "fell through to z {z} (blocked {blocked})");
}

#[test]
fn centred_bsp_floor_stops_a_falling_mover() {
    let engine = engine_with_floor_slab(0.0);
    assert!(!engine.shadows.objects_in_cell(REACH_CELL).is_empty());
    let (z, blocked) = drop_onto(&engine, 1.4);
    assert!(z > -0.5, "fell through to z {z} (blocked {blocked})");
}

// ── CrowdJumpTests ─────────────────────────────────────────────────────

#[test]
fn blocked_jump_velocity_bleeds_to_zero_then_gravity_resumes() {
    const DT: f32 = 1.0 / 30.0;
    let mut engine = engine_at(LANDBLOCK, 0.0);
    register_shadow(
        &mut engine,
        0xC0B0,
        0,
        Vec3::new(12.0, 10.0, 4.5),
        Quat::IDENTITY,
        RADIUS,
        LANDBLOCK,
        ShadowCollisionType::Sphere,
        0.0,
        1.0,
        0,
        EntityCollisionFlags(EntityCollisionFlags::IS_CREATURE),
        0,
        false,
    );

    let mut body = PhysicsBody::new();
    body.set_position(Vec3::new(12.0, 10.0, 3.0));
    body.orientation = Quat::IDENTITY;
    body.state = PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS;
    body.transient_state = TransientState::default();
    body.velocity = Vec3::new(0.0, 0.0, 12.0);
    let mut cell = CELL;

    let mut bled = false;
    for frame in 0..20 {
        let pre = body.position();
        body.calc_acceleration();
        body.update_physics_internal(DT);
        let post = body.position();
        let candidate_moved = post != pre;

        let r = step(&engine, pre, post - pre, cell, TALL, false, Some(&mut body));

        let prev_on_walkable = body.on_walkable();
        body.set_position(r.position);
        cell = r.cell_id;
        if r.is_on_ground && body.velocity.z <= 0.0 {
            body.transient_state
                .insert(TransientState::CONTACT | TransientState::ON_WALKABLE);
            body.calc_acceleration();
            if body.velocity.z < 0.0 {
                body.velocity.z = 0.0;
            }
        } else {
            body.transient_state
                .remove(TransientState::CONTACT | TransientState::ON_WALKABLE);
            body.calc_acceleration();
        }

        if candidate_moved {
            let now_on_walkable = body.on_walkable();
            handle_all_collisions(
                &mut body,
                r.collision_normal_valid,
                r.collision_normal,
                false,
                prev_on_walkable,
                now_on_walkable,
            );
        }

        if (1..=6).contains(&frame) && body.velocity.z <= 0.5 {
            bled = true;
        }
    }

    assert!(
        bled,
        "the blocked jump must bleed its velocity within a few frames"
    );
    assert!(
        body.velocity.z <= 0.5,
        "velocity kept pushing up: {}",
        body.velocity.z
    );
}
