//! A landblock's collision made resident from the retail dats, as the
//! runtime's residency loads it (OpenAC `HeadlessCollisionNeighborhood`
//! through `LandblockPhysicsContentBuilder`). These tests need the dats: set
//! `OPENAC_DAT_DIR` or install them under `Documents\Asheron's Call`; run
//! with `cargo test -- --include-ignored`.

use std::path::PathBuf;

use ac_dat::DatCollection;
use ac_dat::objects::Region;
use ac_physics::content::{landblock_origin, publish_landblock};
use ac_physics::{CollisionWorld, ObjectInfoState, PhysicsEngine, ResolveRequest, Vec3};

const HOLTBURG: u32 = 0xA9B4_FFFF;

fn dats() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).expect("open the retail dats")
}

fn height_table(d: &DatCollection) -> [f32; 256] {
    let region: Region = d.get(0x1300_0000).unwrap().expect("region");
    region.land_defs.land_height_table
}

fn holtburg_engine(d: &DatCollection) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    let table = height_table(d);
    for dx in -1i32..=1 {
        for dy in -1i32..=1 {
            let id = ((0xA9i32 + dx) as u32) << 24 | ((0xB4i32 + dy) as u32) << 16 | 0xFFFF;
            publish_landblock(&mut engine, d, &table, id, landblock_origin(HOLTBURG, id))
                .expect("landblock publishes")
                .expect("landblock exists");
        }
    }
    engine
}

