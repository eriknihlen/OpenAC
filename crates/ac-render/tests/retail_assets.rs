//! Renderer asset preparation against the retail dats. Ignored by default;
//! `scripts/check.*` runs them when the dats are present.

use std::path::PathBuf;

use ac_dat::objects::Region;
use ac_render::assets::{self, REGION_ID};
use ac_render::content::Content;
use ac_render::texture_detail::WorldTextureDetail;
use ac_render::world_frame::LandblockCoord;

fn dats() -> Content {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Content::open(&dir).expect("open the retail dats")
}

/// The landscape detail texture is the region's first terrain
/// description's (the building shells' is the second's, the interior
/// cells' the third's).
#[test]
#[ignore = "needs the retail dat files"]
fn the_landscape_detail_is_the_first_terrain_descriptions() {
    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let descs = &region.terrain_info.land_surfaces.tex_merge.terrain_desc;
    for (i, desc) in descs.iter().take(3).enumerate() {
        println!(
            "description {i}: detail {:#010x} x {}",
            desc.terrain_tex.detail_texture_id, desc.terrain_tex.detail_tex_tiling
        );
    }
    let atlas = assets::build_terrain_atlas(&d, &region, WorldTextureDetail::FULL);
    let landscape = atlas.landscape_detail.expect("a landscape detail texture");
    let first = &descs[assets::LANDSCAPE_DETAIL_DESCRIPTION].terrain_tex;
    assert_eq!(assets::LANDSCAPE_DETAIL_DESCRIPTION, 0);
    assert_ne!(first.detail_texture_id, 0);
    assert_eq!(landscape.tiling, first.detail_tex_tiling as f32);
    assert!(landscape.tiling > 0.0);
    assert!(landscape.image.width > 0 && !landscape.image.levels.is_empty());
    // The dats give the landscape its own image (description 0 names
    // 0x05001786, descriptions 1 and 2 both 0x05001787), so the loaded one
    // is not the building shells' or the interior cells'.
    assert_eq!(first.detail_texture_id, 0x0500_1786);
    assert_ne!(Some(&landscape), atlas.details[0].as_ref());
    assert_ne!(Some(&landscape), atlas.details[1].as_ref());
}

