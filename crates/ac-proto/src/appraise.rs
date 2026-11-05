//! Appraisal: the request and the identify-object response with its
//! property tables, spellbook and profiles.

use ac_bits::Reader;

use crate::wire::u32_action;
use crate::{GameActionType, PropertyTables, ProtoError, Result};

/// Largest spellbook the parser accepts.
pub const MAX_SPELLBOOK: u32 = 4096;

/// Which sections an identify response carries.
pub mod identify_flags {
    /// Integer properties.
    pub const INT_STATS_TABLE: u32 = 0x0001;
    /// Boolean properties.
    pub const BOOL_STATS_TABLE: u32 = 0x0002;
    /// Double properties.
    pub const FLOAT_STATS_TABLE: u32 = 0x0004;
    /// String properties.
    pub const STRING_STATS_TABLE: u32 = 0x0008;
    /// Spellbook.
    pub const SPELL_BOOK: u32 = 0x0010;
    /// Weapon profile.
    pub const WEAPON_PROFILE: u32 = 0x0020;
    /// Hook profile.
    pub const HOOK_PROFILE: u32 = 0x0040;
    /// Armor profile.
    pub const ARMOR_PROFILE: u32 = 0x0080;
    /// Creature profile.
    pub const CREATURE_PROFILE: u32 = 0x0100;
    /// Armor enchantment highlight bits.
    pub const ARMOR_ENCHANTMENT_BITFIELD: u32 = 0x0200;
    /// Resistance enchantment highlight bits.
    pub const RESIST_ENCHANTMENT_BITFIELD: u32 = 0x0400;
    /// Weapon enchantment highlight bits.
    pub const WEAPON_ENCHANTMENT_BITFIELD: u32 = 0x0800;
    /// Data-id properties.
    pub const DID_STATS_TABLE: u32 = 0x1000;
    /// 64-bit integer properties.
    pub const INT64_STATS_TABLE: u32 = 0x2000;
    /// Armor levels per body part.
    pub const ARMOR_LEVELS: u32 = 0x4000;
}

/// Creature profile bit: the attributes and vitals follow.
const CREATURE_SHOW_ATTRIBUTES: u32 = 0x08;
/// Creature profile bit: the attribute highlight words follow.
const CREATURE_HAS_BUFFS: u32 = 0x01;

/// Appraise an object. Game action, UI queue.
#[must_use]
pub fn build_appraise(sequence: u32, target_guid: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::IDENTIFY_OBJECT, target_guid)
}

/// Protection against each damage type.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ArmorProfile {
    /// Slashing.
    pub slashing: f32,
    /// Piercing.
    pub piercing: f32,
    /// Bludgeoning.
    pub bludgeoning: f32,
    /// Cold.
    pub cold: f32,
    /// Fire.
    pub fire: f32,
    /// Acid.
    pub acid: f32,
    /// Nether.
    pub nether: f32,
    /// Lightning.
    pub lightning: f32,
}

/// Armor level of each body part.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ArmorLevels {
    /// Head.
    pub head: i32,
    /// Chest.
    pub chest: i32,
    /// Abdomen.
    pub abdomen: i32,
    /// Upper arm.
    pub upper_arm: i32,
    /// Lower arm.
    pub lower_arm: i32,
    /// Hand.
    pub hand: i32,
    /// Upper leg.
    pub upper_leg: i32,
    /// Lower leg.
    pub lower_leg: i32,
    /// Foot.
    pub foot: i32,
}

/// A weapon's statistics.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WeaponProfile {
    /// Damage type bits.
    pub damage_type: u32,
    /// Attack speed.
    pub weapon_time: u32,
    /// Skill used.
    pub weapon_skill: u32,
    /// Maximum damage.
    pub damage: u32,
    /// Damage variance.
    pub damage_variance: f64,
    /// Damage modifier.
    pub damage_mod: f64,
    /// Weapon length.
    pub weapon_length: f64,
    /// Missile speed.
    pub max_velocity: f64,
    /// Offense bonus.
    pub weapon_offense: f64,
    /// Estimated missile speed.
    pub max_velocity_estimated: u32,
}

