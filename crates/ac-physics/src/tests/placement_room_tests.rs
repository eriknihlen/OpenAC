//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/PlacementRoomProbeTests.cs`:
//! the room check a summoner makes before calling a pet. Can a body be set
//! down three metres ahead on flat ground, against a wall, beside a
//! creature, on a step, and off the edge of what is loaded.

use std::f32::consts::FRAC_PI_2;

use glam::{Quat, Vec3};

use super::support::register_shadow;
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::PhysicsEngine;
use crate::placement_room::{PlacementRoom, PlacementRoomBody, PlacementRoomResult, check};
use crate::shadow::ShadowCollisionType;
use crate::terrain::TerrainSurface;
use crate::transition::CollisionSphere;

const LANDBLOCK: u32 = 0xA9B4_0000;
const CELL: u32 = LANDBLOCK | 0x0001;
const SELF: u32 = 0x5000_0001;
const WALL: u32 = 0x8000_0010;
const MONSTER: u32 = 0x8000_0011;
const RADIUS: f32 = 0.48;
const HEIGHT: f32 = 1.835;
const FEET: Vec3 = Vec3::new(96.0, 96.0, 0.0);

fn spheres() -> [CollisionSphere; 2] {
    [
        CollisionSphere::new(Vec3::new(0.0, 0.0, RADIUS), RADIUS),
        CollisionSphere::new(Vec3::new(0.0, 0.0, HEIGHT - RADIUS), RADIUS),
    ]
}

fn body(spheres: &[CollisionSphere]) -> PlacementRoomBody<'_> {
    PlacementRoomBody {
        spheres,
        scale: 1.0,
        step_up_height: 0.4,
        step_down_height: 0.4,
        self_entity_id: SELF,
    }
}

/// Facing north, the heading a fresh orientation has.
fn north() -> Quat {
    Quat::IDENTITY
}

/// Facing east: a quarter turn clockwise seen from above.
fn east() -> Quat {
    Quat::from_axis_angle(Vec3::Z, -FRAC_PI_2)
}

fn flat_world() -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(crate::world::CollisionWorld::new());
    engine.add_landblock(
        LANDBLOCK | 0xFFFF,
        TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    engine
}

fn register_cylinder(
    engine: &mut PhysicsEngine,
    id: u32,
    at: Vec3,
    radius: f32,
    height: f32,
    flags: u8,
) {
    register_shadow(
        engine,
        id,
        0,
        at,
        Quat::IDENTITY,
        radius,
        LANDBLOCK | 0xFFFF,
        ShadowCollisionType::Cylinder,
        height,
        1.0,
        0,
        EntityCollisionFlags(flags),
        CELL,
        false,
    );
}

fn look(engine: &mut PhysicsEngine, facing: Quat, distance: f32) -> PlacementRoomResult {
    let spheres = spheres();
    check(engine, &body(&spheres), FEET, facing, CELL, FEET, distance)
}

fn near(actual: f32, expected: f32) -> bool {
    (actual - expected).abs() < 0.005
}

#[test]
fn open_ground_ahead_is_clear_and_the_body_stands_three_metres_out() {
    let mut engine = flat_world();

    let result = look(&mut engine, north(), 3.0);

    assert_eq!(result.room, PlacementRoom::Clear);
    assert_eq!(result.cell_id & 0xFFFF_0000, LANDBLOCK);
    assert!(near(result.position.x, 96.0), "{:?}", result.position);
    assert!(near(result.position.y, 99.0), "{:?}", result.position);
    // Set down a hand's width up and settled onto the ground, not left
    // hanging or sunk into it.
    assert!(
        (0.0..=0.11).contains(&result.position.z),
        "{:?}",
        result.position
    );
}

#[test]
fn a_wall_where_the_body_would_stand_blocks_it() {
    let mut engine = flat_world();
    register_cylinder(
        &mut engine,
        WALL,
        Vec3::new(96.0, 99.0, 0.0),
        1.5,
        4.0,
        EntityCollisionFlags::NONE,
    );

    assert_eq!(look(&mut engine, north(), 3.0).room, PlacementRoom::Blocked);
}

#[test]
fn the_check_looks_where_the_body_faces_not_elsewhere() {
    let mut engine = flat_world();
    register_cylinder(
        &mut engine,
        WALL,
        Vec3::new(96.0, 99.0, 0.0),
        1.5,
        4.0,
        EntityCollisionFlags::NONE,
    );

    let result = look(&mut engine, east(), 3.0);

    assert_eq!(result.room, PlacementRoom::Clear);
    assert!(near(result.position.x, 99.0), "{:?}", result.position);
    assert!(near(result.position.y, 96.0), "{:?}", result.position);
}

#[test]
fn a_creature_standing_there_does_not_count() {
    let mut engine = flat_world();
    register_cylinder(
        &mut engine,
        MONSTER,
        Vec3::new(96.0, 99.0, 0.0),
        0.6,
        1.8,
        EntityCollisionFlags::IS_CREATURE,
    );

    assert_eq!(look(&mut engine, north(), 3.0).room, PlacementRoom::Clear);
}

#[test]
fn ground_that_rises_ahead_is_climbed_onto() {
    let mut engine = flat_world();
    // A broad step half a metre high: the spot a hand's width above the
    // feet is inside it, so the check climbs until the body fits on top.
    register_cylinder(
        &mut engine,
        WALL,
        Vec3::new(96.0, 99.0, 0.0),
        2.0,
        0.5,
        EntityCollisionFlags::NONE,
    );

    let result = look(&mut engine, north(), 3.0);

    assert_eq!(result.room, PlacementRoom::Clear);
    assert!(
        (0.49..=0.61).contains(&result.position.z),
        "{:?}",
        result.position
    );
}

#[test]
fn ground_that_falls_away_under_a_low_roof_is_stepped_down_to() {
    let mut engine = flat_world();
    // The character stands half a metre up; ahead the ground is lower and
    // a roof hangs 2.2 m over it, so the body only fits set down lower.
    let on_a_ledge = Vec3::new(FEET.x, FEET.y, 0.5);
    register_cylinder(
        &mut engine,
        WALL,
        Vec3::new(96.0, 99.0, 2.2),
        2.0,
        3.0,
        EntityCollisionFlags::NONE,
    );
    let spheres = spheres();

    let result = check(
        &mut engine,
        &body(&spheres),
        on_a_ledge,
        north(),
        CELL,
        on_a_ledge,
        3.0,
    );

    assert_eq!(result.room, PlacementRoom::Clear);
    assert!(
        (0.0..=0.3).contains(&result.position.z),
        "{:?}",
        result.position
    );
}

#[test]
fn a_spot_beyond_what_is_loaded_is_unknown() {
    let mut engine = flat_world();
    let near_the_edge = Vec3::new(191.0, 96.0, 0.0);
    let spheres = spheres();

    let result = check(
        &mut engine,
        &body(&spheres),
        near_the_edge,
        east(),
        CELL,
        near_the_edge,
        3.0,
    );

    assert_eq!(result.room, PlacementRoom::Unknown);
}

#[test]
fn a_distance_outside_the_range_is_unknown() {
    for distance in [0.0, -1.0, 10.5, f32::NAN] {
        let mut engine = flat_world();

        assert_eq!(
            look(&mut engine, north(), distance).room,
            PlacementRoom::Unknown,
            "{distance}"
        );
    }
}

#[test]
fn a_body_with_no_shape_is_unknown() {
    let mut engine = flat_world();

    let result = check(&mut engine, &body(&[]), FEET, north(), CELL, FEET, 3.0);

    assert_eq!(result.room, PlacementRoom::Unknown);
}
