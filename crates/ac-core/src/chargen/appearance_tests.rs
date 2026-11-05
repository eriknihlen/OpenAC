//! Golden behavior from OpenAC
//! `tests/AcDream.Core.Tests/CharGen/ChargenAppearanceFactoryTests.cs`,
//! `ChargenSwatchColorResolverTests.cs` and `ChargenPalSetMathTests.cs`.

use std::collections::HashMap;
use std::sync::Arc;

use super::*;
use crate::chargen::options::{
    ChargenEyeStrip, ChargenFaceStrip, ChargenGenderOptions, ChargenHairStyle,
    ChargenHeritageOptions,
};

const HERITAGE_ID: u32 = 1;
const GENDER_KEY: i32 = 1;
const BODY_SETUP_ID: u32 = 0x0200_0001;
const ALTERNATE_BODY_SETUP_ID: u32 = 0x0200_00FF;
const BASE_PALETTE_ID: u32 = 0x0400_0001;
const SKIN_PAL_SET_ID: u32 = 0x0F00_0001;
const HAIR_COLOR_PAL_SET_ID: u32 = 0x0F00_0002;
const EYE_COLOR_PALETTE_ID: u32 = 0x0400_0099;
const DYE_PAL_SET_ID: u32 = 0x0F00_0003;
const HEADGEAR_TABLE: u32 = 0x1900_0001;
const TROUSERS_TABLE: u32 = 0x1900_0002;
const SHIRT_TABLE: u32 = 0x1900_0003;
const FOOTWEAR_TABLE: u32 = 0x1900_0004;

#[derive(Default)]
struct PalSets(HashMap<u32, Arc<ChargenPalSet>>);

impl PalSets {
    fn add(&mut self, id: u32, palette_ids: &[u32]) {
        self.0.insert(
            id,
            Arc::new(ChargenPalSet {
                palette_ids: palette_ids.to_vec(),
            }),
        );
    }
}

impl ChargenPalSetSource for PalSets {
    fn pal_set(&self, pal_set_id: u32) -> Option<Arc<ChargenPalSet>> {
        self.0.get(&pal_set_id).cloned()
    }
}

#[derive(Default)]
struct Clothing(HashMap<u32, Arc<ChargenClothingTable>>);

impl Clothing {
    fn add(&mut self, id: u32, table: ChargenClothingTable) {
        self.0.insert(id, Arc::new(table));
    }
}

impl ChargenClothingTableSource for Clothing {
    fn clothing_table(&self, clothing_table_id: u32) -> Option<Arc<ChargenClothingTable>> {
        self.0.get(&clothing_table_id).cloned()
    }
}

#[derive(Default)]
struct Colors(HashMap<(u32, i32), ChargenSwatchRgb>);

impl Colors {
    fn add(&mut self, palette_id: u32, index: i32, r: u8, g: u8, b: u8) {
        self.0
            .insert((palette_id, index), ChargenSwatchRgb { r, g, b });
    }
}

impl ChargenPaletteColorSource for Colors {
    fn color(&self, palette_id: u32, index: i32) -> Option<ChargenSwatchRgb> {
        self.0.get(&(palette_id, index)).copied()
    }
}

fn obj_desc(tag: u32) -> ChargenObjDesc {
    let part = u8::try_from(tag).expect("small tag");
    ChargenObjDesc {
        palette_id: 0,
        sub_palettes: Vec::new(),
        texture_changes: vec![ChargenTextureChange {
            part_index: part,
            old_texture_id: 0x0500_0000 + tag,
            new_texture_id: 0x0500_1000 + tag,
        }],
        anim_part_changes: vec![ChargenAnimPartChange {
            part_index: part,
            part_id: 0x0100_0000 + tag,
        }],
    }
}

fn gear(name: &str, table: u32, weenie: u32) -> Vec<ChargenGearOption> {
    vec![ChargenGearOption {
        name: name.to_owned(),
        clothing_table_id: table,
        weenie_default_id: weenie,
    }]
}

