use ac_bits::Reader;

use super::DatObject;
use super::read::{self, keyed, legacy_string, packable_table_count};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// File id of the skill table.
pub const SKILL_TABLE_ID: u32 = 0x0E00_0004;

/// Every skill: its name, costs, the advancement class it starts to count
/// at, and the attribute formula behind its level. File id `0x0E000004`.
#[derive(Debug, Clone, PartialEq)]
pub struct SkillTable {
    /// File id.
    pub id: u32,
    /// Skills keyed by skill id, in file order.
    pub skills: Vec<(u32, SkillBase)>,
}

impl DatObject for SkillTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = packable_table_count(r)?;
        let skills = keyed(r, "skills", count, read::u32, SkillBase::unpack)?;
        Ok(Self { id, skills })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// One skill.
#[derive(Debug, Clone, PartialEq)]
pub struct SkillBase {
    /// Description.
    pub description: String,
    /// Name.
    pub name: String,
    /// Icon id.
    pub icon: u32,
    /// Credits to train.
    pub trained_cost: i32,
    /// Credits to specialize (the training cost included).
    pub specialized_cost: i32,
    /// Category (1 combat, 2 other, 3 magic).
    pub category: u32,
    /// Offered at character creation (a four-byte boolean).
    pub chargen_use: bool,
    /// The advancement class (1 untrained, 2 trained, 3 specialized) at
    /// which the skill starts to count.
    pub min_level: u32,
    /// The attribute formula.
    pub formula: SkillFormula,
    /// Upper bound.
    pub upper_bound: f64,
    /// Lower bound.
    pub lower_bound: f64,
    /// Learning modifier.
    pub learn_mod: f64,
}

impl SkillBase {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            description: legacy_string(r)?,
            name: legacy_string(r)?,
            icon: r.u32()?,
            trained_cost: r.i32()?,
            specialized_cost: r.i32()?,
            category: r.u32()?,
            chargen_use: r.u32()? != 0,
            min_level: r.u32()?,
            formula: SkillFormula::unpack(r)?,
            upper_bound: r.f64()?,
            lower_bound: r.f64()?,
            learn_mod: r.f64()?,
        })
    }
}

/// `(attribute1 * mult1 + attribute2 * mult2) / divisor + additive`, as
/// stored (`W`, `X`, `Y`, `Z`, then the two attribute ids).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SkillFormula {
    /// Added after the division (`W`; 0 in retail data).
    pub additive_bonus: i32,
    /// First attribute's multiplier (`X`).
    pub attribute1_multiplier: i32,
    /// Second attribute's multiplier (`Y`).
    pub attribute2_multiplier: i32,
    /// Divisor (`Z`).
    pub divisor: i32,
    /// First attribute id (1 strength .. 6 self; 0 none).
    pub attribute1: u32,
    /// Second attribute id.
    pub attribute2: u32,
}

impl SkillFormula {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            additive_bonus: r.i32()?,
            attribute1_multiplier: r.i32()?,
            attribute2_multiplier: r.i32()?,
            divisor: r.i32()?,
            attribute1: r.u32()?,
            attribute2: r.u32()?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn legacy(w: &mut Writer, text: &str) {
        w.u16(text.len() as u16);
        for b in text.bytes() {
            w.u8(b);
        }
        w.align(4);
    }

    #[test]
    fn a_skill_reads_its_strings_costs_and_formula() {
        // The skill table's layout (skills and their formulas): packable
        // table, two aligned one-byte
        // strings, then fixed fields.
        let mut w = Writer::new();
        w.u32(SKILL_TABLE_ID);
        w.u16(1);
        w.u16(64);
        w.u32(24);
        legacy(&mut w, "Run fast.");
        legacy(&mut w, "Run");
        w.u32(0x0600_1234);
        w.i32(4);
        w.i32(8);
        w.u32(2);
        w.u32(1);
        w.u32(1);
        for v in [0i32, 1, 1, 2] {
            w.i32(v);
        }
        w.u32(1);
        w.u32(4);
        w.f64(1.0);
        w.f64(0.0);
        w.f64(0.5);
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let table = SkillTable::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        let (id, run) = &table.skills[0];
        assert_eq!(*id, 24);
        assert_eq!(
            (run.name.as_str(), run.description.as_str()),
            ("Run", "Run fast.")
        );
        assert_eq!((run.trained_cost, run.specialized_cost), (4, 8));
        assert!(run.chargen_use);
        assert_eq!(run.min_level, 1);
        assert_eq!(
            run.formula,
            SkillFormula {
                additive_bonus: 0,
                attribute1_multiplier: 1,
                attribute2_multiplier: 1,
                divisor: 2,
                attribute1: 1,
                attribute2: 4,
            }
        );
        assert_eq!(run.learn_mod, 0.5);
    }
}
