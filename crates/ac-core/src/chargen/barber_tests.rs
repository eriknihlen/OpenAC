//! The barber's rules over a small made-up gender. The constants (bodies,
//! effects, motion tables, the bald texture) are the original client's
//! barber values; the look's word order is the barber messages'.

use std::collections::HashMap;
use std::sync::Arc;

use super::*;
use crate::chargen::{
    ChargenAnimPartChange, ChargenEyeStrip, ChargenHairStyle, ChargenObjDesc, ChargenTextureChange,
};

const SKIN_SET: u32 = 0x0F00_0001;
const HAIR_SET_A: u32 = 0x0F00_0010;
const HAIR_SET_B: u32 = 0x0F00_0011;
const HEAD_A: u32 = 0x0100_1001;
const HEAD_B: u32 = 0x0100_1002;
const GENDER_SETUP: u32 = 0x0200_0001;

struct Sets(HashMap<u32, Arc<ChargenPalSet>>);

impl ChargenPalSetSource for Sets {
    fn pal_set(&self, id: u32) -> Option<Arc<ChargenPalSet>> {
        self.0.get(&id).cloned()
    }
}

fn sets() -> Sets {
    let set = |ids: &[u32]| {
        Arc::new(ChargenPalSet {
            palette_ids: ids.to_vec(),
        })
    };
    Sets(HashMap::from([
        (
            SKIN_SET,
            set(&[0x0400_0101, 0x0400_0102, 0x0400_0103, 0x0400_0104]),
        ),
        (HAIR_SET_A, set(&[0x0400_0201, 0x0400_0202])),
        (HAIR_SET_B, set(&[0x0400_0301, 0x0400_0302, 0x0400_0303])),
    ]))
}

fn tex(new: u32, old: u32) -> ChargenTextureChange {
    ChargenTextureChange {
        part_index: 16,
        old_texture_id: old,
        new_texture_id: new,
    }
}

fn desc(head: Option<u32>, textures: &[ChargenTextureChange]) -> ChargenObjDesc {
    ChargenObjDesc {
        anim_part_changes: head
            .map(|part_id| ChargenAnimPartChange {
                part_index: 16,
                part_id,
            })
            .into_iter()
            .collect(),
        texture_changes: textures.to_vec(),
        ..ChargenObjDesc::default()
    }
}

fn style(head: u32, bald: bool, hair: u32, alternate_setup: u32) -> ChargenHairStyle {
    ChargenHairStyle {
        bald,
        alternate_setup,
        obj_desc: desc(Some(head), &[tex(hair, 0x0500_0F00)]),
        ..ChargenHairStyle::default()
    }
}

fn eye(n: u32) -> ChargenEyeStrip {
    ChargenEyeStrip {
        obj_desc: desc(
            None,
            &[
                tex(0x0500_E000 + n, 0x0500_EEEE),
                tex(0x0500_E100 + n, 0x0500_EEEF),
            ],
        ),
        bald_obj_desc: desc(None, &[tex(0x0500_B000 + n, 0x0500_BBBB)]),
        ..ChargenEyeStrip::default()
    }
}

fn face(base: u32, n: u32) -> ChargenFaceStrip {
    ChargenFaceStrip {
        obj_desc: desc(
            None,
            &[
                tex(base + n, base + 0xFF),
                tex(base + 0x100 + n, base + 0x1FF),
            ],
        ),
        ..ChargenFaceStrip::default()
    }
}

fn gender() -> ChargenGenderOptions {
    ChargenGenderOptions {
        gender_key: 1,
        setup_id: GENDER_SETUP,
        base_palette_id: 0x0400_007E,
        skin_pal_set_id: SKIN_SET,
        hair_colors: vec![HAIR_SET_A, HAIR_SET_B],
        hair_styles: vec![
            style(HEAD_A, false, 0x0500_A001, 0),
            style(HEAD_B, false, 0x0500_A002, 0),
            style(HEAD_B, true, BALD_TEXTURE, 0),
            style(0x0100_1003, false, 0x0500_A003, undead::MALE_SKELETON_SETUP),
        ],
        eye_colors: vec![0x0400_0E01, 0x0400_0E02, 0x0400_0E03],
        eye_strips: vec![eye(0), eye(1), eye(2)],
        nose_strips: vec![face(0x0500_C000, 0), face(0x0500_C000, 1)],
        mouth_strips: vec![face(0x0500_D000, 0), face(0x0500_D000, 1)],
        ..ChargenGenderOptions::default()
    }
}

