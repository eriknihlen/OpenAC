//! The selection living and dying with the object (OpenAC
//! `Scenarios/SelectionLifetimeParityTests.cs`): removing or hiding the
//! selected object clears it, removing something else or sending the
//! selected object again leaves it.

use ac_plugin::abi::calls;

use crate::host_parity::arm::{Arm, run};
use crate::host_parity::transcript::Transcript;
use crate::host_parity::wire::{self, Object};
use crate::host_parity::world;

fn selected(arm: &mut Arm) -> Option<u32> {
    arm.call::<calls::SelectionState>(&()).selected
}

fn record_selection(transcript: &mut Transcript, arm: &mut Arm) {
    let state = arm.call::<calls::SelectionState>(&());
    transcript.record("selection", state.selected);
    transcript.record("previous", state.previous);
}

fn stage(arm: &mut Arm, transcript: &mut Transcript) {
    world::stage(arm);
    let _ = arm.outbound();
    transcript.step("select a creature");
    let taken = arm.call::<calls::SelectionSelect>(&world::MONSTER);
    transcript.record("selected", taken);
    record_selection(transcript, arm);
}

// OpenAC `SelectionLifetimeParityTests.RemovingTheSelectedObjectClearsTheSelectionOnBothClients`.
#[test]
fn removing_the_selected_object_clears_the_selection_on_both_clients() {
    run(|arm, transcript| {
        stage(arm, transcript);
        transcript.step("the server takes it out of the world");
        arm.server.send(&wire::delete_object(world::MONSTER, 1));
        arm.sync();
        record_selection(transcript, arm);
        assert_eq!(selected(arm), None, "{}", arm.name());
        let known = arm.call::<calls::WorldObject>(&world::MONSTER).is_some();
        transcript.record("known", known);
        arm.record_outbound(transcript);
    });
}

// OpenAC `SelectionLifetimeParityTests.HidingTheSelectedObjectClearsTheSelectionOnBothClients`.
#[test]
fn hiding_the_selected_object_clears_the_selection_on_both_clients() {
    run(|arm, transcript| {
        stage(arm, transcript);
        transcript.step("the server stops showing it");
        arm.server
            .send(&wire::set_state(world::MONSTER, world::HIDDEN, 1, 2));
        arm.sync();
        record_selection(transcript, arm);
        assert_eq!(selected(arm), None, "{}", arm.name());
        arm.record_outbound(transcript);
    });
}

// OpenAC `SelectionLifetimeParityTests.RemovingSomethingElseLeavesTheSelectionAloneOnBothClients`.
#[test]
fn removing_something_else_leaves_the_selection_alone_on_both_clients() {
    run(|arm, transcript| {
        stage(arm, transcript);
        transcript.step("the server takes a different creature out");
        arm.server
            .send(&wire::delete_object(world::SECOND_MONSTER, 1));
        arm.sync();
        record_selection(transcript, arm);
        assert_eq!(selected(arm), Some(world::MONSTER), "{}", arm.name());
        arm.record_outbound(transcript);
    });
}

// OpenAC `SelectionLifetimeParityTests.ReSendingTheSelectedObjectKeepsItSelectedOnBothClients`.
#[test]
fn re_sending_the_selected_object_keeps_it_selected_on_both_clients() {
    run(|arm, transcript| {
        stage(arm, transcript);
        transcript.step("the server sends it again");
        let again = Object {
            instance_seq: 2,
            ..world::monster(world::MONSTER, world::PLAYER_X + 3.0)
        };
        arm.server.send(&again.build());
        arm.sync();
        record_selection(transcript, arm);
        assert_eq!(selected(arm), Some(world::MONSTER), "{}", arm.name());
        let known = arm.call::<calls::WorldObject>(&world::MONSTER).is_some();
        transcript.record("known", known);
        arm.record_outbound(transcript);
    });
}
