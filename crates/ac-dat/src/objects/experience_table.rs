//! The experience table: what each rank of an attribute, vital and skill
//! costs, the experience each character level needs, and the skill credits
//! each level grants.

use ac_bits::Reader;

use super::DatObject;
use super::read;
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// File id of the experience table.
pub const EXPERIENCE_TABLE_ID: u32 = 0x0E00_0018;

/// Cumulative experience curves, indexed by rank (or level). File id
/// `0x0E000018`.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ExperienceTable {
    /// File id.
    pub id: u32,
    /// Experience to raise an attribute to each rank.
    pub attributes: Vec<u32>,
    /// Experience to raise a vital to each rank.
    pub vitals: Vec<u32>,
    /// Experience to raise a trained skill to each rank.
    pub trained_skills: Vec<u32>,
    /// Experience to raise a specialized skill to each rank.
    pub specialized_skills: Vec<u32>,
    /// Total experience each character level needs.
    pub levels: Vec<u64>,
    /// Skill credits granted at each level.
    pub skill_credits: Vec<u32>,
}

/// A stored top index: the list holds one more entry than it says.
fn top_index(r: &mut Reader<'_>) -> Result<u64> {
    let n = r.i32()?;
    u64::try_from(n)
        .map(|n| n + 1)
        .map_err(|_| DecodeError::NegativeLength(n))
}

impl DatObject for ExperienceTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let attributes = top_index(r)?;
        let vitals = top_index(r)?;
        let trained = top_index(r)?;
        let specialized = top_index(r)?;
        let levels = u64::from(r.u32()?) + 1;
        Ok(Self {
            id,
            attributes: read::list(r, attributes, |r| Ok(r.u32()?))?,
            vitals: read::list(r, vitals, |r| Ok(r.u32()?))?,
            trained_skills: read::list(r, trained, |r| Ok(r.u32()?))?,
            specialized_skills: read::list(r, specialized, |r| Ok(r.u32()?))?,
            levels: read::list(r, levels, |r| Ok(r.u64()?))?,
            skill_credits: read::list(r, levels, |r| Ok(r.u32()?))?,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    /// The counts are stored as the top index, one less than each list's
    /// length.
    #[test]
    fn reads_each_curve_one_longer_than_its_stored_count() {
        let mut w = Writer::new();
        w.u32(EXPERIENCE_TABLE_ID);
        for _ in 0..4 {
            w.i32(3);
        }
        w.u32(3);
        for v in [1, 2, 3, 4, 100, 200, 300, 400, 1000, 2000, 3000, 4000] {
            w.u32(v);
        }
        for v in [10000, 20000, 30000, 40000] {
            w.u32(v);
        }
        for v in [5u64, 6, 7, 8] {
            w.u64(v);
        }
        for v in [9, 10, 11, 12] {
            w.u32(v);
        }
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let t = ExperienceTable::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(t.attributes, [1, 2, 3, 4]);
        assert_eq!(t.vitals, [100, 200, 300, 400]);
        assert_eq!(t.trained_skills, [1000, 2000, 3000, 4000]);
        assert_eq!(t.specialized_skills, [10000, 20000, 30000, 40000]);
        assert_eq!(t.levels, [5, 6, 7, 8]);
        assert_eq!(t.skill_credits, [9, 10, 11, 12]);
        for len in 0..bytes.len() {
            assert!(ExperienceTable::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }

    #[test]
    fn a_negative_count_is_refused() {
        let mut w = Writer::new();
        w.u32(EXPERIENCE_TABLE_ID);
        w.i32(-2);
        let bytes = w.into_vec();
        assert!(matches!(
            ExperienceTable::unpack(&mut Reader::new(&bytes)),
            Err(DecodeError::NegativeLength(-2))
        ));
    }
}
