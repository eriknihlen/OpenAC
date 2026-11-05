//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/TransitionTypesTests.cs`
//! and `IndoorWalkablePlaneTests.cs` (`Transition.TryFindIndoorWalkablePlane`).
//! OpenAC's flat-versus-graph comparison has no counterpart (one tree
//! representation here).

use glam::Vec3;

use super::support::*;
use crate::geometry::Plane;
use crate::polygon::ResolvedPolygon;
use crate::transition::Transition;
use crate::world::{CellPhysics, Placement};

fn two_floor_cell(lower_z: f32, upper_z: f32) -> CellPhysics {
    let lower = ResolvedPolygon::new(
        0,
        vec![
            Vec3::new(0.0, 0.0, lower_z),
            Vec3::new(1.0, 0.0, lower_z),
            Vec3::new(1.0, 1.0, lower_z),
            Vec3::new(0.0, 1.0, lower_z),
        ],
        Plane::new(Vec3::Z, -lower_z),
    );
    let upper = ResolvedPolygon::new(
        1,
        vec![
            Vec3::new(0.0, 0.0, upper_z),
            Vec3::new(1.0, 0.0, upper_z),
            Vec3::new(1.0, 1.0, upper_z),
            Vec3::new(0.0, 1.0, upper_z),
        ],
        Plane::new(Vec3::Z, -upper_z),
    );
    let center = Vec3::new(0.5, 0.5, (lower_z + upper_z) * 0.5);
    let half_height = (upper_z - lower_z).abs() * 0.5 + 1.0;
    let radius = (0.5f32 * 0.5 + 0.5 * 0.5 + half_height * half_height).sqrt();
    cell_with_bsp(
        0,
        single_leaf(center, radius, vec![lower, upper]),
        Placement::default(),
    )
}

fn cell_with_floor(floor_z: f32) -> CellPhysics {
    let verts = vec![
        Vec3::new(-5.0, -5.0, floor_z),
        Vec3::new(5.0, -5.0, floor_z),
        Vec3::new(5.0, 5.0, floor_z),
        Vec3::new(-5.0, 5.0, floor_z),
    ];
    let d = -Vec3::Z.dot(verts[0]);
    let poly = ResolvedPolygon::new(0, verts, Plane::new(Vec3::Z, d));
    cell_with_bsp(
        0,
        single_leaf(Vec3::new(0.0, 0.0, floor_z), 10.0, vec![poly]),
        Placement::default(),
    )
}

// ── TransitionTypesTests ───────────────────────────────────────────────

#[test]
fn try_find_indoor_walkable_plane_two_overlapping_floors_picks_closest_below_foot_preserves_allowance()
 {
    let cell = two_floor_cell(0.0, 3.0);
    let mut t = Transition::new();
    let sentinel_allowance = 0.42;
    t.sphere_path.walk.walkable_allowance = sentinel_allowance;
    t.sphere_path.walk.walk_interp = 1.0;
    let (plane, vertices, id) = t
        .try_find_indoor_walkable_plane(&cell, Vec3::new(0.5, 0.5, 0.4), 0.48)
        .expect("found");
    assert_eq_prec(1.0, plane.normal.z, 3);
    assert_eq!(vertices.len(), 4);
    assert_eq_prec(0.0, vertices[0].z, 3);
    assert_eq!(id, 0);
    assert_eq!(t.sphere_path.walk.walkable_allowance, sentinel_allowance);
}

// ── IndoorWalkablePlaneTests ───────────────────────────────────────────

#[test]
fn player_directly_over_floor_returns_true() {
    let cell = cell_with_floor(0.0);
    let mut t = Transition::new();
    assert!(
        t.try_find_indoor_walkable_plane(&cell, Vec3::new(0.0, 0.0, 0.4), 0.48)
            .is_some()
    );
}

#[test]
fn player_directly_over_floor_plane_normal_is_up() {
    let cell = cell_with_floor(0.0);
    let mut t = Transition::new();
    let (plane, _, _) = t
        .try_find_indoor_walkable_plane(&cell, Vec3::new(0.0, 0.0, 0.4), 0.48)
        .unwrap();
    assert!(plane.normal.z > 0.99);
}

#[test]
fn player_directly_over_floor_plane_at_floor_z() {
    let floor_z = 2.5;
    let cell = cell_with_floor(floor_z);
    let mut t = Transition::new();
    let (plane, _, _) = t
        .try_find_indoor_walkable_plane(&cell, Vec3::new(0.0, 0.0, floor_z + 0.4), 0.48)
        .unwrap();
    assert!((plane.d - (-floor_z)).abs() < 1e-4, "d {}", plane.d);
}

#[test]
fn player_outside_polygon_xy_returns_false() {
    let cell = cell_with_floor(0.0);
    let mut t = Transition::new();
    assert!(
        t.try_find_indoor_walkable_plane(&cell, Vec3::new(20.0, 20.0, 0.4), 0.48)
            .is_none()
    );
}

#[test]
fn no_bsp_returns_false() {
    let cell = CellPhysics::default();
    let mut t = Transition::new();
    assert!(
        t.try_find_indoor_walkable_plane(&cell, Vec3::new(0.0, 0.0, 0.4), 0.48)
            .is_none()
    );
}

#[test]
fn wall_poly_in_bsp_returns_false() {
    let wall = ResolvedPolygon::new(
        0,
        vec![
            Vec3::new(0.0, 0.0, 0.0),
            Vec3::new(1.0, 0.0, 0.0),
            Vec3::new(1.0, 0.0, 1.0),
            Vec3::new(0.0, 0.0, 1.0),
        ],
        Plane::new(Vec3::Y, 0.0),
    );
    let cell = cell_with_bsp(
        0,
        single_leaf(Vec3::new(0.5, 0.0, 0.5), 2.0, vec![wall]),
        Placement::default(),
    );
    let mut t = Transition::new();
    t.sphere_path.walk.walk_interp = 1.0;
    assert!(
        t.try_find_indoor_walkable_plane(&cell, Vec3::new(0.5, 0.0, 0.5), 0.48)
            .is_none()
    );
}

#[test]
fn empty_resolved_returns_false() {
    let cell = cell_with_bsp(
        0,
        single_leaf(Vec3::ZERO, 10.0, Vec::new()),
        Placement::default(),
    );
    let mut t = Transition::new();
    assert!(
        t.try_find_indoor_walkable_plane(&cell, Vec3::new(0.0, 0.0, 0.4), 0.48)
            .is_none()
    );
}

#[test]
fn with_world_translation_plane_in_world_space() {
    let poly = ResolvedPolygon::new(
        0,
        vec![
            Vec3::new(-5.0, -5.0, 0.0),
            Vec3::new(5.0, -5.0, 0.0),
            Vec3::new(5.0, 5.0, 0.0),
            Vec3::new(-5.0, 5.0, 0.0),
        ],
        Plane::new(Vec3::Z, 0.0),
    );
    let cell = cell_with_bsp(
        0,
        single_leaf(Vec3::ZERO, 10.0, vec![poly]),
        Placement::at(Vec3::new(100.0, 200.0, 94.0)),
    );
    let mut t = Transition::new();
    let (plane, world_verts, _) = t
        .try_find_indoor_walkable_plane(&cell, Vec3::new(0.0, 0.0, 0.4), 0.48)
        .unwrap();
    assert!(plane.normal.z > 0.99);
    assert!((world_verts[0].x - 95.0).abs() < 1e-3);
    assert!((world_verts[0].y - 195.0).abs() < 1e-3);
    assert!((world_verts[0].z - 94.0).abs() < 1e-3);
}