fn gender(alternate_hair_setup: u32, bald: bool) -> ChargenGenderOptions {
    ChargenGenderOptions {
        gender_key: GENDER_KEY,
        name: "Male".to_owned(),
        scale: 100,
        setup_id: BODY_SETUP_ID,
        base_palette_id: BASE_PALETTE_ID,
        skin_pal_set_id: SKIN_PAL_SET_ID,
        base_obj_desc: obj_desc(0),
        hair_colors: vec![HAIR_COLOR_PAL_SET_ID],
        hair_styles: vec![ChargenHairStyle {
            icon_id: 0x0600_0002,
            bald,
            alternate_setup: alternate_hair_setup,
            obj_desc: obj_desc(1),
        }],
        eye_colors: vec![EYE_COLOR_PALETTE_ID],
        eye_strips: vec![ChargenEyeStrip {
            icon_id: 0x0600_0003,
            bald_icon_id: 0x0600_0004,
            obj_desc: obj_desc(2),
            bald_obj_desc: obj_desc(20),
        }],
        nose_strips: vec![ChargenFaceStrip {
            icon_id: 0x0600_0005,
            obj_desc: obj_desc(3),
        }],
        mouth_strips: vec![ChargenFaceStrip {
            icon_id: 0x0600_0006,
            obj_desc: obj_desc(4),
        }],
        headgears: gear("Cap", HEADGEAR_TABLE, 0x3000_0001),
        shirts: gear("Shirt", SHIRT_TABLE, 0x3000_0002),
        pants: gear("Pants", TROUSERS_TABLE, 0x3000_0003),
        footwear: gear("Boots", FOOTWEAR_TABLE, 0x3000_0004),
        clothing_colors: vec![7],
        ..ChargenGenderOptions::default()
    }
}

fn options(gender: ChargenGenderOptions) -> ChargenOptions {
    let heritage = ChargenHeritageOptions {
        heritage_id: HERITAGE_ID,
        name: "Test".to_owned(),
        setup_id: BODY_SETUP_ID,
        environment_setup_id: BODY_SETUP_ID,
        attribute_credits: 180,
        skill_credits: 100,
        primary_start_area_indices: vec![0],
        genders_by_key: HashMap::from([(GENDER_KEY, gender)]),
        ..ChargenHeritageOptions::default()
    };
    ChargenOptions {
        heritages_by_id: HashMap::from([(HERITAGE_ID, heritage)]),
        ..ChargenOptions::default()
    }
}

fn clothing_table(table_id: u32, pal_set_id: u32, body_setup_id: u32) -> ChargenClothingTable {
    ChargenClothingTable {
        base_effects_by_setup_id: HashMap::from([(
            body_setup_id,
            ChargenClothingBaseEffect {
                part_changes: vec![ChargenAnimPartChange {
                    part_index: 5,
                    part_id: 0x0100_5000 + table_id,
                }],
                texture_changes: vec![ChargenTextureChange {
                    part_index: 5,
                    old_texture_id: 0x0500_5000,
                    new_texture_id: 0x0500_6000,
                }],
            },
        )]),
        palette_templates_by_id: HashMap::from([(
            7,
            ChargenClothingPaletteTemplate {
                choices: vec![ChargenClothingSubPaletteChoice {
                    pal_set_id,
                    ranges: vec![ChargenClothingSubPaletteRange {
                        offset: 80,
                        num_colors: 16,
                    }],
                }],
            },
        )]),
    }
}

fn sources(body_setup_id: u32) -> (PalSets, Clothing) {
    let mut pal = PalSets::default();
    pal.add(SKIN_PAL_SET_ID, &[0x0400_0010, 0x0400_0011, 0x0400_0012]);
    pal.add(HAIR_COLOR_PAL_SET_ID, &[0x0400_0020, 0x0400_0021]);
    pal.add(DYE_PAL_SET_ID, &[0x0400_0030, 0x0400_0031]);
    let mut clothing = Clothing::default();
    for table in [HEADGEAR_TABLE, TROUSERS_TABLE, SHIRT_TABLE, FOOTWEAR_TABLE] {
        clothing.add(table, clothing_table(table, DYE_PAL_SET_ID, body_setup_id));
    }
    (pal, clothing)
}

fn compose(
    options: &ChargenOptions,
    selection: &ChargenAppearanceSelection,
    pal: &PalSets,
    clothing: &Clothing,
) -> ChargenAppearanceResult {
    try_compose(
        options,
        HERITAGE_ID,
        GENDER_KEY,
        selection,
        pal,
        clothing,
        None,
    )
    .expect("composes")
}

