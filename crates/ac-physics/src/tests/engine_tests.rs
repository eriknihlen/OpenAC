//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/PhysicsEngineTests.cs`,
//! `PhysicsEngineResidencyTests.cs`, `PhysicsEngineAdjustPositionTests.cs`,
//! `NeighborhoodTerrainResidencyTests.cs`, `ResolveResultOkTests.cs`,
//! `ResolveCellIdTests.cs`, `CellGraphPopulationTests.cs`,
//! `CellGraphMembershipTests.cs`, `RemoveCellsForLandblockTests.cs`,
//! `CellMarchLandblockPreservationTests.cs`, `CellTransitFindCellListTests.cs`,
//! `CellPhysicsPortalWiringTests.cs` and `DungeonTeleportPrefixTests.cs`.
//!
//! OpenAC's allocation-counting cases (`GC.GetAllocatedBytesForCurrentThread`)
//! have no counterpart: they measure the .NET allocator, not behavior.

use ac_dat::objects::{
    BspLeaf, BspNode, BspTree, BspTreeKind, CellStruct, EnvCell, EnvCellFlags, VertexArray,
};
use glam::{Quat, Vec3};

use super::support::*;
use crate::bsp::{CellBsp, CellBspNode};
use crate::cell_array::CellArray;
use crate::cell_transit::find_cell_set;
use crate::collision_flags::EntityCollisionFlags;
use crate::engine::{PhysicsEngine, ResolveRequest};
use crate::geometry::Plane;
use crate::portal::PortalInfo;
use crate::set_position::{PhysicsSetPositionFlags, PhysicsSetPositionRequest};
use crate::shadow::ShadowCollisionType;
use crate::state::ObjectInfoState;
use crate::terrain::TerrainSurface;
use crate::world::{BuildingPhysics, CellPhysics, CollisionWorld, Placement};
use crate::{PhysicsBody, TransientState};

fn zero_terrain() -> TerrainSurface {
    TerrainSurface::new(&[0u8; 81], &[0.0; 256], 0, 0, None)
}

fn flat_engine(z: u8) -> PhysicsEngine {
    engine_with_terrain(terrain_from_heights(&[z; 81]))
}

fn request(
    current: Vec3,
    target: Vec3,
    cell_id: u32,
    radius: f32,
    height: f32,
    on_ground: bool,
) -> ResolveRequest {
    ResolveRequest {
        current_pos: current,
        target_pos: target,
        cell_id,
        sphere_radius: radius,
        sphere_height: height,
        step_up_height: 0.4,
        step_down_height: 0.4,
        is_on_ground: on_ground,
        ..ResolveRequest::default()
    }
}

/// A dat interior cell with no portals and no visible cells.
fn bare_env_cell() -> EnvCell {
    EnvCell {
        id: 0,
        flags: EnvCellFlags::default(),
        cell_id: 0,
        surfaces: Vec::new(),
        environment_id: 0,
        cell_structure: 0,
        position: ac_dat::types::Frame::default(),
        cell_portals: Vec::new(),
        visible_cells: Vec::new(),
        static_objects: Vec::new(),
        restriction_obj: 0,
    }
}

/// A dat cell shape with no polygons, no collision tree and the given
/// containment tree.
fn bare_cell_struct(cell_nodes: Vec<BspNode>) -> CellStruct {
    CellStruct {
        vertex_array: VertexArray {
            vertex_type: VertexArray::SW_VERTEX,
            vertices: Vec::new(),
        },
        polygons: Vec::new(),
        portals: Vec::new(),
        cell_bsp: BspTree {
            kind: BspTreeKind::Cell,
            nodes: cell_nodes,
        },
        physics_polygons: Vec::new(),
        physics_bsp: BspTree {
            kind: BspTreeKind::Physics,
            nodes: Vec::new(),
        },
        drawing_bsp: None,
    }
}

fn dat_leaf() -> BspNode {
    BspNode::Leaf(BspLeaf {
        index: 0,
        solid: 0,
        sphere: ac_dat::types::Sphere::default(),
        polygons: Vec::new(),
    })
}

/// A world holding one interior cell (leaf containment), entered into the
/// cell graph as the dat path does.
fn world_with_graph_cell(cell_id: u32) -> CollisionWorld {
    let mut world = CollisionWorld::new();
    world
        .cache_cell_struct(
            cell_id,
            &bare_env_cell(),
            &bare_cell_struct(vec![dat_leaf()]),
            Placement::default(),
        )
        .unwrap();
    world
}

