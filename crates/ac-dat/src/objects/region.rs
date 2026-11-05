use ac_bits::Reader;

use super::DatObject;
use super::read::{self, legacy_string, list};
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::{ColorArgb, read_bool32};

type Result<T> = core::result::Result<T, DecodeError>;

/// Region part bits: which optional descriptions are stored.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PartsMask(pub u32);

impl PartsMask {
    /// Ambient sound description.
    pub const HAS_SOUND_INFO: u32 = 0x01;
    /// Scenery description.
    pub const HAS_SCENE_INFO: u32 = 0x02;
    /// Sky description.
    pub const HAS_SKY_INFO: u32 = 0x10;
    /// Map and test ids.
    pub const HAS_REGION_MISC: u32 = 0x200;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u32) -> bool {
        self.0 & bits == bits
    }
}

/// The world's description: land dimensions and heights, calendar, sky,
/// ambient sound, scenery and terrain textures. File id `0x13......`; the
/// retail portal dat holds one, `0x13000000` (Dereth).
#[derive(Debug, Clone, PartialEq)]
pub struct Region {
    /// File id.
    pub id: u32,
    /// Region number.
    pub region_number: u32,
    /// Format version; always [`Region::VERSION`].
    pub version: u32,
    /// Name.
    pub name: String,
    /// Land dimensions and the height table.
    pub land_defs: LandDefs,
    /// Calendar.
    pub game_time: GameTime,
    /// Which optional parts are present.
    pub parts_mask: PartsMask,
    /// Sky; present with [`PartsMask::HAS_SKY_INFO`].
    pub sky_info: Option<SkyDesc>,
    /// Ambient sounds; present with [`PartsMask::HAS_SOUND_INFO`].
    pub sound_info: Option<SoundDesc>,
    /// Scenery; present with [`PartsMask::HAS_SCENE_INFO`].
    pub scene_info: Option<SceneDesc>,
    /// Terrain types and textures.
    pub terrain_info: TerrainDesc,
    /// Map ids; present with [`PartsMask::HAS_REGION_MISC`].
    pub region_misc: Option<RegionMisc>,
}

impl Region {
    /// The only format version; any other is refused.
    pub const VERSION: u32 = 3;
}