fn unset() -> ChargenAppearanceSelection {
    ChargenAppearanceSelection::default()
}

#[test]
fn unknown_heritage_or_gender_fails() {
    let o = options(gender(0, false));
    let (pal, clothing) = sources(BODY_SETUP_ID);
    assert_eq!(
        try_compose(&o, 999, GENDER_KEY, &unset(), &pal, &clothing, None),
        Err(ChargenAppearanceError::UnknownHeritage(999))
    );
    assert_eq!(
        try_compose(&o, HERITAGE_ID, 999, &unset(), &pal, &clothing, None),
        Err(ChargenAppearanceError::UnknownGender(999))
    );
}

#[test]
fn default_selection_resolves_body_and_skips_unset_skin_shade() {
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let result = compose(&options(gender(0, false)), &unset(), &pal, &clothing);
    assert_eq!(result.setup_id, BODY_SETUP_ID);
    assert_eq!(result.base_palette_id, BASE_PALETTE_ID);
    assert!(result.missing_pal_set_ids.is_empty());
    assert!(result.missing_clothing_table_ids.is_empty());
    assert!(!result.obj_desc.sub_palettes.iter().any(|sp| sp.offset == 0));
    assert!(
        result
            .obj_desc
            .anim_part_changes
            .iter()
            .any(|c| c.part_index == 0)
    );
}

fn single(result: &ChargenAppearanceResult, offset: u8, num_colors: u8) -> ChargenSubPalette {
    let matches: Vec<_> = result
        .obj_desc
        .sub_palettes
        .iter()
        .filter(|sp| sp.offset == offset && sp.num_colors == num_colors)
        .copied()
        .collect();
    assert_eq!(
        matches.len(),
        1,
        "one sub-palette at ({offset}, {num_colors})"
    );
    matches[0]
}

#[test]
fn skin_shade_emits_skin_sub_palette() {
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let selection = ChargenAppearanceSelection {
        skin_shade: 0.5,
        ..unset()
    };
    let result = compose(&options(gender(0, false)), &selection, &pal, &clothing);
    assert_eq!(single(&result, 0, 24).sub_palette_id, 0x0400_0011);
}

#[test]
fn hair_color_emits_hair_sub_palette() {
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let selection = ChargenAppearanceSelection {
        hair_color: 0,
        hair_shade: 1.0,
        ..unset()
    };
    let result = compose(&options(gender(0, false)), &selection, &pal, &clothing);
    assert_eq!(single(&result, 24, 8).sub_palette_id, 0x0400_0021);
}

#[test]
fn eye_color_uses_the_palette_directly() {
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let selection = ChargenAppearanceSelection {
        eye_color: 0,
        ..unset()
    };
    let result = compose(&options(gender(0, false)), &selection, &pal, &clothing);
    assert_eq!(single(&result, 32, 8).sub_palette_id, EYE_COLOR_PALETTE_ID);
}

#[test]
fn hair_style_appends_after_base() {
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let selection = ChargenAppearanceSelection {
        hair_style: 0,
        ..unset()
    };
    let result = compose(&options(gender(0, false)), &selection, &pal, &clothing);
    assert_eq!(result.obj_desc.anim_part_changes[0].part_index, 0);
    assert!(
        result
            .obj_desc
            .anim_part_changes
            .iter()
            .any(|c| c.part_index == 1)
    );
}

#[test]
fn setup_resolution() {
    let hair = ChargenAppearanceSelection {
        hair_style: 0,
        ..unset()
    };

    // A hair style's alternate setup replaces the body.
    let (pal, clothing) = sources(ALTERNATE_BODY_SETUP_ID);
    let r = compose(
        &options(gender(ALTERNATE_BODY_SETUP_ID, false)),
        &hair,
        &pal,
        &clothing,
    );
    assert_eq!(r.setup_id, ALTERNATE_BODY_SETUP_ID);

    // Both sources zero: the human setup.
    let (pal, clothing) = sources(0);
    let zero = ChargenGenderOptions {
        setup_id: 0,
        ..gender(0, false)
    };
    assert_eq!(
        compose(&options(zero), &unset(), &pal, &clothing).setup_id,
        HUMAN_SETUP_ID
    );

    // An invalid alternate setup is ignored.
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let r = compose(&options(gender(0xFFFF_FFFF, false)), &hair, &pal, &clothing);
    assert_eq!(r.setup_id, BODY_SETUP_ID);

    // An invalid gender setup falls back to the human setup.
    let (pal, clothing) = sources(0xFFFF_FFFF);
    let invalid = ChargenGenderOptions {
        setup_id: 0xFFFF_FFFF,
        ..gender(0, false)
    };
    assert_eq!(
        compose(&options(invalid), &unset(), &pal, &clothing).setup_id,
        HUMAN_SETUP_ID
    );
}