// ── ResolveResultOkTests ───────────────────────────────────────────────

#[test]
fn no_start_cell_reports_not_ok() {
    let engine = PhysicsEngine::new();
    let r = engine.resolve_with_transition(&request(Vec3::ZERO, Vec3::X, 0, 0.3, 0.0, false), None);
    assert!(!r.ok);
}

#[test]
fn zero_movement_valid_cell_reports_ok() {
    let engine = PhysicsEngine::new();
    let p = Vec3::splat(5.0);
    let r = engine.resolve_with_transition(&request(p, p, 0xA9B4_0001, 0.3, 0.0, false), None);
    assert!(r.ok);
}

// ── ResolveCellIdTests ─────────────────────────────────────────────────

#[test]
fn resolve_cell_id_fallback_zero_returns_zero() {
    assert_eq!(PhysicsEngine::new().resolve_cell_id(Vec3::ZERO, 0.5, 0), 0);
}

#[test]
fn resolve_cell_id_no_landblock_outdoor_seed_returns_fallback() {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    assert_eq!(
        engine.resolve_cell_id(Vec3::new(100.0, 100.0, 0.0), 0.5, 0xA9B4_0001),
        0xA9B4_0001
    );
}

#[test]
fn resolve_cell_id_no_world_returns_fallback() {
    let engine = PhysicsEngine::new();
    assert_eq!(
        engine.resolve_cell_id(Vec3::new(100.0, 100.0, 0.0), 0.5, 0xA9B4_0100),
        0xA9B4_0100
    );
}

// ── CellGraphPopulationTests ───────────────────────────────────────────

#[test]
fn cache_cell_struct_rejects_rootless_containment_then_allows_valid_retry() {
    let mut world = CollisionWorld::new();
    let env = bare_env_cell();
    world
        .cache_cell_struct(
            0xA9B4_0174,
            &env,
            &bare_cell_struct(Vec::new()),
            Placement::default(),
        )
        .unwrap();
    assert!(world.cell(0xA9B4_0174).is_none());
    assert!(!world.is_visible(0xA9B4_0174));

    world
        .cache_cell_struct(
            0xA9B4_0174,
            &env,
            &bare_cell_struct(vec![dat_leaf()]),
            Placement::default(),
        )
        .unwrap();
    let loaded = world.cell(0xA9B4_0174).expect("cached");
    assert!(!loaded.has_physics());
    assert!(loaded.has_containment());
    assert!(world.is_visible(0xA9B4_0174));
}

// ── CellGraphMembershipTests ───────────────────────────────────────────

fn engine_with_graph_cell(cell_id: u32) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world_with_graph_cell(cell_id));
    engine
}

#[test]
fn update_player_curr_cell_resolved_writes_curr_cell() {
    let mut engine = engine_with_graph_cell(0xA9B4_0174);
    engine.update_player_curr_cell(0xA9B4_0174);
    assert_eq!(engine.world().unwrap().curr_cell(), Some(0xA9B4_0174));
}

#[test]
fn update_player_curr_cell_unresolvable_id_leaves_curr_cell_unchanged() {
    let mut engine = engine_with_graph_cell(0xA9B4_0174);
    engine.update_player_curr_cell(0xA9B4_0174);
    engine.update_player_curr_cell(0xDEAD_BEEF);
    assert_eq!(engine.world().unwrap().curr_cell(), Some(0xA9B4_0174));
}

#[test]
fn resolve_cell_id_does_not_write_the_current_cell() {
    let engine = engine_with_graph_cell(0xA9B4_0174);
    assert_eq!(
        engine.resolve_cell_id(Vec3::ZERO, 0.5, 0xA9B4_0174),
        0xA9B4_0174
    );
    assert_eq!(engine.world().unwrap().curr_cell(), None);
}

// ── RemoveCellsForLandblockTests ───────────────────────────────────────

fn minimal_cell(id: u32) -> CellPhysics {
    CellPhysics {
        id,
        ..CellPhysics::default()
    }
}