fn choices() -> BarberChoices {
    BarberChoices {
        hair_style: 1,
        hair_color: 1,
        hair_shade: 2.0 / 3.0,
        eyes_strip: 2,
        eye_color: 1,
        nose_strip: 1,
        mouth_strip: 0,
        skin_shade: 0.5,
    }
}

#[test]
fn a_hair_style_is_found_by_its_head_and_baldness() {
    let g = gender();
    assert_eq!(hair_index_from_id(&g, HEAD_B, false), 1);
    assert_eq!(hair_index_from_id(&g, HEAD_B, true), 2);
    // No head, or a head no style has: the first style.
    assert_eq!(hair_index_from_id(&g, NO_ID, false), 0);
    assert_eq!(hair_index_from_id(&g, 0x0100_FFFF, false), 0);
}

#[test]
fn eyes_are_found_by_texture_a_skeletons_by_the_second_and_a_bald_heads_by_the_bald_variant() {
    let g = gender();
    assert_eq!(eye_strip_index_from_id(&g, 0x0500_E002, false, false), 2);
    assert_eq!(eye_strip_index_from_id(&g, 0x0500_E101, true, false), 1);
    assert_eq!(eye_strip_index_from_id(&g, 0x0500_B002, false, true), 2);
    // The normal texture is not looked for under a bald head.
    assert_eq!(eye_strip_index_from_id(&g, 0x0500_E002, false, true), 0);
    assert_eq!(
        face_strip_index_from_id(&g.nose_strips, 0x0500_C101, true),
        1
    );
    assert_eq!(
        face_strip_index_from_id(&g.mouth_strips, 0x0500_D001, false),
        1
    );
    assert_eq!(face_strip_index_from_id(&g.mouth_strips, NO_ID, false), 0);
}

#[test]
fn a_palette_reads_back_to_a_shade_that_picks_it_again() {
    let g = gender();
    let sets = sets();
    // One past its place over the set's size.
    assert_eq!(skin_shade_from_id(&g, 0x0400_0102, &sets), 0.5);
    assert_eq!(skin_shade_from_id(&g, NO_ID, &sets), 0.0);
    // Not in the set: one past the end.
    assert_eq!(skin_shade_from_id(&g, 0x0400_0999, &sets), 1.25);
    for (place, &palette) in [0x0400_0101_u32, 0x0400_0102, 0x0400_0103, 0x0400_0104]
        .iter()
        .enumerate()
    {
        let shade = skin_shade_from_id(&g, palette, &sets);
        let set = sets.pal_set(SKIN_SET).unwrap();
        assert_eq!(palette_at(&set, shade), palette, "place {place}");
    }
    assert_eq!(hair_color_from_id(&g, 0x0400_0302, &sets), (1, 2.0 / 3.0));
    assert_eq!(hair_color_from_id(&g, 0x0400_0999, &sets), (0, 0.0));
    assert_eq!(eye_color_from_id(&g, 0x0400_0E03), 2);
    assert_eq!(eye_color_from_id(&g, 0x0400_0EEE), 0);
}

#[test]
fn accepting_sends_the_look_the_choices_compose() {
    let g = gender();
    let f = finish(H::ALUVIAN.0, 1, &g, &choices(), false, &sets()).unwrap();
    assert_eq!(
        f.look,
        BarberLook {
            base_palette: 0x0400_007E,
            head_object: HEAD_B,
            hair_texture: 0x0500_A002,
            default_hair_texture: 0x0500_0F00,
            eyes_texture: 0x0500_E002,
            default_eyes_texture: 0x0500_EEEE,
            nose_texture: 0x0500_C001,
            default_nose_texture: 0x0500_C0FF,
            mouth_texture: 0x0500_D000,
            default_mouth_texture: 0x0500_D0FF,
            skin_palette: 0x0400_0102,
            hair_palette: 0x0400_0302,
            eyes_palette: 0x0400_0E02,
            setup: GENDER_SETUP,
            option1: 0,
            option2: 0,
        }
    );
    assert!(!f.stop_effects);
    assert_eq!((f.script, f.motion_table), (NO_ID, NO_ID));
}

