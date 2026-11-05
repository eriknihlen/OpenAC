//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/CellTransitFindTransitCellsSphereTests.cs`,
//! `CellTransitFindTransitCellsBoxTests.cs`, `CellTransitAddAllOutsideCellsTests.cs`,
//! `CellTransitFindVisibleChildCellTests.cs`, `CellTransitCheckBuildingTransitTests.cs`,
//! `CellTransitBuildingPartsTests.cs`, `CellTransitFindCellSetTests.cs`,
//! `BuildShadowCellSetTests.cs` and `BspBoxCellMembershipTests.cs`.
//!
//! OpenAC runs several of these twice, over its graph cells and over its
//! "prepared" flat copies; there is one representation here, so each runs
//! once. `FirstHitBreaksPartLoop` also counts sphere reads through an
//! instrumented list, which a slice cannot do; its result is checked.
//! The installed-dat cases live in `tests/retail_transit.rs`.

use glam::{Quat, Vec3};

use crate::bsp::{CellBsp, CellBspNode};
use crate::cell_array::CellArray;
use crate::cell_transit::{
    CellCollector, CellTransitError, add_all_outside_cells, add_all_outside_cells_from_parts,
    build_cell_set_and_pick_containing, build_shadow_cell_set, build_shadow_cell_set_from_parts,
    check_building_transit, check_building_transit_from_parts, find_cell_set,
    find_transit_cells_box, find_transit_cells_sphere, find_visible_child_cell,
};
use crate::collision_flags::EntityCollisionFlags;
use crate::geometry::{Plane, Sphere};
use crate::polygon::ResolvedPolygon;
use crate::portal::PortalInfo;
use crate::shadow::{ShadowPartBox, ShadowPartGeometry, ShadowShape};
use crate::shadow_registry::{RegistrationSite, ShadowObjectRegistry};
use crate::world::{BldPortalInfo, BuildingPhysics, CellPhysics, CollisionWorld, Placement};

const CELL_A: u32 = 0xA9B4_0100;
const CELL_B: u32 = 0xA9B4_0101;

fn leaf_containment() -> CellBsp {
    CellBsp::new(vec![CellBspNode::Leaf]).unwrap()
}

