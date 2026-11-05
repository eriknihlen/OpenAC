//! The collision queries against the retail dat files.
//!
//! OpenAC's dat-backed conformance for these pieces compares its two BSP
//! representations over installed dats (`BoxIntersectsCellBspDifferentialTests`
//! `InstalledDat_*`, `FlatBspQueryDifferentialTests` `InstalledDat_*`); here
//! there is one representation, so the sweeps check what must hold for every
//! retail tree instead: every tree prepares, every walkable polygon is found
//! by a sphere resting on it, and the containment queries nest (a point
//! inside implies the sphere around it reaches in, which implies its box
//! does). These tests need the dats: set `OPENAC_DAT_DIR` or install them
//! under `Documents\Asheron's Call`; run with `cargo test -- --include-ignored`.

use std::collections::HashSet;
use std::path::PathBuf;

use ac_dat::DatCollection;
use ac_dat::objects::{EnvCell, Environment, GfxObj, LandBlock, Region};
use ac_physics::globals::FLOOR_Z;
use ac_physics::{
    CellBsp, PhysicsBsp, PhysicsBspNode, ResolvedPolygon, Sphere, TerrainSurface, Vec3, WalkState,
};

fn dats() -> DatCollection {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatCollection::open(dir).expect("open the retail dats")
}

fn ids_in(dat: &ac_dat::DatFile, lo: u32, hi: u32) -> Vec<u32> {
    dat.entries()
        .map(|e| e.unwrap().id)
        .filter(|id| (lo..=hi).contains(id))
        .collect()
}

/// A sphere resting on the polygon's front, 0.3 m above its centroid, must
/// touch some polygon of the tree; when the polygon is walkable, lowering
/// the sphere must land it on walkable ground.
fn probe_polygon(bsp: &PhysicsBsp, poly: &ResolvedPolygon) -> (bool, Option<bool>) {
    let centroid = poly.vertices.iter().copied().sum::<Vec3>() / poly.vertices.len() as f32;
    let center = centroid + poly.plane.normal * 0.3;
    let touched = bsp.sphere_intersects_poly(center, 0.48).is_some();
    let walkable = (poly.plane.normal.z >= FLOOR_Z).then(|| {
        let mut walk = WalkState::default();
        bsp.find_walkable_sphere(&mut walk, Sphere::new(center, 0.48), 0.5, Vec3::Z)
            .is_some()
    });
    (touched, walkable)
}

