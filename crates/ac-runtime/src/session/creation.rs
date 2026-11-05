//! Character creation: heritage, gender and profession template, the
//! attribute points with their locks and the balancing that keeps them in
//! budget, skill training with credits, the look, starting town, name and
//! slot, the randomize buttons, and the finish gate that builds the create
//! request. The answer is recorded as the created character or the
//! rejection.
//!
//! One per connection, active while character selection is. Nothing here
//! sends: the session sends the request [`CharacterCreation::try_begin_finish`]
//! hands back.

use std::sync::Arc;

use ac_core::chargen::attributes::{ATTRIBUTE_MAX, ATTRIBUTE_MIN};
use ac_core::chargen::{
    ChargenAttributeValues, ChargenGenderOptions, ChargenHeritageGroup, ChargenHeritageOptions,
    ChargenOptions, ChargenSkillAdvancementClass, ChargenSkillAdvancementSet, compute_spent,
};
use ac_proto::character::{
    CharGenVerificationResponse, CharacterAppearance, CharacterAttributes, CharacterCreateRequest,
    SKILL_ADVANCEMENT_CLASS_COUNT, chargen_code,
};

use super::generation::Generation;

/// The six attributes, numbered as the client numbers them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ChargenAttribute {
    /// Strength.
    Strength = 1,
    /// Endurance.
    Endurance = 2,
    /// Quickness.
    Quickness = 3,
    /// Coordination.
    Coordination = 4,
    /// Focus.
    Focus = 5,
    /// Self.
    Self_ = 6,
}

/// The order the balancing walks the attributes in when one is raised past
/// the budget.
const BALANCE_ORDER: [ChargenAttribute; 6] = [
    ChargenAttribute::Strength,
    ChargenAttribute::Endurance,
    ChargenAttribute::Coordination,
    ChargenAttribute::Quickness,
    ChargenAttribute::Focus,
    ChargenAttribute::Self_,
];

impl ChargenAttribute {
    fn bit(self) -> u32 {
        1 << (self as u32 - 1)
    }
}

/// A face, hair or clothing choice.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AppearanceSlot {
    /// Eyes.
    EyesStrip,
    /// Nose.
    NoseStrip,
    /// Mouth.
    MouthStrip,
    /// Hair style.
    HairStyle,
    /// Hair color.
    HairColor,
    /// Eye color.
    EyeColor,
    /// Headgear style.
    HeadgearStyle,
    /// Headgear color.
    HeadgearColor,
    /// Shirt style.
    ShirtStyle,
    /// Shirt color.
    ShirtColor,
    /// Trousers style.
    TrousersStyle,
    /// Trousers color.
    TrousersColor,
    /// Footwear style.
    FootwearStyle,
    /// Footwear color.
    FootwearColor,
}

/// A shade slider.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ShadeSlot {
    /// Skin.
    Skin,
    /// Hair.
    Hair,
    /// Headgear.
    Headgear,
    /// Shirt.
    Shirt,
    /// Trousers.
    Trousers,
    /// Footwear.
    Footwear,
}

/// The look being built. Indexes are into the gender's lists; [`UNSET`]
/// means "none chosen" (no headgear, for instance).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CreationAppearance {
    /// Eyes.
    pub eyes_strip: u32,
    /// Nose.
    pub nose_strip: u32,
    /// Mouth.
    pub mouth_strip: u32,
    /// Hair style.
    pub hair_style: u32,
    /// Hair color.
    pub hair_color: u32,
    /// Eye color.
    pub eye_color: u32,
    /// Headgear style.
    pub headgear_style: u32,
    /// Headgear color.
    pub headgear_color: u32,
    /// Shirt style.
    pub shirt_style: u32,
    /// Shirt color.
    pub shirt_color: u32,
    /// Trousers style.
    pub trousers_style: u32,
    /// Trousers color.
    pub trousers_color: u32,
    /// Footwear style.
    pub footwear_style: u32,
    /// Footwear color.
    pub footwear_color: u32,
    /// Skin shade, 0..1 ([`UNSET_SHADE`] when not chosen).
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

/// No choice made.
pub const UNSET: u32 = 0xFFFF_FFFF;
/// No shade chosen.
pub const UNSET_SHADE: f64 = -1.0;
/// No template chosen.
pub const TEMPLATE_UNSET: u32 = 0xFFFF_FFFF;

impl Default for CreationAppearance {
    fn default() -> Self {
        Self {
            eyes_strip: UNSET,
            nose_strip: UNSET,
            mouth_strip: UNSET,
            hair_style: UNSET,
            hair_color: UNSET,
            eye_color: UNSET,
            headgear_style: UNSET,
            headgear_color: UNSET,
            shirt_style: UNSET,
            shirt_color: UNSET,
            trousers_style: UNSET,
            trousers_color: UNSET,
            footwear_style: UNSET,
            footwear_color: UNSET,
            skin_shade: UNSET_SHADE,
            hair_shade: UNSET_SHADE,
            headgear_shade: UNSET_SHADE,
            shirt_shade: UNSET_SHADE,
            trousers_shade: UNSET_SHADE,
            footwear_shade: UNSET_SHADE,
        }
    }
}

impl CreationAppearance {
    /// The index chosen for a slot.
    #[must_use]
    pub fn get(&self, slot: AppearanceSlot) -> u32 {
        match slot {
            AppearanceSlot::EyesStrip => self.eyes_strip,
            AppearanceSlot::NoseStrip => self.nose_strip,
            AppearanceSlot::MouthStrip => self.mouth_strip,
            AppearanceSlot::HairStyle => self.hair_style,
            AppearanceSlot::HairColor => self.hair_color,
            AppearanceSlot::EyeColor => self.eye_color,
            AppearanceSlot::HeadgearStyle => self.headgear_style,
            AppearanceSlot::HeadgearColor => self.headgear_color,
            AppearanceSlot::ShirtStyle => self.shirt_style,
            AppearanceSlot::ShirtColor => self.shirt_color,
            AppearanceSlot::TrousersStyle => self.trousers_style,
            AppearanceSlot::TrousersColor => self.trousers_color,
            AppearanceSlot::FootwearStyle => self.footwear_style,
            AppearanceSlot::FootwearColor => self.footwear_color,
        }
    }

