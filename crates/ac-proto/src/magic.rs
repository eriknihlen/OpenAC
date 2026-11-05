//! Magic: casting, the spellbook and its favorites and filters, spell
//! components, item mana, and the enchantment events.
//!
//! The purge events ([`crate::GameEventType::MAGIC_PURGE_ENCHANTMENTS`],
//! [`crate::GameEventType::MAGIC_PURGE_BAD_ENCHANTMENTS`]) carry nothing to
//! read. Game-action builders take the session's next action sequence
//! number and are sent on the UI queue.

use ac_bits::Reader;

use crate::wire::{action, u32_action};
use crate::{Enchantment, GameActionType, MAX_ENCHANTMENT_LIST_COUNT, ProtoError, Result};

/// Cast a spell with no target.
#[must_use]
pub fn build_cast_untargeted_spell(sequence: u32, spell_id: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::CAST_UNTARGETED_SPELL, spell_id)
}

/// Cast a spell on a target: target, then spell.
#[must_use]
pub fn build_cast_targeted_spell(sequence: u32, target_guid: u32, spell_id: u32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::CAST_TARGETED_SPELL);
    w.u32(target_guid);
    w.u32(spell_id);
    w.into_vec()
}

/// Forget a spell.
#[must_use]
pub fn build_remove_spell(sequence: u32, spell_id: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::REMOVE_SPELL, spell_id)
}

/// Put a spell on favorites bar `tab_index` at `position`.
#[must_use]
pub fn build_add_spell_favorite(
    sequence: u32,
    spell_id: u32,
    position: i32,
    tab_index: i32,
) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::ADD_SPELL_FAVORITE);
    w.u32(spell_id);
    w.i32(position);
    w.i32(tab_index);
    w.into_vec()
}

/// Take a spell off favorites bar `tab_index`.
#[must_use]
pub fn build_remove_spell_favorite(sequence: u32, spell_id: u32, tab_index: i32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::REMOVE_SPELL_FAVORITE);
    w.u32(spell_id);
    w.i32(tab_index);
    w.into_vec()
}

/// Set the spellbook filter bits.
#[must_use]
pub fn build_spellbook_filter(sequence: u32, filters: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::SPELLBOOK_FILTER, filters)
}

/// Keep `amount` of a spell component: component, then amount.
#[must_use]
pub fn build_set_desired_component_level(sequence: u32, component_id: u32, amount: u32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::SET_DESIRED_COMPONENT_LEVEL);
    w.u32(component_id);
    w.u32(amount);
    w.into_vec()
}

/// Clear every desired component: component 0 with amount `u32::MAX`.
#[must_use]
pub fn build_clear_desired_components(sequence: u32) -> Vec<u8> {
    build_set_desired_component_level(sequence, 0, u32::MAX)
}

/// Ask for an item's mana; the server answers with
/// [`QueryItemManaResponse`].
#[must_use]
pub fn build_query_item_mana(sequence: u32, item_guid: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::QUERY_ITEM_MANA, item_guid)
}

/// An item's mana ([`crate::GameEventType::QUERY_ITEM_MANA_RESPONSE`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct QueryItemManaResponse {
    /// The item.
    pub item_guid: u32,
    /// Mana as a fraction, 0 to 1.
    pub mana_percent: f32,
    /// The item has mana at all.
    pub valid: bool,
}

impl QueryItemManaResponse {
    /// Parse the event payload: item, fraction, valid word.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            item_guid: r.u32()?,
            mana_percent: r.f32()?,
            valid: r.u32()? != 0,
        })
    }
}

/// The spell id of a spellbook event: a spell learned
/// ([`crate::GameEventType::MAGIC_UPDATE_SPELL`]) or forgotten
/// ([`crate::GameEventType::MAGIC_REMOVE_SPELL`]).
pub fn parse_spell_id(payload: &[u8]) -> Result<u32> {
    Ok(Reader::new(payload).u32()?)
}

