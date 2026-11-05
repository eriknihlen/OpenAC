use ac_bits::Reader;

use super::DatObject;
use super::Position;
use super::read::{self, keyed, legacy_string, packable_table_count};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// File id of the contract table.
pub const CONTRACT_TABLE_ID: u32 = 0x0E00_001D;

/// Every quest contract the quest tracker can name: its words, the quest
/// flags behind it and where its people stand. File id `0x0E00001D`.
#[derive(Debug, Clone, PartialEq)]
pub struct ContractTable {
    /// File id.
    pub id: u32,
    /// Contracts keyed by contract id, in file order.
    pub contracts: Vec<(u32, Contract)>,
}

impl DatObject for ContractTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = packable_table_count(r)?;
        let contracts = keyed(r, "contracts", count, read::u32, Contract::unpack)?;
        Ok(Self { id, contracts })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// One contract.
#[derive(Debug, Clone, PartialEq)]
pub struct Contract {
    /// Version.
    pub version: u32,
    /// Contract id.
    pub contract_id: u32,
    /// Name.
    pub contract_name: String,
    /// Description.
    pub description: String,
    /// The progress line, a format with one integer specifier.
    pub description_progress: String,
    /// The NPC who gives the contract.
    pub name_npc_start: String,
    /// The NPC who ends it.
    pub name_npc_end: String,
    /// Quest flag: stamped.
    pub questflag_stamped: String,
    /// Quest flag: started.
    pub questflag_started: String,
    /// Quest flag: finished.
    pub questflag_finished: String,
    /// Quest flag: progress.
    pub questflag_progress: String,
    /// Quest flag: timer.
    pub questflag_timer: String,
    /// Quest flag: repeat time.
    pub questflag_repeat_time: String,
    /// Where the starting NPC stands.
    pub location_npc_start: Position,
    /// Where the ending NPC stands.
    pub location_npc_end: Position,
    /// Where the quest takes place.
    pub location_quest_area: Position,
}

impl Contract {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            version: r.u32()?,
            contract_id: r.u32()?,
            contract_name: legacy_string(r)?,
            description: legacy_string(r)?,
            description_progress: legacy_string(r)?,
            name_npc_start: legacy_string(r)?,
            name_npc_end: legacy_string(r)?,
            questflag_stamped: legacy_string(r)?,
            questflag_started: legacy_string(r)?,
            questflag_finished: legacy_string(r)?,
            questflag_progress: legacy_string(r)?,
            questflag_timer: legacy_string(r)?,
            questflag_repeat_time: legacy_string(r)?,
            location_npc_start: Position::unpack(r)?,
            location_npc_end: Position::unpack(r)?,
            location_quest_area: Position::unpack(r)?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn legacy(w: &mut Writer, text: &str) {
        w.u16(u16::try_from(text.len()).unwrap());
        for b in text.bytes() {
            w.u8(b);
        }
        w.align(4);
    }

    fn position(w: &mut Writer, cell: u32) {
        w.u32(cell);
        for v in [0.0f32, 0.0, 1.0, 1.0, 0.0, 0.0, 0.0] {
            w.f32(v);
        }
    }

    #[test]
    fn a_contract_reads_its_words_flags_and_places() {
        // The contract table's layout: packable table,
        // version, id, eleven aligned one-byte strings, three positions.
        let mut w = Writer::new();
        w.u32(CONTRACT_TABLE_ID);
        w.u16(1);
        w.u16(64);
        w.u32(1);
        w.u32(1);
        w.u32(1);
        for text in [
            "Contract 1",
            "Description 1",
            "Description Progress 1",
            "NPC Start 1",
            "NPC End 1",
            "Stamped 1",
            "Started 1",
            "Finished 1",
            "Progress 1",
            "Timer 1",
            "Repeat Time 1",
        ] {
            legacy(&mut w, text);
        }
        for _ in 0..3 {
            position(&mut w, 0x0001_0100);
        }
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let table = ContractTable::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(table.id, CONTRACT_TABLE_ID);
        let (key, c) = &table.contracts[0];
        assert_eq!((*key, c.contract_id, c.version), (1, 1, 1));
        assert_eq!(c.contract_name, "Contract 1");
        assert_eq!(c.description, "Description 1");
        assert_eq!(c.description_progress, "Description Progress 1");
        assert_eq!(c.name_npc_start, "NPC Start 1");
        assert_eq!(c.name_npc_end, "NPC End 1");
        assert_eq!(c.questflag_stamped, "Stamped 1");
        assert_eq!(c.questflag_started, "Started 1");
        assert_eq!(c.questflag_finished, "Finished 1");
        assert_eq!(c.questflag_progress, "Progress 1");
        assert_eq!(c.questflag_timer, "Timer 1");
        assert_eq!(c.questflag_repeat_time, "Repeat Time 1");
        assert_eq!(c.location_quest_area.cell_id, 0x0001_0100);
        assert!((c.location_npc_end.frame.origin.z - 1.0).abs() < f32::EPSILON);
    }
}
