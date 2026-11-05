//! OpenAC's installed-dat apparatus and replays, ported from
//! `tests/AcDream.Core.Tests/Physics/DoorCollisionApparatusTests.cs`,
//! `DoorBugTrajectoryReplayTests.cs`, `CorridorSeamReplayTests.cs`,
//! `CorridorSweepHysteresisReplayTests.cs`, `SeamTransitLagTests.cs`,
//! `CameraCornerSealReplayTests.cs` and `CellarAscentViewerReplayTests.cs`.
//!
//! They need the retail dats (`OPENAC_DAT_DIR`, else `Documents\Asheron's
//! Call`) and are ignored by default; `scripts/check.ps1` runs them when the
//! dats are present. Diagnostic-only dumps and OpenAC's flat-versus-graph
//! parity halves are not ported; `SortingSphereFloodMeasurementTests` is a
//! measurement report with no behavioral assertion and is not ported.

use std::path::PathBuf;

use ac_dat::DatCollection;
use ac_dat::objects::{EnvCell, Environment, GfxObj, LandBlockInfo, Setup};
use glam::{Quat, Vec3};

use super::fixtures::*;
use super::support::*;
use crate::cell_array::CellArray;
use crate::cell_transit::{
    CellCollector, add_all_outside_cells, find_cell_set, find_transit_cells_sphere,
};
use crate::collision_flags::EntityCollisionFlags;
use crate::dat::{quat, vec3};
use crate::engine::{PhysicsEngine, ResolveRequest, ResolveResult};
use crate::geometry::{Plane, Sphere};
use crate::shadow::{ShadowCollisionType, ShadowPartGeometry, ShadowShape};
use crate::shadow_builder::from_setup;
use crate::shadow_registry::RegistrationSite;
use crate::state::{InsertType, ObjectInfoState, TransitionState};
use crate::terrain::TerrainSurface;
use crate::transition::Transition;
use crate::world::{BldPortalInfo, BuildingPhysics, CollisionWorld, Placement};
use crate::{PhysicsBody, TransientState};

pub(super) fn dats() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).expect("open the retail dats")
}

/// Caches one interior cell (and enters it into the cell graph). False when
/// the dats lack it.
pub(super) fn load_env_cell(d: &DatCollection, world: &mut CollisionWorld, cell_id: u32) -> bool {
    let Ok(Some(cell)) = d.get::<EnvCell>(cell_id) else {
        return false;
    };
    let Ok(Some(env)) = d.get::<Environment>(cell.environment_file_id()) else {
        return false;
    };
    let Some(structure) = env.cell(u32::from(cell.cell_structure)) else {
        return false;
    };
    let placement = Placement::new(quat(cell.position.orientation), vec3(cell.position.origin));
    world
        .cache_cell_struct(cell_id, &cell, structure, placement)
        .expect("cell trees prepare");
    true
}

fn deep_terrain() -> TerrainSurface {
    TerrainSurface::new(&[0u8; 81], &[-1000.0; 256], 0, 0, None)
}

fn player() -> ObjectInfoState {
    ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE)
}

fn viewer() -> ObjectInfoState {
    ObjectInfoState(
        ObjectInfoState::IS_VIEWER
            | ObjectInfoState::PATH_CLIPPED
            | ObjectInfoState::FREE_ROTATE
            | ObjectInfoState::PERFECT_CLIP,
    )
}

#[allow(clippy::too_many_arguments)]
fn request(
    from: Vec3,
    to: Vec3,
    cell: u32,
    radius: f32,
    height: f32,
    up: f32,
    down: f32,
    grounded: bool,
    mover: ObjectInfoState,
    id: u32,
) -> ResolveRequest {
    ResolveRequest {
        current_pos: from,
        target_pos: to,
        cell_id: cell,
        sphere_radius: radius,
        sphere_height: height,
        step_up_height: up,
        step_down_height: down,
        is_on_ground: grounded,
        mover_flags: mover,
        moving_entity_id: id,
        ..ResolveRequest::default()
    }
}

fn floor_body(pos: Vec3, plane: Plane, cell: u32, vertices: Vec<Vec3>) -> PhysicsBody {
    let mut b = PhysicsBody::new();
    b.set_position(pos);
    b.orientation = Quat::IDENTITY;
    b.contact_plane_valid = true;
    b.contact_plane = plane;
    b.contact_plane_cell_id = cell;
    b.walkable_polygon_valid = true;
    b.walkable_plane = plane;
    b.walkable_vertices = Some(vertices);
    b.walkable_up = Vec3::Z;
    b.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    b
}

// ── DoorCollisionApparatusTests ────────────────────────────────────────

const DOOR_SETUP: u32 = 0x0200_19FF;
const DOOR_FRAME_GFX: u32 = 0x0100_44B5;
const HOLTBURG: u32 = 0xA9B4_0000;
const TEST_CELL: u32 = HOLTBURG | 0x0001;