#[test]
fn the_look_sent_reads_back_to_the_same_choices() {
    let g = gender();
    let sets = sets();
    let f = finish(H::ALUVIAN.0, 1, &g, &choices(), false, &sets).unwrap();
    assert_eq!(choices_from_look(&g, &f.look, &sets), choices());
}

#[test]
fn a_skeleton_style_sends_the_second_face_textures_and_reads_them_back() {
    let g = gender();
    let sets = sets();
    let c = BarberChoices {
        hair_style: 3,
        ..choices()
    };
    let f = finish(H::UNDEAD.0, 1, &g, &c, false, &sets).unwrap();
    assert_eq!(f.look.eyes_texture, 0x0500_E102);
    assert_eq!(f.look.nose_texture, 0x0500_C101);
    assert_eq!(f.look.setup, undead::MALE_SKELETON_SETUP);
    assert_eq!(f.script, undead::MALE_SKELETON_SCRIPT);
    assert!(f.stop_effects);
    assert_eq!(choices_from_look(&g, &f.look, &sets), c);
}

#[test]
fn a_bald_style_sends_the_bald_eyes() {
    let g = gender();
    let c = BarberChoices {
        hair_style: 2,
        ..choices()
    };
    let f = finish(H::ALUVIAN.0, 1, &g, &c, false, &sets()).unwrap();
    assert_eq!(f.look.hair_texture, BALD_TEXTURE);
    assert_eq!(f.look.eyes_texture, 0x0500_B002);
    assert_eq!(choices_from_look(&g, &f.look, &sets()), c);
}

#[test]
fn the_undead_toggle_picks_the_body_without_flames() {
    let g = gender();
    let sets = sets();
    let skeleton = BarberChoices {
        hair_style: 3,
        ..choices()
    };
    let on = finish(H::UNDEAD.0, 1, &g, &skeleton, true, &sets).unwrap();
    assert_eq!(on.look.setup, undead::MALE_SKELETON_NOFLAME_SETUP);
    assert_eq!(on.script, NO_ID);
    let female = finish(H::UNDEAD.0, FEMALE, &g, &skeleton, false, &sets).unwrap();
    // Not a zombie body: the female skeleton.
    assert_eq!(female.look.setup, undead::FEMALE_SKELETON_SETUP);
    assert_eq!(female.script, undead::FEMALE_SKELETON_SCRIPT);
    assert_eq!(
        toggled_preview_setup(H::UNDEAD.0, 1, undead::MALE_ZOMBIE_SETUP, true),
        undead::MALE_ZOMBIE_NOFLAME_SETUP
    );
    assert_eq!(
        toggled_preview_setup(H::UNDEAD.0, 1, undead::MALE_ZOMBIE_SETUP, false),
        undead::MALE_ZOMBIE_SETUP
    );
    let look = BarberLook {
        setup: undead::FEMALE_ZOMBIE_NOFLAME_SETUP,
        ..BarberLook::default()
    };
    assert!(toggle_starts_on(H::UNDEAD.0, &look));
    assert_eq!(
        opening_preview_setup(H::UNDEAD.0, &look),
        undead::FEMALE_ZOMBIE_NOFLAME_SETUP
    );
}

