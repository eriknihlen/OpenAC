//! Character management in the login flow: delete, restore, create, the
//! return to selection and the next-login character. Ported from OpenAC
//! `WorldSessionCharacterSelectionTests.cs`, `WorldSessionCharacterCreationTests.cs`,
//! `LiveSessionControllerCharacterCreationTests.cs` and `LiveSessionControllerTests.cs`.

use super::*;
use ac_bits::Writer;

fn words(words: &[u32]) -> Vec<u8> {
    words.iter().flat_map(|w| w.to_le_bytes()).collect()
}

fn create_object(guid: u32) -> Vec<u8> {
    words(&[0xF745, guid, 0x11, 0, 0, 0, 0, 0, 0, 0])
}

fn opcodes(flow: &mut SessionFlow) -> Vec<(u32, Queue)> {
    flow.drain_outgoing()
        .map(|o| (read_opcode(&o.body).unwrap(), o.queue))
        .collect()
}

fn bodies(flow: &mut SessionFlow) -> Vec<(Vec<u8>, Queue)> {
    flow.drain_outgoing().map(|o| (o.body, o.queue)).collect()
}

fn list_with(characters: &[(u32, &str, u32)]) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF658);
    w.u32(0);
    w.u32(characters.len() as u32);
    for (id, name, greyed) in characters {
        w.u32(*id);
        w.string16l(name).unwrap();
        w.u32(*greyed);
    }
    w.u32(0);
    w.i32(11);
    w.string16l("testaccount").unwrap();
    w.u32(1);
    w.u32(1);
    w.into_vec()
}

fn chargen() -> Arc<ChargenOptions> {
    use ac_core::chargen::{
        ChargenAttributeValues, ChargenGenderOptions, ChargenHeritageOptions, ChargenTemplate,
    };
    let gender = ChargenGenderOptions {
        gender_key: 1,
        ..ChargenGenderOptions::default()
    };
    let heritage = ChargenHeritageOptions {
        heritage_id: 1,
        attribute_credits: 60,
        templates: vec![ChargenTemplate {
            attributes: ChargenAttributeValues {
                strength: 10,
                endurance: 10,
                coordination: 10,
                quickness: 10,
                focus: 10,
                self_: 10,
            },
            ..ChargenTemplate::default()
        }],
        genders_by_key: [(1, gender)].into(),
        ..ChargenHeritageOptions::default()
    };
    Arc::new(ChargenOptions {
        heritages_by_id: [(1, heritage)].into(),
        ..ChargenOptions::default()
    })
}

/// At character selection with Zed (0) and Amy (1), creation options
/// installed, nothing queued.
fn selecting() -> SessionFlow {
    let mut flow = SessionFlow::with_generation(
        Duration::ZERO,
        Generation(7),
        chargen(),
        Box::new(SeededRandom::new(3)),
    );
    flow.handle_message(Duration::ZERO, &words(&[0xF7E5]));
    flow.handle_message(
        Duration::ZERO,
        &list_with(&[(0x5000_0002, "Zed", 0), (0x5000_0003, "Amy", 0)]),
    );
    flow.handle_message(Duration::ZERO, &words(&[0xF7EA]));
    flow.drain_outgoing().for_each(drop);
    flow.drain_events().for_each(drop);
    flow
}

fn ready_character(flow: &mut SessionFlow, name: &str) {
    for command in [
        CreationCommand::SelectHeritage(1),
        CreationCommand::SelectGender(1),
        CreationCommand::SelectTemplate(0),
        CreationCommand::SetName(name.to_owned()),
    ] {
        flow.creation_command(&command).unwrap();
    }
}

fn finish(flow: &mut SessionFlow) {
    flow.creation_command(&CreationCommand::Finish {
        confirm_unspent_credits: false,
    })
    .unwrap();
}

fn chargen_answer(code: u32, guid: u32, name: &str) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF643);
    w.u32(code);
    if code == 1 {
        w.u32(guid);
        w.string16l(name).unwrap();
        w.u32(0);
    }
    w.into_vec()
}