#[test]
fn remove_cells_for_landblock_evicts_only_matching_prefix() {
    let mut world = CollisionWorld::new();
    world.insert_cell(minimal_cell(0xAAAA_0100));
    world.insert_cell(minimal_cell(0xAAAA_0200));
    world.insert_cell(minimal_cell(0xBBBB_0100));
    world.remove_cells_for_landblock(0xAAAA_0000);
    assert!(world.cell(0xAAAA_0100).is_none());
    assert!(world.cell(0xAAAA_0200).is_none());
    assert!(world.cell(0xBBBB_0100).is_some());
}

#[test]
fn remove_cells_for_landblock_empty_world_is_harmless() {
    let mut world = CollisionWorld::new();
    world.remove_cells_for_landblock(0xAAAA_0000);
    assert_eq!(world.cell_ids().count(), 0);
}

#[test]
fn remove_cells_for_landblock_no_matching_cells_leaves_others_intact() {
    let mut world = CollisionWorld::new();
    world.insert_cell(minimal_cell(0xBBBB_0100));
    world.remove_cells_for_landblock(0xAAAA_0000);
    assert!(world.cell(0xBBBB_0100).is_some());
    assert_eq!(world.cell_ids().count(), 1);
}

// ── CellMarchLandblockPreservationTests ────────────────────────────────

fn lb_x(cell_id: u32) -> u32 {
    (cell_id >> 24) & 0xFF
}

fn lb_y(cell_id: u32) -> u32 {
    (cell_id >> 16) & 0xFF
}

#[test]
fn west_edge_unstreamed_landblock_no_anchor_preserves_seed_cell() {
    let world = CollisionWorld::new();
    let mut cells = CellArray::new();
    let result = find_cell_set(
        &world,
        Vec3::new(-0.088, 14.8, 12.0),
        0.48,
        0xA9B4_0019,
        &mut cells,
        None,
    );
    assert_eq!(lb_x(result), 0xA9);
    assert_eq!(result, 0xA9B4_0019);
}

#[test]
fn south_edge_unstreamed_landblock_no_anchor_preserves_seed_cell() {
    let world = CollisionWorld::new();
    let mut cells = CellArray::new();
    let result = find_cell_set(
        &world,
        Vec3::new(14.8, -0.088, 12.0),
        0.48,
        0xA9B4_0001,
        &mut cells,
        None,
    );
    assert_eq!(lb_y(result), 0xB4);
    assert_eq!(result, 0xA9B4_0001);
}

#[test]
fn with_carried_anchor_unstreamed_neighbour_returns_correct_cell() {
    let world = CollisionWorld::new();
    let mut cells = CellArray::new();
    let result = find_cell_set(
        &world,
        Vec3::new(14.8, -0.088, 12.0),
        0.48,
        0xA9B3_0001,
        &mut cells,
        Some(Vec3::new(0.0, -192.0, 0.0)),
    );
    assert_eq!(lb_y(result), 0xB3);
}

// ── CellTransitFindCellListTests ───────────────────────────────────────

#[test]
fn find_cell_list_indoor_seed_without_cell_returns_fallback() {
    let mut cells = CellArray::new();
    let result = find_cell_set(
        &CollisionWorld::new(),
        Vec3::ZERO,
        0.5,
        0xA9B4_0100,
        &mut cells,
        None,
    );
    assert_eq!(result, 0xA9B4_0100);
}

#[test]
fn find_cell_list_outdoor_seed_without_terrain_returns_fallback() {
    let mut cells = CellArray::new();
    let result = find_cell_set(
        &CollisionWorld::new(),
        Vec3::new(12.0, 12.0, 0.0),
        0.5,
        0xA9B4_0001,
        &mut cells,
        None,
    );
    assert_eq!(result, 0xA9B4_0001);
}

// ── CellPhysicsPortalWiringTests ───────────────────────────────────────

#[test]
fn cell_physics_defaults_are_empty() {
    let cp = CellPhysics::default();
    assert!(!cp.has_containment());
    assert!(cp.portals.is_empty());
    assert!(cp.portal_polygons.is_empty());
    assert!(cp.visible_cells.is_empty());
}

#[test]
fn cell_physics_portals_round_trip() {
    let cp = CellPhysics {
        portals: vec![PortalInfo::new(0x0101, 7, 0), PortalInfo::new(0xFFFF, 8, 2)],
        ..CellPhysics::default()
    };
    assert_eq!(cp.portals.len(), 2);
    assert_eq!(cp.portals[0].other_cell_id, 0x0101);
    assert!(cp.portals[0].portal_side());
    assert_eq!(cp.portals[1].other_cell_id, 0xFFFF);
    assert!(!cp.portals[1].portal_side());
}

