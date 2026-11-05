use ac_bits::{Reader, cp1252};

use super::DatObject;
use super::read::{self, keyed, packable_table_count, scrambled_string};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// File id of the spell table.
pub const SPELL_TABLE_ID: u32 = 0x0E00_000E;
/// File id of the spell component table.
pub const SPELL_COMPONENT_TABLE_ID: u32 = 0x0E00_000F;

/// Every spell, plus the spells granted by equipment sets. File id
/// `0x0E00000E`.
#[derive(Debug, Clone, PartialEq)]
pub struct SpellTable {
    /// File id.
    pub id: u32,
    /// Spells keyed by spell id, in file order.
    pub spells: Vec<(u32, SpellBase)>,
    /// Spells per equipment set, keyed by set id, in file order.
    pub spell_sets: Vec<(i32, SpellSet)>,
}

impl DatObject for SpellTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = packable_table_count(r)?;
        let spells = keyed(r, "spells", count, read::u32, SpellBase::unpack)?;
        let count = packable_table_count(r)?;
        let spell_sets = keyed(r, "spell sets", count, read::i32, SpellSet::unpack)?;
        Ok(Self {
            id,
            spells,
            spell_sets,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// Spell kinds that carry a duration and decay.
const ENCHANTMENT: u32 = 1;
/// Fellowship enchantments, which also carry a duration and decay.
const FELLOW_ENCHANTMENT: u32 = 12;
/// Portal summoning, which carries a portal lifetime.
const PORTAL_SUMMON: u32 = 7;
/// Highest spell kind with a stored spell record.
const LAST_SPELL_TYPE: u32 = 15;

/// One spell.
#[derive(Debug, Clone, PartialEq)]
pub struct SpellBase {
    /// Name.
    pub name: String,
    /// Description.
    pub description: String,
    /// Magic school.
    pub school: i32,
    /// Icon id.
    pub icon: u32,
    /// Category (spells of one category do not stack).
    pub category: u32,
    /// Spell flag bits.
    pub bitfield: u32,
    /// Base mana cost.
    pub base_mana: u32,
    /// Base range, metres.
    pub base_range_constant: f32,
    /// Range added per skill point.
    pub base_range_mod: f32,
    /// Power level.
    pub power: u32,
    /// Mana cost scale.
    pub spell_economy_mod: f32,
    /// Formula version.
    pub formula_version: u32,
    /// Chance to burn components.
    pub component_loss: f32,
    /// Spell kind (enchantment, projectile, ...); see [`SpellBase::meta_spell_id`].
    pub meta_spell_type: u32,
    /// The kind-specific spell id; zero for an unknown kind, which stores
    /// no record.
    pub meta_spell_id: u32,
    /// Enchantments: duration, seconds.
    pub duration: f64,
    /// Enchantments: decay rate.
    pub degrade_modifier: f32,
    /// Enchantments: decay limit.
    pub degrade_limit: f32,
    /// Portal summoning: portal lifetime, seconds.
    pub portal_lifetime: f64,
    /// Component ids as stored, scrambled with a key from the name and
    /// description; see [`SpellBase::components`].
    pub formula: [u32; 8],
    /// Effect played on the caster.
    pub caster_effect: u32,
    /// Effect played on the target.
    pub target_effect: u32,
    /// Effect played on a fizzle.
    pub fizzle_effect: u32,
    /// Recovery interval, seconds.
    pub recovery_interval: f64,
    /// Recovery amount.
    pub recovery_amount: f32,
    /// Sort order in the spell book.
    pub display_order: u32,
    /// Target item types when the spell has no target component.
    pub non_component_target_type: u32,
    /// Mana modifier.
    pub mana_mod: u32,
}

impl SpellBase {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let name = scrambled_string(r)?;
        let description = scrambled_string(r)?;
        let school = r.i32()?;
        let icon = r.u32()?;
        let category = r.u32()?;
        let bitfield = r.u32()?;
        let base_mana = r.u32()?;
        let base_range_constant = r.f32()?;
        let base_range_mod = r.f32()?;
        let power = r.u32()?;
        let spell_economy_mod = r.f32()?;
        let formula_version = r.u32()?;
        let component_loss = r.f32()?;
        let meta_spell_type = r.u32()?;
        let mut spell = Self {
            name,
            description,
            school,
            icon,
            category,
            bitfield,
            base_mana,
            base_range_constant,
            base_range_mod,
            power,
            spell_economy_mod,
            formula_version,
            component_loss,
            meta_spell_type,
            meta_spell_id: 0,
            duration: 0.0,
            degrade_modifier: 0.0,
            degrade_limit: 0.0,
            portal_lifetime: 0.0,
            formula: [0; 8],
            caster_effect: 0,
            target_effect: 0,
            fizzle_effect: 0,
            recovery_interval: 0.0,
            recovery_amount: 0.0,
            display_order: 0,
            non_component_target_type: 0,
            mana_mod: 0,
        };
        if (1..=LAST_SPELL_TYPE).contains(&meta_spell_type) {
            spell.meta_spell_id = r.u32()?;
            match meta_spell_type {
                ENCHANTMENT | FELLOW_ENCHANTMENT => {
                    spell.duration = r.f64()?;
                    spell.degrade_modifier = r.f32()?;
                    spell.degrade_limit = r.f32()?;
                }
                PORTAL_SUMMON => spell.portal_lifetime = r.f64()?,
                _ => {}
            }
        }
        for c in &mut spell.formula {
            *c = r.u32()?;
        }
        spell.caster_effect = r.u32()?;
        spell.target_effect = r.u32()?;
        spell.fizzle_effect = r.u32()?;
        spell.recovery_interval = r.f64()?;
        spell.recovery_amount = r.f32()?;
        spell.display_order = r.u32()?;
        spell.non_component_target_type = r.u32()?;
        spell.mana_mod = r.u32()?;
        Ok(spell)
    }

    /// The component ids, unscrambled: each non-zero stored value minus a
    /// key made from hashes of the name and the description. Unused slots
    /// stay zero.
    #[must_use]
    pub fn components(&self) -> [u32; 8] {
        let name_hash = string_hash(&cp1252::encode(&self.name));
        let desc_hash = string_hash(&cp1252::encode(&self.description));
        let key = (desc_hash % 0xBEAD_CF45).wrapping_add(name_hash % 0x1210_7680);
        self.formula
            .map(|c| if c == 0 { 0 } else { c.wrapping_sub(key) })
    }
}

/// The hash of a one-byte string: per byte up to the first zero, shift
/// left 4 and add the byte as a signed value, folding the top 4 bits back
/// in. All-ones is reserved and becomes `0xFFFFFFFE`.
fn string_hash(bytes: &[u8]) -> u32 {
    let mut result: u32 = 0;
    for &b in bytes.iter().take_while(|&&b| b != 0) {
        result = (result << 4).wrapping_add(i32::from(b as i8) as u32);
        let top = result & 0xF000_0000;
        if top != 0 {
            result = ((top >> 24) ^ result) & 0x0FFF_FFFF;
        }
    }
    if result == u32::MAX {
        0xFFFF_FFFE
    } else {
        result
    }
}

/// The spells an equipment set grants, by number of pieces worn.
#[derive(Debug, Clone, PartialEq)]
pub struct SpellSet {
    /// Spell ids keyed by piece count, in file order.
    pub tiers: Vec<(u32, Vec<u32>)>,
}

impl SpellSet {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let count = packable_table_count(r)?;
        let tiers = keyed(r, "spell set tiers", count, read::u32, |r| {
            let count = u64::from(r.u32()?);
            read::list(r, count, read::u32)
        })?;
        Ok(Self { tiers })
    }
}

