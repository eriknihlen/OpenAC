//! The login flow: data check, selection, entering, log off (ported from
//! OpenAC `WorldSession` and `LiveSessionController` behaviour), and
//! character management (OpenAC `WorldSessionCharacterSelectionTests.cs`,
//! `WorldSessionCharacterCreationTests.cs`,
//! `LiveSessionControllerCharacterCreationTests.cs`, `LiveSessionControllerTests.cs`).

use super::*;
use ac_bits::Writer;

const PLAYER: u32 = 0x5000_000A;

fn words(words: &[u32]) -> Vec<u8> {
    words.iter().flat_map(|w| w.to_le_bytes()).collect()
}

/// The smallest well-formed create-object body: guid, empty appearance,
/// empty physics block with zero counters, no description.
fn create_object(guid: u32) -> Vec<u8> {
    words(&[0xF745, guid, 0x11, 0, 0, 0, 0, 0, 0, 0])
}

fn character_list(characters: &[(u32, &str, u32)]) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF658);
    w.u32(0);
    w.u32(characters.len() as u32);
    for (id, name, greyed) in characters {
        w.u32(*id);
        w.string16l(name).unwrap();
        w.u32(*greyed);
    }
    w.u32(0); // no deleted characters
    w.i32(11);
    w.string16l("TestAccount").unwrap();
    w.u32(1);
    w.u32(1);
    w.into_vec()
}

fn opcodes(flow: &mut SessionFlow) -> Vec<(u32, Queue)> {
    flow.drain_outgoing()
        .map(|o| (read_opcode(&o.body).unwrap(), o.queue))
        .collect()
}

/// A session through the data check and the character list.
fn connected() -> SessionFlow {
    let mut flow = SessionFlow::new(Duration::ZERO);
    flow.handle_message(Duration::ZERO, &words(&[0xF7E5]));
    flow.handle_message(Duration::ZERO, &character_list(&[(PLAYER, "+Acdream", 0)]));
    flow.handle_message(Duration::ZERO, &words(&[0xF7EA]));
    flow
}

fn in_world() -> SessionFlow {
    let mut flow = connected();
    flow.enter_world(Duration::ZERO, &CharacterSelector::FirstAvailable)
        .unwrap();
    flow.handle_message(Duration::ZERO, &words(&[0xF7DF]));
    flow.drain_outgoing().for_each(drop);
    flow
}

#[test]
fn data_check_answers_on_the_database_queue_and_finishes_connecting() {
    // Sequence from OpenAC WorldSession: interrogation -> response,
    // completion -> one echo, then character select.
    let mut flow = connected();
    assert_eq!(flow.phase(), Phase::InCharacterSelect);
    assert_eq!(
        opcodes(&mut flow),
        [(0xF7E6, Queue::Database), (0xF7EA, Queue::Database)]
    );
    // A second completion is not echoed again.
    flow.handle_message(Duration::ZERO, &words(&[0xF7EA]));
    assert!(opcodes(&mut flow).is_empty());
}

#[test]
fn completion_without_interrogation_is_not_echoed() {
    let mut flow = SessionFlow::new(Duration::ZERO);
    flow.handle_message(Duration::ZERO, &words(&[0xF7EA]));
    flow.handle_message(Duration::ZERO, &character_list(&[(PLAYER, "+Acdream", 0)]));
    assert!(opcodes(&mut flow).is_empty());
    assert_eq!(flow.phase(), Phase::InCharacterSelect);
}

#[test]
fn malformed_completion_and_data_push_fail_the_session() {
    let mut flow = SessionFlow::new(Duration::ZERO);
    flow.handle_message(Duration::ZERO, &words(&[0xF7EA, 0]));
    assert_eq!(flow.phase(), Phase::Failed);
    let mut flow = SessionFlow::new(Duration::ZERO);
    flow.handle_message(Duration::ZERO, &words(&[0xF7E2]));
    assert_eq!(flow.phase(), Phase::Failed);
}

#[test]
fn connect_timeout_names_the_missing_step() {
    let mut flow = SessionFlow::new(Duration::ZERO);
    flow.tick(CONNECT_TIMEOUT - Duration::from_millis(1));
    assert_eq!(flow.phase(), Phase::Connecting);
    flow.tick(CONNECT_TIMEOUT);
    assert_eq!(flow.phase(), Phase::Failed);
    assert!(
        flow.drain_events().any(|e| e
            == SessionEvent::Failed(SessionError::ConnectTimeout("character list not received")))
    );
}

#[test]
fn enter_world_waits_for_server_ready() {
    let mut flow = connected();
    flow.drain_outgoing().for_each(drop);
    flow.enter_world(Duration::ZERO, &CharacterSelector::Name("+ACDREAM".into()))
        .unwrap();
    assert_eq!(flow.phase(), Phase::EnteringWorld);
    assert_eq!(opcodes(&mut flow), [(0xF7C8, Queue::Ui)]);
    flow.handle_message(Duration::ZERO, &words(&[0xF7DF]));
    assert_eq!(flow.phase(), Phase::InWorld);
    assert_eq!(opcodes(&mut flow), [(0xF657, Queue::Ui)]);
}