#[test]
fn override_wins_unless_invalid() {
    let hair = ChargenAppearanceSelection {
        hair_style: 0,
        ..unset()
    };
    let (pal, clothing) = sources(0x0200_00BB);
    let r = try_compose(
        &options(gender(0x0200_00AA, false)),
        HERITAGE_ID,
        GENDER_KEY,
        &hair,
        &pal,
        &clothing,
        Some(0x0200_00BB),
    )
    .expect("composes");
    assert_eq!(r.setup_id, 0x0200_00BB);

    let (pal, clothing) = sources(0x0200_00CC);
    let r = try_compose(
        &options(gender(0, false)),
        HERITAGE_ID,
        GENDER_KEY,
        &unset(),
        &pal,
        &clothing,
        Some(0x0200_00CC),
    )
    .expect("composes");
    assert_eq!(r.setup_id, 0x0200_00CC);

    let (pal, clothing) = sources(BODY_SETUP_ID);
    let r = try_compose(
        &options(gender(0, false)),
        HERITAGE_ID,
        GENDER_KEY,
        &unset(),
        &pal,
        &clothing,
        Some(0xFFFF_FFFF),
    )
    .expect("composes");
    assert_eq!(r.setup_id, BODY_SETUP_ID);
}

#[test]
fn eye_strip_uses_bald_variant_under_bald_hair() {
    let selection = ChargenAppearanceSelection {
        hair_style: 0,
        eyes_strip: 0,
        ..unset()
    };
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let parts = |bald| {
        compose(&options(gender(0, bald)), &selection, &pal, &clothing)
            .obj_desc
            .anim_part_changes
            .iter()
            .map(|c| c.part_id)
            .collect::<Vec<_>>()
    };
    let normal = parts(false);
    assert!(normal.contains(&0x0100_0002));
    assert!(!normal.contains(&0x0100_0014));
    let bald = parts(true);
    assert!(bald.contains(&0x0100_0014));
    assert!(!bald.contains(&0x0100_0002));
}

#[test]
fn nose_and_mouth_append() {
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let selection = ChargenAppearanceSelection {
        nose_strip: 0,
        mouth_strip: 0,
        ..unset()
    };
    let r = compose(&options(gender(0, false)), &selection, &pal, &clothing);
    assert!(
        r.obj_desc
            .anim_part_changes
            .iter()
            .any(|c| c.part_index == 3)
    );
    assert!(
        r.obj_desc
            .anim_part_changes
            .iter()
            .any(|c| c.part_index == 4)
    );
}

#[test]
fn clothing_order_is_headgear_trousers_shirt_footwear() {
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let selection = ChargenAppearanceSelection {
        headgear_style: 0,
        trousers_style: 0,
        shirt_style: 0,
        footwear_style: 0,
        ..unset()
    };
    let r = compose(&options(gender(0, false)), &selection, &pal, &clothing);
    let ids: Vec<u32> = r
        .obj_desc
        .anim_part_changes
        .iter()
        .map(|c| c.part_id)
        .collect();
    assert_eq!(
        ids,
        [
            0x0100_0000,
            0x0100_5000 + HEADGEAR_TABLE,
            0x0100_5000 + TROUSERS_TABLE,
            0x0100_5000 + SHIRT_TABLE,
            0x0100_5000 + FOOTWEAR_TABLE,
        ]
    );
}