// ── NeighborhoodTerrainResidencyTests ──────────────────────────────────

const FAR_RADIUS: i32 = 12;

fn canonical(x: i32, y: i32) -> u32 {
    ((x as u32) << 24) | ((y as u32) << 16) | 0xFFFF
}

fn add_terrain(engine: &mut PhysicsEngine, landblock_id: u32) {
    engine.add_landblock(
        landblock_id,
        zero_terrain(),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
}

fn engine_with_window(cx: i32, cy: i32, radius: i32) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    for dx in -radius..=radius {
        for dy in -radius..=radius {
            add_terrain(&mut engine, canonical(cx + dx, cy + dy));
        }
    }
    engine
}

#[test]
fn neighborhood_query_rejects_a_missing_outer_ring_member() {
    let mut engine = engine_with_window(0x80, 0x80, FAR_RADIUS);
    let center = canonical(0x80, 0x80);
    assert!(engine.is_neighborhood_terrain_resident(center, FAR_RADIUS));
    engine.remove_landblock(canonical(0x80 + FAR_RADIUS, 0x80));
    assert!(!engine.is_neighborhood_terrain_resident(center, FAR_RADIUS));
    assert!(engine.is_neighborhood_terrain_resident(center, FAR_RADIUS - 1));
}

#[test]
fn neighborhood_query_keeps_prefix_masked_membership() {
    let mut engine = PhysicsEngine::new();
    add_terrain(&mut engine, (0x40 << 24) | (0x40 << 16) | 0x0021);
    assert!(engine.is_neighborhood_terrain_resident(canonical(0x40, 0x40), 0));
    assert!(!engine.is_neighborhood_terrain_resident(canonical(0x41, 0x40), 0));
}

// ── PhysicsEngineResidencyTests ────────────────────────────────────────

#[test]
fn is_landblock_terrain_resident_true_only_after_add_landblock() {
    let mut engine = PhysicsEngine::new();
    assert!(!engine.is_landblock_terrain_resident(0xA9B4_FFFF));
    add_terrain(&mut engine, 0xA9B4_0000);
    assert!(engine.is_landblock_terrain_resident(0xA9B4_FFFF));
}

#[test]
fn is_landblock_terrain_resident_matches_on_high_16() {
    let mut engine = PhysicsEngine::new();
    add_terrain(&mut engine, 0xA9B4_0000);
    assert!(engine.is_landblock_terrain_resident(0xA9B4_0019));
    assert!(!engine.is_landblock_terrain_resident(0xC6A9_0019));
}

#[test]
fn demote_landblock_to_terrain_removes_env_cells_but_preserves_terrain_residency() {
    const LANDBLOCK: u32 = 0xA9B4_FFFF;
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world_with_graph_cell(0xA9B4_0174));
    add_terrain(&mut engine, LANDBLOCK);
    assert!(engine.world().unwrap().is_visible(0xA9B4_0174));

    engine.demote_landblock_to_terrain(LANDBLOCK);

    assert!(engine.is_landblock_terrain_resident(LANDBLOCK));
    let world = engine.world().unwrap();
    assert!(!world.is_visible(0xA9B4_0174));
    assert!(world.is_visible(0xA9B4_0001));
}

fn register_seam_object(engine: &mut PhysicsEngine, id: u32, gfx: u32, is_static: bool) {
    let (shadows, world) = engine.shadows_and_world();
    shadows
        .register(
            world,
            id,
            &crate::SingleShape {
                gfx_obj_id: gfx,
                radius: 2.0,
                collision_type: ShadowCollisionType::Bsp,
                cyl_height: 0.0,
                scale: 1.0,
            },
            crate::RegistrationSite {
                world_pos: Vec3::new(191.5, 12.0, 1.0),
                rotation: Quat::IDENTITY,
                world_offset_x: 0.0,
                world_offset_y: 0.0,
                landblock_id: 0xA9B4_FFFF,
                seed_cell_id: 0,
            },
            0,
            EntityCollisionFlags::default(),
            is_static,
            None,
        )
        .unwrap();
}

