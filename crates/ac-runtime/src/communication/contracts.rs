//! The quest tracker: every contract the server says the character has,
//! which one is on display, and when each state arrived (its repeat
//! countdown runs from then).

use std::collections::BTreeMap;

use ac_core::quests::{ContractCatalog, build_progress_text};
use ac_proto::contract::{ContractTracker, ContractTrackerUpdate};

/// One tracked contract and when its state arrived.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TrackedContract {
    /// The contract's state as sent.
    pub tracker: ContractTracker,
    /// When it arrived, UTC milliseconds since the Unix epoch.
    pub received_unix_ms: i64,
}

/// One contract as a host shows it.
#[derive(Debug, Clone, PartialEq)]
pub struct ContractSummary {
    /// The contract id.
    pub contract_id: u32,
    /// Its authored name (empty when the catalog lacks it).
    pub name: String,
    /// The stage as sent.
    pub stage: u32,
    /// The progress column ("In Progress", "Done (...)", "3/20 ...").
    pub progress: String,
    /// It is the contract on display.
    pub displayed: bool,
    /// The progress count at this stage (OpenAC `ContractTracker.Progress`:
    /// the stage past the progress counter, 0 below it).
    pub progress_count: u32,
    /// Its authored description (empty when the catalog lacks it).
    pub description: String,
}

/// The tracker.
#[derive(Debug, Default)]
pub struct ContractState {
    contracts: BTreeMap<u32, TrackedContract>,
    display_contract_id: u32,
    revision: u64,
}

impl ContractState {
    /// No contracts.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Advances on every change.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// How many contracts are tracked.
    #[must_use]
    pub fn len(&self) -> usize {
        self.contracts.len()
    }

    /// True when none are tracked.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.contracts.is_empty()
    }

    /// The contract on display, 0 when none.
    #[must_use]
    pub fn display_contract_id(&self) -> u32 {
        self.display_contract_id
    }

    /// One contract.
    #[must_use]
    pub fn get(&self, contract_id: u32) -> Option<&TrackedContract> {
        self.contracts.get(&contract_id)
    }

    /// Every contract, by id.
    pub fn contracts(&self) -> impl Iterator<Item = &TrackedContract> {
        self.contracts.values()
    }

    /// The full table replaces everything. A display selection the table
    /// still holds stays selected.
    pub fn apply_table(&mut self, table: &BTreeMap<u32, ContractTracker>, now_unix_ms: i64) {
        self.contracts = table
            .iter()
            .map(|(id, tracker)| {
                (
                    *id,
                    TrackedContract {
                        tracker: *tracker,
                        received_unix_ms: now_unix_ms,
                    },
                )
            })
            .collect();
        if self.display_contract_id != 0 && !self.contracts.contains_key(&self.display_contract_id)
        {
            self.display_contract_id = 0;
        }
        self.revision += 1;
    }

    /// One contract changed, was deleted, or was put on display. Deleting
    /// one that is not tracked changes nothing.
    pub fn apply_update(&mut self, update: &ContractTrackerUpdate, now_unix_ms: i64) {
        let id = update.tracker.contract_id;
        if update.delete {
            let removed = self.contracts.remove(&id).is_some();
            if self.display_contract_id == id {
                self.display_contract_id = 0;
            }
            if removed {
                self.revision += 1;
            }
            return;
        }
        self.contracts.insert(
            id,
            TrackedContract {
                tracker: update.tracker,
                received_unix_ms: now_unix_ms,
            },
        );
        if update.set_as_display {
            self.display_contract_id = id;
        }
        self.revision += 1;
    }

    /// Forget everything: the tracker is the server's, sent again at login.
    pub fn reset_session(&mut self) {
        let changed = !self.contracts.is_empty() || self.display_contract_id != 0;
        self.contracts.clear();
        self.display_contract_id = 0;
        if changed {
            self.revision += 1;
        }
    }

    /// Every contract named from `catalog` with its progress text at
    /// `now_unix_ms`.
    #[must_use]
    pub fn summaries(&self, catalog: &ContractCatalog, now_unix_ms: i64) -> Vec<ContractSummary> {
        self.contracts
            .values()
            .map(|c| {
                let entry = catalog.lookup(c.tracker.contract_id);
                ContractSummary {
                    contract_id: c.tracker.contract_id,
                    name: entry.contract_name.clone(),
                    stage: c.tracker.stage,
                    progress: build_progress_text(
                        c.tracker.stage,
                        c.tracker.time_when_repeats,
                        ms_to_seconds(c.received_unix_ms),
                        entry,
                        ms_to_seconds(now_unix_ms),
                    ),
                    displayed: c.tracker.contract_id == self.display_contract_id,
                    progress_count: c.tracker.progress(),
                    description: entry.description.clone(),
                }
            })
            .collect()
    }
}