/// An enchantment added or refreshed
/// ([`crate::GameEventType::MAGIC_UPDATE_ENCHANTMENT`]).
pub fn parse_update_enchantment(payload: &[u8]) -> Result<Enchantment> {
    Enchantment::read(&mut Reader::new(payload))
}

/// Several enchantments added or refreshed
/// ([`crate::GameEventType::MAGIC_UPDATE_MULTIPLE_ENCHANTMENTS`]).
pub fn parse_update_multiple_enchantments(payload: &[u8]) -> Result<Vec<Enchantment>> {
    Enchantment::read_list(&mut Reader::new(payload))
}

/// One enchantment named by spell and layer.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LayeredSpellId {
    /// Spell id.
    pub spell_id: u16,
    /// Layer.
    pub layer: u16,
}

impl LayeredSpellId {
    /// The packed form: spell in the low 16 bits, layer in the high 16.
    #[must_use]
    pub fn packed(self) -> u32 {
        u32::from(self.spell_id) | (u32::from(self.layer) << 16)
    }

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            spell_id: r.u16()?,
            layer: r.u16()?,
        })
    }

    /// Parse the payload of an event that names one enchantment: removed
    /// ([`crate::GameEventType::MAGIC_REMOVE_ENCHANTMENT`]) or dispelled
    /// ([`crate::GameEventType::MAGIC_DISPEL_ENCHANTMENT`]).
    pub fn parse(payload: &[u8]) -> Result<Self> {
        Self::read(&mut Reader::new(payload))
    }
}

