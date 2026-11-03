//! Typed objects decoded from dat files.

mod animation;
mod bsp;
mod char_gen;
mod chat_pose_table;
mod clothing;
mod combat_table;
mod contract_table;
mod db_properties;
mod enum_id_map;
mod enum_mapper;
mod env_cell;
mod environment;
mod experience_table;
mod font;
mod geometry;
mod gfx_obj;
mod gfx_obj_degrade_info;
mod input_map;
mod iteration;
mod landblock;
mod landblock_info;
mod layout_desc;
mod motion_table;
mod palette;
mod particle_emitter;
mod physics_script;
mod read;
mod region;
mod render_surface;
mod scene;
mod setup;
mod skill_table;
mod sound;
mod spell_table;
mod string_table;
mod surface;
mod taboo_table;
mod ui_property;

pub use enum_mapper::EnumMapper;
pub use experience_table::{EXPERIENCE_TABLE_ID, ExperienceTable};
pub use font::{DEFAULT_FONT_ID, Font, FontCharDesc};
pub use input_map::{
    ACTION_MAP_ID, ActionMap, ActionMapValue, ControlSpecification, DeviceKeyMapEntry,
    GAMEPLAY_MASTER_INPUT_MAP_ID, InputsConflictsValue, MasterInputMap, QualifiedControl,
    SYSTEM_MASTER_INPUT_MAP_ID, UserBindingData,
};
pub use layout_desc::{
    DIRECT_STATE_ID, DrawModeType, ElementDesc, IncorporationFlags, LayoutDesc, Media, MediaDesc,
    StateDesc, UI_STATE_NAMES, ui_state_id, ui_state_name,
};
pub use ui_property::{
    BasePropertyDesc, BasePropertyType, EnumMapperData, MASTER_PROPERTY_ID, MasterProperty,
    Property, PropertyValue, StringInfo,
};

pub use animation::{
    Animation, AnimationFlags, AnimationFrame, AnimationHook, AttackCone, HookData,
};
pub use bsp::{
    BspLeaf, BspNode, BspPortal, BspSplit, BspTag, BspTree, BspTreeKind, Plane, PortalRef,
};
pub use clothing::{
    CloObjectEffect, CloSubPalEffect, CloSubPalette, CloSubPaletteRange, CloTextureEffect,
    ClothingBaseEffect, ClothingTable,
};
pub use geometry::{Polygon, SidesType, Stippling, Uv, Vertex, VertexArray};
pub use gfx_obj::{GfxObj, GfxObjFlags};
pub use gfx_obj_degrade_info::{GfxObjDegradeInfo, GfxObjInfo};
pub use iteration::Iteration;
pub use landblock::{LandBlock, TerrainInfo, VERTEX_COUNT, VERTICES_PER_SIDE};
pub use landblock_info::{BuildingInfo, BuildingPortal, LandBlockInfo, Stab};
pub use palette::{Palette, PaletteSet};
pub use render_surface::{PixelFormat, RenderSurface};
pub use setup::{Setup, SetupFlags};
pub use surface::{Surface, SurfaceBase, SurfaceTexture, SurfaceType, TextureType};

pub use char_gen::{
    AnimationPartChange, CHAR_GEN_ID, CharGen, EyeStripCg, FaceStripCg, GearCg, HairStyleCg,
    HeritageGroupCg, ObjDesc, Position, SexCg, SkillCg, StartingArea, SubPalette, TemplateCg,
    TextureMapChange,
};
pub use chat_pose_table::{CHAT_POSE_TABLE_ID, ChatEmoteData, ChatPoseTable};
pub use combat_table::{CombatManeuver, CombatTable};
pub use contract_table::{CONTRACT_TABLE_ID, Contract, ContractTable};
pub use db_properties::DbProperties;
pub use enum_id_map::{DualEnumIdMap, EnumIdMap};
pub use env_cell::{CellPortal, EnvCell, EnvCellFlags};
pub use environment::{CellStruct, Environment};
pub use motion_table::{AnimData, MotionData, MotionDataFlags, MotionTable};
pub use particle_emitter::ParticleEmitter;
pub use physics_script::{
    PhysicsScript, PhysicsScriptData, PhysicsScriptTable, PhysicsScriptTableData, ScriptAndModData,
};
pub use region::{
    AmbientSoundDesc, AmbientStbDesc, DayGroup, GameTime, LAND_HEIGHT_COUNT, LandDefs, LandSurf,
    PartsMask, Region, RegionMisc, RoadAlphaMap, SceneDesc, SceneType, Season, SkyDesc, SkyObject,
    SkyObjectReplace, SkyTimeOfDay, SoundDesc, TerrainAlphaMap, TerrainDesc, TerrainTex,
    TerrainType, TexMerge, TimeOfDay, TmTerrainDesc,
};
pub use scene::{ObjectDesc, Scene};
pub use skill_table::{SKILL_TABLE_ID, SkillBase, SkillFormula, SkillTable};
pub use sound::{SoundData, SoundEntry, SoundHashData, SoundTable, Wave, WaveFormat};
pub use spell_table::{
    SPELL_COMPONENT_TABLE_ID, SPELL_TABLE_ID, SpellBase, SpellComponentBase, SpellComponentTable,
    SpellSet, SpellTable,
};
pub use string_table::{StringTable, StringTableString};
pub use taboo_table::{CHAT_AUDIENCE, TABOO_TABLE_ID, TabooTable, TabooTableEntry};

use ac_bits::Reader;

use crate::error::DecodeError;
use crate::header::DatFileType;

/// An object stored as one file in a dat.
pub trait DatObject: Sized {
    /// The kind of dat the object lives in. Portal objects may also be
    /// found in the high-resolution dat.
    const DAT: DatFileType;

    /// Decode the object from its file bytes.
    fn unpack(r: &mut Reader<'_>) -> Result<Self, DecodeError>;

    /// Decode the object from its file bytes, taking them: an object that
    /// keeps a long run of its bytes as they are (a texture's pixels) keeps
    /// them in place instead of copying them. Decodes as [`Self::unpack`].
    fn unpack_owned(bytes: Vec<u8>) -> Result<Self, DecodeError> {
        Self::unpack(&mut Reader::new(&bytes))
    }

    /// Record the file id the object was read from. Objects whose bytes
    /// carry an id have it replaced by the directory's id.
    fn set_id(&mut self, _id: u32) {}
}

/// Reject a key already present in a table read from the file.
fn check_unique<K: Copy + PartialEq + Into<i64>, V>(
    table: &'static str,
    entries: &[(K, V)],
    key: K,
) -> Result<(), DecodeError> {
    if entries.iter().any(|(k, _)| *k == key) {
        return Err(DecodeError::DuplicateKey {
            table,
            key: key.into(),
        });
    }
    Ok(())
}