    fn set(&mut self, slot: AppearanceSlot, value: u32) {
        let field = match slot {
            AppearanceSlot::EyesStrip => &mut self.eyes_strip,
            AppearanceSlot::NoseStrip => &mut self.nose_strip,
            AppearanceSlot::MouthStrip => &mut self.mouth_strip,
            AppearanceSlot::HairStyle => &mut self.hair_style,
            AppearanceSlot::HairColor => &mut self.hair_color,
            AppearanceSlot::EyeColor => &mut self.eye_color,
            AppearanceSlot::HeadgearStyle => &mut self.headgear_style,
            AppearanceSlot::HeadgearColor => &mut self.headgear_color,
            AppearanceSlot::ShirtStyle => &mut self.shirt_style,
            AppearanceSlot::ShirtColor => &mut self.shirt_color,
            AppearanceSlot::TrousersStyle => &mut self.trousers_style,
            AppearanceSlot::TrousersColor => &mut self.trousers_color,
            AppearanceSlot::FootwearStyle => &mut self.footwear_style,
            AppearanceSlot::FootwearColor => &mut self.footwear_color,
        };
        *field = value;
    }

    fn set_shade(&mut self, slot: ShadeSlot, value: f64) {
        let field = match slot {
            ShadeSlot::Skin => &mut self.skin_shade,
            ShadeSlot::Hair => &mut self.hair_shade,
            ShadeSlot::Headgear => &mut self.headgear_shade,
            ShadeSlot::Shirt => &mut self.shirt_shade,
            ShadeSlot::Trousers => &mut self.trousers_shade,
            ShadeSlot::Footwear => &mut self.footwear_shade,
        };
        *field = value;
    }
}

/// Why finishing was refused before anything was sent.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct LocalRefusal {
    /// No name.
    pub no_name: bool,
    /// Attribute points left (and not confirmed).
    pub attribute_credits_unspent: bool,
    /// A create is already waiting for its answer.
    pub already_pending: bool,
    /// Every slot is taken.
    pub roster_full: bool,
    /// No heritage or gender chosen.
    pub heritage_or_gender_unset: bool,
}

impl LocalRefusal {
    /// True when any reason applies.
    #[must_use]
    pub fn any(&self) -> bool {
        self.no_name
            || self.attribute_credits_unspent
            || self.already_pending
            || self.roster_full
            || self.heritage_or_gender_unset
    }
}

/// The character the server created.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreatedIdentity {
    /// Object id.
    pub guid: u32,
    /// Name as the server spelled it.
    pub name: String,
}

/// The server's refusal of a create.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreationRejection {
    /// The answer code.
    pub raw_code: u32,
    /// The code's name ("NameInUse", ...).
    pub reason: String,
    /// The name that was tried.
    pub attempted_name: String,
}

/// What changed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CreationDeltaKind {
    /// Everything was cleared.
    Reset,
    /// A choice changed.
    StateChanged,
    /// Finishing was refused locally.
    FinishRefused,
    /// The create request was handed out to be sent.
    FinishSent,
    /// The server created the character.
    Created,
    /// The server refused the create.
    CreationFailed,
    /// The rejection was dismissed.
    RejectionAcknowledged,
}

/// One change.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CreationDelta {
    /// The generation.
    pub generation: Generation,
    /// Increases by one per delta.
    pub sequence: u64,
    /// The revision after the change.
    pub revision: u64,
    /// What changed.
    pub kind: CreationDeltaKind,
}

/// The whole creation state as one value.
#[derive(Debug, Clone, PartialEq)]
pub struct CreationSnapshot {
    /// The generation.
    pub generation: Generation,
    /// Creation is possible (character selection is shown).
    pub is_active: bool,
    /// Bumped by every change.
    pub revision: u64,
    /// Heritage (0 none).
    pub heritage_id: u32,
    /// Gender key (0 none).
    pub gender_key: u32,
    /// The look.
    pub appearance: CreationAppearance,
    /// Template index ([`TEMPLATE_UNSET`] none).
    pub template: u32,
    /// The attributes.
    pub attributes: ChargenAttributeValues,
    /// Locked attributes, bit per attribute (bit 0 strength).
    pub attribute_lock_mask: u32,
    /// The heritage's attribute points.
    pub total_attribute_credits: u32,
    /// Points not spent.
    pub remaining_attribute_credits: i32,
    /// The heritage's skill credits.
    pub total_skill_credits: u32,
    /// Credits not spent.
    pub remaining_skill_credits: i32,
    /// Every skill's training (55 slots, wire values).
    pub skills: Vec<ChargenSkillAdvancementClass>,
    /// Name.
    pub name: String,
    /// Starting town (-1 none).
    pub start_area: i32,
    /// Slot.
    pub slot: u32,
    /// A create waits for its answer.
    pub verification_pending: bool,
    /// The last local refusal.
    pub last_local_refusal: LocalRefusal,
    /// The last server refusal, until dismissed.
    pub last_rejection: Option<CreationRejection>,
    /// The last character created.
    pub last_created: Option<CreatedIdentity>,
}

/// The randomness the randomize buttons and the starting town use: a
/// number in `0..max` (0 when `max` is 0).
pub trait ChargenRandom: Send + std::fmt::Debug {
    /// A number in `0..max`.
    fn next(&mut self, max: u32) -> u32;
}

/// A small seeded generator (SplitMix64).
#[derive(Debug, Clone)]
pub struct SeededRandom(u64);

impl SeededRandom {
    /// Seeded with `seed`.
    #[must_use]
    pub fn new(seed: u64) -> Self {
        Self(seed)
    }

    /// Seeded from the clock.
    #[must_use]
    pub fn from_clock() -> Self {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |d| d.as_nanos());
        Self(u64::try_from(nanos & u128::from(u64::MAX)).unwrap_or(0))
    }
}

impl ChargenRandom for SeededRandom {
    fn next(&mut self, max: u32) -> u32 {
        if max == 0 {
            return 0;
        }
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^= z >> 31;
        u32::try_from(z % u64::from(max)).unwrap_or(0)
    }
}

/// The create request the finish gate builds, ready for the wire.
#[derive(Debug, Clone, PartialEq)]
pub struct FinishRequest {
    /// The request.
    pub request: CharacterCreateRequest,
    /// The 55 skill advancement classes.
    pub skill_advancement_classes: [u32; SKILL_ADVANCEMENT_CLASS_COUNT],
}