#[allow(clippy::cast_precision_loss)]
fn ms_to_seconds(ms: i64) -> f64 {
    ms as f64 / 1000.0
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_proto::contract::stage;

    const ARRIVAL: i64 = 1_787_317_200_000;

    fn tracker(id: u32, stage: u32) -> ContractTracker {
        ContractTracker {
            version: 1,
            contract_id: id,
            stage,
            time_when_done: 0.0,
            time_when_repeats: 0.0,
        }
    }

    fn update(id: u32, stage: u32, delete: bool, set_as_display: bool) -> ContractTrackerUpdate {
        ContractTrackerUpdate {
            tracker: tracker(id, stage),
            delete,
            set_as_display,
        }
    }

    // Golden behaviour: OpenAC tests/AcDream.Runtime.Tests/Gameplay/RuntimeContractStateTests.cs

    #[test]
    fn an_update_adds_and_a_second_replaces() {
        let mut state = ContractState::new();
        state.apply_update(&update(0x10, stage::AVAILABLE, false, false), ARRIVAL);
        state.apply_update(&update(0x10, stage::IN_PROGRESS, false, false), ARRIVAL);
        assert_eq!(state.get(0x10).unwrap().tracker.stage, stage::IN_PROGRESS);
        assert_eq!(state.len(), 1);
    }

    #[test]
    fn delete_removes_and_clears_the_display_selection() {
        let mut state = ContractState::new();
        state.apply_update(&update(0x10, 2, false, true), ARRIVAL);
        assert_eq!(state.display_contract_id(), 0x10);
        state.apply_update(&update(0x10, 2, true, false), ARRIVAL);
        assert!(state.get(0x10).is_none());
        assert_eq!(state.display_contract_id(), 0);
    }

    #[test]
    fn a_table_replaces_everything() {
        let mut state = ContractState::new();
        state.apply_update(&update(0x10, 2, false, false), ARRIVAL);
        state.apply_update(&update(0x20, 2, false, false), ARRIVAL);
        state.apply_table(&BTreeMap::from([(0x30, tracker(0x30, 2))]), ARRIVAL);
        assert!(state.get(0x10).is_none());
        assert!(state.get(0x30).is_some());
        assert_eq!(state.len(), 1);
        state.apply_table(&BTreeMap::new(), ARRIVAL);
        assert!(state.is_empty());
    }

    #[test]
    fn a_table_keeps_the_display_selection_only_when_it_holds_it() {
        let mut state = ContractState::new();
        state.apply_update(&update(0x10, 2, false, true), ARRIVAL);
        state.apply_table(&BTreeMap::from([(0x10, tracker(0x10, 2))]), ARRIVAL);
        assert_eq!(state.display_contract_id(), 0x10);
        state.apply_table(&BTreeMap::from([(0x20, tracker(0x20, 2))]), ARRIVAL);
        assert_eq!(state.display_contract_id(), 0);
    }

    #[test]
    fn contracts_come_back_in_id_order() {
        let mut state = ContractState::new();
        for id in [0x30, 0x10, 0x20] {
            state.apply_update(&update(id, 2, false, false), ARRIVAL);
        }
        let ids: Vec<u32> = state.contracts().map(|c| c.tracker.contract_id).collect();
        assert_eq!(ids, [0x10, 0x20, 0x30]);
    }

    #[test]
    fn mutations_advance_the_revision_but_a_stray_delete_does_not() {
        let mut state = ContractState::new();
        let start = state.revision();
        state.apply_update(&update(0x99, 2, true, false), ARRIVAL);
        assert_eq!(state.revision(), start);
        state.apply_update(&update(0x10, 2, false, false), ARRIVAL);
        let after_add = state.revision();
        state.apply_table(&BTreeMap::new(), ARRIVAL);
        assert!(after_add > start && state.revision() > after_add);
    }

    #[test]
    fn reset_session_clears_and_is_safe_to_repeat() {
        let mut state = ContractState::new();
        state.apply_update(&update(0x10, 2, false, true), ARRIVAL);
        state.reset_session();
        state.reset_session();
        assert!(state.is_empty());
        assert_eq!(state.display_contract_id(), 0);
    }

    #[test]
    fn summaries_count_the_repeat_timer_from_arrival() {
        let mut state = ContractState::new();
        let mut done = update(0x10, stage::DONE_OR_PENDING_REPEAT, false, true);
        done.tracker.time_when_repeats = 3_661.0;
        state.apply_update(&done, ARRIVAL);
        let summaries = state.summaries(&ContractCatalog::default(), ARRIVAL);
        assert_eq!(summaries[0].progress, "Done (1h 1m 1s to Repeat)");
        assert!(summaries[0].displayed);
        let later = state.summaries(&ContractCatalog::default(), ARRIVAL + 3_662_000);
        assert_eq!(later[0].progress, "Available");
    }

    // Golden values: OpenAC tests/AcDream.Runtime.Tests/Gameplay/
    // ContractPluginProjectionTests.cs (what a plugin's ContractSnapshot
    // carries).
    fn catalog(id: u32, name: &str, progress_format: &str) -> ContractCatalog {
        let entry = ac_core::quests::ContractEntry {
            contract_id: id,
            contract_name: name.to_owned(),
            description: "Do the thing.".to_owned(),
            description_progress: progress_format.to_owned(),
            ..ac_core::quests::ContractEntry::default()
        };
        ContractCatalog::new(std::collections::HashMap::from([(id, entry)]))
    }

    #[test]
    fn the_projection_carries_the_authored_text_and_the_retail_status() {
        let mut state = ContractState::new();
        state.apply_update(&update(0x10, 9, false, false), ARRIVAL);
        let summaries = state.summaries(&catalog(0x10, "Tusker Hunt", "%d/20 Tuskers"), ARRIVAL);
        let s = &summaries[0];
        assert_eq!((s.contract_id, s.stage, s.progress_count), (0x10, 9, 5));
        assert_eq!(s.name, "Tusker Hunt");
        assert_eq!(s.description, "Do the thing.");
        assert_eq!(s.progress, "5/20 Tuskers");
    }

    #[test]
    fn without_a_catalog_the_numbers_still_project() {
        let mut state = ContractState::new();
        state.apply_update(&update(0x10, 6, false, false), ARRIVAL);
        state.apply_update(&update(0xDEAD, 2, false, false), ARRIVAL);
        let summaries = state.summaries(&catalog(0x20, "Something Else", ""), ARRIVAL);
        assert_eq!((summaries[0].stage, summaries[0].progress_count), (6, 2));
        assert_eq!(summaries[0].name, "");
        assert_eq!(summaries[0].description, "");
        assert_eq!(summaries[1].contract_id, 0xDEAD);
        assert_eq!(summaries[1].progress_count, 0);
        assert_eq!(summaries[1].progress, "In Progress");
    }
}
