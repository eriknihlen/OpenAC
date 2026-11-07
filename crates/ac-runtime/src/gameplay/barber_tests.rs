//! The barber's session state over a small made-up heritage: opening reads
//! the server's look back into the choices, the window's commands change
//! them, accepting answers with the look and closes, cancelling closes and
//! sends nothing.

use std::collections::HashMap;

use ac_core::chargen::barber::undead;
use ac_core::chargen::{
    ChargenAnimPartChange, ChargenEyeStrip, ChargenFaceStrip, ChargenHairStyle,
    ChargenHeritageOptions, ChargenObjDesc, ChargenPalSet, ChargenTextureChange,
};

use super::*;
use crate::gameplay::tables::ChargenPalSets;

pub(crate) const HUMAN: u32 = 1;
pub(crate) const UNDEAD: u32 = 11;
pub(crate) const EMPYREAN: u32 = 9;
pub(crate) const MALE: u32 = 1;
const SKIN_SET: u32 = 0x0F00_0001;
const HAIR_SET: u32 = 0x0F00_0010;

fn tex(new: u32) -> ChargenTextureChange {
    ChargenTextureChange {
        part_index: 16,
        old_texture_id: new | 0xFF,
        new_texture_id: new,
    }
}

fn desc(head: Option<u32>, textures: &[u32]) -> ChargenObjDesc {
    ChargenObjDesc {
        anim_part_changes: head
            .map(|part_id| ChargenAnimPartChange {
                part_index: 16,
                part_id,
            })
            .into_iter()
            .collect(),
        texture_changes: textures.iter().map(|&t| tex(t)).collect(),
        ..ChargenObjDesc::default()
    }
}

fn gender(key: i32, skeleton_setup: u32) -> ChargenGenderOptions {
    ChargenGenderOptions {
        gender_key: key,
        setup_id: 0x0200_0001,
        base_palette_id: 0x0400_007E,
        skin_pal_set_id: SKIN_SET,
        hair_colors: vec![HAIR_SET],
        hair_styles: (0..3)
            .map(|i| ChargenHairStyle {
                alternate_setup: if i == 2 { skeleton_setup } else { 0 },
                obj_desc: desc(Some(0x0100_1000 + i), &[0x0500_A000 + i]),
                ..ChargenHairStyle::default()
            })
            .collect(),
        eye_colors: vec![0x0400_0E01, 0x0400_0E02],
        eye_strips: (0..3)
            .map(|i| ChargenEyeStrip {
                obj_desc: desc(None, &[0x0500_E000 + i, 0x0500_E100 + i]),
                bald_obj_desc: desc(None, &[0x0500_B000 + i]),
                ..ChargenEyeStrip::default()
            })
            .collect(),
        nose_strips: (0..2)
            .map(|i| ChargenFaceStrip {
                obj_desc: desc(None, &[0x0500_C000 + i]),
                ..ChargenFaceStrip::default()
            })
            .collect(),
        mouth_strips: (0..2)
            .map(|i| ChargenFaceStrip {
                obj_desc: desc(None, &[0x0500_D000 + i]),
                ..ChargenFaceStrip::default()
            })
            .collect(),
        shirts: vec![Default::default(); 2],
        clothing_colors: vec![0; 3],
        ..ChargenGenderOptions::default()
    }
}

fn heritage(id: u32, skeleton_setup: u32) -> ChargenHeritageOptions {
    ChargenHeritageOptions {
        heritage_id: id,
        genders_by_key: HashMap::from([
            (1, gender(1, skeleton_setup)),
            (2, gender(2, skeleton_setup)),
        ]),
        ..ChargenHeritageOptions::default()
    }
}