/// The character-creation owner.
#[derive(Debug)]
pub struct CharacterCreation {
    options: Arc<ChargenOptions>,
    random: Box<dyn ChargenRandom>,
    generation: Generation,
    active: bool,
    revision: u64,
    sequence: u64,
    heritage_id: u32,
    gender_key: u32,
    appearance: CreationAppearance,
    template: u32,
    attributes: ChargenAttributeValues,
    attribute_lock_mask: u32,
    total_attribute_credits: u32,
    remaining_attribute_credits: i32,
    total_skill_credits: u32,
    remaining_skill_credits: i32,
    skills: ChargenSkillAdvancementSet,
    attribute_balance_cursor: ChargenAttribute,
    name: String,
    start_area: i32,
    slot: u32,
    verification_pending: bool,
    last_local_refusal: LocalRefusal,
    last_rejection: Option<CreationRejection>,
    last_created: Option<CreatedIdentity>,
    deltas: Vec<CreationDelta>,
}

impl CharacterCreation {
    /// Inactive, choosing from `options`.
    #[must_use]
    pub fn new(options: Arc<ChargenOptions>, random: Box<dyn ChargenRandom>) -> Self {
        Self {
            options,
            random,
            generation: Generation::default(),
            active: false,
            revision: 0,
            sequence: 0,
            heritage_id: 0,
            gender_key: 0,
            appearance: CreationAppearance::default(),
            template: TEMPLATE_UNSET,
            attributes: ChargenAttributeValues::default(),
            attribute_lock_mask: 0,
            total_attribute_credits: 0,
            remaining_attribute_credits: 0,
            total_skill_credits: 0,
            remaining_skill_credits: 0,
            skills: ChargenSkillAdvancementSet::new(),
            attribute_balance_cursor: ChargenAttribute::Strength,
            name: String::new(),
            start_area: -1,
            slot: 0,
            verification_pending: false,
            last_local_refusal: LocalRefusal::default(),
            last_rejection: None,
            last_created: None,
            deltas: Vec::new(),
        }
    }

    /// The options choices come from.
    #[must_use]
    pub fn options(&self) -> &Arc<ChargenOptions> {
        &self.options
    }