#[test]
fn delete_and_restore_use_the_retail_queues_and_the_wire_slot() {
    // CharacterManagementSends_UseRetailQueuesAndExactBodies and
    // CharacterCommands_UseWireSlot*: delete on the login queue with the
    // server's list index, restore on the control queue with the id.
    let mut flow = selecting();
    flow.highlight(&CharacterSelector::Name("amy".into()))
        .unwrap();
    assert_eq!(flow.request_delete().unwrap(), 0x5000_0003);
    assert_eq!(flow.confirm_delete().unwrap(), 0x5000_0003);
    assert_eq!(
        bodies(&mut flow),
        [(
            build_character_delete("testaccount", 1).unwrap(),
            Queue::Login
        )]
    );
    flow.handle_message(Duration::ZERO, &words(&[0xF655]));
    assert_eq!(
        flow.selection().snapshot().operation,
        crate::session::selection::SelectionOperation::DeleteAcknowledged
    );
    flow.handle_message(
        Duration::ZERO,
        &list_with(&[(0x5000_0002, "Zed", 0), (0x5000_0003, "Amy", 3600)]),
    );
    flow.highlight(&CharacterSelector::Id(0x5000_0003)).unwrap();
    assert_eq!(flow.restore(Duration::ZERO).unwrap(), 0x5000_0003);
    assert_eq!(
        bodies(&mut flow),
        [(build_character_restore(0x5000_0003), Queue::Control)]
    );
    flow.handle_message(Duration::ZERO, &chargen_answer(1, 0x5000_0003, "Amy"));
    assert!(
        !flow
            .selection()
            .get(0x5000_0003)
            .unwrap()
            .is_pending_delete()
    );
}

#[test]
fn a_created_character_joins_the_roster_and_enters_at_once() {
    // Finish_ThenOkResponse_AppendsToRosterAndLogsStraightIn.
    let mut flow = selecting();
    ready_character(&mut flow, "NewChar");
    finish(&mut flow);
    let sent = bodies(&mut flow);
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0].1, Queue::Login);
    assert_eq!(read_opcode(&sent[0].0).unwrap(), 0xF656);
    flow.handle_message(Duration::ZERO, &chargen_answer(1, 0x5000_1234, "NewChar"));
    assert!(flow.drain_events().any(|e| e
        == SessionEvent::CharacterCreated {
            guid: 0x5000_1234,
            name: "NewChar".into()
        }));
    let index = |id| flow.selection().get(id).unwrap().active_index;
    assert_eq!(
        (index(0x5000_0002), index(0x5000_0003), index(0x5000_1234)),
        (0, 1, 2)
    );
    assert_eq!(flow.phase(), Phase::EnteringWorld);
    assert_eq!(opcodes(&mut flow), [(0xF7C8, Queue::Ui)]);
    flow.handle_message(Duration::ZERO, &words(&[0xF7DF]));
    assert_eq!(
        bodies(&mut flow),
        [(
            build_enter_world(0x5000_1234, "testaccount").unwrap(),
            Queue::Ui
        )]
    );
    assert_eq!(flow.active_character().map(|c| c.id), Some(0x5000_1234));
}

#[test]
fn a_second_create_after_a_refused_enter_takes_the_next_wire_slot() {
    // SecondCreate_AfterRejectedEnter_GetsTheNextWireSlot.
    let mut flow = selecting();
    ready_character(&mut flow, "NewChar");
    finish(&mut flow);
    flow.handle_message(Duration::ZERO, &chargen_answer(1, 0x5000_1234, "NewChar"));
    flow.handle_message(Duration::ZERO, &words(&[0xF659, 0x0D]));
    assert_eq!(flow.phase(), Phase::InCharacterSelect);
    flow.creation_command(&CreationCommand::SetName("SecondChar".into()))
        .unwrap();
    finish(&mut flow);
    flow.handle_message(
        Duration::ZERO,
        &chargen_answer(1, 0x5000_5678, "SecondChar"),
    );
    let index = |id| flow.selection().get(id).unwrap().active_index;
    assert_eq!(index(0x5000_1234), 2);
    assert_eq!(index(0x5000_5678), 3);
    assert_eq!(index(0x5000_0002), 0);
}

#[test]
fn a_refused_create_reports_the_name_and_stays_at_selection() {
    // Finish_ThenNameInUseResponse_SurfacesRejectionAndStaysAwaitingSelection.
    let mut flow = selecting();
    ready_character(&mut flow, "NewChar");
    finish(&mut flow);
    flow.drain_outgoing().for_each(drop);
    flow.handle_message(Duration::ZERO, &chargen_answer(3, 0, ""));
    assert!(flow.drain_events().any(|e| e
        == SessionEvent::CreationFailed {
            code: 3,
            reason: "NameInUse".into(),
            name: "NewChar".into()
        }));
    assert_eq!(flow.phase(), Phase::InCharacterSelect);
    assert!(flow.selection().get(0x5000_1234).is_none());
    assert!(bodies(&mut flow).is_empty());
}

