//! A fellowship as a plugin reads it (OpenAC
//! `Scenarios/FellowshipParityTests.cs`): the departure notices reach a
//! plugin's chat once, worded the same, and the terms and levels the server
//! states reach it the same.

use ac_plugin::abi::calls;

use crate::host_parity::arm::{Arm, run};
use crate::host_parity::{wire, world};

const FELLOWSHIP_NAME: &str = "Parity Fellows";
const OWN_LEVEL: u32 = 30;
const FELLOW_LEVEL: u32 = 42;

const QUIT: u32 = 0x00A3;
const DISMISS: u32 = 0x00A4;
const DISBAND: u32 = 0x02BF;

fn notices(arm: &mut Arm, expected: &str) -> usize {
    arm.call::<calls::ChatCaptured>(&0)
        .iter()
        .filter(|line| line.text == expected)
        .count()
}

/// OpenAC `FellowshipParityTests.DepartureNoticesReachPluginChatOnBothClients`,
/// one case.
fn departure(kind: u32, self_leads: bool, self_departs: bool, expected: &'static str) {
    run(move |arm, transcript| {
        world::stage(arm);
        let leader = if self_leads {
            world::PLAYER
        } else {
            world::OTHER_PLAYER
        };
        arm.server.game_event(
            world::PLAYER,
            0x02BE,
            &wire::fellowship_full_update(
                FELLOWSHIP_NAME,
                leader,
                true,
                true,
                &[
                    (world::PLAYER, "Parity", OWN_LEVEL),
                    (world::OTHER_PLAYER, "Jerry Seinfeld", FELLOW_LEVEL),
                ],
            ),
        );
        arm.sync();
        let payload = if kind == DISBAND {
            Vec::new()
        } else {
            wire::payload(&[if self_departs {
                world::PLAYER
            } else {
                world::OTHER_PLAYER
            }])
        };
        arm.server.game_event(world::PLAYER, kind, &payload);
        arm.sync();
        let lines = arm.call::<calls::ChatCaptured>(&0);
        let found: Vec<_> = lines.iter().filter(|l| l.text == expected).collect();
        assert_eq!(found.len(), 1, "{}: {lines:?}", arm.name());
        assert_eq!(found[0].log_text_type, 0);
        transcript.record("departure", &found[0].text);
        // Said again, it is not told twice.
        arm.server.game_event(world::PLAYER, kind, &payload);
        arm.sync();
        assert_eq!(notices(arm, expected), 1, "{}", arm.name());
    });
}

#[test]
fn a_fellow_quitting_is_told() {
    departure(
        QUIT,
        false,
        false,
        "Jerry Seinfeld has left your Fellowship.",
    );
}

#[test]
fn the_character_quitting_is_told() {
    departure(
        QUIT,
        false,
        true,
        "You are no longer a member of the Parity Fellows Fellowship.",
    );
}

#[test]
fn a_fellow_dismissed_by_another_is_told() {
    departure(
        DISMISS,
        false,
        false,
        "Jerry Seinfeld has been dismissed from the Fellowship.",
    );
}

#[test]
fn a_fellow_dismissed_by_the_character_is_told() {
    departure(
        DISMISS,
        true,
        false,
        "You dismiss Jerry Seinfeld from your Fellowship.",
    );
}

#[test]
fn the_character_dismissed_is_told() {
    departure(
        DISMISS,
        false,
        true,
        "Jerry Seinfeld has dismissed you from the Fellowship.",
    );
}

#[test]
fn a_disband_by_another_is_told() {
    departure(
        DISBAND,
        false,
        true,
        "Jerry Seinfeld has disbanded your Fellowship.",
    );
}

#[test]
fn a_disband_by_the_character_is_told() {
    departure(DISBAND, true, true, "You have disbanded your Fellowship.");
}