    /// Replace the options. Refused while creation is active.
    ///
    /// # Errors
    /// Creation is active.
    pub fn install_options(&mut self, options: Arc<ChargenOptions>) -> Result<(), &'static str> {
        if self.active {
            return Err("chargen options cannot change while character creation is active");
        }
        self.options = options;
        Ok(())
    }

    /// The state as one value.
    #[must_use]
    pub fn snapshot(&self) -> CreationSnapshot {
        CreationSnapshot {
            generation: self.generation,
            is_active: self.active,
            revision: self.revision,
            heritage_id: self.heritage_id,
            gender_key: self.gender_key,
            appearance: self.appearance,
            template: self.template,
            attributes: self.attributes,
            attribute_lock_mask: self.attribute_lock_mask,
            total_attribute_credits: self.total_attribute_credits,
            remaining_attribute_credits: self.remaining_attribute_credits,
            total_skill_credits: self.total_skill_credits,
            remaining_skill_credits: self.remaining_skill_credits,
            skills: (0..ChargenSkillAdvancementSet::SLOT_COUNT as u32)
                .map(|id| self.skills.get(id))
                .collect(),
            name: self.name.clone(),
            start_area: self.start_area,
            slot: self.slot,
            verification_pending: self.verification_pending,
            last_local_refusal: self.last_local_refusal,
            last_rejection: self.last_rejection.clone(),
            last_created: self.last_created.clone(),
        }
    }

    /// Bumped by every change.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// A skill's training.
    #[must_use]
    pub fn skill_level(&self, skill_id: u32) -> ChargenSkillAdvancementClass {
        self.skills.get(skill_id)
    }

    /// Take the deltas recorded since the last call, oldest first.
    pub fn drain_deltas(&mut self) -> Vec<CreationDelta> {
        std::mem::take(&mut self.deltas)
    }

    /// Character selection began in `generation`: creation is possible.
    pub fn begin(&mut self, generation: Generation) {
        self.generation = generation;
        self.active = true;
        self.clear_session_state();
        self.revision += 1;
        self.publish(CreationDeltaKind::Reset);
    }

    /// A character entered the world: creation ends.
    pub fn complete_enter(&mut self) {
        if !self.active {
            return;
        }
        self.active = false;
        self.revision += 1;
        self.publish(CreationDeltaKind::StateChanged);
    }

    /// Clear everything for `generation`; inactive.
    pub fn reset(&mut self, generation: Generation) {
        self.generation = generation;
        self.active = false;
        self.clear_session_state();
        self.revision += 1;
        self.publish(CreationDeltaKind::Reset);
    }

    // -- Heritage, gender, template ---------------------------------------

    /// Choose a heritage: its budgets, its template, a random primary
    /// starting town, the look constrained to the gender's lists.
    pub fn try_select_heritage(&mut self, heritage_id: u32) -> bool {
        let options = Arc::clone(&self.options);
        let Some(heritage) = options.try_get_heritage(heritage_id) else {
            return false;
        };
        if !self.active {
            return false;
        }
        self.set_heritage_group(heritage_id, heritage);
        self.changed();
        true
    }

    fn set_heritage_group(&mut self, heritage_id: u32, heritage: &ChargenHeritageOptions) {
        self.heritage_id = heritage_id;
        self.total_attribute_credits = heritage.attribute_credits;
        self.total_skill_credits = heritage.skill_credits;
        self.remaining_skill_credits = i32::try_from(heritage.skill_credits).unwrap_or(i32::MAX);
        self.apply_template(heritage);
        self.randomize_start_area(heritage);
        self.constrain_appearance_by_gender();
        self.recompute_remaining_attribute_credits();
        if self.recompute_skill_spend(heritage) < 0 {
            self.reset_skill_levels(heritage);
        }
    }

    /// Choose a gender of the chosen heritage.
    pub fn try_select_gender(&mut self, gender_key: u32) -> bool {
        if !self.active || self.heritage_id == 0 {
            return false;
        }
        let known = self
            .options
            .try_get_heritage(self.heritage_id)
            .is_some_and(|h| {
                i32::try_from(gender_key).is_ok_and(|key| h.genders_by_key.contains_key(&key))
            });
        if !known {
            return false;
        }
        self.set_gender(gender_key);
        self.changed();
        true
    }

    fn set_gender(&mut self, gender_key: u32) {
        self.gender_key = gender_key;
        self.constrain_appearance_by_gender();
    }

    /// Choose a profession template: its attributes and skills.
    pub fn try_select_template(&mut self, template_index: u32) -> bool {
        if !self.active || self.heritage_id == 0 || self.gender_key == 0 {
            return false;
        }
        let options = Arc::clone(&self.options);
        let Some(heritage) = options.try_get_heritage(self.heritage_id) else {
            return false;
        };
        if template_index as usize >= heritage.templates.len() {
            return false;
        }
        self.template = template_index;
        self.apply_template(heritage);
        self.recompute_remaining_attribute_credits();
        self.changed();
        true
    }

    fn apply_template(&mut self, heritage: &ChargenHeritageOptions) {
        self.attribute_lock_mask = 0;
        if is_olthoi(self.heritage_id) {
            self.template = 0;
        }
        if self.heritage_id == 0 || self.gender_key == 0 || self.template == TEMPLATE_UNSET {
            return;
        }
        let Some(row) = heritage.templates.get(self.template as usize) else {
            self.template = TEMPLATE_UNSET;
            return;
        };
        self.attributes = row.attributes;
        // Each attribute setter recomputes the points left, so a template
        // applied by the random roll leaves them right too.
        self.recompute_remaining_attribute_credits();
        self.reset_skill_levels(heritage);
        for &skill_id in &row.normal_skills {
            self.apply_template_skill_entry(
                heritage,
                skill_id,
                ChargenSkillAdvancementClass::Trained,
            );
        }
        for &skill_id in &row.primary_skills {
            self.apply_template_skill_entry(
                heritage,
                skill_id,
                ChargenSkillAdvancementClass::Specialized,
            );
        }
    }

    fn apply_template_skill_entry(
        &mut self,
        heritage: &ChargenHeritageOptions,
        skill_id: u32,
        target: ChargenSkillAdvancementClass,
    ) {
        if skill_id == 0 || skill_id as usize >= ChargenSkillAdvancementSet::SLOT_COUNT {
            return;
        }
        let previous = self.skills.get(skill_id);
        let mut remaining = self.remaining_skill_credits;
        match (previous, self.skill_cost(heritage, skill_id)) {
            (ChargenSkillAdvancementClass::Trained, Some((trained, _))) => remaining += trained,
            (ChargenSkillAdvancementClass::Specialized, Some((_, specialized))) => {
                remaining += specialized;
            }
            _ => {}
        }
        let Some((trained, specialized)) = self.skill_cost(heritage, skill_id) else {
            return;
        };
        remaining -= match target {
            ChargenSkillAdvancementClass::Specialized => specialized,
            ChargenSkillAdvancementClass::Trained => trained,
            _ => 0,
        };
        if remaining < 0 {
            return;
        }
        let _ = self.skills.set(skill_id, target);
        self.remaining_skill_credits = remaining;
    }

    fn reset_skill_levels(&mut self, heritage: &ChargenHeritageOptions) {
        self.remaining_skill_credits = i32::try_from(self.total_skill_credits).unwrap_or(i32::MAX);
        if self.heritage_id == 0 || self.gender_key == 0 {
            return;
        }
        for skill_id in 1..ChargenSkillAdvancementSet::SLOT_COUNT as u32 {
            let Some((trained, specialized)) = self.skill_cost(heritage, skill_id) else {
                continue;
            };
            let class = if trained > 0 {
                ChargenSkillAdvancementClass::Untrained
            } else if specialized <= 0 {
                ChargenSkillAdvancementClass::Specialized
            } else {
                ChargenSkillAdvancementClass::Trained
            };
            let _ = self.skills.set(skill_id, class);
        }
    }

    /// The heritage's price for a skill, else the skill table's.
    fn skill_cost(&self, heritage: &ChargenHeritageOptions, skill_id: u32) -> Option<(i32, i32)> {
        heritage
            .skill_costs_by_skill_id
            .get(&skill_id)
            .or_else(|| self.options.global_skill_costs_by_skill_id.get(&skill_id))
            .map(|c| (c.normal_cost, c.primary_cost))
    }

    fn recompute_skill_spend(&self, heritage: &ChargenHeritageOptions) -> i64 {
        i64::from(self.total_skill_credits)
            - compute_spent(
                &self.skills,
                &heritage.skill_costs_by_skill_id,
                &self.options.global_skill_costs_by_skill_id,
            )
    }

    fn randomize_start_area(&mut self, heritage: &ChargenHeritageOptions) {
        let count = heritage.primary_start_area_indices.len();
        if count == 0 {
            return;
        }
        let pick = self.random.next(u32::try_from(count).unwrap_or(u32::MAX)) as usize;
        let candidate = heritage.primary_start_area_indices[pick];
        self.start_area = if usize::try_from(candidate)
            .is_ok_and(|index| index < self.options.starter_areas.len())
        {
            candidate
        } else {
            -1
        };
    }

    // -- Attributes --------------------------------------------------------

    /// Set an attribute, clamped to 10..=100 and to what the budget allows
    /// (locked attributes and the floor of the others held back); when the
    /// total then exceeds the budget, other unlocked attributes give points
    /// back one at a time, in turn.
    pub fn try_set_attribute(&mut self, attribute: ChargenAttribute, requested: i32) -> bool {
        if !self.active || self.heritage_id == 0 {
            return false;
        }
        let current = self.attribute(attribute);
        let mut clamped = requested.clamp(ATTRIBUTE_MIN, ATTRIBUTE_MAX);
        if clamped > current {
            let abs_remaining = self.abs_remaining_credits(attribute);
            if clamped - current > abs_remaining {
                clamped = current + abs_remaining;
            }
            if clamped < current {
                clamped = current;
            }
        }
        self.set_attribute_raw(attribute, clamped);
        self.balance_attributes(attribute);
        self.recompute_remaining_attribute_credits();
        self.changed();
        true
    }

    /// Lock or unlock an attribute against balancing.
    pub fn try_set_attribute_lock(&mut self, attribute: ChargenAttribute, locked: bool) -> bool {
        if !self.active {
            return false;
        }
        if locked {
            self.attribute_lock_mask |= attribute.bit();
        } else {
            self.attribute_lock_mask &= !attribute.bit();
        }
        self.changed();
        true
    }

    fn abs_remaining_credits(&self, queried: ChargenAttribute) -> i32 {
        let mut total = i32::try_from(self.total_attribute_credits).unwrap_or(i32::MAX);
        for id in BALANCE_ORDER {
            total -= if self.is_locked(id) || id == queried {
                self.attribute(id)
            } else {
                ATTRIBUTE_MIN
            };
        }
        total
    }

    fn balance_attributes(&mut self, excluded: ChargenAttribute) {
        let mut over = self.attribute_total()
            - i32::try_from(self.total_attribute_credits).unwrap_or(i32::MAX);
        if over <= 0 {
            return;
        }
        let mut started = false;
        let mut guard = 6 * (ATTRIBUTE_MAX - ATTRIBUTE_MIN) + 1;
        while guard > 0 {
            guard -= 1;
            for (index, id) in BALANCE_ORDER.into_iter().enumerate() {
                if !started {
                    if self.attribute_balance_cursor != id {
                        continue;
                    }
                    started = true;
                }
                if id == excluded {
                    continue;
                }
                let value = self.attribute(id);
                if value > ATTRIBUTE_MIN && !self.is_locked(id) {
                    over -= 1;
                    self.set_attribute_raw(id, value - 1);
                    if over <= 0 {
                        self.attribute_balance_cursor = BALANCE_ORDER[(index + 1) % 6];
                        return;
                    }
                }
            }
            started = true;
        }
    }

    fn attribute_total(&self) -> i32 {
        i32::try_from(self.attributes.total()).unwrap_or(i32::MAX)
    }

    fn attribute(&self, id: ChargenAttribute) -> i32 {
        let a = &self.attributes;
        match id {
            ChargenAttribute::Strength => a.strength,
            ChargenAttribute::Endurance => a.endurance,
            ChargenAttribute::Quickness => a.quickness,
            ChargenAttribute::Coordination => a.coordination,
            ChargenAttribute::Focus => a.focus,
            ChargenAttribute::Self_ => a.self_,
        }
    }

    fn set_attribute_raw(&mut self, id: ChargenAttribute, value: i32) {
        let a = &mut self.attributes;
        let field = match id {
            ChargenAttribute::Strength => &mut a.strength,
            ChargenAttribute::Endurance => &mut a.endurance,
            ChargenAttribute::Quickness => &mut a.quickness,
            ChargenAttribute::Coordination => &mut a.coordination,
            ChargenAttribute::Focus => &mut a.focus,
            ChargenAttribute::Self_ => &mut a.self_,
        };
        *field = value;
    }

    fn is_locked(&self, id: ChargenAttribute) -> bool {
        self.attribute_lock_mask & id.bit() != 0
    }

    fn recompute_remaining_attribute_credits(&mut self) {
        self.remaining_attribute_credits = i32::try_from(self.total_attribute_credits)
            .unwrap_or(i32::MAX)
            - self.attribute_total();
    }

    // -- Skills ------------------------------------------------------------

    /// Train a skill.
    pub fn try_train_skill(&mut self, skill_id: u32) -> bool {
        self.try_set_skill_level(skill_id, ChargenSkillAdvancementClass::Trained)
    }

    /// Specialize a skill.
    pub fn try_specialize_skill(&mut self, skill_id: u32) -> bool {
        self.try_set_skill_level(skill_id, ChargenSkillAdvancementClass::Specialized)
    }

    /// Untrain a skill.
    pub fn try_untrain_skill(&mut self, skill_id: u32) -> bool {
        self.try_set_skill_level(skill_id, ChargenSkillAdvancementClass::Untrained)
    }

    /// Move a skill to `target`: the cost of the old level comes back and
    /// the new level's is charged (specializing costs the specialized price,
    /// not both); refused for a skill nothing prices and when credits run
    /// out.
    fn try_set_skill_level(&mut self, skill_id: u32, target: ChargenSkillAdvancementClass) -> bool {
        if skill_id == 0 || skill_id as usize >= ChargenSkillAdvancementSet::SLOT_COUNT {
            return false;
        }
        if !self.active || self.heritage_id == 0 || self.gender_key == 0 {
            return false;
        }
        let options = Arc::clone(&self.options);
        let Some(heritage) = options.try_get_heritage(self.heritage_id) else {
            return false;
        };
        let Some((trained, specialized)) = self.skill_cost(heritage, skill_id) else {
            return false;
        };
        let previous = self.skills.get(skill_id);
        if previous == target {
            return true;
        }
        let price = |class| match class {
            ChargenSkillAdvancementClass::Trained => trained,
            ChargenSkillAdvancementClass::Specialized => specialized,
            _ => 0,
        };
        let remaining = self.remaining_skill_credits + price(previous) - price(target);
        if remaining < 0 {
            return false;
        }
        let _ = self.skills.set(skill_id, target);
        self.remaining_skill_credits = remaining;
        self.changed();
        true
    }

    // -- Appearance --------------------------------------------------------

    /// Choose an index for a look slot ([`UNSET`] clears it).
    pub fn try_set_appearance_index(&mut self, slot: AppearanceSlot, index: u32) -> bool {
        if !self.active {
            return false;
        }
        let Some(count) = self.gender().map(|g| slot_count(slot, g)) else {
            return false;
        };
        if index != UNSET && index as usize >= count {
            return false;
        }
        self.appearance.set(slot, index);
        self.changed();
        true
    }

    /// Set a shade, clamped to 0..=1.
    pub fn try_set_shade(&mut self, slot: ShadeSlot, value: f64) -> bool {
        if !self.active {
            return false;
        }
        self.appearance.set_shade(slot, value.clamp(0.0, 1.0));
        self.changed();
        true
    }

    fn gender(&self) -> Option<&ChargenGenderOptions> {
        if self.heritage_id == 0 || self.gender_key == 0 {
            return None;
        }
        let key = i32::try_from(self.gender_key).ok()?;
        self.options
            .try_get_heritage(self.heritage_id)?
            .genders_by_key
            .get(&key)
    }

    fn constrain_appearance_by_gender(&mut self) {
        let Some(counts) = self
            .gender()
            .map(|g| ALL_SLOTS.map(|slot| slot_count(slot, g)))
        else {
            self.appearance = CreationAppearance::default();
            return;
        };
        for (slot, count) in ALL_SLOTS.into_iter().zip(counts) {
            let value = self.appearance.get(slot);
            self.appearance.set(slot, clamp_index(value, count));
        }
    }

    fn roll_dice(&mut self, min: i32, max: i32) -> i32 {
        if min == max {
            return min;
        }
        let (lo, hi) = (min.min(max), min.max(max));
        lo + i32::try_from(
            self.random
                .next(u32::try_from(hi - lo + 1).unwrap_or(u32::MAX)),
        )
        .unwrap_or(0)
    }

    fn randomize_index_excluding(&mut self, count: usize, exclude: u32) -> u32 {
        if count <= 1 {
            return 0;
        }
        let max = u32::try_from(count).unwrap_or(u32::MAX);
        loop {
            let result = self.random.next(max);
            if result != exclude {
                return result;
            }
        }
    }

    fn roll_shade(&mut self) -> f64 {
        f64::from(self.random.next(32768)) * (1.0 / 32767.0)
    }

    fn randomize_one(&mut self, slot: AppearanceSlot, count: usize) {
        if count > 0 {
            let current = self.appearance.get(slot);
            let value = self.randomize_index_excluding(count, current);
            self.appearance.set(slot, value);
        }
    }

    fn randomize_appearance_inner(&mut self) {
        let Some(counts) = self
            .gender()
            .map(|g| ALL_SLOTS.map(|slot| slot_count(slot, g)))
        else {
            return;
        };
        let count = |slot: AppearanceSlot| counts[slot as usize];
        self.randomize_one(AppearanceSlot::EyesStrip, count(AppearanceSlot::EyesStrip));
        self.randomize_one(AppearanceSlot::NoseStrip, count(AppearanceSlot::NoseStrip));
        self.randomize_one(
            AppearanceSlot::MouthStrip,
            count(AppearanceSlot::MouthStrip),
        );
        self.appearance.skin_shade = self.roll_shade();
        self.appearance.hair_shade = self.roll_shade();
        self.randomize_one(AppearanceSlot::HairColor, count(AppearanceSlot::HairColor));
        self.randomize_one(AppearanceSlot::EyeColor, count(AppearanceSlot::EyeColor));
        self.randomize_one(AppearanceSlot::HairStyle, count(AppearanceSlot::HairStyle));
    }

    fn randomize_headgear(&mut self, exclude_current: bool) {
        let Some((styles, colors)) = self
            .gender()
            .map(|g| (g.headgears.len(), g.clothing_colors.len()))
        else {
            return;
        };
        if styles > 0 {
            let current = self.appearance.headgear_style;
            let current_plus_one = if current == UNSET { 0 } else { current + 1 };
            let rolled = if exclude_current {
                self.randomize_index_excluding(styles + 1, current_plus_one)
            } else {
                self.random
                    .next(u32::try_from(styles + 1).unwrap_or(u32::MAX))
            };
            self.appearance.headgear_style = if rolled == 0 { UNSET } else { rolled - 1 };
        }
        self.randomize_one(AppearanceSlot::HeadgearColor, colors);
        self.appearance.headgear_shade = self.roll_shade();
    }

    fn randomize_garment(
        &mut self,
        style: AppearanceSlot,
        color: AppearanceSlot,
        shade: ShadeSlot,
    ) {
        let Some((styles, colors)) = self
            .gender()
            .map(|g| (slot_count(style, g), slot_count(color, g)))
        else {
            return;
        };
        self.randomize_one(style, styles);
        self.randomize_one(color, colors);
        let value = self.roll_shade();
        self.appearance.set_shade(shade, value);
    }

    fn randomize_shirt_trousers_footwear(&mut self) {
        self.randomize_garment(
            AppearanceSlot::ShirtStyle,
            AppearanceSlot::ShirtColor,
            ShadeSlot::Shirt,
        );
        self.randomize_garment(
            AppearanceSlot::TrousersStyle,
            AppearanceSlot::TrousersColor,
            ShadeSlot::Trousers,
        );
        self.randomize_garment(
            AppearanceSlot::FootwearStyle,
            AppearanceSlot::FootwearColor,
            ShadeSlot::Footwear,
        );
    }

    fn randomize_template(&mut self) {
        if self.heritage_id == 0 || self.gender_key == 0 {
            return;
        }
        let options = Arc::clone(&self.options);
        let Some(heritage) = options.try_get_heritage(self.heritage_id) else {
            return;
        };
        if is_olthoi(self.heritage_id) {
            self.apply_template(heritage);
            return;
        }
        let count = heritage.templates.len();
        if count <= 1 {
            return;
        }
        // Template 0 is the custom one; a random pick is one of the others.
        let exclude_shifted = self.template.wrapping_sub(1);
        self.template = self.randomize_index_excluding(count - 1, exclude_shifted) + 1;
        self.apply_template(heritage);
    }

    fn randomize_character_inner(&mut self) {
        self.clear_session_state();
        let heritage_id = self.roll_dice(1, 4).cast_unsigned();
        self.heritage_id = heritage_id;
        let options = Arc::clone(&self.options);
        if let Some(heritage) = options.try_get_heritage(heritage_id) {
            self.set_heritage_group(heritage_id, heritage);
        }
        let gender_key = self.roll_dice(1, 2).cast_unsigned();
        self.set_gender(gender_key);
        self.randomize_appearance_inner();
        self.randomize_headgear(false);
        self.randomize_shirt_trousers_footwear();
        self.randomize_template();
        if let Some(heritage) = options.try_get_heritage(self.heritage_id) {
            self.randomize_start_area(heritage);
        }
    }

    /// Roll a whole character: one of the four human heritages, a gender,
    /// the look, clothing, a template and a town. The name is cleared.
    pub fn try_randomize_character(&mut self) -> bool {
        if !self.active {
            return false;
        }
        self.randomize_character_inner();
        self.changed();
        true
    }

    /// Roll the face and hair (each changes when there is a choice).
    pub fn try_randomize_appearance(&mut self) -> bool {
        if !self.active || self.heritage_id == 0 || self.gender_key == 0 {
            return false;
        }
        self.randomize_appearance_inner();
        self.changed();
        true
    }

    /// Roll the clothing.
    pub fn try_randomize_clothing(&mut self) -> bool {
        if !self.active || self.heritage_id == 0 || self.gender_key == 0 {
            return false;
        }
        self.randomize_headgear(true);
        self.randomize_shirt_trousers_footwear();
        self.changed();
        true
    }

    // -- Town, name, slot --------------------------------------------------

    /// Choose a starting town by index.
    pub fn try_select_start_area(&mut self, index: i32) -> bool {
        if !self.active
            || usize::try_from(index).map_or(true, |i| i >= self.options.starter_areas.len())
        {
            return false;
        }
        self.start_area = index;
        self.changed();
        true
    }

    /// Set the name (at most 32 characters are kept).
    pub fn try_set_name(&mut self, name: &str) -> bool {
        if !self.active {
            return false;
        }
        self.name = name.chars().take(32).collect();
        self.changed();
        true
    }

    /// Set the slot.
    pub fn try_set_slot(&mut self, slot: u32) -> bool {
        if !self.active {
            return false;
        }
        self.slot = slot;
        self.changed();
        true
    }

    // -- Finish and answer -------------------------------------------------

    /// The finish gate: refuses without a name, without heritage and
    /// gender, with attribute points left (unless confirmed), while a create
    /// waits, and with every slot taken. Accepted, it returns the request to
    /// send and marks the create as waiting.
    ///
    /// # Errors
    /// The local refusal (also recorded in the snapshot); a refusal with no
    /// reason set means creation is not active.
    pub fn try_begin_finish(
        &mut self,
        roster_count: usize,
        slot_count: i32,
        confirmed_unspent_credits: bool,
    ) -> Result<FinishRequest, LocalRefusal> {
        if !self.active {
            return Err(LocalRefusal::default());
        }
        self.name = self.name.trim().to_owned();
        let roster_full = usize::try_from(slot_count).is_ok_and(|s| s > 0 && roster_count >= s);
        let refusal = if self.name.is_empty() {
            LocalRefusal {
                no_name: true,
                ..LocalRefusal::default()
            }
        } else if self.heritage_id == 0 || self.gender_key == 0 {
            LocalRefusal {
                heritage_or_gender_unset: true,
                ..LocalRefusal::default()
            }
        } else if !confirmed_unspent_credits && self.remaining_attribute_credits > 0 {
            LocalRefusal {
                attribute_credits_unspent: true,
                ..LocalRefusal::default()
            }
        } else if self.verification_pending {
            LocalRefusal {
                already_pending: true,
                ..LocalRefusal::default()
            }
        } else if roster_full {
            LocalRefusal {
                roster_full: true,
                ..LocalRefusal::default()
            }
        } else {
            LocalRefusal::default()
        };
        self.last_local_refusal = refusal;
        self.revision += 1;
        if refusal.any() {
            self.publish(CreationDeltaKind::FinishRefused);
            return Err(refusal);
        }
        self.verification_pending = true;
        let request = self.build_request();
        self.publish(CreationDeltaKind::FinishSent);
        Ok(request)
    }

    fn build_request(&self) -> FinishRequest {
        let a = &self.appearance;
        let b = &self.attributes;
        let attribute = |v: i32| u32::try_from(v).unwrap_or(0);
        let mut classes = [0u32; SKILL_ADVANCEMENT_CLASS_COUNT];
        for (slot, value) in classes.iter_mut().zip(self.skills.to_wire_classes()) {
            *slot = value;
        }
        FinishRequest {
            request: CharacterCreateRequest {
                heritage: self.heritage_id,
                gender: self.gender_key,
                appearance: CharacterAppearance {
                    eyes_strip: a.eyes_strip,
                    nose_strip: a.nose_strip,
                    mouth_strip: a.mouth_strip,
                    hair_color: a.hair_color,
                    eye_color: a.eye_color,
                    hair_style: a.hair_style,
                    headgear_style: a.headgear_style,
                    headgear_color: a.headgear_color,
                    shirt_style: a.shirt_style,
                    shirt_color: a.shirt_color,
                    trousers_style: a.trousers_style,
                    trousers_color: a.trousers_color,
                    footwear_style: a.footwear_style,
                    footwear_color: a.footwear_color,
                    skin_shade: a.skin_shade,
                    hair_shade: a.hair_shade,
                    headgear_shade: a.headgear_shade,
                    shirt_shade: a.shirt_shade,
                    trousers_shade: a.trousers_shade,
                    footwear_shade: a.footwear_shade,
                },
                template: self.template,
                attributes: CharacterAttributes {
                    strength: attribute(b.strength),
                    endurance: attribute(b.endurance),
                    coordination: attribute(b.coordination),
                    quickness: attribute(b.quickness),
                    focus: attribute(b.focus),
                    self_: attribute(b.self_),
                },
                slot: self.slot,
                class_id: 0,
                name: self.name.clone(),
                start_area: u32::try_from(self.start_area.max(0)).unwrap_or(0),
                is_admin: false,
                is_envoy: false,
            },
            skill_advancement_classes: classes,
        }
    }

    /// The server's answer to the create.
    pub fn apply_creation_response(&mut self, response: &CharGenVerificationResponse) {
        if !self.active || !self.verification_pending {
            return;
        }
        self.verification_pending = false;
        let kind = match (&response.character, response.is_ok()) {
            (Some(created), true) => {
                self.last_created = Some(CreatedIdentity {
                    guid: created.guid,
                    name: created.name.clone(),
                });
                self.last_rejection = None;
                CreationDeltaKind::Created
            }
            _ => {
                self.last_rejection = Some(CreationRejection {
                    raw_code: response.code,
                    reason: chargen_code_name(response.code),
                    attempted_name: self.name.clone(),
                });
                CreationDeltaKind::CreationFailed
            }
        };
        self.revision += 1;
        self.publish(kind);
    }

    /// Dismiss the shown rejection.
    pub fn try_acknowledge_rejection(&mut self) -> bool {
        if self.last_rejection.take().is_none() {
            return false;
        }
        self.revision += 1;
        self.publish(CreationDeltaKind::RejectionAcknowledged);
        true
    }

    fn clear_session_state(&mut self) {
        self.heritage_id = 0;
        self.gender_key = 0;
        self.appearance = CreationAppearance::default();
        self.template = TEMPLATE_UNSET;
        self.attributes = ChargenAttributeValues::default();
        self.attribute_lock_mask = 0;
        self.total_attribute_credits = 0;
        self.remaining_attribute_credits = 0;
        self.total_skill_credits = 0;
        self.remaining_skill_credits = 0;
        self.skills = ChargenSkillAdvancementSet::new();
        self.name.clear();
        self.start_area = -1;
        self.slot = 0;
        self.verification_pending = false;
        self.last_local_refusal = LocalRefusal::default();
        self.last_rejection = None;
        self.last_created = None;
    }

    fn changed(&mut self) {
        self.revision += 1;
        self.publish(CreationDeltaKind::StateChanged);
    }

    fn publish(&mut self, kind: CreationDeltaKind) {
        self.sequence += 1;
        self.deltas.push(CreationDelta {
            generation: self.generation,
            sequence: self.sequence,
            revision: self.revision,
            kind,
        });
    }
}

