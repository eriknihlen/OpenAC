//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/TransitionCheckOtherCellsTests.cs`.

use glam::{Quat, Vec3};

use super::bsp_step_up::tall_wall;
use super::support::*;
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::PhysicsEngine;
use crate::shadow::ShadowCollisionType;
use crate::state::{ObjectInfoState, TransitionState};
use crate::terrain::TerrainSurface;
use crate::transition::Transition;
use crate::world::{BuildingPhysics, CellPhysics, CollisionWorld, Placement};

fn make_transition(contact: bool) -> Transition {
    let mut t = Transition::new();
    t.sphere_path
        .init_path_simple(Vec3::ZERO, Vec3::ZERO, 0xA9B4_0100, 0.48);
    t.object_info.state = ObjectInfoState(if contact {
        ObjectInfoState::CONTACT
    } else {
        ObjectInfoState::NONE
    });
    t.collision_info.contact_plane_valid = true;
    t.collision_info.contact_plane_is_water = true;
    t
}

#[test]
fn ok_continues_iteration_does_not_mutate() {
    let mut t = make_transition(false);
    assert!(!t.apply_other_cell_result(TransitionState::Ok));
    assert!(t.collision_info.contact_plane_valid);
    assert!(t.collision_info.contact_plane_is_water);
    assert!(!t.collision_info.collided_with_environment);
}

#[test]
fn collided_halts_and_sets_collided_with_environment_when_not_in_contact() {
    let mut t = make_transition(false);
    assert!(t.apply_other_cell_result(TransitionState::Collided));
    assert!(t.collision_info.collided_with_environment);
}

#[test]
fn collided_does_not_set_collided_with_environment_when_in_contact() {
    let mut t = make_transition(true);
    assert!(t.apply_other_cell_result(TransitionState::Collided));
    assert!(!t.collision_info.collided_with_environment);
}

#[test]
fn adjusted_halts_and_sets_collided_with_environment_when_not_in_contact() {
    let mut t = make_transition(false);
    assert!(t.apply_other_cell_result(TransitionState::Adjusted));
    assert!(t.collision_info.collided_with_environment);
}

#[test]
fn slid_halts_and_clears_contact_plane_fields() {
    let mut t = make_transition(false);
    assert!(t.apply_other_cell_result(TransitionState::Slid));
    assert!(!t.collision_info.contact_plane_valid);
    assert!(!t.collision_info.contact_plane_is_water);
}

fn linear_flat_terrain() -> TerrainSurface {
    terrain_from_heights(&[0u8; 81])
}