impl DatObject for Region {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let region_number = r.u32()?;
        let version = r.u32()?;
        if version != Self::VERSION {
            return Err(DecodeError::Unsupported("region version"));
        }
        let name = legacy_string(r)?;
        r.align(4)?;
        let land_defs = LandDefs::unpack(r)?;
        let game_time = GameTime::unpack(r)?;
        let parts_mask = PartsMask(r.u32()?);
        let sky_info = if parts_mask.contains(PartsMask::HAS_SKY_INFO) {
            Some(SkyDesc::unpack(r)?)
        } else {
            None
        };
        let sound_info = if parts_mask.contains(PartsMask::HAS_SOUND_INFO) {
            Some(SoundDesc::unpack(r)?)
        } else {
            None
        };
        let scene_info = if parts_mask.contains(PartsMask::HAS_SCENE_INFO) {
            Some(SceneDesc::unpack(r)?)
        } else {
            None
        };
        let terrain_info = TerrainDesc::unpack(r)?;
        let region_misc = if parts_mask.contains(PartsMask::HAS_REGION_MISC) {
            Some(RegionMisc::unpack(r)?)
        } else {
            None
        };
        Ok(Self {
            id,
            region_number,
            version,
            name,
            land_defs,
            game_time,
            parts_mask,
            sky_info,
            sound_info,
            scene_info,
            terrain_info,
            region_misc,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// Number of entries in the land height table.
pub const LAND_HEIGHT_COUNT: usize = 256;

/// Land dimensions and the table that turns a vertex height index into
/// metres.
#[derive(Debug, Clone, PartialEq)]
pub struct LandDefs {
    /// Landblocks along one axis.
    pub num_block_length: i32,
    /// Landblocks along the other axis.
    pub num_block_width: i32,
    /// Side of one terrain square, metres.
    pub square_length: f32,
    /// Terrain squares along a landblock side.
    pub lblock_length: i32,
    /// Vertices per terrain square.
    pub vertex_per_cell: i32,
    /// Highest object, metres.
    pub max_obj_height: f32,
    /// Sky height, metres.
    pub sky_height: f32,
    /// Road width, metres.
    pub road_width: f32,
    /// Height in metres per height index.
    pub land_height_table: [f32; LAND_HEIGHT_COUNT],
}

impl LandDefs {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let num_block_length = r.i32()?;
        let num_block_width = r.i32()?;
        let square_length = r.f32()?;
        let lblock_length = r.i32()?;
        let vertex_per_cell = r.i32()?;
        let max_obj_height = r.f32()?;
        let sky_height = r.f32()?;
        let road_width = r.f32()?;
        let mut land_height_table = [0.0; LAND_HEIGHT_COUNT];
        for h in &mut land_height_table {
            *h = r.f32()?;
        }
        Ok(Self {
            num_block_length,
            num_block_width,
            square_length,
            lblock_length,
            vertex_per_cell,
            max_obj_height,
            sky_height,
            road_width,
            land_height_table,
        })
    }
}

/// The calendar: day length, times of day, weekdays and seasons.
#[derive(Debug, Clone, PartialEq)]
pub struct GameTime {
    /// Game time at the start of the zero year.
    pub zero_time_of_year: f64,
    /// The first year.
    pub zero_year: u32,
    /// Length of a day, seconds.
    pub day_length: f32,
    /// Days per year.
    pub days_per_year: u32,
    /// Year suffix (e.g. "P.Y.").
    pub year_spec: String,
    /// Named times of day.
    pub times_of_day: Vec<TimeOfDay>,
    /// Weekday names.
    pub days_of_week: Vec<String>,
    /// Seasons.
    pub seasons: Vec<Season>,
}

impl GameTime {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let zero_time_of_year = r.f64()?;
        let zero_year = r.u32()?;
        let day_length = r.f32()?;
        let days_per_year = r.u32()?;
        let year_spec = legacy_string(r)?;
        r.align(4)?;
        let count = u64::from(r.u32()?);
        let times_of_day = list(r, count, TimeOfDay::unpack)?;
        let count = u64::from(r.u32()?);
        let days_of_week = list(r, count, legacy_string)?;
        let count = u64::from(r.u32()?);
        let seasons = list(r, count, Season::unpack)?;
        Ok(Self {
            zero_time_of_year,
            zero_year,
            day_length,
            days_per_year,
            year_spec,
            times_of_day,
            days_of_week,
            seasons,
        })
    }
}

/// A named time of day.
#[derive(Debug, Clone, PartialEq)]
pub struct TimeOfDay {
    /// Start, as a fraction of the day.
    pub start: f32,
    /// Whether it counts as night.
    pub is_night: bool,
    /// Name.
    pub name: String,
}

impl TimeOfDay {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let start = r.f32()?;
        let is_night = read_bool32(r)?;
        let name = legacy_string(r)?;
        r.align(4)?;
        Ok(Self {
            start,
            is_night,
            name,
        })
    }
}

/// A season.
#[derive(Debug, Clone, PartialEq)]
pub struct Season {
    /// First day of the season.
    pub start: u32,
    /// Name.
    pub name: String,
}

impl Season {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let start = r.u32()?;
        let name = legacy_string(r)?;
        r.align(4)?;
        Ok(Self { start, name })
    }
}

/// The sky: kinds of day, each with sky objects and lighting over the day.
#[derive(Debug, Clone, PartialEq)]
pub struct SkyDesc {
    /// Sky update interval.
    pub tick_size: f64,
    /// Lighting update interval.
    pub light_tick_size: f64,
    /// Kinds of day (sunny, cloudy, ...).
    pub day_groups: Vec<DayGroup>,
}

impl SkyDesc {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let tick_size = r.f64()?;
        let light_tick_size = r.f64()?;
        let count = u64::from(r.u32()?);
        let day_groups = list(r, count, DayGroup::unpack)?;
        Ok(Self {
            tick_size,
            light_tick_size,
            day_groups,
        })
    }
}