#[test]
fn clothing_with_color_emits_part_texture_and_dye() {
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let selection = ChargenAppearanceSelection {
        headgear_style: 0,
        headgear_color: 0,
        headgear_shade: 0.0,
        ..unset()
    };
    let r = compose(&options(gender(0, false)), &selection, &pal, &clothing);
    assert!(
        r.obj_desc
            .anim_part_changes
            .iter()
            .any(|c| c.part_id == 0x0100_5000 + HEADGEAR_TABLE)
    );
    assert!(
        r.obj_desc
            .texture_changes
            .iter()
            .any(|c| c.part_index == 5 && c.new_texture_id == 0x0500_6000)
    );
    // Range (80, 16) packs to (10, 2).
    assert!(
        r.obj_desc
            .sub_palettes
            .iter()
            .any(|sp| sp.offset == 10 && sp.num_colors == 2)
    );
}

#[test]
fn clothing_without_color_skips_dye() {
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let selection = ChargenAppearanceSelection {
        headgear_style: 0,
        ..unset()
    };
    let r = compose(&options(gender(0, false)), &selection, &pal, &clothing);
    assert!(
        r.obj_desc
            .anim_part_changes
            .iter()
            .any(|c| c.part_id == 0x0100_5000 + HEADGEAR_TABLE)
    );
    assert!(
        !r.obj_desc
            .sub_palettes
            .iter()
            .any(|sp| sp.offset == 10 && sp.num_colors == 2)
    );
}

fn single_dye_table(choices: Vec<ChargenClothingSubPaletteChoice>) -> ChargenClothingTable {
    ChargenClothingTable {
        base_effects_by_setup_id: HashMap::from([(
            BODY_SETUP_ID,
            ChargenClothingBaseEffect::default(),
        )]),
        palette_templates_by_id: HashMap::from([(7, ChargenClothingPaletteTemplate { choices })]),
    }
}

fn dyed_headgear() -> ChargenAppearanceSelection {
    ChargenAppearanceSelection {
        headgear_style: 0,
        headgear_color: 0,
        headgear_shade: 0.0,
        ..unset()
    }
}

#[test]
fn missing_pal_set_ends_the_garments_dyes() {
    let missing = 0x0F00_00AA;
    let present = 0x0F00_00BB;
    let (mut pal, mut clothing) = sources(BODY_SETUP_ID);
    clothing.add(
        HEADGEAR_TABLE,
        single_dye_table(vec![
            ChargenClothingSubPaletteChoice {
                pal_set_id: missing,
                ranges: vec![ChargenClothingSubPaletteRange {
                    offset: 80,
                    num_colors: 16,
                }],
            },
            ChargenClothingSubPaletteChoice {
                pal_set_id: present,
                ranges: vec![ChargenClothingSubPaletteRange {
                    offset: 160,
                    num_colors: 8,
                }],
            },
        ]),
    );
    pal.add(present, &[0x0400_0055]);
    let r = compose(
        &options(gender(0, false)),
        &dyed_headgear(),
        &pal,
        &clothing,
    );
    assert!(r.missing_pal_set_ids.contains(&missing));
    assert!(
        !r.obj_desc
            .sub_palettes
            .iter()
            .any(|sp| sp.offset == 20 && sp.num_colors == 1)
    );
    assert!(
        !r.obj_desc
            .sub_palettes
            .iter()
            .any(|sp| sp.offset == 10 && sp.num_colors == 2)
    );
}

#[test]
fn whole_palette_range_packs_to_zero() {
    let (pal, mut clothing) = sources(BODY_SETUP_ID);
    clothing.add(
        HEADGEAR_TABLE,
        single_dye_table(vec![ChargenClothingSubPaletteChoice {
            pal_set_id: DYE_PAL_SET_ID,
            ranges: vec![ChargenClothingSubPaletteRange {
                offset: 0,
                num_colors: 2048,
            }],
        }]),
    );
    let r = compose(
        &options(gender(0, false)),
        &dyed_headgear(),
        &pal,
        &clothing,
    );
    assert!(
        r.obj_desc
            .sub_palettes
            .iter()
            .any(|sp| sp.offset == 0 && sp.num_colors == 0)
    );
}

#[test]
fn unpackable_range_is_an_error() {
    let (pal, mut clothing) = sources(BODY_SETUP_ID);
    clothing.add(
        HEADGEAR_TABLE,
        single_dye_table(vec![ChargenClothingSubPaletteChoice {
            pal_set_id: DYE_PAL_SET_ID,
            ranges: vec![ChargenClothingSubPaletteRange {
                offset: 0,
                num_colors: 2041,
            }],
        }]),
    );
    assert_eq!(
        try_compose(
            &options(gender(0, false)),
            HERITAGE_ID,
            GENDER_KEY,
            &dyed_headgear(),
            &pal,
            &clothing,
            None
        ),
        Err(ChargenAppearanceError::UnpackableClothingRange {
            offset: 0,
            num_colors: 2041
        })
    );
}

