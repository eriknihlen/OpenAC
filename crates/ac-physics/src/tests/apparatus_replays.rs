//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/HoltburgTightGapReplayTests.cs`,
//! `OutdoorStairsSeamReplayTests.cs`, `DoorwayMembershipReplayTests.cs`,
//! `TransitFailProbeTests.cs` and `SteepSlopeCaptureBisectTests.cs`.
//!
//! Dropped: the prepared-flat parity case (one tree representation here),
//! the transit-fail probe's log assertions (OpenAC's diagnostic probe; the
//! movement assertions are kept) and the diagnostic-only characterisation
//! dumps.

use glam::{Quat, Vec2, Vec3};

use super::bsp_step_up::{SPHERE_RADIUS, tall_wall};
use super::fixtures::*;
use super::support::*;
use crate::cell_array::CellArray;
use crate::cell_transit::find_cell_set;
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::{PhysicsEngine, ResolveRequest, ResolveResult};
use crate::geometry::Plane;
use crate::obj_update::handle_all_collisions;
use crate::polygon::{ResolvedPolygon, check_walkable_support};
use crate::shadow::ShadowCollisionType;
use crate::state::ObjectInfoState;
use crate::terrain::TerrainSurface;
use crate::transition::CollisionSphere;
use crate::world::{BuildingPhysics, CollisionWorld, GfxObjPhysics, Placement};
use crate::{PhysicsBody, PhysicsState, TransientState};

fn player() -> ObjectInfoState {
    ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE)
}

fn terrain_at(z: f32) -> TerrainSurface {
    TerrainSurface::new(&[0u8; 81], &[z; 256], 0, 0, None)
}

/// An engine whose landblock is added before the world is attached (as
/// OpenAC's harnesses that set `DataCache` afterwards), holding `gfx`.
fn late_world_engine(landblock: u32, terrain_z: f32, gfx: Vec<GfxObjPhysics>) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.add_landblock(
        landblock,
        terrain_at(terrain_z),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    let mut world = CollisionWorld::new();
    for g in gfx {
        world.insert_gfx_obj(g);
    }
    engine.attach_world(world);
    engine
}

// ── HoltburgTightGapReplayTests ────────────────────────────────────────

const GAP_LB: u32 = 0xA9B4_0000;
const GAP_CELL: u32 = 0xA9B4_0032;
const SHELL_GFX: u32 = 0x0100_0F69;

fn building_placement() -> Placement {
    Placement::new(
        crate::geometry::normalize_quat(Quat::from_xyzw(0.0, 0.0, -0.343_045, 0.939_319)),
        Vec3::new(158.178, 37.7055, 94.0),
    )
}

