//! The barber's rules: reading the character's current look back into the
//! appearance choices, the heritage's one toggle (an Empyrean on the
//! ground, an Undead without flames, a Shadow without its crown) and the
//! body it shows, and the look the client sends when the player accepts.
//!
//! The barber works on the same choices as character creation (hair
//! style, colour and shade, eyes and their colour, nose, mouth, skin
//! shade); the server sends and takes the look as data ids, so each id is
//! found again in the gender's lists.

use super::appearance::{ChargenPalSet, ChargenPalSetSource, get_palette_index};
use super::options::{ChargenFaceStrip, ChargenGenderOptions, ChargenHeritageGroup as H};

/// "No data id" in the barber's looks.
pub const NO_ID: u32 = 0;
/// The hair texture of a bald head.
pub const BALD_TEXTURE: u32 = 0x0500_10B7;

/// The female gender key (every other key counts as male).
pub const FEMALE: u32 = 2;

/// The Undead bodies.
pub mod undead {
    /// Male skeleton.
    pub const MALE_SKELETON_SETUP: u32 = 0x0200_1A9C;
    /// Male skeleton without flames.
    pub const MALE_SKELETON_NOFLAME_SETUP: u32 = 0x0200_1A9E;
    /// Male zombie.
    pub const MALE_ZOMBIE_SETUP: u32 = 0x0200_1A9D;
    /// Male zombie without flames.
    pub const MALE_ZOMBIE_NOFLAME_SETUP: u32 = 0x0200_1A96;
    /// Female skeleton.
    pub const FEMALE_SKELETON_SETUP: u32 = 0x0200_1AA0;
    /// Female skeleton without flames.
    pub const FEMALE_SKELETON_NOFLAME_SETUP: u32 = 0x0200_1A9F;
    /// Female zombie.
    pub const FEMALE_ZOMBIE_SETUP: u32 = 0x0200_1AA1;
    /// Female zombie without flames.
    pub const FEMALE_ZOMBIE_NOFLAME_SETUP: u32 = 0x0200_1AA2;
    /// The male skeleton's flames.
    pub const MALE_SKELETON_SCRIPT: u32 = 0x3300_12D4;
    /// The male zombie's flames.
    pub const MALE_ZOMBIE_SCRIPT: u32 = 0x3300_12D3;
    /// The female skeleton's flames.
    pub const FEMALE_SKELETON_SCRIPT: u32 = 0x3300_12D5;
    /// The female zombie's flames.
    pub const FEMALE_ZOMBIE_SCRIPT: u32 = 0x3300_12D6;
}

/// The Shadowbound (Umbraen) bodies.
pub mod umbraen {
    /// Male, crowned.
    pub const MALE_CROWN_SETUP: u32 = 0x0200_196F;
    /// Male, no crown.
    pub const MALE_NOCROWN_SETUP: u32 = 0x0200_1A5F;
    /// Female, crowned.
    pub const FEMALE_CROWN_SETUP: u32 = 0x0200_1970;
    /// Female, no crown.
    pub const FEMALE_NOCROWN_SETUP: u32 = 0x0200_1A5E;
    /// The male crown's effect.
    pub const MALE_CROWN_SCRIPT: u32 = 0x3300_1256;
    /// The male uncrowned effect.
    pub const MALE_NOCROWN_SCRIPT: u32 = 0x3300_12BB;
    /// The female crown's effect.
    pub const FEMALE_CROWN_SCRIPT: u32 = 0x3300_1257;
    /// The female uncrowned effect.
    pub const FEMALE_NOCROWN_SCRIPT: u32 = 0x3300_12BA;
}

