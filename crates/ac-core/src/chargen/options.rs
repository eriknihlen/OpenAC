//! The character-creation tables as plain data: starting areas, heritages
//! with their genders, templates, skill costs and appearance choices, and
//! the skill table's names and formulas. The runtime fills them from the
//! portal dat's character-generation table and skill table.

use std::collections::HashMap;

use super::attributes::ChargenAttributeValues;
use crate::player::SkillFormula;
use crate::wire_enum::wire_enum;

wire_enum! {
    values
    /// Heritage group.
    ChargenHeritageGroup {
        INVALID = 0 => "Invalid",
        ALUVIAN = 1 => "Aluvian",
        GHARUNDIM = 2 => "Gharundim",
        SHO = 3 => "Sho",
        VIAMONTIAN = 4 => "Viamontian",
        SHADOWBOUND = 5 => "Shadowbound",
        GEARKNIGHT = 6 => "Gearknight",
        TUMEROK = 7 => "Tumerok",
        LUGIAN = 8 => "Lugian",
        EMPYREAN = 9 => "Empyrean",
        PENUMBRAEN = 10 => "Penumbraen",
        UNDEAD = 11 => "Undead",
        OLTHOI = 12 => "Olthoi",
        OLTHOI_ACID = 13 => "OlthoiAcid",
    }
}

/// A palette swap over a range of the base palette. Offset and count are
/// in units of 8 colors, as the appearance description packs them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ChargenSubPalette {
    /// The palette swapped in.
    pub sub_palette_id: u32,
    /// Start, in units of 8 colors.
    pub offset: u8,
    /// Length, in units of 8 colors (0 means the whole palette).
    pub num_colors: u8,
}

/// A texture swap on one part.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ChargenTextureChange {
    /// Part index.
    pub part_index: u8,
    /// Texture replaced.
    pub old_texture_id: u32,
    /// Texture used instead.
    pub new_texture_id: u32,
}

/// A model swap on one part.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ChargenAnimPartChange {
    /// Part index.
    pub part_index: u8,
    /// The model used.
    pub part_id: u32,
}

/// An appearance description: base palette plus palette, texture and part
/// swaps, in the order they apply.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenObjDesc {
    /// Base palette.
    pub palette_id: u32,
    /// Palette swaps.
    pub sub_palettes: Vec<ChargenSubPalette>,
    /// Texture swaps.
    pub texture_changes: Vec<ChargenTextureChange>,
    /// Part swaps.
    pub anim_part_changes: Vec<ChargenAnimPartChange>,
}

/// A hair style.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenHairStyle {
    /// Icon.
    pub icon_id: u32,
    /// The style is bald (eyes use their bald variant).
    pub bald: bool,
    /// A body setup the style needs instead of the gender's (0 or
    /// `0xFFFFFFFF` for none).
    pub alternate_setup: u32,
    /// The style's appearance changes.
    pub obj_desc: ChargenObjDesc,
}

/// An eye strip, with its bald variant.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenEyeStrip {
    /// Icon.
    pub icon_id: u32,
    /// Icon of the bald variant.
    pub bald_icon_id: u32,
    /// Appearance changes.
    pub obj_desc: ChargenObjDesc,
    /// Appearance changes with a bald hair style.
    pub bald_obj_desc: ChargenObjDesc,
}

/// A nose or mouth strip.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenFaceStrip {
    /// Icon.
    pub icon_id: u32,
    /// Appearance changes.
    pub obj_desc: ChargenObjDesc,
}

/// A starting garment.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenGearOption {
    /// Name.
    pub name: String,
    /// The garment's clothing table.
    pub clothing_table_id: u32,
    /// The weenie the server creates.
    pub weenie_default_id: u32,
}

