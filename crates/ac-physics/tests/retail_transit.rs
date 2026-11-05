//! Cell transit against the retail dat files.
//!
//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/CellTransitTests.cs`
//! and the installed-dat case of `CellTransitFindTransitCellsBoxTests.cs`
//! (its .NET `Random` sequence cannot be reproduced, so a fixed xorshift
//! stream draws the same distributions; the assertions are properties of
//! every draw). These tests need the dats: set `OPENAC_DAT_DIR` or install
//! them under `Documents\Asheron's Call`; run with
//! `cargo test -- --include-ignored`.

use std::path::PathBuf;

use ac_dat::DatCollection;
use ac_dat::objects::{EnvCell, Environment};
use ac_physics::cell_transit::{
    CellCollector, find_cell_set, find_transit_cells_box, find_transit_cells_sphere,
};
use ac_physics::dat::{quat, vec3};
use ac_physics::{CellArray, CollisionWorld, Placement, Quat, ShadowPartBox, Sphere, Vec3};

fn dats() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).expect("open the retail dats")
}

/// Caches one interior cell from the dats, placed where its record says.
fn load_env_cell(d: &DatCollection, world: &mut CollisionWorld, cell_id: u32) {
    let cell: EnvCell = d.get(cell_id).unwrap().expect("cell in the dats");
    let env: Environment = d
        .get(cell.environment_file_id())
        .unwrap()
        .expect("environment in the dats");
    let structure = env
        .cell(u32::from(cell.cell_structure))
        .expect("cell structure in its environment");
    let placement = Placement::new(quat(cell.position.orientation), vec3(cell.position.origin));
    world
        .cache_cell_struct(cell_id, &cell, structure, placement)
        .expect("cell trees prepare");
}

#[test]
#[ignore = "needs the retail dat files"]
fn deep_interior_sphere_no_straddle_adds_no_outdoor_cells() {
    let d = dats();
    let mut world = CollisionWorld::new();
    load_env_cell(&d, &mut world, 0xA9B4_013F);
    load_env_cell(&d, &mut world, 0xA9B4_0150);
    assert!(world.cell(0xA9B4_013F).is_some());
    assert!(world.cell(0xA9B4_0150).is_some());

    let mut cells = CellArray::new();
    find_cell_set(
        &world,
        Vec3::new(132.5935, 16.350_428, 94.48),
        0.48,
        0xA9B4_013F,
        &mut cells,
        None,
    );
    assert!(
        cells.iter().all(|c| c & 0xFFFF >= 0x0100),
        "{:X?}",
        cells.ordered_ids()
    );
    assert!(cells.contains(0xA9B4_013F));
}

#[test]
#[ignore = "needs the retail dat files"]
fn alcove_sphere_straddling_exit_portal_reaches_door_outdoor_cell() {
    let d = dats();
    let mut world = CollisionWorld::new();
    load_env_cell(&d, &mut world, 0xA9B4_0150);
    let mut cells = CellArray::new();
    find_cell_set(
        &world,
        Vec3::new(132.4014, 16.761_757, 94.48),
        0.48,
        0xA9B4_0150,
        &mut cells,
        None,
    );
    assert!(cells.contains(0xA9B4_0029), "{:X?}", cells.ordered_ids());
}

fn part_box(half: f32, world_position: Vec3) -> ShadowPartBox {
    ShadowPartBox {
        local_min: Vec3::splat(-half),
        local_max: Vec3::splat(half),
        world_position,
        world_rotation: Quat::IDENTITY,
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn part_sweep_near_real_portals_box_membership_is_subset_of_sphere_membership() {
    const CELLS: [u32; 10] = [
        0x8A02_016E,
        0x8A02_017A,
        0xA9B4_013F,
        0xA9B4_0150,
        0xA9B4_0159,
        0xA9B4_015A,
        0xA9B4_0161,
        0xA9B4_0162,
        0xA9B4_0164,
        0xA9B4_0166,
    ];
    let d = dats();
    let mut world = CollisionWorld::new();
    for id in CELLS {
        load_env_cell(&d, &mut world, id);
    }

    let mut state = 0x3335_5330u32;
    let mut next = move |lo: f32, hi: f32| {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        lo + (state >> 8) as f32 / (1u32 << 24) as f32 * (hi - lo)
    };

    let transit = |world: &CollisionWorld, cell_id: u32, pos: Vec3, radius: f32, half: f32| {
        let cell = world.cell(cell_id).unwrap();
        let sphere = Sphere::new(pos, radius);
        let mut by_sphere = CellArray::new();
        find_transit_cells_sphere(
            world,
            cell,
            cell_id,
            &[sphere],
            1,
            &mut CellCollector::new(&mut by_sphere),
        );
        let mut by_box = CellArray::new();
        find_transit_cells_box(
            world,
            cell,
            cell_id,
            &[part_box(half, pos)],
            &[sphere],
            &mut CellCollector::new(&mut by_box),
        );
        (by_sphere, by_box)
    };

    // A box smaller than its sphere: the box admit may only shrink the set.
    let (mut swept, mut removed) = (0, 0);
    for cell_id in CELLS {
        let cell = world.cell(cell_id).unwrap();
        for portal in &cell.portals {
            let Some(poly) = cell.portal_polygons.get(&portal.polygon_id) else {
                continue;
            };
            if poly.vertices.is_empty() {
                continue;
            }
            let anchor = poly.vertices.iter().copied().sum::<Vec3>() / poly.vertices.len() as f32;
            for iteration in 0..40 {
                let jitter = Vec3::new(next(-0.6, 0.6), next(-0.6, 0.6), next(-0.6, 0.6));
                let pos = cell.placement.to_world(anchor + jitter);
                let radius = next(0.3, 1.2);
                let half = next(0.02, 0.25);
                let (by_sphere, by_box) = transit(&world, cell_id, pos, radius, half);
                swept += 1;
                let added: Vec<u32> = by_box
                    .iter()
                    .filter(|id| !by_sphere.contains(*id))
                    .collect();
                assert!(
                    added.is_empty(),
                    "cell {cell_id:#010X} portal {:#06X} iteration {iteration}: box added {added:X?}",
                    portal.other_cell_id
                );
                removed += by_sphere.iter().filter(|id| !by_box.contains(*id)).count();
            }
        }
    }
    assert!(swept > 0, "no portals to sweep");
    assert!(
        removed > 0,
        "the box admit removed nothing across {swept} placements"
    );

    // A box larger than its sphere (the common production ratio): only that
    // the sweep runs.
    let mut production = 0;
    for cell_id in CELLS {
        let cell = world.cell(cell_id).unwrap();
        for portal in &cell.portals {
            let Some(poly) = cell.portal_polygons.get(&portal.polygon_id) else {
                continue;
            };
            if poly.vertices.is_empty() {
                continue;
            }
            let anchor = poly.vertices.iter().copied().sum::<Vec3>() / poly.vertices.len() as f32;
            for _ in 0..25 {
                let jitter = Vec3::new(next(-0.6, 0.6), next(-0.6, 0.6), next(-0.6, 0.6));
                let pos = cell.placement.to_world(anchor + jitter);
                let radius = next(0.2, 0.6);
                let half = next(radius, radius * 2.5);
                let _ = transit(&world, cell_id, pos, radius, half);
                production += 1;
            }
        }
    }
    assert!(production > 0);
}