/// The Penumbraen bodies.
pub mod penumbraen {
    /// Male, crowned.
    pub const MALE_CROWN_SETUP: u32 = 0x0200_196E;
    /// Male, no crown.
    pub const MALE_NOCROWN_SETUP: u32 = 0x0200_1A5D;
    /// Female, crowned.
    pub const FEMALE_CROWN_SETUP: u32 = 0x0200_196D;
    /// Female, no crown.
    pub const FEMALE_NOCROWN_SETUP: u32 = 0x0200_1A5C;
    /// The male crown's effect.
    pub const MALE_CROWN_SCRIPT: u32 = 0x3300_1255;
    /// The female crown's effect.
    pub const FEMALE_CROWN_SCRIPT: u32 = 0x3300_1254;
    /// The uncrowned bodies play no effect.
    pub const NOCROWN_SCRIPT: u32 = super::NO_ID;
}

/// The Empyrean motion tables.
pub mod empyrean {
    /// Male, walking.
    pub const MALE_MOTION_TABLE: u32 = 0x0900_020E;
    /// Female, walking.
    pub const FEMALE_MOTION_TABLE: u32 = 0x0900_020D;
    /// Male, floating.
    pub const MALE_FLOAT_MOTION_TABLE: u32 = 0x0900_020B;
    /// Female, floating.
    pub const FEMALE_FLOAT_MOTION_TABLE: u32 = 0x0900_020A;
}

/// A look as the barber messages carry it (see the protocol's barber
/// messages): the appearance data ids ([`NO_ID`]: none), the body setup
/// and the two option words.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct BarberLook {
    /// The base palette.
    pub base_palette: u32,
    /// The head part.
    pub head_object: u32,
    /// The hair texture.
    pub hair_texture: u32,
    /// The texture it replaces.
    pub default_hair_texture: u32,
    /// The eyes texture.
    pub eyes_texture: u32,
    /// The texture it replaces.
    pub default_eyes_texture: u32,
    /// The nose texture.
    pub nose_texture: u32,
    /// The texture it replaces.
    pub default_nose_texture: u32,
    /// The mouth texture.
    pub mouth_texture: u32,
    /// The texture it replaces.
    pub default_mouth_texture: u32,
    /// The skin palette.
    pub skin_palette: u32,
    /// The hair palette.
    pub hair_palette: u32,
    /// The eyes palette.
    pub eyes_palette: u32,
    /// The body setup.
    pub setup: u32,
    /// The first option (the Empyrean walks).
    pub option1: i32,
    /// The second option.
    pub option2: i32,
}

/// The appearance choices the barber changes: indices into the gender's
/// lists and shades in 0..=1.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct BarberChoices {
    /// Hair style.
    pub hair_style: u32,
    /// Hair colour.
    pub hair_color: u32,
    /// Hair shade.
    pub hair_shade: f64,
    /// Eye strip.
    pub eyes_strip: u32,
    /// Eye colour.
    pub eye_color: u32,
    /// Nose strip.
    pub nose_strip: u32,
    /// Mouth strip.
    pub mouth_strip: u32,
    /// Skin shade.
    pub skin_shade: f64,
}

/// The one toggle a heritage's barber window offers.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum BarberToggle {
    /// An Empyrean walks on the ground instead of floating.
    EmpyreanEarthbound,
    /// An Undead without its flames.
    UndeadNoFlame,
    /// A Shadowbound or Penumbraen without its crown.
    ShadowNoCrown,
}

impl BarberToggle {
    /// The toggle `heritage` offers, if any.
    #[must_use]
    pub fn of(heritage: u32) -> Option<Self> {
        match H(heritage) {
            H::EMPYREAN => Some(Self::EmpyreanEarthbound),
            H::UNDEAD => Some(Self::UndeadNoFlame),
            H::SHADOWBOUND | H::PENUMBRAEN => Some(Self::ShadowNoCrown),
            _ => None,
        }
    }

    /// The caption's key in the interface string table.
    #[must_use]
    pub fn string_key(self) -> &'static str {
        match self {
            Self::EmpyreanEarthbound => "ID_Barber_Empyrean_Earthbound",
            Self::UndeadNoFlame => "ID_Barber_Undead_NoFlame",
            Self::ShadowNoCrown => "ID_Barber_Shadow_NoCrown",
        }
    }
}