#[test]
fn demote_landblock_to_terrain_deregisters_cross_boundary_static_but_retains_dynamic() {
    const ADJACENT: u32 = 0xAAB4_0001;
    let mut engine = PhysicsEngine::new();
    add_terrain(&mut engine, 0xA9B4_FFFF);
    register_seam_object(&mut engine, 100, 0x0100_0001, true);
    register_seam_object(&mut engine, 200, 0x0100_0002, false);
    let ids = |e: &PhysicsEngine| -> Vec<u32> {
        e.shadows
            .objects_in_cell(ADJACENT)
            .iter()
            .map(|x| x.entity_id)
            .collect()
    };
    assert!(ids(&engine).contains(&100));
    assert!(ids(&engine).contains(&200));

    engine.demote_landblock_to_terrain(0xA9B4_FFFF);

    assert!(
        !engine
            .shadows
            .capture_entries()
            .iter()
            .any(|e| e.entity_id == 100)
    );
    assert!(ids(&engine).contains(&200));
    assert_eq!(engine.shadows.retained_registration_count(), 1);
}

#[test]
fn remove_landblock_deregisters_cross_boundary_static_but_retains_dynamic() {
    const ADJACENT: u32 = 0xAAB4_0001;
    let mut engine = PhysicsEngine::new();
    add_terrain(&mut engine, 0xA9B4_FFFF);
    register_seam_object(&mut engine, 300, 0x0100_0003, true);
    register_seam_object(&mut engine, 400, 0x0100_0004, false);

    engine.remove_landblock(0xA9B4_FFFF);

    assert!(
        !engine
            .shadows
            .capture_entries()
            .iter()
            .any(|e| e.entity_id == 300)
    );
    assert!(
        engine
            .shadows
            .objects_in_cell(ADJACENT)
            .iter()
            .any(|e| e.entity_id == 400)
    );
    assert_eq!(engine.shadows.retained_registration_count(), 1);
}

#[test]
fn remove_landblock_removes_only_its_cached_buildings() {
    let mut world = CollisionWorld::new();
    world.insert_building(0xA9B4_0001, BuildingPhysics::default());
    world.insert_building(
        0xAAB4_0001,
        BuildingPhysics {
            placement: Placement::new(Quat::IDENTITY, Vec3::new(192.0, 0.0, 0.0)),
            ..BuildingPhysics::default()
        },
    );
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.remove_landblock(0xA9B4_FFFF);
    let world = engine.world().unwrap();
    assert!(world.building(0xA9B4_0001).is_none());
    assert!(world.building(0xAAB4_0001).is_some());
}

// ── PhysicsEngineAdjustPositionTests ───────────────────────────────────

const FEET_CELL: u32 = 0xA9B4_0174;
const ROOM_CELL: u32 = 0xA9B4_0171;

fn y_at_most(boundary: f32) -> CellBsp {
    CellBsp::new(vec![
        CellBspNode::Split {
            plane: Plane::new(-Vec3::Y, boundary),
            positive: Some(1),
            negative: None,
        },
        CellBspNode::Leaf,
    ])
    .unwrap()
}

fn y_at_least(boundary: f32) -> CellBsp {
    CellBsp::new(vec![
        CellBspNode::Split {
            plane: Plane::new(Vec3::Y, -boundary),
            positive: Some(1),
            negative: None,
        },
        CellBspNode::Leaf,
    ])
    .unwrap()
}

fn portal_cell(id: u32, containment: CellBsp, visible: &[u32]) -> CellPhysics {
    let mut cell = cell_with_bsp(
        id,
        single_leaf(Vec3::ZERO, 0.0, Vec::new()),
        Placement::default(),
    );
    cell.containment_bsp = containment;
    cell.portals = vec![PortalInfo::new(0xFFFF, 0, 0)];
    for v in visible {
        cell.visible_cells.add(*v);
    }
    cell
}

fn indoor_engine() -> PhysicsEngine {
    let mut world = CollisionWorld::new();
    world.insert_cell(portal_cell(FEET_CELL, y_at_most(3.0), &[ROOM_CELL]));
    world.insert_cell(portal_cell(ROOM_CELL, y_at_least(7.0), &[]));
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    add_terrain(&mut engine, 0xA9B4_0000);
    engine
}

#[test]
fn adjust_position_indoor_pivot_in_visible_sibling_resolves_sibling() {
    let engine = indoor_engine();
    assert_eq!(
        engine.adjust_position(FEET_CELL, Vec3::new(0.0, 8.0, 0.0)),
        (ROOM_CELL, true)
    );
}

