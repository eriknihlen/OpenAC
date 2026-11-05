//! CPU-side preparation of everything the renderer draws, read from the
//! dats: the terrain texture arrays and blend tables, landblock terrain
//! meshes and static objects, model parts, GfxObj meshes and surface
//! textures. Runs on the loader thread; the window only uploads results.

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::terrain::{
    AlphaMap, LandblockTerrain, SurfaceInfo, TERRAIN_LAYER_CAPACITY, TerrainBlendingContext,
    build_landblock_mesh, build_tiling_table,
};
use ac_core::world::{StaticBuilding, StaticObject, place_landblock_statics};
use ac_dat::cache::DecodedTextureKey;
use ac_dat::objects::{
    GfxObj, LandBlock, LandBlockInfo, Palette, PixelFormat, Region, RenderSurface, Setup, Surface,
    SurfaceBase, SurfaceTexture, SurfaceType, TexMerge,
};
use ac_dat::texture::{self, DecodeOptions, DecodedTexture};
use ac_dat::types::Frame;
use bytemuck::{Pod, Zeroable};
use glam::{Mat4, Vec3};

use ac_core::lighting::LightInfoSource;
use ac_core::sky::{
    DayGroupSource, LoadedSkyDesc, SkyDatLookup, SkyDescSource, SkyObjectReplaceSource,
    SkyObjectSource, SkyTimeSource, load_sky_desc,
};

use crate::appearance::{self, AppearanceKey, PaletteSpec};
use crate::content::Content;
use crate::meshing::{
    self, Cull, MeshRef, SubMesh, Translucency, flatten_setup, is_untextured, opacity_of, quat,
    translucency_of, vec3,
};
use crate::surface_image::{self, SurfaceImage};
use crate::world_frame::LandblockCoord;

/// The retail region (terrain textures, height table, sky).
pub const REGION_ID: u32 = 0x1300_0000;

/// A four-byte-per-texel image with its mip chain (level 0 first): RGBA,
/// or BGRA for the terrain layers.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MipChain {
    /// Width of level 0.
    pub width: u32,
    /// Height of level 0.
    pub height: u32,
    /// Pixels per level, each half the size of the one before.
    pub levels: Vec<Vec<u8>>,
}

/// The terrain texture arrays and the tables the terrain shader and mesh
/// builder need.
#[derive(Debug, Clone)]
pub struct TerrainAtlasData {
    /// One layer per terrain texture, all the same size, with mips; BGRA,
    /// the dats' own order for 32-bit colour, so those images are taken as
    /// stored.
    pub layers: Vec<MipChain>,
    /// Alpha blend maps (corner, side, road), all the same size, no mips.
    pub alpha_width: u32,
    /// Height of every alpha layer.
    pub alpha_height: u32,
    /// One byte per texel per alpha layer: the blend weight.
    pub alpha_layers: Vec<Vec<u8>>,
    /// Repeats per 24 m cell, by texture layer.
    pub tiling: [f32; TERRAIN_LAYER_CAPACITY],
    /// Layer lookups for the blend recipe builder.
    pub blending: TerrainBlendingContext,
    /// The detail textures laid over building shells (index 0) and
    /// interior cells (index 1): the region's second and third terrain
    /// descriptions' detail textures, when they have one.
    pub details: [Option<DetailTexture>; 2],
    /// The detail texture laid over the outdoor terrain: the region's
    /// first terrain description's detail texture, when it has one.
    pub landscape_detail: Option<DetailTexture>,
}

/// Which of the region's terrain descriptions carries each detail
/// texture: the first the landscape's, the second the building shells',
/// the third the interior cells'.
pub const LANDSCAPE_DETAIL_DESCRIPTION: usize = 0;
/// See [`LANDSCAPE_DETAIL_DESCRIPTION`].
pub const BUILDING_DETAIL_DESCRIPTION: usize = 1;
/// See [`LANDSCAPE_DETAIL_DESCRIPTION`].
pub const ENVIRONMENT_DETAIL_DESCRIPTION: usize = 2;

/// A detail texture and how often it repeats per texture unit.
#[derive(Debug, Clone, PartialEq)]
pub struct DetailTexture {
    /// The image with its mips.
    pub image: MipChain,
    /// Repeats per surface texture repeat (0: off).
    pub tiling: f32,
}

/// The detail texture of terrain description `desc`: its surface
/// texture's first image with the default palette, with mips.
fn detail_texture(
    content: &Content,
    desc: &ac_dat::objects::TmTerrainDesc,
) -> Option<DetailTexture> {
    let id = desc.terrain_tex.detail_texture_id;
    if id == 0 {
        return None;
    }
    let (decoded, _) = decode_surface_texture(content, id, DecodeOptions::default())?;
    Some(DetailTexture {
        image: mip_chain(decoded.rgba.clone(), decoded.width, decoded.height),
        tiling: desc.terrain_tex.detail_tex_tiling as f32,
    })
}

/// A missing or broken texture: 1 x 1 magenta.
fn magenta() -> Arc<DecodedTexture> {
    Arc::new(DecodedTexture {
        width: 1,
        height: 1,
        rgba: vec![0xFF, 0x00, 0xFF, 0xFF],
    })
}

/// Decode the first (full-detail) render surface of a surface texture with
/// its default palette.
fn decode_surface_texture(
    content: &Content,
    surface_texture_id: u32,
    options: DecodeOptions,
) -> Option<(Arc<DecodedTexture>, bool)> {
    let st = content
        .dats
        .get::<SurfaceTexture>(surface_texture_id)
        .ok()??;
    decode_render_surface(content, *st.textures.first()?, options)
}

/// The image in the window's title bar and task bar: the retail minimap
/// window's title-bar icon (a 32 x 32 map scroll).
pub const WINDOW_ICON_ID: u32 = 0x0600_1065;

/// The window icon, decoded. `None` when the dats lack it.
#[must_use]
pub fn window_icon(content: &Content) -> Option<Arc<DecodedTexture>> {
    decode_render_surface(content, WINDOW_ICON_ID, DecodeOptions::default()).map(|(t, _)| t)
}

/// Decode a render surface with its default palette through the shared
/// texture cache; also says whether it is paletted.
fn decode_render_surface(
    content: &Content,
    render_surface_id: u32,
    options: DecodeOptions,
) -> Option<(Arc<DecodedTexture>, bool)> {
    let dats = &content.dats;
    let rs = dats.get::<RenderSurface>(render_surface_id).ok()??;
    let paletted = matches!(rs.format, PixelFormat::P8 | PixelFormat::INDEX16);
    let key = DecodedTextureKey::new(rs.id, rs.format, options);
    let (decoded, _) = content
        .textures
        .get_or_create(key, || {
            let palette = if rs.default_palette_id != 0 {
                dats.get::<Palette>(rs.default_palette_id).ok().flatten()
            } else {
                None
            };
            texture::decode(&rs, palette.as_deref(), options)
        })
        .ok()?;
    Some((decoded, paletted))
}

