//! Setting a character option through the plugin surface (OpenAC
//! `Scenarios/CharacterOptionParityTests.cs`): an option the server saves
//! at once goes out, one saved with the set changes at once and sends
//! nothing yet, and an unknown name is refused.

use ac_plugin::abi::calls;
use ac_plugin::abi::character::{OptionResult, OptionStatus};

use crate::host_parity::arm::{Arm, run};
use crate::host_parity::scenarios::item::{one, sent};
use crate::host_parity::transcript::Transcript;
use crate::host_parity::world;

const SET_SINGLE_OPTION_ACTION: u32 = 0x0005;
const ACCEPT_LOOT_PERMITS: u32 = 0x10;

fn stage_world(arm: &mut Arm) {
    world::stage(arm);
    let _ = arm.outbound();
}

fn option(arm: &mut Arm, name: &str) -> Option<bool> {
    arm.call::<calls::CharacterOptions>(&())
        .into_iter()
        .find(|(n, _)| n.eq_ignore_ascii_case(name))
        .map(|(_, v)| v)
}

fn record(transcript: &mut Transcript, key: &str, result: &OptionResult) {
    transcript.record(&format!("{key}.status"), result.status);
    transcript.record(&format!("{key}.notice"), &result.notice);
}

// OpenAC `CharacterOptionParityTests.AnOptionTheServerSavesAtOnceGoesOutOnBothClients`.
#[test]
fn an_option_the_server_saves_at_once_goes_out_on_both_clients() {
    run(|arm, transcript| {
        stage_world(arm);
        transcript.step("turn on accepting loot permits");
        let result = arm.call::<calls::CharacterSetOption>(&("acceptlootpermits".to_owned(), true));
        record(transcript, "set", &result);
        assert_eq!(result.status, OptionStatus::Accepted, "{}", arm.name());
        assert_eq!(option(arm, "AcceptLootPermits"), Some(true));
        let out = arm.record_outbound(transcript);
        let message = one(&out, SET_SINGLE_OPTION_ACTION);
        assert_eq!(message.u32_at(12), ACCEPT_LOOT_PERMITS);
        assert_eq!(message.u32_at(16), 1);
    });
}

// OpenAC `CharacterOptionParityTests.AnOptionSavedWithTheSetChangesAtOnceAndSendsNothingYetOnBothClients`.
#[test]
fn an_option_saved_with_the_set_changes_at_once_and_sends_nothing_yet_on_both_clients() {
    run(|arm, transcript| {
        stage_world(arm);
        let before = option(arm, "AllowGive").expect("AllowGive is an option");

        transcript.step("flip accepting gifts");
        let result = arm.call::<calls::CharacterSetOption>(&("AllowGive".to_owned(), !before));
        record(transcript, "set", &result);
        assert_eq!(result.status, OptionStatus::Accepted, "{}", arm.name());
        let after = option(arm, "AllowGive").expect("AllowGive is an option");
        assert_eq!(after, !before);
        transcript.record("after", after);
        let out = arm.record_outbound(transcript);
        assert!(sent(&out, SET_SINGLE_OPTION_ACTION).is_empty());
    });
}

// OpenAC `CharacterOptionParityTests.AnUnknownNameIsRefusedTheSameOnBothClients`.
#[test]
fn an_unknown_name_is_refused_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_world(arm);
        transcript.step("set an option that does not exist");
        let result = arm.call::<calls::CharacterSetOption>(&("NoSuchOption".to_owned(), true));
        record(transcript, "set", &result);
        assert_eq!(result.status, OptionStatus::UnknownOption, "{}", arm.name());
        assert_eq!(option(arm, "NoSuchOption"), None);
        assert_eq!(option(arm, "6"), None);
        let names: Vec<String> = arm
            .call::<calls::CharacterOptions>(&())
            .into_iter()
            .map(|(n, _)| n)
            .collect();
        assert!(names.iter().any(|n| n == "AllowGive"), "{names:?}");
        assert!(names.iter().any(|n| n == "HearGeneralChat"), "{names:?}");
        transcript.record("names", &names);
        let out = arm.record_outbound(transcript);
        assert!(sent(&out, SET_SINGLE_OPTION_ACTION).is_empty());
    });
}
