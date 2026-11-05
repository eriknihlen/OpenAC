//! Character-creation appearance: the face, hair and clothing choices, the
//! shade-to-palette rule, the swatch colors the pickers show, and the
//! composition of the chosen look into one appearance description.
//!
//! Palette sets, clothing tables and palette colors come from the dat
//! through the source traits the runtime implements.

use std::collections::HashMap;
use std::sync::Arc;

use super::options::{
    ChargenAnimPartChange, ChargenGearOption, ChargenObjDesc, ChargenOptions, ChargenSubPalette,
    ChargenTextureChange,
};

/// The human body setup, used when nothing else names one.
pub const HUMAN_SETUP_ID: u32 = 0x0200_0001;
/// "No data id".
const INVALID_DID: u32 = 0xFFFF_FFFF;

// Packed palette ranges (units of 8 colors).
const SKIN_RANGE_OFFSET: u8 = 0;
const SKIN_RANGE_NUM_COLORS: u8 = 24;
const HAIR_RANGE_OFFSET: u8 = 24;
const HAIR_RANGE_NUM_COLORS: u8 = 8;
const EYE_RANGE_OFFSET: u8 = 32;
const EYE_RANGE_NUM_COLORS: u8 = 8;

/// Palette index the hair swatch samples.
pub const HAIR_SAMPLE_INDEX: i32 = 0xD0;
/// Palette index the skin swatch samples.
pub const SKIN_FAMILY_SAMPLE_INDEX: i32 = 0xB0;
/// Palette index the eye swatch samples.
pub const EYE_SAMPLE_INDEX: i32 = 0x103;
/// Palette index the clothing swatch samples.
pub const CLOTHING_SAMPLE_INDEX: i32 = 0x520;

/// The chosen look. Indices pick from the gender's lists; shades (0..=1)
/// pick within a palette set.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ChargenAppearanceSelection {
    /// Eye strip.
    pub eyes_strip: u32,
    /// Nose strip.
    pub nose_strip: u32,
    /// Mouth strip.
    pub mouth_strip: u32,
    /// Hair style.
    pub hair_style: u32,
    /// Hair color.
    pub hair_color: u32,
    /// Eye color.
    pub eye_color: u32,
    /// Headgear.
    pub headgear_style: u32,
    /// Headgear color.
    pub headgear_color: u32,
    /// Shirt.
    pub shirt_style: u32,
    /// Shirt color.
    pub shirt_color: u32,
    /// Trousers.
    pub trousers_style: u32,
    /// Trousers color.
    pub trousers_color: u32,
    /// Footwear.
    pub footwear_style: u32,
    /// Footwear color.
    pub footwear_color: u32,
    /// Skin shade.
    pub skin_shade: f64,
    /// Hair shade.
    pub hair_shade: f64,
    /// Headgear shade.
    pub headgear_shade: f64,
    /// Shirt shade.
    pub shirt_shade: f64,
    /// Trousers shade.
    pub trousers_shade: f64,
    /// Footwear shade.
    pub footwear_shade: f64,
}

impl ChargenAppearanceSelection {
    /// No choice made.
    pub const UNSET: u32 = 0xFFFF_FFFF;
    /// No shade chosen.
    pub const UNSET_SHADE: f64 = -1.0;
}

impl Default for ChargenAppearanceSelection {
    fn default() -> Self {
        Self {
            eyes_strip: Self::UNSET,
            nose_strip: Self::UNSET,
            mouth_strip: Self::UNSET,
            hair_style: Self::UNSET,
            hair_color: Self::UNSET,
            eye_color: Self::UNSET,
            headgear_style: Self::UNSET,
            headgear_color: Self::UNSET,
            shirt_style: Self::UNSET,
            shirt_color: Self::UNSET,
            trousers_style: Self::UNSET,
            trousers_color: Self::UNSET,
            footwear_style: Self::UNSET,
            footwear_color: Self::UNSET,
            skin_shade: Self::UNSET_SHADE,
            hair_shade: Self::UNSET_SHADE,
            headgear_shade: Self::UNSET_SHADE,
            shirt_shade: Self::UNSET_SHADE,
            trousers_shade: Self::UNSET_SHADE,
            footwear_shade: Self::UNSET_SHADE,
        }
    }
}