#[test]
fn adjust_position_indoor_point_in_no_cell_not_found() {
    let engine = indoor_engine();
    assert!(
        !engine
            .adjust_position(FEET_CELL, Vec3::new(0.0, 5.0, 0.0))
            .1
    );
}

#[test]
fn adjust_position_outdoor_point_in_landblock_snaps_to_landcell() {
    let engine = indoor_engine();
    let (cell, found) = engine.adjust_position(0xA9B4_0001, Vec3::new(12.0, 12.0, 50.0));
    assert!(found);
    assert_eq!(cell & 0xFFFF_0000, 0xA9B4_0000);
    assert!(cell & 0xFFFF < 0x0100);
}

// ── DungeonTeleportPrefixTests ─────────────────────────────────────────

#[test]
fn validated_dungeon_claim_keeps_its_landblock_prefix_not_the_neighbour() {
    const DUNGEON_CELL: u32 = 0x0007_0143;
    // OpenAC resolves a floor polygon for this cell but its single collision
    // leaf lists no polygon, so nothing in the room is solid.
    let mut cell = cell_with_bsp(
        DUNGEON_CELL,
        single_leaf(Vec3::ZERO, 0.0, Vec::new()),
        Placement::default(),
    );
    cell.containment_bsp = CellBsp::new(vec![CellBspNode::Leaf]).unwrap();
    cell.portals = vec![PortalInfo::new(0xFFFF, 0, 0)];
    let mut world = CollisionWorld::new();
    world.insert_cell(cell);
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(
        0xA9B3_0000,
        zero_terrain(),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    engine.add_landblock(
        0x0007_0000,
        zero_terrain(),
        Vec::new(),
        Vec::new(),
        0.0,
        130.0,
    );

    let spawn = Vec3::new(70.0, 70.0, 0.01);
    let result = engine.set_position(
        &PhysicsSetPositionRequest {
            position: spawn,
            orientation: Quat::IDENTITY,
            cell_id: DUNGEON_CELL,
            cell_local_position: spawn,
            scale: 1.0,
            step_up_height: 0.4,
            step_down_height: 0.4,
            flags: PhysicsSetPositionFlags(
                PhysicsSetPositionFlags::PLACEMENT
                    | PhysicsSetPositionFlags::TELEPORT
                    | PhysicsSetPositionFlags::SLIDE,
            ),
            ..PhysicsSetPositionRequest::default()
        },
        None,
    );
    assert!(result.is_committed(), "{result:?}");
    assert_eq!(result.cell_id, DUNGEON_CELL);
    assert_eq!(result.cell_id & 0xFFFF_0000, 0x0007_0000);
}

// ── PhysicsEngineTests ─────────────────────────────────────────────────

#[test]
fn resolve_outdoor_cell_boundary_updates_low_cell_id() {
    let engine = flat_engine(50);
    let r = engine.resolve_with_transition(
        &request(
            Vec3::new(23.0, 10.0, 50.0),
            Vec3::new(25.0, 10.0, 50.0),
            0x0001,
            0.5,
            1.2,
            true,
        ),
        None,
    );
    assert!(r.is_on_ground);
    assert!((24.9..=25.1).contains(&r.position.x));
    assert_eq!(r.cell_id, 0xA9B4_0009);
}

#[test]
fn resolve_edge_slide_flag_allows_normal_flat_movement() {
    let engine = flat_engine(50);
    let r = engine.resolve_with_transition(
        &ResolveRequest {
            mover_flags: ObjectInfoState(ObjectInfoState::EDGE_SLIDE),
            ..request(
                Vec3::new(96.0, 96.0, 50.0),
                Vec3::new(98.0, 96.0, 50.0),
                0x0025,
                0.5,
                1.2,
                true,
            )
        },
        None,
    );
    assert!(r.is_on_ground);
    assert!((97.9..=98.1).contains(&r.position.x));
    assert_eq!(r.cell_id, 0xA9B4_0025);
}

fn grounded_body(position: Vec3) -> PhysicsBody {
    let mut body = PhysicsBody::new();
    body.set_position(position);
    body.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
    body.contact_plane_valid = true;
    body.contact_plane = Plane::new(Vec3::Z, -50.0);
    body.contact_plane_cell_id = 0x003D;
    body
}

fn edge_slide(current: Vec3, target: Vec3) -> ResolveRequest {
    ResolveRequest {
        mover_flags: ObjectInfoState(ObjectInfoState::EDGE_SLIDE),
        ..request(current, target, 0x003D, 0.5, 1.2, true)
    }
}

#[test]
fn resolve_edge_slide_stops_at_loaded_terrain_boundary() {
    let engine = flat_engine(50);
    let start = Vec3::new(191.25, 96.0, 50.0);
    let mut body = grounded_body(start);
    let r = engine.resolve_with_transition(
        &edge_slide(start, Vec3::new(193.0, 96.0, 50.0)),
        Some(&mut body),
    );
    assert!(r.is_on_ground);
    assert!(
        (190.75..=192.0001).contains(&r.position.x),
        "{}",
        r.position.x
    );
    assert_eq_prec(50.0, r.position.z, 2);
}

#[test]
fn resolve_edge_slide_at_loaded_terrain_boundary_preserves_tangent_motion() {
    let engine = flat_engine(50);
    let start = Vec3::new(191.0, 96.0, 50.0);
    let mut body = grounded_body(start);
    let settled = engine.resolve_with_transition(
        &edge_slide(start, Vec3::new(191.25, 96.0, 50.0)),
        Some(&mut body),
    );
    assert!(body.walkable_polygon_valid);
    assert!(body.walkable_vertices.is_some());

    let r = engine.resolve_with_transition(
        &edge_slide(settled.position, Vec3::new(193.0, 98.0, 50.0)),
        Some(&mut body),
    );
    assert!(r.is_on_ground);
    assert!(
        (190.75..=192.0001).contains(&r.position.x),
        "{}",
        r.position.x
    );
    assert!(r.position.y > 96.2);
    assert_eq_prec(50.0, r.position.z, 2);
}

#[test]
fn resolve_landblock_boundary_updates_full_outdoor_cell_id() {
    let mut engine = flat_engine(50);
    engine.add_landblock(
        0xAAB4_FFFF,
        terrain_from_heights(&[50; 81]),
        Vec::new(),
        Vec::new(),
        192.0,
        0.0,
    );
    let r = engine.resolve_with_transition(
        &request(
            Vec3::new(191.0, 10.0, 50.0),
            Vec3::new(193.0, 10.0, 50.0),
            0xA9B4_0039,
            0.5,
            1.2,
            true,
        ),
        None,
    );
    assert!(r.is_on_ground);
    assert!((192.9..=193.1).contains(&r.position.x));
    assert_eq!(r.cell_id, 0xAAB4_0001);
}

#[test]
fn resolve_self_shadow_entry_not_pushed_when_id_matches() {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    engine.add_landblock(
        0xA9B4_FFFF,
        terrain_from_heights(&[50; 81]),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    const MOVER: u32 = 0xDEAD_BEEF;
    let body_pos = Vec3::new(96.0, 96.0, 50.0);
    let target = body_pos + Vec3::new(0.0, 0.0, 0.022);
    register_single(
        &mut engine,
        MOVER,
        0x0200_0001,
        body_pos,
        Quat::IDENTITY,
        0.679,
        0xA9B4_FFFF,
        ShadowCollisionType::Cylinder,
        1.835,
        1.0,
        0,
        EntityCollisionFlags::default(),
        0,
    );

    let unfiltered = engine.resolve_with_transition(
        &request(body_pos, target, 0xA9B4_0039, 0.48, 1.2, false),
        None,
    );
    assert!(
        unfiltered.position.z < target.z - 0.01,
        "the mover's own shadow must block the rise, got z {}",
        unfiltered.position.z
    );

    let filtered = engine.resolve_with_transition(
        &ResolveRequest {
            moving_entity_id: MOVER,
            ..request(body_pos, target, 0xA9B4_0039, 0.48, 1.2, false)
        },
        None,
    );
    let xy = (filtered.position - target).truncate().length();
    assert!((0.0..=0.001).contains(&xy));
}

/// A window builds its camera's collision engine on its loader thread and
/// hands it to the window's thread, so the engine must be `Send`.
#[test]
fn an_engine_can_move_between_threads() {
    fn assert_send<T: Send>() {}
    assert_send::<PhysicsEngine>();
}