fn walker(from: Vec3, to: Vec3, cell: u32) -> ResolveRequest {
    ResolveRequest {
        current_pos: from,
        target_pos: to,
        cell_id: cell,
        sphere_radius: 0.48,
        sphere_height: 1.835,
        step_up_height: 0.4,
        step_down_height: 0.4,
        is_on_ground: true,
        mover_flags: ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE),
        ..ResolveRequest::default()
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_neighbourhood_publishes_cells_buildings_and_statics() {
    let d = dats();
    let table = height_table(&d);
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    let published = publish_landblock(&mut engine, &d, &table, HOLTBURG, Vec3::ZERO)
        .unwrap()
        .unwrap();
    println!(
        "cells={} buildings={} bsp={} setup={} placed={}",
        published.cells,
        published.buildings,
        published.bsp_owners,
        published.setup_owners,
        published.placed.len()
    );
    assert!(published.cells > 50, "Holtburg has many rooms");
    // The placed statics plugins read as scenery: buildings, dat objects
    // and procedural trees, each with a model and a place in the block.
    assert!(published.placed.len() > published.buildings);
    assert!(published.placed.iter().all(|s| s.landblock_id == HOLTBURG
        && matches!(s.source_id >> 24, 0x01 | 0x02)
        && (-1.0..=193.0).contains(&s.position.x)
        && (-1.0..=193.0).contains(&s.position.y)));
    let mut ids: Vec<u32> = published.placed.iter().map(|s| s.id).collect();
    ids.sort_unstable();
    ids.dedup();
    assert_eq!(ids.len(), published.placed.len(), "ids are unique");
    assert!(published.buildings > 10);
    assert!(published.bsp_owners + published.setup_owners > 10);
    assert!(engine.is_landblock_terrain_resident(HOLTBURG));
    let world = engine.world().unwrap();
    // The cottage room the retail cell-search traces use.
    assert!(world.cell(0xA9B4_0171).is_some());
    assert!(world.is_visible(0xA9B4_0001));
    assert!(engine.shadows.total_registered() > 0);
    // Publishing again replaces the landblock rather than doubling it.
    publish_landblock(&mut engine, &d, &table, HOLTBURG, Vec3::ZERO)
        .unwrap()
        .unwrap();
    assert_eq!(engine.landblock_count(), 1);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_walker_cannot_pass_through_a_holtburg_building() {
    let d = dats();
    let engine = holtburg_engine(&d);
    let info: ac_dat::objects::LandBlockInfo = d.get(0xA9B4_FFFE).unwrap().expect("landblock info");
    let mut blocked = 0;
    let mut moved = 0;
    for building in info.buildings.iter().filter(|b| !b.portals.is_empty()) {
        let center = Vec3::new(building.frame.origin.x, building.frame.origin.y, 0.0);
        let start = center + Vec3::new(20.0, 0.0, 0.0);
        let end = center - Vec3::new(20.0, 0.0, 0.0);
        let Some(z0) = engine.sample_terrain_z(start.x, start.y) else {
            continue;
        };
        let start = Vec3::new(start.x, start.y, z0);
        let cell = engine.resolve_cell_id(start, 0.48, HOLTBURG & 0xFFFF_0001);
        let mut at = start;
        let mut cell_id = cell;
        // Walk west in run-sized steps; a shell or wall stops the walk
        // short of the far side.
        for _ in 0..80 {
            let z = engine.sample_terrain_z(at.x - 0.5, at.y).unwrap_or(at.z);
            let r = engine.resolve_with_transition(
                &walker(at, Vec3::new(at.x - 0.5, at.y, z), cell_id),
                None,
            );
            at = r.position;
            cell_id = r.cell_id;
        }
        // The walk got going (the ground carried it) before it stopped.
        if start.x - at.x > 2.0 {
            moved += 1;
        }
        if at.x > end.x + 1.0 {
            blocked += 1;
        }
    }
    println!("buildings that stopped a walk across them: {blocked}; walks that moved: {moved}");
    assert!(moved > 5, "the ground carries a walker");
    assert!(blocked > 5, "building shells collide");
}

/// The procedural scenery around Holtburg (trees, rocks, bushes) collides
/// as OpenAC's headless loader publishes it, under the `0x8XXYYIII` ids,
/// each at the scale it was placed with.
#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_scenery_collides() {
    let d = dats();
    let engine = holtburg_engine(&d);
    let scenery: std::collections::BTreeSet<u32> = engine
        .shadows
        .capture_entries()
        .iter()
        .map(|entry| entry.entity_id)
        .filter(|id| id & 0xF000_0000 == 0x8000_0000)
        .collect();
    println!("scenery objects colliding: {}", scenery.len());
    assert!(scenery.len() > 50, "the woods around Holtburg collide");
    // Each landblock's own namespace: its x and y in the id, all inside the
    // square published.
    let blocks: std::collections::BTreeSet<u32> =
        scenery.iter().map(|id| (id >> 12) & 0xFFFF).collect();
    println!("landblocks with scenery: {blocks:X?}");
    assert!(blocks.len() > 3);
    assert!(
        blocks
            .iter()
            .all(|b| (0xA8..=0xAA).contains(&(b >> 8)) && (0xB3..=0xB5).contains(&(b & 0xFF)))
    );
}

// ── OpenAC `CathedralExteriorWallCollisionInstalledDatTests` ────────────
//
// The cathedral at 0xF418: a character in outdoor cell 0xF4180012 meets
// the shell of the building anchored in 0xF418000A (model 0x01001FB3),
// whose wall runs along x = 48. The neighbouring cell has no terrain under
// the sphere, which must not skip its building (as in OpenAC).

const CATHEDRAL: u32 = 0xF418_FFFF;
const CATHEDRAL_CELL: u32 = 0xF418_0012;
const REPORTED: Vec3 = Vec3::new(48.002_96, 39.257_545, 160.004_99);

struct Human {
    spheres: Vec<ac_physics::CollisionSphere>,
    height: f32,
    step_up: f32,
    step_down: f32,
}

fn human(d: &DatCollection) -> Human {
    let setup: ac_dat::objects::Setup = d.get(0x0200_0001).unwrap().expect("human setup");
    assert_eq!(setup.spheres.len(), 2);
    Human {
        spheres: setup
            .spheres
            .iter()
            .map(|s| {
                ac_physics::CollisionSphere::new(
                    Vec3::new(s.origin.x, s.origin.y, s.origin.z),
                    s.radius,
                )
            })
            .collect(),
        height: setup.height,
        step_up: setup.step_up_height,
        step_down: setup.step_down_height,
    }
}

fn cathedral_engine(d: &DatCollection) -> PhysicsEngine {
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    publish_landblock(&mut engine, d, &height_table(d), CATHEDRAL, Vec3::ZERO)
        .expect("landblock publishes")
        .expect("landblock exists");
    engine
}

fn grounded(position: Vec3) -> ac_physics::PhysicsBody {
    let mut body = ac_physics::PhysicsBody::new();
    body.set_position(position);
    body.contact_plane_valid = true;
    body.contact_plane = ac_physics::Plane::new(Vec3::Z, -160.0);
    body.contact_plane_cell_id = CATHEDRAL_CELL;
    body.transient_state =
        ac_physics::TransientState::CONTACT | ac_physics::TransientState::ON_WALKABLE;
    body
}

fn cathedral_step(
    engine: &PhysicsEngine,
    h: &Human,
    from: Vec3,
    to: Vec3,
) -> ac_physics::ResolveResult {
    let mut body = grounded(from);
    engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: from,
            target_pos: to,
            cell_id: CATHEDRAL_CELL,
            sphere_radius: h.spheres[0].radius,
            sphere_height: h.height,
            step_up_height: h.step_up,
            step_down_height: h.step_down,
            is_on_ground: true,
            mover_flags: ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE),
            moving_entity_id: 0x000F_4243,
            sphere_list: h.spheres.clone(),
            ..ResolveRequest::default()
        },
        Some(&mut body),
    )
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_cathedral_wall_blocks_an_outside_approach() {
    let d = dats();
    let engine = cathedral_engine(&d);
    let h = human(&d);
    let from = Vec3::new(48.6, REPORTED.y, REPORTED.z);
    let r = cathedral_step(&engine, &h, from, REPORTED);
    println!("approach {from} -> {REPORTED}: {} ok={}", r.position, r.ok);
    assert!(r.ok);
    assert!((48.479..=48.601).contains(&r.position.x), "{}", r.position);
    assert!((r.position.y - REPORTED.y).abs() <= 0.001);
    assert!((r.position.z - REPORTED.z).abs() <= 0.01);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_placement_inside_the_cathedral_wall_finds_a_clear_spot_outside() {
    let d = dats();
    let mut engine = cathedral_engine(&d);
    let h = human(&d);
    let placed = engine.set_position(
        &ac_physics::PhysicsSetPositionRequest {
            position: REPORTED,
            cell_id: CATHEDRAL_CELL,
            cell_local_position: REPORTED,
            spheres: h.spheres.clone(),
            step_up_height: h.step_up,
            step_down_height: h.step_down,
            mover_flags: ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE),
            moving_entity_id: 0x000F_4243,
            flags: ac_physics::PhysicsSetPositionFlags(
                ac_physics::PhysicsSetPositionFlags::PLACEMENT
                    | ac_physics::PhysicsSetPositionFlags::SLIDE
                    | ac_physics::PhysicsSetPositionFlags::DO_NOT_CREATE_CELLS,
            ),
            ..ac_physics::PhysicsSetPositionRequest::default()
        },
        None,
    );
    println!(
        "overlap {REPORTED} -> {} {:?}",
        placed.position, placed.error
    );
    assert!(placed.is_committed());
    assert!(
        placed.position.x >= 48.0 + h.spheres[0].radius - 0.001,
        "{}",
        placed.position
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn movement_clear_of_the_cathedral_wall_is_free() {
    let d = dats();
    let engine = cathedral_engine(&d);
    let h = human(&d);
    for (from, to) in [
        (
            Vec3::new(49.0, REPORTED.y, REPORTED.z),
            Vec3::new(49.0, REPORTED.y + 0.1, REPORTED.z),
        ),
        (
            Vec3::new(48.6, REPORTED.y, REPORTED.z),
            Vec3::new(48.8, REPORTED.y, REPORTED.z),
        ),
    ] {
        let r = cathedral_step(&engine, &h, from, to);
        println!("clear {from} -> {to}: {} ok={}", r.position, r.ok);
        assert!(r.ok);
        assert!(r.position.distance(to) <= 0.001, "{}", r.position);
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_character_dropped_onto_the_invisible_ledge_comes_to_rest_on_it() {
    // OpenAC `InvisibleLedgeCollisionInstalledDatTests.ACharacterDropped
    // OntoTheLedgeComesToRestOnIt`: dungeon cell 0x7E0307A3 holds placement
    // 0x02000C2D, whose one part (0x010028C7) draws nothing but carries
    // collision polygons the character stands on.
    const LEDGE_CELL: u32 = 0x7E03_07A3;
    let reported = Vec3::new(310.353_58, -312.687_32, 6.005);
    let d = dats();
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    publish_landblock(&mut engine, &d, &height_table(&d), 0x7E03_FFFF, Vec3::ZERO)
        .expect("landblock publishes")
        .expect("landblock exists");
    let h = human(&d);
    let from = reported + Vec3::Z;
    let to = reported - Vec3::Z;
    let mut body = ac_physics::PhysicsBody::new();
    body.set_position(from);
    let r = engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: from,
            target_pos: to,
            cell_id: LEDGE_CELL,
            sphere_radius: h.spheres[0].radius,
            sphere_height: h.height,
            step_up_height: h.step_up,
            step_down_height: h.step_down,
            is_on_ground: false,
            mover_flags: ObjectInfoState(ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE),
            moving_entity_id: 0x0007_E031,
            sphere_list: h.spheres.clone(),
            ..ResolveRequest::default()
        },
        Some(&mut body),
    );
    println!("drop {from} -> {to}: {} ok={}", r.position, r.ok);
    assert!(r.ok);
    assert!((r.position.z - reported.z).abs() <= 0.05, "{}", r.position);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_walker_cannot_pass_through_an_interior_statue_that_collides_by_its_sphere() {
    // Interior cell 0xD5990125 holds placement 0x02000398 at
    // (177, 182.398, 374.005): a 5.7 m statue whose one part (0x01000F3D)
    // has no collision tree, so it collides through its setup's one sphere
    // (radius 0.944 m, centre 0.944 m up). A character walking north from
    // (177.323, 178.856) meets that sphere about 1.3 m short of the statue's
    // centre; the room itself runs on to y = 186.5.
    const ROOM: u32 = 0xD599_0125;
    const STATUE: Vec3 = Vec3::new(177.0, 182.398, 374.005);
    let start = Vec3::new(177.323, 178.856, 374.005);
    let d = dats();
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    publish_landblock(&mut engine, &d, &height_table(&d), 0xD599_FFFF, Vec3::ZERO)
        .expect("landblock publishes")
        .expect("landblock exists");
    let h = human(&d);
    let mut at = start;
    let mut cell_id = ROOM;
    for _ in 0..20 {
        let to = Vec3::new(at.x, at.y + 0.25, at.z);
        let mut body = ac_physics::PhysicsBody::new();
        body.set_position(at);
        let r = engine.resolve_with_transition(
            &ResolveRequest {
                current_pos: at,
                target_pos: to,
                cell_id,
                sphere_radius: h.spheres[0].radius,
                sphere_height: h.height,
                step_up_height: h.step_up,
                step_down_height: h.step_down,
                is_on_ground: true,
                mover_flags: ObjectInfoState(
                    ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE,
                ),
                moving_entity_id: 0x000D_5991,
                sphere_list: h.spheres.clone(),
                ..ResolveRequest::default()
            },
            Some(&mut body),
        );
        at = r.position;
        cell_id = r.cell_id;
    }
    println!("walk north from {start} stopped at {at} in {cell_id:#010X}");
    assert!(at.y > start.y + 1.0, "the walk got going: {at}");
    assert!(
        (STATUE.y - 1.6..STATUE.y - 1.0).contains(&at.y),
        "the statue's sphere stops the walk short of it: {at}"
    );
}