#[test]
#[ignore = "needs the retail dat files"]
fn terrain_atlas_has_every_layer_and_graded_alpha_maps() {
    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let atlas = assets::build_terrain_atlas(&d, &region, WorldTextureDetail::FULL);
    // Region dump: 4 corner maps, 1 side map, 3 road maps.
    assert_eq!(atlas.blending.corner_alphas.len(), 4);
    assert_eq!(atlas.blending.side_alphas.len(), 1);
    assert_eq!(atlas.blending.road_alphas.len(), 3);
    assert_eq!(atlas.alpha_layers.len(), 8);
    for (i, alphas) in atlas.alpha_layers.iter().enumerate() {
        let graded = alphas.iter().filter(|&&a| a != 0 && a != 255).count();
        assert!(
            graded > alphas.len() / 50,
            "alpha layer {i}: only {graded} of {} pixels between 0 and 255",
            alphas.len()
        );
    }
    let road = atlas.blending.road_layer;
    assert_ne!(road, ac_core::terrain::SurfaceInfo::NONE, "road texture");
    assert!(atlas.layers.len() <= ac_core::terrain::TERRAIN_LAYER_CAPACITY);
    let first = &atlas.layers[0];
    assert_eq!(
        first.levels.len() as u32,
        assets::mip_level_count(first.width, first.height)
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_landblock_meshes_and_places_its_statics() {
    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let atlas = assets::build_terrain_atlas(&d, &region, WorldTextureDetail::FULL);
    let block = assets::build_landblock(
        &d,
        LandblockCoord { x: 0xA9, y: 0xB4 },
        &region.land_defs.land_height_table,
        &atlas.blending,
        &mut std::collections::HashMap::new(),
    )
    .expect("Holtburg exists");
    assert_eq!(
        block.vertices.len(),
        ac_core::terrain::VERTICES_PER_LANDBLOCK
    );
    assert!(block.min_z <= block.max_z);
    let near = assets::build_near(&d, LandblockCoord { x: 0xA9, y: 0xB4 }, &region);
    assert!(!near.statics.is_empty());
    for s in &near.statics {
        let parts = assets::model_parts(&d, s.model_id).expect("static model exists");
        assert!(!parts.is_empty());
        for p in parts {
            let gfx = assets::build_gfx_obj(&d, p.gfx_obj_id).expect("part exists");
            for sub in &gfx.sub_meshes {
                let surface = assets::build_surface(&d, sub.surface_id);
                let (w, h, level0) = surface.texture.levels()[0];
                assert!(surface.texture.block_format().is_none());
                assert_eq!(level0.len() as u32, w * h * 4);
            }
        }
    }
}

/// Holtburg's object textures that stay block compressed keep the dat's
/// own blocks as level 0 and get a full chain of whole-block levels; every
/// level decodes like the dat's texture.
#[test]
#[ignore = "needs the retail dat files"]
fn block_compressed_textures_keep_their_blocks_and_get_a_full_mip_chain() {
    use ac_dat::objects::{RenderSurface, Surface, SurfaceBase, SurfaceTexture, SurfaceType};
    use ac_render::block_compression::{self as bc, BcFormat};
    use ac_render::surface_image::{self, SurfaceImage};

    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let near = assets::build_near(&d, LandblockCoord { x: 0xA9, y: 0xB4 }, &region);
    let mut surfaces = std::collections::BTreeSet::new();
    for s in &near.statics {
        for p in assets::model_parts(&d, s.model_id).unwrap() {
            let gfx = assets::build_gfx_obj(&d, p.gfx_obj_id).unwrap();
            surfaces.extend(gfx.sub_meshes.iter().map(|sub| sub.surface_id));
        }
    }
    let (mut kept, mut decoded_dxt) = (std::collections::BTreeMap::new(), 0);
    for id in surfaces {
        let data = assets::build_surface_with(&d, id, id, None, None, true);
        let surface = d.dats.get::<Surface>(id).unwrap().unwrap();
        let SurfaceBase::Texture { texture, .. } = surface.base else {
            assert!(data.texture.block_format().is_none());
            continue;
        };
        let rs = d
            .dats
            .get::<SurfaceTexture>(texture)
            .unwrap()
            .and_then(|st| {
                d.dats
                    .get::<RenderSurface>(d.dats.texture_source(&st)?)
                    .unwrap()
            });
        let Some(rs) = rs else { continue };
        let Some(format) = data.texture.block_format() else {
            if BcFormat::from_pixel_format(rs.format).is_some() {
                // Decoded because the surface changes its pixels.
                assert!(
                    surface.surface_type.contains(SurfaceType::BASE1_CLIP_MAP)
                        || surface.translucency > 0.0,
                    "surface {id:#x} is a plain DXT texture but was decoded"
                );
                decoded_dxt += 1;
            }
            continue;
        };
        *kept.entry(format).or_insert(0) += 1;
        assert_eq!(BcFormat::from_pixel_format(rs.format), Some(format));
        assert_eq!(
            (data.texture.width(), data.texture.height()),
            (rs.width, rs.height)
        );
        let size = bc::level_size_bytes(format, rs.width, rs.height);
        assert_eq!(
            data.texture.levels()[0].2,
            &rs.data[..size],
            "the dat's blocks"
        );

        let image = surface_image::prepare(data.texture, &WorldTextureDetail::FULL, false).unwrap();
        let SurfaceImage::Compressed { levels, .. } = &image else {
            panic!("{id:#x} stays compressed");
        };
        assert_eq!(
            levels.len() as u32,
            bc::full_mip_level_count(rs.width, rs.height)
        );
        for (level, (w, h, bytes)) in image.levels().into_iter().enumerate() {
            assert_eq!((w, h), bc::level_extent(rs.width, rs.height, level as u32));
            assert_eq!(
                bytes.len(),
                bc::level_size_bytes(format, w, h),
                "level {level}"
            );
        }
        assert_eq!(image.levels().last().map(|l| (l.0, l.1)), Some((1, 1)));

        // Level 0 decodes as the dat's texture does (the two decoders round
        // the in-between colours differently by at most one).
        let ours = bc::decode_level(format, levels[0].as_slice(), rs.width, rs.height).unwrap();
        let dat = ac_dat::texture::decode(&rs, None, Default::default()).unwrap();
        assert!(
            ours.iter().zip(&dat.rgba).all(|(a, b)| a.abs_diff(*b) <= 1),
            "{id:#x} decodes differently"
        );
    }
    assert!(
        kept.get(&BcFormat::Bc1).copied().unwrap_or(0) > 10,
        "Holtburg's statics keep DXT1 textures compressed: {kept:?} ({decoded_dxt} decoded)"
    );
}

/// The landblocks around Holtburg (a town, farmland, woods, a river).
fn around_holtburg() -> impl Iterator<Item = LandblockCoord> {
    (0xA6..=0xAC).flat_map(|x| (0xB1..=0xB7).map(move |y| LandblockCoord { x, y }))
}

#[test]
#[ignore = "needs the retail dat files"]
fn scenery_is_deterministic_and_keeps_its_placement_rules() {
    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let mut total = 0;
    for coord in around_holtburg() {
        let Some(block) = d
            .dats
            .get::<ac_dat::objects::LandBlock>(coord.terrain_id())
            .unwrap()
        else {
            continue;
        };
        let near = assets::build_near(&d, coord, &region);
        let again = assets::build_near(&d, coord, &region);
        assert_eq!(
            near.scenery.iter().map(|s| s.transform).collect::<Vec<_>>(),
            again
                .scenery
                .iter()
                .map(|s| s.transform)
                .collect::<Vec<_>>()
        );
        let ground =
            ac_physics::TerrainSurface::from_landblock(&block, &region.land_defs.land_height_table);
        for s in &near.scenery {
            let p = s.transform.w_axis.truncate();
            assert!((0.0..192.0).contains(&p.x) && (0.0..192.0).contains(&p.y));
            assert!(
                !ac_render::scenery::on_road(&block, p.x, p.y),
                "{p} on a road"
            );
            // Standing on the ground.
            assert!((ground.sample_z(p.x, p.y) - p.z).abs() < 1e-3);
            // Upright: only the heading turns it.
            let (_, rotation, _) = s.transform.to_scale_rotation_translation();
            assert!((rotation * glam::Vec3::Z).z > 0.99);
            assert!(matches!(s.model_id >> 24, 0x01 | 0x02));
        }
        total += near.scenery.len();
    }
    // Holtburg's surroundings are wooded.
    assert!(total > 1000, "{total} scenery objects in 49 landblocks");
}

#[test]
#[ignore = "needs the retail dat files"]
fn interiors_and_dungeons_build_their_cells() {
    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    // Holtburg: 12 buildings, 123 interior cells.
    let holtburg = assets::build_near(&d, LandblockCoord { x: 0xA9, y: 0xB4 }, &region);
    assert_eq!(holtburg.buildings.len(), 12);
    assert_eq!(holtburg.cells.len(), 123);
    assert!(holtburg.cells.iter().all(|c| c.seen_outside));
    assert!(holtburg.cells.iter().all(|c| c.physics.is_some()));
    for b in &holtburg.buildings {
        assert!(!b.walk.portals.is_empty(), "a building without doorways");
        assert!(b.trees.contains_key(&b.part0.0), "no doorway tree");
    }
    // The Town Network: 205 sealed cells, all but one with a room mesh.
    let network = assets::build_near(&d, LandblockCoord { x: 0x00, y: 0x07 }, &region);
    assert_eq!(network.cells.len(), 205);
    assert_eq!(
        network.cells.iter().filter(|c| c.mesh.is_some()).count(),
        204
    );
    assert!(network.cells.iter().all(|c| !c.seen_outside));
    for cell in network.cells.iter().filter_map(|c| c.mesh.as_ref()) {
        assert!(cell.sub_meshes.iter().all(|s| s.surface_id >> 24 == 0x08));
        assert_eq!(
            cell.sub_meshes.iter().map(|s| s.index_count).sum::<u32>() as usize,
            cell.indices.len()
        );
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_holtburg_room_floods_through_its_doorway() {
    use ac_render::walk::{FrameWalk, ViewSet, WalkCamera, WalkSink};
    use glam::Vec3;

    #[derive(Default)]
    struct Cells(Vec<Vec<u32>>);
    impl WalkSink for Cells {
        fn landscape_begin(&mut self, _: &ViewSet, _: usize, _: bool) {}
        fn land_block(&mut self, _: usize) {}
        fn land_cell_terrain(&mut self, _: usize, _: usize) {}
        fn land_cell(&mut self, _: usize, _: usize) {}
        fn sort_cell_exit(&mut self, _: usize, _: usize) {}
        fn building_drawable(&mut self, _: usize) -> bool {
            true
        }
        fn punch(&mut self, _: &[Vec3], _: usize) {}
        fn draw_cells(&mut self, cells: &[(u32, ViewSet, usize)]) {
            self.0.push(cells.iter().map(|c| c.0).collect());
        }
        fn building_shell(&mut self, _: usize) {}
        fn interior_landscape_done(&mut self, _: &[(Vec<Vec3>, ViewSet, usize)]) {}
    }

    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let near = assets::build_near(&d, LandblockCoord { x: 0xA9, y: 0xB4 }, &region);
    let mut walk = FrameWalk::new();
    for cell in &near.cells {
        walk.insert_cell(cell.walk.clone());
    }
    // The weapon shop's ground floor (cell 0100), looking west toward its
    // inner doorway and stair, from the camera of a live screenshot.
    let eye = Vec3::new(91.4, 131.5, 68.0);
    let view = glam::camera::rh::view::look_at_mat4(eye, eye + Vec3::NEG_X, Vec3::Z);
    let proj = glam::camera::rh::proj::directx::perspective(1.2, 16.0 / 9.0, 0.1, 5000.0);
    let vp = proj * view;
    let camera = WalkCamera {
        view_proj: vp,
        inverse_view_proj: vp.inverse(),
        eye,
        forward: Vec3::NEG_X,
        width: 1280.0,
        height: 720.0,
    };
    let mut sink = Cells::default();
    walk.walk_frame(0xA9B4_0100, &camera, &[], &[], &mut sink);
    let drawn = sink.0.concat();
    assert_eq!(
        drawn.last(),
        Some(&0xA9B4_0100),
        "the viewer's room is drawn last"
    );
    assert!(drawn.len() > 1, "nothing beyond the room: {drawn:X?}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_window_icon_is_a_32_pixel_image() {
    let d = dats();
    let icon = assets::window_icon(&d).expect("the icon is in the portal dat");
    assert_eq!((icon.width, icon.height), (32, 32));
    assert_eq!(icon.rgba.len(), 32 * 32 * 4);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_second_atlas_build_decodes_from_the_shared_caches() {
    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let first = assets::build_terrain_atlas(&d, &region, WorldTextureDetail::FULL);
    let before = d.textures.stats();
    let second = assets::build_terrain_atlas(&d, &region, WorldTextureDetail::FULL);
    let after = d.textures.stats();
    assert_eq!(first.alpha_layers, second.alpha_layers);
    assert_eq!(after.misses, before.misses, "nothing decoded twice");
    assert!(after.hits > before.hits);
}

/// FNV-1a over a run of byte slices, each length-prefixed.
fn fnv1a<'a>(parts: impl IntoIterator<Item = &'a [u8]>) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325u64;
    for part in parts {
        for b in (part.len() as u64).to_le_bytes().iter().chain(part) {
            hash = (hash ^ u64::from(*b)).wrapping_mul(0x0000_0100_0000_01b3);
        }
    }
    hash
}

/// The terrain atlas is built on several threads; its pixels are the same
/// bytes the one-thread build made. Golden: the hash of the atlas built by
/// the one-thread `build_terrain_atlas` before the layers went parallel,
/// full detail, when every layer was RGBA: the BGRA terrain layers are
/// hashed with red and blue swapped back, the one-byte alpha layers with
/// the weight in all four channels (what the terrain shader samples).
#[test]
#[ignore = "needs the retail dat files"]
fn terrain_atlas_pixels_match_the_one_thread_build() {
    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let atlas = assets::build_terrain_atlas(&d, &region, WorldTextureDetail::FULL);
    let rgba = |bgra: &[u8]| -> Vec<u8> {
        bgra.chunks_exact(4)
            .flat_map(|t| [t[2], t[1], t[0], t[3]])
            .collect()
    };
    let widened = |weights: &[u8]| -> Vec<u8> { weights.iter().flat_map(|&w| [w; 4]).collect() };
    let layers: Vec<Vec<u8>> = atlas
        .layers
        .iter()
        .flat_map(|chain| chain.levels.iter().map(|l| rgba(l)))
        .chain(
            atlas
                .details
                .iter()
                .flatten()
                .flat_map(|d| d.image.levels.iter().cloned()),
        )
        .chain(atlas.alpha_layers.iter().map(|l| widened(l)))
        .collect();
    let hash = fnv1a(layers.iter().map(Vec::as_slice));
    println!("terrain atlas hash {hash:#018x}");
    assert_eq!(hash, ATLAS_GOLDEN);
}

const ATLAS_GOLDEN: u64 = 0xb19b_bcd7_056c_880f;

/// The terrain array takes each terrain image's texels in BGRA order (a
/// 32-bit colour image as the dats store it), and the alpha array one byte
/// per texel; both are exactly the four-channel decodes they replace: the
/// layer with red and blue swapped, every channel of the "additive" alpha
/// decode the weight.
#[test]
#[ignore = "needs the retail dat files"]
fn terrain_layers_are_bgra_and_alpha_maps_one_byte_of_the_decode() {
    use ac_dat::objects::{Palette, RenderSurface, SurfaceTexture};
    use ac_dat::texture::{DecodeOptions, DecodedTexture};

    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let atlas = assets::build_terrain_atlas(&d, &region, WorldTextureDetail::FULL);
    let decode = |surface_texture: u32, additive: bool| -> DecodedTexture {
        let st = d
            .dats
            .get::<SurfaceTexture>(surface_texture)
            .unwrap()
            .unwrap();
        let rs = d
            .dats
            .get::<RenderSurface>(d.dats.texture_source(&st).unwrap())
            .unwrap()
            .unwrap();
        let palette = (rs.default_palette_id != 0).then(|| {
            d.dats
                .get::<Palette>(rs.default_palette_id)
                .unwrap()
                .unwrap()
        });
        let options = DecodeOptions {
            clip_map: false,
            additive,
        };
        ac_dat::texture::decode(&rs, palette.as_deref(), options).unwrap()
    };
    let tex_merge = &region.terrain_info.land_surfaces.tex_merge;
    let mut seen = std::collections::HashSet::new();
    let mut layer = 0;
    for desc in &tex_merge.terrain_desc {
        if !seen.insert(desc.terrain_type) {
            continue;
        }
        let expected = decode(desc.terrain_tex.texture_id, false);
        let chain = &atlas.layers[layer];
        assert_eq!(
            (chain.width, chain.height),
            (expected.width, expected.height)
        );
        let bgra = &chain.levels[0];
        assert_eq!(bgra.len(), expected.rgba.len());
        for (i, (b, e)) in bgra
            .chunks_exact(4)
            .zip(expected.rgba.chunks_exact(4))
            .enumerate()
        {
            assert_eq!(
                b,
                [e[2], e[1], e[0], e[3]],
                "terrain layer {layer} texel {i}"
            );
        }
        layer += 1;
    }
    assert!(layer > 20, "only {layer} terrain layers");

    let alpha_ids: Vec<u32> = tex_merge
        .corner_terrain_maps
        .iter()
        .chain(&tex_merge.side_terrain_maps)
        .map(|m| m.texture_id)
        .chain(tex_merge.road_maps.iter().map(|m| m.texture_id))
        .collect();
    assert_eq!(atlas.alpha_layers.len(), alpha_ids.len());
    for (layer, (&id, weights)) in alpha_ids.iter().zip(&atlas.alpha_layers).enumerate() {
        let expected = decode(id, true);
        assert_eq!(
            (atlas.alpha_width, atlas.alpha_height),
            (expected.width, expected.height)
        );
        assert_eq!(
            weights.len() * 4,
            expected.rgba.len(),
            "alpha layer {layer}"
        );
        for (i, (&w, e)) in weights
            .iter()
            .zip(expected.rgba.chunks_exact(4))
            .enumerate()
        {
            assert_eq!([w; 4], e, "alpha layer {layer} texel {i}");
        }
    }
}

/// What the loader reads for an area: the atlas, terrain within `far`,
/// near content within `near` and every model, mesh and surface it places.
fn load_area(d: &Content, center: LandblockCoord, near: u32, far: u32) {
    use std::collections::HashSet;
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let atlas = assets::build_terrain_atlas(d, &region, WorldTextureDetail::FULL);
    let heights = region.land_defs.land_height_table;
    let mut surface_cache = std::collections::HashMap::new();
    for coord in ac_render::world_frame::landblocks_around(center, far) {
        assets::build_landblock(d, coord, &heights, &atlas.blending, &mut surface_cache);
    }
    let (mut gfx_seen, mut surfaces_seen) = (HashSet::new(), HashSet::new());
    for coord in ac_render::world_frame::landblocks_around(center, near) {
        let data = assets::build_near(d, coord, &region);
        let mut gfx: Vec<u32> = Vec::new();
        for id in data.model_ids() {
            for part in assets::model_parts(d, id).into_iter().flatten() {
                gfx.push(part.gfx_obj_id);
            }
        }
        let mut surfaces: Vec<u32> = data
            .cells
            .iter()
            .flat_map(|c| c.mesh.iter().flat_map(|m| &m.sub_meshes))
            .map(|s| s.surface_id)
            .collect();
        while let Some(id) = gfx.pop() {
            if !gfx_seen.insert(id) {
                continue;
            }
            if let Some(mesh) = assets::build_gfx_obj(d, id) {
                surfaces.extend(mesh.sub_meshes.iter().map(|s| s.surface_id));
                gfx.extend(
                    mesh.degrades
                        .iter()
                        .map(|l| l.gfx_obj_id)
                        .filter(|&l| l != 0),
                );
            }
        }
        for id in surfaces {
            if surfaces_seen.insert(id) {
                let _ = assets::build_surface(d, id);
            }
        }
    }
}

/// The object caches are sized for the streaming area: one Holtburg load
/// (near 4, far 12, the default area) evicts nothing (OpenAC's 256 objects
/// per dat evicted about 8,000), so loading it again reads no object from
/// the dats.
#[test]
#[ignore = "needs the retail dat files"]
fn one_default_area_load_fits_the_object_caches() {
    let d = dats();
    let holtburg = LandblockCoord::of_cell(0xA9B4_0019);
    load_area(&d, holtburg, 4, 12);
    let first = d.dats.stats();
    assert_eq!(first.evictions, 0, "{first:?}");
    let held: usize = d.dats.occupancy().iter().map(|(_, n, _)| n).sum();
    assert!(held > 3000, "{held}");
    load_area(&d, holtburg, 4, 12);
    let second = d.dats.stats();
    assert_eq!(second.evictions, 0, "{second:?}");
    // Nothing new is cached the second time: every object read is a hit
    // (a miss is only a lookup in a dat that does not hold the id).
    let held_again: usize = d.dats.occupancy().iter().map(|(_, n, _)| n).sum();
    assert_eq!(held_again, held);
}

/// Why objects draw without their ladder (OpenAC draws them so): every
/// ladder of the models placed around Holtburg's lifestone ends in an empty
/// level, so walking it made most trees and fences vanish 50 to 200 m away
/// (measured 2026-10-13: 162 of 181 part models have a ladder, all 162 end
/// empty, most switch to a cheaper level 50 to 75 m away).
#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_ladders_end_in_an_empty_level() {
    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let mut seen = std::collections::HashSet::new();
    let (mut with_ladder, mut ending_empty) = (0, 0);
    for (x, y) in [
        (0xA9, 0xB4),
        (0xA8, 0xB4),
        (0xA8, 0xB5),
        (0xA9, 0xB5),
        (0xAA, 0xB4),
    ] {
        let near = assets::build_near(&d, LandblockCoord { x, y }, &region);
        for id in near.model_ids() {
            for part in assets::model_parts(&d, id).into_iter().flatten() {
                if !seen.insert(part.gfx_obj_id) {
                    continue;
                }
                let Some(gfx) = assets::build_gfx_obj(&d, part.gfx_obj_id) else {
                    continue;
                };
                if gfx.degrades.is_empty() {
                    continue;
                }
                with_ladder += 1;
                if gfx.degrades.last().is_some_and(|l| l.gfx_obj_id == 0) {
                    ending_empty += 1;
                }
            }
        }
    }
    assert_eq!((seen.len(), with_ladder, ending_empty), (181, 162, 162));
}

/// A humanoid server object in the world draws each part's closest model
/// (OpenAC `DatLiveEntityProjectionMaterializer` with `RetailCloseDegrades`);
/// a figure in a viewport keeps the setup's own parts. Golden ids: the human
/// male setup's dat ladders.
#[test]
#[ignore = "needs the retail dat files"]
fn a_humanoid_in_the_world_draws_its_close_models() {
    use ac_render::appearance::AppearanceKey;
    let d = dats();
    let model = ac_proto::ModelData {
        anim_part_changes: vec![ac_proto::AnimPartChange {
            part_index: 1,
            new_model_id: 0x0100_0055,
        }],
        ..ac_proto::ModelData::default()
    };
    let ids = |key: &AppearanceKey| -> Vec<u32> {
        assets::appearance_parts(&d, key)
            .unwrap()
            .iter()
            .map(|p| p.part.gfx_obj_id)
            .collect()
    };
    let world = ids(&AppearanceKey::in_world(0x0200_0001, &model));
    let viewport = ids(&AppearanceKey::new(0x0200_0001, &model));
    assert_eq!(world.len(), 34);
    assert_eq!(&viewport[..3], &[0x0100_004E, 0x0100_0055, 0x0100_004D]);
    // Part 1's swap is resolved after the swap: 0x01000055's level 0.
    assert_eq!(&world[..3], &[0x0100_1787, 0x0100_1795, 0x0100_178D]);
    // A part whose level 0 is itself, and the null parts, stay.
    assert_eq!(world[16], 0x0100_005A);
    assert!(world[17..].iter().all(|&id| id == 0x0100_01EC));
}

/// Counts the building shells a frame walk draws.
#[derive(Default)]
struct ShellCount(Vec<usize>);

impl ac_render::walk::WalkSink for ShellCount {
    fn landscape_begin(&mut self, _: &ac_render::walk::ViewSet, _: usize, _: bool) {}
    fn land_block(&mut self, _: usize) {}
    fn land_cell_terrain(&mut self, _: usize, _: usize) {}
    fn land_cell(&mut self, _: usize, _: usize) {}
    fn sort_cell_exit(&mut self, _: usize, _: usize) {}
    fn building_drawable(&mut self, _: usize) -> bool {
        true
    }
    fn punch(&mut self, _: &[glam::Vec3], _: usize) {}
    fn draw_cells(&mut self, _: &[(u32, ac_render::walk::ViewSet, usize)]) {}
    fn building_shell(&mut self, building: usize) {
        self.0.push(building);
    }
    fn interior_landscape_done(
        &mut self,
        _: &[(Vec<glam::Vec3>, ac_render::walk::ViewSet, usize)],
    ) {
    }
}

/// OpenAC draws buildings out to the object range: seen from high
/// above Holtburg with the near tier five rings out (the High preset's four
/// and a ring kept while the player walks), every building of the town's
/// dat landblocks within four rings is drawn once and none five rings out.
/// Golden counts: the buildings of the retail dats in each ring around
/// 0xA9B4.
#[test]
#[ignore = "needs the retail dat files"]
fn holtburg_draws_its_buildings_out_to_the_object_range() {
    use ac_render::walk::{BuildingRef, FrameWalk, WalkBlock, WalkCamera};
    use glam::Vec3;

    let d = dats();
    let region = d.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    let center = LandblockCoord { x: 0xA9, y: 0xB4 };
    let near: Vec<(LandblockCoord, assets::NearData)> =
        ac_render::world_frame::landblocks_around(center, 5)
            .into_iter()
            .map(|c| (c, assets::build_near(&d, c, &region)))
            .collect();
    let ring_of = |c: LandblockCoord| {
        (i32::from(c.x) - i32::from(center.x))
            .abs()
            .max((i32::from(c.y) - i32::from(center.y)).abs()) as usize
    };
    let mut blocks = Vec::new();
    let mut refs = Vec::new();
    let mut ring_of_building = Vec::new();
    for (coord, data) in &near {
        let dx = f32::from(coord.x) - f32::from(center.x);
        let dy = f32::from(coord.y) - f32::from(center.y);
        let mut by_cell = Vec::new();
        for b in &data.buildings {
            by_cell.push((b.land_cell, refs.len()));
            refs.push(BuildingRef {
                building: &b.walk,
                tree: None,
            });
            ring_of_building.push(ring_of(*coord));
        }
        blocks.push(WalkBlock {
            coord: (coord.x, coord.y),
            origin: Vec3::new(dx * 192.0, dy * 192.0, 0.0),
            min_z: 0.0,
            max_z: 200.0,
            near: true,
            buildings: by_cell,
        });
    }
    let mut per_ring = [0usize; 6];
    for &r in &ring_of_building {
        per_ring[r] += 1;
    }

    // Straight down from 2 km over the town's centre: every block is in view.
    let eye = Vec3::new(96.0, 96.0, 2000.0);
    let target = Vec3::new(96.0, 96.0, 0.0);
    let vp =
        glam::camera::rh::proj::directx::perspective(std::f32::consts::FRAC_PI_2, 1.0, 0.1, 5000.0)
            * glam::camera::rh::view::look_at_mat4(eye, target, Vec3::Y);
    let camera = WalkCamera {
        view_proj: vp,
        inverse_view_proj: vp.inverse(),
        eye,
        forward: (target - eye).normalize(),
        width: 1024.0,
        height: 1024.0,
    };
    let mut walk = FrameWalk::new();
    walk.object_ring_limit = 4;
    let mut drawn = ShellCount::default();
    walk.walk_frame(0xA9B4_0019, &camera, &blocks, &refs, &mut drawn);
    let mut drawn_per_ring = [0usize; 6];
    for &b in &drawn.0 {
        drawn_per_ring[ring_of_building[b]] += 1;
    }
    assert_eq!(drawn.0.len(), drawn_per_ring.iter().sum::<usize>());
    let mut once = drawn.0.clone();
    once.sort_unstable();
    once.dedup();
    assert_eq!(once.len(), drawn.0.len(), "each shell once");
    assert_eq!(
        &drawn_per_ring[..5],
        &per_ring[..5],
        "rings 0 to 4 all drawn"
    );
    assert_eq!(drawn_per_ring[5], 0, "ring 5 kept loaded but not drawn");
    assert_eq!(per_ring, [12, 4, 7, 2, 15, 6]);
}

/// Without `client_highres.dat` a surface whose texture keeps its
/// full-detail level only in that dat is drawn from its reduced level (the
/// portal dat's), not as the missing-texture magenta; with it, from the
/// full one. The first 64 such surfaces, both ways.
#[test]
#[ignore = "needs the retail dat files"]
fn surfaces_draw_from_the_texture_level_the_dats_hold() {
    use ac_dat::objects::{RenderSurface, Surface, SurfaceBase, SurfaceTexture};
    let full = dats();
    let raw = full.dats.uncached();
    let high_res = raw
        .high_res
        .as_ref()
        .expect("the install has client_highres.dat");
    let in_high_res_only = |id: u32| {
        high_res.find_entry(id).unwrap().is_some() && raw.portal.find_entry(id).unwrap().is_none()
    };
    let mut cases = Vec::new();
    for id in raw.portal.entries().map(|e| e.unwrap().id) {
        if id >> 24 != 0x08 {
            continue;
        }
        let surface = full.dats.get::<Surface>(id).unwrap().unwrap();
        let SurfaceBase::Texture { texture, .. } = surface.base else {
            continue;
        };
        let Some(st) = full.dats.get::<SurfaceTexture>(texture).unwrap() else {
            continue;
        };
        if let [hi, lo] = st.textures[..]
            && in_high_res_only(hi)
        {
            cases.push((id, hi, lo));
        }
        if cases.len() == 64 {
            break;
        }
    }
    assert_eq!(cases.len(), 64);

    let dir = std::env::temp_dir().join(format!("ac-render-no-high-res-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    for name in [ac_dat::PORTAL_DAT, ac_dat::CELL_DAT, ac_dat::LOCAL_DAT] {
        std::fs::hard_link(full.dats.directory().join(name), dir.join(name))
            .expect("link the dat into the test folder (same volume)");
    }
    let reduced = Content::open(&dir).expect("open without client_highres.dat");
    for (content, pick) in [(&full, 0usize), (&reduced, 1)] {
        for &(id, hi, lo) in &cases {
            let level = [hi, lo][pick];
            let rs = full.dats.get::<RenderSurface>(level).unwrap().unwrap();
            let built = assets::build_surface(content, id);
            assert_eq!(
                (built.texture.width(), built.texture.height()),
                (rs.width, rs.height),
                "surface {id:08X} level {pick} {level:08X}"
            );
        }
    }
    drop(reduced);
    std::fs::remove_dir_all(dir).ok();
}