/// The polygons worth probing: those some leaf lists (a few physics
/// polygons, such as the underside of model 0x01002DCA's base, are in no
/// leaf, so no walk can find them) and not so small that the probe could
/// miss them.
fn probe_worthy(bsp: &PhysicsBsp) -> Vec<&ResolvedPolygon> {
    let listed: HashSet<u16> = bsp
        .nodes()
        .iter()
        .flat_map(|n| match n {
            PhysicsBspNode::Leaf { polygons, .. } => polygons.clone(),
            PhysicsBspNode::Split { .. } => Vec::new(),
        })
        .collect();
    bsp.polygons()
        .values()
        .filter(|p| listed.contains(&p.id))
        .filter(|p| {
            let centroid = p.vertices.iter().copied().sum::<Vec3>() / p.vertices.len() as f32;
            p.vertices.iter().all(|v| v.distance(centroid) > 0.05)
        })
        .collect()
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_cell_structure_prepares_and_its_floors_are_found() {
    let d = dats();
    let (mut structs, mut probed, mut walkable) = (0, 0, 0);
    for id in ids_in(&d.portal, 0x0D00_0000, 0x0D00_FFFF) {
        let env: Environment = d.get(id).unwrap().unwrap();
        for (index, cell) in &env.cells {
            let bsp = PhysicsBsp::from_cell_struct(cell)
                .unwrap_or_else(|e| panic!("environment {id:#010x} cell {index}: {e}"));
            CellBsp::from_dat(&cell.cell_bsp).unwrap();
            structs += 1;
            for poly in probe_worthy(&bsp) {
                let (touched, on_floor) = probe_polygon(&bsp, poly);
                assert!(
                    touched,
                    "environment {id:#010x} cell {index} polygon {}",
                    poly.id
                );
                if let Some(found) = on_floor {
                    assert!(
                        found,
                        "environment {id:#010x} cell {index} floor {}",
                        poly.id
                    );
                    walkable += 1;
                }
                probed += 1;
            }
        }
    }
    println!("cell structures {structs}, polygons probed {probed}, floors {walkable}");
    assert!(structs > 1000 && walkable > 1000);
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_model_physics_tree_prepares_and_is_touched_where_its_polygons_are() {
    let d = dats();
    let (mut trees, mut probed) = (0, 0);
    for id in ids_in(&d.portal, 0x0100_0000, 0x01FF_FFFF) {
        let gfx: GfxObj = d.get(id).unwrap().unwrap();
        let Some(tree) = &gfx.physics_bsp else {
            continue;
        };
        let bsp = PhysicsBsp::from_dat(tree, &gfx.physics_polygons, &gfx.vertices)
            .unwrap_or_else(|e| panic!("model {id:#010x}: {e}"));
        trees += 1;
        for poly in probe_worthy(&bsp) {
            let (touched, on_floor) = probe_polygon(&bsp, poly);
            assert!(touched, "model {id:#010x} polygon {}", poly.id);
            if let Some(found) = on_floor {
                assert!(found, "model {id:#010x} floor {}", poly.id);
            }
            probed += 1;
        }
    }
    println!("model trees {trees}, polygons probed {probed}");
    assert!(trees > 1000);
}

/// The cells OpenAC's installed-dat box sweep uses.
const SWEEP_CELLS: [u32; 10] = [
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

#[test]
#[ignore = "needs the retail dat files"]
fn cell_containment_queries_nest() {
    let d = dats();
    let mut state = 0x4230_5820u32;
    let mut next = move || {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        (state >> 8) as f32 / (1u32 << 24) as f32
    };
    let mut inside = 0;
    for cell_id in SWEEP_CELLS {
        let cell: EnvCell = d.get(cell_id).unwrap().unwrap();
        let env: Environment = d.get(cell.environment_file_id()).unwrap().unwrap();
        let structure = env.cell(u32::from(cell.cell_structure)).unwrap();
        let tree = CellBsp::from_dat(&structure.cell_bsp).unwrap();
        let anchors: Vec<Vec3> = structure
            .vertex_array
            .vertices
            .iter()
            .map(|(_, v)| ac_physics::dat::vec3(v.origin))
            .collect();
        for i in 0..2000 {
            let anchor = anchors[(next() * anchors.len() as f32) as usize % anchors.len()];
            let center = anchor + Vec3::new(next() - 0.5, next() - 0.5, next() - 0.5) * 3.0;
            let half = match i % 9 {
                0 => ac_physics::BOX_PLANE_EPSILON,
                1 => 0.01,
                2 => 0.5,
                _ => 0.05 + next() * 2.45,
            };
            let point = tree.point_inside(center);
            let sphere = tree.sphere_intersects(center, half);
            // The sphere test allows 1 cm beyond the radius; the box that
            // holds that larger sphere must reach in whenever it does.
            let reach = half + 0.01;
            let boxed =
                tree.box_intersects(center - Vec3::splat(reach), center + Vec3::splat(reach));
            assert!(
                !point || sphere,
                "cell {cell_id:#010x}: point inside but sphere not"
            );
            assert!(
                !sphere || boxed,
                "cell {cell_id:#010x}: sphere in but box not"
            );
            inside += usize::from(point);
        }
    }
    assert!(inside > 1000, "{inside}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_ground_matches_the_height_table_at_every_corner() {
    let d = dats();
    let region: Region = d.get(0x1300_0000).unwrap().unwrap();
    let table = &region.land_defs.land_height_table;
    let block: LandBlock = d.get(0xA9B4_FFFF).unwrap().unwrap();
    let surface = TerrainSurface::from_landblock(&block, table);
    // Each cell's own bottom-left corner is exact (the far edges are only
    // reached as other cells' corners).
    for x in 0..8usize {
        for y in 0..8usize {
            let expected = table[usize::from(block.height[x * 9 + y])];
            let (lx, ly) = (x as f32 * 24.0, y as f32 * 24.0);
            let z = surface.sample_z(lx, ly);
            assert_eq!(z, expected, "corner ({x}, {y})");
            let poly = surface.sample_surface_polygon(lx + 3.0, ly + 7.0);
            assert!((poly.z - surface.sample_z(lx + 3.0, ly + 7.0)).abs() < 1e-4);
            assert!(poly.normal.z > 0.0);
            assert!(
                poly.vertices
                    .iter()
                    .any(|v| v.x == lx && v.y == ly && v.z == expected)
            );
        }
    }
    assert_eq!(surface.block_water_type(), 0, "Holtburg is dry land");
}
