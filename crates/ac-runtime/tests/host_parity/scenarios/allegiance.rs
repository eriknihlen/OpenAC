//! Swearing to a patron and breaking a tie, and who the allegiance is
//! (OpenAC `Scenarios/AllegianceParityTests.cs`): each step is asserted per
//! arm on the answer the plugin got and on the id that really left. The
//! allegiance is said by the server and read by the client's own parser.

use ac_plugin::abi::calls;
use ac_plugin::abi::social::{AllegianceMember, AllegianceStatus};

use crate::host_parity::arm::{Arm, run, run_from_login};
use crate::host_parity::scenarios::item::{one, sent};
use crate::host_parity::wire::{self, AllegianceMember as Member};
use crate::host_parity::world;

const SWEAR_ACTION: u32 = 0x001D;
const BREAK_ACTION: u32 = 0x001E;
/// Ask the server to state the allegiance and keep the client told.
const UPDATE_REQUEST_ACTION: u32 = 0x001F;
/// The client's own actions on arrival (login complete, the house query).
const LOGIN_COMPLETE_ACTION: u32 = 0x00A1;
const HOUSE_QUERY_ACTION: u32 = 0x021E;

/// The allegiance the server states for the character: a monarch, the
/// patron above it, and itself below that patron (OpenAC
/// `ParityWorld.StageAllegiance`).
fn stage_allegiance(arm: &mut Arm) {
    let payload = wire::allegiance_update(
        3,
        3,
        1,
        "The Order",
        &Member::new(world::MONARCH, 0, true, "Monarch"),
        &[
            Member::new(world::PATRON, world::MONARCH, false, "Patron"),
            Member::new(world::PLAYER, world::PATRON, true, "Parity"),
        ],
    );
    arm.server.game_event(world::PLAYER, 0x0020, &payload);
    arm.sync();
}

/// The character with an allegiance behind it and another player beside
/// it, the outbound record wiped.
fn stage_world(arm: &mut Arm) {
    world::stage(arm);
    world::stage_another_player(arm);
    stage_allegiance(arm);
    let _ = arm.outbound();
}

// OpenAC `AllegianceParityTests.ArrivingInTheWorldAsksTheServerAboutTheAllegianceOnBothClients`.
#[test]
fn arriving_in_the_world_asks_the_server_about_the_allegiance_on_both_clients() {
    run_from_login(|arm, transcript| {
        transcript.step("arrive in the world");
        let before = arm.outbound();
        assert!(sent(&before, UPDATE_REQUEST_ACTION).is_empty());
        arm.enter_world();
        let out = arm.record_outbound(transcript);
        let request = one(&out, UPDATE_REQUEST_ACTION);
        // Asked once, in the subscribe form.
        assert_eq!(request.u32_at(12), 1, "{}", arm.name());
    });
}

// OpenAC `AllegianceParityTests.ArrivingInTheWorldSendsNothingButThatOneQuestion`:
// this client also says its login is complete and asks after its house on
// arrival, as retail does; beside those two, the allegiance question is the
// one thing it says.
#[test]
fn arriving_in_the_world_sends_nothing_but_that_one_question() {
    run_from_login(|arm, transcript| {
        transcript.step("arrive in the world");
        assert!(arm.outbound().is_empty());
        arm.enter_world();
        let out = arm.record_outbound(transcript);
        let actions: Vec<Option<u32>> = out.iter().map(|m| m.game_action()).collect();
        transcript.record("actions", &actions);
        let questions: Vec<u32> = actions
            .iter()
            .flatten()
            .copied()
            .filter(|a| ![LOGIN_COMPLETE_ACTION, HOUSE_QUERY_ACTION].contains(a))
            .collect();
        assert_eq!(
            questions,
            [UPDATE_REQUEST_ACTION],
            "{}: {out:?}",
            arm.name()
        );
    });
}

// OpenAC `AllegianceParityTests.SwearingToAPlayerLeavesBothClientsWithThatPlayersId`.
#[test]
fn swearing_to_a_player_leaves_both_clients_with_that_players_id() {
    run(|arm, transcript| {
        stage_world(arm);
        transcript.step("swear to the other player");
        let result = arm.call::<calls::AllegianceSwear>(&world::OTHER_PLAYER);
        transcript.record("swear", &result);
        let out = arm.record_outbound(transcript);
        assert_eq!(result.status, AllegianceStatus::Sent, "{}", arm.name());
        assert_eq!(one(&out, SWEAR_ACTION).u32_at(12), world::OTHER_PLAYER);
        assert!(sent(&out, BREAK_ACTION).is_empty());
    });
}