/// A character-creation request from a host.
#[derive(Debug, Clone, PartialEq)]
pub enum CreationCommand {
    /// Reply with the creation state.
    Show,
    /// Choose a heritage.
    SelectHeritage(u32),
    /// Choose a gender.
    SelectGender(u32),
    /// Choose a template by index.
    SelectTemplate(u32),
    /// Set an attribute.
    SetAttribute(ChargenAttribute, i32),
    /// Lock or unlock an attribute.
    SetAttributeLock(ChargenAttribute, bool),
    /// Train a skill.
    TrainSkill(u32),
    /// Specialize a skill.
    SpecializeSkill(u32),
    /// Untrain a skill.
    UntrainSkill(u32),
    /// Choose a look index.
    SetAppearance(AppearanceSlot, u32),
    /// Set a shade.
    SetShade(ShadeSlot, f64),
    /// Choose a starting town.
    SelectStartArea(i32),
    /// Set the name.
    SetName(String),
    /// Set the slot.
    SetSlot(u32),
    /// Send the create; `confirm_unspent_credits` accepts points left over.
    Finish {
        /// Accept attribute points left over.
        confirm_unspent_credits: bool,
    },
    /// Dismiss the server's refusal.
    AcknowledgeRejection,
    /// Roll a whole character.
    RandomizeCharacter,
    /// Roll the face and hair.
    RandomizeAppearance,
    /// Roll the clothing.
    RandomizeClothing,
}

