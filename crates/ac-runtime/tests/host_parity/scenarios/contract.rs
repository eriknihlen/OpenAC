//! The character's contracts, as a plugin reads them (OpenAC
//! `Scenarios/ContractParityTests.cs`): the words come from the tables the
//! session was given; without them a contract still reports its id, stage
//! and progress.

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::quests::{ContractCatalog, ContractEntry};
use ac_plugin::abi::calls;
use ac_plugin::abi::world::Contract;
use ac_runtime::gameplay::GameTables;

use crate::host_parity::arm::{Arm, Setup, run_with};
use crate::host_parity::transcript::Transcript;
use crate::host_parity::{wire, world};

const HUNT: u32 = 0x0000_0010;
/// The first stage that carries a count (`ac-proto` `contract::stage`).
const PROGRESS_COUNTER: u32 = 4;

/// The server tracks the hunt for the character (OpenAC `Track`).
fn track(arm: &mut Arm) {
    arm.server.game_event(
        world::PLAYER,
        0x0315,
        &wire::contract_tracker(HUNT, PROGRESS_COUNTER, false, true),
    );
    arm.sync();
}

fn record(transcript: &mut Transcript, contracts: &[Contract]) {
    transcript.record("contracts.count", contracts.len());
    for (index, c) in contracts.iter().enumerate() {
        transcript.record_id(&format!("contracts[{index}].id"), c.contract_id);
        transcript.record(&format!("contracts[{index}].stage"), c.stage);
        transcript.record(&format!("contracts[{index}].name"), &c.name);
        transcript.record(&format!("contracts[{index}].description"), &c.description);
        transcript.record(&format!("contracts[{index}].status"), &c.status);
        transcript.record(&format!("contracts[{index}].displayed"), c.is_displayed);
    }
}

// OpenAC `ContractParityTests.WithoutTheWordsBothClientsStillReportTheContract`.
#[test]
fn without_the_words_both_clients_still_report_the_contract() {
    run_with(&Setup::default(), &[], true, |arm, transcript| {
        track(arm);
        transcript.step("a plugin reads the contracts");
        let contracts = arm.call::<calls::WorldContracts>(&());
        record(transcript, &contracts);
        assert_eq!(contracts.len(), 1, "{}", arm.name());
        assert_eq!(contracts[0].contract_id, HUNT);
        assert_eq!(contracts[0].name, "");
    });
}

// OpenAC `ContractParityTests.WithTheWordsBothClientsReadTheSameNames`.
#[test]
fn with_the_words_both_clients_read_the_same_names() {
    let catalog = ContractCatalog::new(HashMap::from([(
        HUNT,
        ContractEntry {
            contract_id: HUNT,
            contract_name: "Tusker Hunt".into(),
            description: "Thin the herd.".into(),
            ..ContractEntry::default()
        },
    )]));
    let setup = Setup {
        tables: Some(Arc::new(GameTables::empty().with_contracts(catalog))),
        ..Setup::default()
    };
    run_with(&setup, &[], true, |arm, transcript| {
        track(arm);
        transcript.step("a plugin reads the contracts");
        let contracts = arm.call::<calls::WorldContracts>(&());
        record(transcript, &contracts);
        assert_eq!(contracts.len(), 1, "{}", arm.name());
        assert_eq!(contracts[0].name, "Tusker Hunt");
        assert_eq!(contracts[0].description, "Thin the herd.");
    });
}
