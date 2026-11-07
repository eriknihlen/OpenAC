//! The barber: the server opens it with the character's look, the window's
//! choices are commands that change the look being built, and accepting
//! sends the look chosen; cancelling sends nothing.
//!
//! The look is built the way character creation builds one (the same
//! choices, randomised first, so the outfit the preview wears is a random
//! one), with the character's heritage and gender and the face the server
//! sent read back into the choices. Only the hair, eyes, nose, mouth and
//! skin change here.

use std::sync::Arc;

use ac_core::chargen::barber::{
    self, BarberChoices, BarberLook, BarberToggle, NO_ID, choices_from_look, look_setup,
};
use ac_core::chargen::{ChargenGenderOptions, ChargenOptions, ChargenPalSetSource};

use crate::session::Generation;
use crate::session::creation::{
    AppearanceSlot, CharacterCreation, CreationAppearance, SeededRandom, ShadeSlot, UNSET,
};

/// A barber request (the window's choices and buttons).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum BarberCommand {
    /// Choose an index for a look slot: the hair style or colour, the eye
    /// strip or colour, the nose or the mouth.
    SetAppearance(AppearanceSlot, u32),
    /// Set the hair or the skin shade (0..=1).
    SetShade(ShadeSlot, f64),
    /// Set the heritage's toggle.
    SetToggle(bool),
    /// Accept: the look goes to the server and the barber closes.
    Finish,
    /// Cancel: the barber closes and nothing is sent.
    Cancel,
}

/// The barber as a window shows it.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct BarberView {
    /// Counts changes.
    pub revision: u64,
    /// Counts the times the barber opened (each opening sets the window up
    /// anew).
    pub openings: u64,
    /// The barber is open.
    pub open: bool,
    /// The character's heritage.
    pub heritage: u32,
    /// The character's gender key.
    pub gender: u32,
    /// The look being built.
    pub appearance: CreationAppearance,
    /// The toggle the heritage offers.
    pub toggle: Option<BarberToggle>,
    /// It is on.
    pub toggle_on: bool,
    /// The body the preview shows in place of the look's own ([`NO_ID`]:
    /// the look's own).
    pub preview_setup: u32,
}

#[derive(Debug)]
struct Open {
    creation: CharacterCreation,
    heritage: u32,
    gender: u32,
    toggle: Option<BarberToggle>,
    toggle_on: bool,
    preview_setup: u32,
}

impl Open {
    fn gender_options(&self) -> Option<&ChargenGenderOptions> {
        self.creation
            .options()
            .try_get_heritage(self.heritage)?
            .genders_by_key
            .get(&i32::try_from(self.gender).ok()?)
    }

    fn appearance(&self) -> CreationAppearance {
        self.creation.snapshot().appearance
    }

    fn choices(&self) -> BarberChoices {
        let a = self.appearance();
        BarberChoices {
            hair_style: a.hair_style,
            hair_color: a.hair_color,
            hair_shade: a.hair_shade,
            eyes_strip: a.eyes_strip,
            eye_color: a.eye_color,
            nose_strip: a.nose_strip,
            mouth_strip: a.mouth_strip,
            skin_shade: a.skin_shade,
        }
    }
}

/// See the module documentation.
#[derive(Debug, Default)]
pub struct BarberState {
    open: Option<Open>,
    revision: u64,
    openings: u64,
}

/// The slots the barber changes.
const BARBER_SLOTS: [AppearanceSlot; 6] = [
    AppearanceSlot::HairStyle,
    AppearanceSlot::HairColor,
    AppearanceSlot::EyesStrip,
    AppearanceSlot::EyeColor,
    AppearanceSlot::NoseStrip,
    AppearanceSlot::MouthStrip,
];

impl BarberState {
    /// The barber as it stands.
    #[must_use]
    pub fn view(&self) -> BarberView {
        let mut view = BarberView {
            revision: self.revision,
            openings: self.openings,
            ..BarberView::default()
        };
        if let Some(open) = &self.open {
            view.open = true;
            view.heritage = open.heritage;
            view.gender = open.gender;
            view.appearance = open.appearance();
            view.toggle = open.toggle;
            view.toggle_on = open.toggle_on;
            view.preview_setup = open.preview_setup;
        }
        view
    }