/// One kind of day.
#[derive(Debug, Clone, PartialEq)]
pub struct DayGroup {
    /// Relative chance of this kind of day.
    pub chance_of_occur: f32,
    /// Name.
    pub day_name: String,
    /// Sky objects (sun, moon, clouds, ...).
    pub sky_objects: Vec<SkyObject>,
    /// Lighting keyframes over the day.
    pub sky_time: Vec<SkyTimeOfDay>,
}

impl DayGroup {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let chance_of_occur = r.f32()?;
        let day_name = legacy_string(r)?;
        r.align(4)?;
        let count = u64::from(r.u32()?);
        let sky_objects = list(r, count, SkyObject::unpack)?;
        let count = u64::from(r.u32()?);
        let sky_time = list(r, count, SkyTimeOfDay::unpack)?;
        Ok(Self {
            chance_of_occur,
            day_name,
            sky_objects,
            sky_time,
        })
    }
}

/// An object drawn in the sky.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SkyObject {
    /// Time of day it appears (fraction of the day).
    pub begin_time: f32,
    /// Time of day it disappears.
    pub end_time: f32,
    /// Angle at the start, degrees.
    pub begin_angle: f32,
    /// Angle at the end, degrees.
    pub end_angle: f32,
    /// Texture scroll speed, u.
    pub tex_velocity_x: f32,
    /// Texture scroll speed, v.
    pub tex_velocity_y: f32,
    /// Model id.
    pub default_gfx_object_id: u32,
    /// Physics script id.
    pub default_pes_object_id: u32,
    /// Property bits.
    pub properties: u32,
}

impl SkyObject {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            begin_time: r.f32()?,
            end_time: r.f32()?,
            begin_angle: r.f32()?,
            end_angle: r.f32()?,
            tex_velocity_x: r.f32()?,
            tex_velocity_y: r.f32()?,
            default_gfx_object_id: r.u32()?,
            default_pes_object_id: r.u32()?,
            properties: r.u32()?,
        })
    }
}

/// Lighting and fog from one time of day on.
#[derive(Debug, Clone, PartialEq)]
pub struct SkyTimeOfDay {
    /// Start, as a fraction of the day.
    pub begin: f32,
    /// Sunlight brightness.
    pub dir_bright: f32,
    /// Sunlight heading, degrees.
    pub dir_heading: f32,
    /// Sunlight pitch, degrees.
    pub dir_pitch: f32,
    /// Sunlight colour.
    pub dir_color: ColorArgb,
    /// Ambient brightness.
    pub amb_bright: f32,
    /// Ambient colour.
    pub amb_color: ColorArgb,
    /// Fog start distance, metres.
    pub min_world_fog: f32,
    /// Fog end distance, metres.
    pub max_world_fog: f32,
    /// Fog colour.
    pub world_fog_color: ColorArgb,
    /// Fog mode.
    pub world_fog: u32,
    /// Sky object overrides at this time.
    pub sky_obj_replace: Vec<SkyObjectReplace>,
}

impl SkyTimeOfDay {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let begin = r.f32()?;
        let dir_bright = r.f32()?;
        let dir_heading = r.f32()?;
        let dir_pitch = r.f32()?;
        let dir_color = ColorArgb::unpack(r)?;
        let amb_bright = r.f32()?;
        let amb_color = ColorArgb::unpack(r)?;
        let min_world_fog = r.f32()?;
        let max_world_fog = r.f32()?;
        let world_fog_color = ColorArgb::unpack(r)?;
        let world_fog = r.u32()?;
        let count = u64::from(r.u32()?);
        let sky_obj_replace = list(r, count, SkyObjectReplace::unpack)?;
        Ok(Self {
            begin,
            dir_bright,
            dir_heading,
            dir_pitch,
            dir_color,
            amb_bright,
            amb_color,
            min_world_fog,
            max_world_fog,
            world_fog_color,
            world_fog,
            sky_obj_replace,
        })
    }
}

/// An override of one sky object's look.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SkyObjectReplace {
    /// Index of the sky object.
    pub object_index: u32,
    /// Replacement model id (0 keeps the model).
    pub gfx_obj_id: u32,
    /// Rotation, degrees.
    pub rotate: f32,
    /// Transparency.
    pub transparent: f32,
    /// Luminosity.
    pub luminosity: f32,
    /// Maximum brightness.
    pub max_bright: f32,
}

