//! What a plugin is told when an interaction with the world ends (OpenAC
//! `Scenarios/WorldInteractionEventParityTests.cs`): a use finishing, going
//! through a portal, and an activation ended by a portal.
//!
//! OpenAC emits the portal snapshots on the runtime's sink directly; here
//! the server moves the character through a portal (its teleport, then the
//! destination), which is where the client's portal snapshots come from.
//! The window answers the held arrival as soon as it is shown (a window
//! without a drawn world shows it at once); without a window the session
//! completes it itself.

use ac_plugin::abi::Event;
use ac_plugin::abi::calls;
use ac_plugin::abi::items::ItemStatus;
use ac_plugin::abi::world::{ActivationOutcome, PortalTransition};

use crate::host_parity::arm::{Arm, run};
use crate::host_parity::transcript::Transcript;
use crate::host_parity::{wire, world};

/// Where the portal takes the character: the next landblock east.
const DESTINATION: (u32, f32, f32, f32) = (0xAAB4_0001, 12.0, 13.0, 40.0);
/// The kind of a portal transition (`PortalTransition::kind`).
const PORTAL: u8 = 2;

fn transitions(events: &[Event]) -> Vec<PortalTransition> {
    events
        .iter()
        .filter_map(|e| match e {
            Event::PortalTransition(t) => Some(t.clone()),
            _ => None,
        })
        .collect()
}

fn record(transcript: &mut Transcript, key: &str, transitions: &[PortalTransition]) {
    transcript.record(&format!("{key}.count"), transitions.len());
    for (index, t) in transitions.iter().enumerate() {
        transcript.record(&format!("{key}[{index}].revision"), t.revision);
        transcript.record(&format!("{key}[{index}].kind"), t.kind);
        transcript.record(&format!("{key}[{index}].completed"), t.is_completed);
        transcript.record(&format!("{key}[{index}].cancelled"), t.is_cancelled);
        transcript.record_id(&format!("{key}[{index}].destination"), t.destination_cell);
    }
}

/// The server moves the character through a portal and the arrival
/// completes; returns the transitions the plugin heard on the way.
fn go_through_a_portal(arm: &mut Arm) -> Vec<PortalTransition> {
    arm.server.send(&wire::player_teleport(1));
    arm.sync();
    let mut heard = transitions(&arm.events());
    arm.server.send(&wire::update_position(
        world::PLAYER,
        DESTINATION,
        1,
        2,
        1,
        0,
    ));
    arm.sync();
    heard.extend(transitions(&arm.events()));
    arm.until("the arrival completes", |a| {
        a.parity_events()
            .any(|e| matches!(e, Event::PortalTransition(t) if t.is_completed))
    });
    arm.sync();
    heard.extend(transitions(&arm.events()));
    heard
}

// OpenAC `WorldInteractionEventParityTests.AUseFinishingTellsAPluginOnBothClients`.
#[test]
fn a_use_finishing_tells_a_plugin_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        world::stage_corpse(arm, world::WITHIN_ARMS_REACH);
        let _ = arm.events();

        transcript.step("use the corpse");
        let used = arm.call::<calls::ItemsUse>(&world::CORPSE);
        assert_eq!(used.status, ItemStatus::Started, "{}", arm.name());

        transcript.step("the server says the use is done");
        arm.server
            .game_event(world::PLAYER, 0x01C7, &wire::payload(&[0]));
        arm.sync();
        let completions: Vec<_> = arm
            .events()
            .into_iter()
            .filter_map(|e| match e {
                Event::ItemUseCompleted(c) => Some(c),
                _ => None,
            })
            .collect();
        transcript.record("completions", completions.len());
        for (index, c) in completions.iter().enumerate() {
            transcript.record(&format!("completion[{index}].revision"), c.revision);
            transcript.record(&format!("completion[{index}].error"), c.weenie_error);
        }
        // Exactly once, and with no error.
        assert_eq!(completions.len(), 1, "{}", arm.name());
        assert_eq!(completions[0].weenie_error, 0);
    });
}

// OpenAC `WorldInteractionEventParityTests.GoingThroughAPortalTellsAPluginOnBothClients`.
#[test]
fn going_through_a_portal_tells_a_plugin_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        let _ = arm.events();

        transcript.step("the character goes through a portal");
        let heard = go_through_a_portal(arm);
        record(transcript, "transitions", &heard);
        assert!(!heard.is_empty(), "{}", arm.name());
        assert_eq!(heard[0].kind, PORTAL, "{}: {heard:?}", arm.name());
        assert!(!heard[0].is_completed, "{}: {heard:?}", arm.name());
        let last = heard.last().unwrap();
        assert!(last.is_completed, "{}: {heard:?}", arm.name());
        assert_eq!(last.destination_cell, DESTINATION.0);
        // Stamped in order, one step each, and never the same snapshot
        // twice.
        for (index, t) in heard.iter().enumerate() {
            assert_eq!(t.revision, heard[0].revision + index as i64, "{heard:?}");
        }
    });
}

// OpenAC `WorldInteractionEventParityTests.AnActivationEndingTellsAPluginOnBothClients`.
#[test]
fn an_activation_ending_tells_a_plugin_on_both_clients() {
    run(|arm, transcript| {
        world::stage(arm);
        world::stage_corpse(arm, world::WITHIN_ARMS_REACH);
        let _ = arm.events();

        transcript.step("activate the corpse");
        let activate = arm.call::<calls::WorldActivate>(&world::CORPSE);
        transcript.record("activate.status", activate.status);
        let endings = |events: &[Event]| -> Vec<_> {
            events
                .iter()
                .filter_map(|e| match e {
                    Event::ActivationCompleted(c) => Some(c.clone()),
                    _ => None,
                })
                .collect()
        };
        let before = endings(&arm.events());
        transcript.record("endings", before.len());
        assert!(before.is_empty(), "{}", arm.name());

        transcript.step("a portal completes while it is outstanding");
        let _ = go_through_a_portal(arm);
        let after: Vec<_> = endings(&arm.parity_events().collect::<Vec<_>>());
        transcript.record("endings", after.len());
        for (index, e) in after.iter().enumerate() {
            transcript.record_id(&format!("ending[{index}].id"), e.object_id);
            transcript.record(&format!("ending[{index}].outcome"), e.outcome);
        }
        assert_eq!(after.len(), 1, "{}", arm.name());
        assert_eq!(after[0].object_id, world::CORPSE);
        assert_eq!(after[0].outcome, ActivationOutcome::Completed);
    });
}
