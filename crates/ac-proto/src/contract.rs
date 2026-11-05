//! Contracts (the quest tracker): the full table, single updates, and the
//! request to abandon one.
//!
//! The arrival time OpenAC stamps on each tracker is not on the wire; the
//! runtime records it when it applies a message.

use std::collections::BTreeMap;

use ac_bits::Reader;

use crate::social::read_hash_header;
use crate::wire::{read_bool32, u32_action};
use crate::{GameActionType, ProtoError, Result};

/// Largest tracker table the parser accepts.
pub const MAX_TABLE_ENTRIES: u16 = 4096;

/// Contract stages; values from [`stage::PROGRESS_COUNTER`] up carry a
/// progress count.
pub mod stage {
    /// Can be started.
    pub const AVAILABLE: u32 = 1;
    /// Started.
    pub const IN_PROGRESS: u32 = 2;
    /// Done, or waiting to repeat.
    pub const DONE_OR_PENDING_REPEAT: u32 = 3;
    /// First stage that carries a count: progress is `stage - 4`.
    pub const PROGRESS_COUNTER: u32 = 4;
}

/// One contract's state.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ContractTracker {
    /// Record version.
    pub version: u32,
    /// The contract.
    pub contract_id: u32,
    /// Stage (see [`stage`]).
    pub stage: u32,
    /// Seconds until done.
    pub time_when_done: f64,
    /// Seconds until it can be repeated.
    pub time_when_repeats: f64,
}

impl ContractTracker {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            version: r.u32()?,
            contract_id: r.u32()?,
            stage: r.u32()?,
            time_when_done: r.f64()?,
            time_when_repeats: r.f64()?,
        })
    }

    /// The progress count, 0 when the stage carries none.
    #[must_use]
    pub fn progress(&self) -> u32 {
        self.stage.saturating_sub(stage::PROGRESS_COUNTER)
    }

    /// True when the stage carries a progress count.
    #[must_use]
    pub fn has_progress_counter(&self) -> bool {
        self.stage >= stage::PROGRESS_COUNTER
    }
}

/// One contract changed
/// ([`crate::GameEventType::SEND_CLIENT_CONTRACT_TRACKER`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ContractTrackerUpdate {
    /// The contract's state.
    pub tracker: ContractTracker,
    /// Remove it from the tracker.
    pub delete: bool,
    /// Make it the displayed contract.
    pub set_as_display: bool,
}

impl ContractTrackerUpdate {
    /// Parse the event payload: the tracker, then two flag words.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            tracker: ContractTracker::read(&mut r)?,
            delete: read_bool32(&mut r)?,
            set_as_display: read_bool32(&mut r)?,
        })
    }
}

/// Every contract the character has
/// ([`crate::GameEventType::SEND_CLIENT_CONTRACT_TRACKER_TABLE`]): a hash
/// table header, then a key and a tracker per entry. A key repeated on the
/// wire keeps its last tracker.
pub fn parse_contract_table(payload: &[u8]) -> Result<BTreeMap<u32, ContractTracker>> {
    let mut r = Reader::new(payload);
    let count = read_hash_header(&mut r, "contract tracker table")?;
    if count > MAX_TABLE_ENTRIES {
        return Err(ProtoError::CountTooLarge {
            what: "contract trackers",
            count: u32::from(count),
            max: u32::from(MAX_TABLE_ENTRIES),
        });
    }
    let mut table = BTreeMap::new();
    for _ in 0..count {
        let key = r.u32()?;
        table.insert(key, ContractTracker::read(&mut r)?);
    }
    Ok(table)
}

/// Abandon a contract. Game action, UI queue.
#[must_use]
pub fn build_abandon_contract(sequence: u32, contract_id: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::ABANDON_CONTRACT, contract_id)
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/Messages/ContractTrackerMessagesTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    fn tracker(w: &mut Writer, version: u32, id: u32, stage: u32, done: f64, repeats: f64) {
        w.u32(version);
        w.u32(id);
        w.u32(stage);
        w.f64(done);
        w.f64(repeats);
    }

    #[test]
    fn update_decodes_every_field_in_order() {
        let mut w = Writer::new();
        tracker(&mut w, 3, 0x1234, 2, 120.5, 86400.0);
        w.u32(0);
        w.u32(1);
        let update = ContractTrackerUpdate::parse(w.as_slice()).unwrap();
        assert_eq!(update.tracker.version, 3);
        assert_eq!(update.tracker.contract_id, 0x1234);
        assert_eq!(update.tracker.stage, stage::IN_PROGRESS);
        assert_eq!(update.tracker.time_when_done, 120.5);
        assert_eq!(update.tracker.time_when_repeats, 86400.0);
        assert!(!update.delete && update.set_as_display);
        assert_every_prefix_fails(w.as_slice(), ContractTrackerUpdate::parse);
    }

    #[test]
    fn stage_carries_the_progress_count_from_four() {
        for (stage, progress, has) in [(1, 0, false), (3, 0, false), (4, 0, true), (9, 5, true)] {
            let t = ContractTracker {
                version: 1,
                contract_id: 7,
                stage,
                time_when_done: 0.0,
                time_when_repeats: 0.0,
            };
            assert_eq!(t.progress(), progress);
            assert_eq!(t.has_progress_counter(), has);
        }
    }

    #[test]
    fn table_decodes_every_entry() {
        let mut w = Writer::new();
        w.u32((8 << 16) | 2);
        w.u32(0x1111);
        tracker(&mut w, 1, 0x1111, 1, 0.0, 0.0);
        w.u32(0x2222);
        tracker(&mut w, 1, 0x2222, 6, 10.0, 20.0);
        let table = parse_contract_table(w.as_slice()).unwrap();
        assert_eq!(table.len(), 2);
        assert_eq!(table[&0x1111].stage, stage::AVAILABLE);
        assert_eq!(table[&0x2222].progress(), 2);
        assert_every_prefix_fails(w.as_slice(), parse_contract_table);
    }

    #[test]
    fn table_edge_cases() {
        assert!(parse_contract_table(&words(&[0])).unwrap().is_empty());
        let mut no_buckets = Writer::new();
        no_buckets.u32(1);
        no_buckets.u32(1);
        tracker(&mut no_buckets, 1, 1, 1, 0.0, 0.0);
        assert!(matches!(
            parse_contract_table(no_buckets.as_slice()),
            Err(ProtoError::Inconsistent { .. })
        ));
        assert!(matches!(
            parse_contract_table(&words(&[(256 << 16) | 60000])),
            Err(ProtoError::CountTooLarge { .. })
        ));
    }

    #[test]
    fn abandon_carries_only_the_contract_id() {
        assert_eq!(
            build_abandon_contract(7, 0x1234),
            words(&[0xF7B1, 7, 0x0316, 0x1234])
        );
    }
}
