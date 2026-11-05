use ac_bits::Reader;

use super::DatObject;
use super::read::list;
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// The attack motions a creature uses, by stance, height, attack type and
/// skill. File id `0x30......`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CombatTable {
    /// File id.
    pub id: u32,
    /// Maneuvers, in file order.
    pub combat_maneuvers: Vec<CombatManeuver>,
}

impl DatObject for CombatTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = u64::from(r.u32()?);
        Ok(Self {
            id,
            combat_maneuvers: list(r, count, CombatManeuver::unpack)?,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// One attack motion and when it applies.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CombatManeuver {
    /// Stance.
    pub style: u32,
    /// Attack height (1 high, 2 medium, 3 low).
    pub attack_height: i32,
    /// Attack type bits.
    pub attack_type: i32,
    /// Lowest skill that uses it.
    pub min_skill_level: u32,
    /// Motion to play.
    pub motion: u32,
}

impl CombatManeuver {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            style: r.u32()?,
            attack_height: r.i32()?,
            attack_type: r.i32()?,
            min_skill_level: r.u32()?,
            motion: r.u32()?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    #[test]
    fn unpacks_maneuvers() {
        let mut w = Writer::new();
        w.u32(0x3000_0000);
        w.u32(2);
        for i in 0..2 {
            w.u32(0x8000_003C);
            w.i32(1 + i);
            w.i32(8);
            w.u32(0);
            w.u32(0x1000_0068);
        }
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let t = CombatTable::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(
            t.combat_maneuvers[1],
            CombatManeuver {
                style: 0x8000_003C,
                attack_height: 2,
                attack_type: 8,
                min_skill_level: 0,
                motion: 0x1000_0068
            }
        );
        for len in 0..bytes.len() {
            assert!(CombatTable::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