/// What a hook holds.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HookProfile {
    /// Hook flags.
    pub flags: u32,
    /// Valid locations of the hooked item.
    pub valid_locations: u32,
    /// Ammunition type of the hooked item.
    pub ammo_type: u32,
}

/// A creature's statistics.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CreatureProfile {
    /// Profile flags.
    pub flags: u32,
    /// Health.
    pub health: u32,
    /// Maximum health.
    pub health_max: u32,
    /// Attributes and vitals, when shown: strength, endurance, quickness,
    /// coordination, focus, self, stamina, mana, maximum stamina, maximum
    /// mana.
    pub attributes: Option<CreatureAttributes>,
    /// Attribute highlight and color words, when present.
    pub attribute_highlights: Option<(u16, u16)>,
}

/// The attribute block of a creature profile.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CreatureAttributes {
    /// Strength.
    pub strength: u32,
    /// Endurance.
    pub endurance: u32,
    /// Quickness.
    pub quickness: u32,
    /// Coordination.
    pub coordination: u32,
    /// Focus.
    pub focus: u32,
    /// Self.
    pub self_: u32,
    /// Stamina.
    pub stamina: u32,
    /// Mana.
    pub mana: u32,
    /// Maximum stamina.
    pub stamina_max: u32,
    /// Maximum mana.
    pub mana_max: u32,
}

/// Highlight and color words of an enchantment bit field.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EnchantmentHighlight {
    /// Which stats are enchanted.
    pub highlight: u16,
    /// Which of them are beneficial.
    pub color: u16,
}

/// An appraisal ([`crate::GameEventType::IDENTIFY_OBJECT_RESPONSE`]).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct IdentifyObjectResponse {
    /// The object.
    pub guid: u32,
    /// Sections present (see [`identify_flags`]).
    pub flags: u32,
    /// The appraisal succeeded.
    pub success: bool,
    /// Property tables (instance ids and positions are never sent).
    pub properties: PropertyTables,
    /// Spells on the item, in wire order.
    pub spell_book: Vec<u32>,
    /// Armor profile.
    pub armor_profile: Option<ArmorProfile>,
    /// Creature profile.
    pub creature_profile: Option<CreatureProfile>,
    /// Weapon profile.
    pub weapon_profile: Option<WeaponProfile>,
    /// Hook profile.
    pub hook_profile: Option<HookProfile>,
    /// Armor enchantment highlights.
    pub armor_enchantments: Option<EnchantmentHighlight>,
    /// Weapon enchantment highlights.
    pub weapon_enchantments: Option<EnchantmentHighlight>,
    /// Resistance enchantment highlights.
    pub resist_enchantments: Option<EnchantmentHighlight>,
    /// Armor levels.
    pub armor_levels: Option<ArmorLevels>,
}