// OpenAC `FellowshipParityTests.AFellowshipsTermsAndLevelsReachAPluginOnBothClients`.
#[test]
fn a_fellowships_terms_and_levels_reach_a_plugin_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        world::stage_another_player(arm);

        transcript.step("no fellowship yet");
        let state = arm.call::<calls::FellowshipState>(&());
        transcript.record("sharing", state.shares_experience);
        assert!(!state.is_in_fellowship);
        assert!(!state.shares_experience);
        assert!(!state.splits_experience_evenly);

        let full = |share, even| {
            wire::fellowship_full_update(
                FELLOWSHIP_NAME,
                world::PLAYER,
                share,
                even,
                &[
                    (world::PLAYER, "Parity", OWN_LEVEL),
                    (world::OTHER_PLAYER, "Another", FELLOW_LEVEL),
                ],
            )
        };

        transcript.step("the server states a fellowship sharing unevenly");
        arm.server
            .game_event(world::PLAYER, 0x02BE, &full(true, false));
        arm.sync();
        let state = arm.call::<calls::FellowshipState>(&());
        transcript.record("inFellowship", state.is_in_fellowship);
        transcript.record("sharing", state.shares_experience);
        transcript.record("even", state.splits_experience_evenly);
        assert!(state.is_in_fellowship, "{}", arm.name());
        assert!(state.shares_experience);
        assert!(!state.splits_experience_evenly);
        let mut roster = arm.call::<calls::FellowshipRoster>(&());
        roster.sort_by_key(|m| m.object_id);
        let levels: Vec<String> = roster
            .iter()
            .map(|m| format!("{:08X}:{}", m.object_id, m.level))
            .collect();
        transcript.record("levels", &levels);
        let level_of = |id| roster.iter().find(|m| m.object_id == id).map(|m| m.level);
        assert_eq!(level_of(world::PLAYER), Some(OWN_LEVEL));
        assert_eq!(level_of(world::OTHER_PLAYER), Some(FELLOW_LEVEL));

        transcript.step("it is restated with an even split, not sharing");
        arm.server
            .game_event(world::PLAYER, 0x02BE, &full(false, true));
        arm.sync();
        let state = arm.call::<calls::FellowshipState>(&());
        transcript.record("sharing", state.shares_experience);
        transcript.record("even", state.splits_experience_evenly);
        assert!(!state.shares_experience, "{}", arm.name());
        assert!(state.splits_experience_evenly);
    });
}

/// A fellow standing far out of sight, one the server says is hidden and
/// one it says is not drawn are still on the roster, at an infinite
/// distance; the fellows the client can see are still only the one in
/// sight.
#[test]
fn the_roster_keeps_fellows_out_of_sight_on_both_clients() {
    const UNDRAWN: u32 = 0x5000_0022;
    const NO_DRAW: u32 = 0x20;
    run(|arm, transcript| {
        world::stage(arm);
        world::stage_another_player(arm);
        let undrawn = wire::Object {
            physics_state: NO_DRAW,
            ..world::bystander(UNDRAWN, world::PLAYER_X - 4.0)
        };
        arm.say_grounded(&undrawn);
        arm.sync();

        transcript.step("the server states a fellowship with fellows out of sight");
        arm.server.game_event(
            world::PLAYER,
            0x02BE,
            &wire::fellowship_full_update(
                FELLOWSHIP_NAME,
                world::PLAYER,
                true,
                true,
                &[
                    (world::PLAYER, "Parity", OWN_LEVEL),
                    (world::OTHER_PLAYER, "Another", FELLOW_LEVEL),
                    (world::PATRON, "Far Away", FELLOW_LEVEL),
                    (world::HIDDEN_MONSTER, "Hidden", FELLOW_LEVEL),
                    (UNDRAWN, "Undrawn", FELLOW_LEVEL),
                ],
            ),
        );
        arm.sync();

        let mut roster = arm.call::<calls::FellowshipRoster>(&());
        roster.sort_by_key(|m| m.object_id);
        let read: Vec<String> = roster
            .iter()
            .map(|m| format!("{:08X}:{}", m.object_id, m.distance))
            .collect();
        transcript.record("roster", &read);
        let distance_of = |id| {
            roster
                .iter()
                .find(|m| m.object_id == id)
                .map(|m| m.distance)
        };
        assert_eq!(roster.len(), 5, "{}: {read:?}", arm.name());
        assert_eq!(distance_of(world::PLAYER), Some(0.0));
        assert!(
            distance_of(world::OTHER_PLAYER).is_some_and(f32::is_finite),
            "{read:?}"
        );
        for out_of_sight in [world::PATRON, world::HIDDEN_MONSTER, UNDRAWN] {
            assert_eq!(
                distance_of(out_of_sight),
                Some(f32::INFINITY),
                "{out_of_sight:08X}: {read:?}"
            );
        }

        let others: Vec<u32> = arm
            .call::<calls::FellowshipMembers>(&())
            .iter()
            .map(|m| m.object_id)
            .collect();
        transcript.record("others", &others);
        assert_eq!(others, [world::OTHER_PLAYER], "{}", arm.name());
    });
}