/// Build the terrain texture arrays from the region: one layer per terrain
/// type (first listing wins), resampled to the largest, in BGRA order; the
/// alpha maps one byte per texel, the blend weight (the value an
/// "additive" decode puts in every channel).
#[must_use]
pub fn build_terrain_atlas(
    content: &Content,
    region: &Region,
    detail: crate::texture_detail::WorldTextureDetail,
) -> TerrainAtlasData {
    let tex_merge = &region.terrain_info.land_surfaces.tex_merge;

    // Terrain layers in first-listed order.
    let mut descs: Vec<&ac_dat::objects::TmTerrainDesc> = Vec::new();
    for desc in &tex_merge.terrain_desc {
        if !descs.iter().any(|d| d.terrain_type == desc.terrain_type) {
            descs.push(desc);
        }
    }
    descs.truncate(TERRAIN_LAYER_CAPACITY);
    // The layers are independent: decode them, then resample them, on
    // several threads (the result is the same in the same order).
    let decoded = parallel_map(&descs, |desc| {
        terrain_layer_texels(content, desc.terrain_tex.texture_id)
            .unwrap_or_else(|| LayerTexels::bgra(&magenta()))
    });
    // The layers at the landscape texture detail (alpha maps keep theirs).
    let max_w = detail.landscape_size(decoded.iter().map(|d| d.width).max().unwrap_or(1));
    let max_h = detail.landscape_size(decoded.iter().map(|d| d.height).max().unwrap_or(1));

    let layers = parallel_map_owned(decoded, |texels| {
        mip_chain(resample(texels, max_w, max_h), max_w, max_h)
    });
    let mut blending = TerrainBlendingContext::empty();
    let mut tiling_entries = Vec::new();
    for (layer, desc) in descs.iter().enumerate() {
        let (terrain_type, tiling) = (&desc.terrain_type, &desc.terrain_tex.tex_tiling);
        let layer = layer as u8;
        if let Some(slot) = usize::try_from(*terrain_type)
            .ok()
            .and_then(|t| blending.terrain_type_to_layer.get_mut(t))
        {
            *slot = layer;
        }
        if *terrain_type == ac_dat::objects::TmTerrainDesc::ROAD_TERRAIN_TYPE {
            blending.road_layer = layer;
        }
        tiling_entries.push((u32::from(layer), *tiling));
    }
    let tiling = build_tiling_table(tiling_entries).unwrap_or([1.0; TERRAIN_LAYER_CAPACITY]);

    let (alpha_layers, alpha_width, alpha_height) =
        decode_alpha_maps(content, tex_merge, &mut blending);
    let mut details = parallel_map(
        &[
            BUILDING_DETAIL_DESCRIPTION,
            ENVIRONMENT_DETAIL_DESCRIPTION,
            LANDSCAPE_DETAIL_DESCRIPTION,
        ],
        |&i| {
            tex_merge
                .terrain_desc
                .get(i)
                .and_then(|d| detail_texture(content, d))
        },
    )
    .into_iter();

    TerrainAtlasData {
        layers,
        alpha_width,
        alpha_height,
        alpha_layers,
        tiling,
        blending,
        details: [details.next().flatten(), details.next().flatten()],
        landscape_detail: details.next().flatten(),
    }
}

/// `f` of every item, taken by value, in the items' order, computed on
/// several threads as [`parallel_map`] does.
fn parallel_map_owned<T: Send, R: Send>(items: Vec<T>, f: impl Fn(T) -> R + Sync) -> Vec<R> {
    let slots: Vec<std::sync::Mutex<Option<T>>> = items
        .into_iter()
        .map(|item| std::sync::Mutex::new(Some(item)))
        .collect();
    parallel_map(&slots, |slot| {
        let item = slot
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .take()
            .expect("each item is taken once");
        f(item)
    })
}

/// One terrain source image as the arrays take it: four bytes per texel
/// (BGRA) for a terrain layer, one (the blend weight) for an alpha map.
#[derive(Debug, Clone, PartialEq, Eq)]
struct LayerTexels {
    width: u32,
    height: u32,
    texels: Vec<u8>,
}

impl LayerTexels {
    /// A decoded RGBA image in BGRA order.
    fn bgra(decoded: &DecodedTexture) -> Self {
        let mut texels = decoded.rgba.clone();
        for texel in texels.chunks_exact_mut(4) {
            texel.swap(0, 2);
        }
        Self {
            width: decoded.width,
            height: decoded.height,
            texels,
        }
    }

    /// The image's stored bytes, `bytes_per_texel` a texel, when the dats
    /// hold the whole image.
    fn stored(rs: &RenderSurface, bytes_per_texel: usize) -> Option<Self> {
        let len = (rs.width as usize)
            .checked_mul(rs.height as usize)?
            .checked_mul(bytes_per_texel)?;
        if len == 0 {
            return None;
        }
        Some(Self {
            width: rs.width,
            height: rs.height,
            texels: rs.data.get(..len)?.to_vec(),
        })
    }
}

/// The first (full-detail) render surface of a surface texture.
fn first_render_surface(content: &Content, surface_texture_id: u32) -> Option<Arc<RenderSurface>> {
    let st = content
        .dats
        .get::<SurfaceTexture>(surface_texture_id)
        .ok()??;
    content
        .dats
        .get::<RenderSurface>(*st.textures.first()?)
        .ok()?
}

/// A terrain layer's BGRA texels: a 32-bit colour image is already BGRA
/// and is taken as stored; any other is decoded with its default palette.
fn terrain_layer_texels(content: &Content, surface_texture_id: u32) -> Option<LayerTexels> {
    let rs = first_render_surface(content, surface_texture_id)?;
    if rs.format == PixelFormat::A8R8G8B8
        && let Some(stored) = LayerTexels::stored(&rs, 4)
    {
        return Some(stored);
    }
    decode_render_surface(content, rs.id, DecodeOptions::default())
        .map(|(decoded, _)| LayerTexels::bgra(&decoded))
}

/// An alpha map's blend weights, one byte per texel: a one-byte alpha
/// image is taken as stored (its "additive" decode puts that byte in every
/// channel); any other gives its decoded alpha, the channel the terrain
/// shader takes the weight from.
fn alpha_map_texels(content: &Content, surface_texture_id: u32) -> Option<LayerTexels> {
    let rs = first_render_surface(content, surface_texture_id)?;
    if matches!(
        rs.format,
        PixelFormat::A8 | PixelFormat::CUSTOM_LSCAPE_ALPHA
    ) && let Some(stored) = LayerTexels::stored(&rs, 1)
    {
        return Some(stored);
    }
    let additive = DecodeOptions {
        clip_map: false,
        additive: true,
    };
    let (decoded, _) = decode_render_surface(content, rs.id, additive)?;
    Some(LayerTexels {
        width: decoded.width,
        height: decoded.height,
        texels: decoded.rgba.iter().skip(3).step_by(4).copied().collect(),
    })
}