/// A palette set: the palettes a shade picks from.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenPalSet {
    /// Palette ids.
    pub palette_ids: Vec<u32>,
}

/// A range of palette colors a clothing dye covers (real color offsets).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ChargenClothingSubPaletteRange {
    /// First color.
    pub offset: u32,
    /// Number of colors (2048 means the whole palette).
    pub num_colors: u32,
}

/// One dye of a clothing color template.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenClothingSubPaletteChoice {
    /// The palette set the shade picks from.
    pub pal_set_id: u32,
    /// The ranges it colors.
    pub ranges: Vec<ChargenClothingSubPaletteRange>,
}

/// A clothing color template.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenClothingPaletteTemplate {
    /// Dyes, in order.
    pub choices: Vec<ChargenClothingSubPaletteChoice>,
}

/// What a garment does to one body setup.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenClothingBaseEffect {
    /// Part swaps.
    pub part_changes: Vec<ChargenAnimPartChange>,
    /// Texture swaps.
    pub texture_changes: Vec<ChargenTextureChange>,
}

/// A garment's clothing table.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenClothingTable {
    /// Effects by body setup.
    pub base_effects_by_setup_id: HashMap<u32, ChargenClothingBaseEffect>,
    /// Color templates by id.
    pub palette_templates_by_id: HashMap<u32, ChargenClothingPaletteTemplate>,
}

/// A 24-bit color.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ChargenSwatchRgb {
    /// Red.
    pub r: u8,
    /// Green.
    pub g: u8,
    /// Blue.
    pub b: u8,
}

/// Palette sets by id (from the dat).
pub trait ChargenPalSetSource {
    /// A palette set, `None` when the dat has none with this id.
    fn pal_set(&self, pal_set_id: u32) -> Option<Arc<ChargenPalSet>>;
}

/// Clothing tables by id (from the dat).
pub trait ChargenClothingTableSource {
    /// A clothing table, `None` when the dat has none with this id.
    fn clothing_table(&self, clothing_table_id: u32) -> Option<Arc<ChargenClothingTable>>;
}

/// Palette colors (from the dat).
pub trait ChargenPaletteColorSource {
    /// One color of a palette, `None` for an unknown palette or an index
    /// outside it.
    fn color(&self, palette_id: u32, index: i32) -> Option<ChargenSwatchRgb>;
}

/// The palette a shade picks from `count` palettes: `trunc((count -
/// 0.000001) * shade)`, clamped; -1 for no palettes or a shade outside
/// 0..=1.
#[must_use]
pub fn get_palette_index(count: i32, shade: f64) -> i32 {
    if count <= 0 || !(0.0..=1.0).contains(&shade) {
        return -1;
    }
    let index = ((f64::from(count) - 0.000_001) * shade) as i32;
    index.clamp(0, count - 1)
}

/// The average of one color across a palette set's palettes (a palette
/// that lacks the color counts as black). Black for an empty set; `None`
/// for an unknown set.
#[must_use]
pub fn pal_set_average_color(
    pal_sets: &impl ChargenPalSetSource,
    colors: &impl ChargenPaletteColorSource,
    pal_set_id: u32,
    sample_index: i32,
) -> Option<ChargenSwatchRgb> {
    let pal_set = pal_sets.pal_set(pal_set_id)?;
    if pal_set.palette_ids.is_empty() {
        return Some(ChargenSwatchRgb::default());
    }
    let (mut r, mut g, mut b) = (0usize, 0usize, 0usize);
    for &palette_id in &pal_set.palette_ids {
        if let Some(color) = colors.color(palette_id, sample_index) {
            r += usize::from(color.r);
            g += usize::from(color.g);
            b += usize::from(color.b);
        }
    }
    let count = pal_set.palette_ids.len();
    // An average of bytes is a byte.
    let average = |sum: usize| u8::try_from(sum / count).unwrap_or(u8::MAX);
    Some(ChargenSwatchRgb {
        r: average(r),
        g: average(g),
        b: average(b),
    })
}

