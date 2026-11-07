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

/// A human and an Undead heritage, both genders each.
pub(crate) fn options() -> ChargenOptions {
    ChargenOptions {
        heritages_by_id: HashMap::from([
            (HUMAN, heritage(HUMAN, 0)),
            (UNDEAD, heritage(UNDEAD, undead::MALE_SKELETON_SETUP)),
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
    let sent = barber.command(BarberCommand::Finish, &pal_sets());
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
    let sent = barber.command(BarberCommand::Finish, &sets).unwrap();
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
    let sent = barber.command(BarberCommand::Finish, &sets).unwrap();
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
