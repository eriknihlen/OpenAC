//! Whether another object standing in an object's cells overlaps it, the
//! test an object turning solid again waits on (retail's ethereal overlap
//! check: every other object in its cells that is not static, placed where
//! it stands with its own spheres, against this object alone).

use glam::{Quat, Vec3};

use super::support::*;
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::{OverlapShape, PhysicsEngine};
use crate::shadow::ShadowCollisionType;
use crate::state::ObjectInfoState;
use crate::transition::CollisionSphere;
use crate::world::CollisionWorld;

const LANDBLOCK: u32 = 0xA9B4_0000;
const CELL: u32 = LANDBLOCK | 0x0001;
const DOOR: u32 = 0x10;
const OTHER: u32 = 0x20;
const DOOR_AT: Vec3 = Vec3::new(12.0, 12.0, 0.0);
/// Report collisions; ethereal while its animation holds it open.
const DOOR_SOLID: u32 = 0x08;
const DOOR_ETHEREAL: u32 = 0x0C;
/// Gravity and report collisions: a creature or player.
const BODY_STATE: u32 = 0x408;

fn engine_with_door() -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(
        LANDBLOCK,
        flat_terrain(0.0),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    register_single(
        &mut engine,
        DOOR,
        0,
        DOOR_AT,
        Quat::IDENTITY,
        1.0,
        LANDBLOCK,
        ShadowCollisionType::Cylinder,
        2.0,
        1.0,
        DOOR_ETHEREAL,
        EntityCollisionFlags::default(),
        CELL,
    );
    engine
}

fn add_other(engine: &mut PhysicsEngine, at: Vec3, state: u32) {
    register_single(
        engine,
        OTHER,
        0,
        at,
        Quat::IDENTITY,
        0.4,
        LANDBLOCK,
        ShadowCollisionType::Cylinder,
        1.8,
        1.0,
        state,
        EntityCollisionFlags::default(),
        CELL,
    );
}

/// A person's foot sphere.
fn person(id: u32) -> Option<OverlapShape> {
    (id == OTHER).then(|| OverlapShape {
        spheres: vec![CollisionSphere::new(Vec3::new(0.0, 0.0, 0.4), 0.4)],
        scale: 1.0,
        mover_flags: ObjectInfoState(ObjectInfoState::IS_PLAYER),
    })
}

#[test]
fn a_body_standing_in_the_doorway_overlaps_the_door() {
    let mut engine = engine_with_door();
    add_other(&mut engine, DOOR_AT + Vec3::new(0.5, 0.0, 0.0), BODY_STATE);
    assert!(engine.is_overlapped_by_another(DOOR, DOOR_SOLID, &mut person));
}

#[test]
fn the_door_is_tested_as_solid_not_as_its_rows_say() {
    // Its rows still carry the ethereal bit; tested with it, nothing would
    // ever overlap the door.
    let mut engine = engine_with_door();
    add_other(&mut engine, DOOR_AT + Vec3::new(0.5, 0.0, 0.0), BODY_STATE);
    assert!(!engine.is_overlapped_by_another(DOOR, DOOR_ETHEREAL, &mut person));
}

#[test]
fn a_body_clear_of_the_door_does_not_overlap_it() {
    let mut engine = engine_with_door();
    add_other(&mut engine, DOOR_AT + Vec3::new(3.0, 0.0, 0.0), BODY_STATE);
    assert!(!engine.is_overlapped_by_another(DOOR, DOOR_SOLID, &mut person));
}

#[test]
fn a_static_object_never_holds_the_door() {
    let mut engine = engine_with_door();
    add_other(
        &mut engine,
        DOOR_AT + Vec3::new(0.5, 0.0, 0.0),
        BODY_STATE | 0x1,
    );
    assert!(!engine.is_overlapped_by_another(DOOR, DOOR_SOLID, &mut person));
}

#[test]
fn an_object_given_no_shape_is_not_tested() {
    // The caller gives none for an object held by another.
    let mut engine = engine_with_door();
    add_other(&mut engine, DOOR_AT + Vec3::new(0.5, 0.0, 0.0), BODY_STATE);
    assert!(!engine.is_overlapped_by_another(DOOR, DOOR_SOLID, &mut |_| None));
}

#[test]
fn the_door_alone_does_not_overlap_itself() {
    let engine = engine_with_door();
    assert!(
        !engine.is_overlapped_by_another(DOOR, DOOR_SOLID, &mut |_| {
            Some(OverlapShape {
                spheres: Vec::new(),
                scale: 1.0,
                mover_flags: ObjectInfoState::default(),
            })
        })
    );
}