#[test]
fn unknown_clothing_table_is_recorded_and_skipped() {
    let g = ChargenGenderOptions {
        headgears: gear("Missing", 0x1900_00FF, 0x3000_0099),
        ..gender(0, false)
    };
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let selection = ChargenAppearanceSelection {
        headgear_style: 0,
        ..unset()
    };
    let r = compose(&options(g), &selection, &pal, &clothing);
    assert!(r.missing_clothing_table_ids.contains(&0x1900_00FF));
    assert!(
        !r.obj_desc
            .anim_part_changes
            .iter()
            .any(|c| c.part_index == 5)
    );
}

#[test]
fn unknown_hair_color_pal_set_is_recorded_and_skipped() {
    let g = ChargenGenderOptions {
        hair_colors: vec![0x0F00_00FF],
        ..gender(0, false)
    };
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let selection = ChargenAppearanceSelection {
        hair_color: 0,
        hair_shade: 0.5,
        ..unset()
    };
    let r = compose(&options(g), &selection, &pal, &clothing);
    assert!(r.missing_pal_set_ids.contains(&0x0F00_00FF));
    assert!(!r.obj_desc.sub_palettes.iter().any(|sp| sp.offset == 24));
}

#[test]
fn body_setup_absent_from_clothing_is_recorded() {
    let (pal, clothing) = sources(0x0200_DEAD);
    let selection = ChargenAppearanceSelection {
        headgear_style: 0,
        ..unset()
    };
    let r = compose(&options(gender(0, false)), &selection, &pal, &clothing);
    assert!(
        r.clothing_tables_missing_base_effect_for_setup
            .contains(&HEADGEAR_TABLE)
    );
    assert!(
        !r.obj_desc
            .anim_part_changes
            .iter()
            .any(|c| c.part_index == 5)
    );
}

#[test]
fn out_of_range_index_is_unselected() {
    let (pal, clothing) = sources(BODY_SETUP_ID);
    let selection = ChargenAppearanceSelection {
        hair_style: 999,
        eyes_strip: 999,
        ..unset()
    };
    let r = compose(&options(gender(0, false)), &selection, &pal, &clothing);
    assert!(
        !r.obj_desc
            .anim_part_changes
            .iter()
            .any(|c| c.part_index == 1)
    );
    assert!(
        !r.obj_desc
            .anim_part_changes
            .iter()
            .any(|c| c.part_index == 2)
    );
}

// Palette-set shade math.

#[test]
fn palette_index_formula() {
    for (count, shade, expected) in [
        (5, 0.0, 0),
        (5, 1.0, 4),
        (5, 0.5, 2),
        (1, 0.0, 0),
        (1, 1.0, 0),
    ] {
        assert_eq!(get_palette_index(count, shade), expected);
    }
}

#[test]
fn palette_index_rejects_bad_input() {
    assert_eq!(get_palette_index(0, 0.5), -1);
    assert_eq!(get_palette_index(-1, 0.5), -1);
    assert_eq!(get_palette_index(5, -0.0001), -1);
    assert_eq!(get_palette_index(5, 1.0001), -1);
    assert_eq!(
        get_palette_index(5, ChargenAppearanceSelection::UNSET_SHADE),
        -1
    );
}

#[test]
fn palette_index_tops_out_at_count_minus_one() {
    for count in 1..=64 {
        assert_eq!(get_palette_index(count, 1.0), count - 1);
    }
}

#[test]
fn palette_index_is_monotonic() {
    let mut previous = -1;
    let mut shade = 0.0;
    while shade <= 1.0 {
        let index = get_palette_index(13, shade);
        assert!(index >= previous, "regressed at {shade}");
        previous = index;
        shade += 0.01;
    }
}

// Swatch colors.