/// Whether the toggle starts on for the look the server sent: the
/// Empyrean's first option, or a body without flames or crown.
#[must_use]
pub fn toggle_starts_on(heritage: u32, look: &BarberLook) -> bool {
    let setup = look.setup;
    match H(heritage) {
        H::EMPYREAN => look.option1 != 0,
        H::UNDEAD => [
            undead::FEMALE_SKELETON_NOFLAME_SETUP,
            undead::FEMALE_ZOMBIE_NOFLAME_SETUP,
            undead::MALE_SKELETON_NOFLAME_SETUP,
            undead::MALE_ZOMBIE_NOFLAME_SETUP,
        ]
        .contains(&setup),
        H::SHADOWBOUND => {
            setup == umbraen::MALE_NOCROWN_SETUP || setup == umbraen::FEMALE_NOCROWN_SETUP
        }
        H::PENUMBRAEN => {
            setup == penumbraen::MALE_NOCROWN_SETUP || setup == penumbraen::FEMALE_NOCROWN_SETUP
        }
        _ => false,
    }
}

/// The body the preview shows in place of the look's own when the window
/// opens: the server's body for the heritages whose toggle changes it,
/// [`NO_ID`] (the look's own) otherwise.
#[must_use]
pub fn opening_preview_setup(heritage: u32, look: &BarberLook) -> u32 {
    match H(heritage) {
        H::UNDEAD | H::SHADOWBOUND | H::PENUMBRAEN => look.setup,
        _ => NO_ID,
    }
}

/// The body the preview shows after the toggle was set to `on`, from the
/// look's own body `setup`: without flames or crown when on, the look's
/// own body otherwise.
#[must_use]
pub fn toggled_preview_setup(heritage: u32, gender: u32, setup: u32, on: bool) -> u32 {
    let female = gender == FEMALE;
    match H(heritage) {
        H::UNDEAD if on => {
            let (skeleton, skeleton_noflame, zombie, zombie_noflame) = if female {
                (
                    undead::FEMALE_SKELETON_SETUP,
                    undead::FEMALE_SKELETON_NOFLAME_SETUP,
                    undead::FEMALE_ZOMBIE_SETUP,
                    undead::FEMALE_ZOMBIE_NOFLAME_SETUP,
                )
            } else {
                (
                    undead::MALE_SKELETON_SETUP,
                    undead::MALE_SKELETON_NOFLAME_SETUP,
                    undead::MALE_ZOMBIE_SETUP,
                    undead::MALE_ZOMBIE_NOFLAME_SETUP,
                )
            };
            let setup = if setup == skeleton {
                skeleton_noflame
            } else {
                setup
            };
            if setup == zombie {
                zombie_noflame
            } else {
                setup
            }
        }
        H::SHADOWBOUND if on => {
            if female {
                umbraen::FEMALE_NOCROWN_SETUP
            } else {
                umbraen::MALE_NOCROWN_SETUP
            }
        }
        H::PENUMBRAEN if on => {
            if female {
                penumbraen::FEMALE_NOCROWN_SETUP
            } else {
                penumbraen::MALE_NOCROWN_SETUP
            }
        }
        _ => setup,
    }
}

/// The body of the chosen look: the hair style's own body when it has one,
/// else the gender's ([`u32::MAX`] as the style: none chosen).
#[must_use]
pub fn look_setup(gender: &ChargenGenderOptions, hair_style: u32) -> u32 {
    let style = usize::try_from(hair_style)
        .ok()
        .and_then(|i| gender.hair_styles.get(i));
    match style {
        Some(s) if s.alternate_setup != NO_ID => s.alternate_setup,
        _ => gender.setup_id,
    }
}