fn gap_engine(include_shell: bool, include_blocking_post: bool) -> PhysicsEngine {
    let mut world = CollisionWorld::new();
    world.insert_gfx_obj(load_gfx_obj("tight-gap", SHELL_GFX));
    if include_shell {
        world.cache_building(
            GAP_CELL,
            BuildingPhysics {
                placement: building_placement(),
                portals: Vec::new(),
                model_id: SHELL_GFX,
            },
        );
    }
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(
        GAP_LB,
        terrain_at(if include_shell { -1000.0 } else { 96.0 }),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    let mut post = |id: u32, base: Vec3, radius: f32| {
        register_shadow(
            &mut engine,
            id,
            0,
            base,
            Quat::IDENTITY,
            radius,
            GAP_LB,
            ShadowCollisionType::Cylinder,
            5.564,
            1.0,
            0,
            EntityCollisionFlags::default(),
            GAP_CELL,
            true,
        );
    };
    if include_blocking_post {
        post(0xCA9B_4027, Vec3::new(160.173, 34.487, 95.975), 0.282);
    }
    post(0xCA9B_402E, Vec3::new(158.282, 34.610, 95.975), 0.282);
    post(0xCA9B_402F, Vec3::new(157.952, 32.239, 96.0), 0.600);
    engine
}

const GAP_START: Vec3 = Vec3::new(160.016, 33.562, 96.005);

fn gap_body(include_shell: bool) -> PhysicsBody {
    let placement = building_placement();
    let floor = Plane::new(Vec3::Z, -96.0);
    let mut b = PhysicsBody::new();
    b.set_position(GAP_START);
    b.orientation = Quat::IDENTITY;
    b.contact_plane_valid = true;
    b.contact_plane = floor;
    b.contact_plane_cell_id = GAP_CELL;
    b.walkable_polygon_valid = include_shell;
    b.walkable_plane = floor;
    b.walkable_up = Vec3::Z;
    b.walkable_vertices = include_shell.then(|| {
        [
            Vec3::new(4.0, 7.25, 2.0),
            Vec3::new(3.3, 6.5003, 2.0),
            Vec3::new(3.3, -2.0157, 2.0),
            Vec3::new(4.0, -4.7, 2.0),
        ]
        .iter()
        .map(|v| placement.to_world(*v))
        .collect()
    });
    b.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    b
}

fn gap_replay(engine: &PhysicsEngine, body: &mut PhysicsBody) -> Vec3 {
    let offsets = [
        Vec3::new(1.396, 1.123, 0.0),
        Vec3::new(0.727, 0.584, 0.0),
        Vec3::new(0.624, 0.501, 0.0),
        Vec3::new(0.727, 0.585, 0.0),
        Vec3::new(0.728, 0.585, 0.0),
        Vec3::new(0.727, 0.585, 0.0),
        Vec3::new(0.727, 0.585, 0.0),
        Vec3::new(0.728, 0.585, 0.0),
    ];
    let mut position = GAP_START;
    let mut cell = GAP_CELL;
    for offset in offsets {
        let r = engine.resolve_with_transition(
            &ResolveRequest {
                current_pos: position,
                target_pos: position + offset,
                cell_id: cell,
                sphere_radius: 0.48,
                sphere_height: 1.835,
                step_up_height: 0.6,
                step_down_height: 1.5,
                is_on_ground: true,
                mover_flags: player(),
                moving_entity_id: 0x000F_4243,
                sphere_list: vec![
                    CollisionSphere::new(Vec3::new(0.0, 0.0, 0.475), 0.48),
                    CollisionSphere::new(Vec3::new(0.0, 0.0, 1.350), 0.48),
                ],
                sphere_scale: 1.0,
                ..ResolveRequest::default()
            },
            Some(body),
        );
        position = r.position;
        cell = r.cell_id;
        body.set_position(position);
    }
    position
}

#[test]
fn retail_half_radius_support_rejects_center_beyond_quarter_meter_edge() {
    let floor = [Vec3::ZERO, Vec3::X, Vec3::new(1.0, 1.0, 0.0), Vec3::Y];
    for (x, expected) in [(1.239, true), (1.241, false)] {
        assert_eq!(
            check_walkable_support(
                &Plane::new(Vec3::Z, 0.0),
                &floor,
                Vec3::new(x, 0.5, 0.48),
                0.24,
                Vec3::Z
            ),
            expected,
            "x {x}"
        );
    }
}

#[test]
fn captured_run_does_not_squeeze_between_post_and_building() {
    let engine = gap_engine(true, true);
    let p = gap_replay(&engine, &mut gap_body(true));
    assert!(p.y < 34.487, "squeezed through to {p}");
}

#[test]
fn captured_run_requires_both_shell_and_blocking_post() {
    for (shell, post, omitted) in [(false, true, "shell"), (true, false, "blocking post")] {
        let engine = gap_engine(shell, post);
        let p = gap_replay(&engine, &mut gap_body(shell));
        assert!(p.y > 34.487, "omitting the {omitted} still blocked: {p}");
    }
}

// ── OutdoorStairsSeamReplayTests ───────────────────────────────────────

const STAIR_CELL: u32 = 0xF682_002C;
const STAIR_LB: u32 = 0xF682_0000;
const STEP_GFX: u32 = 0x0100_0AC5;

fn stair_engine() -> PhysicsEngine {
    let step = load_gfx_obj("outdoor-stairs-seam", STEP_GFX);
    let radius = step.bounding_sphere.radius;
    let mut engine = late_world_engine(STAIR_LB, -1000.0, vec![step]);
    let rotation = Quat::from_axis_angle(Vec3::Z, std::f32::consts::FRAC_PI_2);
    for k in 0..8u32 {
        let origin =
            Vec3::new(132.0, 75.245, 58.815) + Vec3::new(0.0, 0.5 * k as f32, 0.4 * k as f32);
        register_single(
            &mut engine,
            0xF682_0100 + k,
            STEP_GFX,
            origin,
            rotation,
            radius,
            STAIR_LB,
            ShadowCollisionType::Bsp,
            0.0,
            1.0,
            0,
            EntityCollisionFlags::default(),
            STAIR_CELL,
        );
    }
    engine
}

fn grounded_on_tread() -> PhysicsBody {
    let tread = Plane::new(Vec3::new(0.0, -0.624_695_06, 0.780_868_77), 0.765_995);
    let mut b = PhysicsBody::new();
    b.set_position(Vec3::new(131.72375, 77.49132, 61.146_755));
    b.orientation = Quat::IDENTITY;
    b.contact_plane_valid = true;
    b.contact_plane = tread;
    b.contact_plane_cell_id = STAIR_CELL;
    b.walkable_polygon_valid = true;
    b.walkable_plane = tread;
    b.walkable_up = Vec3::Z;
    b.walkable_vertices = Some(vec![
        Vec3::new(132.75, 77.495, 61.015),
        Vec3::new(131.25, 77.495, 61.015),
        Vec3::new(131.25, 76.995, 60.615),
        Vec3::new(132.75, 76.995, 60.615),
    ]);
    b.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    b
}

fn stair_step(
    engine: &PhysicsEngine,
    body: &mut PhysicsBody,
    from: Vec3,
    to: Vec3,
    cell: u32,
) -> ResolveResult {
    engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: from,
            target_pos: to,
            cell_id: cell,
            sphere_radius: 0.48,
            sphere_height: 1.835,
            step_up_height: 0.6,
            step_down_height: 1.5,
            is_on_ground: true,
            mover_flags: player(),
            moving_entity_id: 0x0100_0000,
            ..ResolveRequest::default()
        },
        Some(body),
    )
}