/// One color of a palette, read directly (eye colors are palettes, not
/// palette sets).
#[must_use]
pub fn direct_color(
    colors: &impl ChargenPaletteColorSource,
    palette_id: u32,
    sample_index: i32,
) -> Option<ChargenSwatchRgb> {
    colors.color(palette_id, sample_index)
}

/// The palette set a clothing color swatch shows: the template's first
/// dye.
#[must_use]
pub fn clothing_swatch_pal_set_id(
    clothing_tables: &impl ChargenClothingTableSource,
    clothing_table_id: u32,
    palette_template_id: u32,
) -> Option<u32> {
    let table = clothing_tables.clothing_table(clothing_table_id)?;
    let template = table.palette_templates_by_id.get(&palette_template_id)?;
    template.choices.first().map(|choice| choice.pal_set_id)
}

/// The composed look.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChargenAppearanceResult {
    /// Body setup.
    pub setup_id: u32,
    /// Base palette.
    pub base_palette_id: u32,
    /// The appearance description.
    pub obj_desc: ChargenObjDesc,
    /// Palette sets the dat did not have.
    pub missing_pal_set_ids: Vec<u32>,
    /// Clothing tables the dat did not have.
    pub missing_clothing_table_ids: Vec<u32>,
    /// Clothing tables with no effect for the body setup.
    pub clothing_tables_missing_base_effect_for_setup: Vec<u32>,
}

/// Why a look could not be composed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChargenAppearanceError {
    /// The heritage is not in the table.
    UnknownHeritage(u32),
    /// The heritage has no such gender.
    UnknownGender(i32),
    /// A clothing dye range does not fit the packed byte form (a multiple
    /// of 8 up to 2040, or 2048 colors for the whole palette).
    UnpackableClothingRange {
        /// The range's offset.
        offset: u32,
        /// The range's color count.
        num_colors: u32,
    },
}

impl std::fmt::Display for ChargenAppearanceError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::UnknownHeritage(id) => write!(f, "unknown heritage {id}"),
            Self::UnknownGender(key) => write!(f, "unknown gender {key}"),
            Self::UnpackableClothingRange { offset, num_colors } => write!(
                f,
                "clothing palette range (offset {offset}, {num_colors} colors) does not fit the packed form"
            ),
        }
    }
}

impl std::error::Error for ChargenAppearanceError {}