/// `f` of every item, in the items' order, computed on up to as many
/// threads as the machine runs at once (scoped: they end before this
/// returns).
fn parallel_map<T: Sync, R: Send>(items: &[T], f: impl Fn(&T) -> R + Sync) -> Vec<R> {
    let threads = std::thread::available_parallelism()
        .map_or(1, std::num::NonZeroUsize::get)
        .min(items.len());
    if threads <= 1 {
        return items.iter().map(f).collect();
    }
    let next = std::sync::atomic::AtomicUsize::new(0);
    let mut done: Vec<(usize, R)> = std::thread::scope(|scope| {
        let workers: Vec<_> = (0..threads)
            .map(|_| {
                scope.spawn(|| {
                    let mut out = Vec::new();
                    loop {
                        let i = next.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                        let Some(item) = items.get(i) else {
                            return out;
                        };
                        out.push((i, f(item)));
                    }
                })
            })
            .collect();
        workers
            .into_iter()
            .flat_map(|w| match w.join() {
                Ok(out) => out,
                Err(panic) => std::panic::resume_unwind(panic),
            })
            .collect()
    });
    done.sort_unstable_by_key(|(i, _)| *i);
    done.into_iter().map(|(_, r)| r).collect()
}

fn decode_alpha_maps(
    content: &Content,
    tex_merge: &TexMerge,
    blending: &mut TerrainBlendingContext,
) -> (Vec<Vec<u8>>, u32, u32) {
    // Every map's image, read on several threads, then listed in order
    // (a map whose image cannot be read takes no layer).
    let maps: Vec<(u32, u32, usize)> = tex_merge
        .corner_terrain_maps
        .iter()
        .map(|m| (m.texture_id, m.t_code, 0))
        .chain(
            tex_merge
                .side_terrain_maps
                .iter()
                .map(|m| (m.texture_id, m.t_code, 1)),
        )
        .chain(
            tex_merge
                .road_maps
                .iter()
                .map(|m| (m.texture_id, m.r_code, 2)),
        )
        .collect();
    let images = parallel_map(&maps, |&(texture_id, _, _)| {
        alpha_map_texels(content, texture_id)
    });
    let mut decoded: Vec<LayerTexels> = Vec::new();
    for (&(_, code, list), image) in maps.iter().zip(images) {
        let Some(d) = image else {
            continue;
        };
        let list = match list {
            0 => &mut blending.corner_alphas,
            1 => &mut blending.side_alphas,
            _ => &mut blending.road_alphas,
        };
        list.push(AlphaMap {
            layer: decoded.len() as u8,
            code,
        });
        decoded.push(d);
    }
    if decoded.is_empty() {
        return (vec![vec![0xFF]], 1, 1);
    }
    let w = decoded.iter().map(|d| d.width).max().unwrap_or(1);
    let h = decoded.iter().map(|d| d.height).max().unwrap_or(1);
    let layers = decoded
        .into_iter()
        .map(|d| resize_nearest(d, 1, w, h))
        .collect();
    (layers, w, h)
}

/// Box-average a four-byte image when it only shrinks; nearest neighbour
/// otherwise.
fn resample(src: LayerTexels, w: u32, h: u32) -> Vec<u8> {
    let grows = w > src.width || h > src.height;
    let shrinks = w < src.width || h < src.height;
    if !grows && shrinks {
        downsample_box(&src.texels, src.width, src.height, w, h)
    } else {
        resize_nearest(src, 4, w, h)
    }
}

/// Nearest-neighbour resize of `bytes_per_texel` texels; an image already
/// that size is returned as it is.
fn resize_nearest(src: LayerTexels, bytes_per_texel: usize, w: u32, h: u32) -> Vec<u8> {
    if src.width == w && src.height == h {
        return src.texels;
    }
    let n = bytes_per_texel;
    let mut out = vec![0u8; w as usize * h as usize * n];
    for y in 0..h {
        let sy = (y as u64 * u64::from(src.height) / u64::from(h)) as u32;
        for x in 0..w {
            let sx = (x as u64 * u64::from(src.width) / u64::from(w)) as u32;
            let si = (sy * src.width + sx) as usize * n;
            let di = (y * w + x) as usize * n;
            out[di..di + n].copy_from_slice(&src.texels[si..si + n]);
        }
    }
    out
}

/// Average the source pixels covering each destination pixel.
fn downsample_box(src: &[u8], sw: u32, sh: u32, w: u32, h: u32) -> Vec<u8> {
    let mut out = vec![0u8; (w * h * 4) as usize];
    for y in 0..h {
        let y0 = y * sh / h;
        let y1 = ((y + 1) * sh / h).max(y0 + 1);
        for x in 0..w {
            let x0 = x * sw / w;
            let x1 = ((x + 1) * sw / w).max(x0 + 1);
            let mut sum = [0u32; 4];
            for sy in y0..y1 {
                for sx in x0..x1 {
                    let i = ((sy * sw + sx) * 4) as usize;
                    for c in 0..4 {
                        sum[c] += u32::from(src[i + c]);
                    }
                }
            }
            let n = (y1 - y0) * (x1 - x0);
            let di = ((y * w + x) * 4) as usize;
            for c in 0..4 {
                out[di + c] = ((sum[c] + n / 2) / n) as u8;
            }
        }
    }
    out
}

/// Number of mip levels down to 1 x 1.
#[must_use]
pub fn mip_level_count(w: u32, h: u32) -> u32 {
    32 - w.max(h).max(1).leading_zeros()
}

/// A full mip chain by repeated 2 x 2 box filtering.
#[must_use]
pub fn mip_chain(level0: Vec<u8>, width: u32, height: u32) -> MipChain {
    let mut levels = vec![level0];
    let (mut w, mut h) = (width, height);
    for _ in 1..mip_level_count(width, height) {
        let (nw, nh) = ((w / 2).max(1), (h / 2).max(1));
        let next = downsample_box(levels.last().expect("level 0 exists"), w, h, nw, nh);
        levels.push(next);
        (w, h) = (nw, nh);
    }
    MipChain {
        width,
        height,
        levels,
    }
}

/// One terrain vertex as the GPU reads it: 40 bytes.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Pod, Zeroable)]
pub struct TerrainGpuVertex {
    /// Landblock-local position, meters.
    pub position: [f32; 3],
    /// Unit normal.
    pub normal: [f32; 3],
    /// The cell's packed blend recipe and split (four words).
    pub data: [u32; 4],
}

/// One placed static object (or building shell) of a landblock.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StaticInstance {
    /// Model id (`0x01` GfxObj or `0x02` Setup).
    pub model_id: u32,
    /// Landblock-local placement (scale, rotation, translation).
    pub transform: Mat4,
    /// True for a building's outside shell.
    pub is_building_shell: bool,
}

/// A landblock's terrain.
#[derive(Debug, Clone)]
pub struct LandblockData {
    /// Which landblock.
    pub coord: LandblockCoord,
    /// Terrain mesh (six vertices per cell), landblock-local.
    pub vertices: Vec<TerrainGpuVertex>,
    /// Lowest terrain height, meters.
    pub min_z: f32,
    /// Highest terrain height, meters.
    pub max_z: f32,
    /// The ground as physics samples it (the height under a point).
    pub ground: Box<ac_physics::TerrainSurface>,
}

/// What a landblock holds besides its terrain: everything the near tier
/// draws.
#[derive(Debug, Clone, Default)]
pub struct NearData {
    /// Static objects then building shells.
    pub statics: Vec<StaticInstance>,
    /// Procedural scenery.
    pub scenery: Vec<StaticInstance>,
    /// Buildings with their doorways (same order as the shells).
    pub buildings: Vec<crate::cells::BuildingData>,
    /// Interior cells.
    pub cells: Vec<crate::cells::CellData>,
    /// Interior statics whose cells could not be worked out (drawn in no
    /// cell), one line each.
    pub membership_failures: Vec<String>,
}

