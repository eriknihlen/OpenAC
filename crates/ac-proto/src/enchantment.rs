//! The enchantment record: one spell effect active on a creature. Shared
//! by the player description and the magic update events.

use ac_bits::Reader;

use crate::{ProtoError, Result};

/// Largest enchantment list count accepted.
pub const MAX_ENCHANTMENT_LIST_COUNT: u32 = 0x4000;

/// One active spell effect: 60 bytes, plus 4 when it belongs to a spell set.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Enchantment {
    /// Spell id.
    pub spell_id: u16,
    /// Layer, which orders effects of the same spell.
    pub layer: u16,
    /// Spell category: effects of one category do not stack.
    pub spell_category: u16,
    /// Non-zero when a spell-set id follows the record.
    pub has_spell_set_id: u16,
    /// Spell power.
    pub power_level: u32,
    /// When it started, game time in seconds.
    pub start_time: f64,
    /// How long it lasts, seconds; negative means until removed.
    pub duration: f64,
    /// Who cast it.
    pub caster_guid: u32,
    /// Degradation rate.
    pub degrade_modifier: f32,
    /// Degradation floor.
    pub degrade_limit: f32,
    /// When it last degraded, game time in seconds.
    pub last_time_degraded: f64,
    /// What kind of stat it modifies (bit field).
    pub stat_mod_type: u32,
    /// Which stat it modifies.
    pub stat_mod_key: u32,
    /// By how much.
    pub stat_mod_value: f32,
    /// The spell set, present when [`Enchantment::has_spell_set_id`] is
    /// non-zero.
    pub spell_set_id: Option<u32>,
}

impl Enchantment {
    /// Read one record. A NaN or infinite time, degradation or modifier
    /// value is an error.
    pub fn read(r: &mut Reader<'_>) -> Result<Self> {
        let spell_id = r.u16()?;
        let layer = r.u16()?;
        let spell_category = r.u16()?;
        let has_spell_set_id = r.u16()?;
        let power_level = r.u32()?;
        let start_time = r.f64()?;
        let duration = r.f64()?;
        let caster_guid = r.u32()?;
        let degrade_modifier = r.f32()?;
        let degrade_limit = r.f32()?;
        let last_time_degraded = r.f64()?;
        let stat_mod_type = r.u32()?;
        let stat_mod_key = r.u32()?;
        let stat_mod_value = r.f32()?;
        let spell_set_id = if has_spell_set_id != 0 {
            Some(r.u32()?)
        } else {
            None
        };
        let finite = start_time.is_finite()
            && duration.is_finite()
            && degrade_modifier.is_finite()
            && degrade_limit.is_finite()
            && last_time_degraded.is_finite()
            && stat_mod_value.is_finite();
        if !finite {
            return Err(ProtoError::NonFinite {
                what: "enchantment",
            });
        }
        Ok(Self {
            spell_id,
            layer,
            spell_category,
            has_spell_set_id,
            power_level,
            start_time,
            duration,
            caster_guid,
            degrade_modifier,
            degrade_limit,
            last_time_degraded,
            stat_mod_type,
            stat_mod_key,
            stat_mod_value,
            spell_set_id,
        })
    }

    /// Read a `u32` count, then that many records. A count above
    /// [`MAX_ENCHANTMENT_LIST_COUNT`] is an error.
    pub fn read_list(r: &mut Reader<'_>) -> Result<Vec<Self>> {
        let count = r.u32()?;
        if count > MAX_ENCHANTMENT_LIST_COUNT {
            return Err(ProtoError::CountTooLarge {
                what: "enchantment list",
                count,
                max: MAX_ENCHANTMENT_LIST_COUNT,
            });
        }
        let mut list = Vec::new();
        for _ in 0..count {
            list.push(Self::read(r)?);
        }
        Ok(list)
    }
}

#[cfg(test)]
pub(crate) mod tests {
    //! Record layout from OpenAC `src/AcDream.Core.Net/Messages/EnchantmentWireReader.cs`;
    //! the field values are those of OpenAC
    //! `tests/AcDream.Core.Net.Tests/PlayerDescriptionParserTests.cs`
    //! (`TryParse_EnchantmentBlock_PopulatesEnchantments_WithStatModAndBucket`).

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::assert_every_prefix_fails;

