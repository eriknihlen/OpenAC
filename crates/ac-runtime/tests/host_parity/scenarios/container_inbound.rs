//! A corpse opened and read through the server's own answers (OpenAC
//! `Scenarios/ContainerInboundParityTests.cs`): the listing, a description
//! off the wire, an unanswered description freeing the channel, an open
//! inside the pacing, and an unsuccessful appraisal of a corpse.

use ac_plugin::abi::calls;
use ac_plugin::abi::items::{ItemStatus, PickupRequest};

use crate::host_parity::arm::{Arm, PACING, run};
use crate::host_parity::scenarios::item::{record, settle_the_pacing};
use crate::host_parity::transcript::Transcript;
use crate::host_parity::{wire, world};

const VALUE_PROPERTY: u32 = 0x0013;
/// Past the appraisal slot's wait for an answer (4990 ms).
const PAST_THE_DESCRIPTION_WAIT: std::time::Duration = std::time::Duration::from_millis(5500);

fn stage(arm: &mut Arm) {
    world::stage(arm);
    world::stage_carried_items(arm);
    world::stage_corpse(arm, world::WITHIN_ARMS_REACH);
    world::stage_corpse_contents(arm);
    let _ = arm.outbound();
}

fn record_loot(transcript: &mut Transcript, arm: &mut Arm) {
    let loot = arm.call::<calls::LootState>(&());
    transcript.record("available", loot.is_available);
    transcript.record("busy", loot.is_busy);
    transcript.record_id("requested", loot.requested_container_id);
    transcript.record_id("current", loot.current_container_id);
    transcript.record("ready", loot.current_contents_ready);
}

fn record_appraisal(transcript: &mut Transcript, arm: &mut Arm) {
    let appraisal = arm.call::<calls::LootState>(&()).appraisal;
    transcript.record_id("appraisal.awaiting", appraisal.awaiting_id);
    transcript.record_id("appraisal.current", appraisal.current_id);
    transcript.record_id("appraisal.abandoned", appraisal.last_abandoned_id);
}

fn use_done(arm: &mut Arm) {
    arm.server
        .game_event(world::PLAYER, 0x01C7, &wire::payload(&[0]));
}

// OpenAC `ContainerInboundParityTests.ACorpseOpenedByTheServersOwnAnswersRunsTheSameOnBothClients`.
#[test]
fn a_corpse_opened_by_the_servers_own_answers_runs_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage(arm);

        transcript.step("nothing open");
        record_loot(transcript, arm);
        transcript.record("corpses", arm.call::<calls::LootCorpses>(&10.0).len());

        transcript.step("open it");
        let open = arm.call::<calls::LootOpen>(&world::CORPSE);
        record(transcript, "open", &open);
        record_loot(transcript, arm);
        let out = arm.record_outbound(transcript);
        assert!(!out.is_empty(), "{}", arm.name());
        assert_eq!(
            arm.call::<calls::LootState>(&()).requested_container_id,
            world::CORPSE
        );

        transcript.step("the server lists what is inside");
        arm.server.game_event(
            world::PLAYER,
            0x0196,
            &wire::view_contents(
                world::CORPSE,
                &[(world::CORPSE_COIN, 0), (world::CORPSE_GEM, 0)],
            ),
        );
        use_done(arm);
        arm.sync();
        settle_the_pacing(arm);
        record_loot(transcript, arm);
        let contents = arm.call::<calls::LootContents>(&());
        transcript.record("contents.count", contents.len());
        for (index, item) in contents.iter().enumerate() {
            transcript.record_id(&format!("contents[{index}].id"), item.object_id);
            transcript.record(&format!("contents[{index}].name"), &item.name);
        }
        let loot = arm.call::<calls::LootState>(&());
        assert_eq!(loot.current_container_id, world::CORPSE, "{}", arm.name());
        assert!(loot.current_contents_ready);
        assert!(
            !loot.is_busy,
            "{}: the answer did not release the open",
            arm.name()
        );
        assert_eq!(contents.len(), 2);

        transcript.step("take the gem");
        let pickup = arm.call::<calls::LootPickup>(&PickupRequest {
            object_id: world::CORPSE_GEM,
            main_pack: false,
        });
        record(transcript, "pickup", &pickup);
        record_loot(transcript, arm);
        arm.record_outbound(transcript);

        transcript.step("the take completes");
        use_done(arm);
        arm.sync();
        record_loot(transcript, arm);

        transcript.step("close it");
        arm.wait(PACING);
        let close = arm.call::<calls::LootClose>(&world::CORPSE);
        record(transcript, "close", &close);
        record_loot(transcript, arm);
        arm.record_outbound(transcript);
    });
}