impl NearData {
    /// Every model id it places.
    pub fn model_ids(&self) -> impl Iterator<Item = u32> + '_ {
        self.statics
            .iter()
            .chain(&self.scenery)
            .map(|s| s.model_id)
            .chain(
                self.cells
                    .iter()
                    .flat_map(|c| c.statics.iter().map(|s| s.model_id)),
            )
    }
}

/// The near-tier content of a landblock: statics, scenery, buildings and
/// interior cells. Empty when the landblock has no info record.
#[must_use]
pub fn build_near(content: &Content, coord: LandblockCoord, region: &Region) -> NearData {
    let Some(block) = content
        .dats
        .get::<LandBlock>(coord.terrain_id())
        .ok()
        .flatten()
    else {
        return NearData::default();
    };
    let info = if block.has_objects {
        content
            .dats
            .get::<LandBlockInfo>(coord.info_id())
            .ok()
            .flatten()
    } else {
        None
    };
    let building_origins: Vec<Vec3> = info
        .iter()
        .flat_map(|i| i.buildings.iter().map(|b| vec3(b.frame.origin)))
        .collect();
    let scenery =
        crate::scenery::generate(region, &block, &building_origins, &SceneryContent(content))
            .into_iter()
            .map(|s| StaticInstance {
                model_id: s.object_id,
                transform: Mat4::from_scale_rotation_translation(
                    Vec3::splat(s.scale),
                    s.rotation,
                    s.origin,
                ),
                is_building_shell: false,
            })
            .collect();
    let Some(info) = info else {
        return NearData {
            scenery,
            ..NearData::default()
        };
    };
    let buildings = crate::cells::build_buildings(content, coord, &info);
    let mut cells = crate::cells::build_cells(content, coord, &info);
    let membership_failures =
        crate::membership::assign_interior_membership(content, coord, &mut cells, &buildings);
    NearData {
        statics: place_statics(coord, &info),
        scenery,
        buildings,
        cells,
        membership_failures,
    }
}

/// The dat reads scenery placement needs.
struct SceneryContent<'a>(&'a Content);

impl crate::scenery::SceneryDats for SceneryContent<'_> {
    fn scene(&self, id: u32) -> Option<Arc<ac_dat::objects::Scene>> {
        self.0.dats.get::<ac_dat::objects::Scene>(id).ok().flatten()
    }

    fn shape(&self, object_id: u32) -> Option<crate::scenery::ObjectShape> {
        object_shape(self.0, object_id)
    }
}

/// The shape scenery placement keeps inside its landblock: a setup's
/// sorting sphere, cylinders and spheres, or for a lone GfxObj the root
/// sphere of its collision tree (else of its drawing tree).
#[must_use]
pub fn object_shape(content: &Content, object_id: u32) -> Option<crate::scenery::ObjectShape> {
    crate::scenery::object_shape(
        &|id| content.dats.get::<GfxObj>(id).ok().flatten(),
        &|id| content.dats.get::<Setup>(id).ok().flatten(),
        object_id,
    )
}

/// Read and mesh one landblock's terrain. `None` when the cell dat has no
/// such landblock.
pub fn build_landblock(
    content: &Content,
    coord: LandblockCoord,
    height_table: &[f32; 256],
    blending: &TerrainBlendingContext,
    surface_cache: &mut HashMap<u32, SurfaceInfo>,
) -> Option<LandblockData> {
    let block = content.dats.get::<LandBlock>(coord.terrain_id()).ok()??;
    let terrain: [u16; 81] = block.terrain.map(|t| t.0);
    let mesh = build_landblock_mesh(
        &LandblockTerrain {
            landblock_x: u32::from(coord.x),
            landblock_y: u32::from(coord.y),
            terrain: &terrain,
            heights: &block.height,
            height_table,
        },
        blending,
        surface_cache,
    );
    let (mut min_z, mut max_z) = (f32::MAX, f32::MIN);
    let vertices = mesh
        .vertices
        .iter()
        .map(|v| {
            min_z = min_z.min(v.position[2]);
            max_z = max_z.max(v.position[2]);
            TerrainGpuVertex {
                position: v.position,
                normal: v.normal,
                data: v.data,
            }
        })
        .collect();

    let ground = Box::new(ac_physics::TerrainSurface::new(
        &block.height,
        height_table,
        u32::from(coord.x),
        u32::from(coord.y),
        None,
    ));
    Some(LandblockData {
        coord,
        vertices,
        min_z,
        max_z,
        ground,
    })
}

fn static_object(model_id: u32, frame: &Frame) -> StaticObject {
    let o = frame.origin;
    let q = frame.orientation;
    StaticObject {
        model_id,
        origin: [o.x, o.y, o.z],
        orientation: [q.w, q.x, q.y, q.z],
    }
}

fn place_statics(coord: LandblockCoord, info: &LandBlockInfo) -> Vec<StaticInstance> {
    let objects: Vec<StaticObject> = info
        .objects
        .iter()
        .map(|s| static_object(s.id, &s.frame))
        .collect();
    let portal_cells: Vec<Vec<u16>> = info
        .buildings
        .iter()
        .map(|b| b.portals.iter().map(|p| p.other_cell_id).collect())
        .collect();
    let buildings: Vec<StaticBuilding<'_>> = info
        .buildings
        .iter()
        .zip(&portal_cells)
        .map(|(b, cells)| StaticBuilding {
            shell: static_object(b.model_id, &b.frame),
            portal_other_cell_ids: cells,
        })
        .collect();
    let Ok(placements) = place_landblock_statics(coord.terrain_id(), &objects, &buildings) else {
        return Vec::new();
    };
    placements
        .iter()
        .map(|p| {
            let [w, x, y, z] = p.orientation;
            StaticInstance {
                model_id: p.model_id,
                transform: Mat4::from_rotation_translation(
                    glam::Quat::from_xyzw(x, y, z, w),
                    Vec3::from(p.origin),
                ),
                is_building_shell: p.is_building_shell,
            }
        })
        .collect()
}

/// The parts of a model: a GfxObj is one part at the object's origin; a
/// Setup is its parts in their resting pose. `None` for an unknown id.
pub fn model_parts(content: &Content, model_id: u32) -> Option<Vec<MeshRef>> {
    match model_id >> 24 {
        0x01 => Some(vec![MeshRef {
            gfx_obj_id: model_id,
            transform: Mat4::IDENTITY,
            variant: 0,
        }]),
        0x02 => {
            let setup = content.dats.get::<Setup>(model_id).ok()??;
            Some(flatten_setup(&setup, None))
        }
        _ => None,
    }
}

/// One object vertex as the GPU reads it: 32 bytes.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Pod, Zeroable)]
pub struct MeshGpuVertex {
    /// Model-local position, meters.
    pub position: [f32; 3],
    /// Unit normal.
    pub normal: [f32; 3],
    /// Texture coordinate.
    pub uv: [f32; 2],
}