impl IdentifyObjectResponse {
    /// Parse the event payload: object, flags, success word, then each
    /// section its flag announces, in this order: int, int64, bool, float,
    /// string and data-id tables, spellbook, armor, creature, weapon and
    /// hook profiles, armor, weapon and resistance highlights, armor
    /// levels.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        use identify_flags as f;
        let mut r = Reader::new(payload);
        let mut out = Self {
            guid: r.u32()?,
            flags: r.u32()?,
            success: r.u32()? != 0,
            ..Self::default()
        };
        let flags = out.flags;
        let has = |bit: u32| flags & bit != 0;
        let p = &mut out.properties;
        if has(f::INT_STATS_TABLE) {
            for _ in 0..read_table_header(&mut r)? {
                let key = r.u32()?;
                p.ints.insert(key, r.i32()?);
            }
        }
        if has(f::INT64_STATS_TABLE) {
            for _ in 0..read_table_header(&mut r)? {
                let key = r.u32()?;
                p.int64s.insert(key, r.i64()?);
            }
        }
        if has(f::BOOL_STATS_TABLE) {
            for _ in 0..read_table_header(&mut r)? {
                let key = r.u32()?;
                p.bools.insert(key, r.u32()? != 0);
            }
        }
        if has(f::FLOAT_STATS_TABLE) {
            for _ in 0..read_table_header(&mut r)? {
                let key = r.u32()?;
                p.floats.insert(key, r.f64()?);
            }
        }
        if has(f::STRING_STATS_TABLE) {
            for _ in 0..read_table_header(&mut r)? {
                let key = r.u32()?;
                p.strings.insert(key, crate::wire::read_string(&mut r)?);
            }
        }
        if has(f::DID_STATS_TABLE) {
            for _ in 0..read_table_header(&mut r)? {
                let key = r.u32()?;
                p.data_ids.insert(key, r.u32()?);
            }
        }
        if has(f::SPELL_BOOK) {
            let count = r.u32()?;
            if count > MAX_SPELLBOOK {
                return Err(ProtoError::CountTooLarge {
                    what: "appraisal spellbook",
                    count,
                    max: MAX_SPELLBOOK,
                });
            }
            crate::wire::check_count(&r, count, 4, "appraisal spellbook")?;
            out.spell_book = (0..count)
                .map(|_| r.u32())
                .collect::<ac_bits::Result<_>>()?;
        }
        if has(f::ARMOR_PROFILE) {
            out.armor_profile = Some(ArmorProfile {
                slashing: r.f32()?,
                piercing: r.f32()?,
                bludgeoning: r.f32()?,
                cold: r.f32()?,
                fire: r.f32()?,
                acid: r.f32()?,
                nether: r.f32()?,
                lightning: r.f32()?,
            });
        }
        if has(f::CREATURE_PROFILE) {
            out.creature_profile = Some(read_creature_profile(&mut r)?);
        }
        if has(f::WEAPON_PROFILE) {
            out.weapon_profile = Some(WeaponProfile {
                damage_type: r.u32()?,
                weapon_time: r.u32()?,
                weapon_skill: r.u32()?,
                damage: r.u32()?,
                damage_variance: r.f64()?,
                damage_mod: r.f64()?,
                weapon_length: r.f64()?,
                max_velocity: r.f64()?,
                weapon_offense: r.f64()?,
                max_velocity_estimated: r.u32()?,
            });
        }
        if has(f::HOOK_PROFILE) {
            out.hook_profile = Some(HookProfile {
                flags: r.u32()?,
                valid_locations: r.u32()?,
                ammo_type: r.u32()?,
            });
        }
        if has(f::ARMOR_ENCHANTMENT_BITFIELD) {
            out.armor_enchantments = Some(read_highlight(&mut r)?);
        }
        if has(f::WEAPON_ENCHANTMENT_BITFIELD) {
            out.weapon_enchantments = Some(read_highlight(&mut r)?);
        }
        if has(f::RESIST_ENCHANTMENT_BITFIELD) {
            out.resist_enchantments = Some(read_highlight(&mut r)?);
        }
        if has(f::ARMOR_LEVELS) {
            out.armor_levels = Some(ArmorLevels {
                head: r.i32()?,
                chest: r.i32()?,
                abdomen: r.i32()?,
                upper_arm: r.i32()?,
                lower_arm: r.i32()?,
                hand: r.i32()?,
                upper_leg: r.i32()?,
                lower_leg: r.i32()?,
                foot: r.i32()?,
            });
        }
        Ok(out)
    }
}

/// A property table header: 16-bit count, 16-bit bucket count.
fn read_table_header(r: &mut Reader<'_>) -> Result<u16> {
    let count = r.u16()?;
    let _buckets = r.u16()?;
    Ok(count)
}

fn read_highlight(r: &mut Reader<'_>) -> Result<EnchantmentHighlight> {
    Ok(EnchantmentHighlight {
        highlight: r.u16()?,
        color: r.u16()?,
    })
}