/// Composes the look for a heritage and gender. Parts apply in the
/// game's order: base body, hair style, headgear, trousers, shirt,
/// footwear, eyes (bald variant under a bald style), nose, mouth; then the
/// skin, hair and eye palettes. `alternate_setup_override` (unless
/// `0xFFFFFFFF`) replaces the body setup; a setup of 0 or `0xFFFFFFFF`
/// falls back to the human setup. Missing dat entries are recorded, not
/// errors.
///
/// # Errors
/// [`ChargenAppearanceError`] for an unknown heritage or gender, or a
/// clothing range that does not pack.
pub fn try_compose(
    options: &ChargenOptions,
    heritage_id: u32,
    gender_key: i32,
    selection: &ChargenAppearanceSelection,
    pal_sets: &impl ChargenPalSetSource,
    clothing_tables: &impl ChargenClothingTableSource,
    alternate_setup_override: Option<u32>,
) -> Result<ChargenAppearanceResult, ChargenAppearanceError> {
    let heritage = options
        .try_get_heritage(heritage_id)
        .ok_or(ChargenAppearanceError::UnknownHeritage(heritage_id))?;
    let gender = heritage
        .genders_by_key
        .get(&gender_key)
        .ok_or(ChargenAppearanceError::UnknownGender(gender_key))?;

    let mut result = ChargenAppearanceResult {
        base_palette_id: gender.base_palette_id,
        ..ChargenAppearanceResult::default()
    };

    let mut setup_id = gender.setup_id;
    let hair_style = pick(&gender.hair_styles, selection.hair_style);
    if let Some(style) = hair_style
        && style.alternate_setup != 0
        && style.alternate_setup != INVALID_DID
    {
        setup_id = style.alternate_setup;
    }
    if let Some(alternate) = alternate_setup_override
        && alternate != INVALID_DID
    {
        setup_id = alternate;
    }
    if setup_id == 0 || setup_id == INVALID_DID {
        setup_id = HUMAN_SETUP_ID;
    }
    result.setup_id = setup_id;

    let desc = &mut result.obj_desc;
    desc.palette_id = gender.base_palette_id;
    append(desc, &gender.base_obj_desc);
    if let Some(style) = hair_style {
        append(desc, &style.obj_desc);
    }

    let slots = [
        (
            &gender.headgears,
            selection.headgear_style,
            selection.headgear_color,
            selection.headgear_shade,
        ),
        (
            &gender.pants,
            selection.trousers_style,
            selection.trousers_color,
            selection.trousers_shade,
        ),
        (
            &gender.shirts,
            selection.shirt_style,
            selection.shirt_color,
            selection.shirt_shade,
        ),
        (
            &gender.footwear,
            selection.footwear_style,
            selection.footwear_color,
            selection.footwear_shade,
        ),
    ];
    for (gear, style, color, shade) in slots {
        compose_clothing_slot(
            gear,
            style,
            &gender.clothing_colors,
            color,
            shade,
            setup_id,
            clothing_tables,
            pal_sets,
            &mut result,
        )?;
    }

    let desc = &mut result.obj_desc;
    if let Some(strip) = pick(&gender.eye_strips, selection.eyes_strip) {
        let bald = hair_style.is_some_and(|style| style.bald);
        append(
            desc,
            if bald {
                &strip.bald_obj_desc
            } else {
                &strip.obj_desc
            },
        );
    }
    if let Some(strip) = pick(&gender.nose_strips, selection.nose_strip) {
        append(desc, &strip.obj_desc);
    }
    if let Some(strip) = pick(&gender.mouth_strips, selection.mouth_strip) {
        append(desc, &strip.obj_desc);
    }

    // The skin palette is always looked up; an unset shade adds nothing.
    match pal_sets.pal_set(gender.skin_pal_set_id) {
        None => result.missing_pal_set_ids.push(gender.skin_pal_set_id),
        Some(skin) => {
            if let Some(&palette) = palette_for(&skin, selection.skin_shade) {
                result.obj_desc.sub_palettes.push(ChargenSubPalette {
                    sub_palette_id: palette,
                    offset: SKIN_RANGE_OFFSET,
                    num_colors: SKIN_RANGE_NUM_COLORS,
                });
            }
        }
    }

    if let Some(&hair_pal_set_id) = pick(&gender.hair_colors, selection.hair_color) {
        match pal_sets.pal_set(hair_pal_set_id) {
            None => result.missing_pal_set_ids.push(hair_pal_set_id),
            Some(hair) => {
                if let Some(&palette) = palette_for(&hair, selection.hair_shade) {
                    result.obj_desc.sub_palettes.push(ChargenSubPalette {
                        sub_palette_id: palette,
                        offset: HAIR_RANGE_OFFSET,
                        num_colors: HAIR_RANGE_NUM_COLORS,
                    });
                }
            }
        }
    }

    if let Some(&eye_palette_id) = pick(&gender.eye_colors, selection.eye_color) {
        result.obj_desc.sub_palettes.push(ChargenSubPalette {
            sub_palette_id: eye_palette_id,
            offset: EYE_RANGE_OFFSET,
            num_colors: EYE_RANGE_NUM_COLORS,
        });
    }

    Ok(result)
}