/// One drawable piece of a GfxObj: a range of its index buffer and the
/// surface it is painted with.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SubMeshRange {
    /// Surface dat id.
    pub surface_id: u32,
    /// First index.
    pub first_index: u32,
    /// Index count.
    pub index_count: u32,
    /// Added to every index.
    pub base_vertex: i32,
    /// The texture must repeat (some coordinate leaves 0..1).
    pub repeat: bool,
    /// Faces the draw culls.
    pub cull: Cull,
}

/// A GfxObj ready to upload: all sub-meshes in one vertex and index buffer.
#[derive(Debug, Clone)]
pub struct GfxObjData {
    /// GfxObj id.
    pub id: u32,
    /// Vertices of every sub-mesh, one after the other.
    pub vertices: Vec<MeshGpuVertex>,
    /// Indices of every sub-mesh, one after the other (each sub-mesh's own
    /// numbering; see [`SubMeshRange::base_vertex`]).
    pub indices: Vec<u32>,
    /// The pieces.
    pub sub_meshes: Vec<SubMeshRange>,
    /// Its level-of-detail ladder (empty without one): a part made of this
    /// GfxObj draws the chosen level's model instead.
    pub degrades: Vec<crate::degrade::DegradeLevel>,
    /// The point its distance from the viewer is measured to, model-local.
    pub sort_center: Vec3,
    /// The sphere a view tests the part by (`(centre, radius)`,
    /// model-local): [`drawing_sphere`]; `None` when there is none.
    pub drawing_sphere: Option<(Vec3, f32)>,
}

/// Mesh a GfxObj. `None` when it is missing or unreadable.
pub fn build_gfx_obj(content: &Content, id: u32) -> Option<GfxObjData> {
    let gfx = content.dats.get::<GfxObj>(id).ok()??;
    let mut data = pack_sub_meshes(id, &meshing::build_gfx_obj_mesh(&gfx));
    data.degrades = crate::cells::degrade_levels(content, &gfx);
    data.sort_center = vec3(gfx.sort_center);
    data.drawing_sphere = drawing_sphere(&gfx);
    Some(data)
}

/// The sphere a view tests a GfxObj by: the root sphere of its drawing
/// tree as authored, else the sphere around the box of its vertices (its
/// centre and half its diagonal). `None` without either.
#[must_use]
pub fn drawing_sphere(gfx: &GfxObj) -> Option<(Vec3, f32)> {
    if let Some(root) = gfx.drawing_bsp.as_ref().and_then(|tree| tree.root()) {
        let sphere = match root {
            ac_dat::objects::BspNode::Split(s) => s.sphere,
            ac_dat::objects::BspNode::Portal(p) => p.sphere,
            ac_dat::objects::BspNode::Leaf(l) => l.sphere,
        };
        return Some((vec3(sphere.origin), sphere.radius));
    }
    let mut points = gfx.vertices.vertices.iter().map(|(_, v)| vec3(v.origin));
    let first = points.next()?;
    let (lo, hi) = points.fold((first, first), |(lo, hi), p| (lo.min(p), hi.max(p)));
    Some(((lo + hi) * 0.5, (hi - lo).length() * 0.5))
}

fn pack_sub_meshes(id: u32, subs: &[SubMesh]) -> GfxObjData {
    let mut data = GfxObjData {
        id,
        vertices: Vec::new(),
        indices: Vec::new(),
        sub_meshes: Vec::new(),
        degrades: Vec::new(),
        sort_center: Vec3::ZERO,
        drawing_sphere: None,
    };
    for sub in subs {
        if sub.indices.is_empty() {
            continue;
        }
        data.sub_meshes.push(SubMeshRange {
            surface_id: sub.surface_id,
            first_index: data.indices.len() as u32,
            index_count: sub.indices.len() as u32,
            base_vertex: data.vertices.len() as i32,
            repeat: sub.needs_uv_repeat(),
            cull: meshing::cull_of(sub.sides_type),
        });
        data.indices.extend_from_slice(&sub.indices);
        data.vertices
            .extend(sub.vertices.iter().map(|v| MeshGpuVertex {
                position: v.position.to_array(),
                normal: v.normal.to_array(),
                uv: v.uv.to_array(),
            }));
    }
    data
}

/// A surface ready to upload: its texture and how it blends.
#[derive(Debug, Clone)]
pub struct SurfaceData {
    /// Surface id.
    pub id: u32,
    /// The texture (1 x 1 RGBA for a solid colour; block compressed when
    /// it was built to stay so).
    pub texture: SurfaceImage,
    /// Blend mode.
    pub translucency: Translucency,
    /// Opacity multiplier from the surface's translucency.
    pub opacity: f32,
    /// Pixels with alpha below this are discarded.
    pub alpha_cutoff: f32,
    /// A flat colour, not an image.
    pub untextured: bool,
    /// Self-lighting, 0..1 (the sky reads it).
    pub luminosity: f32,
    /// Diffuse lighting factor (the sky reads it).
    pub diffuse: f32,
    /// An additive surface: the game's fixed-function fog skips it.
    pub disable_fog: bool,
}

/// The retail alpha-test reference of a clip-mapped surface: 100/255 for
/// a paletted texture, 200/255 otherwise. Other surfaces discard below
/// 0.05.
#[must_use]
pub fn alpha_cutoff(surface_type: SurfaceType, paletted: bool) -> f32 {
    let clip = surface_type.0 & SurfaceType::BASE1_CLIP_MAP != 0;
    let translucent = surface_type.0 & SurfaceType::TRANSLUCENT != 0;
    if !clip || translucent {
        0.05
    } else if paletted {
        100.0 / 255.0
    } else {
        200.0 / 255.0
    }
}

/// Decode a surface: a solid colour or its texture (first render surface,
/// default palette, clip-map and additive options from the surface type).
/// Missing pieces give magenta. The texture is always decoded RGBA.
#[must_use]
pub fn build_surface(content: &Content, id: u32) -> SurfaceData {
    build_surface_with(content, id, id, None, None, false)
}