#[test]
fn outdoor_stairs_forward_run_climbs_past_seam_no_wedge() {
    let engine = stair_engine();
    let mut body = grounded_on_tread();
    let mut pos = body.position();
    let mut cell = STAIR_CELL;
    for _ in 0..12 {
        let r = stair_step(
            &engine,
            &mut body,
            pos,
            Vec3::new(pos.x, pos.y + 0.2, pos.z),
            cell,
        );
        pos = r.position;
        cell = r.cell_id;
        body.set_position(pos);
    }
    assert!(pos.y > 78.10, "pinned at y {}", pos.y);
    assert!(!body.transient_state.contains(TransientState::SLIDING));
}

fn side_wall_body(x: f32) -> PhysicsBody {
    let mut b = grounded_on_tread();
    b.set_position(Vec3::new(x, 75.53931, 59.608_147));
    let tread = Plane::new(
        Vec3::new(3.278_255_5e-7, -0.624_695_06, 0.780_868_77),
        0.751_934_05,
    );
    b.contact_plane = tread;
    b.walkable_plane = tread;
    let delta = Vec3::new(0.0, 1.5, 1.2);
    b.walkable_vertices = b
        .walkable_vertices
        .take()
        .map(|v| v.into_iter().map(|p| p - delta).collect());
    b
}

const SIDE_WALL_MOVE: Vec3 = Vec3::new(0.300_079_35, 0.883_773_8, 0.0);

#[test]
fn outdoor_stairs_side_wall_contact_does_not_reverse_downhill() {
    let engine = stair_engine();
    let mut body = side_wall_body(133.03775);
    let start = body.position();
    let r = stair_step(
        &engine,
        &mut body,
        start,
        start + SIDE_WALL_MOVE,
        STAIR_CELL,
    );
    assert!(
        r.position.y >= start.y - 0.001,
        "{} -> {}",
        start.y,
        r.position.y
    );
    assert!(
        r.position.z >= start.z - 0.001,
        "{} -> {}",
        start.z,
        r.position.z
    );
}

#[test]
fn outdoor_stairs_side_wall_contact_inside_half_radius_advances_uphill() {
    let engine = stair_engine();
    let mut body = side_wall_body(132.95);
    let start = body.position();
    let r = stair_step(
        &engine,
        &mut body,
        start,
        start + SIDE_WALL_MOVE,
        STAIR_CELL,
    );
    assert!(
        r.position.y > start.y + 0.25,
        "{} -> {}",
        start.y,
        r.position.y
    );
    assert!(
        r.position.z > start.z + 0.10,
        "{} -> {}",
        start.z,
        r.position.z
    );
}

// ── DoorwayMembershipReplayTests ───────────────────────────────────────

fn seam_records() -> Vec<CaptureRecord> {
    let mut records = load_captures("cellar-ascent", "doorway-threshold-capture.jsonl");
    records.truncate(80);
    records
}

fn membership(rec: &CaptureRecord) -> u32 {
    let mut cells = CellArray::new();
    find_cell_set(
        &CollisionWorld::new(),
        rec.input.current_pos,
        0.48,
        rec.input.cell_id,
        &mut cells,
        None,
    )
}