    /// Counts changes.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// The barber is open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.open.is_some()
    }

    /// The server opened the barber with the character's `look`, for a
    /// character of `heritage` and `gender`. `seed` seeds the random
    /// outfit. An open barber starts again.
    pub fn start(
        &mut self,
        look: &BarberLook,
        heritage: u32,
        gender: u32,
        options: Arc<ChargenOptions>,
        pal_sets: &impl ChargenPalSetSource,
        seed: u64,
    ) {
        let mut creation = CharacterCreation::new(options, Box::new(SeededRandom::new(seed)));
        creation.begin(Generation::default());
        creation.try_randomize_character();
        creation.try_set_appearance_index(AppearanceSlot::HeadgearStyle, UNSET);
        creation.try_select_gender(gender);
        creation.try_select_heritage(heritage);
        // Again: the random heritage may not have had the gender.
        creation.try_select_gender(gender);
        let mut open = Open {
            creation,
            heritage,
            gender,
            toggle: BarberToggle::of(heritage),
            toggle_on: barber::toggle_starts_on(heritage, look),
            preview_setup: barber::opening_preview_setup(heritage, look),
        };
        if let Some(g) = open.gender_options() {
            let c = choices_from_look(g, look, pal_sets);
            let creation = &mut open.creation;
            for (slot, index) in [
                (AppearanceSlot::HairStyle, c.hair_style),
                (AppearanceSlot::EyesStrip, c.eyes_strip),
                (AppearanceSlot::NoseStrip, c.nose_strip),
                (AppearanceSlot::MouthStrip, c.mouth_strip),
                (AppearanceSlot::HairColor, c.hair_color),
                (AppearanceSlot::EyeColor, c.eye_color),
            ] {
                creation.try_set_appearance_index(slot, index);
            }
            creation.try_set_shade(ShadeSlot::Hair, c.hair_shade);
            creation.try_set_shade(ShadeSlot::Skin, c.skin_shade);
        }
        open.creation.drain_deltas();
        self.open = Some(open);
        self.openings += 1;
        self.revision += 1;
    }

    /// Run a window request; a finish answers with the look to send
    /// (`None` when nothing is sent).
    pub fn command(
        &mut self,
        command: BarberCommand,
        pal_sets: &impl ChargenPalSetSource,
    ) -> Option<BarberLook> {
        let open = self.open.as_mut()?;
        let changed = match command {
            BarberCommand::SetAppearance(slot, index) => {
                BARBER_SLOTS.contains(&slot) && open.creation.try_set_appearance_index(slot, index)
            }
            BarberCommand::SetShade(slot, value) => {
                matches!(slot, ShadeSlot::Hair | ShadeSlot::Skin)
                    && open.creation.try_set_shade(slot, value)
            }
            BarberCommand::SetToggle(on) => {
                if open.toggle.is_none() {
                    false
                } else {
                    let setup = open
                        .gender_options()
                        .map_or(NO_ID, |g| look_setup(g, open.appearance().hair_style));
                    open.toggle_on = on;
                    open.preview_setup =
                        barber::toggled_preview_setup(open.heritage, open.gender, setup, on);
                    true
                }
            }
            BarberCommand::Finish => {
                let send = open.gender_options().and_then(|g| {
                    barber::finish(
                        open.heritage,
                        open.gender,
                        g,
                        &open.choices(),
                        open.toggle_on,
                        pal_sets,
                    )
                });
                self.close();
                return send.map(|f| f.look);
            }
            BarberCommand::Cancel => {
                self.close();
                return None;
            }
        };
        open.creation.drain_deltas();
        if changed {
            self.revision += 1;
        }
        None
    }

    fn close(&mut self) {
        if self.open.take().is_some() {
            self.revision += 1;
        }
    }

    /// The character left the world: the barber closes.
    pub fn reset_session(&mut self) {
        self.close();
    }
}

macro_rules! copy_look {
    ($from:expr, $to:ident) => {{
        let l = $from;
        $to {
            base_palette: l.base_palette,
            head_object: l.head_object,
            hair_texture: l.hair_texture,
            default_hair_texture: l.default_hair_texture,
            eyes_texture: l.eyes_texture,
            default_eyes_texture: l.default_eyes_texture,
            nose_texture: l.nose_texture,
            default_nose_texture: l.default_nose_texture,
            mouth_texture: l.mouth_texture,
            default_mouth_texture: l.default_mouth_texture,
            skin_palette: l.skin_palette,
            hair_palette: l.hair_palette,
            eyes_palette: l.eyes_palette,
            setup: l.setup,
            option1: l.option1,
            option2: l.option2,
        }
    }};
}

/// The look the server sent, as the barber's rules read it.
#[must_use]
pub fn core_look(look: &ac_proto::barber::BarberLook) -> BarberLook {
    copy_look!(look, BarberLook)
}

/// The look chosen, as the finish message carries it.
#[must_use]
pub fn wire_look(look: &BarberLook) -> ac_proto::barber::BarberLook {
    use ac_proto::barber::BarberLook as Wire;
    copy_look!(look, Wire)
}

#[cfg(test)]
#[path = "barber_tests.rs"]
pub(crate) mod tests;