impl CharacterCreation {
    /// Carry out a command that only changes the creation state. `None` for
    /// [`CreationCommand::Show`] and [`CreationCommand::Finish`], which the
    /// session handles.
    pub fn apply_command(&mut self, command: &CreationCommand) -> Option<bool> {
        use CreationCommand as C;
        Some(match command {
            C::Show | C::Finish { .. } => return None,
            C::SelectHeritage(id) => self.try_select_heritage(*id),
            C::SelectGender(key) => self.try_select_gender(*key),
            C::SelectTemplate(index) => self.try_select_template(*index),
            C::SetAttribute(attribute, value) => self.try_set_attribute(*attribute, *value),
            C::SetAttributeLock(attribute, locked) => {
                self.try_set_attribute_lock(*attribute, *locked)
            }
            C::TrainSkill(id) => self.try_train_skill(*id),
            C::SpecializeSkill(id) => self.try_specialize_skill(*id),
            C::UntrainSkill(id) => self.try_untrain_skill(*id),
            C::SetAppearance(slot, index) => self.try_set_appearance_index(*slot, *index),
            C::SetShade(slot, value) => self.try_set_shade(*slot, *value),
            C::SelectStartArea(index) => self.try_select_start_area(*index),
            C::SetName(name) => self.try_set_name(name),
            C::SetSlot(slot) => self.try_set_slot(*slot),
            C::AcknowledgeRejection => self.try_acknowledge_rejection(),
            C::RandomizeCharacter => self.try_randomize_character(),
            C::RandomizeAppearance => self.try_randomize_appearance(),
            C::RandomizeClothing => self.try_randomize_clothing(),
        })
    }
}