/// The list entry an index picks; nothing for "unset" or out of range.
fn pick<T>(list: &[T], index: u32) -> Option<&T> {
    if index == ChargenAppearanceSelection::UNSET {
        return None;
    }
    usize::try_from(index)
        .ok()
        .and_then(|index| list.get(index))
}

fn palette_for(pal_set: &ChargenPalSet, shade: f64) -> Option<&u32> {
    let count = i32::try_from(pal_set.palette_ids.len()).unwrap_or(i32::MAX);
    let index = usize::try_from(get_palette_index(count, shade)).ok()?;
    pal_set.palette_ids.get(index)
}

fn append(desc: &mut ChargenObjDesc, source: &ChargenObjDesc) {
    desc.sub_palettes.extend_from_slice(&source.sub_palettes);
    desc.texture_changes
        .extend_from_slice(&source.texture_changes);
    desc.anim_part_changes
        .extend_from_slice(&source.anim_part_changes);
}

#[allow(clippy::too_many_arguments)]
fn compose_clothing_slot(
    gear_options: &[ChargenGearOption],
    style_index: u32,
    clothing_colors: &[u32],
    color_index: u32,
    shade: f64,
    body_setup_id: u32,
    clothing_tables: &impl ChargenClothingTableSource,
    pal_sets: &impl ChargenPalSetSource,
    result: &mut ChargenAppearanceResult,
) -> Result<(), ChargenAppearanceError> {
    let Some(gear) = pick(gear_options, style_index) else {
        return Ok(());
    };
    let Some(table) = clothing_tables.clothing_table(gear.clothing_table_id) else {
        result
            .missing_clothing_table_ids
            .push(gear.clothing_table_id);
        return Ok(());
    };

    match table.base_effects_by_setup_id.get(&body_setup_id) {
        Some(effect) => {
            let desc = &mut result.obj_desc;
            desc.anim_part_changes
                .extend_from_slice(&effect.part_changes);
            desc.texture_changes
                .extend_from_slice(&effect.texture_changes);
        }
        None => result
            .clothing_tables_missing_base_effect_for_setup
            .push(gear.clothing_table_id),
    }

    let Some(&template_id) = pick(clothing_colors, color_index) else {
        return Ok(());
    };
    let Some(template) = table.palette_templates_by_id.get(&template_id) else {
        return Ok(());
    };

    for choice in &template.choices {
        let Some(pal_set) = pal_sets.pal_set(choice.pal_set_id) else {
            // A missing palette set ends this garment's dyes.
            result.missing_pal_set_ids.push(choice.pal_set_id);
            break;
        };
        let Some(&palette) = palette_for(&pal_set, shade) else {
            continue;
        };
        for range in &choice.ranges {
            result.obj_desc.sub_palettes.push(ChargenSubPalette {
                sub_palette_id: palette,
                offset: pack_offset(range)?,
                num_colors: pack_num_colors(range)?,
            });
        }
    }
    Ok(())
}

fn unpackable(range: &ChargenClothingSubPaletteRange) -> ChargenAppearanceError {
    ChargenAppearanceError::UnpackableClothingRange {
        offset: range.offset,
        num_colors: range.num_colors,
    }
}

fn pack_offset(range: &ChargenClothingSubPaletteRange) -> Result<u8, ChargenAppearanceError> {
    if !range.offset.is_multiple_of(8) || range.offset > 2040 {
        return Err(unpackable(range));
    }
    u8::try_from(range.offset / 8).map_err(|_| unpackable(range))
}

fn pack_num_colors(range: &ChargenClothingSubPaletteRange) -> Result<u8, ChargenAppearanceError> {
    if range.num_colors == 2048 {
        return Ok(0);
    }
    if !range.num_colors.is_multiple_of(8) || range.num_colors > 2040 {
        return Err(unpackable(range));
    }
    u8::try_from(range.num_colors / 8).map_err(|_| unpackable(range))
}

#[cfg(test)]
#[path = "appearance_tests.rs"]
mod tests;