    /// Append `e` in wire order.
    pub(crate) fn write_enchantment(w: &mut Writer, e: &Enchantment) {
        w.u16(e.spell_id);
        w.u16(e.layer);
        w.u16(e.spell_category);
        w.u16(e.has_spell_set_id);
        w.u32(e.power_level);
        w.f64(e.start_time);
        w.f64(e.duration);
        w.u32(e.caster_guid);
        w.f32(e.degrade_modifier);
        w.f32(e.degrade_limit);
        w.f64(e.last_time_degraded);
        w.u32(e.stat_mod_type);
        w.u32(e.stat_mod_key);
        w.f32(e.stat_mod_value);
        if let Some(id) = e.spell_set_id {
            w.u32(id);
        }
    }

    pub(crate) const MULTIPLICATIVE: Enchantment = Enchantment {
        spell_id: 1234,
        layer: 5,
        spell_category: 100,
        has_spell_set_id: 0,
        power_level: 999,
        start_time: 12.5,
        duration: 1800.0,
        caster_guid: 0xCAFE_0001,
        degrade_modifier: 1.0,
        degrade_limit: 0.5,
        last_time_degraded: 0.0,
        stat_mod_type: 0x0001_0000,
        stat_mod_key: 3,
        stat_mod_value: 1.5,
        spell_set_id: None,
    };

    fn bytes(e: &Enchantment) -> Vec<u8> {
        let mut w = Writer::new();
        write_enchantment(&mut w, e);
        w.into_vec()
    }

    #[test]
    fn record_is_sixty_bytes_and_round_trips() {
        let body = bytes(&MULTIPLICATIVE);
        assert_eq!(body.len(), 60);
        assert_eq!(
            Enchantment::read(&mut Reader::new(&body)),
            Ok(MULTIPLICATIVE)
        );
    }

    #[test]
    fn golden_prefix() {
        let body = bytes(&MULTIPLICATIVE);
        assert_eq!(
            &body[..12],
            &[
                0xD2, 0x04, // spell 1234
                0x05, 0x00, // layer 5
                0x64, 0x00, // category 100
                0x00, 0x00, // no spell set
                0xE7, 0x03, 0x00, 0x00, // power 999
            ]
        );
    }

    #[test]
    fn spell_set_id_adds_four_bytes() {
        let e = Enchantment {
            has_spell_set_id: 1,
            spell_set_id: Some(0x0000_0042),
            ..MULTIPLICATIVE
        };
        let body = bytes(&e);
        assert_eq!(body.len(), 64);
        assert_eq!(Enchantment::read(&mut Reader::new(&body)), Ok(e));
    }

    #[test]
    fn every_truncation_fails() {
        let e = Enchantment {
            has_spell_set_id: 1,
            spell_set_id: Some(7),
            ..MULTIPLICATIVE
        };
        assert_every_prefix_fails(&bytes(&e), |b| Enchantment::read(&mut Reader::new(b)));
        let mut list = 2u32.to_le_bytes().to_vec();
        list.extend(bytes(&MULTIPLICATIVE));
        list.extend(bytes(&e));
        assert_every_prefix_fails(&list, |b| Enchantment::read_list(&mut Reader::new(b)));
    }

    #[test]
    fn non_finite_values_are_errors() {
        for e in [
            Enchantment {
                start_time: f64::NAN,
                ..MULTIPLICATIVE
            },
            Enchantment {
                duration: f64::INFINITY,
                ..MULTIPLICATIVE
            },
            Enchantment {
                degrade_modifier: f32::NAN,
                ..MULTIPLICATIVE
            },
            Enchantment {
                degrade_limit: f32::NEG_INFINITY,
                ..MULTIPLICATIVE
            },
            Enchantment {
                last_time_degraded: f64::NAN,
                ..MULTIPLICATIVE
            },
            Enchantment {
                stat_mod_value: f32::INFINITY,
                ..MULTIPLICATIVE
            },
        ] {
            assert_eq!(
                Enchantment::read(&mut Reader::new(&bytes(&e))),
                Err(ProtoError::NonFinite {
                    what: "enchantment"
                })
            );
        }
    }

    #[test]
    fn list_count_above_the_limit_is_an_error() {
        let body = (MAX_ENCHANTMENT_LIST_COUNT + 1).to_le_bytes();
        assert_eq!(
            Enchantment::read_list(&mut Reader::new(&body)),
            Err(ProtoError::CountTooLarge {
                what: "enchantment list",
                count: MAX_ENCHANTMENT_LIST_COUNT + 1,
                max: MAX_ENCHANTMENT_LIST_COUNT
            })
        );
        // An empty list is fine.
        assert_eq!(
            Enchantment::read_list(&mut Reader::new(&0u32.to_le_bytes())),
            Ok(Vec::new())
        );
    }
}