/// Every look slot, in [`AppearanceSlot`] order.
const ALL_SLOTS: [AppearanceSlot; 14] = [
    AppearanceSlot::EyesStrip,
    AppearanceSlot::NoseStrip,
    AppearanceSlot::MouthStrip,
    AppearanceSlot::HairStyle,
    AppearanceSlot::HairColor,
    AppearanceSlot::EyeColor,
    AppearanceSlot::HeadgearStyle,
    AppearanceSlot::HeadgearColor,
    AppearanceSlot::ShirtStyle,
    AppearanceSlot::ShirtColor,
    AppearanceSlot::TrousersStyle,
    AppearanceSlot::TrousersColor,
    AppearanceSlot::FootwearStyle,
    AppearanceSlot::FootwearColor,
];

/// How many choices a slot offers for a gender.
fn slot_count(slot: AppearanceSlot, gender: &ChargenGenderOptions) -> usize {
    match slot {
        AppearanceSlot::EyesStrip => gender.eye_strips.len(),
        AppearanceSlot::NoseStrip => gender.nose_strips.len(),
        AppearanceSlot::MouthStrip => gender.mouth_strips.len(),
        AppearanceSlot::HairStyle => gender.hair_styles.len(),
        AppearanceSlot::HairColor => gender.hair_colors.len(),
        AppearanceSlot::EyeColor => gender.eye_colors.len(),
        AppearanceSlot::HeadgearStyle => gender.headgears.len(),
        AppearanceSlot::ShirtStyle => gender.shirts.len(),
        AppearanceSlot::TrousersStyle => gender.pants.len(),
        AppearanceSlot::FootwearStyle => gender.footwear.len(),
        AppearanceSlot::HeadgearColor
        | AppearanceSlot::ShirtColor
        | AppearanceSlot::TrousersColor
        | AppearanceSlot::FootwearColor => gender.clothing_colors.len(),
    }
}

