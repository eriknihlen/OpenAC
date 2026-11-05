//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/InitialPlacementObjectSweepTests.cs`,
//! `InitialPlacementOverlapTests.cs` and `PlacementContainmentTests.cs`.

use glam::{Quat, Vec3};

use super::support::*;
use crate::bsp::{CellBsp, CellBspNode};
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::PhysicsEngine;
use crate::geometry::Plane;
use crate::globals::EPSILON;
use crate::polygon::ResolvedPolygon;
use crate::portal::PortalInfo;
use crate::set_position::{
    PhysicsSetPositionFlags, PhysicsSetPositionRequest, PhysicsSetPositionResult,
};
use crate::state::{InsertType, ObjectInfoState, TransitionState};
use crate::terrain::TerrainSurface;
use crate::transition::Transition;
use crate::world::{CollisionWorld, Placement};

const LANDBLOCK: u32 = 0xA9B4_0000;
const CELL: u32 = LANDBLOCK | 0x0001;
const RADIUS: f32 = 0.48;

fn zero_terrain() -> TerrainSurface {
    TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, None)
}

fn new_engine() -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(LANDBLOCK, zero_terrain(), Vec::new(), Vec::new(), 0.0, 0.0);
    engine
}

fn place(
    engine: &mut PhysicsEngine,
    feet: Vec3,
    cell: u32,
    mover_flags: u32,
    id: u32,
) -> PhysicsSetPositionResult {
    engine.set_position(
        &PhysicsSetPositionRequest {
            position: feet,
            orientation: Quat::IDENTITY,
            cell_id: cell,
            cell_local_position: feet,
            spheres: human_spheres(),
            scale: 1.0,
            step_up_height: 0.4,
            step_down_height: 0.4,
            mover_flags: ObjectInfoState(mover_flags),
            moving_entity_id: id,
            flags: PhysicsSetPositionFlags(
                PhysicsSetPositionFlags::PLACEMENT | PhysicsSetPositionFlags::SLIDE,
            ),
            ..PhysicsSetPositionRequest::default()
        },
        None,
    )
}

// ── InitialPlacementObjectSweepTests ───────────────────────────────────

const PLAYER_ID: u32 = 0x5000_0001;
const STONE_ID: u32 = 0x7AA0_C34F;

fn register_stone(engine: &mut PhysicsEngine, center: Vec3) {
    register_sphere(
        engine,
        STONE_ID,
        center,
        RADIUS,
        LANDBLOCK,
        0x1,
        EntityCollisionFlags(EntityCollisionFlags::HAS_WEENIE),
        CELL,
        true,
    );
}

#[test]
fn initial_placement_sweep_ignores_objects_placement_sweep_tests_them() {
    let mut engine = new_engine();
    let feet = Vec3::new(10.0, 10.0, 0.0);
    register_stone(&mut engine, feet + Vec3::new(0.0, 0.0, RADIUS));
    let mut t = Transition::new();
    t.sphere_path
        .init_path_spheres(feet, feet, CELL, &human_spheres(), 1.0, None, None);
    t.sphere_path.insert_type = InsertType::InitialPlacement;
    assert_eq!(
        t.find_obj_collisions_in_cell(&engine, CELL),
        TransitionState::Ok
    );
    t.sphere_path.insert_type = InsertType::Placement;
    assert_ne!(
        t.find_obj_collisions_in_cell(&engine, CELL),
        TransitionState::Ok
    );
}

#[test]
fn player_arriving_inside_a_stuck_object_is_placed_beside_it() {
    let mut engine = new_engine();
    let feet = Vec3::new(10.0, 10.0, 0.0);
    let stone_center = feet + Vec3::new(0.0, 0.0, RADIUS);
    register_stone(&mut engine, stone_center);
    let result = place(
        &mut engine,
        feet,
        CELL,
        ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE,
        PLAYER_ID,
    );
    assert!(
        result.is_committed(),
        "placement failed: {:?}",
        result.error
    );
    let center_distance = (result.position + Vec3::new(0.0, 0.0, RADIUS)).distance(stone_center);
    assert!(
        center_distance >= RADIUS * 2.0 - EPSILON,
        "{center_distance}"
    );
    assert!(feet.distance(result.position) <= 4.0);
}

// ── InitialPlacementOverlapTests ───────────────────────────────────────