/// The hair style whose head is `head_object` and whose baldness is
/// `bald`; the first style when none is (or no head was sent).
#[must_use]
pub fn hair_index_from_id(gender: &ChargenGenderOptions, head_object: u32, bald: bool) -> u32 {
    if head_object == NO_ID {
        return 0;
    }
    gender
        .hair_styles
        .iter()
        .position(|s| {
            s.obj_desc
                .anim_part_changes
                .first()
                .is_some_and(|c| c.part_id == head_object)
                && s.bald == bald
        })
        .map_or(0, index)
}

/// The eye strip whose texture is `eyes_texture`: a skeleton's eyes are
/// the strip's second texture change, a bald head's the bald variant.
#[must_use]
pub fn eye_strip_index_from_id(
    gender: &ChargenGenderOptions,
    eyes_texture: u32,
    skeleton: bool,
    bald: bool,
) -> u32 {
    if eyes_texture == NO_ID {
        return 0;
    }
    gender
        .eye_strips
        .iter()
        .position(|s| {
            let desc = if bald { &s.bald_obj_desc } else { &s.obj_desc };
            desc.texture_changes
                .get(usize::from(skeleton))
                .is_some_and(|t| t.new_texture_id == eyes_texture)
        })
        .map_or(0, index)
}

/// The nose or mouth strip whose texture is `texture` (a skeleton's is
/// the second texture change).
#[must_use]
pub fn face_strip_index_from_id(strips: &[ChargenFaceStrip], texture: u32, skeleton: bool) -> u32 {
    if texture == NO_ID {
        return 0;
    }
    strips
        .iter()
        .position(|s| {
            s.obj_desc
                .texture_changes
                .get(usize::from(skeleton))
                .is_some_and(|t| t.new_texture_id == texture)
        })
        .map_or(0, index)
}

/// The skin shade that picks `skin_palette` from the skin palette set:
/// one past its place over the set's size (one past the end when the set
/// does not hold it); 0 for no palette or a set the dats lack.
#[must_use]
pub fn skin_shade_from_id(
    gender: &ChargenGenderOptions,
    skin_palette: u32,
    pal_sets: &impl ChargenPalSetSource,
) -> f64 {
    if skin_palette == NO_ID {
        return 0.0;
    }
    let Some(set) = pal_sets.pal_set(gender.skin_pal_set_id) else {
        return 0.0;
    };
    let count = set.palette_ids.len();
    let place = set
        .palette_ids
        .iter()
        .position(|&p| p == skin_palette)
        .unwrap_or(count);
    (place + 1) as f64 / count as f64
}

/// The hair colour whose palette set holds `hair_palette`, and the shade
/// that picks it (one past its place over the set's size); colour 0 and
/// shade 0 when no set holds it.
#[must_use]
pub fn hair_color_from_id(
    gender: &ChargenGenderOptions,
    hair_palette: u32,
    pal_sets: &impl ChargenPalSetSource,
) -> (u32, f64) {
    if hair_palette == NO_ID {
        return (0, 0.0);
    }
    for (color, &pal_set_id) in gender.hair_colors.iter().enumerate() {
        let Some(set) = pal_sets.pal_set(pal_set_id) else {
            continue;
        };
        if let Some(place) = set.palette_ids.iter().position(|&p| p == hair_palette) {
            return (
                index(color),
                (place + 1) as f64 / set.palette_ids.len() as f64,
            );
        }
    }
    (0, 0.0)
}

/// The eye colour that is `eyes_palette`; 0 when none is.
#[must_use]
pub fn eye_color_from_id(gender: &ChargenGenderOptions, eyes_palette: u32) -> u32 {
    if eyes_palette == NO_ID {
        return 0;
    }
    gender
        .eye_colors
        .iter()
        .position(|&p| p == eyes_palette)
        .map_or(0, index)
}

/// A list place as a choice index.
fn index(place: usize) -> u32 {
    u32::try_from(place).unwrap_or(u32::MAX)
}