impl SkyObjectReplace {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            object_index: r.u32()?,
            gfx_obj_id: r.u32()?,
            rotate: r.f32()?,
            transparent: r.f32()?,
            luminosity: r.f32()?,
            max_bright: r.f32()?,
        })
    }
}

/// Ambient sounds, grouped by sound table.
#[derive(Debug, Clone, PartialEq)]
pub struct SoundDesc {
    /// Groups, in file order; terrain picks one by index.
    pub stb_desc: Vec<AmbientStbDesc>,
}

impl SoundDesc {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let count = u64::from(r.u32()?);
        Ok(Self {
            stb_desc: list(r, count, AmbientStbDesc::unpack)?,
        })
    }
}

/// Ambient sounds played from one sound table.
#[derive(Debug, Clone, PartialEq)]
pub struct AmbientStbDesc {
    /// Sound table id.
    pub stb_id: u32,
    /// Sounds.
    pub ambient_sounds: Vec<AmbientSoundDesc>,
}

impl AmbientStbDesc {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let stb_id = r.u32()?;
        let count = u64::from(r.u32()?);
        Ok(Self {
            stb_id,
            ambient_sounds: list(r, count, AmbientSoundDesc::unpack)?,
        })
    }
}

/// One ambient sound and how often it plays.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AmbientSoundDesc {
    /// Sound type within the table.
    pub sound_type: u32,
    /// Volume.
    pub volume: f32,
    /// Base chance to play.
    pub base_chance: f32,
    /// Minimum interval.
    pub min_rate: f32,
    /// Maximum interval.
    pub max_rate: f32,
}

impl AmbientSoundDesc {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            sound_type: r.u32()?,
            volume: r.f32()?,
            base_chance: r.f32()?,
            min_rate: r.f32()?,
            max_rate: r.f32()?,
        })
    }
}

/// Scenery: lists of scenes, picked per terrain type.
#[derive(Debug, Clone, PartialEq)]
pub struct SceneDesc {
    /// Scene types, in file order.
    pub scene_types: Vec<SceneType>,
}

impl SceneDesc {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let count = u64::from(r.u32()?);
        Ok(Self {
            scene_types: list(r, count, SceneType::unpack)?,
        })
    }
}

/// A list of scenes and the ambient sound group that goes with them.
#[derive(Debug, Clone, PartialEq)]
pub struct SceneType {
    /// Index into [`SoundDesc::stb_desc`].
    pub stb_index: u32,
    /// Scene ids (`0x12......`).
    pub scenes: Vec<u32>,
}

impl SceneType {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let stb_index = r.u32()?;
        let count = u64::from(r.u32()?);
        Ok(Self {
            stb_index,
            scenes: list(r, count, read::u32)?,
        })
    }
}

/// Terrain types and how terrain is textured.
#[derive(Debug, Clone, PartialEq)]
pub struct TerrainDesc {
    /// Terrain types, indexed by a vertex's terrain type.
    pub terrain_types: Vec<TerrainType>,
    /// Texturing.
    pub land_surfaces: LandSurf,
}

impl TerrainDesc {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let count = u64::from(r.u32()?);
        let terrain_types = list(r, count, TerrainType::unpack)?;
        let land_surfaces = LandSurf::unpack(r)?;
        Ok(Self {
            terrain_types,
            land_surfaces,
        })
    }
}

/// One terrain type.
#[derive(Debug, Clone, PartialEq)]
pub struct TerrainType {
    /// Name.
    pub terrain_name: String,
    /// Map colour.
    pub terrain_color: ColorArgb,
    /// Scene type index per vertex scenery index.
    pub scene_types: Vec<u32>,
}

impl TerrainType {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let terrain_name = legacy_string(r)?;
        r.align(4)?;
        let terrain_color = ColorArgb::unpack(r)?;
        let count = u64::from(r.u32()?);
        Ok(Self {
            terrain_name,
            terrain_color,
            scene_types: list(r, count, read::u32)?,
        })
    }
}