/// A human, an Undead and an Empyrean heritage, both genders each.
pub(crate) fn options() -> ChargenOptions {
    ChargenOptions {
        heritages_by_id: HashMap::from([
            (HUMAN, heritage(HUMAN, 0)),
            (UNDEAD, heritage(UNDEAD, undead::MALE_SKELETON_SETUP)),
            (EMPYREAN, heritage(EMPYREAN, 0)),
        ]),
        ..ChargenOptions::default()
    }
}

/// The skin and hair palette sets.
pub(crate) fn pal_sets() -> ChargenPalSets {
    let set = |ids: &[u32]| {
        Arc::new(ChargenPalSet {
            palette_ids: ids.to_vec(),
        })
    };
    ChargenPalSets(HashMap::from([
        (
            SKIN_SET,
            set(&[0x0400_0101, 0x0400_0102, 0x0400_0103, 0x0400_0104]),
        ),
        (HAIR_SET, set(&[0x0400_0201, 0x0400_0202])),
    ]))
}

/// The look the server sends for hair style 1, eyes 2, nose 1, mouth 0,
/// the second skin and hair palettes and the second eye colour.
pub(crate) fn server_look() -> BarberLook {
    BarberLook {
        base_palette: 0x0400_007E,
        head_object: 0x0100_1001,
        hair_texture: 0x0500_A001,
        default_hair_texture: 0x0500_A0FF,
        eyes_texture: 0x0500_E002,
        default_eyes_texture: 0x0500_E0FF,
        nose_texture: 0x0500_C001,
        default_nose_texture: 0x0500_C0FF,
        mouth_texture: 0x0500_D000,
        default_mouth_texture: 0x0500_D0FF,
        skin_palette: 0x0400_0102,
        hair_palette: 0x0400_0202,
        eyes_palette: 0x0400_0E02,
        setup: 0x0200_0001,
        option1: 0,
        option2: 0,
    }
}

fn opened(heritage: u32) -> BarberState {
    let mut barber = BarberState::default();
    barber.start(
        &server_look(),
        heritage,
        MALE,
        Arc::new(options()),
        &pal_sets(),
        7,
    );
    barber
}

#[test]
fn opening_reads_the_servers_look_into_the_choices() {
    let barber = opened(HUMAN);
    let view = barber.view();
    assert!(view.open);
    assert_eq!((view.heritage, view.gender), (HUMAN, MALE));
    assert_eq!(view.openings, 1);
    let a = view.appearance;
    assert_eq!(
        (a.hair_style, a.eyes_strip, a.nose_strip, a.mouth_strip),
        (1, 2, 1, 0)
    );
    assert_eq!((a.hair_color, a.eye_color), (0, 1));
    assert_eq!((a.hair_shade, a.skin_shade), (1.0, 0.5));
    // The headgear comes off for the barber.
    assert_eq!(a.headgear_style, UNSET);
    assert_eq!((view.toggle, view.preview_setup), (None, NO_ID));
}

#[test]
fn accepting_unchanged_sends_the_look_back_and_closes() {
    let mut barber = opened(HUMAN);
    let revision = barber.revision();
    let sent = barber
        .command(BarberCommand::Finish, &pal_sets())
        .and_then(|f| f.look);
    assert_eq!(sent, Some(server_look()));
    assert!(!barber.is_open());
    assert!(barber.revision() > revision);
}

#[test]
fn a_choice_changes_the_look_sent() {
    let mut barber = opened(HUMAN);
    let sets = pal_sets();
    let revision = barber.revision();
    barber.command(
        BarberCommand::SetAppearance(AppearanceSlot::HairStyle, 0),
        &sets,
    );
    barber.command(BarberCommand::SetShade(ShadeSlot::Skin, 0.0), &sets);
    assert_eq!(barber.revision(), revision + 2);
    let sent = barber
        .command(BarberCommand::Finish, &sets)
        .and_then(|f| f.look)
        .unwrap();
    assert_eq!(sent.head_object, 0x0100_1000);
    assert_eq!(sent.hair_texture, 0x0500_A000);
    assert_eq!(sent.skin_palette, 0x0400_0101);
}