/// The first style when `found` is past the list's end.
fn first_when_past(found: u32, count: usize) -> u32 {
    if found as usize >= count { 0 } else { found }
}

/// The choices the server's look reads back to, in the order the window
/// sets them: the hair style (by its head and baldness), the eyes, nose
/// and mouth (a skeleton's by their second texture), the hair colour and
/// shade, the skin shade and the eye colour.
#[must_use]
pub fn choices_from_look(
    gender: &ChargenGenderOptions,
    look: &BarberLook,
    pal_sets: &impl ChargenPalSetSource,
) -> BarberChoices {
    let skeleton = [
        undead::MALE_SKELETON_SETUP,
        undead::MALE_SKELETON_NOFLAME_SETUP,
        undead::FEMALE_SKELETON_SETUP,
        undead::FEMALE_SKELETON_NOFLAME_SETUP,
    ]
    .contains(&look.setup);
    let bald = look.hair_texture == BALD_TEXTURE;
    let (hair_color, hair_shade) = hair_color_from_id(gender, look.hair_palette, pal_sets);
    BarberChoices {
        hair_style: first_when_past(
            hair_index_from_id(gender, look.head_object, bald),
            gender.hair_styles.len(),
        ),
        eyes_strip: first_when_past(
            eye_strip_index_from_id(gender, look.eyes_texture, skeleton, bald),
            gender.eye_strips.len(),
        ),
        nose_strip: first_when_past(
            face_strip_index_from_id(&gender.nose_strips, look.nose_texture, skeleton),
            gender.nose_strips.len(),
        ),
        mouth_strip: first_when_past(
            face_strip_index_from_id(&gender.mouth_strips, look.mouth_texture, skeleton),
            gender.mouth_strips.len(),
        ),
        hair_color,
        hair_shade,
        skin_shade: skin_shade_from_id(gender, look.skin_palette, pal_sets),
        eye_color: eye_color_from_id(gender, look.eyes_palette),
    }
}

/// The appearance part of a look, from the choices.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct BarberAppearance {
    /// The base palette.
    pub base_palette: u32,
    /// The head part.
    pub head_object: u32,
    /// The eyes texture and the one it replaces.
    pub eyes_texture: (u32, u32),
    /// The nose texture and the one it replaces.
    pub nose_texture: (u32, u32),
    /// The mouth texture and the one it replaces.
    pub mouth_texture: (u32, u32),
    /// The skin palette.
    pub skin_palette: u32,
    /// The hair palette.
    pub hair_palette: u32,
    /// The eyes palette.
    pub eyes_palette: u32,
}

fn pick<T>(list: &[T], index: u32) -> Option<&T> {
    usize::try_from(index).ok().and_then(|i| list.get(i))
}

fn palette_at(set: &ChargenPalSet, shade: f64) -> u32 {
    let count = i32::try_from(set.palette_ids.len()).unwrap_or(i32::MAX);
    usize::try_from(get_palette_index(count, shade))
        .ok()
        .and_then(|i| set.palette_ids.get(i))
        .copied()
        .unwrap_or(NO_ID)
}

/// The texture change a face part's description gives: a skeleton's
/// second one when it has one, else the first.
fn face_texture(desc: &super::options::ChargenObjDesc, skeleton: bool) -> (u32, u32) {
    let change = if skeleton {
        desc.texture_changes
            .get(1)
            .or_else(|| desc.texture_changes.first())
    } else {
        desc.texture_changes.first()
    };
    change.map_or((NO_ID, NO_ID), |t| (t.new_texture_id, t.old_texture_id))
}