/// Decode surface `surface_id` as `handle` with an object's substitutions:
/// `texture` replaces its surface texture, `palette` its texture's palette
/// (only a paletted texture changes). Missing pieces give magenta.
/// `keep_compressed`: a block-compressed texture that may stay so
/// ([`surface_image::keeps_compressed`]) is kept as stored instead of
/// decoded, for a GPU that samples it.
#[must_use]
pub fn build_surface_with(
    content: &Content,
    handle: u32,
    surface_id: u32,
    texture: Option<u32>,
    palette: Option<&Palette>,
    keep_compressed: bool,
) -> SurfaceData {
    let Some(surface) = content.dats.get::<Surface>(surface_id).ok().flatten() else {
        return SurfaceData {
            id: handle,
            texture: SurfaceImage::rgba(magenta()),
            translucency: Translucency::Opaque,
            opacity: 1.0,
            alpha_cutoff: 0.05,
            untextured: false,
            luminosity: 0.0,
            diffuse: 1.0,
            disable_fog: false,
        };
    };
    let t = surface.surface_type;
    let clip_map = t.contains(SurfaceType::BASE1_CLIP_MAP);
    let compressed = match surface.base {
        SurfaceBase::Texture { texture: own, .. }
            if keep_compressed && own != 0 && !t.contains(SurfaceType::BASE1_SOLID) =>
        {
            compressed_surface_texture(
                content,
                texture.unwrap_or(own),
                clip_map,
                surface.translucency,
            )
        }
        _ => None,
    };
    let (decoded, paletted) = match surface.base {
        _ if compressed.is_some() => (magenta(), false),
        SurfaceBase::Solid(color) => (
            Arc::new(texture::solid_color(color, surface.translucency)),
            false,
        ),
        // A solid-flagged or texture-less image surface has no stored
        // colour: it decodes as the zero colour.
        _ if t.contains(SurfaceType::BASE1_SOLID) => (
            Arc::new(texture::solid_color(
                ac_dat::types::ColorArgb::default(),
                surface.translucency,
            )),
            false,
        ),
        SurfaceBase::Texture { texture: 0, .. } => (
            Arc::new(texture::solid_color(
                ac_dat::types::ColorArgb::default(),
                surface.translucency,
            )),
            false,
        ),
        SurfaceBase::Texture { texture: own, .. } => decode_surface_texture_with(
            content,
            texture.unwrap_or(own),
            DecodeOptions {
                clip_map,
                additive: t.contains(SurfaceType::ADDITIVE),
            },
            palette,
        )
        .unwrap_or_else(|| (magenta(), false)),
    };
    SurfaceData {
        id: handle,
        texture: compressed.unwrap_or_else(|| SurfaceImage::rgba(decoded)),
        translucency: translucency_of(t),
        opacity: opacity_of(t, surface.translucency),
        alpha_cutoff: alpha_cutoff(t, paletted),
        untextured: is_untextured(t),
        luminosity: surface.luminosity,
        diffuse: surface.diffuse,
        disable_fog: t.contains(SurfaceType::ADDITIVE),
    }
}

/// A surface texture's first render surface as stored, when it may stay
/// block compressed. A substituted palette does not matter: block-compressed
/// textures are not paletted.
fn compressed_surface_texture(
    content: &Content,
    surface_texture_id: u32,
    clip_map: bool,
    translucency: f32,
) -> Option<SurfaceImage> {
    let st = content
        .dats
        .get::<SurfaceTexture>(surface_texture_id)
        .ok()??;
    let rs = content
        .dats
        .get::<RenderSurface>(*st.textures.first()?)
        .ok()??;
    let format =
        surface_image::keeps_compressed(rs.format, clip_map, translucency, rs.width, rs.height)?;
    surface_image::compressed_from_dat(format, rs.width, rs.height, &rs.data)
}

/// Decode a surface texture's first render surface, with `palette` in
/// place of its own when it is paletted. A substituted palette bypasses the
/// shared decoded-texture cache (its key names the stored palette).
fn decode_surface_texture_with(
    content: &Content,
    surface_texture_id: u32,
    options: DecodeOptions,
    palette: Option<&Palette>,
) -> Option<(Arc<DecodedTexture>, bool)> {
    let Some(palette) = palette else {
        return decode_surface_texture(content, surface_texture_id, options);
    };
    let st = content
        .dats
        .get::<SurfaceTexture>(surface_texture_id)
        .ok()??;
    let rs_id = *st.textures.first()?;
    let rs = content.dats.get::<RenderSurface>(rs_id).ok()??;
    if !matches!(rs.format, PixelFormat::P8 | PixelFormat::INDEX16) {
        return decode_render_surface(content, rs_id, options);
    }
    let decoded = texture::decode(&rs, Some(palette), options).ok()?;
    Some((Arc::new(decoded), true))
}

/// The palette a surface is painted with when nothing replaces it: its own
/// palette, else its texture's default palette; 0 for a colour surface.
fn surface_original_palette(content: &Content, surface: &Surface) -> u32 {
    match surface.base {
        SurfaceBase::Texture { palette, .. } if palette != 0 => palette,
        SurfaceBase::Texture { texture, .. } if texture != 0 => content
            .dats
            .get::<SurfaceTexture>(texture)
            .ok()
            .flatten()
            .and_then(|st| st.textures.first().copied())
            .and_then(|rs| content.dats.get::<RenderSurface>(rs).ok().flatten())
            .map_or(0, |rs| rs.default_palette_id),
        _ => 0,
    }
}

/// One surface of a part that an appearance changes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SurfaceChange {
    /// The part's surface.
    pub surface_id: u32,
    /// The surface texture shown instead of its own.
    pub texture: Option<u32>,
    /// The surface takes the appearance's modified palette.
    pub repaint: bool,
}

/// One part of an object drawn with its appearance.
#[derive(Debug, Clone, PartialEq)]
pub struct AppearancePart {
    /// The part's model (after part swaps) and pose; `variant` is 0.
    pub part: MeshRef,
    /// Its surfaces that look different.
    pub changes: Vec<SurfaceChange>,
}

/// The parts of a model drawn with an appearance: part swaps applied, and
/// for each part the surfaces whose texture or palette changes. A part is
/// repainted when the palette it was authored with is the appearance's
/// base palette. `None` for an unknown model.
#[must_use]
pub fn appearance_parts(content: &Content, key: &AppearanceKey) -> Option<Vec<AppearancePart>> {
    let mut parts = model_parts(content, key.setup_id)?;
    let close = key.close_degrades
        && crate::degrade::has_humanoid_null_part_layout(parts.iter().map(|p| p.gfx_obj_id));
    appearance::apply_part_changes(&mut parts, &key.part_changes);
    // A humanoid in the world draws each (swapped) part's closest model;
    // its texture swaps then name that model's surfaces.
    if close {
        close_degrade_parts(content, &mut parts);
    }
    let base = key.palette.as_ref().map(|p| p.base_palette_id);
    Some(
        parts
            .into_iter()
            .enumerate()
            .map(|(index, part)| {
                let surfaces: Vec<(u32, Option<Arc<Surface>>)> = content
                    .dats
                    .get::<GfxObj>(part.gfx_obj_id)
                    .ok()
                    .flatten()
                    .map(|gfx| {
                        gfx.surfaces
                            .iter()
                            .map(|&id| (id, content.dats.get::<Surface>(id).ok().flatten()))
                            .collect()
                    })
                    .unwrap_or_default();
                let original = appearance::part_original_palette(surfaces.iter().map(|(_, s)| {
                    s.as_deref()
                        .map_or(0, |s| surface_original_palette(content, s))
                }));
                let repaint = base.is_some() && original == base;
                let changes = surfaces
                    .iter()
                    .filter_map(|(id, surface)| {
                        let surface = surface.as_deref()?;
                        let SurfaceBase::Texture { texture: own, .. } = surface.base else {
                            return None;
                        };
                        if own == 0 || surface.surface_type.contains(SurfaceType::BASE1_SOLID) {
                            return None;
                        }
                        let texture =
                            appearance::texture_change_for(&key.texture_changes, index, own);
                        (texture.is_some() || repaint).then_some(SurfaceChange {
                            surface_id: *id,
                            texture,
                            repaint,
                        })
                    })
                    .collect();
                AppearancePart { part, changes }
            })
            .collect(),
    )
}