#[test]
fn doorway_seam_find_cell_set_stable_no_strobe() {
    let records = seam_records();
    assert!(!records.is_empty());
    let out: Vec<u32> = records.iter().map(membership).collect();
    for i in 0..out.len().saturating_sub(2) {
        if out[i] == out[i + 2] && out[i] != out[i + 1] {
            let span = records[i]
                .input
                .current_pos
                .distance(records[i + 2].input.current_pos);
            assert!(
                span >= 0.1,
                "ping-pong at {i}: {:#010X} -> {:#010X}",
                out[i],
                out[i + 1]
            );
        }
    }
}

#[test]
fn outdoor_seam_records_find_cell_set_returns_correct_outdoor_cell() {
    let mut mismatches = 0;
    for rec in seam_records()
        .iter()
        .filter(|r| r.input.cell_id & 0xFFFF < 0x0100)
    {
        let live = rec.result.cell_id;
        if live & 0xFFFF < 0x0100 && membership(rec) != live {
            mismatches += 1;
        }
    }
    assert_eq!(mismatches, 0);
}

// ── TransitFailProbeTests ──────────────────────────────────────────────

fn transit_engine() -> PhysicsEngine {
    const GFX: u32 = 0x0100_F100;
    let mut gfx = gfx_obj(GFX, tall_wall(), 10.0);
    gfx.bounding_sphere.center = Vec3::new(0.0, 0.0, 2.5);
    let mut engine = late_world_engine(0xA9B4_FFFF, -50.0, vec![gfx]);
    register_single(
        &mut engine,
        GFX,
        GFX,
        Vec3::ZERO,
        Quat::IDENTITY,
        10.0,
        0xA9B4_FFFF,
        ShadowCollisionType::Bsp,
        0.0,
        1.0,
        0,
        EntityCollisionFlags::default(),
        0,
    );
    engine
}

fn fresh_body() -> PhysicsBody {
    let mut b = PhysicsBody::new();
    b.state = PhysicsState::GRAVITY;
    b.transient_state = TransientState::ACTIVE;
    b
}

fn transit_move(from: Vec3, to: Vec3) -> Vec3 {
    let engine = transit_engine();
    let mut body = fresh_body();
    engine
        .resolve_with_transition(
            &ResolveRequest {
                current_pos: from,
                target_pos: to,
                cell_id: 0xA9B4_0001,
                sphere_radius: SPHERE_RADIUS,
                sphere_height: 1.20,
                step_up_height: 0.60,
                step_down_height: 1.50,
                is_on_ground: true,
                mover_flags: player(),
                moving_entity_id: 0x5000_000A,
                ..ResolveRequest::default()
            },
            Some(&mut body),
        )
        .position
}

#[test]
fn stuck_tick_wall_absorbs_whole_request() {
    let p = transit_move(Vec3::new(0.30, 0.0, 0.20), Vec3::new(0.60, 0.0, 0.20));
    let moved = Vec2::new(p.x - 0.30, p.y).length();
    assert!(moved < 0.01, "moved {moved} to {p}");
}

#[test]
fn ordinary_moving_tick_advances() {
    let p = transit_move(Vec3::new(-1.50, 0.0, 0.20), Vec3::new(-1.50, -0.30, 0.20));
    assert!(p.y.abs() > 0.20, "y {}", p.y);
}

// ── SteepSlopeCaptureBisectTests ───────────────────────────────────────

const ROOF_V0: Vec3 = Vec3::new(240.0, 0.0, 88.0);
const ROOF_V1: Vec3 = Vec3::new(264.0, 0.0, 80.0);
const ROOF_V2: Vec3 = Vec3::new(264.0, 24.0, 68.0);
const ROOF_CELL: u32 = 0x0000_0001;
const ROOF_GFX: u32 = 0x265B_EEF1;
const DT: f32 = 1.0 / 30.0;
const APPROACH_POS: Vec3 = Vec3::new(244.59, -0.81, 92.79);
const APPROACH_VEL: Vec3 = Vec3::new(11.150_95, 14.129_979, -16.72);

fn roof_centroid() -> Vec3 {
    (ROOF_V0 + ROOF_V1 + ROOF_V2) / 3.0
}