#[test]
fn player_relogging_inside_monster_searches_out_to_nearest_clear_ring() {
    const MONSTER_ID: u32 = 0x5000_0002;
    let mut engine = new_engine();
    let saved_feet = Vec3::new(10.0, 10.0, 0.0);
    let player_center = saved_feet + Vec3::new(0.0, 0.0, RADIUS);
    let monster_center = player_center + Vec3::new(0.10, 0.0, 0.0);
    register_sphere(
        &mut engine,
        PLAYER_ID,
        player_center,
        RADIUS,
        LANDBLOCK,
        0,
        EntityCollisionFlags(EntityCollisionFlags::IS_PLAYER | EntityCollisionFlags::IS_CREATURE),
        CELL,
        false,
    );
    register_sphere(
        &mut engine,
        MONSTER_ID,
        monster_center,
        RADIUS,
        LANDBLOCK,
        0,
        EntityCollisionFlags(EntityCollisionFlags::IS_CREATURE),
        CELL,
        false,
    );
    let result = place(
        &mut engine,
        saved_feet,
        CELL,
        ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE,
        PLAYER_ID,
    );
    assert!(result.is_committed());
    assert!(result.in_contact);
    assert!(result.on_walkable);
    assert_eq!(result.contact_plane_cell_id, CELL);
    assert_ne!(result.position, saved_feet);
    let center_distance = (result.position + Vec3::new(0.0, 0.0, RADIUS)).distance(monster_center);
    assert!(
        center_distance >= RADIUS * 2.0 - EPSILON,
        "{center_distance}"
    );
    assert!(saved_feet.distance(result.position) <= 4.0);
}

// ── PlacementContainmentTests ──────────────────────────────────────────

const ROOM: u32 = LANDBLOCK | 0x0150;
const MOVER_ID: u32 = 0x5000_0002;

fn half_space_containment() -> CellBsp {
    CellBsp::new(vec![
        CellBspNode::Split {
            plane: Plane::new(Vec3::X, 0.0),
            positive: Some(1),
            negative: None,
        },
        CellBspNode::Leaf,
    ])
    .unwrap()
}

fn slab_containment() -> CellBsp {
    CellBsp::new(vec![
        CellBspNode::Split {
            plane: Plane::new(Vec3::X, 0.0),
            positive: Some(1),
            negative: None,
        },
        CellBspNode::Split {
            plane: Plane::new(-Vec3::X, 2.0),
            positive: Some(2),
            negative: None,
        },
        CellBspNode::Leaf,
    ])
    .unwrap()
}

fn engine_with_room(containment: CellBsp) -> PhysicsEngine {
    let floor_poly = ResolvedPolygon::new(
        0,
        vec![
            Vec3::new(-20.0, -20.0, 0.0),
            Vec3::new(20.0, -20.0, 0.0),
            Vec3::new(20.0, 20.0, 0.0),
            Vec3::new(-20.0, 20.0, 0.0),
        ],
        Plane::new(Vec3::Z, 0.0),
    );
    let mut room = cell_with_bsp(
        ROOM,
        single_leaf(Vec3::ZERO, 40.0, vec![floor_poly]),
        Placement::default(),
    );
    room.portals = vec![PortalInfo::new(0xFFFF, 0, 0)];
    room.visible_cells.add(ROOM);
    room.containment_bsp = containment;
    let mut world = CollisionWorld::new();
    world.insert_cell(room);
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(LANDBLOCK, zero_terrain(), Vec::new(), Vec::new(), 0.0, 0.0);
    engine
}

#[test]
fn placement_inside_the_room_commits() {
    let mut engine = engine_with_room(half_space_containment());
    let result = place(
        &mut engine,
        Vec3::new(2.0, 3.0, 0.0),
        ROOM,
        ObjectInfoState::EDGE_SLIDE,
        MOVER_ID,
    );
    assert!(result.is_committed(), "{result:?}");
    assert_eq!(result.cell_id, ROOM);
    assert!(result.position.x >= 0.0);
}

#[test]
fn placement_target_outside_every_cell_is_deferred_never_committed() {
    let mut engine = engine_with_room(half_space_containment());
    let result = place(
        &mut engine,
        Vec3::new(-1.0, 3.0, 0.0),
        ROOM,
        ObjectInfoState::EDGE_SLIDE,
        MOVER_ID,
    );
    assert!(
        !result.is_committed() || result.position.x >= 0.0,
        "{result:?}"
    );
}

#[test]
fn crowded_placement_inside_a_slab_room_never_commits_outside_the_room() {
    let mut engine = engine_with_room(slab_containment());
    let target = Vec3::new(1.0, 3.0, 0.0);
    let mut id = 0x5000_1000;
    let mut x = 0.3f32;
    while x <= 1.8 {
        let mut y = -2.0f32;
        while y <= 8.0 {
            register_sphere(
                &mut engine,
                id,
                Vec3::new(x, y, 0.0) + Vec3::new(0.0, 0.0, RADIUS),
                RADIUS,
                LANDBLOCK,
                0,
                EntityCollisionFlags(EntityCollisionFlags::IS_CREATURE),
                ROOM,
                false,
            );
            id += 1;
            y += 0.75;
        }
        x += 0.75;
    }
    let result = place(
        &mut engine,
        target,
        ROOM,
        ObjectInfoState::EDGE_SLIDE,
        MOVER_ID,
    );
    assert!(
        !result.is_committed() || (result.position.x >= 0.0 && result.position.x <= 2.0),
        "{result:?}"
    );
}