#[test]
fn the_barber_changes_only_the_face_and_hair() {
    let mut barber = opened(HUMAN);
    let sets = pal_sets();
    let revision = barber.revision();
    barber.command(
        BarberCommand::SetAppearance(AppearanceSlot::ShirtStyle, 0),
        &sets,
    );
    barber.command(BarberCommand::SetShade(ShadeSlot::Shirt, 0.3), &sets);
    // A human offers no toggle.
    barber.command(BarberCommand::SetToggle(true), &sets);
    // An index past the list.
    barber.command(
        BarberCommand::SetAppearance(AppearanceSlot::NoseStrip, 9),
        &sets,
    );
    assert_eq!(barber.revision(), revision);
    assert!(!barber.view().toggle_on);
}

#[test]
fn cancelling_closes_and_sends_nothing() {
    let mut barber = opened(HUMAN);
    assert_eq!(barber.command(BarberCommand::Cancel, &pal_sets()), None);
    assert!(!barber.is_open());
    assert!(!barber.view().open);
    // Nothing is open: nothing is done.
    let revision = barber.revision();
    assert_eq!(barber.command(BarberCommand::Finish, &pal_sets()), None);
    assert_eq!(barber.revision(), revision);
}

#[test]
fn the_undead_toggle_changes_the_preview_body_and_the_look_sent() {
    let mut barber = opened(UNDEAD);
    let sets = pal_sets();
    let view = barber.view();
    assert_eq!(view.toggle, Some(BarberToggle::UndeadNoFlame));
    assert!(!view.toggle_on);
    // The server's body shows until the toggle is touched.
    assert_eq!(view.preview_setup, 0x0200_0001);
    barber.command(
        BarberCommand::SetAppearance(AppearanceSlot::HairStyle, 2),
        &sets,
    );
    barber.command(BarberCommand::SetToggle(true), &sets);
    let view = barber.view();
    assert!(view.toggle_on);
    assert_eq!(view.preview_setup, undead::MALE_SKELETON_NOFLAME_SETUP);
    let sent = barber
        .command(BarberCommand::Finish, &sets)
        .and_then(|f| f.look)
        .unwrap();
    assert_eq!(sent.setup, undead::MALE_SKELETON_NOFLAME_SETUP);
}

#[test]
fn opening_again_starts_afresh_and_leaving_the_world_closes() {
    let mut barber = opened(HUMAN);
    barber.command(
        BarberCommand::SetAppearance(AppearanceSlot::HairStyle, 0),
        &pal_sets(),
    );
    barber.start(
        &server_look(),
        HUMAN,
        MALE,
        Arc::new(options()),
        &pal_sets(),
        8,
    );
    let view = barber.view();
    assert_eq!(view.openings, 2);
    assert_eq!(view.appearance.hair_style, 1);
    barber.reset_session();
    assert!(!barber.is_open());
}

#[test]
fn the_messages_look_converts_both_ways() {
    let look = server_look();
    assert_eq!(core_look(&wire_look(&look)), look);
}

#[test]
fn a_skin_palette_its_set_lacks_comes_back_as_no_palette() {
    // A palette the skin set does not hold reads back past the last
    // shade; accepting then names no skin palette rather than the last.
    let mut barber = BarberState::default();
    let look = BarberLook {
        skin_palette: 0x0400_0999,
        ..server_look()
    };
    barber.start(&look, HUMAN, MALE, Arc::new(options()), &pal_sets(), 7);
    assert!(barber.view().appearance.skin_shade > 1.0);
    let sent = barber
        .command(BarberCommand::Finish, &pal_sets())
        .and_then(|f| f.look)
        .unwrap();
    assert_eq!(sent.skin_palette, NO_ID);
}