/// Every spell component. File id `0x0E00000F`.
#[derive(Debug, Clone, PartialEq)]
pub struct SpellComponentTable {
    /// File id.
    pub id: u32,
    /// Components keyed by component id, in file order.
    pub components: Vec<(u32, SpellComponentBase)>,
}

impl DatObject for SpellComponentTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = packable_table_count(r)?;
        let components = keyed(
            r,
            "components",
            count,
            read::u32,
            SpellComponentBase::unpack,
        )?;
        Ok(Self { id, components })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// One spell component.
#[derive(Debug, Clone, PartialEq)]
pub struct SpellComponentBase {
    /// Name.
    pub name: String,
    /// Category.
    pub category: u32,
    /// Icon id.
    pub icon: u32,
    /// Component kind.
    pub component_type: u32,
    /// Casting gesture (a motion).
    pub gesture: u32,
    /// Gesture time, seconds.
    pub time: f32,
    /// Words spoken.
    pub text: String,
    /// Chance to burn the component.
    pub cdm: f32,
}

impl SpellComponentBase {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let name = scrambled_string(r)?;
        let category = r.u32()?;
        let icon = r.u32()?;
        let component_type = r.u32()?;
        let gesture = r.u32()?;
        let time = r.f32()?;
        let text = scrambled_string(r)?;
        let cdm = r.f32()?;
        Ok(Self {
            name,
            category,
            icon,
            component_type,
            gesture,
            time,
            text,
            cdm,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn scrambled(w: &mut Writer, text: &str) {
        let bytes = cp1252::encode(text);
        w.u16(bytes.len() as u16);
        for b in bytes {
            w.u8(b.rotate_left(4));
        }
        w.align(4);
    }

    fn spell(w: &mut Writer, meta_type: u32, name: &str, desc: &str, comps: [u32; 8]) {
        scrambled(w, name);
        scrambled(w, desc);
        w.i32(4);
        w.u32(0x0600_138C);
        w.u32(1);
        w.u32(6);
        w.u32(10);
        w.f32(5.0);
        w.f32(1.0);
        w.u32(1);
        w.f32(1.0);
        w.u32(1);
        w.f32(0.0);
        w.u32(meta_type);
        if (1..=15).contains(&meta_type) {
            w.u32(77);
            match meta_type {
                1 | 12 => {
                    w.f64(1800.0);
                    w.f32(0.0);
                    w.f32(-666.0);
                }
                7 => w.f64(60.0),
                _ => {}
            }
        }
        for c in comps {
            w.u32(c);
        }
        w.u32(0);
        w.u32(6);
        w.u32(0);
        w.f64(0.0);
        w.f32(0.0);
        w.u32(10412);
        w.u32(16);
        w.u32(0);
    }

    #[test]
    fn hash_folds_the_top_bits_and_reads_bytes_as_signed() {
        assert_eq!(string_hash(b""), 0);
        assert_eq!(string_hash(b"A"), 0x41);
        assert_eq!(string_hash(b"AB"), 0x41 * 16 + 0x42);
        // Stops at a zero byte.
        assert_eq!(string_hash(b"A\0B"), 0x41);
        // A high byte counts as negative: 0 - 110 = 0xFFFFFF92, whose top
        // nibble folds in: (0xF0 ^ 0xFFFFFF92) & 0x0FFFFFFF.
        assert_eq!(string_hash(&[0x92]), 0x0FFF_FF62);
        // Seven characters push bits into the top nibble, which folds.
        assert_eq!(string_hash(b"ABCDEFGH") & 0xF000_0000, 0);
    }

    #[test]
    fn components_subtract_the_name_key() {
        let name = "Strength Other I";
        let desc = "Increases the target's Strength by 10 points.";
        let key = (string_hash(desc.as_bytes()) % 0xBEAD_CF45)
            .wrapping_add(string_hash(name.as_bytes()) % 0x1210_7680);
        let plain = [1u32, 7, 33, 44, 49, 0, 0, 0];
        let stored = plain.map(|c| if c == 0 { 0 } else { c.wrapping_add(key) });
        let mut w = Writer::new();
        spell(&mut w, 1, name, desc, stored);
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let s = SpellBase::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(s.name, name);
        assert_eq!(s.description, desc);
        assert_eq!(s.components(), plain);
        assert_eq!(s.formula, stored);
        assert_eq!(
            (s.meta_spell_id, s.duration, s.degrade_limit),
            (77, 1800.0, -666.0)
        );
    }

    #[test]
    fn kind_specific_fields() {
        for (meta_type, id, lifetime) in [(7, 77, 60.0), (2, 77, 0.0), (0, 0, 0.0), (99, 0, 0.0)] {
            let mut w = Writer::new();
            spell(&mut w, meta_type, "a", "b", [0; 8]);
            let bytes = w.into_vec();
            let r = &mut Reader::new(&bytes);
            let s = SpellBase::unpack(r).unwrap();
            assert_eq!(r.remaining(), 0, "type {meta_type}");
            assert_eq!((s.meta_spell_id, s.portal_lifetime), (id, lifetime));
            assert_eq!(s.duration, 0.0);
            assert_eq!(s.display_order, 10412);
        }
    }

    fn table() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(SPELL_TABLE_ID);
        w.u16(2);
        w.u16(64);
        for (id, t) in [(1u32, 1u32), (157, 7)] {
            w.u32(id);
            spell(&mut w, t, "Name", "Desc", [0; 8]);
        }
        w.u16(1);
        w.u16(256);
        w.i32(138);
        w.u16(2);
        w.u16(4);
        w.u32(1);
        w.i32(2);
        w.u32(6221);
        w.u32(6226);
        w.u32(2);
        w.i32(0);
        w.into_vec()
    }

    #[test]
    fn unpacks_a_spell_table() {
        let bytes = table();
        let r = &mut Reader::new(&bytes);
        let t = SpellTable::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(t.spells[1].0, 157);
        assert_eq!(t.spells[1].1.portal_lifetime, 60.0);
        assert_eq!(t.spell_sets[0].0, 138);
        assert_eq!(
            t.spell_sets[0].1.tiers,
            vec![(1, vec![6221, 6226]), (2, vec![])]
        );
        for len in 0..bytes.len() {
            assert!(SpellTable::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }

    #[test]
    fn unpacks_a_component_table() {
        let mut w = Writer::new();
        w.u32(SPELL_COMPONENT_TABLE_ID);
        w.u16(1);
        w.u16(32);
        w.u32(1);
        scrambled(&mut w, "Lead Scarab");
        w.u32(0);
        w.u32(0x0600_13E7);
        w.u32(1);
        w.u32(0x8000_0000);
        w.f32(0.0);
        scrambled(&mut w, "");
        w.f32(0.1);
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let t = SpellComponentTable::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        let (key, c) = &t.components[0];
        assert_eq!(*key, 1);
        assert_eq!(c.name, "Lead Scarab");
        assert_eq!((c.icon, c.gesture, c.cdm), (0x0600_13E7, 0x8000_0000, 0.1));
        assert_eq!(c.text, "");
        for len in 0..bytes.len() {
            assert!(SpellComponentTable::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