/// OpenAC `SinglePlaneCellBsp`: inside where local x >= 0.
fn single_plane_containment() -> CellBsp {
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

fn right_wall_polygon(id: u16) -> ResolvedPolygon {
    ResolvedPolygon::new(
        id,
        vec![
            Vec3::new(2.5, -2.5, 0.0),
            Vec3::new(2.5, 2.5, 0.0),
            Vec3::new(2.5, 2.5, 5.0),
            Vec3::new(2.5, -2.5, 5.0),
        ],
        Plane::new(Vec3::X, -2.5),
    )
}

/// A room with one portal (polygon 10) on its right wall, at local x = 2.5.
fn portal_cell(
    id: u32,
    placement: Placement,
    other: u16,
    flags: u16,
    containment: Option<CellBsp>,
) -> CellPhysics {
    let mut cell = CellPhysics {
        id,
        placement,
        portals: vec![PortalInfo::new(other, 10, flags)],
        ..CellPhysics::default()
    };
    cell.portal_polygons.insert(10, right_wall_polygon(10));
    if let Some(c) = containment {
        cell.containment_bsp = c;
    }
    cell
}

fn plain_cell(id: u32, placement: Placement, containment: Option<CellBsp>) -> CellPhysics {
    CellPhysics {
        id,
        placement,
        containment_bsp: containment.unwrap_or_default(),
        ..CellPhysics::default()
    }
}

fn world_of(cells: Vec<CellPhysics>) -> CollisionWorld {
    let mut world = CollisionWorld::new();
    for c in cells {
        world.insert_cell(c);
    }
    world
}

fn sphere_transit(
    world: &CollisionWorld,
    cell_id: u32,
    center: Vec3,
    radius: f32,
) -> (Vec<u32>, bool) {
    let cell = world.cell(cell_id).unwrap();
    let mut cells = CellArray::new();
    let mut collector = CellCollector::new(&mut cells);
    let exit = find_transit_cells_sphere(
        world,
        cell,
        cell_id,
        &[Sphere::new(center, radius)],
        1,
        &mut collector,
    );
    (cells.ordered_ids().to_vec(), exit)
}

fn box_transit(
    world: &CollisionWorld,
    cell_id: u32,
    boxes: &[ShadowPartBox],
    spheres: &[Sphere],
) -> (Vec<u32>, bool) {
    let cell = world.cell(cell_id).unwrap();
    let mut cells = CellArray::new();
    let mut collector = CellCollector::new(&mut cells);
    let exit = find_transit_cells_box(world, cell, cell_id, boxes, spheres, &mut collector);
    (cells.ordered_ids().to_vec(), exit)
}

/// A part box with these local corners, placed (unrotated) at `world_pos`.
fn part_box(min: Vec3, max: Vec3, world_pos: Vec3) -> ShadowPartBox {
    ShadowPartBox {
        local_min: min,
        local_max: max,
        world_position: world_pos,
        world_rotation: Quat::IDENTITY,
    }
}

fn shifted(x: f32) -> Placement {
    Placement::at(Vec3::new(x, 0.0, 0.0))
}

// ── CellTransitFindTransitCellsSphereTests ─────────────────────────────

#[test]
fn sphere_inside_cell_a_near_portal_adds_cell_b() {
    let world = world_of(vec![
        portal_cell(CELL_A, Placement::default(), 0x0101, 0, None),
        plain_cell(CELL_B, shifted(5.0), None),
    ]);
    let (cells, exit) = sphere_transit(&world, CELL_A, Vec3::new(2.0, 0.0, 2.5), 0.5);
    assert!(cells.contains(&CELL_B));
    assert!(!exit);
}

#[test]
fn sphere_inside_cell_a_far_from_portal_does_not_add_cell_b() {
    let world = world_of(vec![portal_cell(
        CELL_A,
        Placement::default(),
        0x0101,
        0,
        None,
    )]);
    let (cells, _) = sphere_transit(&world, CELL_A, Vec3::new(-1.0, 0.0, 2.5), 0.5);
    assert!(!cells.contains(&CELL_B));
}

#[test]
fn loaded_neighbor_sphere_in_its_containment_adds_even_when_portal_side_would_reject() {
    let world = world_of(vec![
        portal_cell(CELL_A, Placement::default(), 0x0101, 2, None),
        plain_cell(CELL_B, shifted(5.0), Some(single_plane_containment())),
    ]);
    let (cells, exit) = sphere_transit(&world, CELL_A, Vec3::new(4.75, 0.0, 2.5), 0.5);
    assert!(cells.contains(&CELL_B));
    assert!(!exit);
}

#[test]
fn loaded_neighbor_sphere_outside_its_containment_does_not_fall_back_to_portal_side() {
    let world = world_of(vec![
        portal_cell(CELL_A, Placement::default(), 0x0101, 0, None),
        plain_cell(CELL_B, shifted(5.0), Some(single_plane_containment())),
    ]);
    let (cells, exit) = sphere_transit(&world, CELL_A, Vec3::new(3.05, 0.0, 2.5), 0.5);
    assert!(!cells.contains(&CELL_B));
    assert!(!exit);
}

#[test]
fn exit_portal_sphere_straddles_portal_plane_flags_outside() {
    let world = world_of(vec![portal_cell(
        CELL_A,
        Placement::default(),
        0xFFFF,
        0,
        None,
    )]);
    assert!(sphere_transit(&world, CELL_A, Vec3::new(2.0, 0.0, 2.5), 0.5).1);
}

#[test]
fn exit_portal_second_sphere_straddles_portal_plane_flags_outside() {
    let world = world_of(vec![portal_cell(
        CELL_A,
        Placement::default(),
        0xFFFF,
        0,
        None,
    )]);
    let spheres = [
        Sphere::new(Vec3::new(0.0, 0.0, 2.5), 0.5),
        Sphere::new(Vec3::new(2.0, 0.0, 3.2), 0.5),
    ];
    let mut cells = CellArray::new();
    let mut collector = CellCollector::new(&mut cells);
    let exit = find_transit_cells_sphere(
        &world,
        world.cell(CELL_A).unwrap(),
        CELL_A,
        &spheres,
        2,
        &mut collector,
    );
    assert!(exit);
}

// ── CellTransitFindTransitCellsBoxTests ────────────────────────────────

#[test]
fn sphere_reaches_portal_but_box_does_not_box_rejects() {
    let world = world_of(vec![
        portal_cell(CELL_A, Placement::default(), 0x0101, 0, None),
        plain_cell(CELL_B, shifted(5.0), None),
    ]);
    let pos = Vec3::new(2.0, 0.0, 2.5);
    let sphere = Sphere::new(pos, 0.5);
    let boxes = [part_box(Vec3::splat(-0.1), Vec3::splat(0.1), pos)];

    let (sphere_cells, sphere_exit) = sphere_transit(&world, CELL_A, pos, 0.5);
    assert!(sphere_cells.contains(&CELL_B));
    assert!(!sphere_exit);

    let (box_cells, box_exit) = box_transit(&world, CELL_A, &boxes, &[sphere]);
    assert!(!box_cells.contains(&CELL_B));
    assert!(!box_exit);

    let flood = build_shadow_cell_set_from_parts(&world, CELL_A, &boxes, &[sphere], false).unwrap();
    assert!(!flood.contains(&CELL_B));
}

#[test]
fn static_part_array_crossing_exterior_portal_keeps_outdoor_cells() {
    const SEED: u32 = 0xF418_0112;
    let world = world_of(vec![portal_cell(
        SEED,
        Placement::default(),
        0xFFFF,
        0,
        None,
    )]);
    let pos = Vec3::new(2.0, 0.0, 2.5);
    let cells = build_shadow_cell_set_from_parts(
        &world,
        SEED,
        &[part_box(Vec3::splat(-0.7), Vec3::splat(0.7), pos)],
        &[Sphere::new(pos, 0.7)],
        true,
    )
    .unwrap();
    assert!(cells.contains(&SEED));
    assert!(cells.iter().any(|id| (1..=64).contains(&(id & 0xFFFF))));
}

#[test]
fn box_crossing_portal_admitted_with_unloaded_neighbour() {
    let world = world_of(vec![
        portal_cell(CELL_A, Placement::default(), 0x0101, 0, None),
        plain_cell(CELL_B, shifted(5.0), None),
    ]);
    let pos = Vec3::new(2.0, 0.0, 2.5);
    let sphere = Sphere::new(pos, 0.5);
    let (sphere_cells, _) = sphere_transit(&world, CELL_A, pos, 0.5);
    assert!(sphere_cells.contains(&CELL_B));
    let (box_cells, exit) = box_transit(
        &world,
        CELL_A,
        &[part_box(Vec3::splat(-0.7), Vec3::splat(0.7), pos)],
        &[sphere],
    );
    assert!(box_cells.contains(&CELL_B));
    assert!(!exit);
}

#[test]
fn box_crossing_portal_admitted_through_loaded_neighbour_gate() {
    let world = world_of(vec![
        portal_cell(CELL_A, Placement::default(), 0x0101, 0, None),
        plain_cell(CELL_B, shifted(3.0), Some(single_plane_containment())),
    ]);
    let pos = Vec3::new(2.9, 0.0, 2.5);
    let sphere = Sphere::new(pos, 0.5);
    let (sphere_cells, _) = sphere_transit(&world, CELL_A, pos, 0.5);
    assert!(sphere_cells.contains(&CELL_B));
    let (box_cells, exit) = box_transit(
        &world,
        CELL_A,
        &[part_box(Vec3::splat(-0.3), Vec3::splat(0.3), pos)],
        &[sphere],
    );
    assert!(box_cells.contains(&CELL_B));
    assert!(!exit);
}

// ── CellTransitAddAllOutsideCellsTests ─────────────────────────────────

fn outside_cells(center: Vec3, radius: f32, current: u32, origin: Vec3) -> Vec<u32> {
    let mut cells = CellArray::new();
    let mut collector = CellCollector::new(&mut cells);
    add_all_outside_cells(center, radius, current, origin, &mut collector);
    cells.ordered_ids().to_vec()
}

#[test]
fn sphere_well_inside_cell_adds_one_cell() {
    assert_eq!(
        outside_cells(Vec3::new(12.0, 12.0, 0.0), 0.5, 0xA9B4_0001, Vec3::ZERO),
        vec![0xA9B4_0001]
    );
}

#[test]
fn sphere_at_cell_east_boundary_adds_two_cells() {
    let cells = outside_cells(Vec3::new(23.6, 12.0, 0.0), 0.5, 0xA9B4_0001, Vec3::ZERO);
    assert_eq!(cells.len(), 2);
    assert!(cells.contains(&0xA9B4_0001));
    assert!(cells.contains(&0xA9B4_0009));
}

#[test]
fn sphere_just_south_of_block_boundary_adds_both_blocks() {
    let cells = outside_cells(Vec3::new(150.0, -0.2, 0.0), 0.5, 0xA9B4_0031, Vec3::ZERO);
    assert!(cells.contains(&0xA9B3_0038));
    assert!(cells.contains(&0xA9B4_0031));
    assert_eq!(cells.len(), 2);
}

#[test]
fn sphere_deep_in_neighbour_block_adds_neighbour_cell_only() {
    let cells = outside_cells(Vec3::new(150.0, -109.65, 0.0), 0.5, 0xA9B4_0031, Vec3::ZERO);
    assert!(cells.contains(&0xA9B3_0034));
    assert!(!cells.contains(&0xA9B4_0031));
}

#[test]
fn non_anchor_block_origin_converts_world_frame() {
    let cells = outside_cells(
        Vec3::new(150.0, -0.2, 0.0),
        0.5,
        0xA9B3_0038,
        Vec3::new(0.0, -192.0, 0.0),
    );
    assert!(cells.contains(&0xA9B3_0038));
    assert!(cells.contains(&0xA9B4_0031));
}

// ── CellTransitFindVisibleChildCellTests ───────────────────────────────

const START: u32 = 0xA9B4_0174;
const SIBLING: u32 = 0xA9B4_0171;

fn y_bound(normal_y: f32, d: f32) -> CellBsp {
    CellBsp::new(vec![
        CellBspNode::Split {
            plane: Plane::new(Vec3::new(0.0, normal_y, 0.0), d),
            positive: Some(1),
            negative: None,
        },
        CellBspNode::Leaf,
    ])
    .unwrap()
}

fn stab_world() -> CollisionWorld {
    let mut start = plain_cell(START, Placement::default(), Some(y_bound(-1.0, 3.0)));
    start.portals = vec![PortalInfo::new(0xFFFF, 0, 0)];
    start.visible_cells.add(SIBLING);
    let mut sibling = plain_cell(SIBLING, Placement::default(), Some(y_bound(1.0, -7.0)));
    sibling.portals = vec![PortalInfo::new(0xFFFF, 0, 0)];
    world_of(vec![start, sibling])
}

#[test]
fn visible_child_point_inside_start_cell_returns_start() {
    assert_eq!(
        find_visible_child_cell(&stab_world(), START, Vec3::new(0.0, 1.0, 0.0), true, None),
        START
    );
}

#[test]
fn visible_child_point_in_stab_list_sibling_returns_sibling() {
    assert_eq!(
        find_visible_child_cell(&stab_world(), START, Vec3::new(0.0, 8.0, 0.0), true, None),
        SIBLING
    );
}

#[test]
fn visible_child_point_in_no_cell_returns_zero() {
    assert_eq!(
        find_visible_child_cell(&stab_world(), START, Vec3::new(0.0, 5.0, 0.0), true, None),
        0
    );
}

#[test]
fn visible_child_unknown_start_cell_returns_zero() {
    assert_eq!(
        find_visible_child_cell(&CollisionWorld::new(), 0xDEAD_BEEF, Vec3::Y, true, None),
        0
    );
}

// ── CellTransitCheckBuildingTransitTests ───────────────────────────────

fn building(portals: Vec<BldPortalInfo>) -> BuildingPhysics {
    BuildingPhysics {
        placement: Placement::default(),
        portals,
        model_id: 0,
    }
}

fn bld_portal(other_cell_id: u32, other_portal_id: i16, flags: u16) -> BldPortalInfo {
    BldPortalInfo {
        other_cell_id,
        other_portal_id,
        flags,
    }
}

fn building_transit(
    world: &CollisionWorld,
    b: &BuildingPhysics,
    spheres: &[Sphere],
    n: usize,
) -> (Vec<u32>, bool) {
    let mut cells = CellArray::new();
    let mut collector = CellCollector::new(&mut cells);
    let hits = check_building_transit(world, b, spheres, n, &mut collector);
    (cells.ordered_ids().to_vec(), hits)
}

#[test]
fn building_portal_with_rootless_containment_is_rejected() {
    let world = world_of(vec![plain_cell(CELL_A, Placement::default(), None)]);
    let b = building(vec![bld_portal(CELL_A, 0, 0)]);
    assert!(
        building_transit(&world, &b, &[Sphere::new(Vec3::ZERO, 0.5)], 1)
            .0
            .is_empty()
    );
}

#[test]
fn building_portal_with_unavailable_cell_adds_nothing() {
    let b = building(vec![bld_portal(CELL_A, 0, 0)]);
    assert!(
        building_transit(
            &CollisionWorld::new(),
            &b,
            &[Sphere::new(Vec3::ZERO, 0.5)],
            1
        )
        .0
        .is_empty()
    );
}

#[test]
fn negative_other_portal_id_rejects_transit_positive_admits() {
    let world = world_of(vec![
        plain_cell(0xA9B4_0101, Placement::default(), Some(leaf_containment())),
        plain_cell(0xA9B4_0102, Placement::default(), Some(leaf_containment())),
    ]);
    let b = building(vec![
        bld_portal(0xA9B4_0101, -1, 0),
        bld_portal(0xA9B4_0102, 0, 0),
    ]);
    let (cells, _) = building_transit(&world, &b, &[Sphere::new(Vec3::ZERO, 0.5)], 1);
    assert!(!cells.contains(&0xA9B4_0101));
    assert!(cells.contains(&0xA9B4_0102));
}

#[test]
fn multi_sphere_any_sphere_admits_and_sets_hits_interior() {
    let world = world_of(vec![plain_cell(
        0xA9B4_0103,
        Placement::default(),
        Some(leaf_containment()),
    )]);
    let b = building(vec![bld_portal(0xA9B4_0103, 1, 0)]);
    let spheres = [
        Sphere::new(Vec3::new(500.0, 0.0, 0.0), 0.5),
        Sphere::new(Vec3::ZERO, 0.5),
    ];
    let (cells, hits) = building_transit(&world, &b, &spheres, 2);
    assert!(cells.contains(&0xA9B4_0103));
    assert!(hits);
    let (none, hits_none) = building_transit(&world, &b, &spheres, 0);
    assert!(none.is_empty());
    assert!(!hits_none);
}

// ── CellTransitBuildingPartsTests ──────────────────────────────────────

const PREFIX: u32 = 0xA9B4_0000;
const OUTSIDE: u32 = PREFIX | 1;
const PA: u32 = PREFIX | 0x100;
const PB: u32 = PREFIX | 0x101;
const PC: u32 = PREFIX | 0x102;
const ORIGIN: Vec3 = Vec3::new(12.0, 12.0, 0.0);
const F_EPSILON: f32 = 0.0002;

fn permissive() -> Option<Plane> {
    Some(Plane::new(Vec3::Z, 100.0))
}

struct PortalSpec {
    other: u16,
    plane: Plane,
    flags: u16,
}

fn default_portal() -> PortalSpec {
    PortalSpec {
        other: 0xFFFF,
        plane: Plane::new(Vec3::X, 0.0),
        flags: 2,
    }
}

fn spec(other: u16, plane: Plane, flags: u16) -> PortalSpec {
    PortalSpec {
        other,
        plane,
        flags,
    }
}

/// A room at `ORIGIN` whose containment is one plane with no children (or
/// none), with portal polygons numbered from 42.
fn parts_cell(id: u32, containment: Option<Plane>, specs: &[PortalSpec]) -> CellPhysics {
    let mut cell = CellPhysics {
        id,
        placement: Placement::at(ORIGIN),
        ..CellPhysics::default()
    };
    if let Some(plane) = containment {
        cell.containment_bsp = CellBsp::new(vec![CellBspNode::Split {
            plane,
            positive: None,
            negative: None,
        }])
        .unwrap();
    }
    cell.portal_polygons.insert(
        7,
        ResolvedPolygon::new(7, Vec::new(), Plane::new(Vec3::Z, -1000.0)),
    );
    for (i, s) in specs.iter().enumerate() {
        let polygon_id = 42 + i as u16;
        cell.portal_polygons.insert(
            polygon_id,
            ResolvedPolygon::new(polygon_id, Vec::new(), s.plane),
        );
        cell.portals
            .push(PortalInfo::new(s.other, polygon_id, s.flags));
    }
    cell
}

fn parts_world(terrain: bool) -> CollisionWorld {
    let mut world = CollisionWorld::new();
    if terrain {
        world.register_terrain(PREFIX, Vec3::ZERO);
    }
    world
}

fn sphere_at(local: Vec3, radius: f32) -> Sphere {
    Sphere::new(ORIGIN + local, radius)
}

fn small_box() -> ShadowPartBox {
    part_box(Vec3::splat(-0.1), Vec3::splat(0.1), ORIGIN)
}

fn origin_box(min: Vec3, max: Vec3) -> ShadowPartBox {
    part_box(min, max, ORIGIN)
}

fn flood(world: &CollisionWorld, boxes: &[ShadowPartBox], spheres: &[Sphere]) -> Vec<u32> {
    build_shadow_cell_set_from_parts(world, OUTSIDE, boxes, spheres, true).unwrap()
}

#[test]
fn cathedral_like_sphere_hit_box_below_destination_plane_rejects_only_part_route() {
    let mut world = parts_world(true);
    world.insert_cell(parts_cell(
        PA,
        Some(Plane::new(Vec3::Z, -21.8948)),
        &[default_portal()],
    ));
    let b = building(vec![bld_portal(PA, 0, 0)]);
    world.insert_building(OUTSIDE, b.clone());
    let boxes = [origin_box(
        Vec3::new(-1.0, -1.0, 16.86699),
        Vec3::new(1.0, 1.0, 21.894_302),
    )];
    let spheres = [sphere_at(Vec3::new(0.0, 0.0, 19.380_646), 3.93215)];

    let (sphere_cells, hits) = building_transit(&world, &b, &spheres, 1);
    assert!(hits);
    assert_eq!(sphere_cells, vec![PA]);
    assert_eq!(flood(&world, &boxes, &spheres), vec![OUTSIDE]);
}

#[test]
fn reciprocal_side_admits_only_crossing_or_same_side() {
    for (negative_side, box_side, admitted) in [
        (false, -1, false),
        (false, 0, true),
        (false, 1, true),
        (true, -1, true),
        (true, 0, true),
        (true, 1, false),
    ] {
        let flags = if negative_side { 0 } else { 2 };
        let mut world = parts_world(true);
        world.insert_cell(parts_cell(
            PA,
            permissive(),
            &[spec(0xFFFF, Plane::new(Vec3::Z, 0.0), flags)],
        ));
        // Opposite building flags must not replace the room's reciprocal side.
        world.insert_building(OUTSIDE, building(vec![bld_portal(PA, 0, flags ^ 2)]));
        let center = box_side as f32 * 0.5;
        let boxes = [origin_box(
            Vec3::new(-0.1, -0.1, center - 0.1),
            Vec3::new(0.1, 0.1, center + 0.1),
        )];
        let expected = if admitted {
            vec![OUTSIDE, PA]
        } else {
            vec![OUTSIDE]
        };
        assert_eq!(
            flood(&world, &boxes, &[sphere_at(Vec3::ZERO, 1.0)]),
            expected,
            "negative side {negative_side}, box side {box_side}"
        );
    }
}

#[test]
fn building_cheap_gate_equality_admits_adjacent_outside_float_rejects() {
    for (negative_side, beyond) in [(false, false), (false, true), (true, false), (true, true)] {
        let mut world = parts_world(true);
        world.insert_cell(parts_cell(
            PA,
            permissive(),
            &[spec(
                0xFFFF,
                Plane::new(Vec3::Z, 0.0),
                if negative_side { 0 } else { 2 },
            )],
        ));
        world.insert_building(OUTSIDE, building(vec![bld_portal(PA, 0, 0)]));
        let padded = 0.5 + F_EPSILON;
        let mut z = if negative_side { padded } else { -padded };
        if beyond {
            z = if negative_side {
                z.next_up()
            } else {
                z.next_down()
            };
        }
        let box_z = if negative_side {
            padded - 0.5
        } else {
            0.5 - padded
        };
        assert!(box_z.abs() <= F_EPSILON.next_down());
        let boxes = [origin_box(
            Vec3::new(0.0, 0.0, box_z),
            Vec3::new(0.0, 0.0, box_z),
        )];
        let expected = if beyond {
            vec![OUTSIDE]
        } else {
            vec![OUTSIDE, PA]
        };
        assert_eq!(
            flood(&world, &boxes, &[sphere_at(Vec3::new(0.0, 0.0, z), 0.5)]),
            expected,
            "negative side {negative_side}, beyond {beyond}"
        );
    }
}

#[test]
fn earlier_part_rejects_later_part_admits_without_changing_their_order() {
    let mut world = parts_world(true);
    world.insert_cell(parts_cell(
        PA,
        permissive(),
        &[spec(0xFFFF, Plane::new(Vec3::Z, 0.0), 2)],
    ));
    world.insert_building(OUTSIDE, building(vec![bld_portal(PA, 0, 0)]));
    let boxes = [
        origin_box(Vec3::new(-0.1, -0.1, -2.0), Vec3::new(0.1, 0.1, -1.0)),
        origin_box(Vec3::new(-0.1, -0.1, 1.0), Vec3::new(0.1, 0.1, 2.0)),
    ];
    let spheres = [sphere_at(Vec3::ZERO, 3.0), sphere_at(Vec3::ZERO, 3.0)];
    assert_eq!(flood(&world, &boxes[..1], &spheres[..1]), vec![OUTSIDE]);
    assert_eq!(flood(&world, &boxes, &spheres), vec![OUTSIDE, PA]);
}

fn three_room_world() -> CollisionWorld {
    let mut world = parts_world(true);
    world.insert_cell(parts_cell(
        PA,
        permissive(),
        &[default_portal(), spec(0x102, Plane::new(Vec3::X, 0.0), 0)],
    ));
    world.insert_cell(parts_cell(PB, permissive(), &[default_portal()]));
    world.insert_cell(parts_cell(PC, permissive(), &[]));
    world
}

#[test]
fn building_destination_expands_before_next_authored_portal() {
    let mut world = three_room_world();
    world.insert_building(
        OUTSIDE,
        building(vec![bld_portal(PA, 0, 0), bld_portal(PB, 0, 0)]),
    );
    assert_eq!(
        flood(&world, &[small_box()], &[sphere_at(Vec3::ZERO, 1.0)]),
        vec![OUTSIDE, PA, PC, PB]
    );
}

#[test]
fn immediate_destination_expansion_receives_all_parts() {
    let mut world = parts_world(true);
    world.insert_cell(parts_cell(
        PA,
        permissive(),
        &[
            spec(0xFFFF, Plane::new(Vec3::Z, 0.0), 2),
            spec(0x102, Plane::new(Vec3::X, 0.0), 0),
        ],
    ));
    world.insert_cell(parts_cell(PB, permissive(), &[default_portal()]));
    world.insert_cell(parts_cell(PC, permissive(), &[]));
    world.insert_building(
        OUTSIDE,
        building(vec![bld_portal(PA, 0, 0), bld_portal(PB, 0, 0)]),
    );
    let boxes = [
        origin_box(Vec3::new(-1.0, -0.1, 0.1), Vec3::new(-0.5, 0.1, 0.2)),
        origin_box(Vec3::new(0.5, -0.1, 0.1), Vec3::new(1.0, 0.1, 0.2)),
    ];
    assert_eq!(
        flood(
            &world,
            &boxes,
            &[sphere_at(Vec3::ZERO, 2.0), sphere_at(Vec3::ZERO, 2.0)]
        ),
        vec![OUTSIDE, PA, PC, PB]
    );
}

#[test]
fn duplicate_destination_still_expands_immediately_with_shared_outside_latch() {
    for already_outside in [false, true] {
        let world = three_room_world();
        let mut cells = CellArray::new();
        cells.add(PA);
        let mut outside = already_outside;
        {
            let mut collector = CellCollector::new(&mut cells);
            check_building_transit_from_parts(
                &world,
                &building(vec![bld_portal(PA, 0, 0), bld_portal(PB, 0, 0)]),
                &[small_box()],
                &[sphere_at(Vec3::ZERO, 1.0)],
                &mut collector,
                OUTSIDE,
                Vec3::ZERO,
                &mut outside,
            )
            .unwrap();
        }
        assert!(outside);
        let expected = if already_outside {
            vec![PA, PC, PB]
        } else {
            vec![PA, PC, OUTSIDE, PB]
        };
        assert_eq!(
            cells.ordered_ids(),
            expected.as_slice(),
            "already outside {already_outside}"
        );
    }
}

#[test]
fn first_hit_breaks_part_loop() {
    let mut world = parts_world(true);
    world.insert_cell(parts_cell(PA, permissive(), &[default_portal()]));
    world.insert_building(OUTSIDE, building(vec![bld_portal(PA, 0, 0)]));
    assert_eq!(
        flood(
            &world,
            &[small_box(), small_box()],
            &[sphere_at(Vec3::ZERO, 1.0), sphere_at(Vec3::ZERO, 1.0)]
        ),
        vec![OUTSIDE, PA]
    );
}

#[test]
fn negative_reciprocal_is_skipped() {
    for reciprocal in [-1, i16::MIN] {
        let mut world = parts_world(true);
        world.insert_cell(parts_cell(PA, permissive(), &[default_portal()]));
        world.insert_building(OUTSIDE, building(vec![bld_portal(PA, reciprocal, 0)]));
        assert_eq!(
            flood(&world, &[small_box()], &[sphere_at(Vec3::ZERO, 1.0)]),
            vec![OUTSIDE]
        );
    }
}

#[test]
fn unavailable_or_rootless_destination_is_skipped_before_reciprocal_validation() {
    for rootless in [false, true] {
        let mut world = parts_world(true);
        if rootless {
            world.insert_cell(parts_cell(PA, None, &[default_portal()]));
        }
        world.insert_building(OUTSIDE, building(vec![bld_portal(PA, 300, 0)]));
        assert_eq!(
            flood(&world, &[small_box()], &[sphere_at(Vec3::ZERO, 1.0)]),
            vec![OUTSIDE]
        );
    }
}

#[test]
fn malformed_positive_reciprocal_is_a_content_integrity_error() {
    for reciprocal in [1i16, 300] {
        let mut world = parts_world(true);
        world.insert_cell(parts_cell(PA, permissive(), &[default_portal()]));
        world.insert_building(OUTSIDE, building(vec![bld_portal(PA, reciprocal, 0)]));
        let error = build_shadow_cell_set_from_parts(
            &world,
            OUTSIDE,
            &[small_box()],
            &[sphere_at(Vec3::ZERO, 1.0)],
            true,
        )
        .unwrap_err();
        assert_eq!(
            error,
            CellTransitError::MissingReciprocalPortal {
                cell: PA,
                portal: reciprocal as usize,
                portals: 1,
            }
        );
        assert_eq!(
            error.to_string(),
            format!(
                "building portal to cell 0xA9B40100 references reciprocal portal {reciprocal}, \
                 but the destination has 1 portals"
            )
        );
    }
}

#[test]
fn empty_inputs_preserve_seed_and_part_guards() {
    let mut world = parts_world(true);
    world.insert_building(OUTSIDE, building(vec![bld_portal(PA, 0, 0)]));
    assert!(flood(&world, &[], &[]).is_empty());
    assert_eq!(flood(&world, &[small_box()], &[]), vec![OUTSIDE]);
    assert!(
        build_shadow_cell_set_from_parts(
            &world,
            0,
            &[small_box()],
            &[sphere_at(Vec3::ZERO, 1.0)],
            false
        )
        .unwrap()
        .is_empty()
    );
}

#[test]
fn unloaded_active_outdoor_cell_keeps_outside_ids_without_walking_its_building() {
    let mut world = parts_world(false);
    world.insert_cell(parts_cell(PA, permissive(), &[default_portal()]));
    world.insert_building(OUTSIDE, building(vec![bld_portal(PA, 0, 0)]));
    assert_eq!(
        flood(&world, &[small_box()], &[sphere_at(Vec3::ZERO, 1.0)]),
        vec![OUTSIDE]
    );
}

#[test]
fn unloaded_later_outdoor_candidate_does_not_walk_its_building() {
    const ADJACENT: u32 = 0xAAB4_0001;
    let mut world = parts_world(true);
    world.insert_cell(parts_cell(PA, permissive(), &[default_portal()]));
    world.insert_building(ADJACENT, building(vec![bld_portal(PA, 0, 0)]));
    let boxes = [part_box(
        Vec3::new(-1.0, -1.0, -0.1),
        Vec3::new(1.0, 1.0, 0.1),
        Vec3::new(192.0, 12.0, 0.0),
    )];
    let result = build_shadow_cell_set_from_parts(
        &world,
        PREFIX | 57,
        &boxes,
        &[sphere_at(Vec3::new(180.0, 0.0, 0.0), 2.0)],
        false,
    )
    .unwrap();
    assert_eq!(result, vec![PREFIX | 57, ADJACENT]);
}

// ── CellTransitFindCellSetTests ────────────────────────────────────────

fn cell_set(world: &CollisionWorld, center: Vec3, radius: f32, current: u32) -> (u32, Vec<u32>) {
    let mut cells = CellArray::new();
    let containing = find_cell_set(world, center, radius, current, &mut cells, None);
    (containing, cells.ordered_ids().to_vec())
}

#[test]
fn find_cell_set_sphere_fully_inside_primary_returns_only_primary() {
    let world = world_of(vec![portal_cell(
        CELL_A,
        Placement::default(),
        0x0101,
        0,
        Some(leaf_containment()),
    )]);
    let (containing, cells) = cell_set(&world, Vec3::new(-1.0, 0.0, 2.5), 0.5, CELL_A);
    assert_eq!(containing, CELL_A);
    assert_eq!(cells, vec![CELL_A]);
}

#[test]
fn find_cell_set_sphere_straddling_portal_returns_both_cells() {
    let world = world_of(vec![
        portal_cell(
            CELL_A,
            Placement::default(),
            0x0101,
            0,
            Some(leaf_containment()),
        ),
        plain_cell(CELL_B, shifted(5.0), Some(leaf_containment())),
    ]);
    let (_, cells) = cell_set(&world, Vec3::new(2.0, 0.0, 2.5), 0.5, CELL_A);
    assert!(cells.contains(&CELL_A));
    assert!(cells.contains(&CELL_B));
}

fn terrain_world(blocks: &[(u32, Vec3)]) -> CollisionWorld {
    let mut world = CollisionWorld::new();
    for (id, origin) in blocks {
        world.register_terrain(*id, *origin);
    }
    world
}

#[test]
fn find_cell_set_outdoor_seed_includes_neighbour_landcells() {
    let world = terrain_world(&[(0xA9B4_0000, Vec3::ZERO)]);
    let (containing, cells) = cell_set(&world, Vec3::new(23.8, 12.0, 0.0), 0.5, 0xA9B4_0001);
    assert_eq!(containing, 0xA9B4_0001);
    assert!(cells.len() >= 2);
}

#[test]
fn find_cell_set_indoor_seed_exit_portal_touched_only_by_second_sphere_adds_outdoor_cell() {
    let world = world_of(vec![portal_cell(
        CELL_A,
        Placement::default(),
        0xFFFF,
        0,
        Some(leaf_containment()),
    )]);
    let spheres = [
        Sphere::new(Vec3::new(0.0, 12.0, 2.5), 0.5),
        Sphere::new(Vec3::new(2.0, 12.0, 3.2), 0.5),
    ];
    let mut cells = CellArray::new();
    let mut collector = CellCollector::new(&mut cells);
    let (containing, _) =
        build_cell_set_and_pick_containing(&world, &spheres, 2, CELL_A, None, &mut collector);
    assert_eq!(containing, CELL_A);
    assert!(cells.contains(CELL_A));
    assert!(cells.contains(0xA9B4_0001));
}

#[test]
fn find_cell_set_outdoor_seed_crosses_south_boundary_after_destination_hydrates() {
    let mut world = terrain_world(&[(0xA9B4_0000, Vec3::ZERO)]);
    let (unavailable, cells) = cell_set(&world, Vec3::new(150.0, -0.2, 0.0), 0.5, 0xA9B4_0031);
    assert_eq!(unavailable, 0xA9B4_0031);
    assert!(cells.contains(&0xA9B3_0038));
    assert!(cells.contains(&0xA9B4_0031));

    world.register_terrain(0xA9B3_0000, Vec3::new(0.0, -192.0, 0.0));
    let (containing, _) = cell_set(&world, Vec3::new(150.0, -0.2, 0.0), 0.5, 0xA9B4_0031);
    assert_eq!(containing, 0xA9B3_0038);
}

#[test]
fn find_cell_set_outdoor_seed_near_boundary_but_inside_stays_current() {
    let world = terrain_world(&[(0xA9B4_0000, Vec3::ZERO)]);
    let (containing, cells) = cell_set(&world, Vec3::new(150.0, 0.2, 0.0), 0.5, 0xA9B4_0031);
    assert_eq!(containing, 0xA9B4_0031);
    assert!(cells.contains(&0xA9B3_0038));
}

#[test]
fn find_cell_set_outdoor_seed_non_anchor_block_uses_registered_origin() {
    let world = terrain_world(&[
        (0xA9B3_0000, Vec3::new(0.0, -192.0, 0.0)),
        (0xA9B4_0000, Vec3::ZERO),
    ]);
    let (containing, _) = cell_set(&world, Vec3::new(150.0, 1.0, 0.0), 0.5, 0xA9B3_0038);
    assert_eq!(containing, 0xA9B4_0031);
}

#[test]
fn find_cell_set_indoor_seed_sphere_fully_outside_keeps_current() {
    let world = world_of(vec![plain_cell(
        0xA9B4_0150,
        Placement::default(),
        Some(single_plane_containment()),
    )]);
    assert_eq!(
        cell_set(&world, Vec3::new(-10.0, 12.0, 0.0), 0.5, 0xA9B4_0150).0,
        0xA9B4_0150
    );
}

#[test]
fn find_cell_set_indoor_seed_sphere_straddling_boundary_stays_current() {
    let world = world_of(vec![plain_cell(
        0xA9B4_0150,
        Placement::default(),
        Some(single_plane_containment()),
    )]);
    assert_eq!(
        cell_set(&world, Vec3::new(-0.3, 12.0, 0.0), 0.5, 0xA9B4_0150).0,
        0xA9B4_0150
    );
}

#[test]
fn find_visible_child_cell_rootless_containment_is_unavailable() {
    let mut cell = plain_cell(0xA9B4_0150, Placement::default(), None);
    cell.portals = vec![PortalInfo::new(0x0101, 0, 0)];
    let world = world_of(vec![cell]);
    assert_eq!(
        find_visible_child_cell(&world, 0xA9B4_0150, Vec3::new(-10.0, 12.0, 0.0), true, None),
        0
    );
}

#[test]
fn two_overlapping_cells_current_cell_wins_the_straddle() {
    for (current, other) in [(CELL_A, CELL_B), (CELL_B, CELL_A)] {
        let world = world_of(vec![
            portal_cell(
                current,
                Placement::default(),
                (other & 0xFFFF) as u16,
                0,
                Some(leaf_containment()),
            ),
            portal_cell(
                other,
                Placement::default(),
                (current & 0xFFFF) as u16,
                0,
                Some(leaf_containment()),
            ),
        ]);
        let (containing, cells) = cell_set(&world, Vec3::new(2.0, 0.0, 2.5), 0.5, current);
        assert!(cells.contains(&current));
        assert!(cells.contains(&other));
        assert_eq!(containing, current);
    }
}

#[test]
fn find_cell_set_current_cell_is_first_in_the_set() {
    let world = world_of(vec![
        portal_cell(
            CELL_A,
            Placement::default(),
            0x0101,
            0,
            Some(leaf_containment()),
        ),
        plain_cell(CELL_B, shifted(5.0), Some(leaf_containment())),
    ]);
    assert_eq!(
        cell_set(&world, Vec3::new(2.0, 0.0, 2.5), 0.5, CELL_A).1[0],
        CELL_A
    );
}

#[test]
fn indoor_with_exit_portal_interior_wins_while_it_contains_centre() {
    let world = world_of(vec![portal_cell(
        CELL_A,
        Placement::default(),
        0xFFFF,
        0,
        Some(leaf_containment()),
    )]);
    assert_eq!(
        cell_set(&world, Vec3::new(0.0, 12.0, 2.5), 0.5, CELL_A).0,
        CELL_A
    );
}

// ── BuildShadowCellSetTests ────────────────────────────────────────────

fn one(center: Vec3, radius: f32) -> [Sphere; 1] {
    [Sphere::new(center, radius)]
}

fn exterior_portal_cell() -> CellPhysics {
    portal_cell(
        CELL_A,
        Placement::default(),
        0xFFFF,
        0,
        Some(leaf_containment()),
    )
}

#[test]
fn shadow_set_indoor_seed_away_from_portals_floods_seed_only() {
    let world = world_of(vec![portal_cell(
        CELL_A,
        Placement::default(),
        0x0101,
        0,
        Some(leaf_containment()),
    )]);
    assert_eq!(
        build_shadow_cell_set(
            &world,
            CELL_A,
            &one(Vec3::new(-1.0, 0.0, 2.5), 0.5),
            1,
            false
        ),
        vec![CELL_A]
    );
}

#[test]
fn shadow_set_indoor_seed_overlapping_neighbour_floods_neighbour() {
    let world = world_of(vec![
        portal_cell(
            CELL_A,
            Placement::default(),
            0x0101,
            0,
            Some(leaf_containment()),
        ),
        plain_cell(CELL_B, shifted(5.0), Some(leaf_containment())),
    ]);
    let set = build_shadow_cell_set(
        &world,
        CELL_A,
        &one(Vec3::new(2.0, 0.0, 2.5), 0.5),
        1,
        false,
    );
    assert!(set.contains(&CELL_A));
    assert!(set.contains(&CELL_B));
    assert_eq!(set[0], CELL_A);
}

#[test]
fn shadow_set_unloaded_indoor_seed_returns_seed_only() {
    assert_eq!(
        build_shadow_cell_set(
            &CollisionWorld::new(),
            CELL_A,
            &one(Vec3::ZERO, 0.5),
            1,
            false
        ),
        vec![CELL_A]
    );
}

#[test]
fn shadow_set_indoor_seed_refloods_after_cell_arrives() {
    let mut world = CollisionWorld::new();
    let sphere = one(Vec3::new(2.4, 0.0, 2.5), 0.5);
    assert_eq!(
        build_shadow_cell_set(&world, CELL_A, &sphere, 1, false),
        vec![CELL_A]
    );
    world.insert_cell(exterior_portal_cell());
    let hydrated = build_shadow_cell_set(&world, CELL_A, &sphere, 1, false);
    assert!(hydrated.contains(&CELL_A));
    assert!(hydrated.iter().any(|id| id & 0xFFFF < 0x0100));
}

#[test]
fn shadow_set_outdoor_seed_floods_overlapped_landcells() {
    let set = build_shadow_cell_set(
        &CollisionWorld::new(),
        0xA9B4_0001,
        &one(Vec3::new(23.8, 12.0, 0.0), 0.5),
        1,
        false,
    );
    assert!(set.contains(&0xA9B4_0001));
    assert!(set.len() >= 2);
    assert!(set.iter().all(|id| id & 0xFFFF < 0x0100));
}

fn building_world(other_portal_id: i16) -> (CollisionWorld, u32) {
    let mut world = terrain_world(&[(0xA9B4_0000, Vec3::ZERO)]);
    world.insert_cell(plain_cell(
        CELL_B,
        Placement::default(),
        Some(leaf_containment()),
    ));
    let sphere = one(Vec3::new(12.0, 12.0, 0.0), 0.5);
    let landcell = build_shadow_cell_set(&world, 0xA9B4_0001, &sphere, 1, false)[0];
    world.insert_building(
        landcell,
        building(vec![bld_portal(CELL_B, other_portal_id, 0)]),
    );
    (world, landcell)
}

#[test]
fn shadow_set_outdoor_seed_building_on_landcell_adds_interior_cell() {
    let (world, landcell) = building_world(0);
    let set = build_shadow_cell_set(
        &world,
        0xA9B4_0001,
        &one(Vec3::new(12.0, 12.0, 0.0), 0.5),
        1,
        false,
    );
    assert!(set.contains(&landcell));
    assert!(set.contains(&CELL_B));
}

#[test]
fn shadow_set_outdoor_seed_building_portal_negative_id_interior_not_added() {
    let (world, _) = building_world(-1);
    let set = build_shadow_cell_set(
        &world,
        0xA9B4_0001,
        &one(Vec3::new(12.0, 12.0, 0.0), 0.5),
        1,
        false,
    );
    assert!(!set.contains(&CELL_B));
}

#[test]
fn shadow_set_unavailable_outdoor_seed_skips_transit_until_terrain_arrives() {
    let mut world = CollisionWorld::new();
    world.insert_cell(plain_cell(
        CELL_B,
        Placement::default(),
        Some(leaf_containment()),
    ));
    let sphere = one(Vec3::new(12.0, 12.0, 0.0), 0.5);
    let landcell = build_shadow_cell_set(&world, 0xA9B4_0001, &sphere, 1, false)[0];
    world.insert_building(landcell, building(vec![bld_portal(CELL_B, 0, 0)]));

    let unavailable = build_shadow_cell_set(&world, 0xA9B4_0001, &sphere, 1, false);
    assert!(!unavailable.is_empty());
    assert!(unavailable.iter().all(|id| id & 0xFFFF < 0x0100));
    assert!(!unavailable.contains(&CELL_B));

    world.register_terrain(0xA9B4_0000, Vec3::ZERO);
    let hydrated = build_shadow_cell_set(&world, 0xA9B4_0001, &sphere, 1, false);
    assert!(hydrated.contains(&landcell));
    assert!(hydrated.contains(&CELL_B));
}

#[test]
fn shadow_set_loaded_seed_absent_adjacent_landcell_skips_its_building_until_it_arrives() {
    const SEED: u32 = 0xA9B4_0031;
    const ADJACENT: u32 = 0xA9B3_0038;
    const INTERIOR: u32 = 0xA9B3_0100;
    let mut world = terrain_world(&[(0xA9B4_0000, Vec3::ZERO)]);
    world.insert_cell(plain_cell(
        INTERIOR,
        Placement::default(),
        Some(leaf_containment()),
    ));
    world.insert_building(ADJACENT, building(vec![bld_portal(INTERIOR, 0, 0)]));
    let sphere = one(Vec3::new(150.0, 0.2, 0.0), 0.5);

    let unavailable = build_shadow_cell_set(&world, SEED, &sphere, 1, false);
    assert!(unavailable.contains(&SEED));
    assert!(unavailable.contains(&ADJACENT));
    assert!(!unavailable.contains(&INTERIOR));

    world.register_terrain(0xA9B3_0000, Vec3::new(0.0, -192.0, 0.0));
    let hydrated = build_shadow_cell_set(&world, SEED, &sphere, 1, false);
    assert!(hydrated.contains(&ADJACENT));
    assert!(hydrated.contains(&INTERIOR));
}

#[test]
fn shadow_set_indoor_seed_exterior_portal_straddle_adds_outside_cells() {
    let world = world_of(vec![exterior_portal_cell()]);
    let set = build_shadow_cell_set(
        &world,
        CELL_A,
        &one(Vec3::new(2.4, 0.0, 2.5), 0.5),
        1,
        false,
    );
    assert!(set.contains(&CELL_A));
    assert!(set.iter().any(|id| id & 0xFFFF < 0x0100));
}

#[test]
fn shadow_set_indoor_seed_away_from_exterior_portal_no_outside_cells() {
    let world = world_of(vec![exterior_portal_cell()]);
    assert_eq!(
        build_shadow_cell_set(
            &world,
            CELL_A,
            &one(Vec3::new(-1.0, 0.0, 2.5), 0.5),
            1,
            false
        ),
        vec![CELL_A]
    );
}

#[test]
fn shadow_set_static_indoor_seed_prunes_cells_outside_visible_list() {
    let world = world_of(vec![
        portal_cell(
            CELL_A,
            Placement::default(),
            0x0101,
            0,
            Some(leaf_containment()),
        ),
        plain_cell(CELL_B, shifted(5.0), Some(leaf_containment())),
    ]);
    let sphere = one(Vec3::new(2.0, 0.0, 2.5), 0.5);
    assert!(build_shadow_cell_set(&world, CELL_A, &sphere, 1, false).contains(&CELL_B));
    assert_eq!(
        build_shadow_cell_set(&world, CELL_A, &sphere, 1, true),
        vec![CELL_A]
    );
}

#[test]
fn shadow_set_static_indoor_seed_keeps_cells_in_visible_list() {
    let mut seed = portal_cell(
        CELL_A,
        Placement::default(),
        0x0101,
        0,
        Some(leaf_containment()),
    );
    seed.visible_cells.add(CELL_B);
    let world = world_of(vec![
        seed,
        plain_cell(CELL_B, shifted(5.0), Some(leaf_containment())),
    ]);
    let set = build_shadow_cell_set(&world, CELL_A, &one(Vec3::new(2.0, 0.0, 2.5), 0.5), 1, true);
    assert!(set.contains(&CELL_A));
    assert!(set.contains(&CELL_B));
}

#[test]
fn shadow_set_zero_spheres_returns_empty() {
    assert!(build_shadow_cell_set(&CollisionWorld::new(), CELL_A, &[], 0, false).is_empty());
}

// ── BspBoxCellMembershipTests ──────────────────────────────────────────

const LB: u32 = 0xA9B4_0000;
const GX: i32 = 0xA9 * 8;
const GY: i32 = 0xB4 * 8;

fn gcell(gx: i32, gy: i32) -> u32 {
    (((((gx >> 3) << 8) | (gy >> 3)) << 16) | ((gx & 7) * 8 + (gy & 7) + 1)) as u32
}

fn bsp_part(min: Vec3, max: Vec3, local_position: Vec3, local_rotation: Quat) -> ShadowShape {
    let geometry = ShadowPartGeometry {
        sphere: Sphere::new(Vec3::ZERO, 1.0),
        box_min: min,
        box_max: max,
    };
    ShadowShape::bsp(0x0100_46D8, local_position, local_rotation, 1.0, geometry)
}

fn plain_bsp_part(min: Vec3, max: Vec3) -> ShadowShape {
    bsp_part(min, max, Vec3::ZERO, Quat::IDENTITY)
}

fn sphere_only_part(radius: f32) -> ShadowShape {
    ShadowShape::bsp(
        0x0100_46D8,
        Vec3::ZERO,
        Quat::IDENTITY,
        1.0,
        ShadowPartGeometry::new(Sphere::new(Vec3::ZERO, radius), None),
    )
}

fn rectangle(entity: Vec3, seed: u32, shapes: &[ShadowShape]) -> Vec<u32> {
    let boxes: Vec<ShadowPartBox> = shapes
        .iter()
        .map(|s| ShadowPartBox::from_shape(s, entity, Quat::IDENTITY))
        .collect();
    let mut cells = CellArray::new();
    let mut collector = CellCollector::new(&mut cells);
    add_all_outside_cells_from_parts(&boxes, seed, Vec3::ZERO, &mut collector);
    cells.ordered_ids().to_vec()
}

fn sorted(mut v: Vec<u32>) -> Vec<u32> {
    v.sort_unstable();
    v
}

#[test]
fn hundred_metre_box_spans_five_cells_per_axis() {
    let shape = plain_bsp_part(Vec3::new(-50.0, -50.0, -3.0), Vec3::new(50.0, 50.0, 3.0));
    let cells = rectangle(Vec3::new(36.0, 36.0, 0.0), LB | 10, &[shape]);
    let mut expected = Vec::new();
    for x in GX - 1..=GX + 3 {
        for y in GY - 1..=GY + 3 {
            expected.push(gcell(x, y));
        }
    }
    assert_eq!(cells.len(), 25);
    assert_eq!(sorted(expected), sorted(cells));

    let sphere_only = rectangle(
        Vec3::new(36.0, 36.0, 0.0),
        LB | 10,
        &[sphere_only_part(1.0)],
    );
    assert_eq!(sphere_only, vec![gcell(GX + 1, GY + 1)]);
}

#[test]
fn l_shaped_part_array_claims_the_corner_that_closes_the_l() {
    let (min, max) = (Vec3::new(-5.0, -5.0, -2.0), Vec3::new(5.0, 5.0, 2.0));
    let anchor = plain_bsp_part(min, max);
    let east = bsp_part(min, max, Vec3::new(48.0, 0.0, 0.0), Quat::IDENTITY);
    let north = bsp_part(min, max, Vec3::new(0.0, 48.0, 0.0), Quat::IDENTITY);
    let entity = Vec3::new(36.0, 36.0, 0.0);
    let cells = rectangle(entity, LB | 10, &[anchor, east, north]);
    let corner = gcell(GX + 3, GY + 3);
    assert!(cells.contains(&corner));
    assert_eq!(cells.len(), 9);
    assert!(!rectangle(entity, LB | 10, &[anchor]).contains(&corner));
    assert!(!rectangle(entity, LB | 10, &[anchor, east]).contains(&corner));
    assert!(!rectangle(entity, LB | 10, &[anchor, north]).contains(&corner));
}

#[test]
fn box_past_the_block_edge_produces_neighbour_landblock_cells() {
    let shape = plain_bsp_part(Vec3::new(-30.0, -30.0, -2.0), Vec3::new(30.0, 30.0, 2.0));
    let cells = rectangle(Vec3::new(180.0, 180.0, 0.0), LB | 64, &[shape]);
    assert_eq!(cells.len(), 9);
    for (dx, dy) in [(7, 7), (8, 7), (7, 8), (8, 8)] {
        assert!(cells.contains(&gcell(GX + dx, GY + dy)));
    }
    for prefix in [0xAAB4_0000, 0xA9B5_0000, 0xAAB5_0000] {
        assert!(cells.iter().any(|id| id & 0xFFFF_0000 == prefix));
    }
}

#[test]
fn rectangle_at_the_map_corners_emits_nothing_outside_the_map() {
    let shape = plain_bsp_part(Vec3::new(-50.0, -50.0, -2.0), Vec3::new(50.0, 50.0, 2.0));
    let sw = rectangle(Vec3::new(12.0, 12.0, 0.0), 0x0000_0001, &[shape]);
    assert!(sw.iter().all(|id| *id != 0));
    assert_eq!(sw.len(), 9);
    assert!(sw.contains(&0x0000_0001));

    let ne = rectangle(Vec3::new(180.0, 180.0, 0.0), 0xFEFE_0000 | 64, &[shape]);
    assert!(ne.iter().all(|id| *id != 0));
    assert_eq!(ne.len(), 9);
    assert!(ne.contains(&gcell(254 * 8 + 7, 254 * 8 + 7)));
    assert!(!ne.contains(&gcell(2040 & 0x7FF, 2040 & 0x7FF)));
}

#[test]
fn rotated_asymmetric_box_keeps_the_corner_overhang() {
    let yaw37 = Quat::from_axis_angle(Vec3::Z, 37f32.to_radians());
    let shape = bsp_part(Vec3::ZERO, Vec3::new(60.0, 4.0, 2.0), Vec3::ZERO, yaw37);
    let cells = rectangle(Vec3::new(48.0, 12.0, 0.0), LB | 17, &[shape]);
    assert!(cells.contains(&gcell(GX + 1, GY)));
    assert!(cells.contains(&gcell(GX + 3, GY + 2)));
    let flat = rectangle(
        Vec3::new(48.0, 12.0, 0.0),
        LB | 17,
        &[plain_bsp_part(Vec3::ZERO, Vec3::new(60.0, 4.0, 2.0))],
    );
    assert!(!flat.contains(&gcell(GX + 1, GY)));
}

#[test]
fn box_overhanging_the_block_origin_reaches_the_negative_column() {
    let shape = plain_bsp_part(Vec3::new(-20.0, -20.0, -2.0), Vec3::new(20.0, 20.0, 2.0));
    let cells = rectangle(Vec3::new(12.0, 12.0, 0.0), LB | 1, &[shape]);
    assert!(cells.contains(&gcell(GX - 1, GY - 1)));
    assert!(cells.contains(&gcell(GX - 1, GY)));
    assert!(cells.contains(&gcell(GX, GY - 1)));
    assert_eq!(cells.len(), 9);
    assert!(cells.contains(&gcell(GX + 1, GY + 1)));
}

#[test]
fn base_position_off_the_map_adds_nothing() {
    let shape = plain_bsp_part(Vec3::new(-5.0, -5.0, -2.0), Vec3::new(5.0, 5.0, 2.0));
    let boxes = [ShadowPartBox::from_shape(
        &shape,
        Vec3::new(-100_000.0, -100_000.0, 0.0),
        Quat::IDENTITY,
    )];
    let mut cells = CellArray::new();
    let mut collector = CellCollector::new(&mut cells);
    assert!(!add_all_outside_cells_from_parts(
        &boxes,
        0x0000_0001,
        Vec3::ZERO,
        &mut collector
    ));
    assert!(cells.is_empty());
}

fn site(world_pos: Vec3, seed: u32) -> RegistrationSite {
    RegistrationSite {
        world_pos,
        rotation: Quat::IDENTITY,
        world_offset_x: 0.0,
        world_offset_y: 0.0,
        landblock_id: LB,
        seed_cell_id: seed,
    }
}

#[test]
fn cylinder_only_owner_matches_the_sphere_flood() {
    let cylinder = ShadowShape::cylinder(0, Vec3::ZERO, Quat::IDENTITY, 1.0, 12.0, 24.0);
    let world = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    const OWNER: u32 = 0x33_4001;
    let pos = Vec3::new(36.0, 36.0, 50.0);
    reg.register_multi_part(
        &world,
        OWNER,
        &[cylinder],
        site(pos, 0),
        0,
        EntityCollisionFlags::default(),
        false,
        None,
    )
    .unwrap();

    let expected = build_shadow_cell_set(&world, LB | 10, &one(pos, 12.0), 1, false);
    assert_eq!(expected, vec![LB | 10]);
    for index in 1..=64u32 {
        let id = LB | index;
        let held = reg.objects_in_cell(id).iter().any(|e| e.entity_id == OWNER);
        assert_eq!(expected.contains(&id), held, "cell {id:#010X}");
    }
}

#[test]
fn register_multi_part_bsp_owner_occupies_the_full_box_rectangle() {
    let shape = plain_bsp_part(Vec3::new(-50.0, -50.0, -3.0), Vec3::new(50.0, 50.0, 3.0));
    let world = CollisionWorld::new();
    let mut reg = ShadowObjectRegistry::new();
    const OWNER: u32 = 0x33_4002;
    reg.register_multi_part(
        &world,
        OWNER,
        &[shape],
        site(Vec3::new(36.0, 36.0, 0.0), LB | 10),
        0,
        EntityCollisionFlags::default(),
        false,
        None,
    )
    .unwrap();
    let mut held = 0;
    for x in GX - 1..=GX + 3 {
        for y in GY - 1..=GY + 3 {
            let id = gcell(x, y);
            assert!(
                reg.objects_in_cell(id).iter().any(|e| e.entity_id == OWNER),
                "cell {id:#010X}"
            );
            held += 1;
        }
    }
    assert_eq!(held, 25);
}
