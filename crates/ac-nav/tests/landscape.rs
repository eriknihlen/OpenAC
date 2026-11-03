//! The landscape reads against the installed game files: a land and a sea
//! landblock's vertices, their heights as the physics ground has them, the
//! region's terrain palette, and how long a full request of landblocks
//! takes. These need the dats: set `OPENAC_DAT_DIR` or install them under
//! `Documents\Asheron's Call`; run with `cargo test -- --include-ignored`.

mod support;

use std::time::Instant;

use ac_dat::objects::{LandBlock, Region};
use ac_nav::landscape::{
    LandscapeTerrainCache, ROAD_TERRAIN_TYPE, read_landblock_terrain, read_terrain_palette,
};
use ac_physics::TerrainSurface;

/// The region record: terrain types, textures and the height table.
const REGION_ID: u32 = 0x1300_0000;

/// Holtburg's landblock, on dry land.
const HOLTBURG: u16 = 0xA9B4;

/// A landblock of open sea, far west of Holtburg: the physics ground has
/// every cell of it under water.
const OPEN_SEA: u16 = 0x01B4;

/// A landblock the files have no terrain for: the last column of the map
/// grid has none.
const NO_TERRAIN: u16 = 0xFF00;

/// The terrain types the physics ground counts as water.
fn is_water(terrain_type: u8) -> bool {
    (0x10..=0x14).contains(&terrain_type)
}

fn region(dats: &ac_dat::DatCollection) -> Region {
    dats.get::<Region>(REGION_ID).unwrap().expect("the region")
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_land_landblocks_vertices_stand_where_the_physics_ground_does() {
    let dats = support::dats();
    let table = region(&dats).land_defs.land_height_table;
    let terrain = read_landblock_terrain(&dats, &table, HOLTBURG)
        .unwrap()
        .expect("Holtburg's terrain");
    assert_eq!(terrain.landblock, HOLTBURG);
    assert_eq!(terrain.vertices.len(), 81);
    let block = dats
        .get::<LandBlock>(u32::from(HOLTBURG) << 16 | 0xFFFF)
        .unwrap()
        .unwrap();
    let ground = TerrainSurface::from_landblock(&block, &table);
    for x in 0..9 {
        for y in 0..9 {
            let vertex = terrain.vertices[x * 9 + y];
            let at = ground.sample_z(24.0 * x as f32, 24.0 * y as f32);
            // The ground is sampled just inside its east and north edges.
            let within = if x == 8 || y == 8 { 0.25 } else { 1e-4 };
            assert!(
                (vertex.height_meters - at).abs() <= within,
                "({x}, {y}): {} against {at}",
                vertex.height_meters
            );
            assert!(vertex.terrain_type < 32 && vertex.road < 4);
        }
    }
    assert!(terrain.vertices.iter().all(|v| !is_water(v.terrain_type)));
    assert!(
        terrain.vertices.iter().any(|v| v.road != 0),
        "Holtburg's roads"
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_sea_landblock_is_all_water_and_one_the_files_lack_has_none() {
    let dats = support::dats();
    let table = region(&dats).land_defs.land_height_table;
    let terrain = read_landblock_terrain(&dats, &table, OPEN_SEA)
        .unwrap()
        .expect("the sea's terrain");
    assert!(terrain.vertices.iter().all(|v| is_water(v.terrain_type)));
    let block = dats
        .get::<LandBlock>(u32::from(OPEN_SEA) << 16 | 0xFFFF)
        .unwrap()
        .unwrap();
    assert!(TerrainSurface::from_landblock(&block, &table).is_entirely_water());
    assert_eq!(
        read_landblock_terrain(&dats, &table, NO_TERRAIN).unwrap(),
        None
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_terrain_type_and_the_road_have_their_colours_and_names() {
    let dats = support::dats();
    let region = region(&dats);
    let started = Instant::now();
    let palette = read_terrain_palette(&dats, &region.terrain_info).unwrap();
    println!(
        "palette: {} types in {:.1} ms",
        palette.len(),
        started.elapsed().as_secs_f64() * 1000.0
    );
    // One entry a terrain type, then the road.
    let types = &region.terrain_info.terrain_types;
    assert_eq!(palette.len(), types.len() + 1);
    for (i, (colour, defined)) in palette.iter().zip(types).enumerate() {
        assert_eq!(usize::from(colour.terrain_type), i);
        assert_ne!(colour.rgb, 0, "{i} {:?}", colour.name);
        assert!(colour.rgb <= 0xFF_FFFF);
        let c = defined.terrain_color;
        let map = u32::from(c.red) << 16 | u32::from(c.green) << 8 | u32::from(c.blue);
        assert_eq!(colour.map_rgb, map, "{i}");
        assert_eq!(colour.name.as_deref(), Some(defined.terrain_name.as_str()));
        println!(
            "{i:2} texture {:06X} map {:06X} {:?}",
            colour.rgb, colour.map_rgb, colour.name
        );
    }
    // The dat's golden: the first type's map colour is stored 0xFF649664.
    assert_eq!(palette[0].map_rgb, 0x0064_9664);
    let road = palette.last().unwrap();
    println!("road texture {:06X}", road.rgb);
    assert_eq!(road.terrain_type, ROAD_TERRAIN_TYPE);
    assert_eq!(road.name.as_deref(), Some("Road"));
    assert_ne!(road.rgb, 0);
    assert_eq!(road.map_rgb, road.rgb);
}

/// A full request (256 landblocks, a 16 by 16 square around Holtburg) read
/// cold and then from the cache, timed.
#[test]
#[ignore = "needs the retail dat files"]
fn a_full_request_of_landblocks_takes_a_few_milliseconds() {
    let dats = support::dats();
    let table = region(&dats).land_defs.land_height_table;
    let square: Vec<u16> = (0xA0u16..0xB0)
        .flat_map(|x| (0xAAu16..0xBA).map(move |y| x << 8 | y))
        .collect();
    assert_eq!(square.len(), 256);
    let mut cache = LandscapeTerrainCache::new();
    let started = Instant::now();
    let read = square
        .iter()
        .filter(|&&lb| cache.terrain(&dats, &table, lb).unwrap().is_some())
        .count();
    let cold = started.elapsed();
    let started = Instant::now();
    for &lb in &square {
        let _ = cache.terrain(&dats, &table, lb).unwrap();
    }
    let warm = started.elapsed();
    println!(
        "{read} of 256 landblocks: cold {:.2} ms, cached {:.3} ms",
        cold.as_secs_f64() * 1000.0,
        warm.as_secs_f64() * 1000.0
    );
    assert_eq!(read, 256);
}