fn roof_engine(scale: f32) -> PhysicsEngine {
    let bounding = 30.0 * scale.max(1.0);
    let c = roof_centroid();
    let v = [
        (ROOF_V0 - c) * scale,
        (ROOF_V1 - c) * scale,
        (ROOF_V2 - c) * scale,
    ];
    let normal = crate::geometry::normalize((v[1] - v[0]).cross(v[2] - v[0]));
    let d = -normal.dot(v[0]);
    let gfx = gfx_obj(
        ROOF_GFX,
        single_leaf(
            Vec3::ZERO,
            bounding,
            vec![ResolvedPolygon::new(1, v.to_vec(), Plane::new(normal, d))],
        ),
        bounding,
    );
    let mut engine = late_world_engine(0x0000_0000, -1000.0, vec![gfx]);
    register_single(
        &mut engine,
        ROOF_GFX,
        ROOF_GFX,
        Vec3::ZERO,
        Quat::IDENTITY,
        bounding,
        0x0000_0000,
        ShadowCollisionType::Bsp,
        0.0,
        1.0,
        0,
        EntityCollisionFlags::default(),
        ROOF_CELL,
    );
    engine
}

#[derive(Clone, Copy)]
struct ComposedSample {
    pos: Vec3,
    velocity: Vec3,
    on_walkable: bool,
    frozen: usize,
}

fn roof_resolve(
    engine: &PhysicsEngine,
    body: &mut PhysicsBody,
    from: Vec3,
    to: Vec3,
    cell: u32,
    grounded: bool,
) -> ResolveResult {
    engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: from,
            target_pos: to,
            cell_id: cell,
            sphere_radius: 0.48,
            sphere_height: 1.835,
            step_up_height: 0.6,
            step_down_height: 1.5,
            is_on_ground: grounded,
            mover_flags: player(),
            moving_entity_id: 0x0100_0000,
            ..ResolveRequest::default()
        },
        Some(body),
    )
}

/// One composed tick: integrate, sweep, settle contact, respond.
fn composed_tick(engine: &PhysicsEngine, body: &mut PhysicsBody, cell: &mut u32) -> (f32, bool) {
    let pre = body.position();
    let on_ground_before = body.on_walkable();
    body.calc_acceleration();
    body.update_physics_internal(DT);
    let post = body.position();
    let moved = post != pre;
    let r = roof_resolve(engine, body, pre, post, *cell, on_ground_before);
    let advance = r.position.distance(pre);
    let prev_contact = body.in_contact();
    let prev_on_walkable = body.on_walkable();
    body.set_position(r.position);
    *cell = r.cell_id;
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
    if moved {
        let now = body.on_walkable();
        handle_all_collisions(
            body,
            r.collision_normal_valid,
            r.collision_normal,
            prev_contact,
            prev_on_walkable,
            now,
        );
    }
    (advance, on_ground_before)
}

fn replay_roof_composed(preserve_residual: bool) -> Vec<ComposedSample> {
    let engine = roof_engine(6.0);
    let mut body = PhysicsBody::new();
    body.transient_state = TransientState::ACTIVE;
    body.set_position(APPROACH_POS - roof_centroid());
    body.velocity = APPROACH_VEL;
    let mut cell = ROOF_CELL;
    let mut frozen = 0;
    let mut since_grounded: Option<usize> = None;
    let mut samples = Vec::new();
    for _ in 0..(18 + 90 + 20) {
        if body.on_walkable() && !preserve_residual {
            body.velocity = Vec3::new(0.0, 0.0, body.velocity.z);
        }
        let (advance, on_ground_before) = composed_tick(&engine, &mut body, &mut cell);
        frozen = if advance < 0.001 { frozen + 1 } else { 0 };
        samples.push(ComposedSample {
            pos: body.position(),
            velocity: body.velocity,
            on_walkable: body.on_walkable(),
            frozen,
        });
        if !on_ground_before && body.on_walkable() {
            since_grounded = Some(0);
        } else if body.on_walkable() {
            let n = since_grounded.map_or(1, |n| n + 1);
            since_grounded = Some(n);
            if n >= 90 {
                break;
            }
        }
    }
    samples
}