/// How land is textured. Only the blended-texture form (type 0) is stored
/// in the retail dats and read here.
#[derive(Debug, Clone, PartialEq)]
pub struct LandSurf {
    /// Form; always 0.
    pub surf_type: u32,
    /// Blended terrain textures.
    pub tex_merge: TexMerge,
}

impl LandSurf {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let surf_type = r.u32()?;
        if surf_type != 0 {
            return Err(DecodeError::Unsupported("palette-shift land surface"));
        }
        Ok(Self {
            surf_type,
            tex_merge: TexMerge::unpack(r)?,
        })
    }
}

/// Terrain textures and the alpha maps that blend them.
#[derive(Debug, Clone, PartialEq)]
pub struct TexMerge {
    /// Base texture size, pixels.
    pub base_tex_size: u32,
    /// Corner blend maps.
    pub corner_terrain_maps: Vec<TerrainAlphaMap>,
    /// Side blend maps.
    pub side_terrain_maps: Vec<TerrainAlphaMap>,
    /// Road blend maps.
    pub road_maps: Vec<RoadAlphaMap>,
    /// Texture per terrain type.
    pub terrain_desc: Vec<TmTerrainDesc>,
}

impl TexMerge {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let base_tex_size = r.u32()?;
        let count = u64::from(r.u32()?);
        let corner_terrain_maps = list(r, count, TerrainAlphaMap::unpack)?;
        let count = u64::from(r.u32()?);
        let side_terrain_maps = list(r, count, TerrainAlphaMap::unpack)?;
        let count = u64::from(r.u32()?);
        let road_maps = list(r, count, RoadAlphaMap::unpack)?;
        let count = u64::from(r.u32()?);
        let terrain_desc = list(r, count, TmTerrainDesc::unpack)?;
        Ok(Self {
            base_tex_size,
            corner_terrain_maps,
            side_terrain_maps,
            road_maps,
            terrain_desc,
        })
    }
}

/// A terrain blend map.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TerrainAlphaMap {
    /// Which corners or sides it covers.
    pub t_code: u32,
    /// Texture id.
    pub texture_id: u32,
}

impl TerrainAlphaMap {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            t_code: r.u32()?,
            texture_id: r.u32()?,
        })
    }
}

/// A road blend map.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RoadAlphaMap {
    /// Which road edges it covers.
    pub r_code: u32,
    /// Texture id.
    pub texture_id: u32,
}

impl RoadAlphaMap {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            r_code: r.u32()?,
            texture_id: r.u32()?,
        })
    }
}

/// The texture of one terrain type.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TmTerrainDesc {
    /// Terrain type.
    pub terrain_type: i32,
    /// Texture.
    pub terrain_tex: TerrainTex,
}

impl TmTerrainDesc {
    /// The terrain-type value the region's texture list gives the road
    /// texture, laid where a vertex's road bits are set; the landscape's
    /// own terrain types are 0 to 31.
    pub const ROAD_TERRAIN_TYPE: i32 = 0x20;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            terrain_type: r.i32()?,
            terrain_tex: TerrainTex::unpack(r)?,
        })
    }
}

/// A terrain texture and its tiling and vertex colour ranges.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TerrainTex {
    /// Texture id.
    pub texture_id: u32,
    /// Tiling.
    pub tex_tiling: u32,
    /// Vertex brightness range, maximum.
    pub max_vert_bright: u32,
    /// Vertex brightness range, minimum.
    pub min_vert_bright: u32,
    /// Vertex saturation range, maximum.
    pub max_vert_saturate: u32,
    /// Vertex saturation range, minimum.
    pub min_vert_saturate: u32,
    /// Vertex hue range, maximum.
    pub max_vert_hue: u32,
    /// Vertex hue range, minimum.
    pub min_vert_hue: u32,
    /// Detail texture tiling.
    pub detail_tex_tiling: u32,
    /// Detail texture id.
    pub detail_texture_id: u32,
}

