//! One contract's authored description and the catalog of them, keyed by
//! contract id. The wire carries only an id, a stage and two timers; every
//! word the player reads comes from here.

use std::collections::HashMap;

/// One contract's authored description.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ContractEntry {
    /// The table version.
    pub version: u32,
    /// The contract id.
    pub contract_id: u32,
    /// The contract's name.
    pub contract_name: String,
    /// What the contract asks for.
    pub description: String,
    /// A progress format with one `%d` for the counter (e.g.
    /// `"%d/20 Tuskers"`), or empty.
    pub description_progress: String,
    /// Who offers the contract.
    pub name_npc_start: String,
    /// Who completes it.
    pub name_npc_end: String,
    /// Quest flag stamped when taken.
    pub questflag_stamped: String,
    /// Quest flag for started.
    pub questflag_started: String,
    /// Quest flag for finished.
    pub questflag_finished: String,
    /// Quest flag for progress.
    pub questflag_progress: String,
    /// Quest flag for the timer.
    pub questflag_timer: String,
    /// Quest flag for the repeat time; empty when the contract cannot be
    /// repeated.
    pub questflag_repeat_time: String,
    /// Landcell of the NPC who offers the contract.
    pub location_npc_start_cell: u32,
    /// Landcell of the NPC who completes it.
    pub location_npc_end_cell: u32,
    /// Landcell of the quest area.
    pub location_quest_area_cell: u32,
}

/// The entry for a contract the catalog does not know: every text empty.
static UNKNOWN: ContractEntry = ContractEntry {
    version: 0,
    contract_id: 0,
    contract_name: String::new(),
    description: String::new(),
    description_progress: String::new(),
    name_npc_start: String::new(),
    name_npc_end: String::new(),
    questflag_stamped: String::new(),
    questflag_started: String::new(),
    questflag_finished: String::new(),
    questflag_progress: String::new(),
    questflag_timer: String::new(),
    questflag_repeat_time: String::new(),
    location_npc_start_cell: 0,
    location_npc_end_cell: 0,
    location_quest_area_cell: 0,
};

impl ContractEntry {
    /// The entry for an unknown contract: every text empty.
    #[must_use]
    pub fn unknown() -> &'static Self {
        &UNKNOWN
    }
}

/// The installed contract catalog.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ContractCatalog {
    contracts: HashMap<u32, ContractEntry>,
}

impl ContractCatalog {
    /// A catalog of these contracts.
    #[must_use]
    pub fn new(contracts: HashMap<u32, ContractEntry>) -> Self {
        Self { contracts }
    }

    /// Every contract by id.
    #[must_use]
    pub fn contracts(&self) -> &HashMap<u32, ContractEntry> {
        &self.contracts
    }

    /// How many contracts.
    #[must_use]
    pub fn len(&self) -> usize {
        self.contracts.len()
    }

    /// True when the catalog is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.contracts.is_empty()
    }

    /// The entry for `contract_id`, or [`ContractEntry::unknown`]. A miss is
    /// normal: the server may track a contract this client's data lacks.
    #[must_use]
    pub fn lookup(&self, contract_id: u32) -> &ContractEntry {
        self.contracts
            .get(&contract_id)
            .unwrap_or(ContractEntry::unknown())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_miss_is_the_unknown_entry() {
        let catalog = ContractCatalog::new(HashMap::from([(
            7,
            ContractEntry {
                contract_id: 7,
                contract_name: "Tusker Hunt".to_owned(),
                ..ContractEntry::default()
            },
        )]));
        assert_eq!(catalog.len(), 1);
        assert_eq!(catalog.lookup(7).contract_name, "Tusker Hunt");
        assert_eq!(catalog.lookup(8), ContractEntry::unknown());
        assert_eq!(*ContractEntry::unknown(), ContractEntry::default());
    }
}