#[test]
fn greyed_out_characters_are_skipped() {
    let mut flow = SessionFlow::new(Duration::ZERO);
    flow.handle_message(
        Duration::ZERO,
        &character_list(&[(1, "Old", 3600), (2, "New", 0)]),
    );
    flow.handle_message(Duration::ZERO, &words(&[0xF7EA]));
    flow.enter_world(Duration::ZERO, &CharacterSelector::FirstAvailable)
        .unwrap();
    assert_eq!(flow.active_character().map(|c| c.id), Some(2));
    let mut flow = SessionFlow::new(Duration::ZERO);
    flow.handle_message(Duration::ZERO, &character_list(&[(1, "Old", 3600)]));
    flow.handle_message(Duration::ZERO, &words(&[0xF7EA]));
    assert!(matches!(
        flow.enter_world(Duration::ZERO, &CharacterSelector::Name("Old".into())),
        Err(SessionError::NoCharacter(_))
    ));
}

#[test]
fn character_error_returns_to_character_select() {
    let mut flow = connected();
    flow.enter_world(Duration::ZERO, &CharacterSelector::FirstAvailable)
        .unwrap();
    // The NumErrors sentinel (0x19) is ignored; a real error is not.
    flow.handle_message(Duration::ZERO, &words(&[0xF659, 0x19]));
    assert_eq!(flow.phase(), Phase::EnteringWorld);
    flow.handle_message(Duration::ZERO, &words(&[0xF659, 0x0D]));
    assert_eq!(flow.phase(), Phase::InCharacterSelect);
}

/// The server's boot of a session whose account logs in again elsewhere:
/// When the account logs in again elsewhere, the server sends
/// `CharacterError(Logon)` to the connected session and drops it. The game
/// then shows its disconnected screen with the error's text, in the world
/// or not.
fn ended_by(flow: &mut SessionFlow) -> Option<SessionError> {
    flow.drain_events().find_map(|e| match e {
        SessionEvent::Failed(error) => Some(error),
        _ => None,
    })
}

#[test]
fn a_boot_in_the_world_ends_the_session_at_once_with_its_text() {
    let mut flow = in_world();
    flow.drain_events().for_each(drop);
    flow.handle_message(Duration::ZERO, &words(&[0xF659, 0x01]));
    assert_eq!(flow.phase(), Phase::Failed);
    let error = ended_by(&mut flow).expect("the session failed");
    assert_eq!(
        error,
        SessionError::ServerEnded {
            code: 1,
            reason: "Another account is already logged on from this client.".into(),
        }
    );
    assert_eq!(
        error.to_string(),
        "the server ended the session: Another account is already logged on from this client. (error 0x1)"
    );
}

#[test]
fn a_boot_while_logging_off_ends_without_waiting_for_the_confirmation() {
    let mut flow = in_world();
    flow.request_logoff(Duration::ZERO).unwrap();
    flow.handle_message(Duration::from_secs(1), &words(&[0xF659, 0x01]));
    assert_eq!(flow.phase(), Phase::Failed);
    assert!(matches!(
        ended_by(&mut flow),
        Some(SessionError::ServerEnded { code: 1, .. })
    ));
    // No log-off timeout follows.
    flow.tick(LOGOFF_CONFIRMATION_TIMEOUT);
    assert!(
        !flow
            .drain_events()
            .any(|e| matches!(e, SessionEvent::LoggedOff { .. }))
    );
}

/// A server that falls silent (the server dying, or dropping the session
/// without a word) ends the session with "connection lost", in the world or
/// while logging off (OpenAC's server-silence rule).
#[test]
fn a_lost_connection_ends_the_session_in_the_world() {
    let mut flow = in_world();
    flow.drain_events().for_each(drop);
    flow.lose_connection();
    assert_eq!(flow.phase(), Phase::Failed);
    let error = ended_by(&mut flow).expect("the session failed");
    assert_eq!(error, SessionError::ConnectionLost);
    assert_eq!(error.to_string(), "the connection to the server was lost");
    // Once: a second report changes nothing.
    flow.lose_connection();
    assert!(ended_by(&mut flow).is_none());
}

#[test]
fn a_lost_connection_while_logging_off_ends_without_the_confirmation() {
    let mut flow = in_world();
    flow.request_logoff(Duration::ZERO).unwrap();
    flow.lose_connection();
    assert_eq!(flow.phase(), Phase::Failed);
    assert_eq!(ended_by(&mut flow), Some(SessionError::ConnectionLost));
    flow.tick(LOGOFF_CONFIRMATION_TIMEOUT);
    assert!(
        !flow
            .drain_events()
            .any(|e| matches!(e, SessionEvent::LoggedOff { .. }))
    );
}

