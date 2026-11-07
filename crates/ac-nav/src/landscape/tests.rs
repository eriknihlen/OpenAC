//! The landscape reads over game files built in code.

use std::any::Any;
use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};

use ac_dat::objects::{
    LandSurf, PixelFormat, TerrainInfo, TerrainTex, TerrainType, TexMerge, TextureType,
    TmTerrainDesc,
};
use ac_dat::types::ColorArgb;
use ac_dat::{DatError, DatObject};

use super::*;

/// Dat objects by file id; counts every read.
#[derive(Default)]
struct FakeDats {
    objects: HashMap<u32, Arc<dyn Any + Send + Sync>>,
    reads: AtomicUsize,
}

impl FakeDats {
    fn add<T: Any + Send + Sync>(&mut self, id: u32, value: T) {
        self.objects.insert(id, Arc::new(value));
    }
}

impl DatSource for FakeDats {
    fn object<T: DatObject + Send + Sync + 'static>(
        &self,
        id: u32,
    ) -> Result<Option<Arc<T>>, DatError> {
        self.reads.fetch_add(1, Ordering::Relaxed);
        Ok(self
            .objects
            .get(&id)
            .and_then(|o| Arc::clone(o).downcast::<T>().ok()))
    }

    fn has_high_res(&self) -> bool {
        true
    }
}

/// A height table of 2 m a step.
fn table() -> [f32; 256] {
    std::array::from_fn(|i| i as f32 * 2.0)
}

/// Landblock 0xA9B4 whose vertex `i` has terrain type `i % 32`, road
/// `i % 4` and height index `i`.
fn landblock() -> LandBlock {
    LandBlock {
        id: 0xA9B4_FFFF,
        has_objects: false,
        terrain: std::array::from_fn(|i| TerrainInfo(((i as u16 % 32) << 2) | (i as u16 % 4))),
        height: std::array::from_fn(|i| i as u8),
    }
}

#[test]
fn a_landblocks_vertices_come_in_the_files_order_with_their_heights_in_metres() {
    let mut dats = FakeDats::default();
    dats.add(0xA9B4_FFFF, landblock());
    let terrain = read_landblock_terrain(&dats, &table(), 0xA9B4)
        .unwrap()
        .expect("the landblock's terrain");
    assert_eq!(terrain.landblock, 0xA9B4);
    let block = landblock();
    for x in 0..VERTICES_PER_SIDE {
        for y in 0..VERTICES_PER_SIDE {
            let i = x * VERTICES_PER_SIDE + y;
            let v = terrain.vertices[i];
            assert_eq!(
                v.terrain_type,
                block.terrain[i].terrain_type(),
                "({x}, {y})"
            );
            assert_eq!(v.road, block.terrain[i].road(), "({x}, {y})");
            assert_eq!(
                v.height_meters,
                TerrainSurface::vertex_height(&block.height, &table(), x, y)
            );
        }
    }
    // Vertex (1, 0), one step east of the south-west corner, is index 9.
    assert_eq!(terrain.vertices[9].height_meters, 18.0);
    assert_eq!(terrain.vertices[9].terrain_type, 9);
    assert_eq!(terrain.vertices[9].road, 1);
}

#[test]
fn a_landblock_without_a_terrain_file_has_none() {
    let dats = FakeDats::default();
    assert_eq!(
        read_landblock_terrain(&dats, &table(), 0x0102).unwrap(),
        None
    );
}

#[test]
fn the_cache_reads_each_landblock_once_and_drops_the_oldest() {
    let mut dats = FakeDats::default();
    dats.add(0xA9B4_FFFF, landblock());
    let mut cache = LandscapeTerrainCache::new();
    let first = cache.terrain(&dats, &table(), 0xA9B4).unwrap().unwrap();
    let again = cache.terrain(&dats, &table(), 0xA9B4).unwrap().unwrap();
    assert!(Arc::ptr_eq(&first, &again));
    assert!(cache.terrain(&dats, &table(), 0x0001).unwrap().is_none());
    assert!(cache.terrain(&dats, &table(), 0x0001).unwrap().is_none());
    assert_eq!(dats.reads.load(Ordering::Relaxed), 2);
    for landblock in 0..MAXIMUM_CACHED_TERRAIN_LANDBLOCKS as u16 {
        let _ = cache.terrain(&dats, &table(), 0x1000 + landblock).unwrap();
    }
    let reads = dats.reads.load(Ordering::Relaxed);
    let _ = cache.terrain(&dats, &table(), 0xA9B4).unwrap();
    assert_eq!(dats.reads.load(Ordering::Relaxed), reads + 1, "read again");
}

#[test]
fn the_mean_colour_is_each_channels_rounded_mean() {
    assert_eq!(mean_rgb(&[]), 0);
    assert_eq!(mean_rgb(&[10, 20, 30, 255, 20, 40, 50, 0]), 0x0F_1E_28);
    assert_eq!(mean_rgb(&[0, 0, 0, 255, 1, 1, 1, 255]), 0x01_01_01);
    assert_eq!(mean_rgb(&[255, 128, 0, 255]), 0xFF_80_00);
}

/// A terrain type with its map colour, `0xRRGGBB`.
fn terrain_type(name: &str, map_rgb: u32) -> TerrainType {
    let [_, red, green, blue] = map_rgb.to_be_bytes();
    TerrainType {
        terrain_name: name.to_owned(),
        terrain_color: ColorArgb {
            blue,
            green,
            red,
            alpha: 255,
        },
        scene_types: Vec::new(),
    }
}

