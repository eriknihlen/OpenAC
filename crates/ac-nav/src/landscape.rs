//! The outdoor landscape as the game files store it, for a plugin that
//! draws a map: each landblock's 9 by 9 terrain vertices (terrain type,
//! road bits and ground height), and how each terrain type looks (the mean
//! colour of its landscape texture, and its name).

use std::collections::{HashMap, VecDeque};
use std::sync::Arc;

use ac_dat::DatError;

use ac_dat::objects::{
    LandBlock, Palette, RenderSurface, SurfaceTexture, TerrainDesc, TmTerrainDesc, VERTEX_COUNT,
    VERTICES_PER_SIDE,
};
use ac_dat::texture::{self, DecodeOptions};
use ac_physics::TerrainSurface;
use ac_physics::content::DatSource;

/// The palette entry for the road: the texture laid where a vertex's road
/// bits are set, after the 32 terrain types.
#[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]
pub const ROAD_TERRAIN_TYPE: u8 = TmTerrainDesc::ROAD_TERRAIN_TYPE as u8;

/// How many landblocks' terrain is kept; the landblock read longest ago
/// goes first once it is full. Four full requests' worth.
pub const MAXIMUM_CACHED_TERRAIN_LANDBLOCKS: usize = 1024;

/// One vertex of a landblock's terrain grid.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct TerrainVertex {
    /// The terrain type, 0 to 31.
    pub terrain_type: u8,
    /// The road bits, 0 to 3.
    pub road: u8,
    /// The ground's height, metres, through the region's height table.
    pub height_meters: f32,
}

/// A landblock's outdoor terrain.
#[derive(Debug, Clone, PartialEq)]
pub struct LandblockTerrain {
    /// The landblock, `x << 8 | y`.
    pub landblock: u16,
    /// Vertex `(x, y)` at index `x * 9 + y`: `x` east and `y` north of the
    /// south-west corner, 24 m apart, as the game files order them.
    pub vertices: [TerrainVertex; VERTEX_COUNT],
}

/// The terrain of landblock `landblock` (`x << 8 | y`) from its terrain
/// file, its heights through `height_table` as the physics ground takes
/// them; none when the files have no terrain for it.
///
/// # Errors
/// A dat read or decode failure.
pub fn read_landblock_terrain<D: DatSource>(
    dats: &D,
    height_table: &[f32; 256],
    landblock: u16,
) -> Result<Option<LandblockTerrain>, DatError> {
    let Some(block) = dats.object::<LandBlock>(u32::from(landblock) << 16 | 0xFFFF)? else {
        return Ok(None);
    };
    let vertices = std::array::from_fn(|i| {
        let (x, y) = (i / VERTICES_PER_SIDE, i % VERTICES_PER_SIDE);
        TerrainVertex {
            terrain_type: block.terrain[i].terrain_type(),
            road: block.terrain[i].road(),
            height_meters: TerrainSurface::vertex_height(&block.height, height_table, x, y),
        }
    });
    Ok(Some(LandblockTerrain {
        landblock,
        vertices,
    }))
}

/// The terrain of the landblocks asked about, read once each.
#[derive(Debug, Default)]
pub struct LandscapeTerrainCache {
    kept: HashMap<u16, Option<Arc<LandblockTerrain>>>,
    order: VecDeque<u16>,
}

impl LandscapeTerrainCache {
    /// Nothing kept yet.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// The terrain of `landblock` ([`read_landblock_terrain`]); the same
    /// answer comes back until the cache drops it.
    ///
    /// # Errors
    /// A dat read or decode failure; nothing is kept then.
    pub fn terrain<D: DatSource>(
        &mut self,
        dats: &D,
        height_table: &[f32; 256],
        landblock: u16,
    ) -> Result<Option<Arc<LandblockTerrain>>, DatError> {
        if let Some(kept) = self.kept.get(&landblock) {
            return Ok(kept.clone());
        }
        let read = read_landblock_terrain(dats, height_table, landblock)?.map(Arc::new);
        if self.kept.len() >= MAXIMUM_CACHED_TERRAIN_LANDBLOCKS
            && let Some(oldest) = self.order.pop_front()
        {
            self.kept.remove(&oldest);
        }
        self.kept.insert(landblock, read.clone());
        self.order.push_back(landblock);
        Ok(read)
    }
}

/// How one of the region's terrain types, or the road, looks.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TerrainColor {
    /// The terrain type, or [`ROAD_TERRAIN_TYPE`] for the road.
    pub terrain_type: u8,
    /// The mean colour of its landscape texture, `0xRRGGBB`; 0 when the
    /// files have no texture for it or it does not decode.
    pub rgb: u32,
    /// The colour the region assigns the type for maps, `0xRRGGBB`; the
    /// region has none for the road, whose entry repeats `rgb`.
    pub map_rgb: u32,
    /// Its name as the region gives it, "Road" for the road; none for an
    /// empty one.
    pub name: Option<String>,
}