/// An index past a shorter list moves to its last entry (unset stays
/// unset; an empty list makes it unset).
fn clamp_index(value: u32, count: usize) -> u32 {
    if value == UNSET {
        return value;
    }
    if value as usize >= count {
        u32::try_from(count).map_or(UNSET, |c| c.wrapping_sub(1))
    } else {
        value
    }
}

fn is_olthoi(heritage_id: u32) -> bool {
    matches!(
        ChargenHeritageGroup(heritage_id),
        ChargenHeritageGroup::OLTHOI | ChargenHeritageGroup::OLTHOI_ACID
    )
}

/// The name of a create answer code.
#[must_use]
pub fn chargen_code_name(code: u32) -> String {
    match code {
        chargen_code::UNDEF => "Undef".to_owned(),
        chargen_code::OK => "Ok".to_owned(),
        chargen_code::PENDING => "Pending".to_owned(),
        chargen_code::NAME_IN_USE => "NameInUse".to_owned(),
        chargen_code::NAME_BANNED => "NameBanned".to_owned(),
        chargen_code::CORRUPT => "Corrupt".to_owned(),
        chargen_code::DATABASE_DOWN => "DatabaseDown".to_owned(),
        chargen_code::ADMIN_PRIVILEGE_DENIED => "AdminPrivilegeDenied".to_owned(),
        other => other.to_string(),
    }
}

#[cfg(test)]
#[path = "creation_tests.rs"]
mod tests;