impl TerrainTex {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            texture_id: r.u32()?,
            tex_tiling: r.u32()?,
            max_vert_bright: r.u32()?,
            min_vert_bright: r.u32()?,
            max_vert_saturate: r.u32()?,
            min_vert_saturate: r.u32()?,
            max_vert_hue: r.u32()?,
            min_vert_hue: r.u32()?,
            detail_tex_tiling: r.u32()?,
            detail_texture_id: r.u32()?,
        })
    }
}

/// Map and test ids.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RegionMisc {
    /// Version.
    pub version: u32,
    /// World map image id.
    pub game_map_id: u32,
    /// Test map image id.
    pub autotest_map_id: u32,
    /// Test map size.
    pub autotest_map_size: u32,
    /// Cell id used to clear the test map.
    pub clear_cell_id: u32,
    /// Monster id used to clear the test map.
    pub clear_monster_id: u32,
}

impl RegionMisc {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            version: r.u32()?,
            game_map_id: r.u32()?,
            autotest_map_id: r.u32()?,
            autotest_map_size: r.u32()?,
            clear_cell_id: r.u32()?,
            clear_monster_id: r.u32()?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn legacy(w: &mut Writer, text: &str) {
        w.u16(text.len() as u16);
        w.bytes(text.as_bytes());
        w.align(4);
    }

    fn color(w: &mut Writer) {
        w.bytes(&[0x28, 0x17, 0x17, 0xFF]);
    }

