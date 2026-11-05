//! A region's geometry takes the collision of the objects the server placed
//! in it that a body meets, so their tops are floor a walk stands on and a
//! jump lands on, and it remembers what those objects came to, so a grid is
//! known to be out of date once one arrives, leaves or moves.
//!
//! Ported from OpenAC
//! `tests/AcDream.Core.Tests/Navigation/NavGeometryObjectCaptureTests.cs`.

use ac_nav::geometry::{self, NavGeometry};
use ac_nav::grid::DEFAULT_CELL_SIZE;
use ac_nav::{NavBody, NavGrid};
use ac_physics::{
    PhysicsEngine, PhysicsState, Quat, RegistrationSite, ShadowCollisionType, SingleShape,
    TerrainSurface, Vec3,
};

const LANDBLOCK_ID: u32 = 0xA9B4_FFFF;
const ROCK: u32 = 0x8000_2AAE;
const POST: u32 = 0xCA9B_4061;
const BOLT: u32 = 0x8000_2AAF;
const BODY: NavBody = NavBody::player(0.6, 1.5);

fn anything(_: u32) -> bool {
    true
}

fn standable(entity_id: u32) -> bool {
    entity_id == ROCK
}

#[allow(clippy::too_many_arguments)]
fn register(
    engine: &mut PhysicsEngine,
    entity_id: u32,
    at: Vec3,
    radius: f32,
    height: f32,
    state: u32,
    seed_cell_id: u32,
    is_static: bool,
) {
    let (shadows, world) = engine.shadows_and_world();
    shadows
        .register(
            world,
            entity_id,
            &SingleShape {
                gfx_obj_id: 0,
                radius,
                collision_type: ShadowCollisionType::Cylinder,
                cyl_height: height,
                scale: 1.0,
            },
            RegistrationSite {
                world_pos: at,
                rotation: Quat::IDENTITY,
                world_offset_x: 0.0,
                world_offset_y: 0.0,
                landblock_id: LANDBLOCK_ID,
                seed_cell_id,
            },
            state,
            Default::default(),
            is_static,
            None,
        )
        .expect("the object registers");
}

fn register_rock(engine: &mut PhysicsEngine, at: Vec3, radius: f32, height: f32) {
    register(
        engine,
        ROCK,
        at.with_z(at.z + 50.0),
        radius,
        height,
        0,
        0,
        false,
    );
}

fn register_bolt(engine: &mut PhysicsEngine, at: Vec3) {
    register(
        engine,
        BOLT,
        at,
        0.1,
        0.2,
        PhysicsState::MISSILE.bits() | PhysicsState::REPORT_COLLISIONS.bits(),
        0,
        false,
    );
}

fn flat_world() -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    let mut height_table = [0.0f32; 256];
    for (index, height) in height_table.iter_mut().enumerate() {
        *height = index as f32;
    }
    let terrain = TerrainSurface::new(&[50; 81], &height_table, 0, 0, None);
    engine.add_landblock(LANDBLOCK_ID, terrain, Vec::new(), Vec::new(), 0.0, 0.0);
    engine
}

fn capture(engine: &PhysicsEngine, stands_on: Option<&dyn Fn(u32) -> bool>) -> NavGeometry {
    geometry::capture(engine, 0.0, 0.0, 64.0, stands_on).expect("the landblock is resident")
}

fn fingerprint(engine: &PhysicsEngine, stands_on: Option<&dyn Fn(u32) -> bool>) -> u64 {
    geometry::fingerprint_objects(engine, 0.0, 0.0, 64.0, stands_on)
}

#[test]
fn an_object_the_server_placed_becomes_floor_on_top_and_wall_below() {
    let mut engine = flat_world();
    register_rock(&mut engine, Vec3::new(30.0, 30.0, 0.0), 1.2, 1.5);

    let geometry = capture(&engine, Some(&standable));
    let grid = NavGrid::build(&geometry, BODY, DEFAULT_CELL_SIZE).unwrap();

    assert!(geometry.object_ids.contains(&ROCK));
    assert_ne!(geometry.object_fingerprint, 0);
    let top = grid.find_node(Vec3::new(30.0, 30.0, 51.5), 0.5, 0.3);
    assert!(top >= 0, "no node stands on the object the server placed");
    let z = grid.position(top).z;
    assert!((51.4..=51.6).contains(&z), "{z}");
}