/// Replace each part's model with its closest level
/// ([`crate::degrade::close_gfx_obj`]).
pub fn close_degrade_parts(content: &Content, parts: &mut [MeshRef]) {
    let ladder_of = |id: u32| {
        let gfx = content.dats.get::<GfxObj>(id).ok().flatten()?;
        Some(
            crate::cells::degrade_levels(content, &gfx)
                .iter()
                .map(|l| l.gfx_obj_id)
                .collect(),
        )
    };
    for part in parts {
        if let Some(close) = crate::degrade::close_gfx_obj(part.gfx_obj_id, ladder_of) {
            part.gfx_obj_id = close;
        }
    }
}

/// The modified palette of a palette swap; `None` when it cannot be built
/// (then nothing is repainted).
#[must_use]
pub fn modified_palette(content: &Content, spec: &PaletteSpec) -> Option<Palette> {
    let base = content.dats.get::<Palette>(spec.base_palette_id).ok()??;
    appearance::compose_modified_palette(&base, &spec.ranges, |id| {
        content.dats.get::<Palette>(id).ok().flatten()
    })
}

fn rgb(c: ac_dat::types::ColorArgb) -> [u8; 3] {
    [c.red, c.green, c.blue]
}

/// The region's sky as the sky rules take it: a field-for-field copy.
#[must_use]
pub fn sky_source(sky: &ac_dat::objects::SkyDesc) -> SkyDescSource {
    SkyDescSource {
        tick_size: sky.tick_size,
        light_tick_size: sky.light_tick_size,
        day_groups: sky
            .day_groups
            .iter()
            .map(|g| DayGroupSource {
                chance_of_occur: g.chance_of_occur,
                day_name: g.day_name.clone(),
                sky_objects: g
                    .sky_objects
                    .iter()
                    .map(|o| SkyObjectSource {
                        begin_time: o.begin_time,
                        end_time: o.end_time,
                        begin_angle: o.begin_angle,
                        end_angle: o.end_angle,
                        tex_velocity_x: o.tex_velocity_x,
                        tex_velocity_y: o.tex_velocity_y,
                        default_gfx_object_id: o.default_gfx_object_id,
                        default_pes_object_id: o.default_pes_object_id,
                        properties: o.properties,
                    })
                    .collect(),
                sky_time: g
                    .sky_time
                    .iter()
                    .map(|t| SkyTimeSource {
                        begin: t.begin,
                        dir_bright: t.dir_bright,
                        dir_heading: t.dir_heading,
                        dir_pitch: t.dir_pitch,
                        dir_color: rgb(t.dir_color),
                        amb_bright: t.amb_bright,
                        amb_color: rgb(t.amb_color),
                        min_world_fog: t.min_world_fog,
                        max_world_fog: t.max_world_fog,
                        world_fog_color: rgb(t.world_fog_color),
                        world_fog: t.world_fog,
                        sky_obj_replace: t
                            .sky_obj_replace
                            .iter()
                            .map(|r| SkyObjectReplaceSource {
                                object_index: r.object_index,
                                gfx_obj_id: r.gfx_obj_id,
                                rotate: r.rotate,
                                transparent: r.transparent,
                                luminosity: r.luminosity,
                                max_bright: r.max_bright,
                            })
                            .collect(),
                    })
                    .collect(),
            })
            .collect(),
    }
}

/// The sky loader's model lookups through the content; a failed read is
/// "unknown".
struct ContentSkyLookup<'a>(&'a Content);

impl SkyDatLookup for ContentSkyLookup<'_> {
    fn gfx_obj_sort_center(&self, id: u32) -> Option<Vec3> {
        let g = self.0.dats.get::<GfxObj>(id).ok()??;
        Some(vec3(g.sort_center))
    }
    fn setup_default_script(&self, id: u32) -> Option<u32> {
        Some(self.0.dats.get::<Setup>(id).ok()??.default_script)
    }
}

/// The region's sky, ready for use; `None` when the region has none.
#[must_use]
pub fn region_sky(content: &Content, region: &Region) -> Option<LoadedSkyDesc> {
    let sky = region.sky_info.as_ref()?;
    Some(load_sky_desc(
        &sky_source(sky),
        Some(&ContentSkyLookup(content)),
    ))
}

/// A setup's lights as the light rules take them, in stored order.
#[must_use]
pub fn setup_lights(setup: &Setup) -> Vec<LightInfoSource> {
    setup
        .lights
        .iter()
        .map(|(_, info)| {
            let f = &info.view_space_location;
            LightInfoSource {
                origin: vec3(f.origin),
                orientation: quat(f.orientation),
                color: rgb(info.color),
                intensity: info.intensity,
                falloff: info.falloff,
                cone_angle: info.cone_angle,
            }
        })
        .collect()
}

/// The lights of a model: a setup's lights; none for a single GfxObj.
#[must_use]
pub fn model_lights(content: &Content, model_id: u32) -> Vec<LightInfoSource> {
    if model_id >> 24 != 0x02 {
        return Vec::new();
    }
    content
        .dats
        .get::<Setup>(model_id)
        .ok()
        .flatten()
        .map(|s| setup_lights(&s))
        .unwrap_or_default()
}

/// The box around a GfxObj's vertices, model-local; `None` without
/// vertices.
#[must_use]
pub fn gfx_bounds(gfx: &GfxObjData) -> Option<(Vec3, Vec3)> {
    let mut points = gfx.vertices.iter().map(|v| Vec3::from(v.position));
    let first = points.next()?;
    Some(points.fold((first, first), |(lo, hi), p| (lo.min(p), hi.max(p))))
}

/// The box around a model's parts, given each GfxObj's own box.
#[must_use]
pub fn parts_bounds(
    parts: &[MeshRef],
    mut bounds_of: impl FnMut(u32) -> Option<(Vec3, Vec3)>,
) -> Option<(Vec3, Vec3)> {
    let mut out: Option<(Vec3, Vec3)> = None;
    for part in parts {
        let Some((lo, hi)) = bounds_of(part.gfx_obj_id) else {
            continue;
        };
        for i in 0..8 {
            let corner = Vec3::new(
                if i & 1 == 0 { lo.x } else { hi.x },
                if i & 2 == 0 { lo.y } else { hi.y },
                if i & 4 == 0 { lo.z } else { hi.z },
            );
            let p = part.transform.transform_point3(corner);
            out = Some(out.map_or((p, p), |(a, b)| (a.min(p), b.max(p))));
        }
    }
    out
}

/// A server object's placement: its landblock-local frame as a matrix with
/// its scale.
#[must_use]
pub fn object_transform(origin: Vec3, orientation: glam::Quat, scale: f32) -> Mat4 {
    Mat4::from_scale_rotation_translation(Vec3::splat(scale), orientation, origin)
}