/// The door model and setup from the dats; the world caches the frame.
fn door_world(d: &DatCollection) -> (CollisionWorld, Setup) {
    let gfx: GfxObj = d.get(DOOR_FRAME_GFX).unwrap().expect("door frame");
    let mut world = CollisionWorld::new();
    world.cache_gfx_obj(&gfx).unwrap();
    assert!(world.gfx_obj(DOOR_FRAME_GFX).is_some());
    let setup: Setup = d.get(DOOR_SETUP).unwrap().expect("door setup");
    (world, setup)
}

fn apparatus_engine() -> PhysicsEngine {
    let d = dats();
    let (world, setup) = door_world(&d);
    let shapes = from_setup(
        &setup,
        1.0,
        &|id| world.gfx_obj(id).is_some_and(|g| g.has_physics()),
        None,
        None,
        None,
        None,
    );
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(HOLTBURG, deep_terrain(), Vec::new(), Vec::new(), 0.0, 0.0);
    let (shadows, world) = engine.shadows_and_world();
    shadows
        .register_multi_part(
            world,
            0xF424F,
            &shapes,
            RegistrationSite {
                world_pos: Vec3::new(12.0, 12.0, 0.0),
                rotation: Quat::IDENTITY,
                world_offset_x: 0.0,
                world_offset_y: 0.0,
                landblock_id: HOLTBURG,
                seed_cell_id: 0,
            },
            0x10008,
            EntityCollisionFlags::default(),
            false,
            None,
        )
        .unwrap();
    engine
}