/// One gender of a heritage.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenGenderOptions {
    /// Gender key.
    pub gender_key: i32,
    /// Name.
    pub name: String,
    /// Scale (percent).
    pub scale: u32,
    /// Body setup.
    pub setup_id: u32,
    /// Sound table.
    pub sound_table_id: u32,
    /// Icon.
    pub icon_id: u32,
    /// Base palette.
    pub base_palette_id: u32,
    /// Skin palette set.
    pub skin_pal_set_id: u32,
    /// Physics script table.
    pub physics_table_id: u32,
    /// Motion table.
    pub motion_table_id: u32,
    /// Combat table.
    pub combat_table_id: u32,
    /// Base appearance.
    pub base_obj_desc: ChargenObjDesc,
    /// Hair color palette sets.
    pub hair_colors: Vec<u32>,
    /// Hair styles.
    pub hair_styles: Vec<ChargenHairStyle>,
    /// Eye color palettes (used directly, no palette set).
    pub eye_colors: Vec<u32>,
    /// Eye strips.
    pub eye_strips: Vec<ChargenEyeStrip>,
    /// Nose strips.
    pub nose_strips: Vec<ChargenFaceStrip>,
    /// Mouth strips.
    pub mouth_strips: Vec<ChargenFaceStrip>,
    /// Headgear.
    pub headgears: Vec<ChargenGearOption>,
    /// Shirts.
    pub shirts: Vec<ChargenGearOption>,
    /// Trousers.
    pub pants: Vec<ChargenGearOption>,
    /// Footwear.
    pub footwear: Vec<ChargenGearOption>,
    /// Clothing color palette templates.
    pub clothing_colors: Vec<u32>,
}

impl ChargenGenderOptions {
    /// True when the gender offers any face, hair or clothing choice.
    #[must_use]
    pub fn has_any_appearance_options(&self) -> bool {
        !self.hair_styles.is_empty()
            || !self.eye_strips.is_empty()
            || !self.nose_strips.is_empty()
            || !self.mouth_strips.is_empty()
            || !self.headgears.is_empty()
            || !self.shirts.is_empty()
            || !self.pants.is_empty()
            || !self.footwear.is_empty()
    }
}

/// What training and specializing a skill costs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ChargenSkillCost {
    /// Skill id.
    pub skill_id: u32,
    /// Credits to train.
    pub normal_cost: i32,
    /// Credits to specialize.
    pub primary_cost: i32,
}

/// A skill-table entry as character creation shows it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenSkillDetail {
    /// Skill id.
    pub skill_id: u32,
    /// Name.
    pub name: String,
    /// The advancement class at which the skill starts to count.
    pub min_level: u32,
    /// Description.
    pub description: String,
    /// Attribute formula.
    pub formula: SkillFormula,
}

/// A profession template.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenTemplate {
    /// Name.
    pub name: String,
    /// Icon.
    pub icon_id: u32,
    /// Title string.
    pub title_string_id: u32,
    /// Suggested attributes.
    pub attributes: ChargenAttributeValues,
    /// Skills the template trains.
    pub normal_skills: Vec<u32>,
    /// Skills the template specializes.
    pub primary_skills: Vec<u32>,
}

/// A starting location.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ChargenPosition {
    /// Cell.
    pub cell_id: u32,
    /// Position in the cell (meters).
    pub origin: [f32; 3],
    /// Orientation quaternion, `[w, x, y, z]`.
    pub rotation_wxyz: [f32; 4],
}

/// A starting town.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ChargenStarterArea {
    /// Index in the table.
    pub index: i32,
    /// Name.
    pub name: String,
    /// Possible start locations.
    pub locations: Vec<ChargenPosition>,
}

/// One heritage's choices.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenHeritageOptions {
    /// Heritage id.
    pub heritage_id: u32,
    /// Name.
    pub name: String,
    /// Icon.
    pub icon_id: u32,
    /// Setup shown on the heritage page.
    pub setup_id: u32,
    /// Background setup.
    pub environment_setup_id: u32,
    /// Attribute points to spend.
    pub attribute_credits: u32,
    /// Skill credits to spend.
    pub skill_credits: u32,
    /// Starting areas offered first.
    pub primary_start_area_indices: Vec<i32>,
    /// Further starting areas.
    pub secondary_start_area_indices: Vec<i32>,
    /// This heritage's skill costs (falls back to the skill table's).
    pub skill_costs_by_skill_id: HashMap<u32, ChargenSkillCost>,
    /// Profession templates.
    pub templates: Vec<ChargenTemplate>,
    /// Genders by key.
    pub genders_by_key: HashMap<i32, ChargenGenderOptions>,
}