/// A dat frame as a matrix (rotation then translation).
#[must_use]
pub fn frame_matrix(frame: &Frame) -> Mat4 {
    Mat4::from_rotation_translation(quat(frame.orientation), vec3(frame.origin))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mip_chain_halves_to_one_pixel() {
        let chain = mip_chain(vec![10; 8 * 4 * 4], 8, 4);
        let sizes: Vec<usize> = chain.levels.iter().map(Vec::len).collect();
        assert_eq!(sizes, [128, 32, 8, 4]);
        assert!(chain.levels.iter().all(|l| l.iter().all(|&b| b == 10)));
    }

    #[test]
    fn box_downsample_averages() {
        let src = [
            0, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 0, 255, 255, 255, 255,
        ];
        assert_eq!(downsample_box(&src, 2, 2, 1, 1), [128, 128, 128, 128]);
    }

    #[test]
    fn nearest_resize_repeats_pixels() {
        let src = LayerTexels {
            width: 1,
            height: 1,
            texels: vec![1, 2, 3, 4],
        };
        assert_eq!(resize_nearest(src, 4, 2, 1), [1, 2, 3, 4, 1, 2, 3, 4]);
        let weights = LayerTexels {
            width: 2,
            height: 1,
            texels: vec![7, 9],
        };
        assert_eq!(resize_nearest(weights, 1, 4, 2), [7, 7, 9, 9, 7, 7, 9, 9]);
    }

    #[test]
    fn bgra_swaps_red_and_blue() {
        let decoded = DecodedTexture {
            width: 2,
            height: 1,
            rgba: vec![1, 2, 3, 4, 5, 6, 7, 8],
        };
        assert_eq!(LayerTexels::bgra(&decoded).texels, [3, 2, 1, 4, 7, 6, 5, 8]);
    }

    #[test]
    fn clip_maps_test_alpha_at_the_retail_references() {
        let clip = SurfaceType(SurfaceType::BASE1_CLIP_MAP);
        assert!((alpha_cutoff(clip, true) - 100.0 / 255.0).abs() < 1e-6);
        assert!((alpha_cutoff(clip, false) - 200.0 / 255.0).abs() < 1e-6);
        let translucent_clip = SurfaceType(SurfaceType::BASE1_CLIP_MAP | SurfaceType::TRANSLUCENT);
        assert_eq!(alpha_cutoff(translucent_clip, true), 0.05);
        assert_eq!(
            alpha_cutoff(SurfaceType(SurfaceType::BASE1_IMAGE), false),
            0.05
        );
    }

    #[test]
    fn sub_meshes_pack_into_one_buffer_with_ranges() {
        let tri = |surface_id| SubMesh {
            surface_index: 0,
            surface_id,
            is_negative_side: false,
            sides_type: 0,
            vertices: vec![
                meshing::MeshVertex {
                    position: Vec3::ZERO,
                    normal: Vec3::Z,
                    uv: glam::Vec2::ZERO,
                };
                3
            ],
            indices: vec![0, 1, 2],
        };
        let data = pack_sub_meshes(1, &[tri(8), tri(9)]);
        assert_eq!(data.vertices.len(), 6);
        assert_eq!(data.indices, [0, 1, 2, 0, 1, 2]);
        assert_eq!(data.sub_meshes[1].first_index, 3);
        assert_eq!(data.sub_meshes[1].base_vertex, 3);
        assert_eq!(data.sub_meshes[1].surface_id, 9);
    }

    #[test]
    fn parallel_map_keeps_the_items_order() {
        let items: Vec<u32> = (0..257).collect();
        let slow_first = |&i: &u32| {
            // Early items finish last, so completion order differs.
            if i < 4 {
                std::thread::sleep(std::time::Duration::from_millis(20));
            }
            i * 3
        };
        let expected: Vec<u32> = items.iter().map(slow_first).collect();
        assert_eq!(parallel_map(&items, slow_first), expected);
        assert_eq!(parallel_map(&[] as &[u32], slow_first), Vec::<u32>::new());
        assert_eq!(parallel_map(&[7], slow_first), vec![21]);
    }

    /// A quad of side 4 m with the drawing tree `drawing_bsp`.
    fn quad_gfx(drawing_bsp: Option<ac_dat::objects::BspTree>) -> GfxObj {
        use ac_dat::objects::{GfxObjFlags, Vertex, VertexArray};
        use ac_dat::types::Vector3;
        let corner = |i: u16, x: f32, y: f32| {
            (
                i,
                Vertex {
                    origin: Vector3 { x, y, z: 0.0 },
                    normal: Vector3::default(),
                    uvs: Vec::new(),
                },
            )
        };
        GfxObj {
            id: 0x0100_0001,
            flags: GfxObjFlags(GfxObjFlags::HAS_DRAWING),
            surfaces: Vec::new(),
            vertices: VertexArray {
                vertex_type: VertexArray::SW_VERTEX,
                vertices: vec![
                    corner(0, -2.0, -2.0),
                    corner(1, 2.0, -2.0),
                    corner(2, 2.0, 2.0),
                    corner(3, -2.0, 2.0),
                ],
            },
            physics_polygons: Vec::new(),
            physics_bsp: None,
            sort_center: Vector3::default(),
            polygons: Vec::new(),
            drawing_bsp,
            did_degrade: 0,
        }
    }

    /// OpenAC `MeshExtractorSolidFaceExtractionTests.PrepareMeshData_CarriesAuthoredDrawingBspSphereInsteadOfVertexAabbSphere`:
    /// the drawing tree's root sphere, as authored, is the part's sphere.
    #[test]
    fn the_drawing_sphere_is_the_drawing_trees_authored_root_sphere() {
        use ac_dat::objects::{BspLeaf, BspNode, BspTree, BspTreeKind};
        use ac_dat::types::{Sphere, Vector3};
        let authored = Sphere {
            origin: Vector3 {
                x: -0.25,
                y: 0.125,
                z: 0.5,
            },
            radius: 3.125,
        };
        let gfx = quad_gfx(Some(BspTree {
            kind: BspTreeKind::Drawing,
            nodes: vec![BspNode::Leaf(BspLeaf {
                index: 0,
                solid: 0,
                sphere: authored,
                polygons: Vec::new(),
            })],
        }));
        assert_eq!(
            drawing_sphere(&gfx),
            Some((Vec3::new(-0.25, 0.125, 0.5), 3.125))
        );
    }

    /// OpenAC `MeshExtractor.PrepareGfxObjMeshData`: without a drawing tree
    /// the sphere is the vertices' box: its centre, half its diagonal.
    #[test]
    fn without_a_drawing_tree_the_sphere_is_the_vertex_boxs() {
        let (centre, radius) = drawing_sphere(&quad_gfx(None)).unwrap();
        assert_eq!(centre, Vec3::ZERO);
        assert!((radius - 8f32.sqrt()).abs() < 1e-6, "{radius}");
    }

    /// OpenAC `InstalledPreparedCollisionCatalogTests.InstalledPackage_FacilityHubStepsCarryRetailDrawingSphere`:
    /// the Facility Hub's step model (GfxObj 0x010000DE) is admitted by its
    /// drawing tree's root sphere as the dat authors it.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_facility_hub_steps_carry_their_authored_drawing_sphere() {
        use ac_dat::objects::BspNode;
        const STAIR: u32 = 0x0100_00DE;
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let d = Content::open(&dir).expect("open the retail dats");
        let gfx = d.dats.get::<GfxObj>(STAIR).unwrap().unwrap();
        let root = match gfx.drawing_bsp.as_ref().and_then(|t| t.root()).unwrap() {
            BspNode::Split(s) => s.sphere,
            BspNode::Portal(p) => p.sphere,
            BspNode::Leaf(l) => l.sphere,
        };
        let data = build_gfx_obj(&d, STAIR).unwrap();
        assert_eq!(data.drawing_sphere, Some((vec3(root.origin), root.radius)));
        assert!(root.radius > 0.0);
    }
}