#[test]
fn check_other_cells_cell_with_null_bsp_root_is_skipped_no_crash() {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(
        0xA9B4_FFFF,
        linear_flat_terrain(),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    engine.world_mut().unwrap().insert_cell(CellPhysics {
        id: 0xA9B4_0157,
        ..CellPhysics::default()
    });
    let mut t = make_transition(false);
    let result = t.check_other_cells(&engine, 0.48, &[0xA9B4_0157]);
    assert_eq!(result, TransitionState::Ok);
}

#[test]
fn check_other_cells_outdoor_landcell_candidate_uses_terrain_walkable() {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(
        0xA9B4_FFFF,
        linear_flat_terrain(),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    let mut t = make_transition(true);
    t.sphere_path.init_path_simple(
        Vec3::new(10.0, 10.0, -0.28),
        Vec3::new(10.0, 10.0, -0.28),
        0xA9B4_0147,
        0.48,
    );
    let result = t.check_other_cells(&engine, 0.48, &[0xA9B4_0001]);
    assert_eq!(result, TransitionState::Adjusted);
    assert!(t.collision_info.contact_plane_valid);
    assert_eq!(t.collision_info.contact_plane_cell_id, 0xA9B4_0001);
    assert!(t.sphere_path.check_pos.z > -0.28);
}

const NEIGHBOR_CELL: u32 = 0xA9B4_0009;
const NEIGHBOR_GFX: u32 = 0x0100_F482;
const NEIGHBOR_OBJECT: u32 = 0xCA9B_4482;

fn neighbor_transition(target_x: f32) -> Transition {
    let target = Vec3::new(target_x, 12.0, 1.0);
    let mut t = Transition::new();
    t.sphere_path.init_path_simple(
        Vec3::new(target_x - 0.4, 12.0, 1.0),
        target,
        0xA9B4_0001,
        0.48,
    );
    t.sphere_path.set_check_pos(target, 0xA9B4_0001);
    t
}

fn register_neighbor_object(engine: &mut PhysicsEngine, origin: Vec3) {
    register_single(
        engine,
        NEIGHBOR_OBJECT,
        NEIGHBOR_GFX,
        origin,
        Quat::IDENTITY,
        10.0,
        0xA9B4_0000,
        ShadowCollisionType::Bsp,
        0.0,
        1.0,
        0x0001_0000,
        EntityCollisionFlags::default(),
        NEIGHBOR_CELL,
    );
}

fn neighbor_wall(object_channel: bool, wall_origin_x: f32) -> PhysicsEngine {
    let bsp = tall_wall();
    let root_sphere = *bsp.nodes()[0].sphere();
    let mut world = CollisionWorld::new();
    world.insert_gfx_obj(crate::world::GfxObjPhysics {
        id: NEIGHBOR_GFX,
        bsp,
        bounding_sphere: root_sphere,
        visual_bounds: None,
    });
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(
        0xA9B4_FFFF,
        TerrainSurface::new(&[0u8; 81], &[-1000.0; 256], 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    let origin = Vec3::new(wall_origin_x, 12.0, 0.0);
    if object_channel {
        register_neighbor_object(&mut engine, origin);
        assert!(
            engine
                .shadows
                .objects_in_cell(NEIGHBOR_CELL)
                .iter()
                .any(|e| e.entity_id == NEIGHBOR_OBJECT)
        );
    } else {
        engine.world_mut().unwrap().cache_building(
            NEIGHBOR_CELL,
            BuildingPhysics {
                placement: Placement::at(origin),
                portals: Vec::new(),
                model_id: NEIGHBOR_GFX,
            },
        );
    }
    engine
}

fn outdoor_terrain_miss_retains_prepared_building_and_object_collision(object_channel: bool) {
    let engine = neighbor_wall(object_channel, 23.5);
    let mut t = neighbor_transition(23.8);
    assert!(engine.world().unwrap().is_visible(NEIGHBOR_CELL));
    assert!(
        engine
            .sample_terrain_walkable_in_cell(NEIGHBOR_CELL, 23.8, 12.0)
            .is_none()
    );
    let result = t.check_other_cells(&engine, 0.48, &[NEIGHBOR_CELL]);
    assert_eq!(result, TransitionState::Adjusted);
    assert!(t.sphere_path.collide);
    assert_eq!(t.sphere_path.step_up_normal, -Vec3::X);
    if object_channel {
        assert_eq!(
            t.collision_info.last_collided_object_guid,
            Some(NEIGHBOR_OBJECT)
        );
    } else {
        assert!(t.collision_info.collided_with_environment);
    }
}

#[test]
fn outdoor_terrain_miss_retains_prepared_building_and_object_collision_building() {
    outdoor_terrain_miss_retains_prepared_building_and_object_collision(false);
}

#[test]
fn outdoor_terrain_miss_retains_prepared_building_and_object_collision_object() {
    outdoor_terrain_miss_retains_prepared_building_and_object_collision(true);
}

fn outdoor_terrain_miss_nonintersecting_prepared_neighbor_remains_clear(object_channel: bool) {
    let engine = neighbor_wall(object_channel, 30.0);
    let mut t = neighbor_transition(23.8);
    let target = t.sphere_path.check_pos;
    let result = t.check_other_cells(&engine, 0.48, &[NEIGHBOR_CELL]);
    assert_eq!(result, TransitionState::Ok);
    assert_eq!(t.sphere_path.check_pos, target);
    assert!(!t.collision_info.collision_normal_valid);
    assert_eq!(t.collision_info.last_collided_object_guid, None);
}

#[test]
fn outdoor_terrain_miss_nonintersecting_prepared_neighbor_remains_clear_building() {
    outdoor_terrain_miss_nonintersecting_prepared_neighbor_remains_clear(false);
}

#[test]
fn outdoor_terrain_miss_nonintersecting_prepared_neighbor_remains_clear_object() {
    outdoor_terrain_miss_nonintersecting_prepared_neighbor_remains_clear(true);
}

fn unavailable_outdoor_cell_does_not_dispatch_retained_building_or_objects(object_channel: bool) {
    let mut engine = neighbor_wall(object_channel, 24.0);
    let mut available = neighbor_transition(24.3);
    assert_eq!(
        engine.sample_terrain_walkable(24.3, 12.0).unwrap().cell_id,
        NEIGHBOR_CELL
    );
    assert_eq!(
        available.check_other_cells(&engine, 0.48, &[NEIGHBOR_CELL]),
        TransitionState::Adjusted
    );
    engine
        .world_mut()
        .unwrap()
        .remove_landblock_from_graph(NEIGHBOR_CELL);
    assert!(!engine.world().unwrap().is_visible(NEIGHBOR_CELL));
    assert!(engine.world().unwrap().gfx_obj(NEIGHBOR_GFX).is_some());
    if object_channel {
        assert!(!engine.shadows.objects_in_cell(NEIGHBOR_CELL).is_empty());
    } else {
        assert!(engine.world().unwrap().building(NEIGHBOR_CELL).is_some());
    }
    let mut t = neighbor_transition(24.3);
    let target = t.sphere_path.check_pos;
    let result = t.check_other_cells(&engine, 0.48, &[NEIGHBOR_CELL]);
    assert_eq!(result, TransitionState::Ok);
    assert_eq!(t.sphere_path.check_pos, target);
    assert!(!t.collision_info.collision_normal_valid);
    assert_eq!(t.collision_info.last_collided_object_guid, None);
}

#[test]
fn unavailable_outdoor_cell_does_not_dispatch_retained_building_or_objects_building() {
    unavailable_outdoor_cell_does_not_dispatch_retained_building_or_objects(false);
}

#[test]
fn unavailable_outdoor_cell_does_not_dispatch_retained_building_or_objects_object() {
    unavailable_outdoor_cell_does_not_dispatch_retained_building_or_objects(true);
}

#[test]
fn outdoor_primary_cell_is_not_dispatched_again() {
    let engine = neighbor_wall(false, 23.5);
    let mut t = neighbor_transition(23.8);
    let check = t.sphere_path.check_pos;
    t.sphere_path.set_check_pos(check, NEIGHBOR_CELL);
    assert_eq!(
        t.check_other_cells(&engine, 0.48, &[NEIGHBOR_CELL]),
        TransitionState::Ok
    );
    assert!(!t.collision_info.collision_normal_valid);
}

#[test]
fn neighbor_building_non_ok_stops_before_neighbor_objects() {
    let mut engine = neighbor_wall(false, 23.5);
    register_neighbor_object(&mut engine, Vec3::new(23.5, 12.0, 0.0));
    assert!(!engine.shadows.objects_in_cell(NEIGHBOR_CELL).is_empty());
    let mut t = neighbor_transition(23.8);
    assert_eq!(
        t.check_other_cells(&engine, 0.48, &[NEIGHBOR_CELL]),
        TransitionState::Adjusted
    );
    assert!(t.collision_info.collided_with_environment);
    assert_eq!(t.collision_info.last_collided_object_guid, None);
}