/// The enchantments of a multiple-remove
/// ([`crate::GameEventType::MAGIC_REMOVE_MULTIPLE_ENCHANTMENTS`]) or
/// multiple-dispel ([`crate::GameEventType::MAGIC_DISPEL_MULTIPLE_ENCHANTMENTS`])
/// event: a count, then spell and layer each.
pub fn parse_layered_spell_list(payload: &[u8]) -> Result<Vec<LayeredSpellId>> {
    let mut r = Reader::new(payload);
    let count = r.u32()?;
    if count > MAX_ENCHANTMENT_LIST_COUNT {
        return Err(ProtoError::CountTooLarge {
            what: "layered spells",
            count,
            max: MAX_ENCHANTMENT_LIST_COUNT,
        });
    }
    crate::wire::check_count(&r, count, 4, "layered spells")?;
    (0..count).map(|_| LayeredSpellId::read(&mut r)).collect()
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `CastSpellTests.cs`, the spell cases of `ClientCommandRequestsTests.cs`,
    //! the mana cases of `SocialActionsTests.cs` and
    //! `GameEventDispatcherTests.cs`, and the body shapes of
    //! `WorldSessionCombatTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    #[test]
    fn cast_requests() {
        assert_eq!(
            build_cast_untargeted_spell(5, 0x3E1),
            words(&[0xF7B1, 5, 0x48, 0x3E1])
        );
        assert_eq!(
            build_cast_targeted_spell(5, 0xAAAA, 0x3E1),
            words(&[0xF7B1, 5, 0x4A, 0xAAAA, 0x3E1])
        );
    }

    #[test]
    fn spellbook_requests() {
        assert_eq!(build_remove_spell(4, 77), words(&[0xF7B1, 4, 0x01A8, 77]));
        assert_eq!(
            build_add_spell_favorite(9, 42, 3, 7),
            words(&[0xF7B1, 9, 0x01E3, 42, 3, 7])
        );
        assert_eq!(
            build_remove_spell_favorite(2, 42, 6),
            words(&[0xF7B1, 2, 0x01E4, 42, 6])
        );
        assert_eq!(
            build_spellbook_filter(3, 0xA5),
            words(&[0xF7B1, 3, 0x0286, 0xA5])
        );
        assert_eq!(
            build_set_desired_component_level(6, 0x1200_0042, 25),
            words(&[0xF7B1, 6, 0x0224, 0x1200_0042, 25])
        );
        assert_eq!(
            build_clear_desired_components(6),
            words(&[0xF7B1, 6, 0x0224, 0, u32::MAX])
        );
        assert_eq!(
            build_query_item_mana(4, 0x5000_0A01),
            words(&[0xF7B1, 4, 0x0263, 0x5000_0A01])
        );
    }

    #[test]
    fn item_mana_response() {
        let mut w = Writer::new();
        w.u32(0x5000_0A01);
        w.f32(0.625);
        w.u32(1);
        assert_eq!(
            QueryItemManaResponse::parse(w.as_slice()),
            Ok(QueryItemManaResponse {
                item_guid: 0x5000_0A01,
                mana_percent: 0.625,
                valid: true
            })
        );
        assert_every_prefix_fails(w.as_slice(), QueryItemManaResponse::parse);
    }

    fn enchantment(spell: u16, layer: u16, duration: f64, caster: u32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u16(spell);
        w.u16(layer);
        w.u16(3);
        w.u16(0);
        w.u32(8);
        w.f64(10.0);
        w.f64(duration);
        w.u32(caster);
        w.f32(0.1);
        w.f32(-1.0);
        w.f64(0.0);
        w.u32(0x20);
        w.u32(7);
        w.f32(1.25);
        w.into_vec()
    }

    #[test]
    fn enchantment_events() {
        let parsed = parse_update_enchantment(&enchantment(42, 7, 300.0, 0xBEEF)).unwrap();
        assert_eq!((parsed.spell_id, parsed.layer), (42, 7));
        assert_eq!(parsed.duration, 300.0);
        assert_eq!(parsed.caster_guid, 0xBEEF);
        assert_eq!(parsed.stat_mod_type, 0x20);
        assert_eq!(parsed.stat_mod_key, 7);
        assert_eq!(parsed.stat_mod_value, 1.25);
        // A has-spell-set flag with no spell set word behind it.
        let mut cut = enchantment(42, 1, 60.0, 0xAA);
        cut[6..8].copy_from_slice(&1u16.to_le_bytes());
        assert!(parse_update_enchantment(&cut).is_err());

        let mut list = words(&[2]);
        list.extend(enchantment(42, 1, 60.0, 0xAA));
        list.extend(enchantment(43, 2, 120.0, 0xBB));
        let parsed = parse_update_multiple_enchantments(&list).unwrap();
        assert_eq!(parsed.len(), 2);
        assert_eq!((parsed[0].spell_id, parsed[1].spell_id), (42, 43));
    }

    #[test]
    fn remove_and_dispel_events() {
        let mut w = Writer::new();
        w.u16(42);
        w.u16(7);
        let one = LayeredSpellId::parse(w.as_slice()).unwrap();
        assert_eq!(
            one,
            LayeredSpellId {
                spell_id: 42,
                layer: 7
            }
        );
        assert_eq!(one.packed(), 0x0007_002A);
        assert_every_prefix_fails(w.as_slice(), LayeredSpellId::parse);

        let mut w = Writer::new();
        w.u32(2);
        w.u16(42);
        w.u16(1);
        w.u16(43);
        w.u16(2);
        assert_eq!(
            parse_layered_spell_list(w.as_slice()),
            Ok(vec![
                LayeredSpellId {
                    spell_id: 42,
                    layer: 1
                },
                LayeredSpellId {
                    spell_id: 43,
                    layer: 2
                }
            ])
        );
        // Two announced, one present.
        let mut cut = words(&[2]);
        cut.extend([42, 0, 1, 0]);
        assert!(parse_layered_spell_list(&cut).is_err());
        assert!(parse_layered_spell_list(&words(&[MAX_ENCHANTMENT_LIST_COUNT + 1])).is_err());
        assert_eq!(parse_spell_id(&words(&[0x1234_03E1])), Ok(0x1234_03E1));
    }
}
