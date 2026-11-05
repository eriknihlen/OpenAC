//! An ethereal mover (a body whose own state has the ethereal bit, as a
//! respawned gem token: 0x414) passes through objects that are not static
//! and collides with static ones. Objects made from the dats (landblock
//! and cell statics, scenery) are static; objects the server creates are
//! static only when their state says so. Ported with OpenAC
//! `tests/AcDream.Runtime.Tests/Physics/MoverEtherealTests.cs`.

use glam::{Quat, Vec3};

use super::support::*;
use crate::collision_flags::EntityCollisionFlags;
use crate::content::{StaticCollisionKind, StaticEntity, register_static};
use crate::engine::{PhysicsEngine, ResolveRequest};
use crate::shadow::ShadowCollisionType;
use crate::shadow_builder::PartRef;
use crate::state::ObjectInfoState;
use crate::world::CollisionWorld;
use crate::{PhysicsBody, PhysicsState};

const LANDBLOCK: u32 = 0xA9B4_0000;
const CELL: u32 = LANDBLOCK | 0x0001;
const TABLE_ID: u32 = 0x7300_0001;
const TABLE_GFX: u32 = 0x0100_7301;
/// The table top, metres above the floor (terrain at 0).
const TABLE_TOP: f32 = 0.8;
const TABLE_AT: Vec3 = Vec3::new(12.0, 12.0, 0.0);
/// A gem token's one sphere.
const TOKEN_RADIUS: f32 = 0.1;

/// Flat terrain at 0 with a table: a 2 m square top at `TABLE_TOP`.
fn engine_with_table_model() -> PhysicsEngine {
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
    let top = floor(
        1,
        &[
            Vec3::new(-1.0, -1.0, TABLE_TOP),
            Vec3::new(1.0, -1.0, TABLE_TOP),
            Vec3::new(1.0, 1.0, TABLE_TOP),
            Vec3::new(-1.0, 1.0, TABLE_TOP),
        ],
    );
    let mut gfx = gfx_obj(
        TABLE_GFX,
        single_leaf(Vec3::new(0.0, 0.0, TABLE_TOP), 1.5, vec![top]),
        1.5,
    );
    gfx.bounding_sphere.center = Vec3::new(0.0, 0.0, TABLE_TOP);
    engine.world_mut().unwrap().insert_gfx_obj(gfx);
    engine
}

/// The table as the dats place it (a landblock static).
fn engine_with_dat_table() -> PhysicsEngine {
    let mut engine = engine_with_table_model();
    let entity = StaticEntity {
        id: TABLE_ID,
        source_id: TABLE_GFX,
        position: TABLE_AT,
        rotation: Quat::IDENTITY,
        parts: vec![PartRef {
            gfx_obj_id: TABLE_GFX,
            position: Vec3::ZERO,
            rotation: Quat::IDENTITY,
            scale: Vec3::ONE,
        }],
        scale: 1.0,
        parent_cell_id: 0,
        is_building_shell: false,
    };
    let (shadows, world) = engine.shadows_and_world();
    let kind = register_static(shadows, world, LANDBLOCK, Vec3::ZERO, &entity).unwrap();
    assert_eq!(kind, StaticCollisionKind::Bsp);
    engine
}

/// The table as the server creates it: its state has no static bit.
fn engine_with_server_table() -> PhysicsEngine {
    let mut engine = engine_with_table_model();
    register_single(
        &mut engine,
        TABLE_ID,
        TABLE_GFX,
        TABLE_AT,
        Quat::IDENTITY,
        1.5,
        LANDBLOCK,
        ShadowCollisionType::Bsp,
        0.0,
        1.0,
        0,
        EntityCollisionFlags::default(),
        0,
    );
    engine
}

/// Drops a body with `state` from 2 m above the table centre, 0.25 m a
/// tick, through the body's own sweep; returns where its foot ends.
fn drop_body(engine: &PhysicsEngine, state: PhysicsState) -> Vec3 {
    let mut body = PhysicsBody::new();
    body.state = state;
    let mut pos = Vec3::new(TABLE_AT.x, TABLE_AT.y, 2.0);
    body.set_position(pos);
    body.orientation = Quat::IDENTITY;
    let mut cell = CELL;
    for _ in 0..20 {
        let r = engine.resolve_with_transition(
            &ResolveRequest {
                current_pos: pos,
                target_pos: pos - Vec3::new(0.0, 0.0, 0.25),
                cell_id: cell,
                sphere_radius: TOKEN_RADIUS,
                sphere_height: 0.0,
                step_up_height: 0.04,
                step_down_height: 0.09,
                mover_flags: ObjectInfoState(ObjectInfoState::EDGE_SLIDE),
                moving_entity_id: 0x7000_0D51,
                ..ResolveRequest::default()
            },
            Some(&mut body),
        );
        assert!(r.ok, "the sweep failed at {pos}");
        pos = r.position;
        cell = r.cell_id;
        body.set_position(pos);
    }
    pos
}

const TOKEN_STATE: PhysicsState = PhysicsState(0x414);

#[test]
fn dat_statics_are_registered_static() {
    let engine = engine_with_dat_table();
    let (state, is_static) = engine.shadows.collision_owner(TABLE_ID).unwrap();
    assert!(is_static);
    assert_eq!(state, PhysicsState::STATIC.bits());
}

#[test]
fn an_ethereal_token_falls_through_a_server_table_to_the_floor() {
    let engine = engine_with_server_table();
    let end = drop_body(&engine, TOKEN_STATE);
    assert!(end.z.abs() < 0.01, "it lies on the floor: {end}");
}

#[test]
fn an_ethereal_token_lands_on_a_dat_table() {
    let engine = engine_with_dat_table();
    let end = drop_body(&engine, TOKEN_STATE);
    assert!(
        (end.z - TABLE_TOP).abs() < 0.01,
        "it lies on the table: {end}"
    );
}

#[test]
fn a_solid_object_lands_on_a_server_table() {
    let engine = engine_with_server_table();
    let end = drop_body(&engine, PhysicsState::GRAVITY);
    assert!(
        (end.z - TABLE_TOP).abs() < 0.01,
        "it lies on the table: {end}"
    );
}
