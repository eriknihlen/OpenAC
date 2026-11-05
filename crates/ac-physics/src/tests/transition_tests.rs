//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/TransitionTests.cs`.

use glam::Vec3;

use super::support::*;
use crate::engine::PhysicsEngine;
use crate::globals::EPSILON;
use crate::state::ObjectInfoState;
use crate::terrain::TerrainSurface;
use crate::transition::Transition;

fn flat(z: f32) -> TerrainSurface {
    let idx = (z as i32).clamp(0, 255) as u8;
    terrain_from_heights(&[idx; 81])
}

fn sloped(base_z: f32, rise_per_cell: f32) -> TerrainSurface {
    let mut heights = [0u8; 81];
    for x in 0..9 {
        for y in 0..9 {
            let z = base_z + x as f32 * rise_per_cell;
            heights[x * 9 + y] = (z as i32).clamp(0, 255) as u8;
        }
    }
    terrain_from_heights(&heights)
}

fn make_transition(from: Vec3, to: Vec3, sphere_radius: f32, cell_id: u32) -> Transition {
    let mut t = Transition::new();
    t.sphere_path
        .init_path_simple(from, to, cell_id, sphere_radius);
    t.object_info.state = ObjectInfoState(ObjectInfoState::NONE);
    t
}

#[test]
fn find_transitional_position_flat_terrain_moves_full_distance() {
    let ground_z = 10.0;
    let engine = engine_with_terrain(flat(ground_z));
    let from = Vec3::new(50.0, 50.0, ground_z);
    let to = Vec3::new(55.0, 50.0, ground_z);
    let mut t = make_transition(from, to, 0.5, 0x0001);
    let ok = t.find_transitional_position(&engine);
    assert!(ok);
    let cur = t.sphere_path.cur_pos;
    assert!(cur.x > from.x);
    assert!((from.x + 1.0..=to.x + 0.1).contains(&cur.x), "x {}", cur.x);
    assert!(
        (ground_z - 0.1..=ground_z + 0.1).contains(&cur.z),
        "z {}",
        cur.z
    );
}

#[test]
fn find_transitional_position_null_begin_cell_returns_false() {
    let engine = engine_with_terrain(flat(0.0));
    let mut t = make_transition(
        Vec3::new(50.0, 50.0, 0.0),
        Vec3::new(55.0, 50.0, 0.0),
        0.5,
        0,
    );
    assert!(!t.find_transitional_position(&engine));
}

#[test]
fn find_transitional_position_no_terrain_allows_pass_through() {
    let engine = PhysicsEngine::new();
    let mut t = make_transition(
        Vec3::new(50.0, 50.0, 0.0),
        Vec3::new(55.0, 50.0, 0.0),
        0.5,
        0x0001,
    );
    assert!(t.find_transitional_position(&engine));
}

#[test]
fn find_transitional_position_zero_movement_returns_true_with_unchanged_position() {
    let engine = engine_with_terrain(flat(5.0));
    let start = Vec3::new(96.0, 96.0, 5.0);
    let mut t = make_transition(start, start, 0.5, 0x0001);
    assert!(t.find_transitional_position(&engine));
    assert_eq!(t.sphere_path.cur_pos, start);
}

#[test]
fn find_transitional_position_sphere_above_terrain_snaps_terrain() {
    let engine = engine_with_terrain(flat(0.0));
    let from = Vec3::new(50.0, 50.0, 3.0);
    let to = Vec3::new(51.0, 50.0, 3.0);
    let mut t = make_transition(from, to, 0.5, 0x0001);
    t.object_info.state = ObjectInfoState(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE);
    assert!(t.find_transitional_position(&engine));
    assert!(t.sphere_path.cur_pos.z <= from.z + 0.1);
}

#[test]
fn find_transitional_position_into_hill_adjusts_or_stops() {
    let terrain = sloped(0.0, 5.0);
    let engine = engine_with_terrain(terrain.clone());
    let radius = 0.5;
    let from = Vec3::new(12.0, 96.0, radius);
    let to = Vec3::new(30.0, 96.0, radius);
    let mut t = make_transition(from, to, radius, 0x0001);
    if t.find_transitional_position(&engine) {
        let cur = t.sphere_path.cur_pos;
        let terrain_at_final = terrain.sample_z(cur.x, cur.y);
        assert!(
            cur.z >= terrain_at_final - 0.1,
            "z {} terrain {}",
            cur.z,
            terrain_at_final
        );
    }
}

#[test]
fn step_down_maintains_ground_contact() {
    let ground_z = 10.0;
    let engine = engine_with_terrain(flat(ground_z));
    let radius = 1.0;
    let from = Vec3::new(50.0, 96.0, ground_z + radius);
    let to = Vec3::new(65.0, 96.0, ground_z + radius);
    let mut t = make_transition(from, to, radius, 0x0001);
    t.object_info.state = ObjectInfoState(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE);
    assert!(t.find_transitional_position(&engine));
    let final_bottom = t.sphere_path.cur_pos.z - radius;
    assert!(final_bottom >= ground_z - EPSILON, "bottom {final_bottom}");
    assert!(t.sphere_path.cur_pos.x > from.x);
}

#[test]
fn adjust_offset_contact_plane_present_removes_into_plane_component() {
    let engine = engine_with_terrain(flat(10.0));
    let ground_z = 10.0;
    let radius = 0.5;
    let from1 = Vec3::new(50.0, 50.0, ground_z + radius);
    let to1 = Vec3::new(51.0, 50.0, ground_z + radius);
    let mut t1 = make_transition(from1, to1, radius, 0x0001);
    assert!(t1.find_transitional_position(&engine));

    let from2 = t1.sphere_path.cur_pos;
    let to2 = from2 + Vec3::new(2.0, 0.0, 0.0);
    let mut t2 = make_transition(from2, to2, radius, 0x0001);
    t2.object_info.state = ObjectInfoState(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE);
    assert!(t2.find_transitional_position(&engine));
    let bottom = t2.sphere_path.cur_pos.z - radius;
    assert!(bottom >= ground_z - EPSILON, "bottom {bottom}");
}

#[test]
fn find_transitional_position_long_sweep_has_no_invented_step_cap() {
    let ground_z = 10.0;
    let engine = engine_with_terrain(flat(ground_z));
    let from = Vec3::new(50.0, 50.0, ground_z);
    let to = Vec3::new(62.0, 50.0, ground_z);

    let mut normal = make_transition(from, to, 0.3, 0x0001);
    assert!(normal.find_transitional_position(&engine));
    assert!(normal.sphere_path.cur_pos.x > from.x);

    let mut viewer = make_transition(from, to, 0.3, 0x0001);
    viewer.object_info.state.insert(ObjectInfoState::IS_VIEWER);
    assert!(viewer.find_transitional_position(&engine));
    assert!(viewer.sphere_path.cur_pos.x > from.x);
}

#[test]
fn sample_terrain_z_finds_correct_landblock() {
    let mut engine = PhysicsEngine::new();
    engine.add_landblock(0xAAAA_0000, flat(10.0), Vec::new(), Vec::new(), 0.0, 0.0);
    engine.add_landblock(0xAAAB_0000, flat(20.0), Vec::new(), Vec::new(), 192.0, 0.0);
    let z1 = engine.sample_terrain_z(96.0, 96.0).unwrap();
    let z2 = engine.sample_terrain_z(288.0, 96.0).unwrap();
    assert_eq_prec(10.0, z1, 0);
    assert_eq_prec(20.0, z2, 0);
}
