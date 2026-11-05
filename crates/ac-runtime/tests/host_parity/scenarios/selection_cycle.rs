//! Stepping the selection from one nearby person to the next (OpenAC
//! `Scenarios/SelectionCycleParityTests.cs`): three characters at known
//! distances, walked out and back in, and the creatures left alone.

use ac_plugin::abi::calls;
use ac_plugin::abi::social::SelectionAction;

use crate::host_parity::arm::{Arm, run};
use crate::host_parity::transcript::Transcript;
use crate::host_parity::wire::Object;
use crate::host_parity::world;

/// The three people standing about, nearest first.
const NEAR: u32 = 0x5000_0061;
const MIDDLE: u32 = 0x5000_0062;
const FAR: u32 = 0x5000_0063;
/// Shown on the radar always.
const SHOW_ALWAYS: u8 = 4;

fn person(guid: u32, name: &str, x: f32) -> Object {
    Object {
        object_description_flags: world::BF_PLAYER,
        radar_behavior: Some(SHOW_ALWAYS),
        ..world::on_the_ground(guid, name, world::CREATURE, x)
    }
}

/// Three other characters in a line out from the character, each shown
/// on the radar, so the order they come out in is known in advance.
fn stage_bystanders(arm: &mut Arm) {
    arm.say_grounded(&person(NEAR, "Near", world::PLAYER_X + 2.0));
    arm.say_grounded(&person(MIDDLE, "Middle", world::PLAYER_X + 5.0));
    arm.say_grounded(&person(FAR, "Far", world::PLAYER_X + 9.0));
    arm.sync();
}

fn step(transcript: &mut Transcript, arm: &mut Arm, action: SelectionAction) -> Option<u32> {
    let executed = arm.call::<calls::SelectionExecute>(&action);
    let state = arm.call::<calls::SelectionState>(&());
    transcript.record("executed", executed);
    transcript.record("selected", state.selected);
    transcript.record("previous", state.previous);
    state.selected
}

// OpenAC `SelectionCycleParityTests.SteppingThroughNearbyPeopleLandsOnTheSameOnesInTheSameOrder`.
#[test]
fn stepping_through_nearby_people_lands_on_the_same_ones_in_the_same_order() {
    run(|arm, transcript| {
        world::stage(arm);
        stage_bystanders(arm);
        let _ = arm.outbound();

        for (what, action, expected) in [
            (
                "step out from nothing selected",
                SelectionAction::NextPlayer,
                NEAR,
            ),
            ("step out again", SelectionAction::NextPlayer, MIDDLE),
            ("and again", SelectionAction::NextPlayer, FAR),
            (
                "past the last one, which wraps round",
                SelectionAction::NextPlayer,
                NEAR,
            ),
            (
                "step back in, which wraps the other way",
                SelectionAction::PreviousPlayer,
                FAR,
            ),
            (
                "back to whatever was selected before",
                SelectionAction::PreviousSelection,
                NEAR,
            ),
        ] {
            transcript.step(what);
            let selected = step(transcript, arm, action);
            assert_eq!(selected, Some(expected), "{}: {what}", arm.name());
        }
        arm.record_outbound(transcript);
    });
}

// OpenAC `SelectionCycleParityTests.SteppingThroughPeopleIgnoresCreatures`.
#[test]
fn stepping_through_people_ignores_creatures() {
    run(|arm, transcript| {
        world::stage(arm);
        transcript.step("nobody to step to");
        let selected = step(transcript, arm, SelectionAction::NextPlayer);
        assert_eq!(selected, None, "{}", arm.name());
    });
}

// OpenAC issue #220 `SelectionCycleParityTests.SteppingThroughPeopleReachesSomeoneFarBelow`:
// someone well below, close enough across the ground to be on the radar,
// is in the cycle; reach is ground distance against the radar's 75 m and
// height only weighs into the order. With reach measured by the
// height-weighted order, the one in the pit (40 m out, 30 m down: order
// 76) is never reached.
#[test]
fn stepping_through_people_reaches_someone_far_below() {
    const IN_THE_PIT: u32 = 0x5000_0064;
    run(|arm, transcript| {
        world::stage(arm);
        arm.say_grounded(&person(NEAR, "Near", world::PLAYER_X + 2.0));
        // 30 m below the ground the character stands on.
        let here = arm.grounded(person(IN_THE_PIT, "Pit", world::PLAYER_X));
        let mut pit = arm.grounded(person(IN_THE_PIT, "Pit", world::PLAYER_X + 40.0));
        if let (Some(here), Some(pit)) = (here.position, pit.position.as_mut()) {
            pit.3 = here.3 - 30.0;
        }
        arm.server.send(&pit.build());
        arm.sync();
        let _ = arm.outbound();

        for (what, expected) in [
            ("the one on this level first", NEAR),
            ("then the one down in the pit", IN_THE_PIT),
            ("and round again", NEAR),
        ] {
            transcript.step(what);
            let selected = step(transcript, arm, SelectionAction::NextPlayer);
            assert_eq!(selected, Some(expected), "{}: {what}", arm.name());
        }
    });
}