/// The appearance of the choices: the gender's base palette, the hair
/// style's head (none for a Gearknight), the eyes (bald variant under a
/// bald style), nose and mouth textures (a skeleton style's second
/// texture change), and the skin, hair and eyes palettes. `None` when the
/// dats lack the skin or the hair colour's palette set (nothing is sent
/// then).
#[must_use]
pub fn appearance_of(
    heritage: u32,
    gender: &ChargenGenderOptions,
    choices: &BarberChoices,
    pal_sets: &impl ChargenPalSetSource,
) -> Option<BarberAppearance> {
    let mut out = BarberAppearance {
        base_palette: gender.base_palette_id,
        ..BarberAppearance::default()
    };
    let style = pick(&gender.hair_styles, choices.hair_style);
    if let Some(head) = style.and_then(|s| s.obj_desc.anim_part_changes.first()) {
        out.head_object = if H(heritage) == H::GEARKNIGHT {
            NO_ID
        } else {
            head.part_id
        };
    }
    let skeleton = style.is_some_and(|s| {
        s.alternate_setup == undead::MALE_SKELETON_SETUP
            || s.alternate_setup == undead::FEMALE_SKELETON_SETUP
    });
    let bald = style.is_some_and(|s| s.bald);
    if let Some(strip) = pick(&gender.eye_strips, choices.eyes_strip) {
        let desc = if bald {
            &strip.bald_obj_desc
        } else {
            &strip.obj_desc
        };
        out.eyes_texture = face_texture(desc, skeleton);
    }
    if let Some(strip) = pick(&gender.nose_strips, choices.nose_strip) {
        out.nose_texture = face_texture(&strip.obj_desc, skeleton);
    }
    if let Some(strip) = pick(&gender.mouth_strips, choices.mouth_strip) {
        out.mouth_texture = face_texture(&strip.obj_desc, skeleton);
    }
    let skin = pal_sets.pal_set(gender.skin_pal_set_id)?;
    out.skin_palette = palette_at(&skin, choices.skin_shade);
    let hair = pal_sets.pal_set(*pick(&gender.hair_colors, choices.hair_color)?)?;
    out.hair_palette = palette_at(&hair, choices.hair_shade);
    out.eyes_palette = pick(&gender.eye_colors, choices.eye_color)
        .copied()
        .unwrap_or(NO_ID);
    Some(out)
}

/// What accepting the barber does: the look sent to the server, and what
/// the client does to its own character at once (the body's effect
/// stopped and a new one played, the Empyrean's motion table changed).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BarberFinish {
    /// The look sent.
    pub look: BarberLook,
    /// The character's particle effects are stopped first.
    pub stop_effects: bool,
    /// The effect the character then plays ([`NO_ID`]: none).
    pub script: u32,
    /// The motion table the character takes ([`NO_ID`]: unchanged).
    pub motion_table: u32,
}