fn desc(terrain_type: i32, texture_id: u32) -> TmTerrainDesc {
    TmTerrainDesc {
        terrain_type,
        terrain_tex: TerrainTex {
            texture_id,
            tex_tiling: 1,
            max_vert_bright: 0,
            min_vert_bright: 0,
            max_vert_saturate: 0,
            min_vert_saturate: 0,
            max_vert_hue: 0,
            min_vert_hue: 0,
            detail_tex_tiling: 0,
            detail_texture_id: 0,
        },
    }
}

/// A 2 x 1 image of 32-bit colour, stored blue, green, red, alpha.
fn image(dats: &mut FakeDats, texture_id: u32, render_id: u32, bgra: [u8; 8]) {
    dats.add(
        texture_id,
        SurfaceTexture {
            id: texture_id,
            data_category: 0,
            texture_type: TextureType::default(),
            textures: vec![render_id],
        },
    );
    dats.add(
        render_id,
        RenderSurface {
            id: render_id,
            data_category: 0,
            width: 2,
            height: 1,
            format: PixelFormat::A8R8G8B8,
            data: bgra.to_vec(),
            default_palette_id: 0,
        },
    );
}

#[test]
fn each_terrain_type_has_its_first_textures_mean_colour_and_its_name() {
    let mut dats = FakeDats::default();
    // Type 0: red and blue, the first listing; a second listing is not used.
    image(
        &mut dats,
        0x0500_0001,
        0x0600_0001,
        [0, 0, 200, 255, 100, 0, 0, 255],
    );
    image(
        &mut dats,
        0x0500_0002,
        0x0600_0002,
        [9, 9, 9, 255, 9, 9, 9, 255],
    );
    // Type 1: green.
    image(
        &mut dats,
        0x0500_0003,
        0x0600_0003,
        [0, 60, 0, 255, 0, 80, 0, 255],
    );
    let terrain = TerrainDesc {
        terrain_types: vec![
            terrain_type("BarrenRock", 0x64_9664),
            terrain_type("Grassland", 0x12_3456),
            terrain_type("", 0),
        ],
        land_surfaces: LandSurf {
            surf_type: 0,
            tex_merge: TexMerge {
                base_tex_size: 2,
                corner_terrain_maps: Vec::new(),
                side_terrain_maps: Vec::new(),
                road_maps: Vec::new(),
                terrain_desc: vec![
                    desc(0, 0x0500_0001),
                    desc(1, 0x0500_0003),
                    desc(0, 0x0500_0002),
                    // The road texture: its own entry, after the types.
                    desc(32, 0x0500_0002),
                ],
            },
        },
    };
    let palette = read_terrain_palette(&dats, &terrain).unwrap();
    assert_eq!(
        palette,
        [
            TerrainColor {
                terrain_type: 0,
                rgb: 0x0064_0032,
                map_rgb: 0x0064_9664,
                name: Some("BarrenRock".into()),
            },
            TerrainColor {
                terrain_type: 1,
                rgb: 0x0000_4600,
                map_rgb: 0x0012_3456,
                name: Some("Grassland".into()),
            },
            // No texture listed, and no name.
            TerrainColor {
                terrain_type: 2,
                rgb: 0,
                map_rgb: 0,
                name: None,
            },
            // The region gives the road no map colour: it repeats the
            // texture's.
            TerrainColor {
                terrain_type: ROAD_TERRAIN_TYPE,
                rgb: 0x0009_0909,
                map_rgb: 0x0009_0909,
                name: Some("Road".into()),
            },
        ]
    );
}

#[test]
fn without_a_road_texture_the_palette_has_no_road() {
    let terrain = TerrainDesc {
        terrain_types: vec![terrain_type("BarrenRock", 0x64_9664)],
        land_surfaces: LandSurf {
            surf_type: 0,
            tex_merge: TexMerge {
                base_tex_size: 2,
                corner_terrain_maps: Vec::new(),
                side_terrain_maps: Vec::new(),
                road_maps: Vec::new(),
                terrain_desc: Vec::new(),
            },
        },
    };
    let palette = read_terrain_palette(&FakeDats::default(), &terrain).unwrap();
    assert_eq!(
        palette,
        [TerrainColor {
            terrain_type: 0,
            rgb: 0,
            map_rgb: 0x0064_9664,
            name: Some("BarrenRock".into()),
        }]
    );
}

#[test]
fn a_texture_that_does_not_decode_gives_its_type_no_colour() {
    let mut dats = FakeDats::default();
    image(&mut dats, 0x0500_0001, 0x0600_0001, [0; 8]);
    // Three bytes for a 2 x 1 image of four bytes a texel.
    dats.add(
        0x0600_0001,
        RenderSurface {
            id: 0x0600_0001,
            data_category: 0,
            width: 2,
            height: 1,
            format: PixelFormat::A8R8G8B8,
            data: vec![1, 2, 3],
            default_palette_id: 0,
        },
    );
    let terrain = TerrainDesc {
        terrain_types: vec![terrain_type("BarrenRock", 0x64_9664)],
        land_surfaces: LandSurf {
            surf_type: 0,
            tex_merge: TexMerge {
                base_tex_size: 2,
                corner_terrain_maps: Vec::new(),
                side_terrain_maps: Vec::new(),
                road_maps: Vec::new(),
                terrain_desc: vec![desc(0, 0x0500_0001)],
            },
        },
    };
    let palette = read_terrain_palette(&dats, &terrain).unwrap();
    assert_eq!(palette.len(), 1);
    assert_eq!(palette[0].rgb, 0);
    assert_eq!(palette[0].map_rgb, 0x0064_9664);
}