#[test]
fn average_color_over_the_set() {
    let mut pal = PalSets::default();
    pal.add(0x0F00_0001, &[0x0400_0001, 0x0400_0002]);
    let mut colors = Colors::default();
    colors.add(0x0400_0001, 0xD0, 100, 0, 0);
    colors.add(0x0400_0002, 0xD0, 200, 0, 0);
    assert_eq!(
        pal_set_average_color(&pal, &colors, 0x0F00_0001, HAIR_SAMPLE_INDEX),
        Some(ChargenSwatchRgb { r: 150, g: 0, b: 0 })
    );
}

#[test]
fn missing_palette_counts_as_black() {
    let mut pal = PalSets::default();
    pal.add(0x0F00_0002, &[0x0400_0010, 0x0400_0011]);
    let mut colors = Colors::default();
    colors.add(0x0400_0010, 0xD0, 200, 100, 50);
    assert_eq!(
        pal_set_average_color(&pal, &colors, 0x0F00_0002, HAIR_SAMPLE_INDEX),
        Some(ChargenSwatchRgb {
            r: 100,
            g: 50,
            b: 25
        })
    );
}

#[test]
fn empty_set_is_black_and_unknown_set_is_none() {
    let mut pal = PalSets::default();
    pal.add(0x0F00_0003, &[]);
    let colors = Colors::default();
    assert_eq!(
        pal_set_average_color(&pal, &colors, 0x0F00_0003, HAIR_SAMPLE_INDEX),
        Some(ChargenSwatchRgb::default())
    );
    assert_eq!(
        pal_set_average_color(&pal, &colors, 0x0F00_DEAD, HAIR_SAMPLE_INDEX),
        None
    );
}

#[test]
fn direct_color_reads_the_palette() {
    let mut colors = Colors::default();
    colors.add(0x0400_0099, EYE_SAMPLE_INDEX, 15, 63, 93);
    assert_eq!(
        direct_color(&colors, 0x0400_0099, EYE_SAMPLE_INDEX),
        Some(ChargenSwatchRgb {
            r: 15,
            g: 63,
            b: 93
        })
    );
    assert_eq!(direct_color(&colors, 0x0400_DEAD, EYE_SAMPLE_INDEX), None);
}

fn two_choice_table(template_id: u32, first: u32, second: u32) -> ChargenClothingTable {
    ChargenClothingTable {
        base_effects_by_setup_id: HashMap::new(),
        palette_templates_by_id: HashMap::from([(
            template_id,
            ChargenClothingPaletteTemplate {
                choices: vec![
                    ChargenClothingSubPaletteChoice {
                        pal_set_id: first,
                        ranges: vec![ChargenClothingSubPaletteRange {
                            offset: 0,
                            num_colors: 8,
                        }],
                    },
                    ChargenClothingSubPaletteChoice {
                        pal_set_id: second,
                        ranges: vec![ChargenClothingSubPaletteRange {
                            offset: 8,
                            num_colors: 8,
                        }],
                    },
                ],
            },
        )]),
    }
}

#[test]
fn clothing_swatch_uses_the_first_choice() {
    let mut clothing = Clothing::default();
    clothing.add(0x1900_0001, two_choice_table(9, 0x0F00_0010, 0x0F00_0011));
    assert_eq!(
        clothing_swatch_pal_set_id(&clothing, 0x1900_0001, 9),
        Some(0x0F00_0010)
    );
    assert_eq!(clothing_swatch_pal_set_id(&clothing, 0x1900_DEAD, 9), None);
    assert_eq!(
        clothing_swatch_pal_set_id(&clothing, 0x1900_0001, 999),
        None
    );
}

#[test]
fn clothing_swatch_with_no_choices_is_none() {
    let mut clothing = Clothing::default();
    clothing.add(
        0x1900_0003,
        ChargenClothingTable {
            base_effects_by_setup_id: HashMap::new(),
            palette_templates_by_id: HashMap::from([(
                5,
                ChargenClothingPaletteTemplate::default(),
            )]),
        },
    );
    assert_eq!(clothing_swatch_pal_set_id(&clothing, 0x1900_0003, 5), None);
}

#[test]
fn sample_index_constants() {
    assert_eq!(HAIR_SAMPLE_INDEX, 0xD0);
    assert_eq!(SKIN_FAMILY_SAMPLE_INDEX, 0xB0);
    assert_eq!(EYE_SAMPLE_INDEX, 0x103);
    assert_eq!(CLOTHING_SAMPLE_INDEX, 0x520);
}