#[test]
fn an_object_is_left_out_where_nothing_says_a_body_meets_it() {
    let mut engine = flat_world();
    register_rock(&mut engine, Vec3::new(30.0, 30.0, 0.0), 1.2, 1.5);

    let geometry = capture(&engine, None);
    let grid = NavGrid::build(&geometry, BODY, DEFAULT_CELL_SIZE).unwrap();

    assert!(geometry.object_ids.is_empty());
    assert_eq!(geometry.object_fingerprint, 0);
    assert!(
        grid.find_node(Vec3::new(30.0, 30.0, 51.5), 0.5, 0.3) < 0,
        "a node stands on an object the grid was not given"
    );
}

#[test]
fn what_the_objects_came_to_changes_as_one_arrives_and_moves() {
    let mut engine = flat_world();
    let empty = fingerprint(&engine, Some(&standable));

    register_rock(&mut engine, Vec3::new(30.0, 30.0, 0.0), 1.2, 1.5);
    let arrived = fingerprint(&engine, Some(&standable));

    register_rock(&mut engine, Vec3::new(34.0, 30.0, 0.0), 1.2, 1.5);
    let moved = fingerprint(&engine, Some(&standable));

    assert_eq!(empty, 0);
    assert_ne!(empty, arrived);
    assert_ne!(arrived, moved);
    assert_eq!(fingerprint(&engine, None), 0);
}

/// A grid must not be out of date the moment it is built, or every plan
/// rebuilds it and gives up. What the capture recorded and what the world
/// holds now have to weigh the same objects, landblock-owned ones included.
#[test]
fn a_fresh_capture_is_not_already_out_of_date() {
    let mut engine = flat_world();
    register_rock(&mut engine, Vec3::new(30.0, 30.0, 0.0), 1.2, 1.5);
    register(
        &mut engine,
        POST,
        Vec3::new(26.0, 30.0, 50.0),
        0.2,
        2.2,
        0,
        (LANDBLOCK_ID & 0xFFFF_0000) | 0x0021,
        true,
    );

    let geometry = capture(&engine, Some(&anything));

    assert_eq!(
        geometry.object_fingerprint,
        fingerprint(&engine, Some(&anything))
    );
    assert_ne!(geometry.object_fingerprint, 0);
}

/// A spell's bolt, an arrow or a thrown blade crosses a dungeon every frame
/// it is in flight. None of it is floor or wall, and none of it may count
/// towards what the objects here came to.
#[test]
fn a_missile_in_flight_is_neither_floor_nor_a_reason_to_build_again() {
    let mut engine = flat_world();
    register_rock(&mut engine, Vec3::new(30.0, 30.0, 0.0), 1.2, 1.5);
    let still = fingerprint(&engine, Some(&anything));

    register_bolt(&mut engine, Vec3::new(20.0, 30.0, 51.0));
    let geometry = capture(&engine, Some(&anything));
    let cast = fingerprint(&engine, Some(&anything));

    register_bolt(&mut engine, Vec3::new(24.0, 30.0, 51.0));
    let flying = fingerprint(&engine, Some(&anything));

    assert_eq!(still, cast);
    assert_eq!(still, flying);
    assert_eq!(geometry.object_fingerprint, flying);
    assert!(!geometry.object_ids.contains(&BOLT));
    let grid = NavGrid::build(&geometry, BODY, DEFAULT_CELL_SIZE).unwrap();
    assert!(
        grid.find_node(Vec3::new(20.0, 30.0, 51.5), 0.5, 0.3) < 0,
        "a node stands on a missile in flight"
    );
}