/// Why the terrain palette could not be read.
#[derive(Debug)]
pub enum PaletteError {
    /// A dat read failure.
    Dat(DatError),
}

impl std::fmt::Display for PaletteError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Dat(error) => error.fmt(f),
        }
    }
}

impl std::error::Error for PaletteError {}

impl From<DatError> for PaletteError {
    fn from(error: DatError) -> Self {
        Self::Dat(error)
    }
}

/// Terrain types a vertex can name: its terrain word gives the type five
/// bits.
const TERRAIN_TYPE_COUNT: usize = 32;

/// Each terrain type the region's terrain description `terrain` defines, in
/// type order, then the road when the region lists a road texture: the
/// mean colour of the landscape texture the region gives each, the colour
/// the region assigns each type for maps, and its name. The texture is the
/// first the region lists for the type, as the landscape is drawn, decoded
/// as the renderer decodes it: the texture's first (full-size) image with
/// its default palette. The region has no map colour or name for the road:
/// its entry repeats the texture's colour and is named "Road".
///
/// # Errors
/// A dat read failure.
pub fn read_terrain_palette<D: DatSource>(
    dats: &D,
    terrain: &TerrainDesc,
) -> Result<Vec<TerrainColor>, PaletteError> {
    let textures = &terrain.land_surfaces.tex_merge.terrain_desc;
    let first_texture = |terrain_type: i32| {
        textures
            .iter()
            .find(|desc| desc.terrain_type == terrain_type)
            .map(|desc| desc.terrain_tex.texture_id)
    };
    let mean_of = |texture_id: Option<u32>| -> Result<u32, PaletteError> {
        Ok(match texture_id {
            Some(id) => texture_mean_rgb(dats, id)?.unwrap_or(0),
            None => 0,
        })
    };
    let mut palette = Vec::with_capacity(terrain.terrain_types.len() + 1);
    for (index, defined) in terrain
        .terrain_types
        .iter()
        .enumerate()
        .take(TERRAIN_TYPE_COUNT)
    {
        let terrain_type = u8::try_from(index).unwrap_or(u8::MAX);
        let map = defined.terrain_color;
        palette.push(TerrainColor {
            terrain_type,
            rgb: mean_of(first_texture(i32::from(terrain_type)))?,
            map_rgb: u32::from(map.red) << 16 | u32::from(map.green) << 8 | u32::from(map.blue),
            name: Some(defined.terrain_name.clone()).filter(|name| !name.is_empty()),
        });
    }
    if let Some(road) = first_texture(TmTerrainDesc::ROAD_TERRAIN_TYPE) {
        let rgb = mean_of(Some(road))?;
        palette.push(TerrainColor {
            terrain_type: ROAD_TERRAIN_TYPE,
            rgb,
            map_rgb: rgb,
            name: Some("Road".to_owned()),
        });
    }
    Ok(palette)
}

/// The mean colour of surface texture `surface_texture_id`'s first image
/// with its default palette; none when the files lack the texture or its
/// image.
fn texture_mean_rgb<D: DatSource>(
    dats: &D,
    surface_texture_id: u32,
) -> Result<Option<u32>, PaletteError> {
    let Some(texture) = dats.object::<SurfaceTexture>(surface_texture_id)? else {
        return Ok(None);
    };
    let Some(&render_surface_id) = texture.textures.first() else {
        return Ok(None);
    };
    let Some(surface) = dats.object::<RenderSurface>(render_surface_id)? else {
        return Ok(None);
    };
    let palette = if surface.default_palette_id == 0 {
        None
    } else {
        dats.object::<Palette>(surface.default_palette_id)?
    };
    // A texture that does not decode gives its type no colour (0), as one
    // the files lack does, rather than failing the whole palette.
    let Ok(decoded) = texture::decode(&surface, palette.as_deref(), DecodeOptions::default())
    else {
        return Ok(None);
    };
    Ok(Some(mean_rgb(&decoded.rgba)))
}

/// The mean of the texels' red, green and blue (four bytes a texel, RGBA),
/// each rounded to the nearest, as `0xRRGGBB`; 0 for no texels.
#[must_use]
pub fn mean_rgb(rgba: &[u8]) -> u32 {
    let texels = rgba.chunks_exact(4);
    let count = texels.len() as u64;
    if count == 0 {
        return 0;
    }
    let mut sums = [0u64; 3];
    for texel in texels {
        for (sum, &channel) in sums.iter_mut().zip(texel) {
            *sum += u64::from(channel);
        }
    }
    let [r, g, b] = sums.map(|sum| (sum + count / 2) / count);
    // Each mean is at most 255.
    u32::try_from(r << 16 | g << 8 | b).unwrap_or(0)
}

#[cfg(test)]
mod tests;