#[test]
fn real_captured_roof_landing_reaches_the_roof() {
    let engine = roof_engine(1.0);
    let mut body = PhysicsBody::new();
    body.transient_state = TransientState::ACTIVE;
    let mut pos = APPROACH_POS - roof_centroid();
    let mut vel = APPROACH_VEL;
    let mut cell = ROOF_CELL;
    let mut grounded = false;
    let mut landed = None;
    let mut since = 0;
    for tick in 0..(18 + 60 + 20) {
        if !grounded {
            vel.z += -9.8 * DT;
        }
        let requested = if grounded {
            Vec3::new(vel.x, vel.y, 0.0)
        } else {
            vel
        };
        let r = roof_resolve(
            &engine,
            &mut body,
            pos,
            pos + requested * DT,
            cell,
            grounded,
        );
        if landed.is_none() && r.is_on_ground {
            landed = Some(tick);
        }
        pos = r.position;
        cell = r.cell_id;
        body.set_position(pos);
        if !grounded && r.is_on_ground {
            grounded = true;
            since = 0;
        } else if grounded {
            since += 1;
            if since >= 60 {
                break;
            }
        }
    }
    assert!(
        landed.is_some_and(|t| t < 30),
        "never reached the roof: {landed:?}"
    );
}

#[test]
fn composed_roof_landing_old_zeroing_model_reproduces_the_freeze() {
    let samples = replay_roof_composed(false);
    let landed = samples.iter().position(|s| s.on_walkable).expect("landed");
    assert!(landed < 30);
    assert_eq!(samples[landed + 1].velocity, Vec3::ZERO);
    assert!(
        samples.last().unwrap().frozen >= 40,
        "frozen {}",
        samples.last().unwrap().frozen
    );
}

#[test]
fn composed_roof_landing_new_fix_velocity_survives_and_keeps_advancing() {
    let samples = replay_roof_composed(true);
    let landed = samples.iter().position(|s| s.on_walkable).expect("landed");
    assert!(landed < 30);
    let after = samples[landed + 1].velocity;
    assert!(
        after.truncate().length() > 5.0,
        "speed after landing {after}"
    );
    let max_frozen = samples.iter().map(|s| s.frozen).max().unwrap();
    assert!(max_frozen < 10, "max frozen {max_frozen}");
    let travel = samples[landed].pos.distance(samples.last().unwrap().pos);
    assert!(travel > 1.0, "travel {travel}");
}

#[test]
fn composed_roof_landing_synthetic_grazing_approach_decays_via_friction() {
    let engine = roof_engine(1.0);
    let mut body = PhysicsBody::new();
    body.transient_state = TransientState::ACTIVE;
    let approach = crate::geometry::normalize(Vec3::new(0.4286, -0.2857, 0.0)) * 6.0;
    body.set_position(Vec3::new(0.0, 0.0, 12.0));
    body.velocity = Vec3::new(approach.x, approach.y, -6.0);
    let mut cell = ROOF_CELL;
    let mut samples = Vec::new();
    let mut since_grounded: Option<usize> = None;
    for _ in 0..80 {
        let (_, on_ground_before) = composed_tick(&engine, &mut body, &mut cell);
        samples.push((body.velocity, body.on_walkable()));
        if !on_ground_before && body.on_walkable() {
            since_grounded = Some(0);
        } else if body.on_walkable() {
            let n = since_grounded.map_or(1, |n| n + 1);
            since_grounded = Some(n);
            if n >= 40 {
                break;
            }
        }
    }
    let landed = samples.iter().position(|s| s.1).expect("landed");
    assert!(landed < 40);
    let at_landing = samples[landed].0.truncate().length();
    let at_end = samples.last().unwrap().0.truncate().length();
    assert!(at_landing > 3.0, "landing speed {at_landing}");
    assert!(at_end < at_landing * 0.5, "{at_landing} -> {at_end}");
}

#[test]
fn uphill_landing_reflection_decision_unaffected_by_residual_velocity_fix() {
    let slope = 30f32.to_radians();
    let uphill = Vec3::new(-slope.sin(), 0.0, slope.cos());
    let run_once = |preserve: bool| {
        let mut body = PhysicsBody::new();
        body.transient_state = TransientState::ACTIVE;
        body.velocity = Vec3::new(5.0, 0.0, -2.0);
        body.ground_normal = uphill;
        let prev_contact = body.in_contact();
        let prev_on_walkable = body.on_walkable();
        body.transient_state
            .insert(TransientState::CONTACT | TransientState::ON_WALKABLE);
        body.calc_acceleration();
        if body.velocity.z < 0.0 {
            body.velocity.z = 0.0;
        }
        let now = body.on_walkable();
        handle_all_collisions(&mut body, true, uphill, prev_contact, prev_on_walkable, now);
        if body.on_walkable() && !preserve {
            body.velocity = Vec3::new(0.0, 0.0, body.velocity.z);
        }
        body.velocity
    };
    assert_eq!(run_once(false).z > 0.01, run_once(true).z > 0.01);
}
