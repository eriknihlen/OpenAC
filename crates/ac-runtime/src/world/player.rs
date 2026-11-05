//! The local player's own state: the character numbers (`ac_core`'s
//! `LocalPlayerState`, fed by the character description and the private
//! updates that follow it) and the saved positions.
//!
//! The player's property tables live with every other object's in the item
//! table; the character options have their own owner
//! ([`crate::character::CharacterOptions`]). The spellbook lives inside the
//! numbers, so attributes, vitals and skills read with their enchantments;
//! the magic owner ([`crate::gameplay`]) feeds it.

use std::collections::BTreeMap;

use ac_core::player::LocalPlayerState;
use ac_core::spells::Spellbook;
use ac_proto::{
    PlayerDescription, Position, PrivateUpdateAttribute, PrivateUpdateSkill, PrivateUpdateVital,
    PrivateUpdateVitalCurrent,
};

/// The local player's numbers and saved positions.
#[derive(Debug, Clone, Default)]
pub struct PlayerState {
    numbers: LocalPlayerState,
    positions: BTreeMap<u32, Position>,
}

impl PlayerState {
    /// A player whose numbers start from `rules` (its spellbook and skill
    /// formula resolver; no character yet).
    pub(super) fn new(rules: LocalPlayerState) -> Self {
        Self {
            numbers: rules,
            positions: BTreeMap::new(),
        }
    }

    /// Vitals, attributes, skills and int properties.
    #[must_use]
    pub fn numbers(&self) -> &LocalPlayerState {
        &self.numbers
    }

    /// The spellbook (learned spells and active enchantments).
    #[must_use]
    pub fn spellbook(&self) -> Option<&Spellbook> {
        self.numbers.spellbook()
    }

    pub(super) fn spellbook_mut(&mut self) -> Option<&mut Spellbook> {
        self.numbers.spellbook_mut()
    }

    /// Saved positions (lifestone, corpse, recall slots) by slot type.
    #[must_use]
    pub fn positions(&self) -> &BTreeMap<u32, Position> {
        &self.positions
    }

    /// The character description: int properties replaced, every vital,
    /// attribute and skill record taken, saved positions replaced. A record
    /// with a current value is a vital; one without is a primary attribute.
    pub(super) fn apply_description(&mut self, description: &PlayerDescription) {
        self.numbers
            .on_int_properties(description.properties.ints.clone());
        self.positions.clone_from(&description.positions);
        for attribute in &description.attributes {
            match attribute.current {
                Some(current) => {
                    self.numbers.on_vital_update(
                        attribute.id,
                        attribute.ranks,
                        attribute.start,
                        attribute.xp,
                        current,
                    );
                }
                None => {
                    self.numbers.on_attribute_update(
                        attribute.id,
                        attribute.ranks,
                        attribute.start,
                        attribute.xp,
                    );
                }
            }
        }
        for skill in &description.skills {
            // The formula term is computed below, once every attribute is in.
            self.numbers.on_skill_update(
                skill.skill,
                u32::from(skill.ranks),
                skill.advancement_class,
                skill.xp,
                skill.init,
                skill.resistance,
                skill.last_used,
                0,
            );
        }
        // Each skill's attribute term from the skill table's formula and the
        // attributes just received (stays 0 without the table).
        self.numbers.recompute_skill_formula_bonuses();
    }

    pub(super) fn apply_position(&mut self, slot: u32, position: Position) {
        self.positions.insert(slot, position);
    }

    pub(super) fn apply_int(&mut self, property: u32, value: i32) {
        self.numbers.on_int_property_update(property, value);
    }

    pub(super) fn apply_attribute(&mut self, update: &PrivateUpdateAttribute) {
        self.numbers
            .on_attribute_update(update.attribute, update.ranks, update.start, update.xp);
    }

    pub(super) fn apply_vital(&mut self, update: &PrivateUpdateVital) {
        self.numbers.on_vital_update(
            update.vital,
            update.ranks,
            update.start,
            update.xp,
            update.current,
        );
    }

    pub(super) fn apply_vital_current(&mut self, update: &PrivateUpdateVitalCurrent) {
        self.numbers.on_vital_current(update.vital, update.current);
    }

    pub(super) fn apply_skill(&mut self, update: &PrivateUpdateSkill) {
        self.numbers.on_skill_wire_update(
            update.skill,
            u32::from(update.ranks),
            update.advancement_class,
            update.xp,
            update.init,
            update.resistance,
            update.last_used,
        );
    }
}
