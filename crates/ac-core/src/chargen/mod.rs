//! Character-creation rules: the tables as plain data, attribute points,
//! skill training and credits, starting skill scores and names, and the
//! appearance composition with its swatch colors.
//!
//! The runtime projects the portal dat's character-generation table and
//! skill table into [`ChargenOptions`] and implements the palette-set,
//! clothing-table and palette-color sources over the dat.

mod appearance;
pub mod attributes;
mod options;
mod skills;

pub use appearance::{
    CLOTHING_SAMPLE_INDEX, ChargenAppearanceError, ChargenAppearanceResult,
    ChargenAppearanceSelection, ChargenClothingBaseEffect, ChargenClothingPaletteTemplate,
    ChargenClothingSubPaletteChoice, ChargenClothingSubPaletteRange, ChargenClothingTable,
    ChargenClothingTableSource, ChargenPalSet, ChargenPalSetSource, ChargenPaletteColorSource,
    ChargenSwatchRgb, EYE_SAMPLE_INDEX, HAIR_SAMPLE_INDEX, HUMAN_SETUP_ID,
    SKIN_FAMILY_SAMPLE_INDEX, clothing_swatch_pal_set_id, direct_color, get_palette_index,
    pal_set_average_color, try_compose,
};
pub use attributes::ChargenAttributeValues;
pub use options::{
    ChargenAnimPartChange, ChargenEyeStrip, ChargenFaceStrip, ChargenGearOption,
    ChargenGenderOptions, ChargenHairStyle, ChargenHeritageGroup, ChargenHeritageOptions,
    ChargenObjDesc, ChargenOptions, ChargenPosition, ChargenSkillCost, ChargenSkillDetail,
    ChargenStarterArea, ChargenSubPalette, ChargenTemplate, ChargenTextureChange,
};
pub use skills::{
    ChargenSkillAdvancementClass, ChargenSkillAdvancementSet, SkillIdOutOfRange,
    calculate_chargen_score, chargen_skill_name, chargen_skill_score, compute_spent,
    remaining_skill_credits,
};