// OpenAC `AllegianceParityTests.BreakingWithThePatronLeavesBothClientsWithThePatronsId`.
#[test]
fn breaking_with_the_patron_leaves_both_clients_with_the_patrons_id() {
    run(|arm, transcript| {
        stage_world(arm);
        transcript.step("break with the patron");
        let result = arm.call::<calls::AllegianceBreak>(&world::PATRON);
        transcript.record("break", &result);
        let out = arm.record_outbound(transcript);
        assert_eq!(result.status, AllegianceStatus::Sent, "{}", arm.name());
        assert_eq!(one(&out, BREAK_ACTION).u32_at(12), world::PATRON);
        assert!(sent(&out, SWEAR_ACTION).is_empty());
    });
}

// OpenAC `AllegianceParityTests.SwearingToACreatureIsRefusedTheSameOnBothClients`.
#[test]
fn swearing_to_a_creature_is_refused_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_world(arm);
        transcript.step("swear to a creature");
        let result = arm.call::<calls::AllegianceSwear>(&world::MONSTER);
        transcript.record("swear", &result);
        let out = arm.record_outbound(transcript);
        assert_eq!(
            result.status,
            AllegianceStatus::InvalidTarget,
            "{}",
            arm.name()
        );
        assert!(sent(&out, SWEAR_ACTION).is_empty());
    });
}

// OpenAC `AllegianceParityTests.BreakingWithAStrangerIsRefusedTheSameOnBothClients`.
#[test]
fn breaking_with_a_stranger_is_refused_the_same_on_both_clients() {
    run(|arm, transcript| {
        stage_world(arm);
        transcript.step("break with a player outside the allegiance");
        let result = arm.call::<calls::AllegianceBreak>(&world::OTHER_PLAYER);
        transcript.record("break", &result);
        let out = arm.record_outbound(transcript);
        assert_eq!(
            result.status,
            AllegianceStatus::InvalidTarget,
            "{}",
            arm.name()
        );
        assert!(sent(&out, BREAK_ACTION).is_empty());
    });
}

// OpenAC `AllegianceParityTests.EitherClientNamesTheMonarchThePatronAndTheVassals`.
#[test]
fn either_client_names_the_monarch_the_patron_and_the_vassals() {
    run(|arm, transcript| {
        world::stage(arm);
        const VASSAL: u32 = 0x5000_0077;
        let member = |id, parent, online, name, rank, level, gender, heritage| Member {
            rank,
            level,
            gender,
            heritage_group: heritage,
            ..Member::new(id, parent, online, name)
        };
        let payload = wire::allegiance_update(
            3,
            4,
            1,
            "The Order",
            &member(world::MONARCH, 0, true, "Monarch", 6, 275, 2, 3),
            &[
                member(world::PATRON, world::MONARCH, false, "Patron", 4, 180, 1, 1),
                member(world::PLAYER, world::PATRON, true, "Parity", 3, 90, 1, 2),
                member(VASSAL, world::PLAYER, true, "Squire", 1, 20, 2, 8),
            ],
        );
        arm.server.game_event(world::PLAYER, 0x0020, &payload);
        arm.sync();
        let state = arm.call::<calls::AllegianceState>(&());

        transcript.step("identities");
        transcript.record("monarch", state.monarch.as_ref().map(|m| m.name.clone()));
        transcript.record("patron", state.patron.as_ref().map(|m| m.name.clone()));
        let vassals: Vec<&str> = state.vassals.iter().map(|v| v.name.as_str()).collect();
        transcript.record("vassals", &vassals);
        transcript.record("followers", state.vassal_count);
        let expected = |object_id, name: &str, rank, level, heritage_group, gender, is_online| {
            AllegianceMember {
                object_id,
                name: name.to_owned(),
                rank,
                level,
                heritage_group,
                gender,
                is_online,
            }
        };
        assert_eq!(
            state.monarch,
            Some(expected(world::MONARCH, "Monarch", 6, 275, 3, 2, true)),
            "{}",
            arm.name()
        );
        assert_eq!(
            state.patron,
            Some(expected(world::PATRON, "Patron", 4, 180, 1, 1, false))
        );
        assert_eq!(
            state.vassals,
            [expected(VASSAL, "Squire", 1, 20, 8, 2, true)]
        );
        assert_eq!(state.vassal_count, 1);
        assert_eq!(state.member_count, 4);
    });
}