/// Accept the choices with the toggle `on`: the hair texture of the
/// style (none for a Gearknight or an Olthoi), the body (an Undead,
/// Shadowbound or Penumbraen body with or without flames or crown) and
/// its effect, the Empyrean's option and motion table, and the
/// appearance. `None` when nothing is sent (see [`appearance_of`]).
#[must_use]
pub fn finish(
    heritage: u32,
    gender_key: u32,
    gender: &ChargenGenderOptions,
    choices: &BarberChoices,
    on: bool,
    pal_sets: &impl ChargenPalSetSource,
) -> Option<BarberFinish> {
    let female = gender_key == FEMALE;
    let mut setup = look_setup(gender, choices.hair_style);
    let (mut hair_texture, mut default_hair_texture) = (NO_ID, NO_ID);
    if !matches!(H(heritage), H::GEARKNIGHT | H::OLTHOI | H::OLTHOI_ACID)
        && let Some(change) = pick(&gender.hair_styles, choices.hair_style)
            .and_then(|s| s.obj_desc.texture_changes.first())
    {
        hair_texture = change.new_texture_id;
        default_hair_texture = change.old_texture_id;
    }
    let mut option1 = 0;
    let mut motion_table = NO_ID;
    let mut stop_effects = false;
    let mut script = NO_ID;
    match H(heritage) {
        H::EMPYREAN => {
            option1 = i32::from(on);
            motion_table = match (on, female) {
                (true, true) => empyrean::FEMALE_MOTION_TABLE,
                (true, false) => empyrean::MALE_MOTION_TABLE,
                (false, true) => empyrean::FEMALE_FLOAT_MOTION_TABLE,
                (false, false) => empyrean::MALE_FLOAT_MOTION_TABLE,
            };
        }
        H::UNDEAD => {
            use undead::*;
            let zombie = [
                FEMALE_ZOMBIE_SETUP,
                FEMALE_ZOMBIE_NOFLAME_SETUP,
                MALE_ZOMBIE_SETUP,
                MALE_ZOMBIE_NOFLAME_SETUP,
            ]
            .contains(&setup);
            stop_effects = true;
            (setup, script) = match (on, female, zombie) {
                (false, true, true) => (FEMALE_ZOMBIE_SETUP, FEMALE_ZOMBIE_SCRIPT),
                (false, true, false) => (FEMALE_SKELETON_SETUP, FEMALE_SKELETON_SCRIPT),
                (false, false, true) => (MALE_ZOMBIE_SETUP, MALE_ZOMBIE_SCRIPT),
                (false, false, false) => (MALE_SKELETON_SETUP, MALE_SKELETON_SCRIPT),
                // The bodies without flames play nothing.
                (true, true, true) => (FEMALE_ZOMBIE_NOFLAME_SETUP, NO_ID),
                (true, true, false) => (FEMALE_SKELETON_NOFLAME_SETUP, NO_ID),
                (true, false, true) => (MALE_ZOMBIE_NOFLAME_SETUP, NO_ID),
                (true, false, false) => (MALE_SKELETON_NOFLAME_SETUP, NO_ID),
            };
        }
        H::SHADOWBOUND => {
            use umbraen::*;
            stop_effects = true;
            (setup, script) = match (on, female) {
                (true, true) => (FEMALE_NOCROWN_SETUP, FEMALE_NOCROWN_SCRIPT),
                (true, false) => (MALE_NOCROWN_SETUP, MALE_NOCROWN_SCRIPT),
                (false, true) => (FEMALE_CROWN_SETUP, FEMALE_CROWN_SCRIPT),
                (false, false) => (MALE_CROWN_SETUP, MALE_CROWN_SCRIPT),
            };
        }
        H::PENUMBRAEN => {
            use penumbraen::*;
            stop_effects = true;
            (setup, script) = match (on, female) {
                (true, true) => (FEMALE_NOCROWN_SETUP, NOCROWN_SCRIPT),
                (true, false) => (MALE_NOCROWN_SETUP, NOCROWN_SCRIPT),
                (false, true) => (FEMALE_CROWN_SETUP, FEMALE_CROWN_SCRIPT),
                (false, false) => (MALE_CROWN_SETUP, MALE_CROWN_SCRIPT),
            };
        }
        _ => {}
    }
    let a = appearance_of(heritage, gender, choices, pal_sets)?;
    Some(BarberFinish {
        look: BarberLook {
            base_palette: a.base_palette,
            head_object: a.head_object,
            hair_texture,
            default_hair_texture,
            eyes_texture: a.eyes_texture.0,
            default_eyes_texture: a.eyes_texture.1,
            nose_texture: a.nose_texture.0,
            default_nose_texture: a.nose_texture.1,
            mouth_texture: a.mouth_texture.0,
            default_mouth_texture: a.mouth_texture.1,
            skin_palette: a.skin_palette,
            hair_palette: a.hair_palette,
            eyes_palette: a.eyes_palette,
            setup,
            option1,
            option2: 0,
        },
        stop_effects,
        script,
        motion_table,
    })
}

#[cfg(test)]
#[path = "barber_tests.rs"]
mod tests;