#[test]
fn accepting_an_undead_stops_its_particles_and_plays_the_new_bodys_flames() {
    let mut barber = opened(UNDEAD);
    let sets = pal_sets();
    barber.command(
        BarberCommand::SetAppearance(AppearanceSlot::HairStyle, 2),
        &sets,
    );
    let f = barber.command(BarberCommand::Finish, &sets).unwrap();
    assert!(f.stop_effects);
    assert_eq!(f.script, undead::MALE_SKELETON_SCRIPT);
    assert_eq!(f.motion_table, NO_ID);
}

/// Against the retail dats: for every heritage and gender, looks built
/// from choices across each list (and each toggle a heritage offers) open
/// the barber, over several random outfits, and accepting unchanged sends
/// the same sixteen words back.
#[test]
#[ignore = "needs the retail dat files"]
fn every_heritage_and_gender_accepts_its_own_look_unchanged() {
    use ac_core::chargen::barber::{finish, toggle_starts_on};
    let tables = crate::gameplay::GameTables::open(None).expect("the retail dats");
    let options = Arc::clone(tables.chargen());
    let sets = tables.chargen_pal_sets();
    let mut checked = 0;
    let mut heritages: Vec<_> = options.heritages_by_id.keys().copied().collect();
    heritages.sort_unstable();
    for heritage in heritages {
        let h = &options.heritages_by_id[&heritage];
        let mut keys: Vec<_> = h.genders_by_key.keys().copied().collect();
        keys.sort_unstable();
        for key in keys {
            let g = &h.genders_by_key[&key];
            let gender = key.cast_unsigned();
            let lens = [
                g.hair_styles.len(),
                g.eye_strips.len(),
                g.nose_strips.len(),
                g.mouth_strips.len(),
                g.hair_colors.len(),
                g.eye_colors.len(),
            ];
            let rounds = lens.iter().copied().max().unwrap_or(0).max(1);
            let skin = sets
                .pal_set(g.skin_pal_set_id)
                .map_or(0, |s| s.palette_ids.len());
            for i in 0..rounds {
                let pick = |n: usize| if n == 0 { 0 } else { (i % n) as u32 };
                let hair_color = pick(g.hair_colors.len());
                let hair_len = g
                    .hair_colors
                    .get(hair_color as usize)
                    .and_then(|&id| sets.pal_set(id))
                    .map_or(0, |s| s.palette_ids.len());
                let shade = |n: usize| {
                    if n == 0 {
                        0.0
                    } else {
                        (i % n + 1) as f64 / n as f64
                    }
                };
                let choices = BarberChoices {
                    hair_style: pick(g.hair_styles.len()),
                    hair_color,
                    hair_shade: shade(hair_len),
                    eyes_strip: pick(g.eye_strips.len()),
                    eye_color: pick(g.eye_colors.len()),
                    nose_strip: pick(g.nose_strips.len()),
                    mouth_strip: pick(g.mouth_strips.len()),
                    skin_shade: shade(skin),
                };
                let toggles: &[bool] = if BarberToggle::of(heritage).is_some() {
                    &[false, true]
                } else {
                    &[false]
                };
                for &on in toggles {
                    let Some(look) = finish(heritage, gender, g, &choices, on, sets).look else {
                        continue;
                    };
                    assert_eq!(toggle_starts_on(heritage, &look), on, "{heritage} {gender}");
                    for seed in [1, 7, 12_345] {
                        let mut barber = BarberState::default();
                        barber.start(&look, heritage, gender, Arc::clone(&options), sets, seed);
                        let back = barber
                            .command(BarberCommand::Finish, sets)
                            .and_then(|f| f.look);
                        assert_eq!(
                            back.map(|b| wire_look(&b)),
                            Some(wire_look(&look)),
                            "heritage {heritage} gender {gender} round {i} toggle {on} seed {seed}"
                        );
                        checked += 1;
                    }
                }
            }
        }
    }
    println!("{checked} looks came back unchanged");
    assert!(checked > 0);
}