#[test]
fn an_answer_nothing_asked_for_is_dropped() {
    // ResponseWithNoOutstandingRequest_IsDroppedAndNeverMisattributed and
    // SecondResponse_AfterFirstAlreadyConsumed_IsDroppedNotMisattributed.
    let mut flow = selecting();
    flow.handle_message(Duration::ZERO, &chargen_answer(1, 0x5000_9999, "Ghost"));
    assert!(flow.selection().get(0x5000_9999).is_none());
    ready_character(&mut flow, "NewChar");
    finish(&mut flow);
    flow.handle_message(Duration::ZERO, &chargen_answer(3, 0, ""));
    flow.handle_message(Duration::ZERO, &chargen_answer(1, 0x5000_1234, "NewChar"));
    assert!(flow.selection().get(0x5000_1234).is_none());
}

#[test]
fn unspent_points_refuse_locally_and_send_nothing() {
    // Finish_RefusedLocallyWithUnspentAttributeCredits_NeverTouchesTheWire.
    let mut flow = selecting();
    for command in [
        CreationCommand::SelectHeritage(1),
        CreationCommand::SelectGender(1),
        CreationCommand::SetName("Half".into()),
    ] {
        flow.creation_command(&command).unwrap();
    }
    assert!(matches!(
        flow.creation_command(&CreationCommand::Finish {
            confirm_unspent_credits: false
        }),
        Err(SessionError::Refused(_))
    ));
    assert!(bodies(&mut flow).is_empty());
}

fn selecting_in_world() -> SessionFlow {
    let mut flow = selecting();
    flow.enter_world(Duration::ZERO, &CharacterSelector::Name("Zed".into()))
        .unwrap();
    flow.handle_message(Duration::ZERO, &words(&[0xF7DF]));
    flow.handle_message(Duration::ZERO, &create_object(0x5000_0002));
    flow.drain_outgoing().for_each(drop);
    flow.drain_events().for_each(drop);
    flow
}

#[test]
fn a_logoff_to_selection_starts_a_new_generation_on_the_same_connection() {
    // CompleteCharacterLogOff_ReturnsToSelectionOnTheLiveSession.
    let mut flow = selecting_in_world();
    assert_eq!(flow.generation(), Generation(7));
    flow.request_return_to_selection(Duration::ZERO).unwrap();
    assert_eq!(opcodes(&mut flow), [(0xF653, Queue::Ui)]);
    flow.handle_message(Duration::ZERO, &words(&[0xF653]));
    assert_eq!(flow.phase(), Phase::InCharacterSelect);
    assert_eq!(flow.generation(), Generation(8));
    assert!(flow.active_character().is_none());
    assert!(!flow.login_complete_sent());
    let events: Vec<_> = flow.drain_events().collect();
    assert!(events.contains(&SessionEvent::ReturnedToCharacterSelect(Generation(8))));
    assert!(events.contains(&SessionEvent::LoggedOff { confirmed: true }));
    assert_eq!(flow.outbox_mut().last_sequence(), 0);
    assert_eq!(flow.selection().entries().len(), 2);
    flow.enter_world(Duration::ZERO, &CharacterSelector::Name("Amy".into()))
        .unwrap();
    assert_eq!(flow.phase(), Phase::EnteringWorld);
}

#[test]
fn the_next_login_character_enters_once_after_the_return() {
    // NextLoginAutomaticallyEntersTheRememberedRosterCharacterOnce.
    let mut flow = selecting_in_world();
    flow.set_next_login(Some(&CharacterSelector::Name("Amy".into())))
        .unwrap();
    assert_eq!(flow.next_login_character_id(), 0x5000_0003);
    flow.request_return_to_selection(Duration::ZERO).unwrap();
    flow.handle_message(Duration::ZERO, &words(&[0xF653]));
    assert_eq!(flow.phase(), Phase::EnteringWorld);
    assert_eq!(flow.active_character().map(|c| c.id), Some(0x5000_0003));
    assert_eq!(flow.next_login_character_id(), 0);
}

#[test]
fn selection_commands_are_refused_in_the_world() {
    let mut flow = selecting_in_world();
    assert!(matches!(
        flow.request_delete(),
        Err(SessionError::WrongPhase { .. })
    ));
    assert!(matches!(
        flow.creation_command(&CreationCommand::SelectHeritage(1)),
        Err(SessionError::WrongPhase { .. })
    ));
}