fn read_creature_profile(r: &mut Reader<'_>) -> Result<CreatureProfile> {
    let flags = r.u32()?;
    let health = r.u32()?;
    let health_max = r.u32()?;
    let attributes = if flags & CREATURE_SHOW_ATTRIBUTES != 0 {
        Some(CreatureAttributes {
            strength: r.u32()?,
            endurance: r.u32()?,
            quickness: r.u32()?,
            coordination: r.u32()?,
            focus: r.u32()?,
            self_: r.u32()?,
            stamina: r.u32()?,
            mana: r.u32()?,
            stamina_max: r.u32()?,
            mana_max: r.u32()?,
        })
    } else {
        None
    };
    let attribute_highlights = if flags & CREATURE_HAS_BUFFS != 0 {
        Some((r.u16()?, r.u16()?))
    } else {
        None
    };
    Ok(CreatureProfile {
        flags,
        health,
        health_max,
        attributes,
        attribute_highlights,
    })
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `AppraiseInfoParserTests.cs` and `AppraiseTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};
    use identify_flags as f;

    fn header(flags: u32) -> Writer {
        let mut w = Writer::new();
        w.u32(1);
        w.u32(flags);
        w.u32(1);
        w
    }

    #[test]
    fn flag_values() {
        let all = [
            f::INT_STATS_TABLE,
            f::BOOL_STATS_TABLE,
            f::FLOAT_STATS_TABLE,
            f::STRING_STATS_TABLE,
            f::SPELL_BOOK,
            f::WEAPON_PROFILE,
            f::HOOK_PROFILE,
            f::ARMOR_PROFILE,
            f::CREATURE_PROFILE,
            f::ARMOR_ENCHANTMENT_BITFIELD,
            f::RESIST_ENCHANTMENT_BITFIELD,
            f::WEAPON_ENCHANTMENT_BITFIELD,
            f::DID_STATS_TABLE,
            f::INT64_STATS_TABLE,
            f::ARMOR_LEVELS,
        ];
        for (i, flag) in all.into_iter().enumerate() {
            assert_eq!(flag, 1 << i);
        }
    }

    #[test]
    fn header_only() {
        let mut w = Writer::new();
        w.u32(0xDEAD_BEEF);
        w.u32(0);
        w.u32(1);
        let parsed = IdentifyObjectResponse::parse(w.as_slice()).unwrap();
        assert_eq!(parsed.guid, 0xDEAD_BEEF);
        assert!(parsed.success);
        assert!(IdentifyObjectResponse::parse(&[0; 4]).is_err());
    }

    #[test]
    fn tables_and_spellbook() {
        let mut w = header(
            f::INT_STATS_TABLE | f::BOOL_STATS_TABLE | f::STRING_STATS_TABLE | f::SPELL_BOOK,
        );
        w.u16(3);
        w.u16(16);
        for (k, v) in [(1, 100), (5, -50), (9, 42)] {
            w.u32(k);
            w.i32(v);
        }
        w.u16(2);
        w.u16(8);
        for (k, v) in [(1, 1), (2, 0)] {
            w.u32(k);
            w.u32(v);
        }
        w.u16(2);
        w.u16(8);
        w.u32(1);
        w.string16l("Excalibur").unwrap();
        w.u32(2);
        w.string16l("Rusty Dagger").unwrap();
        w.u32(3);
        for s in [0x3E1, 0x3E2, 0x3E3] {
            w.u32(s);
        }
        let parsed = IdentifyObjectResponse::parse(w.as_slice()).unwrap();
        let p = &parsed.properties;
        assert_eq!(p.ints.len(), 3);
        assert_eq!((p.ints[&1], p.ints[&5], p.ints[&9]), (100, -50, 42));
        assert!(p.bools[&1] && !p.bools[&2]);
        assert_eq!(p.strings[&1], "Excalibur");
        assert_eq!(p.strings[&2], "Rusty Dagger");
        assert_eq!(parsed.spell_book, [0x3E1, 0x3E2, 0x3E3]);
        assert_every_prefix_fails(w.as_slice(), IdentifyObjectResponse::parse);
    }

    #[test]
    fn a_cut_section_rejects_the_whole_event() {
        let mut w = header(f::HOOK_PROFILE);
        w.u32(3);
        assert!(IdentifyObjectResponse::parse(w.as_slice()).is_err());
        let mut w = header(f::SPELL_BOOK);
        w.u32(MAX_SPELLBOOK + 1);
        assert!(matches!(
            IdentifyObjectResponse::parse(w.as_slice()),
            Err(ProtoError::CountTooLarge { .. })
        ));
    }

    #[test]
    fn armor_profile_and_levels() {
        let mut w = header(f::ARMOR_PROFILE | f::ARMOR_LEVELS);
        for v in [0.1f32, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8] {
            w.f32(v);
        }
        for v in [100, 200, 150, 125, 110, 90, 140, 130, 80] {
            w.i32(v);
        }
        let parsed = IdentifyObjectResponse::parse(w.as_slice()).unwrap();
        let armor = parsed.armor_profile.unwrap();
        assert_eq!((armor.slashing, armor.lightning), (0.1, 0.8));
        let levels = parsed.armor_levels.unwrap();
        assert_eq!((levels.head, levels.chest, levels.foot), (100, 200, 80));
        assert_every_prefix_fails(w.as_slice(), IdentifyObjectResponse::parse);
    }

    #[test]
    fn weapon_profile() {
        let mut w = header(f::WEAPON_PROFILE);
        for v in [4, 30, 44, 25] {
            w.u32(v);
        }
        for v in [0.25, 1.5, 1.0, 2.0, 1.1] {
            w.f64(v);
        }
        w.u32(5);
        let weapon = IdentifyObjectResponse::parse(w.as_slice())
            .unwrap()
            .weapon_profile
            .unwrap();
        assert_eq!(weapon.damage_type, 4);
        assert_eq!(weapon.damage, 25);
        assert_eq!(weapon.damage_mod, 1.5);
        assert_eq!(weapon.max_velocity_estimated, 5);
    }

    #[test]
    fn creature_profiles() {
        let mut w = header(f::CREATURE_PROFILE);
        for v in [0x08, 500, 600, 80, 70, 65, 60, 100, 100, 200, 150, 250, 200] {
            w.u32(v);
        }
        let creature = IdentifyObjectResponse::parse(w.as_slice())
            .unwrap()
            .creature_profile
            .unwrap();
        assert_eq!(
            (creature.flags, creature.health, creature.health_max),
            (0x08, 500, 600)
        );
        let attributes = creature.attributes.unwrap();
        assert_eq!((attributes.strength, attributes.mana), (80, 150));
        assert!(creature.attribute_highlights.is_none());

        // A creature profile with no flags is health only, not a weapon.
        let mut w = Writer::new();
        w.u32(0x5000_0001);
        w.u32(0x0100);
        w.u32(1);
        for v in [0, 300, 400] {
            w.u32(v);
        }
        let parsed = IdentifyObjectResponse::parse(w.as_slice()).unwrap();
        let creature = parsed.creature_profile.unwrap();
        assert!(creature.attributes.is_none() && creature.attribute_highlights.is_none());
        assert_eq!(creature.health, 300);
        assert!(parsed.weapon_profile.is_none());
    }

    #[test]
    fn hook_profile_keeps_the_following_fields_aligned() {
        let mut w = header(f::HOOK_PROFILE | f::ARMOR_ENCHANTMENT_BITFIELD);
        for v in [0x0B, 0x0010_0000, 4] {
            w.u32(v);
        }
        w.u16(0x1234);
        w.u16(0x5678);
        let parsed = IdentifyObjectResponse::parse(w.as_slice()).unwrap();
        assert_eq!(
            parsed.hook_profile,
            Some(HookProfile {
                flags: 0x0B,
                valid_locations: 0x0010_0000,
                ammo_type: 4
            })
        );
        assert_eq!(
            parsed.armor_enchantments,
            Some(EnchantmentHighlight {
                highlight: 0x1234,
                color: 0x5678
            })
        );
    }

    #[test]
    fn appraise_request() {
        assert_eq!(
            build_appraise(4, 0x5000_0A01),
            words(&[0xF7B1, 4, 0xC8, 0x5000_0A01])
        );
    }
}