// OpenAC `ContainerInboundParityTests.ADescriptionComingBackOffTheWireReadsTheSameOnBothClients`.
#[test]
fn a_description_coming_back_off_the_wire_reads_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage(arm);

        transcript.step("ask about the gem");
        let identify = arm.call::<calls::WorldIdentify>(&world::CORPSE_GEM);
        record(transcript, "identify", &identify);
        record_appraisal(transcript, arm);
        arm.record_outbound(transcript);
        assert_eq!(
            arm.call::<calls::LootState>(&()).appraisal.awaiting_id,
            world::CORPSE_GEM,
            "{}",
            arm.name()
        );

        transcript.step("the server answers");
        arm.server.game_event(
            world::PLAYER,
            0x00C9,
            &wire::appraisal_response(world::CORPSE_GEM, &[(VALUE_PROPERTY, 1250)], true),
        );
        arm.sync();
        record_appraisal(transcript, arm);
        let value = arm.call::<calls::WorldIntProperty>(&(world::CORPSE_GEM, VALUE_PROPERTY));
        transcript.record("value", value);
        let appraisal = arm.call::<calls::LootState>(&()).appraisal;
        assert_eq!(appraisal.awaiting_id, 0, "{}", arm.name());
        assert_eq!(appraisal.current_id, world::CORPSE_GEM);
        assert_eq!(value, Some(1250));

        transcript.step("ask about something else");
        let identify = arm.call::<calls::WorldIdentify>(&world::CORPSE_COIN);
        record(transcript, "identify", &identify);
        record_appraisal(transcript, arm);
        arm.record_outbound(transcript);
        assert_eq!(
            arm.call::<calls::LootState>(&()).appraisal.awaiting_id,
            world::CORPSE_COIN
        );
    });
}

// OpenAC `ContainerInboundParityTests.AnUnansweredDescriptionFreesTheChannelTheSameOnBothClients`.
#[test]
fn an_unanswered_description_frees_the_channel_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage(arm);

        transcript.step("ask about the gem");
        let first = arm.call::<calls::WorldIdentify>(&world::CORPSE_GEM);
        record(transcript, "identify", &first);
        record_appraisal(transcript, arm);
        arm.record_outbound(transcript);

        transcript.step("ask again while the first is out");
        let second = arm.call::<calls::WorldIdentify>(&world::CORPSE_COIN);
        record(transcript, "identify", &second);
        record_appraisal(transcript, arm);
        arm.record_outbound(transcript);
        assert_eq!(
            arm.call::<calls::LootState>(&()).appraisal.awaiting_id,
            world::CORPSE_GEM,
            "{}",
            arm.name()
        );

        transcript.step("nothing ever answers");
        arm.wait(PAST_THE_DESCRIPTION_WAIT);
        record_appraisal(transcript, arm);

        transcript.step("ask again after giving up");
        let third = arm.call::<calls::WorldIdentify>(&world::CORPSE_COIN);
        record(transcript, "identify", &third);
        record_appraisal(transcript, arm);
        arm.record_outbound(transcript);
        assert_eq!(
            arm.call::<calls::LootState>(&()).appraisal.awaiting_id,
            world::CORPSE_COIN,
            "{}",
            arm.name()
        );

        transcript.step("and that one is answered");
        arm.server.game_event(
            world::PLAYER,
            0x00C9,
            &wire::appraisal_response(world::CORPSE_COIN, &[(VALUE_PROPERTY, 250)], true),
        );
        arm.sync();
        record_appraisal(transcript, arm);
        assert_eq!(
            arm.call::<calls::LootState>(&()).appraisal.current_id,
            world::CORPSE_COIN
        );
    });
}

// OpenAC `ContainerInboundParityTests.AnOpenInsideThePacingIsBusyThenStartedOnBothClients`.
#[test]
fn an_open_inside_the_pacing_is_busy_then_started_on_both_clients() {
    run(|arm, transcript| {
        stage(arm);

        transcript.step("open it");
        let open = arm.call::<calls::LootOpen>(&world::CORPSE);
        record(transcript, "open", &open);
        arm.record_outbound(transcript);

        transcript.step("open it again straight away");
        let early = arm.call::<calls::LootOpen>(&world::CORPSE);
        let busy = arm.call::<calls::LootState>(&()).is_busy;
        record(transcript, "open", &early);
        transcript.record("busy", busy);
        arm.record_outbound(transcript);
        // Early, not failed, and the pacing that put it there is readable.
        assert_eq!(early.status, ItemStatus::Busy, "{}", arm.name());
        assert!(busy, "{}", arm.name());

        transcript.step("and again once the pacing has lapsed");
        use_done(arm);
        arm.sync();
        arm.wait(PACING);
        record_loot(transcript, arm);
        assert!(!arm.call::<calls::LootState>(&()).is_busy);
        let later = arm.call::<calls::LootOpen>(&world::CORPSE);
        record(transcript, "open", &later);
        arm.record_outbound(transcript);
        assert_eq!(later.status, ItemStatus::Started, "{}", arm.name());
    });
}

// OpenAC `ContainerInboundParityTests.AnUnsuccessfulAppraisalOfACorpseIsAnAnswerOnBothClients`.
#[test]
fn an_unsuccessful_appraisal_of_a_corpse_is_an_answer_on_both_clients() {
    run(|arm, transcript| {
        stage(arm);
        let before = arm.call::<calls::LootCorpses>(&10.0);
        assert_eq!(before.len(), 1, "{}", arm.name());
        transcript.step("not yet asked");
        transcript.record("answered", before[0].is_appraisal_answered);
        transcript.record("identified", before[0].is_identified);
        assert!(!before[0].is_appraisal_answered);

        transcript.step("the server has nothing to say");
        arm.server.game_event(
            world::PLAYER,
            0x00C9,
            &wire::appraisal_response(world::CORPSE, &[], false),
        );
        arm.sync();
        let after = arm.call::<calls::LootCorpses>(&10.0);
        assert_eq!(after.len(), 1);
        transcript.record("answered", after[0].is_appraisal_answered);
        transcript.record("identified", after[0].is_identified);
        assert!(after[0].is_appraisal_answered, "{}", arm.name());
        assert!(!after[0].is_identified);
    });
}