#[test]
fn a_boot_at_selection_or_while_entering_ends_the_session_too() {
    let mut flow = connected();
    flow.handle_message(Duration::ZERO, &words(&[0xF659, 0x15]));
    assert_eq!(flow.phase(), Phase::Failed);
    assert_eq!(
        ended_by(&mut flow),
        Some(SessionError::ServerEnded {
            code: 0x15,
            reason: "The server is currently full. Please try again later.".into(),
        })
    );

    let mut flow = connected();
    flow.enter_world(Duration::ZERO, &CharacterSelector::FirstAvailable)
        .unwrap();
    flow.handle_message(Duration::ZERO, &words(&[0xF659, 0x04]));
    assert_eq!(flow.phase(), Phase::Failed);
    // The held enter-world is dropped: server ready no longer enters.
    flow.handle_message(Duration::ZERO, &words(&[0xF7DF]));
    assert_eq!(flow.phase(), Phase::Failed);
}

#[test]
fn an_enter_game_error_in_the_world_does_not_end_the_session() {
    // Enter-game errors go back to character selection in the game; in the
    // world they change nothing here (OpenAC drops them there).
    let mut flow = in_world();
    for code in [0x05, 0x06, 0x0D, 0x16] {
        flow.handle_message(Duration::ZERO, &words(&[0xF659, code]));
        assert_eq!(flow.phase(), Phase::InWorld, "code {code:#x}");
    }
}

#[test]
fn player_object_triggers_login_complete_once() {
    let mut flow = in_world();
    flow.handle_message(Duration::ZERO, &create_object(0x7000_0001));
    assert!(opcodes(&mut flow).is_empty(), "other objects do not count");
    flow.handle_message(Duration::ZERO, &create_object(PLAYER));
    let sent: Vec<Vec<u8>> = flow.drain_outgoing().map(|o| o.body).collect();
    assert_eq!(sent, [build_login_complete(), build_house_query(1)]);
    flow.handle_message(Duration::ZERO, &create_object(PLAYER));
    assert!(opcodes(&mut flow).is_empty());
}

/// OpenAC's window (`LiveSessionRuntimeFactory`): at the first entry the
/// house query goes out and login complete waits for the end of the login
/// presentation (`LocalPlayerTeleportSession.SendLoginComplete`).
#[test]
fn a_window_presenting_the_login_holds_login_complete() {
    let mut flow = in_world();
    flow.set_hold_login_complete(true);
    flow.handle_message(Duration::ZERO, &create_object(PLAYER));
    let sent: Vec<Vec<u8>> = flow.drain_outgoing().map(|o| o.body).collect();
    assert_eq!(sent, [build_house_query(1)]);
    assert!(flow.login_complete_held() && !flow.login_complete_sent());
    flow.handle_message(Duration::ZERO, &create_object(PLAYER));
    assert!(opcodes(&mut flow).is_empty());
    flow.send_held_login_complete();
    let sent: Vec<Vec<u8>> = flow.drain_outgoing().map(|o| o.body).collect();
    assert_eq!(sent, [build_login_complete()]);
    assert!(flow.login_complete_sent() && !flow.login_complete_held());
    assert!(
        flow.drain_events()
            .any(|e| e == SessionEvent::LoginCompleteSent)
    );
    flow.send_held_login_complete();
    assert!(opcodes(&mut flow).is_empty(), "once");
}

/// OpenAC's no-window host with physics (`HeadlessSessionHost`'s event
/// route): login complete, then the house query, go when the character's
/// first entry completes, not when its object arrives.
#[test]
fn without_a_window_login_complete_and_the_house_query_wait_for_the_entry() {
    let mut flow = in_world();
    flow.set_login_awaits_entry(true);
    flow.handle_message(Duration::ZERO, &create_object(PLAYER));
    assert!(opcodes(&mut flow).is_empty(), "nothing at the object");
    assert!(flow.login_complete_held() && !flow.login_complete_sent());
    flow.send_held_login_complete();
    let sent: Vec<Vec<u8>> = flow.drain_outgoing().map(|o| o.body).collect();
    assert_eq!(sent, [build_login_complete(), build_house_query(1)]);
    assert!(flow.login_complete_sent());
    flow.send_held_login_complete();
    assert!(opcodes(&mut flow).is_empty(), "once");
}

#[test]
fn logoff_waits_for_confirmation_or_times_out() {
    let mut flow = in_world();
    flow.request_logoff(Duration::ZERO).unwrap();
    assert_eq!(opcodes(&mut flow), [(0xF653, Queue::Ui)]);
    flow.handle_message(Duration::ZERO, &words(&[0xF653]));
    assert_eq!(flow.phase(), Phase::LoggedOff);
    assert!(
        flow.drain_events()
            .any(|e| e == SessionEvent::LoggedOff { confirmed: true })
    );

    let mut flow = in_world();
    flow.request_logoff(Duration::ZERO).unwrap();
    flow.tick(LOGOFF_CONFIRMATION_TIMEOUT);
    assert!(
        flow.drain_events()
            .any(|e| e == SessionEvent::LoggedOff { confirmed: false })
    );
}

#[test]
fn logoff_outside_the_world_is_refused() {
    let mut flow = connected();
    assert!(matches!(
        flow.request_logoff(Duration::ZERO),
        Err(SessionError::WrongPhase { .. })
    ));
}