fn sweep_until_blocked(engine: &PhysicsEngine, start: Vec3, per_tick: Vec3) -> (bool, Vec3) {
    let mut pos = start;
    let mut cell = TEST_CELL;
    let mut grounded = false;
    for _ in 0..30 {
        let r = engine.resolve_with_transition(
            &request(
                pos,
                pos + per_tick,
                cell,
                0.48,
                1.20,
                0.60,
                0.04,
                grounded,
                player(),
                0,
            ),
            None,
        );
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
#[ignore = "needs the retail dat files"]
fn apparatus_dead_center_front_approach_blocks_on_bsp() {
    let (blocked, at) = sweep_until_blocked(
        &apparatus_engine(),
        Vec3::new(12.0, 11.0, 0.5),
        Vec3::new(0.0, 0.10, 0.0),
    );
    assert!(blocked, "reached {at}");
    assert!(at.y < 12.0, "y {}", at.y);
}

#[test]
#[ignore = "needs the retail dat files"]
fn apparatus_single_large_tick_jump_dead_center_still_blocks_on_bsp() {
    let engine = apparatus_engine();
    let r = engine.resolve_with_transition(
        &request(
            Vec3::new(12.0, 11.0, 0.5),
            Vec3::new(12.0, 13.0, 0.5),
            TEST_CELL,
            0.48,
            1.20,
            0.60,
            0.04,
            false,
            player(),
            0,
        ),
        None,
    );
    assert!(r.collision_normal_valid);
    assert!(r.position.y < 12.0, "y {}", r.position.y);
}

#[test]
#[ignore = "needs the retail dat files"]
fn apparatus_50cm_off_center_front_approach_blocks_on_bsp() {
    let (blocked, at) = sweep_until_blocked(
        &apparatus_engine(),
        Vec3::new(12.5, 11.0, 0.5),
        Vec3::new(0.0, 0.10, 0.0),
    );
    assert!(blocked, "reached {at}");
    assert!(at.y < 12.0);
}

#[test]
#[ignore = "needs the retail dat files"]
fn apparatus_dead_center_back_approach_blocks_on_bsp() {
    let (blocked, at) = sweep_until_blocked(
        &apparatus_engine(),
        Vec3::new(12.0, 13.0, 0.5),
        Vec3::new(0.0, -0.10, 0.0),
    );
    assert!(blocked, "reached {at}");
    assert!(at.y > 12.30, "y {}", at.y);
}

#[test]
#[ignore = "needs the retail dat files"]
fn apparatus_grounded_50cm_off_center_front_approach_blocks() {
    let engine = apparatus_engine();
    let mut body = floor_body(
        Vec3::new(12.5, 11.0, 0.48),
        Plane::new(Vec3::Z, 0.0),
        TEST_CELL,
        vec![
            Vec3::ZERO,
            Vec3::new(24.0, 0.0, 0.0),
            Vec3::new(24.0, 24.0, 0.0),
            Vec3::new(0.0, 24.0, 0.0),
        ],
    );
    let mut pos = body.position();
    let mut cell = TEST_CELL;
    let mut grounded = true;
    let mut blocked = false;
    for _ in 0..30 {
        let r = engine.resolve_with_transition(
            &request(
                pos,
                pos + Vec3::new(0.0, 0.10, 0.0),
                cell,
                0.48,
                1.20,
                0.60,
                0.04,
                grounded,
                player(),
                0,
            ),
            Some(&mut body),
        );
        body.set_position(r.position);
        pos = r.position;
        cell = r.cell_id;
        grounded = r.is_on_ground;
        if r.collision_normal_valid {
            blocked = true;
            break;
        }
    }
    assert!(blocked, "reached {pos}");
    assert!(pos.y < 12.0);
}

// ── DoorBugTrajectoryReplayTests ───────────────────────────────────────

const DOOR_ENTITY: u32 = 0x000F_4246;
const DOOR_CLOSED: u32 = 0x0001_0008;
const DOOR_SPAWN: Vec3 = Vec3::new(132.6, 17.1, 94.1);
const DOOR_CELL_OUTDOOR: u32 = 0xA9B4_0029;
const ALCOVE: u32 = 0xA9B4_0150;

fn door_engine_with(
    world: CollisionWorld,
    shapes: &[ShadowShape],
    pos: Vec3,
    rot: Quat,
) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(HOLTBURG, deep_terrain(), Vec::new(), Vec::new(), 0.0, 0.0);
    let (shadows, world) = engine.shadows_and_world();
    shadows
        .register_multi_part(
            world,
            DOOR_ENTITY,
            shapes,
            RegistrationSite {
                world_pos: pos,
                rotation: rot,
                world_offset_x: 0.0,
                world_offset_y: 0.0,
                landblock_id: HOLTBURG,
                seed_cell_id: 0,
            },
            DOOR_CLOSED,
            EntityCollisionFlags::default(),
            false,
            None,
        )
        .unwrap();
    engine
}

/// OpenAC's `BuildEngineWithDoorFixture`: the captured door as a cylinder
/// and the frame's tree, both at the captured world positions.
fn captured_door_engine(d: &DatCollection) -> PhysicsEngine {
    let (world, _) = door_world(d);
    let bsp_world = Vec3::new(132.57, 16.99, 95.36);
    let cyl_world = Vec3::new(132.56, 17.11, 94.10);
    let shapes = [
        ShadowShape::cylinder(0, cyl_world - bsp_world, Quat::IDENTITY, 1.0, 0.10, 0.20),
        ShadowShape::bsp(
            DOOR_FRAME_GFX,
            Vec3::ZERO,
            Quat::IDENTITY,
            1.0,
            ShadowPartGeometry::new(Sphere::new(Vec3::ZERO, 1.975), None),
        ),
    ];
    door_engine_with(world, &shapes, bsp_world, Quat::IDENTITY)
}

/// OpenAC's `BuildFaithfulDoorEngine`: the door built from its setup like
/// production, each tree part's radius replaced by its model's sphere.
fn faithful_door_engine(d: &DatCollection) -> PhysicsEngine {
    let (world, setup) = door_world(d);
    let raw = from_setup(
        &setup,
        1.0,
        &|id| world.gfx_obj(id).is_some_and(|g| g.has_physics()),
        None,
        None,
        None,
        None,
    );
    let shapes: Vec<ShadowShape> = raw
        .into_iter()
        .map(|s| {
            if s.collision_type != ShadowCollisionType::Bsp {
                return s;
            }
            let radius = world
                .gfx_obj(s.gfx_obj_id)
                .map_or(2.0, |g| g.bounding_sphere.radius);
            ShadowShape::bsp(
                s.gfx_obj_id,
                s.local_position,
                s.local_rotation,
                s.scale,
                ShadowPartGeometry::new(Sphere::new(Vec3::ZERO, radius / s.scale), None),
            )
        })
        .collect();
    assert!(
        shapes
            .iter()
            .any(|s| s.collision_type == ShadowCollisionType::Bsp)
    );
    door_engine_with(
        world,
        &shapes,
        DOOR_SPAWN,
        Quat::from_axis_angle(Vec3::Z, std::f32::consts::PI),
    )
}

fn door_capture(tick: i64) -> CaptureRecord {
    load_captures("door-bug", "live-capture.jsonl")
        .into_iter()
        .find(|r| r.tick == tick)
        .expect("captured record")
}

fn replay_capture(engine: &PhysicsEngine, rec: &CaptureRecord) -> ResolveResult {
    let mut body = seed_body(rec.body_before.as_ref().expect("body before"));
    replay(engine, &rec.input, Some(&mut body))
}

#[test]
#[ignore = "needs the retail dat files"]
fn live_compare_door_off_center_walkthrough_tick_13558() {
    let r = replay_capture(&captured_door_engine(&dats()), &door_capture(13558));
    assert!(r.collision_normal_valid, "walked through to {}", r.position);
    assert!(r.position.y <= 17.0, "y {}", r.position.y);
}

#[test]
#[ignore = "needs the retail dat files"]
fn live_compare_door_blocks_from_outside_tick_22760() {
    let r = replay_capture(&captured_door_engine(&dats()), &door_capture(22760));
    assert!(r.collision_normal_valid, "not blocked at {}", r.position);
    assert!(r.position.y >= 17.95, "y {}", r.position.y);
}

#[test]
#[ignore = "needs the retail dat files"]
fn find_transit_cells_sphere_indoor_exit_portal_adds_outside_for_captured_sphere() {
    let d = dats();
    let mut world = CollisionWorld::new();
    assert!(load_env_cell(&d, &mut world, ALCOVE));
    let cell = world.cell(ALCOVE).expect("alcove");
    assert!(cell.portals.iter().any(|p| p.other_cell_id == 0xFFFF));
    assert!(cell.has_containment());
    let mut cells = CellArray::new();
    let exit = find_transit_cells_sphere(
        &world,
        cell,
        ALCOVE,
        &[Sphere::new(Vec3::new(132.3603, 16.8113, 94.0), 0.48)],
        1,
        &mut CellCollector::new(&mut cells),
    );
    assert!(exit, "candidates {:X?}", cells.ordered_ids());
}

fn with_cottage(engine: &mut PhysicsEngine) {
    const COTTAGE_GFX: u32 = 0x0100_0A2B;
    let cottage = load_gfx_obj("cellar-ascent", COTTAGE_GFX);
    let radius = cottage.bounding_sphere.radius;
    engine.world_mut().unwrap().insert_gfx_obj(cottage);
    register_single(
        engine,
        0x00A9_B479,
        COTTAGE_GFX,
        Vec3::new(130.5, 11.5, 94.0),
        Quat::from_axis_angle(Vec3::Z, std::f32::consts::PI),
        radius,
        HOLTBURG,
        ShadowCollisionType::Bsp,
        0.0,
        1.0,
        0,
        EntityCollisionFlags::default(),
        0,
    );
}

fn floor_94(pos: Vec3, cell: u32) -> PhysicsBody {
    floor_body(
        pos,
        Plane::new(Vec3::Z, -94.0),
        cell,
        vec![
            Vec3::new(120.0, 10.0, 94.0),
            Vec3::new(145.0, 10.0, 94.0),
            Vec3::new(145.0, 30.0, 94.0),
            Vec3::new(120.0, 30.0, 94.0),
        ],
    )
}

fn resolve_at(engine: &PhysicsEngine, from: Vec3, to: Vec3, cell: u32) -> ResolveResult {
    let mut body = floor_94(from, cell);
    engine.resolve_with_transition(
        &request(
            from,
            to,
            cell,
            0.48,
            1.20,
            0.60,
            1.5,
            true,
            player(),
            DOOR_ENTITY + 1,
        ),
        Some(&mut body),
    )
}

#[test]
#[ignore = "needs the retail dat files"]
fn inside_out_tick_3254_with_cottage_walls_blocks() {
    let mut engine = faithful_door_engine(&dats());
    with_cottage(&mut engine);
    let target = Vec3::new(133.54903, 17.599_283, 94.0);
    let r = resolve_at(
        &engine,
        Vec3::new(133.65524, 17.58999, 94.0),
        target,
        ALCOVE,
    );
    assert!(
        r.position.y < target.y - 0.005,
        "y {} toward {}",
        r.position.y,
        target.y
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn corner_slide_alcove_east_to_cottage_north_blocks() {
    let d = dats();
    let mut engine = faithful_door_engine(&d);
    with_cottage(&mut engine);
    assert!(load_env_cell(&d, engine.world_mut().unwrap(), ALCOVE));
    let mut pos = Vec3::new(132.95, 16.8, 94.0);
    let mut cell = ALCOVE;
    let mut grounded = true;
    let mut body = floor_94(pos, cell);
    for _ in 1..=50 {
        let r = engine.resolve_with_transition(
            &request(
                pos,
                pos + Vec3::new(0.0, 0.05, 0.0),
                cell,
                0.48,
                1.20,
                0.60,
                1.5,
                grounded,
                player(),
                DOOR_ENTITY + 1,
            ),
            Some(&mut body),
        );
        pos = r.position;
        cell = r.cell_id;
        grounded = r.is_on_ground;
        body.set_position(pos);
        if pos.y > 18.0 {
            break;
        }
    }
    assert!(pos.y < 17.20, "slid past the cottage wall to y {}", pos.y);
}

#[test]
#[ignore = "needs the retail dat files"]
fn geometric_door_slab_at_sphere_height_overlaps_in_z() {
    let d = dats();
    let setup: Setup = d.get(DOOR_SETUP).unwrap().unwrap();
    let frame = setup
        .placement_frames
        .iter()
        .find(|(k, _)| *k == 0)
        .map(|(_, f)| f.frames[0])
        .expect("default placement");
    let gfx: GfxObj = d.get(DOOR_FRAME_GFX).unwrap().unwrap();
    let mut z_min = f32::MAX;
    for (_, poly) in &gfx.physics_polygons {
        for vid in &poly.vertex_ids {
            if let Some((_, v)) = gfx.vertices.vertices.iter().find(|(id, _)| id == vid) {
                z_min = z_min.min(v.origin.z);
            }
        }
    }
    let slab_bottom = DOOR_SPAWN.z + frame.origin.z + z_min;
    assert!(slab_bottom < 94.0 + 1.20, "slab bottom {slab_bottom}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn directional_outside_in_south_approach_blocks_at_slab_south_face() {
    let engine = faithful_door_engine(&dats());
    let r = resolve_at(
        &engine,
        Vec3::new(132.5, 16.3, 94.0),
        Vec3::new(132.5, 16.7, 94.0),
        DOOR_CELL_OUTDOOR,
    );
    assert!(r.collision_normal_valid, "not blocked at {}", r.position);
    assert!(r.collision_normal.y < -0.5, "normal {}", r.collision_normal);
}

#[test]
#[ignore = "needs the retail dat files"]
fn directional_inside_out_north_approach_blocks_at_slab_north_face() {
    let engine = faithful_door_engine(&dats());
    let r = resolve_at(
        &engine,
        Vec3::new(132.5, 17.6, 94.0),
        Vec3::new(132.5, 17.2, 94.0),
        DOOR_CELL_OUTDOOR,
    );
    assert!(r.collision_normal_valid, "not blocked at {}", r.position);
    assert!(r.collision_normal.y > 0.5, "normal {}", r.collision_normal);
}

#[test]
fn add_all_outside_cells_landblock_local_sphere_adds_door_outdoor_cell() {
    let mut cells = CellArray::new();
    add_all_outside_cells(
        Vec3::new(132.3603, 16.8113, 94.0),
        0.48,
        ALCOVE,
        Vec3::ZERO,
        &mut CellCollector::new(&mut cells),
    );
    assert!(
        cells.contains(DOOR_CELL_OUTDOOR),
        "{:X?}",
        cells.ordered_ids()
    );
}

// ── CorridorSeamReplayTests and SeamTransitLagTests ────────────────────

const SEAM_WEST: u32 = 0x8A02_016E;
const SEAM_EAST: u32 = 0x8A02_017A;
const HUB: u32 = 0x8A02_0000;

/// The two seam cells and three rings of their portal neighbours (the
/// first ring from the seeds' portals as OpenAC's corridor harness adds).
fn corridor_engine() -> PhysicsEngine {
    let d = dats();
    let mut ids = vec![SEAM_WEST, SEAM_EAST];
    let neighbours = |d: &DatCollection, id: u32| -> Vec<u32> {
        d.get::<EnvCell>(id)
            .ok()
            .flatten()
            .map(|c| {
                c.cell_portals
                    .iter()
                    .map(|p| HUB | u32::from(p.other_cell_id))
                    .collect()
            })
            .unwrap_or_default()
    };
    for _ in 0..4 {
        for id in ids.clone() {
            for n in neighbours(&d, id) {
                if !ids.contains(&n) {
                    ids.push(n);
                }
            }
        }
    }
    let mut world = CollisionWorld::new();
    for id in ids {
        load_env_cell(&d, &mut world, id);
    }
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine
}

fn corridor_body(vertices: Vec<Vec3>) -> PhysicsBody {
    let mut b = PhysicsBody::new();
    let plane = Plane::new(Vec3::Z, 6.0);
    b.contact_plane_valid = true;
    b.contact_plane = plane;
    b.transient_state
        .insert(TransientState::CONTACT | TransientState::ON_WALKABLE);
    b.walkable_polygon_valid = true;
    b.walkable_plane = plane;
    b.walkable_up = Vec3::Z;
    b.walkable_vertices = Some(vertices);
    b
}

fn corridor_floor() -> Vec<Vec3> {
    vec![
        Vec3::new(75.0, -41.67, -6.0),
        Vec3::new(85.0, -41.67, -6.0),
        Vec3::new(85.0, -38.33, -6.0),
        Vec3::new(75.0, -38.33, -6.0),
    ]
}

fn corridor_resolve(
    engine: &PhysicsEngine,
    body: &mut PhysicsBody,
    from: Vec3,
    to: Vec3,
    cell: u32,
) -> ResolveResult {
    engine.resolve_with_transition(
        &request(from, to, cell, 0.48, 1.2, 0.4, 0.4, true, player(), 0),
        Some(body),
    )
}

#[test]
#[ignore = "needs the retail dat files"]
fn seam_shake_west_boundary_snapshot_exact_advances() {
    let engine = corridor_engine();
    let mut body = corridor_body(vec![
        Vec3::new(75.0, -38.33333, -6.0),
        Vec3::new(75.0, -41.66667, -6.0),
        Vec3::new(78.33333, -41.66667, -6.0),
        Vec3::new(78.33333, -38.33333, -6.0),
    ]);
    body.contact_plane_cell_id = SEAM_WEST;
    let from = Vec3::new(75.28674, -40.03537, -6.0);
    let to = Vec3::new(74.6854, -39.988_018, -6.0);
    let r = engine.resolve_with_transition(
        &request(from, to, SEAM_WEST, 0.48, 1.2, 0.6, 1.5, true, player(), 0),
        Some(&mut body),
    );
    assert!(
        r.position.x < from.x - 0.3,
        "{} -> {}",
        from.x,
        r.position.x
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn window_opening_head_cannot_fit_entry_blocked() {
    let engine = corridor_engine();
    let mut body = PhysicsBody::new();
    body.contact_plane_valid = true;
    body.contact_plane = Plane::new(Vec3::Z, 5.112);
    body.contact_plane_cell_id = 0x8A02_0179;
    body.transient_state
        .insert(TransientState::CONTACT | TransientState::ON_WALKABLE);
    let mut pos = Vec3::new(88.60, -41.10, -5.05);
    let mut cell = 0x8A02_0179;
    for i in 0..22 {
        let dir = crate::geometry::normalize(
            Vec3::new(90.209, -41.809, 0.0) - Vec3::new(pos.x, pos.y, 0.0),
        );
        let step = Vec3::new(dir.x, dir.y, 0.0) * 0.13;
        let r = engine.resolve_with_transition(
            &request(
                pos,
                pos + step,
                cell,
                0.48,
                1.835,
                0.6,
                1.5,
                true,
                player(),
                0,
            ),
            Some(&mut body),
        );
        pos = r.position;
        cell = r.cell_id;
        assert_ne!(r.cell_id, 0x8A02_017E, "frame {i} entered the window");
        assert!(r.position.y > -41.6, "frame {i} y {}", r.position.y);
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn window_alcove_raised_placement_head_in_lintel_solid_collides() {
    let engine = corridor_engine();
    let world = engine.world().unwrap();
    let cell = world.cell(0x8A02_0179).expect("cell 0x8A020179");
    assert!(cell.has_physics());
    let foot = cell.placement.to_local(Vec3::new(89.683, -41.247, -4.539));
    let head = cell.placement.to_local(Vec3::new(89.683, -41.247, -3.339));
    let mut t = Transition::new();
    let at = Vec3::new(89.683, -41.247, -5.019);
    t.sphere_path
        .init_path(at, at, 0x8A02_0179, 0.48, 1.2, None, None, None);
    t.sphere_path.insert_type = InsertType::Placement;
    let spheres = crate::bsp_collisions::LocalSpheres {
        sphere0: Sphere::new(foot, 0.48),
        sphere1: Some(Sphere::new(head, 0.48)),
        curr_center: foot,
        space_z: Vec3::Z,
    };
    let frame = crate::bsp_collisions::BspFrame {
        local_to_world: cell.placement.rotation,
        scale: 1.0,
        world_origin: cell.placement.origin,
    };
    let mut ctx = crate::transition::bsp_bridge::BspContext {
        transition: &mut t,
        engine: Some(&engine),
    };
    assert_eq!(
        cell.physics_bsp.find_collisions(&mut ctx, &spheres, &frame),
        TransitionState::Collided
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn seam_crossing_from_deep_straddle_start_advances() {
    let engine = corridor_engine();
    let mut body = corridor_body(corridor_floor());
    let from = Vec3::new(84.851, -39.764, -6.0);
    let r = corridor_resolve(
        &engine,
        &mut body,
        from,
        Vec3::new(85.453, -39.782, -6.0),
        SEAM_WEST,
    );
    assert!(
        r.position.x > from.x + 0.2,
        "{} -> {}",
        from.x,
        r.position.x
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn seam_crossing_does_not_persist_synthetic_sliding_normal_and_run_continues() {
    let engine = corridor_engine();
    let mut body = corridor_body(corridor_floor());
    let r1 = corridor_resolve(
        &engine,
        &mut body,
        Vec3::new(84.638, -39.758, -6.0),
        Vec3::new(85.253, -39.776, -6.0),
        SEAM_WEST,
    );
    assert!(!body.transient_state.contains(TransientState::SLIDING));
    let mut pos = r1.position;
    let mut cell = r1.cell_id;
    for i in 0..6 {
        let r = corridor_resolve(
            &engine,
            &mut body,
            pos,
            pos + Vec3::new(0.13, -0.004, 0.0),
            cell,
        );
        assert!(
            r.position.x > pos.x + 0.05,
            "frame {}: {} -> {}",
            i + 2,
            pos.x,
            r.position.x
        );
        pos = r.position;
        cell = r.cell_id;
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn run_across_seam_flips_cell() {
    for direction in [1.0f32, -1.0] {
        let engine = corridor_engine();
        let mut body = corridor_body(corridor_floor());
        let mut pos = Vec3::new(if direction > 0.0 { 83.8 } else { 86.2 }, -40.0, -6.0);
        let mut cell = if direction > 0.0 {
            SEAM_WEST
        } else {
            SEAM_EAST
        };
        let mut flip = None;
        for _ in 0..26 {
            let r = engine.resolve_with_transition(
                &request(
                    pos,
                    pos + Vec3::new(direction * 0.135, 0.0, 0.0),
                    cell,
                    0.48,
                    1.835,
                    0.4,
                    0.4,
                    true,
                    player(),
                    0,
                ),
                Some(&mut body),
            );
            if r.cell_id != cell && flip.is_none() {
                flip = Some(r.position.x);
            }
            cell = r.cell_id;
            pos = r.position;
            if (direction > 0.0 && pos.x > 86.4) || (direction < 0.0 && pos.x < 83.6) {
                break;
            }
        }
        assert!(flip.is_some(), "direction {direction}: never changed cell");
    }
}

// ── Viewer sweeps ──────────────────────────────────────────────────────

const VIEWER_RADIUS: f32 = 0.3;
const PIVOT_HEIGHT: f32 = 1.5;

fn sweep_viewer(
    engine: &PhysicsEngine,
    pivot: Vec3,
    desired_eye: Vec3,
    cell: u32,
) -> ResolveResult {
    let mut start = cell;
    if cell & 0xFFFF >= 0x0100 {
        let (pivot_cell, found) = engine.adjust_position(cell, pivot);
        if found {
            start = pivot_cell;
        }
    }
    let lift = Vec3::new(0.0, 0.0, VIEWER_RADIUS);
    engine.resolve_with_transition(
        &request(
            pivot - lift,
            desired_eye - lift,
            start,
            VIEWER_RADIUS,
            0.0,
            0.0,
            0.0,
            false,
            viewer(),
            0,
        ),
        None,
    )
}

fn engine_with_cells(landblock: u32, lows: std::ops::RangeInclusive<u32>) -> PhysicsEngine {
    let d = dats();
    let mut world = CollisionWorld::new();
    for low in lows {
        load_env_cell(&d, &mut world, landblock | low);
    }
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(landblock, deep_terrain(), Vec::new(), Vec::new(), 0.0, 0.0);
    engine
}

#[test]
#[ignore = "needs the retail dat files"]
fn corridor_clipped_stop_is_the_contact_point_not_a_step_boundary() {
    let engine = engine_with_cells(HUB, 0x0100..=0x01FF);
    let pivot = Vec3::new(70.58, -40.16, -4.40);
    let hit = Vec3::new(70.366_05, -38.628_315, -3.935_829);
    let dir = crate::geometry::normalize(hit - pivot);
    let mut clipped = Vec::new();
    let mut s = 1.20f32;
    while s <= 1.75 {
        let r = sweep_viewer(&engine, pivot, pivot + dir * s, 0x8A02_0164);
        let eye_back = pivot.distance(r.position + Vec3::new(0.0, 0.0, VIEWER_RADIUS));
        if s <= 1.55 {
            assert!(
                !r.collision_normal_valid,
                "s {s}: clipped short of the wall"
            );
            assert!((eye_back - s).abs() < 0.01, "s {s}: eye back {eye_back}");
        } else if s >= 1.65 {
            assert!(r.collision_normal_valid, "s {s}: did not clip");
            clipped.push(eye_back);
        }
        s += 0.025;
    }
    assert!(clipped.len() >= 4);
    let spread = clipped.iter().copied().fold(f32::MIN, f32::max)
        - clipped.iter().copied().fold(f32::MAX, f32::min);
    assert!(spread < 0.03, "spread {spread}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn viewer_sweep_through_openings_passes_without_collision() {
    let engine = engine_with_cells(HOLTBURG, 0x016F..=0x0175);
    let samples = [
        (
            0xA9B4_0170,
            Vec3::new(154.934_85, 16.451_527, 94.0),
            Vec3::new(154.797_03, 19.320_54, 96.248_83),
        ),
        (
            0xA9B4_0170,
            Vec3::new(154.945_37, 16.451_527, 94.0),
            Vec3::new(154.797_03, 19.320_54, 96.248_83),
        ),
        (
            0xA9B4_0171,
            Vec3::new(155.164_3, 14.392_493, 94.0),
            Vec3::new(154.804_49, 18.434_128, 96.248_83),
        ),
        (
            0xA9B4_0171,
            Vec3::new(155.475_72, 11.463_923, 94.0),
            Vec3::new(154.990_14, 16.249_46, 96.248_83),
        ),
    ];
    let mut failures = Vec::new();
    for (i, (cell, player_pos, eye)) in samples.into_iter().enumerate() {
        let pivot = player_pos + Vec3::new(0.0, 0.0, PIVOT_HEIGHT);
        let desired = pivot.distance(eye);
        let r = sweep_viewer(&engine, pivot, eye, cell);
        let back = pivot.distance(r.position + Vec3::new(0.0, 0.0, VIEWER_RADIUS));
        if r.collision_normal_valid || desired - back >= 0.10 || !r.ok {
            failures.push(i);
        }
    }
    assert!(failures.is_empty(), "obstructed samples {failures:?}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_cottage_exit_viewer_sweep_advances_from_alcove_to_outdoor() {
    let engine = engine_with_cells(HOLTBURG, 0x013F..=0x0150);
    let pivot = Vec3::new(132.70, 16.80, 94.00) + Vec3::new(0.0, 0.0, PIVOT_HEIGHT);
    let (pivot_cell, found) = engine.adjust_position(ALCOVE, pivot);
    assert!(found);
    assert_eq!(pivot_cell, ALCOVE);
    let r = sweep_viewer(&engine, pivot, Vec3::new(131.30, 19.97, 96.25), ALCOVE);
    assert_eq!(
        r.cell_id, 0xA9B4_0029,
        "ended in {:#010X} at {}",
        r.cell_id, r.position
    );
}

// ── CellarAscentViewerReplayTests ──────────────────────────────────────

fn cellar_ascent_engine() -> (PhysicsEngine, Vec<u32>) {
    let d = dats();
    let mut world = CollisionWorld::new();
    let mut cells = Vec::new();
    for low in 0x0100..=0x01FF {
        if load_env_cell(&d, &mut world, HOLTBURG | low) {
            cells.push(HOLTBURG | low);
        }
    }
    let info: LandBlockInfo = d.get(HOLTBURG | 0xFFFE).unwrap().expect("landblock info");
    for b in &info.buildings {
        if b.portals.is_empty() {
            continue;
        }
        let portals = b
            .portals
            .iter()
            .map(|p| BldPortalInfo {
                other_cell_id: HOLTBURG | u32::from(p.other_cell_id),
                other_portal_id: p.other_portal_id as i16,
                flags: p.flags,
            })
            .collect();
        let origin = vec3(b.frame.origin);
        let low = (origin.x / 24.0) as u32 * 8 + (origin.y / 24.0) as u32 + 1;
        world.cache_building(
            HOLTBURG | low,
            BuildingPhysics {
                placement: Placement::new(quat(b.frame.orientation), origin),
                portals,
                model_id: 0,
            },
        );
    }
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(HOLTBURG, deep_terrain(), Vec::new(), Vec::new(), 0.0, 0.0);
    (engine, cells)
}

/// OpenAC's camera resolve: the sweep, else the eye's own cell, else none.
fn resolve_viewer_cell(
    engine: &PhysicsEngine,
    pivot: Vec3,
    desired: Vec3,
    cell: u32,
    player: Vec3,
) -> (Vec3, u32) {
    if cell == 0 {
        return (player, 0);
    }
    let r = sweep_viewer(engine, pivot, desired, cell);
    if r.ok {
        return (r.position + Vec3::new(0.0, 0.0, VIEWER_RADIUS), r.cell_id);
    }
    let (eye_cell, found) = engine.adjust_position(cell, desired);
    if found {
        return (desired, eye_cell);
    }
    (player, 0)
}

fn feet_z(y: f32) -> f32 {
    if y < 5.73 {
        90.0
    } else if y < 9.30 {
        (90.25 + 0.836 * (y - 5.73)).min(93.25)
    } else if y < 10.40 {
        93.25 + (y - 9.30) * (0.75 / 1.10)
    } else {
        94.0
    }
}

fn feet_x(y: f32) -> f32 {
    if y <= 10.4 {
        153.9
    } else if y >= 14.0 {
        155.0
    } else {
        153.9 + (y - 10.4) / (14.0 - 10.4) * (155.0 - 153.9)
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn cellar_ascent_viewer_stays_interior_while_eye_below_grade() {
    const BOOM: f32 = 2.61;
    const PITCH: f32 = 0.291;
    let (engine, _) = cellar_ascent_engine();
    let world = engine.world().unwrap();
    let (y_start, y_end, step) = (5.2f32, 16.0f32, 0.02f32);
    let count = ((y_end - y_start) / step).round() as usize;
    let mut player_cell = HOLTBURG | 0x0174;
    let mut failures = Vec::new();
    for i in 0..=count {
        let y = y_start + i as f32 * step;
        let feet = Vec3::new(feet_x(y), y, feet_z(y));
        let mut cells = CellArray::new();
        player_cell = find_cell_set(
            world,
            feet + Vec3::new(0.0, 0.0, 0.48),
            0.48,
            player_cell,
            &mut cells,
            None,
        );
        let pivot = feet + Vec3::new(0.0, 0.0, PIVOT_HEIGHT);
        let desired =
            pivot - Vec3::Y * (BOOM * PITCH.cos()) + Vec3::new(0.0, 0.0, BOOM * PITCH.sin());
        let (eye, viewer_cell) = resolve_viewer_cell(&engine, pivot, desired, player_cell, feet);
        let outdoor_or_none = viewer_cell == 0 || viewer_cell & 0xFFFF < 0x0100;
        if eye.z < 94.0 - 0.05 && outdoor_or_none {
            failures.push(i);
        }
    }
    assert!(
        failures.is_empty(),
        "{} steps in the grass window: {failures:?}",
        failures.len()
    );
}