impl ChargenHeritageOptions {
    /// True for the two Olthoi heritages.
    #[must_use]
    pub fn is_olthoi(&self) -> bool {
        matches!(
            ChargenHeritageGroup(self.heritage_id),
            ChargenHeritageGroup::OLTHOI | ChargenHeritageGroup::OLTHOI_ACID
        )
    }
}

/// Every character-creation choice.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ChargenOptions {
    /// Starting towns, by index.
    pub starter_areas: Vec<ChargenStarterArea>,
    /// Heritages by id.
    pub heritages_by_id: HashMap<u32, ChargenHeritageOptions>,
    /// Skill-table costs by skill id.
    pub global_skill_costs_by_skill_id: HashMap<u32, ChargenSkillCost>,
    /// Skill-table entries by skill id.
    pub global_skill_details_by_skill_id: HashMap<u32, ChargenSkillDetail>,
}

impl ChargenOptions {
    /// A heritage.
    #[must_use]
    pub fn try_get_heritage(&self, heritage_id: u32) -> Option<&ChargenHeritageOptions> {
        self.heritages_by_id.get(&heritage_id)
    }

    /// A starting town by index.
    #[must_use]
    pub fn try_get_starter_area(&self, index: i32) -> Option<&ChargenStarterArea> {
        usize::try_from(index)
            .ok()
            .and_then(|index| self.starter_areas.get(index))
    }

    /// A skill-table entry.
    #[must_use]
    pub fn try_get_skill_detail(&self, skill_id: u32) -> Option<&ChargenSkillDetail> {
        self.global_skill_details_by_skill_id.get(&skill_id)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden behavior from OpenAC
    // `tests/AcDream.Core.Tests/CharGen/ChargenOptionsTests.cs`.

    fn heritage(id: u32) -> ChargenHeritageOptions {
        ChargenHeritageOptions {
            heritage_id: id,
            name: "Test".to_owned(),
            attribute_credits: 180,
            skill_credits: 100,
            primary_start_area_indices: vec![0],
            ..ChargenHeritageOptions::default()
        }
    }

    #[test]
    fn empty_has_nothing() {
        let options = ChargenOptions::default();
        assert!(options.starter_areas.is_empty());
        assert!(options.heritages_by_id.is_empty());
        assert!(options.try_get_heritage(1).is_none());
    }

    #[test]
    fn finds_registered_heritage() {
        let options = ChargenOptions {
            heritages_by_id: HashMap::from([(1, heritage(1))]),
            ..ChargenOptions::default()
        };
        assert_eq!(options.try_get_heritage(1), Some(&heritage(1)));
    }

    #[test]
    fn starter_area_by_index() {
        let area = ChargenStarterArea {
            index: 0,
            name: "Holtburg".to_owned(),
            locations: Vec::new(),
        };
        let options = ChargenOptions {
            starter_areas: vec![area.clone()],
            ..ChargenOptions::default()
        };
        assert_eq!(options.try_get_starter_area(0), Some(&area));
        assert_eq!(options.try_get_starter_area(1), None);
        assert_eq!(options.try_get_starter_area(-1), None);
    }

    #[test]
    fn olthoi_only_for_the_two_variants() {
        for (id, expected) in [(1, false), (11, false), (12, true), (13, true)] {
            assert_eq!(heritage(id).is_olthoi(), expected, "heritage {id}");
        }
    }

    #[test]
    fn appearance_options() {
        let mut gender = ChargenGenderOptions::default();
        assert!(!gender.has_any_appearance_options());
        gender.footwear.push(ChargenGearOption::default());
        assert!(gender.has_any_appearance_options());
    }
}