    fn sample(mask: u32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x1300_0000);
        w.u32(1);
        w.u32(3);
        legacy(&mut w, "Dereth");
        // Land defs.
        w.i32(255);
        w.i32(255);
        w.f32(24.0);
        w.i32(8);
        w.i32(1);
        w.f32(200.0);
        w.f32(1000.0);
        w.f32(5.0);
        for i in 0..256 {
            w.f32(i as f32 * 2.0);
        }
        // Game time.
        w.f64(3600.0);
        w.u32(10);
        w.f32(7620.0);
        w.u32(360);
        legacy(&mut w, "P.Y.");
        w.u32(2);
        w.f32(0.0);
        w.u32(1);
        legacy(&mut w, "Darktide");
        w.f32(0.25);
        w.u32(0);
        legacy(&mut w, "Dawnsong");
        w.u32(1);
        legacy(&mut w, "FirstDay");
        w.u32(1);
        w.u32(30);
        legacy(&mut w, "Solclaim");
        w.u32(mask);
        if mask & PartsMask::HAS_SKY_INFO != 0 {
            w.f64(0.8);
            w.f64(15.0);
            w.u32(1);
            w.f32(5.0);
            legacy(&mut w, "Sunny");
            w.u32(1);
            for v in [0.0f32, 1.0, 10.0, 170.0, 0.0, 0.0] {
                w.f32(v);
            }
            w.u32(0x0100_15EE);
            w.u32(0);
            w.u32(0);
            w.u32(1);
            for v in [0.0f32, 0.25, 90.0, 0.9] {
                w.f32(v);
            }
            color(&mut w);
            w.f32(0.4);
            color(&mut w);
            w.f32(0.0);
            w.f32(400.0);
            color(&mut w);
            w.u32(1);
            w.u32(1);
            w.u32(0);
            w.u32(0x0100_15EF);
            for v in [270.0f32, -1.0, 11.0, 11.0] {
                w.f32(v);
            }
        }
        if mask & PartsMask::HAS_SOUND_INFO != 0 {
            w.u32(1);
            w.u32(0x2000_0017);
            w.u32(1);
            w.u32(70);
            for v in [0.6f32, 0.5, 1.4, 10.0] {
                w.f32(v);
            }
        }
        if mask & PartsMask::HAS_SCENE_INFO != 0 {
            w.u32(1);
            w.u32(8);
            w.u32(2);
            w.u32(0x1200_0076);
            w.u32(0x1200_0077);
        }
        // Terrain: one type, then the texture merge.
        w.u32(1);
        legacy(&mut w, "BarrenRock");
        color(&mut w);
        w.u32(2);
        w.u32(1);
        w.u32(29);
        w.u32(0);
        w.u32(1024);
        w.u32(1);
        w.u32(8);
        w.u32(0x0500_1371);
        w.u32(1);
        w.u32(9);
        w.u32(0x0500_1441);
        w.u32(1);
        w.u32(9);
        w.u32(0x0500_168E);
        w.u32(1);
        w.i32(0);
        for v in [0x0500_145C, 2, 100, 90, 90, 70, 150, 90, 4, 0x0500_1786] {
            w.u32(v);
        }
        if mask & PartsMask::HAS_REGION_MISC != 0 {
            for v in [1, 0x0600_127D, 0x0600_0261, 4, 0x0100_0FDE, 0x0100_1612] {
                w.u32(v);
            }
        }
        w.into_vec()
    }

    #[test]
    fn unpacks_every_part() {
        let bytes = sample(0x21F);
        let r = &mut Reader::new(&bytes);
        let g = Region::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(
            (g.region_number, g.version, g.name.as_str()),
            (1, 3, "Dereth")
        );
        assert_eq!(g.land_defs.square_length, 24.0);
        assert_eq!(g.land_defs.land_height_table[255], 510.0);
        assert_eq!(g.game_time.year_spec, "P.Y.");
        assert_eq!(g.game_time.times_of_day[0].name, "Darktide");
        assert!(g.game_time.times_of_day[0].is_night);
        assert!(!g.game_time.times_of_day[1].is_night);
        assert_eq!(g.game_time.days_of_week, vec!["FirstDay"]);
        assert_eq!(g.game_time.seasons[0].name, "Solclaim");
        let sky = g.sky_info.unwrap();
        assert_eq!(sky.day_groups[0].day_name, "Sunny");
        assert_eq!(
            sky.day_groups[0].sky_objects[0].default_gfx_object_id,
            0x0100_15EE
        );
        let t = &sky.day_groups[0].sky_time[0];
        assert_eq!(t.max_world_fog, 400.0);
        assert_eq!(t.world_fog_color.red, 0x17);
        assert_eq!(t.sky_obj_replace[0].gfx_obj_id, 0x0100_15EF);
        let sound = g.sound_info.unwrap();
        assert_eq!(sound.stb_desc[0].ambient_sounds[0].sound_type, 70);
        let scene = g.scene_info.unwrap();
        assert_eq!(scene.scene_types[0].scenes, vec![0x1200_0076, 0x1200_0077]);
        let terrain = &g.terrain_info;
        assert_eq!(terrain.terrain_types[0].terrain_name, "BarrenRock");
        assert_eq!(terrain.terrain_types[0].scene_types, vec![1, 29]);
        let tm = &terrain.land_surfaces.tex_merge;
        assert_eq!(tm.base_tex_size, 1024);
        assert_eq!(tm.road_maps[0].texture_id, 0x0500_168E);
        assert_eq!(
            tm.terrain_desc[0].terrain_tex.detail_texture_id,
            0x0500_1786
        );
        assert_eq!(g.region_misc.unwrap().clear_monster_id, 0x0100_1612);
    }

    #[test]
    fn optional_parts_follow_the_mask() {
        let bytes = sample(0);
        let r = &mut Reader::new(&bytes);
        let g = Region::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert!(g.sky_info.is_none());
        assert!(g.sound_info.is_none());
        assert!(g.scene_info.is_none());
        assert!(g.region_misc.is_none());
    }

    #[test]
    fn truncated_region_is_an_error_at_every_length() {
        let bytes = sample(0x21F);
        for len in 0..bytes.len() {
            assert!(
                Region::unpack(&mut Reader::new(&bytes[..len])).is_err(),
                "len {len}"
            );
        }
    }

    #[test]
    fn other_versions_are_refused() {
        let mut bytes = sample(0);
        bytes[8] = 2;
        assert_eq!(
            Region::unpack(&mut Reader::new(&bytes)),
            Err(DecodeError::Unsupported("region version"))
        );
    }

    #[test]
    fn palette_shift_land_surface_is_refused() {
        let mut r = Reader::new(&[1, 0, 0, 0, 0, 0, 0, 0]);
        assert_eq!(
            LandSurf::unpack(&mut r),
            Err(DecodeError::Unsupported("palette-shift land surface"))
        );
    }
}