#[test]
fn the_shadow_toggle_takes_the_crown_off() {
    let g = gender();
    let sets = sets();
    let crowned = finish(H::SHADOWBOUND.0, FEMALE, &g, &choices(), false, &sets).unwrap();
    assert_eq!(crowned.look.setup, umbraen::FEMALE_CROWN_SETUP);
    assert_eq!(crowned.script, umbraen::FEMALE_CROWN_SCRIPT);
    let bare = finish(H::PENUMBRAEN.0, 1, &g, &choices(), true, &sets).unwrap();
    assert_eq!(bare.look.setup, penumbraen::MALE_NOCROWN_SETUP);
    assert_eq!(bare.script, NO_ID);
    assert!(bare.stop_effects);
    assert_eq!(
        toggled_preview_setup(H::SHADOWBOUND.0, 1, GENDER_SETUP, true),
        umbraen::MALE_NOCROWN_SETUP
    );
    let look = BarberLook {
        setup: penumbraen::MALE_NOCROWN_SETUP,
        ..BarberLook::default()
    };
    assert!(toggle_starts_on(H::PENUMBRAEN.0, &look));
    assert!(!toggle_starts_on(H::SHADOWBOUND.0, &look));
}

#[test]
fn the_empyrean_toggle_is_the_first_option_and_changes_the_motion_table() {
    let g = gender();
    let sets = sets();
    let walking = finish(H::EMPYREAN.0, FEMALE, &g, &choices(), true, &sets).unwrap();
    assert_eq!(walking.look.option1, 1);
    assert_eq!(walking.motion_table, empyrean::FEMALE_MOTION_TABLE);
    assert!(!walking.stop_effects);
    let floating = finish(H::EMPYREAN.0, 1, &g, &choices(), false, &sets).unwrap();
    assert_eq!(floating.look.option1, 0);
    assert_eq!(floating.motion_table, empyrean::MALE_FLOAT_MOTION_TABLE);
    let look = BarberLook {
        option1: 1,
        ..BarberLook::default()
    };
    assert!(toggle_starts_on(H::EMPYREAN.0, &look));
    assert_eq!(opening_preview_setup(H::EMPYREAN.0, &look), NO_ID);
}

#[test]
fn a_gearknight_sends_no_head_and_no_hair_texture() {
    let f = finish(H::GEARKNIGHT.0, 1, &gender(), &choices(), false, &sets()).unwrap();
    assert_eq!(f.look.head_object, NO_ID);
    assert_eq!(
        (f.look.hair_texture, f.look.default_hair_texture),
        (NO_ID, NO_ID)
    );
    let olthoi = finish(H::OLTHOI.0, 1, &gender(), &choices(), false, &sets()).unwrap();
    assert_eq!(olthoi.look.head_object, HEAD_B);
    assert_eq!(olthoi.look.hair_texture, NO_ID);
}

#[test]
fn nothing_is_sent_without_the_skin_or_hair_palette_set() {
    let g = gender();
    let mut sets = sets();
    sets.0.remove(&HAIR_SET_B);
    assert!(finish(H::ALUVIAN.0, 1, &g, &choices(), false, &sets).is_none());
    sets.0.remove(&SKIN_SET);
    assert!(appearance_of(H::ALUVIAN.0, &g, &choices(), &sets).is_none());
}

#[test]
fn only_four_heritages_offer_a_toggle() {
    assert_eq!(
        BarberToggle::of(H::EMPYREAN.0),
        Some(BarberToggle::EmpyreanEarthbound)
    );
    assert_eq!(
        BarberToggle::of(H::UNDEAD.0),
        Some(BarberToggle::UndeadNoFlame)
    );
    assert_eq!(
        BarberToggle::of(H::SHADOWBOUND.0),
        Some(BarberToggle::ShadowNoCrown)
    );
    assert_eq!(
        BarberToggle::of(H::PENUMBRAEN.0),
        Some(BarberToggle::ShadowNoCrown)
    );
    for h in [H::ALUVIAN, H::GEARKNIGHT, H::OLTHOI, H::LUGIAN] {
        assert_eq!(BarberToggle::of(h.0), None);
    }
}

#[test]
fn the_look_body_is_the_styles_own_else_the_genders() {
    let g = gender();
    assert_eq!(look_setup(&g, 3), undead::MALE_SKELETON_SETUP);
    assert_eq!(look_setup(&g, 0), GENDER_SETUP);
    assert_eq!(look_setup(&g, u32::MAX), GENDER_SETUP);
}
